# SmartPiXL — ETL Pipeline Design & Implementation Plan

**Database:** SmartPiXL on `localhost\SQL2025` (SQL Server 2025 Developer)
**Status:** Core pipeline + geo enrichment implemented. Legacy PiXL support and Company/Pixel sync in progress.
**Last updated:** February 15, 2026

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Architecture](#2-architecture)
3. [Implementation Status](#3-implementation-status)
4. [TODO Phases](#4-todo-phases)
5. [Design Decisions](#5-design-decisions)
6. [Future Considerations](#6-future-considerations)
7. [Risk Register](#7-risk-register)
8. [File Reference](#8-file-reference)

---

## 1. Executive Summary

End-to-end ETL pipeline for the SmartPiXL tracking system (initial target: CompanyID `12800` — The Trivia Quest). The pipeline normalizes raw tracking hits into a star schema with device, IP, visit, and match dimensions, then resolves email identity by matching against the `AutoConsumer` table (421M rows, 67M with email).

### Core Pipeline (every 60 seconds via `EtlBackgroundService`)

```
PiXL.Test (raw, 9 columns)
  → ETL.usp_ParseNewHits → PiXL.Parsed (~175 columns, materialized warehouse)
                          → PiXL.Device (dimension, keyed on DeviceHash)
                          → PiXL.IP     (dimension, keyed on IPAddress)
                          → PiXL.Visit  (fact, 1:1 with Parsed, has ClientParamsJson)
  → ETL.usp_MatchVisits  → PiXL.Match  (identity resolution against AutoConsumer)
```

---

## 2. Architecture

### 2.1 Data Flow

```
  MODERN PATH                              LEGACY PATH
  ───────────                              ───────────
  <script src="_SMART.js">                 <img src="_SMART.GIF">
        │                                        │
        ▼                                        │
  ┌──────────────────┐                           │
  │  PiXLScript.js   │  100+ data pts            │  Server-side data only:
  │  (served by app) │  collected in browser      │  IP, UA, Referer, Headers
  └───────┬──────────┘                           │
          │  new Image().src =                   │
          │  _SMART.GIF?sw=...&sh=...            │
          ▼                                      ▼
  ┌─────────────────────────────────────────────────┐
  │  TrackingEndpoints — /{**path} _SMART.GIF       │  Both paths land here.
  │  CaptureAndEnqueue:                             │  Hit-type detected:
  │    IP behavior, DC detection, geo lookup,       │  modern (has JS params)
  │    fingerprint stability, tz mismatch           │  legacy (no JS params)
  │    → appends _srv_hitType=modern|legacy         │
  └───────────────────┬─────────────────────────────┘
                      │
                      ▼
              ┌──────────────────┐
              │ DatabaseWriter   │  Channel<T> → SqlBulkCopy
              │ Service          │  bounded, single-reader
              └───────┬──────────┘
                      │
                      ▼
        ┌─────────────────────────────┐
        │        PiXL.Test            │  9 columns (raw ingest)
        │  Id, CompanyID, PiXLID,     │  Legacy rows: QueryString is
        │  QueryString, IP, UA, etc.  │  empty or has only ?ref=...
        └─────────────┬───────────────┘
                      │  ETL.usp_ParseNewHits (every 60s)
                      ▼
  ┌───────────────────────────────────────────┐
  │              PiXL.Parsed                  │  ~175 columns + HitType
  │  Modern: all fields populated             │  Legacy: mostly NULL,
  │  Legacy: IP, UA, Referer, HitType only    │  HitType = 'legacy'
  └───────┬───────────┬───────────┬───────────┘
          │           │           │
          ▼           ▼           ▼
    PiXL.Device   PiXL.IP    PiXL.Visit (+ HitType)
    (dimension)   (dimension) (fact, JSON)
                                 │
                                 │  ETL.usp_MatchVisits (every 60s)
                                 ▼
                         ┌──────────────┐
                         │ PiXL.Match   │  Modern: email-based match
                         │ IndividualKey│  Legacy: deferred (IP+UA+TS)
                         │ AddressKey   │  via IX_AutoConsumer_EMail
                         └──────────────┘
```

### 2.1.1 URL Patterns

| URL Pattern | Purpose | Response |
|-------------|---------|----------|
| `/{companyId}/{pixlId}_SMART.js` | Modern pixel — serves PiXLScript JS | `Content-Type: text/javascript` |
| `/{companyId}/{pixlId}_SMART.GIF` | Pixel hit (modern with JS params, or legacy bare) | `Content-Type: image/gif` (1×1 transparent) |
| `/js/{companyId}/{pixlId}.js` | Legacy JS endpoint (backward compat) | `Content-Type: text/javascript` |

### 2.2 Table Lineage

| Table | Source | Key | Updated By |
|-------|--------|-----|------------|
| `PiXL.Test` | SqlBulkCopy | `Id BIGINT IDENTITY` | `DatabaseWriterService` |
| `PiXL.Parsed` | `PiXL.Test` query-string parsing | `Id` (same as Test) | `ETL.usp_ParseNewHits` |
| `PiXL.Device` | Composite hash of device fields | `DeviceHash` | `ETL.usp_ParseNewHits` (MERGE) |
| `PiXL.IP` | IP address | `IPAddress` | `ETL.usp_ParseNewHits` (MERGE) |
| `PiXL.Visit` | Per-parsed-row fact | `VisitId BIGINT IDENTITY` | `ETL.usp_ParseNewHits` |
| `PiXL.Match` | Visit + AutoConsumer join | `VisitId` FK | `ETL.usp_MatchVisits` (MERGE) |
| `PiXL.Company` | Xavier sync | `CompanyId` | `CompanyPixelSyncService` (every 15 min from `Xavier.SmartPixl.dbo.Company`) |
| `PiXL.Pixel` | Xavier sync | `(CompanyId, PiXLId)` | `CompanyPixelSyncService` (every 15 min from `Xavier.SmartPixl.dbo.PiXL`) |
| `PiXL.Config` | Config | `CompanyId + PixelId` | Manual |
| `ETL.Watermark` | Bookkeeping | `ProcessName` | `ETL.usp_ParseNewHits` |
| `ETL.MatchWatermark` | Bookkeeping | `ProcessName` | `ETL.usp_MatchVisits` |

### 2.3 Service Architecture

| Service | Lifecycle | Role |
|---------|-----------|------|
| `DatabaseWriterService` | Hosted (singleton) | Channel → SqlBulkCopy into `PiXL.Test` |
| `EtlBackgroundService` | Hosted | Every 60s: calls `ETL.usp_ParseNewHits`, then `ETL.usp_MatchVisits` |
| `TrackingCaptureService` | Singleton | HTTP request → `TrackingData` |
| `FingerprintStabilityService` | Singleton | Per-IP fingerprint variation scoring |
| `IpBehaviorService` | Singleton | Subnet /24 velocity detection |
| `DatacenterIpService` | Hosted (singleton) | AWS/GCP IP range downloader |
| `IpClassificationService` | Singleton | DC / residential / reserved classification |
| `GeoCacheService` | Singleton | Two-tier non-blocking IP geo lookup (ConcurrentDict + MemoryCache → IPAPI.IP) |
| `IpApiSyncService` | Hosted | Daily watermark sync from Xavier IPGEO → IPAPI.IP |
| `CompanyPixelSyncService` | Hosted | Every 15 min: MERGE sync from Xavier.SmartPixl.dbo.Company/PiXL |

---

## 3. Implementation Status

### Done

| Phase | Description | SQL Script | Status |
|-------|-------------|------------|--------|
| 0 | Schema creation (`PiXL`, `ETL`) | `SQL/17B_CreateSchemas.sql` | Done |
| 1 | Company, Pixel, MatchWatermark tables | `SQL/19_DeviceIpVisitMatchTables.sql` | Done |
| 1B | Device, IP, Visit, Match tables + indexes | `SQL/19_DeviceIpVisitMatchTables.sql` | Done |
| 3 | Client params → `PiXL.Visit.ClientParamsJson` (JSON column, no separate cols) | `SQL/19_DeviceIpVisitMatchTables.sql` | Done (no-op: JSON replaces columns) |
| 4 | ETL phases 9–13 (Device, IP, Visit population in `usp_ParseNewHits`) | `SQL/20_ETLPhases9to13.sql` | Done |
| 5 | AutoConsumer email index (`IX_AutoConsumer_EMail`) | `SQL/22_AutoConsumerEmailIndex.sql` | Done |
| 6 | Match stored procedure (`ETL.usp_MatchVisits`) | `SQL/23_MatchVisits.sql` | Done |
| — | Backfill Visit/Device/IP from existing Parsed rows | `SQL/21_BackfillVisitDeviceIP.sql` | Done |
| — | Pipeline health view (`vw_Dash_PipelineHealth`) | `SQL/24_PipelineHealthView.sql` | Done |
| — | C# `EtlBackgroundService` (unified loop calling both procs) | `Services/EtlBackgroundService.cs` | Done |
| — | Geo enrichment proc (`ETL.usp_EnrichParsedGeo`) | `SQL/26_ETL_GeoEnrichment.sql` | Done |
| — | `GeoCacheService` — two-tier non-blocking geo lookup | `Services/GeoCacheService.cs` | Done |
| — | `IpApiSyncService` — daily incremental sync from Xavier IPGEO | `Services/IpApiSyncService.cs` | Done |
| — | IPAPI schema + IP table + SyncLog | `SQL/25_GeoIntegration.sql` | Done |

### In Progress — P0: Legacy PiXL Support

| Phase | Description | Status |
|-------|-------------|--------|
| L1 | Remove `queryString.Length > 10` gate — accept all `_SMART.GIF` hits | Not started |
| L2 | Add `_SMART.js` route — serves PiXLScript from `/{companyId}/{pixlId}_SMART.js` | Not started |
| L3 | Hit-type detection (`_srv_hitType=legacy\|modern`) in `CaptureAndEnqueue` | Not started |
| L4 | Populate `Referer` from `?ref=` param for legacy `<script>` style hits | Not started |
| L5 | SQL: Add `HitType VARCHAR(10)` column to `PiXL.Parsed` and `PiXL.Visit` | Not started |
| L6 | Update `ETL.usp_ParseNewHits` to populate `HitType` from `_srv_hitType` | Not started |
| L7 | Update `vw_Dash_*` views for legacy vs modern hit reporting | Not started |

### In Progress — P0: Company/Pixel Sync from Xavier

| Phase | Description | Status |
|-------|-------------|--------|
| S1 | `CompanyPixelSyncService` — `BackgroundService` syncing Company + PiXL from Xavier | Not started |
| S2 | `ETL.CompanyPixelSyncLog` table for audit | Not started |
| S3 | Config: `CompanyPixelSyncIntervalMinutes` in `TrackingSettings` | Not started |
| S4 | Register in `Program.cs` | Not started |

### TODO (Lower Priority)

| Phase | Description | Blocked By |
|-------|-------------|------------|
| 2 | SQLCLR assembly (`SmartPixl.Clr`) — `dbo.fn_ExtractParam`, `dbo.fn_ExtractAllParams` | Requires CLR project, `PERMISSION_SET = SAFE` |
| 7 | `PiXLConfigCacheService` — in-memory cache of `PiXL.Config` rows | None |
| 8 | JS script client-param support (`{{CLIENT_PARAMS}}` placeholder in PiXL script) | Phase 7 |
| 10 | Config wiring for match interval/batch size in `TrackingSettings` | None |
| 11 | Seed data for CompanyID 12800 (The Trivia Quest) | Phases 7, 8 |
| LM1 | Design legacy match process (IP + UA + timestamp window) | Legacy ingest live |
| LM2 | `ETL.usp_MatchLegacyVisits` stored procedure | LM1 |

> **Note:** Phase 9 (separate `MatchBackgroundService`) was **superseded** by the unified `EtlBackgroundService`, which calls both `ETL.usp_ParseNewHits` and `ETL.usp_MatchVisits` on a single 60-second loop.

---

## 4. TODO Phases — Detail

### Phase 2: SQLCLR Assembly

Create a .NET CLR assembly (`SmartPiXL.Clr`) with two scalar functions:

- **`dbo.fn_ExtractParam(@qs, @key)`** — Extracts a single query-string parameter. Replaces the T-SQL `dbo.GetQueryParam` scalar UDF that causes per-row function calls.
- **`dbo.fn_ExtractAllParams(@qs, @prefix)`** — Returns all `_cp_*` parameters as a JSON object for `PiXL.Visit.ClientParamsJson`.

Both must use `PERMISSION_SET = SAFE` (no file/network/registry access). Register via:

```sql
CREATE ASSEMBLY SmartPiXLClr FROM 'path\SmartPiXL.Clr.dll' WITH PERMISSION_SET = SAFE;
```

Then swap `ETL.usp_ParseNewHits` to use CLR functions instead of T-SQL scalar UDFs.

### Phase 7: PiXL Config Cache Service

Create `Services/PiXLConfigCacheService.cs`:

- Load `PiXL.Config` rows into an in-memory dictionary on startup
- Expose `GetClientParams(companyId, pixelId)` returning the list of custom `_cp_*` parameter names
- Refresh on a timer (e.g., every 5 minutes) or via manual invalidation

### Phase 8: JS Script Client Param Support

Modify the pixel JS template in `TrackingEndpoints.cs` to inject `{{CLIENT_PARAMS}}` — the list of custom DOM selectors / form fields to capture, driven by `PiXL.Config`.

### Phase 11: Seed Data

Create `SQL/25_SeedClient12800.sql`:

```sql
INSERT INTO PiXL.Company (CompanyId, CompanyName) VALUES ('12800', 'The Trivia Quest');
INSERT INTO PiXL.Pixel   (PixelId, CompanyId, PixelName) VALUES ('1', '12800', 'Main');
INSERT INTO PiXL.Config  (CompanyId, PixelId, ParamName, DomSelector)
VALUES ('12800', '1', '_cp_email', '#email-input'),
       ('12800', '1', '_cp_hid',   '#hashed-id');
```

---

## 5. Design Decisions

### Decision 1: `_cp_` Prefix Convention

Client-configurable parameters use a `_cp_` prefix in the query string. The ETL extracts all `_cp_*` params into `PiXL.Visit.ClientParamsJson` as a JSON object. This avoids schema changes when new client params are added.

### Decision 2: JSON for Client Params Storage

`PiXL.Visit.ClientParamsJson` uses SQL Server 2025's native `json` data type:
- Pre-parsed binary storage with built-in validation
- `CREATE JSON INDEX` for indexed seeks on `$.email`, `$.hid` without computed columns
- `JSON_OBJECTAGG` for clean aggregation in ETL (no string concatenation)
- ~30% smaller than `NVARCHAR(MAX)`

### Decision 3: Regular Column for `MatchEmail`

`PiXL.Visit.MatchEmail` is a regular `VARCHAR(200)` column, not a computed column. SQL Server cannot create filtered indexes on computed columns referencing JSON (`Msg 10609`), so the ETL materializes the email value during insert.

### Decision 4: Separate Match Watermark Table

`ETL.MatchWatermark` is separate from `ETL.Watermark` because it tracks different metrics (`RowsMatched` in addition to `RowsProcessed`) and the two ETL procs advance independently.

### Decision 5: MERGE for PiXL.Match Upserts

`ETL.usp_MatchVisits` uses `MERGE` to upsert into `PiXL.Match`. On conflict (`VisitId` already matched), it updates `IndividualKey` and `AddressKey` if a better match is found. This handles re-processing gracefully.

### Decision 6: Company / Pixel Tables — Xavier System of Record

`PiXL.Company` (41 columns, ~467 rows) and `PiXL.Pixel` (52 columns, ~5612 rows) are synced from Xavier (`192.168.88.35`, database `SmartPixl`, schema `dbo`). Xavier is the system of record — changes are made there and mirrored to SmartPiXL every 15 minutes via `CompanyPixelSyncService`. SmartPiXL adds local-only columns (`ClientParams`, `Notes`, `SysStartTime`, `SysEndTime`, `IsActive` on Pixel) that are **not overwritten** by the MERGE sync.

### Decision 7: No Staging Tables

The ETL reads directly from `PiXL.Test` using the watermark. No staging tables are needed because `PiXL.Test` is append-only and the watermark provides exactly-once processing.

### Decision 8: SQLCLR Permission Level

When implemented, the CLR assembly must use `PERMISSION_SET = SAFE`. No file, network, or registry access. The functions are pure string manipulation (query-string parsing).

### Decision 9: Normalized Star Schema

`PiXL.Device` and `PiXL.IP` are shared dimensions across all companies. `PiXL.Visit` is the fact table (one row per tracking hit). This allows cross-company analytics on device and IP patterns without denormalization.

### Decision 10: IndividualKey / AddressKey from AutoConsumer

`PiXL.Match` stores `IndividualKey` and `AddressKey` from the `AutoConsumer` table. These are the canonical identity keys used by downstream systems (mail merge, suppression, etc.).

### Decision 11: Lean PiXL.Device

`PiXL.Device` only stores the composite `DeviceHash` and its component fields (CPU cores, RAM, GPU, screen, etc.). It does **not** store behavioral data (fingerprint hashes, evasion signals) — those live in `PiXL.Parsed`.

### Decision 12: Device Hash Computed in SQL

The composite `DeviceHash` is computed by `ETL.usp_ParseNewHits` using `HASHBYTES('SHA2_256', CONCAT(...))`. This keeps the hash definition in one place (SQL) rather than split between C# and SQL.

### Decision 13: Global Device and IP Tables

`PiXL.Device` and `PiXL.IP` are global (not per-company). A device visiting Company A and Company B produces one `PiXL.Device` row. The `PiXL.Visit` fact table links device → company via `CompanyId`.

### Decision 14: PiXL.Visit PK = VisitId

`PiXL.Visit` uses its own `VisitId BIGINT IDENTITY` as the primary key, not `ParsedId`. This decouples the visit dimension from the parsed warehouse and allows future visit sources beyond `PiXL.Parsed`.

### Decision 15: Tag, Don't Fork — Legacy vs Modern Hits

Legacy pixel hits (`<img src="_SMART.GIF">`) and modern hits (`<script src="_SMART.js">` → JS fires `_SMART.GIF?params...`) flow through the **same pipeline** (PiXL.Test → Parsed → Visit → Match). They are differentiated by a `HitType` column (`'legacy'` or `'modern'`), not by separate tables or code paths. Legacy rows will have NULL values for all JS-only fields (~170 of ~175 columns in PiXL.Parsed). The ETL proc is already NULL-tolerant. This enables side-by-side comparison of legacy vs modern match yield per company.

### Decision 16: Modern Pixel URL Mirrors Legacy Format

Modern pixel embed code uses `<script src=".../{companyId}/{pixlId}_SMART.js">` — the same URL structure as legacy `<img src=".../{companyId}/{pixlId}_SMART.GIF">`, just changing the extension. This is intentional: clients upgrade by changing one character in one tag. The old `/js/{companyId}/{pixlId}.js` endpoint is retained for backward compatibility.

### Decision 17: Accept All _SMART.GIF Hits

The `queryString.Length > 10` gate is removed. The `_SMART.GIF` path suffix is sufficient to identify tracking pixel requests vs noise (favicon, robots, etc.). Legacy bare `<img>` requests with no query string are now recorded with server-side data only (IP, UA, Referer, all HTTP headers). Server-side enrichment (geo, IP classification, datacenter detection, IP behavior) still runs on legacy hits.

### Decision 18: Xavier Traffic Forwarding

Xavier's `Default.aspx.cs` can forward live production hits to SmartPiXL via a fire-and-forget HTTP request (`http://192.168.88.176/{path}`) with the original client IP in `X-Forwarded-For`. SmartPiXL already reads `X-Forwarded-For` in its IP extraction chain. This provides real production traffic for parallel validation without disrupting Xavier's operation.

---

## 6. Future Considerations

### Fingerprint-Based Visitor Identity
Use `DeviceHash` + `CanvasFingerprint` + `WebGLFingerprint` as a composite visitor identifier alongside email matching. Requires a similarity threshold and confidence scoring model.

### IP + Geo Matching
Geo data is now available via `IPAPI.IP` (342M rows). Add IP + city + timezone as a secondary match signal for `PiXL.Match`.

### Cross-Device Linking
Link the same visitor across devices using email as the anchor: if Device A and Device B both resolve to the same email, create a `PiXL.Visitor` entity spanning both.

### Date Partitioning
When `PiXL.Visit` exceeds 100M rows, partition by `CreatedAt` (monthly). SQL Server 2025 supports online partition switching for zero-downtime maintenance.

### Client Delivery
Export matched visitors as CSV/API for client consumption. Requires a delivery service and access control layer.

### Real-Time Matching
Move from batch matching (every 60s) to event-driven matching using SQL Server Service Broker or a Change Data Capture (CDC) feed from `PiXL.Visit`.

---

## 7. Risk Register

| Risk | Likelihood | Impact | Mitigation | Status |
|------|-----------|--------|------------|--------|
| AutoConsumer email lookup slow (421M rows) | Medium | High | `IX_AutoConsumer_EMail` index built (SQL/22) | **Mitigated** |
| ETL watermark corruption | Low | High | Watermark uses atomic UPDATE with row lock | Mitigated |
| `PiXL.Test` Id overflow (INT) | Low | Critical | Changed to `BIGINT` | Mitigated |
| SQLCLR deployment complexity | Medium | Medium | Deferred; T-SQL UDFs work for current volume | Accepted |
| `dotnet publish` overwrites `web.config` | High | High | Post-publish verification step in deploy script | Mitigated |
| Legacy hits flood PiXL.Test with sparse rows | Medium | Low | `HitType` column enables filtering; ETL is NULL-tolerant; storage cost minimal | Accepted |
| Xavier Company/Pixel sync overwrites local columns | Medium | High | MERGE excludes local-only columns (`ClientParams`, `Notes`, temporal cols) | Mitigated |
| Xavier connectivity loss stops sync | Low | Medium | Service retries every 15 min; data is stale but not lost; SyncLog tracks failures | Accepted |

---

## 8. File Reference

### SQL Migration Scripts

| Script | Purpose |
|--------|---------|
| `SQL/05_MaintenanceProcedures.sql` | Index maintenance procs |
| `SQL/10_PiXLConfiguration.sql` | PiXL.Config table |
| `SQL/11_EvasionCountermeasures.sql` | Evasion signal columns on PiXL.Parsed |
| `SQL/16_MaterializedParsedTable.sql` | PiXL.Parsed materialized table + ETL.usp_ParseNewHits |
| `SQL/17_IpBehaviorSignals.sql` | IP behavior signal columns |
| `SQL/17B_CreateSchemas.sql` | CREATE SCHEMA PiXL / ETL |
| `SQL/18_NvarcharToVarchar.sql` | NVARCHAR → VARCHAR migration |
| `SQL/19_DeviceIpVisitMatchTables.sql` | PiXL.Device, IP, Visit, Match, Company, Pixel + ETL.MatchWatermark |
| `SQL/20_ETLPhases9to13.sql` | ETL phases 9–13 (Device, IP, Visit population in usp_ParseNewHits) |
| `SQL/21_BackfillVisitDeviceIP.sql` | One-time backfill of dimension tables from existing Parsed rows |
| `SQL/22_AutoConsumerEmailIndex.sql` | IX_AutoConsumer_EMail on AutoConsumer(EMail) |
| `SQL/23_MatchVisits.sql` | ETL.usp_MatchVisits stored procedure |
| `SQL/24_PipelineHealthView.sql` | vw_Dash_PipelineHealth view |
| `SQL/25_GeoIntegration.sql` | IPAPI schema, IPAPI.IP table, IPAPI.SyncLog |
| `SQL/26_ETL_GeoEnrichment.sql` | ETL.usp_EnrichParsedGeo stored procedure |
| `SQL/27_MatchTypeConfig.sql` | Match type configuration |
| `SQL/28_LegacySupport.sql` | HitType columns on PiXL.Parsed and PiXL.Visit |
| `SQL/29_CompanyPixelSyncLog.sql` | ETL.CompanyPixelSyncLog audit table |

### C# Services

| Service | File |
|---------|------|
| `DatabaseWriterService` | `Services/DatabaseWriterService.cs` |
| `EtlBackgroundService` | `Services/EtlBackgroundService.cs` |
| `TrackingCaptureService` | `Services/TrackingCaptureService.cs` |
| `FingerprintStabilityService` | `Services/FingerprintStabilityService.cs` |
| `IpBehaviorService` | `Services/IpBehaviorService.cs` |
| `DatacenterIpService` | `Services/DatacenterIpService.cs` |
| `IpClassificationService` | `Services/IpClassificationService.cs` |
| `InfraHealthService` | `Services/InfraHealthService.cs` |
| `GeoCacheService` | `Services/GeoCacheService.cs` |
| `IpApiSyncService` | `Services/IpApiSyncService.cs` |
| `CompanyPixelSyncService` | `Services/CompanyPixelSyncService.cs` |
