using System.IO.Compression;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using TrackingPixel.Configuration;
using TrackingPixel.Endpoints;
using TrackingPixel.Services;

// ============================================================================
// SMARTPIXL TRACKING SERVER — Program.cs (Composition Root)
// ============================================================================
// This is the entry point and composition root for the SmartPiXL tracking
// server. It wires up the entire dependency graph:
//
//   Configuration/  → Strongly-typed settings (TrackingSettings, TrackingLogSettings)
//   Services/       → Core business logic: request capture, database writing,
//                     fingerprint stability analysis, IP behavior detection,
//                     datacenter IP lookup, ETL background processing, file logging
//   Endpoints/      → HTTP route handlers: pixel serving, JS generation, dashboard API
//   Scripts/        → Pixel JavaScript template compiled into the assembly
//
// DATA FLOW (hot path):
//   HTTP request → TrackingEndpoints (route match → _SMART.GIF suffix)
//     → TrackingCaptureService (parse headers, IP extraction, path decomposition)
//     → FingerprintStabilityService + IpBehaviorService (server-side enrichment)
//     → DatabaseWriterService (Channel<T> queue → SqlBulkCopy to PiXL.Raw)
//     → EtlBackgroundService (every 60s, calls ETL.usp_ParseNewHits → PiXL.Parsed)
//
// DEPLOYMENT: See .github/copilot-instructions.md for IIS vs dev port assignments.
//   Dev:  ports 7000/7001 (this file or appsettings.json)
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

// EtlBackgroundService: Calls ETL.usp_ParseNewHits every 60 seconds to move
// raw data from PiXL.Raw → PiXL.Parsed (materialized warehouse with ~175 columns).
builder.Services.AddHostedService<EtlBackgroundService>();

// GeoCacheService: Non-blocking in-memory IP geolocation lookups backed by IPAPI.IP.
// Used on the hot path for timezone mismatch signals and _srv_geo* enrichment params.
// On cache miss, writes to a bounded Channel<string>; background reader task performs
// SQL lookups and populates the two-tier cache for the next hit.
builder.Services.AddSingleton<GeoCacheService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GeoCacheService>());

// IpApiSyncService: Daily incremental sync from Xavier (IPGEO.dbo.IP_Location_New)
// to local IPAPI.IP. Pulls delta by Last_Seen watermark, MERGEs via staging table.
// After sync, enriches PiXL.IP geo columns and clears the geo hot cache.
builder.Services.AddSingleton<IpApiSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IpApiSyncService>());

// InfraHealthService: Probes Windows services, SQL connectivity, IIS websites,
// and in-process app metrics. Results cached 15s to avoid hammering on refresh.
builder.Services.AddSingleton<InfraHealthService>();

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

// ---------------------------------------------------------------------------
// WINDOWS SERVICE SUPPORT — Allows the app to run as a Windows Service
// (sc.exe create SmartPiXL) in addition to IIS InProcess hosting.
// ---------------------------------------------------------------------------
builder.Host.UseWindowsService(options => options.ServiceName = "SmartPiXL");

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

    return next();
});

// 5. Static Files — Serves wwwroot/ assets (index.html, tron.html, images, CSS).
//    HTML files: no-cache so browser always fetches latest after deploys.
app.UseStaticFiles(new StaticFileOptions
{
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
app.MapDashboardEndpoints();

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

logger.Info("SmartPiXL Tracking Server starting...");
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

logger.Info("SmartPiXL Tracking Server running — Ctrl+C to stop");

app.Run();
