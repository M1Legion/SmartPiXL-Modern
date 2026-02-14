using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TrackingPixel.Configuration;

namespace TrackingPixel.Services;

// ============================================================================
// INFRASTRUCTURE HEALTH SERVICE
//
// Probes Windows services, SQL connectivity, IIS websites, and internal app
// components to build a single JSON snapshot for the Tron dashboard.
//
// Design decisions:
//   • Runs probes in parallel with a 5-second overall timeout per check.
//   • Each probe is independent — a SQL failure won't block the IIS check.
//   • Results are cached for 15 seconds to avoid hammering services on
//     rapid dashboard refreshes.
//   • Service checks use ServiceController (WMI-free, low overhead).
//   • Website checks use lightweight HEAD requests with short timeouts.
// ============================================================================

[SupportedOSPlatform("windows")]
public sealed class InfraHealthService : IDisposable
{
    private readonly TrackingSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly ITrackingLogger _logger;
    private readonly DatabaseWriterService _dbWriter;

    // Cached result to avoid hammering probes on every 10s dashboard refresh
    private InfraHealthSnapshot? _cached;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public InfraHealthService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger,
        DatabaseWriterService dbWriter)
    {
        _settings = settings.Value;
        _logger = logger;
        _dbWriter = dbWriter;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    /// <summary>
    /// Returns a full infrastructure health snapshot. Results are cached for 15 seconds.
    /// </summary>
    public async Task<InfraHealthSnapshot> GetHealthAsync()
    {
        if (_cached is not null && DateTime.UtcNow < _cacheExpiry)
            return _cached;

        await _lock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_cached is not null && DateTime.UtcNow < _cacheExpiry)
                return _cached;

            var snapshot = await ProbeAllAsync();
            _cached = snapshot;
            _cacheExpiry = DateTime.UtcNow.Add(_cacheDuration);
            return snapshot;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<InfraHealthSnapshot> ProbeAllAsync()
    {
        var sw = Stopwatch.StartNew();

        // Run all probes in parallel
        var servicesTask = Task.Run(ProbeWindowsServices);
        var sqlTask = ProbeSqlAsync();
        var websitesTask = ProbeWebsitesAsync();
        var appTask = Task.Run(ProbeAppComponents);
        var dataFlowTask = ProbeDataFlowAsync();
        var logTask = Task.Run(ProbeIisLogs);

        await Task.WhenAll(servicesTask, sqlTask, websitesTask, appTask, dataFlowTask, logTask);

        sw.Stop();

        return new InfraHealthSnapshot
        {
            CheckedAt = DateTime.UtcNow,
            ProbeTimeMs = (int)sw.ElapsedMilliseconds,
            Services = servicesTask.Result,
            Sql = sqlTask.Result,
            Websites = websitesTask.Result,
            App = appTask.Result,
            DataFlow = dataFlowTask.Result,
            RecentErrors = logTask.Result,
            OverallStatus = ComputeOverallStatus(
                servicesTask.Result, sqlTask.Result,
                websitesTask.Result, appTask.Result,
                dataFlowTask.Result, logTask.Result)
        };
    }

    // ========================================================================
    // WINDOWS SERVICES
    // ========================================================================
    private List<ServiceHealthItem> ProbeWindowsServices()
    {
        var targets = new[]
        {
            ("MSSQL$SQL2025",    "SQL Server (SQL2025)",    true),   // Primary DB
            ("W3SVC",            "IIS (World Wide Web)",    true),   // Web server
            ("SQLAgent$SQL2025", "SQL Agent (SQL2025)",     false),  // Nice to have
            ("MSSQLSERVER",      "SQL Server (Default)",    false),  // Legacy instance
        };

        var results = new List<ServiceHealthItem>(targets.Length);
        foreach (var (serviceName, displayName, critical) in targets)
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                results.Add(new ServiceHealthItem
                {
                    Name = displayName,
                    ServiceName = serviceName,
                    Status = sc.Status.ToString(),
                    IsRunning = sc.Status == ServiceControllerStatus.Running,
                    Critical = critical
                });
            }
            catch (Exception ex)
            {
                results.Add(new ServiceHealthItem
                {
                    Name = displayName,
                    ServiceName = serviceName,
                    Status = "NotFound",
                    IsRunning = false,
                    Critical = critical,
                    Error = ex.Message
                });
            }
        }
        return results;
    }

    // ========================================================================
    // SQL CONNECTIVITY
    // ========================================================================
    private async Task<SqlHealthItem> ProbeSqlAsync()
    {
        var item = new SqlHealthItem();
        var sw = Stopwatch.StartNew();

        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync();

            // Quick smoke test — read the watermark to prove we can query
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT 
                    (SELECT COUNT(*) FROM PiXL.Test) AS TestRows,
                    (SELECT COUNT(*) FROM PiXL.Parsed) AS ParsedRows,
                    (SELECT LastProcessedId FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits') AS Watermark,
                    (SELECT LastRunAt FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits') AS LastEtlRun,
                    @@VERSION AS ServerVersion";
            cmd.CommandTimeout = 5;

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                item.TestRows = reader.GetInt32(0);
                item.ParsedRows = reader.GetInt32(1);
                item.Watermark = reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetValue(2));
                item.LastEtlRun = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
                item.ServerVersion = reader.IsDBNull(4) ? null : reader.GetString(4);
            }

            item.IsConnected = true;
            item.Database = conn.Database;
            item.DataSource = conn.DataSource;
        }
        catch (Exception ex)
        {
            item.IsConnected = false;
            item.Error = ex.Message;
        }

        sw.Stop();
        item.ResponseMs = (int)sw.ElapsedMilliseconds;
        return item;
    }

    // ========================================================================
    // WEBSITE PROBES
    // ========================================================================
    private async Task<List<WebsiteHealthItem>> ProbeWebsitesAsync()
    {
        var targets = new (string Name, string Url, bool Critical, bool IsPixelTest)[]
        {
            ("IIS Production (HTTP)",   "http://192.168.88.176/health",  true,  false),
            ("IIS Pixel Endpoint",      "http://192.168.88.176/HEALTHCHECK/probe_SMART.GIF?_hc=1", true, true),
            ("Dev Kestrel (HTTP)",      "http://localhost:7000/health",  false, false),
        };

        var tasks = targets.Select(async t =>
        {
            var item = new WebsiteHealthItem
            {
                Name = t.Name,
                Url = t.Url,
                Critical = t.Critical
            };
            var sw = Stopwatch.StartNew();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, t.Url);
                request.Headers.Add("User-Agent", "SmartPiXL-HealthCheck/1.0");

                using var response = await _httpClient.SendAsync(request);
                sw.Stop();

                item.StatusCode = (int)response.StatusCode;
                item.IsHealthy = response.IsSuccessStatusCode;
                item.ResponseMs = (int)sw.ElapsedMilliseconds;
            }
            catch (TaskCanceledException)
            {
                sw.Stop();
                item.IsHealthy = false;
                item.Error = "Timeout (5s)";
                item.ResponseMs = (int)sw.ElapsedMilliseconds;
            }
            catch (HttpRequestException ex)
            {
                sw.Stop();
                item.IsHealthy = false;
                item.Error = ex.InnerException?.Message ?? ex.Message;
                item.ResponseMs = (int)sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                sw.Stop();
                item.IsHealthy = false;
                item.Error = ex.Message;
                item.ResponseMs = (int)sw.ElapsedMilliseconds;
            }

            return item;
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    // ========================================================================
    // APP COMPONENTS (in-process checks)
    // ========================================================================
    private AppHealthItem ProbeAppComponents()
    {
        var process = Process.GetCurrentProcess();
        return new AppHealthItem
        {
            ProcessId = process.Id,
            Uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime(),
            WorkingSetMB = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1),
            ThreadCount = process.Threads.Count,
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            TotalAllocatedMB = Math.Round(GC.GetTotalAllocatedBytes(true) / 1024.0 / 1024.0, 1),
            GcHeapMB = Math.Round(GC.GetTotalMemory(false) / 1024.0 / 1024.0, 1),
            MachineName = Environment.MachineName,
            OsVersion = Environment.OSVersion.ToString(),
            DotNetVersion = Environment.Version.ToString(),
            QueueDepth = _dbWriter.QueueDepth,
        };
    }

    // ========================================================================
    // DATA FLOW PROBE — Is data actually making it into the database?
    // This is the check that would have caught the schema migration failure.
    // ========================================================================
    private async Task<DataFlowHealthItem> ProbeDataFlowAsync()
    {
        var item = new DataFlowHealthItem();
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT 
                    (SELECT MAX(ReceivedAt) FROM PiXL.Test) AS LastInsert,
                    (SELECT COUNT(*) FROM PiXL.Test WHERE ReceivedAt >= DATEADD(HOUR, -1, GETUTCDATE())) AS HitsLastHour,
                    (SELECT COUNT(*) FROM PiXL.Test WHERE ReceivedAt >= DATEADD(MINUTE, -5, GETUTCDATE())) AS HitsLast5Min,
                    (SELECT MAX(Id) FROM PiXL.Test) AS MaxTestId,
                    (SELECT LastProcessedId FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits') AS WatermarkId,
                    (SELECT MAX(SourceId) FROM PiXL.Parsed) AS MaxParsedId";
            cmd.CommandTimeout = 5;

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                item.LastInsertUtc = reader.IsDBNull(0) ? null : reader.GetDateTime(0);
                item.HitsLastHour = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                item.HitsLast5Min = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                item.MaxTestId = reader.IsDBNull(3) ? 0 : Convert.ToInt64(reader.GetValue(3));
                item.WatermarkId = reader.IsDBNull(4) ? 0 : Convert.ToInt64(reader.GetValue(4));
                item.MaxParsedId = reader.IsDBNull(5) ? 0 : Convert.ToInt64(reader.GetValue(5));
            }

            // Compute staleness
            if (item.LastInsertUtc.HasValue)
            {
                item.SecondsSinceLastInsert = (int)(DateTime.UtcNow - item.LastInsertUtc.Value).TotalSeconds;
            }

            // ETL lag = rows in Test that haven't been parsed yet 
            item.EtlLag = (int)(item.MaxTestId - item.WatermarkId);

            // Queue depth from the in-process writer
            item.QueueDepth = _dbWriter.QueueDepth;

            // Data flow status logic:
            // No inserts in 30+ minutes during business hours? Something is wrong.
            item.IsFlowing = item.SecondsSinceLastInsert < 1800; // 30 minutes
        }
        catch (Exception ex)
        {
            item.Error = ex.Message;
            item.IsFlowing = false;
        }

        return item;
    }

    // ========================================================================
    // IIS PRODUCTION LOG PROBE — Scan for recent errors in the log file
    // This is what would have caught "Cannot access destination table"
    // ========================================================================
    private RecentErrorsItem ProbeIisLogs()
    {
        var item = new RecentErrorsItem();
        try
        {
            // Check both IIS and dev log directories
            var logDirs = new[]
            {
                ("IIS Production", @"C:\inetpub\Smartpixl.info\Log"),
                ("Dev Instance", "Log"),
            };

            foreach (var (source, dir) in logDirs)
            {
                var fullDir = Path.IsPathRooted(dir) ? dir : Path.Combine(AppContext.BaseDirectory, dir);
                if (!Directory.Exists(fullDir)) continue;

                // Find today's log file (format: yyyy_MM_dd.log)
                var todayFile = Path.Combine(fullDir, DateTime.UtcNow.ToString("yyyy_MM_dd") + ".log");
                if (!File.Exists(todayFile)) continue;

                // Read recent lines (last 200 lines is enough)
                var lines = ReadTailLines(todayFile, 200);
                var errors = new List<string>();
                foreach (var line in lines)
                {
                    if (line.Contains("[ERROR"))
                    {
                        errors.Add(line);
                    }
                }

                if (errors.Count > 0)
                {
                    item.TotalErrorsToday += errors.Count;
                    // Keep last 5 distinct error messages per source
                    var distinct = errors
                        .Select(e => ExtractErrorMessage(e))
                        .Distinct()
                        .TakeLast(5)
                        .Select(msg => new ErrorEntry { Source = source, Message = msg, Count = errors.Count(e => ExtractErrorMessage(e) == msg) })
                        .ToList();
                    item.Errors.AddRange(distinct);

                    // Extract the most recent error timestamp
                    var lastErr = errors.Last();
                    if (lastErr.Length > 25 && DateTime.TryParse(lastErr[1..24], out var ts))
                    {
                        var tsUtc = DateTime.SpecifyKind(ts, DateTimeKind.Utc);
                        if (!item.LastErrorUtc.HasValue || tsUtc > item.LastErrorUtc.Value)
                            item.LastErrorUtc = tsUtc;
                    }
                }
            }

            item.HasErrors = item.TotalErrorsToday > 0;
        }
        catch (Exception ex)
        {
            item.ScanError = ex.Message;
        }

        return item;
    }

    /// <summary>Reads the last N lines from a file without locking it (the logger may be writing).</summary>
    private static List<string> ReadTailLines(string path, int count)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var lines = new List<string>();
            string? line;
            while ((line = sr.ReadLine()) is not null)
                lines.Add(line);
            return lines.Count <= count ? lines : lines.GetRange(lines.Count - count, count);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Extracts the message portion after the level tag from a log line.</summary>
    private static string ExtractErrorMessage(string logLine)
    {
        // Format: [2026-02-14 03:35:39.981] [ERROR  ] Failed to write batch of 1 records: ...
        var idx = logLine.IndexOf("] ", logLine.IndexOf("[ERROR") + 1);
        return idx >= 0 && idx + 2 < logLine.Length ? logLine[(idx + 2)..].Trim() : logLine;
    }

    // ========================================================================
    // OVERALL STATUS COMPUTATION
    // ========================================================================
    private static string ComputeOverallStatus(
        List<ServiceHealthItem> services,
        SqlHealthItem sql,
        List<WebsiteHealthItem> websites,
        AppHealthItem app,
        DataFlowHealthItem dataFlow,
        RecentErrorsItem recentErrors)
    {
        // Critical failures → RED
        if (!sql.IsConnected) return "critical";
        if (services.Any(s => s.Critical && !s.IsRunning)) return "critical";
        if (websites.Any(w => w.Critical && !w.IsHealthy)) return "critical";
        // Data not flowing + recent errors = something is very wrong (exactly today's bug)
        if (!dataFlow.IsFlowing && recentErrors.HasErrors) return "critical";
        // Repeated write errors = critical even if data was flowing before
        if (recentErrors.TotalErrorsToday >= 5) return "critical";

        // Non-critical warnings → YELLOW
        if (services.Any(s => !s.IsRunning)) return "degraded";
        if (websites.Any(w => !w.IsHealthy)) return "degraded";
        if (!dataFlow.IsFlowing) return "degraded";
        if (recentErrors.HasErrors) return "degraded";
        if (app.WorkingSetMB > 500) return "degraded";
        if (dataFlow.QueueDepth > 100) return "degraded";
        if (dataFlow.EtlLag > 100) return "degraded";

        return "healthy";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _lock.Dispose();
    }
}

// ============================================================================
// DTOs
// ============================================================================

public sealed class InfraHealthSnapshot
{
    public DateTime CheckedAt { get; set; }
    public int ProbeTimeMs { get; set; }
    public string OverallStatus { get; set; } = "unknown";
    public List<ServiceHealthItem> Services { get; set; } = [];
    public SqlHealthItem Sql { get; set; } = new();
    public List<WebsiteHealthItem> Websites { get; set; } = [];
    public AppHealthItem App { get; set; } = new();
    public DataFlowHealthItem DataFlow { get; set; } = new();
    public RecentErrorsItem RecentErrors { get; set; } = new();
}

public sealed class ServiceHealthItem
{
    public string Name { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string Status { get; set; } = "Unknown";
    public bool IsRunning { get; set; }
    public bool Critical { get; set; }
    public string? Error { get; set; }
}

public sealed class SqlHealthItem
{
    public bool IsConnected { get; set; }
    public string? Database { get; set; }
    public string? DataSource { get; set; }
    public int ResponseMs { get; set; }
    public int TestRows { get; set; }
    public int ParsedRows { get; set; }
    public long Watermark { get; set; }
    public DateTime? LastEtlRun { get; set; }
    public string? ServerVersion { get; set; }
    public string? Error { get; set; }
}

public sealed class WebsiteHealthItem
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool IsHealthy { get; set; }
    public int StatusCode { get; set; }
    public int ResponseMs { get; set; }
    public bool Critical { get; set; }
    public string? Error { get; set; }
}

public sealed class AppHealthItem
{
    public int ProcessId { get; set; }
    public TimeSpan Uptime { get; set; }
    public double WorkingSetMB { get; set; }
    public int ThreadCount { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public double TotalAllocatedMB { get; set; }
    public double GcHeapMB { get; set; }
    public string MachineName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string DotNetVersion { get; set; } = "";
    public int QueueDepth { get; set; }
}

public sealed class DataFlowHealthItem
{
    public bool IsFlowing { get; set; }
    public DateTime? LastInsertUtc { get; set; }
    public int SecondsSinceLastInsert { get; set; }
    public int HitsLastHour { get; set; }
    public int HitsLast5Min { get; set; }
    public long MaxTestId { get; set; }
    public long WatermarkId { get; set; }
    public long MaxParsedId { get; set; }
    public int EtlLag { get; set; }
    public int QueueDepth { get; set; }
    public string? Error { get; set; }
}

public sealed class RecentErrorsItem
{
    public bool HasErrors { get; set; }
    public int TotalErrorsToday { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public List<ErrorEntry> Errors { get; set; } = [];
    public string? ScanError { get; set; }
}

public sealed class ErrorEntry
{
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public int Count { get; set; }
}
