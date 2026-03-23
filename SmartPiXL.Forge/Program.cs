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
//   - Enrichment pipeline (adaptive workers, NUMA-pinned)
//   - SqlBulkCopy writer (Channel<T> → PiXL.Parsed)
//   - Unified replay (ForgeReplayService: Edge failover + Forge failover + dead-letter)
//   - ETL identity resolution (MatchVisits + MatchLegacyVisits every 60s)
//   - IP data acquisition (IPtoASN, DB-IP Lite → IPInfo schema, daily)
//   - Company/PiXL sync (Xavier → PiXL.Company/PiXL.Settings, every 6h)
//
// NO HTTP — This is a pure Worker Service.
// Tron dashboard + Atlas portal will be served by SmartPiXL.Sentinel (Phase 10).
//
// COMMUNICATION WITH IIS EDGE:
//   Inbound:  Named pipe "SmartPiXL-Enrichment" (TrackingData JSON lines)
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

// Xavier SQL Auth rewrite — needed for CompanyPiXL sync (Xavier's SmartPiXL DB).
// IIS app pool identity can't delegate Windows Auth to the remote Xavier server,
// so use machine-level environment variables instead.
var sqlUser = Environment.GetEnvironmentVariable("SQL_USERNAME", EnvironmentVariableTarget.Machine);
var sqlPass = Environment.GetEnvironmentVariable("SQL_PASSWORD", EnvironmentVariableTarget.Machine);
if (!string.IsNullOrEmpty(sqlUser) && !string.IsNullOrEmpty(sqlPass))
{
    builder.Services.PostConfigure<TrackingSettings>(settings =>
    {
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



// ── Enrichment services ───────────────────────────────────────────────────
// Three-lane architecture (Session 17):
//   Lane 1: CPU/memory services run inline on enrichment workers (<5ms total)
//   Lane 2: IPInfo geo enrichment via SQL ETL (batch range-lookup)
//   Lane 3: DNS/WHOIS via BackgroundIpEnrichmentService (off hot path)
//
// ── Lane 1 — Tier 1 (Phase 4) — Pure-compute / memory-only ──
builder.Services.AddSingleton<BotUaDetectionService>();
builder.Services.AddSingleton<UaParsingService>();
builder.Services.AddSingleton<MaxMindGeoService>();
builder.Services.AddSingleton<IpRangeLookupService>();
//
// ── Lane 1 — Tier 2 (Phase 5) — In-memory state + math ──
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<SessionStitchingService>();
builder.Services.AddSingleton<CrossCustomerIntelService>();
builder.Services.AddSingleton<DeviceAffluenceService>();
builder.Services.AddSingleton<FingerprintStabilityService>();
builder.Services.AddSingleton<IpBehaviorService>();
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
builder.Services.AddSingleton<WhoisAsnService>();

// DatacenterIpService: Downloads AWS/GCP IP ranges at startup, refreshes weekly.
// Provides lock-free CIDR trie lookup for datacenter IP detection.
builder.Services.AddHttpClient("DatacenterIp");
builder.Services.AddSingleton<DatacenterIpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DatacenterIpService>());

// BackgroundIpEnrichmentService: Lane 3 background workers.
// Receives unique IPs from pipeline via fire-and-forget Enqueue().
// Runs DNS/WHOIS async, populates caches for inline reads.
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

// SqlBulkCopyWriterService: Drains SqlWriter channel → parse inline → SqlBulkCopy → PiXL.Parsed.
builder.Services.AddHostedService<SqlBulkCopyWriterService>();

// ForgeReplayService: Unified replay for Edge failover + Forge failover + dead-letter.
// Scans directories on startup and periodically, routes records appropriately.
builder.Services.AddHostedService<ForgeReplayService>();

// ── Ported Worker services ────────────────────────────────────────────────

// EtlBackgroundService: Runs identity resolution (usp_MatchVisits + usp_MatchLegacyVisits)
// every 60 seconds. Parsing is handled by ParsedBulkInsertService.
builder.Services.AddHostedService<EtlBackgroundService>();

// ParsedBulkInsertService: DISABLED — The Forge now writes directly to PiXL.Parsed
// via SqlBulkCopyWriterService (merged pipeline). The two-step Raw → Parsed pipeline
// is eliminated. This service can be re-enabled for backfilling historical data.
// builder.Services.AddHostedService<ParsedBulkInsertService>();

// ── IP Data Acquisition ───────────────────────────────────────────────────
// Downloads free public IP data (IPtoASN, DB-IP Lite, etc.) and imports into
// the IPInfo SQL schema. Runs daily at the configured UTC hour.
builder.Services.AddHttpClient<IpDataAcquisitionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IpDataAcquisitionService>());

// ── Company sync ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<CompanyPiXLSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CompanyPiXLSyncService>());

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

// Pre-load IPInfo range tables into memory for fast IP lookups.
var ipRangeLookup = host.Services.GetRequiredService<IpRangeLookupService>();
await ipRangeLookup.LoadAsync(CancellationToken.None);

logger.Info($"Pipe: {host.Services.GetRequiredService<IOptions<ForgeSettings>>().Value.PipeName}");

// NUMA node pinning — isolate Forge to a single NUMA node for cache locality.
var forgeSettings = host.Services.GetRequiredService<IOptions<ForgeSettings>>().Value;
if (forgeSettings.NumaNode >= 0)
{
    var lpCount = NumaHelper.PinToNumaNode(forgeSettings.NumaNode, logger, forgeSettings.RamPerNumaNodeGB);
    if (lpCount > 0)
    {
        forgeSettings.NumaLogicalProcessors = lpCount;
        if (forgeSettings.EffectiveMaxWorkers < forgeSettings.EnrichmentWorkerCount)
            logger.Info($"NUMA: EffectiveMaxWorkers capped to {forgeSettings.EffectiveMaxWorkers} (node {forgeSettings.NumaNode} has {lpCount} LPs, config requested {forgeSettings.EnrichmentWorkerCount})");
    }
}

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

await host.RunAsync();
