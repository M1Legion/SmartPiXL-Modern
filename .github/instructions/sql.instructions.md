---
description: 'SQL Server conventions for the SmartPiXL database (SQL Server 2025, PiXL/ETL/IPAPI schemas)'
applyTo: '**/*.sql'
---

# SQL Conventions — SmartPiXL

## Database

- **SQL Server 2025 Developer** on `localhost\SQL2025`
- Database: `SmartPiXL` (capital X and L)

## Schema Organization

| Schema | Purpose | Examples |
|--------|---------|---------|
| `PiXL` | Domain tables | `PiXL.Test`, `PiXL.Parsed`, `PiXL.Device`, `PiXL.IP`, `PiXL.Visit`, `PiXL.Match`, `PiXL.Config` |
| `ETL` | Pipeline infrastructure | `ETL.Watermark`, `ETL.MatchWatermark` |
| `IPAPI` | IP geolocation (synced from Xavier) | `IPAPI.IP`, `IPAPI.SyncLog` |
| `dbo` | Dashboard views, scalar functions | `dbo.vw_Dash_*`, `dbo.GetQueryParam()` |

## Naming Conventions

| Object | Pattern | Example |
|--------|---------|---------|
| Domain tables | `{Schema}.{Entity}` | `PiXL.Visit` |
| Dashboard views | `dbo.vw_Dash_{Name}` | `dbo.vw_Dash_SystemHealth` |
| ETL stored procs | `ETL.usp_{Name}` | `ETL.usp_ParseNewHits` |
| Scalar functions | `dbo.{Name}` | `dbo.GetQueryParam` |
| Migration scripts | `{NN}_{Description}.sql` | `27_MatchTypeConfig.sql` |

## ETL Patterns

- **Watermark-based incremental processing** — never reprocess old rows
- Watermark tables: `ETL.Watermark` (ParseNewHits, IpApiSync), `ETL.MatchWatermark` (MatchVisits)
- Always `SET NOCOUNT ON` in stored procedures
- Use `MERGE` for dimension table upserts (`PiXL.Device`, `PiXL.IP`)
- Use `INSERT ... SELECT` for fact tables (`PiXL.Visit`)

## SQL Server 2025 Features (use when appropriate)

- Native `json` data type for structured data (e.g., `PiXL.Visit.ClientParams`)
- `JSON_OBJECTAGG` for building JSON from relational data
- `CREATE JSON INDEX` for JSON property queries
- Enhanced `MERGE` performance

## Key Tables

| Table | Insert Rate | Key Columns |
|-------|------------|-------------|
| `PiXL.Test` | Every request (SqlBulkCopy) | 9 cols: CompanyID, PiXLID, IPAddress, RequestPath, QueryString, HeadersJson, UserAgent, Referer, ReceivedAt |
| `PiXL.Parsed` | Every 60s (ETL batch) | ~175 cols parsed from QueryString |
| `PiXL.Device` | MERGE on DeviceHash | DeviceHash = hash of 5 fingerprint fields |
| `PiXL.IP` | MERGE on IPAddress | Geo-enriched from IPAPI.IP |
| `PiXL.Visit` | 1:1 with Parsed | Links Device + IP + ClientParams JSON |
| `PiXL.Match` | MERGE on device+email | Identity resolution output (AutoConsumer lookup) |

## Dashboard View Rules

- Views power the Tron dashboard via `/api/dash/*` endpoints
- Read from `PiXL.Parsed` (materialized, indexed) — never from `PiXL.Test` (raw, unindexed QueryString)
- Keep views simple: single SELECT, no CTEs unless necessary
- Prefix: `vw_Dash_`
