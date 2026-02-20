using System.Threading.Channels;
using SmartPiXL.Models;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// FORGE CHANNELS — Holds the two bounded Channel<TrackingData> instances
// that connect the Forge pipeline stages.
//
//   PipeListenerService        → Enrichment channel → EnrichmentPipelineService
//   FailoverCatchupService     → Enrichment channel → EnrichmentPipelineService
//   EnrichmentPipelineService  → SqlWriter channel  → SqlBulkCopyWriterService
//
// Registered as a singleton in DI. Both channels use BoundedChannelFullMode.Wait
// so TryWrite returns false immediately when full (callers drop or log).
// ============================================================================

/// <summary>
/// Singleton container for the two <see cref="Channel{T}"/> instances that
/// connect the Forge's pipeline stages. Avoids ambiguous DI registration
/// of two <c>Channel&lt;TrackingData&gt;</c> instances.
/// </summary>
public sealed class ForgeChannels
{
    /// <summary>
    /// Pipe listener → enrichment pipeline. High capacity for burst absorption.
    /// </summary>
    public Channel<TrackingData> Enrichment { get; }

    /// <summary>
    /// Enrichment pipeline → SQL bulk writer. Standard capacity.
    /// </summary>
    public Channel<TrackingData> SqlWriter { get; }

    public ForgeChannels(int enrichmentCapacity, int sqlWriterCapacity)
    {
        Enrichment = Channel.CreateBounded<TrackingData>(
            new BoundedChannelOptions(enrichmentCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true // Only EnrichmentPipelineService reads
            });

        SqlWriter = Channel.CreateBounded<TrackingData>(
            new BoundedChannelOptions(sqlWriterCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true // Only SqlBulkCopyWriterService reads
            });
    }
}
