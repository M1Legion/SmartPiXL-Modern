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
            IsResidentialIp: true,           // +12
            HasConsistentFingerprint: true,   // +10
            MouseEntropy: 3.5,               // +8
            FontCount: 10,                   // +8
            HasCleanCanvas: true,            // +6
            HasMatchingTimezone: true,        // +6
            SessionHitNumber: 5,             // +8
            IsKnownBot: false,               // +12
            ContradictionCount: 0,           // +10
            HasKeyboardLanguage: true,       // +6
            HasScrollActivity: true,         // +8
            HasSupportedOs: true);           // +6

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
            ContradictionCount: 3,
            HasKeyboardLanguage: false,
            HasScrollActivity: false,
            HasSupportedOs: false);

        _service.Score(signals).Should().Be(0);
    }

    // ========================================================================
    // INDIVIDUAL SIGNAL CONTRIBUTIONS
    // ========================================================================

    [Fact]
    public void Score_should_add12_when_residentialIp()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { IsResidentialIp = true };

        _service.Score(withSignal).Should().Be(12);
    }

    [Fact]
    public void Score_should_add10_when_consistentFingerprint()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { HasConsistentFingerprint = true };

        _service.Score(withSignal).Should().Be(10);
    }

    [Fact]
    public void Score_should_add8_when_mouseEntropy_above2()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { MouseEntropy = 2.5 };

        _service.Score(withSignal).Should().Be(8);
    }

    [Fact]
    public void Score_should_notAddMouseEntropy_when_exactly2()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { MouseEntropy = 2.0 };

        _service.Score(withSignal).Should().Be(0);
    }

    [Fact]
    public void Score_should_add8_when_3OrMoreFonts()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { FontCount = 3 };

        _service.Score(withSignal).Should().Be(8);
    }

    [Fact]
    public void Score_should_notAddFonts_when_only2()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { FontCount = 2 };

        _service.Score(withSignal).Should().Be(0);
    }

    [Fact]
    public void Score_should_add6_when_cleanCanvas()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { HasCleanCanvas = true };

        _service.Score(withSignal).Should().Be(6);
    }

    [Fact]
    public void Score_should_add6_when_matchingTimezone()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { HasMatchingTimezone = true };

        _service.Score(withSignal).Should().Be(6);
    }

    [Fact]
    public void Score_should_add8_when_sessionHitNumber_2OrMore()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { SessionHitNumber = 2 };

        _service.Score(withSignal).Should().Be(8);
    }

    [Fact]
    public void Score_should_notAddSession_when_hitNumber1()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { SessionHitNumber = 1 };

        _service.Score(withSignal).Should().Be(0);
    }

    [Fact]
    public void Score_should_add12_when_notBot()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { IsKnownBot = false };

        _service.Score(withSignal).Should().Be(12);
    }

    [Fact]
    public void Score_should_add10_when_noContradictions()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { ContradictionCount = 0 };

        _service.Score(withSignal).Should().Be(10);
    }

    [Fact]
    public void Score_should_add6_when_keyboardLanguagePresent()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { HasKeyboardLanguage = true };

        _service.Score(withSignal).Should().Be(6);
    }

    [Fact]
    public void Score_should_add8_when_scrollActivity()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { HasScrollActivity = true };

        _service.Score(withSignal).Should().Be(8);
    }

    [Fact]
    public void Score_should_add6_when_supportedOs()
    {
        var baseline = CreateAllNegative();
        var withSignal = baseline with { HasSupportedOs = true };

        _service.Score(withSignal).Should().Be(6);
    }

    // ========================================================================
    // COMBINED SCENARIOS
    // ========================================================================

    [Fact]
    public void Score_should_returnTypicalHumanScore()
    {
        // Typical human visitor: residential, consistent FP, some mouse movement,
        // fonts present, first page visit, not a bot, no contradictions, keyboard
        // present, scrolled on page, supported OS
        var signals = new LeadQualityScoringService.LeadSignals(
            IsResidentialIp: true,           // +12
            HasConsistentFingerprint: true,   // +10
            MouseEntropy: 3.0,               // +8
            FontCount: 8,                    // +8
            HasCleanCanvas: true,            // +6
            HasMatchingTimezone: true,        // +6
            SessionHitNumber: 1,             // +0  (first hit)
            IsKnownBot: false,               // +12
            ContradictionCount: 0,           // +10
            HasKeyboardLanguage: true,       // +6
            HasScrollActivity: true,         // +8
            HasSupportedOs: true);           // +6

        // 12+10+8+8+6+6+0+12+10+6+8+6 = 92
        _service.Score(signals).Should().Be(92);
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
            HasMatchingTimezone: true,        // +6
            SessionHitNumber: 3,             // +8
            IsKnownBot: true,
            ContradictionCount: 5,
            HasKeyboardLanguage: false,
            HasScrollActivity: false,
            HasSupportedOs: false);

        // 0+0+0+0+0+6+8+0+0+0+0+0 = 14
        _service.Score(signals).Should().Be(14);
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
            IsKnownBot: true,      // is a bot → no +12
            ContradictionCount: 1, // has contradictions → no +10
            HasKeyboardLanguage: false,
            HasScrollActivity: false,
            HasSupportedOs: false);
    }
}
