using System.Collections.Concurrent;
using NetCrawlerDetect;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services.Enrichments;

// ============================================================================
// BOT UA DETECTION SERVICE — Wraps NetCrawlerDetect to identify known
// bots/crawlers by User-Agent string matching.
//
// NetCrawlerDetect maintains a regularly-updated list of known bot User-Agent
// patterns (Googlebot, Bingbot, Yahoo Slurp, Baidu Spider, etc.).
//
// PERFORMANCE:
//   ConcurrentDictionary cache eliminates regex evaluation for repeat UAs.
//   Production data shows 5.5% unique UA ratio across 100K records — giving
//   94.5% cache hit rate. Cache hits are a lock-free hash table lookup (~100ns)
//   vs compiled regex evaluation (~300-500μs) on miss.
//
//   Per-call CrawlerDetect instances fix a thread-safety bug: the library
//   stores MatchCollection in an instance field, which is mutated by IsCrawler.
//   With 8 concurrent enrichment workers, the previous shared instance could
//   return another thread's bot name via the Matches property.
//
// APPENDED PARAMS:
//   _srv_knownBot=1          — UA matched a known bot pattern
//   _srv_botName={name}      — Name of the matched bot
// ============================================================================

/// <summary>
/// Detects known bots/crawlers by User-Agent string matching using NetCrawlerDetect.
/// Thread-safe singleton with ConcurrentDictionary cache for repeat UA elimination.
/// </summary>
public sealed class BotUaDetectionService
{
    private readonly ConcurrentDictionary<string, (bool IsCrawler, string? BotName)> _cache = new();
    private readonly ITrackingLogger _logger;

    /// <summary>Maximum cache entries before full eviction. 50K entries ≈ 10 MB.</summary>
    private const int MaxCacheSize = 50_000;

    public BotUaDetectionService(ITrackingLogger logger)
    {
        _logger = logger;
        // Warm the static compiled regex on first construction.
        // CrawlerDetect caches _compiledRegex/_compiledExclusions in static fields —
        // subsequent per-call instances reuse them with zero recompilation cost.
        _ = new CrawlerDetect();
    }

    /// <summary>
    /// Checks if the given User-Agent belongs to a known bot/crawler.
    /// Lock-free cache lookup for repeat UAs (~94.5% hit rate).
    /// Thread-safe: per-call CrawlerDetect instance on cache miss.
    /// </summary>
    /// <param name="userAgent">The raw User-Agent header value.</param>
    /// <returns>
    /// A tuple: (isCrawler, botName). If the UA matches a known bot, <c>isCrawler</c>
    /// is <c>true</c> and <c>botName</c> contains the matched bot name.
    /// </returns>
    public (bool IsCrawler, string? BotName) Check(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return (false, null);

        // Lock-free cache lookup — ConcurrentDictionary.TryGetValue is a hash probe
        if (_cache.TryGetValue(userAgent, out var cached))
            return cached;

        // Cache miss — per-call instance avoids thread-safety issue with _matches field.
        // Static _compiledRegex/_compiledExclusions are compiled on first use and shared
        // across all instances. The per-instance constructor is lightweight (~10-50μs).
        //
        // We specifically DON'T lock a shared instance here: with 8 workers, serializing
        // the expensive regex evaluation (~4ms per miss against 800+ bot patterns) under
        // a lock performs worse than parallel per-instance evaluation. At 5.5% miss rate,
        // the occasional concurrent misses benefit from parallel execution.
        try
        {
            var detector = new CrawlerDetect();
            var isCrawler = detector.IsCrawler(userAgent);

            // Avoid LINQ FirstOrDefault() — index directly into MatchCollection
            var result = isCrawler
                ? (true, detector.Matches?.Count > 0 ? detector.Matches[0].Value : null)
                : (false, (string?)null);

            // Bounded cache — full eviction at threshold. Simpler than LRU,
            // re-populates quickly from live traffic repeats.
            if (_cache.Count >= MaxCacheSize)
                _cache.Clear();

            _cache.TryAdd(userAgent, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.Debug($"BotUaDetection: check failed — {ex.Message}");
            return (false, null);
        }
    }
}
