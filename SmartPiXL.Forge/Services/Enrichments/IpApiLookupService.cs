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
// On startup: loads all known IPs from IPAPI.IP into a HashSet<string>.
// Per record: check if IP is known+fresh (<90 days). If not, call the Pro API.
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
/// Singleton — thread-safe. Maintains a HashSet of known IPs to avoid redundant lookups.
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
    /// Loads all known IPs and their last-updated dates from IPAPI.IP into memory.
    /// Call this during startup before processing records.
    /// </summary>
    public async Task LoadKnownIpsAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand(
                "SELECT IP, LastSeen FROM IPAPI.IP WITH (NOLOCK) WHERE LastSeen IS NOT NULL", conn);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var count = 0;
            while (await reader.ReadAsync(ct))
            {
                var ip = reader.GetString(0);
                var lastSeen = reader.GetDateTime(1);
                _knownIps[ip] = lastSeen;
                count++;
            }

            _logger.Info($"IpApiLookup: Loaded {count:N0} known IPs from IPAPI.IP");
        }
        catch (Exception ex)
        {
            _logger.Warning($"IpApiLookup: Failed to load known IPs — {ex.Message}");
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

        // Check if known and fresh
        if (_knownIps.TryGetValue(ipAddress, out var updatedAt) &&
            (DateTime.UtcNow - updatedAt) < s_staleness)
        {
            return default; // Already enriched, skip
        }

        // Rate-limited API call
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

            return new IpApiResult(
                response.CountryCode,
                response.Isp,
                response.Proxy,
                response.Mobile,
                response.Reverse,
                ParseAsNumber(response.As));
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
