using FluentAssertions;
using Moq;
using SmartPiXL.Forge.Services.Enrichments;
using SmartPiXL.Services;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for <see cref="UaParsingService"/> — structured UA parsing via
/// UAParser + DeviceDetector.NET. Validates browser, OS, and device classification 
/// for common and edge-case User-Agent strings.
/// </summary>
public sealed class UaParsingServiceTests
{
    private readonly UaParsingService _service;

    public UaParsingServiceTests()
    {
        var mockLogger = new Mock<ITrackingLogger>();
        _service = new UaParsingService(mockLogger.Object);
    }

    // ========================================================================
    // Chrome on Windows
    // ========================================================================

    [Fact]
    public void Parse_should_identifyChromeOnWindows()
    {
        var ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.6099.130 Safari/537.36";
        var result = _service.Parse(ua);

        result.Browser.Should().Be("Chrome");
        result.BrowserVersion.Should().StartWith("120");
        result.OS.Should().Be("Windows");
        result.DeviceType.Should().NotBeNullOrEmpty();
    }

    // ========================================================================
    // Safari on macOS
    // ========================================================================

    [Fact]
    public void Parse_should_identifySafariOnMac()
    {
        var ua = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15";
        var result = _service.Parse(ua);

        result.Browser.Should().Be("Safari");
        result.OS.Should().Be("Mac OS X");
    }

    // ========================================================================
    // Firefox on Linux
    // ========================================================================

    [Fact]
    public void Parse_should_identifyFirefoxOnLinux()
    {
        var ua = "Mozilla/5.0 (X11; Linux x86_64; rv:120.0) Gecko/20100101 Firefox/120.0";
        var result = _service.Parse(ua);

        result.Browser.Should().Be("Firefox");
        result.BrowserVersion.Should().StartWith("120");
        result.OS.Should().Be("Linux");
    }

    // ========================================================================
    // Mobile — Chrome on Android
    // ========================================================================

    [Fact]
    public void Parse_should_identifyMobileDevice()
    {
        var ua = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36";
        var result = _service.Parse(ua);

        result.Browser.Should().Be("Chrome Mobile");
        result.OS.Should().Be("Android");
        result.DeviceType.Should().NotBeNullOrEmpty();
        result.DeviceBrand.Should().Be("Google");
    }

    // ========================================================================
    // Mobile — Safari on iOS
    // ========================================================================

    [Fact]
    public void Parse_should_identifyiPhoneSafari()
    {
        var ua = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";
        var result = _service.Parse(ua);

        result.Browser.Should().Be("Mobile Safari");
        result.OS.Should().Be("iOS");
        result.DeviceBrand.Should().Be("Apple");
    }

    // ========================================================================
    // Edge on Windows
    // ========================================================================

    [Fact]
    public void Parse_should_identifyEdgeOnWindows()
    {
        var ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0";
        var result = _service.Parse(ua);

        result.Browser.Should().Be("Edge");
        result.OS.Should().Be("Windows");
    }

    // ========================================================================
    // Edge cases
    // ========================================================================

    [Fact]
    public void Parse_should_returnDefault_when_nullOrEmpty()
    {
        var result = _service.Parse(null);
        result.Browser.Should().BeNull();
        result.OS.Should().BeNull();

        result = _service.Parse("");
        result.Browser.Should().BeNull();
        result.OS.Should().BeNull();
    }

    [Fact]
    public void Parse_should_handleGarbageUA()
    {
        // Shouldn't throw, just return best-effort or nulls
        var result = _service.Parse("completely-random-garbage-string");
        // Just verifying no exception
        result.Should().NotBeNull();
    }
}
