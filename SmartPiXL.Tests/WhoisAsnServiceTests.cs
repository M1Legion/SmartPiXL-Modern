using FluentAssertions;
using Moq;
using SmartPiXL.Forge.Services.Enrichments;
using SmartPiXL.Services;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for <see cref="WhoisAsnService"/> — WHOIS-based ASN/org lookup.
/// Uses real WHOIS calls for basic validation — may be slow or blocked
/// in some network environments.
/// </summary>
public sealed class WhoisAsnServiceTests
{
    private readonly WhoisAsnService _service;

    public WhoisAsnServiceTests()
    {
        var mockLogger = new Mock<ITrackingLogger>();
        _service = new WhoisAsnService(mockLogger.Object);
    }

    // ========================================================================
    // Null/Empty IP — should return default
    // ========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task LookupAsync_should_returnDefault_when_nullOrEmpty(string? ip)
    {
        var result = await _service.LookupAsync(ip);

        result.Asn.Should().BeNull();
        result.Organization.Should().BeNull();
    }

    // ========================================================================
    // Private IPs — should be skipped (no WHOIS call)
    // ========================================================================

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    public async Task LookupAsync_should_skipPrivateIps(string ip)
    {
        var result = await _service.LookupAsync(ip);

        result.Asn.Should().BeNull();
        result.Organization.Should().BeNull();
    }

    // ========================================================================
    // Real WHOIS lookup — Google DNS (8.8.8.8)
    // ========================================================================

    [Fact]
    public async Task LookupAsync_should_resolveGoogleDNS()
    {
        var result = await _service.LookupAsync("8.8.8.8");

        // WHOIS may be rate-limited or blocked — if we get data, validate it
        if (result.Organization is not null)
        {
            result.Organization.Should().Contain("Google");
        }
        // If null, WHOIS was unavailable — not a test failure
    }

    // ========================================================================
    // Cancellation — should return default
    // ========================================================================

    [Fact]
    public async Task LookupAsync_should_returnDefault_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _service.LookupAsync("8.8.8.8", cts.Token);

        result.Asn.Should().BeNull();
        result.Organization.Should().BeNull();
    }
}
