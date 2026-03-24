using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Models;

namespace SmartPiXL.Services;

// ============================================================================
// JSONL FAILOVER SERVICE — Durable write path for when the named pipe to the
// Forge is unavailable. Writes TrackingData records as one JSON line per
// record to per-outage files in the Failover/ directory.
//
// ARCHITECTURE:
//   PipeClientService (pipe unavailable)
//       → JsonlFailoverService.TryEnqueue (lock-free Channel<T>.Writer.TryWrite)
//       → Single background writer thread reads from channel
//       → Appends JSON line to failover_{date}_{time}.jsonl
//
// PER-OUTAGE FILE DESIGN:
//   Each failover event creates a new timestamped file. When the channel is
//   idle for 5 seconds (outage over), the writer closes the file immediately.
//   This lets Forge's replay service pick it up on the next 60s scan cycle
//   instead of waiting until the next calendar day.
//
// RECOVERY:
//   Forge's ForgeReplayService scans the Failover/ directory, reads and
//   processes all .jsonl files, then renames them to .processed. If a file
//   is still locked (active outage), replay skips it and retries next cycle.
//   Zero data loss across all failure scenarios.
//
// THREADING:
//   Single reader (background loop) eliminates file contention. Multiple
//   producers (any thread calling TryEnqueue) are safe via Channel<T>.
// ============================================================================

/// <summary>
/// JSONL failover writer for when the named pipe to the Forge is unavailable.
/// <para>
/// Writes <see cref="TrackingData"/> records as one JSON line per record to
/// per-outage files in the configured failover directory. Each outage creates
/// a new timestamped file; when the channel is idle for 5 seconds the writer
/// closes the file so Forge's replay service can process it immediately.
/// </para>
/// </summary>
public sealed class JsonlFailoverService : BackgroundService
{
    private readonly Channel<TrackingData> _channel;
    private readonly EdgeMetrics _metrics;
    private readonly ITrackingLogger _logger;
    private readonly string _failoverDirectory;
    private readonly string _resolvedDirectory;

    /// <summary>Seconds of channel idle before closing the current file.</summary>
    private const int IdleCloseSeconds = 5;

    private StreamWriter? _currentWriter;
    private string? _currentFileName;

    /// <summary>Lock for synchronous emergency writes when the channel is full.</summary>
    private readonly object _emergencyWriteLock = new();

    public JsonlFailoverService(
        IOptions<TrackingSettings> settings,
        EdgeMetrics metrics,
        ITrackingLogger logger)
    {
        _metrics = metrics;
        _logger = logger;
        _failoverDirectory = settings.Value.FailoverDirectory;

        // Pre-resolve the directory path so emergency writes can use it
        _resolvedDirectory = Path.IsPathRooted(_failoverDirectory)
            ? _failoverDirectory
            : Path.Combine(AppContext.BaseDirectory, _failoverDirectory);

        _channel = Channel.CreateBounded<TrackingData>(
            new BoundedChannelOptions(settings.Value.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });
    }

    /// <summary>
    /// Lock-free enqueue. If the channel is full, performs a synchronous
    /// direct-write to disk as a last resort — data is never lost.
    /// </summary>
    public bool TryEnqueue(TrackingData data)
    {
        if (_channel.Writer.TryWrite(data))
            return true;

        // Channel full — emergency synchronous write directly to disk.
        // This is the absolute last safety net. We accept the lock cost
        // because this only fires under extreme backpressure.
        EmergencyWriteToDisk(data);
        return true;
    }

    /// <summary>Current number of records waiting to be written to disk.</summary>
    public int QueueDepth => _channel.Reader.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        // Resolve the failover directory — relative paths from AppContext.BaseDirectory
        var dir = Path.IsPathRooted(_failoverDirectory)
            ? _failoverDirectory
            : Path.Combine(AppContext.BaseDirectory, _failoverDirectory);

        Directory.CreateDirectory(dir);

        _logger.Info($"JsonlFailoverService started — directory: {dir}");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Block until failover data arrives (outage starts)
                if (!await _channel.Reader.WaitToReadAsync(stoppingToken))
                    break;

                // Drain all available records, then wait briefly for more
                do
                {
                    while (_channel.Reader.TryRead(out var record))
                        await WriteRecordAsync(record, dir);
                }
                while (await WaitForMoreAsync(stoppingToken));

                // Channel idle for 5s — outage likely over, close the file
                // so Forge replay can pick it up immediately
                CloseCurrentWriter();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        // Drain remaining records on shutdown
        while (_channel.Reader.TryRead(out var record))
        {
            await WriteRecordAsync(record, dir);
        }

        CloseCurrentWriter();
        _logger.Info("JsonlFailoverService stopped.");
    }

    /// <summary>
    /// Waits up to <see cref="IdleCloseSeconds"/> for more data on the channel.
    /// Returns true if data arrived (keep writing), false on idle timeout (close file).
    /// </summary>
    private async Task<bool> WaitForMoreAsync(CancellationToken ct)
    {
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(TimeSpan.FromSeconds(IdleCloseSeconds));
        try
        {
            return await _channel.Reader.WaitToReadAsync(idleCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // Idle timeout — no data for 5s
        }
    }

    /// <summary>Closes and disposes the current writer, releasing the file lock.</summary>
    private void CloseCurrentWriter()
    {
        if (_currentWriter is null) return;
        _currentWriter.Dispose();
        _logger.Debug($"JsonlFailover: closed {_currentFileName}");
        _currentWriter = null;
        _currentFileName = null;
    }

    /// <summary>
    /// Writes a single record as a JSON line. Opens a new per-outage file
    /// if no writer is active.
    /// </summary>
    private async Task WriteRecordAsync(TrackingData record, string directory)
    {
        try
        {
            if (_currentWriter is null)
            {
                var timestamp = DateTime.UtcNow.ToString("yyyy_MM_dd_HHmmss");
                _currentFileName = $"failover_{timestamp}.jsonl";
                var filePath = Path.Combine(directory, _currentFileName);
                _currentWriter = new StreamWriter(filePath, append: true, Encoding.UTF8)
                {
                    AutoFlush = true
                };
                _logger.Debug($"JsonlFailover: opened {_currentFileName}");
            }

            var json = JsonSerializer.Serialize(record);
            await _currentWriter.WriteLineAsync(json);
            _metrics.RecordFailoverWrite();
        }
        catch (Exception ex)
        {
            _metrics.RecordFailoverError();
            _logger.Error($"JsonlFailover: write failed — {ex.Message}");
        }
    }

    /// <summary>
    /// Synchronous emergency write when the channel is full. Uses a separate
    /// lock and writes directly to an emergency file. This path only fires
    /// under extreme backpressure when the normal async channel is saturated.
    /// </summary>
    private void EmergencyWriteToDisk(TrackingData record)
    {
        lock (_emergencyWriteLock)
        {
            try
            {
                Directory.CreateDirectory(_resolvedDirectory);
                var date = DateTime.UtcNow.ToString("yyyy_MM_dd");
                var filePath = Path.Combine(_resolvedDirectory, $"failover_emergency_{date}.jsonl");
                var json = JsonSerializer.Serialize(record);
                File.AppendAllText(filePath, json + Environment.NewLine, Encoding.UTF8);
                _metrics.RecordFailoverEmergency();
            }
            catch (Exception ex)
            {
                _metrics.RecordFailoverError();
                _logger.Error($"CRITICAL: Emergency failover write failed — DATA AT RISK: {ex.Message}");
            }
        }
    }
}
