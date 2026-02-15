using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using TrackingPixel.Services;

namespace TrackingPixel.Tests;

/// <summary>
/// Tests for FingerprintStabilityService (V-05) - anti-detect browser detection.
/// Validates that consistent fingerprints from the same IP are marked stable,
/// and varying fingerprints are flagged as suspicious.
/// </summary>
public sealed class FingerprintStabilityServiceTests : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly FingerprintStabilityService _service;

    public FingerprintStabilityServiceTests()
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
        _service = new FingerprintStabilityService(_cache);
    }

    // ========================================================================
    // FIRST OBSERVATION - Always stable (no history)
    // ========================================================================

    [Fact]
    public void RecordAndCheck_should_beStable_when_firstObservation()
    {
        var result = _service.RecordAndCheck("1.2.3.4", "canvas1", "webgl1", "audio1");

        result.IsStable.Should().BeTrue();
        result.UniqueFingerprints.Should().Be(1);
        result.ObservationCount.Should().Be(1);
        result.SuspiciousVariation.Should().BeFalse();
    }

    // ========================================================================
    // CONSISTENT FINGERPRINT - Same user, same device
    // ========================================================================

    [Fact]
    public void RecordAndCheck_should_remainStable_when_sameFingerprintRepeated()
    {
        for (int i = 0; i < 10; i++)
        {
            var result = _service.RecordAndCheck("1.2.3.4", "canvas1", "webgl1", "audio1");

            result.IsStable.Should().BeTrue();
            result.UniqueFingerprints.Should().Be(1);
            result.SuspiciousVariation.Should().BeFalse();
        }
    }

    // ========================================================================
    // TWO FINGERPRINTS - Could be incognito + regular, not yet suspicious
    // ========================================================================

    [Fact]
    public void RecordAndCheck_should_notBeSuspicious_when_twoFingerprints()
    {
        _service.RecordAndCheck("1.2.3.4", "canvas1", "webgl1", "audio1");
        var result = _service.RecordAndCheck("1.2.3.4", "canvas2", "webgl2", "audio2");

        result.IsStable.Should().BeFalse("fingerprint changed");
        result.UniqueFingerprints.Should().Be(2);
        result.SuspiciousVariation.Should().BeFalse("only 2 unique, need 3+ for suspicious");
    }

    // ========================================================================
    // ANTI-DETECT BROWSER PATTERN - 3+ unique fingerprints = suspicious
    // ========================================================================

    [Fact]
    public void RecordAndCheck_should_beSuspicious_when_threeOrMoreUnique()
    {
        // Simulate anti-detect browser: each "profile" has unique fingerprints from same IP
        _service.RecordAndCheck("1.2.3.4", "canvas1", "webgl1", "audio1");
        _service.RecordAndCheck("1.2.3.4", "canvas2", "webgl2", "audio2");
        _service.RecordAndCheck("1.2.3.4", "canvas3", "webgl3", "audio3");
        var result = _service.RecordAndCheck("1.2.3.4", "canvas4", "webgl4", "audio4");

        result.UniqueFingerprints.Should().BeGreaterThanOrEqualTo(3);
        result.ObservationCount.Should().BeGreaterThanOrEqualTo(4);
        result.SuspiciousVariation.Should().BeTrue(
            "3+ unique fingerprints from same IP in 24h indicates anti-detect browser");
    }

    // ========================================================================
    // DIFFERENT IPS - Should track independently
    // ========================================================================

    [Fact]
    public void RecordAndCheck_should_trackIndependently_when_differentIps()
    {
        _service.RecordAndCheck("1.2.3.4", "canvas1", "webgl1", "audio1");
        _service.RecordAndCheck("5.6.7.8", "canvas2", "webgl2", "audio2");

        var result1 = _service.RecordAndCheck("1.2.3.4", "canvas1", "webgl1", "audio1");
        var result2 = _service.RecordAndCheck("5.6.7.8", "canvas2", "webgl2", "audio2");

        result1.IsStable.Should().BeTrue();
        result1.UniqueFingerprints.Should().Be(1);

        result2.IsStable.Should().BeTrue();
        result2.UniqueFingerprints.Should().Be(1);
    }

    // ========================================================================
    // NULL FINGERPRINT COMPONENTS - Should handle gracefully
    // ========================================================================

    [Fact]
    public void RecordAndCheck_should_handleGracefully_when_nullComponents()
    {
        var result = _service.RecordAndCheck("1.2.3.4", null, null, null);

        result.IsStable.Should().BeTrue();
        result.UniqueFingerprints.Should().Be(1);
    }

    [Fact]
    public void RecordAndCheck_should_trackCorrectly_when_mixedNullAndValue()
    {
        _service.RecordAndCheck("1.2.3.4", "canvas1", null, null);
        var result = _service.RecordAndCheck("1.2.3.4", "canvas1", "webgl1", null);

        // Different composite string, so it's a new unique fingerprint
        result.IsStable.Should().BeFalse();
        result.UniqueFingerprints.Should().Be(2);
    }

    // ========================================================================
    // THREAD SAFETY - Multiple concurrent observations
    // ========================================================================

    [Fact]
    public async Task RecordAndCheck_should_notThrow_when_concurrentObservations()
    {
        var tasks = Enumerable.Range(0, 100).Select(i =>
            Task.Run(() => _service.RecordAndCheck(
                "1.2.3.4",
                $"canvas{i % 5}",
                $"webgl{i % 5}",
                $"audio{i % 5}")));

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r =>
        {
            r.ObservationCount.Should().BeGreaterThan(0);
            r.UniqueFingerprints.Should().BeGreaterThan(0);
        });
    }

    public void Dispose()
    {
        _cache.Dispose();
    }
}
