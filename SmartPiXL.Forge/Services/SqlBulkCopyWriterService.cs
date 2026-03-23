using System.Data;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Models;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// SQL BULK COPY WRITER SERVICE — Merged pipeline: parse + write in one step.
//
// Data flow:
//   EnrichmentPipelineService → ForgeChannels.SqlWriter → this → parse QS →
//   SqlBulkCopy → PiXL.Parsed (all 230 columns in a single write)
//
// PREVIOUS ARCHITECTURE (eliminated):
//   Forge → SqlBulkCopy → PiXL.Raw (9 cols) → ParsedBulkInsertService →
//   PiXL.Parsed (229 cols). That two-step pipeline is gone. PiXL.Raw is no
//   longer written to by the Forge. QueryString and HeadersJson are now stored
//   directly in PiXL.Parsed for re-parse capability.
//
// KEY DESIGN DECISIONS:
//   • Parse inline: ParsedRecordParser.Parse() runs ~1μs/record — negligible
//     overhead vs the SQL round-trip saved by eliminating the Raw pipeline.
//   • SourceId auto-generated: SQL SEQUENCE (PiXL.HitSequence) provides IDs
//     via DEFAULT constraint. BulkCopy omits SourceId from column mappings.
//   • Connection reuse: Single SqlConnection kept open across batches.
//     Reopened automatically on error. Avoids pool lookup + TDS setup per batch.
//   • Deadlock handling: Error 1205 triggers retry (not circuit trip). Deadlocks
//     are transient contention, not infrastructure failures.
//   • Batch fill metrics: Records batch fill percentage for capacity planning.
//
// CIRCUIT BREAKER:
//   Same Closed/Open/HalfOpen pattern as before. Only infrastructure errors
//   (1105 filegroup full, 9002 log full) and consecutive batch failures trip it.
// ============================================================================

/// <summary>Circuit breaker state for the SQL writer.</summary>
public enum ForgeCircuitState { Closed, Open, HalfOpen }

/// <summary>
/// Background service that consumes <see cref="TrackingData"/> from the SQL writer
/// channel, parses all fields inline via <see cref="ParsedRecordParser"/>, and
/// bulk-writes batches to <c>PiXL.Parsed</c> via <see cref="SqlBulkCopy"/>.
/// </summary>
public sealed class SqlBulkCopyWriterService : BackgroundService
{
    private readonly Channel<TrackingData> _channel;
    private readonly TrackingSettings _trackingSettings;
    private readonly ForgeSettings _forgeSettings;
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
    private static readonly TimeSpan BatchFillWindow = TimeSpan.FromMilliseconds(50);

    // ── Retry + dead-letter ────────────────────────────────────────────
    private const int MaxRetries = 3;
    private static readonly int[] RetryDelaysMs = [1000, 2000, 4000];
    private readonly string _deadLetterDir;
    private static readonly JsonSerializerOptions s_jsonOpts = new() { WriteIndented = false };

    // ── Connection reuse ───────────────────────────────────────────────
    private SqlConnection? _conn;

    // ── Drain buffer (reused to avoid allocation per drain cycle) ──────
    private readonly List<TrackingData> _drainBuffer = new(500);

    // ── Lifetime health counters (cumulative, never reset) ───────────────
    private long _lifetimeBatches;
    private long _lifetimeFailures;

    /// <summary>Total successful batch writes since process start.</summary>
    public long LifetimeBatches => Volatile.Read(ref _lifetimeBatches);

    /// <summary>Total failed batch attempts since process start.</summary>
    public long LifetimeFailures => Volatile.Read(ref _lifetimeFailures);

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

    public SqlBulkCopyWriterService(
        ForgeChannels channels,
        IOptions<TrackingSettings> trackingSettings,
        IOptions<ForgeSettings> forgeSettings,
        ITrackingLogger logger,
        ForgeMetrics metrics,
        ForgeFailoverWriter failoverWriter)
    {
        _channel = channels.SqlWriter;
        _trackingSettings = trackingSettings.Value;
        _forgeSettings = forgeSettings.Value;
        _logger = logger;
        _metrics = metrics;
        _failoverWriter = failoverWriter;
        _deadLetterDir = _forgeSettings.DeadLetterDirectory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _logger.Info($"SqlBulkCopyWriterService started. " +
            $"Target=PiXL.Parsed, BatchSize={_forgeSettings.ForgeBatchSize}, " +
            $"DeadLetter={_deadLetterDir}");

        var batchSize = _forgeSettings.ForgeBatchSize;
        var batch = new List<TrackingData>(batchSize);
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
                }
                else
                {
                    DrainChannelToFailover();
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }
            }

            try
            {
                if (await reader.WaitToReadAsync(stoppingToken))
                {
                    // Greedy drain
                    while (batch.Count < batchSize && reader.TryRead(out var item))
                        batch.Add(item);

                    // Batch fill window: wait briefly for more items
                    if (batch.Count < batchSize)
                    {
                        using var fillCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        fillCts.CancelAfter(BatchFillWindow);

                        try
                        {
                            while (batch.Count < batchSize
                                && await reader.WaitToReadAsync(fillCts.Token))
                            {
                                while (batch.Count < batchSize && reader.TryRead(out var extra))
                                    batch.Add(extra);
                            }
                        }
                        catch (OperationCanceledException) when (fillCts.IsCancellationRequested)
                        {
                            // Fill window expired — write what we have
                        }
                    }

                    if (batch.Count > 0)
                    {
                        _metrics.RecordBatchFill(batch.Count, batchSize);
                        await WriteBatchAsync(batch, stoppingToken);
                    }
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

        // Graceful shutdown
        await DrainChannelAsync(batch, batchSize);

        // Dispose persistent connection
        _conn?.Dispose();
        _conn = null;
    }

    // ════════════════════════════════════════════════════════════════════
    // CONNECTION MANAGEMENT — Reuse across batches, reopen on error
    // ════════════════════════════════════════════════════════════════════

    private async ValueTask<SqlConnection> EnsureConnectionAsync(CancellationToken ct)
    {
        if (_conn is { State: ConnectionState.Open })
            return _conn;

        _conn?.Dispose();
        _conn = new SqlConnection(_trackingSettings.ConnectionString);
        await _conn.OpenAsync(ct);
        return _conn;
    }

    private void InvalidateConnection()
    {
        _conn?.Dispose();
        _conn = null;
    }

    // ════════════════════════════════════════════════════════════════════
    // BATCH WRITING — Parse inline + BulkCopy to PiXL.Parsed
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes a batch: parse all records inline via ParsedRecordParser, then
    /// BulkCopy to PiXL.Parsed with retry and dead-letter on failure.
    /// </summary>
    private async Task WriteBatchAsync(List<TrackingData> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        // Parse all records inline (~1μs per record, ~0.5ms for 500 records)
        var parsed = new List<object?[]>(batch.Count);
        foreach (var td in batch)
        {
            parsed.Add(ParsedRecordParser.Parse(
                td.CompanyID, td.PiXLID, td.IPAddress,
                td.ReceivedAt, td.RequestPath,
                td.QueryString, td.HeadersJson,
                td.UserAgent, td.Referer));
        }

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

                var conn = await EnsureConnectionAsync(ct);
                using var dataReader = new ParsedDataReader(parsed);
                using var bulkCopy = new SqlBulkCopy(conn)
                {
                    DestinationTableName = "PiXL.Parsed",
                    BatchSize = parsed.Count,
                    BulkCopyTimeout = _trackingSettings.BulkCopyTimeoutSeconds
                };

                var cols = ParsedRecordParser.ColumnNames;
                for (var i = 0; i < cols.Length; i++)
                    bulkCopy.ColumnMappings.Add(i, cols[i]);

                await bulkCopy.WriteToServerAsync(dataReader, ct);

                _metrics.Record(Stage.SqlBulkCopy, ts, batch.Count);
                OnWriteSuccess();
                _logger.Debug($"Forge wrote {batch.Count} records to PiXL.Parsed");
                return;
            }
            catch (SqlException sqlEx) when (IsCircuitTripError(sqlEx))
            {
                InvalidateConnection();
                ClassifyAndTrip(sqlEx);
                OnBatchFailure();
                break;
            }
            catch (SqlException sqlEx) when (IsDeadlock(sqlEx))
            {
                // Deadlock (1205): transient contention — always retry, never trip circuit
                _logger.Warning($"Forge deadlock on attempt {attempt + 1}: {sqlEx.Message}");
                InvalidateConnection();
                if (attempt >= MaxRetries)
                {
                    _logger.Error($"Forge: deadlock persisted after {MaxRetries + 1} attempts for {batch.Count} records");
                    break;
                }
                continue;
            }
            catch (SqlException sqlEx) when (attempt < MaxRetries)
            {
                _logger.Warning($"Forge SQL error on batch attempt {attempt + 1}: [{sqlEx.Number}] {sqlEx.Message}");
                InvalidateConnection();
            }
            catch (SqlException sqlEx)
            {
                _logger.Error($"Forge: all {MaxRetries + 1} attempts failed for batch of {batch.Count}: {sqlEx.Message}");
                InvalidateConnection();
                ClassifyAndTrip(sqlEx);
                OnBatchFailure();
                break;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                _logger.Warning($"Forge non-SQL error on batch attempt {attempt + 1}: {ex.Message}");
                InvalidateConnection();
            }
            catch (Exception ex)
            {
                _logger.Error($"Forge: all {MaxRetries + 1} attempts failed for batch of {batch.Count}: {ex.Message}");
                InvalidateConnection();
                OnBatchFailure();
                break;
            }
        }

        // All retries exhausted — persist to dead-letter
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

    private static bool IsDeadlock(SqlException sqlEx)
    {
        foreach (SqlError err in sqlEx.Errors)
        {
            if (err.Number == 1205) return true;
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════════════
    // DEAD-LETTER PERSISTENCE
    // ════════════════════════════════════════════════════════════════════

    private async Task WriteDeadLetterAsync(List<TrackingData> batch)
    {
        try
        {
            Directory.CreateDirectory(_deadLetterDir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var fileName = $"deadletter_{timestamp}_{Guid.NewGuid():N}.jsonl";
            var filePath = Path.Combine(_deadLetterDir, fileName);

            await using var writer = new StreamWriter(filePath, append: false, Encoding.UTF8);
            foreach (var record in batch)
            {
                var line = JsonSerializer.Serialize(record, s_jsonOpts);
                await writer.WriteLineAsync(line);
            }

            _logger.Warning($"Forge dead-lettered {batch.Count} records to {fileName}");
        }
        catch (Exception ex)
        {
            _logger.Error($"CRITICAL: Forge failed to dead-letter {batch.Count} records — DATA LOST: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // CIRCUIT BREAKER
    // ════════════════════════════════════════════════════════════════════

    private void ClassifyAndTrip(SqlException sqlEx)
    {
        foreach (SqlError err in sqlEx.Errors)
        {
            switch (err.Number)
            {
                case 1105:
                    var msg = err.Message;
                    var isPrimary = msg.Contains("'PRIMARY'", StringComparison.OrdinalIgnoreCase);
                    TripCircuit(
                        isPrimary ? "PrimaryFilegroupFull" : "DiskFull",
                        $"SQL error 1105 — {(isPrimary ? "object on wrong filegroup" : "filegroup full")}. Detail: {msg}");
                    return;

                case 9002:
                    TripCircuit("TransactionLogFull",
                        $"SQL error 9002 — transaction log full. Detail: {err.Message}");
                    return;
            }
        }

        _logger.Error($"Forge SQL error writing batch: {sqlEx.Message}");
    }

    private void OnBatchFailure()
    {
        Interlocked.Increment(ref _lifetimeFailures);
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
        InvalidateConnection();
        _logger.Error($"Forge circuit breaker TRIPPED → Open. Reason: {reason}. {detail}");
    }

    private void OnWriteSuccess()
    {
        Interlocked.Increment(ref _lifetimeBatches);

        if (_circuitState == ForgeCircuitState.HalfOpen)
        {
            _circuitState = ForgeCircuitState.Closed;
            _consecutiveBatchFailures = 0;
            _logger.Info("Forge circuit breaker → Closed (probe write succeeded, SQL recovered)");
        }
        else if (_consecutiveBatchFailures > 0)
        {
            _consecutiveBatchFailures = 0;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // FAILOVER DRAIN — Reuses buffer to avoid allocation per cycle
    // ════════════════════════════════════════════════════════════════════

    private void DrainChannelToFailover()
    {
        _drainBuffer.Clear();

        while (_channel.Reader.TryRead(out var item))
        {
            _drainBuffer.Add(item);

            if (_drainBuffer.Count >= _forgeSettings.ForgeBatchSize)
            {
                _failoverWriter.AppendBatch(_drainBuffer);
                _drainBuffer.Clear();
            }
        }

        if (_drainBuffer.Count > 0)
            _failoverWriter.AppendBatch(_drainBuffer);
    }

    // ════════════════════════════════════════════════════════════════════
    // GRACEFUL SHUTDOWN DRAIN
    // ════════════════════════════════════════════════════════════════════

    private async Task DrainChannelAsync(List<TrackingData> batch, int batchSize)
    {
        _channel.Writer.TryComplete();
        _logger.Info("Forge SQL writer shutting down. Draining remaining items...");

        var deadline = DateTime.UtcNow.AddSeconds(_trackingSettings.ShutdownTimeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            batch.Clear();

            while (batch.Count < batchSize && _channel.Reader.TryRead(out var item))
                batch.Add(item);

            if (batch.Count > 0)
            {
                if (_circuitState == ForgeCircuitState.Open)
                    _failoverWriter.AppendBatch(batch);
                else
                    await WriteBatchAsync(batch, CancellationToken.None);
            }
            else
                break;
        }

        var remaining = _channel.Reader.Count;
        if (remaining > 0)
        {
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

        _failoverWriter.Flush();
    }
}
