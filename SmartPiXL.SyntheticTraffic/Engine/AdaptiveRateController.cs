using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace SmartPiXL.SyntheticTraffic.Engine;

// ============================================================================
// ADAPTIVE RATE CONTROLLER — AIMD (Additive Increase / Multiplicative Decrease)
//
// Linear ramp with multiplicative back-off:
//   1. RAMP UP:  Rate increases +50/s every probe interval (gentle, no overshoot)
//   2. BACK-OFF: Rate halves when queue exceeds ceiling or throughput collapses
//
// No exponential slow-start — that overshoots Edge capacity and causes IIS
// thread-pool starvation under sustained load. Linear ramp finds the
// sustainable rate without overwhelming the pipeline.
// ============================================================================

internal sealed class AdaptiveRateController : IDisposable
{
    private readonly TrafficSettings _settings;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();

    private int _currentRate;
    private int _peakRate;
    private int _backOffCount;

    // Latest health probe results
    private int _lastQueueDepth;
    private bool _lastPipeConnected;
    private string _lastQueueStatus = "unknown";

    // Failure-aware back-off: tracks success/fail deltas between probes
    private Func<(long success, long failed)>? _throughputProbe;
    private long _prevSuccess;
    private long _prevFailed;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private const int WarmupGraceSeconds = 30; // skip stall/failure detection during JIT warmup

    private Task? _probeTask;

    /// <summary>Current send rate (hits/sec).</summary>
    public int CurrentRate => _currentRate;

    /// <summary>Peak rate achieved before any back-off.</summary>
    public int PeakRate => _peakRate;

    /// <summary>Number of back-off events.</summary>
    public int BackOffCount => _backOffCount;

    /// <summary>Last observed queue depth from Edge health endpoint.</summary>
    public int LastQueueDepth => _lastQueueDepth;

    /// <summary>Whether the Edge→Forge pipe is connected.</summary>
    public bool PipeConnected => _lastPipeConnected;

    /// <summary>Queue status string from last health probe.</summary>
    public string QueueStatus => _lastQueueStatus;

    public AdaptiveRateController(TrafficSettings settings)
    {
        _settings = settings;
        _currentRate = settings.InitialRatePerSecond;

        _http = new HttpClient
        {
            BaseAddress = new Uri(settings.TargetUrl),
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    /// <summary>
    /// Provide a callback for the controller to read actual success/fail counts
    /// from the HttpTrafficWriter. Called every probe interval to detect failures.
    /// </summary>
    public void AttachThroughputProbe(Func<(long success, long failed)> probe)
    {
        _throughputProbe = probe;
    }

    /// <summary>
    /// Capture baseline metrics from the Edge health endpoint.
    /// Call before starting traffic generation.
    /// </summary>
    public async Task<HealthSnapshot?> CaptureBaseline()
    {
        try
        {
            return await ProbeHealth();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [WARN] Could not capture baseline: {ex.Message}");
            return null;
        }
    }

    /// <summary>Start the background probe loop.</summary>
    public void StartProbing()
    {
        _probeTask = Task.Run(ProbeLoop);
    }

    /// <summary>Stop the background probe loop.</summary>
    public async Task StopProbing()
    {
        _cts.Cancel();
        if (_probeTask is not null)
        {
            try { await _probeTask; }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Calculate the delay between sends for the current rate.
    /// Returns TimeSpan.Zero if rate is very high (fire as fast as possible).
    /// </summary>
    public TimeSpan SendInterval =>
        _currentRate <= 0 ? TimeSpan.FromSeconds(1) :
        _currentRate >= 10_000 ? TimeSpan.Zero :
        TimeSpan.FromMilliseconds(1000.0 / _currentRate);

    private async Task ProbeLoop()
    {
        var interval = TimeSpan.FromSeconds(_settings.ProbeIntervalSeconds);

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _cts.Token);
                var snapshot = await ProbeHealth();
                if (snapshot is null) continue;

                _lastQueueDepth = snapshot.QueueDepth;
                _lastPipeConnected = snapshot.PipeConnected;
                _lastQueueStatus = snapshot.QueueStatus;

                // Check failure rate from actual HTTP traffic
                // (skip during warmup grace period — JIT and connection pool establishment
                // cause artificially low throughput for the first 30 seconds)
                if (_throughputProbe is not null && _uptime.Elapsed.TotalSeconds > WarmupGraceSeconds)
                {
                    var (success, failed) = _throughputProbe();
                    var deltaSuccess = success - _prevSuccess;
                    var deltaFailed = failed - _prevFailed;
                    _prevSuccess = success;
                    _prevFailed = failed;

                    var deltaTotal = deltaSuccess + deltaFailed;
                    if (deltaTotal > 10 && deltaFailed > 0)
                    {
                        var failRate = (double)deltaFailed / deltaTotal;
                        if (failRate > 0.05) // >5% failure rate
                        {
                            BackOff($"failure_rate_{failRate:P0}");
                            continue; // skip queue-based adjustment this cycle
                        }
                    }

                    // NOTE: Throughput collapse detection was removed.
                    // When Edge is slow (not crashed), workers self-limit naturally.
                    // The collapse detector caused harmful AIMD oscillation —
                    // backing off when Edge was just slow, which prevented
                    // the request backlog from clearing and made things worse.
                    // Failure_rate detection (above) catches genuine crashes.
                }

                AdjustRate(snapshot);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Health probe failed: {ex.Message}");
                // On probe failure, assume congestion — back off
                BackOff("probe_failure");
            }
        }
    }

    private void AdjustRate(HealthSnapshot snapshot)
    {
        var qd = snapshot.QueueDepth;

        if (!snapshot.PipeConnected)
        {
            // Pipe disconnected — hard back-off to minimum
            _currentRate = Math.Max(1, _settings.InitialRatePerSecond);
            Console.WriteLine($"  [RATE] Pipe disconnected! Rate → {_currentRate}/s");
            return;
        }

        if (qd > _settings.QueueDepthCeiling)
        {
            // CONGESTION — multiplicative decrease (halve)
            BackOff("queue_depth_exceeded");
        }
        else if (qd < _settings.QueueDepthFloor)
        {
            // ADDITIVE INCREASE — gentle ramp (+AdditiveIncrease per probe)
            // No exponential slow-start: that overshoots and overloads Edge.
            var oldRate = _currentRate;
            _currentRate = Math.Min(_currentRate + _settings.AdditiveIncrease, _settings.MaxRatePerSecond);
            if (_currentRate != oldRate)
                Console.WriteLine($"  [RATE] Ramp up: {oldRate} → {_currentRate}/s (queue={qd})");
        }
        // else: queue is between floor and ceiling — hold steady

        if (_currentRate > _peakRate)
            _peakRate = _currentRate;
    }

    private void BackOff(string reason)
    {
        var oldRate = _currentRate;
        _currentRate = Math.Max(_currentRate / 2, _settings.InitialRatePerSecond);
        _backOffCount++;
        Console.WriteLine($"  [RATE] Back-off ({reason}): {oldRate} → {_currentRate}/s");
    }

    private async Task<HealthSnapshot?> ProbeHealth()
    {
        using var response = await _http.GetAsync("/health", _cts.Token);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(_cts.Token);

        return new HealthSnapshot
        {
            Status = json.GetProperty("status").GetString() ?? "unknown",
            PipeConnected = json.GetProperty("pipeConnected").GetBoolean(),
            QueueDepth = json.GetProperty("queueDepth").GetInt32(),
            QueueStatus = json.GetProperty("queueStatus").GetString() ?? "unknown",
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _http.Dispose();
    }
}

internal sealed class HealthSnapshot
{
    public required string Status { get; init; }
    public required bool PipeConnected { get; init; }
    public required int QueueDepth { get; init; }
    public required string QueueStatus { get; init; }
}
