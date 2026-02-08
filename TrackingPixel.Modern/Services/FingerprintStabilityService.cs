using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Memory;

namespace TrackingPixel.Services;

/// <summary>
/// V-05: Tracks fingerprint stability over time to detect anti-detect browsers
/// AND high-volume synthetic traffic from the same IP.
/// 
/// LAYER 1 (existing): Fingerprint variation detection
///   Anti-detect browsers change fingerprints per "profile", flagged when 3+ unique FPs appear.
/// 
/// LAYER 2 (Pass 3 gap): Volume/rate detection
///   Pass 3 red team sent 600 identical fingerprints from 1 IP — unflagged because
///   existing logic only detected VARIATION, not volume. Now also flags:
///   - High volume: >50 hits from same IP in 24h
///   - Extreme volume: >200 hits from same IP in 24h
///   - High rate: >20 hits from same IP in 5 minutes
/// </summary>
public sealed class FingerprintStabilityService(IMemoryCache cache)
{
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromHours(24);
    private const long RateWindowTicks = TimeSpan.TicksPerMinute * 5; // 5 min in ticks — avoids TimeSpan alloc

    /// <summary>
    /// Records a fingerprint observation and returns stability + volume metrics.
    /// Thread-safe via locking on the per-visitor history object.
    /// </summary>
    public FingerprintStabilityResult RecordAndCheck(
        string ipAddress,
        string? canvasHash,
        string? webglHash,
        string? audioHash)
    {
        // string.Create with interpolation handler — single alloc, no intermediate concat
        var visitorKey = $"fp:{ipAddress}";
        var currentFP = $"{canvasHash ?? ""}|{webglHash ?? ""}|{audioHash ?? ""}";

        var history = cache.GetOrCreate(visitorKey, entry =>
        {
            entry.SlidingExpiration = CacheExpiry;
            return new FingerprintHistory();
        })!;

        lock (history)
        {
            // --- LAYER 1: Fingerprint variation ---
            var isStable = history.Fingerprints.Count == 0 ||
                           history.Fingerprints.Contains(currentFP);

            history.Fingerprints.Add(currentFP); // HashSet deduplicates
            history.ObservationCount++;

            // --- LAYER 2: Rate tracking via tick-based ring buffer ---
            var nowTicks = DateTime.UtcNow.Ticks;
            var cutoff = nowTicks - RateWindowTicks;
            
            // Prune stale entries — front of list is oldest
            var ts = history.RecentTicks;
            var pruneCount = 0;
            for (var i = 0; i < ts.Count; i++)
            {
                if (ts[i] >= cutoff) break;
                pruneCount++;
            }
            if (pruneCount > 0) ts.RemoveRange(0, pruneCount);
            
            // Bound memory: cap at 1000 entries per IP
            if (ts.Count < 1000)
                ts.Add(nowTicks);

            var recentRate = ts.Count;
            var uniqueFPs = history.Fingerprints.Count;
            var obsCount = history.ObservationCount;

            return new FingerprintStabilityResult
            {
                IsStable = isStable,
                UniqueFingerprints = uniqueFPs,
                ObservationCount = obsCount,
                SuspiciousVariation = uniqueFPs > 2 & obsCount > 3, // Branchless: bitwise AND
                HighVolume = obsCount > 50,
                ExtremeVolume = obsCount > 200,
                RecentRate = recentRate,
                HighRate = recentRate > 20
            };
        }
    }

    private sealed class FingerprintHistory
    {
        public HashSet<string> Fingerprints { get; } = [];
        public int ObservationCount { get; set; }
        public List<long> RecentTicks { get; } = []; // Ticks instead of DateTime — 8 bytes, no struct overhead
    }
}

/// <summary>
/// Immutable result record. All bool comparisons use > which JIT compiles to
/// branchless CMP+SETG on x64 — no branch misprediction penalty.
/// </summary>
public readonly record struct FingerprintStabilityResult
{
    // Layer 1: variation detection
    public bool IsStable { get; init; }
    public int UniqueFingerprints { get; init; }
    public int ObservationCount { get; init; }
    public bool SuspiciousVariation { get; init; }
    
    // Layer 2: volume/rate detection
    public bool HighVolume { get; init; }
    public bool ExtremeVolume { get; init; }
    public int RecentRate { get; init; }
    public bool HighRate { get; init; }
}
