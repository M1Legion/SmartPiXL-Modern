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
//   5. IpApiLookup      — CACHE-ONLY read from Lane 3 background (inline, ~0μs)
//   6. WhoisAsn         — CACHE-ONLY read from Lane 3 background (inline, ~0μs)
//
// ENRICHMENT CHAIN (Phase 5 — Tier 2):
//   7. SessionStitching — in-memory session graph, 30-min timeout
//   8. CrossCustomerIntel — sliding window cross-customer scraper detection
//   9. DeviceAffluence  — GPU tier + CPU/RAM/screen/platform → affluence
//
// ENRICHMENT CHAIN (Phase 6 — Tier 3: Asymmetric Detection):
//  10. ContradictionMatrix — impossible/improbable field combination rules
//  11. GeographicArbitrage — cultural fingerprint vs geo-IP consistency
//  12. DeviceAgeEstimation — GPU/OS/browser age triangulation + anomalies
//  13. BehavioralReplay — mouse path FNV-1a hashing, cross-FP replay detection
//  14. DeadInternet — per-customer bot/engagement/diversity aggregate index
//
// FINAL SCORING:
//  15. LeadQualityScoring — 0-100 score from positive human signals
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
    private readonly BotUaDetectionService? _botDetection;
    private readonly UaParsingService? _uaParsing;
    private readonly DnsLookupService? _dnsLookup;
    private readonly MaxMindGeoService? _maxMindGeo;
    private readonly IpApiLookupService? _ipApiLookup;
    private readonly WhoisAsnService? _whoisAsn;

    // ── Tier 2 enrichment services ──────────────────────────────────────
    private readonly SessionStitchingService? _sessionStitching;
    private readonly CrossCustomerIntelService? _crossCustomerIntel;
    private readonly DeviceAffluenceService? _deviceAffluence;
    private readonly LeadQualityScoringService? _leadQualityScoring;

    // ── Tier 3 enrichment services (Asymmetric Detection) ───────────────
    private readonly ContradictionMatrixService? _contradictionMatrix;
    private readonly GeographicArbitrageService? _geographicArbitrage;
    private readonly DeviceAgeEstimationService? _deviceAgeEstimation;
    private readonly BehavioralReplayService? _behavioralReplay;
    private readonly DeadInternetService? _deadInternet;

    // ── Lane 3 — Background IP enrichment (fire-and-forget) ─────────────
    private readonly BackgroundIpEnrichmentService? _backgroundIp;

    // ── Constructor-computed flags — skip shared variable extraction ─────
    // When all consumers of a shared variable are null, we skip the
    // QueryParamReader scan entirely. Zero cost for disabled services.
    private readonly bool _needDeviceHash;
    private readonly bool _needDeviceParams;
    private readonly bool _needPlatform;
    private readonly bool _needFonts;

    public EnrichmentPipelineService(
        ForgeChannels channels,
        IOptions<ForgeSettings> forgeSettings,
        ITrackingLogger logger,
        ForgeMetrics metrics,
        BotUaDetectionService? botDetection = null,
        UaParsingService? uaParsing = null,
        DnsLookupService? dnsLookup = null,
        MaxMindGeoService? maxMindGeo = null,
        IpApiLookupService? ipApiLookup = null,
        WhoisAsnService? whoisAsn = null,
        SessionStitchingService? sessionStitching = null,
        CrossCustomerIntelService? crossCustomerIntel = null,
        DeviceAffluenceService? deviceAffluence = null,
        LeadQualityScoringService? leadQualityScoring = null,
        ContradictionMatrixService? contradictionMatrix = null,
        GeographicArbitrageService? geographicArbitrage = null,
        DeviceAgeEstimationService? deviceAgeEstimation = null,
        BehavioralReplayService? behavioralReplay = null,
        DeadInternetService? deadInternet = null,
        BackgroundIpEnrichmentService? backgroundIp = null)
    {
        _enrichmentChannel = channels.Enrichment;
        _sqlWriterChannel = channels.SqlWriter;
        _forgeSettings = forgeSettings.Value;
        _logger = logger;
        _metrics = metrics;
        _botDetection = botDetection;
        _uaParsing = uaParsing;
        _dnsLookup = dnsLookup;
        _maxMindGeo = maxMindGeo;
        _ipApiLookup = ipApiLookup;
        _whoisAsn = whoisAsn;
        _sessionStitching = sessionStitching;
        _crossCustomerIntel = crossCustomerIntel;
        _deviceAffluence = deviceAffluence;
        _leadQualityScoring = leadQualityScoring;
        _contradictionMatrix = contradictionMatrix;
        _geographicArbitrage = geographicArbitrage;
        _deviceAgeEstimation = deviceAgeEstimation;
        _behavioralReplay = behavioralReplay;
        _deadInternet = deadInternet;
        _backgroundIp = backgroundIp;

        // Pre-compute which shared variables are needed — set once, read every record.
        // Avoids 10+ pointless QueryParamReader scans when downstream services are null.
        _needDeviceHash = sessionStitching is not null || crossCustomerIntel is not null
                       || behavioralReplay is not null || deadInternet is not null;
        _needDeviceParams = deviceAffluence is not null || contradictionMatrix is not null
                         || deviceAgeEstimation is not null;
        _needPlatform = deviceAffluence is not null || contradictionMatrix is not null
                     || geographicArbitrage is not null;
        _needFonts = contradictionMatrix is not null || geographicArbitrage is not null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var workerCount = _forgeSettings.EnrichmentWorkerCount > 0
            ? _forgeSettings.EnrichmentWorkerCount
            : 8; // Default: 8 concurrent enrichment workers

        _logger.Info($"EnrichmentPipelineService started. Workers: {workerCount}, Enrichments enabled: {_forgeSettings.EnableEnrichments}");

        // IPAPI cache load is now a no-op — we use inline SQL checks instead
        // of loading 344M rows into memory. See IpApiLookupService.IsKnownInSqlAsync.
        if (_forgeSettings.EnableEnrichments && _ipApiLookup is not null)
        {
            await _ipApiLookup.LoadKnownIpsAsync(stoppingToken);
        }

        // Launch N concurrent workers. Each reads from the enrichment channel
        // (Channel<T> supports concurrent readers), enriches one record at a time,
        // and writes to the SQL writer channel. This overlaps I/O waits (DNS, IPAPI)
        // across records rather than waiting sequentially.
        var tasks = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            var workerId = i;
            tasks[i] = Task.Run(() => RunWorkerAsync(workerId, stoppingToken), stoppingToken);
        }

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
    /// A single enrichment worker. Reads records from the shared enrichment
    /// channel, enriches each, and writes to the SQL writer channel.
    /// Multiple instances run concurrently — all enrichment services are thread-safe.
    /// </summary>
    private async Task RunWorkerAsync(int workerId, CancellationToken ct)
    {
        var reader = _enrichmentChannel.Reader;
        var processedCount = 0L;

        try
        {
            await foreach (var record in reader.ReadAllAsync(ct))
            {
                try
                {
                    var ts = ForgeMetrics.StartTimer();

                    // Fully synchronous enrichment — no await points.
                    // DNS/IpApi/WHOIS use cache-only reads (TryGetCached).
                    // Background workers populate caches asynchronously (Lane 3).
                    var enriched = _forgeSettings.EnableEnrichments
                        ? EnrichRecord(record)
                        : record;

                    if (!_sqlWriterChannel.Writer.TryWrite(enriched))
                    {
                        _metrics.RecordDrop(Stage.Enrichment);
                        _logger.Warning($"Worker {workerId}: SQL writer channel full — dropping enriched record");
                    }
                    else
                    {
                        _metrics.Record(Stage.Enrichment, ts);
                        processedCount++;
                        if (processedCount % 10_000 == 0)
                            _logger.Info($"Worker {workerId}: {processedCount:N0} records processed");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Worker {workerId}: enrichment error — {ex.Message}");
                    // Skip failed record, continue processing
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
    /// Shared variable extraction is gated by constructor-computed boolean flags.
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

        // Shared state across enrichments — defaults when service is null
        var isCrawler = false;
        string? botName = null;

        // ── 1. Bot/Crawler Detection ──────────────────────────────────────
        if (_botDetection is not null)
        {
            (isCrawler, botName) = _botDetection.Check(record.UserAgent);
            if (isCrawler)
            {
                AppendParam(sb, "_srv_knownBot", "1");
                if (botName is not null)
                    AppendParam(sb, "_srv_botName", Uri.EscapeDataString(botName));
            }
        }

        // ── 2. UA Parsing ─────────────────────────────────────────────────
        UaParsingService.UaParseResult uaResult = default;
        if (_uaParsing is not null)
        {
            uaResult = _uaParsing.Parse(record.UserAgent);
            if (uaResult.Browser is not null) AppendParam(sb, "_srv_browser", Uri.EscapeDataString(uaResult.Browser));
            if (uaResult.BrowserVersion is not null) AppendParam(sb, "_srv_browserVer", Uri.EscapeDataString(uaResult.BrowserVersion));
            if (uaResult.OS is not null) AppendParam(sb, "_srv_os", Uri.EscapeDataString(uaResult.OS));
            if (uaResult.OSVersion is not null) AppendParam(sb, "_srv_osVer", Uri.EscapeDataString(uaResult.OSVersion));
            if (uaResult.DeviceType is not null) AppendParam(sb, "_srv_deviceType", Uri.EscapeDataString(uaResult.DeviceType));
            if (uaResult.DeviceModel is not null) AppendParam(sb, "_srv_deviceModel", Uri.EscapeDataString(uaResult.DeviceModel));
            if (uaResult.DeviceBrand is not null) AppendParam(sb, "_srv_deviceBrand", Uri.EscapeDataString(uaResult.DeviceBrand));
        }

        // ── 3. Reverse DNS (cache-only — lookups run in Lane 3 background) ──
        DnsLookupService.DnsLookupResult dnsResult = default;
        if (_dnsLookup is not null)
        {
            var cached = _dnsLookup.TryGetCached(record.IPAddress);
            if (cached.HasValue)
            {
                dnsResult = cached.Value;
                if (dnsResult.Hostname is not null)
                {
                    AppendParam(sb, "_srv_rdns", Uri.EscapeDataString(dnsResult.Hostname));
                    if (dnsResult.IsCloud)
                        AppendParam(sb, "_srv_rdnsCloud", "1");
                }
            }
        }

        // ── 4. MaxMind Geo ────────────────────────────────────────────────
        MaxMindGeoService.MaxMindResult mmResult = default;
        if (_maxMindGeo is not null)
        {
            mmResult = _maxMindGeo.Lookup(record.IPAddress);
            if (mmResult.CountryCode is not null) AppendParam(sb, "_srv_mmCC", mmResult.CountryCode);
            if (mmResult.Region is not null) AppendParam(sb, "_srv_mmReg", Uri.EscapeDataString(mmResult.Region));
            if (mmResult.City is not null) AppendParam(sb, "_srv_mmCity", Uri.EscapeDataString(mmResult.City));
            if (mmResult.Latitude.HasValue) AppendParam(sb, "_srv_mmLat", mmResult.Latitude.Value.ToString("F6", CultureInfo.InvariantCulture));
            if (mmResult.Longitude.HasValue) AppendParam(sb, "_srv_mmLon", mmResult.Longitude.Value.ToString("F6", CultureInfo.InvariantCulture));
            if (mmResult.Asn.HasValue) AppendParam(sb, "_srv_mmASN", mmResult.Asn.Value.ToString(CultureInfo.InvariantCulture));
            if (mmResult.AsnOrg is not null) AppendParam(sb, "_srv_mmASNOrg", Uri.EscapeDataString(mmResult.AsnOrg));
            if (mmResult.PostalCode is not null) AppendParam(sb, "_srv_mmZip", mmResult.PostalCode);
            if (mmResult.TimeZone is not null) AppendParam(sb, "_srv_mmTZ", Uri.EscapeDataString(mmResult.TimeZone));
        }

        // ── Lane 3: Fire-and-forget background IP enrichment ──────────────
        // Enqueues unique IPs for async DNS/IpApi/WHOIS lookups. Non-blocking.
        // Results populate service caches for zero-latency reads on subsequent hits.
        _backgroundIp?.Enqueue(record.IPAddress);

        // ── 5. IPAPI Lookup (cache-only — lookups run in Lane 3 background) ──
        // Result hoisted to method scope so LeadQualityScoring (#15) can
        // read IsProxy/IsMobile directly instead of re-scanning the QS.
        IpApiLookupService.IpApiResult ipapiResult = default;
        if (_ipApiLookup is not null)
        {
            var cached = _ipApiLookup.TryGetCached(record.IPAddress);
            if (cached.HasValue)
            {
                ipapiResult = cached.Value;
                if (ipapiResult.CountryCode is not null) AppendParam(sb, "_srv_ipapiCC", ipapiResult.CountryCode);
                if (ipapiResult.Isp is not null) AppendParam(sb, "_srv_ipapiISP", Uri.EscapeDataString(ipapiResult.Isp));
                if (ipapiResult.IsProxy) AppendParam(sb, "_srv_ipapiProxy", "1");
                if (ipapiResult.IsMobile) AppendParam(sb, "_srv_ipapiMobile", "1");
                if (ipapiResult.Reverse is not null) AppendParam(sb, "_srv_ipapiReverse", Uri.EscapeDataString(ipapiResult.Reverse));
                if (ipapiResult.Asn is not null) AppendParam(sb, "_srv_ipapiASN", Uri.EscapeDataString(ipapiResult.Asn));
            }
        }

        // ── 6. WHOIS ASN (cache-only — lookups run in Lane 3 background) ──
        if (_whoisAsn is not null && !mmResult.Asn.HasValue && mmResult.CountryCode is not null)
        {
            var cached = _whoisAsn.TryGetCached(record.IPAddress);
            if (cached.HasValue)
            {
                var whoisResult = cached.Value;
                if (whoisResult.Asn is not null) AppendParam(sb, "_srv_whoisASN", Uri.EscapeDataString(whoisResult.Asn));
                if (whoisResult.Organization is not null) AppendParam(sb, "_srv_whoisOrg", Uri.EscapeDataString(whoisResult.Organization));
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // TIER 2 — Cross-Request Intelligence (Phase 5)
        // ═══════════════════════════════════════════════════════════════════

        // Shared variables — extracted ONLY when downstream services need them.
        // Controlled by constructor-computed boolean flags to avoid pointless
        // QueryParamReader scans (each scan = IndexOf on the full QS string).
        string? deviceHash = null;
        if (_needDeviceHash)
            deviceHash = QueryParamReader.Get(qs, "deviceHash") ?? QueryParamReader.Get(qs, "canvasFP");

        // ── 7. Session Stitching ──────────────────────────────────────────
        SessionStitchingService.SessionResult sessionResult = default;
        if (_sessionStitching is not null)
        {
            sessionResult = _sessionStitching.RecordHit(deviceHash, record.RequestPath);
            AppendParam(sb, "_srv_sessionId", sessionResult.SessionId);
            AppendParam(sb, "_srv_sessionHitNum", sessionResult.HitNumber.ToString(CultureInfo.InvariantCulture));
            AppendParam(sb, "_srv_sessionDurationSec", sessionResult.DurationSec.ToString(CultureInfo.InvariantCulture));
            AppendParam(sb, "_srv_sessionPages", sessionResult.PageCount.ToString(CultureInfo.InvariantCulture));
        }

        // ── 8. Cross-Customer Intelligence ────────────────────────────────
        if (_crossCustomerIntel is not null)
        {
            var crossResult = _crossCustomerIntel.RecordHit(record.IPAddress, deviceHash, record.CompanyID);
            AppendParam(sb, "_srv_crossCustHits", crossResult.DistinctCompanies.ToString(CultureInfo.InvariantCulture));
            AppendParam(sb, "_srv_crossCustWindow", crossResult.WindowMinutes.ToString(CultureInfo.InvariantCulture));
            if (crossResult.IsAlert)
                AppendParam(sb, "_srv_crossCustAlert", "1");
        }

        // ── 9. Device Affluence ───────────────────────────────────────────
        // Shared device params — extracted only when consuming services registered.
        string? gpu = null;
        int cores = 0, mem = 0, sw = 0, sh = 0;
        if (_needDeviceParams)
        {
            gpu = QueryParamReader.Get(qs, "gpu");
            cores = QueryParamReader.GetInt(qs, "cores");
            mem = QueryParamReader.GetInt(qs, "mem");
            sw = QueryParamReader.GetInt(qs, "sw");
            sh = QueryParamReader.GetInt(qs, "sh");
        }

        string? platform = null;
        if (_needPlatform)
            platform = QueryParamReader.Get(qs, "plt") ?? QueryParamReader.Get(qs, "uaPlatform");

        if (_deviceAffluence is not null)
        {
            var affluenceResult = _deviceAffluence.Classify(gpu, cores, mem, sw, sh, platform);
            if (affluenceResult.Affluence is not null) AppendParam(sb, "_srv_affluence", affluenceResult.Affluence);
            if (affluenceResult.GpuTierStr is not null) AppendParam(sb, "_srv_gpuTier", affluenceResult.GpuTierStr);
        }

        // ═══════════════════════════════════════════════════════════════════
        // TIER 3 — Asymmetric Detection (Phase 6)
        // ═══════════════════════════════════════════════════════════════════

        // Shared fonts — used by ContradictionMatrix and GeographicArbitrage
        string? fonts = null;
        if (_needFonts)
            fonts = QueryParamReader.Get(qs, "fonts");

        // ── 10. Contradiction Matrix ──────────────────────────────────────
        ContradictionMatrixService.ContradictionResult contradictionResult = default;
        if (_contradictionMatrix is not null)
        {
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

            contradictionResult = _contradictionMatrix.Evaluate(in sigSnapshot);
            if (contradictionResult.Count > 0)
            {
                AppendParam(sb, "_srv_contradictions", contradictionResult.Count.ToString(CultureInfo.InvariantCulture));
                if (contradictionResult.FlagList is not null)
                    AppendParam(sb, "_srv_contradictionList", Uri.EscapeDataString(contradictionResult.FlagList));
            }
        }

        // ── 11. Geographic Arbitrage (Cultural Consistency) ───────────────
        GeographicArbitrageService.ArbitrageResult arbitrageResult = default;
        if (_geographicArbitrage is not null)
        {
            // Use mmResult.CountryCode directly — avoids re-scanning QS for _srv_mmCC
            var lang = QueryParamReader.Get(qs, "lang");
            var timezone = QueryParamReader.Get(qs, "tz");
            var numberFormat = QueryParamReader.Get(qs, "numberFormat");
            var tzLocale = QueryParamReader.Get(qs, "tzLocale");
            var voices = QueryParamReader.Get(qs, "voices");

            arbitrageResult = _geographicArbitrage.Analyze(
                mmResult.CountryCode, platform, fonts, lang, timezone, numberFormat, tzLocale, voices, uaResult.OS);
            AppendParam(sb, "_srv_culturalScore", arbitrageResult.CulturalScore.ToString(CultureInfo.InvariantCulture));
            if (arbitrageResult.Flags is not null)
                AppendParam(sb, "_srv_culturalFlags", Uri.EscapeDataString(arbitrageResult.Flags));
        }

        // ── 12. Device Age Estimation ─────────────────────────────────────
        if (_deviceAgeEstimation is not null)
        {
            var mouseEntropy = QueryParamReader.GetDouble(qs, "mouseEntropy");
            // Use dnsResult.IsCloud directly — avoids re-scanning QS for _srv_rdnsCloud
            var ageResult = _deviceAgeEstimation.Estimate(
                gpu, uaResult.OS, uaResult.OSVersion, uaResult.Browser, uaResult.BrowserVersion,
                dnsResult.IsCloud, mouseEntropy);
            if (ageResult.AgeYears > 0)
                AppendParam(sb, "_srv_deviceAge", ageResult.AgeYears.ToString(CultureInfo.InvariantCulture));
            if (ageResult.IsAnomaly)
                AppendParam(sb, "_srv_deviceAgeAnomaly", "1");
        }

        // ── 13. Behavioral Replay Detection ───────────────────────────────
        BehavioralReplayService.ReplayResult replayResult = default;
        if (_behavioralReplay is not null)
        {
            var mousePath = QueryParamReader.Get(qs, "mousePath");
            replayResult = _behavioralReplay.Check(mousePath, deviceHash);
            if (replayResult.Detected)
            {
                AppendParam(sb, "_srv_replayDetected", "1");
                if (replayResult.MatchFingerprint is not null)
                    AppendParam(sb, "_srv_replayMatchFP", Uri.EscapeDataString(replayResult.MatchFingerprint));
            }
        }

        // ── 14. Dead Internet Index ───────────────────────────────────────
        if (_deadInternet is not null)
        {
            var mouseMovesForDead = QueryParamReader.GetInt(qs, "mouseMoves");
            // Use dnsResult.IsCloud directly — avoids re-scanning QS for _srv_rdnsCloud
            var deadIdx = _deadInternet.RecordHit(
                record.CompanyID,
                isBotHit: isCrawler,
                hasMouseMoves: mouseMovesForDead > 0,
                isDatacenter: dnsResult.IsCloud,
                contradictionCount: contradictionResult.Count,
                isReplay: replayResult.Detected,
                fingerprint: deviceHash);
            if (deadIdx > 0)
                AppendParam(sb, "_srv_deadInternetIdx", deadIdx.ToString(CultureInfo.InvariantCulture));
        }

        // ═══════════════════════════════════════════════════════════════════
        // FINAL SCORING — Lead Quality (runs last to consume Tier 3 results)
        // ═══════════════════════════════════════════════════════════════════

        // ── 15. Lead Quality Scoring ──────────────────────────────────────
        if (_leadQualityScoring is not null)
        {
            var mouseEntropy = QueryParamReader.GetDouble(qs, "mouseEntropy");
            // Use result structs directly — no QS re-scanning for _srv_* params
            var fpAlert = QueryParamReader.GetBool(qs, "_srv_fpAlert");
            var fontCount = QueryParamReader.GetInt(qs, "fontCount");
            var canvasNoise = QueryParamReader.GetBool(qs, "canvasNoise");

            var leadSignals = new LeadQualityScoringService.LeadSignals(
                IsResidentialIp: !ipapiResult.IsProxy && !ipapiResult.IsMobile && !dnsResult.IsCloud,
                HasConsistentFingerprint: !fpAlert,
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

        // Return enriched record with updated query string (single ToString allocation)
        // If nothing was appended — skip ToString() + record copy (zero-alloc fast path).
        // With only BotUaDetection enabled, ~95% of records take this path.
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
