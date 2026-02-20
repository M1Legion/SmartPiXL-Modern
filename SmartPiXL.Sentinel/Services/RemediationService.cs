using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Sentinel.Services;

// ============================================================================
// REMEDIATION SERVICE — Sentinel-side approve/skip interface for Ops remediation.
//
// The Forge runs the SelfHealingService background loop that DETECTS issues
// and PROPOSES remediations (writes to Ops.RemediationLog with Status='Pending').
//
// This service provides API-callable methods for the dashboard operator to:
//   • List pending remediations
//   • Approve (execute the ActionSql and mark 'Executed')
//   • Skip (mark 'Skipped' without executing)
//
// DOES NOT run a background loop — that's the Forge's responsibility.
// ============================================================================

/// <summary>
/// Provides approve/skip/list operations for the Ops.RemediationLog table.
/// Registered as a singleton in the Sentinel composition root.
/// </summary>
public sealed class RemediationService
{
    private readonly TrackingSettings _settings;
    private readonly IEdgeHealthClient _edge;
    private readonly ITrackingLogger _logger;

    public RemediationService(
        IOptions<TrackingSettings> settings,
        IEdgeHealthClient edge,
        ITrackingLogger logger)
    {
        _settings = settings.Value;
        _edge = edge;
        _logger = logger;
    }

    /// <summary>
    /// Returns all remediation entries, most recent first.
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> ListRemediationsAsync(CancellationToken ct = default)
    {
        var results = new List<Dictionary<string, object?>>();

        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 50 Id, IssueType, Severity, Description,
                   ProposedAction, ActionSql, Status,
                   DetectedAtUtc, ExecutedAtUtc, ExecutedBy, ResultMessage
            FROM Ops.RemediationLog
            ORDER BY Id DESC";
        cmd.CommandTimeout = 10;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                row[reader.GetName(i)] = value == DBNull.Value ? null : value;
            }
            results.Add(row);
        }

        return results;
    }

    /// <summary>
    /// Executes an approved remediation by its ID. Called from the dashboard API.
    /// </summary>
    public async Task<(bool Success, string Message)> ExecuteRemediationAsync(int id, CancellationToken ct = default)
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
    public async Task<bool> SkipRemediationAsync(int id, CancellationToken ct = default)
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
