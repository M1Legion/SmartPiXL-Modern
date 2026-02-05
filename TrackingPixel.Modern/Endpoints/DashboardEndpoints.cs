using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TrackingPixel.Configuration;
using TrackingPixel.Services;

namespace TrackingPixel.Endpoints;

/// <summary>
/// Dashboard API endpoints - exposes SQL views as JSON for the DevOps dashboard.
/// All endpoints are read-only SELECT queries against views.
/// </summary>
public static class DashboardEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new() 
    { 
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Maps all dashboard API endpoints under /api/dashboard/*
    /// </summary>
    public static void MapDashboardEndpoints(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<TrackingSettings>>().Value;
        var logger = app.Services.GetRequiredService<ITrackingLogger>();
        
        // ============================================================================
        // KPIs - Main summary cards
        // ============================================================================
        app.MapGet("/api/dashboard/kpis", async (HttpContext ctx) =>
        {
            var data = await QuerySingleRowAsync(settings.ConnectionString, 
                "SELECT * FROM vw_Dashboard_KPIs", logger, "GetKPIs");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // RISK BUCKETS - Bot risk breakdown
        // ============================================================================
        app.MapGet("/api/dashboard/risk-buckets", async (HttpContext ctx) =>
        {
            var date = ctx.Request.Query["date"].FirstOrDefault();
            var dateFilter = GetSafeDateFilter(date);
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT * FROM vw_Dashboard_RiskBuckets WHERE DateBucket = {dateFilter} ORDER BY ScoreRange DESC", logger, "GetRiskBuckets");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // BOT DETAILS - Individual bot records for drill-down
        // ============================================================================
        app.MapGet("/api/dashboard/bot-details", async (HttpContext ctx) =>
        {
            var bucket = ctx.Request.Query["bucket"].FirstOrDefault();
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? l : 50;
            
            var whereClause = bucket switch
            {
                "High Risk" => "WHERE RiskBucket = 'High Risk'",
                "Medium Risk" => "WHERE RiskBucket = 'Medium Risk'",
                "Low Risk" => "WHERE RiskBucket = 'Low Risk'",
                _ => ""
            };
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {limit} * FROM vw_Dashboard_BotDetails {whereClause} ORDER BY ReceivedAt DESC", logger, "GetBotDetails");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // EVASION SUMMARY - Evasion type breakdown
        // ============================================================================
        app.MapGet("/api/dashboard/evasion-summary", async (HttpContext ctx) =>
        {
            var date = ctx.Request.Query["date"].FirstOrDefault();
            var dateFilter = GetSafeDateFilter(date);
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT * FROM vw_Dashboard_EvasionSummary WHERE DateBucket = {dateFilter}", logger, "GetEvasionSummary");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // EVASION DETAILS - Individual evasion records
        // ============================================================================
        app.MapGet("/api/dashboard/evasion-details", async (HttpContext ctx) =>
        {
            var type = ctx.Request.Query["type"].FirstOrDefault();
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? l : 50;
            
            var whereClause = type switch
            {
                "Both" => "WHERE EvasionType = 'Both'",
                "Canvas Only" => "WHERE EvasionType = 'Canvas Only'",
                "WebGL Only" => "WHERE EvasionType = 'WebGL Only'",
                _ => ""
            };
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {limit} * FROM vw_Dashboard_EvasionDetails {whereClause} ORDER BY ReceivedAt DESC", logger, "GetEvasionDetails");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // TIMING ANALYSIS - Script execution timing
        // ============================================================================
        app.MapGet("/api/dashboard/timing", async (HttpContext ctx) =>
        {
            var date = ctx.Request.Query["date"].FirstOrDefault();
            var dateFilter = GetSafeDateFilter(date);
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT * FROM vw_Dashboard_TimingAnalysis WHERE DateBucket = {dateFilter}", logger, "GetTimingAnalysis");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // FINGERPRINT DETAILS - Fingerprint breakdown
        // ============================================================================
        app.MapGet("/api/dashboard/fingerprints", async (HttpContext ctx) =>
        {
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? l : 50;
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {limit} * FROM vw_Dashboard_FingerprintDetails ORDER BY Hits DESC", logger, "GetFingerprintDetails");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // GPU DISTRIBUTION - For fingerprint drill-down
        // ============================================================================
        app.MapGet("/api/dashboard/gpu-distribution", async (HttpContext ctx) =>
        {
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? l : 20;
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {limit} * FROM vw_Dashboard_GPUDistribution ORDER BY HitCount DESC", logger, "GetGPUDistribution");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // SCREEN DISTRIBUTION - Resolution breakdown
        // ============================================================================
        app.MapGet("/api/dashboard/screen-distribution", async (HttpContext ctx) =>
        {
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? l : 20;
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {limit} * FROM vw_Dashboard_ScreenDistribution ORDER BY HitCount DESC", logger, "GetScreenDistribution");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // TRENDS - Day-over-day comparison
        // ============================================================================
        app.MapGet("/api/dashboard/trends", async (HttpContext ctx) =>
        {
            var days = int.TryParse(ctx.Request.Query["days"].FirstOrDefault(), out var d) ? d : 7;
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {days} * FROM vw_Dashboard_Trends ORDER BY DateBucket DESC", logger, "GetTrends");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // LIVE FEED - Real-time activity
        // ============================================================================
        app.MapGet("/api/dashboard/live-feed", async (HttpContext ctx) =>
        {
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? l : 25;
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {limit} * FROM vw_Dashboard_LiveFeed ORDER BY ReceivedAt DESC", logger, "GetLiveFeed");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // DEVICE BREAKDOWN - Device/OS/Browser stats
        // ============================================================================
        app.MapGet("/api/dashboard/devices", async (HttpContext ctx) =>
        {
            var date = ctx.Request.Query["date"].FirstOrDefault();
            var dateFilter = GetSafeDateFilter(date);
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT * FROM vw_PiXL_DeviceBreakdown WHERE DateBucket = {dateFilter}", logger, "GetDeviceBreakdown");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // CROSS-NETWORK DEVICES - Devices seen from multiple IPs
        // ============================================================================
        app.MapGet("/api/dashboard/cross-network", async (HttpContext ctx) =>
        {
            var limit = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault(), out var l) ? l : 20;
            
            var data = await QueryAsync(settings.ConnectionString, 
                $"SELECT TOP {limit} * FROM vw_PiXL_DeviceIdentity WHERE UniqueIPAddresses > 1 ORDER BY UniqueIPAddresses DESC", logger, "GetCrossNetworkDevices");
            await WriteJsonAsync(ctx, data);
        });
        
        // ============================================================================
        // DASHBOARD HTML PAGE
        // ============================================================================
        app.MapGet("/dashboard", async (HttpContext ctx, IWebHostEnvironment env) =>
        {
            // Use WebRootPath which points to the wwwroot folder
            var dashboardPath = Path.Combine(env.WebRootPath ?? "wwwroot", "dashboard.html");
            
            // Fallback to source directory if not found in WebRootPath
            if (!File.Exists(dashboardPath))
            {
                dashboardPath = Path.Combine(env.ContentRootPath, "wwwroot", "dashboard.html");
            }
            
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
    }
    
    // ============================================================================
    // HELPER METHODS
    // ============================================================================
    
    private static async Task<List<Dictionary<string, object?>>> QueryAsync(string connectionString, string sql, ITrackingLogger logger, string endpointName)
    {
        try
        {
            var results = new List<Dictionary<string, object?>>();
            
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            
            await using var cmd = new SqlCommand(sql, conn);
            cmd.CommandTimeout = 30;
            
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
        catch (Exception ex)
        {
            logger.Error($"{endpointName}: Query failed", ex);
            return new List<Dictionary<string, object?>>();
        }
    }
    
    private static async Task<Dictionary<string, object?>> QuerySingleRowAsync(string connectionString, string sql, ITrackingLogger logger, string endpointName)
    {
        var results = await QueryAsync(connectionString, sql, logger, endpointName);
        return results.FirstOrDefault() ?? new Dictionary<string, object?>();
    }
    
    private static async Task WriteJsonAsync(HttpContext ctx, object data)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-cache";
        
        // Convert dictionary keys to camelCase for JavaScript consumption
        var camelCased = ToCamelCase(data);
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(camelCased, JsonOptions));
    }
    
    /// <summary>
    /// Recursively converts dictionary keys from PascalCase to camelCase.
    /// SQL views return PascalCase column names; JavaScript expects camelCase.
    /// </summary>
    private static object? ToCamelCase(object? data)
    {
        if (data is Dictionary<string, object?> dict)
        {
            return dict.ToDictionary(
                kvp => ToCamelCaseString(kvp.Key),
                kvp => ToCamelCase(kvp.Value)
            );
        }
        
        if (data is List<Dictionary<string, object?>> list)
        {
            return list.Select(d => ToCamelCase(d)).ToList();
        }
        
        return data;
    }
    
    private static string ToCamelCaseString(string s)
    {
        if (string.IsNullOrEmpty(s) || !char.IsUpper(s[0]))
            return s;
        
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (i > 0 && i + 1 < chars.Length && !char.IsUpper(chars[i + 1]))
                break;
            chars[i] = char.ToLowerInvariant(chars[i]);
        }
        return new string(chars);
    }
    
    /// <summary>
    /// Safely parses a date filter to prevent SQL injection.
    /// Returns a SQL expression for the date filter.
    /// </summary>
    private static string GetSafeDateFilter(string? dateInput)
    {
        if (string.IsNullOrEmpty(dateInput) || dateInput.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            return "CAST(GETUTCDATE() AS DATE)";
        }
        
        // Validate that the input is a valid date format (YYYY-MM-DD)
        if (DateTime.TryParse(dateInput, out var parsedDate))
        {
            return $"'{parsedDate:yyyy-MM-dd}'";
        }
        
        // Default to today if invalid
        return "CAST(GETUTCDATE() AS DATE)";
    }
}
