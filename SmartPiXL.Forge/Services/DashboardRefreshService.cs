using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// DASHBOARD REFRESH SERVICE — Pre-aggregates BrilliantPiXL dashboard data.
//
// Runs every 30 seconds. Checks the ETL watermark to detect new modern rows
// in PiXL.Parsed. When new data arrives (or the snapshot becomes stale),
// executes Dashboard.usp_RefreshBrilliantPiXL which computes all 13 endpoint
// results as JSON and stores them in Dashboard.BrilliantPiXL.
//
// Sentinel reads the pre-computed JSON — zero queries against PiXL.Parsed.
// ============================================================================

public sealed class DashboardRefreshService : BackgroundService
{
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);

    private long _lastWatermark;
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public DashboardRefreshService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _logger.Info("Dashboard refresh service started (30s interval).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error($"Dashboard refresh failed: {ex.Message}");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct);

        // Read watermark — quick check for new rows. SourceId is the identity PK
        // and is strictly monotonic, so TOP 1 DESC is a single-row backwards seek
        // on PK_PiXL_Parsed regardless of fragmentation or row count. HitType filter
        // removed deliberately: new SourceId => new data, HitType is irrelevant here.
        // NOLOCK avoids shared-lock contention with concurrent SqlBulkCopy inserts.
        long currentMax;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT TOP 1 ISNULL(SourceId, 0) FROM PiXL.Parsed WITH (NOLOCK) ORDER BY SourceId DESC";
            cmd.CommandTimeout = 10;
            var result = await cmd.ExecuteScalarAsync(ct);
            currentMax = result is null || result is DBNull ? 0 : Convert.ToInt64(result);
        }

        bool newData = currentMax > _lastWatermark;
        bool stale = (DateTime.UtcNow - _lastRefreshUtc) >= StaleThreshold;

        if (!newData && !stale) return;

        // Execute the refresh stored procedure
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "Dashboard.usp_RefreshBrilliantPiXL";
            cmd.CommandType = System.Data.CommandType.StoredProcedure;
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        sw.Stop();

        // Update ETL watermark
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE ETL.Watermark SET LastProcessedId = @Id WHERE ProcessName = 'RefreshBrilliantPiXL'";
            cmd.Parameters.AddWithValue("@Id", currentMax);
            cmd.CommandTimeout = 10;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        _lastWatermark = currentMax;
        _lastRefreshUtc = DateTime.UtcNow;

        _logger.Info($"BrilliantPiXL dashboard refreshed in {sw.ElapsedMilliseconds}ms (watermark → {currentMax}{(stale && !newData ? ", stale" : "")})");
    }
}
