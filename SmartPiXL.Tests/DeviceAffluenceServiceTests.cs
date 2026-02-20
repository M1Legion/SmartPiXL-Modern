using FluentAssertions;
using Moq;
using SmartPiXL.Forge.Services.Enrichments;
using SmartPiXL.Services;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for DeviceAffluenceService — hardware signal → affluence classification.
/// Validates GPU tier weighting, core/memory scoring, screen resolution, and
/// platform bonuses across all three tiers (HIGH, MID, LOW).
/// </summary>
public sealed class DeviceAffluenceServiceTests
{
    private readonly DeviceAffluenceService _service;

    public DeviceAffluenceServiceTests()
    {
        var logger = new Mock<ITrackingLogger>();
        _service = new DeviceAffluenceService(logger.Object);
    }

    // ========================================================================
    // HIGH AFFLUENCE — Flagship GPU + premium hardware
    // ========================================================================

    [Fact]
    public void Classify_should_returnHigh_when_flagshipGpu_highCores_highMem()
    {
        // RTX 4090 (+40) + 16 cores (+15) + 16GB (+15) = 70 → HIGH
        var result = _service.Classify("NVIDIA GeForce RTX 4090", cores: 16, mem: 16,
            screenWidth: 1920, screenHeight: 1080, platform: null);

        result.Affluence.Should().Be("HIGH");
        result.GpuTierStr.Should().Be("HIGH");
    }

    [Fact]
    public void Classify_should_returnHigh_when_flagshipGpu_macPlatform()
    {
        // Apple M4 (+40) + 8 cores (+10) + 16GB (+15) + Mac (+10) = 75 → HIGH
        var result = _service.Classify("Apple M4", cores: 8, mem: 16,
            screenWidth: 2560, screenHeight: 1600, platform: "MacARM");

        result.Affluence.Should().Be("HIGH");
        result.GpuTierStr.Should().Be("HIGH");
    }

    [Fact]
    public void Classify_should_returnHigh_when_flagshipGpu_4kScreen()
    {
        // RTX 5080 (+40) + 12 cores (+10) + 32GB (+15) + 4K (+10) = 75 → HIGH
        var result = _service.Classify("NVIDIA GeForce RTX 5080", cores: 12, mem: 32,
            screenWidth: 3840, screenHeight: 2160, platform: "Win32");

        result.Affluence.Should().Be("HIGH");
    }

    // ========================================================================
    // MID AFFLUENCE — Mid-range or mixed signals
    // ========================================================================

    [Fact]
    public void Classify_should_returnMid_when_midGpu_standardHardware()
    {
        // RTX 3060 (+25) + 8 cores (+10) + 8GB (+10) = 45 → MID
        var result = _service.Classify("NVIDIA GeForce RTX 3060", cores: 8, mem: 8,
            screenWidth: 1920, screenHeight: 1080, platform: "Win32");

        result.Affluence.Should().Be("MID");
        result.GpuTierStr.Should().Be("MID");
    }

    [Fact]
    public void Classify_should_returnMid_when_lowGpu_butApplePlatform()
    {
        // UHD Graphics (+10) + 8 cores (+10) + 8GB (+10) + Mac (+10) = 40 → MID
        var result = _service.Classify("Intel(R) UHD Graphics 630", cores: 8, mem: 8,
            screenWidth: 1920, screenHeight: 1080, platform: "MacIntel");

        result.Affluence.Should().Be("MID");
        result.GpuTierStr.Should().Be("LOW");
    }

    [Fact]
    public void Classify_should_returnMid_when_unknownGpu_highCoresMem()
    {
        // Unknown GPU (+0) + 16 cores (+15) + 16GB (+15) + 4K (+10) = 40 → MID
        var result = _service.Classify("SomeUnknownGPU", cores: 16, mem: 16,
            screenWidth: 3840, screenHeight: 2160, platform: null);

        result.Affluence.Should().Be("MID");
        result.GpuTierStr.Should().BeNull(); // Unknown tier
    }

    // ========================================================================
    // LOW AFFLUENCE — Integrated GPU, minimal hardware
    // ========================================================================

    [Fact]
    public void Classify_should_returnLow_when_integratedGpu_lowHardware()
    {
        // HD Graphics (+10) + 4 cores (+0) + 4GB (+5) = 15 → LOW
        var result = _service.Classify("Intel(R) HD Graphics 620", cores: 4, mem: 4,
            screenWidth: 1366, screenHeight: 768, platform: "Win32");

        result.Affluence.Should().Be("LOW");
        result.GpuTierStr.Should().Be("LOW");
    }

    [Fact]
    public void Classify_should_returnLow_when_allZeros()
    {
        // No data → score = 0 → LOW
        var result = _service.Classify(null, cores: 0, mem: 0,
            screenWidth: 0, screenHeight: 0, platform: null);

        result.Affluence.Should().Be("LOW");
        result.GpuTierStr.Should().BeNull();
    }

    [Fact]
    public void Classify_should_returnLow_when_swiftshader()
    {
        // SwiftShader (+10) + 2 cores (+0) + 2GB (+0) = 10 → LOW
        var result = _service.Classify("Google SwiftShader", cores: 2, mem: 2,
            screenWidth: 1024, screenHeight: 768, platform: "Linux x86_64");

        result.Affluence.Should().Be("LOW");
    }

    // ========================================================================
    // SCORING EDGE CASES
    // ========================================================================

    [Fact]
    public void Classify_should_include_iPhonePlatformBonus()
    {
        // HD Graphics (+10) + 6 cores (+5) + 4GB (+5) + iPhone (+10) = 30 → MID
        var result = _service.Classify("Intel(R) HD Graphics 500", cores: 6, mem: 4,
            screenWidth: 1170, screenHeight: 2532, platform: "iPhone");

        result.Affluence.Should().Be("MID");
    }

    [Fact]
    public void Classify_should_include_iPadPlatformBonus()
    {
        // HD Graphics (+10) + 6 cores (+5) + 4GB (+5) + iPad (+10) = 30 → MID
        var result = _service.Classify("Intel(R) HD Graphics 500", cores: 6, mem: 4,
            screenWidth: 2048, screenHeight: 2732, platform: "iPad");

        result.Affluence.Should().Be("MID");
    }

    [Fact]
    public void Classify_should_add1440pScreenBonus()
    {
        // RTX 3070 (+25) + 0 cores (+0) + 0GB (+0) + 1440p (+5) = 30 → MID
        var result = _service.Classify("NVIDIA GeForce RTX 3070", cores: 0, mem: 0,
            screenWidth: 2560, screenHeight: 1440, platform: null);

        result.Affluence.Should().Be("MID");
    }

    [Fact]
    public void Classify_should_borderlineHigh_at60()
    {
        // Need exactly 60: RTX 4090 (+40) + 8 cores (+10) + 4GB (+5) + 1440p (+5) = 60 → HIGH
        var result = _service.Classify("NVIDIA GeForce RTX 4090", cores: 8, mem: 4,
            screenWidth: 2560, screenHeight: 1440, platform: null);

        result.Affluence.Should().Be("HIGH");
    }

    [Fact]
    public void Classify_should_borderlineMid_at30()
    {
        // Need exactly 30: RTX 3060 (+25) + 6 cores (+5) = 30 → MID
        var result = _service.Classify("NVIDIA GeForce RTX 3060", cores: 6, mem: 0,
            screenWidth: 800, screenHeight: 600, platform: null);

        result.Affluence.Should().Be("MID");
    }
}
