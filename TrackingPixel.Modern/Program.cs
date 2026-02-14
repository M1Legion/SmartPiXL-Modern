using Microsoft.AspNetCore.HttpOverrides;
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
//     → DatabaseWriterService (Channel<T> queue → SqlBulkCopy to PiXL.Test)
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
// raw data from PiXL.Test → PiXL.Parsed (materialized warehouse with ~175 columns).
builder.Services.AddHostedService<EtlBackgroundService>();

// InfraHealthService: Probes Windows services, SQL connectivity, IIS websites,
// and in-process app metrics. Results cached 15s to avoid hammering on refresh.
builder.Services.AddSingleton<InfraHealthService>();

// CORS: Wide-open for tracking pixel — any origin can embed our script/GIF.
builder.Services.AddCors();

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
//    ForwardLimit=null accepts any chain depth (CDN → LB → IIS → Kestrel).
//    KnownNetworks/Proxies cleared so all forwarded headers are trusted.
//    SECURITY NOTE: In this deployment IIS is the only upstream, so this is safe.
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = null
};
forwardedOptions.KnownIPNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);

// 2. CORS — Tracking pixels are embedded on third-party sites; must allow all origins.
app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// 3. Response Headers — Injected on every response. The static lambda avoids
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

// 4. Static Files — Serves wwwroot/ assets (index.html, tron.html, images, CSS).
app.UseStaticFiles();

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

Console.WriteLine("SmartPiXL Tracking Server running — Ctrl+C to stop");

app.Run();
