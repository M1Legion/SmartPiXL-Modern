using Microsoft.Data.SqlClient;
using SmartPiXL.Worker.Services;
using TrackingPixel.Configuration;
using TrackingPixel.Endpoints;
using TrackingPixel.Services;

// ============================================================================
// SMARTPIXL WORKER — Program.cs (Composition Root)
// ============================================================================
// Background Windows Service that runs all non-hot-path workloads:
//   - ETL pipeline (PiXL.Raw → PiXL.Parsed every 60s)
//   - IP geolocation sync (Xavier → IPAPI.IP, daily 2 AM UTC)
//   - Company/PiXL sync (Xavier → PiXL.Company/PiXL.Settings, every 6h)
//   - Infrastructure health monitoring (InfraHealthService, 15s cache)
//   - Self-healing (circuit breaker watch, filegroup checks, auto-remediation)
//   - Maintenance scheduling (purge 3AM, index rebuild Sunday 4AM)
//   - Tron dashboard API (/api/dash/*, /tron)
//   - Atlas documentation portal (/atlas, /api/atlas/*)
//   - Email + SMS ops notifications
//
// COMMUNICATION WITH IIS EDGE:
//   The IIS Edge process owns the pixel capture hot path and the
//   DatabaseWriterService (Channel<T> + circuit breaker). This Worker
//   communicates with it via localhost HTTP through IEdgeHealthClient:
//     GET  /internal/health        → circuit state, queue depth, uptime
//     POST /internal/circuit-reset → reset circuit breaker
//     POST /internal/geo-cache/clear → invalidate geo cache after sync
//
// DEPLOYMENT:
//   Dev:  dotnet run (ports 7500/7501)
//   Prod: sc.exe create SmartPiXL-Worker (ports 7500/7501)
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// CONFIGURATION
// ---------------------------------------------------------------------------
builder.Services.Configure<TrackingSettings>(
    builder.Configuration.GetSection(TrackingSettings.SectionName));

// Xavier SQL Auth rewrite (IIS app pool identity can't delegate Windows Auth)
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
// SERVICE REGISTRATION — All singletons, started in registration order.
// ---------------------------------------------------------------------------

// FileTrackingLogger: Channel<T>-backed async file logger.
builder.Services.AddSingleton(new FileTrackingLogger(logSettings));
builder.Services.AddSingleton<ITrackingLogger>(sp => sp.GetRequiredService<FileTrackingLogger>());

// IEdgeHealthClient: HTTP bridge to the IIS Edge process.
// Base address comes from Tracking:EdgeBaseUrl (default http://127.0.0.1:6000).
builder.Services.AddHttpClient<IEdgeHealthClient, HttpEdgeHealthClient>((sp, client) =>
{
    var settings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TrackingSettings>>().Value;
    client.BaseAddress = new Uri(settings.EdgeBaseUrl ?? "http://127.0.0.1:6000");
    client.Timeout = TimeSpan.FromSeconds(5);
});

// EtlBackgroundService: PiXL.Raw → PiXL.Parsed every 60s.
builder.Services.AddHostedService<EtlBackgroundService>();

// IpApiSyncService: Xavier → IPAPI.IP daily sync.
builder.Services.AddSingleton<IpApiSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IpApiSyncService>());

// CompanyPiXLSyncService: Xavier → PiXL.Company/PiXL.Settings every 6h.
builder.Services.AddSingleton<CompanyPiXLSyncService>();
builder.Services.AddHostedService(sp =>
{
    var svc = sp.GetRequiredService<CompanyPiXLSyncService>();
    svc.EmailService = sp.GetService<EmailNotificationService>();
    return svc;
});

// InfraHealthService: Probes Windows services, SQL, IIS, app metrics.
builder.Services.AddSingleton<InfraHealthService>();

// EmailNotificationService: SMTP + SMS ops notifications.
builder.Services.AddSingleton<EmailNotificationService>();

// SelfHealingService: Monitors health, auto-remediates safe issues.
builder.Services.AddSingleton<SelfHealingService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SelfHealingService>());

// MaintenanceSchedulerService: Purge (3AM daily) + index rebuild (Sunday 4AM).
builder.Services.AddHostedService<MaintenanceSchedulerService>();

// CORS: Atlas is public-facing, Tron dashboard is localhost-only.
builder.Services.AddCors();

// ---------------------------------------------------------------------------
// WINDOWS SERVICE SUPPORT
// ---------------------------------------------------------------------------
builder.Host.UseWindowsService(options => options.ServiceName = "SmartPiXL-Worker");

// ---------------------------------------------------------------------------
// KESTREL — Worker listens on port 7500 (dev/prod).
// ---------------------------------------------------------------------------
if (!builder.Configuration.GetSection("Kestrel:Endpoints").Exists())
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(7500);
    });
}

var app = builder.Build();

// ===========================================================================
// MIDDLEWARE
// ===========================================================================

// CORS — Atlas is public; dashboard is protected by RequireLoopback.
app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// Security headers — defense-in-depth even though Worker is localhost-only.
app.Use(static (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    return next();
});

// Static files — tron.html, atlas.html, tron/*.mjs
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
// ENDPOINT MAPPING
// ===========================================================================
app.MapDashboardEndpoints();
app.MapAtlasEndpoints();

// ---------------------------------------------------------------------------
// STARTUP LOGGING + GRACEFUL SHUTDOWN
// ---------------------------------------------------------------------------
var logger = app.Services.GetRequiredService<ITrackingLogger>();

app.Services.GetRequiredService<IHostApplicationLifetime>()
    .ApplicationStopping.Register(() =>
    {
        app.Services.GetRequiredService<FileTrackingLogger>()
            .DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

logger.Info("SmartPiXL Worker starting...");
logger.Info("HTTP: http://localhost:7500");

// Validate Edge connectivity — warn early if EdgeBaseUrl is misconfigured.
{
    var settings = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TrackingSettings>>().Value;
    var edgeUrl = settings.EdgeBaseUrl ?? "http://127.0.0.1:6000";
    logger.Info($"EdgeBaseUrl: {edgeUrl}");

    _ = Task.Run(async () =>
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await http.GetAsync($"{edgeUrl.TrimEnd('/')}/health");
            if (response.IsSuccessStatusCode)
                logger.Info($"Edge health check OK ({edgeUrl})");
            else
                logger.Warning($"Edge health check returned {(int)response.StatusCode} \u2014 verify EdgeBaseUrl '{edgeUrl}' matches the running Edge process port (dev=7000, prod=6000)");
        }
        catch (Exception ex)
        {
            logger.Warning($"Edge unreachable at '{edgeUrl}': {ex.Message} \u2014 verify EdgeBaseUrl matches the running Edge process port (dev=7000, prod=6000)");
        }
    });
}

logger.Info("SmartPiXL Worker running — Ctrl+C to stop");

app.Run();
