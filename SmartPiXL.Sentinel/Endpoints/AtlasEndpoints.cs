using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Sentinel.Services;
using SmartPiXL.Services;

namespace SmartPiXL.Sentinel.Endpoints;

// ============================================================================
// ATLAS DOCUMENTATION PORTAL — Markdown-backed API + live SQL metrics.
//
// ROUTE MAP:
//   /atlas                    →  wwwroot/atlas.html (SPA shell)
//   /api/atlas/sections       →  All sections from markdown files (4 tiers)
//   /api/atlas/section/{slug} →  Single section by slug
//   /api/atlas/categories     →  Sections grouped by category
//   /api/atlas/status         →  System statuses from Docs.SystemStatus (SQL)
//   /api/atlas/metrics        →  Live metrics from Docs.Metric (SQL)
//
// DESIGN:
//   Content comes from docs/atlas/*.md files (version-controlled, Markdig-parsed).
//   Live metrics come from SQL (row counts, watermarks, etc.).
//   FileSystemWatcher invalidates cache on markdown changes.
// ============================================================================

/// <summary>
/// Atlas documentation portal API endpoints — serves markdown-backed 4-tier content
/// with live SQL metrics. Content is read from docs/atlas/*.md and parsed via Markdig.
/// Protected by <see cref="SentinelAccessControl"/> like all other Sentinel endpoints.
/// </summary>
public static class AtlasEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static ITrackingLogger _logger = null!;

    public static void MapAtlasEndpoints(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<TrackingSettings>>().Value;
        _logger = app.Services.GetRequiredService<ITrackingLogger>();
        var atlas = app.Services.GetRequiredService<MarkdownAtlasService>();

        // ====================================================================
        // ATLAS HTML — SPA shell (served as static file)
        // ====================================================================
        app.MapGet("/atlas", async (HttpContext ctx, IWebHostEnvironment env) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            await ServeAtlasHtml(ctx, env);
        });

        // ====================================================================
        // SECTIONS — All markdown-backed content
        // ====================================================================
        app.MapGet("/api/atlas/sections", (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return Task.CompletedTask;
            var sections = atlas.GetSections();
            return WriteJsonAsync(ctx, sections);
        });

        // ====================================================================
        // SINGLE SECTION — By slug
        // ====================================================================
        app.MapGet("/api/atlas/section/{slug}", async (HttpContext ctx, string slug) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            var section = atlas.GetSectionBySlug(slug);
            if (section is null)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("Section not found.");
                return;
            }
            await WriteJsonAsync(ctx, section);
        });

        // ====================================================================
        // CATEGORIES — Sections grouped by category for tabbed navigation
        // ====================================================================
        app.MapGet("/api/atlas/categories", (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return Task.CompletedTask;
            var categories = atlas.GetCategories();
            return WriteJsonAsync(ctx, categories);
        });

        // ====================================================================
        // SYSTEM STATUS — Still SQL-backed (roadmap phases, live verification)
        // ====================================================================
        app.MapGet("/api/atlas/status", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            try
            {
                var statuses = await QueryAsync(settings.ConnectionString, @"
                    SELECT s.SystemId, s.SystemName, s.Phase, s.Status,
                           s.SectionId, s.LastVerified, s.VerifiedBy, s.Notes
                    FROM Docs.SystemStatus s
                    ORDER BY s.Phase, s.SystemName");
                await WriteJsonAsync(ctx, statuses);
            }
            catch (Exception ex)
            {
                _logger.Warning($"[Atlas] Status query failed: {ex.Message}");
                await WriteJsonAsync(ctx, Array.Empty<object>());
            }
        });

        // ====================================================================
        // METRICS — Live SQL queries for hero stats + section metrics
        // ====================================================================
        app.MapGet("/api/atlas/metrics", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            try
            {
                var metrics = await GetMetricsAsync(settings.ConnectionString);
                await WriteJsonAsync(ctx, metrics);
            }
            catch (Exception ex)
            {
                _logger.Warning($"[Atlas] Metrics query failed: {ex.Message}");
                await WriteJsonAsync(ctx, GetStaticFallbackMetrics());
            }
        });

        // ====================================================================
        // LIVE DEMO — Returns the most recent PiXL.Raw hit for the Atlas
        // demo pixel (company 12344, pixel 1) matching the viewer's IP.
        // The query string contains the real PiXL Script output — all 159
        // fields as collected by the live pipeline.
        // ====================================================================
        app.MapGet("/api/atlas/demo", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            try
            {
                var viewerIp = ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "";
                var data = await GetDemoDataAsync(settings.ConnectionString, viewerIp);
                if (data is null)
                {
                    ctx.Response.StatusCode = 204; // No hit yet — script hasn't fired or ETL hasn't run
                    return;
                }
                await WriteJsonAsync(ctx, data);
            }
            catch (Exception ex)
            {
                _logger.Warning($"[Atlas] Demo query failed: {ex.Message}");
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync(ex.Message);
            }
        });

        _logger.Info("[Atlas] Markdown-backed documentation portal mapped: /atlas, /api/atlas/*");
    }

    // ========================================================================
    // STATIC FALLBACK METRICS — Used when SQL is unavailable
    // ========================================================================
    private static List<Dictionary<string, object?>> GetStaticFallbackMetrics()
    {
        return
        [
            new() { ["metricId"] = 1, ["sectionId"] = null, ["label"] = "Browser Signals", ["value"] = "159", ["formatHint"] = "number", ["sortOrder"] = 1 },
            new() { ["metricId"] = 2, ["sectionId"] = null, ["label"] = "Data Points", ["value"] = "230+", ["formatHint"] = "text", ["sortOrder"] = 2 },
            new() { ["metricId"] = 3, ["sectionId"] = null, ["label"] = "Bot Signals", ["value"] = "80+", ["formatHint"] = "text", ["sortOrder"] = 3 },
            new() { ["metricId"] = 4, ["sectionId"] = null, ["label"] = "ETL Cadence", ["value"] = "60s", ["formatHint"] = "text", ["sortOrder"] = 4 },
            new() { ["metricId"] = 5, ["sectionId"] = null, ["label"] = "Enrichment Steps", ["value"] = "15", ["formatHint"] = "number", ["sortOrder"] = 5 },
        ];
    }

    private static async Task<List<Dictionary<string, object?>>> GetMetricsAsync(string connectionString)
    {
        var results = new List<Dictionary<string, object?>>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(@"
            SELECT MetricId, SectionId, Label, StaticValue, QuerySql, FormatHint, SortOrder
            FROM Docs.Metric
            ORDER BY SortOrder, MetricId", conn);
        cmd.CommandTimeout = 15;

        var metrics = new List<(int metricId, int? sectionId, string label, string? staticValue,
            string? querySql, string formatHint, int sortOrder)>();

        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                metrics.Add((
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6)
                ));
            }
        }

        foreach (var m in metrics)
        {
            string? value = m.staticValue;

            if (!string.IsNullOrEmpty(m.querySql))
            {
                try
                {
                    await using var qCmd = new SqlCommand(m.querySql, conn);
                    qCmd.CommandTimeout = 10;
                    var result = await qCmd.ExecuteScalarAsync();
                    value = result?.ToString() ?? "N/A";
                }
                catch (Exception ex)
                {
                    _logger.Warning($"[Atlas] Metric query failed for '{m.label}': {ex.Message}");
                    value = m.staticValue ?? "N/A";
                }
            }

            results.Add(new Dictionary<string, object?>
            {
                ["metricId"] = m.metricId,
                ["sectionId"] = m.sectionId,
                ["label"] = m.label,
                ["value"] = value,
                ["formatHint"] = m.formatHint,
                ["sortOrder"] = m.sortOrder
            });
        }

        return results;
    }

    private static async Task ServeAtlasHtml(HttpContext ctx, IWebHostEnvironment env)
    {
        var path = Path.Combine(env.WebRootPath ?? "wwwroot", "atlas.html");
        if (!File.Exists(path))
            path = Path.Combine(env.ContentRootPath, "wwwroot", "atlas.html");

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
            await ctx.Response.WriteAsync("Atlas documentation portal not found.");
        }
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

    private static async Task WriteJsonAsync(HttpContext ctx, object data)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-cache";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, data, JsonOptions);
    }

    // ========================================================================
    // LIVE DEMO DATA — Query PiXL.Raw for Atlas demo hits (company 12344)
    // ========================================================================

    /// <summary>
    /// Fetches the most recent Raw hit for company 12344 matching the viewer's IP,
    /// parses the query string into a key-value dictionary, and returns the full
    /// field set so the Atlas UI can display real pipeline output.
    /// Falls back to the most recent 12344 hit regardless of IP if no viewer match.
    /// </summary>
    private static async Task<Dictionary<string, object?>?> GetDemoDataAsync(
        string connectionString, string viewerIp)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        // Try viewer's IP first — personalised demo
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 Id, IPAddress, QueryString, UserAgent, ReceivedAt
            FROM PiXL.Raw
            WHERE CompanyID = '12344' AND PiXLID = '00001'
              AND IPAddress = @ViewerIp
            ORDER BY Id DESC";
        cmd.Parameters.AddWithValue("@ViewerIp", viewerIp);
        cmd.CommandTimeout = 10;

        await using var reader1 = await cmd.ExecuteReaderAsync();
        if (await reader1.ReadAsync())
            return ParseDemoRow(reader1);

        await reader1.CloseAsync();

        // Fallback: return the most recent 12344 hit regardless of IP
        await using var fallback = conn.CreateCommand();
        fallback.CommandText = @"
            SELECT TOP 1 Id, IPAddress, QueryString, UserAgent, ReceivedAt
            FROM PiXL.Raw
            WHERE CompanyID = '12344' AND PiXLID = '00001'
            ORDER BY Id DESC";
        fallback.CommandTimeout = 10;

        await using var reader2 = await fallback.ExecuteReaderAsync();
        if (await reader2.ReadAsync())
            return ParseDemoRow(reader2);

        return null;
    }

    /// <summary>
    /// Parses a PiXL.Raw row into the demo response dictionary.
    /// </summary>
    private static Dictionary<string, object?> ParseDemoRow(SqlDataReader reader)
    {
        var rawId = reader.GetInt64(0);
        var ip = reader.IsDBNull(1) ? "" : reader.GetString(1);
        var qs = reader.IsDBNull(2) ? "" : reader.GetString(2);
        var ua = reader.IsDBNull(3) ? "" : reader.GetString(3);
        var receivedAt = reader.IsDBNull(4) ? DateTime.UtcNow : reader.GetDateTime(4);

        // Parse the query string into individual fields
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(qs))
        {
            foreach (var pair in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIdx = pair.IndexOf('=');
                if (eqIdx > 0)
                {
                    var key = pair[..eqIdx];
                    var value = Uri.UnescapeDataString(pair[(eqIdx + 1)..]);
                    fields[key] = value;
                }
            }
        }

        return new Dictionary<string, object?>
        {
            ["rawId"] = rawId,
            ["ip"] = ip,
            ["userAgent"] = ua,
            ["receivedAt"] = receivedAt.ToString("o"),
            ["fieldCount"] = fields.Count,
            ["fields"] = fields
        };
    }
}
