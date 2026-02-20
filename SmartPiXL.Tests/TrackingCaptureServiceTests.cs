using FluentAssertions;
using Microsoft.AspNetCore.Http;
using SmartPiXL.Services;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for TrackingCaptureService - HTTP request data extraction.
/// Validates path parsing, IP extraction, header capture, and query string handling.
/// </summary>
public sealed class TrackingCaptureServiceTests
{
    private readonly TrackingCaptureService _service = new();

    // ========================================================================
    // PATH PARSING - CompanyID and PiXLID extraction
    // ========================================================================

    [Fact]
    public void CaptureFromRequest_should_extractCompanyAndPiXLID_when_validPath()
    {
        var context = CreateHttpContext("/12345/TestCamp_SMART.GIF", "sw=1920&sh=1080");

        var result = _service.CaptureFromRequest(context.Request);

        result.CompanyID.Should().Be("12345");
        result.PiXLID.Should().Be("TestCamp");
    }

    [Fact]
    public void CaptureFromRequest_should_parseCorrectly_when_numericPiXLID()
    {
        var context = CreateHttpContext("/99/1_SMART.GIF", "sw=1920");

        var result = _service.CaptureFromRequest(context.Request);

        result.CompanyID.Should().Be("99");
        result.PiXLID.Should().Be("1");
    }

    [Fact]
    public void CaptureFromRequest_should_haveNullIds_when_rootPath()
    {
        var context = CreateHttpContext("/", "");

        var result = _service.CaptureFromRequest(context.Request);

        result.CompanyID.Should().BeNull();
        result.PiXLID.Should().BeNull();
    }

    [Fact]
    public void CaptureFromRequest_should_haveNullIds_when_noMatchingPath()
    {
        var context = CreateHttpContext("/health", "");

        var result = _service.CaptureFromRequest(context.Request);

        // /health only has one segment, regex expects client/campaign
        result.CompanyID.Should().BeNull();
        result.PiXLID.Should().BeNull();
    }

    // ========================================================================
    // IP EXTRACTION - Only XFF and RemoteIpAddress are trusted (no CDN)
    // CDN-specific headers (CF-Connecting-IP, True-Client-IP, X-Real-IP)
    // are intentionally IGNORED — they can be spoofed without a CDN.
    // ========================================================================

    [Fact]
    public void CaptureFromRequest_should_ignoreCloudflareIp_whenNoCdnConfigured()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920");
        context.Request.Headers["CF-Connecting-IP"] = "203.0.113.50";
        context.Request.Headers["X-Forwarded-For"] = "10.0.0.1, 172.16.0.1";
        context.Request.Headers["X-Real-IP"] = "192.168.1.1";

        var result = _service.CaptureFromRequest(context.Request);

        // CF-Connecting-IP is ignored; falls through to XFF first entry
        result.IPAddress.Should().Be("10.0.0.1");
    }

    [Fact]
    public void CaptureFromRequest_should_ignoreTrueClientIp_whenNoCdnConfigured()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920");
        context.Request.Headers["True-Client-IP"] = "198.51.100.25";
        context.Request.Headers["X-Forwarded-For"] = "10.0.0.1";

        var result = _service.CaptureFromRequest(context.Request);

        // True-Client-IP is ignored; falls through to XFF
        result.IPAddress.Should().Be("10.0.0.1");
    }

    [Fact]
    public void CaptureFromRequest_should_ignoreXRealIp_whenNoCdnConfigured()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920");
        context.Request.Headers["X-Real-IP"] = "93.184.216.34";
        context.Request.Headers["X-Forwarded-For"] = "10.0.0.1, 172.16.0.1";

        var result = _service.CaptureFromRequest(context.Request);

        // X-Real-IP is ignored; falls through to XFF first entry
        result.IPAddress.Should().Be("10.0.0.1");
    }

    [Fact]
    public void CaptureFromRequest_should_takeFirstIp_when_xffMultiple()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920");
        context.Request.Headers["X-Forwarded-For"] = "151.101.1.140, 10.0.0.1, 172.16.0.1";

        var result = _service.CaptureFromRequest(context.Request);

        result.IPAddress.Should().Be("151.101.1.140");
    }

    [Fact]
    public void CaptureFromRequest_should_parseCorrectly_when_xffSingleIp()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920");
        context.Request.Headers["X-Forwarded-For"] = "8.8.8.8";

        var result = _service.CaptureFromRequest(context.Request);

        result.IPAddress.Should().Be("8.8.8.8");
    }

    [Fact]
    public void CaptureFromRequest_should_fallBackToConnection_when_noProxyHeaders()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920");
        // No proxy headers set - should fall back to connection RemoteIpAddress
        // DefaultHttpContext has null RemoteIpAddress by default

        var result = _service.CaptureFromRequest(context.Request);

        result.IPAddress.Should().BeNull("No proxy headers and default context has null RemoteIpAddress");
    }

    // ========================================================================
    // QUERY STRING HANDLING
    // ========================================================================

    [Fact]
    public void CaptureFromRequest_should_trimLeadingQuestionMark()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920&sh=1080&cores=8");

        var result = _service.CaptureFromRequest(context.Request);

        result.QueryString.Should().NotStartWith("?");
        result.QueryString.Should().Be("sw=1920&sh=1080&cores=8");
    }

    [Fact]
    public void CaptureFromRequest_should_returnEmpty_when_emptyQueryString()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "");

        var result = _service.CaptureFromRequest(context.Request);

        result.QueryString.Should().BeEmpty();
    }

    [Fact]
    public void CaptureFromRequest_should_preserveLargeQueryString()
    {
        // Simulate a real query string with lots of fingerprint params
        var qs = "sw=1920&sh=1080&saw=1920&sah=1040&vw=1903&vh=969&ow=1920&oh=1040" +
                 "&sx=0&sy=0&cd=24&pd=1&ori=landscape-primary&cores=8&mem=8" +
                 "&canvasFP=abc123&webglFP=def456&audioFP=1.234567&audioHash=gh789" +
                 "&fonts=Arial,Verdana,Courier+New&plugins=PDF+Viewer,Chrome+PDF+Viewer" +
                 "&tz=America/New_York&tzo=-300&lang=en-US&langs=en-US,en";

        var context = CreateHttpContext("/1/1_SMART.GIF", qs);

        var result = _service.CaptureFromRequest(context.Request);

        result.QueryString.Should().Contain("canvasFP=abc123");
        result.QueryString.Should().Contain("webglFP=def456");
        result.QueryString.Should().Contain("audioHash=gh789");
    }

    // ========================================================================
    // HEADERS JSON - Should capture known headers, escape special chars
    // ========================================================================

    [Fact]
    public void CaptureFromRequest_should_captureUserAgent_inHeadersJson()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920");
        context.Request.Headers["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

        var result = _service.CaptureFromRequest(context.Request);

        result.HeadersJson.Should().Contain("User-Agent");
        result.HeadersJson.Should().Contain("Mozilla/5.0");
        result.UserAgent.Should().Contain("Mozilla/5.0");
    }

    [Fact]
    public void CaptureFromRequest_should_produceValidJson_inHeadersJson()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920");
        context.Request.Headers["User-Agent"] = "TestAgent";
        context.Request.Headers["Referer"] = "https://example.com";

        var result = _service.CaptureFromRequest(context.Request);

        result.HeadersJson.Should().StartWith("{");
        result.HeadersJson.Should().EndWith("}");
    }

    [Fact]
    public void CaptureFromRequest_should_escapeQuotes_inHeadersJson()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920");
        context.Request.Headers["User-Agent"] = "Test \"Agent\" v1";

        var result = _service.CaptureFromRequest(context.Request);

        // Quotes should be escaped in JSON
        result.HeadersJson.Should().Contain("\\\"Agent\\\"");
    }

    [Fact]
    public void CaptureFromRequest_should_returnEmptyJson_when_noHeaders()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920");
        // No headers set at all

        var result = _service.CaptureFromRequest(context.Request);

        result.HeadersJson.Should().Be("{}");
    }

    [Fact]
    public void CaptureFromRequest_should_captureClientHintHeaders()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920");
        context.Request.Headers["Sec-CH-UA"] = "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\"";
        context.Request.Headers["Sec-CH-UA-Platform"] = "\"Windows\"";
        context.Request.Headers["Sec-CH-UA-Mobile"] = "?0";

        var result = _service.CaptureFromRequest(context.Request);

        result.HeadersJson.Should().Contain("Sec-CH-UA");
        result.HeadersJson.Should().Contain("Sec-CH-UA-Platform");
        result.HeadersJson.Should().Contain("Sec-CH-UA-Mobile");
    }

    // ========================================================================
    // TRUNCATION
    // ========================================================================

    [Fact]
    public void CaptureFromRequest_should_truncateLongUserAgent()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920");
        context.Request.Headers["User-Agent"] = new string('A', 5000);

        var result = _service.CaptureFromRequest(context.Request);

        result.UserAgent!.Length.Should().BeLessThanOrEqualTo(2000);
    }

    [Fact]
    public void CaptureFromRequest_should_truncateLongReferer()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920");
        context.Request.Headers["Referer"] = "https://example.com/" + new string('x', 5000);

        var result = _service.CaptureFromRequest(context.Request);

        result.Referer!.Length.Should().BeLessThanOrEqualTo(2000);
    }

    // ========================================================================
    // TIMESTAMP
    // ========================================================================

    [Fact]
    public void CaptureFromRequest_should_setRecentUtcTimestamp()
    {
        var before = DateTime.UtcNow;
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920");

        var result = _service.CaptureFromRequest(context.Request);
        var after = DateTime.UtcNow;

        result.ReceivedAt.Should().BeOnOrAfter(before);
        result.ReceivedAt.Should().BeOnOrBefore(after);
        result.ReceivedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    // ========================================================================
    // TLS FINGERPRINT HEADERS (V-07)
    // ========================================================================

    [Fact]
    public void CaptureFromRequest_should_captureTlsHeaders_when_present()
    {
        var context = CreateHttpContext("/1/1_SMART.GIF", "sw=1920");
        context.Request.Headers["CF-JA3-Fingerprint"] = "abc123def456";
        context.Request.Headers["X-TLS-Version"] = "TLSv1.3";

        var result = _service.CaptureFromRequest(context.Request);

        result.HeadersJson.Should().Contain("CF-JA3-Fingerprint");
        result.HeadersJson.Should().Contain("X-TLS-Version");
    }

    // ========================================================================
    // HELPER
    // ========================================================================

    private static DefaultHttpContext CreateHttpContext(string path, string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = string.IsNullOrEmpty(queryString)
            ? QueryString.Empty
            : new QueryString("?" + queryString);
        return context;
    }
}
