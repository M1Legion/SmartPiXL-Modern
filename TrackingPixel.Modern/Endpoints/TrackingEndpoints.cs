using System.Text.Json;
using System.Text.RegularExpressions;
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
    
    // Route parameter validation: alphanumeric, hyphens, underscores only (max 64 chars)
    // Prevents JS injection via companyId/pixlId and bounds the script cache
    private static readonly Regex SafeRouteParam = new(@"^[a-zA-Z0-9\-_]{1,64}$", RegexOptions.Compiled);
    
    // Cached static file paths - resolved once at startup
    private static string? _wwwrootPath;
    
    /// <summary>
    /// Maps all tracking-related endpoints.
    /// </summary>
    public static void MapTrackingEndpoints(this WebApplication app)
    {
        // Get required services
        var captureService = app.Services.GetRequiredService<TrackingCaptureService>();
        var writerService = app.Services.GetRequiredService<DatabaseWriterService>();
        var logger = app.Services.GetRequiredService<ITrackingLogger>();
        
        // Resolve wwwroot path once at startup
        _wwwrootPath = ResolveWwwrootPath();
        
        // ============================================================================
        // LANDING PAGE - Simple logo page at root
        // ============================================================================
        app.MapGet("/", async (HttpContext ctx) =>
        {
            var indexPath = _wwwrootPath != null ? Path.Combine(_wwwrootPath, "index.html") : null;
            if (indexPath != null && File.Exists(indexPath))
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.SendFileAsync(indexPath);
            }
            else
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync("<html><body><h1>SmartPiXL</h1></body></html>");
            }
        });
        
        // ============================================================================
        // DEMO PAGE - Shows data collection in action
        // ============================================================================
        app.MapGet("/demo", async (HttpContext ctx) =>
        {
            var demoPath = _wwwrootPath != null ? Path.Combine(_wwwrootPath, "demo.html") : null;
            if (demoPath != null && File.Exists(demoPath))
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.SendFileAsync(demoPath);
            }
            else
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("Demo page not found.");
            }
        });
        
        // ============================================================================
        // STATIC IMAGES - Serve logo and other assets
        // ============================================================================
        app.MapGet("/images/{fileName}", async (HttpContext ctx, string fileName) =>
        {
            // Sanitize filename to prevent path traversal
            var sanitized = Path.GetFileName(fileName);
            if (string.IsNullOrEmpty(sanitized) || sanitized != fileName)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            
            var imagePath = _wwwrootPath != null ? Path.Combine(_wwwrootPath, "images", sanitized) : null;
            if (imagePath != null && File.Exists(imagePath))
            {
                var ext = Path.GetExtension(fileName).ToLowerInvariant();
                ctx.Response.ContentType = ext switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".svg" => "image/svg+xml",
                    ".ico" => "image/x-icon",
                    ".webp" => "image/webp",
                    _ => "application/octet-stream"
                };
                ctx.Response.Headers.CacheControl = "public, max-age=86400"; // Cache for 1 day
                await ctx.Response.SendFileAsync(imagePath);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
        });
        
        // ============================================================================
        // INTERNAL TEST PAGE - Removed from public access
        // This was at /test but is now only accessible locally via file
        // ============================================================================
        
        // ============================================================================
        // DEBUG ENDPOINT - Restricted to loopback in production
        // ============================================================================
        app.MapGet("/debug/headers", (HttpContext ctx) =>
        {
            var remoteIp = ctx.Connection.RemoteIpAddress;
            if (remoteIp != null && !System.Net.IPAddress.IsLoopback(remoteIp))
            {
                return Results.StatusCode(403);
            }
            var data = captureService.CaptureFromRequest(ctx.Request);
            return Results.Json(data, DebugJsonOptions);
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
            // Validate route params — prevents JS injection and unbounded cache growth
            if (!SafeRouteParam.IsMatch(companyId) || !SafeRouteParam.IsMatch(pixlId))
            {
                ctx.Response.StatusCode = 400;
                return Results.Text("// invalid parameters", "application/javascript");
            }
            
            var baseUrl = config["Tracking:BaseUrl"];
            if (string.IsNullOrEmpty(baseUrl))
            {
                baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            }
            var pixelUrl = $"{baseUrl}/{companyId}/{pixlId}_SMART.GIF";
            var javascript = Tier5Script.GetScript(pixelUrl);
            
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            
            return Results.Text(javascript, "application/javascript");
        });
        
        // ============================================================================
        // MAIN PIXEL ENDPOINT - Returns 1x1 GIF
        // TIER 5 ONLY: Only records requests with actual tracking data (querystring)
        // Ignores favicon.ico, robots.txt, and other browser chrome requests
        // ============================================================================
        app.MapGet("/{**path}", (HttpContext ctx) =>
        {
            var path = ctx.Request.Path.ToString();
            var queryString = ctx.Request.QueryString.ToString();
            
            // Only record if:
            // 1. Path ends with _SMART.GIF (tracking pixel pattern)
            // 2. Has a querystring with actual data (from JS)
            var isTrackingPixel = path.EndsWith("_SMART.GIF", StringComparison.OrdinalIgnoreCase);
            var hasTrackingData = queryString.Length > 10; // More than just "?"
            
            if (isTrackingPixel && hasTrackingData)
            {
                var trackingData = captureService.CaptureFromRequest(ctx.Request);
                
                if (!writerService.TryQueue(trackingData))
                {
                    logger.Warning("Queue full - dropped tracking request");
                }
            }
            // Else: silently return the GIF without recording (favicon, robots, etc.)
            
            ctx.Response.ContentType = "image/gif";
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers.Pragma = "no-cache";
            ctx.Response.Headers["Expires"] = "0";
            
            return Results.Bytes(TransparentGif, "image/gif");
        });
        
        // ============================================================================
        // ALTERNATIVE 204 ENDPOINT - No body, slightly faster (TIER 5 ONLY)
        // ============================================================================
        app.MapGet("/pixel204/{**path}", (HttpContext ctx) =>
        {
            var path = ctx.Request.Path.ToString();
            var queryString = ctx.Request.QueryString.ToString();
            
            // Only record Tier 5 tracking data
            var isTrackingPixel = path.Contains("_SMART.GIF", StringComparison.OrdinalIgnoreCase);
            var hasTrackingData = queryString.Length > 10;
            
            if (isTrackingPixel && hasTrackingData)
            {
                var trackingData = captureService.CaptureFromRequest(ctx.Request);
                writerService.TryQueue(trackingData);
            }
            
            return Results.NoContent();
        });
        
        return;
        
        // Local function to resolve wwwroot path once
        static string? ResolveWwwrootPath()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (Directory.Exists(path)) return path;
            
            path = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            return Directory.Exists(path) ? path : null;
        }
    }
}
