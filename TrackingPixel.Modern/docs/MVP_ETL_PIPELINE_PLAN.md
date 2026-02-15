# SmartPiXL — ETL Pipeline Design & Implementation Plan

**Database:** SmartPiXL on `localhost\SQL2025` (SQL Server 2025 Developer)
**Status:** Core pipeline implemented. SQLCLR and client-param JS phases remain TODO.
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
                    ┌──────────────────┐
                    │  Browser / JS    │
                    │  100+ data pts   │
                    └───────┬──────────┘
                            │  _SMART.GIF?qs=...
                            ▼
                    ┌──────────────────┐
                    │ TrackingEndpoints│  server-side enrichment:
                    │ CaptureAndEnqueue│  IP behavior, DC detection,
                    └───────┬──────────┘  fingerprint stability
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
              │  Id, SourceId, Headers,     │
              │  QueryString, IP, UA, etc.  │
              └─────────────┬───────────────┘
                            │  ETL.usp_ParseNewHits (every 60s)
                            ▼
        ┌───────────────────────────────────────────┐
        │              PiXL.Parsed                  │  ~175 columns
        │  All query-string fields extracted         │  (materialized warehouse)
        └───────┬───────────┬───────────┬───────────┘
                │           │           │
                ▼           ▼           ▼
          PiXL.Device   PiXL.IP    PiXL.Visit
          (dimension)   (dimension) (fact, JSON)
                                       │
                                       │  ETL.usp_MatchVisits (every 60s)
                                       ▼
                               ┌──────────────┐
                               │ PiXL.Match   │  Joined against
                               │ IndividualKey│  AutoConsumer (421M)
                               │ AddressKey   │  via IX_AutoConsumer_EMail
                               └──────────────┘
```

### 2.2 Table Lineage

| Table | Source | Key | Updated By |
|-------|--------|-----|------------|
| `PiXL.Test` | SqlBulkCopy | `Id BIGINT IDENTITY` | `DatabaseWriterService` |
| `PiXL.Parsed` | `PiXL.Test` query-string parsing | `Id` (same as Test) | `ETL.usp_ParseNewHits` |
| `PiXL.Device` | Composite hash of device fields | `DeviceHash` | `ETL.usp_ParseNewHits` (MERGE) |
| `PiXL.IP` | IP address | `IPAddress` | `ETL.usp_ParseNewHits` (MERGE) |
| `PiXL.Visit` | Per-parsed-row fact | `VisitId BIGINT IDENTITY` | `ETL.usp_ParseNewHits` |
| `PiXL.Match` | Visit + AutoConsumer join | `VisitId` FK | `ETL.usp_MatchVisits` (MERGE) |
| `PiXL.Company` | Config | `CompanyId` | Manual |
| `PiXL.Pixel` | Config | `PixelId` | Manual |
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

### TODO

| Phase | Description | Blocked By |
|-------|-------------|------------|
| 2 | SQLCLR assembly (`SmartPixl.Clr`) — `dbo.fn_ExtractParam`, `dbo.fn_ExtractAllParams` | Requires CLR project, `PERMISSION_SET = SAFE` |
| 7 | `PiXLConfigCacheService` — in-memory cache of `PiXL.Config` rows | None |
| 8 | JS script client-param support (`{{CLIENT_PARAMS}}` placeholder in PiXL script) | Phase 7 |
| 10 | Config wiring for match interval/batch size in `TrackingSettings` | None |
| 11 | Seed data for CompanyID 12800 (The Trivia Quest) | Phases 7, 8 |

> **Note:** Phase 9 (separate `MatchBackgroundService`) was **superseded** by the unified `EtlBackgroundService`, which calls both `ETL.usp_ParseNewHits` and `ETL.usp_MatchVisits` on a single 60-second loop.

---

## 4. TODO Phases — Detail

### Phase 2: SQLCLR Assembly

Create a .NET CLR assembly (`SmartPixl.Clr`) with two scalar functions:

- **`dbo.fn_ExtractParam(@qs, @key)`** — Extracts a single query-string parameter. Replaces the T-SQL `dbo.GetQueryParam` scalar UDF that causes per-row function calls.
- **`dbo.fn_ExtractAllParams(@qs, @prefix)`** — Returns all `_cp_*` parameters as a JSON object for `PiXL.Visit.ClientParamsJson`.

Both must use `PERMISSION_SET = SAFE` (no file/network/registry access). Register via:

```sql
CREATE ASSEMBLY SmartPixlClr FROM 'path\SmartPixl.Clr.dll' WITH PERMISSION_SET = SAFE;
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

### Decision 6: Lean Company / Pixel Tables

`PiXL.Company` and `PiXL.Pixel` are intentionally minimal (name + active flag). Additional metadata (billing, contact, etc.) belongs in a future CRM system, not in the tracking database.

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

---

## 6. Future Considerations

### Fingerprint-Based Visitor Identity
Use `DeviceHash` + `CanvasFingerprint` + `WebGLFingerprint` as a composite visitor identifier alongside email matching. Requires a similarity threshold and confidence scoring model.

### IP + Geo Matching
When geo data is available (see [ROADMAP.md](ROADMAP.md)), add IP + city + timezone as a secondary match signal for `PiXL.Match`.

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
