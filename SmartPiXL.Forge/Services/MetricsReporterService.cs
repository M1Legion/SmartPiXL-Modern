using System.Diagnostics;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// METRICS REPORTER SERVICE — Periodically snapshots ForgeMetrics and logs
// the results. Runs every 10 seconds, providing a rolling performance view.
//
// Also samples channel depths each tick so the snapshot includes current
// backpressure state.
// ============================================================================

/// <summary>
/// Background service that logs <see cref="ForgeMetrics"/> snapshots every 10 seconds.
/// </summary>
public sealed class MetricsReporterService : BackgroundService
{
    private readonly ForgeMetrics _metrics;
    private readonly ForgeChannels _channels;
    private readonly BackgroundIpEnrichmentService? _bgIp;
    private readonly ITrackingLogger _logger;

    private const int ReportIntervalSeconds = 10;

    public MetricsReporterService(
        ForgeMetrics metrics,
        ForgeChannels channels,
        ITrackingLogger logger,
        BackgroundIpEnrichmentService? bgIp = null)
    {
        _metrics = metrics;
        _channels = channels;
        _logger = logger;
        _bgIp = bgIp;
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
                // Sample channel depths before snapshot
                _metrics.SampleChannelDepths(
                    _channels.Enrichment.Reader.Count,
                    _channels.SqlWriter.Reader.Count);

                // Sample Lane 3 depths
                if (_bgIp is not null)
                    _metrics.SampleBgIpDepths(_bgIp.ChannelDepth, _bgIp.DedupCacheSize);

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
}
