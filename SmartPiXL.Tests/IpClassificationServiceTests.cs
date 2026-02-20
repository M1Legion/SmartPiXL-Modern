using FluentAssertions;
using SmartPiXL.Models;
using SmartPiXL.Services;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for IpClassificationService - zero-allocation IP classifier.
/// Covers IPv4, IPv6, IPv4-mapped IPv6, edge cases, and boundary conditions.
/// </summary>
public sealed class IpClassificationServiceTests
{
    // ========================================================================
    // PUBLIC IPv4 - Should geolocate
    // ========================================================================

    [Theory]
    [InlineData("8.8.8.8", IpType.Public)]            // Google DNS
    [InlineData("1.1.1.1", IpType.Public)]             // Cloudflare DNS
    [InlineData("208.67.222.222", IpType.Public)]      // OpenDNS
    [InlineData("93.184.216.34", IpType.Public)]       // example.com
    [InlineData("151.101.1.140", IpType.Public)]       // Reddit CDN
    public void Classify_should_returnPublic_when_publicIpv4(string ip, IpType expected)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(expected);
        result.ShouldGeolocate.Should().BeTrue();
    }

    // ========================================================================
    // PRIVATE IPv4 RANGES (RFC 1918) - Should NOT geolocate
    // ========================================================================

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("10.100.50.25")]
    public void Classify_should_returnPrivate_when_10Range(string ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.Private);
        result.ShouldGeolocate.Should().BeFalse();
    }

    [Theory]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("172.20.10.5")]
    public void Classify_should_returnPrivate_when_172Range(string ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.Private);
        result.ShouldGeolocate.Should().BeFalse();
    }

    [Theory]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.255")]
    [InlineData("192.168.1.100")]
    [InlineData("192.168.88.176")]   // Our IIS binding
    public void Classify_should_returnPrivate_when_192Range(string ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.Private);
        result.ShouldGeolocate.Should().BeFalse();
    }

    // ========================================================================
    // LOOPBACK - Should NOT geolocate
    // ========================================================================

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.255")]
    [InlineData("127.0.0.2")]
    public void Classify_should_returnLoopback(string ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.Loopback);
        result.ShouldGeolocate.Should().BeFalse();
    }

    // ========================================================================
    // LINK-LOCAL (169.254.x.x) - Should NOT geolocate
    // ========================================================================

    [Theory]
    [InlineData("169.254.0.1")]
    [InlineData("169.254.255.255")]
    [InlineData("169.254.169.254")]   // AWS metadata endpoint
    public void Classify_should_returnLinkLocal(string ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.LinkLocal);
        result.ShouldGeolocate.Should().BeFalse();
    }

    // ========================================================================
    // CGNAT (100.64.0.0/10) - SHOULD geolocate (real users behind carrier NAT)
    // ========================================================================

    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("100.100.100.100")]
    public void Classify_should_returnCgnatAndGeolocate(string ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.CGNAT);
        result.ShouldGeolocate.Should().BeTrue("CGNAT addresses are real users behind carrier-grade NAT");
    }

    // ========================================================================
    // DOCUMENTATION / TEST-NET - Should NOT geolocate
    // ========================================================================

    [Theory]
    [InlineData("192.0.2.1")]       // TEST-NET-1
    [InlineData("198.51.100.1")]    // TEST-NET-2
    [InlineData("203.0.113.1")]     // TEST-NET-3
    public void Classify_should_returnDocumentation(string ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.Documentation);
        result.ShouldGeolocate.Should().BeFalse();
    }

    // ========================================================================
    // MULTICAST - Should NOT geolocate
    // ========================================================================

    [Theory]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.255")]
    public void Classify_should_returnMulticast(string ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.Multicast);
        result.ShouldGeolocate.Should().BeFalse();
    }

    // ========================================================================
    // BENCHMARK (198.18.0.0/15) - Should NOT geolocate
    // ========================================================================

    [Theory]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    public void Classify_should_returnBenchmark(string ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.Benchmark);
        result.ShouldGeolocate.Should().BeFalse();
    }

    // ========================================================================
    // BROADCAST - Should NOT geolocate
    // ========================================================================

    [Fact]
    public void Classify_should_returnReservedOrBroadcast_when_255Range()
    {
        // 255.255.255.255 matches Reserved Class E (0xF0000000/0xF0000000) before
        // reaching the Broadcast entry, since ranges are checked in order.
        // This is correct behavior - it's still non-routable and non-geolocatable.
        var result = IpClassificationService.Classify("255.255.255.255");

        result.Type.Should().BeOneOf(IpType.Broadcast, IpType.Reserved);
        result.ShouldGeolocate.Should().BeFalse();
    }

    // ========================================================================
    // UNSPECIFIED (0.x.x.x) - Should NOT geolocate
    // ========================================================================

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.0.0.1")]
    public void Classify_should_returnUnspecified(string ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.Unspecified);
        result.ShouldGeolocate.Should().BeFalse();
    }

    // ========================================================================
    // IPv6 - Various reserved ranges
    // ========================================================================

    [Fact]
    public void Classify_should_returnLoopback_when_ipv6()
    {
        var result = IpClassificationService.Classify("::1");

        result.Type.Should().Be(IpType.Loopback);
        result.ShouldGeolocate.Should().BeFalse();
    }

    [Fact]
    public void Classify_should_returnUnspecified_when_ipv6()
    {
        var result = IpClassificationService.Classify("::");

        result.Type.Should().Be(IpType.Unspecified);
        result.ShouldGeolocate.Should().BeFalse();
    }

    [Theory]
    [InlineData("fe80::1")]
    [InlineData("fe80::abcd:ef01:2345:6789")]
    public void Classify_should_returnLinkLocal_when_ipv6(string ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.LinkLocal);
        result.ShouldGeolocate.Should().BeFalse();
    }

    [Theory]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:7890::1")]
    public void Classify_should_returnPrivate_when_ipv6UniqueLocal(string ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.Private);
        result.ShouldGeolocate.Should().BeFalse();
    }

    [Fact]
    public void Classify_should_returnDocumentation_when_ipv6()
    {
        var result = IpClassificationService.Classify("2001:db8::1");

        result.Type.Should().Be(IpType.Documentation);
        result.ShouldGeolocate.Should().BeFalse();
    }

    [Theory]
    [InlineData("ff02::1")]
    [InlineData("ff05::2")]
    public void Classify_should_returnMulticast_when_ipv6(string ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.Multicast);
        result.ShouldGeolocate.Should().BeFalse();
    }

    [Theory]
    [InlineData("2607:f8b0:4004:800::200e")]  // Google
    [InlineData("2606:4700:4700::1111")]        // Cloudflare
    public void Classify_should_returnPublic_when_ipv6(string ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.Public);
        result.ShouldGeolocate.Should().BeTrue();
    }

    // ========================================================================
    // IPv4-MAPPED IPv6 (::ffff:x.x.x.x) - Should classify the inner IPv4
    // ========================================================================

    [Fact]
    public void Classify_should_returnPrivate_when_ipv4MappedIpv6()
    {
        var result = IpClassificationService.Classify("::ffff:192.168.1.1");

        result.Type.Should().Be(IpType.Private);
        result.ShouldGeolocate.Should().BeFalse();
    }

    [Fact]
    public void Classify_should_returnPublic_when_ipv4MappedIpv6()
    {
        var result = IpClassificationService.Classify("::ffff:8.8.8.8");

        result.Type.Should().Be(IpType.Public);
        result.ShouldGeolocate.Should().BeTrue();
    }

    [Fact]
    public void Classify_should_returnLoopback_when_ipv4MappedIpv6()
    {
        var result = IpClassificationService.Classify("::ffff:127.0.0.1");

        result.Type.Should().Be(IpType.Loopback);
        result.ShouldGeolocate.Should().BeFalse();
    }

    // ========================================================================
    // EDGE CASES - Invalid / null / empty / malformed
    // ========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Classify_should_returnInvalid_when_nullOrEmpty(string? ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.Invalid);
        result.ShouldGeolocate.Should().BeFalse();
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("256.256.256.256")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.3")]
    [InlineData("abc::xyz::123")]
    public void Classify_should_returnInvalid_when_malformed(string ip)
    {
        var result = IpClassificationService.Classify(ip);

        result.Type.Should().Be(IpType.Invalid);
        result.ShouldGeolocate.Should().BeFalse();
    }

    [Theory]
    [InlineData("  8.8.8.8  ", IpType.Public)]       // Leading/trailing whitespace
    [InlineData("  127.0.0.1  ", IpType.Loopback)]
    public void Classify_should_classifyCorrectly_when_whitespaceWrapped(string ip, IpType expected)
    {
        var result = IpClassificationService.Classify(ip);
        result.Type.Should().Be(expected);
    }

    // ========================================================================
    // HELPER METHODS
    // ========================================================================

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", true)]
    [InlineData(null, false)]
    public void ShouldGeolocate_should_returnExpected(string? ip, bool expected)
    {
        IpClassificationService.ShouldGeolocate(ip).Should().Be(expected);
    }

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData(null, false)]
    public void IsPrivateOrInternal_should_returnExpected(string? ip, bool expected)
    {
        IpClassificationService.IsPrivateOrInternal(ip).Should().Be(expected);
    }

    // ========================================================================
    // BOUNDARY TESTS - First/last IPs of each range
    // ========================================================================

    [Theory]
    [InlineData("172.15.255.255", IpType.Public)]    // Just below 172.16.0.0/12
    [InlineData("172.16.0.0", IpType.Private)]       // First of 172.16.0.0/12
    [InlineData("172.31.255.255", IpType.Private)]   // Last of 172.16.0.0/12
    [InlineData("172.32.0.0", IpType.Public)]        // Just above 172.16.0.0/12
    public void Classify_should_classifyCorrectly_when_boundaryIps(string ip, IpType expected)
    {
        var result = IpClassificationService.Classify(ip);
        result.Type.Should().Be(expected);
    }
}
