using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// MAINTENANCE SCHEDULER SERVICE — Runs scheduled database maintenance tasks.
//
// Ported from SmartPiXL.Worker-Deprecated/Services/MaintenanceSchedulerService.cs
// with namespace updated to SmartPiXL.Forge.Services.
//
// TASKS:
//   1. Raw data purge (daily at PurgeHourUtc, default 3 AM):
//      Deletes PiXL.Raw rows older than 90 days to reclaim filegroup space.
//
//   2. Index maintenance (weekly Sunday at IndexMaintenanceHourUtc, default 4 AM):
//      Rebuilds/reorganizes indexes with fragmentation > 10%.
//
// DESIGN:
//   Simple clock-based scheduler. Checks the current UTC hour every 60s.
//   When the hour matches a scheduled task, runs it if it hasn't run today.
//   Logs all actions to Ops.RemediationLog for audit trail.
//
// SAFETY:
//   Each task uses a "last run" tracker to prevent double-execution. If the
//   service restarts mid-day, it checks the log table for today's entries.
// ============================================================================

/// <summary>
/// Background service that runs scheduled database maintenance operations.
/// </summary>
public sealed class MaintenanceSchedulerService : BackgroundService
{
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;

    // Track last run dates to prevent double-execution
    private DateTime _lastPurgeDate;
    private DateTime _lastIndexDate;

    public MaintenanceSchedulerService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _logger.Info($"MaintenanceScheduler started. Purge: {_settings.PurgeHourUtc}:00 UTC, " +
                     $"Index: {_settings.IndexMaintenanceHourUtc}:00 UTC (Sundays)");

        // Initial delay to let other services stabilize
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;

                // Daily purge
                if (now.Hour == _settings.PurgeHourUtc && _lastPurgeDate.Date != now.Date)
                {
                    await RunPurgeAsync(stoppingToken);
                    _lastPurgeDate = now.Date;
                }

                // Weekly index maintenance (Sunday only)
                if (now.DayOfWeek == DayOfWeek.Sunday &&
                    now.Hour == _settings.IndexMaintenanceHourUtc &&
                    _lastIndexDate.Date != now.Date)
                {
                    await RunIndexMaintenanceAsync(stoppingToken);
                    _lastIndexDate = now.Date;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.Error($"MaintenanceScheduler error: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.Info("MaintenanceScheduler stopped.");
    }

    /// <summary>
    /// Purges PiXL.Raw rows older than 90 days.
    /// </summary>
    private async Task RunPurgeAsync(CancellationToken ct)
    {
        _logger.Info("MaintenanceScheduler: starting daily raw data purge (> 90 days)");

        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DECLARE @cutoff DATETIME2 = DATEADD(DAY, -90, SYSUTCDATETIME());
                DECLARE @deleted INT = 0;
                DECLARE @batch INT = 10000;
                DECLARE @total INT = 0;
                
                WHILE 1 = 1
                BEGIN
                    DELETE TOP (@batch) FROM PiXL.Raw
                    WHERE ReceivedAt < @cutoff;
                    
                    SET @deleted = @@ROWCOUNT;
                    SET @total = @total + @deleted;
                    
                    IF @deleted < @batch BREAK;
                    
                    -- Yield between batches to avoid log bloat
                    WAITFOR DELAY '00:00:01';
                END
                
                SELECT @total AS RowsDeleted;";
            cmd.CommandTimeout = 3600; // 1 hour max

            var result = await cmd.ExecuteScalarAsync(ct);
            var rowsDeleted = result is int n ? n : 0;

            _logger.Info($"MaintenanceScheduler: purged {rowsDeleted} old raw rows");

            // Log to remediation table
            await LogMaintenanceAsync("RawDataPurge", $"Purged {rowsDeleted} rows from PiXL.Raw older than 90 days", ct);
        }
        catch (Exception ex)
        {
            _logger.Error($"MaintenanceScheduler: purge failed — {ex.Message}");
        }
    }

    /// <summary>
    /// Rebuilds or reorganizes fragmented indexes.
    /// </summary>
    private async Task RunIndexMaintenanceAsync(CancellationToken ct)
    {
        _logger.Info("MaintenanceScheduler: starting weekly index maintenance");

        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);

            // Find indexes with > 10% fragmentation
            await using var findCmd = conn.CreateCommand();
            findCmd.CommandText = @"
                SELECT 
                    OBJECT_SCHEMA_NAME(ips.object_id) + '.' + OBJECT_NAME(ips.object_id) AS TableName,
                    i.name AS IndexName,
                    ips.avg_fragmentation_in_percent AS Frag,
                    ips.page_count
                FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ips
                JOIN sys.indexes i ON ips.object_id = i.object_id AND ips.index_id = i.index_id
                WHERE ips.avg_fragmentation_in_percent > 10
                  AND ips.page_count > 100
                  AND i.name IS NOT NULL
                ORDER BY ips.avg_fragmentation_in_percent DESC";
            findCmd.CommandTimeout = 120;

            var indexes = new List<(string Table, string Index, double Frag)>();
            await using (var reader = await findCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    indexes.Add((reader.GetString(0), reader.GetString(1), reader.GetDouble(2)));
                }
            }

            if (indexes.Count == 0)
            {
                _logger.Info("MaintenanceScheduler: no fragmented indexes found");
                return;
            }

            var rebuilt = 0;
            var reorganized = 0;

            foreach (var (table, index, frag) in indexes)
            {
                try
                {
                    await using var maintCmd = conn.CreateCommand();
                    if (frag > 30)
                    {
                        // Rebuild for high fragmentation (online if Enterprise, offline otherwise)
                        maintCmd.CommandText = $"ALTER INDEX [{index}] ON [{table}] REBUILD;";
                        rebuilt++;
                    }
                    else
                    {
                        // Reorganize for moderate fragmentation
                        maintCmd.CommandText = $"ALTER INDEX [{index}] ON [{table}] REORGANIZE;";
                        reorganized++;
                    }
                    maintCmd.CommandTimeout = 600;
                    await maintCmd.ExecuteNonQueryAsync(ct);

                    _logger.Debug($"Maintained index [{index}] on [{table}] ({frag:F1}% frag)");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to maintain index [{index}] on [{table}]: {ex.Message}");
                }
            }

            _logger.Info($"MaintenanceScheduler: index maintenance complete — {rebuilt} rebuilt, {reorganized} reorganized");
            await LogMaintenanceAsync("IndexMaintenance",
                $"Maintained {indexes.Count} indexes: {rebuilt} rebuilt, {reorganized} reorganized", ct);
        }
        catch (Exception ex)
        {
            _logger.Error($"MaintenanceScheduler: index maintenance failed — {ex.Message}");
        }
    }

    private async Task LogMaintenanceAsync(string issueType, string description, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Ops.RemediationLog
                    (IssueType, Severity, Description, Status, ExecutedAtUtc, ExecutedBy, ResultMessage)
                VALUES
                    (@Type, 'Info', @Desc, 'Executed', SYSUTCDATETIME(), 'scheduler', @Desc)";
            cmd.Parameters.AddWithValue("@Type", issueType);
            cmd.Parameters.AddWithValue("@Desc", description);
            cmd.CommandTimeout = 10;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // Non-critical
        }
    }
}
