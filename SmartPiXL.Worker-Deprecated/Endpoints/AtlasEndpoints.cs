using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TrackingPixel.Configuration;
using TrackingPixel.Services;

namespace TrackingPixel.Endpoints;

// ============================================================================
// ATLAS DOCUMENTATION PORTAL — Public-facing API for the 4-tier docs portal.
//
// ROUTE MAP:
//   /atlas                  →  wwwroot/atlas.html (documentation portal SPA)
//   /api/atlas/sections     →  Docs.Section (full content tree, all 4 tiers)
//   /api/atlas/section/{slug} → Single Docs.Section by slug
//   /api/atlas/status       →  Docs.SystemStatus (phase + status for roadmap)
//   /api/atlas/metrics      →  Docs.Metric (hero stats, some with live SQL queries)
//
// DESIGN:
//   - NOT localhost-restricted — Atlas is for external audiences (investors, clients)
//   - Read-only: all data comes from the Docs schema, populated by agents
//   - Live metrics execute QuerySql dynamically (single scalar SELECTs)
//   - Content is HTML (rendered by the browser); Mermaid diagrams render client-side
//
// SQL TABLE MAPPING:
//   /api/atlas/sections   →  Docs.Section       (SectionId, Slug, Title, 4×Html, Mermaid)
//   /api/atlas/status     →  Docs.SystemStatus   (SystemName, Phase, Status, SectionId)
//   /api/atlas/metrics    →  Docs.Metric         (Label, StaticValue or QuerySql result)
// ============================================================================

/// <summary>
/// Atlas documentation portal API endpoints — serves SQL-backed 4-tier content
/// for the professional documentation portal at <c>/atlas</c>.
/// <para>
/// Unlike <see cref="DashboardEndpoints"/> (localhost-only), Atlas endpoints are
/// accessible from any IP. They serve documentation content, not operational data.
/// </para>
/// </summary>
public static class AtlasEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static ITrackingLogger _logger = null!;

    /// <summary>
    /// Maps all Atlas documentation endpoints. Called once at startup from <c>Program.cs</c>.
    /// </summary>
    public static void MapAtlasEndpoints(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<TrackingSettings>>().Value;
        _logger = app.Services.GetRequiredService<ITrackingLogger>();

        // ====================================================================
        // ATLAS HTML — Professional documentation portal
        // ====================================================================
        app.MapGet("/atlas", ServeAtlasHtml);

        // ====================================================================
        // SECTIONS — Full content tree with all 4 audience tiers
        // ====================================================================
        app.MapGet("/api/atlas/sections", async (HttpContext ctx) =>
        {
            var sections = await QueryAsync(settings.ConnectionString, @"
                SELECT SectionId, ParentSectionId, Slug, Title, IconClass, SortOrder,
                       PitchHtml, ManagementHtml, TechnicalHtml, WalkthroughHtml,
                       MermaidDiagram, LastUpdated, UpdatedBy
                FROM Docs.Section
                ORDER BY SortOrder, SectionId");
            await WriteJsonAsync(ctx, sections);
        });

        // ====================================================================
        // SINGLE SECTION — By slug (e.g., /api/atlas/section/etl-processing)
        // ====================================================================
        app.MapGet("/api/atlas/section/{slug}", async (HttpContext ctx, string slug) =>
        {
            var sections = await QueryAsync(settings.ConnectionString, @"
                SELECT SectionId, ParentSectionId, Slug, Title, IconClass, SortOrder,
                       PitchHtml, ManagementHtml, TechnicalHtml, WalkthroughHtml,
                       MermaidDiagram, LastUpdated, UpdatedBy
                FROM Docs.Section
                WHERE Slug = @Slug",
                new SqlParameter("@Slug", slug));

            if (sections.Count == 0)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("Section not found.");
                return;
            }
            await WriteJsonAsync(ctx, sections[0]);
        });

        // ====================================================================
        // SYSTEM STATUS — Roadmap phases and feature statuses
        // ====================================================================
        app.MapGet("/api/atlas/status", async (HttpContext ctx) =>
        {
            var statuses = await QueryAsync(settings.ConnectionString, @"
                SELECT s.SystemId, s.SystemName, s.Phase, s.Status,
                       s.SectionId, sec.Slug AS SectionSlug,
                       s.LastVerified, s.VerifiedBy, s.Notes
                FROM Docs.SystemStatus s
                LEFT JOIN Docs.Section sec ON sec.SectionId = s.SectionId
                ORDER BY s.Phase, s.SystemName");
            await WriteJsonAsync(ctx, statuses);
        });

        // ====================================================================
        // METRICS — Hero stats + section-specific metrics
        // Live metrics execute QuerySql and return the scalar result.
        // Static metrics return StaticValue directly.
        // ====================================================================
        app.MapGet("/api/atlas/metrics", async (HttpContext ctx) =>
        {
            var metrics = await GetMetricsAsync(settings.ConnectionString);
            await WriteJsonAsync(ctx, metrics);
        });

        _logger.Info("[Atlas] Documentation portal endpoints mapped: /atlas, /api/atlas/*");
    }

    /// <summary>
    /// Fetches all metrics from Docs.Metric. For rows with QuerySql, executes the
    /// query and returns the live result. For rows with StaticValue, returns as-is.
    /// Errors in QuerySql execution are swallowed — the metric returns "N/A" instead.
    /// </summary>
    private static async Task<List<Dictionary<string, object?>>> GetMetricsAsync(string connectionString)
    {
        var results = new List<Dictionary<string, object?>>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        // First, fetch all metric definitions
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

        // Now resolve each metric's value
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
                    value = "N/A";
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

    /// <summary>
    /// Serves <c>wwwroot/atlas.html</c>. Unlike Tron, Atlas is NOT localhost-only.
    /// </summary>
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

    // ========================================================================
    // ADO.NET helpers — identical pattern to DashboardEndpoints
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

    private static async Task WriteJsonAsync(HttpContext ctx, object data)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-cache";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, data, JsonOptions);
    }
}
