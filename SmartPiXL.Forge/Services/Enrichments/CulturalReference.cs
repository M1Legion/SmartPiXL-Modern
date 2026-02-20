// ─────────────────────────────────────────────────────────────────────────────
// SmartPiXL Forge — Cultural Reference Data
// Static lookup tables for geographic/cultural arbitrage detection.
// Font-platform markers, language-country maps, timezone-country maps,
// number format conventions, and non-Gregorian calendar systems.
// Phase 6 — Tier 3 Enrichments (Asymmetric Detection)
// ─────────────────────────────────────────────────────────────────────────────

namespace SmartPiXL.Forge.Services.Enrichments;

/// <summary>
/// Static cultural fingerprint reference data used by <see cref="GeographicArbitrageService"/>.
/// Contains platform-font signatures, language-country mappings, timezone validation,
/// and locale-specific conventions (decimal separators, calendars).
/// </summary>
internal static class CulturalReference
{
    // ════════════════════════════════════════════════════════════════════════
    // PLATFORM-SPECIFIC MARKER FONTS
    // Fonts that ship exclusively (or nearly so) with a specific OS.
    // Presence on a different platform = spoofed environment.
    // ════════════════════════════════════════════════════════════════════════

    public static readonly HashSet<string> WindowsMarkerFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Segoe UI", "Segoe UI Symbol", "Segoe UI Emoji", "Segoe MDL2 Assets",
        "Consolas", "Calibri", "Cambria", "Corbel", "Candara", "Constantia",
        "Trebuchet MS", "MS Sans Serif", "MS Serif", "Tahoma",
        "Microsoft YaHei", "Microsoft JhengHei", "MingLiU", "SimSun", "SimHei"
    };

    public static readonly HashSet<string> MacOSMarkerFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Helvetica Neue", "Lucida Grande", "Geneva", "Optima", "Futura",
        "Avenir", "Avenir Next", "Apple Color Emoji", "Apple Symbols",
        ".AppleSystemUIFont", ".SF Pro", "Menlo", "Monaco", "Apple Chancery",
        "Skia", "Hoefler Text", "Phosphate", "Chalkduster"
    };

    public static readonly HashSet<string> LinuxMarkerFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "DejaVu Sans", "DejaVu Sans Mono", "DejaVu Serif",
        "Liberation Sans", "Liberation Mono", "Liberation Serif",
        "Ubuntu", "Ubuntu Mono", "Cantarell", "Droid Sans", "Droid Sans Mono",
        "FreeSans", "FreeMono", "FreeSerif"
    };

    // ════════════════════════════════════════════════════════════════════════
    // REGIONAL MARKER FONTS
    // Font families that indicate a specific cultural/regional origin.
    // Cross-referenced against geo-IP country for consistency.
    // ════════════════════════════════════════════════════════════════════════

    public static readonly HashSet<string> CJKFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        // Japanese
        "MS Gothic", "MS Mincho", "MS PGothic", "MS PMincho", "Meiryo",
        "Yu Gothic", "Yu Mincho", "Hiragino Sans", "Hiragino Mincho",
        // Chinese (Simplified + Traditional)
        "SimSun", "SimHei", "Microsoft YaHei", "Microsoft JhengHei",
        "NSimSun", "KaiTi", "FangSong", "PingFang SC", "PingFang TC",
        "PingFang HK", "STSong", "STKaiti", "STXihei", "STHeiti",
        // Korean
        "NanumGothic", "NanumMyeongjo", "Malgun Gothic", "Gulim", "Batang"
    };

    public static readonly HashSet<string> ArabicFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Arabic Typesetting", "Simplified Arabic", "Traditional Arabic",
        "Sakkal Majalla", "Amiri", "Noto Naskh Arabic", "Scheherazade"
    };

    public static readonly HashSet<string> CyrillicFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "PT Sans", "PT Serif", "PT Mono", "Yandex Sans Text"
    };

    // ════════════════════════════════════════════════════════════════════════
    // LANGUAGE → EXPECTED COUNTRIES
    // ISO 639-1 → set of ISO 3166-1 alpha-2 codes where the language is
    // a dominant/official language. Missing entries = no check possible.
    // ════════════════════════════════════════════════════════════════════════

    public static readonly Dictionary<string, HashSet<string>> LanguageCountries =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new(StringComparer.OrdinalIgnoreCase) { "US", "GB", "AU", "CA", "NZ", "IE", "ZA", "IN", "SG", "PH", "KE", "NG" },
        ["fr"] = new(StringComparer.OrdinalIgnoreCase) { "FR", "CA", "BE", "CH", "LU", "MC", "CI", "SN", "ML", "BF", "CD", "MG" },
        ["de"] = new(StringComparer.OrdinalIgnoreCase) { "DE", "AT", "CH", "LI", "LU" },
        ["es"] = new(StringComparer.OrdinalIgnoreCase) { "ES", "MX", "AR", "CO", "PE", "CL", "VE", "EC", "GT", "CU", "BO", "DO", "HN", "PY", "SV", "NI", "CR", "PA", "UY" },
        ["pt"] = new(StringComparer.OrdinalIgnoreCase) { "BR", "PT", "AO", "MZ" },
        ["it"] = new(StringComparer.OrdinalIgnoreCase) { "IT", "CH", "SM", "VA" },
        ["nl"] = new(StringComparer.OrdinalIgnoreCase) { "NL", "BE", "SR" },
        ["da"] = new(StringComparer.OrdinalIgnoreCase) { "DK", "GL" },
        ["sv"] = new(StringComparer.OrdinalIgnoreCase) { "SE", "FI" },
        ["no"] = new(StringComparer.OrdinalIgnoreCase) { "NO" },
        ["nb"] = new(StringComparer.OrdinalIgnoreCase) { "NO" },
        ["nn"] = new(StringComparer.OrdinalIgnoreCase) { "NO" },
        ["fi"] = new(StringComparer.OrdinalIgnoreCase) { "FI" },
        ["pl"] = new(StringComparer.OrdinalIgnoreCase) { "PL" },
        ["cs"] = new(StringComparer.OrdinalIgnoreCase) { "CZ" },
        ["sk"] = new(StringComparer.OrdinalIgnoreCase) { "SK" },
        ["hu"] = new(StringComparer.OrdinalIgnoreCase) { "HU" },
        ["ro"] = new(StringComparer.OrdinalIgnoreCase) { "RO", "MD" },
        ["bg"] = new(StringComparer.OrdinalIgnoreCase) { "BG" },
        ["hr"] = new(StringComparer.OrdinalIgnoreCase) { "HR" },
        ["sr"] = new(StringComparer.OrdinalIgnoreCase) { "RS" },
        ["uk"] = new(StringComparer.OrdinalIgnoreCase) { "UA" },
        ["ru"] = new(StringComparer.OrdinalIgnoreCase) { "RU", "BY", "KZ", "KG" },
        ["ja"] = new(StringComparer.OrdinalIgnoreCase) { "JP" },
        ["ko"] = new(StringComparer.OrdinalIgnoreCase) { "KR" },
        ["zh"] = new(StringComparer.OrdinalIgnoreCase) { "CN", "TW", "HK", "SG" },
        ["ar"] = new(StringComparer.OrdinalIgnoreCase) { "SA", "AE", "EG", "IQ", "MA", "DZ", "TN", "LB", "JO", "KW", "QA", "BH", "OM", "YE", "LY", "SY" },
        ["hi"] = new(StringComparer.OrdinalIgnoreCase) { "IN" },
        ["th"] = new(StringComparer.OrdinalIgnoreCase) { "TH" },
        ["vi"] = new(StringComparer.OrdinalIgnoreCase) { "VN" },
        ["id"] = new(StringComparer.OrdinalIgnoreCase) { "ID" },
        ["ms"] = new(StringComparer.OrdinalIgnoreCase) { "MY", "SG", "BN" },
        ["tr"] = new(StringComparer.OrdinalIgnoreCase) { "TR" },
        ["el"] = new(StringComparer.OrdinalIgnoreCase) { "GR", "CY" },
        ["he"] = new(StringComparer.OrdinalIgnoreCase) { "IL" },
        ["fa"] = new(StringComparer.OrdinalIgnoreCase) { "IR", "AF" },
    };

    // ════════════════════════════════════════════════════════════════════════
    // COUNTRY → TIMEZONE PREFIX(ES)
    // Expected IANA timezone prefix(es) for top ~50 countries.
    // Any timezone starting with one of these prefixes is considered consistent.
    // ════════════════════════════════════════════════════════════════════════

    public static readonly Dictionary<string, string[]> CountryTimezones =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Americas
        ["US"] = ["America/"], ["CA"] = ["America/"], ["MX"] = ["America/"],
        ["BR"] = ["America/"], ["AR"] = ["America/"], ["CL"] = ["America/"],
        ["CO"] = ["America/"], ["PE"] = ["America/"], ["VE"] = ["America/"],
        ["EC"] = ["America/"], ["CR"] = ["America/"],
        // Europe
        ["GB"] = ["Europe/London"], ["IE"] = ["Europe/Dublin"],
        ["FR"] = ["Europe/Paris"], ["DE"] = ["Europe/Berlin"],
        ["ES"] = ["Europe/Madrid"], ["IT"] = ["Europe/Rome"],
        ["NL"] = ["Europe/Amsterdam"], ["BE"] = ["Europe/Brussels"],
        ["CH"] = ["Europe/Zurich"], ["AT"] = ["Europe/Vienna"],
        ["SE"] = ["Europe/Stockholm"], ["NO"] = ["Europe/Oslo"],
        ["DK"] = ["Europe/Copenhagen"], ["FI"] = ["Europe/Helsinki"],
        ["PL"] = ["Europe/Warsaw"], ["CZ"] = ["Europe/Prague"],
        ["HU"] = ["Europe/Budapest"], ["RO"] = ["Europe/Bucharest"],
        ["BG"] = ["Europe/Sofia"], ["GR"] = ["Europe/Athens"],
        ["TR"] = ["Europe/Istanbul"], ["UA"] = ["Europe/Kiev", "Europe/Kyiv"],
        ["RU"] = ["Europe/Moscow", "Asia/"],
        // Asia
        ["JP"] = ["Asia/Tokyo"], ["KR"] = ["Asia/Seoul"],
        ["CN"] = ["Asia/Shanghai", "Asia/Chongqing", "Asia/Urumqi"],
        ["TW"] = ["Asia/Taipei"], ["HK"] = ["Asia/Hong_Kong"],
        ["SG"] = ["Asia/Singapore"],
        ["IN"] = ["Asia/Kolkata", "Asia/Calcutta"],
        ["TH"] = ["Asia/Bangkok"], ["VN"] = ["Asia/Ho_Chi_Minh", "Asia/Saigon"],
        ["ID"] = ["Asia/Jakarta", "Asia/Makassar"],
        ["MY"] = ["Asia/Kuala_Lumpur"], ["PH"] = ["Asia/Manila"],
        ["IL"] = ["Asia/Jerusalem", "Asia/Tel_Aviv"],
        ["AE"] = ["Asia/Dubai"], ["SA"] = ["Asia/Riyadh"],
        // Africa/Oceania
        ["EG"] = ["Africa/Cairo"], ["ZA"] = ["Africa/Johannesburg"],
        ["NG"] = ["Africa/Lagos"], ["KE"] = ["Africa/Nairobi"],
        ["AU"] = ["Australia/"], ["NZ"] = ["Pacific/Auckland"],
    };

    // ════════════════════════════════════════════════════════════════════════
    // NUMBER FORMAT: COMMA-DECIMAL COUNTRIES
    // Countries where the decimal separator is comma (1.000,00 format).
    // All unlisted countries assumed to use period (1,000.00 format).
    // ════════════════════════════════════════════════════════════════════════

    public static readonly HashSet<string> CommaDecimalCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "DE", "FR", "IT", "ES", "PT", "NL", "BE", "AT", "CH", "SE", "NO", "DK", "FI",
        "PL", "CZ", "SK", "HU", "RO", "BG", "HR", "RS", "GR", "TR", "RU", "UA",
        "BR", "AR", "CL", "CO", "VE", "EC", "PE", "ID", "VN"
    };

    // ════════════════════════════════════════════════════════════════════════
    // NON-GREGORIAN CALENDAR SYSTEMS → EXPECTED COUNTRIES
    // If a browser reports a non-Gregorian calendar, the geo country should
    // match. Persian calendar on a "US" IP = strong VPN/proxy indicator.
    // ════════════════════════════════════════════════════════════════════════

    public static readonly Dictionary<string, HashSet<string>> CalendarCountries =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["buddhist"]         = new(StringComparer.OrdinalIgnoreCase) { "TH" },
        ["persian"]          = new(StringComparer.OrdinalIgnoreCase) { "IR", "AF" },
        ["islamic"]          = new(StringComparer.OrdinalIgnoreCase) { "SA", "AE", "EG", "IQ", "MA", "DZ", "TN", "LB", "JO", "KW", "QA", "BH", "OM", "YE", "LY", "SY" },
        ["islamic-civil"]    = new(StringComparer.OrdinalIgnoreCase) { "SA", "AE", "EG", "IQ", "MA", "DZ", "TN" },
        ["islamic-umalqura"] = new(StringComparer.OrdinalIgnoreCase) { "SA" },
        ["hebrew"]           = new(StringComparer.OrdinalIgnoreCase) { "IL" },
        ["chinese"]          = new(StringComparer.OrdinalIgnoreCase) { "CN", "TW", "HK", "SG" },
        ["japanese"]         = new(StringComparer.OrdinalIgnoreCase) { "JP" },
        ["roc"]              = new(StringComparer.OrdinalIgnoreCase) { "TW" },
    };

    // ════════════════════════════════════════════════════════════════════════
    // REGIONAL COUNTRY SETS
    // Used to validate regional-specific fonts against geo country.
    // ════════════════════════════════════════════════════════════════════════

    public static readonly HashSet<string> CJKCountries = new(StringComparer.OrdinalIgnoreCase)
    { "JP", "KR", "CN", "TW", "HK", "SG" };

    public static readonly HashSet<string> ArabicCountries = new(StringComparer.OrdinalIgnoreCase)
    { "SA", "AE", "EG", "IQ", "MA", "DZ", "TN", "LB", "JO", "KW", "QA", "BH", "OM", "YE", "LY", "SY" };

    public static readonly HashSet<string> CyrillicCountries = new(StringComparer.OrdinalIgnoreCase)
    { "RU", "UA", "BY", "BG", "RS", "KZ", "KG", "MN" };

    // ════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extracts the primary language code from a locale string (e.g., "en-US" → "en").
    /// </summary>
    public static string? ExtractPrimaryLanguage(string? lang)
    {
        if (string.IsNullOrEmpty(lang)) return null;
        var idx = lang.IndexOf('-');
        return idx > 0 ? lang[..idx] : lang;
    }

    /// <summary>
    /// Checks whether the given IANA timezone is consistent with the geo-IP country code.
    /// Returns true if no data available (can't determine inconsistency).
    /// </summary>
    public static bool IsTimezoneConsistentWithCountry(string? timezone, string? countryCode)
    {
        if (string.IsNullOrEmpty(timezone) || string.IsNullOrEmpty(countryCode))
            return true; // missing → no inconsistency detectable

        if (!CountryTimezones.TryGetValue(countryCode, out var prefixes))
            return true; // country not in reference → can't check

        for (var i = 0; i < prefixes.Length; i++)
        {
            if (timezone.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether the primary browser language is consistent with the geo-IP country.
    /// English is allowed everywhere (widely used as secondary language).
    /// Returns true if no data available.
    /// </summary>
    public static bool IsLanguageConsistentWithCountry(string? primaryLang, string? countryCode)
    {
        if (string.IsNullOrEmpty(primaryLang) || string.IsNullOrEmpty(countryCode))
            return true;

        // English is a reasonable secondary language everywhere
        if (primaryLang.Equals("en", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!LanguageCountries.TryGetValue(primaryLang, out var countries))
            return true; // language not in reference → can't check

        return countries.Contains(countryCode);
    }
}
