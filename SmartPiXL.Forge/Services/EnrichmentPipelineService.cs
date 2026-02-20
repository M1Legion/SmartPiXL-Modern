using System.Globalization;
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
//   1. BotUaDetection  — NetCrawlerDetect bot/crawler detection
//   2. UaParsing        — UAParser + DeviceDetector.NET structured parsing
//   3. DnsLookup        — async reverse DNS (PTR) + cloud pattern detection
//   4. MaxMindGeo       — offline GeoIP2 (~1μs lookup)
//   5. IpApiLookup      — real-time IPAPI Pro for new/stale IPs (rate-limited)
//   6. WhoisAsn         — WHOIS ASN/org (supplementary, only if MaxMind empty)
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
//   Single-reader from the enrichment channel. I/O-bound enrichments (DNS, IPAPI,
//   WHOIS) are async with timeouts — pipeline never blocks indefinitely.
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

    // ── Tier 1 enrichment services ──────────────────────────────────────
    private readonly BotUaDetectionService _botDetection;
    private readonly UaParsingService _uaParsing;
    private readonly DnsLookupService _dnsLookup;
    private readonly MaxMindGeoService _maxMindGeo;
    private readonly IpApiLookupService _ipApiLookup;
    private readonly WhoisAsnService _whoisAsn;

    // ── Tier 2 enrichment services ──────────────────────────────────────
    private readonly SessionStitchingService _sessionStitching;
    private readonly CrossCustomerIntelService _crossCustomerIntel;
    private readonly DeviceAffluenceService _deviceAffluence;
    private readonly LeadQualityScoringService _leadQualityScoring;

    // ── Tier 3 enrichment services (Asymmetric Detection) ───────────────
    private readonly ContradictionMatrixService _contradictionMatrix;
    private readonly GeographicArbitrageService _geographicArbitrage;
    private readonly DeviceAgeEstimationService _deviceAgeEstimation;
    private readonly BehavioralReplayService _behavioralReplay;
    private readonly DeadInternetService _deadInternet;

    public EnrichmentPipelineService(
        ForgeChannels channels,
        IOptions<ForgeSettings> forgeSettings,
        ITrackingLogger logger,
        BotUaDetectionService botDetection,
        UaParsingService uaParsing,
        DnsLookupService dnsLookup,
        MaxMindGeoService maxMindGeo,
        IpApiLookupService ipApiLookup,
        WhoisAsnService whoisAsn,
        SessionStitchingService sessionStitching,
        CrossCustomerIntelService crossCustomerIntel,
        DeviceAffluenceService deviceAffluence,
        LeadQualityScoringService leadQualityScoring,
        ContradictionMatrixService contradictionMatrix,
        GeographicArbitrageService geographicArbitrage,
        DeviceAgeEstimationService deviceAgeEstimation,
        BehavioralReplayService behavioralReplay,
        DeadInternetService deadInternet)
    {
        _enrichmentChannel = channels.Enrichment;
        _sqlWriterChannel = channels.SqlWriter;
        _forgeSettings = forgeSettings.Value;
        _logger = logger;
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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _logger.Info($"EnrichmentPipelineService started. Enrichments enabled: {_forgeSettings.EnableEnrichments}");

        // Load IPAPI known-IP cache at startup
        if (_forgeSettings.EnableEnrichments)
        {
            await _ipApiLookup.LoadKnownIpsAsync(stoppingToken);
        }

        var reader = _enrichmentChannel.Reader;
        var processedCount = 0L;

        try
        {
            await foreach (var record in reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    var enriched = _forgeSettings.EnableEnrichments
                        ? await EnrichRecordAsync(record, stoppingToken)
                        : record;

                    if (!_sqlWriterChannel.Writer.TryWrite(enriched))
                    {
                        _logger.Warning("SQL writer channel full — dropping enriched record");
                    }
                    else
                    {
                        processedCount++;
                        if (processedCount % 10_000 == 0)
                            _logger.Info($"Enrichment pipeline: {processedCount} records processed");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Enrichment pipeline error on record: {ex.Message}");
                    // Skip failed record, continue processing
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        _logger.Info($"EnrichmentPipelineService stopped. Total processed: {processedCount}");
    }

    /// <summary>
    /// Runs the full Tier 1 enrichment chain on a single record.
    /// Each enrichment appends <c>_srv_*</c> params to the query string.
    /// Returns a new <see cref="TrackingData"/> with the enriched query string.
    /// </summary>
    private async Task<TrackingData> EnrichRecordAsync(TrackingData record, CancellationToken ct)
    {
        var qs = record.QueryString ?? string.Empty;

        // ── 1. Bot/Crawler Detection ──────────────────────────────────────
        var (isCrawler, botName) = _botDetection.Check(record.UserAgent);
        if (isCrawler)
        {
            qs = AppendParam(qs, "_srv_knownBot", "1");
            if (botName is not null)
                qs = AppendParam(qs, "_srv_botName", Uri.EscapeDataString(botName));
        }

        // ── 2. UA Parsing ─────────────────────────────────────────────────
        var uaResult = _uaParsing.Parse(record.UserAgent);
        if (uaResult.Browser is not null) qs = AppendParam(qs, "_srv_browser", Uri.EscapeDataString(uaResult.Browser));
        if (uaResult.BrowserVersion is not null) qs = AppendParam(qs, "_srv_browserVer", Uri.EscapeDataString(uaResult.BrowserVersion));
        if (uaResult.OS is not null) qs = AppendParam(qs, "_srv_os", Uri.EscapeDataString(uaResult.OS));
        if (uaResult.OSVersion is not null) qs = AppendParam(qs, "_srv_osVer", Uri.EscapeDataString(uaResult.OSVersion));
        if (uaResult.DeviceType is not null) qs = AppendParam(qs, "_srv_deviceType", Uri.EscapeDataString(uaResult.DeviceType));
        if (uaResult.DeviceModel is not null) qs = AppendParam(qs, "_srv_deviceModel", Uri.EscapeDataString(uaResult.DeviceModel));
        if (uaResult.DeviceBrand is not null) qs = AppendParam(qs, "_srv_deviceBrand", Uri.EscapeDataString(uaResult.DeviceBrand));

        // ── 3. Reverse DNS ────────────────────────────────────────────────
        var dnsResult = await _dnsLookup.LookupAsync(record.IPAddress, ct);
        if (dnsResult.Hostname is not null)
        {
            qs = AppendParam(qs, "_srv_rdns", Uri.EscapeDataString(dnsResult.Hostname));
            if (dnsResult.IsCloud)
                qs = AppendParam(qs, "_srv_rdnsCloud", "1");
        }

        // ── 4. MaxMind Geo ────────────────────────────────────────────────
        var mmResult = _maxMindGeo.Lookup(record.IPAddress);
        if (mmResult.CountryCode is not null) qs = AppendParam(qs, "_srv_mmCC", mmResult.CountryCode);
        if (mmResult.Region is not null) qs = AppendParam(qs, "_srv_mmReg", Uri.EscapeDataString(mmResult.Region));
        if (mmResult.City is not null) qs = AppendParam(qs, "_srv_mmCity", Uri.EscapeDataString(mmResult.City));
        if (mmResult.Latitude.HasValue) qs = AppendParam(qs, "_srv_mmLat", mmResult.Latitude.Value.ToString("F6", CultureInfo.InvariantCulture));
        if (mmResult.Longitude.HasValue) qs = AppendParam(qs, "_srv_mmLon", mmResult.Longitude.Value.ToString("F6", CultureInfo.InvariantCulture));
        if (mmResult.Asn.HasValue) qs = AppendParam(qs, "_srv_mmASN", mmResult.Asn.Value.ToString(CultureInfo.InvariantCulture));
        if (mmResult.AsnOrg is not null) qs = AppendParam(qs, "_srv_mmASNOrg", Uri.EscapeDataString(mmResult.AsnOrg));

        // ── 5. IPAPI Lookup (conditional — new/stale IPs only) ────────────
        var ipapiResult = await _ipApiLookup.LookupAsync(record.IPAddress, ct);
        if (ipapiResult.CountryCode is not null) qs = AppendParam(qs, "_srv_ipapiCC", ipapiResult.CountryCode);
        if (ipapiResult.Isp is not null) qs = AppendParam(qs, "_srv_ipapiISP", Uri.EscapeDataString(ipapiResult.Isp));
        if (ipapiResult.IsProxy) qs = AppendParam(qs, "_srv_ipapiProxy", "1");
        if (ipapiResult.IsMobile) qs = AppendParam(qs, "_srv_ipapiMobile", "1");
        if (ipapiResult.Reverse is not null) qs = AppendParam(qs, "_srv_ipapiReverse", Uri.EscapeDataString(ipapiResult.Reverse));
        if (ipapiResult.Asn is not null) qs = AppendParam(qs, "_srv_ipapiASN", Uri.EscapeDataString(ipapiResult.Asn));

        // ── 6. WHOIS ASN (supplementary — only if MaxMind ASN empty) ──────
        if (!mmResult.Asn.HasValue)
        {
            var whoisResult = await _whoisAsn.LookupAsync(record.IPAddress, ct);
            if (whoisResult.Asn is not null) qs = AppendParam(qs, "_srv_whoisASN", Uri.EscapeDataString(whoisResult.Asn));
            if (whoisResult.Organization is not null) qs = AppendParam(qs, "_srv_whoisOrg", Uri.EscapeDataString(whoisResult.Organization));
        }

        // ═══════════════════════════════════════════════════════════════════
        // TIER 2 — Cross-Request Intelligence (Phase 5)
        // ═══════════════════════════════════════════════════════════════════

        // ── 7. Session Stitching ──────────────────────────────────────────
        // Key: DeviceHash or canvasFP from the query string
        var deviceHash = QueryParamReader.Get(qs, "deviceHash")
                      ?? QueryParamReader.Get(qs, "canvasFP");
        var pagePath = record.RequestPath;
        var sessionResult = _sessionStitching.RecordHit(deviceHash, pagePath);
        qs = AppendParam(qs, "_srv_sessionId", sessionResult.SessionId);
        qs = AppendParam(qs, "_srv_sessionHitNum", sessionResult.HitNumber.ToString(CultureInfo.InvariantCulture));
        qs = AppendParam(qs, "_srv_sessionDurationSec", sessionResult.DurationSec.ToString(CultureInfo.InvariantCulture));
        qs = AppendParam(qs, "_srv_sessionPages", sessionResult.PageCount.ToString(CultureInfo.InvariantCulture));

        // ── 8. Cross-Customer Intelligence ────────────────────────────────
        var crossResult = _crossCustomerIntel.RecordHit(record.IPAddress, deviceHash, record.CompanyID);
        qs = AppendParam(qs, "_srv_crossCustHits", crossResult.DistinctCompanies.ToString(CultureInfo.InvariantCulture));
        qs = AppendParam(qs, "_srv_crossCustWindow", crossResult.WindowMinutes.ToString(CultureInfo.InvariantCulture));
        if (crossResult.IsAlert)
            qs = AppendParam(qs, "_srv_crossCustAlert", "1");

        // ── 9. Device Affluence ───────────────────────────────────────────
        var gpu = QueryParamReader.Get(qs, "gpu");
        var cores = QueryParamReader.GetInt(qs, "cores");
        var mem = QueryParamReader.GetInt(qs, "mem");
        var sw = QueryParamReader.GetInt(qs, "sw");
        var sh = QueryParamReader.GetInt(qs, "sh");
        var platform = QueryParamReader.Get(qs, "plt") ?? QueryParamReader.Get(qs, "uaPlatform");
        var affluenceResult = _deviceAffluence.Classify(gpu, cores, mem, sw, sh, platform);
        if (affluenceResult.Affluence is not null) qs = AppendParam(qs, "_srv_affluence", affluenceResult.Affluence);
        if (affluenceResult.GpuTierStr is not null) qs = AppendParam(qs, "_srv_gpuTier", affluenceResult.GpuTierStr);

        // ═══════════════════════════════════════════════════════════════════
        // TIER 3 — Asymmetric Detection (Phase 6)
        // ═══════════════════════════════════════════════════════════════════

        // ── 10. Contradiction Matrix ──────────────────────────────────────
        var mouseMovesRaw = QueryParamReader.GetInt(qs, "mouseMoves");
        var mouseEntropy = QueryParamReader.GetDouble(qs, "mouseEntropy");
        var touchRaw = QueryParamReader.GetInt(qs, "touch");
        var touchEventRaw = QueryParamReader.GetBool(qs, "touchEvent");
        var batteryLevel = QueryParamReader.GetDouble(qs, "batteryLevel");
        var gpuVendor = QueryParamReader.Get(qs, "gpuVendor");
        var webDriver = QueryParamReader.GetBool(qs, "webdr");
        var coresRaw = QueryParamReader.GetInt(qs, "cores");
        var memRaw = QueryParamReader.GetDouble(qs, "mem");
        var hoverRaw = QueryParamReader.GetBool(qs, "hover");
        var fonts = QueryParamReader.Get(qs, "fonts");
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
            MouseEntropy: mouseEntropy,
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
            qs = AppendParam(qs, "_srv_contradictions", contradictionResult.Count.ToString(CultureInfo.InvariantCulture));
            if (contradictionResult.FlagList is not null)
                qs = AppendParam(qs, "_srv_contradictionList", Uri.EscapeDataString(contradictionResult.FlagList));
        }

        // ── 11. Geographic Arbitrage (Cultural Consistency) ───────────────
        var country = QueryParamReader.Get(qs, "_srv_mmCC");
        var lang = QueryParamReader.Get(qs, "lang");
        var timezone = QueryParamReader.Get(qs, "tz");
        var numberFormat = QueryParamReader.Get(qs, "numberFormat");
        var tzLocale = QueryParamReader.Get(qs, "tzLocale");
        var voices = QueryParamReader.Get(qs, "voices");

        var arbitrageResult = _geographicArbitrage.Analyze(
            country, platform, fonts, lang, timezone, numberFormat, tzLocale, voices, uaResult.OS);
        qs = AppendParam(qs, "_srv_culturalScore", arbitrageResult.CulturalScore.ToString(CultureInfo.InvariantCulture));
        if (arbitrageResult.Flags is not null)
            qs = AppendParam(qs, "_srv_culturalFlags", Uri.EscapeDataString(arbitrageResult.Flags));

        // ── 12. Device Age Estimation ─────────────────────────────────────
        var isDatacenter = dnsResult.IsCloud || QueryParamReader.GetBool(qs, "_srv_rdnsCloud");
        var ageResult = _deviceAgeEstimation.Estimate(
            gpu, uaResult.OS, uaResult.OSVersion, uaResult.Browser, uaResult.BrowserVersion,
            isDatacenter, mouseEntropy);
        if (ageResult.AgeYears > 0)
            qs = AppendParam(qs, "_srv_deviceAge", ageResult.AgeYears.ToString(CultureInfo.InvariantCulture));
        if (ageResult.IsAnomaly)
            qs = AppendParam(qs, "_srv_deviceAgeAnomaly", "1");

        // ── 13. Behavioral Replay Detection ───────────────────────────────
        var mousePath = QueryParamReader.Get(qs, "mousePath");
        var replayResult = _behavioralReplay.Check(mousePath, deviceHash);
        if (replayResult.Detected)
        {
            qs = AppendParam(qs, "_srv_replayDetected", "1");
            if (replayResult.MatchFingerprint is not null)
                qs = AppendParam(qs, "_srv_replayMatchFP", Uri.EscapeDataString(replayResult.MatchFingerprint));
        }

        // ── 14. Dead Internet Index ───────────────────────────────────────
        var deadIdx = _deadInternet.RecordHit(
            record.CompanyID,
            isBotHit: isCrawler,
            hasMouseMoves: mouseMovesRaw > 0,
            isDatacenter: isDatacenter,
            contradictionCount: contradictionResult.Count,
            isReplay: replayResult.Detected,
            fingerprint: deviceHash);
        if (deadIdx > 0)
            qs = AppendParam(qs, "_srv_deadInternetIdx", deadIdx.ToString(CultureInfo.InvariantCulture));

        // ═══════════════════════════════════════════════════════════════════
        // FINAL SCORING — Lead Quality (runs last to consume Tier 3 results)
        // ═══════════════════════════════════════════════════════════════════

        // ── 15. Lead Quality Scoring ──────────────────────────────────────
        var isProxy = QueryParamReader.GetBool(qs, "_srv_ipapiProxy");
        var isMobileIp = QueryParamReader.GetBool(qs, "_srv_ipapiMobile");
        var isHosting = dnsResult.IsCloud;
        var fpAlert = QueryParamReader.GetBool(qs, "_srv_fpAlert");
        var fontCount = QueryParamReader.GetInt(qs, "fontCount");
        var canvasNoise = QueryParamReader.GetBool(qs, "canvasNoise");

        var leadSignals = new LeadQualityScoringService.LeadSignals(
            IsResidentialIp: !isProxy && !isMobileIp && !isHosting,
            HasConsistentFingerprint: !fpAlert,
            MouseEntropy: mouseEntropy,
            FontCount: fontCount,
            HasCleanCanvas: !canvasNoise,
            HasMatchingTimezone: arbitrageResult.TimezoneMatch,
            SessionHitNumber: sessionResult.HitNumber,
            IsKnownBot: isCrawler,
            ContradictionCount: contradictionResult.Count);
        var leadScore = _leadQualityScoring.Score(in leadSignals);
        qs = AppendParam(qs, "_srv_leadScore", leadScore.ToString(CultureInfo.InvariantCulture));

        // Return enriched record with updated query string
        return record with { QueryString = qs };
    }

    /// <summary>
    /// Appends a query string parameter, handling the separator correctly.
    /// </summary>
    private static string AppendParam(string qs, string key, string value)
    {
        var separator = qs.Length == 0 ? "" : "&";
        return $"{qs}{separator}{key}={value}";
    }
}
