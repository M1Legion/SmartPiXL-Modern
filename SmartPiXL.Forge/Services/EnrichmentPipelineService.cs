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
// Future phases add Tier 2-3:
//   Tier 2: Cross-customer intel, lead scoring, session stitching, affluence
//   Tier 3: Cultural arbitrage, device age, contradiction matrix, behavioral replay
//
// DESIGN:
//   Each enrichment service appends _srv_* params to TrackingData.QueryString.
//   Pipeline is sequential per record (record semantics with `with` expression).
//   Single-reader from the enrichment channel. I/O-bound enrichments (DNS, IPAPI,
//   WHOIS) are async with timeouts — pipeline never blocks indefinitely.
// ============================================================================

/// <summary>
/// Background service that reads <see cref="TrackingData"/> from the enrichment
/// channel, applies Tier 1 enrichment processing, and enqueues enriched records to
/// the SQL writer channel via <see cref="ForgeChannels"/>.
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

    public EnrichmentPipelineService(
        ForgeChannels channels,
        IOptions<ForgeSettings> forgeSettings,
        ITrackingLogger logger,
        BotUaDetectionService botDetection,
        UaParsingService uaParsing,
        DnsLookupService dnsLookup,
        MaxMindGeoService maxMindGeo,
        IpApiLookupService ipApiLookup,
        WhoisAsnService whoisAsn)
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
