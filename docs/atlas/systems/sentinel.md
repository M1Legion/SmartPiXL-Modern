---
title: SmartPiXL Sentinel
node: sentinel
nodeType: system
status: current
related:
  - platform/smartpixl
  - systems/edge
  - systems/forge
---

# SmartPiXL Sentinel

## Atlas Public

The SmartPiXL Sentinel is your window into visitor intelligence. It's the dashboard service that presents all the data SmartPiXL collects, enriches, and analyzes — giving you clear, actionable views of your website traffic.

**What you'll see:**

- **Traffic quality overview** — instantly see what percentage of your traffic is real people vs bots
- **Visitor detail** — drill into individual visit records with full enrichment data
- **Quality trends** — track how your traffic quality changes over time
- **Device intelligence** — what devices and platforms your visitors use
- **Geographic distribution** — where your visitors are really coming from
- **Session analysis** — understand how visitors navigate through your site

## Atlas Internal

### What the Sentinel Does

The Sentinel is a pure HTTP server — it reads from the database and displays results. It doesn't process data or run enrichment. That's the Forge's job.

Three areas:

1. **Tron Dashboard** (internal, for M1 ops)
   - Infrastructure health monitoring (49-node health tree)
   - Pipeline status (PARSED → VISITS → IP MATCH → RESOLVED)
   - Real-time traffic flow metrics
   - Remediation queue (approve/skip proposed fixes)

2. **TrafficAlert API** (customer-facing reports)
   - Per-visitor composite quality scores
   - Per-customer traffic quality summaries with letter grades (A–F)
   - Time-series trend data for charting

3. **Atlas Portal** (documentation)
   - Multi-tier documentation for different audiences
   - Live system metrics from SQL
   - PiXL Script live demo

### Quality Grades

| Grade | Bot Rate | Meaning |
|-------|----------|---------|
| A | < 20% | Excellent — mostly human traffic |
| B | 20–40% | Good — manageable bot level |
| C | 40–60% | Concerning — significant bot presence |
| D | 60–80% | Poor — mostly bot traffic |
| F | > 80% | Critical — overwhelmingly automated |

### Health Probes

Sentinel exposes 4 health probes:

| Probe | What It Checks |
|-------|---------------|
| **SQL Connectivity** | SQL Server connection test and basic queries |
| **Windows Services** | Critical services running (SQL Server, IIS, Forge, Sentinel) |
| **IIS Reachability** | Edge site responds to HTTP probe (`http://127.0.0.1/internal/health`) |
| **Self** | Sentinel process health (always 1 if responding) |

## Atlas Technical

### Project Structure

```
SmartPiXL.Sentinel/
├── Program.cs                            # Composition root, endpoint mapping
├── appsettings.json                      # Port 7500, SQL connection
├── SentinelAccessControl.cs              # IP allow-list + auth
├── Endpoints/
│   ├── DashboardEndpoints.cs             # /api/dash/* (Tron)
│   ├── AtlasEndpoints.cs                 # /atlas, /api/atlas/*
│   ├── HealthTreeEndpoints.cs            # /api/health-tree
│   └── TrafficAlertEndpoints.cs          # /api/traffic-alert/*
├── Services/
│   ├── HealthTreeService.cs              # 49-node health tree from Health.Node
│   ├── MarkdownAtlasService.cs           # Markdown → JSON for Atlas
│   ├── InfraHealthService.cs             # Infrastructure health probes
│   └── RemediationService.cs             # Approve/skip remediation
└── wwwroot/
    ├── tron.html                         # Tron operations dashboard
    └── atlas.html                        # Atlas documentation portal
```

### Production Location

| Setting | Value |
|---------|-------|
| Service Name | `SmartPiXL-Sentinel` |
| Path | `C:\Services\SmartPiXL-Sentinel\` |
| Port | 7500 |
| Access | IP allow-list via `SentinelAccessControl` |

### Dashboard Data Sources

All dashboard data comes from pre-built SQL views (35+ views across `dbo.vw_Dash_*` and `dbo.vw_TrafficAlert_*`). Sentinel never runs ad-hoc queries — it reads from views optimized for dashboard performance.

## Atlas Private

### Health Tree Architecture

The Health.Node table (49 rows) defines the monitoring hierarchy:

```
SmartPiXL (platform)
├── Edge (system) → 4 probes
├── Forge (system) → 7 subsystems → 27 probes
└── Sentinel (system) → 4 probes
```

`HealthTreeService.cs` loads the tree from SQL (cached 5 min), fetches live probe data from Edge (`GET http://127.0.0.1/internal/health`), Forge (`GET http://127.0.0.1:7100/health`), and local probes, then rolls up health ratios per node.

### Atlas Content Pipeline

Atlas documentation is served from markdown files in `docs/atlas/`. The `MarkdownAtlasService` reads `.md` files, parses YAML frontmatter, splits content by tier headers (`## Atlas Public/Internal/Technical/Private`), converts to HTML via Markdig, and serves as JSON. A `FileSystemWatcher` invalidates the cache when markdown files change.
