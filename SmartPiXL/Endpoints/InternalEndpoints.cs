using SmartPiXL.Services;

namespace SmartPiXL.Endpoints;

// ============================================================================
// INTERNAL ENDPOINTS — Localhost-only HTTP bridge for Forge/Sentinel processes.
//
// The SmartPiXL Forge and Sentinel processes run as separate Windows Services.
// They need to query and control state inside the IIS Edge process (pipe
// connectivity, queue depth). These endpoints provide that bridge.
//
// ENDPOINTS:
//   GET  /internal/health        → EdgeHealthReport JSON (per-probe health + metrics)
//   POST /internal/circuit-reset → { success: bool } — resets circuit breaker
//
// SECURITY:
//   RequireLoopback filter (same as DashboardEndpoints) — only 127.0.0.1/::1.
//   These endpoints are NOT exposed externally; IIS only binds the public IP.
// ============================================================================

/// <summary>
/// Internal HTTP endpoints called by the SmartPiXL Worker process to query
/// and control Edge-owned state (health probes, pipe queue depth).
/// </summary>
public static class InternalEndpoints
{
    /// <summary>
    /// Maps the <c>/internal/*</c> endpoints. Called from <c>Program.cs</c>.
    /// </summary>
    public static void MapInternalEndpoints(this WebApplication app)
    {
        // ── Health tree report ──────────────────────────────────────
        // Returns per-probe health (1/0) + metrics for all 4 Edge probes,
        // plus aggregated Edge health ratio. Used by Forge, Sentinel, and
        // external monitoring.
        app.MapGet("/internal/health", (HttpContext ctx, EdgeMetrics metrics) =>
        {
            if (!IsLoopback(ctx))
            {
                ctx.Response.StatusCode = 404;
                return Results.Empty;
            }

            return Results.Json(metrics.GetHealthReport());
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
