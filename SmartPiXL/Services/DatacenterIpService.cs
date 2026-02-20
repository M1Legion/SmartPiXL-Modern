using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SmartPiXL.Services;

// ============================================================================
// DATACENTER IP SERVICE — Cloud provider IP range detection.
//
// PURPOSE:
//   Detects whether an IP belongs to a known cloud/datacenter provider (AWS, GCP).
//   Bot farms frequently run on cloud infrastructure, so "datacenter IP" is a
//   strong signal for bot classification (not conclusive alone, but weighted).
//
// DATA SOURCES:
//   • AWS: https://ip-ranges.amazonaws.com/ip-ranges.json (~8,000 CIDR entries)
//   • GCP: https://www.gstatic.com/ipranges/cloud.json (~500 CIDR entries)
//   Both are official, machine-readable feeds maintained by the cloud providers.
//
// REFRESH STRATEGY:
//   • Initial load at startup (StartAsync)
//   • Weekly refresh via Timer (cloud providers add new ranges regularly)
//   • Failure tolerance: if refresh fails, the old trie remains active
//
// LOCK-FREE ARCHITECTURE:
//   The _trie field is a volatile reference to an immutable CidrTrie.
//   Writers build the trie on refresh, then atomically swap the reference.
//   Readers get a consistent snapshot via volatile read.
//
// CIDR MATCHING:
//   Uses a binary prefix trie (CidrTrie) for O(32) IPv4 / O(128) IPv6 lookups.
//   The previous linear scan of ~8,500 CIDRs has been replaced with bit-level
//   traversal: each bit of the IP address follows a trie edge, and the first
//   terminal node found is the matching CIDR.
// ============================================================================

/// <summary>
/// Hosted service that downloads AWS and GCP IP ranges on startup and weekly,
/// providing a lock-free CIDR check for incoming IPs.
/// <para>
/// Implements <see cref="IHostedService"/> for lifecycle management (start/stop)
/// and <see cref="IDisposable"/> for Timer cleanup.
/// </para>
/// </summary>
public sealed class DatacenterIpService : IHostedService, IDisposable
{
    private readonly ITrackingLogger _logger;
    private readonly HttpClient _httpClient;
    private Timer? _refreshTimer;
    
    /// <summary>
    /// Lock-free trie. Readers see a consistent snapshot via volatile read.
    /// Writers build a new CidrTrie on refresh and atomically swap the reference.
    /// <para>
    /// The trie is fully immutable after construction. Only the reference is swapped.
    /// </para>
    /// </summary>
    private volatile CidrTrie _trie = CidrTrie.Empty;

    /// <summary>Official AWS IP ranges endpoint (JSON, ~8K CIDRs including IPv4 + IPv6).</summary>
    private const string AwsUrl = "https://ip-ranges.amazonaws.com/ip-ranges.json";
    
    /// <summary>Official GCP cloud IP ranges endpoint (JSON, ~500 CIDRs).</summary>
    private const string GcpUrl = "https://www.gstatic.com/ipranges/cloud.json";

    public DatacenterIpService(ITrackingLogger logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        // Named HttpClient from the factory — uses the DI-configured "DatacenterIp" settings.
        // The factory manages handler lifetime and connection pooling.
        _httpClient = httpClientFactory.CreateClient("DatacenterIp");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Called by the host on startup. Downloads IP ranges, then starts a weekly refresh timer.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshRangesAsync(cancellationToken);
        // Timer fires every 7 days. The lambda discards the Timer state parameter
        // and fires RefreshRangesAsync with CancellationToken.None (no cancellation
        // support on timer callbacks — the refresh is best-effort).
        _refreshTimer = new Timer(_ => _ = RefreshRangesAsync(CancellationToken.None),
            null, TimeSpan.FromDays(7), TimeSpan.FromDays(7));
    }

    /// <summary>
    /// Called by the host on shutdown. Disables the refresh timer (does not dispose yet —
    /// Dispose() handles that to avoid potential timer callback during shutdown).
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _refreshTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks whether the given IP address falls within any known cloud provider CIDR range.
    /// <para>
    /// Zero-lock: reads a single volatile trie reference and walks it bit-by-bit.
    /// O(32) for IPv4, O(128) for IPv6 — vs O(8,500) for the old linear scan.
    /// Returns a stack-allocated <see cref="DatacenterCheckResult"/> — no GC pressure.
    /// </para>
    /// </summary>
    /// <param name="ipAddress">The client IP address to check.</param>
    /// <returns><c>default</c> (IsDatacenter=false) if not in any range, or a result with the provider name.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DatacenterCheckResult Check(string? ipAddress)
    {
        if (ipAddress is null || ipAddress.Length == 0 || !IPAddress.TryParse(ipAddress, out var ip))
            return default; // IsDatacenter=false, Provider=null

        return _trie.Lookup(ip); // Single volatile read + O(prefix_len) trie walk
    }

    /// <summary>
    /// Downloads AWS and GCP IP range lists and atomically replaces the in-memory array.
    /// <para>
    /// Each provider is loaded independently — if one fails, the other's ranges
    /// are still included. Only if BOTH fail (and newRanges is empty) do we
    /// keep the previous array (no swap occurs).
    /// </para>
    /// </summary>
    private async Task RefreshRangesAsync(CancellationToken ct)
    {
        _logger.Info("Refreshing datacenter IP ranges...");
        // Pre-allocate for ~8500 total expected ranges (AWS ~8000 + GCP ~500)
        var newRanges = new List<(string Cidr, string Provider)>(8000);

        // ---- AWS IP Ranges ----
        var awsCountBefore = 0;
        try
        {
            var json = await _httpClient.GetStringAsync(AwsUrl, ct);
            using var doc = JsonDocument.Parse(json);
            // AWS JSON format: { "prefixes": [{ "ip_prefix": "1.2.3.0/24", ... }], "ipv6_prefixes": [...] }
            foreach (var prefix in doc.RootElement.GetProperty("prefixes").EnumerateArray())
            {
                var cidr = prefix.GetProperty("ip_prefix").GetString();
                if (cidr is not null) newRanges.Add((cidr, "AWS"));
            }
            foreach (var prefix in doc.RootElement.GetProperty("ipv6_prefixes").EnumerateArray())
            {
                var cidr = prefix.GetProperty("ipv6_prefix").GetString();
                if (cidr is not null) newRanges.Add((cidr, "AWS"));
            }
            awsCountBefore = newRanges.Count;
            _logger.Info($"Loaded {awsCountBefore} AWS IP ranges");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to load AWS IP ranges", ex);
        }

        // ---- GCP IP Ranges ----
        try
        {
            var json = await _httpClient.GetStringAsync(GcpUrl, ct);
            using var doc = JsonDocument.Parse(json);
            // GCP JSON format: { "prefixes": [{ "ipv4Prefix": "1.2.3.0/24" } or { "ipv6Prefix": "..." }] }
            foreach (var prefix in doc.RootElement.GetProperty("prefixes").EnumerateArray())
            {
                if (prefix.TryGetProperty("ipv4Prefix", out var v4))
                    newRanges.Add((v4.GetString()!, "GCP"));
                else if (prefix.TryGetProperty("ipv6Prefix", out var v6))
                    newRanges.Add((v6.GetString()!, "GCP"));
            }
            _logger.Info($"Loaded {newRanges.Count - awsCountBefore} GCP IP ranges");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to load GCP IP ranges", ex);
        }

        if (newRanges.Count > 0)
        {
            // Build the immutable trie from the collected ranges.
            // CidrTrie.Build() parses each CIDR string once and constructs the
            // bit-level prefix tree. The volatile write makes the new trie
            // visible to all reader threads on the next volatile read.
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(newRanges);
            _trie = CidrTrie.Build(span);
            _logger.Info($"Total datacenter IP ranges loaded: {newRanges.Count} (trie built)");
        }
    }

    /// <summary>
    /// Disposes the weekly refresh timer.
    /// </summary>
    public void Dispose()
    {
        _refreshTimer?.Dispose();
    }
}

/// <summary>
/// Result of a datacenter IP check. Stack-allocated (<c>readonly record struct</c>).
/// <para>
/// The <c>default</c> value is <c>(false, null)</c> — the "not a datacenter IP" case.
/// This means Check() can return <c>default</c> for the common (non-datacenter) path
/// without allocating anything.
/// </para>
/// </summary>
public readonly record struct DatacenterCheckResult(bool IsDatacenter, string? Provider);
