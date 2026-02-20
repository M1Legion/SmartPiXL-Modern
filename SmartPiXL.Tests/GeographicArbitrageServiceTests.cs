// ─────────────────────────────────────────────────────────────────────────────
// Tests for GeographicArbitrageService + CulturalReference
// Phase 6 — Tier 3 Enrichments
// ─────────────────────────────────────────────────────────────────────────────

using SmartPiXL.Forge.Services.Enrichments;
using Xunit;

namespace TrackingPixel.Tests;

public sealed class GeographicArbitrageServiceTests
{
    private readonly GeographicArbitrageService _sut = new();

    // ── Full consistency: US visitor with matching signals ─────────
    [Fact]
    public void FullyConsistent_USVisitor_Score100()
    {
        var result = _sut.Analyze(
            country: "US", platform: "Win32",
            fonts: "Segoe UI,Consolas,Calibri",
            lang: "en-US", timezone: "America/New_York",
            numberFormat: "1,234.56", tzLocale: "en-US-u-ca-gregory",
            voices: "3", detectedOS: "Windows");

        Assert.Equal(100, result.CulturalScore);
        Assert.Null(result.Flags);
        Assert.True(result.TimezoneMatch);
    }

    // ── Timezone mismatch: US IP but Asia/Tokyo timezone ──────────
    [Fact]
    public void TimezoneMismatch_USIpTokyoTZ_Flagged()
    {
        var result = _sut.Analyze(
            country: "US", platform: "Win32",
            fonts: "Segoe UI,Consolas", lang: "en-US",
            timezone: "Asia/Tokyo",
            numberFormat: "1,234.56", tzLocale: null,
            voices: "3", detectedOS: "Windows");

        Assert.True(result.CulturalScore < 100);
        Assert.False(result.TimezoneMatch);
        Assert.Contains("tz-mismatch", result.Flags!);
    }

    // ── Language mismatch: Japan IP but Vietnamese language ────────
    [Fact]
    public void LanguageMismatch_JpIpViLang_Flagged()
    {
        var result = _sut.Analyze(
            country: "JP", platform: "Win32",
            fonts: null, lang: "vi",
            timezone: "Asia/Tokyo",
            numberFormat: null, tzLocale: null,
            voices: null, detectedOS: "Windows");

        Assert.True(result.CulturalScore < 100);
        Assert.Contains("lang-mismatch", result.Flags!);
    }

    // ── Font-platform mismatch: Windows platform but macOS fonts ──
    [Fact]
    public void FontPlatformMismatch_WindowsWithMacFonts_Flagged()
    {
        var result = _sut.Analyze(
            country: "US", platform: "Win32",
            fonts: "Helvetica Neue,Lucida Grande,Apple Color Emoji",
            lang: "en-US", timezone: "America/Chicago",
            numberFormat: "1,234.56", tzLocale: null,
            voices: "3", detectedOS: "Windows");

        Assert.True(result.CulturalScore < 100);
        Assert.Contains("font-platform-mismatch", result.Flags!);
    }

    // ── Font-regional mismatch: CJK fonts on US IP ────────────────
    [Fact]
    public void FontRegional_CJKOnUSIp_Flagged()
    {
        var result = _sut.Analyze(
            country: "US", platform: "Win32",
            fonts: "MS Gothic,Meiryo,Segoe UI",
            lang: "ja", // Japanese language
            timezone: "America/New_York",
            numberFormat: null, tzLocale: null,
            voices: null, detectedOS: "Windows");

        Assert.Contains("font-region-cjk", result.Flags!);
    }

    // ── CJK fonts consistent on JP IP ─────────────────────────────
    [Fact]
    public void FontRegional_CJKOnJpIp_NotFlagged()
    {
        var result = _sut.Analyze(
            country: "JP", platform: "Win32",
            fonts: "MS Gothic,Meiryo,Segoe UI",
            lang: "ja", timezone: "Asia/Tokyo",
            numberFormat: null, tzLocale: null,
            voices: null, detectedOS: "Windows");

        Assert.DoesNotContain("font-region-cjk", result.Flags ?? string.Empty);
    }

    // ── Number format mismatch: US IP but comma decimal ───────────
    [Fact]
    public void NumberFormatMismatch_USWithCommaDecimal_Flagged()
    {
        var result = _sut.Analyze(
            country: "US", platform: "Win32",
            fonts: "Segoe UI", lang: "en-US",
            timezone: "America/New_York",
            numberFormat: "1.234,56", // comma decimal = European
            tzLocale: null, voices: null, detectedOS: "Windows");

        Assert.Contains("number-format-mismatch", result.Flags!);
    }

    // ── Number format consistent: Germany IP with comma decimal ────
    [Fact]
    public void NumberFormatConsistent_GermanWithCommaDecimal_Ok()
    {
        var result = _sut.Analyze(
            country: "DE", platform: "Win32",
            fonts: "Segoe UI", lang: "de",
            timezone: "Europe/Berlin",
            numberFormat: "1.234,56",
            tzLocale: null, voices: null, detectedOS: "Windows");

        Assert.DoesNotContain("number-format-mismatch", result.Flags ?? string.Empty);
    }

    // ── Calendar mismatch: Persian calendar on US IP ──────────────
    [Fact]
    public void CalendarMismatch_PersianOnUSIp_Flagged()
    {
        var result = _sut.Analyze(
            country: "US", platform: "Win32",
            fonts: null, lang: "en-US",
            timezone: "America/New_York",
            numberFormat: null,
            tzLocale: "en-US-u-ca-persian",
            voices: null, detectedOS: "Windows");

        Assert.Contains("calendar-mismatch", result.Flags!);
    }

    // ── Buddhist calendar consistent with Thailand ────────────────
    [Fact]
    public void CalendarConsistent_BuddhistInThailand_Ok()
    {
        var result = _sut.Analyze(
            country: "TH", platform: "Win32",
            fonts: null, lang: "th",
            timezone: "Asia/Bangkok",
            numberFormat: null,
            tzLocale: "th-TH-u-ca-buddhist",
            voices: null, detectedOS: "Windows");

        Assert.DoesNotContain("calendar-mismatch", result.Flags ?? string.Empty);
    }

    // ── Zero voices on desktop = suspicious ───────────────────────
    [Fact]
    public void ZeroVoices_Desktop_Flagged()
    {
        var result = _sut.Analyze(
            country: "US", platform: "Win32",
            fonts: "Segoe UI", lang: "en-US",
            timezone: "America/New_York",
            numberFormat: "1,234.56", tzLocale: null,
            voices: "0", detectedOS: "Windows");

        Assert.Contains("voice-zero", result.Flags!);
    }

    // ── No geo country = score 100, no checks ─────────────────────
    [Fact]
    public void NoCountry_Returns100()
    {
        var result = _sut.Analyze(
            country: null, platform: "Win32",
            fonts: null, lang: null, timezone: null,
            numberFormat: null, tzLocale: null,
            voices: null, detectedOS: null);

        Assert.Equal(100, result.CulturalScore);
        Assert.True(result.TimezoneMatch);
    }

    // ── Maximum inconsistency: everything wrong ───────────────────
    [Fact]
    public void MaximalInconsistency_LowScore()
    {
        var result = _sut.Analyze(
            country: "US", platform: "Win32",
            fonts: "Helvetica Neue,Lucida Grande,MS Gothic,Meiryo", // macOS fonts + CJK
            lang: "fa",                    // Persian on US IP
            timezone: "Asia/Tehran",       // Iran timezone on US IP
            numberFormat: "1.234,56",      // comma decimal on US IP
            tzLocale: "fa-IR-u-ca-persian", // Persian calendar
            voices: "0",                    // zero voices
            detectedOS: "Windows");

        // Should have many flags and low score
        Assert.True(result.CulturalScore <= 25, $"Score {result.CulturalScore} should be <= 25");
        Assert.NotNull(result.Flags);
    }

    // ── English language is always consistent (global lingua franca) ──
    [Fact]
    public void EnglishLanguage_AlwaysConsistent()
    {
        var result = _sut.Analyze(
            country: "JP", platform: "Win32",
            fonts: null, lang: "en",
            timezone: "Asia/Tokyo",
            numberFormat: null, tzLocale: null,
            voices: null, detectedOS: "Windows");

        Assert.DoesNotContain("lang-mismatch", result.Flags ?? string.Empty);
    }
}

// ── CulturalReference helper tests ────────────────────────────────
public sealed class CulturalReferenceTests
{
    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("ja", "ja")]
    [InlineData("zh-TW", "zh")]
    [InlineData("pt-BR", "pt")]
    public void ExtractPrimaryLanguage_Correct(string input, string expected)
    {
        Assert.Equal(expected, CulturalReference.ExtractPrimaryLanguage(input));
    }

    [Fact]
    public void ExtractPrimaryLanguage_Null_ReturnsNull()
    {
        Assert.Null(CulturalReference.ExtractPrimaryLanguage(null));
    }

    [Fact]
    public void TimezoneConsistent_USWithAmericaNewYork()
    {
        Assert.True(CulturalReference.IsTimezoneConsistentWithCountry("America/New_York", "US"));
    }

    [Fact]
    public void TimezoneInconsistent_USWithAsiaTokyo()
    {
        Assert.False(CulturalReference.IsTimezoneConsistentWithCountry("Asia/Tokyo", "US"));
    }

    [Fact]
    public void LanguageConsistent_JapaneseInJapan()
    {
        Assert.True(CulturalReference.IsLanguageConsistentWithCountry("ja", "JP"));
    }

    [Fact]
    public void LanguageInconsistent_VietnameseInJapan()
    {
        Assert.False(CulturalReference.IsLanguageConsistentWithCountry("vi", "JP"));
    }

    [Fact]
    public void LanguageConsistent_EnglishAnywhere()
    {
        Assert.True(CulturalReference.IsLanguageConsistentWithCountry("en", "JP"));
        Assert.True(CulturalReference.IsLanguageConsistentWithCountry("en", "BR"));
    }
}
