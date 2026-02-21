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
/// Unlike <see cref="DashboardEndpoints"/> (localhost-only), Atlas endpoints are
/// accessible from any IP.
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
        app.MapGet("/atlas", ServeAtlasHtml);

        // ====================================================================
        // SECTIONS — All markdown-backed content
        // ====================================================================
        app.MapGet("/api/atlas/sections", (HttpContext ctx) =>
        {
            var sections = atlas.GetSections();
            return WriteJsonAsync(ctx, sections);
        });

        // ====================================================================
        // SINGLE SECTION — By slug
        // ====================================================================
        app.MapGet("/api/atlas/section/{slug}", async (HttpContext ctx, string slug) =>
        {
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
            var categories = atlas.GetCategories();
            return WriteJsonAsync(ctx, categories);
        });

        // ====================================================================
        // SYSTEM STATUS — Still SQL-backed (roadmap phases, live verification)
        // ====================================================================
        app.MapGet("/api/atlas/status", async (HttpContext ctx) =>
        {
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
}
