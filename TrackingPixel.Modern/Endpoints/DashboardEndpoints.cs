using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TrackingPixel.Configuration;
using TrackingPixel.Services;

namespace TrackingPixel.Endpoints;

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
    /// <summary>
    /// Shared JSON serializer options for all dashboard responses.
    /// <para>
    /// <c>CamelCase</c> naming matches JavaScript conventions (the Tron SPA expects camelCase keys).
    /// <c>WriteIndented = false</c> minimizes payload size for dashboard polling.
    /// These are static to avoid per-request allocation of the options object.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new() 
    { 
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Parsed set of allowed remote IPs (from Tracking:DashboardAllowedIPs in appsettings.json).
    /// Built once at endpoint registration time. Empty = localhost only.
    /// </summary>
    private static HashSet<IPAddress> _allowedIps = new();

    /// <summary>Logger resolved once at endpoint registration time.</summary>
    private static ITrackingLogger _logger = null!;

    /// <summary>
    /// Returns true if the request originates from this machine or an allowed IP.
    /// Checks loopback (127.0.0.1/::1), same-machine (remote == local IP),
    /// then the DashboardAllowedIPs allow-list from config.
    /// Rejects external requests with 404 to avoid revealing the API exists.
    /// </summary>
    private static bool RequireLoopback(HttpContext ctx)
    {
        var remoteIp = ctx.Connection.RemoteIpAddress;
        if (remoteIp is null) return true; // null = Unix socket / pipe, always local
        
        if (IPAddress.IsLoopback(remoteIp)) return true;
        
        // Same-machine check: browser on this server connecting to its own NIC IP
        var localIp = ctx.Connection.LocalIpAddress;
        if (localIp != null && remoteIp.Equals(localIp)) return true;
        
        // Allowed remote IPs from config (e.g. developer workstation)
        // Handle IPv4-mapped IPv6 addresses (::ffff:x.x.x.x)
        var checkIp = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;
        if (_allowedIps.Contains(checkIp)) return true;
        
        // 404 — don't reveal the endpoint exists to outsiders
        ctx.Response.StatusCode = 404;
        return false;
    }

    /// <summary>
    /// Maps all dashboard API endpoints under <c>/api/dash/*</c> and the Tron HTML routes.
    /// <para>
    /// Called once at startup from <c>Program.cs</c>. Captures <see cref="TrackingSettings"/>
    /// by value (the connection string and allowed IPs won't change at runtime).
    /// Each endpoint is a minimal API lambda that checks loopback access, runs
    /// a parameterized SQL query against a <c>vw_Dash_*</c> view, and serializes
    /// the result as JSON.
    /// </para>
    /// </summary>
    public static void MapDashboardEndpoints(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<TrackingSettings>>().Value;
        _logger = app.Services.GetRequiredService<ITrackingLogger>();
        
        // Parse allowed dashboard IPs from config into a HashSet for O(1) lookup.
        // Runs once at startup — config changes require app restart.
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
        
        // ============================================================================
        // MATERIALIZED TABLE — PiXL.Parsed (instant reads, powers Tron dashboard)
        // Each endpoint maps 1:1 to a SQL view. The views do all the heavy lifting;
        // the C# code just serializes the rows to JSON and enforces access control.
        // ============================================================================
        
        // System-wide KPI summary: total hits, bot %, evasion %, uptime
        app.MapGet("/api/dash/health", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QuerySingleRowAsync(settings.ConnectionString,
                "SELECT * FROM vw_Dash_SystemHealth");
            await WriteJsonAsync(ctx, data);
        });
        
        // Hourly traffic rollup — ?hours=N controls depth (default 72, max 720 = 30 days)
        app.MapGet("/api/dash/hourly", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var hours = int.TryParse(ctx.Request.Query["hours"].FirstOrDefault(), out var h) ? Math.Clamp(h, 1, 720) : 72;
            var data = await QueryAsync(settings.ConnectionString,
                $"SELECT TOP {hours} * FROM vw_Dash_HourlyRollup ORDER BY HourBucket DESC");
            await WriteJsonAsync(ctx, data);
        });
        
        // Bot risk tier breakdown (High/Medium/Low/Clean) with sort order
        app.MapGet("/api/dash/bots", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QueryAsync(settings.ConnectionString,
                "SELECT * FROM vw_Dash_BotBreakdown ORDER BY SortOrder");
            await WriteJsonAsync(ctx, data);
        });
        
        // Top 20 detection signals by frequency (e.g., "headless", "webdriver", "rapid-fire")
        app.MapGet("/api/dash/bot-signals", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QueryAsync(settings.ConnectionString,
                "SELECT TOP 20 * FROM vw_Dash_TopBotSignals ORDER BY TimesTriggered DESC");
            await WriteJsonAsync(ctx, data);
        });
        
        // Top 30 device/browser/OS combinations by hit count
        app.MapGet("/api/dash/devices", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QueryAsync(settings.ConnectionString,
                "SELECT TOP 30 * FROM vw_Dash_DeviceBreakdown ORDER BY HitCount DESC");
            await WriteJsonAsync(ctx, data);
        });
        
        // Canvas/WebGL evasion summary (single aggregate row)
        app.MapGet("/api/dash/evasion", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QuerySingleRowAsync(settings.ConnectionString,
                "SELECT * FROM vw_Dash_EvasionSummary");
            await WriteJsonAsync(ctx, data);
        });
        
        // Behavioral analysis: interaction signals, timing anomalies
        app.MapGet("/api/dash/behavior", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QueryAsync(settings.ConnectionString,
                "SELECT * FROM vw_Dash_BehavioralAnalysis");
            await WriteJsonAsync(ctx, data);
        });
        
        // Most recent raw hits (for the live feed panel in Tron)
        app.MapGet("/api/dash/recent", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QueryAsync(settings.ConnectionString,
                "SELECT * FROM vw_Dash_RecentHits");
            await WriteJsonAsync(ctx, data);
        });
        
        // Fingerprint clusters — ?limit=N controls depth (default 50, max 200)
        app.MapGet("/api/dash/fingerprints", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 200) : 50;
            var data = await QueryAsync(settings.ConnectionString,
                $"SELECT TOP {limit} * FROM vw_Dash_FingerprintClusters ORDER BY HitCount DESC");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // INFRASTRUCTURE HEALTH — Services, SQL, Websites, App metrics
        // ============================================================================
        app.MapGet("/api/dash/infra", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var infraService = ctx.RequestServices.GetRequiredService<InfraHealthService>();
            var snapshot = await infraService.GetHealthAsync();
            await WriteJsonAsync(ctx, snapshot);
        });
        
        // ============================================================================
        // PIPELINE HEALTH — Device, IP, Visit, Match tables & watermarks
        // ============================================================================
        app.MapGet("/api/dash/pipeline", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QuerySingleRowAsync(settings.ConnectionString,
                "SELECT * FROM vw_Dash_PipelineHealth");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // DASHBOARD HTML PAGES
        // ============================================================================
        
        // Tron Operations dashboard (DevOps daily driver)
        // Both /tron and /tron/analytics serve the same SPA — JS handles the view switch.
        app.MapGet("/tron", ServeTronHtml);
        app.MapGet("/tron/analytics", ServeTronHtml);
    }
    
    /// <summary>
    /// Serves <c>wwwroot/tron.html</c> with aggressive no-cache headers.
    /// <para>
    /// Shared handler for both <c>/tron</c> and <c>/tron/analytics</c> routes.
    /// The Tron SPA reads <c>window.location.pathname</c> to decide which view to show,
    /// so both routes serve the identical HTML file. No-cache headers ensure the
    /// dashboard always reflects the latest deployed version — critical during
    /// rapid iteration on the Tron UI.
    /// </para>
    /// <para>
    /// Falls back to <c>ContentRootPath/wwwroot/</c> when <c>WebRootPath</c> is null
    /// (can happen in certain IIS hosting configurations).
    /// </para>
    /// </summary>
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
    
    // ============================================================================
    // HELPER METHODS — Thin ADO.NET wrappers for read-only view queries.
    // Each opens a pooled connection, runs a single SELECT, and returns
    // Dictionary<string, object?> rows. No stored procedures, no writes.
    // ============================================================================
    
    /// <summary>
    /// Executes a SELECT query and returns all rows as a list of column-name → value dictionaries.
    /// <para>
    /// Uses <see cref="SqlConnection"/> pooling (connection string as key) so we don't
    /// hold persistent connections open. The 30-second command timeout prevents
    /// runaway queries from blocking the dashboard indefinitely.
    /// </para>
    /// <para>
    /// <see cref="DBNull.Value"/> is normalized to <c>null</c> so JSON serialization
    /// emits <c>null</c> instead of an empty object.
    /// </para>
    /// </summary>
    /// <param name="connectionString">SQL Server connection string (from TrackingSettings).</param>
    /// <param name="sql">The SELECT statement to execute. Must NOT contain user input unless parameterized.</param>
    /// <param name="param">Optional SqlParameter for parameterized queries (e.g., date filters).</param>
    /// <returns>List of rows, each represented as a string → object dictionary.</returns>
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
    
    /// <summary>
    /// Convenience wrapper: executes a query expected to return exactly one row.
    /// Returns an empty dictionary if the view is empty (e.g., no data yet after a fresh deploy).
    /// Used by single-aggregate endpoints like <c>/api/dash/health</c> and <c>/api/dash/evasion</c>.
    /// </summary>
    private static async Task<Dictionary<string, object?>> QuerySingleRowAsync(string connectionString, string sql)
    {
        var results = await QueryAsync(connectionString, sql);
        return results.FirstOrDefault() ?? new Dictionary<string, object?>();
    }
    
    /// <summary>
    /// Writes a JSON response with <c>application/json</c> content type and no-cache header.
    /// <para>
    /// Uses the shared <see cref="JsonOptions"/> instance (camelCase, not indented).
    /// No-cache ensures dashboard polling always gets fresh data, never a stale CDN/proxy response.
    /// </para>
    /// </summary>
    private static async Task WriteJsonAsync(HttpContext ctx, object data)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-cache";
        // Stream directly to response body — avoids intermediate string allocation.
        // For /api/dash/recent (~60KB), this eliminates a 60KB string alloc.
        await JsonSerializer.SerializeAsync(ctx.Response.Body, data, JsonOptions);
    }
}
