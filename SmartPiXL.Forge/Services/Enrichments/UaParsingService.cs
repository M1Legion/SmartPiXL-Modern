using DeviceDetectorNET;
using UAParser;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services.Enrichments;

// ============================================================================
// UA PARSING SERVICE — Structured User-Agent parsing using two libraries:
//   1. UAParser (Google's regex DB) — fast first pass for browser/OS/device
//   2. DeviceDetector.NET (10,000+ patterns) — deep second pass for IoT/TV/
//      console/car-browser classification
//
// PERFORMANCE:
//   ConcurrentDictionary cache eliminates re-parsing for repeat UAs.
//   Production data shows 5.5% unique UA ratio across 100K records — giving
//   94.5% cache hit rate. Cache hits are a lock-free hash table lookup (~100ns)
//   vs UAParser + DeviceDetector.NET regex evaluation (~2-5ms) on miss.
//
// APPENDED PARAMS:
//   _srv_browser={name}       — Browser family (Chrome, Firefox, Safari, Edge)
//   _srv_browserVer={ver}     — Major.Minor browser version
//   _srv_os={name}            — OS family (Windows, macOS, Linux, Android, iOS)
//   _srv_osVer={ver}          — OS version
//   _srv_deviceType={type}    — desktop, smartphone, tablet, tv, console, car, IoT, etc.
//   _srv_deviceModel={model}  — Device model (iPhone 15, Galaxy S24, etc.)
//   _srv_deviceBrand={brand}  — Device brand (Apple, Samsung, Google, etc.)
// ============================================================================

/// <summary>
/// Parses User-Agent strings into structured browser, OS, and device information
/// using UAParser (first pass) and DeviceDetector.NET (deep second pass).
/// Singleton — thread-safe with ConcurrentDictionary cache for repeat UA elimination.
/// </summary>
public sealed class UaParsingService
{
    private readonly BoundedCache<string, UaParseResult> _cache = new(
        maxEntries: 50_000, evictTarget: 25_000, maxAge: TimeSpan.FromMinutes(30));
    private readonly Parser _uaParser;
    private readonly ITrackingLogger _logger;

    /// <summary>Current cache entry count (for health tree sampling).</summary>
    public int CacheCount => _cache.Count;

    public UaParsingService(ITrackingLogger logger)
    {
        _uaParser = Parser.GetDefault();
        _logger = logger;
    }

    /// <summary>
    /// Result of a User-Agent parse operation.
    /// </summary>
    public readonly record struct UaParseResult(
        string? Browser,
        string? BrowserVersion,
        string? OS,
        string? OSVersion,
        string? DeviceType,
        string? DeviceModel,
        string? DeviceBrand);

    /// <summary>
    /// Parses the given User-Agent string into structured fields.
    /// Lock-free cache lookup for repeat UAs (~94.5% hit rate).
    /// On cache miss: first pass via UAParser for browser/OS, second pass via
    /// DeviceDetector.NET for deep device classification.
    /// </summary>
    public UaParseResult Parse(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
            return default;

        // Lock-free cache lookup — BoundedCache.TryGet is a hash probe
        if (_cache.TryGet(userAgent, out var cached))
            return cached;

        // Cache miss — full parse through both libraries
        var result = ParseCore(userAgent);

        // Hybrid eviction when over cap (shared BoundedCache pattern)
        if (_cache.Count >= _cache.MaxEntries)
            _cache.Evict();

        _cache.Set(userAgent, result);
        return result;
    }

    /// <summary>
    /// Core parsing logic — runs UAParser + DeviceDetector.NET.
    /// Only called on cache miss.
    /// </summary>
    private UaParseResult ParseCore(string userAgent)
    {
        string? browser = null, browserVer = null;
        string? os = null, osVer = null;
        string? deviceType = null, deviceModel = null, deviceBrand = null;

        try
        {
            // First pass: UAParser — fast regex-based parsing
            var clientInfo = _uaParser.Parse(userAgent);

            browser = NullIfOther(clientInfo.UA.Family);
            browserVer = BuildVersion(clientInfo.UA.Major, clientInfo.UA.Minor);
            os = NullIfOther(clientInfo.OS.Family);
            osVer = BuildVersion(clientInfo.OS.Major, clientInfo.OS.Minor);
            deviceModel = NullIfOther(clientInfo.Device.Model);
            deviceBrand = NullIfOther(clientInfo.Device.Brand);
        }
        catch (Exception ex)
        {
            _logger.Debug($"UaParsing: UAParser failed — {ex.Message}");
        }

        try
        {
            // Second pass: DeviceDetector.NET — deep device classification
            // Catches IoT, smart TV, console, car browser, etc.
            var dd = new DeviceDetector(userAgent);
            dd.Parse();

            if (dd.IsParsed())
            {
                // Device type from DeviceDetector is more granular
                var ddDeviceType = dd.GetDeviceName();
                if (!string.IsNullOrEmpty(ddDeviceType))
                    deviceType = ddDeviceType;

                // Fill in brand/model if UAParser missed them
                var ddBrand = dd.GetBrandName();
                var ddModel = dd.GetModel();
                if (!string.IsNullOrEmpty(ddBrand) && string.IsNullOrEmpty(deviceBrand))
                    deviceBrand = ddBrand;
                if (!string.IsNullOrEmpty(ddModel) && string.IsNullOrEmpty(deviceModel))
                    deviceModel = ddModel;

                // If UAParser didn't get browser, try DeviceDetector
                if (string.IsNullOrEmpty(browser))
                {
                    var clientMatch = dd.GetClient();
                    if (clientMatch.Match is not null)
                    {
                        browser = clientMatch.Match.Name;
                        browserVer = clientMatch.Match.Version;
                    }
                }

                // If UAParser didn't get OS, try DeviceDetector
                if (string.IsNullOrEmpty(os))
                {
                    var osMatch = dd.GetOs();
                    if (osMatch.Match is not null)
                    {
                        os = osMatch.Match.Name;
                        osVer = osMatch.Match.Version;
                    }
                }
            }

            // Default device type if neither library classified it
            if (string.IsNullOrEmpty(deviceType))
                deviceType = "desktop";
        }
        catch (Exception ex)
        {
            _logger.Debug($"UaParsing: DeviceDetector failed — {ex.Message}");
        }

        return new UaParseResult(browser, browserVer, os, osVer, deviceType, deviceModel, deviceBrand);
    }

    private static string? NullIfOther(string? value) =>
        string.IsNullOrEmpty(value) || string.Equals(value, "Other", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

    private static string? BuildVersion(string? major, string? minor) =>
        string.IsNullOrEmpty(major) ? null :
        string.IsNullOrEmpty(minor) ? major :
        $"{major}.{minor}";
}
