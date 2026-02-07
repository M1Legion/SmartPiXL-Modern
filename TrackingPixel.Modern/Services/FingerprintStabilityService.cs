using Microsoft.Extensions.Caching.Memory;

namespace TrackingPixel.Services;

/// <summary>
/// V-05: Tracks fingerprint stability over time to detect anti-detect browsers.
/// Anti-detect browsers (Multilogin, GoLogin, Dolphin Anty) change fingerprints per "profile"
/// but legitimate users have consistent fingerprints from the same IP.
/// Uses IP as visitor identifier with 24-hour sliding window.
/// </summary>
public sealed class FingerprintStabilityService
{
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromHours(24);

    public FingerprintStabilityService(IMemoryCache cache)
    {
        _cache = cache;
    }

    /// <summary>
    /// Records a fingerprint observation and returns stability metrics.
    /// Thread-safe via locking on the per-visitor history object.
    /// </summary>
    public FingerprintStabilityResult RecordAndCheck(
        string ipAddress,
        string? canvasHash,
        string? webglHash,
        string? audioHash)
    {
        var visitorKey = $"fp:{ipAddress}";
        var currentFP = $"{canvasHash ?? ""}|{webglHash ?? ""}|{audioHash ?? ""}";

        var history = _cache.GetOrCreate(visitorKey, entry =>
        {
            entry.SlidingExpiration = CacheExpiry;
            return new FingerprintHistory();
        })!;

        lock (history)
        {
            var isStable = history.Fingerprints.Count == 0 ||
                           history.Fingerprints.Contains(currentFP);

            history.Fingerprints.Add(currentFP); // HashSet ignores duplicates
            history.ObservationCount++;

            return new FingerprintStabilityResult
            {
                IsStable = isStable,
                UniqueFingerprints = history.Fingerprints.Count,
                ObservationCount = history.ObservationCount,
                // 3+ unique fingerprints from same IP in 24h = suspicious
                SuspiciousVariation = history.Fingerprints.Count > 2 &&
                                      history.ObservationCount > 3
            };
        }
    }

    private sealed class FingerprintHistory
    {
        public HashSet<string> Fingerprints { get; } = new();
        public int ObservationCount { get; set; }
    }
}

public record FingerprintStabilityResult
{
    public bool IsStable { get; init; }
    public int UniqueFingerprints { get; init; }
    public int ObservationCount { get; init; }
    public bool SuspiciousVariation { get; init; }
}
