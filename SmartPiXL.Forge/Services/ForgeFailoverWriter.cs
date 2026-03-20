using System.Text.Json;
using SmartPiXL.Models;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// FORGE FAILOVER WRITER — Persists enriched TrackingData to JSONL files when
// the SQL writer channel is full or the circuit breaker is open.
//
// PURPOSE:
//   Prevents data loss during SQL outages. When the SQL writer can't keep up
//   (channel full) or SQL is unavailable (circuit open), enriched records are
//   written to disk as JSONL files in the ForgeFailover/ directory. These
//   records have ALREADY been enriched with all _srv_* params — re-enrichment
//   is unnecessary on replay.
//
// FILE FORMAT:
//   Each file contains one JSON line per TrackingData record (JSONL format).
//   Files rotate every MaxRecordsPerFile records or when RotateFile() is called.
//   File naming: failover_{yyyyMMdd_HHmmss}_{guid}.jsonl
//
// THREAD SAFETY:
//   All public methods are thread-safe via lock. This is acceptable because:
//   - Failover is exceptional (only during SQL issues), not hot-path
//   - Lock contention during outages is negligible vs the SQL outage itself
//
// REPLAY:
//   SqlBulkCopyWriterService calls ReplayFailoverFilesAsync() on startup
//   and when the circuit breaker transitions from Open → Closed. Replay
//   reads JSONL files, deserializes, and writes directly via SqlBulkCopy
//   (bypassing the channel entirely).
//
// RELATIONSHIP TO OTHER FAILOVER:
//   Edge JSONL failover: Edge → disk when named pipe is unavailable (un-enriched)
//   Forge JSONL failover: Forge → disk when SQL is unavailable (enriched) ← THIS
//   Dead-letter: SQL writer → disk when a specific batch exhausts all retries
// ============================================================================

/// <summary>
/// Thread-safe JSONL writer for persisting enriched <see cref="TrackingData"/>
/// records to disk when the SQL writer channel is full or circuit is open.
/// Registered as a singleton in DI. Used by <see cref="EnrichmentPipelineService"/>
/// and <see cref="SqlBulkCopyWriterService"/>.
/// </summary>
public sealed class ForgeFailoverWriter : IDisposable
{
    private readonly string _failoverDir;
    private readonly ITrackingLogger _logger;
    private readonly ForgeMetrics _metrics;
    private readonly object _gate = new();

    private StreamWriter? _currentWriter;
    private string? _currentFilePath;
    private int _recordsInCurrentFile;
    private long _totalRecordsWritten;

    /// <summary>Maximum records per JSONL file before rotation.</summary>
    private const int MaxRecordsPerFile = 10_000;

    private static readonly JsonSerializerOptions s_jsonOpts = new() { WriteIndented = false };

    /// <summary>Total records written to failover since service start.</summary>
    public long TotalRecordsWritten => Interlocked.Read(ref _totalRecordsWritten);

    public ForgeFailoverWriter(string failoverDir, ITrackingLogger logger, ForgeMetrics metrics)
    {
        _failoverDir = failoverDir;
        _logger = logger;
        _metrics = metrics;
        Directory.CreateDirectory(_failoverDir);
    }

    /// <summary>
    /// Appends a single enriched <see cref="TrackingData"/> record to the
    /// current JSONL failover file. Thread-safe. Rotates files automatically.
    /// </summary>
    public void Append(TrackingData record)
    {
        var json = JsonSerializer.Serialize(record, s_jsonOpts);

        lock (_gate)
        {
            try
            {
                EnsureWriter();
                _currentWriter!.WriteLine(json);
                _recordsInCurrentFile++;
                Interlocked.Increment(ref _totalRecordsWritten);
                _metrics.RecordFailover();

                if (_recordsInCurrentFile >= MaxRecordsPerFile)
                    RotateFileLocked();
            }
            catch (Exception ex)
            {
                _logger.Error($"CRITICAL: Forge failover write failed — DATA LOST: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Appends a batch of enriched records to failover. Used by the SQL writer
    /// when draining the channel during circuit-open state.
    /// </summary>
    public void AppendBatch(IReadOnlyList<TrackingData> records)
    {
        if (records.Count == 0) return;

        lock (_gate)
        {
            try
            {
                EnsureWriter();

                foreach (var record in records)
                {
                    var json = JsonSerializer.Serialize(record, s_jsonOpts);
                    _currentWriter!.WriteLine(json);
                    _recordsInCurrentFile++;
                    Interlocked.Increment(ref _totalRecordsWritten);
                    _metrics.RecordFailover();

                    if (_recordsInCurrentFile >= MaxRecordsPerFile)
                        RotateFileLocked();
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"CRITICAL: Forge failover batch write failed — DATA LOST: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Flushes the current writer and closes the file. Call during shutdown.
    /// </summary>
    public void Flush()
    {
        lock (_gate)
        {
            CloseCurrentWriterLocked();
        }
    }

    /// <summary>
    /// Returns all failover JSONL file paths in chronological order (oldest first).
    /// </summary>
    public string[] GetFailoverFiles()
    {
        if (!Directory.Exists(_failoverDir)) return [];
        return Directory.GetFiles(_failoverDir, "failover_*.jsonl")
            .OrderBy(f => f)
            .ToArray();
    }

    /// <summary>
    /// Reads and deserializes all records from a single failover JSONL file.
    /// Returns an empty list if the file is corrupt or missing.
    /// </summary>
    public List<TrackingData> ReadFile(string filePath)
    {
        var records = new List<TrackingData>();

        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var record = JsonSerializer.Deserialize<TrackingData>(line, s_jsonOpts);
                    if (record is not null)
                        records.Add(record);
                }
                catch (JsonException)
                {
                    _logger.Warning($"Forge failover: dead-lettering malformed line in {Path.GetFileName(filePath)}");
                    // Preserve malformed line in dead-letter file
                    WriteToDeadLetter(line, Path.GetFileName(filePath));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Forge failover: failed to read {Path.GetFileName(filePath)}: {ex.Message}");
        }

        return records;
    }

    /// <summary>
    /// Renames a failover file to <c>.processed</c> after successful replay.
    /// Source data is preserved for 7 days in case of downstream failures.
    /// </summary>
    public void MarkFileProcessed(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var processedPath = filePath + ".processed";
                File.Move(filePath, processedPath);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Forge failover: failed to mark processed {Path.GetFileName(filePath)}: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes a raw line to a dead-letter file so malformed data is never lost.
    /// </summary>
    private void WriteToDeadLetter(string rawLine, string sourceFileName)
    {
        try
        {
            var date = DateTime.UtcNow.ToString("yyyy_MM_dd");
            var deadLetterPath = Path.Combine(_failoverDir, $"dead_letter_{date}.jsonl");
            var entry = $"// Source: {sourceFileName} at {DateTime.UtcNow:O}" + Environment.NewLine + rawLine;
            File.AppendAllText(deadLetterPath, entry + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.Error($"Forge failover: failed to write dead-letter: {ex.Message}");
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private void EnsureWriter()
    {
        if (_currentWriter is not null) return;

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"failover_{timestamp}_{Guid.NewGuid():N}.jsonl";
        _currentFilePath = Path.Combine(_failoverDir, fileName);
        _recordsInCurrentFile = 0;

        _currentWriter = new StreamWriter(
            new FileStream(_currentFilePath, FileMode.Create, FileAccess.Write, FileShare.Read),
            leaveOpen: false)
        {
            AutoFlush = true // Flush each line — no data loss on crash
        };

        _logger.Info($"Forge failover: opened {fileName}");
    }

    private void RotateFileLocked()
    {
        if (_currentWriter is null) return;

        _logger.Info($"Forge failover: rotated {Path.GetFileName(_currentFilePath)} ({_recordsInCurrentFile:N0} records)");
        CloseCurrentWriterLocked();
    }

    private void CloseCurrentWriterLocked()
    {
        if (_currentWriter is null) return;

        try
        {
            _currentWriter.Flush();
            _currentWriter.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warning($"Forge failover: error closing file — {ex.Message}");
        }
        finally
        {
            _currentWriter = null;
            _currentFilePath = null;
            _recordsInCurrentFile = 0;
        }
    }

    public void Dispose()
    {
        Flush();
    }
}
