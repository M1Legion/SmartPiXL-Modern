# SmartPiXL — Deployment & Operations Guide

Reference for bringing all SmartPiXL services online, redeploying after code changes, and common troubleshooting.

---

## Architecture Overview

SmartPiXL runs as **three separate processes**:

| Process | Project | Deploy Location | Ports | Purpose |
|---------|---------|-----------------|-------|---------|
| **PiXL Edge** | `SmartPiXL` | `C:\inetpub\Smartpixl.info\` | IIS 80/443, Kestrel 6000/6001 | Pixel capture, 12 fast enrichments |
| **SmartPiXL Forge** | `SmartPiXL.Forge` | `C:\Services\SmartPiXL-Forge\` | — (named pipe) | Enrichment pipeline, ETL, SQL writer |
| **SmartPiXL Sentinel** | `SmartPiXL.Sentinel` | `C:\Services\SmartPiXL-Sentinel\` | 7500 | Tron dashboard, Atlas portal |

**Database:** SQL Server 2025 Developer at `localhost\SQL2025`, database `SmartPiXL`.

> **Important:** Edge runs InProcess inside IIS (`w3wp.exe`). The Kestrel ports 6000/6001 in
> `appsettings.json` are used by the ASP.NET Core module internally — Edge is only accessible
> via IIS-bound ports (80/443 on `192.168.88.176`). Do NOT try to reach Edge on port 6000.

---

## Prerequisites

### SQL Server Permissions

The IIS app pool and Windows Services need database access:

```sql
-- IIS App Pool identity (Edge)
CREATE LOGIN [IIS APPPOOL\Smartpixl.info] FROM WINDOWS;
USE SmartPiXL;
CREATE USER [IIS APPPOOL\Smartpixl.info] FOR LOGIN [IIS APPPOOL\Smartpixl.info];
ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\Smartpixl.info];
ALTER ROLE db_datawriter ADD MEMBER [IIS APPPOOL\Smartpixl.info];
GRANT EXECUTE TO [IIS APPPOOL\Smartpixl.info];

-- Windows Service identity (Forge + Sentinel run as SYSTEM by default)
CREATE LOGIN [NT AUTHORITY\SYSTEM] FROM WINDOWS;
USE SmartPiXL;
CREATE USER [NT AUTHORITY\SYSTEM] FOR LOGIN [NT AUTHORITY\SYSTEM];
ALTER ROLE db_datareader ADD MEMBER [NT AUTHORITY\SYSTEM];
ALTER ROLE db_datawriter ADD MEMBER [NT AUTHORITY\SYSTEM];
GRANT EXECUTE TO [NT AUTHORITY\SYSTEM];
```

### Source Location

All source lives at: `C:\Users\Administrator\source\repos\SmartPiXL`

---

## 1. Deploying PiXL Edge (IIS)

```powershell
# ── Step 1: Stop the IIS app pool ──
Import-Module WebAdministration
Stop-WebAppPool -Name "Smartpixl.info"

# ── Step 2: Publish ──
Push-Location "C:\Users\Administrator\source\repos\SmartPiXL\SmartPiXL"
dotnet publish -c Release -o "C:\inetpub\Smartpixl.info"
Pop-Location

# ── Step 3: CRITICAL — Verify web.config wasn't clobbered ──
# Must contain: hostingModel="inprocess", maxQueryString="16384"
Get-Content "C:\inetpub\Smartpixl.info\web.config"

# ── Step 4: CRITICAL — Verify appsettings.json has production values ──
# Kestrel ports MUST be 6000/6001 (NOT 7000/7001)
# Connection string MUST point to localhost\SQL2025
Get-Content "C:\inetpub\Smartpixl.info\appsettings.json"

# ── Step 5: Start the app pool ──
Start-WebAppPool -Name "Smartpixl.info"

# ── Step 6: Verify with a test pixel hit ──
Invoke-WebRequest -Uri "http://192.168.88.176/DEMO/deploy-test_SMART.GIF?verify=1" -UseBasicParsing | Select-Object StatusCode, Headers
Start-Sleep -Seconds 3
Get-ChildItem "C:\inetpub\Smartpixl.info\Log" | Sort-Object LastWriteTime -Desc | Select-Object -First 1 | ForEach-Object { Get-Content $_.FullName -Tail 5 }
```

### Required `web.config` Content

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

### Required Production `appsettings.json` Values

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

---

## 2. Deploying SmartPiXL Forge (Windows Service)

```powershell
# ── Step 1: Stop the service (if running) ──
Stop-Service -Name "SmartPiXL-Forge" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

# ── Step 2: Publish ──
Push-Location "C:\Users\Administrator\source\repos\SmartPiXL\SmartPiXL.Forge"
dotnet publish -c Release -o "C:\Services\SmartPiXL-Forge"
Pop-Location

# ── Step 3: Register as Windows Service (first time only) ──
sc.exe create SmartPiXL-Forge binPath= "C:\Services\SmartPiXL-Forge\SmartPiXL.Forge.exe" start= auto
sc.exe description SmartPiXL-Forge "SmartPiXL Forge — enrichment pipeline, ETL, SQL writer"

# ── Step 4: Start the service ──
Start-Service -Name "SmartPiXL-Forge"

# ── Step 5: Verify startup ──
Start-Sleep -Seconds 5
Get-ChildItem "C:\Services\SmartPiXL-Forge\Log" | Sort-Object LastWriteTime -Desc | Select-Object -First 1 | ForEach-Object { Get-Content $_.FullName -Tail 15 }
```

### What to Look For in Forge Startup Logs

Good startup shows these lines (no `[ERROR]`):
```
SmartPiXL Forge starting...
Edge health check OK (http://192.168.88.176)
PipeListenerService started. Pipe: SmartPiXL-Enrichment, Instances: 4
ETL background service started. Running every 60 seconds.
SqlBulkCopyWriterService started.
SelfHealingService started.
```

Known non-critical warnings:
- `MaxMindGeo: No .mmdb files found` — Optional. Place MaxMind GeoLite2 `.mmdb` files in `C:\Services\SmartPiXL-Forge\MaxMind\` to enable.

### Forge ETL Verification

Wait ~70 seconds after startup, then:

```powershell
# Check latest log for ETL results
Get-ChildItem "C:\Services\SmartPiXL-Forge\Log" | Sort-Object LastWriteTime -Desc | Select-Object -First 1 | ForEach-Object { Get-Content $_.FullName -Tail 5 }
# Should see: "ETL parsed N rows (Id X–Y)"

# Check pipeline counts
sqlcmd -S "localhost\SQL2025" -d "SmartPiXL" -C -Q "SELECT 'PiXL.Raw' AS T, COUNT(*) AS N FROM PiXL.Raw UNION ALL SELECT 'PiXL.Parsed', COUNT(*) FROM PiXL.Parsed"
```

---

## 3. Deploying SmartPiXL Sentinel (Windows Service)

```powershell
# ── Step 1: Stop the service (if running) ──
Stop-Service -Name "SmartPiXL-Sentinel" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

# ── Step 2: Publish ──
Push-Location "C:\Users\Administrator\source\repos\SmartPiXL\SmartPiXL.Sentinel"
dotnet publish -c Release -o "C:\Services\SmartPiXL-Sentinel"
Pop-Location

# ── Step 3: Register as Windows Service (first time only) ──
sc.exe create SmartPiXL-Sentinel binPath= "C:\Services\SmartPiXL-Sentinel\SmartPiXL.Sentinel.exe" start= auto
sc.exe description SmartPiXL-Sentinel "SmartPiXL Sentinel — Tron dashboard and Atlas portal on port 7500"

# ── Step 4: Start the service ──
Start-Service -Name "SmartPiXL-Sentinel"

# ── Step 5: Verify endpoints ──
Invoke-WebRequest -Uri "http://localhost:7500/tron" -UseBasicParsing | Select-Object StatusCode
Invoke-WebRequest -Uri "http://localhost:7500/api/dash/health" -UseBasicParsing | Select-Object StatusCode
Invoke-WebRequest -Uri "http://localhost:7500/atlas" -UseBasicParsing | Select-Object StatusCode
```

---

## Quick Reference — Start All Services

```powershell
# Start everything (assumes services are already registered)
Import-Module WebAdministration
Start-WebAppPool -Name "Smartpixl.info"
Start-Service -Name "SmartPiXL-Forge"
Start-Service -Name "SmartPiXL-Sentinel"

# Verify all running
Get-WebAppPoolState -Name "Smartpixl.info"
Get-Service SmartPiXL-Forge, SmartPiXL-Sentinel | Format-Table Name, Status -AutoSize
```

## Quick Reference — Stop All Services

```powershell
Import-Module WebAdministration
Stop-WebAppPool -Name "Smartpixl.info"
Stop-Service -Name "SmartPiXL-Forge" -ErrorAction SilentlyContinue
Stop-Service -Name "SmartPiXL-Sentinel" -ErrorAction SilentlyContinue
```

## Quick Reference — Full Redeploy (All Three)

```powershell
# Stop everything
Import-Module WebAdministration
Stop-WebAppPool -Name "Smartpixl.info"
Stop-Service -Name "SmartPiXL-Forge" -ErrorAction SilentlyContinue
Stop-Service -Name "SmartPiXL-Sentinel" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

# Publish all three
$repo = "C:\Users\Administrator\source\repos\SmartPiXL"
dotnet publish "$repo\SmartPiXL" -c Release -o "C:\inetpub\Smartpixl.info"
dotnet publish "$repo\SmartPiXL.Forge" -c Release -o "C:\Services\SmartPiXL-Forge"
dotnet publish "$repo\SmartPiXL.Sentinel" -c Release -o "C:\Services\SmartPiXL-Sentinel"

# CRITICAL: Verify web.config + appsettings after Edge publish
Get-Content "C:\inetpub\Smartpixl.info\web.config"
Get-Content "C:\inetpub\Smartpixl.info\appsettings.json" | Select-String "Url|ConnectionString"

# Start everything
Start-WebAppPool -Name "Smartpixl.info"
Start-Service -Name "SmartPiXL-Forge"
Start-Service -Name "SmartPiXL-Sentinel"

# Verify
Get-WebAppPoolState -Name "Smartpixl.info"
Get-Service SmartPiXL-Forge, SmartPiXL-Sentinel | Format-Table Name, Status -AutoSize
Invoke-WebRequest -Uri "http://192.168.88.176/DEMO/deploy-test_SMART.GIF?verify=1" -UseBasicParsing | Select-Object StatusCode
Invoke-WebRequest -Uri "http://localhost:7500/api/dash/health" -UseBasicParsing | Select-Object StatusCode
```

---

## Diagnostics

### Service Status

```powershell
# IIS Edge
Import-Module WebAdministration
Get-WebAppPoolState -Name "Smartpixl.info"

# Windows Services
Get-Service SmartPiXL-Forge, SmartPiXL-Sentinel | Format-Table Name, Status -AutoSize
```

### Log Locations

| Service | Log Directory |
|---------|---------------|
| Edge | `C:\inetpub\Smartpixl.info\Log\` |
| Forge | `C:\Services\SmartPiXL-Forge\Log\` |
| Sentinel | `C:\Services\SmartPiXL-Sentinel\Log\` |
| IIS stdout | `C:\inetpub\Smartpixl.info\logs\stdout_*.log` |

```powershell
# Tail the latest log for any service
$dir = "C:\Services\SmartPiXL-Forge\Log"  # or Sentinel or Edge
Get-ChildItem $dir | Sort-Object LastWriteTime -Desc | Select-Object -First 1 | ForEach-Object { Get-Content $_.FullName -Tail 30 }
```

### Pipeline Health

```powershell
sqlcmd -S "localhost\SQL2025" -d "SmartPiXL" -C -Q "
SELECT 'PiXL.Raw' AS T, COUNT(*) AS N FROM PiXL.Raw UNION ALL
SELECT 'PiXL.Parsed', COUNT(*) FROM PiXL.Parsed UNION ALL
SELECT 'PiXL.Device', COUNT(*) FROM PiXL.Device UNION ALL
SELECT 'PiXL.IP', COUNT(*) FROM PiXL.IP UNION ALL
SELECT 'PiXL.Visit', COUNT(*) FROM PiXL.Visit UNION ALL
SELECT 'PiXL.Match', COUNT(*) FROM PiXL.Match"
```

### ETL Watermarks

```powershell
sqlcmd -S "localhost\SQL2025" -d "SmartPiXL" -C -Q "SELECT * FROM ETL.Watermark"
```

### Named Pipe / Failover

```powershell
# Check if JSONL failover files are accumulating (pipe was unavailable)
Get-ChildItem "C:\inetpub\Smartpixl.info\Failover\*.jsonl" -ErrorAction SilentlyContinue | Measure-Object | Select-Object Count
Get-ChildItem "C:\Services\SmartPiXL-Forge\Failover\*.jsonl" -ErrorAction SilentlyContinue | Measure-Object | Select-Object Count
```

---

## Common Failure Modes

| Symptom | Cause | Fix |
|---------|-------|-----|
| HTTP 404.15 on pixel hits | `maxQueryString` too small in `web.config` | Verify `maxQueryString="16384"` |
| All IPs show as 127.0.0.1 | Not InProcess hosting | Verify `hostingModel="inprocess"` in `web.config` |
| SQL login failed | App pool / SYSTEM identity missing | Run the SQL permission scripts above |
| ETL not processing | Watermark ahead of data or duplicate keys | Reset: `UPDATE ETL.Watermark SET LastProcessedId = 0 WHERE ProcessName = 'ParseNewHits'` |
| Named pipe won't connect | Forge not running | `Get-Service SmartPiXL-Forge` |
| JSONL files accumulating | Pipe was unavailable | Start Forge — `FailoverCatchupService` processes them |
| Edge unreachable from Forge/Sentinel | Wrong `EdgeBaseUrl` | Must be `http://192.168.88.176` (IIS port 80), NOT port 6000 or 7000 |
| `dotnet publish` broke Edge | `web.config` overwritten | Restore from the template in this doc |

---

## Configuration Files (Must Stay In Sync)

| # | File | Key Settings |
|---|------|-------------|
| 1 | `SmartPiXL/appsettings.json` | Dev Edge: ports 7000/7001 |
| 2 | `SmartPiXL.Forge/appsettings.json` | Forge: EdgeBaseUrl, PipeName |
| 3 | `SmartPiXL.Sentinel/appsettings.json` | Sentinel: EdgeBaseUrl, port 7500 |
| 4 | `C:\inetpub\Smartpixl.info\appsettings.json` | **Prod Edge**: ports 6000/6001 |
| 5 | `C:\inetpub\Smartpixl.info\web.config` | IIS hosting config |

> **Warning:** `dotnet publish` overwrites `web.config` and `appsettings.json` at the destination.
> Always verify these files after publishing Edge to IIS.

---

## Port Quick Reference

| Instance | HTTP | HTTPS | Notes |
|----------|------|-------|-------|
| IIS (Production Edge) | 80/443 | — | External facing via IIS bindings |
| Kestrel (inside IIS) | 6000 | 6001 | Internal only — do NOT access directly |
| Dev (dotnet run Edge) | 7000 | 7001 | Local development only |
| Sentinel | 7500 | — | Dashboard / API |

**Never mix dev and prod ports.** The Forge and Sentinel `EdgeBaseUrl` must point to `http://192.168.88.176` (IIS port 80), not any Kestrel port.
