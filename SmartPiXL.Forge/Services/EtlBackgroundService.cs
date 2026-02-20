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
//   3. ETL.usp_EnrichParsedGeo — Backfill geo data from IPAPI.IP
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
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct);

        // Phase 1: Parse new hits (PiXL.Raw → PiXL.Parsed + Device/IP/Visit)
        await using var parseCmd = conn.CreateCommand();
        parseCmd.CommandText = "ETL.usp_ParseNewHits";
        parseCmd.CommandType = System.Data.CommandType.StoredProcedure;
        parseCmd.CommandTimeout = 300;

        await using var reader = await parseCmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var rowsParsed = reader.GetInt32(0);
            var fromId = reader.GetInt32(1);
            var toId = reader.GetInt32(2);

            if (rowsParsed > 0)
                _logger.Info($"ETL parsed {rowsParsed} rows (Id {fromId}–{toId})");
        }
        await reader.CloseAsync();

        // Phase 2: Match visits against AutoConsumer for identity resolution
        await using var matchCmd = conn.CreateCommand();
        matchCmd.CommandText = "ETL.usp_MatchVisits";
        matchCmd.CommandType = System.Data.CommandType.StoredProcedure;
        matchCmd.Parameters.AddWithValue("@BatchSize", 1000);
        matchCmd.CommandTimeout = 300;

        await using var matchReader = await matchCmd.ExecuteReaderAsync(ct);
        if (await matchReader.ReadAsync(ct))
        {
            var rowsProcessed = matchReader.GetInt32(0);
            var rowsMatched = matchReader.GetInt32(1);

            if (rowsProcessed > 0)
                _logger.Info($"ETL match: {rowsProcessed} processed, {rowsMatched} matched");
        }
        await matchReader.CloseAsync();

        // Phase 3: Enrich geo data on PiXL.Parsed and PiXL.IP from IPAPI.IP
        await using var geoCmd = conn.CreateCommand();
        geoCmd.CommandText = "ETL.usp_EnrichParsedGeo";
        geoCmd.CommandType = System.Data.CommandType.StoredProcedure;
        geoCmd.Parameters.AddWithValue("@BatchSize", 10000);
        geoCmd.CommandTimeout = 300;

        await using var geoReader = await geoCmd.ExecuteReaderAsync(ct);
        if (await geoReader.ReadAsync(ct))
        {
            var parsedEnriched = geoReader.GetInt32(0);
            var srvFallback = geoReader.GetInt32(1);
            var ipEnriched = geoReader.GetInt32(2);

            if (parsedEnriched > 0 || ipEnriched > 0)
                _logger.Info($"ETL geo: {parsedEnriched} parsed + {srvFallback} srv fallback + {ipEnriched} IPs enriched");
        }
        await geoReader.CloseAsync();

        // Phase 4: Match legacy visits against AutoConsumer by IP address
        await using var legacyCmd = conn.CreateCommand();
        legacyCmd.CommandText = "ETL.usp_MatchLegacyVisits";
        legacyCmd.CommandType = System.Data.CommandType.StoredProcedure;
        legacyCmd.Parameters.AddWithValue("@BatchSize", 5000);
        legacyCmd.CommandTimeout = 300;

        await using var legacyReader = await legacyCmd.ExecuteReaderAsync(ct);
        if (await legacyReader.ReadAsync(ct))
        {
            var rowsProcessed = legacyReader.GetInt32(0);
            var rowsMatched = legacyReader.GetInt32(1);

            if (rowsProcessed > 0)
                _logger.Info($"ETL legacy match: {rowsProcessed} processed, {rowsMatched} matched by IP");
        }
    }
}
