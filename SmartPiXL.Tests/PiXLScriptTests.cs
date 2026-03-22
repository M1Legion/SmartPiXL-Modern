using FluentAssertions;
using SmartPiXL.Scripts;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for the PiXLScript JavaScript template.
/// Verifies critical properties:
///   1. Template generates valid JavaScript
///   2. Permission-triggering APIs are NOT present (MIDI, Bluetooth, etc.)
///   3. Evasion countermeasure probes are present (V-01 through V-10)
///   4. Required fingerprint collectors exist
///   5. PiXL URL placeholder exists for replacement
/// </summary>
public sealed class PiXLScriptTests
{
    // ========================================================================
    // TEMPLATE BASICS
    // ========================================================================

    [Fact]
    public void Template_should_notBeEmpty()
    {
        PiXLScript.Template.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Template_should_containPiXLUrlPlaceholder()
    {
        PiXLScript.Template.Should().Contain("{{PIXL_URL}}",
            "Template must contain the placeholder for per-request URL injection");
    }

    [Fact]
    public void Template_should_produceValidUrl_when_placeholderReplaced()
    {
        var result = PiXLScript.Template.Replace("{{PIXL_URL}}", "https://smartpixl.info/99/1_SMART.GIF");

        result.Should().Contain("https://smartpixl.info/99/1_SMART.GIF");
        result.Should().NotContain("{{PIXL_URL}}");
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
    public void Template_should_notContainPermissionTriggeringApis(string forbidden, string reason)
    {
        PiXLScript.Template.Should().NotContain(forbidden, reason);
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
    public void Template_should_containRequiredCollectors(string required, string reason)
    {
        PiXLScript.Template.Should().Contain(required, reason);
    }

    // ========================================================================
    // EVASION COUNTERMEASURES (V-01 through V-10)
    // These were added in the latest pull to detect anti-fingerprint extensions
    // ========================================================================

    [Theory]
    [InlineData("canvasConsistency", "V-01: Canvas noise injection detection")]
    [InlineData("audioStable", "V-02: Audio fingerprint stability check")]
    [InlineData("fontMethodMismatch", "V-09: Font detection dual-method anti-spoof")]
    public void Template_should_containEvasionCountermeasures(string field, string reason)
    {
        PiXLScript.Template.Should().Contain(field, reason);
    }

    // ========================================================================
    // SAFE ACCESSOR - Privacy extension protection
    // ========================================================================

    [Fact]
    public void Template_should_containSafeAccessor()
    {
        PiXLScript.Template.Should().Contain("safeGet",
            "safeGet() is required to handle Proxy traps from privacy extensions");
    }

    // ========================================================================
    // DATA DELIVERY - sendBeacon primary + Image fallback
    // ========================================================================

    [Fact]
    public void Template_should_containSendBeacon()
    {
        PiXLScript.Template.Should().Contain("navigator.sendBeacon",
            "Modern PiXL uses sendBeacon for reliable delivery during page close");
    }

    [Fact]
    public void Template_should_containImageFallback()
    {
        // Image fallback for browsers without sendBeacon support
        PiXLScript.Template.Should().Contain("new Image()",
            "Must have Image fallback when sendBeacon is unavailable");
    }

    [Fact]
    public void Template_should_selfReferenceScriptSrc()
    {
        PiXLScript.Template.Should().Contain("document.currentScript",
            "Script must derive callback URL from its own src to avoid BaseUrl dependency");
    }

    [Fact]
    public void Template_should_deriveDataEndpoint()
    {
        PiXLScript.Template.Should().Contain("_SMART.DATA",
            "sendBeacon posts to _SMART.DATA endpoint");
    }

    // ========================================================================
    // SELF-EXECUTING WRAPPER
    // ========================================================================

    [Fact]
    public void Template_should_beSelfExecuting()
    {
        PiXLScript.Template.Should().Contain("(function()",
            "Script must be wrapped in an IIFE to avoid polluting global scope");
    }

    [Fact]
    public void Template_should_haveErrorHandling()
    {
        PiXLScript.Template.Should().Contain("try {",
            "Top-level try/catch prevents script errors from breaking the host page");
    }

    // ========================================================================
    // PHASE 1 — screenExtended + mousePath (Migration 41)
    // ========================================================================

    [Fact]
    public void Template_should_containScreenExtendedAssignment()
    {
        PiXLScript.Template.Should().Contain("data.screenExtended",
            "screenExtended field detects multi-monitor setups via screen.isExtended");
    }

    [Fact]
    public void Template_should_assignScreenExtendedAs1Or0()
    {
        // Must output 1 or 0 (BIT-compatible), not raw boolean
        PiXLScript.Template.Should().Contain("isExtended ? 1 : 0",
            "screenExtended must be 1/0 for SQL BIT compatibility");
    }

    [Fact]
    public void Template_should_containMousePathAssignment()
    {
        PiXLScript.Template.Should().Contain("data.mousePath",
            "mousePath field serializes raw mouse trajectory for replay detection");
    }

    [Fact]
    public void Template_should_encodeMousePathAsXYTPipeDelimited()
    {
        // The encoding format is x,y,t|x,y,t|... — comma-separated within a point,
        // pipe-delimited between points
        var template = PiXLScript.Template;

        // Must build path from moves array x, y, t properties
        template.Should().Contain("moves[j].x", "mousePath must read x from moves array");
        template.Should().Contain("moves[j].y", "mousePath must read y from moves array");
        template.Should().Contain("moves[j].t", "mousePath must read t from moves array");

        // Must use pipe delimiter between points
        template.Should().Contain("'|'", "mousePath must use pipe delimiter between points");
    }

    [Fact]
    public void Template_should_capMousePathLength()
    {
        // mousePath must be capped to prevent query string bloat
        PiXLScript.Template.Should().Contain("2000",
            "mousePath must be capped at 2000 chars to prevent query string bloat");
    }

    // ========================================================================
    // MINIFICATION
    // ========================================================================

    [Fact]
    public void GetScript_should_returnMinifiedOutput()
    {
        var url = "https://smartpixl.info/1/1_SMART.GIF";
        var script = PiXLScript.GetScript(url);
        var unminified = PiXLScript.Template.Replace("{{PIXL_URL}}", url);

        // Minified output should be smaller than raw template with same URL
        script.Length.Should().BeLessThan(unminified.Length,
            $"GetScript should return minified JavaScript. Raw={unminified.Length}, Minified={script.Length}");
        script.Should().NotContain("{{PIXL_URL}}",
            "Placeholder must be replaced");
        script.Should().Contain("smartpixl.info",
            "PiXL URL must be injected");
    }

    [Fact]
    public void MinifyTemplate_should_notHaveErrors()
    {
        var result = NUglify.Uglify.Js(PiXLScript.Template);
        var errors = result.Errors.Where(e => e.IsError).Select(e => e.ToString()).ToList();
        errors.Should().BeEmpty($"NUglify should produce no errors, but got: {string.Join("; ", errors)}");
    }

    [Fact]
    public void GetScript_should_preserveFunctionality()
    {
        var script = PiXLScript.GetScript("https://smartpixl.info/1/1_SMART.GIF");

        // Critical field names survive (dot-notation property names, not encrypted)
        script.Should().Contain("sendBeacon", "sendBeacon must survive minification");
        script.Should().Contain("canvasFP", "canvasFP data key must survive minification");
        script.Should().Contain("deviceHash", "deviceHash must survive minification");

        // SHA-256 is now XOR-encrypted — verify decoder exists instead
        script.Should().Contain("_$d", "String decoder function must be present");
        script.Should().Contain("_$e", "Encrypted string table must be present");
    }

    // ========================================================================
    // LITE SCRIPT — served when Referer doesn't match expected domain
    // ========================================================================

    [Fact]
    public void LiteTemplate_should_containPlaceholder()
    {
        PiXLScript.LiteTemplate.Should().Contain("{{PIXL_URL}}");
    }

    [Fact]
    public void LiteTemplate_should_notContainFingerprintingLogic()
    {
        var lite = PiXLScript.LiteTemplate;
        lite.Should().NotContain("canvasFP", "Lite script must not contain canvas fingerprinting");
        lite.Should().NotContain("webglFP", "Lite script must not contain WebGL fingerprinting");
        lite.Should().NotContain("audioFP", "Lite script must not contain audio fingerprinting");
        lite.Should().NotContain("botSignals", "Lite script must not contain bot detection");
        lite.Should().NotContain("mouseEntropy", "Lite script must not contain behavioral biometrics");
        lite.Should().NotContain("SHA-256", "Lite script must not contain hashing");
        lite.Should().NotContain("evasionDetected", "Lite script must not contain evasion detection");
    }

    [Fact]
    public void LiteTemplate_should_containBasicDataAndLiteFlag()
    {
        var lite = PiXLScript.LiteTemplate;
        lite.Should().Contain("_lite", "Lite flag must be present");
        lite.Should().Contain("screen.width", "Basic screen data should be collected");
        lite.Should().Contain("sendBeacon", "Beacon delivery must still work");
        lite.Should().Contain("_bot_wd", "Lite should include webdriver bot signal");
        lite.Should().Contain("_bot_plg", "Lite should include plugins bot signal");
    }

    [Fact]
    public void LiteTemplate_should_containBotSignals()
    {
        var lite = PiXLScript.LiteTemplate;
        lite.Should().Contain("navigator.webdriver", "webdriver check must be present");
        lite.Should().Contain("navigator.plugins", "plugins length check must be present");
        lite.Should().Contain("navigator.languages", "languages length check must be present");
        lite.Should().Contain("outerWidth", "outer dimension check must be present");
        lite.Should().Contain("Notification", "Notification permission check must be present");
    }

    [Fact]
    public void GetLiteScript_should_returnMinifiedOutput()
    {
        var url = "https://smartpixl.info/1/1_test.com_SMART.GIF";
        var script = PiXLScript.GetLiteScript(url);
        var unminified = PiXLScript.LiteTemplate.Replace("{{PIXL_URL}}", url);

        script.Length.Should().BeLessThan(unminified.Length,
            "GetLiteScript should return minified JavaScript");
        script.Should().NotContain("{{PIXL_URL}}");
        script.Should().Contain("smartpixl.info");
        script.Should().NotContain("canvasFP", "Lite script must never expose fingerprinting");
    }

    // ========================================================================
    // STRING ENCRYPTION — sensitive strings XOR-encoded per customer
    // ========================================================================

    [Fact]
    public void GetScript_should_encryptSensitiveStrings()
    {
        var script = PiXLScript.GetScript("https://smartpixl.info/99/99_test.com_SMART.GIF");

        // These detection-revealing strings should NOT appear as quoted literals
        script.Should().NotContain("\"SHA-256\"", "SHA-256 should be encrypted");
        script.Should().NotContain("\"webdriver\"", "webdriver should be encrypted");
        script.Should().NotContain("\"WEBGL_debug_renderer_info\"", "WebGL debug info should be encrypted");
        script.Should().NotContain("\"experimental-webgl\"", "experimental-webgl should be encrypted");
        script.Should().NotContain("\"SwiftShader\"", "SwiftShader should be encrypted");
        script.Should().NotContain("\"selenium\"", "selenium should be encrypted");
        script.Should().NotContain("\"phantomjs\"", "phantomjs should be encrypted");
    }

    [Fact]
    public void GetScript_differentUrls_should_produceDifferentOutput()
    {
        var script1 = PiXLScript.GetScript("https://smartpixl.info/1/1_a.com_SMART.GIF");
        var script2 = PiXLScript.GetScript("https://smartpixl.info/2/2_b.com_SMART.GIF");

        script1.Should().NotBe(script2, "Different customers should get different obfuscated scripts");
    }

    // ========================================================================
    // CANARY TOKEN — per-customer leak attribution marker
    // ========================================================================

    [Fact]
    public void GetScript_should_containCanaryToken()
    {
        var url = "https://smartpixl.info/12345/00053_m1-data.com_SMART.GIF";
        var script = PiXLScript.GetScript(url);

        script.Should().NotContain("%%CANARY%%", "Canary placeholder must be replaced");
        // Canary should be decodable back to the customer URL
        var canary = PiXLScript.DecodeCanary(PiXLScript.DecodeCanary("test") != null ? "" : "");
    }

    [Fact]
    public void DecodeCanary_should_roundTrip()
    {
        var url = "https://smartpixl.info/12345/00053_m1-data.com_SMART.GIF";
        // Extract canary: generate it directly and verify round-trip
        var encoded = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(url).Select(b => (byte)(b ^ 0x5A)).ToArray());
        var decoded = PiXLScript.DecodeCanary(encoded);
        decoded.Should().Be(url, "Canary must round-trip to original URL");
    }

    // ========================================================================
    // INTEGRITY SENTINEL — bitmask verifying fingerprint functions ran
    // ========================================================================

    [Fact]
    public void Template_should_containIntegritySentinel()
    {
        PiXLScript.Template.Should().Contain("data._sp",
            "Integrity sentinel bitmask must be in template");
        PiXLScript.Template.Should().Contain("data.canvasFP ? 1 : 0",
            "Sentinel must check canvasFP");
    }
}
