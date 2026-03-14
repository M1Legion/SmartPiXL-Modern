using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using SmartPiXL.SyntheticTraffic.Generation;

namespace SmartPiXL.SyntheticTraffic.Engine;

// ============================================================================
// HTTP TRAFFIC WRITER — Parallel Channel-based sender for high throughput.
//
// Architecture:
//   Producer (main loop) → Channel<SyntheticHit> → N consumer worker tasks → HTTP
//
// Rate limiting uses a token-bucket pattern:
//   A background task drips tokens into a SemaphoreSlim at the target rate
//   (controlled by AdaptiveRateController). Each worker acquires a token
//   before sending, so the aggregate send rate across all workers matches
//   the controller's current rate.
//
// At 64 workers × ~5ms per HTTP round-trip on localhost = ~12,800 req/s max.
// The rate controller governs actual throughput below that ceiling.
// ============================================================================

internal sealed class HttpTrafficWriter : IDisposable
{
    private readonly HttpClient _http;
    private readonly AdaptiveRateController _rateController;
    private readonly int _concurrency;

    // Bounded channel — backpressure if producer is way ahead of senders
    private readonly Channel<SyntheticHit> _channel;

    // Token bucket for rate limiting across all workers
    private readonly SemaphoreSlim _rateLimiter = new(0, 100_000);
    private readonly CancellationTokenSource _internalCts = new();
    private Task? _tokenDripTask;
    private Task[]? _workerTasks;

    // Counters — all accessed via Interlocked
    private long _totalSent;
    private long _totalSuccess;
    private long _totalFailed;
    private long _totalQueued;
    private readonly Stopwatch _elapsed = new();

    public long TotalSent => Interlocked.Read(ref _totalSent);
    public long TotalSuccess => Interlocked.Read(ref _totalSuccess);
    public long TotalFailed => Interlocked.Read(ref _totalFailed);
    public long TotalQueued => Interlocked.Read(ref _totalQueued);
    public TimeSpan Elapsed => _elapsed.Elapsed;
    public double HitsPerSecond => _elapsed.Elapsed.TotalSeconds > 0
        ? TotalSuccess / _elapsed.Elapsed.TotalSeconds : 0;

    public HttpTrafficWriter(TrafficSettings settings, AdaptiveRateController rateController)
    {
        _rateController = rateController;
        _concurrency = Math.Max(1, settings.Concurrency);

        // Bounded channel: 10K buffer provides enough runway for burst absorption
        _channel = Channel.CreateBounded<SyntheticHit>(new BoundedChannelOptions(10_000)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = _concurrency,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(settings.TargetUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/gif"));
    }

    /// <summary>Start the worker pool and token drip task.</summary>
    public void Start()
    {
        _elapsed.Start();

        // Start token drip — releases semaphore tokens at the current rate
        _tokenDripTask = Task.Run(TokenDripLoop);

        // Start N worker tasks that consume from the channel
        _workerTasks = new Task[_concurrency];
        for (var i = 0; i < _concurrency; i++)
        {
            _workerTasks[i] = Task.Run(WorkerLoop);
        }
    }

    /// <summary>
    /// Enqueue hits for sending. Backpressures if channel is full.
    /// Call from the producer (main loop).
    /// </summary>
    public async ValueTask EnqueueSession(SyntheticHit[] hits, CancellationToken ct)
    {
        foreach (var hit in hits)
        {
            await _channel.Writer.WriteAsync(hit, ct);
            Interlocked.Increment(ref _totalQueued);
        }
    }

    /// <summary>Signal that no more hits will be produced.</summary>
    public void CompleteProducer() => _channel.Writer.Complete();

    /// <summary>Wait for all queued hits to drain and workers to finish.</summary>
    public async Task DrainAndStop()
    {
        // Complete the channel so workers exit when empty
        _channel.Writer.TryComplete();

        // Wait for all workers to finish processing
        if (_workerTasks is not null)
            await Task.WhenAll(_workerTasks);

        // Stop the token drip
        _internalCts.Cancel();
        if (_tokenDripTask is not null)
        {
            try { await _tokenDripTask; }
            catch (OperationCanceledException) { }
        }

        _elapsed.Stop();
    }

    /// <summary>Forcibly stop (on Ctrl+C). Drains what it can.</summary>
    public async Task ForceStop()
    {
        _channel.Writer.TryComplete();
        _internalCts.Cancel();

        if (_workerTasks is not null)
            await Task.WhenAll(_workerTasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_tokenDripTask is not null)
            await _tokenDripTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        _elapsed.Stop();
    }

    // ════════════════════════════════════════════════════════════════════
    // TOKEN DRIP — Releases rate-limit tokens at the adaptive rate
    // ════════════════════════════════════════════════════════════════════

    private async Task TokenDripLoop()
    {
        // Drip tokens in batches every 10ms for smoother throughput
        const int dripIntervalMs = 10;
        var ct = _internalCts.Token;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(dripIntervalMs, ct);

                // How many tokens to release this tick?
                var rate = _rateController.CurrentRate;
                var tokensPerTick = Math.Max(1, rate * dripIntervalMs / 1000);

                // Don't exceed semaphore capacity
                var available = _rateLimiter.CurrentCount;
                var release = Math.Min(tokensPerTick, 100_000 - available);
                if (release > 0)
                    _rateLimiter.Release(release);
            }
            catch (OperationCanceledException) { break; }
            catch (SemaphoreFullException) { /* at capacity, skip */ }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // WORKER — Pulls hits from channel, acquires rate token, sends HTTP
    // ════════════════════════════════════════════════════════════════════

    private async Task WorkerLoop()
    {
        var ct = _internalCts.Token;
        var reader = _channel.Reader;

        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var hit))
            {
                if (ct.IsCancellationRequested) return;

                // Acquire a rate-limit token (blocks until token available)
                try
                {
                    await _rateLimiter.WaitAsync(ct);
                }
                catch (OperationCanceledException) { return; }

                await SendHit(hit, ct);
            }
        }
    }

    private async Task SendHit(SyntheticHit hit, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{hit.RequestPath}?{hit.QueryString}");

            request.Headers.TryAddWithoutValidation("User-Agent", hit.UserAgent);
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", hit.IpAddress);

            if (!string.IsNullOrEmpty(hit.Referrer))
                request.Headers.TryAddWithoutValidation("Referer", hit.Referrer);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            Interlocked.Increment(ref _totalSent);

            if (response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _totalSuccess);
            }
            else
            {
                Interlocked.Increment(ref _totalFailed);
                if (Interlocked.Read(ref _totalFailed) <= 10)
                    Console.WriteLine($"  [WARN] HTTP {(int)response.StatusCode} for {hit.RequestPath}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down — don't count as failure
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _totalSent);
            Interlocked.Increment(ref _totalFailed);
            if (Interlocked.Read(ref _totalFailed) <= 10)
                Console.WriteLine($"  [ERR] {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _internalCts.Cancel();
        _internalCts.Dispose();
        _rateLimiter.Dispose();
        _http.Dispose();
    }
}
