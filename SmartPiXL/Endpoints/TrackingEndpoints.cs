using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartPiXL.Scripts;
using SmartPiXL.Services;

namespace SmartPiXL.Endpoints;

// ============================================================================
// TRACKING ENDPOINTS — The core PiXL-serving + enrichment pipeline.
//
// ROUTE MAP:
//   /                                                      →  Landing page (wwwroot/index.html)
//   /demo                                                  →  Demo page (wwwroot/demo.html)
//   /debug/headers                                         →  Diagnostic JSON dump (localhost only)
//   /health                                                →  Health check for load balancers
//   /{companyId}/{pixlId}_{domain}_SMART.js  (catch-all)   →  Modern PiXL script endpoint
//   /js/{clientId}/{pixlId}.js              (catch-all)   →  Legacy script endpoint (serves modern script)
//   /{companyId}/{pixlId}_{domain}_SMART.DATA (POST)       →  Beacon data endpoint (sendBeacon)
//   /{companyId}/{pixlId}_{domain}_SMART.GIF (catch-all)   →  Legacy/fallback tracking GIF endpoint
//   /{**path} (catch-all fallback)                         →  Bot trap — returns GIF, flags record
//
// URL DESIGN (owner-specified):
//   Legacy:  GET /{companyId}/{pixlId}_{pixldomain}.com_SMART.GIF
//   Modern:  GET /{companyId}/{pixlId}_{pixldomain}.com_SMART.js
//   Example: GET /12800/00029_thetriviaquest.com_SMART.GIF
//
// CAPTURE PIPELINE (CaptureAndEnqueue):
//   1. TrackingCaptureService parses HTTP request into TrackingData
//   2. Hit type (_srv_hitType=modern|legacy) and bot trap flag appended
//   3. TrackingData sent to Forge via named pipe (or JSONL failover)
//   4. ALL enrichment happens in the Forge — Edge is capture-only
// ============================================================================

/// <summary>
/// Extension methods for registering all tracking-related HTTP endpoints.
/// <para>
/// Keeps route definitions separate from <c>Program.cs</c> for readability.
/// The class is <c>partial</c> to support the source-generated regex in
/// <see cref="SafeRouteParam"/>. All state is static/thread-static —
/// the class itself is never instantiated.
/// </para>
/// </summary>
public static partial class TrackingEndpoints
{
    // Pre-generated 1x1 transparent GIF (43 bytes) — pinned static, zero-copy writes
    private static readonly byte[] TransparentGif = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
    
    // Pre-configured JSON options - avoid allocation per request
    private static readonly JsonSerializerOptions DebugJsonOptions = new() { WriteIndented = true };
    
    // Source-generated regex — AOT-friendly, zero-alloc matching
    // Validates: alphanumeric, hyphens, underscores only (max 64 chars)
    [GeneratedRegex(@"^[a-zA-Z0-9\-_]{1,64}$")]
    private static partial Regex SafeRouteParam();
    
    // Full PiXL URL pattern: /{companyId}/{pixlId}_{domain}_SMART.(GIF|js|DATA)
    // Example: /12800/00029_thetriviaquest.com_SMART.GIF
    // Groups: companyId, pixlId, domain (domain includes TLD)
    [GeneratedRegex(@"^/?(?<companyId>[^/]+)/(?<pixlId>[^_]+)_(?<domain>.+)_SMART\.(GIF|js|DATA)$", RegexOptions.IgnoreCase)]
    private static partial Regex PiXLUrlPattern();
    
    // Legacy ClearDot URL pattern: /{companyId}/{clientName}_{zipCode}_ClearDot.gif
    // Example: /epush/villagetoyota_34448_ClearDot.gif
    // This is the original pixel format from the first-generation system.
    // CompanyID and PiXLID are extracted by TrackingCaptureService.PathParseRegex.
    [GeneratedRegex(@"_ClearDot\.gif$", RegexOptions.IgnoreCase)]
    private static partial Regex ClearDotPattern();
    
    // Content type constants — avoid per-request string allocs
    private const string GifContentType = "image/gif";
    private const string JsContentType = "application/javascript";
    private const string HtmlContentType = "text/html; charset=utf-8";
    
    // CORS: sendBeacon from a cross-origin page needs the server to allow it.
    // This is the permissive origin used for beacon POST responses.
    private const string CorsAllowAll = "*";
    
    // Cached static file paths - resolved once at startup
    private static string? _wwwrootPath;
    
    /// <summary>
    /// Maps all tracking-related endpoints. Called once at startup from <c>Program.cs</c>.
    /// <para>
    /// Resolves services from DI and captures them by closure in the endpoint lambdas.
    /// The catch-all <c>/{**path}</c> route MUST be registered last — it matches
    /// everything, so more specific routes (/, /demo, /health, /js/...) must be
    /// registered first or they'll be shadowed.
    /// </para>
    /// </summary>
    public static void MapTrackingEndpoints(this WebApplication app)
    {
        // Resolve services once at startup — captured by closure in each endpoint lambda.
        // This is safe because all these services are registered as singletons.
        var captureService = app.Services.GetRequiredService<TrackingCaptureService>();
        var pipeClient = app.Services.GetRequiredService<PipeClientService>();
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
                ctx.Response.ContentType = HtmlContentType;
                await ctx.Response.SendFileAsync(indexPath);
            }
            else
            {
                ctx.Response.ContentType = HtmlContentType;
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
                ctx.Response.ContentType = HtmlContentType;
                await ctx.Response.SendFileAsync(demoPath);
            }
            else
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("Demo page not found.");
            }
        });
        
        // NOTE: Static images are served by UseStaticFiles() middleware from wwwroot/.
        // The legacy /images/{fileName} endpoint was removed — it was redundant.
        
        // ============================================================================
        // DEBUG ENDPOINT - Restricted to local machine only
        // ============================================================================
        app.MapGet("/debug/headers", (HttpContext ctx) =>
        {
            var remoteIp = ctx.Connection.RemoteIpAddress;
            var localIp = ctx.Connection.LocalIpAddress;
            var isLocal = remoteIp is null
                || System.Net.IPAddress.IsLoopback(remoteIp)
                || (localIp != null && remoteIp.Equals(localIp));
            
            if (!isLocal) return Results.StatusCode(404);
            
            var data = captureService.CaptureFromRequest(ctx.Request);
            return Results.Json(data, DebugJsonOptions);
        });
        
        // ============================================================================
        // HEALTH CHECK - Useful for load balancers
        // ============================================================================
        app.MapGet("/health", (HttpContext ctx) =>
        {
            var queueDepth = pipeClient.QueueDepth;
            var status = queueDepth < 5000 ? "ok" : queueDepth < 9000 ? "warning" : "critical";
            return Results.Json(new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                pipeConnected = pipeClient.IsConnected,
                queueDepth,
                queueStatus = status
            });
        });
        
        // ============================================================================
        // CATCH-ALL ENDPOINT — Handles all PiXL URLs + bot trap
        //
        // URL DESIGN (owner-specified):
        //   Legacy:  /{companyId}/{pixlId}_{domain}_SMART.GIF  → capture + return 1x1 GIF
        //   Modern:  /{companyId}/{pixlId}_{domain}_SMART.js   → serve fingerprint script
        //   Other:   anything else → return GIF silently, flag as bot trap for analysis
        //
        // The catch-all MUST be registered last — it matches everything.
        // ============================================================================
        app.MapGet("/{**path}", (HttpContext ctx, IConfiguration config) =>
        {
            var path = ctx.Request.Path.ToString();
            
            // ── Modern PiXL script: *_SMART.js ──────────────────────────────
            if (path.EndsWith("_SMART.js", StringComparison.OrdinalIgnoreCase))
            {
                // Block direct browser navigation — only serve when loaded as <script>.
                // Sec-Fetch-Dest: "document" = browser address bar / link click.
                // Sec-Fetch-Dest: "script"   = <script src="..."> (legitimate use).
                // Missing header  = older browsers / curl — allow (obfuscated anyway).
                var fetchDest = ctx.Request.Headers["Sec-Fetch-Dest"].FirstOrDefault();
                if (string.Equals(fetchDest, "document", StringComparison.OrdinalIgnoreCase))
                    return Results.StatusCode(403);

                var urlMatch = PiXLUrlPattern().Match(path);
                if (urlMatch.Success)
                {
                    var companyId = urlMatch.Groups["companyId"].Value;
                    var pixlId = urlMatch.Groups["pixlId"].Value;
                    var domain = urlMatch.Groups["domain"].Value;
                    
                    if (SafeRouteParam().IsMatch(companyId) && SafeRouteParam().IsMatch(pixlId))
                    {
                        // Fallback URL embedded in JS — only used when document.currentScript
                        // is unavailable (very rare). The JS self-derives its callback URL
                        // from its own <script src> at runtime, making BaseUrl non-critical.
                        var baseUrl = config["Tracking:BaseUrl"];
                        if (string.IsNullOrEmpty(baseUrl))
                            baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
                        
                        var pixlUrl = $"{baseUrl}/{companyId}/{pixlId}_{domain}_SMART.GIF";
                        var javascript = PiXLScript.GetScript(pixlUrl);
                        
                        ctx.Response.ContentType = JsContentType;
                        ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                        // CORS: script is loaded cross-origin from customer sites
                        ctx.Response.Headers["Access-Control-Allow-Origin"] = CorsAllowAll;
                        return Results.Text(javascript, JsContentType);
                    }
                }
                return Results.StatusCode(400);
            }
            
            // ── Legacy script: any .js request that didn't match _SMART.js ─
            // smartpixl.com (and potentially other legacy installs) use an older
            // script tag like <script src="/js/CLIENT_ID/PIXL_ID.js">.
            // Serve the full fingerprint script with a placeholder callback URL
            // so we still collect all client data — companyId/pixlId stay 0/0
            // until the customer's tag is updated to the modern _SMART.js format.
            if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                var fetchDest = ctx.Request.Headers["Sec-Fetch-Dest"].FirstOrDefault();
                if (string.Equals(fetchDest, "document", StringComparison.OrdinalIgnoreCase))
                    return Results.StatusCode(403);

                var baseUrl = config["Tracking:BaseUrl"];
                if (string.IsNullOrEmpty(baseUrl))
                    baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";

                var pixlUrl = $"{baseUrl}/0/0_legacy_SMART.GIF";
                var javascript = PiXLScript.GetScript(pixlUrl);

                ctx.Response.ContentType = JsContentType;
                ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                ctx.Response.Headers["Access-Control-Allow-Origin"] = CorsAllowAll;
                return Results.Text(javascript, JsContentType);
            }
            
            // ── Legacy/Modern GIF: *_SMART.GIF ────────────────────────────
            if (path.EndsWith("_SMART.GIF", StringComparison.OrdinalIgnoreCase))
            {
                var urlMatch = PiXLUrlPattern().Match(path);
                var isValidPiXLUrl = urlMatch.Success;
                
                // Capture and enqueue — enrichment adds _srv_hitType and bot flags
                CaptureAndEnqueue(ctx, captureService, pipeClient, logger, isValidPiXLUrl);
                
                ctx.Response.ContentType = GifContentType;
                ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                ctx.Response.Headers.Pragma = "no-cache";
                ctx.Response.Headers["Expires"] = "0";
                return Results.Bytes(TransparentGif, GifContentType);
            }
            
            // ── Legacy ClearDot.gif: first-generation pixel format ─────────
            // Pattern: /{companyId}/{clientName}_{zipCode}_ClearDot.gif
            // These are legitimate customer pixels from the original platform.
            // CompanyID/PiXLID are extracted by CaptureService.PathParseRegex.
            if (ClearDotPattern().IsMatch(path))
            {
                CaptureAndEnqueue(ctx, captureService, pipeClient, logger, isValidPiXLUrl: true);
                
                ctx.Response.ContentType = GifContentType;
                ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                ctx.Response.Headers.Pragma = "no-cache";
                ctx.Response.Headers["Expires"] = "0";
                return Results.Bytes(TransparentGif, GifContentType);
            }
            
            // ── Catch-all: anything that doesn't match a known pixel format ───
            // Record every hit — scanner probes, bots, malformed URLs are all valuable
            // for traffic quality analysis. Flag with _srv_botTrap=1 for downstream
            // bot traffic metrics and client reporting.
            CaptureAndEnqueue(ctx, captureService, pipeClient, logger, isValidPiXLUrl: false);
            
            ctx.Response.ContentType = GifContentType;
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers.Pragma = "no-cache";
            ctx.Response.Headers["Expires"] = "0";
            return Results.Bytes(TransparentGif, GifContentType);
        });
        
        // ============================================================================
        // BEACON DATA ENDPOINT — POST *_SMART.DATA (sendBeacon from modern PiXL script)
        //
        // Receives fingerprint data as application/x-www-form-urlencoded POST body
        // from navigator.sendBeacon(). Same data as the GIF query string, but:
        //   - Survives page close/navigation (guaranteed delivery)
        //   - No URL length limit (POST body vs query string)
        //   - Returns 204 No Content (no GIF needed)
        //
        // CORS: sendBeacon from cross-origin customer sites requires permissive headers.
        // ============================================================================
        app.MapPost("/{**path}", async (HttpContext ctx) =>
        {
            var path = ctx.Request.Path.ToString();
            
            if (!path.EndsWith("_SMART.DATA", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            
            var urlMatch = PiXLUrlPattern().Match(path);
            if (!urlMatch.Success)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            
            // Read the form-urlencoded body and set it as the query string
            // so CaptureAndEnqueue + all enrichment services work unchanged.
            string body;
            using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true))
            {
                body = await reader.ReadToEndAsync();
            }
            
            // Rewrite the request's query string from the POST body so the existing
            // capture pipeline (which reads ctx.Request.Query) works without changes.
            if (body.Length > 0)
            {
                ctx.Request.QueryString = new QueryString("?" + body);
            }
            
            CaptureAndEnqueue(ctx, captureService, pipeClient, logger, isValidPiXLUrl: true);
            
            ctx.Response.Headers["Access-Control-Allow-Origin"] = CorsAllowAll;
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Response.StatusCode = 204;
        });
        
        // CORS preflight for sendBeacon POST requests from cross-origin customer sites
        app.MapMethods("/{**path}", ["OPTIONS"], (HttpContext ctx) =>
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = CorsAllowAll;
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            ctx.Response.Headers["Access-Control-Max-Age"] = "86400";
            return Results.StatusCode(204);
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
    
    // ========================================================================
    // SHARED HIT PROCESSING — Captures tracking data, tags hit type, and
    // enqueues for Forge delivery. No enrichment — that's all Forge work.
    // ========================================================================
    
    /// <summary>
    /// Captures tracking data from the HTTP request, tags hit type (modern/legacy)
    /// and bot trap flag, then enqueues for delivery to the Forge via named pipe.
    /// <para>
    /// This is the hot path — called for every tracking hit. Kept minimal:
    /// parse request, classify hit type, forward to Forge. All enrichment
    /// (fingerprint stability, IP behavior, datacenter detection, geo lookup)
    /// happens in the Forge process.
    /// </para>
    /// </summary>
    private static void CaptureAndEnqueue(
        HttpContext ctx,
        TrackingCaptureService captureService,
        PipeClientService pipeClient,
        ITrackingLogger logger,
        bool isValidPiXLUrl)
    {
        // Step 1: Parse HTTP request into a TrackingData record
        var trackingData = captureService.CaptureFromRequest(ctx.Request);
        
        // --- Hit-type detection ---
        // Modern hits have PiXLScript-collected parameters (canvasFP, sw).
        // Legacy hits arrive as bare <img> requests or with only ?ref=<url>.
        var isModernHit = ctx.Request.Query.ContainsKey("sw")
                       || ctx.Request.Query.ContainsKey("canvasFP");
        
        // --- Legacy ?ref= Referer fallback ---
        // Legacy Format B scripts send ?ref=<pageURL>. When the HTTP Referer header
        // is absent (some browsers strip it for cross-origin image requests), we
        // populate the Referer field from the ?ref= query parameter.
        if (!isModernHit && string.IsNullOrEmpty(trackingData.Referer))
        {
            var refParam = ctx.Request.Query["ref"].FirstOrDefault();
            if (!string.IsNullOrEmpty(refParam))
            {
                var truncated = refParam.Length > 2000 ? refParam[..2000] : refParam;
                trackingData = trackingData with { Referer = truncated };
            }
        }
        
        // Step 2: Append hit type + bot trap flag to QueryString.
        // These are the ONLY _srv_* params the Edge adds. All other enrichment
        // (_srv_fp*, _srv_subnet*, _srv_dc, _srv_ipType, _srv_geo*) is Forge work.
        {
            var sb = t_alertSb ??= new StringBuilder(512);
            sb.Clear();
            sb.Append(trackingData.QueryString);
            
            // Hit type: always present. Drives legacy vs modern analytics.
            sb.Append(sb.Length > 0 ? "&" : "")
              .Append("_srv_hitType=").Append(isModernHit ? "modern" : "legacy");
            
            // Bot trap: URL didn't match /{companyId}/{pixlId}_{domain}_SMART.(GIF|js)
            if (!isValidPiXLUrl)
                sb.Append("&_srv_botTrap=1");
            
            trackingData = trackingData with { QueryString = sb.ToString() };
        }
        
        // Step 3: Enqueue for Forge delivery via named pipe.
        if (!pipeClient.TryEnqueue(trackingData))
        {
            logger.Warning("Pipe queue full — dropped tracking request");
        }
    }
    
    /// <summary>
    /// Thread-static <see cref="StringBuilder"/> reused across requests on the same thread.
    /// <para>
    /// Declared with <see cref="ThreadStaticAttribute"/> so each thread-pool thread gets
    /// its own instance. After the first request, <c>Clear()</c> reuses the internal buffer
    /// with zero allocation. Typical enriched query strings are 200–400 chars, well within
    /// the initial 256-char capacity.
    /// </para>
    /// </summary>
    [ThreadStatic]
    private static StringBuilder? t_alertSb;
}
