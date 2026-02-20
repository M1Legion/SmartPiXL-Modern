// ─────────────────────────────────────────────────────────────────────────────
// Tests for ContradictionMatrixService — Impossible, Improbable, Suspicious
// Phase 6 — Tier 3 Enrichments
// ─────────────────────────────────────────────────────────────────────────────

using SmartPiXL.Forge.Services.Enrichments;
using Xunit;

namespace TrackingPixel.Tests;

public sealed class ContradictionMatrixServiceTests
{
    private readonly ContradictionMatrixService _sut = new();

    private static ContradictionMatrixService.SignalSnapshot Clean() => new(
        IsMobileUA: false, IsMacOS: false, IsLinux: false, IsWindows: true,
        IsSafari: false, IsChrome: true, ScreenWidth: 1920, ScreenHeight: 1080,
        MouseMoves: 50, MouseEntropy: 4.5, TouchPoints: 0,
        TouchEventsSupported: false, HasBatteryAPI: false,
        GpuString: "NVIDIA GeForce RTX 4090", GpuVendor: "NVIDIA Corporation",
        Platform: "Win32", Fonts: "Segoe UI,Consolas,Calibri",
        WebDriverDetected: false, HardwareConcurrency: 16,
        DeviceMemoryGB: 32.0, HoverCapable: true);

    // ── Zero contradictions for a clean desktop profile ────────────
    [Fact]
    public void CleanDesktop_NoContradictions()
    {
        var result = _sut.Evaluate(Clean());
        Assert.Equal(0, result.Count);
        Assert.Null(result.FlagList);
    }

    // ── IMPOSSIBLE: Mobile + 4K + mouse ───────────────────────────
    [Fact]
    public void MobileHighRes_Fires()
    {
        var s = Clean() with { IsMobileUA = true, ScreenWidth = 3840, IsWindows = false };
        var result = _sut.Evaluate(s);
        Assert.True(result.Count >= 1);
        Assert.Contains("MobileHighRes", result.FlagList!);
    }

    // ── IMPOSSIBLE: macOS + DirectX ───────────────────────────────
    [Fact]
    public void MacOSDirectX_Fires()
    {
        var s = Clean() with { IsMacOS = true, IsWindows = false, GpuString = "ANGLE (Direct3D 11)" };
        var result = _sut.Evaluate(s);
        Assert.Contains("MacOSDirectX", result.FlagList!);
    }

    // ── IMPOSSIBLE: Safari + Battery API ──────────────────────────
    [Fact]
    public void SafariBattery_Fires()
    {
        var s = Clean() with { IsSafari = true, IsChrome = false, HasBatteryAPI = true };
        var result = _sut.Evaluate(s);
        Assert.Contains("SafariBattery", result.FlagList!);
    }

    // ── IMPOSSIBLE: Touch points but no touch events ──────────────
    [Fact]
    public void TouchMismatch_Fires()
    {
        var s = Clean() with { TouchPoints = 5, TouchEventsSupported = false };
        var result = _sut.Evaluate(s);
        Assert.Contains("TouchMismatch", result.FlagList!);
    }

    // ── IMPOSSIBLE: Linux + Apple fonts ───────────────────────────
    [Fact]
    public void LinuxAppleFonts_Fires()
    {
        var s = Clean() with
        {
            IsLinux = true, IsWindows = false,
            Platform = "Linux x86_64",
            Fonts = "Helvetica Neue,Liberation Sans,Ubuntu"
        };
        var result = _sut.Evaluate(s);
        Assert.Contains("LinuxAppleFonts", result.FlagList!);
    }

    // ── IMPOSSIBLE: Windows + Safari ──────────────────────────────
    [Fact]
    public void WindowsSafari_Fires()
    {
        var s = Clean() with { IsSafari = true, IsChrome = false };
        var result = _sut.Evaluate(s);
        Assert.Contains("WindowsSafari", result.FlagList!);
    }

    // ── IMPOSSIBLE: Apple GPU on non-Mac ──────────────────────────
    [Fact]
    public void AppleGPUNonMac_Fires()
    {
        var s = Clean() with { GpuVendor = "Apple" };
        var result = _sut.Evaluate(s);
        Assert.Contains("AppleGPUNonMac", result.FlagList!);
    }

    // ── IMPROBABLE: Desktop + tiny screen ─────────────────────────
    [Fact]
    public void DesktopTinyScreen_Fires()
    {
        var s = Clean() with { ScreenWidth = 320 };
        var result = _sut.Evaluate(s);
        Assert.Contains("DesktopTinyScreen", result.FlagList!);
    }

    // ── IMPROBABLE: High cores + virtual GPU ──────────────────────
    [Fact]
    public void HighCoresVirtualGPU_Fires()
    {
        var s = Clean() with { HardwareConcurrency = 32, GpuString = "Google SwiftShader" };
        var result = _sut.Evaluate(s);
        Assert.Contains("HighCoresVirtualGPU", result.FlagList!);
    }

    // ── IMPROBABLE: WebDriver + mouse entropy ─────────────────────
    [Fact]
    public void WebDriverEntropy_Fires()
    {
        var s = Clean() with { WebDriverDetected = true, MouseEntropy = 4.0 };
        var result = _sut.Evaluate(s);
        Assert.Contains("WebDriverEntropy", result.FlagList!);
    }

    // ── SUSPICIOUS: iPhone with wide screen ───────────────────────
    [Fact]
    public void PhoneWideScreen_Fires()
    {
        var s = Clean() with
        {
            IsMobileUA = true, IsWindows = false,
            Platform = "iPhone", ScreenWidth = 600,
            MouseMoves = 0 // Avoid MobileHighRes by removing mouse
        };
        var result = _sut.Evaluate(s);
        Assert.Contains("PhoneWideScreen", result.FlagList!);
    }

    // ── SUSPICIOUS: Low memory + high cores ───────────────────────
    [Fact]
    public void LowMemHighCores_Fires()
    {
        var s = Clean() with { DeviceMemoryGB = 0.25, HardwareConcurrency = 16 };
        var result = _sut.Evaluate(s);
        Assert.Contains("LowMemHighCores", result.FlagList!);
    }

    // ── SUSPICIOUS: Mobile + touch + hover ────────────────────────
    [Fact]
    public void MobileTouchHover_Fires()
    {
        var s = Clean() with
        {
            IsMobileUA = true, IsWindows = false,
            TouchPoints = 5, TouchEventsSupported = true,
            HoverCapable = true,
            ScreenWidth = 414, MouseMoves = 0 // Realistic mobile dims
        };
        var result = _sut.Evaluate(s);
        Assert.Contains("MobileTouchHover", result.FlagList!);
    }

    // ── Multiple contradictions compound ──────────────────────────
    [Fact]
    public void MultipleContradictions_AllReported()
    {
        var s = Clean() with
        {
            IsSafari = true, IsChrome = false,   // WindowsSafari
            HasBatteryAPI = true,                  // SafariBattery
            TouchPoints = 3, TouchEventsSupported = false // TouchMismatch
        };
        var result = _sut.Evaluate(s);
        Assert.True(result.Count >= 3, $"Expected >=3 but got {result.Count}: {result.FlagList}");
    }

    // ── No false positive: macOS without DirectX (clean Mac) ──────
    [Fact]
    public void CleanMac_NoDirectXFlag()
    {
        var s = Clean() with
        {
            IsMacOS = true, IsWindows = false,
            GpuString = "Apple M2 Pro", GpuVendor = "Apple",
            Platform = "MacIntel",
            Fonts = "Helvetica Neue,Menlo,Monaco"
        };
        var result = _sut.Evaluate(s);
        // Should NOT fire MacOSDirectX, but AppleGPUNonMac shouldn't fire either
        // because IsMacOS = true
        Assert.DoesNotContain("MacOSDirectX", result.FlagList ?? string.Empty);
        Assert.DoesNotContain("AppleGPUNonMac", result.FlagList ?? string.Empty);
    }

    // ── Null/empty fields don't cause exceptions ──────────────────
    [Fact]
    public void NullFields_NoExceptions()
    {
        var s = new ContradictionMatrixService.SignalSnapshot(
            IsMobileUA: false, IsMacOS: false, IsLinux: false, IsWindows: false,
            IsSafari: false, IsChrome: false, ScreenWidth: 0, ScreenHeight: 0,
            MouseMoves: 0, MouseEntropy: 0, TouchPoints: 0,
            TouchEventsSupported: false, HasBatteryAPI: false,
            GpuString: null, GpuVendor: null, Platform: null, Fonts: null,
            WebDriverDetected: false, HardwareConcurrency: 0,
            DeviceMemoryGB: 0, HoverCapable: false);
        var result = _sut.Evaluate(s);
        Assert.True(result.Count >= 0); // Just shouldn't throw
    }
}
