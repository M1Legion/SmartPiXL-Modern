using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TrackingPixel.Configuration;

namespace TrackingPixel.Services;

/// <summary>
/// Background service that periodically runs usp_ParseNewHits to materialize
/// raw PiXL_Test rows into the indexed PiXL_Parsed table.
/// This keeps the Tron dashboard views fed with fresh data.
/// 
/// Default: runs every 60 seconds, processing up to 50,000 rows per cycle.
/// Configure via appsettings.json "Tracking:EtlIntervalSeconds" and "Tracking:EtlBatchSize".
/// </summary>
public sealed class EtlBackgroundService : BackgroundService
{
    private readonly TrackingSettings _settings;
    private readonly ILogger<EtlBackgroundService> _logger;

    public EtlBackgroundService(
        IOptions<TrackingSettings> settings,
        ILogger<EtlBackgroundService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the rest of the app start up before we begin ETL work
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        _logger.LogInformation("ETL background service started. Interval={Interval}s, BatchSize={Batch}",
            _settings.EtlIntervalSeconds, _settings.EtlBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var rowsParsed = await RunEtlCycleAsync(stoppingToken);

                if (rowsParsed > 0)
                    _logger.LogInformation("ETL cycle complete: {Rows} rows materialized", rowsParsed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ETL cycle failed — will retry next interval");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.EtlIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("ETL background service stopped");
    }

    private async Task<int> RunEtlCycleAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand("dbo.usp_ParseNewHits", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 120
        };
        cmd.Parameters.AddWithValue("@BatchSize", _settings.EtlBatchSize);

        // The proc returns a result set with RowsParsed column
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var ordinal = reader.GetOrdinal("RowsParsed");
            return reader.GetInt32(ordinal);
        }

        return 0;
    }
}
