# SmartPiXL Tracking Server

A high-performance ASP.NET Core tracking pixel server that captures 100+ data points from website visitors using a single `<script>` tag. No cookies required.

## Overview

SmartPiXL is a complete rewrite of the legacy ASP.NET WebForms tracking pixel system, built on **.NET 10 Minimal APIs** with a normalized star-schema database, real-time ETL pipeline, and a 3D Tron-themed dashboard.

### Key Features

- **One-Line Integration** — Clients add a single `<script>` tag; the pixel JS is generated server-side
- **100+ Data Points** — Screen, device, browser fingerprints, timing, preferences, and evasion countermeasures
- **No Cookies** — Cookieless identification via Canvas, WebGL, Audio, Math, and Font fingerprinting
- **Real-Time ETL** — `EtlBackgroundService` runs every 60 s, parsing raw hits into a materialized warehouse and resolving email identity matches
- **Tron Dashboard** — Live 3D WebGL dashboard at `/tron` powered by SQL views
- **Bot & Datacenter Detection** — Server-side IP behavior analysis, datacenter IP range lookups (AWS/GCP), and fingerprint stability scoring

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- SQL Server (`localhost\SQL2025` with Windows Auth, or configure a connection string)

### Run

```bash
cd TrackingPixel.Modern
dotnet run
```

- HTTP: `http://localhost:7000`
- HTTPS: `https://localhost:7001`

Visit `https://localhost:7001/demo` to see live data collection from your browser.

## Client Integration

Add this to any webpage:

```html
<script src="https://smartpixl.info/js/{CompanyID}/{PiXLID}.js" async></script>
```

The server-generated JS collects all data points and fires them back as a query string on a `_SMART.GIF` request.

## Architecture

```
TrackingPixel.Modern/
├── Program.cs                  # Minimal API entry point, DI, Kestrel config
├── Configuration/
│   └── TrackingSettings.cs     # Typed config (connection string, batch settings)
├── Endpoints/
│   ├── TrackingEndpoints.cs    # Pixel serving, JS generation, enrichment pipeline
│   └── DashboardEndpoints.cs   # Tron HTML, /api/dash/* JSON endpoints
├── Models/
│   ├── TrackingData.cs         # 100+ field model
│   ├── IpClassification.cs     # IP classification enum
│   └── InfraHealthModels.cs    # DTOs for infrastructure health probes
├── Services/
│   ├── DatabaseWriterService.cs        # Channel<T> → SqlBulkCopy to PiXL.Test
│   ├── TrackingCaptureService.cs       # HTTP request → TrackingData parser
│   ├── EtlBackgroundService.cs         # Calls ETL.usp_ParseNewHits + ETL.usp_MatchVisits every 60s
│   ├── FingerprintStabilityService.cs  # Per-IP fingerprint variation detection
│   ├── IpBehaviorService.cs            # Subnet /24 velocity & rapid-fire detection
│   ├── DatacenterIpService.cs          # AWS/GCP IP range downloader
│   ├── IpClassificationService.cs      # Datacenter / residential / reserved classification
│   ├── InfraHealthService.cs           # Probes Windows services, SQL, IIS health
│   └── FileTrackingLogger.cs           # Daily rolling log files
├── SQL/                        # Migration scripts (05–24)
├── wwwroot/
│   ├── tron.html               # 3D Tron dashboard (Three.js)
│   └── demo.html               # Live data collection demo
└── docs/                       # Architecture, field reference, ETL plans, security
```

## Data Pipeline

```
Browser → IIS (443) → ASP.NET Core InProcess
  → TrackingEndpoints.cs    — route: /{**path} ending in _SMART.GIF
  → TrackingCaptureService  — parse HTTP request into TrackingData (100+ fields)
  → DatabaseWriterService   — Channel<T> queue → SqlBulkCopy into PiXL.Test (9 columns)
  → EtlBackgroundService    — every 60s:
      1. ETL.usp_ParseNewHits  → PiXL.Parsed (~175 columns, materialized warehouse)
      2. ETL.usp_MatchVisits   → PiXL.Device, PiXL.IP, PiXL.Visit, PiXL.Match
  → vw_Dash_* views         — power the Tron dashboard at /tron
```

## Database

| Property | Value |
|----------|-------|
| **Instance** | `localhost\SQL2025` (MSSQL 2025 Developer) |
| **Database** | `SmartPiXL` (capital X and L) |
| **Connection** | `Server=localhost\SQL2025;Database=SmartPiXL;Integrated Security=True;TrustServerCertificate=True` |
| **Config path** | `appsettings.json` → `Tracking:ConnectionString` |

### Schemas

| Schema | Purpose |
|--------|---------|
| `PiXL` | Domain tables — `Test`, `Parsed`, `Config`, `Device`, `IP`, `Visit`, `Match`, `Company`, `Pixel` |
| `ETL` | Pipeline infrastructure — `Watermark`, `MatchWatermark`, `usp_ParseNewHits`, `usp_MatchVisits` |
| `dbo` | Dashboard views (`vw_Dash_*`), scalar functions |

### Setup

Run the SQL migration scripts in order:

```powershell
$scripts = Get-ChildItem "TrackingPixel.Modern\SQL\*.sql" | Sort-Object Name
foreach ($s in $scripts) {
    sqlcmd -S "localhost\SQL2025" -d SmartPiXL -E -i $s.FullName -b
}
```

## Endpoints

| Route | Purpose |
|-------|---------|
| `/{**path}` (ending `_SMART.GIF`) | Tracking pixel — receives all data |
| `/pixel204/{**path}` | 204-response pixel variant |
| `/js/{CompanyID}/{PiXLID}.js` | Server-generated pixel JavaScript |
| `/demo` | Live data collection demo page |
| `/health` | Health check |
| `/tron` | 3D Tron dashboard (localhost only) |
| `/api/dash/health` | System health JSON |
| `/api/dash/hourly` | Hourly rollup |
| `/api/dash/bots` | Bot breakdown |
| `/api/dash/bot-signals` | Top bot signals |
| `/api/dash/devices` | Device breakdown |
| `/api/dash/evasion` | Evasion summary |
| `/api/dash/behavior` | Behavioral analysis |
| `/api/dash/recent` | Recent hits |
| `/api/dash/fingerprints` | Fingerprint clusters |
| `/api/dash/pipeline` | Pipeline health |
| `/api/dash/infra` | Infrastructure health |

## Configuration

All config is in `appsettings.json` — there are no hardcoded values in `Program.cs`:

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http":  { "Url": "http://*:7000" },
      "Https": { "Url": "https://*:7001" }
    }
  },
  "Tracking": {
    "ConnectionString": "Server=localhost\\SQL2025;Database=SmartPiXL;Integrated Security=True;TrustServerCertificate=True"
  }
}
```

> **Note:** Production (IIS) uses ports 6000/6001. Dev uses 7000/7001. See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## Documentation

| Document | Description |
|----------|-------------|
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | Production IIS deployment guide |
| [docs/FIELD_REFERENCE.md](docs/FIELD_REFERENCE.md) | All 175 PiXL.Parsed columns documented |
| [docs/MVP_ETL_PIPELINE_PLAN.md](docs/MVP_ETL_PIPELINE_PLAN.md) | Star-schema ETL design & implementation plan |
| [docs/EVASION_COUNTERMEASURES.md](docs/EVASION_COUNTERMEASURES.md) | 10 fingerprint evasion vulnerabilities & countermeasures |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Feature roadmap and backlog |
| [docs/FINGERPRINTING_EXPLAINED.md](docs/FINGERPRINTING_EXPLAINED.md) | Non-technical fingerprinting explainer |
| [docs/RESERVED_IP_RANGES.md](docs/RESERVED_IP_RANGES.md) | IPv4/IPv6 reserved range reference |
| [docs/MSSQL_REVIEW.md](docs/MSSQL_REVIEW.md) | SQL Server architecture review (2026-02-02 snapshot) |
| [docs/SmartPiXL_ETL_Pipeline_Map.md](docs/SmartPiXL_ETL_Pipeline_Map.md) | Legacy Xavier pipeline reference |
| [docs/LiQ Database Staleness Report.md](docs/LiQ%20Database%20Staleness%20Report.md) | LiQ database assessment |

## Privacy & Compliance

This tool collects extensive data. Ensure you:

- Have proper consent mechanisms in place
- Include tracking disclosure in privacy policies
- Comply with GDPR, CCPA, and other applicable regulations

## License

Proprietary — M1 Data & Analytics
