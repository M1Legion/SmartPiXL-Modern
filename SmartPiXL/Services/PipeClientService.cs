using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Models;

namespace SmartPiXL.Services;

// ============================================================================
// PIPE CLIENT SERVICE — Sends enriched TrackingData records to the Forge via
// a named pipe. Uses a Channel<T> internally so the hot path (TryEnqueue) is
// a lock-free CAS operation — zero blocking, zero allocation.
//
// ARCHITECTURE:
//   TrackingEndpoints.CaptureAndEnqueue
//       → PipeClientService.TryEnqueue (lock-free Channel<T>.Writer.TryWrite)
//       → Background batch loop reads from channel
//       → Serialize batch to JSON lines + SINGLE flush to pipe
//
// BATCHING:
//   Previous design flushed the pipe after EVERY record (~5-15ms per kernel
//   flush). On a 144-core Xeon, single-record flush was the sole bottleneck
//   at ~70 rec/s. Batch drain + single flush eliminates per-record kernel
//   transitions:
//
//     Old: read → serialize → write → FLUSH (×N)  = ~70/s
//     New: drain N → serialize N → write N → FLUSH (×1) = thousands/s
//
//   A 25ms fill window collects arriving records before flushing. At 1,000/s
//   incoming, that's ~25 records per batch. At 10,000/s, ~250 per batch.
//   Max batch size is 512 to bound memory and latency.
//
// FAILOVER:
//   When the pipe is unavailable (Forge not running, pipe broken), records
//   are delegated to JsonlFailoverService which writes durable JSONL files
//   to the Failover/ directory. The Forge's FailoverCatchupService picks
//   these up when it restarts.
//
// RECONNECTION:
//   Exponential backoff: 1s → 2s → 4s → 8s → 16s → 30s cap.
//   During backoff, records go directly to JSONL failover — no blocking.
//   On successful reconnect, the backoff resets to 0.
// ============================================================================

/// <summary>
/// Named pipe client that sends enriched <see cref="TrackingData"/> records to the Forge.
/// <para>
/// Uses a bounded <see cref="Channel{T}"/> internally so <see cref="TryEnqueue"/>
/// is a lock-free CAS operation safe for the hot path. A background loop batch-drains
/// the channel, serializes records to JSON lines, and flushes once per batch to the
/// named pipe. Falls back to <see cref="JsonlFailoverService"/> when the pipe is
/// unavailable.
/// </para>
/// </summary>
public sealed class PipeClientService : BackgroundService
{
    private readonly Channel<TrackingData> _channel;
    private readonly JsonlFailoverService _failoverService;
    private readonly EdgeMetrics _metrics;
    private readonly ITrackingLogger _logger;
    private readonly string _pipeName;

    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;

    /// <summary>
    /// Tracks consecutive reconnection failures for exponential backoff.
    /// Reset to 0 on successful connect.
    /// </summary>
    private int _reconnectAttempts;

    /// <summary>
    /// UTC time before which reconnection attempts are suppressed.
    /// Records arriving before this time go directly to JSONL failover.
    /// </summary>
    private DateTime _nextReconnectAttempt = DateTime.MinValue;

    private static readonly TimeSpan s_maxBackoff = TimeSpan.FromSeconds(30);
    private const int ConnectTimeoutMs = 3000;

    /// <summary>Max records per pipe batch. Bounds memory and latency.</summary>
    private const int MaxBatchSize = 512;

    /// <summary>
    /// After the first record arrives, wait up to this duration for more
    /// records before flushing. 25ms at 1,000/s incoming = ~25 records/batch.
    /// </summary>
    private static readonly TimeSpan BatchFillWindow = TimeSpan.FromMilliseconds(25);

    /// <summary>StreamWriter buffer — 64KB avoids mid-batch auto-flush.</summary>
    private const int StreamWriterBufferSize = 65_536;

    public PipeClientService(
        IOptions<TrackingSettings> settings,
        JsonlFailoverService failoverService,
        EdgeMetrics metrics,
        ITrackingLogger logger)
    {
        _failoverService = failoverService;
        _metrics = metrics;
        _logger = logger;
        _pipeName = settings.Value.PipeName;

        _channel = Channel.CreateBounded<TrackingData>(
            new BoundedChannelOptions(settings.Value.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });
    }

    /// <summary>
    /// Lock-free enqueue for the hot path. If the channel is full, routes
    /// the record to JSONL failover instead of dropping it.
    /// </summary>
    public bool TryEnqueue(TrackingData data)
    {
        if (_channel.Writer.TryWrite(data))
            return true;

        // Channel full — failover to JSONL so no data is ever lost
        return _failoverService.TryEnqueue(data);
    }

    /// <summary>Current number of records waiting to be written to the pipe.</summary>
    public int QueueDepth => _channel.Reader.Count;

    /// <summary>Whether the pipe is currently connected to the Forge.</summary>
    public bool IsConnected => _pipe?.IsConnected == true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _logger.Info($"PipeClientService started — pipe: {_pipeName}, batch max: {MaxBatchSize}, fill window: {BatchFillWindow.TotalMilliseconds}ms");

        var batch = new List<TrackingData>(MaxBatchSize);
        var reader = _channel.Reader;

        while (!stoppingToken.IsCancellationRequested)
        {
            batch.Clear();

            try
            {
                // Wait for at least one record
                if (!await reader.WaitToReadAsync(stoppingToken))
                    break;

                // Greedy drain: grab everything immediately available
                while (batch.Count < MaxBatchSize && reader.TryRead(out var item))
                    batch.Add(item);

                // Fill window: wait briefly for more records to arrive.
                // At high throughput, this collects dozens/hundreds of records
                // before a single flush — massive reduction in kernel transitions.
                if (batch.Count < MaxBatchSize)
                {
                    using var fillCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    fillCts.CancelAfter(BatchFillWindow);

                    try
                    {
                        while (batch.Count < MaxBatchSize
                            && await reader.WaitToReadAsync(fillCts.Token))
                        {
                            while (batch.Count < MaxBatchSize && reader.TryRead(out var extra))
                                batch.Add(extra);
                        }
                    }
                    catch (OperationCanceledException) when (fillCts.IsCancellationRequested)
                    {
                        // Fill window expired — write what we have
                    }
                }

                if (batch.Count > 0)
                    await WriteBatchAsync(batch, stoppingToken);

                // Sample pipe state for health probes
                _metrics.SamplePipeState(IsConnected, QueueDepth);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error($"PipeClient: unexpected error in batch loop — {ex.Message}");
                // Failover the entire batch so records aren't lost
                foreach (var record in batch)
                    _failoverService.TryEnqueue(record);
            }
        }

        // Drain remaining items on shutdown — try pipe first, failover for the rest
        await DrainAsync();

        DisposePipe();
        _logger.Info("PipeClientService stopped.");
    }

    /// <summary>
    /// Writes a batch of records to the pipe with a SINGLE flush.
    /// All JSON lines are written to the StreamWriter buffer synchronously,
    /// then one <see cref="StreamWriter.FlushAsync"/> call pushes everything
    /// to the pipe kernel buffer. Replaces per-record flush (the old bottleneck).
    /// </summary>
    private async Task WriteBatchAsync(List<TrackingData> batch, CancellationToken ct)
    {
        // Ensure pipe is connected (or reconnect if backoff expired)
        if (!await EnsureConnectedAsync(ct))
        {
            // Pipe unavailable — failover entire batch to JSONL
            foreach (var record in batch)
            {
                if (!_failoverService.TryEnqueue(record))
                    _logger.Warning("PipeClient: both pipe and JSONL failover unavailable — record dropped");
            }
            return;
        }

        try
        {
            var batchStart = EdgeMetrics.StartTimer();

            // Write all records to StreamWriter buffer (sync — no kernel transitions)
            for (var i = 0; i < batch.Count; i++)
            {
                var json = JsonSerializer.Serialize(batch[i]);
                _writer!.WriteLine(json);
            }

            // SINGLE flush for the entire batch — one kernel transition
            await _writer!.FlushAsync(ct);
            _reconnectAttempts = 0;

            _metrics.RecordPipeWrite(batchStart, batch.Count);
        }
        catch (IOException ex)
        {
            _logger.Warning($"PipeClient: batch write failed ({batch.Count} records) — {ex.Message}");
            DisposePipe();
            _reconnectAttempts++;
            SetNextReconnectTime();

            // Failover entire batch to JSONL
            _metrics.RecordPipeFailover(batch.Count);
            foreach (var record in batch)
                _failoverService.TryEnqueue(record);
        }
    }

    /// <summary>
    /// Ensures the pipe is connected. If not connected and the backoff period
    /// has expired, attempts reconnection. Returns <c>true</c> if the pipe is
    /// connected and ready for writing.
    /// </summary>
    private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_pipe?.IsConnected == true)
            return true;

        DisposePipe();

        // Don't attempt reconnection until backoff expires
        if (DateTime.UtcNow < _nextReconnectAttempt)
            return false;

        try
        {
            _pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            await _pipe.ConnectAsync(ConnectTimeoutMs, ct);
            _writer = new StreamWriter(_pipe, Encoding.UTF8, StreamWriterBufferSize, leaveOpen: true)
            {
                AutoFlush = false
            };

            _reconnectAttempts = 0;
            _metrics.RecordPipeReconnect();
            _logger.Info("PipeClient: connected to Forge");
            return true;
        }
        catch (OperationCanceledException)
        {
            DisposePipe();
            throw; // Propagate shutdown
        }
        catch (TimeoutException)
        {
            _reconnectAttempts++;
            SetNextReconnectTime();
            DisposePipe();

            // Log sparingly: first 3 attempts and then every 10th
            if (_reconnectAttempts <= 3 || _reconnectAttempts % 10 == 0)
            {
                var backoff = GetCurrentBackoffSeconds();
                _logger.Debug($"PipeClient: connect timeout (attempt {_reconnectAttempts}, next retry in {backoff:F0}s)");
            }

            return false;
        }
        catch (IOException ex)
        {
            _reconnectAttempts++;
            SetNextReconnectTime();
            _logger.Warning($"PipeClient: connect failed — {ex.Message}");
            DisposePipe();
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            // IIS app pool identity may lack pipe access — treat as transient
            _reconnectAttempts++;
            SetNextReconnectTime();
            if (_reconnectAttempts <= 3 || _reconnectAttempts % 10 == 0)
            {
                _logger.Warning($"PipeClient: access denied to pipe (attempt {_reconnectAttempts}) — {ex.Message}");
            }
            DisposePipe();
            return false;
        }
    }

    /// <summary>
    /// Drains remaining records from the channel on shutdown.
    /// Tries the pipe first (batch flush); if unavailable, uses JSONL failover.
    /// </summary>
    private async Task DrainAsync()
    {
        var batch = new List<TrackingData>(MaxBatchSize);

        while (_channel.Reader.TryRead(out var record))
            batch.Add(record);

        if (batch.Count == 0)
            return;

        if (_pipe?.IsConnected == true)
        {
            try
            {
                for (var i = 0; i < batch.Count; i++)
                {
                    var json = JsonSerializer.Serialize(batch[i]);
                    _writer!.WriteLine(json);
                }
                await _writer!.FlushAsync();
                _logger.Info($"PipeClient: drained {batch.Count} records to pipe on shutdown");
            }
            catch
            {
                // Pipe failed during drain — failover everything
                foreach (var record in batch)
                    _failoverService.TryEnqueue(record);
            }
        }
        else
        {
            foreach (var record in batch)
                _failoverService.TryEnqueue(record);
            _logger.Info($"PipeClient: drained {batch.Count} records to JSONL failover on shutdown");
        }
    }

    private void SetNextReconnectTime()
    {
        var backoffSeconds = GetCurrentBackoffSeconds();
        _nextReconnectAttempt = DateTime.UtcNow.AddSeconds(backoffSeconds);
    }

    private double GetCurrentBackoffSeconds() =>
        Math.Min(Math.Pow(2, _reconnectAttempts - 1), s_maxBackoff.TotalSeconds);

    private void DisposePipe()
    {
        try
        {
            _writer?.Dispose();
            _pipe?.Dispose();
        }
        catch
        {
            // Swallow — disposal errors are not actionable
        }
        finally
        {
            _writer = null;
            _pipe = null;
        }
    }
}
