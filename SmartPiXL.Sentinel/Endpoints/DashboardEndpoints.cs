using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Sentinel.Services;
using SmartPiXL.Services;

namespace SmartPiXL.Sentinel.Endpoints;

// ============================================================================
// DASHBOARD API ENDPOINTS — Read-only JSON API over SQL views.
//
// ARCHITECTURE:
//   /api/dash/* routes  →  QueryAsync/QuerySingleRowAsync  →  vw_Dash_* views
//   (HTTP GET)              (ADO.NET SqlDataReader)              (PiXL.Parsed materialized data)
//
// All endpoints are restricted to localhost + DashboardAllowedIPs from config.
// External requests receive a 404 (not 403) to avoid revealing the API exists.
//
// SQL VIEW MAPPING:
//   /api/dash/health       →  vw_Dash_SystemHealth       (single row aggregate)
//   /api/dash/hourly       →  vw_Dash_HourlyRollup       (time-bucketed rollup)
//   /api/dash/bots         →  vw_Dash_BotBreakdown       (risk tiers)
//   /api/dash/bot-signals  →  vw_Dash_TopBotSignals      (detection signal frequency)
//   /api/dash/devices      →  vw_Dash_DeviceBreakdown    (browser/OS/device stats)
//   /api/dash/evasion      →  vw_Dash_EvasionSummary     (canvas/WebGL evasion)
//   /api/dash/behavior     →  vw_Dash_BehavioralAnalysis (timing/interaction signals)
//   /api/dash/recent       →  vw_Dash_RecentHits         (latest raw hits)
//   /api/dash/fingerprints →  vw_Dash_FingerprintClusters(grouped fingerprints)
//   /api/dash/infra        →  InfraHealthService         (live OS/SQL/IIS probes)
//   /api/dash/pipeline     →  vw_Dash_PipelineHealth     (ETL watermarks & lags)
//   /api/dash/sessions     →  vw_Dash_SessionSummary     (session reconstruction)
//   /api/dash/dead-internet → vw_Dash_DeadInternet       (dead internet index)
//   /api/dash/customer-quality → vw_Dash_CustomerQuality (traffic quality trending)
//   /api/dash/cross-customer → vw_Dash_CrossCustomer     (cross-customer intelligence)
//   /api/dash/impossible-travel → vw_Dash_ImpossibleTravel (geo anomaly detection)
//   /api/dash/device-lifecycle → vw_Dash_DeviceLifecycle  (device age/lifecycle)
//   /api/dash/subnet-clusters → vw_Dash_SubnetClusters   (subnet reputation)
//
// DASHBOARD HTML:
//   /tron                  →  wwwroot/tron.html          (Tron ops dashboard SPA)
//   /tron/analytics        →  wwwroot/tron.html          (same SPA, JS view switch)
// ============================================================================

/// <summary>
/// Dashboard API endpoints — exposes SQL views as JSON for the Tron operations dashboard.
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

    private static HashSet<IPAddress> _allowedIps = new();
    private static ITrackingLogger _logger = null!;

    // ── Endpoint-level caches (15 s TTL) ────────────────────────────
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);

    private static volatile Dictionary<string, object?>? _healthCache;
    private static DateTime _healthExpiry = DateTime.MinValue;

    private static volatile List<Dictionary<string, object?>>? _xavierCache;
    private static DateTime _xavierExpiry = DateTime.MinValue;

    private static bool RequireLoopback(HttpContext ctx)
    {
        var remoteIp = ctx.Connection.RemoteIpAddress;
        if (remoteIp is null) return true;

        if (IPAddress.IsLoopback(remoteIp)) return true;

        var localIp = ctx.Connection.LocalIpAddress;
        if (localIp != null && remoteIp.Equals(localIp)) return true;

        var checkIp = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;
        if (_allowedIps.Contains(checkIp)) return true;

        ctx.Response.StatusCode = 404;
        return false;
    }

    public static void MapDashboardEndpoints(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<TrackingSettings>>().Value;
        _logger = app.Services.GetRequiredService<ITrackingLogger>();

        _allowedIps.Clear();
        foreach (var ipStr in settings.DashboardAllowedIPs)
        {
            if (IPAddress.TryParse(ipStr.Trim(), out var parsed))
            {
                _allowedIps.Add(parsed);
                _logger.Info($"[Dashboard] Allowed remote IP: {parsed}");
            }
            else
            {
                _logger.Warning($"[Dashboard] Could not parse allowed IP: '{ipStr}'");
            }
        }

        // ====================================================================
        // ORIGINAL DASHBOARD VIEWS (ported from Worker)
        // ====================================================================

        app.MapGet("/api/dash/health", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var now = DateTime.UtcNow;
            var cached = _healthCache;
            if (cached is not null && now < _healthExpiry)
            {
                await WriteJsonAsync(ctx, cached);
                return;
            }
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QuerySingleRowAsync(settings.ConnectionString,
                    "SELECT * FROM vw_Dash_SystemHealth");
                _healthCache = data;
                _healthExpiry = now + CacheDuration;
                await WriteJsonAsync(ctx, data);
            });
        });

        app.MapGet("/api/dash/hourly", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var hours = int.TryParse(ctx.Request.Query["hours"].FirstOrDefault(), out var h) ? Math.Clamp(h, 1, 720) : 72;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT TOP (@N) * FROM vw_Dash_HourlyRollup ORDER BY HourBucket DESC",
                new SqlParameter("@N", hours));
        });

        app.MapGet("/api/dash/bots", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT * FROM vw_Dash_BotBreakdown ORDER BY SortOrder");
        });

        app.MapGet("/api/dash/bot-signals", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT TOP 20 * FROM vw_Dash_TopBotSignals ORDER BY TimesTriggered DESC");
        });

        app.MapGet("/api/dash/devices", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT TOP 30 * FROM vw_Dash_DeviceBreakdown ORDER BY HitCount DESC");
        });

        app.MapGet("/api/dash/evasion", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewSingleRowAsync(ctx, settings.ConnectionString,
                "SELECT * FROM vw_Dash_EvasionSummary");
        });

        app.MapGet("/api/dash/behavior", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT * FROM vw_Dash_BehavioralAnalysis");
        });

        app.MapGet("/api/dash/recent", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT * FROM vw_Dash_RecentHits");
        });

        app.MapGet("/api/dash/fingerprints", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 200) : 50;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT TOP (@N) * FROM vw_Dash_FingerprintClusters ORDER BY HitCount DESC",
                new SqlParameter("@N", limit));
        });

        // ====================================================================
        // INFRASTRUCTURE HEALTH — Services, SQL, Websites, App metrics
        // ====================================================================
        app.MapGet("/api/dash/infra", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var infraService = ctx.RequestServices.GetRequiredService<InfraHealthService>();
                var snapshot = await infraService.GetHealthAsync();
                await WriteJsonAsync(ctx, snapshot);
            });
        });

        // ====================================================================
        // XAVIER SYNC HEALTH — Cached 15 s
        // ====================================================================
        app.MapGet("/api/dash/xavier-sync", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var now = DateTime.UtcNow;
            var cached = _xavierCache;
            if (cached is not null && now < _xavierExpiry)
            {
                await WriteJsonAsync(ctx, cached);
                return;
            }
            await SafeExecuteAsync(ctx, async () =>
            {
                var data = await QueryAsync(settings.ConnectionString,
                    "SELECT * FROM vw_Dash_XavierSync");
                _xavierCache = data;
                _xavierExpiry = now + CacheDuration;
                await WriteJsonAsync(ctx, data);
            });
        });

        // ====================================================================
        // PIPELINE HEALTH — Device, IP, Visit, Match tables & watermarks
        // ====================================================================
        app.MapGet("/api/dash/pipeline", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewSingleRowAsync(ctx, settings.ConnectionString,
                "SELECT * FROM vw_Dash_PipelineHealth");
        });

        // ====================================================================
        // ENRICHMENT-AWARE VIEWS (Phase 8+ — new in Sentinel)
        // ====================================================================

        app.MapGet("/api/dash/sessions", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT * FROM vw_Dash_SessionSummary");
        });

        app.MapGet("/api/dash/dead-internet", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT * FROM vw_Dash_DeadInternet");
        });

        app.MapGet("/api/dash/customer-quality", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT * FROM vw_Dash_CustomerQuality");
        });

        app.MapGet("/api/dash/cross-customer", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT * FROM vw_Dash_CrossCustomer");
        });

        app.MapGet("/api/dash/cross-customer/detail", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT TOP 100 * FROM vw_Dash_CrossCustomerDetail");
        });

        app.MapGet("/api/dash/impossible-travel", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT * FROM vw_Dash_ImpossibleTravel");
        });

        app.MapGet("/api/dash/device-lifecycle", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT * FROM vw_Dash_DeviceLifecycle");
        });

        app.MapGet("/api/dash/device-hops", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT TOP 100 * FROM vw_Dash_DeviceCustomerHops");
        });

        app.MapGet("/api/dash/subnet-clusters", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT TOP 100 * FROM vw_Dash_SubnetClusters");
        });

        // ====================================================================
        // REMEDIATION ENDPOINTS — View, approve, skip remediations
        // ====================================================================

        app.MapGet("/api/dash/remediations", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await QueryViewAsync(ctx, settings.ConnectionString,
                "SELECT TOP 50 * FROM Ops.RemediationLog ORDER BY DetectedAtUtc DESC");
        });

        app.MapPost("/api/dash/remediation/approve/{id:int}", async (HttpContext ctx, int id) =>
        {
            if (!RequireLoopback(ctx)) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var remediation = ctx.RequestServices.GetRequiredService<RemediationService>();
                var (success, message) = await remediation.ExecuteRemediationAsync(id, ctx.RequestAborted);
                await WriteJsonAsync(ctx, new { success, message });
            });
        });

        app.MapPost("/api/dash/remediation/skip/{id:int}", async (HttpContext ctx, int id) =>
        {
            if (!RequireLoopback(ctx)) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var remediation = ctx.RequestServices.GetRequiredService<RemediationService>();
                var skipped = await remediation.SkipRemediationAsync(id, ctx.RequestAborted);
                await WriteJsonAsync(ctx, new { success = skipped, message = skipped ? "Skipped" : "Not found or not pending" });
            });
        });

        // ====================================================================
        // CIRCUIT BREAKER RESET — Proxied to IIS Edge via HTTP
        // ====================================================================
        app.MapPost("/api/dash/circuit-reset", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
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
        // TEST NOTIFICATION — Verify email + SMS notification stack
        // ====================================================================
        app.MapPost("/api/dash/test-notify", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var email = ctx.RequestServices.GetRequiredService<EmailNotificationService>();
                var (emailSent, smsSent) = await email.NotifyAsync(
                    "TestNotification",
                    "Test Alert — System OK",
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
        app.MapGet("/tron/analytics", ServeTronHtml);
        app.MapGet("/tron/{file}", ServeTronModule);

        _logger.Info("[Dashboard] All dashboard endpoints mapped: /tron, /api/dash/*");
    }

    private static async Task ServeTronHtml(HttpContext ctx, IWebHostEnvironment env)
    {
        if (!RequireLoopback(ctx)) return;
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

    private static async Task ServeTronModule(HttpContext ctx, IWebHostEnvironment env, string file)
    {
        if (!RequireLoopback(ctx)) return;

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
    // ADO.NET HELPERS — Thin wrappers for read-only view queries.
    // ========================================================================

    private static async Task<List<Dictionary<string, object?>>> QueryAsync(
        string connectionString, string sql, SqlParameter? param = null)
    {
        var results = new List<Dictionary<string, object?>>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = 30;

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

    // ========================================================================
    // SAFE QUERY WRAPPERS — Execute SQL view queries with error handling.
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
            _logger.Error($"[Dashboard] Query failed: {ex.Message}");
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
            _logger.Error($"[Dashboard] Query failed: {ex.Message}");
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
            _logger.Error($"[Dashboard] Request failed: {ex.Message}");
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
