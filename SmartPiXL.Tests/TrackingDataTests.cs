using FluentAssertions;
using SmartPiXL.Models;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for TrackingData record - immutability, default values, equality.
/// </summary>
public sealed class TrackingDataTests
{
    [Fact]
    public void DefaultValues_should_allBeNull()
    {
        var data = new TrackingData();

        data.CompanyID.Should().BeNull();
        data.PiXLID.Should().BeNull();
        data.IPAddress.Should().BeNull();
        data.RequestPath.Should().BeNull();
        data.QueryString.Should().BeNull();
        data.HeadersJson.Should().BeNull();
        data.UserAgent.Should().BeNull();
        data.Referer.Should().BeNull();
    }

    [Fact]
    public void WithInit_should_setAllValues()
    {
        var now = DateTime.UtcNow;
        var data = new TrackingData
        {
            ReceivedAt = now,
            CompanyID = 100,
            PiXLID = 1,
            IPAddress = "8.8.8.8",
            RequestPath = "/100/1_SMART.GIF",
            QueryString = "sw=1920&sh=1080",
            HeadersJson = "{\"User-Agent\":\"Test\"}",
            UserAgent = "Test",
            Referer = "https://example.com"
        };

        data.ReceivedAt.Should().Be(now);
        data.CompanyID.Should().Be(100);
        data.PiXLID.Should().Be(1);
        data.IPAddress.Should().Be("8.8.8.8");
        data.RequestPath.Should().Be("/100/1_SMART.GIF");
        data.QueryString.Should().Be("sw=1920&sh=1080");
    }

    [Fact]
    public void RecordEquality_should_beEqual_when_sameValues()
    {
        var now = DateTime.UtcNow;
        var data1 = new TrackingData { ReceivedAt = now, CompanyID = 1, PiXLID = 1 };
        var data2 = new TrackingData { ReceivedAt = now, CompanyID = 1, PiXLID = 1 };

        data1.Should().Be(data2, "Records with same values should be equal");
    }

    [Fact]
    public void RecordEquality_should_notBeEqual_when_differentValues()
    {
        var data1 = new TrackingData { CompanyID = 1 };
        var data2 = new TrackingData { CompanyID = 2 };

        data1.Should().NotBe(data2);
    }

    [Fact]
    public void RecordWith_should_createModifiedCopy()
    {
        var original = new TrackingData
        {
            CompanyID = 1,
            PiXLID = 1,
            IPAddress = "8.8.8.8"
        };

        var modified = original with { CompanyID = 2 };

        modified.CompanyID.Should().Be(2);
        modified.PiXLID.Should().Be(1, "Unmodified fields should carry over");
        modified.IPAddress.Should().Be("8.8.8.8");
        original.CompanyID.Should().Be(1, "Original should be unchanged");
    }
}
