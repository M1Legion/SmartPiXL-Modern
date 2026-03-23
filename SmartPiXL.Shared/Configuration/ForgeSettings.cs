namespace SmartPiXL.Configuration;

// ============================================================================
// FORGE SETTINGS — Configuration for the SmartPiXL Forge Windows Service.
//
// Bound from the "Forge" section of SmartPiXL.Forge/appsettings.json.
// Controls named pipe server, failover directory, channel capacities,
// and enrichment pipeline toggles.
//
// Separate from TrackingSettings because these settings are only relevant
// to the Forge process. TrackingSettings is shared across Edge + Forge
// for connection strings, sync intervals, SMTP, etc.
// ============================================================================

/// <summary>
/// Strongly-typed configuration for the SmartPiXL Forge service.
/// Bound from the <c>"Forge"</c> section of appsettings.json.
/// Controls the named pipe server, failover catch-up, channel capacities,
/// and enrichment pipeline feature flags.
/// </summary>
public sealed class ForgeSettings
{
    /// <summary>JSON section name used for binding.</summary>
    public const string SectionName = "Forge";

    /// <summary>
    /// Named pipe name shared between the IIS Edge client and the Forge server.
    /// Both processes must use the same value. Default: <c>SmartPiXL-Enrichment</c>.
    /// </summary>
    public string PipeName { get; set; } = "SmartPiXL-Enrichment";

    /// <summary>
    /// Directory where the Edge writes JSONL failover files when the pipe is unavailable.
    /// The Forge's <c>FailoverCatchupService</c> scans this directory every 60 seconds.
    /// Relative paths resolve from <c>AppContext.BaseDirectory</c>.
    /// </summary>
    public string FailoverDirectory { get; set; } = @"C:\inetpub\Smartpixl.info\Failover";

    /// <summary>
    /// Maximum number of <c>TrackingData</c> items the pipe-to-enrichment
    /// <c>Channel&lt;T&gt;</c> can hold. Set high for burst absorption.
    /// </summary>
    public int PipeChannelCapacity { get; set; } = 50_000;

    /// <summary>
    /// Maximum number of enriched <c>TrackingData</c> items the
    /// enrichment-to-SQL-writer <c>Channel&lt;T&gt;</c> can hold.
    /// </summary>
    public int SqlWriterChannelCapacity { get; set; } = 50_000;

    /// <summary>
    /// Number of concurrent <c>NamedPipeServerStream</c> instances.
    /// Multiple instances allow the Edge to reconnect immediately if one
    /// pipe instance is busy processing a record.
    /// </summary>
    public int MaxConcurrentPipeInstances { get; set; } = 4;

    /// <summary>
    /// Maximum number of concurrent enrichment workers. Adaptive scaling starts
    /// at <see cref="MinEnrichmentWorkers"/> and scales up to this value based
    /// on enrichment channel depth. Capped at the NUMA node's logical processor
    /// count when <see cref="NumaNode"/> is configured. Default 32.
    /// </summary>
    public int EnrichmentWorkerCount { get; set; } = 32;

    /// <summary>
    /// Minimum number of enrichment workers that are always active. Adaptive
    /// scaling never drops below this value. Default 8.
    /// </summary>
    public int MinEnrichmentWorkers { get; set; } = 8;

    /// <summary>
    /// NUMA node index to pin the Forge process to. On a 4-socket system,
    /// valid values are 0-3. When set, all Forge threads run on the specified
    /// NUMA node's processors. Default 3 (bottom-right socket).
    /// </summary>
    public int NumaNode { get; set; } = 3;

    /// <summary>
    /// Logical processor count discovered at runtime by NUMA pinning.
    /// Set once by Program.cs after <see cref="NumaHelper.PinToNumaNode"/>.
    /// Not a config file value — populated at startup. Default <see cref="int.MaxValue"/> (no cap).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int NumaLogicalProcessors { get; set; } = int.MaxValue;

    /// <summary>
    /// Effective maximum worker count, capped by NUMA topology when applicable.
    /// Use this instead of <see cref="EnrichmentWorkerCount"/> to respect NUMA limits.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveMaxWorkers => NumaLogicalProcessors < EnrichmentWorkerCount
        ? NumaLogicalProcessors
        : EnrichmentWorkerCount;

    /// <summary>
    /// Number of background I/O workers for DNS and WHOIS cache warming
    /// (Lane 3). These workers are I/O-bound, not CPU-bound. Pinned to the
    /// same NUMA node as the rest of the Forge process. Default 8.
    /// </summary>
    public int BackgroundIpWorkerCount { get; set; } = 8;

    /// <summary>
    /// Timeout in milliseconds for writing a deserialized record to the
    /// enrichment channel. If the channel is full for this long, the record
    /// is dropped (Edge JSONL failover handles persistence). Default 5000 (5s).
    /// </summary>
    public int PipeChannelWriteTimeoutMs { get; set; } = 5_000;

    /// <summary>
    /// Master toggle for the enrichment pipeline. When false, records pass
    /// through the pipeline without any enrichment processing (useful for
    /// testing pipe + SQL writer in isolation).
    /// </summary>
    public bool EnableEnrichments { get; set; } = true;

    /// <summary>
    /// Seconds between failover directory scans. Default 60.
    /// </summary>
    public int FailoverScanIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Directory where the Forge writes enriched JSONL failover files when the
    /// SQL writer channel is full or the circuit breaker is open. Enriched records
    /// are persisted here instead of being dropped, and replayed when SQL recovers.
    /// Must be an absolute path.
    /// </summary>
    public string ForgeFailoverDirectory { get; set; } = @"C:\Services\SmartPiXL-Forge\ForgeFailover";

    /// <summary>
    /// Directory where the Forge writes dead-letter files when all batch retries
    /// are exhausted. Must be an absolute path. Replayed by ForgeReplayService.
    /// </summary>
    public string DeadLetterDirectory { get; set; } = @"C:\Services\SmartPiXL-Forge\DeadLetter";

    /// <summary>
    /// Maximum number of records per SqlBulkCopy write to PiXL.Parsed.
    /// Separate from <see cref="TrackingSettings.BatchSize"/> which controls the
    /// Edge's write batches. Forge batches can be larger because the Forge writes
    /// to a single table with fewer indexes. Default 500.
    /// </summary>
    public int ForgeBatchSize { get; set; } = 500;

    // ========================================================================
    // IP DATA ACQUISITION SETTINGS
    // ========================================================================

    /// <summary>
    /// Directory where downloaded IP data files are cached on disk.
    /// Relative paths resolve from <c>AppContext.BaseDirectory</c>.
    /// </summary>
    public string IpDataDirectory { get; set; } = "IpData";

    /// <summary>
    /// UTC hour (0-23) when the daily IP data acquisition check runs.
    /// Downloads new files if upstream sources have changed.
    /// Default 1 AM UTC (low traffic, before enrichment cycle).
    /// </summary>
    public int IpDataAcquisitionHourUtc { get; set; } = 1;

    // ========================================================================
    // NUMA SETTINGS
    // ========================================================================

    /// <summary>
    /// Estimated total RAM in GB on each NUMA node. Used for the NUMA pinning
    /// log message. Default 500 (matches 2TB / 4-node Xeon Gold 6254 topology).
    /// </summary>
    public int RamPerNumaNodeGB { get; set; } = 500;

    // ========================================================================
    // HEALTH TREE — CACHE THRESHOLDS
    // ========================================================================
    // Health probes report unhealthy (0) when a cache exceeds its threshold.
    // TODO: Explore auto-tuning thresholds based on memory pressure and hit rates.

    /// <summary>Max entries for UaParsing cache before health probe flags unhealthy.</summary>
    public int CacheThreshold_UaParsing { get; set; } = 50_000;

    /// <summary>Max entries for BotUaDetection cache.</summary>
    public int CacheThreshold_BotUaDetection { get; set; } = 50_000;

    /// <summary>Max entries for DnsLookup cache.</summary>
    public int CacheThreshold_DnsLookup { get; set; } = 200_000;

    /// <summary>Max entries for WhoisAsn cache.</summary>
    public int CacheThreshold_WhoisAsn { get; set; } = 200_000;

    /// <summary>Max entries for MaxMindGeo cache.</summary>
    public int CacheThreshold_MaxMindGeo { get; set; } = 200_000;

    /// <summary>Max entries for DeadInternet cache.</summary>
    public int CacheThreshold_DeadInternet { get; set; } = 100_000;

    /// <summary>Max entries for BehavioralReplay cache.</summary>
    public int CacheThreshold_BehavioralReplay { get; set; } = 500_000;

    /// <summary>Max entries for CrossCustomerIntel cache.</summary>
    public int CacheThreshold_CrossCustomerIntel { get; set; } = 500_000;

    /// <summary>Max entries for SessionStitching cache.</summary>
    public int CacheThreshold_SessionStitching { get; set; } = 500_000;
}
