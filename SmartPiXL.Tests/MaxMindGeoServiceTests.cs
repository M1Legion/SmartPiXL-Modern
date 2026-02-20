using FluentAssertions;
using Moq;
using SmartPiXL.Forge.Services.Enrichments;
using SmartPiXL.Services;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for <see cref="MaxMindGeoService"/> — offline MaxMind GeoIP2 lookups.
/// Since .mmdb files may not be present in the test environment, tests validate
/// graceful degradation (null results when files are missing) and the service's
/// contract (correct struct defaults, no exceptions on invalid input).
/// </summary>
public sealed class MaxMindGeoServiceTests : IDisposable
{
    private readonly MaxMindGeoService _service;

    public MaxMindGeoServiceTests()
    {
        var mockLogger = new Mock<ITrackingLogger>();
        _service = new MaxMindGeoService(mockLogger.Object);
    }

    // ========================================================================
    // Null/Empty/Invalid IP — should return default
    // ========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("999.999.999.999")]
    public void Lookup_should_returnDefault_when_invalidInput(string? ip)
    {
        var result = _service.Lookup(ip);

        result.CountryCode.Should().BeNull();
        result.Region.Should().BeNull();
        result.City.Should().BeNull();
        result.Latitude.Should().BeNull();
        result.Longitude.Should().BeNull();
        result.Asn.Should().BeNull();
        result.AsnOrg.Should().BeNull();
    }

    // ========================================================================
    // Graceful degradation — no .mmdb files
    // ========================================================================

    [Fact]
    public void Lookup_should_notThrow_when_dbFilesMissing()
    {
        // In CI/test environments, .mmdb files won't exist.
        // Service should return default (empty) results, not throw.
        var result = _service.Lookup("8.8.8.8");

        // Either we get real data (if .mmdb files exist) or null (if not)
        // Either way, no exception is the key assertion
        result.Should().NotBeNull();
    }

    // ========================================================================
    // Known IP — If .mmdb files exist, should return data
    // ========================================================================

    [Fact]
    public void Lookup_should_returnCountrCode_when_dbFilesPresent()
    {
        var result = _service.Lookup("8.8.8.8");

        // If MaxMind DBs are installed, Google DNS should resolve to US
        if (result.CountryCode is not null)
        {
            result.CountryCode.Should().Be("US");
        }
        // If DBs not installed, just ensure no exception
    }

    [Fact]
    public void Lookup_should_returnASN_when_dbFilesPresent()
    {
        var result = _service.Lookup("8.8.8.8");

        // Google DNS ASN is AS15169
        if (result.Asn.HasValue)
        {
            result.Asn.Value.Should().Be(15169);
            result.AsnOrg.Should().Contain("Google");
        }
    }

    // ========================================================================
    // Private IP — should return empty (not in any MaxMind database)
    // ========================================================================

    [Fact]
    public void Lookup_should_returnDefault_when_privateIp()
    {
        var result = _service.Lookup("192.168.1.1");

        result.CountryCode.Should().BeNull();
        result.Asn.Should().BeNull();
    }

    // ========================================================================
    // Localhost — should return empty
    // ========================================================================

    [Fact]
    public void Lookup_should_returnDefault_when_localhost()
    {
        var result = _service.Lookup("127.0.0.1");

        result.CountryCode.Should().BeNull();
    }

    public void Dispose() => _service.Dispose();
}
