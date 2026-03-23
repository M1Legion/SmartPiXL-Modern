using System.Net.Http.Json;
using System.Text.Json;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// HTTP EDGE HEALTH CLIENT — Calls the IIS Edge process's /internal/* endpoints.
//
// Ported from SmartPiXL.Worker-Deprecated/Services/HttpEdgeHealthClient.cs
// with namespace updated to SmartPiXL.Forge.Services.
//
// The IIS "PiXL Edge" process exposes two localhost-only HTTP endpoints:
//   GET  /internal/health        → Per-probe health + metrics (EdgeHealthReport)
//   POST /internal/circuit-reset → Reset circuit breaker to Closed
//
// RESILIENCE:
//   All calls swallow exceptions and return safe defaults. The Edge being
//   down should not crash the Forge — it just means health data is stale
//   and cache clears are skipped (the cache has TTL anyway).
// ============================================================================

/// <summary>
/// <see cref="IEdgeHealthClient"/> implementation that calls the IIS Edge
/// process over localhost HTTP. Configured with <c>Tracking:EdgeBaseUrl</c>.
/// </summary>
public sealed class HttpEdgeHealthClient : IEdgeHealthClient
{
    private readonly HttpClient _http;
    private readonly ITrackingLogger _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public HttpEdgeHealthClient(HttpClient httpClient, ITrackingLogger logger)
    {
        _http = httpClient;
        _logger = logger;
    }

    public async Task<EdgeHealthReport> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/internal/health", ct);
            if (response.IsSuccessStatusCode)
            {
                var report = await response.Content.ReadFromJsonAsync<EdgeHealthReport>(JsonOpts, ct);
                return report ?? new EdgeHealthReport { IsReachable = false };
            }

            _logger.Warning($"Edge health returned {(int)response.StatusCode}");
            return new EdgeHealthReport { IsReachable = false };
        }
        catch (Exception ex)
        {
            _logger.Debug($"Edge health unreachable: {ex.Message}");
            return new EdgeHealthReport { IsReachable = false };
        }
    }

    public async Task<bool> ResetCircuitAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync("/internal/circuit-reset", null, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.Info("Edge circuit breaker reset via HTTP");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Edge circuit reset failed: {ex.Message}");
            return false;
        }
    }

}
