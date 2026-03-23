namespace SmartPiXL.Services;

// ============================================================================
// EDGE HEALTH CLIENT — Abstraction for cross-process communication.
//
// The IIS "PiXL Edge" process owns the pixel capture hot path, the
// Channel<TrackingData> write queue, and the pipe connection. Backend
// services running in the Forge/Sentinel processes need to query that state.
// This interface provides the bridge.
//
// IMPLEMENTATIONS:
//   IIS:    Not used — the IIS process exposes /internal/* HTTP endpoints.
//   Worker: HttpEdgeHealthClient (calls http://127.0.0.1:6000/internal/*)
//
// ENDPOINTS CALLED:
//   GET  /internal/health        → EdgeHealthReport (per-probe health + metrics)
//   POST /internal/circuit-reset → bool (true if reset succeeded)
// ============================================================================

/// <summary>
/// Cross-process abstraction for querying and controlling the IIS Edge process.
/// <para>
/// Used by <c>InfraHealthService</c> (reads health probes) and
/// <c>SelfHealingService</c> (reads health + can reset circuit).
/// </para>
/// </summary>
public interface IEdgeHealthClient
{
    /// <summary>
    /// Gets the full Edge health report, including per-probe health (1/0),
    /// per-probe metrics, and overall Edge health ratio.
    /// Returns a degraded report if the Edge is unreachable.
    /// </summary>
    Task<EdgeHealthReport> GetHealthAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Attempts to reset the circuit breaker from Open/HalfOpen back to Closed.
    /// Returns true if the reset was performed, false if already closed or unreachable.
    /// </summary>
    Task<bool> ResetCircuitAsync(CancellationToken ct = default);
}

/// <summary>Per-probe health + metrics for a single health probe.</summary>
public sealed record ProbeReport
{
    public required string Name { get; init; }
    public required int Health { get; init; }
    public object? Metrics { get; init; }

    public ProbeReport() { }

    [System.Text.Json.Serialization.JsonConstructor]
    public ProbeReport(string name, int health, object? metrics)
    {
        Name = name;
        Health = health;
        Metrics = metrics;
    }
}

/// <summary>
/// Full Edge health report: system-level ratio + per-probe detail.
/// Returned by <c>GET /internal/health</c>.
/// </summary>
public sealed class EdgeHealthReport
{
    public string System { get; init; } = "Edge";
    public int Healthy { get; init; }
    public int Total { get; init; }
    public double Ratio { get; init; }
    public double UptimeSeconds { get; init; }
    public ProbeReport[] Probes { get; init; } = [];

    /// <summary>True if the Edge process responded successfully.</summary>
    public bool IsReachable { get; init; }

    // ── Convenience properties for backward-compat consumers ───────
    // These extract values that InfraHealthService / SelfHealingService
    // previously read from the old EdgeHealthStatus type.

    /// <summary>Pipe channel queue depth (from Pipe Client probe metrics).</summary>
    public int QueueDepth { get; init; }

    /// <summary>
    /// Circuit breaker state. Always "Closed" — the old circuit breaker was
    /// removed; Edge pipes reconnect automatically. Kept so SelfHealingService
    /// compiles and correctly skips the check.
    /// </summary>
    public string Circuit { get; init; } = "Closed";

    /// <summary>Always null — circuit breaker no longer exists.</summary>
    public string? LastTripReason { get; init; }
}
