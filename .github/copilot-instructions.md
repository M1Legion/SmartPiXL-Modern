# SmartPiXL — Copilot Custom Instructions

## Authoritative Documents

| Document | Purpose |
|----------|---------|
| `docs/BRILLIANT-PIXL-DESIGN.md` | **Design source of truth** — all architectural decisions |
| `docs/SmartPiXL Authoritative WorkPlan .md` | **Implementation plan** — 10 sequential phases |
| `docs/IMPLEMENTATION-LOG.md` | **Decision log** — conflicts resolved, decisions made, progress tracked |

All agents must read these before starting work. If anything in this file conflicts with the design doc, the design doc wins.

## Conflict Resolution Authority

Agents have authority to resolve conflicts between the authoritative docs and deprecated reference code (Worker-Deprecated, Modern-Deprecated) without asking. However, **every conflict MUST be logged** in `docs/IMPLEMENTATION-LOG.md` with:
1. What the conflict was
2. Why the agent made the decision it did
3. What the decision was

The platform owner reviews the implementation log after each session. This is the audit trail.

## Current Build Status

SmartPiXL is in an **active rebuild**. The Worker is OFF. Dashboards are DOWN. Implementation follows sequential phases — complete and verify each before starting the next.

| Component | Status | Notes |
|-----------|--------|-------|
| PiXL Edge (IIS) | **LIVE** | Pixel capture hot path, 12 fast enrichments |
| SmartPiXL Worker | **OFF — DEPRECATED** | Keep in solution as read-only reference. Delete in Phase 10. |
| SmartPiXL Forge | **BUILT — Phase 2 complete** | Named pipe server, enrichment pipeline, SQL writer. Phase 3 wired Edge→Forge pipe. |
| SmartPiXL Sentinel | **NOT BUILT** | Phase 10. Replaces Worker for Tron + Atlas dashboards. |
| PiXL Script | 159/159 fields | Phase 1 complete — `screenExtended` + `mousePath` added |
| ETL | **OFF** | Resumes after Edge→Forge integration test (Phase 3 verification) |
| Dashboards (Tron + Atlas) | **OFFLINE** | Rebuilt in Phase 10 based on new enrichment data |

## Target Architecture (Post-Rebuild)

SmartPiXL runs as **three separate processes**:

| Process | Project | Location (Prod) | Ports | Purpose |
|---------|---------|------------------|-------|---------|
| **PiXL Edge** (IIS) | `SmartPiXL` | `C:\inetpub\Smartpixl.info\` | 80/443 via IIS, Kestrel 6000/6001 | Pixel capture, 12 fast enrichments, named pipe client |
| **SmartPiXL Forge** | `SmartPiXL.Forge` | Windows Service `SmartPiXL-Forge` | — | Named pipe server, Tier 1-3 enrichments, ETL, SQL writer |
| **SmartPiXL Sentinel** | `SmartPiXL.Sentinel` | Windows Service `SmartPiXL-Sentinel` | 7500 | Tron ops, Tron metrics, Atlas portal (Phase 10) |
| **Shared Library** | `SmartPiXL.Shared` | (referenced by all) | — | Configuration, models, interfaces |

### Dev Ports

| Process | HTTP | HTTPS |
|---------|------|-------|
| Edge (dev) | 7000 | 7001 |
| Sentinel (dev) | 7500 | — |
| Edge (prod/IIS) | 6000 | 6001 |

### Cross-Process Communication

**Edge → Forge** (named pipe):
```
IIS Edge → NamedPipeClientStream("SmartPiXL-Enrichment") → Forge NamedPipeServerStream
  Sends: enriched TrackingData record (JSON line)
  Failover: JSONL file to Failover/ directory if pipe unavailable
```

**Forge → Edge** (HTTP, health checks):
```
Forge → http://127.0.0.1:{6000|7000}/internal/health        → GET
Forge → http://127.0.0.1:{6000|7000}/internal/circuit-reset  → POST
Forge → http://127.0.0.1:{6000|7000}/internal/geo-cache/clear → POST
```

## Solution Structure

```
SmartPixl.sln
├── SmartPiXL.Shared/          Shared class library (zero NuGet deps)
│   ├── Configuration/         TrackingSettings, ForgeSettings
│   ├── Models/                TrackingData, GeoResult, IpClassification
│   └── Services/              ITrackingLogger, FileTrackingLogger, IEdgeHealthClient
├── SmartPiXL/                 IIS Edge — pixel capture hot path
│   ├── Endpoints/             TrackingEndpoints, InternalEndpoints
│   ├── Services/              TrackingCaptureService, PipeClientService, JsonlFailoverService,
│   │                          DatabaseWriterService (fallback), FingerprintStabilityService,
│   │                          IpBehaviorService, DatacenterIpService, GeoCacheService,
│   │                          IpClassificationService
│   ├── Scripts/               PiXL JavaScript template (PiXLScript.cs)
│   └── SQL/                   Numbered migration scripts (41+)
├── SmartPiXL.Forge/           Windows Service — enrichment + ETL (Phase 2+)
│   └── Services/              PipeListenerService, EnrichmentPipelineService,
│                               SqlBulkCopyWriterService, FailoverCatchupService,
│                               EtlBackgroundService, IpApiSyncService, CompanyPiXLSyncService,
│                               Services/Enrichments/ (Tier 1-3 enrichment services)
├── SmartPiXL.Worker-Deprecated/ ⚠ DEPRECATED — read-only reference, DO NOT MODIFY
├── SmartPiXL.SqlClr/          CLR assembly for SQL Server 2025 (Phase 7)
├── SmartPiXL.Tests/           xUnit tests
└── docs/                      Design docs, workplan
```

## Database

| Property | Value |
|----------|-------|
| **SQL Server Instance** | `localhost\SQL2025` (MSSQL 2025 Developer) |
| **Database Name** | `SmartPiXL` (capital X and L) |
| **CLR Database** | `SmartPiXL_CLR` (Phase 7 — separate database for CLR assemblies) |
| **Connection String** | `Server=localhost\SQL2025;Database=SmartPiXL;Integrated Security=True;TrustServerCertificate=True` |
| **IIS App Pool Identity** | `IIS APPPOOL\Smartpixl.info` (needs login + db_datareader/db_datawriter/execute) |
| **Old Instance (RETIRED)** | `localhost` default instance, database `SmartPixl` — do NOT use |

### Schema Map

| Schema | Purpose | Status |
|--------|---------|--------|
| `PiXL` | Domain tables (Raw, Parsed, Device, IP, Visit, Match, Config, Company, Pixel) | Live |
| `ETL` | Pipeline (Watermark, usp_ParseNewHits, usp_MatchVisits, usp_EnrichParsedGeo) | Live (paused during rebuild) |
| `IPAPI` | IP geolocation (342M+ rows synced from Xavier) | Live |
| `Geo` | Zipcode polygon table from Census ZCTA (Phase 8) | Planned |
| `TrafficAlert` | Visitor scoring + customer summaries (Phase 9) | Planned |
| `Graph` | Node/edge tables for identity resolution (Phase 7) | Planned |
| `dbo` | Dashboard views (`vw_Dash_*`), functions (`GetQueryParam`) | Live |

## Critical Files That Must Stay In Sync

When changing **connection strings**, **ports**, or **config**, you must update ALL of these:

| # | File | What Lives There |
|---|------|------------------|
| 1 | `SmartPiXL/appsettings.json` | Edge dev connection string, Kestrel ports 7000/7001, PipeName |
| 2 | `SmartPiXL.Forge/appsettings.json` | Forge dev connection string, PipeName, Failover dir |
| 3 | `C:\inetpub\Smartpixl.info\appsettings.json` | **Production Edge** connection string, Kestrel ports 6000/6001 |
| 4 | `SmartPiXL.Shared/Configuration/TrackingSettings.cs` | Compiled default fallback connection string |
| 5 | `C:\inetpub\Smartpixl.info\web.config` | ASP.NET Core module config for IIS hosting |
| 6 | `SmartPiXL/web.config` | Source web.config (copied during publish) |

**⚠ `dotnet publish` overwrites `web.config` at the destination. If you publish, verify `web.config` afterwards.**

**⚠ The IIS `appsettings.json` uses ports 6000/6001 (IIS internal). The dev copy uses 7000/7001. Do NOT make them the same.**

**⚠ Do NOT modify SmartPiXL.Worker-Deprecated — it is a read-only reference. All Worker functionality is being ported to the Forge.**

## Deploying an Update to IIS (Edge)

```powershell
# 1. Stop the IIS app pool (graceful shutdown)
Import-Module WebAdministration
Stop-WebAppPool -Name "Smartpixl.info"

# 2. Publish Edge from source
Push-Location "C:\Users\Administrator\source\repos\SmartPiXL\SmartPiXL"
dotnet publish -c Release -o "C:\inetpub\Smartpixl.info"
Pop-Location

# 3. CRITICAL: Verify web.config wasn't clobbered by publish
type "C:\inetpub\Smartpixl.info\web.config"

# 4. CRITICAL: Verify appsettings.json has production values
# - Connection string must point to localhost\SQL2025, Database SmartPiXL
# - Kestrel ports must be 6000/6001 (NOT 7000/7001)
type "C:\inetpub\Smartpixl.info\appsettings.json"

# 5. Start the app pool
Start-WebAppPool -Name "Smartpixl.info"

# 6. Verify — send a test hit and check the log
Invoke-WebRequest -Uri "http://192.168.88.176/DEMO/deploy-test_SMART.GIF?verify=1" -UseBasicParsing | Out-Null
Start-Sleep -Seconds 3
Get-Content "C:\inetpub\Smartpixl.info\Log\$(Get-Date -Format 'yyyy_MM_dd').log" -Tail 10
```

### Deploying the Forge

```powershell
# Forge runs as a Windows Service
Stop-Service -Name "SmartPiXL-Forge" -ErrorAction SilentlyContinue

Push-Location "C:\Users\Administrator\source\repos\SmartPiXL\SmartPiXL.Forge"
dotnet publish -c Release -o "C:\Services\SmartPiXL-Forge"
Pop-Location

# First time: sc.exe create SmartPiXL-Forge binPath= "C:\Services\SmartPiXL-Forge\SmartPiXL.Forge.exe"
Start-Service -Name "SmartPiXL-Forge"
```

### What `web.config` MUST contain:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <security>
        <requestFiltering>
          <requestLimits maxQueryString="16384" maxUrl="8192" />
        </requestFiltering>
      </security>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="dotnet" arguments=".\SmartPiXL.dll"
                  stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout"
                  hostingModel="inprocess" />
    </system.webServer>
  </location>
</configuration>
```

### What IIS `appsettings.json` MUST contain (differs from dev):

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http":  { "Url": "http://*:6000" },
      "Https": { "Url": "https://*:6001" }
    }
  },
  "Tracking": {
    "ConnectionString": "Server=localhost\\SQL2025;Database=SmartPiXL;Integrated Security=True;TrustServerCertificate=True"
  }
}
```

## SQL Server Permissions for IIS

```sql
CREATE LOGIN [IIS APPPOOL\Smartpixl.info] FROM WINDOWS;
USE SmartPiXL;
CREATE USER [IIS APPPOOL\Smartpixl.info] FOR LOGIN [IIS APPPOOL\Smartpixl.info];
ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\Smartpixl.info];
ALTER ROLE db_datawriter ADD MEMBER [IIS APPPOOL\Smartpixl.info];
GRANT EXECUTE TO [IIS APPPOOL\Smartpixl.info];
```

## Data Pipeline (Target Architecture)

```
Browser → PiXL Script (159 fields, 500ms window)
     │
     ▼ _SMART.GIF request
IIS (PiXL Edge) — in-process w3wp.exe
     │  Parse HTTP + 12 fast enrichments (~5μs) + _srv_* params
     │  GIF response returned immediately
     ▼ Named pipe (enriched TrackingData as JSON line)
SmartPiXL Forge — Windows Service
     │  Tier 1: IPAPI, NetCrawlerDetect, UAParser, DnsClient, MaxMind, WHOIS
     │  Tier 2: Cross-customer intel, lead scoring, session stitching, affluence
     │  Tier 3: Cultural arbitrage, device age, contradiction matrix, behavioral replay
     │  → SqlBulkCopy → PiXL.Raw (9 columns)
     │  → ETL every 60s: usp_ParseNewHits → PiXL.Parsed (300+ cols)
     │                                     → PiXL.Device, PiXL.IP, PiXL.Visit
     │                    usp_MatchVisits  → PiXL.Match (identity resolution)
     ▼
SmartPiXL Sentinel — Windows Service, port 7500 (Phase 10)
     │  Tron Operations, Tron Metrics, Atlas Portal
     │  /api/dash/*, /api/atlas/*, /api/traffic-alert/*
```

**Failover path (pipe unavailable):**
```
IIS Edge → JSONL file to Failover/ directory (durable on disk)
Forge restarts → FailoverCatchupService reads JSONL → enrichment pipeline → SQL
```

## Troubleshooting

### No data after deploy
1. Check `C:\inetpub\Smartpixl.info\Log\{date}.log` — look for "Failed to write batch" errors
2. Most common cause: **SQL login failed for IIS app pool identity** on the target instance
3. Second most common: **`web.config` was emptied/overwritten** by `dotnet publish`

### ETL not processing
1. `PiXL.Raw` has rows but `PiXL.Parsed` doesn't → ETL proc hasn't run. Check `ETL.Watermark` table
2. Watermark is ahead of PiXL.Raw max ID → Reset: `UPDATE ETL.Watermark SET LastProcessedId = 0, RowsProcessed = 0 WHERE ProcessName = 'ParseNewHits'`
3. Run manually: `EXEC ETL.usp_ParseNewHits`

### Named pipe not connecting (Phase 3+)
1. Verify the Forge is running: `Get-Service SmartPiXL-Forge`
2. Check pipe name matches in both Edge and Forge appsettings.json
3. Check Failover/ directory for JSONL files (indicates pipe was unavailable)

### Process needs restart after config change
- **IIS**: `iisreset` or `Stop-WebAppPool` / `Start-WebAppPool`
- **Forge**: `Restart-Service -Name "SmartPiXL-Forge"`
- **Dev**: Kill the process and re-run `dotnet run`
