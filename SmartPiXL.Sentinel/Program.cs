using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Sentinel.Endpoints;
using SmartPiXL.Sentinel.Services;
using SmartPiXL.Services;

// ============================================================================
// SMARTPIXL SENTINEL — Program.cs (Composition Root)
// ============================================================================
// Windows Service that hosts the operational dashboards and documentation portal:
//   - Tron Operations dashboard (/tron, /api/dash/*)
//   - Tron Metrics (enrichment-aware analytics panels)
//   - Atlas documentation portal (/atlas, /api/atlas/*)
//   - TrafficAlert API (/api/traffic-alert/*)
//   - Email + SMS ops notifications
//
// NO BACKGROUND PROCESSING — The Forge handles all background work (ETL, sync,
// self-healing loop, maintenance). The Sentinel is a pure HTTP API server that
// reads from the database and proxies control commands to the Edge.
//
// COMMUNICATION:
//   Sentinel → SQL Server (read-only queries against views/tables)
//   Sentinel → IIS Edge (HTTP: /internal/health, /internal/circuit-reset)
//   Browser  → Sentinel (Tron SPA, Atlas SPA, JSON API)
//
// DEPLOYMENT:
//   Dev:  dotnet run (port 7500)
//   Prod: sc.exe create SmartPiXL-Sentinel binPath= "C:\Services\SmartPiXL-Sentinel\SmartPiXL.Sentinel.exe"
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// CONFIGURATION
// ---------------------------------------------------------------------------
builder.Services.Configure<TrackingSettings>(
    builder.Configuration.GetSection(TrackingSettings.SectionName));

// Xavier SQL Auth rewrite (same pattern as Forge — environment variables for
// SQL Auth when Windows Auth delegation isn't available)
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
// SERVICE REGISTRATION — All singletons. No background workers in Sentinel.
// ---------------------------------------------------------------------------

// FileTrackingLogger: Channel<T>-backed async file logger.
builder.Services.AddSingleton(new FileTrackingLogger(logSettings));
builder.Services.AddSingleton<ITrackingLogger>(sp => sp.GetRequiredService<FileTrackingLogger>());

// IEdgeHealthClient: HTTP bridge to the IIS Edge process.
builder.Services.AddHttpClient<IEdgeHealthClient, HttpEdgeHealthClient>((sp, client) =>
{
    var settings = sp.GetRequiredService<IOptions<TrackingSettings>>().Value;
    client.BaseAddress = new Uri(settings.EdgeBaseUrl ?? "http://127.0.0.1:6000");
    client.Timeout = TimeSpan.FromSeconds(5);
});

// InfraHealthService: Probes Windows services, SQL, IIS, app metrics (15s cache).
builder.Services.AddSingleton<InfraHealthService>();

// EmailNotificationService: SMTP + SMS ops notifications.
builder.Services.AddSingleton<EmailNotificationService>();

// RemediationService: Approve/skip/list remediation entries from Ops.RemediationLog.
// Unlike the Forge's SelfHealingService, this does NOT run a healing loop —
// it only provides the API surface for operator interaction.
builder.Services.AddSingleton<RemediationService>();

// CORS: Atlas is public-facing, Tron dashboard is localhost-only.
builder.Services.AddCors();

// ---------------------------------------------------------------------------
// WINDOWS SERVICE SUPPORT
// ---------------------------------------------------------------------------
builder.Host.UseWindowsService(options => options.ServiceName = "SmartPiXL-Sentinel");

// ---------------------------------------------------------------------------
// KESTREL — Sentinel listens on port 7500 (dev and prod).
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

// Security headers — defense-in-depth.
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
app.MapTrafficAlertEndpoints();

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

logger.Info("SmartPiXL Sentinel starting...");
logger.Info("HTTP: http://localhost:7500");

// Validate Edge connectivity — warn early if EdgeBaseUrl is misconfigured.
{
    var settings = app.Services.GetRequiredService<IOptions<TrackingSettings>>().Value;
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
                logger.Warning($"Edge health check returned {(int)response.StatusCode} — verify EdgeBaseUrl '{edgeUrl}' matches the running Edge process port (dev=7000, prod=6000)");
        }
        catch (Exception ex)
        {
            logger.Warning($"Edge unreachable at '{edgeUrl}': {ex.Message} — verify EdgeBaseUrl matches the running Edge process port (dev=7000, prod=6000)");
        }
    });
}

logger.Info("SmartPiXL Sentinel running — Ctrl+C to stop");

app.Run();
