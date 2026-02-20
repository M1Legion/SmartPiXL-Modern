// ─────────────────────────────────────────────────────────────────────────────
// SmartPiXL Forge — Contradiction Matrix Service
// Rule engine that detects impossible, improbable, and suspicious field
// combinations in tracking data. Each rule is a concise delegate that
// evaluates a pre-extracted signal snapshot.
// Phase 6 — Tier 3 Enrichments (Asymmetric Detection)
// ─────────────────────────────────────────────────────────────────────────────

namespace SmartPiXL.Forge.Services.Enrichments;

/// <summary>
/// Evaluates tracking records against a matrix of contradiction rules.
/// Detects impossible hardware/software combinations that indicate spoofing,
/// emulation, or automation. Stateless — all state is in the signal snapshot.
/// </summary>
public sealed class ContradictionMatrixService
{
    // ════════════════════════════════════════════════════════════════════════
    // SIGNAL SNAPSHOT — Pre-extracted fields for fast rule evaluation
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pre-extracted signal values from the tracking record query string.
    /// Built once per record, evaluated against all rules.
    /// </summary>
    public readonly record struct SignalSnapshot(
        bool IsMobileUA,
        bool IsMacOS,
        bool IsLinux,
        bool IsWindows,
        bool IsSafari,
        bool IsChrome,
        int ScreenWidth,
        int ScreenHeight,
        int MouseMoves,
        double MouseEntropy,
        int TouchPoints,
        bool TouchEventsSupported,
        bool HasBatteryAPI,
        string? GpuString,
        string? GpuVendor,
        string? Platform,
        string? Fonts,
        bool WebDriverDetected,
        int HardwareConcurrency,
        double DeviceMemoryGB,
        bool HoverCapable);

    // ════════════════════════════════════════════════════════════════════════
    // RESULT
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Result of contradiction evaluation.
    /// </summary>
    /// <param name="Count">Number of contradiction rules that fired.</param>
    /// <param name="FlagList">Comma-separated rule names, or null if none fired.</param>
    public readonly record struct ContradictionResult(int Count, string? FlagList);

    // ════════════════════════════════════════════════════════════════════════
    // SEVERITY TIERS
    // Not used in scoring (count = count), but preserved in rule ordering
    // so IMPOSSIBLE rules appear first in the flag list for triage.
    // ════════════════════════════════════════════════════════════════════════

    private enum Severity { Impossible, Improbable, Suspicious }

    // ════════════════════════════════════════════════════════════════════════
    // CONTRADICTION RULES
    // Static array of (Name, Severity, Predicate). First match does NOT
    // short-circuit — ALL rules are evaluated per record for complete
    // contradiction profiling.
    // ════════════════════════════════════════════════════════════════════════

    private static readonly (string Name, Severity Severity, Func<SignalSnapshot, bool> Test)[] s_rules =
    [
        // ── IMPOSSIBLE: These combinations cannot exist on real hardware ──

        // Mobile UA claiming 4K resolution with desktop mouse input
        ("MobileHighRes", Severity.Impossible,
            s => s.IsMobileUA && s.ScreenWidth >= 2560 && s.MouseMoves > 0),

        // macOS does not use DirectX — if GPU string mentions Direct3D, it's spoofed
        ("MacOSDirectX", Severity.Impossible,
            s => s.IsMacOS && s.GpuString is not null &&
                 s.GpuString.Contains("Direct3D", StringComparison.OrdinalIgnoreCase)),

        // Safari never implemented the Battery Status API (W3C removed it for privacy)
        ("SafariBattery", Severity.Impossible,
            s => s.IsSafari && s.HasBatteryAPI),

        // Claiming touch capability but no touch event support = fabricated touch data
        ("TouchMismatch", Severity.Impossible,
            s => s.TouchPoints > 0 && !s.TouchEventsSupported),

        // Apple fonts never exist on Linux — indicates platform string is spoofed
        ("LinuxAppleFonts", Severity.Impossible,
            s => s.IsLinux && s.Fonts is not null && HasAppleFonts(s.Fonts)),

        // Safari for Windows was discontinued in 2012 (v5.1.7). Any modern
        // Safari version on Windows is impossible — likely headless automation
        ("WindowsSafari", Severity.Impossible,
            s => s.IsWindows && s.IsSafari),

        // Apple GPU vendor string on non-Apple platform = impossible
        ("AppleGPUNonMac", Severity.Impossible,
            s => !s.IsMacOS && s.GpuVendor is not null &&
                 s.GpuVendor.Contains("Apple", StringComparison.OrdinalIgnoreCase)),

        // ── IMPROBABLE: Technically possible but extremely unlikely ───────

        // Desktop UA with screen narrower than 600px — almost never happens
        // on real hardware (minimum common resolution is 1024x768)
        ("DesktopTinyScreen", Severity.Improbable,
            s => !s.IsMobileUA && s.ScreenWidth > 0 && s.ScreenWidth < 600),

        // High core count (16+) with virtual GPU = likely a VM/container
        // with emulated graphics
        ("HighCoresVirtualGPU", Severity.Improbable,
            s => s.HardwareConcurrency >= 16 && s.GpuString is not null &&
                 IsVirtualGpu(s.GpuString)),

        // WebDriver automation should not produce natural mouse entropy
        // (> 2.0 suggests real human movement, contradicting automation flag)
        ("WebDriverEntropy", Severity.Improbable,
            s => s.WebDriverDetected && s.MouseEntropy > 2.0),

        // ── SUSPICIOUS: Anomalous but potentially explainable ────────────

        // iPhone or iPad claiming very wide screen (iPhones max ~430pt logical)
        ("PhoneWideScreen", Severity.Suspicious,
            s => s.IsMobileUA && s.Platform is not null &&
                 s.Platform.Contains("iPhone", StringComparison.OrdinalIgnoreCase) &&
                 s.ScreenWidth > 500),

        // Extremely low memory (0.25GB) with high core count = VM lying
        // about resources (real 256MB devices don't have 8+ cores)
        ("LowMemHighCores", Severity.Suspicious,
            s => s.DeviceMemoryGB > 0 && s.DeviceMemoryGB <= 0.5 &&
                 s.HardwareConcurrency >= 8),

        // Browser exposes hover capability but claims mobile UA with touch
        // (most mobile browsers report hover: none)
        ("MobileTouchHover", Severity.Suspicious,
            s => s.IsMobileUA && s.TouchPoints > 0 && s.HoverCapable),
    ];

    // ════════════════════════════════════════════════════════════════════════
    // PUBLIC API
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Evaluates a pre-extracted signal snapshot against all contradiction rules.
    /// Returns the count of fired rules and a comma-separated list of rule names.
    /// </summary>
    public ContradictionResult Evaluate(in SignalSnapshot signals)
    {
        var count = 0;
        Span<int> firedIndices = stackalloc int[s_rules.Length];

        for (var i = 0; i < s_rules.Length; i++)
        {
            if (s_rules[i].Test(signals))
            {
                firedIndices[count] = i;
                count++;
            }
        }

        if (count == 0)
            return new ContradictionResult(0, null);

        // Build comma-separated flag list (IMPOSSIBLE rules first due to array ordering)
        var builder = new System.Text.StringBuilder(count * 20);
        for (var i = 0; i < count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(s_rules[firedIndices[i]].Name);
        }

        return new ContradictionResult(count, builder.ToString());
    }

    // ════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks if the font list contains any Apple-specific marker fonts.
    /// </summary>
    private static bool HasAppleFonts(string fonts)
    {
        // Quick scan for Apple font substrings — avoids full HashSet
        // allocation per record. These substrings are unique to Apple.
        return fonts.Contains("Helvetica Neue", StringComparison.OrdinalIgnoreCase)
            || fonts.Contains("Lucida Grande", StringComparison.OrdinalIgnoreCase)
            || fonts.Contains("Apple Color Emoji", StringComparison.OrdinalIgnoreCase)
            || fonts.Contains(".SF Pro", StringComparison.OrdinalIgnoreCase)
            || fonts.Contains("Apple Symbols", StringComparison.OrdinalIgnoreCase)
            || fonts.Contains("Menlo", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if the GPU renderer string indicates a virtual/emulated GPU.
    /// </summary>
    private static bool IsVirtualGpu(string gpu)
    {
        return gpu.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase)
            || gpu.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase)
            || gpu.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)
            || gpu.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || gpu.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || gpu.Contains("Mesa", StringComparison.OrdinalIgnoreCase);
    }
}
