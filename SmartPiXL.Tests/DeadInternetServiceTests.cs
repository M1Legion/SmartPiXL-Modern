// ─────────────────────────────────────────────────────────────────────────────
// Tests for DeadInternetService — Per-customer traffic quality aggregation
// Phase 6 — Tier 3 Enrichments
// ─────────────────────────────────────────────────────────────────────────────

using SmartPiXL.Forge.Services.Enrichments;
using Xunit;

namespace SmartPiXL.Tests;

public sealed class DeadInternetServiceTests : IDisposable
{
    private readonly DeadInternetService _sut = new();

    // ── Clean traffic: all human signals ──────────────────────────
    [Fact]
    public void CleanTraffic_LowIndex()
    {
        // Record 10 clean hits with unique fingerprints
        var lastIdx = 0;
        for (var i = 0; i < 10; i++)
        {
            lastIdx = _sut.RecordHit(
                companyId: "COMP-001",
                isBotHit: false,
                hasMouseMoves: true,
                isDatacenter: false,
                contradictionCount: 0,
                isReplay: false,
                fingerprint: $"fp-{i}");
        }

        Assert.True(lastIdx <= 20, $"Clean traffic should have low index, got {lastIdx}");
    }

    // ── All bots: 100% bot traffic ────────────────────────────────
    [Fact]
    public void AllBots_HighIndex()
    {
        var lastIdx = 0;
        for (var i = 0; i < 10; i++)
        {
            lastIdx = _sut.RecordHit(
                companyId: "COMP-BOT",
                isBotHit: true,
                hasMouseMoves: false,
                isDatacenter: true,
                contradictionCount: 2,
                isReplay: false,
                fingerprint: "same-fp"); // low diversity
        }

        Assert.True(lastIdx >= 50, $"All-bot traffic should have high index, got {lastIdx}");
    }

    // ── Mixed traffic: moderate index ─────────────────────────────
    [Fact]
    public void MixedTraffic_ModerateIndex()
    {
        // 5 clean hits
        for (var i = 0; i < 5; i++)
        {
            _sut.RecordHit("COMP-MIX", false, true, false, 0, false, $"fp-human-{i}");
        }

        // 5 bot hits
        var lastIdx = 0;
        for (var i = 0; i < 5; i++)
        {
            lastIdx = _sut.RecordHit("COMP-MIX", true, false, true, 1, false, "fp-bot");
        }

        Assert.True(lastIdx > 20 && lastIdx < 80,
            $"Mixed traffic should have moderate index (20-80), got {lastIdx}");
    }

    // ── Null company returns 0 ────────────────────────────────────
    [Fact]
    public void NullCompany_ReturnsZero()
    {
        var idx = _sut.RecordHit(null, true, false, true, 5, true, "fp");
        Assert.Equal(0, idx);
    }

    // ── Below minimum hits returns 0 ──────────────────────────────
    [Fact]
    public void BelowMinimumHits_ReturnsZero()
    {
        // Only 2 hits (minimum is 5)
        _sut.RecordHit("COMP-FEW", true, false, true, 5, true, "fp");
        var idx = _sut.RecordHit("COMP-FEW", true, false, true, 5, true, "fp");
        Assert.Equal(0, idx);
    }

    // ── Different companies have independent tracking ─────────────
    [Fact]
    public void DifferentCompanies_IndependentTracking()
    {
        // Company A: all bots
        for (var i = 0; i < 10; i++)
            _sut.RecordHit("COMP-A", true, false, true, 2, false, "same-fp");

        // Company B: all clean
        var lastIdxB = 0;
        for (var i = 0; i < 10; i++)
            lastIdxB = _sut.RecordHit("COMP-B", false, true, false, 0, false, $"fp-{i}");

        // Company B should have low index despite Company A being all bots
        Assert.True(lastIdxB <= 20, $"Company B (clean) should have low index, got {lastIdxB}");
    }

    // ── Fingerprint diversity: single FP = suspicious ─────────────
    [Fact]
    public void SingleFingerprint_HighDiversityPenalty()
    {
        var lastIdx = 0;
        for (var i = 0; i < 10; i++)
        {
            lastIdx = _sut.RecordHit(
                "COMP-SINGLE-FP",
                isBotHit: false,
                hasMouseMoves: true,
                isDatacenter: false,
                contradictionCount: 0,
                isReplay: false,
                fingerprint: "always-same");
        }

        // Low FP diversity adds to the index
        Assert.True(lastIdx >= 10, $"Single FP should contribute to index, got {lastIdx}");
    }

    // ── Replay hits contribute to index ───────────────────────────
    [Fact]
    public void ReplayHits_ContributeToIndex()
    {
        for (var i = 0; i < 5; i++)
            _sut.RecordHit("COMP-REPLAY", false, true, false, 0, false, $"fp-{i}");

        var lastIdx = 0;
        for (var i = 0; i < 5; i++)
            lastIdx = _sut.RecordHit("COMP-REPLAY", false, false, false, 3, true, $"fp-replay-{i}");

        // Replay + contradictions + no mouse = elevated index
        Assert.True(lastIdx > 0, $"Replay hits should elevate index");
    }

    public void Dispose() => _sut.Dispose();
}
