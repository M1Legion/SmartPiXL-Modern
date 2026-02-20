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
// This is a fast synchronous check — no I/O, no network, pure string matching.
//
// APPENDED PARAMS:
//   _srv_knownBot=1          — UA matched a known bot pattern
//   _srv_botName={name}      — Name of the matched bot
// ============================================================================

/// <summary>
/// Detects known bots/crawlers by User-Agent string matching using NetCrawlerDetect.
/// Stateless singleton — thread-safe for concurrent use.
/// </summary>
public sealed class BotUaDetectionService
{
    private readonly CrawlerDetect _detector;
    private readonly ITrackingLogger _logger;

    public BotUaDetectionService(ITrackingLogger logger)
    {
        _detector = new CrawlerDetect();
        _logger = logger;
    }

    /// <summary>
    /// Checks if the given User-Agent belongs to a known bot/crawler.
    /// </summary>
    /// <param name="userAgent">The raw User-Agent header value.</param>
    /// <returns>
    /// A tuple: (isCrawler, botName). If the UA matches a known bot, <c>isCrawler</c>
    /// is <c>true</c> and <c>botName</c> contains the matched bot name.
    /// </returns>
    public (bool IsCrawler, string? BotName) Check(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
            return (false, null);

        try
        {
            var isCrawler = _detector.IsCrawler(userAgent);
            if (!isCrawler)
                return (false, null);

            var botName = _detector.Matches?.FirstOrDefault()?.Value;
            return (true, botName);
        }
        catch (Exception ex)
        {
            _logger.Debug($"BotUaDetection: check failed — {ex.Message}");
            return (false, null);
        }
    }
}
