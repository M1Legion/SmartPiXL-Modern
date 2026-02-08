using Microsoft.AspNetCore.HttpOverrides;
using TrackingPixel.Configuration;
using TrackingPixel.Endpoints;
using TrackingPixel.Services;

// ============================================================================
// SMARTPIXL TRACKING SERVER - Program.cs
// ============================================================================
// Business logic lives in:
//   Services/ → Data capture, database writing, logging
//   Endpoints/ → HTTP route handlers
//   Configuration/ → Strongly-typed settings
//   Scripts/ → JavaScript templates
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
builder.Services.Configure<TrackingSettings>(
    builder.Configuration.GetSection(TrackingSettings.SectionName));

var logSettings = builder.Configuration
    .GetSection(TrackingLogSettings.SectionName)
    .Get<TrackingLogSettings>() ?? new TrackingLogSettings();

// --- Services ---
builder.Services.AddSingleton(new FileTrackingLogger(logSettings));
builder.Services.AddSingleton<ITrackingLogger>(sp => sp.GetRequiredService<FileTrackingLogger>());
builder.Services.AddSingleton<TrackingCaptureService>();
builder.Services.AddSingleton<DatabaseWriterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DatabaseWriterService>());
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<FingerprintStabilityService>();
builder.Services.AddHttpClient("DatacenterIp");
builder.Services.AddSingleton<DatacenterIpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DatacenterIpService>());
builder.Services.AddCors();

// --- Windows Service ---
builder.Host.UseWindowsService(options => options.ServiceName = "SmartPiXL");

// --- Kestrel (dev fallback — IIS/appsettings overrides in production) ---
if (!builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxConcurrentConnections = 1000;
        options.Limits.MaxConcurrentUpgradedConnections = 1000;
        options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
        options.ListenAnyIP(6000);
        options.ListenAnyIP(6001, lo => lo.UseHttps());
    });
}

var app = builder.Build();

// --- Middleware ---

// Forwarded Headers — MUST be first (reverse proxy: IIS, nginx, Cloudflare)
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = null
};
forwardedOptions.KnownIPNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);

app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// Response headers — Client Hints + security hardening (no closure, no capture)
app.Use(static (context, next) =>
{
    // Compile-time constants — zero allocation per request
    const string acceptCh =
        "Sec-CH-UA, Sec-CH-UA-Mobile, Sec-CH-UA-Platform, Sec-CH-UA-Platform-Version, " +
        "Sec-CH-UA-Full-Version-List, Sec-CH-UA-Arch, Sec-CH-UA-Model, Sec-CH-UA-Bitness";

    var headers = context.Response.Headers;
    headers["Accept-CH"] = acceptCh;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

    return next();
});

app.UseStaticFiles();

// --- Endpoints ---
app.MapTrackingEndpoints();
app.MapDashboardEndpoints();

// --- Startup ---
var logger = app.Services.GetRequiredService<ITrackingLogger>();

app.Services.GetRequiredService<IHostApplicationLifetime>()
    .ApplicationStopping.Register(() =>
    {
        app.Services.GetRequiredService<FileTrackingLogger>()
            .DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

logger.Info("SmartPiXL Tracking Server starting...");
logger.Info("HTTP:  http://localhost:6000");
logger.Info("HTTPS: https://localhost:6001");

Console.WriteLine("SmartPiXL Tracking Server running — Ctrl+C to stop");

app.Run();
