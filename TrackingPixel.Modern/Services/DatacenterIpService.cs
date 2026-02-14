using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace TrackingPixel.Services;

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
//   • Failure tolerance: if refresh fails, the old ranges remain active
//
// LOCK-FREE ARCHITECTURE:
//   The _ranges field is a volatile reference to an immutable array.
//   Writers build a new array on a background thread, then atomically swap
//   the reference via volatile write. Readers get a consistent snapshot via
//   volatile read — no ReaderWriterLockSlim, no locks, no contention.
//
//   Writer: _ranges = [.. newList];  // atomic reference assignment
//   Reader: var snapshot = _ranges;  // single volatile read, then iterate
//
// CIDR MATCHING:
//   Uses stackalloc + TryWriteBytes for zero-heap-allocation byte comparison.
//   Prefix length is decomposed via bit shift (div 8) and bitmask (mod 8).
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
    /// Lock-free range array. Readers see a consistent snapshot via volatile read.
    /// Writers build a new array and atomically swap the reference — no locking needed.
    /// <para>
    /// The array itself is immutable after creation (created from a collection expression
    /// <c>[.. newRanges]</c>). Only the reference is swapped.
    /// </para>
    /// </summary>
    private volatile (string Cidr, string Provider)[] _ranges = [];

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
    /// Zero-lock: reads a single volatile reference (snapshot) and iterates the immutable array.
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

        var snapshot = _ranges; // Single volatile read — snapshot is immutable from here
        foreach (var (cidr, provider) in snapshot)
        {
            if (IsInCidr(ip, cidr))
                return new DatacenterCheckResult(true, provider);
        }

        return default;
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
            // Atomic reference swap: collection expression [.. newRanges] creates
            // a new immutable array. The volatile write makes the new array visible
            // to all reader threads on the next volatile read.
            _ranges = [.. newRanges];
            _logger.Info($"Total datacenter IP ranges loaded: {newRanges.Count}");
        }
    }

    /// <summary>
    /// Checks whether an <see cref="IPAddress"/> falls within a CIDR range.
    /// <para>
    /// ALLOCATION-FREE: Uses <c>stackalloc byte[16]</c> + <c>TryWriteBytes</c> to
    /// avoid the heap allocation from <c>GetAddressBytes()</c>.
    /// </para>
    /// <para>
    /// Prefix matching algorithm:
    /// <list type="number">
    ///   <item><description>Parse CIDR string: "1.2.3.0/24" → network IP + prefix length 24</description></item>
    ///   <item><description>Decompose prefix length: fullBytes = 24 >> 3 = 3, remainBits = 24 &amp; 7 = 0</description></item>
    ///   <item><description>Compare first <c>fullBytes</c> bytes for exact equality</description></item>
    ///   <item><description>If <c>remainBits > 0</c>, mask the next byte and compare</description></item>
    /// </list>
    /// Bit-shift division (<c>>> 3</c>) and bitmask modulo (<c>&amp; 7</c>) are single
    /// CPU instructions — no integer division overhead.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInCidr(IPAddress ip, string cidr)
    {
        // Parse CIDR notation: "10.0.0.0/8" → slash at index 8
        var span = cidr.AsSpan();
        var slashIdx = span.IndexOf('/');
        if (slashIdx < 0) return false;

        if (!IPAddress.TryParse(span[..slashIdx], out var network)) return false;
        if (!int.TryParse(span[(slashIdx + 1)..], out var prefixLen)) return false;

        // stackalloc both byte buffers — zero heap allocation for the comparison
        Span<byte> ipBytes = stackalloc byte[16];
        Span<byte> netBytes = stackalloc byte[16];

        if (!ip.TryWriteBytes(ipBytes, out var ipLen)) return false;
        if (!network.TryWriteBytes(netBytes, out var netLen)) return false;
        
        // IPv4 and IPv6 can't be in the same CIDR range
        if (ipLen != netLen) return false;

        // Decompose prefix length into full bytes and remaining bits
        var fullBytes = prefixLen >> 3;  // div 8 via right bit shift (single CPU instruction)
        var remainBits = prefixLen & 7;  // mod 8 via bitmask (single CPU instruction)

        // Compare the full bytes of the network prefix (byte-for-byte equality)
        for (var i = 0; i < fullBytes & i < ipLen; i++)
        {
            if (ipBytes[i] != netBytes[i]) return false;
        }

        // Compare remaining bits if the prefix length isn't byte-aligned
        // Example: /10 → fullBytes=1, remainBits=2 → mask=0b11000000=0xC0
        if (remainBits > 0 & fullBytes < ipLen)
        {
            var mask = (byte)(0xFF << (8 - remainBits));
            if ((ipBytes[fullBytes] & mask) != (netBytes[fullBytes] & mask)) return false;
        }

        return true;
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
