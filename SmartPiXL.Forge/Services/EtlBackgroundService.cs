using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// ETL BACKGROUND SERVICE — Runs dimension processing + identity resolution.
//
// Phase 9-13 (usp_ProcessDimensions) is now called here because the merged
// pipeline writes directly to PiXL.Parsed — ParsedBulkInsertService no longer
// handles it. Uses a separate 'ProcessDimensions' watermark to track progress.
//
// Calls stored procedures in sequence:
//   0. ETL.usp_ProcessDimensions    — Device/IP/Visit upserts (Phase 9-13)
//   1. ETL.usp_MatchVisits          — Email-based identity resolution
//   2. ETL.usp_MatchLegacyVisits    — Legacy IP-based matching
//   3. ETL.usp_MatchGeoVisits       — Supplemental geo proximity resolution
// ============================================================================

/// <summary>
/// Background service that runs dimension processing (Phase 9-13) and identity
/// resolution every 60 seconds. Processes new PiXL.Parsed rows via watermark,
/// then runs match procs.
/// </summary>
public sealed class EtlBackgroundService : BackgroundService
{
    private readonly TrackingSettings _settings;
    private readonly ForgeMetrics _metrics;
    private readonly ITrackingLogger _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(60);

    public EtlBackgroundService(
        IOptions<TrackingSettings> settings,
        ForgeMetrics metrics,
        ITrackingLogger logger)
    {
        _settings = settings.Value;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _logger.Info("ETL background service started. Running every 60 seconds.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunEtlAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"ETL cycle failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.Info("ETL background service stopped.");
    }

    private async Task RunEtlAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct);

        // ── Phase 9-13: Dimension processing (Device/IP/Visit upserts) ──
        // Reads the 'ProcessDimensions' watermark, catches up to max SourceId
        // in 50K batches, then advances the watermark.
        await RunDimensionProcessingAsync(conn, ct);

        // Phase 2: Match visits by email
        await using (var matchCmd = conn.CreateCommand())
        {
            matchCmd.CommandText = "ETL.usp_MatchVisits";
            matchCmd.CommandType = System.Data.CommandType.StoredProcedure;
            matchCmd.Parameters.AddWithValue("@BatchSize", 1000);
            matchCmd.CommandTimeout = 300;

            await using var matchReader = await matchCmd.ExecuteReaderAsync(ct);
            if (await matchReader.ReadAsync(ct))
            {
                var rowsProcessed = Convert.ToInt64(matchReader.GetValue(0));
                var rowsMatched = Convert.ToInt64(matchReader.GetValue(1));

                _metrics.RecordEtlMatchVisitsRun();
                if (rowsProcessed > 0)
                    _logger.Info($"ETL match: {rowsProcessed} processed, {rowsMatched} matched by email");
            }
        }

        // Phase 4: Legacy IP-based match
        await using (var legacyCmd = conn.CreateCommand())
        {
            legacyCmd.CommandText = "ETL.usp_MatchLegacyVisits";
            legacyCmd.CommandType = System.Data.CommandType.StoredProcedure;
            legacyCmd.Parameters.AddWithValue("@BatchSize", 5000);
            legacyCmd.CommandTimeout = 300;

            await using var legacyReader = await legacyCmd.ExecuteReaderAsync(ct);
            if (await legacyReader.ReadAsync(ct))
            {
                var rowsProcessed = Convert.ToInt64(legacyReader.GetValue(0));
                var rowsMatched = Convert.ToInt64(legacyReader.GetValue(1));

                _metrics.RecordEtlMatchLegacyRun();
                if (rowsProcessed > 0)
                    _logger.Info($"ETL legacy match: {rowsProcessed} processed, {rowsMatched} matched by IP");
            }
        }

        // Phase 5: Supplemental geo proximity match
        await using (var geoCmd = conn.CreateCommand())
        {
            geoCmd.CommandText = "ETL.usp_MatchGeoVisits";
            geoCmd.CommandType = System.Data.CommandType.StoredProcedure;
            geoCmd.Parameters.AddWithValue("@BatchSize", 2000);
            geoCmd.CommandTimeout = 300;

            await using var geoReader = await geoCmd.ExecuteReaderAsync(ct);
            if (await geoReader.ReadAsync(ct))
            {
                var rowsProcessed = Convert.ToInt64(geoReader.GetValue(0));
                var rowsMatched = Convert.ToInt64(geoReader.GetValue(1));

                if (rowsProcessed > 0)
                    _logger.Info($"ETL geo match: {rowsProcessed} processed, {rowsMatched} matched by geo proximity");
            }
        }

        // Dashboard snapshot + hourly stats (lightweight SPs, ~200ms total)
        await WriteDashboardSnapshotAsync(conn, ct);
    }

    private async Task WriteDashboardSnapshotAsync(SqlConnection conn, CancellationToken ct)
    {
        try
        {
            await using (var snapCmd = conn.CreateCommand())
            {
                snapCmd.CommandText = "usp_Dash_WriteSnapshot";
                snapCmd.CommandType = System.Data.CommandType.StoredProcedure;
                snapCmd.CommandTimeout = 30;
                await snapCmd.ExecuteNonQueryAsync(ct);
            }

            await using (var hourlyCmd = conn.CreateCommand())
            {
                hourlyCmd.CommandText = "usp_Dash_WriteHourlyStats";
                hourlyCmd.CommandType = System.Data.CommandType.StoredProcedure;
                hourlyCmd.CommandTimeout = 30;
                await hourlyCmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Dashboard snapshot write failed: {ex.Message}");
        }
    }

    private async Task RunDimensionProcessingAsync(SqlConnection conn, CancellationToken ct)
    {
        const int batchSize = 50_000;

        // Read current watermark
        long watermark;
        await using (var wmCmd = conn.CreateCommand())
        {
            wmCmd.CommandText = "SELECT LastProcessedId FROM ETL.Watermark WHERE ProcessName = 'ProcessDimensions'";
            wmCmd.CommandTimeout = 30;
            var result = await wmCmd.ExecuteScalarAsync(ct);
            if (result is null or DBNull) return;
            watermark = Convert.ToInt64(result);
        }

        // Read max SourceId in PiXL.Parsed
        long maxId;
        await using (var maxCmd = conn.CreateCommand())
        {
            maxCmd.CommandText = "SELECT MAX(SourceId) FROM PiXL.Parsed";
            maxCmd.CommandTimeout = 30;
            var result = await maxCmd.ExecuteScalarAsync(ct);
            if (result is null or DBNull) return;
            maxId = Convert.ToInt64(result);
        }

        if (watermark >= maxId) return;

        long totalProcessed = 0;

        while (watermark < maxId && !ct.IsCancellationRequested)
        {
            var toId = Math.Min(watermark + batchSize, maxId);

            await using (var procCmd = conn.CreateCommand())
            {
                procCmd.CommandText = "ETL.usp_ProcessDimensions";
                procCmd.CommandType = System.Data.CommandType.StoredProcedure;
                procCmd.Parameters.AddWithValue("@FromId", watermark);
                procCmd.Parameters.AddWithValue("@ToId", toId);
                procCmd.CommandTimeout = 300;
                await procCmd.ExecuteNonQueryAsync(ct);
            }

            // Advance watermark
            await using (var upCmd = conn.CreateCommand())
            {
                upCmd.CommandText = "UPDATE ETL.Watermark SET LastProcessedId = @ToId WHERE ProcessName = 'ProcessDimensions'";
                upCmd.Parameters.AddWithValue("@ToId", toId);
                upCmd.CommandTimeout = 30;
                await upCmd.ExecuteNonQueryAsync(ct);
            }

            totalProcessed += toId - watermark;
            watermark = toId;
        }

        if (totalProcessed > 0)
            _logger.Info($"ETL dimensions: processed {totalProcessed} rows (watermark now {watermark})");
    }
}
