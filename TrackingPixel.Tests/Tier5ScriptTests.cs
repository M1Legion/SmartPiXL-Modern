using FluentAssertions;
using TrackingPixel.Scripts;

namespace TrackingPixel.Tests;

/// <summary>
/// Tests for the Tier5Script JavaScript template.
/// Verifies critical properties:
///   1. Template generates valid JavaScript
///   2. Permission-triggering APIs are NOT present (MIDI, Bluetooth, etc.)
///   3. Evasion countermeasure probes are present (V-01 through V-10)
///   4. Required fingerprint collectors exist
///   5. Pixel URL placeholder exists for replacement
/// </summary>
public sealed class Tier5ScriptTests
{
    // ========================================================================
    // TEMPLATE BASICS
    // ========================================================================

    [Fact]
    public void Template_ShouldNotBeEmpty()
    {
        Tier5Script.Template.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Template_ContainsPixelUrlPlaceholder()
    {
        Tier5Script.Template.Should().Contain("{{PIXEL_URL}}",
            "Template must contain the placeholder for per-request URL injection");
    }

    [Fact]
    public void Template_ReplacePlaceholder_ProducesValidUrl()
    {
        var result = Tier5Script.Template.Replace("{{PIXEL_URL}}", "https://smartpixl.info/99/1_SMART.GIF");

        result.Should().Contain("https://smartpixl.info/99/1_SMART.GIF");
        result.Should().NotContain("{{PIXEL_URL}}");
    }

    // ========================================================================
    // PERMISSION-TRIGGERING APIs - MUST NOT BE PRESENT
    // These caused the MIDI popup (screenshot evidence) and similar prompts.
    // ========================================================================

    [Theory]
    [InlineData("requestMIDIAccess", "MIDI access triggers 'control and reprogram your MIDI devices' popup")]
    [InlineData("data.midi", "MIDI property check removed to prevent permission popup")]
    [InlineData("data.bluetooth", "Bluetooth API triggers permission prompt")]
    [InlineData("data.usb", "USB API triggers permission prompt")]
    [InlineData("data.serial", "Serial API triggers permission prompt")]
    [InlineData("data.hid", "HID API triggers permission prompt")]
    [InlineData("data.xr", "WebXR removed - low entropy, flags privacy extensions")]
    [InlineData("data.share", "Web Share removed - low entropy")]
    [InlineData("data.credentials", "Credentials API removed - low entropy")]
    [InlineData("data.geolocation", "Geolocation removed - triggers permission prompt")]
    [InlineData("data.notifications", "Notifications removed - triggers permission prompt")]
    [InlineData("data.push", "Push API removed - associated with notification permission")]
    [InlineData("data.payment", "Payment Request removed - triggers UI")]
    [InlineData("data.speechRecog", "Speech recognition removed - triggers permission prompt")]
    public void Template_ShouldNotContain_PermissionTriggeringApis(string forbidden, string reason)
    {
        Tier5Script.Template.Should().NotContain(forbidden, reason);
    }

    // ========================================================================
    // REQUIRED FINGERPRINT COLLECTORS
    // ========================================================================

    [Theory]
    [InlineData("canvasFP", "Canvas fingerprint is a primary identification vector")]
    [InlineData("webglFP", "WebGL fingerprint provides GPU-based identification")]
    [InlineData("audioFP", "Audio fingerprint provides hardware-based identification")]
    [InlineData("audioHash", "Audio hash provides additional entropy")]
    [InlineData("fonts", "Font enumeration is a key fingerprint component")]
    [InlineData("screen", "Screen dimensions are basic device info")]
    [InlineData("data.cores", "CPU core count helps identify device")]
    [InlineData("data.mem", "Device memory helps identify device")]
    [InlineData("data.tz", "Timezone is critical for geo-inference")]
    [InlineData("data.lang", "Language helps identify user")]
    public void Template_ShouldContain_RequiredCollectors(string required, string reason)
    {
        Tier5Script.Template.Should().Contain(required, reason);
    }

    // ========================================================================
    // EVASION COUNTERMEASURES (V-01 through V-10)
    // These were added in the latest pull to detect anti-fingerprint extensions
    // ========================================================================

    [Theory]
    [InlineData("canvasConsistency", "V-01: Canvas noise injection detection")]
    [InlineData("audioStable", "V-02: Audio fingerprint stability check")]
    [InlineData("fontMethodMismatch", "V-09: Font detection dual-method anti-spoof")]
    public void Template_ShouldContain_EvasionCountermeasures(string field, string reason)
    {
        Tier5Script.Template.Should().Contain(field, reason);
    }

    // ========================================================================
    // SAFE ACCESSOR - Privacy extension protection
    // ========================================================================

    [Fact]
    public void Template_ContainsSafeAccessor()
    {
        Tier5Script.Template.Should().Contain("safeGet",
            "safeGet() is required to handle Proxy traps from privacy extensions");
    }

    // ========================================================================
    // GIF REQUEST - Must fire the pixel
    // ========================================================================

    [Fact]
    public void Template_CreatesImageRequest()
    {
        // The script should create an Image and set src to fire the pixel
        Tier5Script.Template.Should().Contain("new Image()",
            "Must create Image element to fire pixel request");
    }

    [Fact]
    public void Template_SendsDataAsQueryString()
    {
        // Data should be sent as query string params on the GIF URL
        Tier5Script.Template.Should().Contain(".src =",
            "Must set Image src to fire the tracking request");
    }

    // ========================================================================
    // SELF-EXECUTING WRAPPER
    // ========================================================================

    [Fact]
    public void Template_IsSelfExecuting()
    {
        Tier5Script.Template.Should().Contain("(function()",
            "Script must be wrapped in an IIFE to avoid polluting global scope");
    }

    [Fact]
    public void Template_HasErrorHandling()
    {
        Tier5Script.Template.Should().Contain("try {",
            "Top-level try/catch prevents script errors from breaking the host page");
    }
}
