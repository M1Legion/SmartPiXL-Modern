using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Forge.Services;
using SmartPiXL.Services;

// ============================================================================
// SMARTPIXL FORGE — Program.cs (Composition Root)
// ============================================================================
// Background Windows Service that runs all non-hot-path workloads:
//   - Named pipe server (receives enriched records from IIS Edge)
//   - Enrichment pipeline (Tier 1-3, pass-through in Phase 2)
//   - SqlBulkCopy writer (Channel<T> → PiXL.Raw)
//   - Failover catch-up (JSONL files → enrichment pipeline)
//   - ETL pipeline (PiXL.Raw → PiXL.Parsed every 60s)
//   - IP geolocation sync (Xavier → IPAPI.IP, daily 2 AM UTC)
//   - Company/PiXL sync (Xavier → PiXL.Company/PiXL.Settings, every 6h)
//   - Infrastructure health monitoring (InfraHealthService, 15s cache)
//   - Self-healing (circuit breaker watch, filegroup checks, auto-remediation)
//   - Maintenance scheduling (purge 3AM, index rebuild Sunday 4AM)
//   - Email + SMS ops notifications
//
// NO HTTP — This is a pure Worker Service.
// Tron dashboard + Atlas portal will be served by SmartPiXL.Sentinel (Phase 10).
//
// COMMUNICATION WITH IIS EDGE:
//   Inbound:  Named pipe "SmartPiXL-Enrichment" (TrackingData JSON lines)
//   Outbound: IEdgeHealthClient HTTP calls to localhost Edge endpoints
//     GET  /internal/health        → circuit state, queue depth, uptime
//     POST /internal/circuit-reset → reset circuit breaker
//     POST /internal/geo-cache/clear → invalidate geo cache after sync
//
// DEPLOYMENT:
//   sc.exe create SmartPiXL-Forge binPath= "C:\Services\SmartPiXL-Forge\SmartPiXL.Forge.exe"
// ============================================================================

var builder = Host.CreateApplicationBuilder(args);

// ---------------------------------------------------------------------------
// CONFIGURATION
// ---------------------------------------------------------------------------
builder.Services.Configure<TrackingSettings>(
    builder.Configuration.GetSection(TrackingSettings.SectionName));

builder.Services.Configure<ForgeSettings>(
    builder.Configuration.GetSection(ForgeSettings.SectionName));

// Xavier SQL Auth rewrite (IIS app pool identity can't delegate Windows Auth
// to the remote Xavier server — use machine-level environment variables instead)
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

// Log settings — resolved immediately for FileTrackingLogger constructor
var logSettings = builder.Configuration
    .GetSection(TrackingLogSettings.SectionName)
    .Get<TrackingLogSettings>() ?? new TrackingLogSettings();

// ---------------------------------------------------------------------------
// SERVICE REGISTRATION — Registration order controls startup order.
// ---------------------------------------------------------------------------

// FileTrackingLogger: Channel<T>-backed async file logger (singleton, IAsyncDisposable).
builder.Services.AddSingleton(new FileTrackingLogger(logSettings));
builder.Services.AddSingleton<ITrackingLogger>(sp => sp.GetRequiredService<FileTrackingLogger>());

// ForgeChannels: Two bounded Channel<TrackingData> instances for the pipeline.
//   Enrichment channel: PipeListener → EnrichmentPipeline (high capacity, burst absorber)
//   SqlWriter channel:  EnrichmentPipeline → SqlBulkCopyWriter (standard capacity)
builder.Services.AddSingleton(sp =>
{
    var forgeSettings = sp.GetRequiredService<IOptions<ForgeSettings>>().Value;
    return new ForgeChannels(forgeSettings.PipeChannelCapacity, forgeSettings.SqlWriterChannelCapacity);
});

// IEdgeHealthClient: HTTP bridge to the IIS Edge process.
// Base address comes from Tracking:EdgeBaseUrl (default http://127.0.0.1:6000).
builder.Services.AddHttpClient<IEdgeHealthClient, HttpEdgeHealthClient>((sp, client) =>
{
    var settings = sp.GetRequiredService<IOptions<TrackingSettings>>().Value;
    client.BaseAddress = new Uri(settings.EdgeBaseUrl ?? "http://127.0.0.1:6000");
    client.Timeout = TimeSpan.FromSeconds(5);
});

// ── Forge-specific pipeline services ──────────────────────────────────────

// PipeListenerService: Named pipe server receiving TrackingData from Edge.
builder.Services.AddHostedService<PipeListenerService>();

// EnrichmentPipelineService: Reads Enrichment channel, enriches (Phase 2: pass-through),
// writes to SqlWriter channel.
builder.Services.AddHostedService<EnrichmentPipelineService>();

// SqlBulkCopyWriterService: Drains SqlWriter channel → SqlBulkCopy → PiXL.Raw.
builder.Services.AddHostedService<SqlBulkCopyWriterService>();

// FailoverCatchupService: Scans Failover/ for JSONL files, feeds into pipeline.
builder.Services.AddHostedService<FailoverCatchupService>();

// ── Ported Worker services ────────────────────────────────────────────────

// EtlBackgroundService: PiXL.Raw → PiXL.Parsed every 60s (4-phase ETL).
builder.Services.AddHostedService<EtlBackgroundService>();

// IpApiSyncService: Xavier → IPAPI.IP delta sync (daily, 2 AM UTC).
builder.Services.AddSingleton<IpApiSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IpApiSyncService>());

// EmailNotificationService: SMTP + SMS ops notifications (singleton, not hosted).
builder.Services.AddSingleton<EmailNotificationService>();

// CompanyPiXLSyncService: Xavier → PiXL.Company/PiXL.Settings every 6h.
builder.Services.AddSingleton<CompanyPiXLSyncService>();
builder.Services.AddHostedService(sp =>
{
    var svc = sp.GetRequiredService<CompanyPiXLSyncService>();
    svc.EmailService = sp.GetService<EmailNotificationService>();
    return svc;
});

// InfraHealthService: Probes Windows services, SQL, IIS, app metrics (15s cache).
builder.Services.AddSingleton<InfraHealthService>();

// SelfHealingService: Monitors health, auto-remediates safe issues (60s loop).
builder.Services.AddSingleton<SelfHealingService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SelfHealingService>());

// MaintenanceSchedulerService: Purge (3 AM daily) + index rebuild (Sunday 4 AM).
builder.Services.AddHostedService<MaintenanceSchedulerService>();

// ---------------------------------------------------------------------------
// WINDOWS SERVICE SUPPORT
// ---------------------------------------------------------------------------
builder.Services.AddWindowsService(options => options.ServiceName = "SmartPiXL-Forge");

// ---------------------------------------------------------------------------
// BUILD + RUN
// ---------------------------------------------------------------------------
var host = builder.Build();

var logger = host.Services.GetRequiredService<ITrackingLogger>();

// Graceful shutdown: flush the file logger
host.Services.GetRequiredService<IHostApplicationLifetime>()
    .ApplicationStopping.Register(() =>
    {
        host.Services.GetRequiredService<FileTrackingLogger>()
            .DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

logger.Info("SmartPiXL Forge starting...");
logger.Info($"Pipe: {host.Services.GetRequiredService<IOptions<ForgeSettings>>().Value.PipeName}");

// Validate Edge connectivity — warn early if EdgeBaseUrl is misconfigured.
_ = Task.Run(async () =>
{
    try
    {
        var settings = host.Services.GetRequiredService<IOptions<TrackingSettings>>().Value;
        var edgeUrl = settings.EdgeBaseUrl ?? "http://127.0.0.1:6000";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var response = await http.GetAsync($"{edgeUrl.TrimEnd('/')}/health");
        if (response.IsSuccessStatusCode)
            logger.Info($"Edge health check OK ({edgeUrl})");
        else
            logger.Warning($"Edge health check returned {(int)response.StatusCode} — verify EdgeBaseUrl '{edgeUrl}'");
    }
    catch (Exception ex)
    {
        logger.Warning($"Edge unreachable: {ex.Message} — verify EdgeBaseUrl matches the running Edge process port (dev=7000, prod=6000)");
    }
});

logger.Info("SmartPiXL Forge running");

host.Run();
