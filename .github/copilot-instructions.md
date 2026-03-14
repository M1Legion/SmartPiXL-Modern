# SmartPiXL — Copilot Instructions

## Architecture

SmartPiXL is a browser fingerprinting and traffic intelligence platform. Three processes:

| Process | Project | Prod Location | Ports |
|---------|---------|---------------|-------|
| **PiXL Edge** | `SmartPiXL` (IIS) | `C:\inetpub\Smartpixl.info\` | 80/443 via IIS, Kestrel 6000/6001 |
| **SmartPiXL Forge** | `SmartPiXL.Forge` (Windows Service) | `C:\Services\SmartPiXL-Forge\` | — (no HTTP) |
| **SmartPiXL Sentinel** | `SmartPiXL.Sentinel` (Windows Service) | `C:\Services\SmartPiXL-Sentinel\` | 7500 |

Dev ports: Edge 7000/7001, Sentinel 7500. Prod IIS Kestrel: 6000/6001.

**Data flow:** Browser → PiXL Script → `_SMART.GIF` → Edge (parse + fast enrichments) → named pipe → Forge (enrichments + SQL write) → ETL procs → Sentinel (dashboards).

**Failover:** Edge writes JSONL to `C:\inetpub\Smartpixl.info\Failover\` when pipe is unavailable. Forge replays on restart.

## Database

- **Instance:** `localhost\SQL2025` (SQL Server 2025 Developer)
- **Database:** `SmartPiXL`
- **CLR Database:** `SmartPiXL_CLR` (separate, certificate-signed assemblies)
- **Connection String:** `Server=localhost\SQL2025;Database=SmartPiXL;Integrated Security=True;TrustServerCertificate=True`

**Schema design principle:** Objects are separated by domain — `PiXL` for domain tables, `ETL` for pipeline infrastructure, `IPAPI` for IP geolocation, `TrafficAlert` for scoring, `Graph` for identity resolution, `Geo` for geographic data, `dbo` for views/functions. New tables go in the schema that matches their domain.

## Owner Principles

- **SP-first:** Business logic lives in stored procedures. C# services are schedulers and plumbers.
- **No data deletion:** Records are never deleted from domain tables.
- **Incremental build:** Get basics working, build up from there. Don't over-design.

## Critical Sync Files

When changing connection strings, ports, or config, update ALL of these:

| File | Contents |
|------|----------|
| `SmartPiXL/appsettings.json` | Edge dev config (ports 7000/7001) |
| `SmartPiXL.Forge/appsettings.json` | Forge dev config |
| `C:\inetpub\Smartpixl.info\appsettings.json` | **Production Edge** (ports 6000/6001) |
| `SmartPiXL.Shared/Configuration/TrackingSettings.cs` | Compiled default fallback connection string |
| `C:\inetpub\Smartpixl.info\web.config` | IIS hosting config — `dotnet publish` overwrites this |

## Deploy

### Edge (IIS)
```powershell
Import-Module WebAdministration
Stop-WebAppPool -Name "Smartpixl.info"
Push-Location "C:\Users\Administrator\source\repos\SmartPiXL\SmartPiXL"
dotnet publish -c Release -o "C:\inetpub\Smartpixl.info"
Pop-Location
# VERIFY: web.config not clobbered, appsettings.json has prod ports 6000/6001
Start-WebAppPool -Name "Smartpixl.info"
```

### Forge / Sentinel
```powershell
Stop-Service -Name "SmartPiXL-Forge"
dotnet publish SmartPiXL.Forge -c Release -o "C:\Services\SmartPiXL-Forge"
Start-Service -Name "SmartPiXL-Forge"

Stop-Service -Name "SmartPiXL-Sentinel"
dotnet publish SmartPiXL.Sentinel -c Release -o "C:\Services\SmartPiXL-Sentinel"
Start-Service -Name "SmartPiXL-Sentinel"
```

## Troubleshooting

- **No data after deploy:** Check Edge log at `C:\inetpub\Smartpixl.info\Log\`. Usually SQL login failure or clobbered `web.config`.
- **ETL stuck:** Check `ETL.Watermark` table. Reset with `UPDATE ETL.Watermark SET LastProcessedId = 0 WHERE ProcessName = 'ParseNewHits'`.
- **Pipe not connecting:** `Get-Service SmartPiXL-Forge` — is it running? Check `Failover/` for JSONL files.
- **Config change:** Restart IIS pool, Forge service, or Sentinel service as needed.
