using System.Net.Http.Json;
using System.Text.Json;
using SmartPiXL.Services;

namespace SmartPiXL.Sentinel.Services;

// ============================================================================
// HTTP EDGE HEALTH CLIENT — Calls the IIS Edge process's /internal/* endpoints.
//
// The IIS "PiXL Edge" process exposes three localhost-only HTTP endpoints:
//   GET  /internal/health        → Circuit state, queue depth, uptime
//   POST /internal/circuit-reset → Reset circuit breaker to Closed
//   POST /internal/geo-cache/clear → Invalidate geo hot cache after sync
//
// RESILIENCE:
//   All calls swallow exceptions and return safe defaults. The Edge being
//   down should not crash the Sentinel — it just means health data is stale
//   and cache clears are skipped (the cache has TTL anyway).
//
// PORTED FROM: SmartPiXL.Worker-Deprecated/Services/HttpEdgeHealthClient.cs
// NAMESPACE:   SmartPiXL.Sentinel.Services (not SmartPiXL.Worker.Services)
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

    public async Task<EdgeHealthStatus> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/internal/health", ct);
            if (response.IsSuccessStatusCode)
            {
                var status = await response.Content.ReadFromJsonAsync<EdgeHealthStatus>(JsonOpts, ct);
                return status ?? new EdgeHealthStatus { IsReachable = false };
            }

            _logger.Warning($"Edge health returned {(int)response.StatusCode}");
            return new EdgeHealthStatus { IsReachable = false };
        }
        catch (Exception ex)
        {
            _logger.Debug($"Edge health unreachable: {ex.Message}");
            return new EdgeHealthStatus { IsReachable = false };
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

    public async Task ClearGeoCacheAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync("/internal/geo-cache/clear", null, ct);
            if (response.IsSuccessStatusCode)
                _logger.Debug("Edge geo cache cleared via HTTP");
            else
                _logger.Warning($"Edge geo cache clear returned {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            _logger.Debug($"Edge geo cache clear failed (non-critical): {ex.Message}");
        }
    }
}
