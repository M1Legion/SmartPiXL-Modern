---
subsystem: deployment
title: Deployment
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - architecture/overview
  - architecture/edge
  - architecture/forge
  - operations/troubleshooting
---

# Deployment

## Atlas Public

SmartPiXL runs as a self-hosted service on dedicated Windows infrastructure. Deployment is handled by the platform engineering team using automated scripts that ensure zero-downtime updates and configuration consistency.

## Atlas Internal

### Three Deployable Components

| Component | Deployment Target | Method |
|-----------|------------------|--------|
| **PiXL Edge** | IIS on `C:\inetpub\Smartpixl.info\` | `dotnet publish` → IIS app pool restart |
| **SmartPiXL Forge** | Windows Service `SmartPiXL-Forge` | `dotnet publish` → service restart |
| **SmartPiXL Sentinel** | Windows Service `SmartPiXL-Sentinel` | `dotnet publish` → service restart |

### Deployment Impact

| Component | During Deploy | Recovery Time |
|-----------|--------------|---------------|
| Edge | Brief period (seconds) where requests queue or fail | ~5 seconds for app pool restart |
| Forge | Enrichment paused; Edge writes to JSONL failover | ~10 seconds; failover catch-up automatic |
| Sentinel | Dashboard unavailable | ~5 seconds |

### Port Configuration

| Component | Dev Ports | Production Ports |
|-----------|-----------|-----------------|
| Edge (Kestrel) | 7000 / 7001 | 6000 / 6001 |
| Edge (IIS external) | N/A | 80 / 443 |
| Sentinel | 7500 | 7500 |
| Forge | None (pipe only) | None (pipe only) |

**Critical**: Dev and production Edge ports MUST differ. IIS proxies 80/443 → Kestrel 6000/6001 in production.

## Atlas Technical

### Deploying PiXL Edge (IIS)

```powershell
# 1. Stop the IIS app pool (graceful shutdown)
Import-Module WebAdministration
Stop-WebAppPool -Name "Smartpixl.info"

# 2. Publish Edge from source
Push-Location "C:\Users\Administrator\source\repos\SmartPiXL\SmartPiXL"
dotnet publish -c Release -o "C:\inetpub\Smartpixl.info"
Pop-Location

# 3. CRITICAL: Verify web.config wasn't clobbered
type "C:\inetpub\Smartpixl.info\web.config"

# 4. CRITICAL: Verify appsettings.json has production values
type "C:\inetpub\Smartpixl.info\appsettings.json"

# 5. Start the app pool
Start-WebAppPool -Name "Smartpixl.info"

# 6. Verify — send a test hit and check the log
Invoke-WebRequest -Uri "http://192.168.88.176/DEMO/deploy-test_SMART.GIF?verify=1" -UseBasicParsing | Out-Null
Start-Sleep -Seconds 3
Get-Content "C:\inetpub\Smartpixl.info\Log\$(Get-Date -Format 'yyyy_MM_dd').log" -Tail 10
```

### `web.config` Required Content

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
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2"
             resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="dotnet" arguments=".\SmartPiXL.dll"
                  stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout"
                  hostingModel="inprocess" />
    </system.webServer>
  </location>
</configuration>
```

Key settings:
- `maxQueryString="16384"` — PiXL Script sends large querystrings (159 fields)
- `maxUrl="8192"` — Long URLs with embedded field data
- `hostingModel="inprocess"` — Required for hot path performance (~zero IPC overhead)
- `stdoutLogEnabled="true"` — Captures startup errors

### `appsettings.json` — Production vs Dev

| Property | Dev | Production (IIS) |
|----------|-----|-----------------|
| Kestrel HTTP | `http://*:7000` | `http://*:6000` |
| Kestrel HTTPS | `https://*:7001` | `https://*:6001` |
| ConnectionString | Same | Same |
| PipeName | `SmartPiXL-Enrichment` | `SmartPiXL-Enrichment` |

### Deploying SmartPiXL Forge

```powershell
# Stop the service
Stop-Service -Name "SmartPiXL-Forge" -ErrorAction SilentlyContinue

# Publish
Push-Location "C:\Users\Administrator\source\repos\SmartPiXL\SmartPiXL.Forge"
dotnet publish -c Release -o "C:\Services\SmartPiXL-Forge"
Pop-Location

# First time only:
# sc.exe create SmartPiXL-Forge binPath= "C:\Services\SmartPiXL-Forge\SmartPiXL.Forge.exe"

# Start
Start-Service -Name "SmartPiXL-Forge"
```

### Deploying SmartPiXL Sentinel

```powershell
Stop-Service -Name "SmartPiXL-Sentinel" -ErrorAction SilentlyContinue

Push-Location "C:\Users\Administrator\source\repos\SmartPiXL\SmartPiXL.Sentinel"
dotnet publish -c Release -o "C:\Services\SmartPiXL-Sentinel"
Pop-Location

# First time only:
# sc.exe create SmartPiXL-Sentinel binPath= "C:\Services\SmartPiXL-Sentinel\SmartPiXL.Sentinel.exe"

Start-Service -Name "SmartPiXL-Sentinel"
```

### SQL Server Permissions

The IIS App Pool identity needs database access:

```sql
CREATE LOGIN [IIS APPPOOL\Smartpixl.info] FROM WINDOWS;
USE SmartPiXL;
CREATE USER [IIS APPPOOL\Smartpixl.info] FOR LOGIN [IIS APPPOOL\Smartpixl.info];
ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\Smartpixl.info];
ALTER ROLE db_datawriter ADD MEMBER [IIS APPPOOL\Smartpixl.info];
GRANT EXECUTE TO [IIS APPPOOL\Smartpixl.info];
```

### Files That Must Stay In Sync

| # | File | What Lives There |
|---|------|-----------------|
| 1 | `SmartPiXL/appsettings.json` | Edge dev config |
| 2 | `SmartPiXL.Forge/appsettings.json` | Forge dev config |
| 3 | `C:\inetpub\Smartpixl.info\appsettings.json` | **Production Edge** config |
| 4 | `SmartPiXL.Shared/Configuration/TrackingSettings.cs` | Compiled fallback defaults |
| 5 | `C:\inetpub\Smartpixl.info\web.config` | IIS module config |
| 6 | `SmartPiXL/web.config` | Source web.config |

## Atlas Private

### `dotnet publish` Overwrites web.config

This is the #1 deployment footgun. Every `dotnet publish` generates a fresh web.config. If the production web.config has custom settings (which it does — `maxQueryString`, `maxUrl`), they're silently destroyed.

**Mitigation**: Always check web.config after publish. The deployment script explicitly does `type web.config` as a manual verification step. There's no automated comparison — it relies on the operator noticing the content.

**Better mitigation** (not implemented): Copy the known-good web.config from source control after publish:
```powershell
Copy-Item "C:\Users\Administrator\source\repos\SmartPiXL\SmartPiXL\web.config" `
          "C:\inetpub\Smartpixl.info\web.config" -Force
```

### Port Collision Risk

If someone configures dev appsettings.json with port 6000 and publishes to IIS, both dev and IIS try to bind 6000. IIS wins (it starts first), dev `dotnet run` fails with "address already in use."

### Forge Service Account

The Forge runs under LOCAL SYSTEM by default (Windows Service). This means:
- It has full access to the local filesystem (Failover directory, logs)
- SQL connection uses the machine account (`DOMAIN\MACHINENAME$`)
- The machine account needs the same SQL permissions as the IIS app pool

If using a dedicated service account, update: `sc.exe config SmartPiXL-Forge obj= "DOMAIN\ServiceAccount" password= "..."`.

### IIS App Pool Recycling

By default, IIS recycles the app pool every 29 hours (1740 minutes). During recycling:
- A new w3wp.exe process starts (receiving new requests)
- The old w3wp.exe process drains existing requests
- Overlap window: ~5 seconds where both processes are running
- Named pipe connection: the old process's pipe disconnects; new process connects

During the overlap:
- The old process may fail to send to the pipe (pipe broken) → JSONL failover activates
- The new process establishes a fresh pipe connection
- Failover files from the overlap are caught up when the Forge processes them

This is tested and works correctly. No manual intervention needed for app pool recycles.

### First Deployment Checklist

For initial server setup:

1. Install .NET 10 Runtime + ASP.NET Core Module + Hosting Bundle
2. Install SQL Server 2025 Developer (`localhost\SQL2025`)
3. Create SmartPiXL database with all schemas (run migrations 17B through 59)
4. Create IIS site "Smartpixl.info" with app pool
5. Configure IIS bindings (80/443 with SSL cert)
6. Set app pool identity and SQL permissions
7. Deploy Edge (publish + verify web.config + verify appsettings.json)
8. Register and deploy Forge service
9. Register and deploy Sentinel service
10. Send test traffic and verify end-to-end: Edge → Forge → PiXL.Raw → ETL → PiXL.Parsed
