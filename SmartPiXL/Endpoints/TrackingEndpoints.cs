using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartPiXL.Models;
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
//   /{companyId}/{pixlId}_{domain}_SMART.GIF (catch-all)   →  Legacy tracking GIF endpoint
//   /{**path} (catch-all fallback)                         →  Bot trap — returns GIF, flags record
//
// URL DESIGN (owner-specified):
//   Legacy:  GET /{companyId}/{pixlId}_{pixldomain}.com_SMART.GIF
//   Modern:  GET /{companyId}/{pixlId}_{pixldomain}.com_SMART.js
//   Example: GET /12800/00029_thetriviaquest.com_SMART.GIF
//
// ENRICHMENT PIPELINE (CaptureAndEnqueue):
//   1. TrackingCaptureService parses HTTP request into TrackingData
//   2. FingerprintStabilityService checks canvas/WebGL/audio fingerprint variation
//   3. IpBehaviorService checks subnet velocity + rapid-fire timing
//   4. DatacenterIpService checks AWS/GCP IP ranges
//   5. IpClassificationService classifies IP type (Private, CGNAT, Loopback, etc.)
//   6. Alert params appended to QueryString via thread-static StringBuilder
//   7. Enriched TrackingData sent to Forge via named pipe (or direct SQL fallback)
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
    
    // Full PiXL URL pattern: /{companyId}/{pixlId}_{domain}_SMART.(GIF|js)
    // Example: /12800/00029_thetriviaquest.com_SMART.GIF
    // Groups: companyId, pixlId, domain (domain includes TLD)
    [GeneratedRegex(@"^/?(?<companyId>[^/]+)/(?<pixlId>[^_]+)_(?<domain>.+)_SMART\.(GIF|js)$", RegexOptions.IgnoreCase)]
    private static partial Regex PiXLUrlPattern();
    
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
        var pipeClient = app.Services.GetRequiredService<PipeClientService>();
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
                var urlMatch = PiXLUrlPattern().Match(path);
                if (urlMatch.Success)
                {
                    var companyId = urlMatch.Groups["companyId"].Value;
                    var pixlId = urlMatch.Groups["pixlId"].Value;
                    var domain = urlMatch.Groups["domain"].Value;
                    
                    if (SafeRouteParam().IsMatch(companyId) && SafeRouteParam().IsMatch(pixlId))
                    {
                        var baseUrl = config["Tracking:BaseUrl"];
                        if (string.IsNullOrEmpty(baseUrl))
                            baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
                        
                        // Modern script sends data back to the GIF URL (same pattern, .GIF extension)
                        var pixlUrl = $"{baseUrl}/{companyId}/{pixlId}_{domain}_SMART.GIF";
                        var javascript = PiXLScript.GetScript(pixlUrl);
                        
                        ctx.Response.ContentType = JsContentType;
                        ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                        return Results.Text(javascript, JsContentType);
                    }
                }
                return Results.StatusCode(400);
            }
            
            // ── Legacy/Modern GIF: *_SMART.GIF ────────────────────────────
            if (path.EndsWith("_SMART.GIF", StringComparison.OrdinalIgnoreCase))
            {
                var urlMatch = PiXLUrlPattern().Match(path);
                var isValidPiXLUrl = urlMatch.Success;
                
                // Capture and enqueue — enrichment adds _srv_hitType and bot flags
                CaptureAndEnqueue(ctx, captureService, fpService, ipBehaviorService,
                    dcService, geoService, pipeClient, logger, isValidPiXLUrl);
                
                ctx.Response.ContentType = GifContentType;
                ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                ctx.Response.Headers.Pragma = "no-cache";
                ctx.Response.Headers["Expires"] = "0";
                return Results.Bytes(TransparentGif, GifContentType);
            }
            
            // ── Bot trap: anything that doesn't match a PiXL URL pattern ────
            // Return the GIF silently (don't reveal we know it's invalid).
            // Record the hit with _srv_botTrap=1 for botnet detection analysis.
            CaptureAndEnqueue(ctx, captureService, fpService, ipBehaviorService,
                dcService, geoService, pipeClient, logger, isValidPiXLUrl: false);
            
            ctx.Response.ContentType = GifContentType;
            ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Response.Headers.Pragma = "no-cache";
            ctx.Response.Headers["Expires"] = "0";
            return Results.Bytes(TransparentGif, GifContentType);
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
    // SHARED HIT PROCESSING — Server-side enrichment (fingerprint
    // stability, IP behavior) is performed and appended to the query string
    // before enqueuing for database write.
    // ========================================================================
    
    /// <summary>
    /// Captures tracking data from the HTTP request, runs all server-side enrichment
    /// analyses, appends alert/classification parameters to the query string, and
    /// enqueues the enriched record for delivery to the Forge via named pipe.
    /// <para>
    /// This is the hot path — called for every tracking hit.
    /// Enrichment results are appended as <c>&amp;_srv_*</c> query string params
    /// so the ETL pipeline (<c>usp_ParseNewHits</c>) can extract them into
    /// dedicated columns in <c>PiXL_Parsed</c> without any schema changes to <c>PiXL.Raw</c>.
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
    /// <param name="pipeClient">Named pipe client for sending records to the Forge.</param>
    /// <param name="logger">Logger for dropped-request warnings.</param>
    /// <param name="isValidPiXLUrl">True when the URL matches the PiXL pattern; false flags a bot trap.</param>
    private static void CaptureAndEnqueue(
        HttpContext ctx,
        TrackingCaptureService captureService,
        FingerprintStabilityService fpService,
        IpBehaviorService ipBehaviorService,
        DatacenterIpService dcService,
        GeoCacheService geoService,
        PipeClientService pipeClient,
        ITrackingLogger logger,
        bool isValidPiXLUrl)
    {
        // Step 1: Parse HTTP request into an immutable TrackingData record
        var trackingData = captureService.CaptureFromRequest(ctx.Request);
        var ip = trackingData.IPAddress ?? "unknown";
        
        // --- Hit-type detection ---
        // Modern hits have PiXLScript-collected parameters (canvasFP, sw).
        // Legacy hits arrive as bare <img> requests or with only ?ref=<url>.
        // Bot-trap hits have invalid URLs and get a separate flag.
        // The hit type is appended as _srv_hitType=modern|legacy and parsed by ETL.
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
        
        // Always append hit type — even for clean hits with no alerts.
        // This ensures every row in PiXL.Parsed gets a HitType value.
        {
            // Thread-local StringBuilder — zero alloc after first request per thread
            var sb = t_alertSb ??= new StringBuilder(512);
            sb.Clear();
            sb.Append(trackingData.QueryString);
            
            // Hit type: always present. Drives legacy vs modern analytics in the dashboard.
            sb.Append(sb.Length > 0 ? "&" : "")
              .Append("_srv_hitType=").Append(isModernHit ? "modern" : "legacy");
            
            // Bot trap: URL didn't match /{companyId}/{pixlId}_{domain}_SMART.(GIF|js)
            // These are probes, scanners, or replayed/malformed URLs — valuable for analysis.
            if (!isValidPiXLUrl)
                sb.Append("&_srv_botTrap=1");
            
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
        
        // Enqueue for Forge delivery via named pipe.
        // PipeClientService handles: pipe write → flush, or JSONL failover if pipe down.
        // The Edge NEVER writes to SQL directly — all SQL writes go through the Forge.
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
