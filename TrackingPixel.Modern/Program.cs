using Microsoft.AspNetCore.HttpOverrides;
using TrackingPixel.Configuration;
using TrackingPixel.Endpoints;
using TrackingPixel.Services;

// ============================================================================
// SMARTPIXL TRACKING SERVER - Program.cs
// ============================================================================
// This file is intentionally minimal. Business logic lives in:
//   - Services/      → Data capture, database writing, logging
//   - Endpoints/     → HTTP route handlers
//   - Configuration/ → Strongly-typed settings
//   - Scripts/       → JavaScript templates
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// CONFIGURATION
// ============================================================================
builder.Services.Configure<TrackingSettings>(
    builder.Configuration.GetSection(TrackingSettings.SectionName));

var logSettings = builder.Configuration
    .GetSection(TrackingLogSettings.SectionName)
    .Get<TrackingLogSettings>() ?? new TrackingLogSettings();

// ============================================================================
// SERVICES
// ============================================================================

// Logging - async file logger with configurable verbosity
builder.Services.AddSingleton<FileTrackingLogger>(new FileTrackingLogger(logSettings));
builder.Services.AddSingleton<ITrackingLogger>(sp => sp.GetRequiredService<FileTrackingLogger>());

// Capture service - extracts data from HTTP requests (stateless, thread-safe)
builder.Services.AddSingleton<TrackingCaptureService>();

// Database writer - background service with graceful shutdown
builder.Services.AddSingleton<DatabaseWriterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DatabaseWriterService>());

// V-05: Fingerprint stability tracking (anti-detect browser detection)
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<FingerprintStabilityService>();

// V-08: Datacenter IP detection (AWS, GCP, Azure, etc.)
builder.Services.AddHttpClient("DatacenterIp");
builder.Services.AddSingleton<DatacenterIpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DatacenterIpService>());

// CORS - allow any website to request the pixel
builder.Services.AddCors();

// ============================================================================
// WINDOWS SERVICE SUPPORT - Enables running as a Windows Service
// ============================================================================
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "SmartPiXL";
});

// ============================================================================
// KESTREL CONFIGURATION - Uses appsettings.json "Kestrel" section if present
// Falls back to dev ports (6000/6001) if not configured
// ============================================================================
var kestrelEndpoints = builder.Configuration.GetSection("Kestrel:Endpoints");
if (!kestrelEndpoints.Exists())
{
    // Development fallback - hardcoded ports
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxConcurrentConnections = 1000;
        options.Limits.MaxConcurrentUpgradedConnections = 1000;
        options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
        
        options.ListenAnyIP(6000); // HTTP
        options.ListenAnyIP(6001, listenOptions => listenOptions.UseHttps()); // HTTPS
    });
}
// else: Kestrel reads from config automatically

var app = builder.Build();

// ============================================================================
// MIDDLEWARE PIPELINE
// ============================================================================

// Forwarded Headers - MUST be first to populate RemoteIpAddress from X-Forwarded-For
// Required when behind reverse proxy (IIS, nginx, Cloudflare)
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    // Trust all proxies - required when behind multiple proxies or unknown proxy IPs
    ForwardLimit = null
};
forwardedOptions.KnownIPNetworks.Clear(); // Trust any source network
forwardedOptions.KnownProxies.Clear();   // Trust any proxy
app.UseForwardedHeaders(forwardedOptions);

// CORS - must be before endpoints
app.UseCors(policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod());

// Client Hints request header - applied to all responses
app.Use(async (context, next) =>
{
    context.Response.Headers["Accept-CH"] = 
        "Sec-CH-UA, Sec-CH-UA-Mobile, Sec-CH-UA-Platform, Sec-CH-UA-Platform-Version, " +
        "Sec-CH-UA-Full-Version-List, Sec-CH-UA-Arch, Sec-CH-UA-Model, Sec-CH-UA-Bitness";
    
    // Security headers — defense in depth
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    
    await next();
});

// Static files from wwwroot
app.UseStaticFiles();

// ============================================================================
// ENDPOINTS
// ============================================================================
app.MapTrackingEndpoints();
app.MapDashboardEndpoints();

// ============================================================================
// STARTUP
// ============================================================================
var logger = app.Services.GetRequiredService<ITrackingLogger>();

// Ensure FileTrackingLogger is properly disposed on shutdown (drains log queue)
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    var fileLogger = app.Services.GetRequiredService<FileTrackingLogger>();
    fileLogger.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

logger.Info("SmartPiXL Tracking Server starting...");
logger.Info($"HTTP:  http://localhost:6000");
logger.Info($"HTTPS: https://localhost:6001");
logger.Info($"Test:  https://localhost:6001/test");
logger.Info($"Debug: https://localhost:6001/debug/headers");

Console.WriteLine("SmartPiXL Tracking Server running");
Console.WriteLine("HTTP:  http://localhost:6000");
Console.WriteLine("HTTPS: https://localhost:6001 (recommended)");
Console.WriteLine("Press Ctrl+C to stop");

app.Run();
