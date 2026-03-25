---
title: SmartPiXL Platform
node: smartpixl
nodeType: platform
status: current
related:
  - systems/edge
  - systems/forge
  - systems/sentinel
---

# SmartPiXL Platform

## Atlas Public

SmartPiXL is a next-generation visitor intelligence platform that identifies and enriches website visitor data in real time — without cookies. When someone visits your website, SmartPiXL instantly captures over 200 data points about their device, browser, and behavior, then enriches that data with geographic, corporate, and behavioral intelligence.

The result: you know who's visiting, where they're from, what device they're using, whether they're a real person or a bot, and whether you've seen them before — all within milliseconds, all without disrupting their experience.

**Key capabilities:**
- **Cookieless identification** — works even when visitors block cookies or use privacy tools
- **Real-time enrichment** — geographic, behavioral, and device intelligence added instantly
- **Bot detection** — over 80 signals distinguish real visitors from automated traffic
- **Cross-session tracking** — recognize returning visitors across multiple visits
- **Zero visitor impact** — invisible to the end user, no performance degradation

**What the data pipeline delivers:**
- **Identity enrichment** — parsed device type, browser, OS, and model from every visit
- **Geographic intelligence** — city-level location, ISP, network type, timezone, and cultural context
- **Behavioral analysis** — session stitching, mouse movement authenticity, multi-page engagement tracking
- **Quality scoring** — a 0-100 lead quality score that separates real prospects from noise
- **Threat detection** — known bot identification, impossible device configurations, behavioral replay detection

## Atlas Internal

SmartPiXL runs as three separate processes that work together on a single Windows Server:

### The Three Processes

1. **PiXL Edge** (the web server) — the front door. When a visitor loads a page with the tracking pixel, Edge receives the data, performs 12 fast in-memory enrichment checks, and returns a tiny invisible image. Response time: under 10 milliseconds. The visitor never notices.

2. **SmartPiXL Forge** (the enrichment engine) — the brain. Edge passes visitor data to Forge through a named pipe. Forge runs 16 enrichment services, writes enriched records to SQL, and runs the ETL pipeline that resolves visitor identities. Seven subsystems (F1–F7) handle ingest, enrichment, SQL writing, failover, ETL, background IP, and data sync.

3. **SmartPiXL Sentinel** (the dashboard) — the face. Sentinel serves the Tron operations dashboard (internal) and the Atlas documentation portal. It reads from the enriched database and presents the data visually.

### Data Flow

```
Browser → PiXL Script (200+ signals) → _SMART.GIF → Edge (12 fast enrichments)
  → Named Pipe → Forge (16 enrichment services → SQL write → ETL)
  → Sentinel (dashboards, Atlas, TrafficAlert API)
```

### Data Durability

If Forge goes down temporarily, Edge writes records to JSONL files on disk. Forge catches up automatically on restart. Zero data loss under any failure scenario.

### Current Status

| Component | Hosting | Status |
|-----------|---------|--------|
| PiXL Edge | IIS InProcess, ports 80/443 | Live |
| SmartPiXL Forge | Windows Service, port 7100 | Live |
| SmartPiXL Sentinel | Windows Service, port 7500 | Live |
| SQL Server 2025 | localhost\SQL2025 | Live |

## Atlas Technical

SmartPiXL is a 3-process .NET 10 application targeting SQL Server 2025 Developer Edition:

### Process Architecture

| Process | Project | Runtime | Hosting | Purpose |
|---------|---------|---------|---------|---------|
| **PiXL Edge** | `SmartPiXL/` | ASP.NET Core (InProcess) | IIS `w3wp.exe` | HTTP pixel capture, 12 fast enrichments |
| **SmartPiXL Forge** | `SmartPiXL.Forge/` | Worker Service | Windows Service | Named pipe server, 16 enrichments, ETL, SQL writer |
| **SmartPiXL Sentinel** | `SmartPiXL.Sentinel/` | ASP.NET Core | Windows Service (port 7500) | Tron ops, Atlas portal, TrafficAlert API |
| **Shared Library** | `SmartPiXL.Shared/` | Class Library | Referenced by all | Models, configuration, interfaces |

### Inter-Process Communication

```
Edge → NamedPipeClientStream("SmartPiXL-Enrichment") → Forge NamedPipeServerStream
  Payload: TrackingData JSON line (~4 KB per record, 231 fields)
  Failover: JSONL file to Failover/ directory if pipe unavailable
  Catch-up: Failover replay on Forge restart
```

### Request Lifecycle

| Stage | Where | Duration | What Happens |
|-------|-------|----------|-------------|
| Browser collection | PiXL Script | 80–500ms | 200+ signals captured (canvas, WebGL, audio, behavior) |
| Fast enrichment | Edge | <10ms | 12 in-memory checks (datacenter IP, fingerprint stability, geo cache) |
| Deep enrichment | Forge | 1–5s async | 16 services (UA parsing, bot detection, DNS, geo, behavioral, scoring) |
| SQL write | Forge | <100ms | SqlBulkCopy → PiXL.Parsed (231 columns) |
| ETL | Forge | every 60s | Identity resolution, visitor scoring, customer summaries |

### Database Schema

| Schema | Purpose |
|--------|---------|
| `PiXL` | Domain tables — Parsed, Device, IP, Visit, Match, Company, Settings |
| `ETL` | Pipeline infrastructure — Watermark, BatchLog, ErrorLog |
| `IPAPI` | IP geolocation data from MaxMind, IPAPI, IPtoASN, DB-IP |
| `TrafficAlert` | Scoring — VisitorScore, CustomerSummary |
| `Graph` | Identity graph — DeviceContact, ContactMerge |
| `Health` | Health tree — Node table (49 nodes) |
| `Docs` | Atlas metadata — Metric, SystemStatus |
| `Geo` | Geographic reference — Country, Region, City |

## Atlas Private

### Solution Structure

```
SmartPixl.sln
├── SmartPiXL/              → PiXL Edge (IIS)
├── SmartPiXL.Forge/        → Forge (Windows Service)
├── SmartPiXL.Sentinel/     → Sentinel (Windows Service)
├── SmartPiXL.Shared/       → Shared library
├── SmartPiXL.SqlClr/       → SQL CLR functions
├── SmartPiXL.SyntheticTraffic/ → Traffic generator (testing)
├── SmartPiXL.Tests/        → Unit tests (543 tests)
└── docs/atlas/             → Atlas markdown content
```

### Deployment

All three processes deploy to the same server. There is no separate dev/staging/prod — dev IS live.

```powershell
# Edge (IIS)
Stop-WebAppPool -Name "Smartpixl.info"
dotnet publish SmartPiXL -c Release -o "C:\inetpub\Smartpixl.info"
Start-WebAppPool -Name "Smartpixl.info"

# Forge
Stop-Service SmartPiXL-Forge
dotnet publish SmartPiXL.Forge -c Release -o "C:\Services\SmartPiXL-Forge"
Start-Service SmartPiXL-Forge

# Sentinel
Stop-Service SmartPiXL-Sentinel
dotnet publish SmartPiXL.Sentinel -c Release -o "C:\Services\SmartPiXL-Sentinel"
Start-Service SmartPiXL-Sentinel
```

### Health Tree

The Health.Node table (49 nodes) defines the monitoring hierarchy that drives both Tron health view and this Atlas documentation structure. Node types: platform → system → subsystem → component → probe.
