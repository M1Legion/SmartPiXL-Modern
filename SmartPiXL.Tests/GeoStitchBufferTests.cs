using Xunit;
using SmartPiXL.Forge.Services.Enrichments;

namespace SmartPiXL.Tests;

// ════════════════════════════════════════════════════════════════════════════
// GeoStitchBufferTests
//
// Covers the pure MergeQueryStrings helper — the critical merge logic that
// determines how a geo-followup's _usr_* params are stitched onto the main
// beacon's QueryString. End-to-end stitching behaviour (buffering, timeouts,
// DI wiring, channel emission) is exercised by the Playwright harness at
// tools/test-pixl-tag.js, not here.
// ════════════════════════════════════════════════════════════════════════════

public class GeoStitchBufferTests
{
    [Fact]
    public void MergeQueryStrings_EmptyFollowup_ReturnsMainUnchanged()
    {
        var main = "sw=1920&sh=1080&deviceHash=abc";
        var merged = GeoStitchBuffer.MergeQueryStrings(main, string.Empty);
        Assert.Equal(main, merged);
    }

    [Fact]
    public void MergeQueryStrings_AppendsUsrFields()
    {
        var main = "sw=1920&sh=1080&deviceHash=abc&_hit_id=11111111-1111-1111-1111-111111111111";
        var followup = "_hit_id=11111111-1111-1111-1111-111111111111&_geo_followup=1&_usr_lat=37.7749&_usr_lon=-122.4194&_usr_geo_status=granted";
        var merged = GeoStitchBuffer.MergeQueryStrings(main, followup);

        Assert.Contains("_usr_lat=37.7749", merged);
        Assert.Contains("_usr_lon=-122.4194", merged);
        Assert.Contains("_usr_geo_status=granted", merged);

        // The main's original fields are preserved.
        Assert.Contains("sw=1920", merged);
        Assert.Contains("deviceHash=abc", merged);

        // Redundant fields on the followup are stripped — they already exist
        // on the main and re-appending them would double-count downstream.
        Assert.DoesNotContain("_geo_followup=1", merged);
    }

    [Fact]
    public void MergeQueryStrings_StripsFollowupRedundantIdentifiers()
    {
        // The JS tag attaches ua/lang/tz/sw/sh/pd/deviceHash to the followup
        // as fallback identifiers. Once we've matched on HitId, those are
        // duplicates of the main's already-authoritative values.
        var main = "sw=1920&sh=1080&deviceHash=main_hash&_hit_id=22222222-2222-2222-2222-222222222222";
        var followup = "_hit_id=22222222-2222-2222-2222-222222222222&_geo_followup=1&ua=Mozilla&lang=en&tz=UTC&sw=9999&sh=9999&pd=1&deviceHash=followup_hash&_usr_lat=40.0";
        var merged = GeoStitchBuffer.MergeQueryStrings(main, followup);

        Assert.Contains("_usr_lat=40.0", merged);

        // Main's original sw=1920 stays; followup's sw=9999 is dropped.
        Assert.Contains("sw=1920", merged);
        Assert.DoesNotContain("sw=9999", merged);
        Assert.DoesNotContain("deviceHash=followup_hash", merged);
        Assert.Contains("deviceHash=main_hash", merged);
    }

    [Fact]
    public void MergeQueryStrings_KeepsGeoCaptureMs()
    {
        var main = "sw=1920";
        var followup = "_geo_followup=1&_geo_captureMs=342&_usr_lat=40.0";
        var merged = GeoStitchBuffer.MergeQueryStrings(main, followup);

        Assert.Contains("_geo_captureMs=342", merged);
    }

    [Fact]
    public void MergeQueryStrings_KeepsProtocolVersion()
    {
        // _pv is the future protocol-version field on every beacon. We keep
        // it through merges so downstream parser can dispatch correctly.
        var main = "sw=1920";
        var followup = "_geo_followup=1&_pv=2&_usr_lat=40.0";
        var merged = GeoStitchBuffer.MergeQueryStrings(main, followup);

        Assert.Contains("_pv=2", merged);
    }

    [Fact]
    public void MergeQueryStrings_HandlesMainWithTrailingAmpersand()
    {
        var main = "sw=1920&";
        var followup = "_usr_lat=40.0";
        var merged = GeoStitchBuffer.MergeQueryStrings(main, followup);

        Assert.Equal("sw=1920&_usr_lat=40.0", merged);
    }

    [Fact]
    public void MergeQueryStrings_EmptyMain_ReturnsOnlyKeptFollowupFields()
    {
        var merged = GeoStitchBuffer.MergeQueryStrings(string.Empty, "_usr_lat=40.0&_geo_followup=1");
        Assert.Equal("_usr_lat=40.0", merged);
    }
}
