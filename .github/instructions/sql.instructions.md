---
description: 'SQL Server conventions for the SmartPiXL database (SQL Server 2025, PiXL/ETL/IPAPI/TrafficAlert/Graph/Geo schemas)'
applyTo: '**/*.sql'
---

# SQL Conventions — SmartPiXL

## Database

- **SQL Server 2025 Developer** on `localhost\SQL2025`
- Database: `SmartPiXL` (capital X and L)
- CLR database: `SmartPiXL_CLR` (Phase 7 — separate database for CLR assemblies)

## Schema Organization

| Schema | Purpose | Examples | Status |
|--------|---------|---------|--------|
| `PiXL` | Domain tables | `PiXL.Raw`, `PiXL.Parsed`, `PiXL.Device`, `PiXL.IP`, `PiXL.Visit`, `PiXL.Match`, `PiXL.Config`, `PiXL.Company`, `PiXL.Pixel`, `PiXL.SubnetReputation` | Live |
| `ETL` | Pipeline infrastructure | `ETL.Watermark`, `ETL.MatchWatermark`, `ETL.usp_ParseNewHits`, `ETL.usp_MatchVisits`, `ETL.usp_EnrichParsedGeo` | Live (paused) |
| `IPAPI` | IP geolocation (synced from Xavier) | `IPAPI.IP`, `IPAPI.SyncLog` | Live |
| `TrafficAlert` | Visitor scoring + customer summaries | `TrafficAlert.VisitorScore`, `TrafficAlert.CustomerSummary` | Phase 9 |
| `Graph` | Node/edge tables for identity resolution | `Graph.Device AS NODE`, `Graph.Person AS NODE`, edges | Phase 7 |
| `Geo` | Zipcode polygons from Census ZCTA | `Geo.Zipcode` | Phase 8 |
| `dbo` | Dashboard views, scalar functions | `dbo.vw_Dash_*`, `dbo.GetQueryParam()` | Live |

## Naming Conventions

| Object | Pattern | Example |
|--------|---------|---------|
| Domain tables | `{Schema}.{Entity}` | `PiXL.Visit` |
| Dashboard views | `dbo.vw_Dash_{Name}` | `dbo.vw_Dash_SystemHealth` |
| ETL stored procs | `ETL.usp_{Name}` | `ETL.usp_ParseNewHits` |
| TrafficAlert procs | `ETL.usp_{Name}` | `ETL.usp_MaterializeVisitorScores` |
| Scalar functions | `dbo.{Name}` | `dbo.GetQueryParam` |
| CLR functions | `dbo.{Name}` (synonym in SmartPiXL → SmartPiXL_CLR) | `dbo.GetSubnet24` |
| Migration scripts | `{NN}_{Description}.sql` | `41_ScreenExtendedMousePath.sql` |

## Migration Script Numbering

Existing scripts go up to `40`. New migrations start at `41` and follow the workplan:

| Range | Phase | Purpose |
|-------|-------|---------|
| 41 | Phase 1 | PiXL Script final fields |
| 42-44 | Phases 4-6 | Forge enrichment columns (Tier 1-3) |
| 45-47 | Phase 7 | CLR database, vectors, graph tables |
| 48-57 | Phase 8 | Analysis features, session views, geo |
| 58-59 | Phase 9 | TrafficAlert schema + materialization |

## ETL Patterns

- **Watermark-based incremental processing** — never reprocess old rows
- Watermark tables: `ETL.Watermark` (ParseNewHits, IpApiSync), `ETL.MatchWatermark` (MatchVisits)
- Always `SET NOCOUNT ON` in stored procedures
- Use `MERGE` for dimension table upserts (`PiXL.Device`, `PiXL.IP`)
- Use `INSERT ... SELECT` for fact tables (`PiXL.Visit`)
- ETL is owned by the **Forge** process (not the Worker — Worker is deprecated)

## SQL Server 2025 Features (use when appropriate)

- Native `json` data type for structured data (e.g., `PiXL.Visit.ClientParams`)
- `JSON_OBJECTAGG` for building JSON from relational data
- `CREATE JSON INDEX` for JSON property queries
- `VECTOR(n)` type for fingerprint similarity (Phase 7)
- `VECTOR_DISTANCE()` for nearest-neighbor queries
- Graph tables (`AS NODE`, `AS EDGE`, `MATCH` traversal) for identity resolution (Phase 7)
- Enhanced `MERGE` performance

## Key Tables

| Table | Insert Rate | Key Columns |
|-------|------------|-------------|
| `PiXL.Raw` | Every request (SqlBulkCopy) | 9 cols: CompanyID, PiXLID, IPAddress, RequestPath, QueryString, HeadersJson, UserAgent, Referer, ReceivedAt |
| `PiXL.Parsed` | Every 60s (ETL batch) | 300+ cols parsed from QueryString (expanded by Forge enrichments) |
| `PiXL.Device` | MERGE on DeviceHash | DeviceHash = hash of 5 fingerprint fields |
| `PiXL.IP` | MERGE on IPAddress | Geo-enriched from IPAPI.IP |
| `PiXL.Visit` | 1:1 with Parsed | Links Device + IP + ClientParams JSON |
| `PiXL.Match` | MERGE on device+email | Identity resolution output (AutoConsumer lookup) |

## Dashboard View Rules

- Views power the Tron dashboard via `/api/dash/*` endpoints
- Read from `PiXL.Parsed` (materialized, indexed) — never from `PiXL.Raw` (raw, unindexed QueryString)
- Keep views simple: single SELECT, no CTEs unless necessary
- Prefix: `vw_Dash_`
- Dashboard views are currently dormant (Tron is offline during rebuild) — they'll be rebuilt in Phase 10
