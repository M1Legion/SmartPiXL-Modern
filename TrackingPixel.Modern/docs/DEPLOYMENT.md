# SmartPiXL — Production Deployment Guide

> **Canonical reference.** This is the single authoritative deployment document.
> The Copilot instructions in `.github/copilot-instructions.md` mirror the critical values here.

---

## Deployment Architecture

SmartPiXL runs as an ASP.NET Core (.NET 10) app hosted **InProcess** in IIS.

| Instance | Location | Ports | Purpose |
|----------|----------|-------|---------|
| **IIS (Production)** | `C:\inetpub\Smartpixl.info\` | 80 / 443 via IIS binding on `192.168.88.176` | Public traffic from `smartpixl.info` |
| **Dev (`dotnet run`)** | `C:\Users\Administrator\source\repos\SmartPiXL\TrackingPixel.Modern\` | 7000 / 7001 | Local development / testing |

The IIS copy is a **published build** with its own `appsettings.json` and `web.config` that are independent of the repo copies.

---

## Prerequisites

- Windows Server with IIS and the **ASP.NET Core Hosting Bundle for .NET 10**
- SQL Server 2025 Developer (`localhost\SQL2025`) with Windows Authentication
- The `SmartPiXL` database already created and migrations applied (see [README.md](../README.md#setup))

---

## Step 1 — Publish

```powershell
Push-Location "C:\Users\Administrator\source\repos\SmartPiXL\TrackingPixel.Modern"
dotnet publish -c Release -o "C:\inetpub\Smartpixl.info"
Pop-Location
```

---

## Step 2 — Verify `web.config`

`dotnet publish` overwrites `web.config`. It **must** contain the `AspNetCoreModuleV2` handler and `requestLimits`:

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
        <add name="aspNetCore" path="*" verb="*"
             modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="dotnet" arguments=".\TrackingPixel.dll"
                  stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout"
                  hostingModel="inprocess" />
    </system.webServer>
  </location>
</configuration>
```

---

## Step 3 — Verify `appsettings.json`

The IIS copy **must** use production ports (6000/6001) and the correct connection string:

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

> **Warning:** Dev uses 7000/7001. Production uses 6000/6001. Never make them the same.

---

## Step 4 — IIS App Pool & SQL Permissions

The IIS app pool is named `Smartpixl.info` and runs under its own identity.

Grant it SQL access:

```sql
-- On localhost\SQL2025
CREATE LOGIN [IIS APPPOOL\Smartpixl.info] FROM WINDOWS;

USE SmartPiXL;
CREATE USER  [IIS APPPOOL\Smartpixl.info] FOR LOGIN [IIS APPPOOL\Smartpixl.info];
ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\Smartpixl.info];
ALTER ROLE db_datawriter ADD MEMBER [IIS APPPOOL\Smartpixl.info];
GRANT EXECUTE TO [IIS APPPOOL\Smartpixl.info];
```

---

## Step 5 — Start & Verify

```powershell
Import-Module WebAdministration
Start-WebAppPool -Name "Smartpixl.info"

# Send a test hit
Invoke-WebRequest -Uri "http://192.168.88.176/DEMO/deploy-test_SMART.GIF?verify=1" -UseBasicParsing | Out-Null
Start-Sleep -Seconds 3

# Check the log
Get-Content "C:\inetpub\Smartpixl.info\Log\$(Get-Date -Format 'yyyy_MM_dd').log" -Tail 10
```

---

## Full Deploy Script (Copy-Paste)

```powershell
# 1. Stop
Import-Module WebAdministration
Stop-WebAppPool -Name "Smartpixl.info"

# 2. Publish
Push-Location "C:\Users\Administrator\source\repos\SmartPiXL\TrackingPixel.Modern"
dotnet publish -c Release -o "C:\inetpub\Smartpixl.info"
Pop-Location

# 3. Verify web.config
type "C:\inetpub\Smartpixl.info\web.config"

# 4. Verify appsettings.json (ports 6000/6001, Database=SmartPiXL)
type "C:\inetpub\Smartpixl.info\appsettings.json"

# 5. Start
Start-WebAppPool -Name "Smartpixl.info"

# 6. Smoke test
Invoke-WebRequest -Uri "http://192.168.88.176/DEMO/deploy-test_SMART.GIF?verify=1" -UseBasicParsing | Out-Null
Start-Sleep -Seconds 3
Get-Content "C:\inetpub\Smartpixl.info\Log\$(Get-Date -Format 'yyyy_MM_dd').log" -Tail 10
```

---

## Critical Files That Must Stay In Sync

| # | File | What Lives There |
|---|------|------------------|
| 1 | `TrackingPixel.Modern/appsettings.json` | Dev connection string, Kestrel ports 7000/7001 |
| 2 | `C:\inetpub\Smartpixl.info\appsettings.json` | **Production** connection string, Kestrel ports 6000/6001 |
| 3 | `TrackingPixel.Modern/Configuration/TrackingSettings.cs` | Compiled default fallback connection string |
| 4 | `C:\inetpub\Smartpixl.info\web.config` | ASP.NET Core module config for IIS hosting |
| 5 | `TrackingPixel.Modern/web.config` | Source web.config (copied during publish) |

---

## Troubleshooting

### No data after deploy

1. Check `C:\inetpub\Smartpixl.info\Log\{date}.log` for "Failed to write batch" errors
2. Most common: **SQL login failed** for IIS app pool identity on `localhost\SQL2025`
3. Second most common: **`web.config` was overwritten** by `dotnet publish`

### Dashboard shows no data

1. `PiXL.Test` has rows but `PiXL.Parsed` doesn't → ETL hasn't run. Run `EXEC ETL.usp_ParseNewHits` manually.
2. Watermark ahead of max Test ID → Reset:
   ```sql
   UPDATE ETL.Watermark
   SET    LastProcessedId = 0, RowsProcessed = 0
   WHERE  ProcessName = 'ParseNewHits';
   ```

### Process needs restart after config change

The app caches `appsettings.json` at startup.

- **IIS:** `Stop-WebAppPool` / `Start-WebAppPool` (or `iisreset`)
- **Dev:** Kill the `TrackingPixel` process and re-run `dotnet run`

### Service won't start

```powershell
# Check Windows Event Log
Get-EventLog -LogName Application -Newest 20 |
    Where-Object { $_.Source -eq ".NET Runtime" -or $_.Message -like "*TrackingPixel*" }
```

### Connection issues

```powershell
# Verify SQL Server is reachable
Test-NetConnection localhost -Port 1433
```

---

## Client Integration

Give this snippet to clients:

```html
<!-- SmartPiXL Tracking — CompanyID: ACME, PiXL: main -->
<script src="https://smartpixl.info/js/ACME/main.js" async></script>
```

Or the direct image tag (no JavaScript fingerprinting):

```html
<img src="https://smartpixl.info/ACME/main_SMART.GIF" width="1" height="1" style="display:none" alt="">
```
