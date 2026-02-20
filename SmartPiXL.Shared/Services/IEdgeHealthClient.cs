namespace SmartPiXL.Services;

// ============================================================================
// EDGE HEALTH CLIENT — Abstraction for cross-process communication.
//
// The IIS "PiXL Edge" process owns the pixel capture hot path, the
// Channel<TrackingData> write queue, and the circuit breaker. Backend
// services running in the SmartPiXL Worker process need to query and
// control that state. This interface provides that bridge.
//
// IMPLEMENTATIONS:
//   IIS:    Not used — the IIS process exposes /internal/* HTTP endpoints.
//   Worker: HttpEdgeHealthClient (calls http://127.0.0.1:6000/internal/*)
//
// ENDPOINTS CALLED:
//   GET  /internal/health        → EdgeHealthStatus (circuit, queue, uptime)
//   POST /internal/circuit-reset → bool (true if reset succeeded)
//   POST /internal/geo-cache/clear → void (invalidates geo hot cache after sync)
// ============================================================================

/// <summary>
/// Cross-process abstraction for querying and controlling the IIS Edge process.
/// <para>
/// Used by <c>InfraHealthService</c> (reads circuit/queue state),
/// <c>SelfHealingService</c> (reads circuit + can reset it), and
/// <c>IpApiSyncService</c> (clears geo cache after sync).
/// </para>
/// </summary>
public interface IEdgeHealthClient
{
    /// <summary>
    /// Gets the current health status of the Edge process, including
    /// circuit breaker state, queue depth, and uptime.
    /// Returns a degraded/unknown status if the Edge is unreachable.
    /// </summary>
    Task<EdgeHealthStatus> GetHealthAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Attempts to reset the circuit breaker from Open/HalfOpen back to Closed.
    /// Returns true if the reset was performed, false if already closed or unreachable.
    /// </summary>
    Task<bool> ResetCircuitAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Invalidates the Edge process's in-memory geo hot cache. Called by
    /// <c>IpApiSyncService</c> after completing an IP geolocation data sync
    /// so subsequent hits pick up the fresh data.
    /// </summary>
    Task ClearGeoCacheAsync(CancellationToken ct = default);
}

/// <summary>
/// Health snapshot returned by the Edge process's <c>/internal/health</c> endpoint.
/// </summary>
public sealed record EdgeHealthStatus
{
    /// <summary>Circuit breaker state: "Closed", "Open", or "HalfOpen".</summary>
    public string Circuit { get; init; } = "Unknown";
    
    /// <summary>Reason the circuit tripped (null if Closed).</summary>
    public string? LastTripReason { get; init; }
    
    /// <summary>Current Channel&lt;T&gt; write queue depth.</summary>
    public int QueueDepth { get; init; }
    
    /// <summary>Seconds since the Edge process started.</summary>
    public double UptimeSeconds { get; init; }
    
    /// <summary>True if the Edge process responded successfully.</summary>
    public bool IsReachable { get; init; }
}
