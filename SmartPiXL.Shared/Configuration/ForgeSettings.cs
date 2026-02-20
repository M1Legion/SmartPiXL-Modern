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
    public string FailoverDirectory { get; set; } = "Failover";

    /// <summary>
    /// Maximum number of <c>TrackingData</c> items the pipe-to-enrichment
    /// <c>Channel&lt;T&gt;</c> can hold. Set high for burst absorption.
    /// </summary>
    public int PipeChannelCapacity { get; set; } = 50_000;

    /// <summary>
    /// Maximum number of enriched <c>TrackingData</c> items the
    /// enrichment-to-SQL-writer <c>Channel&lt;T&gt;</c> can hold.
    /// </summary>
    public int SqlWriterChannelCapacity { get; set; } = 10_000;

    /// <summary>
    /// Number of concurrent <c>NamedPipeServerStream</c> instances.
    /// Multiple instances allow the Edge to reconnect immediately if one
    /// pipe instance is busy processing a record.
    /// </summary>
    public int MaxConcurrentPipeInstances { get; set; } = 4;

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
}
