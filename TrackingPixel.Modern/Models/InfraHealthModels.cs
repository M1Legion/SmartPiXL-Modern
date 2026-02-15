namespace TrackingPixel.Models;

// ============================================================================
// INFRASTRUCTURE HEALTH DTOs — Used by InfraHealthService and the /api/dash/infra
// endpoint to represent the system health snapshot. Extracted from the service
// file so models live with models.
//
// SERIALIZATION:
//   These classes are serialized to JSON by DashboardEndpoints.WriteJsonAsync()
//   using camelCase naming policy. Property names become camelCase keys in the
//   JSON response consumed by the Tron dashboard SPA.
//
// MUTABILITY:
//   All properties use { get; set; } because InfraHealthService.GetHealthAsync()
//   builds these incrementally from multiple async probe results. Making them
//   records or init-only would complicate the probe-then-assign pattern.
// ============================================================================

/// <summary>
/// Top-level container for the complete infrastructure health probe result.
/// Returned as JSON by the <c>/api/dash/infra</c> endpoint.
/// <para>
/// Each nested object represents one category of health probes:
/// Windows services, SQL connectivity, IIS websites, .NET app metrics,
/// data flow pipeline, ETL watermarks, and recent error log entries.
/// </para>
/// </summary>
public sealed class InfraHealthSnapshot
{
    /// <summary>UTC timestamp when the probe completed.</summary>
    public DateTime CheckedAt { get; set; }
    
    /// <summary>Total wall-clock milliseconds for all probes (services + SQL + websites + app metrics).</summary>
    public int ProbeTimeMs { get; set; }
    
    /// <summary>
    /// Aggregate status: "healthy", "degraded", or "critical".
    /// Derived from the worst individual probe result.
    /// </summary>
    public string OverallStatus { get; set; } = "unknown";
    
    /// <summary>Windows service health (SQL Server, IIS, W3SVC, etc.).</summary>
    public List<ServiceHealthItem> Services { get; set; } = [];
    
    /// <summary>SQL Server connectivity and basic row counts.</summary>
    public SqlHealthItem Sql { get; set; } = new();
    
    /// <summary>IIS-hosted website reachability via HTTP HEAD probes.</summary>
    public List<WebsiteHealthItem> Websites { get; set; } = [];
    
    /// <summary>.NET process metrics: memory, GC, threads, uptime.</summary>
    public AppHealthItem App { get; set; } = new();
    
    /// <summary>Data ingestion flow: last insert time, hourly rates, queue depth.</summary>
    public DataFlowHealthItem DataFlow { get; set; } = new();
    
    /// <summary>ETL pipeline: Device/IP/Visit/Match table sizes and watermarks.</summary>
    public PipelineHealthItem Pipeline { get; set; } = new();
    
    /// <summary>Recent application errors from the log directory.</summary>
    public RecentErrorsItem RecentErrors { get; set; } = new();
}

/// <summary>
/// Health status of a single Windows service (e.g., MSSQLSERVER, W3SVC).
/// </summary>
public sealed class ServiceHealthItem
{
    /// <summary>Human-readable display name (e.g., "SQL Server").</summary>
    public string Name { get; set; } = "";
    
    /// <summary>Windows service name used for <c>ServiceController</c> lookup.</summary>
    public string ServiceName { get; set; } = "";
    
    /// <summary>Service status string: "Running", "Stopped", "StartPending", etc.</summary>
    public string Status { get; set; } = "Unknown";
    
    /// <summary>True if the service is in the Running state.</summary>
    public bool IsRunning { get; set; }
    
    /// <summary>True if this service is essential (SQL, IIS). Affects overall status.</summary>
    public bool Critical { get; set; }
    
    /// <summary>Error message if the service could not be queried (e.g., access denied).</summary>
    public string? Error { get; set; }
}

/// <summary>
/// SQL Server connectivity probe result. Tests the connection string,
/// checks basic row counts in PiXL.Test and PiXL.Parsed, and reads
/// the ETL watermark.
/// </summary>
public sealed class SqlHealthItem
{
    /// <summary>True if the SQL connection opened successfully.</summary>
    public bool IsConnected { get; set; }
    
    /// <summary>Database name from the connection (should be "SmartPiXL").</summary>
    public string? Database { get; set; }
    
    /// <summary>Server instance name from the connection (e.g., "localhost\\SQL2025").</summary>
    public string? DataSource { get; set; }
    
    /// <summary>Round-trip time for the connectivity test query, in milliseconds.</summary>
    public int ResponseMs { get; set; }
    
    /// <summary>Row count in <c>PiXL.Test</c> (raw ingest table).</summary>
    public int TestRows { get; set; }
    
    /// <summary>Row count in <c>PiXL.Parsed</c> (materialized warehouse).</summary>
    public int ParsedRows { get; set; }
    
    /// <summary>Current <c>ETL.Watermark.LastProcessedId</c> for ParseNewHits.</summary>
    public long Watermark { get; set; }
    
    /// <summary>UTC timestamp of the last ETL run (from <c>ETL.Watermark.LastRunAt</c>).</summary>
    public DateTime? LastEtlRun { get; set; }
    
    /// <summary>SQL Server version string (e.g., "Microsoft SQL Server 2025 Developer").</summary>
    public string? ServerVersion { get; set; }
    
    /// <summary>Error message if the SQL probe failed.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// HTTP HEAD probe result for an IIS-hosted website (e.g., smartpixl.info, localhost:7000).
/// </summary>
public sealed class WebsiteHealthItem
{
    /// <summary>Human-readable display name.</summary>
    public string Name { get; set; } = "";
    
    /// <summary>URL that was probed.</summary>
    public string Url { get; set; } = "";
    
    /// <summary>True if the probe returned a 2xx/3xx status code.</summary>
    public bool IsHealthy { get; set; }
    
    /// <summary>HTTP status code returned (200, 404, 503, etc.).</summary>
    public int StatusCode { get; set; }
    
    /// <summary>Response time in milliseconds.</summary>
    public int ResponseMs { get; set; }
    
    /// <summary>True if this website is essential (affects overall status).</summary>
    public bool Critical { get; set; }
    
    /// <summary>Error message if the probe threw an exception (timeout, DNS failure, etc.).</summary>
    public string? Error { get; set; }
}

/// <summary>
/// .NET application process metrics, collected from <see cref="System.Diagnostics.Process"/>
/// and <see cref="System.GC"/>.
/// </summary>
public sealed class AppHealthItem
{
    /// <summary>OS process ID of the running application.</summary>
    public int ProcessId { get; set; }
    
    /// <summary>Time since the process started.</summary>
    public TimeSpan Uptime { get; set; }
    
    /// <summary>Private working set in megabytes (physical memory in use).</summary>
    public double WorkingSetMB { get; set; }
    
    /// <summary>Total managed + unmanaged threads.</summary>
    public int ThreadCount { get; set; }
    
    /// <summary>Gen 0 GC collections since process start.</summary>
    public int Gen0Collections { get; set; }
    
    /// <summary>Gen 1 GC collections since process start.</summary>
    public int Gen1Collections { get; set; }
    
    /// <summary>Gen 2 (full) GC collections since process start. High values indicate memory pressure.</summary>
    public int Gen2Collections { get; set; }
    
    /// <summary>Total bytes allocated by the GC since process start (in MB).</summary>
    public double TotalAllocatedMB { get; set; }
    
    /// <summary>Current GC heap size in megabytes.</summary>
    public double GcHeapMB { get; set; }
    
    /// <summary>Machine hostname (useful when comparing dev vs. production).</summary>
    public string MachineName { get; set; } = "";
    
    /// <summary>Operating system version string.</summary>
    public string OsVersion { get; set; } = "";
    
    /// <summary>.NET runtime version (e.g., "10.0.0").</summary>
    public string DotNetVersion { get; set; } = "";
    
    /// <summary>Current depth of the Channel&lt;T&gt; write queue in <see cref="Services.DatabaseWriterService"/>.</summary>
    public int QueueDepth { get; set; }
}

/// <summary>
/// Data ingestion flow health: tracks whether new data is arriving,
/// how fast it's being processed, and the current ETL lag.
/// </summary>
public sealed class DataFlowHealthItem
{
    /// <summary>True if new rows have arrived in <c>PiXL.Test</c> within the last 5 minutes.</summary>
    public bool IsFlowing { get; set; }
    
    /// <summary>UTC timestamp of the most recent INSERT into <c>PiXL.Test</c>.</summary>
    public DateTime? LastInsertUtc { get; set; }
    
    /// <summary>Seconds elapsed since the last insert. High values indicate ingestion stall.</summary>
    public int SecondsSinceLastInsert { get; set; }
    
    /// <summary>Number of hits ingested in the last 60 minutes.</summary>
    public int HitsLastHour { get; set; }
    
    /// <summary>Number of hits ingested in the last 5 minutes.</summary>
    public int HitsLast5Min { get; set; }
    
    /// <summary>MAX(Id) in <c>PiXL.Test</c> — the latest raw ingest row.</summary>
    public long MaxTestId { get; set; }
    
    /// <summary>Current <c>ETL.Watermark.LastProcessedId</c> for ParseNewHits.</summary>
    public long WatermarkId { get; set; }
    
    /// <summary>MAX(OriginalTestId) in <c>PiXL.Parsed</c>.</summary>
    public long MaxParsedId { get; set; }
    
    /// <summary>Rows in <c>PiXL.Test</c> not yet processed by the ETL (MaxTestId - WatermarkId).</summary>
    public int EtlLag { get; set; }
    
    /// <summary>Current Channel&lt;T&gt; queue depth.</summary>
    public int QueueDepth { get; set; }
    
    /// <summary>Error message if the data flow probe failed.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Summary of recent application errors scraped from the log directory.
/// <para>
/// The service scans log files for "ERROR" and "FAIL" lines, groups by message,
/// and distinguishes "recent" errors (last 30 min) from older ones. Only recent
/// errors affect the overall health status — stale errors are informational.
/// </para>
/// </summary>
public sealed class RecentErrorsItem
{
    /// <summary>True if any errors exist in today's log (regardless of age).</summary>
    public bool HasErrors { get; set; }
    
    /// <summary>True if any errors occurred within the recent window (last 30 min). Only this affects severity.</summary>
    public bool HasRecentErrors { get; set; }
    
    /// <summary>Total error count across today's log file.</summary>
    public int TotalErrorsToday { get; set; }
    
    /// <summary>Error count within the recent window (last 30 min). Only this affects severity.</summary>
    public int RecentErrorCount { get; set; }
    
    /// <summary>UTC timestamp of the most recent error entry.</summary>
    public DateTime? LastErrorUtc { get; set; }
    
    /// <summary>Grouped error entries with counts and recency flags.</summary>
    public List<ErrorEntry> Errors { get; set; } = [];
    
    /// <summary>Error message if the log scan itself failed (file locked, permission denied, etc.).</summary>
    public string? ScanError { get; set; }
}

/// <summary>
/// A single grouped error: all occurrences of the same error message are collapsed
/// into one entry with a count. The dashboard uses <see cref="IsRecent"/> and
/// <see cref="IsStale"/> to color-code entries (red = recent, gray = stale).
/// </summary>
public sealed class ErrorEntry
{
    /// <summary>Error source (log file name or component that produced the error).</summary>
    public string Source { get; set; } = "";
    
    /// <summary>Error message text (first line, truncated for display).</summary>
    public string Message { get; set; } = "";
    
    /// <summary>Total occurrences of this error today.</summary>
    public int Count { get; set; }
    
    /// <summary>How many of this error occurred in the recent window (last 30 min).</summary>
    public int RecentCount { get; set; }
    
    /// <summary>When this specific error was last seen.</summary>
    public DateTime? LastSeenUtc { get; set; }
    
    /// <summary>True if this error occurred within the last 30 minutes.</summary>
    public bool IsRecent { get; set; }
    
    /// <summary>True if all occurrences are older than 2 hours — likely already resolved.</summary>
    public bool IsStale { get; set; }
}

/// <summary>
/// Full pipeline health snapshot from <c>vw_Dash_PipelineHealth</c>.
/// <para>
/// Covers six core tables (PiXL.Test, PiXL.Parsed, PiXL.Device, PiXL.IP,
/// PiXL.Visit, PiXL.Match) and both ETL watermarks (ParseNewHits, MatchVisits).
/// The dashboard uses this to display row counts, max IDs, watermark positions,
/// lag indicators, and timestamp freshness in the pipeline health panel.
/// </para>
/// <para>
/// A non-null <see cref="Error"/> with <see cref="IsAvailable"/> = false means
/// the view query itself failed (SQL timeout, permission issue, etc.).
/// </para>
/// </summary>
public sealed class PipelineHealthItem
{
    /// <summary>True if the view query succeeded. False + non-null <see cref="Error"/> = query failure.</summary>
    public bool IsAvailable { get; set; }

    /// <summary>Error message if the pipeline health query failed (null on success).</summary>
    public string? Error { get; set; }

    // ── Table row counts ────────────────────────────────────────────
    /// <summary>Row count of <c>PiXL.Test</c> (raw ingest table).</summary>
    public int TestRows { get; set; }

    /// <summary>Row count of <c>PiXL.Parsed</c> (materialized warehouse table).</summary>
    public int ParsedRows { get; set; }

    /// <summary>Row count of <c>PiXL.Device</c> (device fingerprint dimension table).</summary>
    public int DeviceRows { get; set; }

    /// <summary>Row count of <c>PiXL.IP</c> (IP address dimension table).</summary>
    public int IpRows { get; set; }

    /// <summary>Row count of <c>PiXL.Visit</c> (session/visit fact table).</summary>
    public int VisitRows { get; set; }

    /// <summary>Row count of <c>PiXL.Match</c> (email-to-visit resolution table).</summary>
    public int MatchRows { get; set; }

    // ── Max IDs ─────────────────────────────────────────────────────
    /// <summary>Maximum identity value in <c>PiXL.Test</c>. Used to calculate parse lag.</summary>
    public long MaxTestId { get; set; }

    /// <summary>Maximum identity value in <c>PiXL.Visit</c>. Used to calculate match lag.</summary>
    public long MaxVisitId { get; set; }

    /// <summary>Maximum identity value in <c>PiXL.Match</c>.</summary>
    public long MaxMatchId { get; set; }

    // ── ParseNewHits watermark ──────────────────────────────────────
    /// <summary>Last PiXL.Test ID processed by <c>ETL.usp_ParseNewHits</c>.</summary>
    public long ParseWatermark { get; set; }

    /// <summary>Cumulative rows processed by the parse ETL since watermark reset.</summary>
    public long ParseTotalProcessed { get; set; }

    /// <summary>UTC timestamp of the last successful <c>ETL.usp_ParseNewHits</c> run.</summary>
    public DateTime? ParseLastRunAt { get; set; }

    // ── MatchVisits watermark ───────────────────────────────────────
    /// <summary>Last PiXL.Visit ID processed by the match ETL.</summary>
    public long MatchWatermark { get; set; }

    /// <summary>Cumulative visits processed by the match ETL.</summary>
    public long MatchTotalProcessed { get; set; }

    /// <summary>Cumulative visits that resolved to an email match.</summary>
    public long MatchTotalMatched { get; set; }

    /// <summary>UTC timestamp of the last successful match ETL run.</summary>
    public DateTime? MatchLastRunAt { get; set; }

    // ── Match resolution ────────────────────────────────────────────
    /// <summary>Matches that resolved to an email address.</summary>
    public int MatchesResolved { get; set; }

    /// <summary>Matches still pending resolution (device seen but no email yet).</summary>
    public int MatchesPending { get; set; }

    /// <summary>Visits where the visitor's email is known.</summary>
    public int VisitsWithEmail { get; set; }

    // ── Lags ────────────────────────────────────────────────────────
    /// <summary>
    /// Parse lag = <see cref="MaxTestId"/> − <see cref="ParseWatermark"/>.
    /// Non-zero means raw hits are waiting to be parsed. Dashboard shows amber if &gt; 0.
    /// </summary>
    public int ParseLag { get; set; }

    /// <summary>
    /// Match lag = <see cref="MaxVisitId"/> − <see cref="MatchWatermark"/>.
    /// Non-zero means visits are waiting for match resolution.
    /// </summary>
    public int MatchLag { get; set; }

    // ── Latest timestamps (freshness indicators) ────────────────────
    /// <summary>Timestamp of the most recent row in <c>PiXL.Test</c>.</summary>
    public DateTime? TestLatest { get; set; }

    /// <summary>Timestamp of the most recent row in <c>PiXL.Parsed</c>.</summary>
    public DateTime? ParsedLatest { get; set; }

    /// <summary>Timestamp of the most recent row in <c>PiXL.Device</c>.</summary>
    public DateTime? DeviceLatest { get; set; }

    /// <summary>Timestamp of the most recent row in <c>PiXL.IP</c>.</summary>
    public DateTime? IpLatest { get; set; }

    /// <summary>Timestamp of the most recent row in <c>PiXL.Visit</c>.</summary>
    public DateTime? VisitLatest { get; set; }

    /// <summary>Timestamp of the most recent row in <c>PiXL.Match</c>.</summary>
    public DateTime? MatchLatest { get; set; }

    // ── Uniqueness ──────────────────────────────────────────────────
    /// <summary>Distinct device fingerprint count across <c>PiXL.Visit</c>.</summary>
    public int UniqueDevicesInVisits { get; set; }

    /// <summary>Distinct IP address count across <c>PiXL.Visit</c>.</summary>
    public int UniqueIpsInVisits { get; set; }
}
