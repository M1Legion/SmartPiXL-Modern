using SmartPiXL.Services;
using static SmartPiXL.Forge.Services.Enrichments.GpuTierReference;

namespace SmartPiXL.Forge.Services.Enrichments;

// ============================================================================
// DEVICE AFFLUENCE SERVICE — Classifies device affluence based on hardware
// signals captured by the PiXL Script.
//
// INPUT FIELDS (from query string):
//   gpu   — WebGL UNMASKED_RENDERER_OES (GPU renderer string)
//   cores — navigator.hardwareConcurrency (logical CPU cores, 0 if unavailable)
//   mem   — navigator.deviceMemory (GB, 0 if unavailable)
//   sw    — screen.width
//   sh    — screen.height
//   plt   — navigator.platform (e.g., "MacIntel", "Win32", "Linux x86_64")
//
// CLASSIFICATION LOGIC:
//   GPU tier (from GpuTierReference) is the primary signal.
//   Supporting signals (cores, memory, screen res, platform) adjust the score.
//   Each positive signal adds points; the total determines LOW/MID/HIGH.
//
// SCORING (0-100 scale, mapped to tier thresholds):
//   GPU = HIGH → +40,  MID → +25,  LOW → +10
//   cores >= 16 → +15,  >= 8 → +10,  >= 6 → +5
//   mem >= 16 → +15,  >= 8 → +10,  >= 4 → +5
//   screen (sw*sh) >= 8M (4K+) → +10,  >= 2M (1440p) → +5
//   platform = macOS → +10 (Apple tax = affluence signal)
//   platform = iOS/iPadOS → +10 (premium mobile ecosystem)
//
// THRESHOLDS:
//   >= 60 → HIGH,  >= 30 → MID,  < 30 → LOW
//
// APPENDED PARAMS:
//   _srv_affluence=HIGH|MID|LOW
//   _srv_gpuTier=HIGH|MID|LOW
// ============================================================================

/// <summary>
/// Classifies device affluence based on GPU, CPU cores, memory, screen resolution,
/// and platform. Singleton — stateless, thread-safe.
/// </summary>
public sealed class DeviceAffluenceService
{
    private readonly ITrackingLogger _logger;

    /// <summary>
    /// Result of device affluence classification.
    /// </summary>
    public readonly record struct AffluenceResult(string? Affluence, string? GpuTierStr);

    public DeviceAffluenceService(ITrackingLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Classifies the device's affluence level from hardware signals in the query string.
    /// </summary>
    /// <param name="gpu">GPU renderer string (WebGL UNMASKED_RENDERER_OES).</param>
    /// <param name="cores">Logical CPU cores (navigator.hardwareConcurrency). 0 = unavailable.</param>
    /// <param name="mem">Device memory in GB (navigator.deviceMemory). 0 = unavailable.</param>
    /// <param name="screenWidth">Screen width in pixels.</param>
    /// <param name="screenHeight">Screen height in pixels.</param>
    /// <param name="platform">navigator.platform string.</param>
    /// <returns>Affluence classification and GPU tier.</returns>
    public AffluenceResult Classify(string? gpu, int cores, int mem, int screenWidth, int screenHeight, string? platform)
    {
        var score = 0;

        // ── GPU tier (primary signal) ─────────────────────────────────────
        var gpuTier = GpuTierReference.Classify(gpu);
        score += gpuTier switch
        {
            GpuTier.High => 40,
            GpuTier.Mid => 25,
            GpuTier.Low => 10,
            _ => 0 // Unknown — no contribution (don't penalize missing data)
        };

        // ── CPU cores ─────────────────────────────────────────────────────
        if (cores >= 16)
            score += 15;
        else if (cores >= 8)
            score += 10;
        else if (cores >= 6)
            score += 5;

        // ── Device memory (GB) ────────────────────────────────────────────
        if (mem >= 16)
            score += 15;
        else if (mem >= 8)
            score += 10;
        else if (mem >= 4)
            score += 5;

        // ── Screen resolution ─────────────────────────────────────────────
        var pixels = (long)screenWidth * screenHeight;
        if (pixels >= 8_294_400)       // 3840×2160 = 4K+
            score += 10;
        else if (pixels >= 2_073_600)  // 2560×1440 = 1440p
            score += 5;

        // ── Platform (Apple ecosystem = affluence proxy) ──────────────────
        if (platform is not null)
        {
            // macOS platforms: "MacIntel", "MacARM" (Apple Silicon)
            if (platform.StartsWith("Mac", StringComparison.OrdinalIgnoreCase))
                score += 10;
            // iOS: "iPhone", "iPad", "iPod"
            else if (platform.StartsWith("iP", StringComparison.OrdinalIgnoreCase))
                score += 10;
        }

        // ── Map score to tier ─────────────────────────────────────────────
        string affluence;
        if (score >= 60)
            affluence = "HIGH";
        else if (score >= 30)
            affluence = "MID";
        else
            affluence = "LOW";

        var gpuTierStr = TierToString(gpuTier);
        return new AffluenceResult(affluence, gpuTierStr);
    }
}
