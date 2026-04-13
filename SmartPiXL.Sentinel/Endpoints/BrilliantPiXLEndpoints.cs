using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Sentinel.Endpoints;

// ============================================================================
// BRILLIANTPIXL METRICS — Live analytics for modern JS PiXL script hits.
//
// ARCHITECTURE:
//   /brilliantpixl          →  wwwroot/brilliantpixl.html  (SPA)
//   /api/brilliantpixl/*    →  Inline SQL against PiXL.Parsed WHERE HitType='modern'
//
// PURPOSE:
//   Management-facing dashboard showing real-time metrics for the modern JS
//   fingerprinting script: human vs bot traffic, channel attribution, signal
//   coverage, device profiles, geographic distribution, and lead quality.
//
// DESIGN TREE:
//   Health.Node → Sentinel → S5: BrilliantPiXL Metrics (NodeId 108)
//     ├─ BrilliantPiXL SPA  (probe, NodeId 109)
//     └─ JS Metrics API     (probe, NodeId 110)
// ============================================================================

public static class BrilliantPiXLEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static ITrackingLogger _logger = null!;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, (object? Data, DateTime Expiry)> _cache = new();

    // Limit concurrent SQL queries to prevent I/O saturation on large tables.
    private static readonly SemaphoreSlim _sqlGate = new(3, 3);

    private static async Task<bool> TryServeCachedAsync(HttpContext ctx, string cacheKey)
    {
        if (_cache.TryGetValue(cacheKey, out var entry) && DateTime.UtcNow < entry.Expiry)
        {
            if (entry.Data is string raw)
                await WriteRawJsonAsync(ctx, raw);
            else
                await WriteJsonAsync(ctx, entry.Data);
            return true;
        }
        return false;
    }

    private static void CacheStore(string cacheKey, object? data)
    {
        _cache[cacheKey] = (data, DateTime.UtcNow + CacheDuration);
    }

    public static void MapBrilliantPiXLEndpoints(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<TrackingSettings>>().Value;
        _logger = app.Services.GetRequiredService<ITrackingLogger>();
        var cs = settings.ConnectionString;

        // ================================================================
        // SPA HTML
        // ================================================================
        app.MapGet("/brilliantpixl", ServeBrilliantPiXLHtml);

        // ================================================================
        // API: Summary headline numbers
        // ================================================================
        app.MapGet("/api/brilliantpixl/summary", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "bp:summary")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var json = await ReadSnapshotAsync(cs, "summary", ctx.RequestAborted);
                if (json is null) { ctx.Response.StatusCode = 503; return; }
                CacheStore("bp:summary", json);
                await WriteRawJsonAsync(ctx, json);
            });
        });

        // ================================================================
        // API: Daily volume trend
        // ================================================================
        app.MapGet("/api/brilliantpixl/daily", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "bp:daily")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var json = await ReadSnapshotAsync(cs, "daily", ctx.RequestAborted);
                if (json is null) { ctx.Response.StatusCode = 503; return; }
                CacheStore("bp:daily", json);
                await WriteRawJsonAsync(ctx, json);
            });
        });

        // ================================================================
        // API: Bot classification breakdown
        // ================================================================
        app.MapGet("/api/brilliantpixl/bots", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "bp:bots")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var json = await ReadSnapshotAsync(cs, "bots", ctx.RequestAborted);
                if (json is null) { ctx.Response.StatusCode = 503; return; }
                CacheStore("bp:bots", json);
                await WriteRawJsonAsync(ctx, json);
            });
        });

        // ================================================================
        // API: Known bot names
        // ================================================================
        app.MapGet("/api/brilliantpixl/bot-names", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "bp:bot-names")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var json = await ReadSnapshotAsync(cs, "bot-names", ctx.RequestAborted);
                if (json is null) { ctx.Response.StatusCode = 503; return; }
                CacheStore("bp:bot-names", json);
                await WriteRawJsonAsync(ctx, json);
            });
        });

        // ================================================================
        // API: Traffic channels (human only)
        // ================================================================
        app.MapGet("/api/brilliantpixl/channels", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "bp:channels")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var json = await ReadSnapshotAsync(cs, "channels", ctx.RequestAborted);
                if (json is null) { ctx.Response.StatusCode = 503; return; }
                CacheStore("bp:channels", json);
                await WriteRawJsonAsync(ctx, json);
            });
        });

        // ================================================================
        // API: Top landing pages (human only)
        // ================================================================
        app.MapGet("/api/brilliantpixl/pages", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "bp:pages")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var json = await ReadSnapshotAsync(cs, "pages", ctx.RequestAborted);
                if (json is null) { ctx.Response.StatusCode = 503; return; }
                CacheStore("bp:pages", json);
                await WriteRawJsonAsync(ctx, json);
            });
        });

        // ================================================================
        // API: Fingerprint signal coverage (human only)
        // ================================================================
        app.MapGet("/api/brilliantpixl/signals", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "bp:signals")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var json = await ReadSnapshotAsync(cs, "signals", ctx.RequestAborted);
                if (json is null) { ctx.Response.StatusCode = 503; return; }
                CacheStore("bp:signals", json);
                await WriteRawJsonAsync(ctx, json);
            });
        });

        // ================================================================
        // API: Browser / OS / Device breakdown (all JS hits)
        // ================================================================
        app.MapGet("/api/brilliantpixl/devices", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "bp:devices")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var json = await ReadSnapshotAsync(cs, "devices", ctx.RequestAborted);
                if (json is null) { ctx.Response.StatusCode = 503; return; }
                CacheStore("bp:devices", json);
                await WriteRawJsonAsync(ctx, json);
            });
        });

        // ================================================================
        // API: Geographic distribution (human only)
        // ================================================================
        app.MapGet("/api/brilliantpixl/geo", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "bp:geo")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var json = await ReadSnapshotAsync(cs, "geo", ctx.RequestAborted);
                if (json is null) { ctx.Response.StatusCode = 503; return; }
                CacheStore("bp:geo", json);
                await WriteRawJsonAsync(ctx, json);
            });
        });

        // ================================================================
        // API: Evasion detection summary
        // ================================================================
        app.MapGet("/api/brilliantpixl/evasion", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "bp:evasion")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var json = await ReadSnapshotAsync(cs, "evasion", ctx.RequestAborted);
                if (json is null) { ctx.Response.StatusCode = 503; return; }
                CacheStore("bp:evasion", json);
                await WriteRawJsonAsync(ctx, json);
            });
        });

        // ================================================================
        // API: Top screen resolutions (human only)
        // ================================================================
        app.MapGet("/api/brilliantpixl/screens", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "bp:screens")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var json = await ReadSnapshotAsync(cs, "screens", ctx.RequestAborted);
                if (json is null) { ctx.Response.StatusCode = 503; return; }
                CacheStore("bp:screens", json);
                await WriteRawJsonAsync(ctx, json);
            });
        });

        // ================================================================
        // API: Lead quality distribution (human only)
        // ================================================================
        app.MapGet("/api/brilliantpixl/leads", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "bp:leads")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var json = await ReadSnapshotAsync(cs, "leads", ctx.RequestAborted);
                if (json is null) { ctx.Response.StatusCode = 503; return; }
                CacheStore("bp:leads", json);
                await WriteRawJsonAsync(ctx, json);
            });
        });

        // ================================================================
        // API: OS version breakdown (human only)
        // ================================================================
        app.MapGet("/api/brilliantpixl/os-versions", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            if (await TryServeCachedAsync(ctx, "bp:os-versions")) return;
            await SafeExecuteAsync(ctx, async () =>
            {
                var json = await ReadSnapshotAsync(cs, "os-versions", ctx.RequestAborted);
                if (json is null) { ctx.Response.StatusCode = 503; return; }
                CacheStore("bp:os-versions", json);
                await WriteRawJsonAsync(ctx, json);
            });
        });

        _logger.Info("[BrilliantPiXL] Endpoints mapped: /brilliantpixl, /api/brilliantpixl/*");
    }

    // ====================================================================
    // HTML SERVING
    // ====================================================================

    private static async Task ServeBrilliantPiXLHtml(HttpContext ctx, IWebHostEnvironment env)
    {
        if (!SentinelAccessControl.IsAllowed(ctx)) return;
        var path = Path.Combine(env.WebRootPath ?? "wwwroot", "brilliantpixl.html");
        if (!File.Exists(path))
            path = Path.Combine(env.ContentRootPath, "wwwroot", "brilliantpixl.html");

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
            await ctx.Response.WriteAsync("BrilliantPiXL Metrics dashboard not found.");
        }
    }

    // ====================================================================
    // SQL + JSON HELPERS (same pattern as DashboardEndpoints)
    // ====================================================================

    private static async Task<List<Dictionary<string, object?>>> QueryAsync(
        string connectionString, string sql, SqlParameter? param = null)
    {
        var results = new List<Dictionary<string, object?>>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = 300;
        if (param is not null) cmd.Parameters.Add(param);
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

    private static async Task<Dictionary<string, object?>> QuerySingleRowAsync(
        string connectionString, string sql)
    {
        var results = await QueryAsync(connectionString, sql);
        return results.FirstOrDefault() ?? new Dictionary<string, object?>();
    }

    private static async Task SafeExecuteAsync(HttpContext ctx, Func<Task> action)
    {
        await _sqlGate.WaitAsync();
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error($"[BrilliantPiXL] Request failed ({ctx.Request.Path}): {ex.Message}");
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 503;
                await WriteJsonAsync(ctx, new { error = "Request failed", detail = ex.Message });
            }
        }
        finally
        {
            _sqlGate.Release();
        }
    }

    private static async Task WriteJsonAsync(HttpContext ctx, object? data)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-cache";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, data, JsonOptions);
    }

    private static async Task WriteRawJsonAsync(HttpContext ctx, string json)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.WriteAsync(json);
    }

    private static async Task<string?> ReadSnapshotAsync(
        string connectionString, string endpointName, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT JsonPayload FROM Dashboard.BrilliantPiXL WHERE EndpointName = @n", conn);
        cmd.Parameters.AddWithValue("@n", endpointName);
        cmd.CommandTimeout = 10;
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }
}
