using Microsoft.Extensions.Caching.Memory;

namespace TrackingPixel.Services;

/// <summary>
/// Server-side IP behavior analysis: detects subnet velocity and rapid-fire patterns
/// that require cross-request correlation impossible in client-side JavaScript.
/// 
/// SIGNAL 1: Subnet /24 velocity
///   Multiple unique IPs from the same /24 subnet hitting within a time window
///   indicates coordinated bot infrastructure (cloud VMs, Docker containers).
///   Threshold: 3+ unique IPs from same /24 in 5 minutes = flagged.
/// 
/// SIGNAL 2: Rapid-fire timing
///   Same IP hitting multiple times within a short window indicates automation.
///   Threshold: 2+ hits from same IP within 15 seconds = flagged.
///   Sub-second duplicate = definite automation.
/// </summary>
public sealed class IpBehaviorService(IMemoryCache cache)
{
    private static readonly TimeSpan SubnetCacheExpiry = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RapidFireCacheExpiry = TimeSpan.FromMinutes(2);
    private const long SubnetWindowTicks = TimeSpan.TicksPerMinute * 5;   // 5 min
    private const long RapidFireWindowTicks = TimeSpan.TicksPerSecond * 15; // 15 sec

    /// <summary>
    /// Records a hit and returns subnet/timing analysis.
    /// Thread-safe via per-key locking.
    /// </summary>
    public IpBehaviorResult RecordAndCheck(string ipAddress)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var subnet = ExtractSubnet24(ipAddress);

        // --- Subnet /24 velocity ---
        int subnetUniqueIps = 0;
        int subnetTotalHits = 0;
        if (subnet != null)
        {
            var subnetKey = $"subnet:{subnet}";
            var subnetHistory = cache.GetOrCreate(subnetKey, entry =>
            {
                entry.SlidingExpiration = SubnetCacheExpiry;
                return new SubnetHistory();
            })!;

            lock (subnetHistory)
            {
                // Prune entries outside the 5-min window
                PruneTicks(subnetHistory.HitTicks, nowTicks - SubnetWindowTicks);
                subnetHistory.HitTicks.Add(nowTicks);
                subnetHistory.IpsInWindow.Add(ipAddress);

                // Also prune IPs that haven't been seen recently
                // (keep it simple: just count unique IPs in window)
                subnetUniqueIps = subnetHistory.IpsInWindow.Count;
                subnetTotalHits = subnetHistory.HitTicks.Count;
            }
        }

        // --- Rapid-fire timing ---
        var rapidKey = $"rapid:{ipAddress}";
        var rapidHistory = cache.GetOrCreate(rapidKey, entry =>
        {
            entry.SlidingExpiration = RapidFireCacheExpiry;
            return new RapidFireHistory();
        })!;

        int hitsIn15s;
        long gapMs;
        bool subSecondDuplicate;
        lock (rapidHistory)
        {
            PruneTicks(rapidHistory.HitTicks, nowTicks - RapidFireWindowTicks);
            
            // Calculate gap from last hit
            if (rapidHistory.HitTicks.Count > 0)
            {
                var lastTick = rapidHistory.HitTicks[^1];
                gapMs = (nowTicks - lastTick) / TimeSpan.TicksPerMillisecond;
                subSecondDuplicate = gapMs < 1000;
            }
            else
            {
                gapMs = -1; // First hit
                subSecondDuplicate = false;
            }

            rapidHistory.HitTicks.Add(nowTicks);
            hitsIn15s = rapidHistory.HitTicks.Count;
        }

        return new IpBehaviorResult
        {
            // Subnet signals
            Subnet24 = subnet,
            SubnetUniqueIps = subnetUniqueIps,
            SubnetTotalHits = subnetTotalHits,
            SubnetVelocityAlert = subnetUniqueIps >= 3,

            // Rapid-fire signals
            HitsIn15Seconds = hitsIn15s,
            LastGapMs = gapMs,
            RapidFireAlert = hitsIn15s >= 3,
            SubSecondDuplicate = subSecondDuplicate
        };
    }

    /// <summary>
    /// Extracts the /24 subnet from an IPv4 address (e.g., "192.168.1.50" → "192.168.1").
    /// Returns null for IPv6 or invalid addresses.
    /// </summary>
    private static string? ExtractSubnet24(string ip)
    {
        // Quick IPv4 check — avoid allocations for the common case
        var lastDot = ip.LastIndexOf('.');
        if (lastDot <= 0 || ip.Contains(':')) return null; // IPv6 or invalid
        return ip[..lastDot];
    }

    private static void PruneTicks(List<long> ticks, long cutoff)
    {
        var pruneCount = 0;
        for (var i = 0; i < ticks.Count; i++)
        {
            if (ticks[i] >= cutoff) break;
            pruneCount++;
        }
        if (pruneCount > 0) ticks.RemoveRange(0, pruneCount);
    }

    private sealed class SubnetHistory
    {
        public HashSet<string> IpsInWindow { get; } = [];
        public List<long> HitTicks { get; } = [];
    }

    private sealed class RapidFireHistory
    {
        public List<long> HitTicks { get; } = [];
    }
}

/// <summary>
/// Immutable result of IP behavior analysis.
/// </summary>
public readonly record struct IpBehaviorResult
{
    // Subnet /24 velocity
    public string? Subnet24 { get; init; }
    public int SubnetUniqueIps { get; init; }
    public int SubnetTotalHits { get; init; }
    public bool SubnetVelocityAlert { get; init; }

    // Rapid-fire timing
    public int HitsIn15Seconds { get; init; }
    public long LastGapMs { get; init; }
    public bool RapidFireAlert { get; init; }
    public bool SubSecondDuplicate { get; init; }
}
