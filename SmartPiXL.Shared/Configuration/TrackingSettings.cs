namespace SmartPiXL.Configuration;

// ============================================================================
// CONFIGURATION POCOs — Bound from appsettings.json via IOptions<T>.
// These classes provide strongly-typed, validated access to application
// configuration. Defaults are compiled in as fallbacks so the server can
// start even with a missing or partial appsettings.json.
//
// CRITICAL: Two copies of appsettings.json exist (see copilot-instructions.md).
//   Dev:  SmartPiXL.Modern/appsettings.json (ports 7000/7001)
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
    /// Default uses the SmartPiXL SQL login. The actual password is injected
    /// at runtime via the <c>Tracking__ConnectionString</c> machine environment
    /// variable — this compiled default is a safe fallback only.
    /// <c>Encrypt=True</c> is the MDSC 4.0+ default but stated explicitly for clarity.
    /// <c>TrustServerCertificate=True</c> is safe for localhost (no network boundary).
    /// </summary>
    public string ConnectionString { get; set; } = "Server=localhost\\SQL2025;Database=SmartPiXL;User Id=SmartPiXL;Password=OVERRIDE_VIA_ENV;TrustServerCertificate=True;Encrypt=True";
    
    /// <summary>
    /// IP addresses allowed to access the /tron dashboard and /api/dash/* endpoints
    /// in addition to localhost/loopback. These are external workstation IPs that
    /// can reach the dashboard remotely. Empty = localhost only (original behavior).
    /// </summary>
    public string[] DashboardAllowedIPs { get; set; } = [];
    
    /// <summary>
    /// Maximum number of <see cref="Models.TrackingData"/> items the bounded
    /// Channel&lt;T&gt; can hold before <c>TryQueue()</c> starts returning false.
    /// Set high enough to absorb traffic spikes without dropping requests.
    /// </summary>
    public int QueueCapacity { get; set; } = 10000;
    
    /// <summary>
    /// Maximum number of records per SqlBulkCopy write. Larger batches are more
    /// efficient but increase memory pressure and lock duration on PiXL.Raw.
    /// 100 is a good balance for sub-1000 RPS workloads.
    /// </summary>
    public int BatchSize { get; set; } = 100;
    
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
    
    // ========================================================================
    // EDGE → FORGE PIPE SETTINGS (Phase 3)
    // ========================================================================
    
    /// <summary>
    /// Name of the named pipe connecting the Edge to the Forge.
    /// Must match <see cref="ForgeSettings.PipeName"/> exactly.
    /// Default: <c>SmartPiXL-Enrichment</c>.
    /// </summary>
    public string PipeName { get; set; } = "SmartPiXL-Enrichment";
    
    /// <summary>
    /// Directory where the Edge writes JSONL failover files when the pipe is unavailable.
    /// Relative paths resolve from <c>AppContext.BaseDirectory</c>.
    /// Must match <see cref="ForgeSettings.FailoverDirectory"/> so the Forge's
    /// <c>FailoverCatchupService</c> can pick up the files.
    /// Default: <c>Failover</c>.
    /// </summary>
    public string FailoverDirectory { get; set; } = "Failover";
    
    // ========================================================================
    // EDGE COMMUNICATION SETTINGS (Forge only)
    // ========================================================================
    
    /// <summary>
    /// Base URL of the IIS Edge process, used by the Worker's
    /// <c>HttpEdgeHealthClient</c> to call <c>/internal/*</c> endpoints.
    /// Null by default — the Worker's <see cref="Program"/> applies a fallback
    /// of <c>http://127.0.0.1:6000</c> (IIS production) when not configured.
    /// Dev override in appsettings.json: <c>http://127.0.0.1:7000</c>.
    /// </summary>
    public string? EdgeBaseUrl { get; set; }
    
    /// <summary>Public base URL for the tracking domain (e.g., "https://smartpixl.info").</summary>
    public string? BaseUrl { get; set; }

    // ========================================================================
    // IP-API / GEO SYNC SETTINGS
    // ========================================================================
    
    // ========================================================================
    // XAVIER SYNC SETTINGS — TEMPORARY BRIDGE
    //
    // These three Xavier syncs (IPGEO, Company, PiXL) exist ONLY as a
    // transitional bridge while Xavier's legacy front-end is the client-
    // facing product. Once a new front-end is built (not yet scoped),
    // SmartPiXL becomes the authoritative data source and Xavier syncs
    // are decommissioned. They are expected to run for an extended period
    // but are NOT permanent architecture.
    //
    // CERT STATUS: Xavier (192.168.88.35 / D43DQBM2) has a self-signed
    // cert (CN=192.168.88.35, sha1RSA, thumbprint 02AC76BB...) installed
    // in our Cert:\LocalMachine\Root. However, SQL Server 2017 on Xavier
    // is NOT configured to present this cert — it uses its auto-generated
    // cert instead. Until Xavier's SQL Server Configuration Manager is
    // updated to use the custom cert, TrustServerCertificate=True is
    // required. Once configured, remove TrustServerCertificate=True and
    // connections will validate against the trusted root cert.
    // ========================================================================
    
    /// <summary>
    /// Connection string for Xavier (192.168.88.35) — the IPGEO database.
    /// Used by <see cref="Services.IpApiSyncService"/> to pull delta rows from
    /// <c>IPGEO.dbo.IP_Location_New</c>. Null or empty = sync disabled.
    /// <para>
    /// <b>TEMPORARY</b>: This sync is a transitional bridge while Xavier's legacy
    /// front-end is client-facing. Once a new front-end replaces Xavier, SmartPiXL
    /// becomes authoritative for IP geolocation and this sync is decommissioned.
    /// </para>
    /// <para>
    /// <b>CERT</b>: <c>TrustServerCertificate=True</c> is required until Xavier's
    /// SQL Server is configured to present the custom cert (CN=192.168.88.35).
    /// See <c>TrackingSettings.cs</c> cert status notes.
    /// </para>
    /// </summary>
    public string? XavierConnectionString { get; set; }

    /// <summary>
    /// Connection string for the SmartPiXL database on Xavier (192.168.88.35).
    /// Used by <see cref="Services.CompanyPiXLSyncService"/> for Company and Pixel
    /// table synchronization. Null or empty = disabled.
    /// <para>
    /// <b>TEMPORARY</b>: This sync is a transitional bridge while Xavier's legacy
    /// front-end is client-facing. Once a new front-end replaces Xavier, SmartPiXL
    /// becomes authoritative for Company/Pixel data and this sync is decommissioned.
    /// </para>
    /// <para>
    /// <b>CERT</b>: <c>TrustServerCertificate=True</c> is required until Xavier's
    /// SQL Server is configured to present the custom cert (CN=192.168.88.35).
    /// See <c>TrackingSettings.cs</c> cert status notes.
    /// </para>
    /// </summary>
    public string? XavierSmartPiXLConnectionString { get; set; }
    
    /// <summary>
    /// UTC hour (0–23) when the daily IP-API sync runs.
    /// Default is 2 AM UTC to avoid peak traffic windows.
    /// </summary>
    public int IpApiSyncHourUtc { get; set; } = 2;
    
    /// <summary>
    /// Hours between Xavier sync cycles. Default 6 = syncs at UTC hours 2, 8, 14, 20.
    /// Running 4× daily avoids concentrating load during Xavier's nightly maintenance
    /// window (which caused deadlocks at the single 2 AM UTC run).
    /// </summary>
    public int SyncIntervalHours { get; set; } = 6;
    
    // ========================================================================
    // SMTP / EMAIL NOTIFICATION SETTINGS
    // ========================================================================
    
    /// <summary>SMTP server hostname (e.g., "smtp.office365.com"). Null = email disabled.</summary>
    public string? SmtpHost { get; set; }
    
    /// <summary>SMTP port. Default 587 (STARTTLS).</summary>
    public int SmtpPort { get; set; } = 587;
    
    /// <summary>SMTP authentication username.</summary>
    public string? SmtpUsername { get; set; }
    
    /// <summary>SMTP authentication password.</summary>
    public string? SmtpPassword { get; set; }
    
    /// <summary>Email sender address for ops notifications.</summary>
    public string SmtpFromAddress { get; set; } = "ops@smartpixl.info";
    
    /// <summary>Operator email address for remediation notifications. Null = no email alerts.</summary>
    public string? OpsNotificationEmail { get; set; }
    
    /// <summary>
    /// Carrier email-to-SMS gateway address (e.g., "5551234567@vtext.com" for Verizon).
    /// Null or empty = SMS alerts disabled. Uses the same SMTP transport as email.
    /// </summary>
    public string? SmsGatewayAddress { get; set; }
    
    /// <summary>Enable SSL/TLS for SMTP connection.</summary>
    public bool SmtpEnableSsl { get; set; } = true;
    
    // ========================================================================
    // MAINTENANCE SCHEDULE SETTINGS
    // ========================================================================
    
    /// <summary>UTC hour (0–23) for daily raw data purge. Default 3 AM.</summary>
    public int PurgeHourUtc { get; set; } = 3;
    
    /// <summary>UTC hour (0–23) for weekly parsed data archive. Default 2 AM Sunday.</summary>
    public int ArchiveHourUtc { get; set; } = 2;
    
    /// <summary>UTC hour (0–23) for weekly index maintenance. Default 4 AM Sunday.</summary>
    public int IndexMaintenanceHourUtc { get; set; } = 4;
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
