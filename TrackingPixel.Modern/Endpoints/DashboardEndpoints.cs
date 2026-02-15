using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TrackingPixel.Configuration;
using TrackingPixel.Services;

namespace TrackingPixel.Endpoints;

/// <summary>
/// Dashboard API endpoints - exposes SQL views as JSON for the DevOps dashboard.
/// All endpoints are read-only SELECT queries against views.
/// Restricted to loopback + explicitly allowed IPs from config.
/// </summary>
public static class DashboardEndpoints
{
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
    /// Maps all dashboard API endpoints under /api/dashboard/*
    /// Restricted to localhost + DashboardAllowedIPs from config.
    /// </summary>
    public static void MapDashboardEndpoints(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<TrackingSettings>>().Value;
        
        // Parse allowed dashboard IPs from config
        _allowedIps.Clear();
        foreach (var ipStr in settings.DashboardAllowedIPs)
        {
            if (IPAddress.TryParse(ipStr.Trim(), out var parsed))
            {
                _allowedIps.Add(parsed);
                Console.WriteLine($"[Dashboard] Allowed remote IP: {parsed}");
            }
            else
            {
                Console.WriteLine($"[Dashboard] WARNING: Could not parse allowed IP: '{ipStr}'");
            }
        }
        
        // ============================================================================
        // KPIs - Main summary cards
        // ============================================================================
        app.MapGet("/api/dashboard/kpis", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QuerySingleRowAsync(settings.ConnectionString, 
                "SELECT * FROM vw_Dashboard_KPIs");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // RISK BUCKETS - Bot risk breakdown
        // ============================================================================
        app.MapGet("/api/dashboard/risk-buckets", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var date = ctx.Request.Query["date"].FirstOrDefault();
            var (sql, param) = BuildDateFilter(date, "vw_Dashboard_RiskBuckets", "ORDER BY ScoreRange DESC");
            if (sql == null) { ctx.Response.StatusCode = 400; return; }
            
            var data = await QueryAsync(settings.ConnectionString, sql, param);
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // BOT DETAILS - Individual bot records for drill-down
        // ============================================================================
        app.MapGet("/api/dashboard/bot-details", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var bucket = ctx.Request.Query["bucket"].FirstOrDefault();
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 500) : 50;
            
            var whereClause = bucket switch
            {
                "High Risk" => "WHERE RiskBucket = 'High Risk'",
                "Medium Risk" => "WHERE RiskBucket = 'Medium Risk'",
                "Low Risk" => "WHERE RiskBucket = 'Low Risk'",
                _ => ""
            };
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {limit} * FROM vw_Dashboard_BotDetails {whereClause} ORDER BY ReceivedAt DESC");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // EVASION SUMMARY - Evasion type breakdown
        // ============================================================================
        app.MapGet("/api/dashboard/evasion-summary", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var date = ctx.Request.Query["date"].FirstOrDefault();
            var (sql, param) = BuildDateFilter(date, "vw_Dashboard_EvasionSummary");
            if (sql == null) { ctx.Response.StatusCode = 400; return; }
            
            var data = await QueryAsync(settings.ConnectionString, sql, param);
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // EVASION DETAILS - Individual evasion records
        // ============================================================================
        app.MapGet("/api/dashboard/evasion-details", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var type = ctx.Request.Query["type"].FirstOrDefault();
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 500) : 50;
            
            var whereClause = type switch
            {
                "Both" => "WHERE EvasionType = 'Both'",
                "Canvas Only" => "WHERE EvasionType = 'Canvas Only'",
                "WebGL Only" => "WHERE EvasionType = 'WebGL Only'",
                _ => ""
            };
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {limit} * FROM vw_Dashboard_EvasionDetails {whereClause} ORDER BY ReceivedAt DESC");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // TIMING ANALYSIS - Script execution timing
        // ============================================================================
        app.MapGet("/api/dashboard/timing", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var date = ctx.Request.Query["date"].FirstOrDefault();
            var (sql, param) = BuildDateFilter(date, "vw_Dashboard_TimingAnalysis");
            if (sql == null) { ctx.Response.StatusCode = 400; return; }
            
            var data = await QueryAsync(settings.ConnectionString, sql, param);
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // FINGERPRINT DETAILS - Fingerprint breakdown
        // ============================================================================
        app.MapGet("/api/dashboard/fingerprints", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 500) : 50;
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {limit} * FROM vw_Dashboard_FingerprintDetails ORDER BY Hits DESC");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // GPU DISTRIBUTION - For fingerprint drill-down
        // ============================================================================
        app.MapGet("/api/dashboard/gpu-distribution", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 200) : 20;
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {limit} * FROM vw_Dashboard_GPUDistribution ORDER BY HitCount DESC");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // SCREEN DISTRIBUTION - Resolution breakdown
        // ============================================================================
        app.MapGet("/api/dashboard/screen-distribution", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 200) : 20;
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {limit} * FROM vw_Dashboard_ScreenDistribution ORDER BY HitCount DESC");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // TRENDS - Day-over-day comparison
        // ============================================================================
        app.MapGet("/api/dashboard/trends", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var days = int.TryParse(ctx.Request.Query["days"].FirstOrDefault(), out var d) ? Math.Clamp(d, 1, 365) : 7;
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {days} * FROM vw_Dashboard_Trends ORDER BY DateBucket DESC");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // LIVE FEED - Real-time activity
        // ============================================================================
        app.MapGet("/api/dashboard/live-feed", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 200) : 25;
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {limit} * FROM vw_Dashboard_LiveFeed ORDER BY ReceivedAt DESC");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // DEVICE BREAKDOWN - Device/OS/Browser stats
        // ============================================================================
        app.MapGet("/api/dashboard/devices", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var date = ctx.Request.Query["date"].FirstOrDefault();
            var (sql, param) = BuildDateFilter(date, "vw_PiXL_DeviceBreakdown");
            if (sql == null) { ctx.Response.StatusCode = 400; return; }
            
            var data = await QueryAsync(settings.ConnectionString, sql, param);
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // CROSS-NETWORK DEVICES - Devices seen from multiple IPs
        // ============================================================================
        app.MapGet("/api/dashboard/cross-network", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? Math.Clamp(l, 1, 200) : 20;
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {limit} * FROM vw_PiXL_DeviceIdentity WHERE UniqueIPAddresses > 1 ORDER BY UniqueIPAddresses DESC");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // NEW MATERIALIZED VIEWS — PiXL_Parsed (instant reads)
        // ============================================================================
        
        app.MapGet("/api/dash/health", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QuerySingleRowAsync(settings.ConnectionString,
                "SELECT * FROM vw_Dash_SystemHealth");
            await WriteJsonAsync(ctx, data);
        });
        
        app.MapGet("/api/dash/hourly", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var hours = int.TryParse(ctx.Request.Query["hours"].FirstOrDefault(), out var h) ? Math.Clamp(h, 1, 720) : 72;
            var data = await QueryAsync(settings.ConnectionString,
                $"SELECT TOP {hours} * FROM vw_Dash_HourlyRollup ORDER BY HourBucket DESC");
            await WriteJsonAsync(ctx, data);
        });
        
        app.MapGet("/api/dash/bots", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QueryAsync(settings.ConnectionString,
                "SELECT * FROM vw_Dash_BotBreakdown ORDER BY SortOrder");
            await WriteJsonAsync(ctx, data);
        });
        
        app.MapGet("/api/dash/bot-signals", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QueryAsync(settings.ConnectionString,
                "SELECT TOP 20 * FROM vw_Dash_TopBotSignals ORDER BY TimesTriggered DESC");
            await WriteJsonAsync(ctx, data);
        });
        
        app.MapGet("/api/dash/devices", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QueryAsync(settings.ConnectionString,
                "SELECT TOP 30 * FROM vw_Dash_DeviceBreakdown ORDER BY HitCount DESC");
            await WriteJsonAsync(ctx, data);
        });
        
        app.MapGet("/api/dash/evasion", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QuerySingleRowAsync(settings.ConnectionString,
                "SELECT * FROM vw_Dash_EvasionSummary");
            await WriteJsonAsync(ctx, data);
        });
        
        app.MapGet("/api/dash/behavior", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QueryAsync(settings.ConnectionString,
                "SELECT * FROM vw_Dash_BehavioralAnalysis");
            await WriteJsonAsync(ctx, data);
        });
        
        app.MapGet("/api/dash/recent", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;
            var data = await QueryAsync(settings.ConnectionString,
                "SELECT * FROM vw_Dash_RecentHits");
            await WriteJsonAsync(ctx, data);
        });
        
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
        app.MapGet("/dashboard", async (HttpContext ctx, IWebHostEnvironment env) =>
        {
            if (!RequireLoopback(ctx)) return;
            var dashboardPath = Path.Combine(env.WebRootPath ?? "wwwroot", "dashboard.html");
            if (!File.Exists(dashboardPath))
                dashboardPath = Path.Combine(env.ContentRootPath, "wwwroot", "dashboard.html");
            
            if (File.Exists(dashboardPath))
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.SendFileAsync(dashboardPath);
            }
            else
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync($"Dashboard not found. Looked in: {dashboardPath}");
            }
        });
        
        // Tron Operations dashboard (DevOps daily driver)
        app.MapGet("/tron", async (HttpContext ctx, IWebHostEnvironment env) =>
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
        });
        
        // Tron Analytics dashboard (SPA — same file, JS handles view switch)
        app.MapGet("/tron/analytics", async (HttpContext ctx, IWebHostEnvironment env) =>
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
        });
    }
    
    // ============================================================================
    // HELPER METHODS
    // ============================================================================
    
    /// <summary>
    /// Builds a parameterized date filter for dashboard views.
    /// Returns the SQL string and an optional SqlParameter.
    /// </summary>
    private static (string? Sql, SqlParameter? Param) BuildDateFilter(
        string? date, string viewName, string orderBy = "")
    {
        if (string.IsNullOrEmpty(date) || date == "today")
        {
            return ($"SELECT * FROM {viewName} WHERE DateBucket = CAST(GETUTCDATE() AS DATE) {orderBy}", null);
        }
        
        // Safe parse — rejects garbage input instead of throwing FormatException
        if (!DateTime.TryParse(date, out var parsed))
        {
            return (null, null);
        }
        
        return (
            $"SELECT * FROM {viewName} WHERE DateBucket = @DateFilter {orderBy}",
            new SqlParameter("@DateFilter", System.Data.SqlDbType.Date) { Value = parsed }
        );
    }
    
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
    
    private static async Task WriteJsonAsync(HttpContext ctx, object data)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(data, JsonOptions));
    }
}
