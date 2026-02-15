# SmartPiXL — Copilot Custom Instructions

## Deployment Architecture

SmartPiXL runs as an ASP.NET Core app hosted **inprocess** in IIS. There are two independent copies of the application:

| Instance | Location | Ports | Purpose |
|----------|----------|-------|---------|
| **IIS (Production)** | `C:\inetpub\Smartpixl.info\` | 80/443 via IIS binding on `192.168.88.176` | Serves public traffic from `smartpixl.info` |
| **Dev (dotnet run)** | `C:\Users\Administrator\source\repos\SmartPiXL\TrackingPixel.Modern\` | 7000/7001 | Local development/testing |

**The IIS copy is a published build — it has its own `appsettings.json` and `web.config` that are NOT the repo copies.**

## Database

| Property | Value |
|----------|-------|
| **SQL Server Instance** | `localhost\SQL2025` (MSSQL 2025 Developer) |
| **Database Name** | `SmartPiXL` (capital X and L) |
| **Connection String** | `Server=localhost\SQL2025;Database=SmartPiXL;Integrated Security=True;TrustServerCertificate=True` |
| **IIS App Pool Identity** | `IIS APPPOOL\Smartpixl.info` (needs login + db_datareader/db_datawriter/execute) |
| **Old Instance (RETIRED)** | `localhost` default instance, database `SmartPixl` — do NOT use |

## Critical Files That Must Stay In Sync

When changing **connection strings**, **ports**, or **config**, you must update ALL of these:

| # | File | What Lives There |
|---|------|------------------|
| 1 | `TrackingPixel.Modern/appsettings.json` | Dev connection string, Kestrel ports 7000/7001 |
| 2 | `C:\inetpub\Smartpixl.info\appsettings.json` | **Production** connection string, Kestrel ports 6000/6001 |
| 3 | `TrackingPixel.Modern/Configuration/TrackingSettings.cs` | Compiled default fallback connection string |
| 4 | `C:\inetpub\Smartpixl.info\web.config` | ASP.NET Core module config for IIS hosting |
| 5 | `TrackingPixel.Modern/web.config` | Source web.config (copied during publish) |

**⚠ `dotnet publish` overwrites `web.config` at the destination. If you publish, verify `web.config` afterwards.**

**⚠ The IIS `appsettings.json` uses ports 6000/6001 (IIS internal). The dev copy uses 7000/7001. Do NOT make them the same.**

## Deploying an Update to IIS

### Step-by-step:

```powershell
# 1. Stop the IIS app pool (graceful shutdown)
Import-Module WebAdministration
Stop-WebAppPool -Name "Smartpixl.info"

# 2. Publish from source
Push-Location "C:\Users\Administrator\source\repos\SmartPiXL\TrackingPixel.Modern"
dotnet publish -c Release -o "C:\inetpub\Smartpixl.info"
Pop-Location

# 3. CRITICAL: Verify web.config wasn't clobbered by publish
# It MUST contain the AspNetCoreModuleV2 handler and requestLimits
# If missing, the app will not start via IIS
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
      <aspNetCore processPath="dotnet" arguments=".\TrackingPixel.dll"
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

If the IIS app pool identity can't connect to a SQL Server instance, run:

```sql
-- On the target SQL Server instance
CREATE LOGIN [IIS APPPOOL\Smartpixl.info] FROM WINDOWS;

USE SmartPiXL;
CREATE USER [IIS APPPOOL\Smartpixl.info] FOR LOGIN [IIS APPPOOL\Smartpixl.info];
ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\Smartpixl.info];
ALTER ROLE db_datawriter ADD MEMBER [IIS APPPOOL\Smartpixl.info];
GRANT EXECUTE TO [IIS APPPOOL\Smartpixl.info];
```

## Data Pipeline

```
Browser → IIS (443) → ASP.NET Core InProcess
  → TrackingEndpoints.cs (route: /{**path} ending in _SMART.GIF)
  → TrackingCaptureService.cs (parse request into TrackingData)
  → DatabaseWriterService.cs (Channel<T> queue → SqlBulkCopy)
  → PiXL.Test (raw ingest, 9 columns)
  → EtlBackgroundService (every 60s):
      1. ETL.usp_ParseNewHits → PiXL.Parsed (~175 cols, materialized warehouse)
                              → PiXL.Device, PiXL.IP, PiXL.Visit
      2. ETL.usp_MatchVisits  → PiXL.Match (identity resolution vs AutoConsumer)
  → vw_Dash_* views (power the Tron dashboard at /tron)
```

## Dashboard

The **Tron dashboard** is the only dashboard. It's served from `wwwroot/tron.html` at the `/tron` endpoint (localhost-only). It calls `/api/dash/*` endpoints which query `vw_Dash_*` SQL views reading from `PiXL.Parsed`.

There is no separate diagnostics dashboard. The old `TrackingPixel.Diagnostics` project (port 5050) was removed.

## Troubleshooting

### No data after deploy
1. Check `C:\inetpub\Smartpixl.info\Log\{date}.log` — look for "Failed to write batch" errors
2. Most common cause: **SQL login failed for IIS app pool identity** on the target instance
3. Second most common: **`web.config` was emptied/overwritten** by `dotnet publish`

### Dashboard shows no data
1. `PiXL.Test` has rows but `PiXL.Parsed` doesn't → ETL proc hasn't run. Check `ETL.Watermark` table and run `EXEC ETL.usp_ParseNewHits` manually
2. Watermark is ahead of PiXL.Test max ID → Reset: `UPDATE ETL.Watermark SET LastProcessedId = 0, RowsProcessed = 0 WHERE ProcessName = 'ParseNewHits'`

### Process needs restart after config change
The running process caches `appsettings.json` at startup. After changing config:
- **IIS**: `iisreset` or `Stop-WebAppPool` / `Start-WebAppPool`
- **Dev**: Kill the `TrackingPixel` process and re-run `dotnet run`
