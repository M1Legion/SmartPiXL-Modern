// ─────────────────────────────────────────────────────────────────────────────
// SmartPiXL Forge — Geographic Arbitrage Service
// Cross-references cultural fingerprint signals (fonts, language, timezone,
// number format, calendar system) against IP-derived geography to detect
// VPN/proxy/spoofed location. Stateless singleton.
// Phase 6 — Tier 3 Enrichments (Asymmetric Detection)
// ─────────────────────────────────────────────────────────────────────────────

namespace SmartPiXL.Forge.Services.Enrichments;

/// <summary>
/// Scores the cultural consistency of a tracking record by comparing browser
/// fingerprint signals against the expected profile for the geo-IP country.
/// Score 0 = maximally inconsistent (likely VPN/proxy), 100 = fully consistent.
/// </summary>
/// <remarks>
/// Each cultural signal is evaluated independently with a weight reflecting its
/// discriminative power. Fonts are the strongest signal (hard to fake without
/// knowledge of which fonts ship with a specific OS/locale), while number
/// format is weaker (easily changed in settings).
/// </remarks>
public sealed class GeographicArbitrageService
{
    // ════════════════════════════════════════════════════════════════════════
    // RESULT
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Result of cultural consistency analysis.
    /// </summary>
    /// <param name="CulturalScore">0-100 consistency score (100 = fully consistent with geo).</param>
    /// <param name="Flags">Comma-separated anomaly flags, or null if no anomalies.</param>
    /// <param name="TimezoneMatch">Whether the browser timezone is consistent with geo country.</param>
    public readonly record struct ArbitrageResult(int CulturalScore, string? Flags, bool TimezoneMatch);

    // ════════════════════════════════════════════════════════════════════════
    // SIGNAL WEIGHTS (must sum to 100)
    // ════════════════════════════════════════════════════════════════════════

    private const int WeightFontPlatform = 25; // Wrong-platform fonts detected
    private const int WeightFontRegional = 10; // Regional fonts inconsistent with geo
    private const int WeightLanguage     = 20; // Primary language vs geo country
    private const int WeightTimezone     = 20; // Browser timezone vs geo country
    private const int WeightNumberFormat = 10; // Decimal separator vs geo conventions
    private const int WeightCalendar     = 10; // Non-Gregorian calendar vs geo country
    private const int WeightVoice        =  5; // Speech synthesis voices vs platform

    // Total = 25 + 10 + 20 + 20 + 10 + 10 + 5 = 100

    // ════════════════════════════════════════════════════════════════════════
    // PUBLIC API
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Analyzes the cultural consistency of a tracking record against its geo-IP country.
    /// </summary>
    /// <param name="country">ISO 3166-1 alpha-2 country code from MaxMind (e.g., "US").</param>
    /// <param name="platform">Browser platform string (e.g., "Win32", "MacIntel", "Linux x86_64").</param>
    /// <param name="fonts">Comma-separated font names detected by PiXL Script.</param>
    /// <param name="lang">Primary browser language (e.g., "en-US", "ja").</param>
    /// <param name="timezone">IANA timezone from browser (e.g., "America/New_York").</param>
    /// <param name="numberFormat">Formatted number from Intl.NumberFormat (e.g., "1,234.56").</param>
    /// <param name="tzLocale">Locale/calendar info from Intl.DateTimeFormat (may contain calendar system).</param>
    /// <param name="voices">Speech synthesis voice count or names.</param>
    /// <param name="detectedOS">Parsed OS from UA (e.g., "Windows", "Mac OS X").</param>
    public ArbitrageResult Analyze(
        string? country,
        string? platform,
        string? fonts,
        string? lang,
        string? timezone,
        string? numberFormat,
        string? tzLocale,
        string? voices,
        string? detectedOS)
    {
        // No geo country = can't perform arbitrage
        if (string.IsNullOrEmpty(country))
            return new ArbitrageResult(100, null, true);

        var score = 0;
        List<string>? flags = null;

        // ── 1. Font-Platform Check (25 pts) ──────────────────────────────
        score += CheckFontPlatform(platform, detectedOS, fonts, ref flags);

        // ── 2. Font-Regional Check (10 pts) ──────────────────────────────
        score += CheckFontRegional(country, fonts, ref flags);

        // ── 3. Language Check (20 pts) ───────────────────────────────────
        score += CheckLanguage(country, lang, ref flags);

        // ── 4. Timezone Check (20 pts) ───────────────────────────────────
        var tzMatch = CulturalReference.IsTimezoneConsistentWithCountry(timezone, country);
        if (tzMatch)
        {
            score += WeightTimezone;
        }
        else
        {
            AddFlag(ref flags, "tz-mismatch");
        }

        // ── 5. Number Format Check (10 pts) ──────────────────────────────
        score += CheckNumberFormat(country, numberFormat, ref flags);

        // ── 6. Calendar Check (10 pts) ───────────────────────────────────
        score += CheckCalendar(country, tzLocale, ref flags);

        // ── 7. Voice Check (5 pts) ───────────────────────────────────────
        score += CheckVoice(platform, detectedOS, voices, ref flags);

        var flagStr = flags is { Count: > 0 }
            ? string.Join(',', flags)
            : null;

        return new ArbitrageResult(score, flagStr, tzMatch);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CHECK IMPLEMENTATIONS
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks if detected fonts match the expected platform.
    /// Finding Windows-exclusive fonts on a macOS platform (or vice versa) = penalty.
    /// </summary>
    private static int CheckFontPlatform(
        string? platform, string? detectedOS, string? fonts, ref List<string>? flags)
    {
        if (string.IsNullOrEmpty(fonts))
            return WeightFontPlatform; // no data → assume consistent

        var isMac = IsMacPlatform(platform, detectedOS);
        var isWindows = IsWindowsPlatform(platform, detectedOS);
        var isLinux = IsLinuxPlatform(platform, detectedOS);

        if (!isMac && !isWindows && !isLinux)
            return WeightFontPlatform; // unknown platform → can't check

        var fontSpan = fonts.AsSpan();
        int wrongCount = 0, totalChecked = 0;

        foreach (var range in fontSpan.Split(','))
        {
            var fontName = fontSpan[range].Trim().ToString();
            if (fontName.Length == 0) continue;

            totalChecked++;

            // Check if this font belongs to a DIFFERENT platform
            if (isWindows && CulturalReference.MacOSMarkerFonts.Contains(fontName))
                wrongCount++;
            else if (isWindows && CulturalReference.LinuxMarkerFonts.Contains(fontName))
                wrongCount++;
            else if (isMac && CulturalReference.WindowsMarkerFonts.Contains(fontName))
                wrongCount++;
            else if (isMac && CulturalReference.LinuxMarkerFonts.Contains(fontName))
                wrongCount++;
            else if (isLinux && CulturalReference.WindowsMarkerFonts.Contains(fontName))
                wrongCount++;
            else if (isLinux && CulturalReference.MacOSMarkerFonts.Contains(fontName))
                wrongCount++;
        }

        if (wrongCount > 0 && totalChecked > 0)
        {
            AddFlag(ref flags, "font-platform-mismatch");
            // Proportional deduction: 1/10 wrong = small penalty, 5/10 = large
            var ratio = (double)wrongCount / totalChecked;
            return (int)(WeightFontPlatform * (1.0 - Math.Min(ratio * 2.0, 1.0)));
        }

        return WeightFontPlatform;
    }

    /// <summary>
    /// Checks if regional fonts (CJK, Arabic, Cyrillic) are inconsistent with geo country.
    /// CJK fonts on a US IP without CJK language = suspicious.
    /// </summary>
    private static int CheckFontRegional(
        string? country, string? fonts, ref List<string>? flags)
    {
        if (string.IsNullOrEmpty(fonts) || string.IsNullOrEmpty(country))
            return WeightFontRegional;

        bool hasCjk = false, hasArabic = false, hasCyrillic = false;

        var fontSpan = fonts.AsSpan();
        foreach (var range in fontSpan.Split(','))
        {
            var fontName = fontSpan[range].Trim().ToString();
            if (fontName.Length == 0) continue;

            if (!hasCjk && CulturalReference.CJKFonts.Contains(fontName))
                hasCjk = true;
            if (!hasArabic && CulturalReference.ArabicFonts.Contains(fontName))
                hasArabic = true;
            if (!hasCyrillic && CulturalReference.CyrillicFonts.Contains(fontName))
                hasCyrillic = true;
        }

        // CJK fonts but country is not CJK
        if (hasCjk && !CulturalReference.CJKCountries.Contains(country))
        {
            AddFlag(ref flags, "font-region-cjk");
            return 0;
        }

        // Arabic fonts but country is not Arabic-speaking
        if (hasArabic && !CulturalReference.ArabicCountries.Contains(country))
        {
            AddFlag(ref flags, "font-region-arabic");
            return 0;
        }

        // Cyrillic fonts but country is not Cyrillic-using
        if (hasCyrillic && !CulturalReference.CyrillicCountries.Contains(country))
        {
            AddFlag(ref flags, "font-region-cyrillic");
            return 0;
        }

        return WeightFontRegional;
    }

    /// <summary>
    /// Checks if the browser's primary language is consistent with the geo country.
    /// </summary>
    private static int CheckLanguage(
        string? country, string? lang, ref List<string>? flags)
    {
        var primary = CulturalReference.ExtractPrimaryLanguage(lang);
        if (CulturalReference.IsLanguageConsistentWithCountry(primary, country))
            return WeightLanguage;

        AddFlag(ref flags, "lang-mismatch");
        return 0;
    }

    /// <summary>
    /// Checks if the number format (comma vs period decimal separator) matches
    /// the expected convention for the geo country.
    /// </summary>
    private static int CheckNumberFormat(
        string? country, string? numberFormat, ref List<string>? flags)
    {
        if (string.IsNullOrEmpty(numberFormat) || string.IsNullOrEmpty(country))
            return WeightNumberFormat;

        // Detect decimal separator: if the formatted number contains a comma
        // AFTER the last digit group, it's a comma-decimal locale
        var usesCommaDecimal = DetectCommaDecimal(numberFormat);
        var expectedComma = CulturalReference.CommaDecimalCountries.Contains(country);

        if (usesCommaDecimal != expectedComma)
        {
            AddFlag(ref flags, "number-format-mismatch");
            return 0;
        }

        return WeightNumberFormat;
    }

    /// <summary>
    /// Checks if a non-Gregorian calendar system is consistent with the geo country.
    /// </summary>
    private static int CheckCalendar(
        string? country, string? tzLocale, ref List<string>? flags)
    {
        if (string.IsNullOrEmpty(tzLocale) || string.IsNullOrEmpty(country))
            return WeightCalendar;

        // Extract calendar from tzLocale (format: "en-US-u-ca-gregory" or just "buddhist")
        var calendar = ExtractCalendar(tzLocale);
        if (string.IsNullOrEmpty(calendar) ||
            calendar.Equals("gregory", StringComparison.OrdinalIgnoreCase) ||
            calendar.Equals("gregorian", StringComparison.OrdinalIgnoreCase))
            return WeightCalendar; // Gregorian is universal

        if (CulturalReference.CalendarCountries.TryGetValue(calendar, out var expectedCountries))
        {
            if (!expectedCountries.Contains(country))
            {
                AddFlag(ref flags, "calendar-mismatch");
                return 0;
            }
        }

        return WeightCalendar;
    }

    /// <summary>
    /// Checks if speech synthesis voice availability is consistent with the reported platform.
    /// Windows typically has ~3-5 voices (Zira, David, Mark), macOS has ~20+.
    /// Zero voices on a modern desktop browser = headless/automation.
    /// </summary>
    private static int CheckVoice(
        string? platform, string? detectedOS, string? voices, ref List<string>? flags)
    {
        if (string.IsNullOrEmpty(voices))
            return WeightVoice; // no data → assume consistent

        // Try to parse voice count
        if (!int.TryParse(voices, out var voiceCount))
            return WeightVoice;

        var isDesktop = IsWindowsPlatform(platform, detectedOS) ||
                        IsMacPlatform(platform, detectedOS) ||
                        IsLinuxPlatform(platform, detectedOS);

        // Zero voices on desktop = headless/bot environment
        if (isDesktop && voiceCount == 0)
        {
            AddFlag(ref flags, "voice-zero");
            return 0;
        }

        return WeightVoice;
    }

    // ════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════════════

    private static bool IsMacPlatform(string? platform, string? os)
        => (platform is not null && platform.Contains("Mac", StringComparison.OrdinalIgnoreCase))
        || (os is not null && os.Contains("Mac", StringComparison.OrdinalIgnoreCase));

    private static bool IsWindowsPlatform(string? platform, string? os)
        => (platform is not null && platform.Contains("Win", StringComparison.OrdinalIgnoreCase))
        || (os is not null && os.Contains("Windows", StringComparison.OrdinalIgnoreCase));

    private static bool IsLinuxPlatform(string? platform, string? os)
        => (platform is not null && platform.Contains("Linux", StringComparison.OrdinalIgnoreCase))
        || (os is not null && (os.Contains("Linux", StringComparison.OrdinalIgnoreCase)
                            || os.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Detects whether a formatted number uses comma as the decimal separator.
    /// Examines the last non-digit separator character in the string.
    /// "1.234,56" → true (comma decimal). "1,234.56" → false (period decimal).
    /// </summary>
    private static bool DetectCommaDecimal(string formatted)
    {
        var lastComma = formatted.LastIndexOf(',');
        var lastPeriod = formatted.LastIndexOf('.');

        if (lastComma < 0 && lastPeriod < 0)
            return false; // no separator

        // The LAST separator is the decimal separator
        // "1.234,56" → last = comma = comma-decimal
        // "1,234.56" → last = period = period-decimal
        return lastComma > lastPeriod;
    }

    /// <summary>
    /// Extracts the calendar system from a tzLocale string.
    /// Supports formats: "en-US-u-ca-buddhist", "buddhist", "gregory".
    /// </summary>
    private static string? ExtractCalendar(string tzLocale)
    {
        // Look for Unicode calendar extension: "-u-ca-{calendar}"
        const string marker = "-u-ca-";
        var idx = tzLocale.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var start = idx + marker.Length;
            var end = tzLocale.IndexOf('-', start);
            return end > start ? tzLocale[start..end] : tzLocale[start..];
        }

        // If no BCP47 structure, treat the whole string as a calendar name
        // (handles case where PiXL Script extracted just the calendar)
        if (!tzLocale.Contains('-') && !tzLocale.Contains('/'))
            return tzLocale;

        return null;
    }

    private static void AddFlag(ref List<string>? flags, string flag)
    {
        flags ??= [];
        flags.Add(flag);
    }
}
