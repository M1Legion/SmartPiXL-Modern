---
subsystem: sentinel
title: SmartPiXL Sentinel
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - architecture/overview
  - architecture/forge
  - subsystems/traffic-alerts
  - operations/monitoring
---

# SmartPiXL Sentinel

## Atlas Public

The SmartPiXL Sentinel is your window into visitor intelligence. It's the dashboard service that presents all the data SmartPiXL collects, enriches, and analyzes — giving you clear, actionable views of your website traffic.

**What you'll see:**

- **Traffic quality overview** — instantly see what percentage of your traffic is real people vs bots
- **Visitor detail** — drill into individual visit records with full enrichment data
- **Quality trends** — track how your traffic quality changes over time (weekly, monthly)
- **Device intelligence** — what devices and platforms your visitors use
- **Geographic distribution** — where your visitors are really coming from
- **Cross-customer insights** — identify visitors who appear across multiple websites
- **Session analysis** — understand how visitors navigate through your site

## Atlas Internal

### What the Sentinel Does

The Sentinel is a pure HTTP server — it reads from the database and displays the results. It doesn't process data or run background enrichment. That's the Forge's job.

Three areas of the Sentinel:

1. **Tron Dashboard** (internal, for M1 ops)
   - Infrastructure health monitoring
   - ETL pipeline status
   - Real-time traffic flow metrics
   - Remediation queue (approve/skip proposed fixes)

2. **TrafficAlert API** (new, for customer-facing reports)
   - Per-visitor composite quality scores
   - Per-customer traffic quality summaries with letter grades (A-F)
   - Time-series trend data for charting
   - Filterable by company, date range, and quality bucket

3. **Atlas Portal** (documentation and metrics)
   - Multi-tier documentation for different audiences
   - Live system metrics
   - API reference

### Quality Grades

Each customer gets a quality grade based on their bot traffic percentage:

| Grade | Bot Rate | Meaning |
|-------|----------|---------|
| A | < 20% | Excellent — mostly human traffic |
| B | 20-40% | Good — manageable bot level |
| C | 40-60% | Concerning — significant bot presence |
| D | 60-80% | Poor — mostly bot traffic |
| F | > 80% | Critical — overwhelmingly automated |

### Dashboard Data Sources

All dashboard data comes from pre-built SQL views (35+ views across `dbo.vw_Dash_*` and `dbo.vw_TrafficAlert_*`). The Sentinel never runs ad-hoc queries — it reads from views optimized for dashboard performance.

## Atlas Technical

### Project Structure

`SmartPiXL.Sentinel/` — an ASP.NET Core web application on port 7500:

```
SmartPiXL.Sentinel/
├── Program.cs                            # Composition root, endpoint mapping
├── appsettings.json                      # Port 7500, SQL connection, SMTP
├── Endpoints/
│   ├── DashboardEndpoints.cs             # /api/dash/* (Tron dashboard)
│   ├── AtlasEndpoints.cs                 # /atlas, /api/atlas/*
│   └── TrafficAlertEndpoints.cs          # /api/traffic-alert/*
├── Services/
│   ├── InfraHealthService.cs             # Parallel infrastructure probes
│   ├── RemediationService.cs             # Approve/skip remediation entries
│   ├── EmailNotificationService.cs       # SMTP + SMS notifications
│   └── HttpEdgeHealthClient.cs           # Edge health HTTP client
└── wwwroot/
    ├── tron.html                         # Tron operations dashboard
    ├── atlas.html                        # Atlas documentation portal
    └── tron/                             # JS modules for Tron UI
        ├── api.mjs
        ├── arena.mjs
        ├── camera.mjs
        ├── cycles.mjs
        ├── particles.mjs
        ├── pathing.mjs
        ├── scene.mjs
        └── trails.mjs
```

### API Endpoints

**Dashboard (`/api/dash/`)**

| Endpoint | View/Source | Purpose |
|----------|-----------|---------|
| `GET /api/dash/health` | InfraHealthService | Live infrastructure probes |
| `GET /api/dash/pipeline` | ETL.Watermark | ETL pipeline status |
| `GET /api/dash/stats` | `vw_Dashboard_*` views | Core tracking statistics |
| `GET /api/dash/sessions` | `vw_Dash_SessionSummary` | Session reconstruction |
| `GET /api/dash/dead-internet` | `vw_Dash_DeadInternet` | Dead internet index |
| `GET /api/dash/customer-quality` | `vw_Dash_CustomerQuality` | Traffic quality per customer |
| `GET /api/dash/cross-customer` | `vw_Dash_CrossCustomer` | Cross-customer device tracking |
| `GET /api/dash/impossible-travel` | `vw_Dash_ImpossibleTravel` | Geographic anomalies |
| `GET /api/dash/device-lifecycle` | `vw_Dash_DeviceLifecycle` | Device age/lifecycle |
| `GET /api/dash/subnet-clusters` | `vw_Dash_SubnetClusters` | Subnet reputation |
| `GET /api/dash/remediations` | Ops.RemediationLog | Pending remediations |
| `POST /api/dash/remediate/{id}` | RemediationService | Execute a remediation |
| `POST /api/dash/skip/{id}` | RemediationService | Skip a remediation |

**TrafficAlert (`/api/traffic-alert/`)**

| Endpoint | View | Purpose |
|----------|------|---------|
| `GET /api/traffic-alert/visitors` | `vw_TrafficAlert_VisitorDetail` | Paginated visitor scores |
| `GET /api/traffic-alert/visitors/{id}` | `vw_TrafficAlert_VisitorDetail` | Single visitor detail |
| `GET /api/traffic-alert/customers` | `vw_TrafficAlert_CustomerOverview` | Customer summaries |
| `GET /api/traffic-alert/customers/{id}` | `vw_TrafficAlert_CustomerOverview` | Single customer detail |
| `GET /api/traffic-alert/trend` | `vw_TrafficAlert_Trend` | Time-series data |
| `GET /api/traffic-alert/summary` | `vw_TrafficAlert_CustomerOverview` | Aggregate KPIs |

**Atlas (`/api/atlas/`)**

| Endpoint | Source | Purpose |
|----------|--------|---------|
| `GET /atlas` | `atlas.html` | Documentation portal |
| `GET /api/atlas/docs` | `docs/atlas/**/*.md` | Documentation catalog |
| `GET /api/atlas/docs/{path}` | `docs/atlas/{path}.md` | Single doc (tier-filtered) |
| `GET /api/atlas/metrics` | SQL aggregate queries | Live system metrics |

### Architecture Decision: No Background Services

The Sentinel is a **pure HTTP server** with zero `AddHostedService` calls. All background work (ETL, self-healing loop, maintenance scheduling, data sync) runs in the Forge. The Sentinel only:
- Reads from SQL views → serves JSON
- Calls `RemediationService` methods on operator request
- Probes infrastructure health on-demand (not on a timer)

This prevents the architectural mistake of the deprecated Worker, which mixed background processing with dashboard serving.

### Deployment

```powershell
Stop-Service -Name "SmartPiXL-Sentinel"
dotnet publish -c Release -o "C:\Services\SmartPiXL-Sentinel"
Start-Service -Name "SmartPiXL-Sentinel"
```

Port 7500. Runs as `NT AUTHORITY\SYSTEM`. Requires SQL login with `db_datareader` and `EXECUTE`.

## Atlas Private

### Why Sentinel Was Separated from the Forge

The deprecated Worker combined background processing with dashboard serving. This created coupling between the data pipeline and the UI layer — restarting the Worker for a dashboard fix would interrupt ETL processing, and ETL errors would take down dashboards. The Forge/Sentinel split eliminates this:

- Forge crashes → Sentinel keeps serving dashboards (stale but available). Data queues on disk via JSONL failover.
- Sentinel crashes → Forge keeps enriching and writing to SQL. No data loss, just no UI.
- Sentinel redeploy → zero-downtime for the data pipeline.

### `RemediationService` vs `SelfHealingService`

The original Worker's `SelfHealingService` (711 lines) combined detection + execution. In the new architecture:
- **Forge's `SelfHealingService`** — 60-second detection loop, writes proposed remediations to `Ops.RemediationLog` with `Status = 'Proposed'`
- **Sentinel's `RemediationService`** — 3 methods: `ListRemediationsAsync()`, `ExecuteRemediationAsync(id)`, `SkipRemediationAsync(id)`

The detect→propose→approve→execute model gives the operator control. Auto-execution is still possible (the Forge can mark remediations as auto-approved based on type), but the Sentinel provides the operator-in-the-loop interface.

### `InfraHealthService` Probe Targets

Parallel probes launched simultaneously:

| Probe | What | Alert Condition |
|-------|------|----------------|
| Windows Services | `MSSQL$SQL2025`, `SmartPiXL-Forge`, `SmartPiXL-Sentinel`, `W3SVC` | Any not running |
| SQL Connectivity | Test query to `localhost\SQL2025` | Connection failure |
| IIS Website | Check `Smartpixl.info` site status | Not started |
| Edge Health | `GET http://192.168.88.176/internal/health` | Non-200 or timeout |
| Data Flow | Compare PiXL.Raw count vs watermark vs last write time | Stale data (>5 min gap) |
| Pipeline Depth | Read Forge channel depths from health endpoint | Growing backlog |
| Log Errors | Scan today's log file for ERROR lines | New errors |

### Known Limitations

- The Sentinel currently renders the Tron dashboard via static HTML files (`tron.html` + JS modules). There's no server-side rendering — the JS client calls API endpoints and renders in the browser.
- Atlas documentation is served from Markdown files parsed at runtime. No SQL sync yet — that was designed for a future iteration where `Docs.Section` table stores parsed documentation for SQL-based querying.
- No authentication on any endpoint. All endpoints are accessible to anyone on the network. Authentication should be added before exposing Sentinel externally.
