using FluentAssertions;
using SmartPiXL.Forge.Services.Enrichments;
using static SmartPiXL.Forge.Services.Enrichments.GpuTierReference;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for GpuTierReference — static GPU renderer string → tier lookup.
/// Validates pattern matching across HIGH, MID, LOW tiers and edge cases.
/// </summary>
public sealed class GpuTierReferenceTests
{
    // ========================================================================
    // NULL / EMPTY — Should return Unknown
    // ========================================================================

    [Fact]
    public void Classify_should_returnUnknown_when_null()
    {
        GpuTierReference.Classify(null).Should().Be(GpuTier.Unknown);
    }

    [Fact]
    public void Classify_should_returnUnknown_when_empty()
    {
        GpuTierReference.Classify("").Should().Be(GpuTier.Unknown);
    }

    [Fact]
    public void Classify_should_returnUnknown_when_noMatch()
    {
        GpuTierReference.Classify("SomeMadeUpGPU 9000").Should().Be(GpuTier.Unknown);
    }

    // ========================================================================
    // HIGH — Current gen / flagship GPUs
    // ========================================================================

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090")]
    [InlineData("NVIDIA GeForce RTX 4080 SUPER")]
    [InlineData("NVIDIA GeForce RTX 4070 Ti")]
    [InlineData("NVIDIA GeForce RTX 4060")]
    [InlineData("NVIDIA GeForce RTX 5090")]
    [InlineData("NVIDIA GeForce RTX 5080")]
    [InlineData("NVIDIA GeForce RTX 5070 Ti")]
    public void Classify_should_returnHigh_when_nvidiaCurrent(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.High);
    }

    [Theory]
    [InlineData("AMD Radeon RX 7900 XTX")]
    [InlineData("AMD Radeon RX 7800 XT")]
    [InlineData("AMD Radeon RX 7600")]
    [InlineData("AMD Radeon RX 9070 XT")]
    public void Classify_should_returnHigh_when_amdCurrent(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.High);
    }

    [Theory]
    [InlineData("Apple M3 Pro")]
    [InlineData("Apple M3 Max")]
    [InlineData("Apple M3 Ultra")]
    [InlineData("Apple M3")]
    [InlineData("Apple M4")]
    public void Classify_should_returnHigh_when_appleSilicon(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.High);
    }

    [Theory]
    [InlineData("Intel(R) Arc(TM) A770")]
    [InlineData("Intel(R) Arc(TM) A750")]
    public void Classify_should_returnHigh_when_intelArcHigh(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.High);
    }

    // ========================================================================
    // MID — Previous gen / mid-range
    // ========================================================================

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3090")]
    [InlineData("NVIDIA GeForce RTX 3080 Ti")]
    [InlineData("NVIDIA GeForce RTX 3070")]
    [InlineData("NVIDIA GeForce RTX 3060 Ti")]
    [InlineData("NVIDIA GeForce RTX 3050")]
    public void Classify_should_returnMid_when_nvidiaPrevGen(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.Mid);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 2080 Ti")]
    [InlineData("NVIDIA GeForce RTX 2070 SUPER")]
    [InlineData("NVIDIA GeForce RTX 2060")]
    public void Classify_should_returnMid_when_nvidia20Series(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.Mid);
    }

    [Theory]
    [InlineData("NVIDIA GeForce GTX 1660 Ti")]
    [InlineData("NVIDIA GeForce GTX 1650 SUPER")]
    public void Classify_should_returnMid_when_nvidia16Series(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.Mid);
    }

    [Theory]
    [InlineData("AMD Radeon RX 6900 XT")]
    [InlineData("AMD Radeon RX 6800 XT")]
    [InlineData("AMD Radeon RX 6700 XT")]
    [InlineData("AMD Radeon RX 6600 XT")]
    [InlineData("AMD Radeon RX 6500 XT")]
    public void Classify_should_returnMid_when_amdPrevGen(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.Mid);
    }

    [Theory]
    [InlineData("AMD Radeon RX 5700 XT")]
    [InlineData("AMD Radeon RX 5600 XT")]
    [InlineData("AMD Radeon RX 5500 XT")]
    public void Classify_should_returnMid_when_amd5000Series(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.Mid);
    }

    [Theory]
    [InlineData("Apple M1")]
    [InlineData("Apple M1 Pro")]
    [InlineData("Apple M1 Max")]
    [InlineData("Apple M1 Ultra")]
    [InlineData("Apple M2")]
    [InlineData("Apple M2 Pro")]
    [InlineData("Apple M2 Max")]
    [InlineData("Apple M2 Ultra")]
    public void Classify_should_returnMid_when_appleOlder(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.Mid);
    }

    [Theory]
    [InlineData("Intel(R) Arc(TM) A580")]
    [InlineData("Intel(R) Arc(TM) A380")]
    public void Classify_should_returnMid_when_intelArcLow(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.Mid);
    }

    [Theory]
    [InlineData("Quadro RTX 5000")]
    [InlineData("Quadro P4000")]
    public void Classify_should_returnMid_when_quadro(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.Mid);
    }

    // ========================================================================
    // LOW — Integrated / virtual / very old
    // ========================================================================

    [Theory]
    [InlineData("NVIDIA GeForce GTX 1080 Ti")]
    [InlineData("NVIDIA GeForce GTX 1070")]
    [InlineData("NVIDIA GeForce GTX 1060 6GB")]
    [InlineData("NVIDIA GeForce GTX 1050 Ti")]
    [InlineData("NVIDIA GeForce GT 1030")]
    [InlineData("NVIDIA GeForce GT 730")]
    public void Classify_should_returnLow_when_nvidiaOld(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.Low);
    }

    [Theory]
    [InlineData("AMD Radeon RX 580")]
    [InlineData("AMD Radeon RX 570")]
    [InlineData("AMD Radeon RX 480")]
    [InlineData("AMD Radeon R9 390")]
    [InlineData("AMD Radeon R7 360")]
    [InlineData("AMD Radeon R5 230")]
    public void Classify_should_returnLow_when_amdOld(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.Low);
    }

    [Theory]
    [InlineData("Intel(R) Iris(R) Xe Graphics")]
    [InlineData("Intel(R) UHD Graphics 630")]
    [InlineData("Intel(R) HD Graphics 620")]
    [InlineData("Intel(R) Iris(R) Plus Graphics")]
    [InlineData("Intel(R) Iris(R) Pro Graphics 6200")]
    [InlineData("Iris Xe Graphics")]
    [InlineData("UHD Graphics 770")]
    [InlineData("HD Graphics 530")]
    [InlineData("Iris Plus Graphics 640")]
    public void Classify_should_returnLow_when_intelIntegrated(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.Low);
    }

    [Theory]
    [InlineData("Google SwiftShader")]
    [InlineData("llvmpipe (LLVM 14.0.0, 256 bits)")]
    [InlineData("Mesa DRI Intel")]
    [InlineData("Microsoft Basic Render Driver")]
    [InlineData("Parallels Display Adapter")]
    [InlineData("VMware SVGA 3D")]
    [InlineData("VirtualBox Graphics Adapter")]
    [InlineData("ANGLE (Intel)")]
    public void Classify_should_returnLow_when_virtual(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.Low);
    }

    [Theory]
    [InlineData("Mali-G78")]
    [InlineData("Mali-T880")]
    [InlineData("Adreno (TM) 660")]
    [InlineData("PowerVR Rogue GE8320")]
    public void Classify_should_returnLow_when_mobileIntegrated(string gpu)
    {
        GpuTierReference.Classify(gpu).Should().Be(GpuTier.Low);
    }

    // ========================================================================
    // CASE INSENSITIVITY
    // ========================================================================

    [Fact]
    public void Classify_should_beCaseInsensitive()
    {
        GpuTierReference.Classify("nvidia geforce rtx 4090").Should().Be(GpuTier.High);
        GpuTierReference.Classify("NVIDIA GEFORCE RTX 4090").Should().Be(GpuTier.High);
        GpuTierReference.Classify("apple m1").Should().Be(GpuTier.Mid);
        GpuTierReference.Classify("SWIFTSHADER").Should().Be(GpuTier.Low);
    }

    // ========================================================================
    // TierToString
    // ========================================================================

    [Fact]
    public void TierToString_should_returnCorrectStrings()
    {
        GpuTierReference.TierToString(GpuTier.High).Should().Be("HIGH");
        GpuTierReference.TierToString(GpuTier.Mid).Should().Be("MID");
        GpuTierReference.TierToString(GpuTier.Low).Should().Be("LOW");
        GpuTierReference.TierToString(GpuTier.Unknown).Should().BeNull();
    }
}
