using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// ETL BACKGROUND SERVICE — Runs identity resolution every 60 seconds.
//
// Calls stored procedures in sequence:
//   1. ETL.usp_MatchVisits        — Email-based identity resolution via AutoConsumer
//   2. ETL.usp_MatchLegacyVisits  — Legacy IP-based matching via AutoConsumer
//   3. ETL.usp_MatchGeoVisits     — Supplemental geo proximity resolution
//
// Phase 1 (usp_ParseNewHits) is handled by ParsedBulkInsertService.
// Phase 3 (usp_EnrichParsedGeo) is disabled — geo enrichment not needed yet.
// ============================================================================

/// <summary>
/// Background service that runs identity resolution every 60 seconds.
/// Calls usp_MatchVisits (email), usp_MatchLegacyVisits (IP), and
/// usp_MatchGeoVisits (geo proximity) to resolve PiXL.Visit records
/// against AutoConsumer into PiXL.Match.
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
        // Phase 1 (usp_ParseNewHits) is handled by ParsedBulkInsertService.
        // Phase 3 (usp_EnrichParsedGeo) is disabled — geo is not needed yet.
        // This service only runs the match procs (Phase 2 + Phase 4).
        // ══════════════════════════════════════════════════════════════════

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct);

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
    }
}
