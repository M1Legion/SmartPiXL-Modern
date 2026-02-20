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
// CACHING: DnsClient handles internal caching based on TTL.
//
// APPENDED PARAMS:
//   _srv_rdns={hostname}     — Reverse DNS hostname (PTR record)
//   _srv_rdnsCloud=1         — Hostname matches a known cloud provider pattern
// ============================================================================

/// <summary>
/// Performs reverse DNS lookups and detects cloud provider hostname patterns.
/// Singleton — thread-safe. Uses DnsClient for async DNS resolution.
/// </summary>
public sealed partial class DnsLookupService
{
    private readonly LookupClient _dnsClient;
    private readonly ITrackingLogger _logger;
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(2);

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

    public DnsLookupService(ITrackingLogger logger)
    {
        _logger = logger;
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
    /// </summary>
    public async Task<DnsLookupResult> LookupAsync(string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ipAddress))
            return default;

        if (!IPAddress.TryParse(ipAddress, out var ip))
            return default;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(s_timeout);

            var result = await _dnsClient.QueryReverseAsync(ip, cts.Token);
            var ptrRecord = result.Answers.PtrRecords().FirstOrDefault();

            if (ptrRecord is null)
                return default;

            var hostname = ptrRecord.PtrDomainName.Value.TrimEnd('.');
            if (string.IsNullOrEmpty(hostname))
                return default;

            var isCloud = IsCloudHostname(hostname);
            return new DnsLookupResult(hostname, isCloud);
        }
        catch (OperationCanceledException)
        {
            // Timeout or pipeline shutdown — expected
            return default;
        }
        catch (DnsResponseException)
        {
            // NXDOMAIN, SERVFAIL, etc. — no PTR record, normal
            return default;
        }
        catch (Exception ex)
        {
            _logger.Debug($"DnsLookup: failed for {ipAddress} — {ex.Message}");
            return default;
        }
    }

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
