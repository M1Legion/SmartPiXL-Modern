using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Forge.Services;
using SmartPiXL.Forge.Services.Enrichments;
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

// ── Enrichment services ───────────────────────────────────────────────────
// Three-lane architecture (Session 17):
//   Lane 1: CPU/memory services run inline on enrichment workers (<5ms total)
//   Lane 2: IPAPI geo enrichment via SQL ETL (batch JOIN, already exists)
//   Lane 3: DNS/IpApi/WHOIS via BackgroundIpEnrichmentService (off hot path)
//
// ── Lane 1 — Tier 1 (Phase 4) — Pure-compute / memory-only ──
builder.Services.AddSingleton<BotUaDetectionService>();
builder.Services.AddSingleton<UaParsingService>();
builder.Services.AddSingleton<MaxMindGeoService>();
//
// ── Lane 1 — Tier 2 (Phase 5) — In-memory state + math ──
builder.Services.AddSingleton<SessionStitchingService>();
builder.Services.AddSingleton<CrossCustomerIntelService>();
builder.Services.AddSingleton<DeviceAffluenceService>();
builder.Services.AddSingleton<LeadQualityScoringService>();
//
// ── Lane 1 — Tier 3 (Phase 6) — Rule engines + pattern detection ──
builder.Services.AddSingleton<ContradictionMatrixService>();
builder.Services.AddSingleton<GeographicArbitrageService>();
builder.Services.AddSingleton<DeviceAgeEstimationService>();
builder.Services.AddSingleton<BehavioralReplayService>();
builder.Services.AddSingleton<DeadInternetService>();
//
// ── Lane 3 — Network I/O services (OFF hot path) ─────────────────────────
// These have seconds-level latency (DNS 2s, WHOIS 5s).
// Registered in DI but NOT called from pipeline workers. Instead:
//   1. Pipeline calls TryGetCached() — zero-latency cache read
//   2. BackgroundIpEnrichmentService runs actual lookups asynchronously
//   3. Results populate service caches for subsequent records
builder.Services.AddSingleton<DnsLookupService>();
// IpApiLookupService: DISABLED — the live Xavier system owns ip-api.com API calls.
// IPAPI.IP (344M rows) is populated by Xavier. Forge reads it via Lane 2
// (ETL.usp_EnrichParsedGeo batch JOIN). Do NOT call ip-api.com from Forge.
// builder.Services.AddSingleton<IpApiLookupService>();
builder.Services.AddSingleton<WhoisAsnService>();

// BackgroundIpEnrichmentService: Lane 3 background workers.
// Receives unique IPs from pipeline via fire-and-forget Enqueue().
// Runs DNS/IpApi/WHOIS async, populates caches for inline reads.
builder.Services.AddSingleton<BackgroundIpEnrichmentService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackgroundIpEnrichmentService>());

// ── Performance metrics ───────────────────────────────────────────────────
builder.Services.AddSingleton<ForgeMetrics>();
builder.Services.AddHostedService<MetricsReporterService>();

// ── Forge failover writer ─────────────────────────────────────────────────
// Persists enriched TrackingData to JSONL files when the SQL writer channel
// is full or the circuit breaker is open. Records are replayed on recovery.
// Separate from Edge failover (pipe unavailable) — these records are ENRICHED.
builder.Services.AddSingleton(sp =>
{
    var forgeSettings = sp.GetRequiredService<IOptions<ForgeSettings>>().Value;
    var failoverDir = Path.IsPathRooted(forgeSettings.ForgeFailoverDirectory)
        ? forgeSettings.ForgeFailoverDirectory
        : Path.Combine(AppContext.BaseDirectory, forgeSettings.ForgeFailoverDirectory);
    var logger = sp.GetRequiredService<ITrackingLogger>();
    var metrics = sp.GetRequiredService<ForgeMetrics>();
    return new ForgeFailoverWriter(failoverDir, logger, metrics);
});

// ── Forge-specific pipeline services ──────────────────────────────────────

// PipeListenerService: Named pipe server receiving TrackingData from Edge.
builder.Services.AddHostedService<PipeListenerService>();

// EnrichmentPipelineService: Reads Enrichment channel, enriches (Phase 2: pass-through),
// writes to SqlWriter channel.
builder.Services.AddHostedService<EnrichmentPipelineService>();

// SqlBulkCopyWriterService: Drains SqlWriter channel → SqlBulkCopy → PiXL.Raw.
builder.Services.AddHostedService<SqlBulkCopyWriterService>();

// FailoverCatchupService: DISABLED — isolating core pipeline first.
// builder.Services.AddHostedService<FailoverCatchupService>();

// ── Ported Worker services ────────────────────────────────────────────────

// EtlBackgroundService: DISABLED — replaced by ParsedBulkInsertService.
// builder.Services.AddHostedService<EtlBackgroundService>();

// ParsedBulkInsertService: .NET backfill for PiXL.Parsed.
// Reads Raw → parses QS in .NET (~1μs/row vs ~7ms/row in SQL UDFs)
// → BulkCopy to Parsed → calls ETL proc for Phase 9–13 only.
builder.Services.AddHostedService<ParsedBulkInsertService>();

// ── Non-essential services DISABLED ───────────────────────────────────────
// All background sync, maintenance, health, and notification services disabled
// to isolate the core Pipe → SQL pipeline. Re-enable after baseline verified.
//
// builder.Services.AddSingleton<IpApiSyncService>();
// builder.Services.AddHostedService(sp => sp.GetRequiredService<IpApiSyncService>());
//
// builder.Services.AddSingleton<EmailNotificationService>();
//
// builder.Services.AddSingleton<CompanyPiXLSyncService>();
// builder.Services.AddHostedService(sp =>
// {
//     var svc = sp.GetRequiredService<CompanyPiXLSyncService>();
//     svc.EmailService = sp.GetService<EmailNotificationService>();
//     return svc;
// });
//
// builder.Services.AddSingleton<InfraHealthService>();
//
// builder.Services.AddSingleton<SelfHealingService>();
// builder.Services.AddHostedService(sp => sp.GetRequiredService<SelfHealingService>());
//
// builder.Services.AddHostedService<MaintenanceSchedulerService>();

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
