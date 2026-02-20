using System.IO.Compression;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Data.SqlClient;
using TrackingPixel.Configuration;
using TrackingPixel.Endpoints;
using TrackingPixel.Services;

// ============================================================================
// SMARTPIXL EDGE — Program.cs (IIS Pixel Capture Composition Root)
// ============================================================================
// This is the IIS-hosted "Edge" process — minimal, hot-path only.
// It handles pixel capture, fingerprint enrichment, and SQL bulk writing.
//
// SERVICES (hot path):
//   TrackingCaptureService    → Parse HTTP request into TrackingData
//   FingerprintStabilityService → Per-IP fingerprint variation tracking
//   IpBehaviorService        → Subnet velocity + rapid-fire detection
//   DatacenterIpService      → AWS/GCP IP range lookup
//   GeoCacheService          → Non-blocking IP geolocation cache
//   IpClassificationService  → Static classification helper
//   DatabaseWriterService    → Channel<T> queue → SqlBulkCopy to PiXL.Raw
//
// ENDPOINTS:
//   TrackingEndpoints: /{**path} (pixel), /js/{co}/{px}.js, /health, /demo
//   InternalEndpoints: /internal/health, /internal/circuit-reset,
//                      /internal/geo-cache/clear (Worker ↔ Edge bridge)
//
// All backend work (ETL, sync, healing, dashboards) runs in SmartPiXL.Worker.
//
// DEPLOYMENT: See .github/copilot-instructions.md for port assignments.
//   Dev:  ports 7000/7001
//   Prod: ports 6000/6001 (IIS appsettings.json override)
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// CONFIGURATION — Bind appsettings.json sections to strongly-typed POCOs.
// TrackingSettings: connection string, queue capacity, batch sizes.
// TrackingLogSettings: log directory, minimum level, console echo.
// ---------------------------------------------------------------------------
builder.Services.Configure<TrackingSettings>(
    builder.Configuration.GetSection(TrackingSettings.SectionName));

// Rewrite Xavier connection strings to use SQL Auth when SQL_USERNAME / SQL_PASSWORD
// env vars are present. IIS app pool identities can't delegate Windows Auth across
// the network (falls back to NT AUTHORITY\ANONYMOUS LOGON), so SQL Auth is required
// for reliable cross-server connectivity to Xavier (192.168.88.35).
var sqlUser = Environment.GetEnvironmentVariable("SQL_USERNAME", EnvironmentVariableTarget.Machine);
var sqlPass = Environment.GetEnvironmentVariable("SQL_PASSWORD", EnvironmentVariableTarget.Machine);
if (!string.IsNullOrEmpty(sqlUser) && !string.IsNullOrEmpty(sqlPass))
{
    builder.Services.PostConfigure<TrackingSettings>(settings =>
    {
        settings.XavierConnectionString = RewriteToSqlAuth(
            settings.XavierConnectionString, sqlUser, sqlPass);
        settings.XavierSmartPiXLConnectionString = RewriteToSqlAuth(
            settings.XavierSmartPiXLConnectionString, sqlUser, sqlPass);
    });
}

static string? RewriteToSqlAuth(string? connStr, string user, string password)
{
    if (string.IsNullOrEmpty(connStr)) return connStr;
    var csb = new SqlConnectionStringBuilder(connStr)
    {
        IntegratedSecurity = false,
        UserID = user,
        Password = password
    };
    return csb.ConnectionString;
}

var logSettings = builder.Configuration
    .GetSection(TrackingLogSettings.SectionName)
    .Get<TrackingLogSettings>() ?? new TrackingLogSettings();

// ---------------------------------------------------------------------------
// SERVICE REGISTRATION — All services are singleton (stateless or shared state).
// Registration order matters for hosted services (started top-to-bottom).
// ---------------------------------------------------------------------------

// FileTrackingLogger: Channel<T>-backed async file logger — non-blocking writes.
// Registered both as concrete type (for DisposeAsync on shutdown) and as
// ITrackingLogger (the abstraction consumed by all other services).
builder.Services.AddSingleton(new FileTrackingLogger(logSettings));
builder.Services.AddSingleton<ITrackingLogger>(sp => sp.GetRequiredService<FileTrackingLogger>());

// TrackingCaptureService: Stateless HTTP request parser. Extracts CompanyID,
// PiXLID, client IP (from proxy header chain), headers JSON, User-Agent, Referer.
builder.Services.AddSingleton<TrackingCaptureService>();

// DatabaseWriterService: BackgroundService that reads from a Channel<TrackingData>
// queue and bulk-inserts into SQL Server via SqlBulkCopy. Registered as both
// singleton (so endpoints can call TryQueue) and hosted service (so the runtime
// calls ExecuteAsync to start the write loop).
builder.Services.AddSingleton<DatabaseWriterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DatabaseWriterService>());

// MemoryCache: Shared in-process cache used by FingerprintStabilityService and
// IpBehaviorService for per-IP sliding window tracking.
builder.Services.AddMemoryCache();

// FingerprintStabilityService: Tracks fingerprint variation per IP over 24h.
// Detects anti-detect browsers (3+ unique fingerprints from same IP).
builder.Services.AddSingleton<FingerprintStabilityService>();

// IpBehaviorService: Server-side subnet /24 velocity and rapid-fire timing.
// Detects coordinated bot infrastructure and automation timing patterns.
builder.Services.AddSingleton<IpBehaviorService>();

// DatacenterIpService: Downloads AWS + GCP IP ranges on startup, refreshes weekly.
// Lock-free volatile reference swap for zero-contention reads on the hot path.
builder.Services.AddHttpClient("DatacenterIp");
builder.Services.AddSingleton<DatacenterIpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DatacenterIpService>());

// GeoCacheService: Non-blocking in-memory IP geolocation lookups backed by IPAPI.IP.
// Used on the hot path for timezone mismatch signals and _srv_geo* enrichment params.
// On cache miss, writes to a bounded Channel<string>; background reader task performs
// SQL lookups and populates the two-tier cache for the next hit.
builder.Services.AddSingleton<GeoCacheService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GeoCacheService>());

// CORS: Wide-open for tracking pixel — any origin can embed our script/GIF.
builder.Services.AddCors();

// Response Compression: Gzip + Brotli for text responses (JS, JSON, HTML).
// The 43-byte GIF pixel is excluded by MIME type (image/gif is not compressible).
// Brotli preferred (better ratio), gzip as fallback for older clients.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
    [
        "application/javascript",
        "text/html",
        "application/json"
    ]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
    options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
    options.Level = CompressionLevel.Fastest);

// NOTE: UseWindowsService is intentionally omitted for the Edge process.
// The Edge always runs as IIS InProcess (aspNetCore hostingModel="inprocess").
// The Worker process (SmartPiXL.Worker) is the Windows Service.

// ---------------------------------------------------------------------------
// KESTREL FALLBACK — Only applies when running outside IIS (dotnet run).
// When Kestrel endpoints are defined in appsettings.json (IIS or explicit config),
// this block is skipped entirely.
//   Dev:  7000 (HTTP) / 7001 (HTTPS)
//   Prod: 6000 / 6001 (set in IIS appsettings.json)
// ---------------------------------------------------------------------------
if (!builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxConcurrentConnections = 1000;
        options.Limits.MaxConcurrentUpgradedConnections = 1000;
        options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
        options.ListenAnyIP(7000);
        options.ListenAnyIP(7001, lo => lo.UseHttps());
    });
}

var app = builder.Build();

// ===========================================================================
// MIDDLEWARE PIPELINE — Order is critical. Each middleware runs in order for
// every request. ForwardedHeaders must be first so downstream middleware
// and endpoint handlers see the real client IP, not the proxy IP.
// ===========================================================================

// 1. Forwarded Headers — Rewrites RemoteIpAddress from X-Forwarded-For.
//    ForwardLimit=1: only the immediate upstream (IIS) is trusted.
//    Loopback is added to KnownProxies so IIS InProcess hosting works.
//    Client-injected X-Forwarded-For chains beyond the first hop are ignored.
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1
};
forwardedOptions.KnownProxies.Add(System.Net.IPAddress.Loopback);      // 127.0.0.1
forwardedOptions.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);  // ::1
app.UseForwardedHeaders(forwardedOptions);

// 2. Response Compression — Brotli/Gzip for text responses (JS, JSON, HTML).
//    Must be before StaticFiles and endpoints so compressed responses flow through.
//    Does NOT compress the 43-byte GIF (image/gif is not in the MIME list).
app.UseResponseCompression();

// 3. CORS — Tracking pixels are embedded on third-party sites; must allow all origins.
app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// 4. Response Headers — Injected on every response. The static lambda avoids
//    closure capture, so no delegate allocation per request. Accept-CH requests
//    high-entropy Client Hints (architecture, bitness, model, platform version)
//    from Chromium browsers, which the pixel JS then reads for fingerprinting.
app.Use(static (context, next) =>
{
    // Compile-time constants — zero allocation per request
    const string acceptCh =
        "Sec-CH-UA, Sec-CH-UA-Mobile, Sec-CH-UA-Platform, Sec-CH-UA-Platform-Version, " +
        "Sec-CH-UA-Full-Version-List, Sec-CH-UA-Arch, Sec-CH-UA-Model, Sec-CH-UA-Bitness";

    var headers = context.Response.Headers;
    headers["Accept-CH"] = acceptCh;                                   // Request Client Hints
    headers["X-Content-Type-Options"] = "nosniff";                     // Prevent MIME sniffing
    headers["X-Frame-Options"] = "DENY";                               // Prevent clickjacking
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";    // Limit Referer leakage
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()"; // Kill risky APIs
    headers["Content-Security-Policy"] = "default-src 'none'";         // Pixel endpoint: no HTML rendering

    // HSTS: only add when the request arrived over HTTPS to avoid
    // breaking plain-HTTP dev/test flows.
    if (context.Request.IsHttps)
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";

    return next();
});

// 5. Static Files — Serves wwwroot/ assets (index.html, tron.html, images, CSS).
//    HTML files: no-cache so browser always fetches latest after deploys.
//    .mjs: ES module files for the Tron 3D scene (wwwroot/tron/*.mjs).
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".mjs"] = "application/javascript";
contentTypes.Mappings[".glsl"] = "text/plain";

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = ctx =>
    {
        var path = ctx.File.Name;
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers.Pragma = "no-cache";
            ctx.Context.Response.Headers.Expires = "0";
        }
    }
});

// ===========================================================================
// ENDPOINT MAPPING — See Endpoints/ folder for route definitions.
//   TrackingEndpoints: /{**path} (pixel), /js/{co}/{px}.js, /health, /demo
//   DashboardEndpoints: /api/dash/*, /api/dashboard/*, /tron, /dashboard
// ===========================================================================
app.MapTrackingEndpoints();
app.MapInternalEndpoints();

// ---------------------------------------------------------------------------
// STARTUP LOGGING + GRACEFUL SHUTDOWN
// ---------------------------------------------------------------------------
var logger = app.Services.GetRequiredService<ITrackingLogger>();

// Register shutdown hook: flush the Channel<LogEntry> and close the log file
// before the process exits. Uses sync-over-async (GetAwaiter().GetResult())
// because ApplicationStopping is a synchronous CancellationToken callback.
app.Services.GetRequiredService<IHostApplicationLifetime>()
    .ApplicationStopping.Register(() =>
    {
        app.Services.GetRequiredService<FileTrackingLogger>()
            .DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

logger.Info("SmartPiXL Edge starting...");
logger.Info("HTTP:  http://localhost:7000");
logger.Info("HTTPS: https://localhost:7001");

// Pre-warm the geo cache with top-hit IPs from PiXL.IP × IPAPI.IP.
// Runs on a background task so it doesn't block app startup.
_ = Task.Run(async () =>
{
    try
    {
        var geoCache = app.Services.GetRequiredService<GeoCacheService>();
        await geoCache.PrewarmAsync(2000);
    }
    catch (Exception ex)
    {
        logger.Warning($"Geo cache prewarm failed: {ex.Message}");
    }
});

logger.Info("SmartPiXL Edge running — Ctrl+C to stop");

app.Run();
