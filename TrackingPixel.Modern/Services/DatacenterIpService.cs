using System.Net;
using System.Text.Json;

namespace TrackingPixel.Services;

/// <summary>
/// V-08: Detects if an IP belongs to a known cloud/datacenter provider.
/// Bot and scraper traffic typically originates from AWS, GCP, Azure, etc.
/// Downloads and caches official IP range lists on startup and refreshes weekly.
/// 
/// Registration: builder.Services.AddSingleton&lt;DatacenterIpService&gt;();
///               builder.Services.AddHostedService(sp => sp.GetRequiredService&lt;DatacenterIpService&gt;());
/// </summary>
public sealed class DatacenterIpService : IHostedService, IDisposable
{
    private readonly ILogger<DatacenterIpService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ReaderWriterLockSlim _rangeLock = new();
    private List<(string Cidr, string Provider)> _ranges = new();
    private Timer? _refreshTimer;

    // Official IP range endpoints
    private static readonly Dictionary<string, string> ProviderUrls = new()
    {
        ["AWS"] = "https://ip-ranges.amazonaws.com/ip-ranges.json",
        ["GCP"] = "https://www.gstatic.com/ipranges/cloud.json",
        // Azure ranges require periodic manual URL updates
        ["DigitalOcean"] = "https://digitalocean.com/geo/google.csv",
    };

    // Well-known datacenter ASN prefixes for quick heuristic checks
    private static readonly string[] DatacenterIndicators = [
        "Amazon", "Google Cloud", "Microsoft Azure", "DigitalOcean",
        "Linode", "Vultr", "OVH", "Hetzner", "Scaleway", "Contabo"
    ];

    public DatacenterIpService(ILogger<DatacenterIpService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("DatacenterIp");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshRangesAsync(cancellationToken);
        // Refresh weekly
        _refreshTimer = new Timer(_ => _ = RefreshRangesAsync(CancellationToken.None),
            null, TimeSpan.FromDays(7), TimeSpan.FromDays(7));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _refreshTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks if an IP address belongs to a known datacenter/cloud provider.
    /// Uses CIDR range matching loaded from provider IP lists.
    /// </summary>
    public DatacenterCheckResult Check(string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress) || !IPAddress.TryParse(ipAddress, out var ip))
            return new DatacenterCheckResult(false, null);

        _rangeLock.EnterReadLock();
        try
        {
            foreach (var (cidr, provider) in _ranges)
            {
                if (IsInCidr(ip, cidr))
                    return new DatacenterCheckResult(true, provider);
            }
        }
        finally
        {
            _rangeLock.ExitReadLock();
        }

        return new DatacenterCheckResult(false, null);
    }

    private async Task RefreshRangesAsync(CancellationToken ct)
    {
        _logger.LogInformation("Refreshing datacenter IP ranges...");
        var newRanges = new List<(string Cidr, string Provider)>();

        // AWS
        try
        {
            var json = await _httpClient.GetStringAsync(ProviderUrls["AWS"], ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var prefix in doc.RootElement.GetProperty("prefixes").EnumerateArray())
            {
                var cidr = prefix.GetProperty("ip_prefix").GetString();
                if (cidr != null) newRanges.Add((cidr, "AWS"));
            }
            // IPv6
            foreach (var prefix in doc.RootElement.GetProperty("ipv6_prefixes").EnumerateArray())
            {
                var cidr = prefix.GetProperty("ipv6_prefix").GetString();
                if (cidr != null) newRanges.Add((cidr, "AWS"));
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
            var json = await _httpClient.GetStringAsync(ProviderUrls["GCP"], ct);
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
            _rangeLock.EnterWriteLock();
            try
            {
                _ranges = newRanges;
            }
            finally
            {
                _rangeLock.ExitWriteLock();
            }
            _logger.LogInformation("Total datacenter IP ranges loaded: {Count}", newRanges.Count);
        }
    }

    /// <summary>
    /// Simple CIDR matching without external dependencies.
    /// Parses CIDR notation and checks if the IP falls within the range.
    /// </summary>
    private static bool IsInCidr(IPAddress ip, string cidr)
    {
        try
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2) return false;
            if (!IPAddress.TryParse(parts[0], out var network)) return false;
            if (!int.TryParse(parts[1], out var prefixLen)) return false;

            var ipBytes = ip.GetAddressBytes();
            var netBytes = network.GetAddressBytes();
            if (ipBytes.Length != netBytes.Length) return false;

            var fullBytes = prefixLen / 8;
            var remainBits = prefixLen % 8;

            for (int i = 0; i < fullBytes && i < ipBytes.Length; i++)
            {
                if (ipBytes[i] != netBytes[i]) return false;
            }

            if (remainBits > 0 && fullBytes < ipBytes.Length)
            {
                var mask = (byte)(0xFF << (8 - remainBits));
                if ((ipBytes[fullBytes] & mask) != (netBytes[fullBytes] & mask)) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _rangeLock.Dispose();
    }
}

public record DatacenterCheckResult(bool IsDatacenter, string? Provider);
