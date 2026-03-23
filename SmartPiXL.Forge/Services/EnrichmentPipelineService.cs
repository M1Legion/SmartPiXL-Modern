using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Forge.Services.Enrichments;
using SmartPiXL.Models;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// ENRICHMENT PIPELINE SERVICE — Reads from the enrichment channel, applies
// enrichments, and enqueues to the SQL writer channel.
//
// ARCHITECTURE:
//   PipeListenerService     → ForgeChannels.Enrichment
//   FailoverCatchupService  → ForgeChannels.Enrichment
//       → EnrichmentPipelineService (this)
//       → ForgeChannels.SqlWriter
//       → SqlBulkCopyWriterService → PiXL.Raw
//
// ENRICHMENT CHAIN (Phase 4 — Tier 1):
//   1. BotUaDetection  — NetCrawlerDetect bot/crawler detection (inline, ~245μs)
//   2. UaParsing        — UAParser + DeviceDetector.NET structured parsing (inline, ~1050μs)
//   3. DnsLookup        — CACHE-ONLY read from Lane 3 background (inline, ~0μs)
//   4. MaxMindGeo       — offline GeoIP2 (~1μs lookup) + Lane 3 enqueue
//   5. DatacenterIp     — AWS/GCP CIDR trie detection, O(32) lookup
//   6. WhoisAsn         — CACHE-ONLY read from Lane 3 background (inline, ~0μs)
//
// ENRICHMENT CHAIN (Phase 5 — Tier 2):
//   7. FingerprintStability — per-IP fingerprint variation + volume/rate tracking
//   8. IpBehavior       — subnet /24 velocity + rapid-fire timing
//   9. SessionStitching — in-memory session graph, 30-min timeout
//  10. CrossCustomerIntel — sliding window cross-customer scraper detection
//  11. DeviceAffluence  — GPU tier + CPU/RAM/screen/platform → affluence
//
// ENRICHMENT CHAIN (Phase 6 — Tier 3: Asymmetric Detection):
//  12. ContradictionMatrix — impossible/improbable field combination rules
//  13. GeographicArbitrage — cultural fingerprint vs geo-IP consistency
//  14. DeviceAgeEstimation — GPU/OS/browser age triangulation + anomalies
//  15. BehavioralReplay — mouse path FNV-1a hashing, cross-FP replay detection
//  16. DeadInternet — per-customer bot/engagement/diversity aggregate index
//
// FINAL SCORING:
//  17. LeadQualityScoring — 0-100 score from positive human signals
//                          (runs last to consume real Tier 3 values)
//
// DESIGN:
//   Each enrichment service appends _srv_* params to TrackingData.QueryString.
//   Pipeline is sequential per record (record semantics with `with` expression).
//   Multiple concurrent workers read from the enrichment channel, each processing
//   one record at a time.
//
//   THREE-LANE ARCHITECTURE (Session 17):
//     Lane 1: 12 CPU/memory services run INLINE on pipeline workers (<5ms total)
//     Lane 2: IPAPI geo enrichment via SQL ETL (batch JOIN, already exists)
//     Lane 3: DNS/IpApi/WHOIS run in BackgroundIpEnrichmentService (off hot path)
//             Pipeline reads their caches synchronously — cache-ahead pattern.
//
//   The enrichment method is FULLY SYNCHRONOUS — no await points. This eliminates
//   async state machine overhead and ensures workers return to channel reads
//   immediately after processing each record.
// ============================================================================

/// <summary>
/// Background service that reads <see cref="TrackingData"/> from the enrichment
/// channel, applies Tier 1-3 enrichment processing (15 steps), and enqueues
/// enriched records to the SQL writer channel via <see cref="ForgeChannels"/>.
/// </summary>
public sealed class EnrichmentPipelineService : BackgroundService
{
    private readonly Channel<TrackingData> _enrichmentChannel;
    private readonly Channel<TrackingData> _sqlWriterChannel;
    private readonly ForgeSettings _forgeSettings;
    private readonly ITrackingLogger _logger;
    private readonly ForgeMetrics _metrics;

    // ── Tier 1 enrichment services ──────────────────────────────────────
    private readonly BotUaDetectionService _botDetection;
    private readonly UaParsingService _uaParsing;
    private readonly DnsLookupService _dnsLookup;
    private readonly MaxMindGeoService _maxMindGeo;
    private readonly IpRangeLookupService _ipRangeLookup;
    private readonly WhoisAsnService _whoisAsn;
    private readonly DatacenterIpService _datacenterIp;

    // ── Tier 2 enrichment services ──────────────────────────────────────
    private readonly SessionStitchingService _sessionStitching;
    private readonly CrossCustomerIntelService _crossCustomerIntel;
    private readonly DeviceAffluenceService _deviceAffluence;
    private readonly FingerprintStabilityService _fingerprintStability;
    private readonly IpBehaviorService _ipBehavior;
    private readonly LeadQualityScoringService _leadQualityScoring;

    // ── Tier 3 enrichment services (Asymmetric Detection) ───────────────
    private readonly ContradictionMatrixService _contradictionMatrix;
    private readonly GeographicArbitrageService _geographicArbitrage;
    private readonly DeviceAgeEstimationService _deviceAgeEstimation;
    private readonly BehavioralReplayService _behavioralReplay;
    private readonly DeadInternetService _deadInternet;

    // ── Lane 3 — Background IP enrichment (fire-and-forget) ─────────────
    private readonly BackgroundIpEnrichmentService _backgroundIp;

    // ── Forge failover writer (enriched records → JSONL on disk) ───────
    private readonly ForgeFailoverWriter _failoverWriter;

    // ── Adaptive worker scaling ─────────────────────────────────────────
    private volatile int _targetWorkerCount;
    private int _maxWorkers;
    private int _minWorkers;

    public EnrichmentPipelineService(
        ForgeChannels channels,
        IOptions<ForgeSettings> forgeSettings,
        ITrackingLogger logger,
        ForgeMetrics metrics,
        ForgeFailoverWriter failoverWriter,
        BotUaDetectionService botDetection,
        UaParsingService uaParsing,
        DnsLookupService dnsLookup,
        MaxMindGeoService maxMindGeo,
        IpRangeLookupService ipRangeLookup,
        WhoisAsnService whoisAsn,
        DatacenterIpService datacenterIp,
        SessionStitchingService sessionStitching,
        CrossCustomerIntelService crossCustomerIntel,
        DeviceAffluenceService deviceAffluence,
        FingerprintStabilityService fingerprintStability,
        IpBehaviorService ipBehavior,
        LeadQualityScoringService leadQualityScoring,
        ContradictionMatrixService contradictionMatrix,
        GeographicArbitrageService geographicArbitrage,
        DeviceAgeEstimationService deviceAgeEstimation,
        BehavioralReplayService behavioralReplay,
        DeadInternetService deadInternet,
        BackgroundIpEnrichmentService backgroundIp)
    {
        _enrichmentChannel = channels.Enrichment;
        _sqlWriterChannel = channels.SqlWriter;
        _forgeSettings = forgeSettings.Value;
        _logger = logger;
        _metrics = metrics;
        _failoverWriter = failoverWriter;
        _botDetection = botDetection;
        _uaParsing = uaParsing;
        _dnsLookup = dnsLookup;
        _maxMindGeo = maxMindGeo;
        _ipRangeLookup = ipRangeLookup;
        _whoisAsn = whoisAsn;
        _datacenterIp = datacenterIp;
        _sessionStitching = sessionStitching;
        _crossCustomerIntel = crossCustomerIntel;
        _deviceAffluence = deviceAffluence;
        _fingerprintStability = fingerprintStability;
        _ipBehavior = ipBehavior;
        _leadQualityScoring = leadQualityScoring;
        _contradictionMatrix = contradictionMatrix;
        _geographicArbitrage = geographicArbitrage;
        _deviceAgeEstimation = deviceAgeEstimation;
        _behavioralReplay = behavioralReplay;
        _deadInternet = deadInternet;
        _backgroundIp = backgroundIp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        // ── Adaptive worker scaling ───────────────────────────────────────
        // Start with MinEnrichmentWorkers. Monitor enrichment channel depth
        // and scale up toward MaxWorkers when backpressure builds. Scale down
        // after 30s idle. Workers with ID >= _targetWorkerCount park themselves.
        _minWorkers = Math.Max(1, _forgeSettings.MinEnrichmentWorkers);
        _maxWorkers = _forgeSettings.EffectiveMaxWorkers > 0
            ? _forgeSettings.EffectiveMaxWorkers
            : Environment.ProcessorCount;
        _targetWorkerCount = _minWorkers;

        _metrics.SampleEnrichmentChannelAlive(true);
        _logger.Info($"EnrichmentPipelineService started. Workers: {_minWorkers}-{_maxWorkers} (adaptive), Enrichments: {_forgeSettings.EnableEnrichments}");

        // Launch max workers (excess ones self-park) + 1 monitor for scaling
        var tasks = new Task[_maxWorkers + 1];
        for (var i = 0; i < _maxWorkers; i++)
        {
            var workerId = i;
            tasks[i] = Task.Run(() => RunWorkerAsync(workerId, stoppingToken), stoppingToken);
        }
        tasks[_maxWorkers] = Task.Run(() => MonitorAndScaleAsync(stoppingToken), stoppingToken);

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        _logger.Info("EnrichmentPipelineService stopped.");
    }

    /// <summary>
    /// Monitors enrichment channel depth and adjusts <see cref="_targetWorkerCount"/>.
    /// Workers with ID >= target park themselves in a 1-second sleep loop.
    /// </summary>
    private async Task MonitorAndScaleAsync(CancellationToken ct)
    {
        const int checkIntervalMs = 1000;
        const int scaleUpThreshold = 1000;    // channel depth to trigger scale-up
        const int scaleDownIdleChecks = 30;   // 30 × 1s = 30s idle before scale-down

        var consecutiveIdleChecks = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(checkIntervalMs, ct);

                var depth = _enrichmentChannel.Reader.Count;
                var current = Volatile.Read(ref _targetWorkerCount);

                _metrics.SampleEnrichmentWorkers(current, true);

                if (depth > scaleUpThreshold && current < _maxWorkers)
                {
                    var newTarget = Math.Min(current + 1, _maxWorkers);
                    Volatile.Write(ref _targetWorkerCount, newTarget);
                    consecutiveIdleChecks = 0;
                    _logger.Info($"Enrichment: scaled up to {newTarget}/{_maxWorkers} workers (channel depth: {depth:N0})");
                }
                else if (depth == 0 && current > _minWorkers)
                {
                    consecutiveIdleChecks++;
                    if (consecutiveIdleChecks >= scaleDownIdleChecks)
                    {
                        var newTarget = Math.Max(current - 1, _minWorkers);
                        Volatile.Write(ref _targetWorkerCount, newTarget);
                        consecutiveIdleChecks = 0;
                        _logger.Info($"Enrichment: scaled down to {newTarget}/{_maxWorkers} workers (idle 30s)");
                    }
                }
                else
                {
                    consecutiveIdleChecks = 0;
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// A single enrichment worker. Reads records from the shared enrichment
    /// channel, enriches each, and writes to the SQL writer channel.
    /// Workers with ID >= <see cref="_targetWorkerCount"/> park in a sleep loop
    /// until adaptive scaling activates them. Thread-safe — all enrichment
    /// services use lock-free or per-key locking.
    /// </summary>
    private async Task RunWorkerAsync(int workerId, CancellationToken ct)
    {
        var reader = _enrichmentChannel.Reader;
        var processedCount = 0L;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Adaptive parking: workers above the current target sleep
                if (workerId >= Volatile.Read(ref _targetWorkerCount))
                {
                    await Task.Delay(1000, ct);
                    continue;
                }

                // Try to read a record — non-blocking first, then wait
                if (!reader.TryRead(out var record))
                {
                    if (!await reader.WaitToReadAsync(ct))
                        break;
                    continue;
                }

                try
                {
                    var ts = ForgeMetrics.StartTimer();

                    var enriched = _forgeSettings.EnableEnrichments
                        ? EnrichRecord(record)
                        : record;

                    // Always record enrichment timing — whether record goes to
                    // channel or failover, the enrichment work was done.
                    _metrics.Record(Stage.Enrichment, ts);

                    if (!_sqlWriterChannel.Writer.TryWrite(enriched))
                    {
                        _failoverWriter.Append(enriched);                        _metrics.RecordFailover();                        _logger.Debug($"Worker {workerId}: SQL writer channel full — record persisted to failover");
                    }

                    processedCount++;
                    if (processedCount % 10_000 == 0)
                        _logger.Info($"Worker {workerId}: {processedCount:N0} records processed");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Worker {workerId}: enrichment error — {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        _logger.Info($"Worker {workerId} stopped. Total processed: {processedCount:N0}");
    }

    /// <summary>
    /// Runs the full Tier 1-3 enrichment chain on a single record.
    /// Each enrichment appends <c>_srv_*</c> params via StringBuilder (O(n) total).
    /// Cross-enrichment reads use result structs directly — no QS re-scanning.
    ///
    /// FULLY SYNCHRONOUS — no await points.
    /// DNS/IpApi/WHOIS use cache-only reads (TryGetCached). Actual I/O-bound
    /// lookups run in <see cref="BackgroundIpEnrichmentService"/> (Lane 3).
    /// First hit for a new IP misses the cache; subsequent hits are instant.
    ///
    /// Returns a new <see cref="TrackingData"/> with the enriched query string.
    /// </summary>
    private TrackingData EnrichRecord(TrackingData record)
    {
        var qs = record.QueryString ?? string.Empty;

        // Pre-sized StringBuilder for O(n) param appending.
        // Typical QS is 235 bytes + ~512 bytes of _srv_* params when all services enabled.
        var sb = new StringBuilder(qs.Length + 512);
        sb.Append(qs);

        // ── 1. Bot/Crawler Detection ──────────────────────────────────────
        var (isCrawler, botName) = _botDetection.Check(record.UserAgent);
        if (isCrawler)
        {
            AppendParam(sb, "_srv_knownBot", "1");
            if (botName is not null)
                AppendParam(sb, "_srv_botName", Uri.EscapeDataString(botName));
        }

        // ── 2. UA Parsing ─────────────────────────────────────────────────
        var uaResult = _uaParsing.Parse(record.UserAgent);
        if (uaResult.Browser is not null) AppendParam(sb, "_srv_browser", Uri.EscapeDataString(uaResult.Browser));
        if (uaResult.BrowserVersion is not null) AppendParam(sb, "_srv_browserVer", Uri.EscapeDataString(uaResult.BrowserVersion));
        if (uaResult.OS is not null) AppendParam(sb, "_srv_os", Uri.EscapeDataString(uaResult.OS));
        if (uaResult.OSVersion is not null) AppendParam(sb, "_srv_osVer", Uri.EscapeDataString(uaResult.OSVersion));
        if (uaResult.DeviceType is not null) AppendParam(sb, "_srv_deviceType", Uri.EscapeDataString(uaResult.DeviceType));
        if (uaResult.DeviceModel is not null) AppendParam(sb, "_srv_deviceModel", Uri.EscapeDataString(uaResult.DeviceModel));
        if (uaResult.DeviceBrand is not null) AppendParam(sb, "_srv_deviceBrand", Uri.EscapeDataString(uaResult.DeviceBrand));

        // ── 3. Reverse DNS (cache-only — lookups run in Lane 3 background) ──
        DnsLookupService.DnsLookupResult dnsResult = default;
        var dnsCache = _dnsLookup.TryGetCached(record.IPAddress);
        if (dnsCache.HasValue)
        {
            dnsResult = dnsCache.Value;
            if (dnsResult.Hostname is not null)
            {
                AppendParam(sb, "_srv_rdns", Uri.EscapeDataString(dnsResult.Hostname));
                if (dnsResult.IsCloud)
                    AppendParam(sb, "_srv_rdnsCloud", "1");
            }
        }

        // ── 4. IP Geo/ASN (in-memory range tables, O(log n) binary search) ──
        // Uses IpRangeLookupService (loaded from IPInfo.GeoRange + IPInfo.AsnRange).
        // Falls back to MaxMind .mmdb if range tables are empty (first run before import).
        var ipResult = _ipRangeLookup.Lookup(record.IPAddress);
        if (ipResult.CountryCode is null)
        {
            // Fallback: MaxMind .mmdb (gracefully returns default if no files present)
            var mmFallback = _maxMindGeo.Lookup(record.IPAddress);
            ipResult = new IpRangeLookupService.IpLookupResult(
                mmFallback.CountryCode, mmFallback.Region, mmFallback.City,
                mmFallback.Latitude, mmFallback.Longitude,
                mmFallback.Asn, mmFallback.AsnOrg, mmFallback.PostalCode, mmFallback.TimeZone);
        }
        if (ipResult.CountryCode is not null) AppendParam(sb, "_srv_mmCC", ipResult.CountryCode);
        if (ipResult.Region is not null) AppendParam(sb, "_srv_mmReg", Uri.EscapeDataString(ipResult.Region));
        if (ipResult.City is not null) AppendParam(sb, "_srv_mmCity", Uri.EscapeDataString(ipResult.City));
        if (ipResult.Latitude.HasValue) AppendParam(sb, "_srv_mmLat", ipResult.Latitude.Value.ToString("F6", CultureInfo.InvariantCulture));
        if (ipResult.Longitude.HasValue) AppendParam(sb, "_srv_mmLon", ipResult.Longitude.Value.ToString("F6", CultureInfo.InvariantCulture));
        if (ipResult.Asn.HasValue) AppendParam(sb, "_srv_mmASN", ipResult.Asn.Value.ToString(CultureInfo.InvariantCulture));
        if (ipResult.AsnOrg is not null) AppendParam(sb, "_srv_mmASNOrg", Uri.EscapeDataString(ipResult.AsnOrg));
        if (ipResult.PostalCode is not null) AppendParam(sb, "_srv_mmZip", ipResult.PostalCode);
        if (ipResult.TimeZone is not null) AppendParam(sb, "_srv_mmTZ", Uri.EscapeDataString(ipResult.TimeZone));

        // ── Lane 3: Fire-and-forget background IP enrichment ──────────────
        // Enqueues unique IPs for async DNS/WHOIS lookups. Non-blocking.
        // Results populate service caches for zero-latency reads on subsequent hits.
        _backgroundIp.Enqueue(record.IPAddress);

        // ── 5. Datacenter IP Detection (CIDR trie, O(32)) ────────────────
        var dcResult = _datacenterIp.Check(record.IPAddress);
        if (dcResult.IsDatacenter)
        {
            AppendParam(sb, "_srv_datacenter", "1");
            if (dcResult.Provider is not null)
                AppendParam(sb, "_srv_dcProvider", dcResult.Provider);
        }

        // ── 6. WHOIS ASN (cache-only — lookups run in Lane 3 background) ──
        if (!ipResult.Asn.HasValue && ipResult.CountryCode is not null)
        {
            var whoisCache = _whoisAsn.TryGetCached(record.IPAddress);
            if (whoisCache.HasValue)
            {
                var whoisResult = whoisCache.Value;
                if (whoisResult.Asn is not null) AppendParam(sb, "_srv_whoisASN", Uri.EscapeDataString(whoisResult.Asn));
                if (whoisResult.Organization is not null) AppendParam(sb, "_srv_whoisOrg", Uri.EscapeDataString(whoisResult.Organization));
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // TIER 2 — Cross-Request Intelligence (Phase 5)
        // ═══════════════════════════════════════════════════════════════════

        // Shared variables — extracted for downstream enrichment services.
        var deviceHash = QueryParamReader.Get(qs, "deviceHash") ?? QueryParamReader.Get(qs, "canvasFP");

        // ── 7. Fingerprint Stability (per-IP history, anti-detect detection) ──
        var canvasHash = QueryParamReader.Get(qs, "canvasFP");
        var webglHash = QueryParamReader.Get(qs, "webglFP");
        var audioHash = QueryParamReader.Get(qs, "audioFP");
        var fpResult = _fingerprintStability.RecordAndCheck(record.IPAddress, canvasHash, webglHash, audioHash);
        if (!fpResult.IsStable) AppendParam(sb, "_srv_fpStable", "0");
        AppendParam(sb, "_srv_fpUnique", fpResult.UniqueFingerprints.ToString(CultureInfo.InvariantCulture));
        if (fpResult.SuspiciousVariation) AppendParam(sb, "_srv_fpAlert", "1");
        if (fpResult.HighVolume) AppendParam(sb, "_srv_fpHighVolume", "1");
        if (fpResult.ExtremeVolume) AppendParam(sb, "_srv_fpExtremeVolume", "1");
        if (fpResult.HighRate) AppendParam(sb, "_srv_fpHighRate", "1");

        // ── 8. IP Behavior (subnet velocity + rapid-fire detection) ───────
        var ipBehaviorResult = _ipBehavior.RecordAndCheck(record.IPAddress);
        if (ipBehaviorResult.SubnetVelocityAlert)
        {
            AppendParam(sb, "_srv_subnetVelocity", "1");
            AppendParam(sb, "_srv_subnetUniqueIps", ipBehaviorResult.SubnetUniqueIps.ToString(CultureInfo.InvariantCulture));
        }
        if (ipBehaviorResult.RapidFireAlert)
            AppendParam(sb, "_srv_rapidFire", "1");
        if (ipBehaviorResult.SubSecondDuplicate)
            AppendParam(sb, "_srv_subSecondDup", "1");

        // ── 9. Session Stitching ──────────────────────────────────────────
        var sessionResult = _sessionStitching.RecordHit(deviceHash, record.RequestPath);
        AppendParam(sb, "_srv_sessionId", sessionResult.SessionId);
        AppendParam(sb, "_srv_sessionHitNum", sessionResult.HitNumber.ToString(CultureInfo.InvariantCulture));
        AppendParam(sb, "_srv_sessionDurationSec", sessionResult.DurationSec.ToString(CultureInfo.InvariantCulture));
        AppendParam(sb, "_srv_sessionPages", sessionResult.PageCount.ToString(CultureInfo.InvariantCulture));

        // ── 10. Cross-Customer Intelligence ───────────────────────────────
        var crossResult = _crossCustomerIntel.RecordHit(record.IPAddress, deviceHash, record.CompanyID?.ToString());
        AppendParam(sb, "_srv_crossCustHits", crossResult.DistinctCompanies.ToString(CultureInfo.InvariantCulture));
        AppendParam(sb, "_srv_crossCustWindow", crossResult.WindowMinutes.ToString(CultureInfo.InvariantCulture));
        if (crossResult.IsAlert)
            AppendParam(sb, "_srv_crossCustAlert", "1");

        // ── 11. Device Affluence ──────────────────────────────────────────
        // Shared device params.
        var gpu = QueryParamReader.Get(qs, "gpu");
        var cores = QueryParamReader.GetInt(qs, "cores");
        var mem = QueryParamReader.GetInt(qs, "mem");
        var sw = QueryParamReader.GetInt(qs, "sw");
        var sh = QueryParamReader.GetInt(qs, "sh");

        var platform = QueryParamReader.Get(qs, "plt") ?? QueryParamReader.Get(qs, "uaPlatform");

        var affluenceResult = _deviceAffluence.Classify(gpu, cores, mem, sw, sh, platform);
        if (affluenceResult.Affluence is not null) AppendParam(sb, "_srv_affluence", affluenceResult.Affluence);
        if (affluenceResult.GpuTierStr is not null) AppendParam(sb, "_srv_gpuTier", affluenceResult.GpuTierStr);

        // ═══════════════════════════════════════════════════════════════════
        // TIER 3 — Asymmetric Detection (Phase 6)
        // ═══════════════════════════════════════════════════════════════════

        // Shared fonts — used by ContradictionMatrix and GeographicArbitrage
        var fonts = QueryParamReader.Get(qs, "fonts");

        // ── 12. Contradiction Matrix ─────────────────────────────────────
        var mouseMovesRaw = QueryParamReader.GetInt(qs, "mouseMoves");
        var mouseEntropyRaw = QueryParamReader.GetDouble(qs, "mouseEntropy");
        var touchRaw = QueryParamReader.GetInt(qs, "touch");
        var touchEventRaw = QueryParamReader.GetBool(qs, "touchEvent");
        var batteryLevel = QueryParamReader.GetDouble(qs, "batteryLevel");
        var gpuVendor = QueryParamReader.Get(qs, "gpuVendor");
        var webDriver = QueryParamReader.GetBool(qs, "webdr");
        var coresRaw = QueryParamReader.GetInt(qs, "cores");
        var memRaw = QueryParamReader.GetDouble(qs, "mem");
        var hoverRaw = QueryParamReader.GetBool(qs, "hover");
        var isMobileUA = string.Equals(uaResult.DeviceType, "smartphone", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(uaResult.DeviceType, "tablet", StringComparison.OrdinalIgnoreCase);
        var isMacOS = platform is not null && platform.Contains("Mac", StringComparison.OrdinalIgnoreCase);
        var isLinux = platform is not null && platform.Contains("Linux", StringComparison.OrdinalIgnoreCase);
        var isWindows = platform is not null && platform.Contains("Win", StringComparison.OrdinalIgnoreCase);
        var isSafari = string.Equals(uaResult.Browser, "Safari", StringComparison.OrdinalIgnoreCase);
        var isChrome = uaResult.Browser is not null && uaResult.Browser.Contains("Chrome", StringComparison.OrdinalIgnoreCase);

        var sigSnapshot = new ContradictionMatrixService.SignalSnapshot(
            IsMobileUA: isMobileUA,
            IsMacOS: isMacOS,
            IsLinux: isLinux,
            IsWindows: isWindows,
            IsSafari: isSafari,
            IsChrome: isChrome,
            ScreenWidth: sw,
            ScreenHeight: sh,
            MouseMoves: mouseMovesRaw,
            MouseEntropy: mouseEntropyRaw,
            TouchPoints: touchRaw,
            TouchEventsSupported: touchEventRaw,
            HasBatteryAPI: batteryLevel > 0,
            GpuString: gpu,
            GpuVendor: gpuVendor,
            Platform: platform,
            Fonts: fonts,
            WebDriverDetected: webDriver,
            HardwareConcurrency: coresRaw,
            DeviceMemoryGB: memRaw,
            HoverCapable: hoverRaw);

        var contradictionResult = _contradictionMatrix.Evaluate(in sigSnapshot);
        if (contradictionResult.Count > 0)
        {
            AppendParam(sb, "_srv_contradictions", contradictionResult.Count.ToString(CultureInfo.InvariantCulture));
            if (contradictionResult.FlagList is not null)
                AppendParam(sb, "_srv_contradictionList", Uri.EscapeDataString(contradictionResult.FlagList));
        }

        // ── 13. Geographic Arbitrage (Cultural Consistency) ──────────────
        var lang = QueryParamReader.Get(qs, "lang");
        var timezone = QueryParamReader.Get(qs, "tz");
        var numberFormat = QueryParamReader.Get(qs, "numberFormat");
        var tzLocale = QueryParamReader.Get(qs, "tzLocale");
        var voices = QueryParamReader.Get(qs, "voices");

        var arbitrageResult = _geographicArbitrage.Analyze(
            ipResult.CountryCode, platform, fonts, lang, timezone, numberFormat, tzLocale, voices, uaResult.OS);
        AppendParam(sb, "_srv_culturalScore", arbitrageResult.CulturalScore.ToString(CultureInfo.InvariantCulture));
        if (arbitrageResult.Flags is not null)
            AppendParam(sb, "_srv_culturalFlags", Uri.EscapeDataString(arbitrageResult.Flags));

        // ── 14. Device Age Estimation ─────────────────────────────────────
        {
            var mouseEntropy = QueryParamReader.GetDouble(qs, "mouseEntropy");
            var ageResult = _deviceAgeEstimation.Estimate(
                gpu, uaResult.OS, uaResult.OSVersion, uaResult.Browser, uaResult.BrowserVersion,
                dnsResult.IsCloud, mouseEntropy);
            if (ageResult.AgeYears > 0)
                AppendParam(sb, "_srv_deviceAge", ageResult.AgeYears.ToString(CultureInfo.InvariantCulture));
            if (ageResult.IsAnomaly)
                AppendParam(sb, "_srv_deviceAgeAnomaly", "1");
        }

        // ── 15. Behavioral Replay Detection ──────────────────────────────
        var mousePath = QueryParamReader.Get(qs, "mousePath");
        var replayResult = _behavioralReplay.Check(mousePath, deviceHash);
        if (replayResult.Detected)
        {
            AppendParam(sb, "_srv_replayDetected", "1");
            if (replayResult.MatchFingerprint is not null)
                AppendParam(sb, "_srv_replayMatchFP", Uri.EscapeDataString(replayResult.MatchFingerprint));
        }

        // ── 16. Dead Internet Index ──────────────────────────────────────
        {
            var mouseMovesForDead = QueryParamReader.GetInt(qs, "mouseMoves");
            var deadIdx = _deadInternet.RecordHit(
                record.CompanyID?.ToString(),
                isBotHit: isCrawler,
                hasMouseMoves: mouseMovesForDead > 0,
                isDatacenter: dnsResult.IsCloud || dcResult.IsDatacenter,
                contradictionCount: contradictionResult.Count,
                isReplay: replayResult.Detected,
                fingerprint: deviceHash);
            if (deadIdx > 0)
                AppendParam(sb, "_srv_deadInternetIdx", deadIdx.ToString(CultureInfo.InvariantCulture));
        }

        // ═══════════════════════════════════════════════════════════════════
        // FINAL SCORING — Lead Quality (runs last to consume Tier 3 results)
        // ═══════════════════════════════════════════════════════════════════

        // ── 17. Lead Quality Scoring ─────────────────────────────────────
        {
            var mouseEntropy = QueryParamReader.GetDouble(qs, "mouseEntropy");
            var fontCount = QueryParamReader.GetInt(qs, "fontCount");
            var canvasNoise = QueryParamReader.GetBool(qs, "canvasNoise");

            var leadSignals = new LeadQualityScoringService.LeadSignals(
                IsResidentialIp: !dnsResult.IsCloud && !dcResult.IsDatacenter,
                HasConsistentFingerprint: !fpResult.SuspiciousVariation,
                MouseEntropy: mouseEntropy,
                FontCount: fontCount,
                HasCleanCanvas: !canvasNoise,
                HasMatchingTimezone: arbitrageResult.TimezoneMatch,
                SessionHitNumber: sessionResult.HitNumber,
                IsKnownBot: isCrawler,
                ContradictionCount: contradictionResult.Count);
            var leadScore = _leadQualityScoring.Score(in leadSignals);
            AppendParam(sb, "_srv_leadScore", leadScore.ToString(CultureInfo.InvariantCulture));
        }

        // Return enriched record with updated query string (single ToString allocation).
        if (sb.Length == qs.Length)
            return record;

        return record with { QueryString = sb.ToString() };
    }

    /// <summary>
    /// Appends a query string parameter to the StringBuilder. O(1) amortized
    /// vs the previous O(n) string interpolation per call. With 30+ params
    /// across 15 services, this changes total append cost from O(n²) to O(n).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendParam(StringBuilder sb, string key, string value)
    {
        if (sb.Length > 0) sb.Append('&');
        sb.Append(key);
        sb.Append('=');
        sb.Append(value);
    }
}
