using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TrackingPixel.Scripts;
using TrackingPixel.Services;

namespace TrackingPixel.Endpoints;

/// <summary>
/// Extension methods for registering tracking endpoints.
/// Keeps route definitions separate from Program.cs.
/// </summary>
public static class TrackingEndpoints
{
    // Pre-generated 1x1 transparent GIF (43 bytes)
    private static readonly byte[] TransparentGif = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
    
    // Pre-configured JSON options - avoid allocation per request
    private static readonly JsonSerializerOptions DebugJsonOptions = new() { WriteIndented = true };
    
    // Cached test page path - resolved once at startup
    private static string? _testPagePath;
    
    /// <summary>
    /// Maps all tracking-related endpoints.
    /// </summary>
    public static void MapTrackingEndpoints(this WebApplication app)
    {
        // Get required services
        var captureService = app.Services.GetRequiredService<TrackingCaptureService>();
        var writerService = app.Services.GetRequiredService<DatabaseWriterService>();
        var logger = app.Services.GetRequiredService<ITrackingLogger>();
        
        // Resolve test page path once at startup
        _testPagePath = ResolveTestPagePath();
        
        // ============================================================================
        // DEBUG ENDPOINT - Shows all captured data
        // ============================================================================
        app.MapGet("/debug/headers", (HttpContext ctx) =>
        {
            var data = captureService.CaptureFromRequest(ctx.Request);
            return Results.Json(data, DebugJsonOptions);
        });
        
        // ============================================================================
        // TEST PAGE - Path resolved once at startup
        // ============================================================================
        app.MapGet("/test", async (HttpContext ctx) =>
        {
            if (_testPagePath != null)
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.SendFileAsync(_testPagePath);
            }
            else
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("Test page not found.");
            }
        });
        
        // ============================================================================
        // HEALTH CHECK - Useful for load balancers
        // ============================================================================
        app.MapGet("/health", (HttpContext ctx) =>
        {
            var queueDepth = writerService.QueueDepth;
            return Results.Json(new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                queueDepth,
                queueStatus = queueDepth < 5000 ? "ok" : queueDepth < 9000 ? "warning" : "critical"
            });
        });
        
        // ============================================================================
        // TIER 5: JAVASCRIPT FILE ENDPOINT
        // ============================================================================
        app.MapGet("/js/{companyId}/{pixlId}.js", (HttpContext ctx, string companyId, string pixlId, IConfiguration config) =>
        {
            var baseUrl = config["Tracking:BaseUrl"];
            if (string.IsNullOrEmpty(baseUrl))
            {
                baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            }
            var pixelUrl = $"{baseUrl}/{companyId}/{pixlId}_SMART.GIF";
            var javascript = Tier5Script.Template.Replace("{{PIXEL_URL}}", pixelUrl);
            
            ctx.Response.ContentType = "application/javascript; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            
            return Results.Text(javascript, "application/javascript");
        });
        
        // ============================================================================
        // MAIN PIXEL ENDPOINT - Returns 1x1 GIF
        // ============================================================================
        app.MapGet("/{**path}", (HttpContext ctx) =>
        {
            var trackingData = captureService.CaptureFromRequest(ctx.Request);
            
            if (!writerService.TryQueue(trackingData))
            {
                logger.Warning("Queue full - dropped tracking request");
            }
            
            ctx.Response.ContentType = "image/gif";
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers.Pragma = "no-cache";
            ctx.Response.Headers["Expires"] = "0";
            
            return Results.Bytes(TransparentGif, "image/gif");
        });
        
        // ============================================================================
        // ALTERNATIVE 204 ENDPOINT - No body, slightly faster
        // ============================================================================
        app.MapGet("/pixel204/{**path}", (HttpContext ctx) =>
        {
            var trackingData = captureService.CaptureFromRequest(ctx.Request);
            writerService.TryQueue(trackingData);
            
            return Results.NoContent();
        });
        
        return;
        
        // Local function to resolve path once - static to avoid closure allocation
        static string? ResolveTestPagePath()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "test.html");
            if (File.Exists(path)) return path;
            
            path = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "test.html");
            return File.Exists(path) ? path : null;
        }
    }
}
