using System.Text.Json;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services.Enrichments;

// ============================================================================
// OS END-OF-LIFE SERVICE — Fetches and caches OS lifecycle data from
// endoflife.date for consumer operating systems.
//
// PURPOSE:
//   Determines whether a visitor's OS version is still actively supported
//   by its vendor. End-of-life OS usage is a negative signal for lead quality
//   (legitimate users tend to keep OS updated) and a positive bot indicator
//   (bot farms frequently run on outdated/unpatched OS images).
//
// DATA SOURCE:
//   https://endoflife.date — Community-maintained, API-accessible lifecycle data.
//   API: GET https://endoflife.date/api/{product}.json
//   Returns array of release cycles with EOL dates.
//   Management-specified reference: https://endoflife.date/tags/os
//
// PRODUCTS TRACKED:
//   windows, macos, ios, android, ipados
//   These cover >99% of consumer browser traffic. Server OS, ChromeOS
//   (rolling release, always current), and niche Linux distros are treated
//   as "unknown" (neutral — not penalized, not rewarded).
//
// REFRESH STRATEGY:
//   • Initial load at startup (StartAsync)
//   • Daily refresh via Timer (EOL dates change infrequently)
//   • Failure tolerance: if refresh fails, previous data remains active
//
// LOCK-FREE ARCHITECTURE:
//   The _eolData field is a volatile reference to an immutable dictionary.
//   Writers build a new dictionary on refresh, then atomically swap.
//   Readers get a consistent snapshot via volatile read.
//
// MATCHING STRATEGY:
//   For each product, we group release cycles by major version and take the
//   latest (most generous) EOL date. This handles Windows' Edition/channel
//   complexity (10-22h2, 10-21h2-e-lts, etc.) by finding ANY non-EOL cycle
//   for that major version.
// ============================================================================

/// <summary>
/// Hosted service that downloads OS end-of-life data at startup and daily,
/// providing a lock-free lookup for OS support status.
/// </summary>
public sealed class OsEndOfLifeService : IHostedService, IDisposable
{
    private readonly ITrackingLogger _logger;
    private readonly HttpClient _httpClient;
    private Timer? _refreshTimer;

    /// <summary>
    /// Lock-free EOL data. Key = normalized OS name (lowercase), Value = dictionary of
    /// major version → latest EOL date (null = still supported).
    /// </summary>
    private volatile IReadOnlyDictionary<string, IReadOnlyDictionary<string, DateOnly?>> _eolData
        = new Dictionary<string, IReadOnlyDictionary<string, DateOnly?>>();

    /// <summary>Tracks whether initial load has completed successfully.</summary>
    public bool IsLoaded => _eolData.Count > 0;

    /// <summary>Count of OS products loaded.</summary>
    public int ProductCount => _eolData.Count;

    /// <summary>Total release cycles across all products.</summary>
    public int TotalCycles => _eolData.Values.Sum(d => d.Count);

    /// <summary>Products to fetch from endoflife.date API.</summary>
    private static readonly (string Slug, string NormalizedName)[] TrackedProducts =
    [
        ("windows", "windows"),
        ("macos", "mac os x"),
        ("ios", "ios"),
        ("android", "android"),
        ("ipados", "ipados"),
    ];

    private const string ApiBaseUrl = "https://endoflife.date/api";

    public OsEndOfLifeService(ITrackingLogger logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("OsEndOfLife");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshDataAsync(cancellationToken);
        // Refresh daily — EOL dates don't change frequently
        _refreshTimer = new Timer(_ => _ = RefreshDataAsync(CancellationToken.None),
            null, TimeSpan.FromDays(1), TimeSpan.FromDays(1));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _refreshTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks whether the given OS + version is still supported.
    /// </summary>
    /// <param name="osName">Parsed OS name (e.g., "Windows", "Mac OS X", "iOS", "Android").</param>
    /// <param name="osVersion">Parsed OS version (e.g., "10", "11", "15.4", "14").</param>
    /// <returns>EOL check result with support status and EOL date if applicable.</returns>
    public OsEolResult Check(string? osName, string? osVersion)
    {
        if (string.IsNullOrEmpty(osName) || string.IsNullOrEmpty(osVersion))
            return OsEolResult.Unknown;

        var data = _eolData; // single volatile read
        if (data.Count == 0)
            return OsEolResult.Unknown;

        // Normalize OS name to match our tracked products
        var normalizedOs = NormalizeOsName(osName);
        if (normalizedOs is null || !data.TryGetValue(normalizedOs, out var versions))
            return OsEolResult.Unknown;

        // Extract major version for lookup
        var majorVersion = ExtractMajorVersion(normalizedOs, osVersion);
        if (majorVersion is null)
            return OsEolResult.Unknown;

        if (!versions.TryGetValue(majorVersion, out var eolDate))
            return OsEolResult.Unknown;

        // eolDate == null means still supported (eol: false in API)
        if (eolDate is null)
            return new OsEolResult(OsEolStatus.Supported, null);

        // Compare with today
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return eolDate.Value <= today
            ? new OsEolResult(OsEolStatus.EndOfLife, eolDate.Value)
            : new OsEolResult(OsEolStatus.Supported, eolDate.Value);
    }

    /// <summary>
    /// Maps parsed OS name to our tracked product key.
    /// UA parsers return varied names; normalize to our canonical keys.
    /// </summary>
    private static string? NormalizeOsName(string osName)
    {
        var lower = osName.ToLowerInvariant();

        if (lower.Contains("windows"))
            return "windows";
        if (lower.Contains("mac os") || lower.Contains("macos") || lower == "os x")
            return "mac os x";
        if (lower == "ios" || lower.Contains("iphone os"))
            return "ios";
        if (lower == "ipados" || lower.Contains("ipad"))
            return "ipados";
        if (lower.Contains("android"))
            return "android";

        return null;
    }

    /// <summary>
    /// Extracts the major version string used as dictionary key.
    /// Windows uses major version (10, 11, 8.1, 7).
    /// macOS/iOS/Android use the major number from the version string.
    /// </summary>
    private static string? ExtractMajorVersion(string normalizedOs, string osVersion)
    {
        // Handle "X.Y.Z" → "X" for most OS, but Windows needs special handling
        if (normalizedOs == "windows")
        {
            // Windows versions: "10", "11", "8.1", "7", "Vista", "XP"
            // UA parsers typically give us the major: "10", "11"
            // But could give "10.0" — strip the minor
            if (osVersion.StartsWith("10"))
                return "10";
            if (osVersion.StartsWith("11"))
                return "11";
            if (osVersion.StartsWith("8.1"))
                return "8.1";
            if (osVersion.StartsWith("8"))
                return "8";
            if (osVersion.StartsWith("7") || osVersion.Contains("7"))
                return "7";
            if (osVersion.Contains("Vista") || osVersion.StartsWith("6.0"))
                return "Vista";
            if (osVersion.Contains("XP") || osVersion.StartsWith("5."))
                return "XP";
            return osVersion;
        }

        // For macOS, iOS, Android, iPadOS — use the first segment of the version
        var dotIndex = osVersion.IndexOf('.');
        return dotIndex > 0 ? osVersion[..dotIndex] : osVersion;
    }

    /// <summary>
    /// Downloads all tracked OS products from endoflife.date and atomically
    /// replaces the in-memory lookup dictionary.
    /// </summary>
    private async Task RefreshDataAsync(CancellationToken ct)
    {
        _logger.Info("Refreshing OS end-of-life data from endoflife.date...");
        var newData = new Dictionary<string, IReadOnlyDictionary<string, DateOnly?>>();

        foreach (var (slug, normalizedName) in TrackedProducts)
        {
            try
            {
                var url = $"{ApiBaseUrl}/{slug}.json";
                var json = await _httpClient.GetStringAsync(url, ct);
                var cycles = ParseCycles(normalizedName, json);
                newData[normalizedName] = cycles;
                _logger.Info($"EOL data loaded: {slug} → {cycles.Count} major versions");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load EOL data for {slug}", ex);
            }
        }

        if (newData.Count > 0)
        {
            _eolData = newData;
            _logger.Info($"OS EOL data refreshed: {newData.Count} products, {newData.Values.Sum(d => d.Count)} total versions");
        }
    }

    /// <summary>
    /// Parses the endoflife.date API response for a single product.
    /// Groups cycles by major version and takes the latest (most generous) EOL date.
    /// </summary>
    private Dictionary<string, DateOnly?> ParseCycles(string normalizedOs, string json)
    {
        var result = new Dictionary<string, DateOnly?>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(json);
        foreach (var cycle in doc.RootElement.EnumerateArray())
        {
            // Extract cycle name for major version grouping
            var cycleName = cycle.TryGetProperty("cycle", out var cycleEl) ? cycleEl.GetString() : null;
            if (cycleName is null) continue;

            var majorVersion = ExtractMajorVersionFromCycle(normalizedOs, cycleName);
            if (majorVersion is null) continue;

            // Parse EOL: false = still supported, "YYYY-MM-DD" = EOL date
            DateOnly? eolDate = null;
            if (cycle.TryGetProperty("eol", out var eolEl))
            {
                if (eolEl.ValueKind == JsonValueKind.String)
                {
                    if (DateOnly.TryParse(eolEl.GetString(), out var parsed))
                        eolDate = parsed;
                }
                else if (eolEl.ValueKind == JsonValueKind.False)
                {
                    // Still supported — eolDate stays null (best case)
                    eolDate = null;
                }
                else if (eolEl.ValueKind == JsonValueKind.True)
                {
                    // EOL but no specific date — treat as very old
                    eolDate = DateOnly.MinValue;
                }
            }

            // Keep the "best" (latest/most generous) EOL date per major version.
            // null = still supported = best possible outcome.
            if (!result.TryGetValue(majorVersion, out var existing))
            {
                result[majorVersion] = eolDate;
            }
            else
            {
                // null (still supported) beats any date
                if (eolDate is null)
                    result[majorVersion] = null;
                else if (existing is not null && eolDate.Value > existing.Value)
                    result[majorVersion] = eolDate.Value;
                // else keep existing (it's either null or a later date)
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts the major version from an endoflife.date cycle name.
    /// </summary>
    private static string? ExtractMajorVersionFromCycle(string normalizedOs, string cycleName)
    {
        if (normalizedOs == "windows")
        {
            // Windows cycles: "11-26h1-e", "10-22h2", "8.1", "8", "7-sp1", "6-sp2", "5-sp3"
            if (cycleName.StartsWith("11"))
                return "11";
            if (cycleName.StartsWith("10"))
                return "10";
            if (cycleName.StartsWith("8.1"))
                return "8.1";
            if (cycleName.StartsWith("8"))
                return "8";
            if (cycleName.StartsWith("7"))
                return "7";
            if (cycleName.StartsWith("6"))
                return "Vista";
            if (cycleName.StartsWith("5"))
                return "XP";
            return cycleName;
        }

        // macOS, iOS, Android, iPadOS — cycle is the major version number directly
        // e.g., "26", "15", "14", "10.15", "18"
        var dotIndex = cycleName.IndexOf('.');
        return dotIndex > 0 ? cycleName[..dotIndex] : cycleName;
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
    }
}

/// <summary>
/// Result of an OS end-of-life check. Stack-allocated.
/// </summary>
public readonly record struct OsEolResult(OsEolStatus Status, DateOnly? EolDate)
{
    /// <summary>OS not recognized or version couldn't be matched — neutral (no penalty).</summary>
    public static readonly OsEolResult Unknown = new(OsEolStatus.Unknown, null);
}

/// <summary>
/// OS end-of-life support status.
/// </summary>
public enum OsEolStatus
{
    /// <summary>OS/version not recognized in our data — neutral.</summary>
    Unknown,
    /// <summary>OS version is still actively supported by its vendor.</summary>
    Supported,
    /// <summary>OS version has reached end-of-life — no longer receiving updates.</summary>
    EndOfLife
}
