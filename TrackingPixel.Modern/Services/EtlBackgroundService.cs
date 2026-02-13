using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TrackingPixel.Configuration;

namespace TrackingPixel.Services;

/// <summary>
/// Background service that runs ETL processing every 60 seconds.
/// Calls dbo.usp_ParseNewHits to move data from PiXL_Test → PiXL_Parsed
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
        
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "dbo.usp_ParseNewHits";
        cmd.CommandType = System.Data.CommandType.StoredProcedure;
        cmd.CommandTimeout = 300; // 5 minutes max for large batches
        
        var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
        
        if (rowsAffected > 0)
        {
            _logger.Info($"ETL processed {rowsAffected} rows");
        }
    }
}
