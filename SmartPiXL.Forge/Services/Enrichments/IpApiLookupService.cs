using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services.Enrichments;

// ============================================================================
// IPAPI LOOKUP SERVICE — Real-time IP geolocation via the ip-api.com Pro API.
//
// Per record: inline SQL check (clustered PK seek on IPAPI.IP, sub-ms) to see
// if the IP is known+fresh (<90 days). If not, call the Pro API (rate-limited).
//
// Previous design loaded all 344M rows from IPAPI.IP into a ConcurrentDictionary
// at startup — took 2+ hours and ~15GB RAM, blocking the pipeline. Replaced
// with inline SQL EXISTS checks that are instant on the clustered PK index.
// IPs looked up via SQL are cached locally in _knownIps to avoid repeat queries.
//
// API: https://pro.ip-api.com/json/{ip}?key={key}&fields=...
// Rate limit: 30 req/min (Pro basic tier). Uses SemaphoreSlim for throttling.
//
// Results are written back to IPAPI.IP and the IP is added to the in-memory set.
// This service supplements MaxMind for IPs that MaxMind doesn't cover well
// (mobile carriers, satellite ISPs, VPNs).
//
// APPENDED PARAMS:
//   _srv_ipapiCC={country code}
//   _srv_ipapiISP={ISP name}
//   _srv_ipapiProxy=1  (if proxy/VPN detected)
//   _srv_ipapiMobile=1 (if mobile carrier)
//   _srv_ipapiReverse={reverse DNS}
//   _srv_ipapiASN={AS number}
// ============================================================================

/// <summary>
/// Real-time IP geolocation enrichment via ip-api.com Pro.
/// Singleton — thread-safe. Uses inline SQL checks + progressive in-memory cache.
/// Rate-limited to 30 requests per minute via a semaphore + delay.
/// </summary>
public sealed class IpApiLookupService : IDisposable
{
    private readonly ConcurrentDictionary<string, DateTime> _knownIps = new();
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private readonly HttpClient _httpClient;
    private readonly string _connectionString;
    private readonly ITrackingLogger _logger;
    private bool _disposed;

    // Result cache for inline pipeline reads — populated by CallApiAsync,
    // read by TryGetCached. Separate from _knownIps (which only tracks freshness).
    private readonly ConcurrentDictionary<string, IpApiResult> _resultCache = new(StringComparer.Ordinal);
    private const int MaxResultCacheSize = 200_000;

    private const string ApiKey = "oJC4NplwJaCnbWw";
    private const string ApiFields = "status,message,country,countryCode,regionName,city,zip,lat,lon,timezone,isp,org,as,reverse,mobile,proxy,hosting,query";
    private static readonly TimeSpan s_staleness = TimeSpan.FromDays(90);
    private static readonly TimeSpan s_rateLimitDelay = TimeSpan.FromMilliseconds(2100); // ~28.5 req/min, under limit

    /// <summary>
    /// Result of an IPAPI lookup.
    /// </summary>
    public readonly record struct IpApiResult(
        string? CountryCode,
        string? Isp,
        bool IsProxy,
        bool IsMobile,
        string? Reverse,
        string? Asn);

    public IpApiLookupService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _connectionString = settings.Value.ConnectionString;
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://pro.ip-api.com/"),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    /// <summary>
    /// Non-blocking cache-only check. Returns the cached result if available,
    /// or null if the IP hasn't been looked up yet (or was already known in SQL).
    /// Used by the enrichment pipeline for zero-latency inline reads (Lane 1).
    /// Background workers populate the cache via <see cref="LookupAsync"/> (Lane 3).
    /// </summary>
    public IpApiResult? TryGetCached(string? ipAddress)
    {
        if (ipAddress is null) return null;
        return _resultCache.TryGetValue(ipAddress, out var result) ? result : null;
    }

    /// <summary>
    /// Loads all known IPs and their last-updated dates from IPAPI.IP into memory.
    /// Call this during startup before processing records.
    /// <para>
    /// DISABLED — Loading 344M rows into a ConcurrentDictionary takes 2+ hours
    /// and ~15GB RAM. The enrichment pipeline now uses inline SQL EXISTS checks
    /// via <see cref="IsKnownInSqlAsync"/> instead. This method is retained as
    /// a no-op so callers don't need changes.
    /// </para>
    /// </summary>
    public Task LoadKnownIpsAsync(CancellationToken ct = default)
    {
        _logger.Info("IpApiLookup: Skipping bulk IP cache load (using inline SQL checks instead)");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Fast inline check: does this IP already exist in IPAPI.IP with fresh data?
    /// Uses the clustered PK index — sub-millisecond on uncontended SQL Server.
    /// On timeout/error, returns true (skip API call) to avoid compounding latency.
    /// </summary>
    private async Task<bool> IsKnownInSqlAsync(string ipAddress, CancellationToken ct)
    {
        // Check in-memory first (populated progressively as we see IPs)
        if (_knownIps.TryGetValue(ipAddress, out var updatedAt) &&
            (DateTime.UtcNow - updatedAt) < s_staleness)
        {
            return true;
        }

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand(
                "SELECT TOP 1 LastSeen FROM IPAPI.IP WHERE IP = @IP AND Status = 'success' OPTION (MAXDOP 1)", conn);
            cmd.Parameters.AddWithValue("@IP", ipAddress);
            cmd.CommandTimeout = 2;

            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is DateTime lastSeen && (DateTime.UtcNow - lastSeen) < s_staleness)
            {
                // Cache locally so we don't hit SQL again for this IP
                _knownIps[ipAddress] = lastSeen;
                return true;
            }

            return false;
        }
        catch
        {
            // On SQL timeout/error, skip the API call entirely.
            // Better to miss one IPAPI enrichment than to block the pipeline
            // with a 2.1s rate-limited API call on top of a SQL timeout.
            return true;
        }
    }

    /// <summary>
    /// Looks up the given IP address. Returns cached/fresh data inline or
    /// triggers an async API call for new/stale IPs.
    /// Returns default if the IP is already known and fresh.
    /// </summary>
    public async Task<IpApiResult> LookupAsync(string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ipAddress))
            return default;

        // Skip private/reserved IPs
        if (ipAddress.StartsWith("10.", StringComparison.Ordinal) ||
            ipAddress.StartsWith("192.168.", StringComparison.Ordinal) ||
            ipAddress.StartsWith("127.", StringComparison.Ordinal) ||
            ipAddress.StartsWith("172.", StringComparison.Ordinal))
            return default;

        // Fast inline SQL check (clustered PK seek, sub-ms) — replaces 344M-row HashSet
        if (await IsKnownInSqlAsync(ipAddress, ct))
            return default; // Already enriched and fresh, skip

        // Rate-limited API call for genuinely new IPs only
        return await CallApiAsync(ipAddress, ct);
    }

    private async Task<IpApiResult> CallApiAsync(string ipAddress, CancellationToken ct)
    {
        await _rateLimiter.WaitAsync(ct);
        try
        {
            await Task.Delay(s_rateLimitDelay, ct); // Respect rate limits

            var url = $"json/{ipAddress}?key={ApiKey}&fields={ApiFields}";
            var response = await _httpClient.GetFromJsonAsync<IpApiResponse>(url, ct);

            if (response is null || response.Status != "success")
            {
                _logger.Debug($"IpApiLookup: Failed for {ipAddress} — {response?.Message ?? "null response"}");
                return default;
            }

            // Write to IPAPI.IP table
            await WriteToDbAsync(ipAddress, response, ct);

            // Mark as known
            _knownIps[ipAddress] = DateTime.UtcNow;

            var result = new IpApiResult(
                response.CountryCode,
                response.Isp,
                response.Proxy,
                response.Mobile,
                response.Reverse,
                ParseAsNumber(response.As));

            // Cache result for inline pipeline reads (TryGetCached)
            if (_resultCache.Count >= MaxResultCacheSize)
                _resultCache.Clear();
            _resultCache.TryAdd(ipAddress, result);

            return result;
        }
        catch (OperationCanceledException)
        {
            return default;
        }
        catch (Exception ex)
        {
            _logger.Debug($"IpApiLookup: API call failed for {ipAddress} — {ex.Message}");
            return default;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    private async Task WriteToDbAsync(string ipAddress, IpApiResponse response, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                MERGE IPAPI.IP AS T
                USING (SELECT @IP AS IP) AS S ON T.IP = S.IP
                WHEN MATCHED THEN UPDATE SET
                    Country = @Country, CountryCode = @CountryCode,
                    RegionName = @RegionName, City = @City, Zip = @Zip,
                    Lat = @Lat, Lon = @Lon, Timezone = @Timezone,
                    ISP = @ISP, Org = @Org, [AS] = @AS, Reverse = @Reverse,
                    Mobile = @Mobile, Proxy = @Proxy,
                    LastSeen = GETUTCDATE()
                WHEN NOT MATCHED THEN INSERT
                    (IP, Country, CountryCode, RegionName, City, Zip, Lat, Lon,
                     Timezone, ISP, Org, [AS], Reverse, Mobile, Proxy, FirstSeen, LastSeen)
                VALUES
                    (@IP, @Country, @CountryCode, @RegionName, @City, @Zip, @Lat, @Lon,
                     @Timezone, @ISP, @Org, @AS, @Reverse, @Mobile, @Proxy, GETUTCDATE(), GETUTCDATE());
                """;

            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@IP", ipAddress);
            cmd.Parameters.AddWithValue("@Country", (object?)response.Country ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CountryCode", (object?)response.CountryCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@RegionName", (object?)response.RegionName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@City", (object?)response.City ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Zip", (object?)response.Zip ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Lat", response.Lat);
            cmd.Parameters.AddWithValue("@Lon", response.Lon);
            cmd.Parameters.AddWithValue("@Timezone", (object?)response.Timezone ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ISP", (object?)response.Isp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Org", (object?)response.Org ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@AS", (object?)response.As ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Reverse", (object?)response.Reverse ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Mobile", response.Mobile);
            cmd.Parameters.AddWithValue("@Proxy", response.Proxy);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.Debug($"IpApiLookup: DB write failed for {ipAddress} — {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts the numeric AS number from the ip-api "as" field (e.g., "AS13335 Cloudflare, Inc." → "AS13335").
    /// </summary>
    private static string? ParseAsNumber(string? asField)
    {
        if (string.IsNullOrEmpty(asField)) return null;
        var spaceIdx = asField.IndexOf(' ', StringComparison.Ordinal);
        return spaceIdx > 0 ? asField[..spaceIdx] : asField;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
        _rateLimiter.Dispose();
    }

    /// <summary>
    /// JSON response model from ip-api.com Pro API.
    /// </summary>
    private sealed class IpApiResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("country")]
        public string? Country { get; set; }

        [JsonPropertyName("countryCode")]
        public string? CountryCode { get; set; }

        [JsonPropertyName("regionName")]
        public string? RegionName { get; set; }

        [JsonPropertyName("city")]
        public string? City { get; set; }

        [JsonPropertyName("zip")]
        public string? Zip { get; set; }

        [JsonPropertyName("lat")]
        public double Lat { get; set; }

        [JsonPropertyName("lon")]
        public double Lon { get; set; }

        [JsonPropertyName("timezone")]
        public string? Timezone { get; set; }

        [JsonPropertyName("isp")]
        public string? Isp { get; set; }

        [JsonPropertyName("org")]
        public string? Org { get; set; }

        [JsonPropertyName("as")]
        public string? As { get; set; }

        [JsonPropertyName("reverse")]
        public string? Reverse { get; set; }

        [JsonPropertyName("mobile")]
        public bool Mobile { get; set; }

        [JsonPropertyName("proxy")]
        public bool Proxy { get; set; }

        [JsonPropertyName("hosting")]
        public bool Hosting { get; set; }

        [JsonPropertyName("query")]
        public string? Query { get; set; }
    }
}
