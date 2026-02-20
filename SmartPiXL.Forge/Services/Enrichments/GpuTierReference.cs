namespace SmartPiXL.Forge.Services.Enrichments;

// ============================================================================
// GPU TIER REFERENCE — Static lookup of GPU renderer strings → tier classification.
//
// The browser exposes the GPU renderer via WebGL's UNMASKED_RENDERER_OES.
// The PiXL Script captures this as the `gpu` query parameter.
//
// GPU tier is an affluence proxy:
//   HIGH  = Current/recent high-end GPU → likely affluent buyer
//   MID   = Mid-range or 1-2 generation old high-end
//   LOW   = Integrated graphics, virtual, or very old discrete GPU
//
// The lookup uses substring matching (case-insensitive) against the GPU
// renderer string. Patterns are ordered longest-first within each tier
// to avoid shorter patterns matching first (e.g., "RTX 50" before "RTX 5").
//
// MAINTENANCE:
//   Add new GPU patterns here as they appear in live data. The pattern list
//   intentionally covers ~50 common GPU families. Start broad (generation-based),
//   refine with live data over time.
//
// USED BY: DeviceAffluenceService
// ============================================================================

/// <summary>
/// Static GPU renderer string → tier lookup. Thread-safe (read-only after init).
/// </summary>
public static class GpuTierReference
{
    /// <summary>
    /// GPU tier classification: LOW, MID, or HIGH.
    /// </summary>
    public enum GpuTier
    {
        Unknown = 0,
        Low = 1,
        Mid = 2,
        High = 3
    }

    // Patterns: (substring, tier) — checked in order, first match wins.
    // Ordered: HIGH patterns first (newest/most expensive), then MID, then LOW.
    // Within each tier: longer/more-specific patterns first.
    private static readonly (string Pattern, GpuTier Tier)[] s_patterns =
    [
        // ── PROFESSIONAL — Must be checked BEFORE consumer RTX patterns ──
        // Quadro contains "RTX" in its name, so Quadro RTX must precede RTX 50/40 etc
        ("Quadro RTX", GpuTier.Mid),
        ("Quadro P",   GpuTier.Mid),

        // ── HIGH — Current gen / flagship ────────────────────────────────
        // NVIDIA RTX 50-series
        ("RTX 5090", GpuTier.High),
        ("RTX 5080", GpuTier.High),
        ("RTX 5070", GpuTier.High),
        ("RTX 50",   GpuTier.High),
        // NVIDIA RTX 40-series
        ("RTX 4090", GpuTier.High),
        ("RTX 4080", GpuTier.High),
        ("RTX 4070", GpuTier.High),
        ("RTX 4060", GpuTier.High),
        ("RTX 40",   GpuTier.High),
        // AMD Radeon RX 9000-series
        ("RX 9070",  GpuTier.High),
        ("RX 90",    GpuTier.High),
        // AMD Radeon RX 7000-series
        ("RX 7900",  GpuTier.High),
        ("RX 7800",  GpuTier.High),
        ("RX 7700",  GpuTier.High),
        ("RX 7600",  GpuTier.High),
        ("RX 7",     GpuTier.High),
        // Apple M4 / M3 Pro/Max/Ultra
        ("Apple M4", GpuTier.High),
        ("Apple M3 Ultra", GpuTier.High),
        ("Apple M3 Max",   GpuTier.High),
        ("Apple M3 Pro",   GpuTier.High),
        ("Apple M3", GpuTier.High),
        // Intel Arc A-series high-end (discrete) — handles both "Arc A770" and "Arc(TM) A770"
        ("A770",  GpuTier.High),
        ("A750",  GpuTier.High),

        // ── MID — Previous gen / mid-range ───────────────────────────────
        // NVIDIA RTX 30-series
        ("RTX 3090", GpuTier.Mid),
        ("RTX 3080", GpuTier.Mid),
        ("RTX 3070", GpuTier.Mid),
        ("RTX 3060", GpuTier.Mid),
        ("RTX 3050", GpuTier.Mid),
        ("RTX 30",   GpuTier.Mid),
        // NVIDIA RTX 20-series
        ("RTX 2080", GpuTier.Mid),
        ("RTX 2070", GpuTier.Mid),
        ("RTX 2060", GpuTier.Mid),
        ("RTX 20",   GpuTier.Mid),
        // NVIDIA GTX 16-series
        ("GTX 1660", GpuTier.Mid),
        ("GTX 1650", GpuTier.Mid),
        ("GTX 16",   GpuTier.Mid),
        // AMD Radeon RX 6000-series
        ("RX 6900",  GpuTier.Mid),
        ("RX 6800",  GpuTier.Mid),
        ("RX 6700",  GpuTier.Mid),
        ("RX 6600",  GpuTier.Mid),
        ("RX 6500",  GpuTier.Mid),
        ("RX 6",     GpuTier.Mid),
        // AMD Radeon RX 5000-series (4-digit model numbers)
        ("RX 5700",  GpuTier.Mid),
        ("RX 5600",  GpuTier.Mid),
        ("RX 5500",  GpuTier.Mid),
        // AMD Radeon RX 400/500-series (3-digit, LOW tier) — MUST precede "RX 5" catch-all
        ("RX 580",   GpuTier.Low),
        ("RX 570",   GpuTier.Low),
        ("RX 560",   GpuTier.Low),
        ("RX 550",   GpuTier.Low),
        ("RX 5",     GpuTier.Mid),  // catch-all for remaining 5000-series (e.g. RX 5300)
        ("RX 480",   GpuTier.Low),
        ("RX 470",   GpuTier.Low),
        ("RX 460",   GpuTier.Low),
        // Apple M1/M2
        ("Apple M2 Ultra", GpuTier.Mid),
        ("Apple M2 Max",   GpuTier.Mid),
        ("Apple M2 Pro",   GpuTier.Mid),
        ("Apple M2", GpuTier.Mid),
        ("Apple M1 Ultra", GpuTier.Mid),
        ("Apple M1 Max",   GpuTier.Mid),
        ("Apple M1 Pro",   GpuTier.Mid),
        ("Apple M1", GpuTier.Mid),
        // Intel Arc (lower-end discrete) — handles both "Arc A580" and "Arc(TM) A580"
        ("A580", GpuTier.Mid),
        ("A380", GpuTier.Mid),
        ("Arc A",    GpuTier.Mid),

        // ── LOW — Integrated / virtual / very old ────────────────────────
        // NVIDIA GTX 10-series and older
        ("GTX 1080", GpuTier.Low),
        ("GTX 1070", GpuTier.Low),
        ("GTX 1060", GpuTier.Low),
        ("GTX 1050", GpuTier.Low),
        ("GTX 10",   GpuTier.Low),
        ("GTX 9",    GpuTier.Low),
        ("GTX 7",    GpuTier.Low),
        ("GT 1030",  GpuTier.Low),
        ("GT 730",   GpuTier.Low),
        ("GT 710",   GpuTier.Low),
        ("GT 6",     GpuTier.Low),
        // AMD older (RX 4xx/5xx already handled above, before "RX 5" catch-all)
        ("Radeon R9", GpuTier.Low),
        ("Radeon R7", GpuTier.Low),
        ("Radeon R5", GpuTier.Low),
        // Intel integrated
        ("Intel(R) Iris(R) Xe",      GpuTier.Low),
        ("Intel(R) UHD Graphics",    GpuTier.Low),
        ("Intel(R) HD Graphics",     GpuTier.Low),
        ("Intel(R) Iris(R) Plus",    GpuTier.Low),
        ("Intel(R) Iris(R) Pro",     GpuTier.Low),
        ("Iris Xe",         GpuTier.Low),
        ("UHD Graphics",    GpuTier.Low),
        ("HD Graphics",     GpuTier.Low),
        ("Iris Plus",       GpuTier.Low),
        ("Iris Pro",        GpuTier.Low),
        // Virtual / software renderers
        ("SwiftShader",     GpuTier.Low),
        ("llvmpipe",        GpuTier.Low),
        ("Mesa",            GpuTier.Low),
        ("Microsoft Basic Render", GpuTier.Low),
        ("Parallels",       GpuTier.Low),
        ("VMware",          GpuTier.Low),
        ("VirtualBox",      GpuTier.Low),
        ("ANGLE",           GpuTier.Low),
        // Mali (mobile integrated, mostly budget)
        ("Mali-G",          GpuTier.Low),
        ("Mali-T",          GpuTier.Low),
        // Adreno (mobile integrated)
        ("Adreno",          GpuTier.Low),
        // PowerVR (mobile, typically budget)
        ("PowerVR",         GpuTier.Low),
    ];

    /// <summary>
    /// Classifies a GPU renderer string into a tier.
    /// Uses case-insensitive substring matching. First match wins.
    /// </summary>
    /// <param name="gpuRenderer">The WebGL UNMASKED_RENDERER_OES value (the <c>gpu</c> query param).</param>
    /// <returns>The GPU tier, or <see cref="GpuTier.Unknown"/> if no pattern matches.</returns>
    public static GpuTier Classify(string? gpuRenderer)
    {
        if (string.IsNullOrEmpty(gpuRenderer))
            return GpuTier.Unknown;

        for (var i = 0; i < s_patterns.Length; i++)
        {
            if (gpuRenderer.Contains(s_patterns[i].Pattern, StringComparison.OrdinalIgnoreCase))
                return s_patterns[i].Tier;
        }

        return GpuTier.Unknown;
    }

    /// <summary>
    /// Returns the tier name as a short string: "HIGH", "MID", "LOW", or null for Unknown.
    /// </summary>
    public static string? TierToString(GpuTier tier) => tier switch
    {
        GpuTier.High => "HIGH",
        GpuTier.Mid => "MID",
        GpuTier.Low => "LOW",
        _ => null
    };

    // ========================================================================
    // GPU RELEASE YEAR ESTIMATION — Used by DeviceAgeEstimationService
    // ========================================================================
    // Separate array optimized for release-year lookup. More specific patterns
    // than the tier classification (e.g., individual models with exact years).
    // First match wins, most specific patterns first.
    // ========================================================================

    private static readonly (string Pattern, int Year)[] s_releaseYears =
    [
        // ── NVIDIA RTX 50-series (2025) ──────────────────────────────────
        ("RTX 5090", 2025), ("RTX 5080", 2025), ("RTX 5070", 2025), ("RTX 50", 2025),
        // ── NVIDIA RTX 40-series (2022-2023) ─────────────────────────────
        ("RTX 4090", 2022), ("RTX 4080", 2022), ("RTX 4070", 2023), ("RTX 4060", 2023), ("RTX 40", 2022),
        // ── NVIDIA RTX 30-series (2020-2022) ─────────────────────────────
        ("RTX 3090", 2020), ("RTX 3080", 2020), ("RTX 3070", 2020),
        ("RTX 3060", 2021), ("RTX 3050", 2022), ("RTX 30", 2020),
        // ── NVIDIA RTX 20-series (2018-2019) ─────────────────────────────
        ("RTX 2080", 2018), ("RTX 2070", 2018), ("RTX 2060", 2019), ("RTX 20", 2018),
        // ── NVIDIA GTX 16-series (2019) ──────────────────────────────────
        ("GTX 1660", 2019), ("GTX 1650", 2019), ("GTX 16", 2019),
        // ── NVIDIA GTX 10-series (2016) ──────────────────────────────────
        ("GTX 1080", 2016), ("GTX 1070", 2016), ("GTX 1060", 2016), ("GTX 1050", 2016), ("GTX 10", 2016),
        // ── NVIDIA GTX 9-series (2014-2015) ──────────────────────────────
        ("GTX 980", 2014), ("GTX 970", 2014), ("GTX 960", 2015), ("GTX 9", 2014),
        // ── NVIDIA GTX 7-series (2013) ───────────────────────────────────
        ("GTX 780", 2013), ("GTX 770", 2013), ("GTX 760", 2013), ("GTX 7", 2013),
        // ── NVIDIA GT (budget/OEM) ───────────────────────────────────────
        ("GT 1030", 2017), ("GT 730", 2014), ("GT 710", 2014), ("GT 6", 2012),
        // ── NVIDIA Quadro (professional) ─────────────────────────────────
        ("Quadro RTX", 2018), ("Quadro P", 2016),
        // ── AMD RX 9000-series (2025) ────────────────────────────────────
        ("RX 9070", 2025), ("RX 90", 2025),
        // ── AMD RX 7000-series (2022-2023) ───────────────────────────────
        ("RX 7900", 2022), ("RX 7800", 2023), ("RX 7700", 2023), ("RX 7600", 2023), ("RX 7", 2022),
        // ── AMD RX 6000-series (2020-2022) ───────────────────────────────
        ("RX 6900", 2020), ("RX 6800", 2020), ("RX 6700", 2021),
        ("RX 6600", 2021), ("RX 6500", 2022), ("RX 6", 2020),
        // ── AMD RX 5000-series (2019-2020) ───────────────────────────────
        ("RX 5700", 2019), ("RX 5600", 2020), ("RX 5500", 2019),
        // ── AMD RX 400/500-series (2016-2017) ────────────────────────────
        ("RX 580", 2017), ("RX 570", 2017), ("RX 560", 2017), ("RX 550", 2017),
        ("RX 480", 2016), ("RX 470", 2016), ("RX 460", 2016),
        // ── AMD Radeon R-series (2013) ───────────────────────────────────
        ("Radeon R9", 2013), ("Radeon R7", 2013), ("Radeon R5", 2013),
        // ── Apple Silicon ────────────────────────────────────────────────
        ("Apple M4", 2024), ("Apple M3", 2023), ("Apple M2", 2022), ("Apple M1", 2020),
        // ── Intel Arc (discrete, 2022-2023) ──────────────────────────────
        ("A770", 2022), ("A750", 2022), ("A580", 2023), ("A380", 2022),
        // ── Intel integrated (generational estimates) ────────────────────
        ("Iris Xe", 2020), ("UHD Graphics 7", 2020), ("UHD Graphics 6", 2017),
        ("HD Graphics 6", 2015), ("HD Graphics 5", 2013), ("HD Graphics 4", 2012),
        ("Iris Plus", 2016), ("Iris Pro", 2014),
    ];

    /// <summary>
    /// Estimates the GPU release year based on model pattern matching.
    /// Returns 0 if the GPU is unrecognized, virtual, or mobile integrated.
    /// Used by <c>DeviceAgeEstimationService</c> for device age anomaly detection.
    /// </summary>
    public static int EstimateReleaseYear(string? gpuRenderer)
    {
        if (string.IsNullOrEmpty(gpuRenderer))
            return 0;

        for (var i = 0; i < s_releaseYears.Length; i++)
        {
            if (gpuRenderer.Contains(s_releaseYears[i].Pattern, StringComparison.OrdinalIgnoreCase))
                return s_releaseYears[i].Year;
        }

        return 0;
    }
}
