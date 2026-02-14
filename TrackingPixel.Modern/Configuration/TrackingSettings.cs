namespace TrackingPixel.Configuration;

// ============================================================================
// CONFIGURATION POCOs — Bound from appsettings.json via IOptions<T>.
// These classes provide strongly-typed, validated access to application
// configuration. Defaults are compiled in as fallbacks so the server can
// start even with a missing or partial appsettings.json.
//
// CRITICAL: Two copies of appsettings.json exist (see copilot-instructions.md).
//   Dev:  TrackingPixel.Modern/appsettings.json (ports 7000/7001)
//   Prod: C:\inetpub\Smartpixl.info\appsettings.json (ports 6000/6001)
// Changes here (compiled defaults) apply to both environments on next deploy.
// ============================================================================

/// <summary>
/// Strongly-typed configuration for the tracking server's core behavior.
/// Bound from the <c>"Tracking"</c> section of appsettings.json.
/// Controls SQL connectivity, the Channel&lt;T&gt; write queue dimensions,
/// and SqlBulkCopy batch parameters.
/// </summary>
public sealed class TrackingSettings
{
    /// <summary>JSON section name used for binding.</summary>
    public const string SectionName = "Tracking";
    
    /// <summary>
    /// SQL Server connection string targeting the SmartPiXL database.
    /// Default targets the local SQL2025 instance with Windows Integrated auth.
    /// </summary>
    public string ConnectionString { get; set; } = "Server=localhost\\SQL2025;Database=SmartPiXL;Integrated Security=True;TrustServerCertificate=True";
    
    /// <summary>
    /// Maximum number of <see cref="Models.TrackingData"/> items the bounded
    /// Channel&lt;T&gt; can hold before <c>TryQueue()</c> starts returning false.
    /// Set high enough to absorb traffic spikes without dropping requests.
    /// </summary>
    public int QueueCapacity { get; set; } = 10000;
    
    /// <summary>
    /// Maximum number of records per SqlBulkCopy write. Larger batches are more
    /// efficient but increase memory pressure and lock duration on PiXL.Test.
    /// 100 is a good balance for sub-1000 RPS workloads.
    /// </summary>
    public int BatchSize { get; set; } = 100;
    
    /// <summary>
    /// Not currently wired into the write loop — reserved for a future timer-based
    /// flush that writes partial batches when traffic is low. The write loop
    /// currently drains as fast as items arrive.
    /// </summary>
    public int BatchTimeoutMs { get; set; } = 100;
    
    /// <summary>
    /// Seconds the shutdown drain loop waits before abandoning unwritten items.
    /// Should be long enough for one full batch cycle under normal load.
    /// </summary>
    public int ShutdownTimeoutSeconds { get; set; } = 30;
    
    /// <summary>
    /// Timeout passed to <c>SqlBulkCopy.BulkCopyTimeout</c>. If a single
    /// bulk insert exceeds this, the batch is logged as failed and dropped.
    /// 60 seconds handles worst-case SQL contention; increase for very large batches.
    /// </summary>
    public int BulkCopyTimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Configuration for the file-based tracking logger (<see cref="Services.FileTrackingLogger"/>).
/// Bound from the <c>"TrackingLog"</c> section of appsettings.json.
/// </summary>
public sealed class TrackingLogSettings
{
    /// <summary>JSON section name used for binding.</summary>
    public const string SectionName = "TrackingLog";
    
    /// <summary>
    /// Directory for daily log files. Relative paths are resolved from
    /// <c>AppContext.BaseDirectory</c> (typically the publish output folder).
    /// Files are named <c>yyyy_MM_dd.log</c> and created on first write each day.
    /// </summary>
    public string LogDirectory { get; set; } = "Log";
    
    /// <summary>
    /// Minimum severity to write. Messages below this level are discarded at the
    /// call site (zero-cost when disabled). Recommended: Info for production,
    /// Debug for troubleshooting, Trace for deep diagnostics.
    /// </summary>
    public TrackingLogLevel MinimumLevel { get; set; } = TrackingLogLevel.Info;
    
    /// <summary>
    /// When true, log messages are also echoed to <c>Console.WriteLine</c>.
    /// Useful during local development; disable in production (IIS stdout goes
    /// to the stdout log file, doubling disk I/O for no benefit).
    /// </summary>
    public bool WriteToConsole { get; set; } = false;
}

/// <summary>
/// Log severity levels, ordered from most verbose (Trace=0) to silent (None=5).
/// Numeric ordering enables fast <c>level &gt;= minimum</c> filtering.
/// </summary>
public enum TrackingLogLevel
{
    /// <summary>Ultra-verbose: per-request data dumps, header contents, query strings.</summary>
    Trace = 0,
    /// <summary>Developer diagnostics: service lifecycle, cache hit/miss, batch sizes.</summary>
    Debug = 1,
    /// <summary>Normal operations: startup banners, ETL row counts, queue drain status.</summary>
    Info = 2,
    /// <summary>Recoverable issues: queue full (dropping requests), ETL proc returned 0 rows.</summary>
    Warning = 3,
    /// <summary>Failures: SQL connection errors, batch write failures, unhandled exceptions.</summary>
    Error = 4,
    /// <summary>Logging disabled entirely.</summary>
    None = 5
}
