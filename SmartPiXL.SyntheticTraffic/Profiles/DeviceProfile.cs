namespace SmartPiXL.SyntheticTraffic.Profiles;

// ============================================================================
// DEVICE PROFILE — Strongly-typed representation of a coherent browser/device.
//
// Every field in this type constrains every other. When the profile says
// "Safari on iPhone 15 Pro", the screen is 393×852 @3x, the GPU is "Apple GPU",
// touch is 5, Client Hints are absent, vendor is "Apple Computer, Inc.",
// and battery API is unavailable. No field can be set independently.
//
// The profile catalog creates these as immutable instances. The QS builder
// reads them to produce internally consistent 159-field query strings.
// ============================================================================

/// <summary>
/// A coherent device profile representing a specific browser + OS + hardware combination.
/// All fields are internally consistent — the type system prevents contradictions.
/// </summary>
public sealed class DeviceProfile
{
    // ── Identity ───────────────────────────────────────────────────────
    public required string Name { get; init; }
    public required int Weight { get; init; }

    // ── Browser ────────────────────────────────────────────────────────
    public required BrowserFamily Browser { get; init; }
    public required string UserAgent { get; init; }
    public required string AppVersion { get; init; }

    // ── Navigator properties ───────────────────────────────────────────
    public required string Platform { get; init; }
    public required string Vendor { get; init; }
    public string Product { get; init; } = "Gecko";
    public required string ProductSub { get; init; }
    public string AppName { get; init; } = "Netscape";
    public string AppCodeName { get; init; } = "Mozilla";

    // ── Hardware ────────────────────────────────────────────────────────
    public required ScreenProfile[] Screens { get; init; }
    public required int[] CoreOptions { get; init; }
    public required int[] MemoryGBOptions { get; init; }
    public required int MaxTouchPoints { get; init; }
    public required bool IsMobile { get; init; }
    public required bool HoverCapable { get; init; }

    // ── GPU ────────────────────────────────────────────────────────────
    public required string[] GPURenderers { get; init; }
    public required string[] GPUVendors { get; init; }

    // ── Client Hints (Chromium-only: null for Firefox/Safari) ──────────
    public ClientHintsProfile? ClientHints { get; init; }

    // ── Firefox-specific ───────────────────────────────────────────────
    public string? OSCPU { get; init; }
    public string? BuildID { get; init; }

    // ── Capabilities ───────────────────────────────────────────────────
    public bool HasBatteryAPI { get; init; }
    public bool HasConnectionAPI { get; init; }
    public required int ColorDepthOverride { get; init; }

    // ── Bot flags ──────────────────────────────────────────────────────
    public bool IsBot { get; init; }
    public bool IsCrawler { get; init; }

    // ── OS ──────────────────────────────────────────────────────────────
    public required OsFamily OS { get; init; }
}

/// <summary>Screen geometry for a specific device model.</summary>
public readonly record struct ScreenProfile(
    int Width, int Height,
    int AvailWidth, int AvailHeight,
    int ColorDepth, double PixelRatio);

/// <summary>
/// Client Hints data for Chromium browsers. When this is non-null, the QS builder
/// emits uaPlatform, uaArch, uaBitness, uaBrands, uaFormFactor, uaMobile, uaModel, uaWow64.
/// When null, all Client Hints fields are omitted (Safari/Firefox behavior).
/// </summary>
public sealed class ClientHintsProfile
{
    public required string UaPlatform { get; init; }
    public required string UaArch { get; init; }
    public required string UaBitness { get; init; }
    public required string UaBrands { get; init; }
    public required string UaFormFactor { get; init; }
    public required bool UaMobile { get; init; }
    public string? UaModel { get; init; }
    public required string UaPlatformVersion { get; init; }
    public required string UaFullVersion { get; init; }
}

public enum BrowserFamily
{
    Chrome,
    Firefox,
    Safari,
    Edge,
    HeadlessChrome,
    Googlebot,
    Bingbot,
    Crawler
}

public enum OsFamily
{
    Windows,
    MacOS,
    Linux,
    Android,
    IOS,
    IPadOS
}
