using FluentAssertions;
using TrackingPixel.Models;

namespace TrackingPixel.Tests;

/// <summary>
/// Tests for IpClassification readonly record struct - value semantics,
/// default values, and the IpType enum.
/// </summary>
public sealed class IpClassificationTests
{
    [Fact]
    public void IpClassification_Constructor_SetsAllProperties()
    {
        var result = new IpClassification(IpType.Public, true, "Routable");

        result.Type.Should().Be(IpType.Public);
        result.ShouldGeolocate.Should().BeTrue();
        result.RangeNote.Should().Be("Routable");
    }

    [Fact]
    public void IpClassification_DefaultRangeNote_IsNull()
    {
        var result = new IpClassification(IpType.Private, false);

        result.RangeNote.Should().BeNull();
    }

    [Fact]
    public void IpClassification_ValueEquality_SameValues_AreEqual()
    {
        var a = new IpClassification(IpType.Public, true, "Test");
        var b = new IpClassification(IpType.Public, true, "Test");

        a.Should().Be(b, "Value types with same values should be equal");
    }

    [Fact]
    public void IpClassification_ValueEquality_DifferentType_NotEqual()
    {
        var a = new IpClassification(IpType.Public, true);
        var b = new IpClassification(IpType.Private, false);

        a.Should().NotBe(b);
    }

    [Theory]
    [InlineData(IpType.Public, (byte)0)]
    [InlineData(IpType.Private, (byte)1)]
    [InlineData(IpType.Loopback, (byte)2)]
    [InlineData(IpType.LinkLocal, (byte)3)]
    [InlineData(IpType.CGNAT, (byte)4)]
    [InlineData(IpType.Documentation, (byte)5)]
    [InlineData(IpType.Multicast, (byte)6)]
    [InlineData(IpType.Reserved, (byte)7)]
    [InlineData(IpType.Broadcast, (byte)8)]
    [InlineData(IpType.Unspecified, (byte)9)]
    [InlineData(IpType.Benchmark, (byte)10)]
    [InlineData(IpType.Invalid, (byte)255)]
    public void IpType_EnumValues_AreCorrect(IpType type, byte expected)
    {
        ((byte)type).Should().Be(expected);
    }

    [Fact]
    public void IpType_AllValuesAccountedFor()
    {
        // Ensure we have all expected enum values
        var values = Enum.GetValues<IpType>();
        values.Should().HaveCount(12, "12 IP type classifications expected");
    }
}
