using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
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
// SQL CACHE: Results are persisted to IPAPI.WhoisCache. On startup, the
// in-memory BoundedCache is pre-warmed from SQL (last 30 days). After each
// fresh external WHOIS query, the result is written to SQL fire-and-forget.
// This eliminates hours of re-warming after service restarts.
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
    private readonly BoundedCache<string, WhoisResult> _cache;
    private const int MaxCacheSize = 200_000;
    private const int EvictTarget = 100_000;

    /// <summary>Current cache entry count (for health tree sampling).</summary>
    public int CacheCount => _cache?.Count ?? 0;
    private readonly ITrackingLogger _logger;
    private readonly string? _connectionString;
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Result of a WHOIS ASN lookup.
    /// </summary>
    public readonly record struct WhoisResult(string? Asn, string? Organization);

    /// <summary>
    /// Non-blocking cache-only check. Returns the cached result if available,
    /// or null if the IP hasn't been looked up yet. Used by the enrichment
    /// pipeline for zero-latency inline reads (Lane 1). Background workers
    /// populate the cache via <see cref="LookupAsync"/> (Lane 3).
    /// </summary>
    public WhoisResult? TryGetCached(string? ipAddress)
    {
        if (ipAddress is null) return null;
        return _cache.TryGet(ipAddress, out var result) ? result : null;
    }

    public WhoisAsnService(ITrackingLogger logger, IOptions<TrackingSettings>? trackingSettings = null)
    {
        _logger = logger;
        _connectionString = trackingSettings?.Value.ConnectionString;
        _cache = new BoundedCache<string, WhoisResult>(
            MaxCacheSize, EvictTarget, TimeSpan.FromMinutes(30), StringComparer.Ordinal);

        // Pre-warm from SQL cache (fire-and-forget, non-blocking)
        if (_connectionString is not null)
            _ = Task.Run(() => PreWarmFromSqlAsync());
    }

    /// <summary>
    /// Loads recent WHOIS results from SQL to pre-warm the in-memory cache.
    /// Runs once at startup. Failures are logged and swallowed.
    /// </summary>
    private async Task PreWarmFromSqlAsync()
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new SqlCommand("IPAPI.usp_WhoisCache_Load", conn)
            {
                CommandType = System.Data.CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@MaxAgeDays", 30);

            await using var reader = await cmd.ExecuteReaderAsync();
            var count = 0;
            while (await reader.ReadAsync())
            {
                var ip = reader.GetString(0);
                var asn = reader.IsDBNull(1) ? null : reader.GetString(1);
                var org = reader.IsDBNull(2) ? null : reader.GetString(2);

                if (asn is not null || org is not null)
                    _cache.Set(ip, new WhoisResult(asn, org));

                count++;
            }

            _logger.Info($"WhoisAsn: Pre-warmed {count:N0} entries from SQL cache");
        }
        catch (Exception ex)
        {
            _logger.Warning($"WhoisAsn: SQL pre-warm failed — {ex.Message}");
        }
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
            ipAddress.StartsWith("127.", StringComparison.Ordinal) ||
            IsPrivate172(ipAddress))
            return default;

        // Lock-free cache lookup — WHOIS is 1-5s per query, caching is critical.
        // IP cardinality ~38% unique per 100K records = ~62% cache hit rate.
        if (_cache.TryGet(ipAddress, out var cached))
            return cached;

        var result = await LookupCoreAsync(ipAddress, ct);

        // Cache both hits and misses (misses = default) to avoid retrying
        _cache.Set(ipAddress, result);

        // Persist to SQL for cross-restart survival (fire-and-forget)
        if (result.Asn is not null || result.Organization is not null)
            _ = Task.Run(() => PersistToSqlAsync(ipAddress, result));

        return result;
    }

    /// <summary>
    /// Evicts stale/excess entries from the WHOIS cache. Called periodically
    /// by BackgroundIpEnrichmentService.
    /// </summary>
    public int EvictCache() => _cache.Evict();

    /// <summary>
    /// Persists a WHOIS result to SQL for cross-restart cache survival.
    /// Fire-and-forget — failures are logged and swallowed.
    /// </summary>
    private async Task PersistToSqlAsync(string ipAddress, WhoisResult result)
    {
        if (_connectionString is null) return;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = new SqlCommand("IPAPI.usp_WhoisCache_Upsert", conn)
            {
                CommandType = System.Data.CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@IPAddress", ipAddress);
            cmd.Parameters.AddWithValue("@Asn", (object?)result.Asn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Organization", (object?)result.Organization ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.Debug($"WhoisAsn: SQL persist failed for {ipAddress} — {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if an IP is in the RFC 1918 172.16.0.0–172.31.255.255 private range.
    /// </summary>
    private static bool IsPrivate172(string ip)
    {
        if (!ip.StartsWith("172.", StringComparison.Ordinal)) return false;
        var dot2 = ip.IndexOf('.', 4);
        if (dot2 < 0) return false;
        return int.TryParse(ip.AsSpan(4, dot2 - 4), out var octet2) && octet2 >= 16 && octet2 <= 31;
    }

    /// <summary>
    /// Core WHOIS lookup — only called on cache miss.
    /// </summary>
    private async Task<WhoisResult> LookupCoreAsync(string ipAddress, CancellationToken ct)
    {
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
