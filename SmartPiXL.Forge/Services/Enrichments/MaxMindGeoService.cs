using MaxMind.Db;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services.Enrichments;

// ============================================================================
// MAXMIND GEO SERVICE — Offline IP geolocation using MaxMind GeoLite2 databases.
//
// Loads .mmdb files at startup for sub-microsecond lookups. Three databases:
//   GeoLite2-City.mmdb    — country, region, city, lat/lon
//   GeoLite2-ASN.mmdb     — ASN number and organization name
//   GeoLite2-Country.mmdb — fallback country-only (smallest, most reliable)
//
// FILES: Must be placed in the MaxMind/ subdirectory relative to app base:
//   C:\Services\SmartPiXL-Forge\MaxMind\GeoLite2-City.mmdb
//   C:\Services\SmartPiXL-Forge\MaxMind\GeoLite2-ASN.mmdb
//   C:\Services\SmartPiXL-Forge\MaxMind\GeoLite2-Country.mmdb
//
// MAXMIND ACCOUNT: Required to download the databases (free GeoLite2 license).
// Files update weekly — a future scheduled task will automate refresh.
//
// APPENDED PARAMS:
//   _srv_mmCC={2-letter country}
//   _srv_mmReg={region/state}
//   _srv_mmCity={city name}
//   _srv_mmLat={latitude}
//   _srv_mmLon={longitude}
//   _srv_mmASN={AS number}
//   _srv_mmASNOrg={AS organization}
// ============================================================================

/// <summary>
/// Offline MaxMind GeoIP2 lookup service. Loads .mmdb files at construction
/// and performs O(1) trie lookups per IP (~1μs). Thread-safe singleton.
/// Gracefully degrades if any .mmdb file is missing.
/// </summary>
public sealed class MaxMindGeoService : IDisposable
{
    private readonly DatabaseReader? _cityReader;
    private readonly DatabaseReader? _asnReader;
    private readonly DatabaseReader? _countryReader;
    private readonly ITrackingLogger _logger;
    private bool _disposed;

    /// <summary>
    /// Result of a MaxMind geo lookup.
    /// </summary>
    public readonly record struct MaxMindResult(
        string? CountryCode,
        string? Region,
        string? City,
        double? Latitude,
        double? Longitude,
        int? Asn,
        string? AsnOrg);

    public MaxMindGeoService(ITrackingLogger logger)
    {
        _logger = logger;

        var baseDir = Path.Combine(AppContext.BaseDirectory, "MaxMind");

        _cityReader = TryLoadDatabase(baseDir, "GeoLite2-City.mmdb");
        _asnReader = TryLoadDatabase(baseDir, "GeoLite2-ASN.mmdb");
        _countryReader = TryLoadDatabase(baseDir, "GeoLite2-Country.mmdb");

        if (_cityReader is null && _asnReader is null && _countryReader is null)
            _logger.Warning("MaxMindGeo: No .mmdb files found — geo lookups disabled. Place files in: " + baseDir);
        else
            _logger.Info($"MaxMindGeo: Loaded databases from {baseDir} (City={_cityReader is not null}, ASN={_asnReader is not null}, Country={_countryReader is not null})");
    }

    /// <summary>
    /// Performs a combined City + ASN lookup for the given IP address.
    /// Returns default struct if IP is invalid or no database is loaded.
    /// </summary>
    public MaxMindResult Lookup(string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress))
            return default;

        if (!System.Net.IPAddress.TryParse(ipAddress, out var ip))
            return default;

        string? countryCode = null;
        string? region = null;
        string? city = null;
        double? lat = null;
        double? lon = null;
        int? asn = null;
        string? asnOrg = null;

        // City lookup (includes country + region + city + coordinates)
        if (_cityReader is not null)
        {
            try
            {
                if (_cityReader.TryCity(ip, out var cityResult) && cityResult is not null)
                {
                    countryCode = cityResult.Country?.IsoCode;
                    region = cityResult.MostSpecificSubdivision?.Name;
                    city = cityResult.City?.Name;
                    lat = cityResult.Location?.Latitude;
                    lon = cityResult.Location?.Longitude;
                }
            }
            catch (GeoIP2Exception)
            {
                // Address not found or corrupt record — fall through
            }
        }

        // Country fallback if city lookup didn't yield a country
        if (countryCode is null && _countryReader is not null)
        {
            try
            {
                if (_countryReader.TryCountry(ip, out var countryResult) && countryResult is not null)
                {
                    countryCode = countryResult.Country?.IsoCode;
                }
            }
            catch (GeoIP2Exception)
            {
                // Address not found — continue without
            }
        }

        // ASN lookup
        if (_asnReader is not null)
        {
            try
            {
                if (_asnReader.TryAsn(ip, out var asnResult) && asnResult is not null)
                {
                    asn = (int?)asnResult.AutonomousSystemNumber;
                    asnOrg = asnResult.AutonomousSystemOrganization;
                }
            }
            catch (GeoIP2Exception)
            {
                // Address not found — continue without
            }
        }

        return new MaxMindResult(countryCode, region, city, lat, lon, asn, asnOrg);
    }

    private DatabaseReader? TryLoadDatabase(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            _logger.Debug($"MaxMindGeo: {fileName} not found at {path}");
            return null;
        }

        try
        {
            return new DatabaseReader(path, FileAccessMode.MemoryMapped);
        }
        catch (Exception ex)
        {
            _logger.Warning($"MaxMindGeo: Failed to load {fileName}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cityReader?.Dispose();
        _asnReader?.Dispose();
        _countryReader?.Dispose();
    }
}
