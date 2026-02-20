// ─────────────────────────────────────────────────────────────────────────────
// SmartPiXL Forge — Device Age Estimation Service
// Estimates the age of a device by cross-referencing GPU release year, OS
// version year, and browser generation. Detects anomalies where device age
// is inconsistent with behavioral signals (e.g., 10-year-old GPU on latest
// OS from datacenter IP with no mouse = bot).
// Phase 6 — Tier 3 Enrichments (Asymmetric Detection)
// ─────────────────────────────────────────────────────────────────────────────

namespace SmartPiXL.Forge.Services.Enrichments;

/// <summary>
/// Estimates device age in years by triangulating GPU generation, OS version,
/// and browser version against their known release dates. Flags anomalies
/// where the device age profile is internally inconsistent or contradicts
/// behavioral signals. Stateless singleton.
/// </summary>
public sealed class DeviceAgeEstimationService
{
    // ════════════════════════════════════════════════════════════════════════
    // RESULT
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Result of device age estimation.
    /// </summary>
    /// <param name="AgeYears">Estimated device age in years (0 = current year or unknown).</param>
    /// <param name="IsAnomaly">True if the device age profile is internally inconsistent.</param>
    public readonly record struct AgeResult(int AgeYears, bool IsAnomaly);

    // ════════════════════════════════════════════════════════════════════════
    // OS VERSION → RELEASE YEAR MAPPING
    // ════════════════════════════════════════════════════════════════════════

    private static readonly (string Pattern, int Year)[] s_osYears =
    [
        // Windows versions (by major version string or build)
        ("Windows 11", 2021), ("Windows 10", 2015), ("Windows 8.1", 2013),
        ("Windows 8", 2012), ("Windows 7", 2009), ("Windows Vista", 2006),
        ("Windows XP", 2001),
        // macOS versions (by version number from UA)
        ("15.", 2024), ("14.", 2023), ("13.", 2022), ("12.", 2021),
        ("11.", 2020), ("10.15", 2019), ("10.14", 2018), ("10.13", 2017),
        ("10.12", 2016), ("10.11", 2015), ("10.10", 2014), ("10.9", 2013),
    ];

    // ════════════════════════════════════════════════════════════════════════
    // BROWSER VERSION → APPROXIMATE RELEASE YEAR
    // Chrome releases ~4 major versions/year since 2019.
    // Firefox releases ~12/year. Safari tied to OS releases.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Estimates the release year of Chrome by major version number.
    /// Chrome 49 (2016) through Chrome 130+ (2025).
    /// </summary>
    private static int ChromeVersionToYear(int major)
    {
        if (major >= 132) return 2025;
        if (major >= 120) return 2024;
        if (major >= 109) return 2023;
        if (major >= 97)  return 2022;
        if (major >= 88)  return 2021;
        if (major >= 79)  return 2020;
        if (major >= 72)  return 2019;
        if (major >= 63)  return 2018;
        if (major >= 56)  return 2017;
        if (major >= 49)  return 2016;
        if (major >= 39)  return 2015;
        if (major >= 31)  return 2014;
        return 2013;
    }

    /// <summary>
    /// Estimates the release year of Firefox by major version number.
    /// </summary>
    private static int FirefoxVersionToYear(int major)
    {
        if (major >= 134) return 2025;
        if (major >= 121) return 2024;
        if (major >= 109) return 2023;
        if (major >= 96)  return 2022;
        if (major >= 84)  return 2021;
        if (major >= 72)  return 2020;
        if (major >= 64)  return 2019;
        if (major >= 57)  return 2018;
        if (major >= 49)  return 2017;
        if (major >= 43)  return 2016;
        if (major >= 35)  return 2015;
        return 2014;
    }

    // ════════════════════════════════════════════════════════════════════════
    // PUBLIC API
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Estimates the device age and detects age-based anomalies.
    /// </summary>
    /// <param name="gpu">GPU renderer string from WebGL.</param>
    /// <param name="os">Detected OS name from UA parsing.</param>
    /// <param name="osVersion">Detected OS version from UA parsing.</param>
    /// <param name="browser">Detected browser name from UA parsing.</param>
    /// <param name="browserVersion">Detected browser version from UA parsing.</param>
    /// <param name="isDatacenter">Whether the IP is from a datacenter/cloud provider.</param>
    /// <param name="mouseEntropy">Mouse movement entropy (0 = no mouse).</param>
    public AgeResult Estimate(
        string? gpu,
        string? os,
        string? osVersion,
        string? browser,
        string? browserVersion,
        bool isDatacenter,
        double mouseEntropy)
    {
        var currentYear = DateTime.UtcNow.Year;

        // ── Estimate age from each signal ────────────────────────────────
        var gpuYear = GpuTierReference.EstimateReleaseYear(gpu);
        var osYear = EstimateOsYear(os, osVersion);
        var browserYear = EstimateBrowserYear(browser, browserVersion);

        // ── Take the OLDEST signal as the device age ─────────────────────
        // Rationale: the oldest hardware component constrains the device.
        // A machine with a 2016 GPU running 2024 OS is likely a 2016 device
        // that got OS updates — the GPU is the true age indicator.
        var oldestYear = MinNonZero(gpuYear, MinNonZero(osYear, browserYear));

        if (oldestYear == 0)
            return new AgeResult(0, false); // no signals → can't estimate

        var ageYears = Math.Max(0, currentYear - oldestYear);

        // ── Anomaly detection ────────────────────────────────────────────
        // Cross-signal plausibility checks for bot/emulation indicators.
        var isAnomaly = false;

        // ANOMALY 1: Very old GPU + brand-new browser + datacenter + no mouse
        // → Server running a headless browser with an emulated old GPU
        if (gpuYear > 0 && browserYear > 0 &&
            (currentYear - gpuYear) >= 7 &&
            (currentYear - browserYear) <= 1 &&
            isDatacenter && mouseEntropy < 0.5)
        {
            isAnomaly = true;
        }

        // ANOMALY 2: GPU and OS ages differ by more than 5 years AND
        // it's a datacenter IP — real consumer hardware doesn't have
        // 5-year gaps between GPU and OS generation
        if (gpuYear > 0 && osYear > 0 &&
            Math.Abs(gpuYear - osYear) > 5 &&
            isDatacenter)
        {
            isAnomaly = true;
        }

        // ANOMALY 3: Very new device with zero engagement from datacenter
        // → Fresh automation environment
        if (ageYears <= 1 && isDatacenter && mouseEntropy < 0.5 &&
            gpuYear > 0 && IsVirtualGpu(gpu!))
        {
            isAnomaly = true;
        }

        return new AgeResult(ageYears, isAnomaly);
    }

    // ════════════════════════════════════════════════════════════════════════
    // PRIVATE HELPERS
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Estimates OS release year from OS name and version.
    /// </summary>
    private static int EstimateOsYear(string? os, string? version)
    {
        if (string.IsNullOrEmpty(os))
            return 0;

        // Windows: check os+version combined
        if (os.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            var combined = string.IsNullOrEmpty(version) ? os : $"Windows {version}";
            for (var i = 0; i < s_osYears.Length; i++)
            {
                if (combined.Contains(s_osYears[i].Pattern, StringComparison.OrdinalIgnoreCase))
                    return s_osYears[i].Year;
            }

            return 2015; // Unknown Windows → assume Win10 era
        }

        // macOS: version string maps to year
        if (os.Contains("Mac", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(version))
        {
            for (var i = 0; i < s_osYears.Length; i++)
            {
                if (version.StartsWith(s_osYears[i].Pattern, StringComparison.OrdinalIgnoreCase))
                    return s_osYears[i].Year;
            }

            // macOS version >= 15 → estimate based on version number
            if (int.TryParse(version.Split('.')[0], out var macMajor) && macMajor >= 15)
                return 2024 + (macMajor - 15);
        }

        // Linux: can't determine age from kernel version reliably
        // Android: version maps roughly to year
        if (os.Contains("Android", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(version))
        {
            if (int.TryParse(version.Split('.')[0], out var androidMajor))
            {
                return androidMajor switch
                {
                    >= 15 => 2025,
                    14 => 2023,
                    13 => 2022,
                    12 => 2021,
                    11 => 2020,
                    10 => 2019,
                    9 => 2018,
                    8 => 2017,
                    7 => 2016,
                    6 => 2015,
                    _ => 2014
                };
            }
        }

        return 0;
    }

    /// <summary>
    /// Estimates browser release year from browser name and major version.
    /// </summary>
    private static int EstimateBrowserYear(string? browser, string? version)
    {
        if (string.IsNullOrEmpty(browser) || string.IsNullOrEmpty(version))
            return 0;

        // Extract major version number
        var dotIdx = version.IndexOf('.');
        var majorStr = dotIdx > 0 ? version[..dotIdx] : version;
        if (!int.TryParse(majorStr, out var major) || major <= 0)
            return 0;

        if (browser.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
            browser.Contains("Edge", StringComparison.OrdinalIgnoreCase))
            return ChromeVersionToYear(major);

        if (browser.Contains("Firefox", StringComparison.OrdinalIgnoreCase))
            return FirefoxVersionToYear(major);

        if (browser.Contains("Safari", StringComparison.OrdinalIgnoreCase))
        {
            // Safari versions ~= macOS versions in recent years
            if (major >= 18) return 2024;
            if (major >= 17) return 2023;
            if (major >= 16) return 2022;
            if (major >= 15) return 2021;
            if (major >= 14) return 2020;
            if (major >= 13) return 2019;
            return 2018;
        }

        return 0;
    }

    /// <summary>
    /// Returns the smaller of two non-zero values. If one is zero, returns the other.
    /// </summary>
    private static int MinNonZero(int a, int b)
    {
        if (a == 0) return b;
        if (b == 0) return a;
        return Math.Min(a, b);
    }

    /// <summary>
    /// Checks if the GPU renderer string indicates a virtual/emulated GPU.
    /// </summary>
    private static bool IsVirtualGpu(string gpu)
    {
        return gpu.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase)
            || gpu.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase)
            || gpu.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)
            || gpu.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || gpu.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase);
    }
}
