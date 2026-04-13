using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Sentinel.Endpoints;
using SmartPiXL.Services;

namespace SmartPiXL.Sentinel.Services;

// ============================================================================
// DASHBOARD CACHE WARMER — Pre-loads slow vw_Dash_* views into the in-memory
// cache so the Tron dashboard is instant on first load.
//
// The vw_Dash_* views scan PiXL.Parsed (57M rows, 888 GB) with 30-day rolling
// windows. Individual queries can take 30-300+ seconds. Without warming, the
// first user to hit the dashboard after a restart (or cache expiry) waits for
// all of them — and the semaphore gate means they queue behind each other.
//
// This service:
//   1. Runs sequentially (one query at a time, no semaphore contention)
//   2. Populates DashboardEndpoints._cache directly
//   3. Refreshes every 5 minutes (matching cache TTL)
//   4. Logs each view's execution time for observability
// ============================================================================

public sealed class DashboardCacheWarmerService : BackgroundService
{
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;

    // Warm interval: short delay between cycles. The full cycle takes ~30 min
    // for all views; this just adds a brief pause before the next cycle starts.
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(2);

    // Warmer uses a long TTL so views stay cached across the entire cycle.
    // On-demand endpoint hits use the default 5-min CacheDuration; the warmer
    // uses 60 min so that slow views don't expire before the cycle finishes.
    private static readonly TimeSpan WarmerCacheTtl = TimeSpan.FromMinutes(60);

    // View-backed queries: (cacheKey, sql, isSingleRow)
    // Order: fastest first so critical views are available sooner.
    private static readonly (string Key, string Sql, bool SingleRow)[] ViewQueries =
    [
        ("recent",              "SELECT * FROM vw_Dash_RecentHits", false),
        ("xavier-sync",         "SELECT * FROM vw_Dash_XavierSync", false),
        ("evasion",             "SELECT * FROM vw_Dash_EvasionSummary", true),
        ("bots",                "SELECT * FROM vw_Dash_BotBreakdown ORDER BY SortOrder", false),
        ("bot-signals",         "SELECT TOP 20 * FROM vw_Dash_TopBotSignals ORDER BY TimesTriggered DESC", false),
        ("devices",             "SELECT TOP 30 * FROM vw_Dash_DeviceBreakdown ORDER BY HitCount DESC", false),
        ("behavior",            "SELECT * FROM vw_Dash_BehavioralAnalysis", false),
        ("fingerprints-50",     "SELECT TOP 50 * FROM vw_Dash_FingerprintClusters ORDER BY HitCount DESC", false),
        ("sessions",            "SELECT * FROM vw_Dash_SessionSummary", false),
        ("dead-internet",       "SELECT * FROM vw_Dash_DeadInternet", false),
        ("customer-quality",    "SELECT * FROM vw_Dash_CustomerQuality", false),
        ("cross-customer",      "SELECT * FROM vw_Dash_CrossCustomer", false),
        ("cross-customer-detail", "SELECT TOP 100 * FROM vw_Dash_CrossCustomerDetail", false),
        ("impossible-travel",   "SELECT * FROM vw_Dash_ImpossibleTravel", false),
        ("device-lifecycle",    "SELECT * FROM vw_Dash_DeviceLifecycle", false),
        ("device-hops",         "SELECT TOP 100 * FROM vw_Dash_DeviceCustomerHops", false),
        ("subnet-clusters",     "SELECT TOP 100 * FROM vw_Dash_SubnetClusters", false),
    ];

    // SP-backed queries: (cacheKey, spName)
    private static readonly (string Key, string SpName)[] SpQueries =
    [
        ("health",   "usp_Dash_SystemHealth"),
        ("pipeline", "usp_Dash_PipelineHealth"),
    ];

    public DashboardCacheWarmerService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield to let the host finish startup before we hog SQL connections.
        await Task.Yield();

        _logger.Info("[CacheWarmer] Starting — will pre-load Tron dashboard views.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            int ok = 0, fail = 0;

            // SPs first (fast)
            foreach (var (key, spName) in SpQueries)
            {
                if (stoppingToken.IsCancellationRequested) break;
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var data = await ExecuteSpAsync(spName, stoppingToken);
                    sw.Stop();
                    DashboardEndpoints.CacheStore(key, data, WarmerCacheTtl);
                    ok++;
                    _logger.Info($"[CacheWarmer] {key}: {sw.ElapsedMilliseconds}ms");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    fail++;
                    _logger.Error($"[CacheWarmer] {key} failed: {ex.Message}");
                }
            }

            // Views (slow, sequential, one at a time)
            foreach (var (key, sql, singleRow) in ViewQueries)
            {
                if (stoppingToken.IsCancellationRequested) break;
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    object? data = singleRow
                        ? await QuerySingleRowAsync(sql, stoppingToken)
                        : await QueryAsync(sql, stoppingToken);
                    sw.Stop();
                    DashboardEndpoints.CacheStore(key, data, WarmerCacheTtl);
                    ok++;
                    _logger.Info($"[CacheWarmer] {key}: {sw.ElapsedMilliseconds}ms");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    fail++;
                    _logger.Error($"[CacheWarmer] {key} failed: {ex.Message}");
                }
            }

            totalSw.Stop();
            _logger.Info($"[CacheWarmer] Cycle complete: {ok} ok, {fail} failed in {totalSw.Elapsed.TotalSeconds:F1}s");

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<List<Dictionary<string, object?>>> QueryAsync(
        string sql, CancellationToken ct)
    {
        var results = new List<Dictionary<string, object?>>();
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 600 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var v = reader.GetValue(i);
                row[reader.GetName(i)] = v == DBNull.Value ? null : v;
            }
            results.Add(row);
        }
        return results;
    }

    private async Task<Dictionary<string, object?>> QuerySingleRowAsync(
        string sql, CancellationToken ct)
    {
        var rows = await QueryAsync(sql, ct);
        return rows.FirstOrDefault() ?? new Dictionary<string, object?>();
    }

    private async Task<Dictionary<string, object?>> ExecuteSpAsync(
        string spName, CancellationToken ct)
    {
        var row = new Dictionary<string, object?>();
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(spName, conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 600
        };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var v = reader.GetValue(i);
                row[reader.GetName(i)] = v == DBNull.Value ? null : v;
            }
        }
        return row;
    }
}
