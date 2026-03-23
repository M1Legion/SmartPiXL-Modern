using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Sentinel.Services;
using SmartPiXL.Services;

namespace SmartPiXL.Sentinel.Endpoints;

// ============================================================================
// DASHBOARD API ENDPOINTS â€” Read-only JSON API over SQL views.
//
// ARCHITECTURE:
//   /api/dash/* routes  â†’  QueryAsync/QuerySingleRowAsync  â†’  vw_Dash_* views
//   (HTTP GET)              (ADO.NET SqlDataReader)              (PiXL.Parsed materialized data)
//
// All endpoints are restricted to localhost + DashboardAllowedIPs from config.
// External requests receive a 404 (not 403) to avoid revealing the API exists.
//
// SQL VIEW MAPPING:
//   /api/dash/health       â†’  vw_Dash_SystemHealth       (single row aggregate)
//   /api/dash/hourly       â†’  vw_Dash_HourlyRollup       (time-bucketed rollup)
//   /api/dash/bots         â†’  vw_Dash_BotBreakdown       (risk tiers)
//   /api/dash/bot-signals  â†’  vw_Dash_TopBotSignals      (detection signal frequency)
//   /api/dash/devices      â†’  vw_Dash_DeviceBreakdown    (browser/OS/device stats)
//   /api/dash/evasion      â†’  vw_Dash_EvasionSummary     (canvas/WebGL evasion)
//   /api/dash/behavior     â†’  vw_Dash_BehavioralAnalysis (timing/interaction signals)
//   /api/dash/recent       â†’  vw_Dash_RecentHits         (latest raw hits)
//   /api/dash/fingerprints â†’  vw_Dash_FingerprintClusters(grouped fingerprints)
//   /api/dash/infra        â†’  InfraHealthService         (live OS/SQL/IIS probes)
//   /api/dash/pipeline     â†’  vw_Dash_PipelineHealth     (ETL watermarks & lags)
//   /api/dash/sessions     â†’  vw_Dash_SessionSummary     (session reconstruction)
//   /api/dash/dead-internet â†’ vw_Dash_DeadInternet       (dead internet index)
//   /api/dash/customer-quality â†’ vw_Dash_CustomerQuality (traffic quality trending)
//   /api/dash/cross-customer â†’ vw_Dash_CrossCustomer     (cross-customer intelligence)
//   /api/dash/impossible-travel â†’ vw_Dash_ImpossibleTravel (geo anomaly detection)
//   /api/dash/device-lifecycle â†’ vw_Dash_DeviceLifecycle  (device age/lifecycle)
//   /api/dash/subnet-clusters â†’ vw_Dash_SubnetClusters   (subnet reputation)
//
// DASHBOARD HTML:
//   /tron                  →  wwwroot/tron.html          (Tron health dashboard SPA)
//   /tron/metrics           →  wwwroot/tron.html          (same SPA, JS view switch)
//   /tron/analytics         →  wwwroot/tron.html          (legacy alias for /tron/metrics)
// ============================================================================

/// <summary>
/// Dashboard API endpoints â€” exposes SQL views as JSON for the Tron operations dashboard.
/// <para>
/// All endpoints are read-only SELECT queries against <c>vw_Dash_*</c> views
/// (materialized from <c>PiXL.Parsed</c>). Access is restricted to loopback
/// addresses and explicitly allowed IPs from <c>Tracking:DashboardAllowedIPs</c>.
/// </para>
/// </summary>
public static class DashboardEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static ITrackingLogger _logger = null!;

    // ── General-purpose endpoint cache ──────────────────────────────────
    // Keys by endpoint path, stores (data, expiry). Thread-safe via ConcurrentDictionary.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (object? Data, DateTime Expiry)>
        _cache = new();

    /// <summary>Try to serve from cache. Returns true (and writes response) on hit.</summary>
    private static async Task<bool> TryServeCachedAsync(HttpContext ctx, string cacheKey)
    {
        if (_cache.TryGetValue(cacheKey, out var entry) && DateTime.UtcNow < entry.Expiry)
        {
            await WriteJsonAsync(ctx, entry.Data);
            return true;
        }
        return false;
    }

    /// <summary>Store result in cache.</summary>
    private static void CacheStore(string cacheKey, object? data)
    {
        _cache[cacheKey] = (data, DateTime.UtcNow + CacheDuration);
    }

    public static void MapDashboardEndpoints(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<TrackingSettings>>().Value;
        _logger = app.Services.GetRequiredService<ITrackingLogger>();

        // ====================================================================
        // ORIGINAL DASHBOARD VIEWS (ported from Worker)
        // ====================================================================

        app.MapGet("/api/dash/health", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "health")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await ExecuteSpSingleRowAsync(settings.ConnectionString,
                    "usp_Dash_SystemHealth");
                CacheStore("health", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/hourly", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            var hours = int.TryParse(ctx.Request.Query["hours"].FirstOrDefault(), out var h) ? Math.Clamp(h, 1, 720) : 72;
            var key = $"hourly-{hours}";
            if (await TryServeCachedAsync(ctx, key)) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "EXEC usp_Dash_HourlyRollup @Hours = @N",
                    new SqlParameter("@N", hours));
                CacheStore(key, data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/bots", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "bots")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT * FROM vw_Dash_BotBreakdown ORDER BY SortOrder");
                CacheStore("bots", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/bot-signals", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "bot-signals")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT TOP 20 * FROM vw_Dash_TopBotSignals ORDER BY TimesTriggered DESC");
                CacheStore("bot-signals", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/devices", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "devices")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT TOP 30 * FROM vw_Dash_DeviceBreakdown ORDER BY HitCount DESC");
                CacheStore("devices", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/evasion", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "evasion")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QuerySingleRowAsync(settings.ConnectionString,
                    "SELECT * FROM vw_Dash_EvasionSummary");
                CacheStore("evasion", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/behavior", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "behavior")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT * FROM vw_Dash_BehavioralAnalysis");
                CacheStore("behavior", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/recent", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "recent")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT * FROM vw_Dash_RecentHits");
                CacheStore("recent", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/fingerprints", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 200) : 50;
            var key = $"fingerprints-{limit}";
            if (await TryServeCachedAsync(ctx, key)) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT TOP (@N) * FROM vw_Dash_FingerprintClusters ORDER BY HitCount DESC",
                    new SqlParameter("@N", limit));
                CacheStore(key, data);
                await WriteJsonAsync(ctx, data);
            });
        });

        // ====================================================================
        // INFRASTRUCTURE HEALTH â€” Services, SQL, Websites, App metrics
        // ====================================================================
        app.MapGet("/api/dash/infra", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var infraService = ctx.RequestServices.GetRequiredService<InfraHealthService>();
                var snapshot = await infraService.GetHealthAsync();
                await WriteJsonAsync(ctx, snapshot);
            });
        });

        // ====================================================================
        // XAVIER SYNC HEALTH â€” Cached 15 s
        // ====================================================================
        app.MapGet("/api/dash/xavier-sync", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "xavier-sync")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT * FROM vw_Dash_XavierSync");
                CacheStore("xavier-sync", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        // ====================================================================
        // PIPELINE HEALTH â€” Device, IP, Visit, Match tables & watermarks
        // ====================================================================
        app.MapGet("/api/dash/pipeline", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "pipeline")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await ExecuteSpSingleRowAsync(settings.ConnectionString,
                    "usp_Dash_PipelineHealth");
                CacheStore("pipeline", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        // ====================================================================
        // ENRICHMENT-AWARE VIEWS (Phase 8+ â€” new in Sentinel)
        // ====================================================================

        app.MapGet("/api/dash/sessions", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "sessions")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT * FROM vw_Dash_SessionSummary");
                CacheStore("sessions", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/dead-internet", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "dead-internet")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT * FROM vw_Dash_DeadInternet");
                CacheStore("dead-internet", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/customer-quality", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "customer-quality")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT * FROM vw_Dash_CustomerQuality");
                CacheStore("customer-quality", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/cross-customer", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "cross-customer")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT * FROM vw_Dash_CrossCustomer");
                CacheStore("cross-customer", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/cross-customer/detail", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "cross-customer-detail")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT TOP 100 * FROM vw_Dash_CrossCustomerDetail");
                CacheStore("cross-customer-detail", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/impossible-travel", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "impossible-travel")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT * FROM vw_Dash_ImpossibleTravel");
                CacheStore("impossible-travel", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/device-lifecycle", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "device-lifecycle")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT * FROM vw_Dash_DeviceLifecycle");
                CacheStore("device-lifecycle", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/device-hops", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "device-hops")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT TOP 100 * FROM vw_Dash_DeviceCustomerHops");
                CacheStore("device-hops", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/subnet-clusters", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "subnet-clusters")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT TOP 100 * FROM vw_Dash_SubnetClusters");
                CacheStore("subnet-clusters", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        // ====================================================================
        // MATCH BREAKDOWN — Per-company match-type counts + entitlements
        // ====================================================================
        app.MapGet("/api/dash/match-breakdown", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "match-breakdown")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "EXEC usp_Dash_MatchBreakdown");
                CacheStore("match-breakdown", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        // ====================================================================
        // REMEDIATION ENDPOINTS â€” View, approve, skip remediations
        // ====================================================================

        app.MapGet("/api/dash/remediations", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "remediations")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT TOP 50 * FROM Ops.RemediationLog ORDER BY DetectedAtUtc DESC");
                CacheStore("remediations", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapPost("/api/dash/remediation/approve/{id:int}", async (HttpContext ctx, int id) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var remediation = ctx.RequestServices.GetRequiredService<RemediationService>();
                var (success, message) = await remediation.ExecuteRemediationAsync(id, ctx.RequestAborted);
                await WriteJsonAsync(ctx, new { success, message });
            });
        });

        app.MapPost("/api/dash/remediation/skip/{id:int}", async (HttpContext ctx, int id) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var remediation = ctx.RequestServices.GetRequiredService<RemediationService>();
                var skipped = await remediation.SkipRemediationAsync(id, ctx.RequestAborted);
                await WriteJsonAsync(ctx, new { success = skipped, message = skipped ? "Skipped" : "Not found or not pending" });
            });
        });

        // ====================================================================
        // CIRCUIT BREAKER RESET â€” Proxied to IIS Edge via HTTP        // ====================================================================

        // ====================================================================
        // DASHBOARD SNAPSHOT — Instant ops view from Ops.DashboardSnapshot
        // Written by Forge every 60s. Single-row read = <1ms.
        // ====================================================================
        app.MapGet("/api/dash/snapshot", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "snapshot")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QuerySingleRowAsync(settings.ConnectionString,
                    "SELECT TOP 1 * FROM Ops.DashboardSnapshot ORDER BY SnapshotId DESC");
                CacheStore("snapshot", data);
                await WriteJsonAsync(ctx, data);
            });
        });

        // ====================================================================
        // HOURLY STATS — Pre-aggregated hourly data for charts
        // ====================================================================
        app.MapGet("/api/dash/hourly-stats", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            var hours = int.TryParse(ctx.Request.Query["hours"].FirstOrDefault(), out var h) ? Math.Clamp(h, 1, 720) : 48;
            var key = $"hourly-stats-{hours}";
            if (await TryServeCachedAsync(ctx, key)) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT * FROM Ops.HourlyStats WHERE HourUtc >= DATEADD(HOUR, -@N, SYSUTCDATETIME()) ORDER BY HourUtc",
                    new SqlParameter("@N", hours));
                CacheStore(key, data);
                await WriteJsonAsync(ctx, data);
            });
        });        // ====================================================================
        app.MapPost("/api/dash/circuit-reset", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var edge = ctx.RequestServices.GetRequiredService<IEdgeHealthClient>();
                var reset = await edge.ResetCircuitAsync(ctx.RequestAborted);
                var health = await edge.GetHealthAsync(ctx.RequestAborted);
                await WriteJsonAsync(ctx, new
                {
                    success = reset,
                    state = health.Circuit,
                    message = reset ? "Circuit breaker reset to Closed" : "Already closed or Edge unreachable"
                });
            });
        });

        // ====================================================================
        // TEST NOTIFICATION â€” Verify email + SMS notification stack
        // ====================================================================
        app.MapPost("/api/dash/test-notify", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var email = ctx.RequestServices.GetRequiredService<EmailNotificationService>();
                var (emailSent, smsSent) = await email.NotifyAsync(
                    "TestNotification",
                    "Test Alert â€” System OK",
                    $"This is a test notification from SmartPiXL Sentinel.\nTimestamp: {DateTime.UtcNow:u}\nEmail + SMS channels verified.");
                await WriteJsonAsync(ctx, new
                {
                    emailSent,
                    smsSent,
                    emailConfigured = email.IsConfigured,
                    smsConfigured = email.IsSmsConfigured
                });
            });
        });

        // ====================================================================
        // DASHBOARD HTML PAGES
        // ====================================================================
        app.MapGet("/tron", ServeTronHtml);
        app.MapGet("/tron/metrics", ServeTronHtml);
        app.MapGet("/tron/analytics", ServeTronHtml);  // legacy alias
        app.MapGet("/tron/{file}", ServeTronModule);
        app.MapGet("/pipeline", ServePipelineHtml);

        _logger.Info("[Dashboard] All dashboard endpoints mapped: /tron, /pipeline, /api/dash/*");
    }

    private static async Task ServeTronHtml(HttpContext ctx, IWebHostEnvironment env)
    {
        if (!SentinelAccessControl.IsAllowed(ctx)) return;
        var path = Path.Combine(env.WebRootPath ?? "wwwroot", "tron.html");
        if (!File.Exists(path))
            path = Path.Combine(env.ContentRootPath, "wwwroot", "tron.html");

        if (File.Exists(path))
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers.Pragma = "no-cache";
            await ctx.Response.SendFileAsync(path);
        }
        else
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("Tron dashboard not found.");
        }
    }

    private static async Task ServePipelineHtml(HttpContext ctx, IWebHostEnvironment env)
    {
        if (!SentinelAccessControl.IsAllowed(ctx)) return;
        var path = Path.Combine(env.WebRootPath ?? "wwwroot", "pipeline.html");
        if (!File.Exists(path))
            path = Path.Combine(env.ContentRootPath, "wwwroot", "pipeline.html");

        if (File.Exists(path))
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers.Pragma = "no-cache";
            await ctx.Response.SendFileAsync(path);
        }
        else
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("Pipeline explorer not found.");
        }
    }

    private static async Task ServeTronModule(HttpContext ctx, IWebHostEnvironment env, string file)
    {
        if (!SentinelAccessControl.IsAllowed(ctx)) return;

        if (file.Contains("..") || file.Contains('/') || file.Contains('\\'))
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var ext = Path.GetExtension(file).ToLowerInvariant();
        var contentType = ext switch
        {
            ".mjs" => "application/javascript; charset=utf-8",
            ".glsl" => "text/plain; charset=utf-8",
            _ => null
        };

        if (contentType is null)
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        var path = Path.Combine(env.WebRootPath ?? "wwwroot", "tron", file);
        if (!File.Exists(path))
            path = Path.Combine(env.ContentRootPath, "wwwroot", "tron", file);

        if (File.Exists(path))
        {
            ctx.Response.ContentType = contentType;
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            await ctx.Response.SendFileAsync(path);
        }
        else
        {
            ctx.Response.StatusCode = 404;
        }
    }

    // ========================================================================
    // ADO.NET HELPERS â€” Thin wrappers for read-only view queries.
    // ========================================================================

    private static async Task<List<Dictionary<string, object?>>> QueryAsync(
        string connectionString, string sql, SqlParameter? param = null)
    {
        var results = new List<Dictionary<string, object?>>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = 60;

        if (param is not null)
            cmd.Parameters.Add(param);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                row[reader.GetName(i)] = value == DBNull.Value ? null : value;
            }
            results.Add(row);
        }

        return results;
    }

    private static async Task<Dictionary<string, object?>> QuerySingleRowAsync(string connectionString, string sql)
    {
        var results = await QueryAsync(connectionString, sql);
        return results.FirstOrDefault() ?? new Dictionary<string, object?>();
    }

    /// <summary>
    /// Execute a stored procedure and return the first result row as a dictionary.
    /// Stored procedures with individual variable assignments avoid the 20+ scalar
    /// subquery optimizer pathology that makes equivalent views 100-1000x slower.
    /// </summary>
    private static async Task<Dictionary<string, object?>> ExecuteSpSingleRowAsync(
        string connectionString, string spName)
    {
        var row = new Dictionary<string, object?>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(spName, conn);
        cmd.CommandType = System.Data.CommandType.StoredProcedure;
        cmd.CommandTimeout = 30;

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                row[reader.GetName(i)] = value == DBNull.Value ? null : value;
            }
        }
        return row;
    }

    /// <summary>
    /// Execute a stored procedure, serialize result to JSON, and write response.
    /// </summary>
    private static async Task ExecuteSpSingleRowAsync(
        HttpContext ctx, string connectionString, string spName)
    {
        try
        {
            var data = await ExecuteSpSingleRowAsync(connectionString, spName);
            await WriteJsonAsync(ctx, data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error($"[Dashboard] SP failed ({spName}): {ex.Message}");
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 503;
                await WriteJsonAsync(ctx, new { error = "Query failed", detail = ex.Message });
            }
        }
    }

    // ========================================================================
    // SAFE QUERY WRAPPERS â€” Execute SQL view queries with error handling.
    //
    // Returns structured JSON error on SqlException/timeout instead of
    // propagating unhandled exceptions as raw 500s. (BUG-Q4 fix)
    // ========================================================================

    /// <summary>
    /// Execute a multi-row query, serialize to JSON, and write the response.
    /// On failure, returns HTTP 503 with structured <c>{ error, detail }</c> JSON.
    /// </summary>
    private static async Task QueryViewAsync(
        HttpContext ctx, string connectionString, string sql, SqlParameter? param = null)
    {
        try
        {
            var data = await QueryAsync(connectionString, sql, param);
            await WriteJsonAsync(ctx, data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error($"[Dashboard] Query failed ({sql}): {ex.Message}");
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 503;
                await WriteJsonAsync(ctx, new { error = "Query failed", detail = ex.Message });
            }
        }
    }

    /// <summary>
    /// Execute a single-row query, serialize to JSON, and write the response.
    /// On failure, returns HTTP 503 with structured <c>{ error, detail }</c> JSON.
    /// </summary>
    private static async Task QueryViewSingleRowAsync(HttpContext ctx, string connectionString, string sql)
    {
        try
        {
            var data = await QuerySingleRowAsync(connectionString, sql);
            await WriteJsonAsync(ctx, data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error($"[Dashboard] Query failed ({sql}): {ex.Message}");
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 503;
                await WriteJsonAsync(ctx, new { error = "Query failed", detail = ex.Message });
            }
        }
    }

    /// <summary>
    /// Execute arbitrary async work with structured error handling.
    /// On failure, returns HTTP 503 with structured <c>{ error, detail }</c> JSON.
    /// Used for non-query endpoints (infra, remediation, notifications).
    /// </summary>
    private static async Task SafeExecuteAsync(HttpContext ctx, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error($"[Dashboard] Request failed ({ctx.Request.Path}): {ex.Message}");
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 503;
                await WriteJsonAsync(ctx, new { error = "Request failed", detail = ex.Message });
            }
        }
    }

    private static async Task WriteJsonAsync(HttpContext ctx, object data)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-cache";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, data, JsonOptions);
    }
}
