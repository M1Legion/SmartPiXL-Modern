// ─────────────────────────────────────────────────────────────────────────────
// Tests for SmartPiXL.SqlClr CLR functions
// Phase 7 — GetSubnet24, RegexFunctions, FeatureBitmaps, MurmurHash3, FuzzyMatch
// ─────────────────────────────────────────────────────────────────────────────

using System.Data.SqlTypes;
using SmartPiXL.SqlClr.Functions;
using Xunit;

namespace SmartPiXL.Tests;

// ════════════════════════════════════════════════════════════════════════
// GetSubnet24 Tests
// ════════════════════════════════════════════════════════════════════════

public sealed class GetSubnet24Tests
{
    [Theory]
    [InlineData("192.168.1.100", "192.168.1.0/24")]
    [InlineData("10.0.0.1", "10.0.0.0/24")]
    [InlineData("172.16.255.254", "172.16.255.0/24")]
    [InlineData("8.8.8.8", "8.8.8.0/24")]
    [InlineData("1.2.3.4", "1.2.3.0/24")]
    public void ValidIPv4_ReturnsSubnet(string ip, string expected)
    {
        var result = GetSubnet24.Execute(new SqlString(ip));
        Assert.False(result.IsNull);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData("not-an-ip")]           // No dots
    [InlineData("192.168.1")]           // Only 2 dots
    [InlineData("1.2.3.4.5")]           // 4 dots
    public void InvalidIP_ReturnsNull(string ip)
    {
        var result = GetSubnet24.Execute(new SqlString(ip));
        Assert.True(result.IsNull);
    }

    [Fact]
    public void NullInput_ReturnsNull()
    {
        var result = GetSubnet24.Execute(SqlString.Null);
        Assert.True(result.IsNull);
    }
}

// ════════════════════════════════════════════════════════════════════════
// RegexFunctions Tests
// ════════════════════════════════════════════════════════════════════════

public sealed class RegexFunctionsTests
{
    // ── RegexExtract ──────────────────────────────────────────────────
    [Theory]
    [InlineData("https://example.com/path", "://([^/]+)", 1, "example.com")]
    [InlineData("user@domain.co.uk", "@(.+)$", 1, "domain.co.uk")]
    [InlineData("Chrome/130.0.6723.92", @"Chrome/(\d+)\.", 1, "130")]
    [InlineData("v5.2.1-beta", @"v(\d+)\.(\d+)\.(\d+)", 2, "2")]
    public void RegexExtract_ValidInput_ReturnsGroup(string input, string pattern, int group, string expected)
    {
        var result = RegexFunctions.RegexExtract(new SqlString(input), new SqlString(pattern), new SqlInt32(group));
        Assert.False(result.IsNull);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void RegexExtract_NoMatch_ReturnsNull()
    {
        var result = RegexFunctions.RegexExtract(
            new SqlString("hello"), new SqlString(@"\d+"), new SqlInt32(0));
        Assert.True(result.IsNull);
    }

    [Fact]
    public void RegexExtract_GroupOutOfRange_ReturnsNull()
    {
        var result = RegexFunctions.RegexExtract(
            new SqlString("abc"), new SqlString("(abc)"), new SqlInt32(5));
        Assert.True(result.IsNull);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void RegexExtract_NullInput_ReturnsNull(bool nullInput, bool nullPattern, bool nullGroup)
    {
        var result = RegexFunctions.RegexExtract(
            nullInput ? SqlString.Null : new SqlString("test"),
            nullPattern ? SqlString.Null : new SqlString("test"),
            nullGroup ? SqlInt32.Null : new SqlInt32(0));
        Assert.True(result.IsNull);
    }

    [Fact]
    public void RegexExtract_InvalidRegex_ReturnsNull()
    {
        var result = RegexFunctions.RegexExtract(
            new SqlString("test"), new SqlString("[invalid"), new SqlInt32(0));
        Assert.True(result.IsNull);
    }

    // ── RegexMatch ────────────────────────────────────────────────────
    [Theory]
    [InlineData("bot@crawl.com", @"^[^@]+@[^@]+\.[^@]+$", true)]
    [InlineData("192.168.1.100", @"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$", true)]
    [InlineData("hello world", @"\d+", false)]
    public void RegexMatch_ReturnsExpected(string input, string pattern, bool expected)
    {
        var result = RegexFunctions.RegexMatch(new SqlString(input), new SqlString(pattern));
        Assert.False(result.IsNull);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void RegexMatch_NullInput_ReturnsNull()
    {
        var result = RegexFunctions.RegexMatch(SqlString.Null, new SqlString("test"));
        Assert.True(result.IsNull);
    }
}

// ════════════════════════════════════════════════════════════════════════
// FeatureBitmaps Tests
// ════════════════════════════════════════════════════════════════════════

public sealed class FeatureBitmapTests
{
    [Fact]
    public void FeatureBitmap_AllTrue_AllBitsSet()
    {
        var t = SqlBoolean.True;
        var result = FeatureBitmaps.FeatureBitmap(
            t, t, t, t, t, t, t, t, t, t, t, t, t, t, t, t, t);
        // 17 bits set → 2^17 - 1 = 131071
        Assert.Equal(0x1FFFF, result.Value);
    }

    [Fact]
    public void FeatureBitmap_AllFalse_Zero()
    {
        var f = SqlBoolean.False;
        var result = FeatureBitmaps.FeatureBitmap(
            f, f, f, f, f, f, f, f, f, f, f, f, f, f, f, f, f);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public void FeatureBitmap_SingleBit_CorrectPosition()
    {
        var f = SqlBoolean.False;
        // Set only bit 5 (webGL)
        var result = FeatureBitmaps.FeatureBitmap(
            f, f, f, f, f, SqlBoolean.True, f, f, f, f, f, f, f, f, f, f, f);
        Assert.Equal(1 << 5, result.Value);
    }

    [Fact]
    public void FeatureBitmap_NullsAreZero()
    {
        var n = SqlBoolean.Null;
        var result = FeatureBitmaps.FeatureBitmap(
            n, n, n, n, n, n, n, n, n, n, n, n, n, n, n, n, n);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public void AccessibilityBitmap_AllTrue_AllBitsSet()
    {
        var t = SqlBoolean.True;
        var result = FeatureBitmaps.AccessibilityBitmap(t, t, t, t, t, t, t, t, t);
        // 9 bits → 2^9 - 1 = 511
        Assert.Equal(0x1FF, result.Value);
    }

    [Fact]
    public void BotBitmap_WebdriverOnly_Bit0()
    {
        var f = SqlBoolean.False;
        var result = FeatureBitmaps.BotBitmap(
            SqlBoolean.True, f, f, f, f, f, f, f, f, f, f, f, f, f, f, f, f, f, f, f);
        Assert.Equal(1, result.Value);
    }

    [Fact]
    public void BotBitmap_AllTrue_AllBitsSet()
    {
        var t = SqlBoolean.True;
        var result = FeatureBitmaps.BotBitmap(
            t, t, t, t, t, t, t, t, t, t, t, t, t, t, t, t, t, t, t, t);
        // 20 bits → 2^20 - 1 = 1048575
        Assert.Equal(0xFFFFF, result.Value);
    }

    [Fact]
    public void EvasionBitmap_AllTrue_AllBitsSet()
    {
        var t = SqlBoolean.True;
        var result = FeatureBitmaps.EvasionBitmap(t, t, t, t, t, t, t, t);
        // 8 bits → 255
        Assert.Equal(0xFF, result.Value);
    }

    [Fact]
    public void EvasionBitmap_CanvasAndWebGL_Bits01()
    {
        var f = SqlBoolean.False;
        var result = FeatureBitmaps.EvasionBitmap(
            SqlBoolean.True, SqlBoolean.True, f, f, f, f, f, f);
        Assert.Equal(3, result.Value); // bits 0 + 1
    }
}

// ════════════════════════════════════════════════════════════════════════
// MurmurHash3 Tests
// ════════════════════════════════════════════════════════════════════════

public sealed class MurmurHash3Tests
{
    [Fact]
    public void SameInput_SameHash()
    {
        var hash1 = MurmurHash3Function.Execute(new SqlString("test-fingerprint-abc123"));
        var hash2 = MurmurHash3Function.Execute(new SqlString("test-fingerprint-abc123"));
        Assert.False(hash1.IsNull);
        Assert.Equal(hash1.Value, hash2.Value);
    }

    [Fact]
    public void DifferentInput_DifferentHash()
    {
        var hash1 = MurmurHash3Function.Execute(new SqlString("string-a"));
        var hash2 = MurmurHash3Function.Execute(new SqlString("string-b"));
        Assert.NotEqual(hash1.Value, hash2.Value);
    }

    [Fact]
    public void ReturnsExactly16Bytes()
    {
        var hash = MurmurHash3Function.Execute(new SqlString("hello"));
        Assert.False(hash.IsNull);
        Assert.Equal(16, hash.Value.Length);
    }

    [Fact]
    public void NullInput_ReturnsNull()
    {
        var result = MurmurHash3Function.Execute(SqlString.Null);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void EmptyString_ProducesHash()
    {
        var hash = MurmurHash3Function.Execute(new SqlString(""));
        Assert.False(hash.IsNull);
        Assert.Equal(16, hash.Value.Length);
    }

    [Fact]
    public void LongInput_ProducesHash()
    {
        var longStr = new string('x', 10000);
        var hash = MurmurHash3Function.Execute(new SqlString(longStr));
        Assert.False(hash.IsNull);
        Assert.Equal(16, hash.Value.Length);
    }

    [Fact]
    public void SimilarInputs_DifferentHashes()
    {
        // Near-miss collision test (avalanche property)
        var h1 = MurmurHash3Function.Execute(new SqlString("AppleWebKit/537.36"));
        var h2 = MurmurHash3Function.Execute(new SqlString("AppleWebKit/537.37"));
        Assert.NotEqual(h1.Value, h2.Value);
    }
}

// ════════════════════════════════════════════════════════════════════════
// FuzzyMatch Tests
// ════════════════════════════════════════════════════════════════════════

public sealed class FuzzyMatchTests
{
    // ── Jaro-Winkler ──────────────────────────────────────────────────
    [Fact]
    public void JaroWinkler_IdenticalStrings_Returns1()
    {
        var result = FuzzyMatch.JaroWinkler(new SqlString("hello"), new SqlString("hello"));
        Assert.Equal(1.0, result.Value, 5);
    }

    [Fact]
    public void JaroWinkler_CompletelyDifferent_Low()
    {
        var result = FuzzyMatch.JaroWinkler(new SqlString("abc"), new SqlString("xyz"));
        Assert.True(result.Value < 0.5);
    }

    [Fact]
    public void JaroWinkler_SimilarUA_HighScore()
    {
        var result = FuzzyMatch.JaroWinkler(
            new SqlString("AppleWebKit/537.36"),
            new SqlString("AppleWebKit/537.37"));
        Assert.True(result.Value > 0.95, $"Expected > 0.95, got {result.Value}");
    }

    [Fact]
    public void JaroWinkler_NullInput_ReturnsNull()
    {
        var result = FuzzyMatch.JaroWinkler(SqlString.Null, new SqlString("test"));
        Assert.True(result.IsNull);
    }

    [Fact]
    public void JaroWinkler_EmptyBoth_Returns1()
    {
        var result = FuzzyMatch.JaroWinkler(new SqlString(""), new SqlString(""));
        Assert.Equal(1.0, result.Value, 5);
    }

    [Fact]
    public void JaroWinkler_OneEmpty_Returns0()
    {
        var result = FuzzyMatch.JaroWinkler(new SqlString("hello"), new SqlString(""));
        Assert.Equal(0.0, result.Value, 5);
    }

    // ── Levenshtein ───────────────────────────────────────────────────
    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("hello", "hello", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("a", "b", 1)]
    public void Levenshtein_ReturnsExpectedDistance(string a, string b, int expected)
    {
        var result = FuzzyMatch.LevenshteinDistance(new SqlString(a), new SqlString(b));
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void Levenshtein_SimilarUA_SmallDistance()
    {
        var result = FuzzyMatch.LevenshteinDistance(
            new SqlString("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"),
            new SqlString("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.37"));
        Assert.Equal(1, result.Value); // Just "6" → "7"
    }

    [Fact]
    public void Levenshtein_NullInput_ReturnsNull()
    {
        var result = FuzzyMatch.LevenshteinDistance(SqlString.Null, new SqlString("test"));
        Assert.True(result.IsNull);
    }
}
