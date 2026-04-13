using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services.Enrichments;

// ============================================================================
// LEAD QUALITY SCORING SERVICE — Scores each hit 0-100 based on positive
// human-visitor signals, the reverse of bot scoring.
//
// A HIGH lead score means: residential IP, consistent fingerprint, real mouse
// movement, multiple fonts detected, clean canvas, matching timezone,
// valid session, no bot flags, no contradictions, keyboard language present,
// human scroll behavior, and a supported (non-EOL) operating system.
//
// SCORING INPUTS (read from query string + Tier 1 enrichment results):
//   +12 — Residential IP (not datacenter, not proxy, not hosting)
//   +12 — No bot signals (knownBot=0 or absent)
//   +10 — Consistent fingerprint (fpUniq=1 or no _srv_fpAlert)
//   +10 — No contradictions (no _srv_contradictions from Phase 6)
//   + 8 — Real mouse entropy (mouseEntropy > 2.0, typical for humans)
//   + 8 — Human scroll behavior (scrolled on page with non-zero depth)
//   + 8 — Valid session (2+ pages, session stitching hit# > 1 means repeat page)
//   + 8 — 3+ detected fonts (fontCount >= 3)
//   + 6 — Clean canvas (no canvasNoise or evasion flags)
//   + 6 — Matching timezone (no mismatch between client TZ and IP geo TZ)
//   + 6 — Keyboard language present (keyboard layout detected — bots lack this)
//   + 6 — Supported OS (OS version not end-of-life per endoflife.date)
//
// Total possible: 100
//
// THRESHOLDS (for labeling):
//   >= 75 → HIGH quality lead
//   >= 40 → MID quality lead
//   <  40 → LOW quality lead
//
// DESIGN:
//   Stateless per record — each record is scored independently based on its
//   own signals. Session context (hit number) comes from SessionStitchingService
//   which runs before this in the pipeline.
//
// APPENDED PARAMS:
//   _srv_leadScore={0-100}
// ============================================================================

/// <summary>
/// Scores each hit 0-100 based on positive human-visitor signals.
/// Singleton — stateless, thread-safe.
/// </summary>
public sealed class LeadQualityScoringService
{
    private readonly ITrackingLogger _logger;

    /// <summary>
    /// Input signals for lead quality scoring. These are extracted from the
    /// query string and Tier 1 enrichment results before calling Score().
    /// </summary>
    public readonly record struct LeadSignals(
        bool IsResidentialIp,
        bool HasConsistentFingerprint,
        double MouseEntropy,
        int FontCount,
        bool HasCleanCanvas,
        bool HasMatchingTimezone,
        int SessionHitNumber,
        bool IsKnownBot,
        int ContradictionCount,
        bool HasKeyboardLanguage,
        bool HasScrollActivity,
        bool HasSupportedOs);

    public LeadQualityScoringService(ITrackingLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Computes the lead quality score (0-100) from the given signal inputs.
    /// </summary>
    public int Score(in LeadSignals signals)
    {
        var score = 0;

        // +12 — Residential IP (not datacenter, proxy, or hosting)
        if (signals.IsResidentialIp)
            score += 12;

        // +12 — No bot signals
        if (!signals.IsKnownBot)
            score += 12;

        // +10 — Consistent fingerprint (no stability alert)
        if (signals.HasConsistentFingerprint)
            score += 10;

        // +10 — No contradictions
        if (signals.ContradictionCount == 0)
            score += 10;

        // +8 — Real mouse entropy (humans typically > 2.0)
        if (signals.MouseEntropy > 2.0)
            score += 8;

        // +8 — Human scroll behavior (scrolled with non-zero scroll depth)
        if (signals.HasScrollActivity)
            score += 8;

        // +8 — Valid session (returning visitor or multi-page session)
        if (signals.SessionHitNumber >= 2)
            score += 8;

        // +8 — 3+ detected fonts (bots often report 0 or limited fonts)
        if (signals.FontCount >= 3)
            score += 8;

        // +6 — Clean canvas (no noise injection or evasion detected)
        if (signals.HasCleanCanvas)
            score += 6;

        // +6 — Matching timezone (client TZ matches IP geo expected TZ)
        if (signals.HasMatchingTimezone)
            score += 6;

        // +6 — Keyboard language present (headless bots lack keyboard layout)
        if (signals.HasKeyboardLanguage)
            score += 6;

        // +6 — Supported OS (not end-of-life per endoflife.date)
        if (signals.HasSupportedOs)
            score += 6;

        return score;
    }
}
