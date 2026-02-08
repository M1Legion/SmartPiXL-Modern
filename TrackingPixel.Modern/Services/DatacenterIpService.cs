using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace TrackingPixel.Services;

/// <summary>
/// V-08: Detects if an IP belongs to a known cloud/datacenter provider.
/// Downloads official IP range lists on startup, refreshes weekly.
/// Uses lock-free volatile reference swap — readers never block.
/// </summary>
public sealed class DatacenterIpService : IHostedService, IDisposable
{
    private readonly ILogger<DatacenterIpService> _logger;
    private readonly HttpClient _httpClient;
    private Timer? _refreshTimer;
    
    // Lock-free: readers see a consistent snapshot via volatile read.
    // Writers atomically swap the entire reference — no ReaderWriterLockSlim needed.
    private volatile (string Cidr, string Provider)[] _ranges = [];

    // Official IP range endpoints (cold path — only used on refresh)
    private const string AwsUrl = "https://ip-ranges.amazonaws.com/ip-ranges.json";
    private const string GcpUrl = "https://www.gstatic.com/ipranges/cloud.json";

    public DatacenterIpService(ILogger<DatacenterIpService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("DatacenterIp");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshRangesAsync(cancellationToken);
        _refreshTimer = new Timer(_ => _ = RefreshRangesAsync(CancellationToken.None),
            null, TimeSpan.FromDays(7), TimeSpan.FromDays(7));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _refreshTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Zero-lock CIDR check. Volatile read gets a stable snapshot.
    /// Returns a stack-allocated readonly record struct — no GC pressure.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DatacenterCheckResult Check(string? ipAddress)
    {
        if (ipAddress is null || ipAddress.Length == 0 || !IPAddress.TryParse(ipAddress, out var ip))
            return default; // IsDatacenter=false, Provider=null

        var snapshot = _ranges; // Single volatile read — snapshot is immutable
        foreach (var (cidr, provider) in snapshot)
        {
            if (IsInCidr(ip, cidr))
                return new DatacenterCheckResult(true, provider);
        }

        return default;
    }

    private async Task RefreshRangesAsync(CancellationToken ct)
    {
        _logger.LogInformation("Refreshing datacenter IP ranges...");
        var newRanges = new List<(string Cidr, string Provider)>(8000);

        // AWS
        try
        {
            var json = await _httpClient.GetStringAsync(AwsUrl, ct);
            using var doc = JsonDocument.Parse(json);
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
            _logger.LogInformation("Loaded {Count} AWS IP ranges", newRanges.Count(r => r.Provider == "AWS"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load AWS IP ranges");
        }

        // GCP
        try
        {
            var json = await _httpClient.GetStringAsync(GcpUrl, ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var prefix in doc.RootElement.GetProperty("prefixes").EnumerateArray())
            {
                if (prefix.TryGetProperty("ipv4Prefix", out var v4))
                    newRanges.Add((v4.GetString()!, "GCP"));
                else if (prefix.TryGetProperty("ipv6Prefix", out var v6))
                    newRanges.Add((v6.GetString()!, "GCP"));
            }
            _logger.LogInformation("Loaded {Count} GCP IP ranges", newRanges.Count(r => r.Provider == "GCP"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load GCP IP ranges");
        }

        if (newRanges.Count > 0)
        {
            // Atomic swap — readers see old array until this completes, then new array
            _ranges = [.. newRanges]; // Collection expression → immutable array snapshot
            _logger.LogInformation("Total datacenter IP ranges loaded: {Count}", newRanges.Count);
        }
    }

    /// <summary>
    /// CIDR match using Span-based slash parsing — avoids string.Split allocation.
    /// Byte-level prefix comparison with bitwise mask on remaining bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInCidr(IPAddress ip, string cidr)
    {
        var span = cidr.AsSpan();
        var slashIdx = span.IndexOf('/');
        if (slashIdx < 0) return false;

        if (!IPAddress.TryParse(span[..slashIdx], out var network)) return false;
        if (!int.TryParse(span[(slashIdx + 1)..], out var prefixLen)) return false;

        var ipBytes = ip.GetAddressBytes();
        var netBytes = network.GetAddressBytes();
        if (ipBytes.Length != netBytes.Length) return false;

        var fullBytes = prefixLen >> 3;  // div 8
        var remainBits = prefixLen & 7;  // mod 8

        for (var i = 0; i < fullBytes & i < ipBytes.Length; i++)
        {
            if (ipBytes[i] != netBytes[i]) return false;
        }

        if (remainBits > 0 & fullBytes < ipBytes.Length)
        {
            var mask = (byte)(0xFF << (8 - remainBits));
            if ((ipBytes[fullBytes] & mask) != (netBytes[fullBytes] & mask)) return false;
        }

        return true;
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
    }
}

/// <summary>
/// Stack-allocated result — no GC pressure on every Check() call.
/// default value = (false, null) which is the "not datacenter" case.
/// </summary>
public readonly record struct DatacenterCheckResult(bool IsDatacenter, string? Provider);
