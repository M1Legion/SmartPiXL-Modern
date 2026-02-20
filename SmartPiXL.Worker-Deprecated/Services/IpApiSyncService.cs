using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TrackingPixel.Configuration;

namespace TrackingPixel.Services;

// ============================================================================
// IP-API SYNC SERVICE — Incremental sync from Xavier → local IPAPI.IP.
//
// ARCHITECTURE:
//   Xavier (192.168.88.35)            SmartPiXL (localhost\SQL2025)
//   IPGEO.dbo.IP_Location_New  →→→   SmartPiXL.IPAPI.IP
//         343M rows                         12M+ rows (growing)
//         ~6M daily delta                   Catches up daily
//
// SYNC STRATEGY:
//   1. Read local watermark: MAX(LastSeen) from IPAPI.IP
//   2. Pull delta from Xavier: WHERE Last_Seen > @Watermark
//   3. Bulk insert into staging table (#IpApiStaging)
//   4. MERGE from staging → IPAPI.IP (upsert by IP key)
//   5. Run IPAPI.usp_EnrichGeo to backfill PiXL.IP geo columns
//   6. Log results to IPAPI.SyncLog
//
// SCHEDULING:
//   Runs once daily at a configurable hour (default 2 AM UTC).
//   The first run after the backfill completes will just catch the delta
//   since the last record loaded. Subsequent runs pick up ~6M daily rows.
//
// BATCH SIZE:
//   Pulls in chunks of 500K rows to avoid memory pressure.
//   Each chunk is a separate MERGE — allows progress tracking and reduces
//   SQL Server transaction log growth.
//
// CONNECTION:
//   Uses a separate connection string for Xavier (XavierConnectionString).
//   Windows Auth over the local network (192.168.88.35).
// ============================================================================

/// <summary>
/// Background service that syncs IP geolocation data from Xavier's IPGEO database
/// to the local SmartPiXL.IPAPI.IP table on a daily schedule.
/// <para>
/// After syncing, runs <c>IPAPI.usp_EnrichGeo</c> to populate geo columns on
/// <c>PiXL.IP</c> for any newly-seen tracking IPs.
/// </para>
/// </summary>
public sealed class IpApiSyncService : BackgroundService
{
    private readonly TrackingSettings _settings;
    private readonly IEdgeHealthClient _edge;
    private readonly ITrackingLogger _logger;
    
    // Pull this many rows from Xavier per batch
    private const int PullBatchSize = 500_000;
    
    // Merge this many rows from staging per MERGE statement
    private const int MergeBatchSize = 100_000;

    public IpApiSyncService(
        IOptions<TrackingSettings> settings,
        IEdgeHealthClient edge,
        ITrackingLogger logger)
    {
        _settings = settings.Value;
        _edge = edge;
        _logger = logger;
    }
    
    /// <summary>
    /// Retries a SQL action up to <paramref name="maxAttempts"/> times on deadlock (error 1205).
    /// Uses jittered exponential backoff: 500ms, 1s, 2s with ±25% jitter.
    /// </summary>
    private async Task<T> WithDeadlockRetryAsync<T>(Func<Task<T>> action, string operationName, int maxAttempts = 3)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (SqlException sqlEx) when (sqlEx.Number == 1205 && attempt < maxAttempts)
            {
                var baseDelay = 500 * (1 << (attempt - 1));
                var jitter = Random.Shared.Next(-baseDelay / 4, baseDelay / 4);
                var delay = baseDelay + jitter;
                _logger.Warning($"Deadlock on {operationName} (attempt {attempt}/{maxAttempts}), retrying in {delay}ms");
                await Task.Delay(delay);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        
        var xavierConnStr = _settings.XavierConnectionString;
        if (string.IsNullOrEmpty(xavierConnStr))
        {
            _logger.Warning("IpApiSyncService: XavierConnectionString not configured — sync disabled.");
            return;
        }
        
        _logger.Info($"IpApiSyncService started. Interval: {_settings.SyncIntervalHours}h");
        
        // Wait for initial backfill to stabilize before first sync
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait until the next sync window
                var delay = GetDelayUntilNextSync();
                _logger.Info($"IpApiSyncService: next sync in {delay.TotalHours:F1}h");
                await Task.Delay(delay, stoppingToken);
                
                await RunSyncAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error($"IpApiSyncService sync failed: {ex.Message}");
                // Wait 1 hour before retrying on error
                try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        
        _logger.Info("IpApiSyncService stopped.");
    }

    /// <summary>
    /// Returns the configured sync interval (default 6h).
    /// </summary>
    private TimeSpan GetDelayUntilNextSync()
    {
        var intervalHours = Math.Max(1, _settings.SyncIntervalHours);
        return TimeSpan.FromHours(intervalHours);
    }

    /// <summary>
    /// Executes one full sync cycle: pull delta from Xavier, merge into local, enrich.
    /// </summary>
    public async Task RunSyncAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var syncLogId = 0;
        var totalInserted = 0;
        var totalUpdated = 0;
        DateTime? watermarkBefore = null;
        DateTime? watermarkAfter = null;
        
        try
        {
            // Step 1: Get current watermark (max LastSeen in local IPAPI.IP)
            watermarkBefore = await GetLocalWatermarkAsync(ct);
            _logger.Info($"IpApiSync: watermark = {watermarkBefore:yyyy-MM-dd HH:mm:ss}");
            
            if (watermarkBefore == null)
            {
                _logger.Warning("IpApiSync: no data in IPAPI.IP yet — skipping (backfill still running?)");
                return;
            }
            
            // Step 2: Log sync start
            syncLogId = await InsertSyncLogAsync(watermarkBefore.Value, ct);
            
            // Step 3: Pull and merge delta in batches
            var batchWatermark = watermarkBefore.Value;
            var moreBatches = true;
            
            while (moreBatches && !ct.IsCancellationRequested)
            {
                var (inserted, updated, maxLastSeen, rowsPulled) = await PullAndMergeBatchAsync(batchWatermark, ct);
                totalInserted += inserted;
                totalUpdated += updated;
                
                if (rowsPulled < PullBatchSize || maxLastSeen == null)
                {
                    moreBatches = false;
                }
                else
                {
                    batchWatermark = maxLastSeen.Value;
                }
                
                watermarkAfter = maxLastSeen ?? watermarkAfter;
                
                if (rowsPulled > 0)
                    _logger.Info($"IpApiSync batch: +{inserted} inserted, ~{updated} updated (pulled {rowsPulled})");
            }
            
            // Step 4: Enrich PiXL.IP with geo from newly synced IPAPI data
            var enriched = await RunGeoEnrichmentAsync(ct);
            if (enriched > 0)
                _logger.Info($"IpApiSync: enriched {enriched} PiXL.IP rows with geo data");
            
            // Step 5: Clear Edge's geo hot cache so new data is picked up
            await _edge.ClearGeoCacheAsync(ct);
            
            sw.Stop();
            _logger.Info($"IpApiSync complete: {totalInserted} inserted, {totalUpdated} updated in {sw.ElapsedMilliseconds}ms");
            
            // Step 6: Update sync log
            if (syncLogId > 0)
                await CompleteSyncLogAsync(syncLogId, totalInserted, totalUpdated, watermarkAfter, (int)sw.ElapsedMilliseconds, null, ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            if (syncLogId > 0)
                await CompleteSyncLogAsync(syncLogId, totalInserted, totalUpdated, watermarkAfter, (int)sw.ElapsedMilliseconds, ex.Message, ct);
            throw;
        }
    }

    /// <summary>
    /// Gets the maximum LastSeen from local IPAPI.IP — this is our sync watermark.
    /// </summary>
    private async Task<DateTime?> GetLocalWatermarkAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(LastSeen) FROM IPAPI.IP";
        cmd.CommandTimeout = 30;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is DateTime dt ? dt : null;
    }

    /// <summary>
    /// Pulls a batch of delta rows from Xavier and merges them into local IPAPI.IP.
    /// Returns (inserted, updated, maxLastSeen, rowsPulled).
    /// </summary>
    private async Task<(int Inserted, int Updated, DateTime? MaxLastSeen, int RowsPulled)> PullAndMergeBatchAsync(
        DateTime watermark, CancellationToken ct)
    {
        // Create staging table, bulk-load from Xavier, then MERGE into IPAPI.IP
        await using var localConn = new SqlConnection(_settings.ConnectionString);
        await conn_OpenAsync(localConn, ct);
        
        // Create temp staging table
        await using (var createCmd = localConn.CreateCommand())
        {
            createCmd.CommandText = @"
                IF OBJECT_ID('tempdb..#IpApiStaging') IS NOT NULL DROP TABLE #IpApiStaging;
                CREATE TABLE #IpApiStaging (
                    FirstSeen   DATETIME     NULL,
                    LastSeen    DATETIME     NULL,
                    IP          VARCHAR(15)  NOT NULL,
                    Country     VARCHAR(99)  NULL,
                    CountryCode VARCHAR(50)  NULL,
                    Region      VARCHAR(99)  NULL,
                    RegionName  VARCHAR(99)  NULL,
                    City        VARCHAR(99)  NULL,
                    Zip         VARCHAR(50)  NULL,
                    Lat         VARCHAR(50)  NULL,
                    Lon         VARCHAR(50)  NULL,
                    Timezone    VARCHAR(50)  NULL,
                    ISP         VARCHAR(999) NULL,
                    Org         VARCHAR(999) NULL,
                    [As]        VARCHAR(999) NULL,
                    Reverse     VARCHAR(50)  NULL,
                    Mobile      VARCHAR(50)  NULL,
                    Proxy       VARCHAR(99)  NULL,
                    Status      VARCHAR(99)  NULL,
                    Message     VARCHAR(999) NULL
                );";
            createCmd.CommandTimeout = 30;
            await createCmd.ExecuteNonQueryAsync(ct);
        }
        
        // Pull from Xavier into staging via SqlBulkCopy
        int rowsPulled;
        await using (var xavierConn = new SqlConnection(_settings.XavierConnectionString))
        {
            await conn_OpenAsync(xavierConn, ct);
            
            await using var pullCmd = xavierConn.CreateCommand();
            pullCmd.CommandText = @"
                SELECT TOP (@BatchSize)
                    First_Seen, Last_Seen, IP, Country, CountryCode,
                    Region, RegionName, City, Zip, Lat, Lon, Timezone,
                    ISP, Org, [As], Reverse, Mobile, Proxy, Status, Message
                FROM IPGEO.dbo.IP_Location_New
                WHERE Last_Seen > @Watermark
                ORDER BY Last_Seen ASC";
            pullCmd.Parameters.AddWithValue("@BatchSize", PullBatchSize);
            pullCmd.Parameters.AddWithValue("@Watermark", watermark);
            pullCmd.CommandTimeout = 600; // 10 min for large pulls
            
            await using var reader = await pullCmd.ExecuteReaderAsync(ct);
            
            using var bulkCopy = new SqlBulkCopy(localConn)
            {
                DestinationTableName = "#IpApiStaging",
                BulkCopyTimeout = 600,
                BatchSize = 50_000
            };
            
            // Map columns by ordinal (same order as SELECT)
            for (int i = 0; i < 20; i++)
                bulkCopy.ColumnMappings.Add(i, i);
            
            await bulkCopy.WriteToServerAsync(reader, ct);
            rowsPulled = (int)bulkCopy.RowsCopied;
        }
        
        if (rowsPulled == 0)
            return (0, 0, null, 0);
        
        // MERGE staging → IPAPI.IP (with deadlock retry)
        int inserted = 0, updated = 0;
        DateTime? maxLastSeen = null;
        
        (inserted, updated, maxLastSeen) = await WithDeadlockRetryAsync(async () =>
        {
            int ins = 0, upd = 0;
            DateTime? maxLs = null;
            await using var mergeCmd = localConn.CreateCommand();
            mergeCmd.CommandText = @"
                DECLARE @ins INT = 0, @upd INT = 0;
                
                MERGE IPAPI.IP AS target
                USING #IpApiStaging AS source ON target.IP = source.IP
                
                WHEN MATCHED AND source.LastSeen > target.LastSeen THEN UPDATE SET
                    target.FirstSeen    = ISNULL(source.FirstSeen, target.FirstSeen),
                    target.LastSeen     = source.LastSeen,
                    target.Country      = ISNULL(source.Country, target.Country),
                    target.CountryCode  = ISNULL(source.CountryCode, target.CountryCode),
                    target.Region       = ISNULL(source.Region, target.Region),
                    target.RegionName   = ISNULL(source.RegionName, target.RegionName),
                    target.City         = ISNULL(source.City, target.City),
                    target.Zip          = ISNULL(source.Zip, target.Zip),
                    target.Lat          = ISNULL(source.Lat, target.Lat),
                    target.Lon          = ISNULL(source.Lon, target.Lon),
                    target.Timezone     = ISNULL(source.Timezone, target.Timezone),
                    target.ISP          = ISNULL(source.ISP, target.ISP),
                    target.Org          = ISNULL(source.Org, target.Org),
                    target.[As]         = ISNULL(source.[As], target.[As]),
                    target.Reverse      = ISNULL(source.Reverse, target.Reverse),
                    target.Mobile       = ISNULL(source.Mobile, target.Mobile),
                    target.Proxy        = ISNULL(source.Proxy, target.Proxy),
                    target.Status       = ISNULL(source.Status, target.Status),
                    target.Message      = source.Message
                
                WHEN NOT MATCHED THEN INSERT (
                    FirstSeen, LastSeen, IP, Country, CountryCode, Region, RegionName,
                    City, Zip, Lat, Lon, Timezone, ISP, Org, [As], Reverse, Mobile,
                    Proxy, Status, Message)
                VALUES (
                    source.FirstSeen, source.LastSeen, source.IP, source.Country, source.CountryCode,
                    source.Region, source.RegionName, source.City, source.Zip, source.Lat, source.Lon,
                    source.Timezone, source.ISP, source.Org, source.[As], source.Reverse,
                    source.Mobile, source.Proxy, source.Status, source.Message);
                
                SET @ins = (SELECT COUNT(*) FROM #IpApiStaging s WHERE NOT EXISTS (
                    SELECT 1 FROM IPAPI.IP t WHERE t.IP = s.IP AND t.LastSeen >= s.LastSeen));
                SET @upd = @@ROWCOUNT - @ins;
                
                SELECT @ins AS Inserted, @upd AS Updated, MAX(LastSeen) AS MaxLastSeen FROM #IpApiStaging;
                
                DROP TABLE #IpApiStaging;";
            mergeCmd.CommandTimeout = 600;
            
            await using var mergeReader = await mergeCmd.ExecuteReaderAsync(ct);
            if (await mergeReader.ReadAsync(ct))
            {
                ins = mergeReader.IsDBNull(0) ? 0 : mergeReader.GetInt32(0);
                upd = mergeReader.IsDBNull(1) ? 0 : mergeReader.GetInt32(1);
                maxLs = mergeReader.IsDBNull(2) ? null : mergeReader.GetDateTime(2);
            }
            return (ins, upd, maxLs);
        }, "IPAPI MERGE");
        
        return (inserted, updated, maxLastSeen, rowsPulled);
    }

    /// <summary>
    /// Runs IPAPI.usp_EnrichGeo to populate geo columns on PiXL.IP.
    /// Returns the number of rows enriched.
    /// </summary>
    private async Task<int> RunGeoEnrichmentAsync(CancellationToken ct)
    {
        var totalEnriched = 0;
        var batchEnriched = 0;
        
        do
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn_OpenAsync(conn, ct);
            
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "IPAPI.usp_EnrichGeo";
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.Parameters.AddWithValue("@BatchSize", 50_000);
            cmd.CommandTimeout = 300;
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            batchEnriched = 0;
            if (await reader.ReadAsync(ct))
                batchEnriched = reader.GetInt32(0);
            
            totalEnriched += batchEnriched;
        }
        while (batchEnriched > 0 && !ct.IsCancellationRequested);
        
        return totalEnriched;
    }

    /// <summary>
    /// Inserts a sync log entry and returns the SyncId.
    /// </summary>
    private async Task<int> InsertSyncLogAsync(DateTime watermarkBefore, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn_OpenAsync(conn, ct);
            
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO IPAPI.SyncLog (WatermarkBefore, SyncType)
                OUTPUT INSERTED.SyncId
                VALUES (@WatermarkBefore, 'IpGeo')";
            cmd.Parameters.AddWithValue("@WatermarkBefore", watermarkBefore);
            
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is int id ? id : 0;
        }
        catch
        {
            return 0; // Non-critical — don't fail the sync over a log entry
        }
    }

    /// <summary>
    /// Updates the sync log entry with completion data.
    /// </summary>
    private async Task CompleteSyncLogAsync(int syncLogId, int rowsInserted, int rowsUpdated,
        DateTime? watermarkAfter, int durationMs, string? errorMessage, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn_OpenAsync(conn, ct);
            
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE IPAPI.SyncLog SET
                    CompletedAt     = SYSUTCDATETIME(),
                    RowsInserted    = @RowsInserted,
                    RowsUpdated     = @RowsUpdated,
                    WatermarkAfter  = @WatermarkAfter,
                    DurationMs      = @DurationMs,
                    ErrorMessage    = @ErrorMessage
                WHERE SyncId = @SyncId";
            cmd.Parameters.AddWithValue("@SyncId", syncLogId);
            cmd.Parameters.AddWithValue("@RowsInserted", rowsInserted);
            cmd.Parameters.AddWithValue("@RowsUpdated", rowsUpdated);
            cmd.Parameters.AddWithValue("@WatermarkAfter", (object?)watermarkAfter ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DurationMs", durationMs);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)errorMessage ?? DBNull.Value);
            
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // Non-critical — don't fail the sync over a log update
        }
    }

    /// <summary>
    /// Helper to open a connection with consistent error handling.
    /// </summary>
    private static async Task conn_OpenAsync(SqlConnection conn, CancellationToken ct)
    {
        await conn.OpenAsync(ct);
    }
}
