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
//       → Background loop reads from channel
//       → Serialize to JSON line + write to NamedPipeClientStream
//       → Flush after each record
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
//
// DESIGN NOTE:
//   The workplan specifies `ValueTask EnqueueAsync(TrackingData)` but we use
//   `bool TryEnqueue(TrackingData)` (Channel<T>.Writer.TryWrite) instead.
//   This matches DatabaseWriterService.TryQueue and the C# coding standards'
//   Channel<T> pattern, keeps the hot path lock-free, and moves serialization
//   + I/O to the background loop. See IMPLEMENTATION-LOG.md Session 5.
// ============================================================================

/// <summary>
/// Named pipe client that sends enriched <see cref="TrackingData"/> records to the Forge.
/// <para>
/// Uses a bounded <see cref="Channel{T}"/> internally so <see cref="TryEnqueue"/>
/// is a lock-free CAS operation safe for the hot path. A single background loop
/// reads from the channel, serializes records to JSON lines, and writes them to
/// the named pipe. Falls back to <see cref="JsonlFailoverService"/> when the
/// pipe is unavailable.
/// </para>
/// </summary>
public sealed class PipeClientService : BackgroundService
{
    private readonly Channel<TrackingData> _channel;
    private readonly JsonlFailoverService _failoverService;
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

    public PipeClientService(
        IOptions<TrackingSettings> settings,
        JsonlFailoverService failoverService,
        ITrackingLogger logger)
    {
        _failoverService = failoverService;
        _logger = logger;
        _pipeName = settings.Value.PipeName;

        _channel = Channel.CreateBounded<TrackingData>(
            new BoundedChannelOptions(settings.Value.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
    }

    /// <summary>
    /// Lock-free enqueue for the hot path. Returns <c>true</c> always
    /// (bounded channel with <see cref="BoundedChannelFullMode.DropOldest"/>
    /// drops the oldest item when full, so TryWrite never returns false).
    /// </summary>
    public bool TryEnqueue(TrackingData data) => _channel.Writer.TryWrite(data);

    /// <summary>Current number of records waiting to be written to the pipe.</summary>
    public int QueueDepth => _channel.Reader.Count;

    /// <summary>Whether the pipe is currently connected to the Forge.</summary>
    public bool IsConnected => _pipe?.IsConnected == true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _logger.Info($"PipeClientService started — pipe: {_pipeName}");

        try
        {
            await foreach (var record in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                await WriteRecordAsync(record, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        // Drain remaining items on shutdown — try pipe first, failover for the rest
        await DrainAsync();

        DisposePipe();
        _logger.Info("PipeClientService stopped.");
    }

    /// <summary>
    /// Writes a single record to the pipe. If the pipe is unavailable,
    /// the record is delegated to <see cref="JsonlFailoverService"/>.
    /// </summary>
    private async Task WriteRecordAsync(TrackingData record, CancellationToken ct)
    {
        // Ensure pipe is connected (or reconnect if backoff expired)
        if (!await EnsureConnectedAsync(ct))
        {
            // Pipe unavailable — failover to JSONL
            if (!_failoverService.TryEnqueue(record))
            {
                _logger.Warning("PipeClient: both pipe and JSONL failover unavailable — record dropped");
            }
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(record);
            await _writer!.WriteLineAsync(json);
            await _writer.FlushAsync(ct);
            _reconnectAttempts = 0;
        }
        catch (IOException ex)
        {
            _logger.Warning($"PipeClient: write failed — {ex.Message}");
            DisposePipe();
            _reconnectAttempts++;
            SetNextReconnectTime();

            // Failover this record to JSONL
            if (!_failoverService.TryEnqueue(record))
            {
                _logger.Warning("PipeClient: JSONL failover also unavailable — record dropped");
            }
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
            _writer = new StreamWriter(_pipe, Encoding.UTF8, leaveOpen: true)
            {
                AutoFlush = false
            };

            _reconnectAttempts = 0;
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
    /// Tries the pipe first; if unavailable, uses JSONL failover.
    /// </summary>
    private async Task DrainAsync()
    {
        while (_channel.Reader.TryRead(out var record))
        {
            if (_pipe?.IsConnected == true)
            {
                try
                {
                    var json = JsonSerializer.Serialize(record);
                    await _writer!.WriteLineAsync(json);
                    await _writer.FlushAsync();
                }
                catch
                {
                    _failoverService.TryEnqueue(record);
                }
            }
            else
            {
                _failoverService.TryEnqueue(record);
            }
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
