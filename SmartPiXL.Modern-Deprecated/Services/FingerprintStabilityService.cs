using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Memory;

namespace TrackingPixel.Services;

// ============================================================================
// FINGERPRINT STABILITY SERVICE — Server-side anti-detect browser + volume detection.
//
// PURPOSE:
//   Client-side JavaScript can collect fingerprints (canvas, WebGL, audio hashes)
//   but cannot correlate them across requests. This service maintains a per-IP
//   history in IMemoryCache to detect two distinct attack patterns:
//
// LAYER 1 — FINGERPRINT VARIATION (anti-detect browsers):
//   Anti-detect browsers (Multilogin, GoLogin, Dolphin Anty) create separate
//   "profiles" with unique fingerprints per tab/session. When 3+ unique fingerprint
//   combinations are seen from the same IP, it's a strong signal of synthetic identity.
//   Normal users have 1 fingerprint (or 2 if they update their browser mid-session).
//
// LAYER 2 — VOLUME / RATE DETECTION (Pass 3 gap fix):
//   Red team testing (Pass 3) sent 600 identical fingerprints from a single IP.
//   Layer 1 didn't flag this because there was no VARIATION — all 600 had the
//   same fingerprint. Layer 2 catches this by tracking:
//   - Total observation count per IP in 24h (>50 = High, >200 = Extreme)
//   - Tick-based rate in a 5-minute sliding window (>20 = High Rate)
//
// MEMORY MODEL:
//   - IMemoryCache with 24h sliding expiration per IP
//   - Each entry is a FingerprintHistory object (~200-400 bytes typical)
//   - Lock granularity: per-history object (not global) for concurrency
//   - RecentTicks list capped at 1000 entries to bound memory per IP
//
// THREAD SAFETY:
//   IMemoryCache.GetOrCreate is thread-safe for reads. The FingerprintHistory
//   object is locked individually (lock(history)) so concurrent requests from
//   different IPs don't contend. Same-IP requests serialize on the lock.
// ============================================================================

/// <summary>
/// Tracks fingerprint stability and request volume per IP to detect anti-detect browsers
/// and high-volume synthetic traffic patterns.
/// <para>
/// Uses primary constructor parameter <c>cache</c> — the <see cref="IMemoryCache"/> is injected
/// directly without a backing field declaration (C# 12 primary constructors).
/// </para>
/// </summary>
public sealed class FingerprintStabilityService(IMemoryCache cache)
{
    /// <summary>
    /// Sliding expiration for per-IP fingerprint history.
    /// 24 hours is long enough to catch anti-detect browsers that rotate profiles
    /// over the course of a workday, but short enough to not accumulate stale data.
    /// </summary>
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromHours(24);
    
    /// <summary>
    /// 5-minute rate window stored as raw ticks to avoid TimeSpan struct allocation
    /// on every comparison. <c>TimeSpan.TicksPerMinute * 5 = 3,000,000,000 ticks</c>.
    /// </summary>
    private const long RateWindowTicks = TimeSpan.TicksPerMinute * 5;

    /// <summary>
    /// Records a fingerprint observation from a pixel hit and returns analysis results.
    /// <para>
    /// Called from <see cref="Endpoints.TrackingEndpoints.CaptureAndEnqueue"/> for every
    /// pixel hit that has tracking data. The fingerprint hashes come from the pixel
    /// JavaScript's canvas, WebGL, and AudioContext fingerprinting.
    /// </para>
    /// </summary>
    /// <param name="ipAddress">Client IP address (used as the cache key prefix).</param>
    /// <param name="canvasHash">Canvas fingerprint hash from JavaScript (may be null if blocked).</param>
    /// <param name="webglHash">WebGL renderer/vendor fingerprint hash (may be null).</param>
    /// <param name="audioHash">AudioContext fingerprint hash (may be null).</param>
    /// <returns>An immutable <see cref="FingerprintStabilityResult"/> with all detection signals.</returns>
    public FingerprintStabilityResult RecordAndCheck(
        string ipAddress,
        string? canvasHash,
        string? webglHash,
        string? audioHash)
    {
        // Cache key format: "fp:{ip}" — the "fp:" prefix avoids collisions with
        // other services (IpBehaviorService uses "subnet:" and "rapid:" prefixes).
        var visitorKey = $"fp:{ipAddress}";
        
        // Concatenate hashes into a single composite fingerprint string.
        // Example: "a1b2c3|x4y5z6|m7n8o9" — the pipe separators make it unique
        // even if individual hashes are empty strings.
        var currentFP = $"{canvasHash ?? ""}|{webglHash ?? ""}|{audioHash ?? ""}";

        // GetOrCreate is thread-safe: if two threads race for the same key, only one
        // factory delegate runs and both get the same FingerprintHistory instance.
        var history = cache.GetOrCreate(visitorKey, entry =>
        {
            entry.SlidingExpiration = CacheExpiry;
            return new FingerprintHistory();
        })!;

        // Per-history lock: concurrent requests from DIFFERENT IPs don't contend.
        // Only requests from the SAME IP serialize here.
        lock (history)
        {
            // --- LAYER 1: Fingerprint variation detection ---
            // IsStable = true if this is the first observation OR the fingerprint
            // has been seen before. False means a NEW fingerprint from a known IP.
            var isStable = history.Fingerprints.Count == 0 ||
                           history.Fingerprints.Contains(currentFP);

            // HashSet<string>.Add: deduplicates automatically. If the fingerprint
            // was already seen, Count stays the same. If new, Count increments.
            history.Fingerprints.Add(currentFP);
            history.ObservationCount++;

            // --- LAYER 2: Rate tracking via tick-based sliding window ---
            var nowTicks = DateTime.UtcNow.Ticks;
            var cutoff = nowTicks - RateWindowTicks; // Everything before this is stale
            
            // Prune entries outside the 5-minute window.
            // The list is ordered oldest-first because we always append at the end,
            // so we scan from the front and count how many are below the cutoff.
            var ts = history.RecentTicks;
            var pruneCount = 0;
            for (var i = 0; i < ts.Count; i++)
            {
                if (ts[i] >= cutoff) break; // Found first in-window entry — stop
                pruneCount++;
            }
            if (pruneCount > 0) ts.RemoveRange(0, pruneCount);
            
            // Cap at 1000 entries per IP to bound memory under extreme volume.
            // An attacker sending 10,000 req/sec would only keep the last 1000.
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
                // Bitwise AND (&) instead of logical AND (&&) — branchless on x64.
                // The JIT compiles this to CMP+SETG for each side, then AND.
                // No branch misprediction penalty. Both conditions are cheap int comparisons.
                SuspiciousVariation = uniqueFPs > 2 & obsCount > 3,
                HighVolume = obsCount > 50,
                ExtremeVolume = obsCount > 200,
                RecentRate = recentRate,
                HighRate = recentRate > 20
            };
        }
    }

    /// <summary>
    /// Per-IP fingerprint history stored in <see cref="IMemoryCache"/>.
    /// <para>
    /// Memory layout (typical): ~200–400 bytes per entry depending on fingerprint diversity.
    /// With 24h sliding expiration, idle IPs are automatically evicted.
    /// </para>
    /// </summary>
    private sealed class FingerprintHistory
    {
        /// <summary>
        /// Set of unique composite fingerprint strings seen from this IP.
        /// Normal users have 1–2 entries. Anti-detect browsers have 3+.
        /// </summary>
        public HashSet<string> Fingerprints { get; } = [];
        
        /// <summary>
        /// Total number of pixel hits from this IP in the cache window (up to 24h).
        /// </summary>
        public int ObservationCount { get; set; }
        
        /// <summary>
        /// Recent hit timestamps as raw ticks (8 bytes each, no DateTime struct overhead).
        /// Ordered oldest-first for efficient front-pruning. Capped at 1000 entries.
        /// </summary>
        public List<long> RecentTicks { get; } = [];
    }
}

/// <summary>
/// Immutable result of fingerprint stability + volume analysis.
/// <para>
/// <c>readonly record struct</c> — stack-allocated, no GC pressure.
/// All boolean fields use <c>&gt;</c> comparisons which the JIT compiles to
/// branchless <c>CMP+SETG</c> instructions on x64.
/// </para>
/// </summary>
public readonly record struct FingerprintStabilityResult
{
    // ---- Layer 1: Fingerprint variation detection ----
    
    /// <summary>True if the current fingerprint was previously seen from this IP (or is the first).</summary>
    public bool IsStable { get; init; }
    
    /// <summary>Count of distinct composite fingerprints from this IP. Normal: 1–2, suspicious: 3+.</summary>
    public int UniqueFingerprints { get; init; }
    
    /// <summary>Total pixel hits from this IP in the 24h cache window.</summary>
    public int ObservationCount { get; init; }
    
    /// <summary>True when 3+ unique fingerprints AND 4+ observations (branchless bitwise AND).</summary>
    public bool SuspiciousVariation { get; init; }
    
    // ---- Layer 2: Volume / rate detection ----
    
    /// <summary>True when observation count exceeds 50 in 24h (high-volume bot).</summary>
    public bool HighVolume { get; init; }
    
    /// <summary>True when observation count exceeds 200 in 24h (extreme-volume bot farm).</summary>
    public bool ExtremeVolume { get; init; }
    
    /// <summary>Number of hits from this IP in the last 5-minute window.</summary>
    public int RecentRate { get; init; }
    
    /// <summary>True when 5-minute rate exceeds 20 hits (automated rapid-fire pattern).</summary>
    public bool HighRate { get; init; }
}
