---
subsystem: troubleshooting
title: Troubleshooting
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - operations/deployment
  - operations/monitoring
  - subsystems/failover
---

# Troubleshooting

## Atlas Public

SmartPiXL includes built-in diagnostics and self-healing capabilities. If you experience any issues with data collection or dashboard access, the platform automatically detects and resolves most common problems. For persistent issues, our operations team has comprehensive diagnostic tools to identify and fix any problem quickly.

## Atlas Internal

### Common Issues — Quick Reference

| Symptom | Most Likely Cause | How Support Can Help |
|---------|-------------------|---------------------|
| No new data in dashboard | Forge is stopped or ETL is paused | Check Forge service status; may need restart |
| Dashboard loads but shows old data | ETL watermark is stuck | Operations can reset the watermark |
| "Connection refused" errors | IIS app pool stopped | Restart the app pool |
| Missing geographic data | MaxMind database needs update or IPAPI rate limit hit | Operations refreshes the geo database |
| Intermittent data gaps | IIS app pool recycled during peak traffic | JSONL failover handles this automatically |

### What Customers Never See

Most issues are completely transparent to end users. The failover system catches pipe failures, the ETL self-heals from partial commits, and the self-healing service automatically remediates common operational issues. By the time someone notices a problem, it's usually already been fixed.

## Atlas Technical

### No Data After Deploy

**Symptom**: Tracking pixel requests are returning 200/GIF but no rows appear in PiXL.Raw.

**Diagnostic steps**:

1. **Check Edge logs**:
   ```powershell
   Get-Content "C:\inetpub\Smartpixl.info\Log\$(Get-Date -Format 'yyyy_MM_dd').log" -Tail 30
   ```
   Look for "Failed to write batch" or "SQL login failed" errors.

2. **Check SQL login for IIS identity**:
   ```sql
   SELECT sp.name, sp.type_desc 
   FROM sys.server_principals sp 
   WHERE sp.name LIKE '%Smartpixl%';
   ```
   If missing, the app pool identity can't connect to SQL.

3. **Check web.config**:
   ```powershell
   type "C:\inetpub\Smartpixl.info\web.config"
   ```
   If it's empty or minimal, `dotnet publish` clobbered it. Restore from source.

4. **Check appsettings.json ports**:
   ```powershell
   type "C:\inetpub\Smartpixl.info\appsettings.json" | Select-String "Url"
   ```
   Must show 6000/6001 (not 7000/7001).

### ETL Not Processing

**Symptom**: PiXL.Raw has rows but PiXL.Parsed doesn't grow.

**Diagnostic steps**:

1. **Check watermark position**:
   ```sql
   SELECT ProcessName, LastProcessedId, LastRunAt FROM ETL.Watermark;
   SELECT MAX(Id) AS MaxRawId FROM PiXL.Test;
   SELECT MAX(SourceId) AS MaxParsedId FROM PiXL.Parsed;
   ```
   If `LastProcessedId >= MaxRawId`, there's nothing to process (normal).
   If `LastProcessedId < MaxRawId` and `LastRunAt` is old, the Forge ETL is stuck.

2. **Check Forge service status**:
   ```powershell
   Get-Service SmartPiXL-Forge | Select-Object Status, StartType
   ```

3. **Run ETL manually** (if Forge is running but ETL isn't firing):
   ```sql
   EXEC ETL.usp_ParseNewHits @BatchSize = 100;
   ```

4. **Reset watermark** (if watermark is ahead of data — after backup restore or truncation):
   ```sql
   UPDATE ETL.Watermark SET LastProcessedId = 0, RowsProcessed = 0 
   WHERE ProcessName = 'ParseNewHits';
   ```

### Named Pipe Not Connecting

**Symptom**: Edge logs show "Pipe unavailable — wrote to JSONL failover" repeatedly.

**Diagnostic steps**:

1. **Verify Forge is running**:
   ```powershell
   Get-Service SmartPiXL-Forge
   ```

2. **Verify pipe name matches**:
   ```powershell
   # Check both configs
   Select-String "PipeName" "C:\Users\Administrator\source\repos\SmartPiXL\SmartPiXL\appsettings.json"
   Select-String "PipeName" "C:\Users\Administrator\source\repos\SmartPiXL\SmartPiXL.Forge\appsettings.json"
   ```
   Both must say `SmartPiXL-Enrichment`.

3. **Check for active pipe listener**:
   ```powershell
   [System.IO.Directory]::GetFiles("\\.\pipe\") | Where-Object { $_ -match "SmartPiXL" }
   ```

4. **Check Failover directory for accumulated files**:
   ```powershell
   Get-ChildItem "C:\inetpub\Smartpixl.info\Failover\" -Filter "*.jsonl"
   ```
   If files exist, the pipe was unavailable. When the Forge starts, `FailoverCatchupService` will process them automatically.

### Dashboard Shows Stale Data

**Symptom**: Dashboard loads but data is hours or days old.

**Diagnostic steps**:

1. **Check ETL lag**:
   ```sql
   SELECT 
       w.ProcessName, w.LastProcessedId,
       (SELECT MAX(Id) FROM PiXL.Test) - w.LastProcessedId AS PendingRows
   FROM ETL.Watermark w;
   ```

2. **Check Forge logs** for errors:
   ```powershell
   Get-Content "C:\Services\SmartPiXL-Forge\Log\$(Get-Date -Format 'yyyy_MM_dd').log" -Tail 30
   ```

3. **Check materialization lag** (TrafficAlert):
   ```sql
   SELECT MAX(MaterializedAt) AS LastMaterialized FROM TrafficAlert.VisitorScore;
   SELECT MAX(MaterializedAt) AS LastMaterialized FROM TrafficAlert.CustomerSummary;
   ```

### Process Restart Procedures

| Component | Restart Command |
|-----------|----------------|
| IIS Edge | `iisreset` or `Stop-WebAppPool "Smartpixl.info"; Start-WebAppPool "Smartpixl.info"` |
| Forge | `Restart-Service SmartPiXL-Forge` |
| Sentinel | `Restart-Service SmartPiXL-Sentinel` |
| Dev (any) | Kill process, `dotnet run` |

### SQL Server Common Issues

**Error 1105 — Filegroup full**:
```sql
-- Check filegroup space
SELECT fg.name, df.name, df.size * 8/1024 AS SizeMB, df.max_size
FROM sys.filegroups fg JOIN sys.database_files df ON fg.data_space_id = df.data_space_id;
```

**Login failed for IIS APPPOOL**:
```sql
CREATE LOGIN [IIS APPPOOL\Smartpixl.info] FROM WINDOWS;
USE SmartPiXL;
CREATE USER [IIS APPPOOL\Smartpixl.info] FOR LOGIN [IIS APPPOOL\Smartpixl.info];
ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\Smartpixl.info];
ALTER ROLE db_datawriter ADD MEMBER [IIS APPPOOL\Smartpixl.info];
GRANT EXECUTE TO [IIS APPPOOL\Smartpixl.info];
```

**CLR function errors**:
```sql
-- Verify CLR is enabled
SELECT name, value_in_use FROM sys.configurations WHERE name = 'clr enabled';

-- Verify assembly is loaded
USE SmartPiXL_CLR;
SELECT * FROM sys.assemblies WHERE name = 'SmartPiXL.SqlClr';

-- Test CLR function
SELECT dbo.GetQueryParam('sw=1920&sh=1080', 'sw');  -- Should return '1920'
```

## Atlas Private

### Self-Healing Service Decision Tree

The `SelfHealingService` runs every 60 seconds and auto-remediates:

| Issue | Severity | Action | Auto/Manual |
|-------|----------|--------|-------------|
| Default filegroup is PRIMARY | Low | ALTER DATABASE SET DEFAULT_FILEGROUP | Auto |
| Watermark stuck (>5 min lag) | Medium | Reset watermark | Auto |
| ETL catch-up needed | Medium | Execute usp_ParseNewHits | Auto |
| User objects on PRIMARY | Medium | Log for manual move | Manual (email alert) |
| Filegroup > 80% full | High | Alert before error 1105 | Manual (email alert) |
| Edge circuit breaker open | High | Attempt HTTP reset | Auto |
| Forge service stopped | Critical | Cannot auto-restart (no admin) | Manual (email alert) |

De-duplication: same (IssueType, Status) within 2 hours is not re-logged.
Email throttle: 1 email per issue type per hour.

### The Circuit Breaker Pattern

`DatabaseWriterService` in the Edge has a circuit breaker:
- **Closed** (normal): SQL writes proceed
- **Open** (tripped): SQL writes fail immediately (no connection attempt)
- **Half-open** (testing): Single write attempted; success → closed, failure → open

Trip triggers: 3 consecutive SQL failures within 60 seconds.
Auto-recovery: After 30 seconds in open state, transitions to half-open.
Manual reset: `POST /internal/circuit-reset` from localhost.

The Forge's `SelfHealingService` monitors the Edge circuit via `GET /internal/health` and can trigger `POST /internal/circuit-reset` when SQL connectivity is restored.

### Known Fragile Points

1. **web.config after publish** — #1 cause of "it worked before deploy, now it doesn't."
   Symptom: IIS returns 500.0 with no stdout log (ANCM can't find the app DLL).
   Fix: Restore web.config content.

2. **SQL login after instance restart** — Windows authentication logins can become orphaned if the SID changes.
   Symptom: "Login failed for IIS APPPOOL\Smartpixl.info" in Edge logs.
   Fix: DROP and recreate the login.

3. **MaxMind database expiry** — GeoIP2 databases expire after a configurable period (default: 30 days since download).
   Symptom: `MaxMindGeoService` throws on startup; all geo lookups return null.
   Fix: Download fresh GeoLite2 database from MaxMind account.

4. **IPAPI rate limit exhaustion** — If the Forge restarts under heavy load, the burst of new-IP lookups can hit the 500 req/min cap.
   Symptom: `_srv_ipapiCC` is empty for many records.
   Fix: Self-correcting — known IPs skip the API. New IPs are retried on next visit.

5. **Disk full on Failover directory** — If the Edge is writing to JSONL failover for an extended period (Forge down for days).
   Symptom: `JsonlFailoverService` throws IOException; falls through to `DatabaseWriterService` (direct SQL).
   Fix: Start the Forge (catch-up processes files). If disk is truly full, clear `.done` files first.

### Watermark Gotcha After Backup Restore

If the database is restored from backup:
- PiXL.Raw may have rows beyond what the watermark has processed
- OR the watermark may be ahead of the max Raw ID (if backup is older than current watermark)

After restore:
```sql
-- Check state
SELECT * FROM ETL.Watermark;
SELECT MAX(Id) FROM PiXL.Test;

-- If watermark > max Raw ID:
UPDATE ETL.Watermark SET LastProcessedId = 0 WHERE ProcessName = 'ParseNewHits';
UPDATE ETL.MatchWatermark SET LastProcessedId = 0 WHERE ProcessName = 'MatchVisits';
UPDATE ETL.Watermark SET LastProcessedId = 0 WHERE ProcessName = 'MaterializeVisitorScores';
UPDATE ETL.Watermark SET LastProcessedId = 0 WHERE ProcessName = 'MaterializeCustomerSummary';
```

This causes a full reprocess of all data — safe but slow for large databases.
