using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TrackingPixel.Configuration;

namespace TrackingPixel.Services;

/// <summary>
/// Background service that runs ETL processing every 60 seconds.
/// Calls ETL.usp_ParseNewHits to move data from PiXL.Test → PiXL.Parsed
/// and populates dimension tables (PiXL_Device, PiXL_IP, PiXL_Visit).
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
        // Yield to allow host startup to complete
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
        
        // Phase 1: Parse new hits (PiXL.Test → PiXL.Parsed + Device/IP/Visit)
        await using var parseCmd = conn.CreateCommand();
        parseCmd.CommandText = "ETL.usp_ParseNewHits";
        parseCmd.CommandType = System.Data.CommandType.StoredProcedure;
        parseCmd.CommandTimeout = 300; // 5 minutes max for large batches
        
        // Use ExecuteReader to consume the proc's result set (RowsParsed, FromId, ToId).
        // ExecuteNonQuery ignores result sets and returns -1 with SET NOCOUNT ON,
        // which silently swallows errors that arrive after the first result batch.
        await using var reader = await parseCmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var rowsParsed = reader.GetInt32(0);    // RowsParsed
            var fromId = reader.GetInt32(1);         // FromId
            var toId = reader.GetInt32(2);           // ToId
            
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
            var rowsProcessed = matchReader.GetInt32(0); // RowsProcessed
            var rowsMatched = matchReader.GetInt32(1);   // RowsMatched
            
            if (rowsProcessed > 0)
                _logger.Info($"ETL match: {rowsProcessed} processed, {rowsMatched} matched");
        }
    }
}
