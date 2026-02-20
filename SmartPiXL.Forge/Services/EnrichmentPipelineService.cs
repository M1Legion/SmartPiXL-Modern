using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
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
// CURRENT STATE (Phase 2):
//   Placeholder enrichment chain — records pass through unmodified.
//   Phases 4-6 will add Tier 1-3 enrichment services that are wired into
//   the chain here:
//     Tier 1: IPAPI, UAParser, NetCrawlerDetect, DeviceDetector, DNS, MaxMind, WHOIS
//     Tier 2: Cross-customer intel, lead scoring, session stitching, affluence
//     Tier 3: Cultural arbitrage, device age, contradiction matrix, behavioral replay
//
// DESIGN:
//   Single-reader from the enrichment channel. Processes records sequentially
//   in the current phase. Future phases may add parallelism for I/O-bound
//   enrichments (e.g., DNS lookups, WHOIS queries).
// ============================================================================

/// <summary>
/// Background service that reads <see cref="TrackingData"/> from the enrichment
/// channel, applies enrichment processing, and enqueues enriched records to
/// the SQL writer channel via <see cref="ForgeChannels"/>.
/// <para>
/// Phase 2: pass-through only. Enrichment services will be added in Phases 4-6.
/// </para>
/// </summary>
public sealed class EnrichmentPipelineService : BackgroundService
{
    private readonly Channel<TrackingData> _enrichmentChannel;
    private readonly Channel<TrackingData> _sqlWriterChannel;
    private readonly ForgeSettings _forgeSettings;
    private readonly ITrackingLogger _logger;

    public EnrichmentPipelineService(
        ForgeChannels channels,
        IOptions<ForgeSettings> forgeSettings,
        ITrackingLogger logger)
    {
        _enrichmentChannel = channels.Enrichment;
        _sqlWriterChannel = channels.SqlWriter;
        _forgeSettings = forgeSettings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _logger.Info($"EnrichmentPipelineService started. Enrichments enabled: {_forgeSettings.EnableEnrichments}");

        var reader = _enrichmentChannel.Reader;
        var processedCount = 0L;

        try
        {
            await foreach (var record in reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    // ── Phase 2: Pass-through (no enrichments yet) ────────────
                    // Future phases will call enrichment services here:
                    //   record = await _tier1.EnrichAsync(record, ct);
                    //   record = await _tier2.EnrichAsync(record, ct);
                    //   record = await _tier3.EnrichAsync(record, ct);

                    if (!_sqlWriterChannel.Writer.TryWrite(record))
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
}
