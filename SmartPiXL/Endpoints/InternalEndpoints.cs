using System.Diagnostics;
using SmartPiXL.Services;

namespace SmartPiXL.Endpoints;

// ============================================================================
// INTERNAL ENDPOINTS — Localhost-only HTTP bridge for Forge/Sentinel processes.
//
// The SmartPiXL Forge and Sentinel processes run as separate Windows Services.
// They need to query and control state inside the IIS Edge process (pipe
// connectivity, queue depth, geo cache). These endpoints provide that bridge.
//
// ENDPOINTS:
//   GET  /internal/health        → EdgeHealthStatus JSON (circuit, queue, uptime)
//   POST /internal/circuit-reset → { success: bool } — resets circuit breaker
//   POST /internal/geo-cache/clear → 204 — invalidates geo hot cache after sync
//
// SECURITY:
//   RequireLoopback filter (same as DashboardEndpoints) — only 127.0.0.1/::1.
//   These endpoints are NOT exposed externally; IIS only binds the public IP.
// ============================================================================

/// <summary>
/// Internal HTTP endpoints called by the SmartPiXL Worker process to query
/// and control Edge-owned state (circuit breaker, write queue, geo cache).
/// </summary>
public static class InternalEndpoints
{
    private static readonly long StartTicks = Stopwatch.GetTimestamp();

    /// <summary>
    /// Maps the <c>/internal/*</c> endpoints. Called from <c>Program.cs</c>.
    /// </summary>
    public static void MapInternalEndpoints(this WebApplication app)
    {
        // ── Health snapshot ─────────────────────────────────────────
        // Edge no longer owns DatabaseWriterService (moved to Forge).
        // Health now reports PipeClientService connectivity + queue depth.
        app.MapGet("/internal/health", (HttpContext ctx, PipeClientService pipeClient) =>
        {
            if (!IsLoopback(ctx))
            {
                ctx.Response.StatusCode = 404;
                return Results.Empty;
            }

            var elapsed = Stopwatch.GetElapsedTime(StartTicks);
            return Results.Json(new EdgeHealthStatus
            {
                Circuit = pipeClient.IsConnected ? "Closed" : "Open",
                LastTripReason = pipeClient.IsConnected ? null : "Pipe disconnected",
                QueueDepth = pipeClient.QueueDepth,
                UptimeSeconds = elapsed.TotalSeconds,
                IsReachable = true
            });
        });

        // ── Circuit breaker reset ───────────────────────────────────
        // Edge pipe reconnects automatically; reset is a no-op but kept
        // for API compatibility with Forge/Sentinel health probes.
        app.MapPost("/internal/circuit-reset", (HttpContext ctx) =>
        {
            if (!IsLoopback(ctx))
            {
                ctx.Response.StatusCode = 404;
                return Results.Empty;
            }

            return Results.Json(new { success = true });
        });

        // ── Geo cache invalidation ─────────────────────────────────
        app.MapPost("/internal/geo-cache/clear", (HttpContext ctx, GeoCacheService geoCache) =>
        {
            if (!IsLoopback(ctx))
            {
                ctx.Response.StatusCode = 404;
                return Results.Empty;
            }

            geoCache.ClearHotCache();
            return Results.StatusCode(204);
        });
    }

    /// <summary>
    /// Returns true if the request originates from the same machine —
    /// either loopback (127.0.0.1 / ::1) or same-interface (remote == local,
    /// which happens when IIS is bound to a LAN IP, not loopback).
    /// </summary>
    private static bool IsLoopback(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null || System.Net.IPAddress.IsLoopback(remote))
            return true;

        // IIS may bind to a LAN IP (e.g. 192.168.88.176:80). When the Worker
        // calls from the same machine, remote == local but neither is loopback.
        var local = ctx.Connection.LocalIpAddress;
        return local is not null && remote.Equals(local);
    }
}
