using System.Net;
using System.Text.RegularExpressions;
using DnsClient;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services.Enrichments;

// ============================================================================
// DNS LOOKUP SERVICE — Async reverse DNS (PTR) lookups via DnsClient.
//
// Resolves IP addresses to hostnames and detects cloud provider patterns
// from the hostname (ec2-*, compute.googleapis.com, etc.).
//
// TIMEOUT: 2 seconds per lookup — will not block the enrichment pipeline.
// CACHING: Two tiers:
//   1. Application-level BoundedCache — caches ALL results (including
//      NXDOMAIN / timeout = default) with hybrid time+count eviction.
//      Eviction called periodically by BackgroundIpEnrichmentService.
//      This prevents repeated 2s DNS timeouts for the same IP.
//   2. DnsClient internal cache — TTL-based caching per DNS spec.
//
// APPENDED PARAMS:
//   _srv_rdns={hostname}     — Reverse DNS hostname (PTR record)
//   _srv_rdnsCloud=1         — Hostname matches a known cloud provider pattern
// ============================================================================

/// <summary>
/// Performs reverse DNS lookups and detects cloud provider hostname patterns.
/// Singleton — thread-safe. Uses DnsClient for async DNS resolution with
/// application-level result caching.
/// </summary>
public sealed partial class DnsLookupService
{
    private readonly LookupClient _dnsClient;
    private readonly ITrackingLogger _logger;
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(2);

    // Application-level cache: IP → DnsLookupResult (including "no result" = default).
    // Prevents repeated 2s DNS lookups for the same IP across concurrent workers.
    // Hybrid eviction (time + count) via BoundedCache — no more nuclear Clear().
    private readonly BoundedCache<string, DnsLookupResult> _cache;
    private const int MaxCacheSize = 200_000;
    private const int EvictTarget = 100_000;

    /// <summary>Current cache entry count (for health tree sampling).</summary>
    public int CacheCount => _cache?.Count ?? 0;

    // Pre-compiled cloud hostname patterns
    [GeneratedRegex(@"(ec2-|\.compute\.amazonaws\.com|\.compute\.internal|\.compute-1\.amazonaws\.com)", RegexOptions.IgnoreCase)]
    private static partial Regex AwsPattern();

    [GeneratedRegex(@"(\.googleusercontent\.com|\.google\.com|\.1e100\.net|\.bc\.googleusercontent\.com)", RegexOptions.IgnoreCase)]
    private static partial Regex GcpPattern();

    [GeneratedRegex(@"(\.cloudapp\.azure\.com|\.azurewebsites\.net|\.azure\.com|\.windows\.net)", RegexOptions.IgnoreCase)]
    private static partial Regex AzurePattern();

    [GeneratedRegex(@"(\.digitaloceanspaces\.com|\.digitalocean\.com)", RegexOptions.IgnoreCase)]
    private static partial Regex DigitalOceanPattern();

    [GeneratedRegex(@"(\.linode\.com|\.akamai\.com|\.akamaiedge\.net|\.akamaized\.net)", RegexOptions.IgnoreCase)]
    private static partial Regex AkamaiPattern();

    [GeneratedRegex(@"(\.cloudflare\.com|\.cloudflare-dns\.com)", RegexOptions.IgnoreCase)]
    private static partial Regex CloudflarePattern();

    [GeneratedRegex(@"(\.ovh\.(net|com)|\.online\.net|\.scaleway\.com|\.hetzner\.(com|de))", RegexOptions.IgnoreCase)]
    private static partial Regex EuCloudPattern();

    /// <summary>
    /// Result of a reverse DNS lookup.
    /// </summary>
    public readonly record struct DnsLookupResult(string? Hostname, bool IsCloud);

    /// <summary>
    /// Non-blocking cache-only check. Returns the cached result if available,
    /// or null if the IP hasn't been looked up yet. Used by the enrichment
    /// pipeline for zero-latency inline reads (Lane 1). Background workers
    /// populate the cache via <see cref="LookupAsync"/> (Lane 3).
    /// </summary>
    public DnsLookupResult? TryGetCached(string? ipAddress)
    {
        if (ipAddress is null) return null;
        return _cache.TryGet(ipAddress, out var result) ? result : null;
    }

    public DnsLookupService(ITrackingLogger logger)
    {
        _logger = logger;
        _cache = new BoundedCache<string, DnsLookupResult>(
            MaxCacheSize, EvictTarget, TimeSpan.FromMinutes(30), StringComparer.Ordinal);
        var options = new LookupClientOptions
        {
            Timeout = s_timeout,
            Retries = 0,
            UseCache = true,
            ThrowDnsErrors = false
        };
        _dnsClient = new LookupClient(options);
    }

    /// <summary>
    /// Performs a reverse DNS lookup for the given IP address.
    /// Returns the hostname (if available) and whether it matches a cloud provider pattern.
    /// Results are cached at the application level to prevent repeated 2s DNS
    /// lookups for the same IP across concurrent enrichment workers.
    /// </summary>
    public async Task<DnsLookupResult> LookupAsync(string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ipAddress))
            return default;

        // Application-level cache hit — includes "no PTR" (default) results
        if (_cache.TryGet(ipAddress, out var cached))
            return cached;

        if (!IPAddress.TryParse(ipAddress, out var ip))
            return default;

        DnsLookupResult result = default;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(s_timeout);

            var dnsResponse = await _dnsClient.QueryReverseAsync(ip, cts.Token);

            // Avoid LINQ FirstOrDefault() — enumerate directly to find the first PTR record
            DnsClient.Protocol.PtrRecord? ptrRecord = null;
            foreach (var record in dnsResponse.Answers.PtrRecords())
            {
                ptrRecord = record;
                break;
            }

            if (ptrRecord is not null)
            {
                var hostname = ptrRecord.PtrDomainName.Value.TrimEnd('.');
                if (!string.IsNullOrEmpty(hostname))
                {
                    var isCloud = IsCloudHostname(hostname);
                    result = new DnsLookupResult(hostname, isCloud);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout or pipeline shutdown — cache the miss to avoid retrying
        }
        catch (DnsResponseException)
        {
            // NXDOMAIN, SERVFAIL, etc. — no PTR record, cache the miss
        }
        catch (Exception ex)
        {
            _logger.Debug($"DnsLookup: failed for {ipAddress} — {ex.Message}");
            // Cache the miss so we don't retry this IP
        }

        // Cache both hits and misses. Eviction handled by periodic Evict() call.
        _cache.Set(ipAddress, result);
        return result;
    }

    /// <summary>
    /// Evicts stale/excess entries from the DNS cache. Called periodically
    /// by BackgroundIpEnrichmentService.
    /// </summary>
    public int EvictCache() => _cache.Evict();

    /// <summary>
    /// Checks if the hostname matches known cloud/datacenter provider patterns.
    /// </summary>
    private static bool IsCloudHostname(string hostname)
    {
        return AwsPattern().IsMatch(hostname)
            || GcpPattern().IsMatch(hostname)
            || AzurePattern().IsMatch(hostname)
            || DigitalOceanPattern().IsMatch(hostname)
            || AkamaiPattern().IsMatch(hostname)
            || CloudflarePattern().IsMatch(hostname)
            || EuCloudPattern().IsMatch(hostname);
    }
}
