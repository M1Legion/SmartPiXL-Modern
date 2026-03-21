using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Models;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// FORGE REPLAY SERVICE — Unified replay for all failover / dead-letter files.
//
// Replaces three separate replay mechanisms:
//   1. FailoverCatchupService — Edge JSONL failover (un-enriched)
//   2. SqlBulkCopyWriterService.ReplayFailoverFilesAsync — Forge JSONL (enriched)
//   3. SqlBulkCopyWriterService.ReplayDeadLettersAsync — dead-letter JSON arrays
//
// INTELLIGENCE:
//   • Format detection: peeks at file content to determine JSONL vs JSON array
//   • Route detection: based on source directory, routes enriched records direct
//     to SQL (bypassing enrichment pipeline) and un-enriched records through the
//     enrichment channel for full Tier 1-3 processing
//   • Graceful retry: if SQL write fails, the file stays for the next scan cycle
//   • Dead-letter: malformed lines preserved in dead_letter_*.jsonl
//   • Cleanup: .processed files removed after 7 days
//
// SCAN DIRECTORIES:
//   Edge failover (un-enriched JSONL):  C:\inetpub\Smartpixl.info\Failover
//   Forge failover (enriched JSONL):    ForgeFailover/
//   Dead-letter (enriched JSON arrays): DeadLetter/
//
// SCHEDULE:
//   Scans all directories every FailoverScanIntervalSeconds (default 60s).
//   10-second startup delay lets the pipeline stabilize first.
// ============================================================================

/// <summary>
/// Unified background service that replays all failover and dead-letter files.
/// Enriched records go direct to SQL via SqlBulkCopy. Un-enriched records are
/// fed through the enrichment channel for full pipeline processing.
/// </summary>
public sealed class ForgeReplayService : BackgroundService
{
    private readonly ForgeChannels _channels;
    private readonly ForgeSettings _forgeSettings;
    private readonly TrackingSettings _trackingSettings;
    private readonly ForgeFailoverWriter _failoverWriter;
    private readonly ITrackingLogger _logger;
    private readonly ForgeMetrics _metrics;

    // Replay source directories
    private readonly string _edgeFailoverDir;   // Un-enriched JSONL (Edge pipe failure)
    private readonly string _forgeFailoverDir;  // Enriched JSONL (SQL circuit open / channel full)
    private readonly string _deadLetterDir;     // Enriched JSONL (batch retry exhaustion)

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public ForgeReplayService(
        ForgeChannels channels,
        IOptions<ForgeSettings> forgeSettings,
        IOptions<TrackingSettings> trackingSettings,
        ForgeFailoverWriter failoverWriter,
        ITrackingLogger logger,
        ForgeMetrics metrics)
    {
        _channels = channels;
        _forgeSettings = forgeSettings.Value;
        _trackingSettings = trackingSettings.Value;
        _failoverWriter = failoverWriter;
        _logger = logger;
        _metrics = metrics;

        _edgeFailoverDir = _forgeSettings.FailoverDirectory;

        _forgeFailoverDir = _forgeSettings.ForgeFailoverDirectory;

        _deadLetterDir = _forgeSettings.DeadLetterDirectory;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();

        _logger.Info($"ForgeReplayService started. Scan interval: {_forgeSettings.FailoverScanIntervalSeconds}s");
        _logger.Info($"  Edge failover:  {_edgeFailoverDir}");
        _logger.Info($"  Forge failover: {_forgeFailoverDir}");
        _logger.Info($"  Dead-letter:    {_deadLetterDir}");

        // Let pipeline services stabilize before first replay
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ScanAndReplayAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error($"ForgeReplay: scan error — {ex.Message}");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_forgeSettings.FailoverScanIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.Info("ForgeReplayService stopped.");
    }

    /// <summary>
    /// Single scan pass: replay enriched files to SQL, un-enriched to enrichment,
    /// then clean up old .processed files across all directories.
    /// </summary>
    private async Task ScanAndReplayAsync(CancellationToken ct)
    {
        // Enriched records first — straight to SQL, fastest path
        await ReplayDirectoryToSqlAsync(_forgeFailoverDir, ct);
        await ReplayDirectoryToSqlAsync(_deadLetterDir, ct);

        // Un-enriched Edge failover — through enrichment pipeline
        await ReplayDirectoryToEnrichmentAsync(_edgeFailoverDir, ct);

        // Clean up .processed files older than 7 days
        CleanupProcessedFiles(_edgeFailoverDir);
        CleanupProcessedFiles(_forgeFailoverDir);
        CleanupProcessedFiles(_deadLetterDir);
    }

    // ── Enriched replay → direct SQL ──────────────────────────────────────

    private async Task ReplayDirectoryToSqlAsync(string dir, CancellationToken ct)
    {
        var files = GetReplayableFiles(dir);
        if (files.Length == 0) return;

        _logger.Info($"ForgeReplay: {files.Length} file(s) in {Path.GetFileName(dir.TrimEnd('\\', '/'))}");

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            var records = ReadFileAdaptive(file);
            if (records.Count == 0)
            {
                MarkProcessed(file);
                continue;
            }

            if (await WriteBatchesToSqlAsync(records, ct))
            {
                MarkProcessed(file);
                _metrics.RecordReplay(records.Count);
                _logger.Info($"ForgeReplay: wrote {records.Count:N0} enriched records from {Path.GetFileName(file)}");
            }
            // else: SQL write failed — file stays for next scan cycle
        }
    }

    // ── Un-enriched replay → enrichment channel ───────────────────────────

    private async Task ReplayDirectoryToEnrichmentAsync(string dir, CancellationToken ct)
    {
        var files = GetReplayableFiles(dir);
        if (files.Length == 0) return;

        _logger.Info($"ForgeReplay: {files.Length} file(s) in {Path.GetFileName(dir.TrimEnd('\\', '/'))}");

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            var records = ReadFileAdaptive(file);
            if (records.Count == 0)
            {
                MarkProcessed(file);
                continue;
            }

            var enqueued = await EnqueueToChannelAsync(records, ct);
            if (enqueued >= 0)
            {
                MarkProcessed(file);
                if (enqueued > 0)
                {
                    _metrics.RecordReplay(enqueued);
                    _logger.Info($"ForgeReplay: enqueued {enqueued:N0} un-enriched records from {Path.GetFileName(file)}");
                }
            }
            // enqueued == -1: channel full timeout — file stays for next cycle
        }
    }

    // ── Smart format detection ────────────────────────────────────────────

    /// <summary>
    /// Reads a replay file. All Forge persistence now uses JSONL format.
    /// Retains JSON array fallback for legacy dead-letter files created before
    /// the format was unified.
    /// </summary>
    private List<TrackingData> ReadFileAdaptive(string filePath)
    {
        try
        {
            // Primary path: JSONL (all current code writes this format)
            var records = ReadAsJsonl(filePath);
            if (records.Count > 0) return records;

            // Fallback: legacy JSON array dead-letters (created before JSONL unification)
            return TryReadAsArray(filePath) ?? [];
        }
        catch (Exception ex)
        {
            _logger.Error($"ForgeReplay: failed to read {Path.GetFileName(filePath)}: {ex.Message}");
            return [];
        }
    }

    /// <summary>Tries to deserialize the file as a JSON array. Returns null on failure.</summary>
    private List<TrackingData>? TryReadAsArray(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var batch = JsonSerializer.Deserialize<List<TrackingData>>(content, s_jsonOpts);
            return batch is { Count: > 0 } ? batch : null;
        }
        catch (JsonException)
        {
            return null; // Not a valid JSON array
        }
    }

    /// <summary>Reads line-by-line JSONL. Dead-letters malformed lines.</summary>
    private List<TrackingData> ReadAsJsonl(string filePath)
    {
        var records = new List<TrackingData>();
        var deadLetterCount = 0;

        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//")) continue;

            try
            {
                var record = JsonSerializer.Deserialize<TrackingData>(line, s_jsonOpts);
                if (record is not null)
                    records.Add(record);
            }
            catch (JsonException)
            {
                deadLetterCount++;
                WriteToDeadLetter(line, Path.GetFileName(filePath));
            }
        }

        if (deadLetterCount > 0)
            _logger.Warning($"ForgeReplay: dead-lettered {deadLetterCount} malformed lines from {Path.GetFileName(filePath)}");

        return records;
    }

    // ── SQL direct write (enriched records) ───────────────────────────────

    /// <summary>
    /// Writes records to PiXL.Parsed via SqlBulkCopy. Records are parsed inline
    /// using ParsedRecordParser before writing. Simple retry (3 attempts).
    /// Returns false if all retries fail (file stays for next scan cycle).
    /// </summary>
    private async Task<bool> WriteBatchesToSqlAsync(List<TrackingData> records, CancellationToken ct)
    {
        const int maxRetries = 3;
        int[] retryDelaysMs = [1000, 2000, 4000];

        var batchSize = _forgeSettings.ForgeBatchSize;

        for (var batchStart = 0; batchStart < records.Count; batchStart += batchSize)
        {
            var batchEnd = Math.Min(batchStart + batchSize, records.Count);
            var batch = records.GetRange(batchStart, batchEnd - batchStart);

            // Parse all records in this sub-batch
            var parsed = new List<object?[]>(batch.Count);
            foreach (var td in batch)
            {
                parsed.Add(ParsedRecordParser.Parse(
                    td.CompanyID, td.PiXLID, td.IPAddress,
                    td.ReceivedAt, td.RequestPath,
                    td.QueryString, td.HeadersJson,
                    td.UserAgent, td.Referer));
            }

            var written = false;
            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(retryDelaysMs[Math.Min(attempt - 1, retryDelaysMs.Length - 1)], ct);

                try
                {
                    var ts = ForgeMetrics.StartTimer();

                    using var reader = new ParsedDataReader(parsed);
                    using var bulkCopy = new SqlBulkCopy(_trackingSettings.ConnectionString)
                    {
                        DestinationTableName = "PiXL.Parsed",
                        BatchSize = parsed.Count,
                        BulkCopyTimeout = _trackingSettings.BulkCopyTimeoutSeconds
                    };

                    var cols = ParsedRecordParser.ColumnNames;
                    for (var i = 0; i < cols.Length; i++)
                        bulkCopy.ColumnMappings.Add(i, cols[i]);

                    await bulkCopy.WriteToServerAsync(reader, ct);

                    _metrics.Record(Stage.SqlBulkCopy, ts, batch.Count);
                    written = true;
                    break;
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    _logger.Warning($"ForgeReplay: SQL write attempt {attempt + 1} failed: {ex.Message}");
                }
                catch (Exception ex)
                {
                    _logger.Error($"ForgeReplay: SQL write failed after {maxRetries + 1} attempts for {batch.Count} records: {ex.Message}");
                    return false;
                }
            }

            if (!written) return false;
        }

        return true;
    }

    // ── Enrichment channel enqueue (un-enriched records) ──────────────────

    /// <summary>
    /// Enqueues records to the enrichment channel with a 30-second timeout per record.
    /// Returns the number of records enqueued, or -1 if the channel was full for 30s.
    /// </summary>
    private async Task<int> EnqueueToChannelAsync(List<TrackingData> records, CancellationToken ct)
    {
        var enqueued = 0;

        foreach (var record in records)
        {
            if (ct.IsCancellationRequested) return -1;

            if (!_channels.Enrichment.Writer.TryWrite(record))
            {
                // Channel full — wait briefly for space
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                try
                {
                    await _channels.Enrichment.Writer.WriteAsync(record, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.Warning($"ForgeReplay: enrichment channel full for 30s — pausing file replay");
                    return -1; // Retry this file next cycle
                }
            }

            enqueued++;
        }

        return enqueued;
    }

    // ── File management ───────────────────────────────────────────────────

    /// <summary>Returns .jsonl and .json files in the directory, sorted oldest first.</summary>
    private static string[] GetReplayableFiles(string dir)
    {
        if (!Directory.Exists(dir)) return [];

        return Directory.EnumerateFiles(dir)
            .Where(f =>
            {
                var ext = Path.GetExtension(f);
                return ext is ".jsonl" or ".json";
            })
            .OrderBy(f => f)
            .ToArray();
    }

    /// <summary>Renames a file to .processed after successful replay.</summary>
    private void MarkProcessed(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var processedPath = filePath + ".processed";
                File.Move(filePath, processedPath);
            }
        }
        catch (IOException ex)
        {
            _logger.Warning($"ForgeReplay: could not mark processed {Path.GetFileName(filePath)}: {ex.Message}");
        }
    }

    /// <summary>Removes .processed files older than 7 days.</summary>
    private void CleanupProcessedFiles(string dir)
    {
        if (!Directory.Exists(dir)) return;

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var file in Directory.GetFiles(dir, "*.processed"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    _logger.Debug($"ForgeReplay: cleaned up {Path.GetFileName(file)}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"ForgeReplay: cleanup error — {ex.Message}");
        }
    }

    /// <summary>Writes a malformed line to a dead-letter file for investigation.</summary>
    private void WriteToDeadLetter(string rawLine, string sourceFileName)
    {
        try
        {
            Directory.CreateDirectory(_deadLetterDir);
            var date = DateTime.UtcNow.ToString("yyyy_MM_dd");
            var deadLetterPath = Path.Combine(_deadLetterDir, $"dead_letter_{date}.jsonl");
            var entry = $"// Source: {sourceFileName} at {DateTime.UtcNow:O}" + Environment.NewLine + rawLine;
            File.AppendAllText(deadLetterPath, entry + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.Error($"ForgeReplay: failed to write dead-letter: {ex.Message}");
        }
    }
}
