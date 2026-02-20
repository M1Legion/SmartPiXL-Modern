using FluentAssertions;
using Moq;
using SmartPiXL.Forge.Services.Enrichments;
using SmartPiXL.Services;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for SessionStitchingService — in-memory session tracking keyed by
/// fingerprint hash. Validates session creation, extension, page counting,
/// and null/empty input handling.
/// </summary>
public sealed class SessionStitchingServiceTests
{
    private readonly SessionStitchingService _service;

    public SessionStitchingServiceTests()
    {
        var logger = new Mock<ITrackingLogger>();
        _service = new SessionStitchingService(logger.Object);
    }

    // ========================================================================
    // FIRST HIT — New session created
    // ========================================================================

    [Fact]
    public void RecordHit_should_createNewSession_when_firstHit()
    {
        var result = _service.RecordHit("fp-abc", "/home");

        result.SessionId.Should().NotBeNullOrEmpty();
        result.HitNumber.Should().Be(1);
        result.DurationSec.Should().Be(0);
        result.PageCount.Should().Be(1);
    }

    // ========================================================================
    // SECOND HIT — Session extended
    // ========================================================================

    [Fact]
    public void RecordHit_should_extendSession_when_sameFingerprint()
    {
        var first = _service.RecordHit("fp-abc", "/home");
        var second = _service.RecordHit("fp-abc", "/about");

        second.SessionId.Should().Be(first.SessionId);
        second.HitNumber.Should().Be(2);
        second.PageCount.Should().Be(2);
    }

    // ========================================================================
    // SAME PAGE — Not double counted
    // ========================================================================

    [Fact]
    public void RecordHit_should_notDoubleCountPages_when_samePageRevisited()
    {
        _service.RecordHit("fp-abc", "/home");
        _service.RecordHit("fp-abc", "/about");
        var result = _service.RecordHit("fp-abc", "/home"); // revisit /home

        result.HitNumber.Should().Be(3);
        result.PageCount.Should().Be(2); // still 2 distinct pages
    }

    // ========================================================================
    // MULTIPLE PAGES — All tracked
    // ========================================================================

    [Fact]
    public void RecordHit_should_trackMultiplePages()
    {
        _service.RecordHit("fp-abc", "/home");
        _service.RecordHit("fp-abc", "/about");
        _service.RecordHit("fp-abc", "/contact");
        _service.RecordHit("fp-abc", "/pricing");
        var result = _service.RecordHit("fp-abc", "/blog");

        result.HitNumber.Should().Be(5);
        result.PageCount.Should().Be(5);
    }

    // ========================================================================
    // DIFFERENT FINGERPRINTS — Independent sessions
    // ========================================================================

    [Fact]
    public void RecordHit_should_createSeparateSessions_when_differentFingerprints()
    {
        var fp1 = _service.RecordHit("fp-abc", "/home");
        var fp2 = _service.RecordHit("fp-xyz", "/home");

        fp1.SessionId.Should().NotBe(fp2.SessionId);
        fp1.HitNumber.Should().Be(1);
        fp2.HitNumber.Should().Be(1);
    }

    // ========================================================================
    // NULL FINGERPRINT — Returns a new session each time (no tracking)
    // ========================================================================

    [Fact]
    public void RecordHit_should_returnNewSession_when_nullFingerprint()
    {
        var r1 = _service.RecordHit(null, "/page");
        var r2 = _service.RecordHit(null, "/page");

        r1.SessionId.Should().NotBe(r2.SessionId); // different GUIDs
        r1.HitNumber.Should().Be(1);
        r2.HitNumber.Should().Be(1);
    }

    [Fact]
    public void RecordHit_should_returnNewSession_when_emptyFingerprint()
    {
        var r1 = _service.RecordHit("", "/page");
        var r2 = _service.RecordHit("", "/page");

        r1.SessionId.Should().NotBe(r2.SessionId);
    }

    // ========================================================================
    // NULL PAGE — Still tracked (hit count increments, page count stays)
    // ========================================================================

    [Fact]
    public void RecordHit_should_handleNullPage()
    {
        _service.RecordHit("fp-abc", null);
        var result = _service.RecordHit("fp-abc", null);

        result.HitNumber.Should().Be(2);
        result.PageCount.Should().Be(0); // null pages not added to set
    }

    [Fact]
    public void RecordHit_should_countPageWhenMixed_withNull()
    {
        _service.RecordHit("fp-abc", "/home");
        _service.RecordHit("fp-abc", null);
        var result = _service.RecordHit("fp-abc", "/about");

        result.HitNumber.Should().Be(3);
        result.PageCount.Should().Be(2); // /home + /about
    }

    // ========================================================================
    // ACTIVE SESSION COUNT — Diagnostics
    // ========================================================================

    [Fact]
    public void ActiveSessionCount_should_reflectUniqueSessions()
    {
        _service.ActiveSessionCount.Should().Be(0);

        _service.RecordHit("fp-abc", "/home");
        _service.ActiveSessionCount.Should().Be(1);

        _service.RecordHit("fp-abc", "/about"); // same session
        _service.ActiveSessionCount.Should().Be(1);

        _service.RecordHit("fp-xyz", "/home"); // new session
        _service.ActiveSessionCount.Should().Be(2);
    }

    // ========================================================================
    // SESSION GUID FORMAT — Must be lowercase 36-char GUID
    // ========================================================================

    [Fact]
    public void RecordHit_should_returnValid_guid_format()
    {
        var result = _service.RecordHit("fp-abc", "/home");

        result.SessionId.Should().HaveLength(36);
        Guid.TryParse(result.SessionId, out _).Should().BeTrue();
    }

    // ========================================================================
    // CASE INSENSITIVITY — Fingerprints matched case-insensitively
    // ========================================================================

    [Fact]
    public void RecordHit_should_matchFingerprints_caseInsensitively()
    {
        var r1 = _service.RecordHit("FP-ABC", "/home");
        var r2 = _service.RecordHit("fp-abc", "/about");

        r2.SessionId.Should().Be(r1.SessionId);
        r2.HitNumber.Should().Be(2);
    }

    // ========================================================================
    // DURATION — Should be non-negative
    // ========================================================================

    [Fact]
    public void RecordHit_should_returnDuration_greaterOrEqualZero()
    {
        _service.RecordHit("fp-abc", "/home");
        Thread.Sleep(50); // small delay
        var result = _service.RecordHit("fp-abc", "/about");

        result.DurationSec.Should().BeGreaterThanOrEqualTo(0);
    }
}
