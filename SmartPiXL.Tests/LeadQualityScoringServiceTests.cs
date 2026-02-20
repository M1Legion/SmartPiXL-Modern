using FluentAssertions;
using Moq;
using SmartPiXL.Forge.Services.Enrichments;
using SmartPiXL.Services;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for LeadQualityScoringService — human-visitor quality scoring (0-100).
/// Validates each signal's contribution and combined scoring thresholds.
/// </summary>
public sealed class LeadQualityScoringServiceTests
{
    private readonly LeadQualityScoringService _service;

    public LeadQualityScoringServiceTests()
    {
        var logger = new Mock<ITrackingLogger>();
        _service = new LeadQualityScoringService(logger.Object);
    }

    // ========================================================================
    // PERFECT SCORE — All signals positive → 100
    // ========================================================================

    [Fact]
    public void Score_should_return100_when_allSignalsPositive()
    {
        var signals = new LeadQualityScoringService.LeadSignals(
            IsResidentialIp: true,           // +15
            HasConsistentFingerprint: true,   // +12
            MouseEntropy: 3.5,               // +12
            FontCount: 10,                   // +10
            HasCleanCanvas: true,            // +8
            HasMatchingTimezone: true,        // +8
            SessionHitNumber: 5,             // +10
            IsKnownBot: false,               // +15
            ContradictionCount: 0);          // +10

        _service.Score(signals).Should().Be(100);
    }

    // ========================================================================
    // ZERO SCORE — All signals negative → 0
    // ========================================================================

    [Fact]
    public void Score_should_return0_when_allSignalsNegative()
    {
        var signals = new LeadQualityScoringService.LeadSignals(
            IsResidentialIp: false,
            HasConsistentFingerprint: false,
            MouseEntropy: 0.0,
            FontCount: 0,
            HasCleanCanvas: false,
            HasMatchingTimezone: false,
            SessionHitNumber: 1,
            IsKnownBot: true,
            ContradictionCount: 3);

        _service.Score(signals).Should().Be(0);
    }

    // ========================================================================
    // INDIVIDUAL SIGNAL CONTRIBUTIONS
    // ========================================================================

    [Fact]
    public void Score_should_add15_when_residentialIp()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { IsResidentialIp = true };

        _service.Score(withSignal).Should().Be(15);
    }

    [Fact]
    public void Score_should_add12_when_consistentFingerprint()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { HasConsistentFingerprint = true };

        _service.Score(withSignal).Should().Be(12);
    }

    [Fact]
    public void Score_should_add12_when_mouseEntropy_above2()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { MouseEntropy = 2.5 };

        _service.Score(withSignal).Should().Be(12);
    }

    [Fact]
    public void Score_should_notAddMouseEntropy_when_exactly2()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { MouseEntropy = 2.0 };

        _service.Score(withSignal).Should().Be(0);
    }

    [Fact]
    public void Score_should_add10_when_3OrMoreFonts()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { FontCount = 3 };

        _service.Score(withSignal).Should().Be(10);
    }

    [Fact]
    public void Score_should_notAddFonts_when_only2()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { FontCount = 2 };

        _service.Score(withSignal).Should().Be(0);
    }

    [Fact]
    public void Score_should_add8_when_cleanCanvas()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { HasCleanCanvas = true };

        _service.Score(withSignal).Should().Be(8);
    }

    [Fact]
    public void Score_should_add8_when_matchingTimezone()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { HasMatchingTimezone = true };

        _service.Score(withSignal).Should().Be(8);
    }

    [Fact]
    public void Score_should_add10_when_sessionHitNumber_2OrMore()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { SessionHitNumber = 2 };

        _service.Score(withSignal).Should().Be(10);
    }

    [Fact]
    public void Score_should_notAddSession_when_hitNumber1()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { SessionHitNumber = 1 };

        _service.Score(withSignal).Should().Be(0);
    }

    [Fact]
    public void Score_should_add15_when_notBot()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { IsKnownBot = false };

        _service.Score(withSignal).Should().Be(15);
    }

    [Fact]
    public void Score_should_add10_when_noContradictions()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { ContradictionCount = 0 };

        _service.Score(withSignal).Should().Be(10);
    }

    // ========================================================================
    // COMBINED SCENARIOS
    // ========================================================================

    [Fact]
    public void Score_should_returnTypicalHumanScore()
    {
        // Typical human visitor: residential, consistent FP, some mouse movement,
        // fonts present, first page visit, not a bot, no contradictions
        var signals = new LeadQualityScoringService.LeadSignals(
            IsResidentialIp: true,           // +15
            HasConsistentFingerprint: true,   // +12
            MouseEntropy: 3.0,               // +12
            FontCount: 8,                    // +10
            HasCleanCanvas: true,            // +8
            HasMatchingTimezone: true,        // +8
            SessionHitNumber: 1,             // +0  (first hit)
            IsKnownBot: false,               // +15
            ContradictionCount: 0);          // +10

        // 15+12+12+10+8+8+0+15+10 = 90
        _service.Score(signals).Should().Be(90);
    }

    [Fact]
    public void Score_should_returnSuspiciousScore()
    {
        // Suspicious: datacenter IP, inconsistent FP, low mouse, known bot
        var signals = new LeadQualityScoringService.LeadSignals(
            IsResidentialIp: false,
            HasConsistentFingerprint: false,
            MouseEntropy: 0.5,
            FontCount: 1,
            HasCleanCanvas: false,
            HasMatchingTimezone: true,        // +8
            SessionHitNumber: 3,             // +10
            IsKnownBot: true,
            ContradictionCount: 5);

        // 0+0+0+0+0+8+10+0+0 = 18
        _service.Score(signals).Should().Be(18);
    }

    /// <summary>
    /// Creates a signal set where all contributions are 0 (worst case bot).
    /// </summary>
    private static LeadQualityScoringService.LeadSignals CreateAllNegative()
    {
        return new LeadQualityScoringService.LeadSignals(
            IsResidentialIp: false,
            HasConsistentFingerprint: false,
            MouseEntropy: 0.0,
            FontCount: 0,
            HasCleanCanvas: false,
            HasMatchingTimezone: false,
            SessionHitNumber: 1,   // 1 = first hit, no session bonus
            IsKnownBot: true,      // is a bot → no +15
            ContradictionCount: 1); // has contradictions → no +10
    }
}
