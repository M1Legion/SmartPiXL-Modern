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
    private readonly ITrackingLogger _logger;
    private readonly string _failoverDirectory;

    private StreamWriter? _currentWriter;
    private string? _currentDate;

    public JsonlFailoverService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _logger = logger;
        _failoverDirectory = settings.Value.FailoverDirectory;

        _channel = Channel.CreateBounded<TrackingData>(
            new BoundedChannelOptions(settings.Value.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
    }

    /// <summary>
    /// Lock-free enqueue. Returns <c>true</c> always (bounded channel with
    /// <see cref="BoundedChannelFullMode.DropOldest"/> drops the oldest item
    /// when full, so TryWrite never returns false).
    /// </summary>
    public bool TryEnqueue(TrackingData data) => _channel.Writer.TryWrite(data);

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
        }
        catch (Exception ex)
        {
            _logger.Error($"JsonlFailover: write failed — {ex.Message}");
        }
    }
}
