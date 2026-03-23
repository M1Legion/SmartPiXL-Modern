using System.Diagnostics;
using SmartPiXL.Forge.Services.Enrichments;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// METRICS REPORTER SERVICE — Periodically snapshots ForgeMetrics and logs
// the results. Runs every 10 seconds, providing a rolling performance view.
//
// Also samples channel depths and health state each tick so the snapshot
// includes current backpressure state and the health tree stays fresh.
// ============================================================================

/// <summary>
/// Background service that logs <see cref="ForgeMetrics"/> snapshots every 10 seconds.
/// Also acts as the central health sampler — reads cache sizes, circuit state,
/// and disk health from other services and pushes them into ForgeMetrics.
/// </summary>
public sealed class MetricsReporterService : BackgroundService
{
    private readonly ForgeMetrics _metrics;
    private readonly ForgeChannels _channels;
    private readonly ITrackingLogger _logger;

    // Services sampled for health tree
    private readonly BackgroundIpEnrichmentService? _bgIp;
    private readonly SqlBulkCopyWriterService? _sqlWriter;
    private readonly ForgeFailoverWriter? _failoverWriter;
    private readonly UaParsingService? _uaParsing;
    private readonly BotUaDetectionService? _botDetection;
    private readonly DnsLookupService? _dns;
    private readonly WhoisAsnService? _whois;
    private readonly MaxMindGeoService? _maxMind;
    private readonly DeadInternetService? _deadInternet;
    private readonly BehavioralReplayService? _behavioralReplay;
    private readonly CrossCustomerIntelService? _crossCustomer;
    private readonly SessionStitchingService? _sessionStitching;

    private const int ReportIntervalSeconds = 10;

    public MetricsReporterService(
        ForgeMetrics metrics,
        ForgeChannels channels,
        ITrackingLogger logger,
        BackgroundIpEnrichmentService? bgIp = null,
        SqlBulkCopyWriterService? sqlWriter = null,
        ForgeFailoverWriter? failoverWriter = null,
        UaParsingService? uaParsing = null,
        BotUaDetectionService? botDetection = null,
        DnsLookupService? dns = null,
        WhoisAsnService? whois = null,
        MaxMindGeoService? maxMind = null,
        DeadInternetService? deadInternet = null,
        BehavioralReplayService? behavioralReplay = null,
        CrossCustomerIntelService? crossCustomer = null,
        SessionStitchingService? sessionStitching = null)
    {
        _metrics = metrics;
        _channels = channels;
        _logger = logger;
        _bgIp = bgIp;
        _sqlWriter = sqlWriter;
        _failoverWriter = failoverWriter;
        _uaParsing = uaParsing;
        _botDetection = botDetection;
        _dns = dns;
        _whois = whois;
        _maxMind = maxMind;
        _deadInternet = deadInternet;
        _behavioralReplay = behavioralReplay;
        _crossCustomer = crossCustomer;
        _sessionStitching = sessionStitching;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _logger.Info($"MetricsReporterService started. Reporting every {ReportIntervalSeconds}s.");

        // Let pipeline services start before first report
        await Task.Delay(TimeSpan.FromSeconds(ReportIntervalSeconds), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // ── Windowed metrics sampling ──────────────────────────────
                _metrics.SampleChannelDepths(
                    _channels.Enrichment.Reader.Count,
                    _channels.SqlWriter.Reader.Count);

                if (_bgIp is not null)
                    _metrics.SampleBgIpDepths(_bgIp.ChannelDepth, _bgIp.DedupCacheSize);

                // ── Health tree sampling ────────────────────────────────────
                SampleHealthState();

                var snapshot = _metrics.Snapshot();

                // Only log if there was any activity
                if (snapshot.PipeCount > 0 || snapshot.EnrichCount > 0 || snapshot.SqlCount > 0)
                {
                    _logger.Info(snapshot.Format(ReportIntervalSeconds));
                }
                else
                {
                    _logger.Debug("Forge metrics: no activity in last window");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"MetricsReporter error: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(ReportIntervalSeconds), stoppingToken);
        }
    }

    /// <summary>
    /// Pushes current service state into ForgeMetrics for health tree derivation.
    /// Called every 10 seconds from the main loop.
    /// </summary>
    private void SampleHealthState()
    {
        // F2: Enrichment cache sizes
        _metrics.SampleEnrichmentCaches(
            _uaParsing?.CacheCount ?? 0,
            _botDetection?.CacheCount ?? 0,
            _dns?.CacheCount ?? 0,
            _whois?.CacheCount ?? 0,
            _maxMind?.CacheCount ?? 0,
            _deadInternet?.CacheCount ?? 0,
            _behavioralReplay?.CacheCount ?? 0,
            _crossCustomer?.CacheCount ?? 0,
            _sessionStitching?.CacheCount ?? 0);

        // F3: SQL writer lifetime health
        if (_sqlWriter is not null)
            _metrics.RecordSqlWriteHealth(_sqlWriter.LifetimeBatches, _sqlWriter.LifetimeFailures);

        // F4: Failover disk health
        if (_failoverWriter is not null)
            _metrics.SampleFailoverDiskWritable(_failoverWriter.DiskHealthy);

        // F6: Background IP processing state
        if (_bgIp is not null)
            _metrics.SampleBgIpProcessingState(_bgIp.DnsEnabled, _bgIp.WhoisEnabled);
    }
}
