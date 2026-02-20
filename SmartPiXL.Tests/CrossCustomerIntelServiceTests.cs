using FluentAssertions;
using Moq;
using SmartPiXL.Forge.Services.Enrichments;
using SmartPiXL.Services;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for CrossCustomerIntelService — cross-customer scraping detection.
/// Validates alert thresholds, sliding window, and null input handling.
/// </summary>
public sealed class CrossCustomerIntelServiceTests
{
    private readonly CrossCustomerIntelService _service;

    public CrossCustomerIntelServiceTests()
    {
        var logger = new Mock<ITrackingLogger>();
        _service = new CrossCustomerIntelService(logger.Object);
    }

    // ========================================================================
    // SINGLE HIT — No alert
    // ========================================================================

    [Fact]
    public void RecordHit_should_return1Company_when_firstHit()
    {
        var result = _service.RecordHit("1.2.3.4", "fp-abc", "COMP001");

        result.DistinctCompanies.Should().Be(1);
        result.IsAlert.Should().BeFalse();
        result.WindowMinutes.Should().Be(5);
    }

    // ========================================================================
    // TWO COMPANIES — Still below threshold
    // ========================================================================

    [Fact]
    public void RecordHit_should_return2Companies_when_twoDistinctCompanies()
    {
        _service.RecordHit("1.2.3.4", "fp-abc", "COMP001");
        var result = _service.RecordHit("1.2.3.4", "fp-abc", "COMP002");

        result.DistinctCompanies.Should().Be(2);
        result.IsAlert.Should().BeFalse();
    }

    // ========================================================================
    // THREE COMPANIES — Alert threshold reached
    // ========================================================================

    [Fact]
    public void RecordHit_should_alert_when_3CompaniesIn5Minutes()
    {
        _service.RecordHit("1.2.3.4", "fp-abc", "COMP001");
        _service.RecordHit("1.2.3.4", "fp-abc", "COMP002");
        var result = _service.RecordHit("1.2.3.4", "fp-abc", "COMP003");

        result.DistinctCompanies.Should().Be(3);
        result.IsAlert.Should().BeTrue();
    }

    // ========================================================================
    // SAME COMPANY REPEATED — Not counted as distinct
    // ========================================================================

    [Fact]
    public void RecordHit_should_notAlert_when_sameCompanyRepeated()
    {
        _service.RecordHit("1.2.3.4", "fp-abc", "COMP001");
        _service.RecordHit("1.2.3.4", "fp-abc", "COMP001");
        _service.RecordHit("1.2.3.4", "fp-abc", "COMP001");
        var result = _service.RecordHit("1.2.3.4", "fp-abc", "COMP001");

        result.DistinctCompanies.Should().Be(1);
        result.IsAlert.Should().BeFalse();
    }

    // ========================================================================
    // DIFFERENT IPs — Tracked independently
    // ========================================================================

    [Fact]
    public void RecordHit_should_trackSeparately_when_differentIPs()
    {
        _service.RecordHit("1.2.3.4", "fp-abc", "COMP001");
        _service.RecordHit("1.2.3.4", "fp-abc", "COMP002");
        _service.RecordHit("1.2.3.4", "fp-abc", "COMP003");

        // Different IP, same fingerprint — should be independent
        var result = _service.RecordHit("5.6.7.8", "fp-abc", "COMP001");

        result.DistinctCompanies.Should().Be(1);
        result.IsAlert.Should().BeFalse();
    }

    // ========================================================================
    // DIFFERENT FINGERPRINTS — Tracked independently
    // ========================================================================

    [Fact]
    public void RecordHit_should_trackSeparately_when_differentFingerprints()
    {
        _service.RecordHit("1.2.3.4", "fp-abc", "COMP001");
        _service.RecordHit("1.2.3.4", "fp-abc", "COMP002");
        _service.RecordHit("1.2.3.4", "fp-abc", "COMP003");

        // Same IP, different fingerprint — should be independent
        var result = _service.RecordHit("1.2.3.4", "fp-xyz", "COMP001");

        result.DistinctCompanies.Should().Be(1);
        result.IsAlert.Should().BeFalse();
    }

    // ========================================================================
    // NULL / EMPTY INPUTS — Should return default (no crash)
    // ========================================================================

    [Fact]
    public void RecordHit_should_returnDefault_when_null_ip()
    {
        var result = _service.RecordHit(null, "fp-abc", "COMP001");

        result.DistinctCompanies.Should().Be(0);
        result.IsAlert.Should().BeFalse();
    }

    [Fact]
    public void RecordHit_should_returnDefault_when_null_fingerprint()
    {
        var result = _service.RecordHit("1.2.3.4", null, "COMP001");

        result.DistinctCompanies.Should().Be(0);
        result.IsAlert.Should().BeFalse();
    }

    [Fact]
    public void RecordHit_should_returnDefault_when_null_companyId()
    {
        var result = _service.RecordHit("1.2.3.4", "fp-abc", null);

        result.DistinctCompanies.Should().Be(0);
        result.IsAlert.Should().BeFalse();
    }

    [Fact]
    public void RecordHit_should_returnDefault_when_emptyStrings()
    {
        var result = _service.RecordHit("", "", "");

        result.DistinctCompanies.Should().Be(0);
        result.IsAlert.Should().BeFalse();
    }

    // ========================================================================
    // TRACKER COUNT — Diagnostics
    // ========================================================================

    [Fact]
    public void TrackerCount_should_reflectUniqueKeys()
    {
        _service.TrackerCount.Should().Be(0);

        _service.RecordHit("1.2.3.4", "fp-abc", "COMP001");
        _service.TrackerCount.Should().Be(1);

        _service.RecordHit("1.2.3.4", "fp-abc", "COMP002"); // same key
        _service.TrackerCount.Should().Be(1);

        _service.RecordHit("5.6.7.8", "fp-xyz", "COMP001"); // different key
        _service.TrackerCount.Should().Be(2);
    }

    // ========================================================================
    // MANY COMPANIES — Well above threshold
    // ========================================================================

    [Fact]
    public void RecordHit_should_alert_when_manyCompanies()
    {
        for (var i = 1; i <= 10; i++)
        {
            _service.RecordHit("1.2.3.4", "fp-abc", $"COMP{i:D3}");
        }

        var result = _service.RecordHit("1.2.3.4", "fp-abc", "COMP011");

        result.DistinctCompanies.Should().Be(11);
        result.IsAlert.Should().BeTrue();
    }
}
