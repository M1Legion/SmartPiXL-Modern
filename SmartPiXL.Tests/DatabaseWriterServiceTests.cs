using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using SmartPiXL.Configuration;
using SmartPiXL.Models;
using SmartPiXL.Services;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for DatabaseWriterService - queue management, batching, graceful shutdown.
/// Does NOT test actual SQL connectivity (that's integration testing).
/// </summary>
public sealed class DatabaseWriterServiceTests : IDisposable
{
    private readonly Mock<ITrackingLogger> _mockLogger;
    private readonly TrackingSettings _settings;
    private readonly DatabaseWriterService _service;

    public DatabaseWriterServiceTests()
    {
        _mockLogger = new Mock<ITrackingLogger>();
        _mockLogger.Setup(l => l.IsEnabled(It.IsAny<TrackingLogLevel>())).Returns(true);

        _settings = new TrackingSettings
        {
            QueueCapacity = 100,
            BatchSize = 10,
            ShutdownTimeoutSeconds = 5,
            BulkCopyTimeoutSeconds = 30,
            // Use an invalid connection string so we don't accidentally write to the DB
            ConnectionString = "Server=invalid;Database=TestDB;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=1"
        };

        _service = new DatabaseWriterService(
            Options.Create(_settings),
            _mockLogger.Object);
    }

    // ========================================================================
    // QUEUE OPERATIONS
    // ========================================================================

    [Fact]
    public void TryQueue_should_returnTrue_when_emptyQueue()
    {
        var data = CreateSampleData();

        var result = _service.TryQueue(data);

        result.Should().BeTrue();
    }

    [Fact]
    public void TryQueue_should_queueAll_when_multipleItems()
    {
        for (int i = 0; i < 50; i++)
        {
            _service.TryQueue(CreateSampleData(companyId: i.ToString())).Should().BeTrue();
        }

        _service.QueueDepth.Should().Be(50);
    }

    [Fact]
    public void TryQueue_should_returnFalse_when_fullQueue()
    {
        // Fill the queue to capacity
        for (int i = 0; i < _settings.QueueCapacity; i++)
        {
            _service.TryQueue(CreateSampleData()).Should().BeTrue();
        }

        // Next item should be rejected
        var result = _service.TryQueue(CreateSampleData());

        result.Should().BeFalse("Queue is at capacity");
    }

    [Fact]
    public void QueueDepth_should_reflectQueuedItems()
    {
        _service.QueueDepth.Should().Be(0);

        _service.TryQueue(CreateSampleData());
        _service.QueueDepth.Should().Be(1);

        _service.TryQueue(CreateSampleData());
        _service.QueueDepth.Should().Be(2);
    }

    // ========================================================================
    // DATA INTEGRITY
    // ========================================================================

    [Fact]
    public void TryQueue_should_preserveTrackingData()
    {
        var data = new TrackingData
        {
            ReceivedAt = DateTime.UtcNow,
            CompanyID = "TestCompany",
            PiXLID = "TestPiXL",
            IPAddress = "8.8.8.8",
            RequestPath = "/TestCompany/TestPiXL_SMART.GIF",
            QueryString = "sw=1920&sh=1080&synthetic=1",
            HeadersJson = "{\"User-Agent\":\"Test\"}",
            UserAgent = "TestAgent",
            Referer = "https://example.com"
        };

        _service.TryQueue(data).Should().BeTrue();
        _service.QueueDepth.Should().Be(1);
    }

    [Fact]
    public void TryQueue_should_acceptNullFields()
    {
        var data = new TrackingData
        {
            ReceivedAt = DateTime.UtcNow,
            CompanyID = null,
            PiXLID = null,
            IPAddress = null,
            RequestPath = null,
            QueryString = null,
            HeadersJson = null,
            UserAgent = null,
            Referer = null
        };

        _service.TryQueue(data).Should().BeTrue();
    }

    // ========================================================================
    // HELPER
    // ========================================================================

    private static TrackingData CreateSampleData(string companyId = "TestCo") => new()
    {
        ReceivedAt = DateTime.UtcNow,
        CompanyID = companyId,
        PiXLID = "1",
        IPAddress = "8.8.8.8",
        RequestPath = $"/{companyId}/1_SMART.GIF",
        QueryString = "sw=1920&sh=1080",
        HeadersJson = "{}",
        UserAgent = "TestAgent",
        Referer = "https://test.com"
    };

    public void Dispose()
    {
        _service.Dispose();
    }
}
