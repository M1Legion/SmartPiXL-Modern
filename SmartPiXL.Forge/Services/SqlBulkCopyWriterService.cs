using System.Collections;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Models;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// SQL BULK COPY WRITER SERVICE — Async bulk writer for the Forge pipeline.
//
// Ported from the Edge's DatabaseWriterService with identical architecture:
//   EnrichmentPipelineService → ForgeChannels.SqlWriter → this → SqlBulkCopy → PiXL.Raw
//
// KEY DIFFERENCES FROM EDGE's DatabaseWriterService:
//   - Reads from ForgeChannels.SqlWriter (enrichment pipeline output)
//   - No TryQueue() method (EnrichmentPipelineService writes directly to channel)
//   - ForgeFailoverWriter: when SQL is down, drains channel to JSONL on disk
//   - Replays failover files when SQL recovers
//   - Otherwise identical: circuit breaker, retry, dead-letter, custom DbDataReader
//
// CIRCUIT BREAKER:
//   Closed  → SQL is healthy. Records flow via SqlBulkCopy.
//   Open    → SQL is down/slow. Drains channel to JSONL failover files.
//              No SQL writes attempted. Probes after HalfOpenCooldown.
//   HalfOpen → Tries one batch. If it succeeds → Closed (replays failover).
//              If it fails → back to Open.
//
// TRIP CONDITIONS:
//   - SQL error 1105 (filegroup full) or 9002 (log full): immediate trip
//   - 2 consecutive batch failures (any error including timeouts): trip
//
// WHY DbDataReader INSTEAD OF DataTable?
//   DataTable.Clone() + N DataRow allocations per batch = significant GC pressure.
//   TrackingDataReader wraps the existing List<TrackingData> directly:
//     • Zero intermediate allocations (no DataTable, no DataRow objects)
//     • SqlBulkCopy calls Read() + GetValue() which index the list by ordinal
//     • For 100 records, eliminates ~100 DataRow objects (~200+ bytes each)
// ============================================================================

/// <summary>Circuit breaker state for the SQL writer.</summary>
public enum ForgeCircuitState { Closed, Open, HalfOpen }

/// <summary>
/// Background service that consumes <see cref="TrackingData"/> from the SQL writer
/// channel and bulk-writes batches to <c>PiXL.Raw</c> via <see cref="SqlBulkCopy"/>.
/// <para>
/// Ported from the Edge's <c>DatabaseWriterService</c> with the same circuit breaker,
/// retry, dead-letter, and zero-allocation <see cref="DbDataReader"/> patterns.
/// </para>
/// </summary>
public sealed class SqlBulkCopyWriterService : BackgroundService
{
    private readonly Channel<TrackingData> _channel;
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;
    private readonly ForgeMetrics _metrics;
    private readonly ForgeFailoverWriter _failoverWriter;

    // ── Circuit breaker ────────────────────────────────────────────────
    private volatile ForgeCircuitState _circuitState = ForgeCircuitState.Closed;
    private string? _lastTripReason;
    private int _consecutiveBatchFailures;
    private DateTime _circuitOpenedUtc;
    private static readonly TimeSpan HalfOpenCooldown = TimeSpan.FromMinutes(2);

    // ── Batch fill window ─────────────────────────────────────────────
    // After the first item arrives, wait up to this duration for more items.
    // At 70 rec/s, 150ms collects ~10 records per batch instead of ~1,
    // reducing per-record overhead from connection/TDS setup by ~10x.
    private static readonly TimeSpan BatchFillWindow = TimeSpan.FromMilliseconds(150);

    // ── Retry + dead-letter ────────────────────────────────────────────
    private const int MaxRetries = 3;
    private static readonly int[] RetryDelaysMs = [1000, 2000, 4000];
    private readonly string _deadLetterDir;
    private static readonly JsonSerializerOptions s_jsonOpts = new() { WriteIndented = false };

    /// <summary>Current circuit breaker state.</summary>
    public ForgeCircuitState Circuit => _circuitState;

    /// <summary>Human-readable reason the circuit last tripped.</summary>
    public string? LastTripReason => _lastTripReason;

    /// <summary>Current number of items buffered in the channel.</summary>
    public int QueueDepth => _channel.Reader.Count;

    /// <summary>
    /// Attempts to close the circuit breaker. Returns true if state changed.
    /// </summary>
    public bool TryReset()
    {
        if (_circuitState == ForgeCircuitState.Closed) return false;
        _circuitState = ForgeCircuitState.Closed;
        _consecutiveBatchFailures = 0;
        _logger.Info("Forge circuit breaker manually reset to Closed");
        return true;
    }

    /// <summary>
    /// SQL column names in ordinal order. Must match the column order in <c>PiXL.Raw</c>.
    /// </summary>
    internal static readonly string[] ColumnNames =
    [
        "CompanyID",    // [0] nvarchar
        "PiXLID",       // [1] nvarchar
        "IPAddress",    // [2] nvarchar
        "RequestPath",  // [3] nvarchar
        "QueryString",  // [4] nvarchar(max)
        "HeadersJson",  // [5] nvarchar(max)
        "UserAgent",    // [6] nvarchar(2000)
        "Referer",      // [7] nvarchar(2000)
        "ReceivedAt"    // [8] datetime2
    ];

    public SqlBulkCopyWriterService(
        ForgeChannels channels,
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger,
        ForgeMetrics metrics,
        ForgeFailoverWriter failoverWriter)
    {
        _channel = channels.SqlWriter;
        _settings = settings.Value;
        _logger = logger;
        _metrics = metrics;
        _failoverWriter = failoverWriter;
        _deadLetterDir = Path.Combine(AppContext.BaseDirectory, "DeadLetter");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _logger.Info("SqlBulkCopyWriterService started.");

        // Replay any dead-letter files from a prior crash
        await ReplayDeadLettersAsync(stoppingToken);

        // Replay any failover JSONL files from a prior SQL outage
        await ReplayFailoverFilesAsync(stoppingToken);

        var batch = new List<TrackingData>(_settings.BatchSize);
        var reader = _channel.Reader;

        while (!stoppingToken.IsCancellationRequested)
        {
            batch.Clear();

            // ── Circuit breaker: drain to failover when open, probe when half-open ──
            if (_circuitState == ForgeCircuitState.Open)
            {
                if (DateTime.UtcNow - _circuitOpenedUtc >= HalfOpenCooldown)
                {
                    _circuitState = ForgeCircuitState.HalfOpen;
                    _logger.Info("Forge circuit breaker → HalfOpen, probing next batch");
                    // Fall through to read a batch and try writing it
                }
                else
                {
                    // SQL is down — drain the channel to failover files so enriched
                    // records are preserved on disk instead of overflowing the channel.
                    DrainChannelToFailover();
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }
            }

            try
            {
                if (await reader.WaitToReadAsync(stoppingToken))
                {
                    // Greedy drain: grab everything available right now.
                    while (batch.Count < _settings.BatchSize && reader.TryRead(out var item))
                    {
                        batch.Add(item);
                    }

                    // Batch fill window: if we haven't hit BatchSize, wait briefly
                    // for more items to arrive. At 70 rec/s, 150ms collects ~10 records
                    // instead of writing 1-record batches 70 times per second.
                    if (batch.Count < _settings.BatchSize)
                    {
                        using var fillCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        fillCts.CancelAfter(BatchFillWindow);

                        try
                        {
                            while (batch.Count < _settings.BatchSize
                                && await reader.WaitToReadAsync(fillCts.Token))
                            {
                                while (batch.Count < _settings.BatchSize && reader.TryRead(out var extra))
                                {
                                    batch.Add(extra);
                                }
                            }
                        }
                        catch (OperationCanceledException) when (fillCts.IsCancellationRequested)
                        {
                            // Fill window expired — write what we have
                        }
                    }

                    if (batch.Count > 0)
                        await WriteBatchAsync(batch, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error($"Unexpected error in Forge write loop: {ex.Message}");
                await Task.Delay(1000, stoppingToken);
            }
        }

        // Graceful shutdown: drain remaining items
        await DrainChannelAsync(batch);
    }

    /// <summary>
    /// Drains remaining channel items during graceful shutdown.
    /// Attempts SQL write first; if that fails, persists to failover.
    /// </summary>
    private async Task DrainChannelAsync(List<TrackingData> batch)
    {
        _channel.Writer.TryComplete();
        _logger.Info("Forge SQL writer shutting down. Draining remaining items...");

        var deadline = DateTime.UtcNow.AddSeconds(_settings.ShutdownTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            batch.Clear();

            while (batch.Count < _settings.BatchSize && _channel.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            if (batch.Count > 0)
            {
                if (_circuitState == ForgeCircuitState.Open)
                {
                    // SQL is down — persist to failover instead of trying SQL
                    _failoverWriter.AppendBatch(batch);
                }
                else
                {
                    await WriteBatchAsync(batch, CancellationToken.None);
                }
            }
            else
                break;
        }

        var remaining = _channel.Reader.Count;
        if (remaining > 0)
        {
            // Last resort — write any remaining records to failover
            var finalBatch = new List<TrackingData>(remaining);
            while (_channel.Reader.TryRead(out var item))
                finalBatch.Add(item);

            if (finalBatch.Count > 0)
            {
                _failoverWriter.AppendBatch(finalBatch);
                _logger.Warning($"Forge shutdown — {finalBatch.Count} remaining items persisted to failover");
            }
        }
        else
            _logger.Info("Forge queue drained successfully");

        // Ensure failover writer flushes to disk
        _failoverWriter.Flush();
    }

    /// <summary>
    /// Writes a batch via SqlBulkCopy with retry and dead-letter on failure.
    /// </summary>
    private async Task WriteBatchAsync(List<TrackingData> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var delay = RetryDelaysMs[Math.Min(attempt - 1, RetryDelaysMs.Length - 1)];
                _logger.Warning($"Retrying Forge batch write (attempt {attempt + 1}/{MaxRetries + 1}) after {delay}ms");
                await Task.Delay(delay, ct);
            }

            try
            {
                var ts = ForgeMetrics.StartTimer();

                using var dataReader = new TrackingDataReader(batch);
                using var bulkCopy = new SqlBulkCopy(_settings.ConnectionString);
                bulkCopy.DestinationTableName = "PiXL.Raw";
                bulkCopy.BatchSize = batch.Count;
                bulkCopy.BulkCopyTimeout = _settings.BulkCopyTimeoutSeconds;

                var cols = ColumnNames;
                for (var i = 0; i < cols.Length; i++)
                    bulkCopy.ColumnMappings.Add(i, cols[i]);

                await bulkCopy.WriteToServerAsync(dataReader, ct);

                _metrics.Record(Stage.SqlBulkCopy, ts, batch.Count);
                OnWriteSuccess();
                _logger.Debug($"Forge wrote {batch.Count} records");
                return;
            }
            catch (SqlException sqlEx) when (IsCircuitTripError(sqlEx))
            {
                ClassifyAndTrip(sqlEx);
                OnBatchFailure();
                break;
            }
            catch (SqlException sqlEx) when (attempt < MaxRetries)
            {
                _logger.Warning($"Forge SQL error on batch attempt {attempt + 1}: [{sqlEx.Number}] {sqlEx.Message}");
            }
            catch (SqlException sqlEx)
            {
                _logger.Error($"Forge: all {MaxRetries + 1} attempts failed for batch of {batch.Count}: {sqlEx.Message}");
                ClassifyAndTrip(sqlEx);
                OnBatchFailure();
                break;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                _logger.Warning($"Forge non-SQL error on batch attempt {attempt + 1}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Forge: all {MaxRetries + 1} attempts failed for batch of {batch.Count}: {ex.Message}");
                OnBatchFailure();
                break;
            }
        }

        // All retries exhausted — persist to dead-letter file
        await WriteDeadLetterAsync(batch);
    }

    private static bool IsCircuitTripError(SqlException sqlEx)
    {
        foreach (SqlError err in sqlEx.Errors)
        {
            if (err.Number is 1105 or 9002) return true;
        }
        return false;
    }

    private async Task WriteDeadLetterAsync(List<TrackingData> batch)
    {
        try
        {
            Directory.CreateDirectory(_deadLetterDir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var fileName = $"deadletter_{timestamp}_{Guid.NewGuid():N}.json";
            var filePath = Path.Combine(_deadLetterDir, fileName);

            var json = JsonSerializer.Serialize(batch, s_jsonOpts);
            await File.WriteAllTextAsync(filePath, json);

            _logger.Warning($"Forge dead-lettered {batch.Count} records to {fileName}");
        }
        catch (Exception ex)
        {
            _logger.Error($"CRITICAL: Forge failed to dead-letter {batch.Count} records — DATA LOST: {ex.Message}");
        }
    }

    private async Task ReplayDeadLettersAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_deadLetterDir)) return;

        var files = Directory.GetFiles(_deadLetterDir, "deadletter_*.json");
        if (files.Length == 0) return;

        _logger.Info($"Forge found {files.Length} dead-letter file(s) to replay");

        foreach (var file in files.OrderBy(f => f))
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var batch = JsonSerializer.Deserialize<List<TrackingData>>(json);

                if (batch is null || batch.Count == 0)
                {
                    File.Delete(file);
                    continue;
                }

                _logger.Info($"Forge replaying {batch.Count} dead-lettered records from {Path.GetFileName(file)}");
                await WriteBatchAsync(batch, ct);

                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex)
            {
                _logger.Error($"Forge failed to replay {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    private void ClassifyAndTrip(SqlException sqlEx)
    {
        foreach (SqlError err in sqlEx.Errors)
        {
            switch (err.Number)
            {
                case 1105:
                    var msg = err.Message;
                    var isPrimary = msg.Contains("'PRIMARY'", StringComparison.OrdinalIgnoreCase);
                    if (isPrimary)
                    {
                        TripCircuit("PrimaryFilegroupFull",
                            $"SQL error 1105 on PRIMARY filegroup — object on wrong filegroup. Detail: {msg}");
                    }
                    else
                    {
                        TripCircuit("DiskFull",
                            $"SQL error 1105 — filegroup full (disk space). Detail: {msg}");
                    }
                    return;

                case 9002:
                    TripCircuit("TransactionLogFull",
                        $"SQL error 9002 — transaction log full. Detail: {err.Message}");
                    return;

                case 1205:
                    _logger.Warning($"Forge deadlock on batch write: {err.Message}");
                    return;
            }
        }

        // Don't increment here — OnBatchFailure handles counting
        _logger.Error($"Forge SQL error writing batch: {sqlEx.Message}");
    }

    /// <summary>
    /// Called when a batch exhausts all retry attempts. Tracks consecutive
    /// batch failures and trips the circuit breaker after 2 in a row.
    /// This is counted per-BATCH (not per-attempt), so a single batch
    /// failing 4 times counts as 1 batch failure.
    /// </summary>
    private void OnBatchFailure()
    {
        _consecutiveBatchFailures++;

        if (_circuitState != ForgeCircuitState.Open && _consecutiveBatchFailures >= 2)
        {
            TripCircuit("ConsecutiveBatchFailures",
                $"{_consecutiveBatchFailures} consecutive batches failed. " +
                "Switching to failover — enriched records will be persisted to disk.");
        }
    }

    private void TripCircuit(string reason, string detail)
    {
        _circuitState = ForgeCircuitState.Open;
        _circuitOpenedUtc = DateTime.UtcNow;
        _lastTripReason = reason;
        _logger.Error($"Forge circuit breaker TRIPPED → Open. Reason: {reason}. {detail}");
    }

    private void OnWriteSuccess()
    {
        var previousState = _circuitState;

        if (_circuitState == ForgeCircuitState.HalfOpen)
        {
            _circuitState = ForgeCircuitState.Closed;
            _consecutiveBatchFailures = 0;
            _logger.Info("Forge circuit breaker → Closed (probe write succeeded, SQL recovered)");

            // Replay failover files now that SQL is back — fire-and-forget.
            // This runs on a separate task so the write loop isn't blocked.
            _ = Task.Run(async () =>
            {
                try
                {
                    await ReplayFailoverFilesAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Forge failover replay after recovery failed: {ex.Message}");
                }
            });
        }
        else if (_consecutiveBatchFailures > 0)
        {
            _consecutiveBatchFailures = 0;
        }
    }

    /// <summary>
    /// Drains all pending records from the SQL writer channel directly to
    /// failover JSONL files. Called when the circuit breaker is Open to prevent
    /// channel overflow while SQL is unavailable. Records are already enriched.
    /// </summary>
    private void DrainChannelToFailover()
    {
        var drainBatch = new List<TrackingData>(100);

        while (_channel.Reader.TryRead(out var item))
        {
            drainBatch.Add(item);

            if (drainBatch.Count >= 100)
            {
                _failoverWriter.AppendBatch(drainBatch);
                drainBatch.Clear();
            }
        }

        if (drainBatch.Count > 0)
            _failoverWriter.AppendBatch(drainBatch);
    }

    /// <summary>
    /// Replays failover JSONL files from a prior SQL outage. Each file contains
    /// enriched records that were persisted to disk instead of being dropped.
    /// Writes directly via SqlBulkCopy, bypassing the channel.
    /// </summary>
    private async Task ReplayFailoverFilesAsync(CancellationToken ct)
    {
        var files = _failoverWriter.GetFailoverFiles();
        if (files.Length == 0) return;

        _logger.Info($"Forge found {files.Length} failover file(s) to replay");
        var totalReplayed = 0;

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            var records = _failoverWriter.ReadFile(file);
            if (records.Count == 0)
            {
                _failoverWriter.DeleteFile(file);
                continue;
            }

            _logger.Info($"Forge replaying {records.Count} failover records from {Path.GetFileName(file)}");

            // Write in batches through the normal WriteBatchAsync path (retry + dead-letter)
            for (var i = 0; i < records.Count; i += _settings.BatchSize)
            {
                if (ct.IsCancellationRequested) break;

                var batchEnd = Math.Min(i + _settings.BatchSize, records.Count);
                var batch = records.GetRange(i, batchEnd - i);
                await WriteBatchAsync(batch, ct);

                // If circuit tripped during replay, stop — we'll retry on next recovery
                if (_circuitState == ForgeCircuitState.Open)
                {
                    _logger.Warning($"Forge circuit tripped during failover replay — aborting replay, will retry on next recovery");
                    return;
                }
            }

            totalReplayed += records.Count;
            _failoverWriter.DeleteFile(file);
        }

        if (totalReplayed > 0)
            _logger.Info($"Forge failover replay complete: {totalReplayed:N0} records recovered");
    }

    // ========================================================================
    // TRACKING DATA READER — Zero-allocation DbDataReader over List<TrackingData>
    // ========================================================================

    /// <summary>
    /// Lightweight <see cref="DbDataReader"/> that wraps a <c>List&lt;TrackingData&gt;</c>
    /// for direct consumption by <see cref="SqlBulkCopy"/>. No intermediate allocations.
    /// </summary>
    private sealed class TrackingDataReader(List<TrackingData> batch) : DbDataReader
    {
        private int _index = -1;

        public override int FieldCount => ColumnNames.Length;
        public override int RecordsAffected => -1;
        public override bool HasRows => batch.Count > 0;
        public override bool IsClosed => _index >= batch.Count;
        public override int Depth => 0;
        public override bool Read() => ++_index < batch.Count;
        public override bool NextResult() => false;

        public override object GetValue(int ordinal)
        {
            var d = batch[_index];
            return ordinal switch
            {
                0 => (object?)d.CompanyID ?? DBNull.Value,
                1 => (object?)d.PiXLID ?? DBNull.Value,
                2 => (object?)d.IPAddress ?? DBNull.Value,
                3 => (object?)d.RequestPath ?? DBNull.Value,
                4 => (object?)d.QueryString ?? DBNull.Value,
                5 => (object?)d.HeadersJson ?? DBNull.Value,
                6 => (object?)d.UserAgent ?? DBNull.Value,
                7 => (object?)d.Referer ?? DBNull.Value,
                8 => d.ReceivedAt,
                _ => throw new IndexOutOfRangeException()
            };
        }

        public override bool IsDBNull(int ordinal)
        {
            if (ordinal == 8) return false;
            var d = batch[_index];
            return ordinal switch
            {
                0 => d.CompanyID is null,
                1 => d.PiXLID is null,
                2 => d.IPAddress is null,
                3 => d.RequestPath is null,
                4 => d.QueryString is null,
                5 => d.HeadersJson is null,
                6 => d.UserAgent is null,
                7 => d.Referer is null,
                _ => true
            };
        }

        public override string GetName(int ordinal) => ColumnNames[ordinal];
        public override int GetOrdinal(string name) => Array.IndexOf(ColumnNames, name);
        public override string GetDataTypeName(int ordinal) => ordinal == 8 ? "datetime2" : "nvarchar";
        public override Type GetFieldType(int ordinal) => ordinal == 8 ? typeof(DateTime) : typeof(string);
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));

        public override int GetValues(object[] values)
        {
            var count = Math.Min(values.Length, FieldCount);
            for (var i = 0; i < count; i++) values[i] = GetValue(i);
            return count;
        }

        public override string GetString(int ordinal)
        {
            var d = batch[_index];
            return (ordinal switch
            {
                0 => d.CompanyID, 1 => d.PiXLID, 2 => d.IPAddress,
                3 => d.RequestPath, 4 => d.QueryString, 5 => d.HeadersJson,
                6 => d.UserAgent, 7 => d.Referer, _ => null
            }) ?? throw new InvalidCastException();
        }

        public override DateTime GetDateTime(int ordinal) =>
            ordinal == 8 ? batch[_index].ReceivedAt : throw new InvalidCastException();

        // Abstract stubs — never called by SqlBulkCopy for PiXL.Raw column types
        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
        public override byte GetByte(int ordinal) => throw new NotSupportedException();
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => throw new NotSupportedException();
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();
        public override double GetDouble(int ordinal) => throw new NotSupportedException();
        public override float GetFloat(int ordinal) => throw new NotSupportedException();
        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
        public override short GetInt16(int ordinal) => throw new NotSupportedException();
        public override int GetInt32(int ordinal) => throw new NotSupportedException();
        public override long GetInt64(int ordinal) => throw new NotSupportedException();
        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    }
}
