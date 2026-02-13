namespace TrackingPixel.Configuration;

/// <summary>
/// Strongly-typed configuration for the tracking server.
/// Bound from appsettings.json "Tracking" section.
/// </summary>
public sealed class TrackingSettings
{
    public const string SectionName = "Tracking";
    
    /// <summary>
    /// SQL Server connection string.
    /// </summary>
    public string ConnectionString { get; set; } = "Server=localhost;Database=SmartPixl;Integrated Security=True;TrustServerCertificate=True";
    
    /// <summary>
    /// Maximum items in the write queue before dropping new requests.
    /// </summary>
    public int QueueCapacity { get; set; } = 10000;
    
    /// <summary>
    /// Maximum records per bulk insert batch.
    /// </summary>
    public int BatchSize { get; set; } = 100;
    
    /// <summary>
    /// Milliseconds to wait for more items before writing a partial batch.
    /// </summary>
    public int BatchTimeoutMs { get; set; } = 100;
    
    /// <summary>
    /// Seconds to wait for queue to drain during shutdown.
    /// </summary>
    public int ShutdownTimeoutSeconds { get; set; } = 30;
    
    /// <summary>
    /// SQL bulk copy timeout in seconds.
    /// </summary>
    public int BulkCopyTimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Logging configuration.
/// </summary>
public sealed class TrackingLogSettings
{
    public const string SectionName = "TrackingLog";
    
    /// <summary>
    /// Directory for log files (relative to app or absolute).
    /// </summary>
    public string LogDirectory { get; set; } = "Log";
    
    /// <summary>
    /// Minimum log level: Trace, Debug, Info, Warning, Error, None
    /// </summary>
    public TrackingLogLevel MinimumLevel { get; set; } = TrackingLogLevel.Info;
    
    /// <summary>
    /// Whether to also write to console (debug only recommended).
    /// </summary>
    public bool WriteToConsole { get; set; } = false;
}

public enum TrackingLogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
    None = 5
}
