using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// FORGE HEALTH ENDPOINT — Minimal HTTP listener for Sentinel health polling.
// ============================================================================
// Exposes GET /health on loopback (127.0.0.1:7100) returning
// ForgeMetrics.GetHealthReport() as JSON. No ASP.NET Core dependency —
// uses raw HttpListener to keep the Forge as a pure Worker Service.
//
// Single endpoint, single purpose: let Sentinel read Forge probe state.
// ============================================================================

/// <summary>
/// Background service that listens on <c>http://127.0.0.1:{ForgeHealthPort}/</c>
/// and responds to <c>GET /health</c> with the Forge health report JSON.
/// </summary>
internal sealed class ForgeHealthEndpoint : BackgroundService
{
    private readonly ForgeMetrics _metrics;
    private readonly int _port;
    private readonly ITrackingLogger _logger;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public ForgeHealthEndpoint(
        ForgeMetrics metrics,
        IOptions<ForgeSettings> settings,
        ITrackingLogger logger)
    {
        _metrics = metrics;
        _port = settings.Value.ForgeHealthPort;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new HttpListener();
        var prefix = $"http://127.0.0.1:{_port}/";
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
            _logger.Info($"[HealthEndpoint] Listening on {prefix}");
        }
        catch (HttpListenerException ex)
        {
            _logger.Warning($"[HealthEndpoint] Failed to start on {prefix}: {ex.Message}");
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var ctxTask = listener.GetContextAsync();

                // Wait for either a request or cancellation
                var completed = await Task.WhenAny(ctxTask, Task.Delay(Timeout.Infinite, stoppingToken));
                if (completed != ctxTask)
                    break;

                var ctx = await ctxTask;
                _ = HandleRequestAsync(ctx);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (ObjectDisposedException) { /* listener stopped */ }
        finally
        {
            listener.Stop();
            listener.Close();
            _logger.Info("[HealthEndpoint] Stopped");
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod == "GET" &&
                ctx.Request.Url?.AbsolutePath is "/health" or "/health/")
            {
                var report = _metrics.GetHealthReport();
                var json = JsonSerializer.SerializeToUtf8Bytes(report, s_jsonOptions);

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = json.Length;
                await ctx.Response.OutputStream.WriteAsync(json);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
        }
        catch
        {
            ctx.Response.StatusCode = 500;
        }
        finally
        {
            ctx.Response.Close();
        }
    }
}
