using Microsoft.Extensions.Caching.Memory;

namespace TrackingPixel.Services;

// ============================================================================
// IP BEHAVIOR SERVICE — Server-side cross-request traffic analysis.
//
// WHY SERVER-SIDE?
//   JavaScript can detect many bot signals, but it cannot correlate traffic
//   across different requests. Only the server can see:
//   1. Multiple IPs from the same /24 subnet hitting in quick succession
//   2. The exact timing gap between consecutive hits from the same IP
//
// SIGNAL 1 — SUBNET /24 VELOCITY:
//   Bot infrastructure (cloud VMs, Docker containers, residential proxies)
//   often shares a /24 subnet. When 3+ unique IPs from the same /24 hit
//   within 5 minutes, it indicates coordinated automation.
//   Example: 198.51.100.10, 198.51.100.42, 198.51.100.77 all within 2 min.
//
// SIGNAL 2 — RAPID-FIRE TIMING:
//   Same IP hitting multiple times within 15 seconds. Normal human browsing
//   doesn't generate multiple tracking pixel hits that fast (each page load
//   fires exactly one pixel). Two or more in 15s = automation.
//   Sub-second gap (< 1000ms) = definite automation (no human clicks that fast).
//
// MEMORY MODEL:
//   Two separate IMemoryCache entries per IP/subnet:
//   - "subnet:{/24}" with 10min sliding expiration (SubnetHistory)
//   - "rapid:{ip}" with 2min sliding expiration (RapidFireHistory)
//   Per-key locking: SubnetHistory and RapidFireHistory are locked individually.
//
// EXTRACTION:
//   IPv4 /24 subnet is extracted via Span-based LastIndexOf('.') — zero allocation.
//   IPv6 returns null (subnet analysis is only meaningful for IPv4 /24 blocks).
// ============================================================================

/// <summary>
/// Server-side IP behavior analyzer that detects subnet velocity and rapid-fire patterns.
/// <para>
/// Uses primary constructor parameter <c>cache</c> — the <see cref="IMemoryCache"/> instance
/// is shared with <see cref="FingerprintStabilityService"/> but keys are namespaced
/// with different prefixes (<c>"subnet:"</c>, <c>"rapid:"</c>) to avoid collisions.
/// </para>
/// </summary>
public sealed class IpBehaviorService(IMemoryCache cache)
{
    /// <summary>Sliding expiration for subnet tracking entries (10 minutes).</summary>
    private static readonly TimeSpan SubnetCacheExpiry = TimeSpan.FromMinutes(10);
    
    /// <summary>Sliding expiration for rapid-fire tracking entries (2 minutes).</summary>
    private static readonly TimeSpan RapidFireCacheExpiry = TimeSpan.FromMinutes(2);
    
    /// <summary>
    /// Subnet velocity window as raw ticks (5 minutes).
    /// Using ticks avoids TimeSpan struct allocation in the comparison hot path.
    /// </summary>
    private const long SubnetWindowTicks = TimeSpan.TicksPerMinute * 5;
    
    /// <summary>
    /// Rapid-fire window as raw ticks (15 seconds).
    /// Two or more hits from the same IP within this window triggers the alert.
    /// </summary>
    private const long RapidFireWindowTicks = TimeSpan.TicksPerSecond * 15;

    /// <summary>
    /// Records a hit from the given IP and returns subnet/timing analysis results.
    /// <para>
    /// Thread-safe via per-key locking — concurrent requests from different IPs
    /// and subnets do not contend. Only same-IP or same-subnet requests serialize.
    /// </para>
    /// </summary>
    /// <param name="ipAddress">The client IP address (after proxy header extraction).</param>
    /// <returns>An immutable <see cref="IpBehaviorResult"/> with all detection signals.</returns>
    public IpBehaviorResult RecordAndCheck(string ipAddress)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        
        // Extract /24 subnet: "192.168.1.50" → "192.168.1"
        // Returns null for IPv6 (contains ':') or invalid addresses
        var subnet = ExtractSubnet24(ipAddress);

        // ---- SIGNAL 1: Subnet /24 velocity ----
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
                // Prune entries (IP + timestamp pairs) outside the 5-minute window
                PruneEntries(subnetHistory.Entries, nowTicks - SubnetWindowTicks);
                subnetHistory.Entries.Add((ipAddress, nowTicks));

                subnetTotalHits = subnetHistory.Entries.Count;
                subnetUniqueIps = CountDistinctIps(subnetHistory.Entries);
            }
        }

        // ---- SIGNAL 2: Rapid-fire timing ----
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
            // Prune hits outside the 15-second window
            PruneTicks(rapidHistory.HitTicks, nowTicks - RapidFireWindowTicks);
            
            // Calculate millisecond gap from the most recent previous hit
            if (rapidHistory.HitTicks.Count > 0)
            {
                var lastTick = rapidHistory.HitTicks[^1]; // [^1] = last element (Index from end)
                gapMs = (nowTicks - lastTick) / TimeSpan.TicksPerMillisecond;
                // Sub-second duplicate: two pixel fires < 1000ms apart = definite automation.
                // No human navigates to a new page and fires a tracking pixel that fast.
                subSecondDuplicate = gapMs < 1000;
            }
            else
            {
                gapMs = -1; // Sentinel: first hit for this IP (no previous timestamp)
                subSecondDuplicate = false;
            }

            rapidHistory.HitTicks.Add(nowTicks);
            hitsIn15s = rapidHistory.HitTicks.Count;
        }

        return new IpBehaviorResult
        {
            // Subnet velocity signals
            Subnet24 = subnet,
            SubnetUniqueIps = subnetUniqueIps,
            SubnetTotalHits = subnetTotalHits,
            SubnetVelocityAlert = subnetUniqueIps >= 3, // 3+ unique IPs from same /24 in 5min

            // Rapid-fire timing signals
            HitsIn15Seconds = hitsIn15s,
            LastGapMs = gapMs,
            RapidFireAlert = hitsIn15s >= 3, // 3+ hits from same IP in 15sec
            SubSecondDuplicate = subSecondDuplicate
        };
    }

    /// <summary>
    /// Extracts the /24 subnet prefix from an IPv4 address.
    /// <para>
    /// Example: <c>"192.168.1.50"</c> → <c>"192.168.1"</c>
    /// </para>
    /// <para>
    /// Uses <see cref="ReadOnlySpan{T}"/> for the colon check and dot search
    /// to avoid intermediate string allocations. The final <c>ip[..lastDot]</c>
    /// does allocate one string (the subnet prefix) which is stored in the cache key.
    /// </para>
    /// </summary>
    /// <returns>The /24 subnet string, or <c>null</c> for IPv6 or invalid addresses.</returns>
    private static string? ExtractSubnet24(string ip)
    {
        var span = ip.AsSpan();
        // Quick IPv6 reject — any colon means it's an IPv6 address
        if (span.Contains(':')) return null;
        // Find the last dot to split off the final octet
        var lastDot = span.LastIndexOf('.');
        if (lastDot <= 0) return null; // No dots or leading dot = invalid IPv4
        // Return everything before the last dot (the /24 prefix)
        return ip[..lastDot];
    }

    /// <summary>
    /// Removes tick timestamps from the front of a sorted list that fall before the cutoff.
    /// <para>
    /// The list is maintained in insertion order (oldest first) because we always
    /// append at the end. This means stale entries are always at the front,
    /// so we scan forward until we find the first entry >= cutoff, then remove
    /// everything before it in a single <c>RemoveRange</c> call.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Removes (IP, Ticks) entries from the front of a sorted list that fall before the cutoff.
    /// <para>
    /// Fixes the prior bug where IPs accumulated in a separate <c>HashSet&lt;string&gt;</c>
    /// beyond the tick window. By pairing each IP with its timestamp, pruning the
    /// timestamp also prunes the IP identity. This ensures <see cref="CountDistinctIps"/>
    /// only counts IPs with at least one hit within the sliding window.
    /// </para>
    /// <para>
    /// Like <see cref="PruneTicks"/>, the list is maintained in insertion order (oldest first)
    /// so stale entries are always at the front — a single forward scan + <c>RemoveRange</c>.
    /// </para>
    /// </summary>
    /// <param name="entries">The (IP, Ticks) entry list (mutated in-place).</param>
    /// <param name="cutoff">Oldest allowed tick value — entries before this are removed.</param>
    private static void PruneEntries(List<(string Ip, long Ticks)> entries, long cutoff)
    {
        var pruneCount = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Ticks >= cutoff) break;
            pruneCount++;
        }
        if (pruneCount > 0) entries.RemoveRange(0, pruneCount);
    }

    /// <summary>
    /// Counts distinct IPs in a sorted entry list without allocating a HashSet.
    /// <para>
    /// O(n²) nested loop but <b>allocation-free</b> — ideal for the typical 3–15 entries
    /// per subnet window. At these sizes, the quadratic cost is negligible compared to
    /// the cost of a HashSet allocation + hashing. For a worst-case /24 with 256 IPs,
    /// the inner loop processes ~32K string comparisons — still sub-microsecond on
    /// modern CPUs with <see cref="StringComparison.Ordinal"/> (branchless byte scan).
    /// </para>
    /// <para>
    /// Ordinal comparison is deliberate: IP strings are ASCII-only and case-sensitive,
    /// so ordinal is both correct and fastest (no culture/case folding overhead).
    /// </para>
    /// </summary>
    /// <param name="entries">The pruned (IP, Ticks) entry list from a subnet window.</param>
    /// <returns>The number of unique IP addresses present in the entry list.</returns>
    private static int CountDistinctIps(List<(string Ip, long Ticks)> entries)
    {
        var count = entries.Count;
        if (count <= 1) return count;

        var unique = 1;
        for (var i = 1; i < count; i++)
        {
            var isNew = true;
            var ip = entries[i].Ip;
            for (var j = 0; j < i; j++)
            {
                if (string.Equals(ip, entries[j].Ip, StringComparison.Ordinal))
                {
                    isNew = false;
                    break;
                }
            }
            if (isNew) unique++;
        }
        return unique;
    }

    /// <summary>
    /// Tracks all IPs and hit timestamps within a /24 subnet's cache entry.
    /// <para>
    /// Uses combined <c>(IP, Ticks)</c> entries so IPs are correctly pruned with
    /// their timestamps. This replaces the prior design which used a separate
    /// <c>HashSet&lt;string&gt; IpsInWindow</c> alongside <c>List&lt;long&gt; HitTicks</c>.
    /// That design had a bug: when ticks were pruned, the corresponding IPs were
    /// not removed from the HashSet, causing <c>SubnetUniqueIps</c> to inflate
    /// over the lifetime of the cache entry (up to 10 min sliding expiration).
    /// </para>
    /// </summary>
    private sealed class SubnetHistory
    {
        /// <summary>
        /// Hit entries pairing IP identity with timestamp, ordered oldest-first.
        /// Pruned by <see cref="PruneEntries"/> to keep only entries within the 5-min window.
        /// </summary>
        public List<(string Ip, long Ticks)> Entries { get; } = [];
    }

    /// <summary>
    /// Tracks hit timestamps for a single IP's rapid-fire detection.
    /// </summary>
    private sealed class RapidFireHistory
    {
        /// <summary>Hit timestamps as raw ticks within the 15-second window, ordered oldest-first.</summary>
        public List<long> HitTicks { get; } = [];
    }
}

/// <summary>
/// Immutable result of IP behavior analysis.
/// <para>
/// <c>readonly record struct</c> — stack-allocated, no GC pressure.
/// Contains both subnet velocity and rapid-fire timing signals.
/// </para>
/// </summary>
public readonly record struct IpBehaviorResult
{
    // ---- Subnet /24 velocity signals ----
    
    /// <summary>The /24 subnet prefix (e.g., "192.168.1"), or null for IPv6.</summary>
    public string? Subnet24 { get; init; }
    
    /// <summary>Number of unique IPs seen from this /24 subnet in the 5-minute window.</summary>
    public int SubnetUniqueIps { get; init; }
    
    /// <summary>Total hit count from this /24 subnet in the 5-minute window.</summary>
    public int SubnetTotalHits { get; init; }
    
    /// <summary>True when 3+ unique IPs from the same /24 hit within 5 minutes.</summary>
    public bool SubnetVelocityAlert { get; init; }

    // ---- Rapid-fire timing signals ----
    
    /// <summary>Number of hits from this exact IP in the last 15 seconds.</summary>
    public int HitsIn15Seconds { get; init; }
    
    /// <summary>Milliseconds since the previous hit from this IP (-1 = first hit).</summary>
    public long LastGapMs { get; init; }
    
    /// <summary>True when 3+ hits from the same IP within 15 seconds.</summary>
    public bool RapidFireAlert { get; init; }
    
    /// <summary>True when two consecutive hits are less than 1 second apart (definite automation).</summary>
    public bool SubSecondDuplicate { get; init; }
}
