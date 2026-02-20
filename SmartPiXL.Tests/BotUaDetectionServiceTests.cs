using FluentAssertions;
using Moq;
using SmartPiXL.Forge.Services.Enrichments;
using SmartPiXL.Services;

namespace SmartPiXL.Tests;

/// <summary>
/// Tests for <see cref="BotUaDetectionService"/> — known bot/crawler UA detection.
/// Validates that known bot UAs are flagged and human UAs pass clean.
/// </summary>
public sealed class BotUaDetectionServiceTests
{
    private readonly BotUaDetectionService _service;

    public BotUaDetectionServiceTests()
    {
        var mockLogger = new Mock<ITrackingLogger>();
        _service = new BotUaDetectionService(mockLogger.Object);
    }

    // ========================================================================
    // Known Bots — should be detected
    // ========================================================================

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)")]
    [InlineData("Mozilla/5.0 (compatible; YandexBot/3.0; +http://yandex.com/bots)")]
    [InlineData("Mozilla/5.0 (compatible; Baiduspider/2.0; +http://www.baidu.com/search/spider.html)")]
    public void Check_should_detectKnownBot(string botUa)
    {
        var (isCrawler, botName) = _service.Check(botUa);

        isCrawler.Should().BeTrue();
        botName.Should().NotBeNullOrEmpty();
    }

    // ========================================================================
    // Human browsers — should NOT be flagged
    // ========================================================================

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:120.0) Gecko/20100101 Firefox/120.0")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36")]
    public void Check_should_notFlagHumanBrowser(string humanUa)
    {
        var (isCrawler, _) = _service.Check(humanUa);

        isCrawler.Should().BeFalse();
    }

    // ========================================================================
    // Edge cases
    // ========================================================================

    [Fact]
    public void Check_should_returnFalse_when_nullOrEmpty()
    {
        _service.Check(null).IsCrawler.Should().BeFalse();
        _service.Check("").IsCrawler.Should().BeFalse();
    }
}
