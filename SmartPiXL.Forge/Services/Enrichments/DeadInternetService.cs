// ─────────────────────────────────────────────────────────────────────────────
// SmartPiXL Forge — Dead Internet Detection Service
// Per-customer per-hour aggregation of bot signals, engagement quality,
// and fingerprint diversity to produce a compound Dead Internet Index (0-100).
// High index = high proportion of non-human or disengaged traffic.
// Phase 6 — Tier 3 Enrichments (Asymmetric Detection)
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Concurrent;

namespace SmartPiXL.Forge.Services.Enrichments;

/// <summary>
/// Tracks per-customer traffic quality metrics over a 24-hour sliding window.
/// Produces a compound Dead Internet Index (0-100) that measures the proportion
/// of non-human, automated, or disengaged traffic for each customer.
/// </summary>
/// <remarks>
/// The index is a weighted combination of five signals:
/// <list type="bullet">
///   <item>Bot ratio (30%): proportion of hits flagged as bots</item>
///   <item>Zero engagement ratio (20%): hits with no mouse movement</item>
///   <item>Datacenter ratio (20%): hits from cloud/datacenter IPs</item>
///   <item>Contradiction ratio (15%): hits with impossible field combos</item>
///   <item>Fingerprint diversity (15%): low unique FP count relative to hits</item>
/// </list>
/// Uses hour-level buckets for efficient aggregation and 24h sliding window
/// for trend stability. Thread-safe via ConcurrentDictionary and Interlocked.
/// </remarks>
public sealed class DeadInternetService : IDisposable
{
    // ════════════════════════════════════════════════════════════════════════
    // CONSTANTS
    // ════════════════════════════════════════════════════════════════════════

    private const double WeightBot           = 0.30;
    private const double WeightZeroEngage    = 0.20;
    private const double WeightDatacenter    = 0.20;
    private const double WeightContradiction = 0.15;
    private const double WeightFpDiversity   = 0.15;

    private const int SlidingWindowHours = 24;
    private const int EvictionIntervalMs = 600_000; // 10 min
    private const int MinHitsForIndex = 5; // Need at least 5 hits to compute

    // ════════════════════════════════════════════════════════════════════════
    // HOUR BUCKET
    // ════════════════════════════════════════════════════════════════════════

    private sealed class HourBucket
    {
        public int TotalHits;
        public int BotHits;
        public int ZeroMouseHits;
        public int DatacenterHits;
        public int ContradictionHits;
        public int ReplayHits;

        // Thread-safe unique FP tracking via lock on the HashSet
        private readonly HashSet<string> _uniqueFingerprints = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _fpLock = new();

        public void AddFingerprint(string? fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint)) return;
            lock (_fpLock)
            {
                _uniqueFingerprints.Add(fingerprint);
            }
        }

        public int UniqueFingerprints
        {
            get
            {
                lock (_fpLock) { return _uniqueFingerprints.Count; }
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // CUSTOMER METRICS
    // ════════════════════════════════════════════════════════════════════════

    private sealed class CustomerMetrics
    {
        public readonly ConcurrentDictionary<long, HourBucket> Buckets = new();
        public DateTime LastAccess = DateTime.UtcNow;
    }

    // ════════════════════════════════════════════════════════════════════════
    // STATE
    // ════════════════════════════════════════════════════════════════════════

    private readonly ConcurrentDictionary<string, CustomerMetrics> _customers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _evictionTimer;

    // ════════════════════════════════════════════════════════════════════════
    // CONSTRUCTOR
    // ════════════════════════════════════════════════════════════════════════

    public DeadInternetService()
    {
        _evictionTimer = new Timer(EvictStaleData, null, EvictionIntervalMs, EvictionIntervalMs);
    }

    // ════════════════════════════════════════════════════════════════════════
    // PUBLIC API
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Records a hit for a customer and returns the current Dead Internet Index (0-100).
    /// </summary>
    /// <param name="companyId">Customer/company identifier.</param>
    /// <param name="isBotHit">Whether this hit was flagged as a bot.</param>
    /// <param name="hasMouseMoves">Whether this hit had any mouse movement.</param>
    /// <param name="isDatacenter">Whether this hit is from a datacenter IP.</param>
    /// <param name="contradictionCount">Number of contradictions detected for this hit.</param>
    /// <param name="isReplay">Whether this hit had a replayed mouse path.</param>
    /// <param name="fingerprint">Device fingerprint for diversity tracking.</param>
    /// <returns>Dead Internet Index 0-100 (0 = healthy traffic, 100 = dead internet).</returns>
    public int RecordHit(
        string? companyId,
        bool isBotHit,
        bool hasMouseMoves,
        bool isDatacenter,
        int contradictionCount,
        bool isReplay,
        string? fingerprint)
    {
        if (string.IsNullOrEmpty(companyId))
            return 0;

        var metrics = _customers.GetOrAdd(companyId, _ => new CustomerMetrics());
        metrics.LastAccess = DateTime.UtcNow;

        var hourKey = GetHourKey(DateTime.UtcNow);
        var bucket = metrics.Buckets.GetOrAdd(hourKey, _ => new HourBucket());

        // Record the hit
        Interlocked.Increment(ref bucket.TotalHits);

        if (isBotHit)
            Interlocked.Increment(ref bucket.BotHits);
        if (!hasMouseMoves)
            Interlocked.Increment(ref bucket.ZeroMouseHits);
        if (isDatacenter)
            Interlocked.Increment(ref bucket.DatacenterHits);
        if (contradictionCount > 0)
            Interlocked.Increment(ref bucket.ContradictionHits);
        if (isReplay)
            Interlocked.Increment(ref bucket.ReplayHits);

        bucket.AddFingerprint(fingerprint);

        // Compute and return the current index
        return ComputeIndex(metrics);
    }

    // ════════════════════════════════════════════════════════════════════════
    // INDEX COMPUTATION
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes the Dead Internet Index for a customer across the 24h sliding window.
    /// </summary>
    private static int ComputeIndex(CustomerMetrics metrics)
    {
        var cutoffHour = GetHourKey(DateTime.UtcNow.AddHours(-SlidingWindowHours));

        int totalHits = 0, botHits = 0, zeroMouseHits = 0;
        int datacenterHits = 0, contradictionHits = 0;
        int totalUniqueFps = 0;

        foreach (var kvp in metrics.Buckets)
        {
            if (kvp.Key < cutoffHour) continue; // expired bucket

            var b = kvp.Value;
            totalHits += b.TotalHits;
            botHits += b.BotHits;
            zeroMouseHits += b.ZeroMouseHits;
            datacenterHits += b.DatacenterHits;
            contradictionHits += b.ContradictionHits;
            totalUniqueFps += b.UniqueFingerprints;
        }

        if (totalHits < MinHitsForIndex)
            return 0; // not enough data

        // Compute individual ratios (0.0 - 1.0)
        var botRatio = (double)botHits / totalHits;
        var zeroEngageRatio = (double)zeroMouseHits / totalHits;
        var dcRatio = (double)datacenterHits / totalHits;
        var contradictionRatio = (double)contradictionHits / totalHits;

        // Fingerprint diversity: low diversity = suspicious
        // If 100 hits but only 2 unique FPs → ratio = 0.98 (bad)
        // If 100 hits and 90 unique FPs → ratio = 0.10 (good)
        var fpDiversityRatio = 1.0 - Math.Min((double)totalUniqueFps / totalHits, 1.0);

        // Weighted sum → index 0-100
        var rawIndex = (botRatio * WeightBot)
                     + (zeroEngageRatio * WeightZeroEngage)
                     + (dcRatio * WeightDatacenter)
                     + (contradictionRatio * WeightContradiction)
                     + (fpDiversityRatio * WeightFpDiversity);

        return (int)Math.Clamp(rawIndex * 100.0, 0, 100);
    }

    // ════════════════════════════════════════════════════════════════════════
    // EVICTION
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Evicts hour buckets older than the sliding window and customers
    /// with no activity in the last 48 hours.
    /// </summary>
    private void EvictStaleData(object? state)
    {
        var hourCutoff = GetHourKey(DateTime.UtcNow.AddHours(-SlidingWindowHours));
        var customerCutoff = DateTime.UtcNow.AddHours(-48);

        foreach (var kvp in _customers)
        {
            if (kvp.Value.LastAccess < customerCutoff)
            {
                _customers.TryRemove(kvp.Key, out _);
                continue;
            }

            // Evict old hour buckets
            foreach (var bucket in kvp.Value.Buckets)
            {
                if (bucket.Key < hourCutoff)
                    kvp.Value.Buckets.TryRemove(bucket.Key, out _);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Converts a DateTime to an hour-level key (hours since Unix epoch).
    /// </summary>
    private static long GetHourKey(DateTime utc)
        => utc.Ticks / TimeSpan.TicksPerHour;

    // ════════════════════════════════════════════════════════════════════════
    // DISPOSE
    // ════════════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        _evictionTimer.Dispose();
    }
}
