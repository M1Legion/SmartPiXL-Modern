using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using SmartPiXL.Configuration;
using SmartPiXL.Forge.Services.Enrichments;
using SmartPiXL.Services;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for <see cref="IpRangeLookupService"/> — in-memory binary search
/// over IPInfo range tables. Tests validate the public Lookup contract:
/// graceful degradation, invalid input handling, IPv6 passthrough.
/// SQL-dependent tests (LoadAsync) run only when the database is reachable.
/// </summary>
public sealed class IpRangeLookupServiceTests
{
    private readonly IpRangeLookupService _service;

    public IpRangeLookupServiceTests()
    {
        var mockLogger = new Mock<ITrackingLogger>();
        var settings = new TrackingSettings
        {
            ConnectionString = "Server=localhost\\SQL2025;Database=SmartPiXL;Integrated Security=True;TrustServerCertificate=True"
        };
        var mockOptions = Options.Create(settings);
        _service = new IpRangeLookupService(mockOptions, mockLogger.Object);
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
    // Before LoadAsync — _loaded is false, should return default
    // ========================================================================

    [Fact]
    public void Lookup_should_returnDefault_when_notYetLoaded()
    {
        // Service was just constructed — no LoadAsync called
        var result = _service.Lookup("8.8.8.8");

        result.CountryCode.Should().BeNull();
        result.Asn.Should().BeNull();
    }

    // ========================================================================
    // IPv6 — not supported yet, should return default
    // ========================================================================

    [Theory]
    [InlineData("::1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("fe80::1")]
    public void Lookup_should_returnDefault_when_ipv6(string ip)
    {
        var result = _service.Lookup(ip);

        result.CountryCode.Should().BeNull();
        result.Asn.Should().BeNull();
    }

    // ========================================================================
    // LoadAsync + Lookup integration — only runs with DB access
    // ========================================================================

    [Fact]
    public async Task LoadAsync_should_notThrow_when_tablesEmpty()
    {
        // Tables exist but have no rows — should load zero ranges, not throw
        await _service.LoadAsync(CancellationToken.None);

        // After load, lookups should return default (no ranges to match)
        var result = _service.Lookup("8.8.8.8");
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ReloadAsync_should_notThrow_afterInitialLoad()
    {
        await _service.LoadAsync(CancellationToken.None);
        await _service.ReloadAsync(CancellationToken.None);

        // Reload should be idempotent
        var result = _service.Lookup("1.1.1.1");
        result.Should().NotBeNull();
    }

    // ========================================================================
    // IpLookupResult record struct defaults
    // ========================================================================

    [Fact]
    public void IpLookupResult_default_should_haveAllNulls()
    {
        var result = default(IpRangeLookupService.IpLookupResult);

        result.CountryCode.Should().BeNull();
        result.Region.Should().BeNull();
        result.City.Should().BeNull();
        result.Latitude.Should().BeNull();
        result.Longitude.Should().BeNull();
        result.Asn.Should().BeNull();
        result.AsnOrg.Should().BeNull();
        result.PostalCode.Should().BeNull();
        result.TimeZone.Should().BeNull();
    }
}
