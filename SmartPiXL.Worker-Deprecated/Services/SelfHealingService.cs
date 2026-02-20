using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TrackingPixel.Configuration;
using TrackingPixel.Models;

namespace TrackingPixel.Services;

// ============================================================================
// SELF-HEALING SERVICE — Automated ops remediation orchestrator.
//
// ARCHITECTURE:
//   InfraHealthService.GetHealthAsync()  →  SelfHealingService (decision tree)
//   DatabaseWriterService.Circuit        →    ↓
//                                         Classify issues
//                                         Log to Ops.RemediationLog
//                                         Auto-execute safe actions
//                                         Email operator for destructive actions
//
// LOOP TIMING:
//   Runs every 60s, offset 30s from EtlBackgroundService. This ensures the
//   health probe is fresh (InfraHealthService caches for 15s) while avoiding
//   overlap with ETL's own 60s cycle.
//
// DECISION TREE:
//   Each detected issue is classified by IssueType and Severity:
//   - Auto-execute: Safe, idempotent, non-destructive (watermark reset, ETL catch-up, default filegroup fix)
//   - Pending approval: Destructive or ambiguous (purge data, move filegroups)
//   - Info-only: Logged but no action needed (stale errors, healthy state)
//
// PROACTIVE CHECKS:
//   - Filegroup files approaching MAXSIZE (> 80%) — alerts BEFORE error 1105
//   - User objects on PRIMARY filegroup — wrong placement, needs manual move
//   - Wrong default filegroup — auto-corrected to SmartPiXL
//
// DE-DUPLICATION:
//   Same (IssueType, Pending|Executed) combination within 2h is not re-logged.
//   This prevents the log from filling up during sustained failure conditions.
//
// EMAIL THROTTLE:
//   Delegated to EmailNotificationService which tracks 1 per issue type per hour.
// ============================================================================

/// <summary>
/// Background service that monitors infrastructure health and automatically
/// remediates common operational issues. Conservative model: auto-executes
/// safe actions, requires manual approval for anything destructive.
/// </summary>
public sealed class SelfHealingService : BackgroundService
{
    private readonly InfraHealthService _health;
    private readonly IEdgeHealthClient _edge;
    private readonly EmailNotificationService _email;
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;
    
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(2);
    
    public SelfHealingService(
        InfraHealthService health,
        IEdgeHealthClient edge,
        EmailNotificationService email,
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _health = health;
        _edge = edge;
        _email = email;
        _settings = settings.Value;
        _logger = logger;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        
        _logger.Info("SelfHealingService started. Loop interval: 60s");
        
        // Offset 30s from EtlBackgroundService to avoid probe contention
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunHealingCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error($"SelfHealingService cycle error: {ex.Message}");
            }
            
            try
            {
                await Task.Delay(LoopInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
        
        _logger.Info("SelfHealingService stopped.");
    }
    
    /// <summary>
    /// One healing cycle: probe health, check circuit breaker, classify issues, remediate.
    /// </summary>
    private async Task RunHealingCycleAsync(CancellationToken ct)
    {
        // 1. Check circuit breaker state (doesn't need health probe)
        await CheckCircuitBreakerAsync(ct);
        
        // 2. Get full health snapshot
        InfraHealthSnapshot snapshot;
        try
        {
            snapshot = await _health.GetHealthAsync();
        }
        catch (Exception ex)
        {
            _logger.Warning($"SelfHealingService: health probe failed — {ex.Message}");
            return;
        }
        
        // 3. Check each dimension
        await CheckEtlLagAsync(snapshot, ct);
        await CheckDataFlowAsync(snapshot, ct);
        await CheckSqlConnectivityAsync(snapshot, ct);
        await CheckRecentErrorsAsync(snapshot, ct);
        await CheckDiskSpaceAsync(ct);
        await CheckFilegroupSpaceAsync(ct);
    }
    
    // ════════════════════════════════════════════════════════════════════
    // CIRCUIT BREAKER CHECKS
    // ════════════════════════════════════════════════════════════════════
    
    private async Task CheckCircuitBreakerAsync(CancellationToken ct)
    {
        var edgeHealth = await _edge.GetHealthAsync(ct);
        if (!edgeHealth.IsReachable || edgeHealth.Circuit == "Closed") return;
        
        var reason = edgeHealth.LastTripReason ?? "Unknown";
        var issueType = reason switch
        {
            "PrimaryFilegroupFull" => "PrimaryFilegroupFull",
            "DiskFull" => "DiskFull",
            "TransactionLogFull" => "TransactionLogFull",
            _ => "CircuitBreakerOpen"
        };
        
        var severity = issueType switch
        {
            "PrimaryFilegroupFull" => "Critical",
            _ => "Warning"
        };
        
        string? actionSql = issueType switch
        {
            // DiskFull: suggest purge of old raw data as auto-action
            "DiskFull" => "EXEC ETL.usp_PurgeRawData @RetainDays = 30;",
            "TransactionLogFull" => null, // Manual: shrink log or add space
            "PrimaryFilegroupFull" => null, // Manual: move object to correct filegroup
            _ => null
        };
        
        var description = issueType switch
        {
            "PrimaryFilegroupFull" =>
                "Circuit breaker tripped: object is on PRIMARY filegroup. " +
                "Move the SmartPiXL objects to the SmartPiXL filegroup. " +
                "This requires manual ALTER TABLE ... MOVE or rebuilding indexes on the correct filegroup.",
            "DiskFull" =>
                "Circuit breaker tripped: named filegroup full (disk space). " +
                "Auto-purge of raw data > 30 days can reclaim space.",
            "TransactionLogFull" =>
                "Circuit breaker tripped: transaction log full. " +
                "Check recovery model, shrink log, or add disk space.",
            _ =>
                $"Circuit breaker tripped: {reason}. Investigate SQL connectivity and recent errors."
        };
        
        var status = actionSql is not null ? "Pending" : "Pending"; // All go to Pending initially
        // For DiskFull with auto-action, we could auto-execute, but purge is destructive
        // so we require approval per the conservative model.
        
        await LogRemediationAsync(issueType, severity, description, actionSql, status, ct);
        
        await _email.NotifyAsync(issueType,
            $"Circuit Breaker: {issueType}",
            $"{description}\n\nCircuit state: {edgeHealth.Circuit}\nApprove or skip via /api/dash/remediations");
    }
    
    // ════════════════════════════════════════════════════════════════════
    // ETL LAG — Auto-remediate by running catch-up procs
    // ════════════════════════════════════════════════════════════════════
    
    private async Task CheckEtlLagAsync(InfraHealthSnapshot snapshot, CancellationToken ct)
    {
        var pipeline = snapshot.Pipeline;
        if (!pipeline.IsAvailable) return;
        
        // Parse lag > 500 rows = ETL might be stalled
        if (pipeline.ParseLag > 500)
        {
            _logger.Info($"SelfHealingService: parse lag {pipeline.ParseLag}, running catch-up ETL");
            
            try
            {
                await using var conn = new SqlConnection(_settings.ConnectionString);
                await conn.OpenAsync(ct);
                
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "EXEC ETL.usp_ParseNewHits;";
                cmd.CommandTimeout = 300;
                await cmd.ExecuteNonQueryAsync(ct);
                
                await LogRemediationAsync("EtlParseLag", "Info",
                    $"Parse lag was {pipeline.ParseLag}. Ran ETL.usp_ParseNewHits catch-up.",
                    "EXEC ETL.usp_ParseNewHits;", "Executed", ct);
            }
            catch (Exception ex)
            {
                _logger.Warning($"SelfHealingService: ETL catch-up failed — {ex.Message}");
                await LogRemediationAsync("EtlParseLag", "Warning",
                    $"Parse lag {pipeline.ParseLag}. Catch-up attempt failed: {ex.Message}",
                    null, "Failed", ct);
            }
        }
        
        // Match lag > 500 = match ETL behind
        if (pipeline.MatchLag > 500)
        {
            _logger.Info($"SelfHealingService: match lag {pipeline.MatchLag}, running catch-up");
            
            try
            {
                await using var conn = new SqlConnection(_settings.ConnectionString);
                await conn.OpenAsync(ct);
                
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "EXEC ETL.usp_MatchVisits;";
                cmd.CommandTimeout = 300;
                await cmd.ExecuteNonQueryAsync(ct);
                
                await LogRemediationAsync("EtlMatchLag", "Info",
                    $"Match lag was {pipeline.MatchLag}. Ran ETL.usp_MatchVisits catch-up.",
                    "EXEC ETL.usp_MatchVisits;", "Executed", ct);
            }
            catch (Exception ex)
            {
                _logger.Warning($"SelfHealingService: match catch-up failed — {ex.Message}");
            }
        }
        
        // Watermark ahead of max ID = watermark corrupt, needs reset
        if (pipeline.ParseWatermark > pipeline.MaxTestId && pipeline.MaxTestId > 0)
        {
            var resetSql = "UPDATE ETL.Watermark SET LastProcessedId = 0, RowsProcessed = 0 WHERE ProcessName = 'ParseNewHits';";
            await LogRemediationAsync("WatermarkAhead", "Critical",
                $"Parse watermark ({pipeline.ParseWatermark}) is ahead of max PiXL.Raw ID ({pipeline.MaxTestId}). " +
                "This means ETL will never process new rows until the watermark is reset.",
                resetSql, "Pending", ct);
            
            await _email.NotifyAsync("WatermarkAhead",
                "ETL Watermark Corrupt",
                $"Parse watermark ({pipeline.ParseWatermark}) > max PiXL.Raw ID ({pipeline.MaxTestId}).\n" +
                $"Approve watermark reset via /api/dash/remediations");
        }
    }
    
    // ════════════════════════════════════════════════════════════════════
    // DATA FLOW — Detect ingestion stall
    // ════════════════════════════════════════════════════════════════════
    
    private async Task CheckDataFlowAsync(InfraHealthSnapshot snapshot, CancellationToken ct)
    {
        var flow = snapshot.DataFlow;
        
        // No data flowing for > 15 minutes during expected traffic hours
        if (!flow.IsFlowing && flow.SecondsSinceLastInsert > 900)
        {
            await LogRemediationAsync("DataFlowStalled", "Warning",
                $"No new data for {flow.SecondsSinceLastInsert / 60} minutes. " +
                $"Last insert: {flow.LastInsertUtc:u}. Queue depth: {flow.QueueDepth}. " +
                "Check IIS logs and tracking endpoint health.",
                null, "Pending", ct);
            
            await _email.NotifyAsync("DataFlowStalled",
                "Data Ingestion Stalled",
                $"No new hits for {flow.SecondsSinceLastInsert / 60} minutes.\n" +
                $"Last insert: {flow.LastInsertUtc:u}\nQueue depth: {flow.QueueDepth}");
        }
    }
    
    // ════════════════════════════════════════════════════════════════════
    // SQL CONNECTIVITY
    // ════════════════════════════════════════════════════════════════════
    
    private async Task CheckSqlConnectivityAsync(InfraHealthSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Sql.IsConnected) return;
        
        await LogRemediationAsync("SqlDisconnected", "Critical",
            $"SQL Server connectivity lost. Error: {snapshot.Sql.Error ?? "unknown"}",
            null, "Pending", ct);
        
        await _email.NotifyAsync("SqlDisconnected",
            "SQL Server Connectivity Lost",
            $"Cannot connect to SQL Server.\nError: {snapshot.Sql.Error}\n" +
            "Check SQL Server service status and connection string.");
    }
    
    // ════════════════════════════════════════════════════════════════════
    // RECENT ERRORS — Notify on sustained error patterns
    // ════════════════════════════════════════════════════════════════════
    
    private async Task CheckRecentErrorsAsync(InfraHealthSnapshot snapshot, CancellationToken ct)
    {
        var errors = snapshot.RecentErrors;
        if (!errors.HasRecentErrors || errors.RecentErrorCount < 5) return;
        
        var topErrors = string.Join("\n", errors.Errors
            .Where(e => e.IsRecent)
            .OrderByDescending(e => e.RecentCount)
            .Take(3)
            .Select(e => $"  [{e.RecentCount}x] {e.Message}"));
        
        await LogRemediationAsync("RecentErrors", "Warning",
            $"{errors.RecentErrorCount} recent errors in last 30 min. Top:\n{topErrors}",
            null, "Pending", ct);
        
        await _email.NotifyAsync("RecentErrors",
            $"{errors.RecentErrorCount} Recent Errors",
            $"{errors.RecentErrorCount} errors in last 30 minutes:\n{topErrors}");
    }
    
    // ════════════════════════════════════════════════════════════════════
    // DISK SPACE — Probe via SQL sys.dm_os_volume_stats
    // ════════════════════════════════════════════════════════════════════
    
    private async Task CheckDiskSpaceAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);
            
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT DISTINCT
                    vs.volume_mount_point,
                    vs.available_bytes / 1048576 AS FreeMB,
                    vs.total_bytes / 1048576 AS TotalMB
                FROM sys.master_files mf
                CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) vs
                WHERE mf.database_id = DB_ID()";
            cmd.CommandTimeout = 10;
            
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var mount = reader.GetString(0);
                var freeMb = reader.GetInt64(1);
                var totalMb = reader.GetInt64(2);
                var pctFree = totalMb > 0 ? (double)freeMb / totalMb * 100 : 100;
                
                if (pctFree < 5 || freeMb < 2048) // < 5% or < 2GB
                {
                    var severity = freeMb < 512 ? "Critical" : "Warning";
                    await LogRemediationAsync("LowDiskSpace", severity,
                        $"Volume {mount}: {freeMb:N0} MB free of {totalMb:N0} MB ({pctFree:F1}% free)",
                        null, "Pending", ct);
                    
                    await _email.NotifyAsync("LowDiskSpace",
                        $"Low Disk Space: {mount}",
                        $"Volume {mount}: {freeMb:N0} MB free of {totalMb:N0} MB ({pctFree:F1}% free)");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"SelfHealingService: disk space probe failed — {ex.Message}");
        }
    }
    
    // ════════════════════════════════════════════════════════════════════
    // FILEGROUP SPACE — Proactive check BEFORE error 1105 fires
    // ════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Proactively checks database filegroup health:
    /// <list type="number">
    ///   <item>Any file approaching its MAXSIZE cap (> 90% full)</item>
    ///   <item>Any user objects on the PRIMARY filegroup (should be on SmartPiXL)</item>
    ///   <item>PRIMARY is not the default filegroup</item>
    /// </list>
    /// This prevents the exact outage that error 1105 causes — we detect pressure
    /// before the filegroup fills and data starts dropping.
    /// </summary>
    private async Task CheckFilegroupSpaceAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);
            
            // ── Check 1: Files approaching maxsize ─────────────────────
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT
                        f.name AS LogicalName,
                        fg.name AS FileGroup,
                        CAST(f.size * 8.0 / 1024 AS DECIMAL(12,1)) AS SizeMB,
                        CAST(f.max_size * 8.0 / 1024 AS DECIMAL(12,1)) AS MaxSizeMB,
                        CASE WHEN f.max_size > 0
                             THEN CAST(f.size * 100.0 / f.max_size AS DECIMAL(5,1))
                             ELSE 0 END AS PctUsed
                    FROM sys.database_files f
                    LEFT JOIN sys.filegroups fg ON f.data_space_id = fg.data_space_id
                    WHERE f.type = 0  -- data files only, not log
                      AND f.max_size > 0  -- has a cap (not UNLIMITED, not NO GROWTH=0)
                      AND f.size * 100.0 / NULLIF(f.max_size, 0) > 80";
                cmd.CommandTimeout = 10;
                
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var name = reader.GetString(0);
                    var fg = reader.IsDBNull(1) ? "N/A" : reader.GetString(1);
                    var sizeMb = reader.GetDecimal(2);
                    var maxMb = reader.GetDecimal(3);
                    var pct = reader.GetDecimal(4);
                    
                    var severity = pct >= 95 ? "Critical" : "Warning";
                    var issueType = fg == "PRIMARY" ? "PrimaryFilegroupNearFull" : "FilegroupNearFull";
                    
                    var description = $"File '{name}' on filegroup [{fg}] is {pct}% of its MAXSIZE " +
                                      $"({sizeMb:N1} MB / {maxMb:N1} MB). ";
                    
                    if (fg == "PRIMARY")
                    {
                        description += "PRIMARY should not hold user data — check for misplaced objects. " +
                                       "If objects are on PRIMARY, rebuild their clustered index ON [SmartPiXL].";
                    }
                    else
                    {
                        description += "Increase MAXSIZE or add a file to the filegroup.";
                    }
                    
                    await LogRemediationAsync(issueType, severity, description, null, "Pending", ct);
                    
                    await _email.NotifyAsync(issueType,
                        $"Filegroup Alert: [{fg}] {pct}% full",
                        description);
                }
            }
            
            // ── Check 2: User objects on PRIMARY filegroup ─────────────
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT TOP 5
                        s.name + '.' + t.name AS TableName,
                        i.name AS IndexName,
                        fg.name AS FileGroup
                    FROM sys.tables t
                    JOIN sys.schemas s ON t.schema_id = s.schema_id
                    JOIN sys.indexes i ON t.object_id = i.object_id
                    JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
                    JOIN sys.allocation_units a ON p.partition_id = a.container_id
                    JOIN sys.filegroups fg ON a.data_space_id = fg.data_space_id
                    WHERE fg.name = 'PRIMARY'
                    GROUP BY s.name, t.name, i.name, fg.name";
                cmd.CommandTimeout = 10;
                
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                var misplaced = new List<string>();
                while (await reader.ReadAsync(ct))
                {
                    misplaced.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
                }
                
                if (misplaced.Count > 0)
                {
                    var objects = string.Join(", ", misplaced);
                    var description = $"Objects on PRIMARY filegroup (should be on SmartPiXL): {objects}. " +
                                      "Rebuild clustered indexes with ON [SmartPiXL] to move them.";
                    
                    await LogRemediationAsync("ObjectsOnPrimary", "Critical", description, null, "Pending", ct);
                    
                    await _email.NotifyAsync("ObjectsOnPrimary",
                        "CRITICAL: Objects on PRIMARY filegroup",
                        description);
                }
            }
            
            // ── Check 3: Ensure SmartPiXL is the default filegroup ─────
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT fg.name
                    FROM sys.filegroups fg
                    WHERE fg.is_default = 1";
                cmd.CommandTimeout = 10;
                
                var defaultFg = (string?)await cmd.ExecuteScalarAsync(ct);
                if (defaultFg is not null && defaultFg != "SmartPiXL")
                {
                    var description = $"Default filegroup is [{defaultFg}], should be [SmartPiXL]. " +
                                      "New objects will land on the wrong filegroup. " +
                                      "Fix: ALTER DATABASE SmartPiXL MODIFY FILEGROUP [SmartPiXL] DEFAULT";
                    
                    // Auto-fix: this is safe and idempotent
                    var fixSql = "ALTER DATABASE SmartPiXL MODIFY FILEGROUP [SmartPiXL] DEFAULT";
                    
                    await LogRemediationAsync("WrongDefaultFilegroup", "Critical", description, fixSql, "Executed", ct);
                    
                    // Actually execute the fix — changing the default filegroup is safe
                    await using var fixCmd = conn.CreateCommand();
                    fixCmd.CommandText = fixSql;
                    fixCmd.CommandTimeout = 10;
                    await fixCmd.ExecuteNonQueryAsync(ct);
                    
                    _logger.Warning($"SelfHealingService: auto-fixed default filegroup from [{defaultFg}] to [SmartPiXL]");
                    
                    await _email.NotifyAsync("WrongDefaultFilegroup",
                        "Auto-Fixed: Default filegroup corrected",
                        $"Changed default filegroup from [{defaultFg}] to [SmartPiXL]. {description}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"SelfHealingService: filegroup space probe failed — {ex.Message}");
        }
    }
    
    // ════════════════════════════════════════════════════════════════════
    // REMEDIATION LOG HELPERS
    // ════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Logs a remediation entry to Ops.RemediationLog with de-duplication.
    /// Same (IssueType, Pending|Executed) within 2h is not re-logged.
    /// </summary>
    private async Task LogRemediationAsync(
        string issueType, string severity, string description,
        string? actionSql, string status, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);
            
            // De-dupe: skip if same issue type already logged within the window
            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = @"
                SELECT COUNT(*) FROM Ops.RemediationLog
                WHERE IssueType = @IssueType
                  AND Status IN ('Pending', 'Executed')
                  AND DetectedAtUtc > DATEADD(HOUR, -2, SYSUTCDATETIME())";
            checkCmd.Parameters.AddWithValue("@IssueType", issueType);
            checkCmd.CommandTimeout = 10;
            
            var existing = (int)(await checkCmd.ExecuteScalarAsync(ct))!;
            if (existing > 0) return;
            
            await using var insertCmd = conn.CreateCommand();
            insertCmd.CommandText = @"
                INSERT INTO Ops.RemediationLog
                    (IssueType, Severity, Description, RecommendedAction, ActionSql, Status, ExecutedBy)
                VALUES
                    (@IssueType, @Severity, @Description, @RecommendedAction, @ActionSql, @Status, @ExecutedBy)";
            insertCmd.Parameters.AddWithValue("@IssueType", issueType);
            insertCmd.Parameters.AddWithValue("@Severity", severity);
            insertCmd.Parameters.AddWithValue("@Description", description);
            insertCmd.Parameters.AddWithValue("@RecommendedAction",
                (object?)GetRecommendedAction(issueType) ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@ActionSql", (object?)actionSql ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@Status", status);
            insertCmd.Parameters.AddWithValue("@ExecutedBy",
                status == "Executed" ? "auto" : (object)DBNull.Value);
            insertCmd.CommandTimeout = 10;
            
            await insertCmd.ExecuteNonQueryAsync(ct);
            _logger.Info($"Remediation logged: [{severity}] {issueType} → {status}");
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to log remediation ({issueType}): {ex.Message}");
        }
    }
    
    /// <summary>Returns a human-readable recommended action for the given issue type.</summary>
    private static string? GetRecommendedAction(string issueType) => issueType switch
    {
        "PrimaryFilegroupFull" =>
            "Move objects to the SmartPiXL filegroup using ALTER TABLE ... REBUILD WITH (DATA_COMPRESSION = PAGE) ON [SmartPiXL]",
        "DiskFull" =>
            "Approve auto-purge of raw data > 30 days, or add disk space to the volume",
        "TransactionLogFull" =>
            "Check recovery model (should be SIMPLE for dev). Shrink log or add disk space.",
        "WatermarkAhead" =>
            "Approve watermark reset to LastProcessedId = 0",
        "DataFlowStalled" =>
            "Check IIS app pool status, web.config, and recent IIS logs for errors",
        "SqlDisconnected" =>
            "Check SQL Server service (MSSQL$SQL2025), connection string, and Windows auth",
        "LowDiskSpace" =>
            "Free disk space: purge old logs, shrink tempdb, or expand the volume",
        "EtlParseLag" =>
            "Run EXEC ETL.usp_ParseNewHits manually to catch up",
        _ => null
    };
    
    /// <summary>
    /// Executes an approved remediation by its ID. Called from the dashboard API.
    /// </summary>
    public async Task<(bool Success, string Message)> ExecuteRemediationAsync(int id, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct);
        
        // Read the pending remediation
        await using var readCmd = conn.CreateCommand();
        readCmd.CommandText = @"
            SELECT IssueType, ActionSql FROM Ops.RemediationLog
            WHERE Id = @Id AND Status = 'Pending'";
        readCmd.Parameters.AddWithValue("@Id", id);
        readCmd.CommandTimeout = 10;
        
        string? issueType = null, actionSql = null;
        await using (var reader = await readCmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                return (false, "Remediation not found or not in Pending status");
            issueType = reader.GetString(0);
            actionSql = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        
        if (string.IsNullOrEmpty(actionSql))
            return (false, "No SQL action defined for this remediation");
        
        try
        {
            await using var execCmd = conn.CreateCommand();
            execCmd.CommandText = actionSql;
            execCmd.CommandTimeout = 300;
            await execCmd.ExecuteNonQueryAsync(ct);
            
            // Update status to Executed
            await using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = @"
                UPDATE Ops.RemediationLog
                SET Status = 'Executed', ExecutedAtUtc = SYSUTCDATETIME(),
                    ExecutedBy = 'operator', ResultMessage = 'Approved and executed successfully'
                WHERE Id = @Id";
            updateCmd.Parameters.AddWithValue("@Id", id);
            updateCmd.CommandTimeout = 10;
            await updateCmd.ExecuteNonQueryAsync(ct);
            
            // If this was a circuit breaker issue, try to reset it
            if (issueType is "PrimaryFilegroupFull" or "DiskFull" or "TransactionLogFull")
                await _edge.ResetCircuitAsync(ct);
            
            _logger.Info($"Remediation #{id} ({issueType}) executed by operator");
            return (true, "Executed successfully");
        }
        catch (Exception ex)
        {
            // Mark as Failed
            await using var failCmd = conn.CreateCommand();
            failCmd.CommandText = @"
                UPDATE Ops.RemediationLog
                SET Status = 'Failed', ExecutedAtUtc = SYSUTCDATETIME(),
                    ExecutedBy = 'operator', ResultMessage = @Msg
                WHERE Id = @Id";
            failCmd.Parameters.AddWithValue("@Id", id);
            failCmd.Parameters.AddWithValue("@Msg", ex.Message);
            failCmd.CommandTimeout = 10;
            await failCmd.ExecuteNonQueryAsync(ct);
            
            return (false, $"Execution failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Skips a pending remediation (operator decided it's not needed).
    /// </summary>
    public async Task<bool> SkipRemediationAsync(int id, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct);
        
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Ops.RemediationLog
            SET Status = 'Skipped', ExecutedAtUtc = SYSUTCDATETIME(),
                ExecutedBy = 'operator', ResultMessage = 'Skipped by operator'
            WHERE Id = @Id AND Status = 'Pending'";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.CommandTimeout = 10;
        
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
}
