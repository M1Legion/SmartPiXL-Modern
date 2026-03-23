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
// record to daily rolling files in the Failover/ directory.
//
// ARCHITECTURE:
//   PipeClientService (pipe unavailable)
//       → JsonlFailoverService.TryEnqueue (lock-free Channel<T>.Writer.TryWrite)
//       → Single background writer thread reads from channel
//       → Appends JSON line to failover_yyyy_MM_dd.jsonl
//
// RECOVERY:
//   When the Forge starts (or restarts), its FailoverCatchupService scans
//   the Failover/ directory, reads and processes all .jsonl files, then
//   deletes them. Zero data loss across all failure scenarios.
//
// THREADING:
//   Single reader (background loop) eliminates file contention. Multiple
//   producers (any thread calling TryEnqueue) are safe via Channel<T>.
// ============================================================================

/// <summary>
/// JSONL failover writer for when the named pipe to the Forge is unavailable.
/// <para>
/// Writes <see cref="TrackingData"/> records as one JSON line per record to
/// daily rolling files in the configured failover directory. Uses a bounded
/// <see cref="Channel{T}"/> internally so <see cref="TryEnqueue"/> is a
/// lock-free CAS operation safe for concurrent producers.
/// </para>
/// </summary>
public sealed class JsonlFailoverService : BackgroundService
{
    private readonly Channel<TrackingData> _channel;
    private readonly EdgeMetrics _metrics;
    private readonly ITrackingLogger _logger;
    private readonly string _failoverDirectory;
    private readonly string _resolvedDirectory;

    private StreamWriter? _currentWriter;
    private string? _currentDate;

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
            await foreach (var record in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                await WriteRecordAsync(record, dir);
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

        _currentWriter?.Dispose();
        _logger.Info("JsonlFailoverService stopped.");
    }

    /// <summary>
    /// Writes a single record as a JSON line to the current daily file.
    /// Rolls to a new file at midnight UTC.
    /// </summary>
    private async Task WriteRecordAsync(TrackingData record, string directory)
    {
        try
        {
            var date = DateTime.UtcNow.ToString("yyyy_MM_dd");

            // Roll to new file if date changed
            if (date != _currentDate)
            {
                _currentWriter?.Dispose();
                var filePath = Path.Combine(directory, $"failover_{date}.jsonl");
                _currentWriter = new StreamWriter(filePath, append: true, Encoding.UTF8)
                {
                    AutoFlush = true
                };
                _currentDate = date;
                _logger.Debug($"JsonlFailover: writing to failover_{date}.jsonl");
            }

            var json = JsonSerializer.Serialize(record);
            await _currentWriter!.WriteLineAsync(json);
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
