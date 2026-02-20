using System.Collections.Concurrent;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services.Enrichments;

// ============================================================================
// CROSS-CUSTOMER INTEL SERVICE — Detects when the same IP+fingerprint combo
// hits multiple different companies within a sliding time window.
//
// This leverages the Forge's unique position: it sees ALL pixel traffic across
// ALL customers in real-time. No single customer can detect cross-customer
// scraping on their own.
//
// DETECTION LOGIC:
//   Key = (IPAddress, FingerprintHash) — composite identity
//   Value = sliding window of (CompanyID, Timestamp) tuples
//
//   Same IP+FP hitting:
//     3+ companies in 5 minutes  → bot signal (_srv_crossCustAlert=1)
//     10+ companies in 1 hour    → definite scraper
//
// MEMORY MANAGEMENT:
//   ConcurrentDictionary with periodic eviction of entries > 2 hours old.
//   Eviction runs every 5 minutes via the enrichment pipeline call.
//   Estimated memory: ~200 bytes per entry × 100K unique visitors = ~20 MB.
//
// APPENDED PARAMS:
//   _srv_crossCustHits={count}     — distinct companies hit in the window
//   _srv_crossCustWindow={minutes} — window duration in minutes
//   _srv_crossCustAlert=1          — set when threshold exceeded (3+ in 5 min)
// ============================================================================

/// <summary>
/// Detects cross-customer scraping patterns by tracking IP+fingerprint
/// combinations across all customers in a sliding time window.
/// Singleton — thread-safe via ConcurrentDictionary.
/// </summary>
public sealed class CrossCustomerIntelService
{
    private readonly ConcurrentDictionary<(string IP, string FP), CrossCustomerTracker> _trackers = new();
    private readonly ITrackingLogger _logger;
    private DateTime _lastEviction = DateTime.UtcNow;

    private static readonly TimeSpan s_windowDuration = TimeSpan.FromHours(2);
    private static readonly TimeSpan s_evictionInterval = TimeSpan.FromMinutes(5);
    private const int AlertThreshold = 3;          // 3+ companies = alert
    private const int AlertWindowMinutes = 5;      // within 5 minutes

    /// <summary>
    /// Result of cross-customer analysis for a single record.
    /// </summary>
    public readonly record struct CrossCustomerResult(
        int DistinctCompanies,
        int WindowMinutes,
        bool IsAlert);

    public CrossCustomerIntelService(ITrackingLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Records a hit for the given IP+fingerprint+company and returns the
    /// current cross-customer analysis.
    /// </summary>
    /// <param name="ipAddress">Client IP address.</param>
    /// <param name="fingerprintHash">Composite fingerprint (canvasFP, audioHash, or DeviceHash).</param>
    /// <param name="companyId">The CompanyID this hit belongs to.</param>
    /// <returns>Cross-customer analysis result.</returns>
    public CrossCustomerResult RecordHit(string? ipAddress, string? fingerprintHash, string? companyId)
    {
        // Can't analyze without all three keys
        if (string.IsNullOrEmpty(ipAddress) || string.IsNullOrEmpty(fingerprintHash) || string.IsNullOrEmpty(companyId))
            return default;

        var key = (ipAddress, fingerprintHash);
        var now = DateTime.UtcNow;

        var tracker = _trackers.GetOrAdd(key, _ => new CrossCustomerTracker());

        int distinctCompanies;
        bool isAlert;

        lock (tracker)
        {
            // Add this hit
            tracker.Hits.Add((companyId, now));

            // Prune hits older than the window
            tracker.Hits.RemoveAll(h => (now - h.Timestamp) > s_windowDuration);

            // Count distinct companies in the short alert window (5 min)
            var alertCutoff = now.AddMinutes(-AlertWindowMinutes);
            var recentCompanyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < tracker.Hits.Count; i++)
            {
                if (tracker.Hits[i].Timestamp >= alertCutoff)
                    recentCompanyIds.Add(tracker.Hits[i].CompanyId);
            }

            distinctCompanies = recentCompanyIds.Count;
            isAlert = distinctCompanies >= AlertThreshold;

            // Count total distinct companies over the full window
            var allCompanyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < tracker.Hits.Count; i++)
            {
                allCompanyIds.Add(tracker.Hits[i].CompanyId);
            }

            distinctCompanies = allCompanyIds.Count;
        }

        // Periodic eviction of stale entries
        if ((now - _lastEviction) > s_evictionInterval)
        {
            _lastEviction = now;
            EvictStaleEntries(now);
        }

        return new CrossCustomerResult(distinctCompanies, AlertWindowMinutes, isAlert);
    }

    /// <summary>
    /// Removes tracker entries that have had no hits within the window duration.
    /// Called periodically from <see cref="RecordHit"/>.
    /// </summary>
    private void EvictStaleEntries(DateTime now)
    {
        var evicted = 0;
        foreach (var kvp in _trackers)
        {
            bool shouldRemove;
            lock (kvp.Value)
            {
                // Remove hits older than the window
                kvp.Value.Hits.RemoveAll(h => (now - h.Timestamp) > s_windowDuration);
                shouldRemove = kvp.Value.Hits.Count == 0;
            }

            if (shouldRemove)
            {
                _trackers.TryRemove(kvp.Key, out _);
                evicted++;
            }
        }

        if (evicted > 0)
            _logger.Debug($"CrossCustomerIntel: evicted {evicted} stale tracker entries");
    }

    /// <summary>
    /// Returns the current number of tracked IP+FP combinations (for diagnostics).
    /// </summary>
    public int TrackerCount => _trackers.Count;

    /// <summary>
    /// Internal tracker for a single IP+FP combination.
    /// </summary>
    internal sealed class CrossCustomerTracker
    {
        public List<(string CompanyId, DateTime Timestamp)> Hits { get; } = new(4);
    }
}
