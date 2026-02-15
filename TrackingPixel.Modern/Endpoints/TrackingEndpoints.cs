using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TrackingPixel.Models;
using TrackingPixel.Scripts;
using TrackingPixel.Services;

namespace TrackingPixel.Endpoints;

// ============================================================================
// TRACKING ENDPOINTS — The core pixel-serving + enrichment pipeline.
//
// ROUTE MAP:
//   /                        →  Landing page (wwwroot/index.html)
//   /demo                    →  Demo page (wwwroot/demo.html)
//   /debug/headers            →  Diagnostic JSON dump (localhost only)
//   /health                   →  Health check for load balancers
//   /js/{companyId}/{pixlId}.js  →  Fingerprint collection script
//   /{**path}                 →  Main pixel endpoint (returns 1x1 GIF)
//   /pixel204/{**path}        →  Alternative 204 No Content endpoint
//
// ENRICHMENT PIPELINE (CaptureAndEnqueue):
//   1. TrackingCaptureService parses HTTP request into TrackingData
//   2. FingerprintStabilityService checks canvas/WebGL/audio fingerprint variation
//   3. IpBehaviorService checks subnet velocity + rapid-fire timing
//   4. DatacenterIpService checks AWS/GCP IP ranges
//   5. IpClassificationService classifies IP type (Private, CGNAT, Loopback, etc.)
//   6. Alert params appended to QueryString via thread-static StringBuilder
//   7. Enriched TrackingData enqueued to Channel<T> for bulk SQL write
//
// STATIC FILES:
//   Images and other assets are served by UseStaticFiles() middleware from
//   wwwroot/. The legacy /images/{fileName} endpoint was removed — redundant.
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
    
    // Content type constants — avoid per-request string allocs
    private const string GifContentType = "image/gif";
    private const string JsContentType = "application/javascript";
    private const string HtmlContentType = "text/html; charset=utf-8";
    
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
        var writerService = app.Services.GetRequiredService<DatabaseWriterService>();
        var logger = app.Services.GetRequiredService<ITrackingLogger>();
        var fpService = app.Services.GetRequiredService<FingerprintStabilityService>();
        var ipBehaviorService = app.Services.GetRequiredService<IpBehaviorService>();
        var dcService = app.Services.GetRequiredService<DatacenterIpService>();
        var geoService = app.Services.GetRequiredService<GeoCacheService>();
        
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
            var queueDepth = writerService.QueueDepth;
            // Branchless status: bit-shift queue depth to map ranges
            var status = queueDepth < 5000 ? "ok" : queueDepth < 9000 ? "warning" : "critical";
            return Results.Json(new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                queueDepth,
                queueStatus = status
            });
        });
        
        // ============================================================================
        // JAVASCRIPT FILE ENDPOINT — Serves the fingerprint collection script
        // ============================================================================
        app.MapGet("/js/{companyId}/{pixlId}.js", (HttpContext ctx, string companyId, string pixlId, IConfiguration config) =>
        {
            // Validate route params — prevents JS injection and unbounded cache growth
            if (!SafeRouteParam().IsMatch(companyId) || !SafeRouteParam().IsMatch(pixlId))
            {
                ctx.Response.StatusCode = 400;
                return Results.Text("// invalid parameters", JsContentType);
            }
            
            var baseUrl = config["Tracking:BaseUrl"];
            if (string.IsNullOrEmpty(baseUrl))
            {
                baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            }
            var pixelUrl = $"{baseUrl}/{companyId}/{pixlId}_SMART.GIF";
            var javascript = PiXLScript.GetScript(pixelUrl);
            
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            
            return Results.Text(javascript, JsContentType);
        });
        
        // ============================================================================
        // MAIN PIXEL ENDPOINT - Returns 1x1 GIF
        // Only records requests with actual tracking data (querystring from the JS script)
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
                CaptureAndEnqueue(ctx, captureService, fpService, ipBehaviorService, dcService, geoService, writerService, logger);
            }
            // Else: silently return the GIF without recording (favicon, robots, etc.)
            
            ctx.Response.ContentType = GifContentType;
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers.Pragma = "no-cache";
            ctx.Response.Headers["Expires"] = "0";
            
            return Results.Bytes(TransparentGif, GifContentType);
        });
        
        // ============================================================================
        // ALTERNATIVE 204 ENDPOINT — No body, slightly faster
        // ============================================================================
        app.MapGet("/pixel204/{**path}", (HttpContext ctx) =>
        {
            var path = ctx.Request.Path.ToString();
            var queryString = ctx.Request.QueryString.ToString();
            
            // Only record enriched tracking data (requires querystring from JS)
            var isTrackingPixel = path.Contains("_SMART.GIF", StringComparison.OrdinalIgnoreCase);
            var hasTrackingData = queryString.Length > 10;
            
            if (isTrackingPixel && hasTrackingData)
            {
                CaptureAndEnqueue(ctx, captureService, fpService, ipBehaviorService, dcService, geoService, writerService, logger);
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
    
    // ========================================================================
    // SHARED PIXEL PROCESSING — Extracted from main pixel + pixel204 endpoints
    // to eliminate code duplication. Server-side enrichment (fingerprint
    // stability, IP behavior) is performed and appended to the query string
    // before enqueuing for database write.
    // ========================================================================
    
    /// <summary>
    /// Captures tracking data from the HTTP request, runs all server-side enrichment
    /// analyses, appends alert/classification parameters to the query string, and
    /// enqueues the enriched record for bulk database write.
    /// <para>
    /// This is the hot path — called for every valid tracking pixel hit.
    /// Enrichment results are appended as <c>&amp;_srv_*</c> query string params
    /// so the ETL pipeline (<c>usp_ParseNewHits</c>) can extract them into
    /// dedicated columns in <c>PiXL_Parsed</c> without any schema changes to <c>PiXL.Test</c>.
    /// </para>
    /// <para>
    /// Uses a <see cref="ThreadStaticAttribute">thread-static</see> <see cref="StringBuilder"/>
    /// to avoid per-request heap allocations when building the enriched query string.
    /// After the first request on each thread, the StringBuilder is reused via <c>Clear()</c>.
    /// </para>
    /// </summary>
    /// <param name="ctx">The current HTTP context (provides Request.Query for fingerprint extraction).</param>
    /// <param name="captureService">Parses the HTTP request into a <see cref="Models.TrackingData"/> record.</param>
    /// <param name="fpService">Fingerprint stability analysis (detects canvas/WebGL/audio variation).</param>
    /// <param name="ipBehaviorService">Subnet velocity + rapid-fire timing detection.</param>
    /// <param name="dcService">Datacenter IP range checker (AWS/GCP).</param>
    /// <param name="writerService">Channel-backed bulk writer for database persistence.</param>
    /// <param name="logger">Logger for dropped-request warnings.</param>
    private static void CaptureAndEnqueue(
        HttpContext ctx,
        TrackingCaptureService captureService,
        FingerprintStabilityService fpService,
        IpBehaviorService ipBehaviorService,
        DatacenterIpService dcService,
        GeoCacheService geoService,
        DatabaseWriterService writerService,
        ITrackingLogger logger)
    {
        // Step 1: Parse HTTP request into an immutable TrackingData record
        var trackingData = captureService.CaptureFromRequest(ctx.Request);
        var ip = trackingData.IPAddress ?? "unknown";
        
        // --- Server-side fingerprint stability + volume analysis ---
        // Catches what client-side can't: correlates multiple hits from the same IP
        // to detect bots rotating browser fingerprints between requests.
        var canvasFP = ctx.Request.Query["canvasFP"].FirstOrDefault();
        var webglFP = ctx.Request.Query["webglFP"].FirstOrDefault();
        var audioFP = ctx.Request.Query["audioFP"].FirstOrDefault();
        var fpResult = fpService.RecordAndCheck(ip, canvasFP, webglFP, audioFP);
        
        // --- Server-side IP behavior analysis ---
        // Detects subnet /24 velocity (coordinated bot infra) and rapid-fire timing
        // (same IP hitting faster than any human page-navigation pattern).
        var ipResult = ipBehaviorService.RecordAndCheck(ip);
        
        // --- Server-side IP enrichment ---
        // Datacenter detection: checks if the IP falls within known AWS/GCP CIDR ranges.
        //   Downloads range files on startup and refreshes weekly.
        // IP classification: categorizes as Public/Private/Loopback/CGNAT/etc.
        //   Uses zero-allocation IPv4 path with manual dotted-decimal parser.
        var dcResult = dcService.Check(ip);
        var ipClass = IpClassificationService.Classify(ip);
        
        // --- Server-side geolocation lookup (non-blocking) ---
        // Returns immediately from in-memory cache. On first hit for an IP,
        // queues an async SQL lookup — the next hit will have geo data.
        var geoResult = ipClass.ShouldGeolocate ? geoService.TryLookup(ip) : GeoResult.NotFound;
        
        // --- Timezone mismatch detection ---
        // Compares the client-reported IANA timezone (from JS Intl.DateTimeFormat)
        // against the IP-derived timezone from IPAPI. A mismatch is a strong VPN/proxy signal.
        var clientTz = ctx.Request.Query["tz"].FirstOrDefault();
        var geoTzMismatch = false;
        if (geoResult.Found && !string.IsNullOrEmpty(geoResult.Timezone) && !string.IsNullOrEmpty(clientTz))
        {
            geoTzMismatch = !string.Equals(clientTz, geoResult.Timezone, StringComparison.OrdinalIgnoreCase);
        }
        
        // Build server-side query string extension when any enrichment fires.
        // Only allocates when there's something to append — clean hits pass through
        // with the original QueryString untouched (no StringBuilder, no string concat).
        var hasFpAlert = fpResult.SuspiciousVariation || fpResult.HighVolume || fpResult.HighRate;
        var hasIpAlert = ipResult.SubnetVelocityAlert || ipResult.RapidFireAlert || ipResult.SubSecondDuplicate;
        var hasDcMatch = dcResult.IsDatacenter;
        // Skip enrichment for Public + Invalid — those are the common/default cases
        var hasIpClass = ipClass.Type is not IpType.Public and not IpType.Invalid;
        var hasGeoData = geoResult.Found || geoTzMismatch;
        
        if (hasFpAlert || hasIpAlert || hasDcMatch || hasIpClass || hasGeoData)
        {
            // Thread-local StringBuilder — zero alloc after first request per thread
            var sb = t_alertSb ??= new StringBuilder(512);
            sb.Clear();
            sb.Append(trackingData.QueryString);
            
            if (hasFpAlert)
            {
                sb.Append("&_srv_fpAlert=1&_srv_fpObs=").Append(fpResult.ObservationCount)
                  .Append("&_srv_fpUniq=").Append(fpResult.UniqueFingerprints)
                  .Append("&_srv_fpRate5m=").Append(fpResult.RecentRate);
            }
            
            if (hasIpAlert)
            {
                sb.Append("&_srv_subnetIps=").Append(ipResult.SubnetUniqueIps)
                  .Append("&_srv_subnetHits=").Append(ipResult.SubnetTotalHits)
                  .Append("&_srv_hitsIn15s=").Append(ipResult.HitsIn15Seconds)
                  .Append("&_srv_lastGapMs=").Append(ipResult.LastGapMs);
                
                if (ipResult.SubSecondDuplicate) sb.Append("&_srv_subSecDupe=1");
                if (ipResult.SubnetVelocityAlert) sb.Append("&_srv_subnetAlert=1");
                if (ipResult.RapidFireAlert) sb.Append("&_srv_rapidFire=1");
            }
            
            // Datacenter flag: _srv_dc=AWS or _srv_dc=GCP
            // Helps the ETL flag cloud-origin traffic for bot scoring
            if (hasDcMatch)
                sb.Append("&_srv_dc=").Append(dcResult.Provider);
            
            // IP classification: _srv_ipType=1 (Private), 3 (LinkLocal), 4 (CGNAT), etc.
            // Byte-backed enum cast — serializes as a single digit, parseable by ETL
            if (hasIpClass)
                sb.Append("&_srv_ipType=").Append((byte)ipClass.Type);
            
            // Geo enrichment: _srv_geo* params for ETL and real-time bot signals.
            // Only appended when the IP was found in the in-memory geo cache.
            // IPs not in cache get geo data via the ETL JOIN (IPAPI.IP) instead.
            if (geoResult.Found)
            {
                if (!string.IsNullOrEmpty(geoResult.CountryCode))
                    sb.Append("&_srv_geoCC=").Append(geoResult.CountryCode);
                if (!string.IsNullOrEmpty(geoResult.Region))
                    sb.Append("&_srv_geoReg=").Append(Uri.EscapeDataString(geoResult.Region));
                if (!string.IsNullOrEmpty(geoResult.City))
                    sb.Append("&_srv_geoCity=").Append(Uri.EscapeDataString(geoResult.City));
                if (!string.IsNullOrEmpty(geoResult.Timezone))
                    sb.Append("&_srv_geoTz=").Append(Uri.EscapeDataString(geoResult.Timezone));
                if (!string.IsNullOrEmpty(geoResult.ISP))
                    sb.Append("&_srv_geoISP=").Append(Uri.EscapeDataString(geoResult.ISP));
                if (geoResult.IsProxy == true)
                    sb.Append("&_srv_geoProxy=1");
                if (geoResult.IsMobile == true)
                    sb.Append("&_srv_geoMobile=1");
            }
            
            // Timezone mismatch: strong VPN/proxy signal.
            // Client says America/New_York but IP resolves to Europe/London → suspicious.
            if (geoTzMismatch)
                sb.Append("&_srv_geoTzMismatch=1");
            
            // Replace the original QueryString with the enriched version.
            // Uses C# record 'with' expression — creates a shallow copy with only
            // QueryString changed. All other fields reference the same strings (no copy).
            trackingData = trackingData with { QueryString = sb.ToString() };
        }
        
        // Enqueue for async bulk write. TryQueue is a lock-free CAS operation
        // that returns false only when the bounded channel is at capacity.
        if (!writerService.TryQueue(trackingData))
        {
            logger.Warning("Queue full - dropped tracking request");
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
