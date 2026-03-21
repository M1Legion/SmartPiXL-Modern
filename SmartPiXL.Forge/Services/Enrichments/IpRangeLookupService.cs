using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services.Enrichments;

// ============================================================================
// IP RANGE LOOKUP SERVICE — In-memory binary search over IPInfo range tables.
//
// Replaces MaxMindGeoService. Loads IPInfo.GeoRange + IPInfo.AsnRange from SQL
// into sorted arrays at startup. Binary search on IP numeric value — O(log n).
//
// HOT RELOAD: IpDataAcquisitionService calls ReloadAsync() after each import
// cycle. New data loads into fresh arrays, then Interlocked.Exchange swaps
// the pointer. Zero-downtime, no locks needed for reads.
//
// MEMORY: ~500K geo ranges × ~80 bytes + ~70K ASN ranges × ~40 bytes ≈ ~45MB.
//
// APPENDED PARAMS (same keys as MaxMindGeoService for backward compatibility):
//   _srv_mmCC, _srv_mmReg, _srv_mmCity, _srv_mmLat, _srv_mmLon,
//   _srv_mmASN, _srv_mmASNOrg, _srv_mmZip, _srv_mmTZ
// ============================================================================

public sealed class IpRangeLookupService
{
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;

    private volatile GeoEntry[] _geoRanges = [];
    private volatile AsnEntry[] _asnRanges = [];
    private readonly ConcurrentDictionary<string, IpLookupResult> _cache = new(StringComparer.Ordinal);
    private const int MaxCacheSize = 200_000;
    private volatile bool _loaded;

    public readonly record struct IpLookupResult(
        string? CountryCode,
        string? Region,
        string? City,
        double? Latitude,
        double? Longitude,
        int? Asn,
        string? AsnOrg,
        string? PostalCode,
        string? TimeZone);

    // Internal structs for sorted arrays — minimal fields for memory efficiency
    private readonly record struct GeoEntry(
        uint IpStart, uint IpEnd,
        string? CountryCode, string? Region, string? City,
        string? PostalCode, decimal? Latitude, decimal? Longitude,
        string? Timezone);

    private readonly record struct AsnEntry(
        uint IpStart, uint IpEnd,
        int AsnNumber, string? AsnDescription);

    public IpRangeLookupService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Loads initial data from SQL. Called once during service startup.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await ReloadAsync(ct);
    }

    /// <summary>
    /// Hot-reloads range data from SQL. Builds new arrays, then atomically
    /// swaps via Interlocked.Exchange. Concurrent reads on old arrays
    /// complete normally — GC collects when all references are released.
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var newGeo = await LoadGeoRangesAsync(ct);
            var newAsn = await LoadAsnRangesAsync(ct);

            // Atomic pointer swap — no locks needed
            Interlocked.Exchange(ref _geoRanges, newGeo);
            Interlocked.Exchange(ref _asnRanges, newAsn);
            _cache.Clear();
            _loaded = true;

            sw.Stop();
            _logger.Info($"IpRangeLookup: Loaded {newGeo.Length:N0} geo + {newAsn.Length:N0} ASN ranges in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            _logger.Error($"IpRangeLookup: Failed to load range data: {ex.Message}");
            // If this is the first load, _loaded stays false and lookups return default
            // If this is a reload, old data stays active (graceful degradation)
        }
    }

    /// <summary>
    /// Performs a combined Geo + ASN lookup for the given IP address.
    /// Returns default struct if IP is invalid or data hasn't loaded.
    /// </summary>
    public IpLookupResult Lookup(string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress) || !_loaded)
            return default;

        if (_cache.TryGetValue(ipAddress, out var cached))
            return cached;

        if (!IPAddress.TryParse(ipAddress, out var ip))
            return default;

        // IPv6 not supported in range tables yet (IPv4-only data sources)
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return default;

        var ipNum = IpToUint(ip);

        // Binary search geo ranges
        string? countryCode = null, region = null, city = null;
        string? postalCode = null, timezone = null;
        double? lat = null, lon = null;

        var geoRanges = _geoRanges; // snapshot volatile ref
        var geoIdx = BinarySearchRange(geoRanges, ipNum);
        if (geoIdx >= 0)
        {
            ref readonly var g = ref geoRanges[geoIdx];
            countryCode = g.CountryCode;
            region = g.Region;
            city = g.City;
            postalCode = g.PostalCode;
            lat = g.Latitude.HasValue ? (double)g.Latitude.Value : null;
            lon = g.Longitude.HasValue ? (double)g.Longitude.Value : null;
            timezone = g.Timezone;
        }

        // Binary search ASN ranges
        int? asn = null;
        string? asnOrg = null;

        var asnRanges = _asnRanges; // snapshot volatile ref
        var asnIdx = BinarySearchRange(asnRanges, ipNum);
        if (asnIdx >= 0)
        {
            ref readonly var a = ref asnRanges[asnIdx];
            asn = a.AsnNumber;
            asnOrg = a.AsnDescription;
        }

        var result = new IpLookupResult(countryCode, region, city, lat, lon, asn, asnOrg, postalCode, timezone);

        if (_cache.Count >= MaxCacheSize)
            _cache.Clear();

        _cache.TryAdd(ipAddress, result);
        return result;
    }

    // ========================================================================
    // SQL loaders
    // ========================================================================

    private async Task<GeoEntry[]> LoadGeoRangesAsync(CancellationToken ct)
    {
        var list = new List<GeoEntry>(600_000);

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        // IPv4 only (AddrFamily = 4), ordered by IpStart for binary search
        cmd.CommandText = @"
            SELECT IpStart, IpEnd, CountryCode, Region, City,
                   PostalCode, Latitude, Longitude, Timezone
            FROM IPInfo.GeoRange
            WHERE AddrFamily = 4
            ORDER BY IpStart";
        cmd.CommandTimeout = 300;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var startBin = reader.GetSqlBytes(0).Value;
            var endBin = reader.GetSqlBytes(1).Value;

            if (startBin.Length != 4 || endBin.Length != 4) continue;

            list.Add(new GeoEntry(
                BinaryToUint(startBin),
                BinaryToUint(endBin),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return list.ToArray();
    }

    private async Task<AsnEntry[]> LoadAsnRangesAsync(CancellationToken ct)
    {
        var list = new List<AsnEntry>(500_000);

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            SELECT IpStart, IpEnd, AsnNumber, AsnDescription
            FROM IPInfo.AsnRange
            WHERE AddrFamily = 4
            ORDER BY IpStart";
        cmd.CommandTimeout = 300;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var startBin = reader.GetSqlBytes(0).Value;
            var endBin = reader.GetSqlBytes(1).Value;

            if (startBin.Length != 4 || endBin.Length != 4) continue;

            list.Add(new AsnEntry(
                BinaryToUint(startBin),
                BinaryToUint(endBin),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return list.ToArray();
    }

    // ========================================================================
    // Binary search helpers
    // ========================================================================

    /// <summary>
    /// Binary search for the range containing the given IP.
    /// Returns the index of the matching range, or -1 if not found.
    /// Ranges are sorted by IpStart. We find the last range where IpStart <= ipNum,
    /// then verify IpEnd >= ipNum.
    /// </summary>
    private static int BinarySearchRange(GeoEntry[] ranges, uint ipNum)
    {
        int lo = 0, hi = ranges.Length - 1;
        int candidate = -1;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (ranges[mid].IpStart <= ipNum)
            {
                candidate = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (candidate >= 0 && ranges[candidate].IpEnd >= ipNum)
            return candidate;

        return -1;
    }

    private static int BinarySearchRange(AsnEntry[] ranges, uint ipNum)
    {
        int lo = 0, hi = ranges.Length - 1;
        int candidate = -1;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (ranges[mid].IpStart <= ipNum)
            {
                candidate = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (candidate >= 0 && ranges[candidate].IpEnd >= ipNum)
            return candidate;

        return -1;
    }

    // ========================================================================
    // IP conversion helpers
    // ========================================================================

    private static uint IpToUint(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }

    private static uint BinaryToUint(byte[] bin)
    {
        return (uint)(bin[0] << 24 | bin[1] << 16 | bin[2] << 8 | bin[3]);
    }
}
