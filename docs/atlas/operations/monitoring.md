---
subsystem: monitoring
title: Monitoring
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - operations/troubleshooting
  - operations/deployment
  - architecture/forge
  - architecture/sentinel
---

# Monitoring

## Atlas Public

SmartPiXL includes comprehensive monitoring that tracks platform health in real time. The system monitors data pipeline throughput, service availability, and infrastructure health — alerting the operations team before issues affect data collection.

## Atlas Internal

### What's Monitored

| Component | What's Checked | Frequency | Alert Threshold |
|-----------|---------------|-----------|-----------------|
| **IIS Edge** | HTTP response, circuit breaker state, queue depth | Every 60s | Circuit open or queue > 1000 |
| **Forge Service** | Windows service status, ETL throughput, pipe listener | Every 60s | Service stopped or ETL lag > 5 min |
| **SQL Server** | Connectivity, filegroup capacity, query performance | Every 60s | Connection failure or filegroup > 80% |
| **ETL Pipeline** | Watermark lag (pending rows), processing rate | Every 60s | Lag > 10,000 rows or rate < 100 rows/min |
| **Named Pipe** | Connection status, failover file accumulation | Continuous | Failover files accumulating |

### Health Dashboard

The Tron dashboard (served by Sentinel on port 7500) provides:
- Real-time infrastructure health status (green/yellow/red per component)
- ETL pipeline throughput graphs
- Per-customer traffic volume and quality trends
- Recent self-healing actions and their outcomes

### Alert Channels

| Severity | Response | Channel |
|----------|----------|---------|
| Info | Logged only | Forge log files |
| Warning | Logged + operator alert | Email notification |
| Critical | Logged + alert + auto-remediation attempt | Email + self-healing action |

## Atlas Technical

### InfraHealthService

The `InfraHealthService` (Forge) builds a comprehensive health snapshot every 60 seconds:

```csharp
public sealed class InfraHealthService
{
    // Cached for 15s to avoid hammering on rapid dashboard refreshes
    public async Task<InfraHealthSnapshot> GetHealthAsync()
    {
        // Probes run in parallel with 5-second per-probe timeout
        return await ProbeAllAsync();
    }
}
```

**Probes executed in parallel:**

| Probe | Target | Method | Timeout |
|-------|--------|--------|---------|
| SQL connectivity | `localhost\SQL2025` | SqlConnection.Open | 5s |
| Edge health | `http://127.0.0.1:{6000|7000}/internal/health` | HTTP GET | 5s |
| Forge pipe listener | Named pipe existence | File system check | Instant |
| IIS website | HTTP HEAD to public URL | HTTP HEAD | 5s |
| Windows services | `SmartPiXL-Forge`, `SmartPiXL-Sentinel` | ServiceController | 2s |
| Disk space | Application directories | DriveInfo.AvailableFreeSpace | Instant |

Each probe is independent — a SQL failure doesn't block the IIS check.

### Edge Internal Endpoints

The Edge exposes localhost-only endpoints for health monitoring:

```
GET  /internal/health        → EdgeHealthStatus JSON
POST /internal/circuit-reset → Reset circuit breaker
POST /internal/geo-cache/clear → Invalidate geo cache
```

`EdgeHealthStatus` response:

```json
{
    "circuit": "Closed",
    "lastTripReason": null,
    "queueDepth": 12,
    "uptimeSeconds": 86400.5,
    "isReachable": true
}
```

Security: loopback only (127.0.0.1 / ::1). Returns 404 for non-loopback requests.

### SelfHealingService Decision Loop

```
Every 60s (offset 30s from ETL):
  1. Call InfraHealthService.GetHealthAsync()
  2. Classify each issue (IssueType + Severity)
  3. De-duplicate (same issue within 2h → skip)
  4. Auto-execute safe actions (watermark reset, default filegroup fix)
  5. Log to Ops.RemediationLog table
  6. Email operator for destructive/manual actions
```

**Proactive checks (before failure):**
- Filegroup files approaching MAXSIZE (> 80%) — alerts before SQL error 1105
- User objects on PRIMARY filegroup — wrong placement, needs manual move
- Default filegroup is PRIMARY instead of SmartPiXL — auto-corrected

### Pipeline Statistics

`ETL.usp_PipelineStatistics` returns:

```sql
SELECT   
    w.ProcessName,
    w.LastProcessedId,
    w.LastRunAt,
    (SELECT MAX(Id) FROM PiXL.Test) - w.LastProcessedId AS PendingRows,
    (SELECT COUNT(*) FROM PiXL.Parsed WHERE SourceId > w.LastProcessedId) AS ParsedAhead,
    DATEDIFF(SECOND, w.LastRunAt, GETUTCDATE()) AS SecondsSinceLastRun
FROM ETL.Watermark w;
```

### Email Notification Service

`EmailNotificationService` (Forge) sends operator alerts:
- Throttled: 1 email per issue type per hour
- Uses SmtpClient (configured in ForgeSettings)
- Includes: issue description, auto-remediation result (if attempted), suggested manual action

### Forge Log Files

Location: `C:\Services\SmartPiXL-Forge\Log\{yyyy_MM_dd}.log`

Format: `[timestamp] [LEVEL] message`

Key log messages to watch:
- `"EnrichmentPipelineService started"` — Forge pipeline is running
- `"SQL writer channel full — dropping enriched record"` — Backpressure issue
- `"Enrichment pipeline: {n} records processed"` — Throughput counter (every 10K)
- `"EtlBackgroundService: {n} rows parsed"` — ETL progress
- `"SelfHealingService: Auto-executed {action}"` — Auto-remediation occurred

### Edge Log Files

Location: `C:\inetpub\Smartpixl.info\Log\{yyyy_MM_dd}.log`

Key log messages:
- `"Pipe unavailable — wrote to JSONL failover"` — Forge is down
- `"Failed to write batch"` — SQL write failure (circuit breaker may trip)
- `"Circuit breaker tripped"` — 3 consecutive SQL failures

### Sentinel Dashboard Endpoints

Sentinel exposes monitoring data as API:

```
GET /api/dash/health     → Infrastructure health snapshot
GET /api/dash/pipeline   → ETL pipeline statistics
GET /api/dash/services   → Windows service status
GET /api/dash/traffic    → Real-time traffic counters
```

## Atlas Private

### Monitoring Gaps

1. **No external uptime monitoring** — All monitoring is internal (Forge probes Edge, Forge probes SQL). If the entire server goes down, there's no external alert. Consider adding an external ping service (UptimeRobot, Pingdom).

2. **No metric time-series** — Health snapshots are point-in-time (cached 15s). Historical health data isn't stored. The `Ops.RemediationLog` table captures issues and actions, but not continuous health metrics. For proper time-series monitoring, integrate with Prometheus/Grafana or similar.

3. **Log rotation** — Log files are created daily but never purged. At ~10MB/day, this is ~3.6GB/year. Not urgent but should eventually have a retention policy.

4. **Email as alert channel** — Email is unreliable for critical alerts (delayed, filtered, ignored). For production critical monitoring, consider integrating PagerDuty or similar on-call alerting.

### Health Probe Interference

The `InfraHealthService` sends an HTTP GET to the Edge every 60 seconds. This creates a synthetic hit that:
- Appears in IIS logs (but NOT in PiXL.Raw because it doesn't match the tracking endpoint pattern)
- Consumes an IIS thread briefly
- At 1 request/minute, this is negligible

The health endpoint is `/internal/health`, not `/_SMART.GIF`, so it doesn't trigger the tracking pipeline.

### Self-Healing Limitations

The `SelfHealingService` can auto-execute safe actions but cannot:
- Restart Windows services (requires admin privileges the Forge service account may not have)
- Modify IIS configuration (separate process space)
- Restart SQL Server (obviously)
- Modify firewall rules

For critical failures (Forge crashed, SQL Server down), the only recourse is operator intervention. The email notification is the escalation path.

### Ops.RemediationLog Schema

```sql
Ops.RemediationLog (
    Id              BIGINT IDENTITY PK,
    IssueType       VARCHAR(50),
    Severity        VARCHAR(20),
    Description     NVARCHAR(1000),
    ActionTaken     VARCHAR(50),      -- 'AutoExecuted', 'PendingApproval', 'InfoOnly'
    ActionResult    NVARCHAR(500),
    DetectedAt      DATETIME2(3),
    ResolvedAt      DATETIME2(3) NULL
)
```

The SelfHealingService uses this table for de-duplication (same IssueType + ActionTaken within 2 hours is not re-logged).

### MaintenanceSchedulerService

The `MaintenanceSchedulerService` (Forge) runs scheduled operations:

| Job | Schedule | What It Does |
|-----|----------|-------------|
| PurgeRawData | Daily 3 AM | `EXEC ETL.usp_PurgeRawData` |
| IndexMaintenance | Weekly Sunday 4 AM | `EXEC ETL.usp_IndexMaintenance` |
| CompanyPiXLSync | Every 6 hours | Sync from Xavier |
| IpApiSync | Every 6 hours | Sync IPAPI data from Xavier |

Schedule is based on UTC. Jobs are idempotent — if the Forge restarts mid-day, missed jobs run on next scheduled interval (not immediately on startup).
