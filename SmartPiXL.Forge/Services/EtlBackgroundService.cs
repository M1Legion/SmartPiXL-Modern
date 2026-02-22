using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// ETL BACKGROUND SERVICE — Runs ETL processing every 60 seconds.
//
// Calls stored procedures in sequence:
//   1. ETL.usp_ParseNewHits  — PiXL.Raw → PiXL.Parsed + Device/IP/Visit
//   2. ETL.usp_MatchVisits   — Identity resolution via AutoConsumer
//   3. ETL.usp_EnrichParsedGeo — DISABLED (locks IPAPI.IP 344M rows, not yet spec'd)
//   4. ETL.usp_MatchLegacyVisits — Legacy IP-based matching
//
// Ported from SmartPiXL.Worker-Deprecated/Services/EtlBackgroundService.cs
// with namespace updated to SmartPiXL.Forge.Services.
// ============================================================================

/// <summary>
/// Background service that runs ETL processing every 60 seconds.
/// Calls stored procedures to parse raw hits, populate dimensions,
/// resolve identities, and enrich geo data.
/// </summary>
public sealed class EtlBackgroundService : BackgroundService
{
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(60);

    public EtlBackgroundService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _settings = settings.Value;
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
        // ══════════════════════════════════════════════════════════════════
        // TEMPORARILY DISABLED — ETL catch-up consumes all SQL resources.
        //
        // With parse lag of 9.8M rows, ETL runs every 60s × 10K rows/run
        // = 16+ hours to catch up. During that time, usp_ParseNewHits holds
        // long transactions that block Forge BulkCopy writes to PiXL.Raw
        // AND Edge geo lookups against IPAPI.IP.
        //
        // The live pipeline (Edge → Forge → PiXL.Raw) is the priority.
        // ETL will be re-enabled after manual catch-up during off-peak hours:
        //   EXEC ETL.usp_ParseNewHits  -- run manually in batches
        //
        // See IMPLEMENTATION-LOG.md for full details.
        // ══════════════════════════════════════════════════════════════════
        _logger.Debug("ETL cycle skipped (temporarily disabled — SQL resources reserved for live pipeline)");
        await Task.CompletedTask;

        /*  ── All ETL phases disabled during catch-up period ──
         *  Re-enable after manual catch-up: EXEC ETL.usp_ParseNewHits (repeat until lag < 500)

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct);

        // Phase 1: Parse new hits
        await using var parseCmd = conn.CreateCommand();
        parseCmd.CommandText = "ETL.usp_ParseNewHits";
        parseCmd.CommandType = System.Data.CommandType.StoredProcedure;
        parseCmd.CommandTimeout = 300;

        await using var reader = await parseCmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var rowsParsed = Convert.ToInt64(reader.GetValue(0));
            var fromId = Convert.ToInt64(reader.GetValue(1));
            var toId = Convert.ToInt64(reader.GetValue(2));

            if (rowsParsed > 0)
                _logger.Info($"ETL parsed {rowsParsed} rows (Id {fromId}–{toId})");
        }
        await reader.CloseAsync();

        // Phase 2: Match visits
        await using var matchCmd = conn.CreateCommand();
        matchCmd.CommandText = "ETL.usp_MatchVisits";
        matchCmd.CommandType = System.Data.CommandType.StoredProcedure;
        matchCmd.Parameters.AddWithValue("@BatchSize", 1000);
        matchCmd.CommandTimeout = 300;

        await using var matchReader = await matchCmd.ExecuteReaderAsync(ct);
        if (await matchReader.ReadAsync(ct))
        {
            var rowsProcessed = Convert.ToInt64(matchReader.GetValue(0));
            var rowsMatched = Convert.ToInt64(matchReader.GetValue(1));

            if (rowsProcessed > 0)
                _logger.Info($"ETL match: {rowsProcessed} processed, {rowsMatched} matched");
        }
        await matchReader.CloseAsync();

        // Phase 3: DISABLED
        // Phase 4: Legacy match
        await using var legacyCmd = conn.CreateCommand();
        legacyCmd.CommandText = "ETL.usp_MatchLegacyVisits";
        legacyCmd.CommandType = System.Data.CommandType.StoredProcedure;
        legacyCmd.Parameters.AddWithValue("@BatchSize", 5000);
        legacyCmd.CommandTimeout = 300;

        await using var legacyReader = await legacyCmd.ExecuteReaderAsync(ct);
        if (await legacyReader.ReadAsync(ct))
        {
            var rowsProcessed = Convert.ToInt64(legacyReader.GetValue(0));
            var rowsMatched = Convert.ToInt64(legacyReader.GetValue(1));

            if (rowsProcessed > 0)
                _logger.Info($"ETL legacy match: {rowsProcessed} processed, {rowsMatched} matched by IP");
        }
        */
    }
}
