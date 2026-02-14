# SmartPiXL MVP ETL Pipeline Plan

> **Client:** CompanyID 12800 / PiXLID 1 (The Trivia Quest)  
> **Platform:** G2_Local (`WIN-NHSTA94G6CJ`) — SmartPixl database  
> **Date:** February 13, 2026  
> **Status:** DRAFT — Awaiting approval before implementation  

---

## Table of Contents

- [1. Executive Summary](#1-executive-summary)
- [2. Current State Assessment](#2-current-state-assessment)
  - [2.1 What Exists Today](#21-what-exists-today)
  - [2.2 What's Missing](#22-whats-missing)
  - [2.3 Key Constraints & Decisions Already Made](#23-key-constraints--decisions-already-made)
- [3. Target Architecture](#3-target-architecture)
  - [3.1 Data Flow Diagram](#31-data-flow-diagram)
  - [3.2 Table Lineage](#32-table-lineage)
  - [3.3 Service Architecture](#33-service-architecture)
- [4. Implementation Plan — Ordered Steps](#4-implementation-plan--ordered-steps)
  - [Phase 0: Schema Creation & Table Migration](#phase-0-schema-creation--table-migration)
  - [Phase 1: Foundation — Configuration Tables](#phase-1-foundation--configuration-tables)
  - [Phase 1B: Normalized Dimension & Fact Tables](#phase-1b-normalized-dimension--fact-tables)
  - [Phase 2: SQLCLR Assembly — High-Performance Functions](#phase-2-sqlclr-assembly--high-performance-functions)
  - [Phase 3: Schema Extensions — Client Params in PiXL.Parsed](#phase-3-schema-extensions--client-params-in-pixlparsed)
  - [Phase 4: ETL Extension — Device, IP, Visit & Client Params](#phase-4-etl-extension--device-ip-visit--client-params)
  - [Phase 5: AutoConsumer Index — Enable Email Matching](#phase-5-autoconsumer-index--enable-email-matching)
  - [Phase 6: Match Stored Procedure — Identity Resolution](#phase-6-match-stored-procedure--identity-resolution)
  - [Phase 7: C# — PiXL Config Cache Service](#phase-7-c--pixl-config-cache-service)
  - [Phase 8: C# — JS Script Client Param Support](#phase-8-c--js-script-client-param-support)
  - [Phase 9: C# — Match Background Service](#phase-9-c--match-background-service)
  - [Phase 10: Configuration & Wiring](#phase-10-configuration--wiring)
  - [Phase 11: Data Seeding & Deployment](#phase-11-data-seeding--deployment)
- [5. Verification & Testing](#5-verification--testing)
- [6. Design Decisions & Rationale](#6-design-decisions--rationale)
- [7. Future Considerations](#7-future-considerations)
- [8. Risk Register](#8-risk-register)

---

## 1. Executive Summary

The boss sold a live ETL pipeline before we spec'd it out. A client (CompanyID 12800 — The Trivia Quest) needs their web traffic captured, parsed, and matched against our consumer reference data (`AutoConsumer`) using **email addresses** embedded in their page URLs.

**What we're building:** An end-to-end pipeline with a **normalized relational schema** that:
1. Extracts client-specific URL parameters (email, hid, etc.) from the host page via our JavaScript pixel
2. Ingests them alongside our full fingerprint data into the existing raw table (`PiXL.Test`)
3. Parses and materializes them into `PiXL.Parsed` (extending the existing ETL)
4. Computes a **composite device fingerprint hash** (SHA-256 of the top 5 entropy signals) and upserts into `PiXL.Device` — a **platform-wide** device dimension table
5. Upserts unique IP addresses into `PiXL.IP` — a **platform-wide** IP dimension table with classification metadata
6. Creates a `PiXL.Visit` fact row (1:1 with `PiXL.Parsed`) that bridges devices, IPs, and client params to each hit
7. Matches emails (and eventually IPs, geo) against `AutoUpdate.dbo.AutoConsumer` using **IndividualKey** (for email/IP matches) and **AddressKey** (for geo matches) — NOT RecordID
8. Stores identity resolution results in `PiXL.Match` with FKs to `PiXL.Device`, `PiXL.IP`, and `PiXL.Visit`

**What we're NOT building (yet):** Client-facing dashboard, billing integration, file delivery mechanism, geo matching (needs geo data not yet in pipeline), CRM push/webhook. Output format is TBD — the boss hasn't specified what the client receives.

**Philosophy:** Build it right the first time so we don't create a minefield. The design is **maximally normalized** — separate dimension tables for devices and IPs (platform-wide), a visit fact table, and an identity resolution table with proper FKs. Every decision accommodates future match types (fingerprint, IP, behavioral, geo), additional clients, and frontend integration without requiring table redesigns or breaking changes.

---

## 2. Current State Assessment

### 2.1 What Exists Today

**Live and working on G2_Local:**

| Component | Location | Status |
|-----------|----------|--------|
| Pixel JS Script | `Scripts/PiXLScript.cs` (1430 lines) | Collecting 158+ fingerprint signals |
| Pixel endpoint | `Endpoints/TrackingEndpoints.cs` — `GET /{**path}` | Capturing _SMART.GIF requests |
| JS script endpoint | `Endpoints/TrackingEndpoints.cs` — `GET /js/{companyId}/{pixlId}.js` | Serving dynamic scripts |
| Raw storage | `PiXL.Test` (9 columns) | 963 rows from test traffic |
| Parsed storage | `PiXL.Parsed` (~175 columns) | 963 rows materialized |
| ETL proc | `ETL.usp_ParseNewHits` (8-phase, watermark-based) | Running every 60s via `EtlBackgroundService` |
| `GetQueryParam` UDF | Pure T-SQL scalar function | ~170 calls per row across 8 phases |
| `PiXL.Config` table | Per-PiXL collection/exclusion rules | Schema exists, 0 rows |
| Server-side enrichment | `FingerprintStabilityService`, `IpBehaviorService` | Active — appends `_srv_*` params |
| Write pipeline | `DatabaseWriterService` — `Channel<T>` → `SqlBulkCopy` | Batched, async, non-blocking |
| Dashboard views | 10+ `vw_Dash_*` views on `PiXL.Parsed` | Operational |
| ETL watermark | `ETL.Watermark` | LastProcessedId = 100916 |

**Available on G2_Local (not SmartPixl DB):**

| Component | Location | Details |
|-----------|----------|---------|
| AutoConsumer | `AutoUpdate.dbo.AutoConsumer` | 421M rows, 67M with email, 66.7M distinct emails |
| AutoConsumer indexes | Clustered on `RecordID` | **No index on EMail** — this is our bottleneck |
| CLR support | `sys.configurations` | CLR enabled, SQL Server 2019 Enterprise |

**Current test data in PiXL.Test/PiXL.Parsed (963 rows):**

| CompanyID | PiXLID | Hits | Date Range |
|-----------|--------|------|------------|
| 12345 | 1 | 686 | 2026-02-08 |
| CLIENT_ID | PIXL | 232 | 2026-02-03 to 2026-02-12 |
| DEMO | demo-pixl | 44 | 2026-02-02 to 2026-02-10 |
| NULL | NULL | 1 | 2026-02-02 |

Decision: **Keep test data as-is.** It coexists harmlessly with production data.

### 2.2 What's Missing

| Gap | Impact | Priority |
|-----|--------|----------|
| No Company / PiXL tables | Can't associate a CompanyID with configuration, billing, or status | **CRITICAL** |
| No client-param extraction in JS | Host page URL params (email, hid, etc.) are never captured | **CRITICAL** |
| No client-param columns in PiXL.Parsed | Even if captured, they wouldn't be materialized | **CRITICAL** |
| No device dimension table | No way to track unique devices across the platform or link fingerprints to visitors | **CRITICAL** |
| No IP dimension table | No way to track unique IPs with classification metadata or link IPs to visitors | **CRITICAL** |
| No visit fact table | No relational bridge between hits, devices, IPs, and client params | **CRITICAL** |
| No match table | Nowhere to store identity resolution results | **CRITICAL** |
| No match procedure | No logic to resolve emails against AutoConsumer | **CRITICAL** |
| No match background service | No C# process to drive the match proc | **CRITICAL** |
| No composite device hash | No way to identify a device across visits — individual hash columns exist but no composite | **HIGH** |
| No AutoConsumer email index | Email lookups scan 421M rows | **HIGH** |
| No SQLCLR functions | ETL relies on slow T-SQL scalar UDFs | **MEDIUM** (perf) |
| No PiXL config cache | JS endpoint would need DB hit per request for client params | **HIGH** |

### 2.3 Key Constraints & Decisions Already Made

1. **AutoConsumer is local** — exists in `AutoUpdate` DB on the same G2_Local SQL Server instance. Cross-database queries are fast. We can add indexes but not change the schema.
2. **IndividualKey for email/IP matches, AddressKey for geo** — AutoConsumer uses `IndividualKey` (varchar(35), 343M distinct, ~1.22 AC rows per key) to identify a person and `AddressKey` (varchar(35), 118M distinct, ~3.56 AC rows per key) to identify a household. We match against these — NOT `RecordID`. AC is denormalized with duplicates across email and VIN vectors; IndividualKey groups those duplicates.
3. **Normalized schema with 4 new tables** — `PiXL.Device` (global device dim), `PiXL.IP` (global IP dim), `PiXL.Visit` (1:1 fact table), `PiXL.Match` (identity resolution with FKs to device/IP/visit). Maximum normalization — same device or IP across companies shares one row.
4. **Device hash computed in SQL** — SHA-256 of top 5 entropy fields (Canvas + Fonts + GPU + WebGL + Audio) computed in Phase 9 of `ETL.usp_ParseNewHits` via `HASHBYTES`. PiXL.Device is lean (hash + metadata only); join back to PiXL.Parsed for component fields.
5. **PiXL.Visit as the relational fact table** — 1:1 with PiXL.Parsed (same PK = SourceId), carries FKs to PiXL.Device and PiXL.IP plus ClientParamsJson. PiXL.Parsed stays as the immutable 175-column warehouse; PiXL.Visit is the lightweight relational bridge.
6. **Client params in JS** — extracted from `location.search` on the host page, prefixed with `_cp_` to avoid collisions with our 158 built-in fingerprint param names.
7. **Config-driven param lists** — stored in the PiXL table per-pixel, cached in memory by a new C# service. Zero DB hits on the hot path.
8. **Lean new tables** — Company and PiXL tables are purpose-built for the new platform, not copies of the 38-col / 46-col old schemas.
9. **Email-first matching for this client** — fingerprint-based and geo-based matching are future phases. Email is deterministic and the client provides it.
10. **No staging tables** — the old platform's 4-table staging rotation was a workaround for blocking IIS writes. Our async `Channel<T>` → `SqlBulkCopy` pipeline doesn't need it. `PiXL.Test` IS our raw table.
11. **No CRM_Match_Dates equivalent** — `PiXL.Visit` is the per-hit fact table. `PiXL.Match` links to visits via `FirstSourceId`/`LatestSourceId`, and all historical hits live in `PiXL.Parsed` (clustered on `ReceivedAt`).
12. **SQLCLR for performance** — `SAFE` permissions where possible, `UNSAFE` only if we go full pointer-math. Assembly signed with a strong name key.
13. **Test data stays** — the 963 existing rows remain untouched.

---

## 3. Target Architecture

### 3.1 Data Flow Diagram

```
 HOST PAGE (thetriviaquest.com)
 └── URL has ?email=xxx&hid=xxx&datatype=xxx
        │
        ▼
 ┌─────────────────────────────────────────────────────┐
 │  Pixel JavaScript (served by /js/12800/1.js)        │
 │                                                     │
 │  1. Collects 158 fingerprint signals (existing)     │
 │  2. NEW: Reads PiXL's ClientParams list             │
 │     → Extracts email, hid, etc. from location.search│
 │     → Adds as data['_cp_email'], data['_cp_hid']... │
 │  3. Fires: GET /12800/1_SMART.GIF?sw=1920&...       │
 │           &_cp_email=bpryce6%40gmail.com             │
 │           &_cp_hid=2602102158531672553               │
 │           &_cp_datatype=ret-openers                  │
 └──────────────────────┬──────────────────────────────┘
                        │
                        ▼
 ┌─────────────────────────────────────────────────────┐
 │  Pixel Endpoint (C# — TrackingEndpoints.cs)         │
 │                                                     │
 │  • Captures request → TrackingData record           │
 │  • Server-side enrichment (_srv_* signals)          │
 │  • Queues to DatabaseWriterService (Channel<T>)     │
 │  • Returns 1×1 transparent GIF                      │
 │                                                     │
 │  ⚡ ALL OF THIS IS UNCHANGED — _cp_ params are     │
 │     just more key=value pairs in the query string   │
 └──────────────────────┬──────────────────────────────┘
                        │
                        ▼
 ┌─────────────────────────────────────────────────────┐
 │  DatabaseWriterService (Background, existing)       │
 │                                                     │
 │  Channel<TrackingData> → batch 100 → SqlBulkCopy   │
 │  → PiXL.Test (9 columns, raw ingest)               │
 │     QueryString carries ALL params including _cp_*  │
 └──────────────────────┬──────────────────────────────┘
                        │
                        ▼
 ┌─────────────────────────────────────────────────────┐
 │  EtlBackgroundService → ETL.usp_ParseNewHits        │
 │  (Every 60s, batch of 50K, existing + NEW phases)   │
 │                                                     │
 │  Phase 1-8:  Existing fingerprint parsing (UNCHGD)  │
 │  Phase 9  (NEW): Compute DeviceHash                 │
 │    → HASHBYTES('SHA2_256', Canvas|Fonts|GPU|WGL|Aud)│
 │  Phase 10 (NEW): MERGE → PiXL.Device               │
 │    → Upsert device by hash, get DeviceId            │
 │  Phase 11 (NEW): MERGE → PiXL.IP                   │
 │    → Upsert IP, get IpId                            │
 │  Phase 12 (NEW): Client param extraction            │
 │    → Scan _cp_* from QueryString → JSON             │
 │  Phase 13 (NEW): INSERT → PiXL.Visit               │
 │    → 1:1 fact row with DeviceId, IpId, ClientParams │
 │                                                     │
 │  → PiXL.Parsed  (~178 cols, immutable warehouse)    │
 │  → PiXL.Device  (global device dimension)           │
 │  → PiXL.IP      (global IP dimension)               │
 │  → PiXL.Visit   (relational fact table)             │
 └──────────────────────┬──────────────────────────────┘
                        │
                        ▼
 ┌─────────────────────────────────────────────────────┐
 │  MatchBackgroundService → ETL.usp_MatchVisits (NEW)  │
 │  (Every 120s, independent watermark)                │
 │                                                     │
 │  1. Reads PiXL.Visit where MatchEmail IS NOT NULL   │
 │  2. Normalizes email (clr_NormalizeEmail)            │
 │  3. Joins AutoUpdate.dbo.AutoConsumer on EMail       │
 │     → Gets IndividualKey (for email/IP matches)     │
 │     → Gets AddressKey (for future geo matches)      │
 │  4. MERGE into PiXL.Match:                          │
 │     • INSERT new matches (with DeviceId, IpId FKs)  │
 │     • UPDATE existing (HitCount, LastSeen)          │
 │                                                     │
 │  → PiXL.Match (identity resolution w/ FKs)          │
 └─────────────────────────────────────────────────────┘
```

### 3.2 Table Lineage

```
 PiXL.Test (raw ingest — SqlBulkCopy target)
       │
       │  ETL.usp_ParseNewHits (watermark-based, 50K batch)
       │  Phases 1-8: parse fingerprints
       │  Phases 9-13: device, IP, client params, visit
       │
       ├─────► PiXL.Parsed (materialized — ~178 typed columns, immutable warehouse)
       │
       ├─────► PiXL.Device (global device dimension — one row per DeviceHash)
       │          │  DeviceHash = SHA2_256(Canvas|Fonts|GPU|WebGL|Audio)
       │          │  Lean: hash + FirstSeen + LastSeen + HitCount
       │          │  FK back to PiXL.Parsed for component fields
       │          │
       ├─────► PiXL.IP (global IP dimension — one row per IPAddress)
       │          │  IPAddress + IpType + IsDatacenter + Provider
       │          │  FirstSeen + LastSeen + HitCount
       │          │
       └─────► PiXL.Visit (fact table — 1:1 with PiXL.Parsed)
                  │  SourceId (PK, same as PiXL.Parsed)
                  │  DeviceId FK → PiXL.Device
                  │  IpId FK → PiXL.IP
                  │  ClientParamsJson + MatchEmail (persisted computed)
                  │
                  │  ETL.usp_MatchVisits (watermark-based, 10K batch)
                  │
                  └─────► PiXL.Match (identity resolution)
                             │  IndividualKey → AutoConsumer (email/IP matches)
                             │  AddressKey → AutoConsumer (geo matches)
                             │  DeviceId FK → PiXL.Device
                             │  IpId FK → PiXL.IP
                             │  FirstSourceId/LatestSourceId FK → PiXL.Visit
                             │
                             │  Cross-DB lookup
                             └─────► AutoUpdate.dbo.AutoConsumer (421M rows)
                                        IndividualKey: 343M distinct 
                                        AddressKey: 118M distinct
                                        EMail: 67M rows


 Supporting / Config:
 ┌────────────────┐   ┌──────────────┐   ┌──────────────────┐
 │ PiXL.Company   │──▶│ PiXL.Pixel   │──▶│ PiXL.Config      │
 │ (client info)  │   │ (pixel cfg)  │   │ (collection rules)│
 └────────────────┘   └──────────────┘   └──────────────────┘
```

### 3.3 Service Architecture

```
 Program.cs Service Registration (after changes):
 ┌─────────────────────────────────────────────────────────────┐
 │ SINGLETON SERVICES                                         │
 │                                                             │
 │  FileTrackingLogger .............. Async file logging       │
 │  TrackingCaptureService .......... HTTP → TrackingData      │
 │  DatabaseWriterService ........... Channel → SqlBulkCopy    │
 │  FingerprintStabilityService ..... FP variation tracking    │
 │  IpBehaviorService ............... Subnet/rapid-fire alerts │
 │  DatacenterIpService ............. AWS/GCP CIDR ranges      │
 │  PiXLConfigCacheService (NEW) .... In-memory PiXL config    │
 │                                                             │
 │ HOSTED SERVICES (background loops)                         │
 │                                                             │
 │  DatabaseWriterService ........... Continuous drain loop    │
 │  DatacenterIpService ............. Weekly CIDR refresh      │
 │  EtlBackgroundService ............ ETL.usp_ParseNewHits q60s   │
 │  MatchBackgroundService (NEW) .... ETL.usp_MatchVisits q120s   │
 │  PiXLConfigCacheService (NEW) .... PiXL table refresh q5m  │
 └─────────────────────────────────────────────────────────────┘
```

---

## 4. Implementation Plan — Ordered Steps

The phases below are ordered by dependency. Each phase can be validated independently before moving to the next. SQL changes come first because everything downstream depends on them.

---

### Phase 0: Schema Creation & Table Migration

**Migration script:** `SQL/17B_CreateSchemas.sql`

This phase creates the `PiXL` and `ETL` schemas and moves existing `dbo` objects into them. Must run **before** all other MVP migrations. Uses `ALTER SCHEMA ... TRANSFER` which is a metadata-only operation — instant, preserves all data, indexes, constraints, and permissions.

#### Step 0.1: Create Schemas

```sql
CREATE SCHEMA PiXL AUTHORIZATION dbo;
CREATE SCHEMA ETL AUTHORIZATION dbo;
```

#### Step 0.2: Transfer Existing Tables and Rename

`ALTER SCHEMA TRANSFER` moves an object to a new schema but **keeps its original object name**. A follow-up `sp_rename` strips the now-redundant prefix so we get clean dot-notation names (e.g. `PiXL.Test` instead of `PiXL.PiXL_Test`).

```sql
-- 1. Move to new schemas (metadata-only, instant)
ALTER SCHEMA PiXL TRANSFER dbo.PiXL_Test;
ALTER SCHEMA PiXL TRANSFER dbo.PiXL_Parsed;
ALTER SCHEMA PiXL TRANSFER dbo.PiXL_Config;
ALTER SCHEMA ETL  TRANSFER dbo.ETL_Watermark;
ALTER SCHEMA ETL  TRANSFER dbo.usp_ParseNewHits;

-- 2. Strip now-redundant prefixes
EXEC sp_rename 'PiXL.PiXL_Test',    'Test',            'OBJECT';
EXEC sp_rename 'PiXL.PiXL_Parsed',  'Parsed',          'OBJECT';
EXEC sp_rename 'PiXL.PiXL_Config',  'Config',          'OBJECT';
EXEC sp_rename 'ETL.ETL_Watermark',  'Watermark',       'OBJECT';
-- ETL.usp_ParseNewHits has no ETL_ prefix — no rename needed
```

#### Step 0.3: Update Dependent Objects

After the transfers, all objects that reference the old `dbo.*` names must be updated:

1. **`ETL.usp_ParseNewHits`** — Already transferred to ETL schema. Internal references to `dbo.PiXL_Test` and `dbo.PiXL_Parsed` must be updated to `PiXL.Test` and `PiXL.Parsed` via `ALTER PROCEDURE`.
2. **Dashboard views** — All 10+ `vw_Dash_*` views reference `dbo.PiXL_Parsed` and `dbo.ETL_Watermark`. Each must be `ALTER VIEW`'d to use the new schema-qualified names.
3. **`dbo.GetQueryParam` UDF** — Stays in `dbo` (generic utility). No change needed since the ETL proc references it by full name.

#### Step 0.4: Update C# Code References

| File | Old Reference | New Reference |
|------|---------------|---------------|
| `DatabaseWriterService.cs` | `dbo.PiXL_Test` (SqlBulkCopy destination) | `PiXL.Test` |
| `EtlBackgroundService.cs` | `EXEC dbo.usp_ParseNewHits` | `EXEC ETL.usp_ParseNewHits` |
| `DashboardEndpoints.cs` | `vw_Dash_*` queries (views updated in Step 0.3) | No change needed if views keep same names |

**Validation after Step 0:**
- `SELECT TOP 1 * FROM PiXL.Test` — data intact
- `SELECT TOP 1 * FROM PiXL.Parsed` — data intact
- `SELECT * FROM ETL.Watermark` — watermark intact
- `EXEC ETL.usp_ParseNewHits @BatchSize = 1` — proc runs successfully
- Hit `/tron` dashboard — verify all panels still load

---

### Phase 1: Foundation — Configuration Tables

**Migration script:** `SQL/18_CompanyAndPiXLTables.sql`

This phase creates the core configuration tables that everything else references. Must be done first because all downstream tables and services depend on Company/PiXL existing.

#### Step 1.1: Create `PiXL.Company`

Lean schema — just enough to identify and manage a client. Fields can be added as the frontend is built.

```
Columns:
  CompanyID         INT             NOT NULL  PRIMARY KEY   (not identity — assigned IDs)
  CompanyName       VARCHAR(100)    NOT NULL
  ContactName       VARCHAR(100)    NULL
  Email             VARCHAR(100)    NULL                    (company contact email, NOT consumer email)
  Phone             VARCHAR(50)     NULL
  Address           VARCHAR(500)    NULL
  City              VARCHAR(50)     NULL
  State             VARCHAR(50)     NULL
  Zipcode           VARCHAR(10)     NULL
  StatusId          INT             NOT NULL  DEFAULT 1
  IsActive          BIT             NOT NULL  DEFAULT 1
  CreatedDate       DATETIME2       NOT NULL  DEFAULT GETUTCDATE()
  ModifiedDate      DATETIME2       NOT NULL  DEFAULT GETUTCDATE()
  Notes             NVARCHAR(500)   NULL
```

**14 columns.** Down from the old platform's 38. We dropped: TaxId, NAICS/SIC codes, ParentCompanyId, billing hierarchy, P&L columns, portal URL, sales rep, ramp-up period — all frontend/billing concerns that don't exist yet.

#### Step 1.2: Create `PiXL.Pixel`

The core pixel configuration table. Named `Pixel` under the `PiXL` schema to avoid the awkward `PiXL.PiXL`. The critical new column here is `ClientParams`.

```
Columns:
  CompanyId         INT             NOT NULL
  PiXLId            INT             NOT NULL
  PiXLName          VARCHAR(200)    NOT NULL
  PiXLURL           VARCHAR(1000)   NULL      (URL where pixel is deployed)
  PiXLDomain        VARCHAR(500)    NULL      (for domain validation)
  Zipcode           VARCHAR(10)     NULL      (pixel's geo center for future geo-matching)
  Radius            INT             NULL      (geo radius in miles for future geo-matching)
  StatusId          INT             NOT NULL  DEFAULT 1
  IsActive          BIT             NOT NULL  DEFAULT 1
  ClientParams      NVARCHAR(500)   NULL      (** NEW ** comma-separated host-page param names)
  CreatedDate       DATETIME2       NOT NULL  DEFAULT GETUTCDATE()
  ModifiedDate      DATETIME2       NOT NULL  DEFAULT GETUTCDATE()
  Notes             NVARCHAR(500)   NULL

Constraints:
  PRIMARY KEY (CompanyId, PiXLId)
  FOREIGN KEY (CompanyId) → PiXL.Company(CompanyID)
```

**13 columns.** Down from the old platform's 46. We dropped: SmartPiXL/PiXLNew/PiXLLegacy paths, output paths, income/networth/marital/children/gender filters, SuspendedId, user assignment, latitude/longitude, policy URL — all legacy concerns.

**`ClientParams` design detail:** This is a simple comma-separated string like `'email,hid,q_id,id,answer,guess,difficulty,decade,category_id,sub_category_id,datatype'`. The C# `PiXLConfigCacheService` parses this into a `string[]` once at load time. The JS endpoint renders it into the script as a JavaScript array literal. Simple, fast, no overhead.

Why not a separate table for client params? Because the data is tiny, static, and always read as a whole set per PiXL. A join to a child table adds complexity for zero benefit at this scale.

#### Step 1.3: Create `ETL.MatchWatermark`

Same pattern as the existing `ETL.Watermark` table but for the match stage. Separate table (not a row in ETL.Watermark) to avoid any contention between the two ETL processes.

```
Columns:
  ProcessName         NVARCHAR(100) NOT NULL  PRIMARY KEY
  LastProcessedId     BIGINT        NOT NULL  DEFAULT 0
  LastRunAt           DATETIME2     NULL
  RowsProcessed       BIGINT        NOT NULL  DEFAULT 0
  RowsMatched         BIGINT        NOT NULL  DEFAULT 0   (** extra counter **)
```

Seed: `INSERT INTO ETL.MatchWatermark VALUES ('MatchVisits', 0, NULL, 0, 0);`

The extra `RowsMatched` counter tracks how many rows actually resolved to an AutoConsumer record, vs. total rows processed. Useful for monitoring match rates.

---

### Phase 1B: Normalized Dimension & Fact Tables

**Migration script:** `SQL/19_DeviceIpVisitMatchTables.sql`

This phase creates the four normalized tables that form the relational star schema. These tables depend on Phase 1 (Company/PiXL for FK references) and must exist before the ETL extensions in Phase 4 can populate them.

#### Step 1B.1: Create `PiXL.Device` — Global Device Dimension

**Platform-wide**, one row per unique device fingerprint hash, regardless of which company or pixel it came from. By design, the same physical device appearing across multiple clients shares one `PiXL.Device` row.

```
Columns:
  DeviceId          BIGINT IDENTITY(1,1)              (surrogate PK — nonclustered)
  DeviceHash        VARBINARY(32)   NOT NULL           (SHA2_256 of Canvas|Fonts|GPU|WebGL|Audio)
  FirstSeen         DATETIME2(3)    NOT NULL  DEFAULT SYSUTCDATETIME()
  LastSeen          DATETIME2(3)    NOT NULL  DEFAULT SYSUTCDATETIME()
  HitCount          INT             NOT NULL  DEFAULT 1

Indexes:
  PK_PiXL_Device         NONCLUSTERED on (DeviceId)
  CIX_PiXL_Device        UNIQUE CLUSTERED on (DeviceHash)    — the natural key + MERGE target
```

**Lean by design.** Only 5 columns. The component fingerprint fields that form the hash (CanvasFingerprint, DetectedFonts, GPURenderer, WebGLFingerprint, AudioFingerprintHash) live in `PiXL.Parsed`. To see what a device looks like, join `PiXL.Visit.DeviceId → PiXL.Device → PiXL.Visit.SourceId → PiXL.Parsed`.

**Why these 5 signals for the hash:**
From real-world entropy analysis (n=252 pixel records):
| Signal | Distinct Values | Entropy |
|--------|:-:|:-:|
| CanvasFingerprint | 68 | ~6.1 bits |
| DetectedFonts | 62 | ~6.0 bits |
| GPURenderer | 53 | ~5.7 bits |
| WebGLFingerprint | 33 | ~5.0 bits |
| AudioFingerprintHash | 29 | ~4.9 bits |

Combined uniqueness: These 5 produce ~85%+ identification rate from test data. Dead signals excluded (MathFingerprint = 0 bits, CSSFontVariantHash ≈ 0.03 bits).

**Hash computation (SQL, Phase 9 of ETL.usp_ParseNewHits):**
```sql
HASHBYTES('SHA2_256', 
  CONCAT_WS('|', 
    CanvasFingerprint, DetectedFonts, GPURenderer, 
    WebGLFingerprint, AudioFingerprintHash))
```

**DeviceHash is clustered** because the MERGE in Phase 10 seeks by DeviceHash. Clustering on the MERGE join key makes the upsert a clustered index seek — the most efficient pattern.

#### Step 1B.2: Create `PiXL.IP` — Global IP Dimension

**Platform-wide**, one row per unique IP address. Same IP across multiple companies shares one row, enabling cross-client pattern detection.

```
Columns:
  IpId                BIGINT IDENTITY(1,1)              (surrogate PK — nonclustered)
  IPAddress           VARCHAR(50)     NOT NULL           (supports IPv4 & IPv6, natural key)
  IpType              VARCHAR(20)     NULL               (from IpClassificationService: Public/Private/CGNAT/etc.)
  IsDatacenter        BIT             NULL               (from DatacenterIpService)
  DatacenterProvider  VARCHAR(20)     NULL               (AWS/GCP/NULL)
  FirstSeen           DATETIME2(3)    NOT NULL  DEFAULT SYSUTCDATETIME()
  LastSeen            DATETIME2(3)    NOT NULL  DEFAULT SYSUTCDATETIME()
  HitCount            INT             NOT NULL  DEFAULT 1

Indexes:
  PK_PiXL_IP            NONCLUSTERED on (IpId)
  CIX_PiXL_IP           UNIQUE CLUSTERED on (IPAddress)     — natural key + MERGE target
```

**Note on IpType/IsDatacenter/DatacenterProvider:** These columns are populated as NULL initially — the `IpClassificationService` and `DatacenterIpService` results are currently NOT persisted to any table (they run at request time only). Phase 11's MERGE will INSERT with NULLs. A future enhancement could backfill these via a scheduled scan, or the MERGE could be extended to call the services inline.

#### Step 1B.3: Create `PiXL.Visit` — Fact Table (1:1 with PiXL.Parsed)

The **relational bridge** between the immutable 175-column warehouse (`PiXL.Parsed`) and the normalized dimension tables. One row per pixel fire, same PK as `PiXL.Parsed`.

```
Columns:
  SourceId          BIGINT          NOT NULL  PRIMARY KEY    (same as PiXL.Parsed.SourceId / PiXL.Test.Id)
  CompanyID         INT             NOT NULL                  (denormalized for partitioning/querying)
  PiXLID            INT             NOT NULL                  (denormalized)
  DeviceId          BIGINT          NULL                      (FK → PiXL.Device — NULL if device hash couldn't be computed)
  IpId              BIGINT          NULL                      (FK → PiXL.IP — NULL if no IP)
  ReceivedAt        DATETIME2(3)    NOT NULL                  (denormalized for time queries)
  ClientParamsJson  NVARCHAR(4000)  NULL                      (extracted _cp_* params as JSON — 11 typical params ≈ 500 chars, 50 max ≈ 2000)
  MatchEmail        AS CAST(JSON_VALUE(ClientParamsJson, '$.email') AS NVARCHAR(200)) PERSISTED
  CreatedAt         DATETIME2(3)    NOT NULL  DEFAULT SYSUTCDATETIME()

Indexes:
  PK_PiXL_Visit           on (SourceId)                       — PK, same grain as PiXL.Parsed
  CIX_PiXL_Visit          CLUSTERED on (ReceivedAt, SourceId)  — time-series physical sort (matches PiXL.Parsed)
  IX_PiXL_Visit_Company   NONCLUSTERED on (CompanyID, PiXLID, ReceivedAt)
  IX_PiXL_Visit_Device    NONCLUSTERED on (DeviceId) WHERE DeviceId IS NOT NULL
  IX_PiXL_Visit_IP        NONCLUSTERED on (IpId) WHERE IpId IS NOT NULL
  IX_PiXL_Visit_MatchEmail NONCLUSTERED on (MatchEmail) 
                           INCLUDE (SourceId, CompanyID, PiXLID, DeviceId, IpId, ReceivedAt)
                           WHERE MatchEmail IS NOT NULL

Foreign Keys:
  FK_Visit_Device → PiXL.Device(DeviceId)
  FK_Visit_IP → PiXL.IP(IpId)
```

**Why SourceId as PK (not IDENTITY):** There's no reason to create a new surrogate key. `PiXL.Visit` is 1:1 with `PiXL.Parsed`, so we reuse the same `SourceId`. This means `PiXL.Visit.SourceId` = `PiXL.Parsed.SourceId` = `PiXL.Test.Id` — a single chain of identity. Joining is trivial and there's no ambiguity.

**Why denormalize CompanyID, PiXLID, ReceivedAt:** These are the most common query filters. Requiring a join to PiXL.Parsed just to filter by company or date would be wasteful. The denormalization is tiny (12 bytes per row) and eliminates constant joins.

**MatchEmail as persisted computed column:** Automatically extracts the `email` value from `ClientParamsJson` and materializes it. SQL Server maintains this on INSERT/UPDATE. The filtered index on `MatchEmail` enables the match proc's watermark scan to be an efficient seek — only rows with emails are indexed.

**ClientParamsJson** stores ALL client-specific params as a JSON object. Example:
```json
{
  "email": "bpryce6@gmail.com",
  "hid": "2602102158531672553",
  "guess": "Tool Time",
  "difficulty": "easy",
  "decade": "90s",
  "category_id": "4",
  "sub_category_id": "4.04",
  "datatype": "ret-openers"
}
```

**Why JSON:** Each client sends different parameters. Hard-coding columns per client would require schema changes for every new client and leave NULLs everywhere. JSON is flexible, self-describing, and `JSON_VALUE()` is optimized in SQL Server 2019.

#### Step 1B.4: Create `PiXL.Match` — Identity Resolution

The identity resolution output table. **One row per unique (CompanyID, PiXLID, MatchType, MatchKey) combination.** This is NOT a per-hit table — it's a per-identity table that accumulates hit metadata.

```
Columns:
  MatchId             BIGINT IDENTITY(1,1)            (surrogate PK — nonclustered)
  CompanyID           INT           NOT NULL
  PiXLID              INT           NOT NULL
  MatchType           VARCHAR(20)   NOT NULL           ('email', 'ip', 'geo' — extensible)
  MatchKey            VARCHAR(256)  NOT NULL           (the matched value: email, IP, zip)
  IndividualKey       VARCHAR(35)   NULL               (→ AutoConsumer.IndividualKey — for email/IP matches)
  AddressKey          VARCHAR(35)   NULL               (→ AutoConsumer.AddressKey — for geo matches)
  DeviceId            BIGINT        NULL               (FK → PiXL.Device — device at time of first match)
  IpId                BIGINT        NULL               (FK → PiXL.IP — IP at time of first match)
  FirstSourceId       BIGINT        NOT NULL           (FK → PiXL.Visit.SourceId — first hit)
  LatestSourceId      BIGINT        NOT NULL           (FK → PiXL.Visit.SourceId — most recent hit)
  FirstSeen           DATETIME2(3)  NOT NULL
  LastSeen            DATETIME2(3)  NOT NULL
  HitCount            INT           NOT NULL  DEFAULT 1
  ConfidenceScore     FLOAT         NULL               (for future scoring)
  MatchedAt           DATETIME2(3)  NULL               (when IndividualKey/AddressKey was resolved)

Indexes:
  PK_PiXL_Match           NONCLUSTERED on (MatchId)
  CIX_PiXL_Match          UNIQUE CLUSTERED on (CompanyID, PiXLID, MatchType, MatchKey)  — dedup key
  IX_PiXL_Match_IndKey     NONCLUSTERED on (IndividualKey) WHERE IndividualKey IS NOT NULL
  IX_PiXL_Match_AddrKey    NONCLUSTERED on (AddressKey) WHERE AddressKey IS NOT NULL
  IX_PiXL_Match_Device     NONCLUSTERED on (DeviceId) WHERE DeviceId IS NOT NULL
  IX_PiXL_Match_LastSeen   NONCLUSTERED on (LastSeen DESC) INCLUDE (CompanyID, PiXLID, MatchType)

Foreign Keys:
  FK_Match_Device → PiXL.Device(DeviceId)
  FK_Match_IP → PiXL.IP(IpId)
  FK_Match_FirstVisit → PiXL.Visit(SourceId)     — via FirstSourceId
  FK_Match_LatestVisit → PiXL.Visit(SourceId)    — via LatestSourceId
```

**Key design change from v1: IndividualKey/AddressKey instead of ReferenceRecordID.**

AutoConsumer is denormalized — the same person can have multiple RecordIDs (one per email, one per VIN, etc.). `IndividualKey` (varchar(35), 343M distinct across 421M rows, ~1.22 rows per key) is the stable identity that groups all AC records for the same person. `AddressKey` (varchar(35), 118M distinct, ~3.56 rows per key) groups people at the same household.

- **Email matches:** Resolve email → `IndividualKey` (identifies the person)
- **IP matches:** Resolve IP → `IndividualKey` (identifies the person at that IP)
- **Geo matches:** Resolve zip/radius → `AddressKey` (identifies the household)

Both are nullable because a match might be unresolvable (email not in AC, etc.). `MatchedAt` records when resolution happened.

**DeviceId and IpId FKs** capture the device and IP at the time of **first** match. This enables queries like "show me all matches from datacenter IPs" or "show me all matches from the same device."

**The clustered index on `(CompanyID, PiXLID, MatchType, MatchKey)`** means the MERGE upsert in the match proc is a clustered index seek — the most efficient pattern for "insert if new, update if exists."

---

### Phase 2: SQLCLR Assembly — High-Performance Functions

**Files:**
- New C# class library project: `SmartPixl.Clr/` (at repo root or under `TrackingPixel.Modern/`)
- Migration script: `SQL/19_ClrFunctions.sql`

This phase builds the SQLCLR assembly that will dramatically accelerate the ETL. The current `GetQueryParam` T-SQL UDF is a scalar function that causes per-row context switching — the single biggest performance bottleneck in `ETL.usp_ParseNewHits`. Replacing it with a CLR function eliminates that overhead.

We build the CLR assembly before modifying the ETL proc so we can swap the UDF references atomically in Phase 4.

#### Step 2.1: Create the `SmartPixl.Clr` C# Project

- Target: **.NET Framework 4.8** (required for SQL Server 2019 CLR hosting — .NET Core CLR is not supported)
- Assembly name: `SmartPixl.Clr`
- Signed with a strong name key (`SmartPixl.Clr.snk`)
- References: `System`, `System.Data`, `Microsoft.SqlServer.Server` (from SQL Server SDK)

#### Step 2.2: `clr_GetQueryParam` — Combined Find-and-Decode

**Signature:** `dbo.clr_GetQueryParam(@QueryString NVARCHAR(MAX), @ParamName NVARCHAR(100)) RETURNS NVARCHAR(4000)`

**What it does:** Replaces the T-SQL `dbo.GetQueryParam` UDF. Finds a named parameter in a URL-encoded query string and returns its decoded value. Single pass, no intermediate string allocations.

**Algorithm:**
1. Scan `@QueryString` for `@ParamName=` (preceded by `&` or at position 0)
2. Extract the substring from `=` to the next `&` (or end of string)
3. URL-decode in-place: `%XX` hex pairs → char, `+` → space
4. Return the decoded value (or NULL if param not found)

**Performance characteristics:**
- Uses `Span<char>` / `stackalloc` for the decode buffer when output ≤ 4000 chars
- Single scan of the query string — O(n) where n = query string length
- No regex, no string splitting, no intermediate collections
- The CLR function attribute: `[SqlFunction(IsDeterministic = true, IsPrecise = true, DataAccess = DataAccessKind.None)]`
  - `IsDeterministic = true` → SQL Server can cache results and fold constant expressions
  - `DataAccess = None` → no context switching back into the SQL engine
  - These attributes allow the query optimizer to treat the function call much more efficiently

**Drop-in replacement strategy:** Same name pattern, different schema prefix (`dbo.clr_GetQueryParam` vs `dbo.GetQueryParam`). In Phase 4 we'll update `ETL.usp_ParseNewHits` to use the CLR version. The old T-SQL UDF stays as a fallback.

#### Step 2.3: `clr_UrlDecode` — Standalone URL Decoder

**Signature:** `dbo.clr_UrlDecode(@Input NVARCHAR(4000)) RETURNS NVARCHAR(4000)`

**What it does:** Full RFC 3986 URL decoding. Useful for ad-hoc queries and the match proc where we need to decode individual values (like the email from `ClientParamsJson`).

**Algorithm:**
- Single pass over input characters
- `%XX` → hex decode to char (handles both upper/lowercase hex)
- `+` → space
- UTF-8 multi-byte sequences: accumulates `%XX%YY%ZZ` bytes and decodes via `Encoding.UTF8.GetString`
- Uses `stackalloc char[]` for output buffer (stack-only, zero heap allocation for typical inputs)

**Safety:** Marked `[SqlFunction(IsDeterministic = true, IsPrecise = true, DataAccess = DataAccessKind.None)]`. Pure computation, no side effects.

#### Step 2.4: `clr_NormalizeEmail` — Email Normalization

**Signature:** `dbo.clr_NormalizeEmail(@Email NVARCHAR(200)) RETURNS NVARCHAR(200)`

**What it does:** Normalizes email addresses for consistent matching. Without this, `BPryce6@Gmail.com` and `bpryce6@gmail.com` would fail to match.

**Normalization rules:**
1. `LTRIM` / `RTRIM` whitespace
2. Lowercase the entire string
3. Strip Gmail-style `+alias` suffixes (e.g., `user+tag@gmail.com` → `user@gmail.com`) — only for gmail.com and googlemail.com domains
4. Normalize `googlemail.com` → `gmail.com`
5. Strip dots from Gmail local parts (Gmail ignores dots: `b.pryce6@gmail.com` = `bpryce6@gmail.com`)
6. Return NULL for obviously invalid inputs (no `@`, empty local part, empty domain)

**Why CLR instead of T-SQL:** The dot-stripping and `+alias` logic would require nested `REPLACE` / `CHARINDEX` / `SUBSTRING` calls in T-SQL that are painful to read and debug. CLR makes it a clean `ReadOnlySpan<char>` scan.

#### Step 2.5: Assembly Deployment

**The migration script `SQL/19_ClrFunctions.sql` will:**

1. Check if CLR is enabled (it is — confirmed `value_in_use = 1`)
2. Create an asymmetric key from the signed assembly DLL
3. Create a login mapped to the asymmetric key
4. Grant `UNSAFE ASSEMBLY` to the login (needed for `stackalloc` / pointer arithmetic)
5. `CREATE ASSEMBLY [SmartPixl.Clr] FROM 0x<hex_bytes>` — embedded hex avoids file path dependencies
6. Create the three scalar functions with proper `EXTERNAL NAME` references
7. Validation queries at the end:
   - `SELECT dbo.clr_UrlDecode('bpryce6%40gmail.com')` → `bpryce6@gmail.com`
   - `SELECT dbo.clr_GetQueryParam('sw=1920&email=test%40test.com&sh=1080', 'email')` → `test@test.com`
   - `SELECT dbo.clr_NormalizeEmail('  BPryce6+spam@Gmail.COM  ')` → `bpryce6@gmail.com`

**Permission set rationale:** `SAFE` is preferred, but `stackalloc` in newer C# compilers may require `UNSAFE`. We'll try `SAFE` first. If the compiler emits `System.Runtime.CompilerServices.Unsafe` references (common with Span<T> on .NET Framework 4.8), we'll need `UNSAFE`. The asymmetric key approach is the secure way to grant this — far better than enabling `TRUSTWORTHY` on the database.

---

### Phase 3: Schema Extensions — Client Params in PiXL.Parsed

> **NOTE (v2 revision):** In the v1 plan, `ClientParamsJson` and `MatchEmail` were added as columns on `PiXL.Parsed`. In the normalized design, **these columns now live on `PiXL.Visit`** (defined in Phase 1B, Step 1B.3). `PiXL.Parsed` remains unchanged — it stays as the immutable 175-column warehouse with no new columns.

**Migration script:** `SQL/20_ClientParamsSupport.sql`

This migration is now a **no-op for PiXL.Parsed** but still creates supporting infrastructure:

#### Step 3.1: Verify PiXL.Visit Indexes

Confirm the `IX_PiXL_Visit_MatchEmail` filtered index (created in Phase 1B) is in place. This index enables the match proc to efficiently find visits with email addresses.

#### Step 3.2: ~~Add `ClientParamsJson` to PiXL.Parsed~~ (MOVED to PiXL.Visit)

**This step is no longer needed.** `ClientParamsJson` and `MatchEmail` (persisted computed column) are defined directly on `PiXL.Visit` in Phase 1B, Step 1B.3.

**Rationale for the move:** `PiXL.Parsed` is the wide immutable warehouse. Adding client-specific JSON to it would break the pattern (it only has columns parsed from the fingerprint query string). Client params are relational metadata — they belong on the fact table that bridges to the dimension tables.

---

### Phase 4: ETL Extension — Device, IP, Visit & Client Params

**Migration script:** `SQL/21_ParseNewHits_Phases9to13.sql`

This phase extends the existing `ETL.usp_ParseNewHits` stored procedure with 5 new phases that populate the normalized dimension and fact tables. All new phases run **after** the existing Phase 8 (IP behavior signals), operating on the same batch of rows already inserted into PiXL.Parsed.

#### Step 4.1: Add Phase 9 — Compute DeviceHash

After Phase 8 inserts rows into PiXL.Parsed, Phase 9 computes the composite device fingerprint hash for each row in the current batch.

```
Logic:
1. In the batch temp table (#NewHits or equivalent), compute:
   DeviceHash = HASHBYTES('SHA2_256', 
     CONCAT_WS('|', 
       CanvasFingerprint, DetectedFonts, GPURenderer, 
       WebGLFingerprint, AudioFingerprintHash))

2. For rows where ALL 5 components are NULL (e.g., synthetic/bot hits),
   DeviceHash = NULL (no device record created).
```

**Why CONCAT_WS with pipe delimiter:** Without a delimiter, `CONCAT('abc', 'def')` = `CONCAT('ab', 'cdef')` — false collisions. The pipe separator ensures each component is distinct in the hash input.

**Why NULL for all-NULL components:** If a hit has no canvas, fonts, GPU, WebGL, or audio data (likely a bot or headless browser), creating a device record would be meaningless. The PiXL.Visit row will have `DeviceId = NULL`.

#### Step 4.2: Add Phase 10 — MERGE PiXL.Device

Upsert device records using the computed DeviceHash from Phase 9.

```
Logic:
MERGE PiXL.Device AS target
USING (
  SELECT DISTINCT DeviceHash, MIN(ReceivedAt) AS BatchFirstSeen
  FROM #BatchRows WHERE DeviceHash IS NOT NULL
  GROUP BY DeviceHash
) AS source ON target.DeviceHash = source.DeviceHash

WHEN MATCHED THEN UPDATE SET
  LastSeen = CASE WHEN source.BatchFirstSeen > target.LastSeen 
                  THEN source.BatchFirstSeen ELSE target.LastSeen END,
  HitCount = target.HitCount + (count of rows with this hash in batch)

WHEN NOT MATCHED THEN INSERT (DeviceHash, FirstSeen, LastSeen, HitCount)
  VALUES (source.DeviceHash, source.BatchFirstSeen, source.BatchFirstSeen, <count>);

-- OUTPUT DeviceId back into #BatchRows for use in Phase 13
UPDATE b SET b.DeviceId = d.DeviceId
FROM #BatchRows b JOIN PiXL.Device d ON b.DeviceHash = d.DeviceHash
WHERE b.DeviceHash IS NOT NULL;
```

**Why DISTINCT in the source:** A batch of 50K rows may have many rows from the same device. MERGE requires a unique source per target key — deduplicating by DeviceHash avoids the "MERGE attempted to update the same row more than once" error.

#### Step 4.3: Add Phase 11 — MERGE PiXL.IP

Upsert IP records.

```
Logic:
MERGE PiXL.IP AS target
USING (
  SELECT DISTINCT IPAddress, MIN(ReceivedAt) AS BatchFirstSeen
  FROM #BatchRows WHERE IPAddress IS NOT NULL AND IPAddress <> ''
  GROUP BY IPAddress
) AS source ON target.IPAddress = source.IPAddress

WHEN MATCHED THEN UPDATE SET
  LastSeen = CASE WHEN source.BatchFirstSeen > target.LastSeen 
                  THEN source.BatchFirstSeen ELSE target.LastSeen END,
  HitCount = target.HitCount + (count of rows with this IP in batch)

WHEN NOT MATCHED THEN INSERT (IPAddress, FirstSeen, LastSeen, HitCount)
  VALUES (source.IPAddress, source.BatchFirstSeen, source.BatchFirstSeen, <count>);

-- OUTPUT IpId back into #BatchRows
UPDATE b SET b.IpId = ip.IpId
FROM #BatchRows b JOIN PiXL.IP ip ON b.IPAddress = ip.IPAddress
WHERE b.IPAddress IS NOT NULL;
```

**IpType, IsDatacenter, DatacenterProvider** are left NULL on initial insert. These could be backfilled by a future maintenance job that calls `IpClassificationService` and `DatacenterIpService` logic in SQL, or by a C# service that scans PiXL.IP rows with NULL metadata.

#### Step 4.4: Add Phase 12 — Client Parameter Extraction

Extract `_cp_*` prefixed parameters from the QueryString and build a JSON object. This is the same logic as the v1 plan's "Phase 9" but now the JSON is written to `PiXL.Visit` (not `PiXL.Parsed`).

```
Logic:
1. For each row in the current batch:
   a. Scan the QueryString (from PiXL.Test, joined by SourceId) for params starting with '_cp_'
   b. Extract each _cp_* param using dbo.clr_GetQueryParam
   c. Strip the '_cp_' prefix from the key name
   d. URL-decode all values
   e. Build a JSON object: {"email":"decoded_value", "hid":"decoded_value", ...}
   f. Store in #BatchRows.ClientParamsJson

2. Only process rows from companies with configured ClientParams in PiXL.Pixel
   (JOIN to PiXL to check — this is a batch operation, not per-row)
```

**Implementation approach: Option A (dynamic `_cp_` scan) recommended.** The `_cp_` prefix convention means we can find ALL client params without knowing what they are. Self-documenting — whatever the JS sends with `_cp_`, the ETL captures.

#### Step 4.5: Add Phase 13 — INSERT PiXL.Visit

Insert the fact table rows, connecting everything together.

```
Logic:
INSERT INTO PiXL.Visit (SourceId, CompanyID, PiXLID, DeviceId, IpId, ReceivedAt, ClientParamsJson)
SELECT 
  SourceId, CompanyID, PiXLID, DeviceId, IpId, ReceivedAt, ClientParamsJson
FROM #BatchRows;
```

**This is a simple INSERT, not a MERGE.** PiXL.Visit is 1:1 with PiXL.Parsed — each SourceId appears exactly once. The ETL's watermark guarantees rows are processed once. No upsert needed.

**The MatchEmail persisted computed column** auto-populates from `ClientParamsJson` on insert. No additional step required.

#### Step 4.6: Swap `GetQueryParam` References to CLR Version

While modifying the proc, replace all `dbo.GetQueryParam(...)` calls in Phases 1-8 with `dbo.clr_GetQueryParam(...)`. This is a mechanical find-and-replace across the proc body — same signature, same semantics, dramatically faster execution.

**Rows affected:** ~170 UDF calls per row × 50,000 rows per batch = 8.5M function invocations per ETL cycle. The CLR version should cut proc execution time significantly.

**Rollback strategy:** Keep the old `dbo.GetQueryParam` T-SQL function. If CLR has issues, a single `ALTER PROCEDURE` can revert to the T-SQL UDF.

---

### Phase 5: AutoConsumer Index — Enable Email Matching

**Migration script:** `SQL/22_AutoConsumerEmailIndex.sql`

This is a single-statement change, but it's in its own phase because:
1. It touches a table we don't own (`AutoUpdate.dbo.AutoConsumer`)
2. Building a nonclustered index on 421M rows takes significant time and disk I/O
3. It should be run during a maintenance window (or with `ONLINE = ON` for zero downtime)

#### Step 5.1: Create Filtered Nonclustered Index on EMail

```sql
USE AutoUpdate;
GO

CREATE NONCLUSTERED INDEX IX_AutoConsumer_EMail
    ON dbo.AutoConsumer (EMail)
    INCLUDE (IndividualKey, AddressKey, IP_Clean, VPN_Flag)
    WHERE EMail IS NOT NULL
    WITH (ONLINE = ON, SORT_IN_TEMPDB = ON, MAXDOP = 4);
GO
```

**Index design rationale:**

| Decision | Why |
|----------|-----|
| **Filtered** (`WHERE EMail IS NOT NULL`) | Only 67M of 421M rows have email. Filtering excludes 354M irrelevant rows, reducing index size by ~84% |
| **INCLUDE columns** | `IndividualKey` and `AddressKey` are the match outputs. `IP_Clean` and `VPN_Flag` are used for filtering/enrichment. Including them avoids key lookups back to the clustered index |
| **ONLINE = ON** | Doesn't block concurrent reads/writes during index build. Critical since AutoConsumer is actively queried by other processes |
| **SORT_IN_TEMPDB = ON** | Uses tempdb for sort operations during build, reducing I/O contention on the AutoUpdate data files |
| **MAXDOP = 4** | Limits parallelism to avoid starving other workloads during the index build |

**Estimated size:** ~67M rows × (~112 bytes EMail + 4 bytes RecordID + 15 bytes IP_Clean + 1 byte VPN_Flag + row overhead) ≈ 10-15 GB. Verify tempdb has sufficient space.

**Estimated build time:** 10-30 minutes depending on I/O throughput. With `ONLINE = ON`, the table remains fully available during the build.

**Before running:** Verify with `SELECT COUNT(*) FROM AutoUpdate.dbo.AutoConsumer WHERE EMail IS NOT NULL` that the filtered row count hasn't changed dramatically from the 67M last measured.

---

### Phase 6: Match Stored Procedure — Identity Resolution

**Migration script:** `SQL/23_MatchVisits.sql`

This is the heart of the new functionality — the procedure that resolves visitor identities against AutoConsumer. **Renamed from `usp_MatchByEmail` to `ETL.usp_MatchVisits`** to reflect that it now reads from `PiXL.Visit` and will support multiple match types.

#### Step 6.1: Create `ETL.usp_MatchVisits`

**Parameters:**
```sql
@BatchSize INT = 10000
```

**Algorithm (detailed):**

```
1. READ WATERMARK
   Read ETL.MatchWatermark.LastProcessedId for 'MatchVisits'
   Determine @MaxId = MIN(MAX(SourceId) in PiXL.Visit, @LastId + @BatchSize)
   Short-circuit if nothing to process

2. BUILD CANDIDATE SET
   SELECT INTO #Candidates:
     v.SourceId, v.CompanyID, v.PiXLID, v.DeviceId, v.IpId, v.ReceivedAt,
     v.MatchEmail (from persisted computed column),
     NormalizedEmail = dbo.clr_NormalizeEmail(v.MatchEmail),
     ip.IPAddress (from PiXL.IP join)
   FROM PiXL.Visit v
   LEFT JOIN PiXL.IP ip ON v.IpId = ip.IpId
   WHERE v.SourceId > @LastId AND v.SourceId <= @MaxId
     AND v.MatchEmail IS NOT NULL
     AND LEN(v.MatchEmail) > 5          -- basic sanity (a@b.c minimum)
     AND v.MatchEmail LIKE '%_@_%.__%'  -- must look like an email

3. RESOLVE AGAINST AUTOCONSUMER — EMAIL MATCH
   UPDATE #Candidates SET
     IndividualKey = ac.IndividualKey,
     AddressKey = ac.AddressKey,
     AC_IP = ac.IP_Clean,
     AC_VPN = ac.VPN_Flag
   FROM #Candidates c
   CROSS APPLY (
     SELECT TOP 1 IndividualKey, AddressKey, IP_Clean, VPN_Flag
     FROM AutoUpdate.dbo.AutoConsumer ac
     WHERE ac.EMail = c.NormalizedEmail
       AND ac.VPN_Flag IS NULL           -- exclude VPN-flagged records
     ORDER BY ac.RecordID DESC           -- most recent record wins
   ) ac;

   Note: CROSS APPLY + TOP 1 + ORDER BY RecordID DESC is the most efficient 
   pattern for "get the best match." It leverages the new IX_AutoConsumer_EMail 
   index with a seek + 1 row fetch per email.

   We retrieve IndividualKey (the person identity) and AddressKey (the 
   household identity) — NOT RecordID. IndividualKey groups all AC records 
   for the same person across duplicate email/VIN entries (~1.22 rows per key).

4. MERGE INTO PiXL.Match
   Using #Candidates as the source:

   MERGE PiXL.Match AS target
   USING (
     SELECT CompanyID, PiXLID, 'email' AS MatchType,
            NormalizedEmail AS MatchKey,
            IndividualKey, AddressKey,
            DeviceId, IpId,
            SourceId, ReceivedAt
     FROM #Candidates
     WHERE NormalizedEmail IS NOT NULL
   ) AS source
   ON target.CompanyID = source.CompanyID
      AND target.PiXLID = source.PiXLID
      AND target.MatchType = source.MatchType
      AND target.MatchKey = source.MatchKey
   
   WHEN MATCHED THEN UPDATE SET
     LatestSourceId = source.SourceId,
     LastSeen = source.ReceivedAt,
     HitCount = target.HitCount + 1,
     -- Only update IndividualKey if we now have a match and didn't before
     IndividualKey = COALESCE(target.IndividualKey, source.IndividualKey),
     AddressKey = COALESCE(target.AddressKey, source.AddressKey),
     MatchedAt = CASE 
       WHEN target.IndividualKey IS NULL AND source.IndividualKey IS NOT NULL 
       THEN SYSUTCDATETIME() 
       ELSE target.MatchedAt 
     END
   
   WHEN NOT MATCHED THEN INSERT (
     CompanyID, PiXLID, MatchType, MatchKey, IndividualKey, AddressKey,
     DeviceId, IpId,
     FirstSourceId, LatestSourceId, FirstSeen, LastSeen,
     HitCount, MatchedAt
   ) VALUES (
     source.CompanyID, source.PiXLID, source.MatchType, source.MatchKey,
     source.IndividualKey, source.AddressKey,
     source.DeviceId, source.IpId,
     source.SourceId, source.SourceId,
     source.ReceivedAt, source.ReceivedAt,
     1, CASE WHEN source.IndividualKey IS NOT NULL THEN SYSUTCDATETIME() END
   );

5. UPDATE WATERMARK
   UPDATE ETL.MatchWatermark SET 
     LastProcessedId = @MaxId,
     LastRunAt = SYSUTCDATETIME(),
     RowsProcessed = RowsProcessed + @TotalProcessed,
     RowsMatched = RowsMatched + @TotalMatched
   WHERE ProcessName = 'MatchVisits';

6. RETURN RESULTS
   SELECT @TotalProcessed AS RowsProcessed, 
          @TotalMatched AS RowsMatched,
          @LastId + 1 AS FromId, 
          @MaxId AS ToId;
```

**Why MERGE:** The upsert pattern (insert if new visitor, update if returning visitor) maps perfectly to MERGE. The clustered index on `(CompanyID, PiXLID, MatchType, MatchKey)` means the MERGE's join condition is a clustered index seek.

**Why `CROSS APPLY` for AutoConsumer lookup:** A regular JOIN could produce multiple matches per email (AutoConsumer has duplicates — some emails appear multiple times with different RecordIDs across email/VIN vectors). `CROSS APPLY` + `TOP 1 ORDER BY RecordID DESC` guarantees exactly one match per email and picks the most recent consumer record. The `IndividualKey` from that record groups all of that person's AC entries.

**Why the `COALESCE` on `IndividualKey` update:** If a visitor emails us twice, and AutoConsumer didn't have their email the first time (new DBA load happened between visits), we want the second visit to populate the match without overwriting an existing good match.

**Why DeviceId/IpId from PiXL.Visit:** The match proc captures the device and IP from the **triggering visit** (via `PiXL.Visit.DeviceId` and `PiXL.Visit.IpId`). For new matches, these are the device/IP at first observation. For existing matches, these aren't updated (we keep the original observation context).

---

### Phase 7: C# — PiXL Config Cache Service

**File:** `TrackingPixel.Modern/Services/PiXLConfigCacheService.cs`  
**Model:** `TrackingPixel.Modern/Models/PiXLConfig.cs`

This service loads PiXL configuration from the database into memory so the JS script endpoint can read client params without hitting the database on every request.

#### Step 7.1: Create `PiXLConfig` Model

```
File: Models/PiXLConfig.cs

Properties:
  int CompanyId
  int PiXLId
  string PiXLName
  string? PiXLDomain
  bool IsActive
  string[] ClientParams           (parsed from comma-separated string)
  string ClientParamsJsLiteral    (pre-rendered JS array: "'email','hid','q_id'")
```

`ClientParamsJsLiteral` is computed once at load time: take the comma-separated `ClientParams` column value, split on `,`, trim each, wrap in single quotes, join with `,`. This avoids string manipulation on every JS request.

#### Step 7.2: Create `PiXLConfigCacheService`

```
File: Services/PiXLConfigCacheService.cs

Class: PiXLConfigCacheService : IHostedService, IDisposable

Private state:
  ConcurrentDictionary<(string CompanyId, string PiXLId), PiXLConfig> _cache
  Timer _refreshTimer
  TrackingSettings _settings (for ConnectionString)
  ILogger _logger

Public methods:
  PiXLConfig? GetConfig(string companyId, string pixlId)
    → Dictionary lookup, returns null if not found
    → Lock-free, O(1)

  Task StartAsync(CancellationToken ct)
    → Calls LoadConfigAsync() to populate cache on startup
    → Starts refresh timer at 5-minute intervals

  Task StopAsync(CancellationToken ct)
    → Disposes timer

Private methods:
  Task LoadConfigAsync()
    → Opens SqlConnection
    → SELECT CompanyId, PiXLId, PiXLName, PiXLDomain, IsActive, ClientParams 
      FROM PiXL.Pixel WHERE IsActive = 1
    → For each row: parse ClientParams, build PiXLConfig, add to dictionary
    → Atomic swap: build new dictionary, then replace reference
    → Log count of loaded configs
```

**Why a timer instead of IMemoryCache:** The PiXL table is tiny (5,610 rows on the old platform, likely <100 on ours for a long time). Loading all of it every 5 minutes is essentially free. A timer-based refresh is simpler than cache eviction policies and guarantees freshness within a known window.

**Why ConcurrentDictionary:** The refresh runs on a timer callback thread while the JS endpoint reads from N Kestrel worker threads concurrently. ConcurrentDictionary handles this safely without explicit locks.

**Startup behavior:** If the database is unavailable at startup, log a warning and start with an empty cache. The refresh timer will populate it when the DB comes back. This prevents the entire service from failing to start due to a transient DB issue.

---

### Phase 8: C# — JS Script Client Param Support

**Files modified:**
- `TrackingPixel.Modern/Scripts/PiXLScript.cs`
- `TrackingPixel.Modern/Endpoints/TrackingEndpoints.cs`

This phase wires the PiXL config cache into the JS script generation so client-specific params are extracted from the host page.

#### Step 8.1: Modify `PiXLScript.cs`

**Add a second placeholder to the JS template:**

Currently, the template has one placeholder: `{{PIXEL_URL}}`. We add `{{CLIENT_PARAMS}}`.

**Insert new JS block** before the `sendPixel` function (currently at line 1395):

```javascript
// Client-specific parameters from host page URL
var cpKeys = [{{CLIENT_PARAMS}}];
if (cpKeys.length > 0) {
    try {
        var sp = new URLSearchParams(window.location.search);
        cpKeys.forEach(function(k) {
            var v = sp.get(k);
            if (v !== null && v !== '') {
                data['_cp_' + k] = v;
            }
        });
    } catch(e) { /* URLSearchParams not supported in ancient browsers — fail silently */ }
}
```

**What `{{CLIENT_PARAMS}}` renders to:**
- For PiXL 12800/1: `'email','hid','q_id','id','answer','guess','difficulty','decade','category_id','sub_category_id','datatype'`
- For a PiXL with no client params: empty string (resulting in `var cpKeys = [];` — zero overhead)

**The `_cp_` prefix** is critical. It namespaces client params to avoid collisions with our 158 built-in fingerprint params. The ETL Phase 9 scans for this prefix to identify client params.

#### Step 8.2: Update `GetScript` to Accept Client Params

**Current signature:** `public static string GetScript(string pixelUrl)`  
**New signature:** `public static string GetScript(string pixelUrl, string clientParamsJs = "")`

**Cache key change:** Currently caches on `pixelUrl` alone. Change to cache on `(pixelUrl, clientParamsJs)` — either by composing a string key like `$"{pixelUrl}|{clientParamsJs}"` or using a `ValueTuple`.

**Implementation:**
```csharp
public static string GetScript(string pixelUrl, string clientParamsJs = "") =>
    _cache.GetOrAdd(
        $"{pixelUrl}|{clientParamsJs}",
        _ => Template
            .Replace("{{PIXEL_URL}}", pixelUrl)
            .Replace("{{CLIENT_PARAMS}}", clientParamsJs));
```

The cache still does its job — the `Replace` runs exactly once per unique (URL, params) combination. For a single PiXL like 12800/1, it runs once and is cached forever.

#### Step 8.3: Update JS Endpoint in `TrackingEndpoints.cs`

**Current code (lines ~145-166 of TrackingEndpoints.cs):**
```csharp
var pixelUrl = $"{baseUrl}/{companyId}/{pixlId}_SMART.GIF";
var javascript = PiXLScript.GetScript(pixelUrl);
```

**New code:**
```csharp
var pixelUrl = $"{baseUrl}/{companyId}/{pixlId}_SMART.GIF";
var config = configCache.GetConfig(companyId, pixlId);
var clientParamsJs = config?.ClientParamsJsLiteral ?? "";
var javascript = PiXLScript.GetScript(pixelUrl, clientParamsJs);
```

Where `configCache` is the `PiXLConfigCacheService` injected into the endpoint handler.

**Fallback behavior:** If `GetConfig` returns null (unknown PiXL, or cache not yet loaded), `clientParamsJs` defaults to empty string → the script works exactly as it does today with no client param extraction. Zero breaking change for existing pixels.

#### Step 8.4: Inject `PiXLConfigCacheService` Into Endpoint Registration

The `MapTrackingEndpoints` extension method needs access to the cache service. In the current code, services are captured via closure from the `app` builder. We add `PiXLConfigCacheService` to that pattern:

```csharp
var configCache = app.Services.GetRequiredService<PiXLConfigCacheService>();
```

This is a one-line addition at the top of the `MapTrackingEndpoints` method, alongside the existing service captures (`captureService`, `writerService`, etc.).

---

### Phase 9: C# — Match Background Service

**File:** `TrackingPixel.Modern/Services/MatchBackgroundService.cs`

This is the C# counterpart to the match stored procedure. It follows the exact same pattern as `EtlBackgroundService` — a `BackgroundService` loop that calls a stored proc on a timer.

#### Step 9.1: Create `MatchBackgroundService`

```
Class: MatchBackgroundService : BackgroundService

Constructor dependencies:
  IOptions<TrackingSettings> settings
  ILogger<MatchBackgroundService> logger

ExecuteAsync loop:
  1. Startup delay: 15 seconds (gives ETL time to parse first batch)
  2. Loop:
     a. Call RunMatchCycleAsync()
     b. Log results if rows matched > 0
     c. Wait MatchIntervalSeconds (default 120)
  3. Error handling: log and retry next interval (same as EtlBackgroundService)

RunMatchCycleAsync:
  1. Open SqlConnection (fresh per cycle)
  2. Execute ETL.usp_MatchVisits with @BatchSize from settings
  3. Read result set: RowsProcessed, RowsMatched, FromId, ToId
  4. Return (RowsProcessed, RowsMatched)
```

**Why 15-second startup delay:** The ETL service starts after 5 seconds and needs at least one cycle (up to 60s) to have parsed data in PiXL.Parsed and PiXL.Visit for the match proc to read. 15 seconds is enough for the ETL to complete its first cycle on the initial 963 existing rows.

**Why 120-second interval (not 60s):** The match proc does cross-database joins against AutoConsumer (421M rows). Running every 120s gives the ETL two full cycles to accumulate candidates, resulting in larger but less frequent batches — more efficient use of the AC index.

**Why separate from EtlBackgroundService:** The match proc reads from PiXL.Visit (output of ETL) using its own watermark. Running them independently means:
- ETL can fall behind without blocking matching
- Match can be restarted or re-watermarked without affecting ingest
- Different batch sizes optimize each workload: ETL processes 50K raw rows, match processes 10K visit rows (since match does cross-DB joins per row, smaller batches keep it responsive)

---

### Phase 10: Configuration & Wiring

**Files modified:**
- `TrackingPixel.Modern/Configuration/TrackingSettings.cs`
- `TrackingPixel.Modern/appsettings.json`
- `TrackingPixel.Modern/Program.cs`

#### Step 10.1: Extend `TrackingSettings`

Add to the `TrackingSettings` class:

```
int MatchIntervalSeconds = 120        (how often the match service runs)
int MatchBatchSize = 10000            (rows per match cycle)
string AutoUpdateConnectionString     (connection string for AutoUpdate DB — if different from SmartPixl)
```

**Note on `AutoUpdateConnectionString`:** Since AutoUpdate is on the same SQL Server instance as SmartPixl, the match proc uses cross-database queries (`AutoUpdate.dbo.AutoConsumer`). The match proc connects to SmartPixl and reaches AutoUpdate via three-part naming. So we may not need a separate connection string. But having the option is future-proof for when AutoConsumer might move to a different server.

#### Step 10.2: Update `appsettings.json`

Add under the `"Tracking"` section:

```json
"MatchIntervalSeconds": 120,
"MatchBatchSize": 10000
```

#### Step 10.3: Update `Program.cs` Service Registration

Add after the existing `EtlBackgroundService` registration:

```csharp
// PiXL configuration cache — in-memory, refreshes every 5 minutes
builder.Services.AddSingleton<PiXLConfigCacheService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PiXLConfigCacheService>());

// Identity resolution — matches visit data against AutoConsumer
builder.Services.AddHostedService<MatchBackgroundService>();
```

**Registration order matters:** `PiXLConfigCacheService` must be registered before it's used in the endpoint. The `AddSingleton` + `AddHostedService` pattern (same as `DatabaseWriterService`) ensures it's both injectable as a dependency and runs its `StartAsync` during app startup.

---

### Phase 11: Data Seeding & Deployment

**Migration script:** `SQL/25_SeedClient12800.sql`

#### Step 11.1: Seed Company 12800

```sql
INSERT INTO PiXL.Company (CompanyID, CompanyName, ContactName, IsActive, Notes)
VALUES (12800, 'The Trivia Quest', NULL, 1, 'First MVP client — email matching via host page URL params');
```

#### Step 11.2: Seed PiXL 12800/1

```sql
INSERT INTO PiXL.Pixel (CompanyId, PiXLId, PiXLName, PiXLURL, PiXLDomain, IsActive, ClientParams, Notes)
VALUES (
    12800, 1, 
    'Trivia Quest Main',
    'https://www.thetriviaquest.com',
    'thetriviaquest.com',
    1,
    'email,hid,q_id,id,answer,guess,difficulty,decade,category_id,sub_category_id,datatype',
    'Client sends email and quiz metadata via URL params'
);
```

#### Step 11.3: Seed Match Watermark

```sql
INSERT INTO ETL.MatchWatermark (ProcessName, LastProcessedId, LastRunAt, RowsProcessed, RowsMatched)
VALUES ('MatchVisits', 0, NULL, 0, 0);
```

**Note:** Setting `LastProcessedId = 0` means the match proc will scan ALL existing PiXL.Visit rows on first run. Since the existing 963 rows don't have emails (no `_cp_email` in their query strings), this processes quickly and finds nothing to match. Once live traffic flows with `_cp_email` params, the watermark advances normally.

#### Step 11.4: Deployment Sequence

The deployment order for all SQL scripts:

```
0. SQL/17B_CreateSchemas.sql            — CREATE SCHEMA PiXL/ETL, ALTER SCHEMA TRANSFER, sp_rename, ALTER PROCEDURE/VIEW fixups
1. SQL/18_CompanyAndPiXLTables.sql     — PiXL.Company, PiXL.Pixel tables
2. SQL/19_DeviceIpVisitMatchTables.sql — PiXL.Device, PiXL.IP, PiXL.Visit, PiXL.Match, ETL.MatchWatermark
3. SQL/20_ClientParamsSupport.sql       — (verification only — columns now on PiXL.Visit)
4. SQL/21_ParseNewHits_Phases9to13.sql  — Phases 9-13 in ETL.usp_ParseNewHits (Device, IP, ClientParams, Visit) + CLR swap
5. SQL/22_AutoConsumerEmailIndex.sql    — IX_AutoConsumer_EMail (run during maintenance)
6. SQL/23_MatchVisits.sql               — ETL.usp_MatchVisits
7. SQL/24_ClrFunctions.sql              — SmartPixl.Clr assembly + 3 functions
8. SQL/25_SeedClient12800.sql           — Company/PiXL/Watermark seed data
```

**Note:** Migration 24 (CLR) can technically be deployed before migration 21, since Phase 4.6 swaps to CLR functions. The CLR assembly just needs to exist before `ETL.usp_ParseNewHits` is altered to reference `clr_GetQueryParam`. If CLR is blocked for any reason, migration 21 can use the existing T-SQL `GetQueryParam` UDF.

**Then deploy the C# changes:**

```
1. Build and publish TrackingPixel.Modern
2. Stop the SmartPiXL Windows Service
3. Deploy the new binaries
4. Start the SmartPiXL Windows Service
5. Verify logs show all services starting:
   - "ETL background service started"
   - "Match background service started"  (NEW)
   - "PiXL config cache loaded: 1 configs" (NEW)
```

#### Step 11.5: Smoke Test After Deployment

```
1. GET https://smartpixl.info/js/12800/1.js
   → Verify response contains: var cpKeys = ['email','hid','q_id',...];

2. Open https://www.thetriviaquest.com/question/1?email=test@example.com
   → Verify PiXL.Test gets a new row with _cp_email in QueryString

3. Wait 60 seconds (ETL cycle)
   → Verify PiXL.Parsed has the row
   → Verify PiXL.Device has a row with the device's fingerprint hash
   → Verify PiXL.IP has a row with the visitor's IP
   → Verify PiXL.Visit has a row with DeviceId, IpId, and ClientParamsJson populated

4. Wait 120 more seconds (Match cycle)
   → Verify PiXL.Match has a row with MatchType='email'
   → Check IndividualKey: populated if test@example.com exists in AutoConsumer, NULL if not

5. Test with a known AutoConsumer email:
   → Verify PiXL.Match.IndividualKey is populated (varchar(35), starts with 'IND')
   → Verify PiXL.Match.AddressKey is populated (varchar(35), starts with 'ADD')
   → Verify PiXL.Match.DeviceId and IpId are populated (FK back to dimension tables)

6. Verify relational integrity:
   → SELECT v.*, d.DeviceHash, ip.IPAddress, m.IndividualKey
     FROM PiXL.Visit v
     LEFT JOIN PiXL.Device d ON v.DeviceId = d.DeviceId
     LEFT JOIN PiXL.IP ip ON v.IpId = ip.IpId
     LEFT JOIN PiXL.Match m ON v.CompanyID = m.CompanyID 
       AND v.PiXLID = m.PiXLID AND m.MatchType = 'email'
   → All FKs should resolve, no orphans
```

---

## 5. Verification & Testing

### Unit Tests (C#)

| Test Class | Tests |
|------------|-------|
| `PiXLConfigCacheServiceTests` | Cache loads from DB, returns correct config, returns null for unknown PiXL, handles empty ClientParams, handles DB unavailable at startup, refresh updates cache, concurrent reads are safe |
| `PiXLScriptTests` (extend) | Script with client params contains `cpKeys` array, script without client params has empty array, cache works with different param sets, special chars in param names |
| `MatchBackgroundServiceTests` | Startup delay, interval timing (120s), error recovery, cancellation |

### Integration Tests (SQL)

| Test | Query | Expected |
|------|-------|----------|
| CLR UrlDecode | `SELECT dbo.clr_UrlDecode('bpryce6%40gmail.com')` | `bpryce6@gmail.com` |
| CLR UrlDecode UTF-8 | `SELECT dbo.clr_UrlDecode('%C3%A9')` | `é` |
| CLR UrlDecode plus | `SELECT dbo.clr_UrlDecode('Tool+Time')` | `Tool Time` |
| CLR GetQueryParam | `SELECT dbo.clr_GetQueryParam('a=1&email=test%40x.com&b=2', 'email')` | `test@x.com` |
| CLR GetQueryParam missing | `SELECT dbo.clr_GetQueryParam('a=1&b=2', 'email')` | `NULL` |
| CLR GetQueryParam partial match | `SELECT dbo.clr_GetQueryParam('remail=no&email=yes', 'email')` | `yes` |
| CLR NormalizeEmail basic | `SELECT dbo.clr_NormalizeEmail('  BPryce6@Gmail.COM  ')` | `bpryce6@gmail.com` |
| CLR NormalizeEmail plus | `SELECT dbo.clr_NormalizeEmail('user+tag@gmail.com')` | `user@gmail.com` |
| CLR NormalizeEmail dots | `SELECT dbo.clr_NormalizeEmail('b.pryce.6@gmail.com')` | `bpryce6@gmail.com` |
| CLR NormalizeEmail non-gmail | `SELECT dbo.clr_NormalizeEmail('USER@Yahoo.COM')` | `user@yahoo.com` |
| AutoConsumer index seek | `SET STATISTICS IO ON; SELECT TOP 1 IndividualKey FROM AutoUpdate.dbo.AutoConsumer WHERE EMail = 'test@test.com'` | Verify "Index Seek" in plan, not "Table Scan" |
| Device hash computation | `SELECT HASHBYTES('SHA2_256', CONCAT_WS('\|', 'canvas1', 'fonts1', 'gpu1', 'webgl1', 'audio1'))` | Returns 32-byte varbinary |
| PiXL.Device MERGE | Insert row with known hash, run Phase 10 again with same hash | HitCount increments, no duplicate |
| PiXL.IP MERGE | Insert row with known IP, run Phase 11 again with same IP | HitCount increments, no duplicate |
| PiXL.Visit insert | Run Phases 9-13 on test batch | PiXL.Visit row count = PiXL.Parsed batch count, all DeviceId/IpId populated |
| Match proc empty | `EXEC ETL.usp_MatchVisits @BatchSize = 100` | Returns 0 rows processed (watermark catches up to existing data) |
| Full pipeline | Insert test row into PiXL.Test with `_cp_email=known@email.com`, run ETL, run Match | PiXL.Visit row with ClientParamsJson, PiXL.Match row with IndividualKey populated |

### Performance Benchmarks

| Benchmark | Method | Target |
|-----------|--------|--------|
| CLR vs T-SQL GetQueryParam | Run ETL.usp_ParseNewHits on 1000 rows with each, compare elapsed time | CLR should be ≥5x faster |
| AutoConsumer email lookup | `SELECT IndividualKey FROM AutoConsumer WHERE EMail = @email` with index | < 1ms per lookup (index seek) |
| Match proc throughput | Process 10K rows with mix of matchable/unmatchable emails | < 10 seconds per batch |
| Device MERGE throughput | MERGE 50K rows into PiXL.Device (mix of new/existing) | < 2 seconds |
| IP MERGE throughput | MERGE 50K rows into PiXL.IP (mix of new/existing) | < 2 seconds |
| JS endpoint latency | `curl` with timing for `/js/12800/1.js` | < 5ms (cached script generation) |

---

## 6. Design Decisions & Rationale

### Decision 1: `_cp_` Prefix Convention

**Chose:** Prefix client params with `_cp_` in the query string  
**Over:** Separate query string, separate header, POST body

**Why:** The pixel fires as a `GET` request via `new Image().src = url`. There's no way to add headers or a POST body to an Image request. Everything must go in the URL. The `_cp_` prefix is a clean namespace separator that:
- Can't collide with any existing fingerprint param (they use short names like `sw`, `sh`, `canvasFP`)
- Is self-documenting in the raw data
- Can be efficiently scanned in SQL (`WHERE QueryString LIKE '%_cp_%'`)

### Decision 2: JSON for Client Params Storage

**Chose:** Single `ClientParamsJson NVARCHAR(4000)` column with JSON  
**Over:** Individual typed columns per param, or EAV (entity-attribute-value) table

**Why:**
- **vs. individual columns:** Each client sends different params. Adding columns per client pollutes the schema and leaves NULL gaps. JSON is self-describing and query-able via `JSON_VALUE()`.
- **vs. EAV table:** An EAV child table would multiply row counts (11 params per hit = 11× more rows) and require pivot operations for queries. JSON keeps the data with the row.
- **The hot-path email value** is extracted via a persisted computed column (`MatchEmail`) with a filtered index, so we get the best of both worlds: flexible storage + fast indexed lookups.

### Decision 3: Persisted Computed Column for MatchEmail

**Chose:** `MatchEmail AS CAST(JSON_VALUE(ClientParamsJson, '$.email') AS NVARCHAR(200)) PERSISTED` on `PiXL.Visit`  
**Over:** Manual population in the ETL proc, or non-persisted computed column, or column on PiXL.Parsed

**Why:**
- **vs. manual:** A computed column self-maintains. No ETL logic to update it. If we update `ClientParamsJson` (e.g., to fix a value), `MatchEmail` auto-updates.
- **vs. non-persisted:** Non-persisted means `JSON_VALUE()` runs on every read. Persisted means the value is physically stored and can be indexed. Critical for the match proc's watermark scan.
- **vs. on PiXL.Parsed:** `PiXL.Parsed` is the immutable 175-column warehouse for fingerprint data. Client params are relational metadata that belong on the fact table (`PiXL.Visit`), where they sit alongside `DeviceId`, `IpId`, and other relational FKs.

### Decision 4: Separate Match Watermark Table

**Chose:** `ETL.MatchWatermark` as a separate table from `ETL.Watermark`  
**Over:** Adding a second row in `ETL.Watermark`

**Why:** Zero contention. The ETL and match services update their watermarks independently in their own transactions. If they shared a table, a long-running match transaction could block the ETL watermark update (or vice versa). Separate tables mean separate page locks — zero interference.

### Decision 5: MERGE for PiXL.Match Upserts

**Chose:** SQL `MERGE` statement  
**Over:** `IF EXISTS / UPDATE / ELSE INSERT`, or separate `INSERT ... WHERE NOT EXISTS` + `UPDATE`

**Why:** `MERGE` is atomic — the check-and-act is a single statement with proper isolation. The `IF EXISTS` pattern has a race condition under concurrency (two match cycles could try to insert the same email simultaneously). `MERGE` handles this natively.

**Caveat:** MERGE has historically had some edge-case bugs in older SQL Server versions. SQL Server 2019 CU12+ has all known fixes. We're on a recent GDR build (KB5068405), which includes these.

### Decision 6: Lean Company/PiXL Tables

**Chose:** Purpose-built minimal schemas  
**Over:** Direct copies of Xavier's 38-col Company / 46-col PiXL schemas

**Why:** The old schemas carry years of accumulated fields for a frontend, billing system, and workflow engine that this platform doesn't have yet. Copying them creates ghost columns that confuse future developers, invite misuse, and add cognitive load. The lean schemas have exactly what the ETL pipeline needs today. When the frontend is built, columns are added deliberately and documented.

### Decision 7: No Staging Tables

**Chose:** Direct ingest into PiXL.Test via async SqlBulkCopy  
**Over:** Old platform's 4-table staging rotation

**Why:** The staging rotation existed because the old IIS handler did a blocking synchronous INSERT into whichever staging table was "active." To prevent read-write contention, it rotated between 4 tables on a 5-minute timer.

Our architecture is fundamentally different:
- The pixel endpoint is non-blocking (fires-and-forgets into a `Channel<T>`)
- `DatabaseWriterService` batches 100 records and uses `SqlBulkCopy` with minimal locking
- `PiXL.Test` doesn't have a clustered columnstore or any read-heavy workload competing with inserts
- The ETL reads by Id range (watermark-based), so it never conflicts with SqlBulkCopy appends

### Decision 8: CLR Assembly Permission Level

**Chose:** Start with `SAFE`, escalate to `UNSAFE` only if needed  
**Over:** Default to `UNSAFE`

**Why:** `SAFE` allows most managed code. `UNSAFE` is only needed if the compiler emits calls to `System.Runtime.CompilerServices.Unsafe` (common when using `Span<T>` on .NET Framework 4.8). We'll attempt a `SAFE` build first. If it fails with a host protection error, we escalate to `UNSAFE` — but secured via asymmetric key signing, not the dangerous `TRUSTWORTHY` database flag.

### Decision 9: Normalized Star Schema (4 New Tables)

**Chose:** Separate `PiXL.Device`, `PiXL.IP`, `PiXL.Visit`, `PiXL.Match` tables with proper FKs  
**Over:** Flat `PiXL.Match` with denormalized device/IP data inline (v1 plan)

**Why:** Maximum normalization means:
- **PiXL.Device is platform-wide** — the same physical device across multiple clients shares one row. Enables cross-client device intelligence without duplicating data.
- **PiXL.IP is platform-wide** — same rationale. One IP row regardless of which pixel saw it.
- **PiXL.Visit bridges everything** — 1:1 with PiXL.Parsed (same PK), carries DeviceId/IpId FKs + ClientParamsJson. Separates relational concerns from the wide fingerprint warehouse.
- **PiXL.Match has proper FKs** — instead of denormalizing IP address as a VARCHAR, it has `IpId` FK to `PiXL.IP` and `DeviceId` FK to `PiXL.Device`. Joins are on indexed integers, not string comparisons.
- **Evolves independently** — adding metadata to PiXL.IP (e.g., geo data, ISP) doesn't touch any other table. Adding signals to PiXL.Device (e.g., stability score) is isolated.

### Decision 10: IndividualKey/AddressKey Instead of RecordID

**Chose:** Store `IndividualKey` (varchar(35)) and `AddressKey` (varchar(35)) from AutoConsumer  
**Over:** Store `RecordID` (int) as in the v1 plan

**Why:** AutoConsumer is denormalized — one person can have multiple RecordIDs (one for each email vector, one for each VIN vector, etc.). `RecordID` is arbitrary and unstable across monthly DBA reloads. `IndividualKey` is the stable person-level identity that groups all AC records for the same individual (~1.22 AC rows per IndividualKey on average, 343M distinct across 421M rows). `AddressKey` groups people at the same household (~3.56 rows per key, 118M distinct). Using these keys means:
- Email match → `IndividualKey` identifies the person regardless of which AC record had their email
- IP match → `IndividualKey` identifies the person at that IP
- Geo match → `AddressKey` identifies the household
- Downstream joins to AC can use `IndividualKey` to pull the full record set for a person

### Decision 11: Lean PiXL.Device (Hash + Metadata Only)

**Chose:** 5-column device table (DeviceId, DeviceHash, FirstSeen, LastSeen, HitCount)  
**Over:** Full component snapshot (Canvas, WebGL, Audio, GPU, Fonts, Screen, Platform, etc.)

**Why:** The component fingerprint fields already exist in `PiXL.Parsed` (175 columns). Duplicating them into `PiXL.Device` would be redundant and require complex UPDATE logic when a device "evolves" (e.g., browser update changes WebGL fingerprint). The lean approach means:
- `PiXL.Device` is tiny and fast to MERGE (5 columns, clustered on 32-byte hash)
- To see component fields, join through `PiXL.Visit → PiXL.Parsed` (one hop)
- Hash collisions can be debugged by comparing component values across PiXL.Parsed rows for the same DeviceId

### Decision 12: Device Hash Computed in SQL (Not C#)

**Chose:** `HASHBYTES('SHA2_256', CONCAT_WS('|', Canvas, Fonts, GPU, WebGL, Audio))` in Phase 9 of `ETL.usp_ParseNewHits`  
**Over:** New C# `MatchBackgroundService` computing hash and doing device upsert

**Why:** Keeps all ETL logic in one place. The hash computation runs on the same batch temp table that Phase 8 already populated — no additional database round-trip. `HASHBYTES` is highly optimized in SQL Server 2019 Enterprise (hardware-accelerated SHA-256). The device MERGE in Phase 10 can use the hash directly from the temp table.

### Decision 13: Global (Platform-Wide) Device and IP Tables

**Chose:** One `PiXL.Device` row per unique device hash, one `PiXL.IP` row per unique IP address, regardless of company  
**Over:** Per-company device/IP tables (CompanyID in the PK)

**Why:**
- **Cross-client intelligence:** If the same device appears on two different clients' pixels, we see it. Useful for fraud detection and audience overlap analysis.
- **More normalized:** A device is a physical thing — it doesn't belong to a company. An IP is a network address — it doesn't belong to a company.
- **Smaller tables:** De-duplication means fewer rows. A bot that hits 10 different pixels is one device row with HitCount=10, not 10 rows.
- **The company relationship** is captured in `PiXL.Visit` (which has CompanyID) and `PiXL.Match` (which has CompanyID). To find "all devices seen by Company X," join `PiXL.Visit` on CompanyID and DeviceId.

### Decision 14: PiXL.Visit as PK=SourceId (Not New IDENTITY)

**Chose:** `PiXL.Visit.SourceId` = `PiXL.Parsed.SourceId` = `PiXL.Test.Id` — reuse the existing chain  
**Over:** New BIGINT IDENTITY column on PiXL.Visit

**Why:** There's no reason to create a new surrogate key for a 1:1 relationship. Using the same `SourceId` means:
- Joining PiXL.Visit to PiXL.Parsed is trivial (same PK)
- No ambiguity about which visit corresponds to which parsed row
- FKs from PiXL.Match (`FirstSourceId`, `LatestSourceId`) point to both tables interchangeably
- One less IDENTITY to manage

---

## 7. Future Considerations

These are explicitly **not** in scope for this MVP but the design accommodates them:

### 7.1 Fingerprint-Based Visitor Identity

The MVP already computes a composite device hash and populates `PiXL.Device`. The next step is using `PiXL.Device` as a match vector:
- `MatchType = 'fingerprint'`
- `MatchKey = CONVERT(VARCHAR(64), DeviceHash, 2)` (hex string of the 32-byte hash)
- New logic in `ETL.usp_MatchVisits`: For visits WITHOUT email, find previous visits from the same `DeviceId` that DO have a resolved `IndividualKey`, and propagate the identity
- No new proc needed — extend `ETL.usp_MatchVisits` with a fingerprint-matching phase
- The `PiXL.Match` table's `(MatchType, MatchKey)` design handles this with zero schema changes
- `PiXL.Match.DeviceId` FK already captures the device relationship

### 7.2 IP + Geo Matching

`PiXL.IP` already exists as a global dimension table. The next step is IP-based and geo-based matching:
- **IP match:** `MatchType = 'ip'`, `MatchKey = <ip_address>`. Join `PiXL.IP.IPAddress` to `AutoConsumer.IP_Clean` (index already exists on AC). Get `IndividualKey`.
- **Geo match:** `MatchType = 'geo'`, `MatchKey = <zip_code>`. Use `PiXL.Zipcode` + `PiXL.Radius` to find AutoConsumer records by `AddressKey` within the geo radius. Requires adding geo data (lat/lon or spatial index) to `PiXL.IP` or a separate geo lookup.
- Both populate `PiXL.Match` with `IndividualKey` (for IP) or `AddressKey` (for geo) — the schema already supports this.

### 7.3 Cross-Device Linking

The normalized schema enables cross-device linking natively:
- `PiXL.Visit` has both `DeviceId` FK and `IpId` FK, plus `MatchEmail`
- When the same `IndividualKey` appears in `PiXL.Match` from different `DeviceId` values → those are the same person on different devices
- When the same `DeviceId` appears from different `IpId` values → that device moved between networks
- Query example: `SELECT DISTINCT m1.DeviceId, m2.DeviceId FROM PiXL.Match m1 JOIN PiXL.Match m2 ON m1.IndividualKey = m2.IndividualKey WHERE m1.DeviceId <> m2.DeviceId` → device graph for people with multiple devices

### 7.4 Date Partitioning

When `PiXL.Parsed` grows beyond ~10M rows, add monthly partitioning on `ReceivedAt`:
- Partition function: monthly boundaries
- Partition scheme: map months to filegroups
- The existing clustered index `CIX_PiXL_Parsed_ReceivedAt` becomes partition-aligned
- Old data can be switched out to archive filegroups

### 7.5 Client Delivery

When the boss decides what the client receives:
- **File export:** SQL Agent job runs nightly, queries `PiXL.Match JOIN AutoConsumer` for new matches, exports to CSV
- **API:** New endpoint in TrackingEndpoints: `GET /api/{companyId}/matches?since=<datetime>`
- **Dashboard:** New views in the Diagnostics project reading from PiXL.Match
- **Webhook:** New background service that POST's new matches to a client-configured URL

### 7.6 CLR Function Expansion

The SQLCLR assembly can grow to include:
- `clr_ComputeFingerprintHash` — SHA-256 of a composite fingerprint string (faster than T-SQL HASHBYTES)
- `clr_ParseUserAgent` — Structured extraction of browser/OS/version from UA strings
- `clr_IpToLong` — Efficient IP-to-integer conversion for range comparisons
- `clr_GeoDistance` — Haversine formula for lat/lon distance (replaces spatial index for simple radius checks)

### 7.7 Real-Time Matching

Instead of the 60-second batch interval, the match could be triggered inline during ETL. This would require careful design to avoid slowing the parse pipeline, but would reduce match latency from ~120s (60s ETL + 60s match) to ~60s.

---

## 8. Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| AutoConsumer email index build takes too long or fills tempdb | Medium | High — blocks match functionality | Check tempdb free space before build. Use `ONLINE = ON` + `SORT_IN_TEMPDB = ON`. Monitor during build. |
| CLR assembly fails to deploy (permission/compatibility) | Low | Medium — falls back to T-SQL UDF | Keep T-SQL `GetQueryParam` as fallback. Can deploy CLR later as a perf optimization. |
| `URLSearchParams` not available in ancient browsers | Low | Low — silent failure, fingerprints still collected | The `try/catch` around the client param extraction ensures graceful degradation. Only client params are lost. |
| MERGE statement deadlocks under high concurrency | Low | Medium — match cycle retries next interval | The match proc processes by SourceId range; concurrent runs won't touch the same MatchKey unless the same email appears in different batches. |
| AutoConsumer has dirty email data (duplicates, bad formats) | High | Medium — match quality degrades | `clr_NormalizeEmail` handles case, whitespace, Gmail aliases. `TOP 1 ORDER BY RecordID DESC` handles duplicates by picking most recent. IndividualKey groups duplicate AC records for the same person. |
| PiXL config cache serves stale data | Low | Low — client params won't be extracted until next refresh | 5-minute refresh interval is acceptable. For urgent changes, restart the service. |
| QueryString exceeds NVARCHAR(MAX) with many client params | Very Low | Low — SQL truncation | Each `_cp_*` param adds ~20-50 chars. Even 100 params would add ~5KB. QueryString is NVARCHAR(MAX). Not a concern. |
| Device hash collisions (different devices produce same hash) | Low | Low — match quality slightly reduced | 5 entropy signals at ~27 combined bits = very low collision rate at our scale. PiXL.Parsed has full component fields for collision analysis. Can add more signals to hash if needed. |
| Triple MERGE per ETL batch (Device + IP + Visit INSERT) adds latency | Medium | Low — ETL takes longer per cycle | Device and IP MERGEs are on unique clustered indexes (seek + point update). Visit is a simple INSERT. Total added time should be <2s per 50K batch. Monitor ETL cycle time after deployment. |
| IndividualKey/AddressKey change semantics in future AC reload | Low | Medium — orphaned keys in PiXL.Match | AC keys are maintained by the DBA team and are contractually stable. If they change, PiXL.Match rows would need a backfill pass. |
| Boss asks "where's the client delivery?" | High | Medium — scope creep | This plan explicitly scopes delivery as TBD. The pipeline produces correct data in `PiXL.Match`. Delivery is a separate workstream. |

---

## Appendix A: File Change Summary

### New Files

| File | Type | Description |
|------|------|-------------|
| `SQL/17B_CreateSchemas.sql` | SQL Migration | CREATE SCHEMA PiXL/ETL, transfer existing tables, rename, fix dependent objects |
| `SQL/18_CompanyAndPiXLTables.sql` | SQL Migration | PiXL.Company, PiXL.Pixel, ETL.MatchWatermark |
| `SQL/19_DeviceIpVisitMatchTables.sql` | SQL Migration | PiXL.Device, PiXL.IP, PiXL.Visit, PiXL.Match (normalized schema) |
| `SQL/20_ClientParamsSupport.sql` | SQL Migration | Verification-only — confirms PiXL.Visit.ClientParamsJson & MatchEmail exist |
| `SQL/21_ParseNewHits_Phases9to13.sql` | SQL Migration | Adds Phases 9-13 to ETL.usp_ParseNewHits (DeviceHash, MERGE Device, MERGE IP, Client Params, INSERT Visit) + CLR swap |
| `SQL/22_AutoConsumerEmailIndex.sql` | SQL Migration | IX_AutoConsumer_EMail (INCLUDE IndividualKey, AddressKey) |
| `SQL/23_MatchVisits.sql` | SQL Migration | ETL.usp_MatchVisits proc |
| `SQL/24_ClrFunctions.sql` | SQL Migration | SmartPixl.Clr assembly + 3 functions |
| `SQL/25_SeedClient12800.sql` | SQL Migration | Company/PiXL/Watermark seed data |
| `SmartPixl.Clr/SmartPixl.Clr.csproj` | C# Project | CLR assembly project |
| `SmartPixl.Clr/UrlFunctions.cs` | C# Class | clr_UrlDecode, clr_GetQueryParam, clr_NormalizeEmail |
| `SmartPixl.Clr/SmartPixl.Clr.snk` | Key File | Strong name signing key |
| `Models/PiXLConfig.cs` | C# Model | PiXL configuration record |
| `Services/PiXLConfigCacheService.cs` | C# Service | In-memory PiXL config cache |
| `Services/MatchBackgroundService.cs` | C# Service | Match proc background loop (120s interval) |

### Modified Files

| File | Changes |
|------|---------|
| `Scripts/PiXLScript.cs` | Add `{{CLIENT_PARAMS}}` placeholder, update `GetScript` signature/cache |
| `Endpoints/TrackingEndpoints.cs` | Inject `PiXLConfigCacheService`, pass client params to JS endpoint |
| `Configuration/TrackingSettings.cs` | Add `MatchIntervalSeconds`, `MatchBatchSize` |
| `appsettings.json` | Add match settings (120s interval) |
| `Program.cs` | Register `PiXLConfigCacheService`, `MatchBackgroundService` |

### Database Objects Created

| Object | Type | Database |
|--------|------|----------|
| `PiXL.Company` | Table | SmartPixl |
| `PiXL.Pixel` | Table | SmartPixl |
| `PiXL.Device` | Table (dimension) | SmartPixl |
| `PiXL.IP` | Table (dimension) | SmartPixl |
| `PiXL.Visit` | Table (fact, 1:1 with PiXL.Parsed) | SmartPixl |
| `PiXL.Match` | Table (identity resolution) | SmartPixl |
| `ETL.MatchWatermark` | Table | SmartPixl |
| `PiXL.Visit.ClientParamsJson` | Column | SmartPixl |
| `PiXL.Visit.MatchEmail` | Computed Column | SmartPixl |
| `IX_PiXL_Visit_MatchEmail` | Index | SmartPixl |
| `SmartPixl.Clr` | Assembly | SmartPixl |
| `dbo.clr_UrlDecode` | CLR Function | SmartPixl |
| `dbo.clr_GetQueryParam` | CLR Function | SmartPixl |
| `dbo.clr_NormalizeEmail` | CLR Function | SmartPixl |
| `ETL.usp_MatchVisits` | Stored Proc | SmartPixl |
| `IX_AutoConsumer_EMail` | Index (INCLUDE IndividualKey, AddressKey) | AutoUpdate |

---

## Appendix B: Sample End-to-End Data Flow

**Input:** User visits `https://www.thetriviaquest.com/question/1?hid=2602102158531672553&email=bpryce6%40gmail.com&q_id=RT2025030601518a07f3e4fa2d11e&answer=true&guess=Tool%20Time&difficulty=easy&decade=90s&category_id=4&sub_category_id=4.04&datatype=ret-openers`

**Step 1 — JS Script executes:**
The pixel script (served from `/js/12800/1.js`) runs on the page. It:
- Collects 158 fingerprint signals into `data = {}`
- Reads `ClientParams` config: `['email','hid','q_id','id','answer','guess','difficulty','decade','category_id','sub_category_id','datatype']`
- Extracts from `location.search`:
  - `data['_cp_email'] = 'bpryce6@gmail.com'` (browser auto-decodes the %40)
  - `data['_cp_hid'] = '2602102158531672553'`
  - `data['_cp_guess'] = 'Tool Time'` (browser auto-decodes %20)
  - ... etc for all configured params
- Fires: `GET https://smartpixl.info/12800/1_SMART.GIF?sw=1920&sh=1080&...&_cp_email=bpryce6%40gmail.com&_cp_hid=2602102158531672553&_cp_guess=Tool%20Time&...`

**Step 2 — Pixel endpoint captures:**
`TrackingCaptureService.CaptureFromRequest()` builds a `TrackingData` record:
```
ReceivedAt: 2026-02-13 14:30:00.000
CompanyID: "12800"
PiXLID: "1"
IPAddress: "98.45.67.123"
QueryString: "sw=1920&sh=1080&...&_cp_email=bpryce6%40gmail.com&_cp_hid=2602102158531672553&..."
```
Server-side enrichment runs (FP stability, IP behavior), then the record is queued.

**Step 3 — DatabaseWriterService writes:**
SqlBulkCopy inserts into `PiXL.Test`. The QueryString column contains everything, including all `_cp_*` params.

**Step 4 — EtlBackgroundService runs ETL.usp_ParseNewHits (Phases 1–13):**
- **Phases 1-8:** Parse all 158+ fingerprint signals into PiXL.Parsed columns (unchanged)
- **Phase 9 — DeviceHash:** Compute `HASHBYTES('SHA2_256', CONCAT_WS('|', CanvasFingerprint, DetectedFonts, GPURenderer, WebGLFingerprint, AudioFingerprintHash))` → `0xA3F1...` (32-byte hash)
- **Phase 10 — MERGE PiXL.Device:**
  ```
  MERGE PiXL.Device ON DeviceHash = 0xA3F1...
  WHEN NOT MATCHED → INSERT (DeviceHash, FirstSeenAt, LastSeenAt, HitCount)
  WHEN MATCHED → UPDATE LastSeenAt, HitCount += 1
  → DeviceId = 47 (new device, identity auto-assigned)
  ```
- **Phase 11 — MERGE PiXL.IP:**
  ```
  MERGE PiXL.IP ON IPAddress = '98.45.67.123'
  WHEN NOT MATCHED → INSERT (IPAddress, FirstSeenAt, LastSeenAt, HitCount)
  WHEN MATCHED → UPDATE LastSeenAt, HitCount += 1
  → IpId = 312 (existing IP, seen before across clients)
  ```
- **Phase 12 — Client Params:** Scan QueryString for `_cp_*` params → build JSON:
  ```json
  {"email":"bpryce6@gmail.com","hid":"2602102158531672553","guess":"Tool Time",...}
  ```
- **Phase 13 — INSERT PiXL.Visit:**
  ```
  INSERT PiXL.Visit (SourceId, CompanyID, PiXLID, DeviceId, IpId, ClientParamsJson, VisitedAt)
  VALUES (964, 12800, 1, 47, 312, '{"email":"bpryce6@gmail.com",...}', '2026-02-13 14:30:00')
  ```
  → `MatchEmail` computed column auto-populates: `bpryce6@gmail.com` (extracted from ClientParamsJson via CLR)

**Step 5 — MatchBackgroundService runs ETL.usp_MatchVisits:**
- Reads PiXL.Visit rows where `SourceId > LastProcessedSourceId` and `MatchEmail IS NOT NULL`
- Normalizes: `clr_NormalizeEmail('bpryce6@gmail.com')` → `bpryce6@gmail.com` (already clean)
- Looks up: `SELECT TOP 1 IndividualKey, AddressKey FROM AutoUpdate.dbo.AutoConsumer WHERE EMail = 'bpryce6@gmail.com' AND VPN_Flag IS NULL ORDER BY RecordID DESC`
  → Returns `IndividualKey = 'IND00000518234567890123456789012'`, `AddressKey = 'ADD00000098765432101234567890012'`
- MERGEs into `PiXL.Match`:
  ```
  MatchId: 1
  CompanyID: 12800
  PiXLID: 1
  DeviceId: 47
  IpId: 312
  MatchType: 'email'
  MatchKey: 'bpryce6@gmail.com'
  IndividualKey: 'IND00000518234567890123456789012'
  AddressKey: 'ADD00000098765432101234567890012'
  FirstSourceId: 964
  LatestSourceId: 964
  FirstSeen: 2026-02-13 14:30:00
  LastSeen: 2026-02-13 14:30:00
  HitCount: 1
  MatchedAt: 2026-02-13 14:32:05
  ```

**Step 6 — Same user returns:**
User visits another question. The pixel fires again with the same email.
- **ETL.usp_ParseNewHits:**
  - Phase 10 MERGE on PiXL.Device: MATCHED → `HitCount = 2`, `LastSeenAt` updated
  - Phase 11 MERGE on PiXL.IP: MATCHED → `HitCount` incremented
  - Phase 13 INSERT PiXL.Visit: New row `SourceId = 965`, same `DeviceId = 47`, same `IpId = 312`
- **ETL.usp_MatchVisits MERGE:**
  - `WHEN MATCHED THEN UPDATE SET LatestSourceId = 965, LastSeen = 2026-02-13 14:35:00, HitCount = 2`
  - IndividualKey/AddressKey stay as-is — already resolved.

**Resulting Star Schema after 2 visits:**
```
PiXL.Device (DeviceId=47)  ←──  PiXL.Visit (SourceId=964)  ──→  PiXL.IP (IpId=312)
                                 PiXL.Visit (SourceId=965)
                                       ↓
                                 PiXL.Match (MatchId=1, IndividualKey='IND...', AddressKey='ADD...')
```

---

*End of plan. Ready for review and approval before implementation begins.*
