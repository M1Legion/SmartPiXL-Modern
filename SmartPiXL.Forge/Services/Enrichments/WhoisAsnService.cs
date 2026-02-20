using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services.Enrichments;

// ============================================================================
// WHOIS ASN SERVICE — ASN/organization lookup via WHOIS protocol.
//
// Supplements MaxMind ASN data for IPs where MaxMind has no ASN info.
// Uses the Whois NuGet package for raw WHOIS queries.
//
// LOW PRIORITY: WHOIS servers are slow (1-5 seconds per query) and may
// rate-limit. This service should not block the pipeline — async with
// graceful timeout and "best effort" semantics.
//
// APPENDED PARAMS:
//   _srv_whoisASN={AS number or name}
//   _srv_whoisOrg={organization name}
// ============================================================================

/// <summary>
/// WHOIS-based ASN/organization lookup. Singleton, thread-safe.
/// Designed as a supplementary enrichment — only called when MaxMind ASN is empty.
/// </summary>
public sealed class WhoisAsnService
{
    private readonly ITrackingLogger _logger;
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Result of a WHOIS ASN lookup.
    /// </summary>
    public readonly record struct WhoisResult(string? Asn, string? Organization);

    public WhoisAsnService(ITrackingLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Performs a WHOIS lookup for the given IP address to extract ASN and organization info.
    /// Timeout after 5 seconds. Returns default on failure.
    /// </summary>
    public async Task<WhoisResult> LookupAsync(string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ipAddress))
            return default;

        // Skip private/reserved IPs
        if (ipAddress.StartsWith("10.", StringComparison.Ordinal) ||
            ipAddress.StartsWith("192.168.", StringComparison.Ordinal) ||
            ipAddress.StartsWith("127.", StringComparison.Ordinal))
            return default;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(s_timeout);

            // Run WHOIS query on thread pool to avoid blocking the pipeline
            var whois = new Whois.WhoisLookup();
            var response = await Task.Run(() => whois.Lookup(ipAddress), cts.Token);

            if (response is null)
                return default;

            var rawText = response.Content;
            if (string.IsNullOrEmpty(rawText))
                return default;

            var asn = ExtractField(rawText, "OriginAS:", "origin:");
            var org = ExtractField(rawText, "OrgName:", "org-name:", "descr:");

            if (asn is null && org is null)
                return default;

            return new WhoisResult(asn, org);
        }
        catch (OperationCanceledException)
        {
            return default; // Timeout or pipeline shutdown
        }
        catch (Exception ex)
        {
            _logger.Debug($"WhoisAsn: Failed for {ipAddress} — {ex.Message}");
            return default;
        }
    }

    /// <summary>
    /// Extracts a field value from raw WHOIS text by searching for any of the given field names.
    /// WHOIS format: "FieldName:     value" (one per line, whitespace-padded).
    /// </summary>
    private static string? ExtractField(string rawText, params string[] fieldNames)
    {
        foreach (var fieldName in fieldNames)
        {
            var idx = rawText.IndexOf(fieldName, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            var valueStart = idx + fieldName.Length;
            // Skip whitespace after the colon
            while (valueStart < rawText.Length && rawText[valueStart] is ' ' or '\t')
                valueStart++;

            // Read to end of line
            var valueEnd = rawText.IndexOf('\n', valueStart);
            if (valueEnd < 0) valueEnd = rawText.Length;

            var value = rawText[valueStart..valueEnd].Trim().TrimEnd('\r');
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        return null;
    }
}
