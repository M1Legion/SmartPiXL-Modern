using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Models;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// INFRASTRUCTURE HEALTH SERVICE
//
// Ported from SmartPiXL.Worker-Deprecated/Services/InfraHealthService.cs
// with namespace updated to SmartPiXL.Forge.Services.
//
// Probes Windows services, SQL connectivity, IIS websites, and internal app
// components to build a single JSON snapshot for the Tron dashboard.
//
// Design decisions:
//   - Runs probes in parallel with a 5-second overall timeout per check.
//   - Each probe is independent — a SQL failure won't block the IIS check.
//   - Results are cached for 15 seconds to avoid hammering services on
//     rapid dashboard refreshes.
//   - Service checks use ServiceController (WMI-free, low overhead).
//   - Website checks use lightweight HEAD requests with short timeouts.
// ============================================================================

[SupportedOSPlatform("windows")]
public sealed class InfraHealthService : IDisposable
{
    private readonly TrackingSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly ITrackingLogger _logger;
    private readonly IEdgeHealthClient _edge;

    // Cached result to avoid hammering probes on every 10s dashboard refresh
    private InfraHealthSnapshot? _cached;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public InfraHealthService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger,
        IEdgeHealthClient edge)
    {
        _settings = settings.Value;
        _logger = logger;
        _edge = edge;
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

        // Run all probes in parallel — Task.Run wraps synchronous probe methods
        // so they execute concurrently via Task.WhenAll. This is NOT the hot path —
        // it's a diagnostic service cached for 15s, so Task.Run is appropriate here.
        var servicesTask = Task.Run(ProbeWindowsServices);
        var sqlTask = ProbeSqlAsync();
        var websitesTask = ProbeWebsitesAsync();
        var appTask = Task.Run(ProbeAppComponents);
        var dataFlowTask = ProbeDataFlowAsync();
        var pipelineTask = ProbePipelineAsync();
        var logTask = Task.Run(ProbeIisLogs);

        await Task.WhenAll(servicesTask, sqlTask, websitesTask, appTask, dataFlowTask, pipelineTask, logTask);

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
            Pipeline = pipelineTask.Result,
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
                    (SELECT COUNT(*) FROM PiXL.Raw) AS TestRows,
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
        // Fetch queue depth from the Edge process via HTTP (cached by IEdgeHealthClient)
        var edgeHealth = _edge.GetHealthAsync().GetAwaiter().GetResult();
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
            QueueDepth = edgeHealth.QueueDepth,
        };
    }

    // ========================================================================
    // DATA FLOW PROBE — Is data actually making it into the database?
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
                    (SELECT MAX(ReceivedAt) FROM PiXL.Raw) AS LastInsert,
                    (SELECT COUNT(*) FROM PiXL.Raw WHERE ReceivedAt >= DATEADD(HOUR, -1, GETUTCDATE())) AS HitsLastHour,
                    (SELECT COUNT(*) FROM PiXL.Raw WHERE ReceivedAt >= DATEADD(MINUTE, -5, GETUTCDATE())) AS HitsLast5Min,
                    (SELECT MAX(Id) FROM PiXL.Raw) AS MaxTestId,
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

            // ETL lag = rows in Raw that haven't been parsed yet
            item.EtlLag = (int)(item.MaxTestId - item.WatermarkId);

            // Queue depth from the Edge process via HTTP
            var edgeHealth = await _edge.GetHealthAsync();
            item.QueueDepth = edgeHealth.QueueDepth;

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
    // PIPELINE HEALTH PROBE — Device, IP, Visit, Match table metrics
    // Queries the vw_Dash_PipelineHealth view for a single-row snapshot.
    // ========================================================================
    private async Task<PipelineHealthItem> ProbePipelineAsync()
    {
        var item = new PipelineHealthItem();
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM vw_Dash_PipelineHealth";
            cmd.CommandTimeout = 10;

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                item.TestRows = reader.IsDBNull(reader.GetOrdinal("TestRows")) ? 0 : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("TestRows")));
                item.ParsedRows = reader.IsDBNull(reader.GetOrdinal("ParsedRows")) ? 0 : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("ParsedRows")));
                item.DeviceRows = reader.IsDBNull(reader.GetOrdinal("DeviceRows")) ? 0 : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("DeviceRows")));
                item.IpRows = reader.IsDBNull(reader.GetOrdinal("IpRows")) ? 0 : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("IpRows")));
                item.VisitRows = reader.IsDBNull(reader.GetOrdinal("VisitRows")) ? 0 : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("VisitRows")));
                item.MatchRows = reader.IsDBNull(reader.GetOrdinal("MatchRows")) ? 0 : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("MatchRows")));

                item.MaxTestId = reader.IsDBNull(reader.GetOrdinal("MaxTestId")) ? 0 : Convert.ToInt64(reader.GetValue(reader.GetOrdinal("MaxTestId")));
                item.MaxVisitId = reader.IsDBNull(reader.GetOrdinal("MaxVisitId")) ? 0 : Convert.ToInt64(reader.GetValue(reader.GetOrdinal("MaxVisitId")));
                item.MaxMatchId = reader.IsDBNull(reader.GetOrdinal("MaxMatchId")) ? 0 : Convert.ToInt64(reader.GetValue(reader.GetOrdinal("MaxMatchId")));

                item.ParseWatermark = reader.IsDBNull(reader.GetOrdinal("ParseWatermark")) ? 0 : Convert.ToInt64(reader.GetValue(reader.GetOrdinal("ParseWatermark")));
                item.ParseTotalProcessed = reader.IsDBNull(reader.GetOrdinal("ParseTotalProcessed")) ? 0 : Convert.ToInt64(reader.GetValue(reader.GetOrdinal("ParseTotalProcessed")));
                item.ParseLastRunAt = reader.IsDBNull(reader.GetOrdinal("ParseLastRunAt")) ? null : reader.GetDateTime(reader.GetOrdinal("ParseLastRunAt"));

                item.MatchWatermark = reader.IsDBNull(reader.GetOrdinal("MatchWatermark")) ? 0 : Convert.ToInt64(reader.GetValue(reader.GetOrdinal("MatchWatermark")));
                item.MatchTotalProcessed = reader.IsDBNull(reader.GetOrdinal("MatchTotalProcessed")) ? 0 : Convert.ToInt64(reader.GetValue(reader.GetOrdinal("MatchTotalProcessed")));
                item.MatchTotalMatched = reader.IsDBNull(reader.GetOrdinal("MatchTotalMatched")) ? 0 : Convert.ToInt64(reader.GetValue(reader.GetOrdinal("MatchTotalMatched")));
                item.MatchLastRunAt = reader.IsDBNull(reader.GetOrdinal("MatchLastRunAt")) ? null : reader.GetDateTime(reader.GetOrdinal("MatchLastRunAt"));

                item.MatchesResolved = reader.IsDBNull(reader.GetOrdinal("MatchesResolved")) ? 0 : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("MatchesResolved")));
                item.MatchesPending = reader.IsDBNull(reader.GetOrdinal("MatchesPending")) ? 0 : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("MatchesPending")));
                item.VisitsWithEmail = reader.IsDBNull(reader.GetOrdinal("VisitsWithEmail")) ? 0 : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("VisitsWithEmail")));

                item.ParseLag = reader.IsDBNull(reader.GetOrdinal("ParseLag")) ? 0 : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("ParseLag")));
                item.MatchLag = reader.IsDBNull(reader.GetOrdinal("MatchLag")) ? 0 : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("MatchLag")));

                item.TestLatest = reader.IsDBNull(reader.GetOrdinal("TestLatest")) ? null : reader.GetDateTime(reader.GetOrdinal("TestLatest"));
                item.ParsedLatest = reader.IsDBNull(reader.GetOrdinal("ParsedLatest")) ? null : reader.GetDateTime(reader.GetOrdinal("ParsedLatest"));
                item.DeviceLatest = reader.IsDBNull(reader.GetOrdinal("DeviceLatest")) ? null : reader.GetDateTime(reader.GetOrdinal("DeviceLatest"));
                item.IpLatest = reader.IsDBNull(reader.GetOrdinal("IpLatest")) ? null : reader.GetDateTime(reader.GetOrdinal("IpLatest"));
                item.VisitLatest = reader.IsDBNull(reader.GetOrdinal("VisitLatest")) ? null : reader.GetDateTime(reader.GetOrdinal("VisitLatest"));
                item.MatchLatest = reader.IsDBNull(reader.GetOrdinal("MatchLatest")) ? null : reader.GetDateTime(reader.GetOrdinal("MatchLatest"));

                item.UniqueDevicesInVisits = reader.IsDBNull(reader.GetOrdinal("UniqueDevicesInVisits")) ? 0 : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("UniqueDevicesInVisits")));
                item.UniqueIpsInVisits = reader.IsDBNull(reader.GetOrdinal("UniqueIpsInVisits")) ? 0 : Convert.ToInt32(reader.GetValue(reader.GetOrdinal("UniqueIpsInVisits")));
            }
            item.IsAvailable = true;
        }
        catch (Exception ex)
        {
            item.IsAvailable = false;
            item.Error = ex.Message;
        }

        return item;
    }

    // ========================================================================
    // IIS PRODUCTION LOG PROBE — Scan for recent errors in the log file
    // ========================================================================
    private static readonly TimeSpan RecentErrorWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StaleErrorWindow = TimeSpan.FromHours(2);

    private RecentErrorsItem ProbeIisLogs()
    {
        var item = new RecentErrorsItem();
        var now = DateTime.UtcNow;
        var recentCutoff = now - RecentErrorWindow;
        var staleCutoff = now - StaleErrorWindow;

        try
        {
            var logDirs = new[]
            {
                ("IIS Production", @"C:\inetpub\Smartpixl.info\Log"),
                ("Dev Instance", "Log"),
            };

            foreach (var (source, dir) in logDirs)
            {
                var fullDir = Path.IsPathRooted(dir) ? dir : Path.Combine(AppContext.BaseDirectory, dir);
                if (!Directory.Exists(fullDir)) continue;

                var todayFile = Path.Combine(fullDir, DateTime.UtcNow.ToString("yyyy_MM_dd") + ".log");
                if (!File.Exists(todayFile)) continue;

                var lines = ReadTailLines(todayFile, 200);
                var errorLines = new List<(string Line, DateTime? Timestamp)>();
                foreach (var line in lines)
                {
                    if (line.Contains("[ERROR"))
                    {
                        DateTime? ts = null;
                        if (line.Length > 25 && DateTime.TryParse(line[1..24], out var parsed))
                            ts = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                        errorLines.Add((line, ts));
                    }
                }

                if (errorLines.Count > 0)
                {
                    item.TotalErrorsToday += errorLines.Count;
                    item.RecentErrorCount += errorLines.Count(e => e.Timestamp.HasValue && e.Timestamp.Value >= recentCutoff);

                    var grouped = errorLines
                        .GroupBy(e => ExtractErrorMessage(e.Line))
                        .TakeLast(5)
                        .Select(g =>
                        {
                            var lastSeen = g.Max(e => e.Timestamp);
                            var recentCount = g.Count(e => e.Timestamp.HasValue && e.Timestamp.Value >= recentCutoff);
                            return new ErrorEntry
                            {
                                Source = source,
                                Message = g.Key,
                                Count = g.Count(),
                                RecentCount = recentCount,
                                LastSeenUtc = lastSeen,
                                IsRecent = lastSeen.HasValue && lastSeen.Value >= recentCutoff,
                                IsStale = lastSeen.HasValue && lastSeen.Value < staleCutoff,
                            };
                        })
                        .ToList();
                    item.Errors.AddRange(grouped);

                    var maxTs = errorLines.Where(e => e.Timestamp.HasValue).MaxBy(e => e.Timestamp);
                    if (maxTs.Timestamp.HasValue && (!item.LastErrorUtc.HasValue || maxTs.Timestamp.Value > item.LastErrorUtc.Value))
                        item.LastErrorUtc = maxTs.Timestamp;
                }
            }

            item.HasErrors = item.TotalErrorsToday > 0;
            item.HasRecentErrors = item.RecentErrorCount > 0;
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
        // CRITICAL — something is broken RIGHT NOW and needs intervention
        if (!sql.IsConnected) return "critical";
        if (services.Any(s => s.Critical && !s.IsRunning)) return "critical";
        if (websites.Any(w => w.Critical && !w.IsHealthy)) return "critical";
        if (!dataFlow.IsFlowing && recentErrors.HasRecentErrors) return "critical";
        if (recentErrors.RecentErrorCount >= 5) return "critical";

        // DEGRADED — something warrants attention but isn't an emergency
        if (services.Any(s => !s.IsRunning)) return "degraded";
        if (websites.Any(w => !w.IsHealthy)) return "degraded";
        if (!dataFlow.IsFlowing) return "degraded";
        if (recentErrors.HasRecentErrors) return "degraded";
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
