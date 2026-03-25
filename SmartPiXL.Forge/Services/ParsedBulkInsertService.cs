using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// PARSED BULK INSERT SERVICE — .NET backfill for PiXL.Parsed.
//
// RETIRED: PiXL.Raw has been dropped. This service was a backfill that read
// from PiXL.Raw and parsed query strings in .NET. It is kept for reference but
// is disabled in Program.cs. Forge now writes directly to PiXL.Parsed via
// SqlBulkCopyWriterService.
// ============================================================================

/// <summary>
/// RETIRED: Background service that formerly read <c>PiXL.Raw</c> in batches.
/// PiXL.Raw has been dropped. This service is disabled in Program.cs.
/// </summary>
public sealed class ParsedBulkInsertService : BackgroundService
{
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;

    /// <summary>Rows per batch. Balances memory (~400MB QS data) vs throughput.</summary>
    private const int DefaultBatchSize = 50_000;

    /// <summary>Seconds to sleep when caught up (no new rows).</summary>
    private static readonly TimeSpan CaughtUpDelay = TimeSpan.FromSeconds(30);

    /// <summary>Seconds to wait before starting (let other services initialize).</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    public ParsedBulkInsertService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield(); // Don't block host startup

        try { await Task.Delay(StartupDelay, ct); }
        catch (OperationCanceledException) { return; }

        _logger.Info("ParsedBulkInsert: Starting backfill service.");

        long totalParsed = 0;
        var serviceStart = Stopwatch.GetTimestamp();
        int consecutiveEmpty = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var batchResult = await ProcessBatchAsync(DefaultBatchSize, ct);

                if (batchResult.Parsed == 0)
                {
                    consecutiveEmpty++;
                    if (consecutiveEmpty == 1)
                        _logger.Info("ParsedBulkInsert: Caught up — no new rows to parse.");

                    try { await Task.Delay(CaughtUpDelay, ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                consecutiveEmpty = 0;
                totalParsed += batchResult.Parsed;

                var elapsed = Stopwatch.GetElapsedTime(serviceStart);
                var rate = totalParsed / elapsed.TotalSeconds;
                var remaining = batchResult.Remaining;
                var eta = remaining > 0 && rate > 0
                    ? TimeSpan.FromSeconds(remaining / rate)
                    : TimeSpan.Zero;

                _logger.Info(
                    $"ParsedBulkInsert: batch={batchResult.Parsed:N0} " +
                    $"({batchResult.ReadMs}ms read, {batchResult.ParseMs}ms parse, " +
                    $"{batchResult.BulkCopyMs}ms bulk, {batchResult.EtlMs}ms etl) | " +
                    $"total={totalParsed:N0} | rate={rate:N0}/sec | " +
                    $"remaining={remaining:N0} | ETA={eta:hh\\:mm\\:ss}");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error($"ParsedBulkInsert: Batch failed — {ex.Message}");

                // Back off on error to avoid tight retry loops
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.Info($"ParsedBulkInsert: Service stopped. Total parsed: {totalParsed:N0}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // BATCH PROCESSING — Read → Parse → BulkCopy → ETL Phase 9–13
    // ════════════════════════════════════════════════════════════════════════

    private async Task<BatchResult> ProcessBatchAsync(int batchSize, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct);

        // ── Step 1: Read watermark + max Raw Id ────────────────────────────
        var (lastProcessedId, maxRawId) = await GetRangeAsync(conn, ct);

        if (lastProcessedId >= maxRawId)
            return BatchResult.Empty(maxRawId - lastProcessedId);

        var rangeEnd = Math.Min(lastProcessedId + batchSize, maxRawId);

        // ── Step 2: Check for pre-existing Parsed rows (crash recovery) ────
        var existingIds = await GetExistingParsedIdsAsync(
            conn, lastProcessedId, rangeEnd, ct);

        // ── Step 3: Read from Parsed (legacy — PiXL.Raw dropped) ──────────────
        var sw = Stopwatch.StartNew();

        await using var readCmd = conn.CreateCommand();
        readCmd.CommandText = """
            SELECT Id, CompanyId, PiXLId, IPAddress, ReceivedAt,
                   RequestPath, QueryString, UserAgent, Referer
            FROM PiXL.Parsed
            WHERE Id > @LastId AND Id <= @MaxId
            ORDER BY Id
            """;
        readCmd.Parameters.AddWithValue("@LastId", lastProcessedId);
        readCmd.Parameters.AddWithValue("@MaxId", rangeEnd);
        readCmd.CommandTimeout = 120;

        var parsed = new List<object?[]>(batchSize);

        await using var reader = await readCmd.ExecuteReaderAsync(ct);
        var readMs = sw.ElapsedMilliseconds;

        sw.Restart();
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);

            // Skip rows already in Parsed (crash recovery / partial batch)
            if (existingIds.Contains(id)) continue;

            parsed.Add(ParsedRecordParser.Parse(
                reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1),   // CompanyID
                reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),   // PiXLID
                reader.IsDBNull(3) ? null : reader.GetString(3),        // IPAddress
                reader.GetDateTime(4),                                   // ReceivedAt
                reader.IsDBNull(5) ? null : reader.GetString(5),        // RequestPath
                reader.IsDBNull(6) ? null : reader.GetString(6),        // QueryString
                null,                                                    // HeadersJson (not in Raw)
                reader.IsDBNull(7) ? null : reader.GetString(7),        // UserAgent
                reader.IsDBNull(8) ? null : reader.GetString(8)));      // Referer
        }
        await reader.CloseAsync();
        var parseMs = sw.ElapsedMilliseconds;

        if (parsed.Count == 0)
        {
            // All rows in this range were already parsed — just run Phase 9-13
            // and advance watermark.
            sw.Restart();
            await CallDimensionsAndAdvanceWatermarkAsync(
                conn, lastProcessedId, rangeEnd, 0, ct);
            return BatchResult.Empty(maxRawId - rangeEnd);
        }

        // ── Step 4: BulkCopy to PiXL.Parsed ───────────────────────────────
        sw.Restart();

        using var bulkCopy = new SqlBulkCopy(conn)
        {
            DestinationTableName = "PiXL.Parsed",
            BatchSize = parsed.Count,
            BulkCopyTimeout = 180
        };

        for (int i = 0; i < ParsedRecordParser.ColumnCount; i++)
            bulkCopy.ColumnMappings.Add(i, ParsedRecordParser.ColumnNames[i]);

        using var bulkReader = new ParsedDataReader(parsed);
        await bulkCopy.WriteToServerAsync(bulkReader, ct);

        var bulkCopyMs = sw.ElapsedMilliseconds;

        // ── Step 5: Phase 9–13 + advance watermark ─────────────────────────
        sw.Restart();
        await CallDimensionsAndAdvanceWatermarkAsync(
            conn, lastProcessedId, rangeEnd, parsed.Count, ct);
        var etlMs = sw.ElapsedMilliseconds;

        return new BatchResult(
            Parsed: parsed.Count,
            Remaining: maxRawId - rangeEnd,
            ReadMs: readMs,
            ParseMs: parseMs,
            BulkCopyMs: bulkCopyMs,
            EtlMs: etlMs);
    }

    // ════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads the watermark only (no self-healing — we manage the range ourselves).
    /// Returns (lastProcessedId, maxParsedId).
    /// </summary>
    private static async Task<(long LastProcessedId, long MaxRawId)> GetRangeAsync(
        SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT LastProcessedId FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits'),
                (SELECT ISNULL(MAX(SourceId), 0) FROM PiXL.Parsed)
            """;
        cmd.CommandTimeout = 60;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (0, 0);

        var lastId = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
        var maxId = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
        return (lastId, maxId);
    }

    /// <summary>
    /// Checks which SourceIds already exist in PiXL.Parsed for the given range.
    /// Used for crash recovery: if a previous batch wrote Parsed but the proc didn't
    /// advance the watermark, the next iteration skips those rows.
    /// </summary>
    private static async Task<HashSet<long>> GetExistingParsedIdsAsync(
        SqlConnection conn, long fromId, long toId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SourceId FROM PiXL.Parsed
            WHERE SourceId > @FromId AND SourceId <= @ToId
            """;
        cmd.Parameters.AddWithValue("@FromId", fromId);
        cmd.Parameters.AddWithValue("@ToId", toId);
        cmd.CommandTimeout = 60;

        var ids = new HashSet<long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetInt64(0));

        return ids;
    }

    /// <summary>
    /// Calls <c>ETL.usp_ProcessDimensions</c> for Phase 9–13 (Device/IP/Visit upserts)
    /// and advances the watermark. Separated from <c>usp_ParseNewHits</c> to avoid
    /// the self-healing range collision (proc advancing past our pre-parsed range).
    /// </summary>
    private async Task CallDimensionsAndAdvanceWatermarkAsync(
        SqlConnection conn, long fromId, long toId, int parsedCount, CancellationToken ct)
    {
        // Phase 9-13: Device/IP/Visit from PiXL.Parsed
        await using var dimCmd = conn.CreateCommand();
        dimCmd.CommandText = "ETL.usp_ProcessDimensions";
        dimCmd.CommandType = CommandType.StoredProcedure;
        dimCmd.Parameters.AddWithValue("@FromId", fromId);
        dimCmd.Parameters.AddWithValue("@ToId", toId);
        dimCmd.CommandTimeout = 600;

        await using var reader = await dimCmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var devices = Convert.ToInt32(reader.GetValue(0));
            var ips = Convert.ToInt32(reader.GetValue(1));
            var visits = Convert.ToInt32(reader.GetValue(2));

            _logger.Debug(
                $"ParsedBulkInsert: Phase 9-13 complete — " +
                $"devices={devices}, ips={ips}, visits={visits}");
        }
        await reader.CloseAsync();

        // Advance watermark
        await using var wmCmd = conn.CreateCommand();
        wmCmd.CommandText = """
            UPDATE ETL.Watermark
            SET LastProcessedId = @MaxId,
                LastRunAt = SYSUTCDATETIME(),
                RowsProcessed = RowsProcessed + @BatchCount
            WHERE ProcessName = 'ParseNewHits'
            """;
        wmCmd.Parameters.AddWithValue("@MaxId", toId);
        wmCmd.Parameters.AddWithValue("@BatchCount", parsedCount);
        wmCmd.CommandTimeout = 30;
        await wmCmd.ExecuteNonQueryAsync(ct);
    }

    // ════════════════════════════════════════════════════════════════════════
    // RESULT RECORD
    // ════════════════════════════════════════════════════════════════════════

    private readonly record struct BatchResult(
        int Parsed, long Remaining,
        long ReadMs, long ParseMs, long BulkCopyMs, long EtlMs)
    {
        public static BatchResult Empty(long remaining) =>
            new(0, remaining, 0, 0, 0, 0);
    }
}
