# SmartPiXL ETL Pipeline — Complete Map

> **Server:** Xavier (162.255.138.254)  
> **Database:** SmartPiXL  
> **Documented:** February 12, 2026  
> **Source:** Live inspection of sys tables, stored procedure source code, SQL Agent jobs, and DMVs  

---

## Table of Contents

- [1. Executive Summary](#1-executive-summary)
- [2. Pipeline Architecture](#2-pipeline-architecture)
  - [2.1 Data Flow Diagram](#21-data-flow-diagram)
  - [2.2 The Orchestrator: "Update CRM" Job](#22-the-orchestrator-update-crm-job)
  - [2.3 Pre-Pipeline: Staging Ingestion](#23-pre-pipeline-staging-ingestion)
- [3. Pipeline Stages — Detailed Breakdown](#3-pipeline-stages--detailed-breakdown)
  - [3.0 Stage 0: IIS → Staging Tables](#30-stage-0-iis--staging-tables)
  - [3.1 Stage 1: ASP_PiXL_New → PiXLCRM](#31-stage-1-asp_pixl_new--pixlcrm)
  - [3.2 Stage 2: PiXLCRM → CRM_Match](#32-stage-2-pixlcrm--crm_match)
  - [3.3 Stage 3: Standard IP Match](#33-stage-3-standard-ip-match)
  - [3.4 Stage 4: Supplemental Geo Match](#34-stage-4-supplemental-geo-match)
  - [3.5 Stage 5: Inception Cookie Match](#35-stage-5-inception-cookie-match)
  - [3.6 Stage 6: UID Match](#36-stage-6-uid-match)
  - [3.7 Stage 7: CRM_Match_Dates Population](#37-stage-7-crm_match_dates-population)
  - [3.8 Stage 8: Time-on-Site Calculation](#38-stage-8-time-on-site-calculation)
- [4. Table Reference](#4-table-reference)
  - [4.1 Core Pipeline Tables](#41-core-pipeline-tables)
  - [4.2 Column Schemas](#42-column-schemas)
  - [4.3 Match-Specific Tables](#43-match-specific-tables)
  - [4.4 Bot / User Agent Tables](#44-bot--user-agent-tables)
  - [4.5 Reference / External Tables (Cross-DB)](#45-reference--external-tables-cross-db)
  - [4.6 Archive / Backup Tables](#46-archive--backup-tables)
  - [4.7 Configuration / Lookup Tables](#47-configuration--lookup-tables)
- [5. Index Reference](#5-index-reference)
  - [5.1 Current Indexes](#51-current-indexes)
  - [5.2 Index Fragmentation State](#52-index-fragmentation-state)
- [6. Stored Procedure Reference](#6-stored-procedure-reference)
  - [6.1 Core ETL Procedures](#61-core-etl-procedures)
  - [6.2 Match Procedures](#62-match-procedures)
  - [6.3 Maintenance Procedures](#63-maintenance-procedures)
  - [6.4 Export / Reporting Procedures](#64-export--reporting-procedures)
  - [6.5 Functions (UDFs / TVFs)](#65-functions-udfs--tvfs)
- [7. SQL Agent Jobs](#7-sql-agent-jobs)
  - [7.1 Enabled Jobs](#71-enabled-jobs)
  - [7.2 Disabled Jobs](#72-disabled-jobs)
  - [7.3 Job Step Detail: "Update CRM"](#73-job-step-detail-update-crm)
- [8. Cross-Database Dependencies](#8-cross-database-dependencies)
- [9. Foreign Keys & Constraints](#9-foreign-keys--constraints)
- [10. Hygiene & Filtering Logic](#10-hygiene--filtering-logic)
  - [10.1 Stage 0 Filters (Staging → ASP_PiXL_New)](#101-stage-0-filters-staging--asp_pixl_new)
  - [10.2 Stage 1 Pre-Filters (Building #ASP Temp Table)](#102-stage-1-pre-filters-building-asp-temp-table)
  - [10.3 Stage 1 Blacklists (INSERT WHERE Clause)](#103-stage-1-blacklists-insert-where-clause)
  - [10.4 Domain Validation Logic](#104-domain-validation-logic)
  - [10.5 Column Transforms in Stage 1](#105-column-transforms-in-stage-1)
  - [10.6 Bot Detection (Current State)](#106-bot-detection-current-state)
- [11. Match Logic Deep Dive](#11-match-logic-deep-dive)
  - [11.1 Standard IP Match Algorithm](#111-standard-ip-match-algorithm)
  - [11.2 Supplemental Geo Match Algorithm](#112-supplemental-geo-match-algorithm)
  - [11.3 Inception Cookie Match Algorithm](#113-inception-cookie-match-algorithm)
  - [11.4 UID Match Algorithm](#114-uid-match-algorithm)
  - [11.5 Lead-to-Opportunity Promotion ("CRM Trigger")](#115-lead-to-opportunity-promotion-crm-trigger)
- [12. Appendix: All Hardcoded Values](#12-appendix-all-hardcoded-values)

---

## 1. Executive Summary

The SmartPiXL ETL pipeline processes web traffic captured by a tracking pixel (`_SMART.GIF`) deployed on client websites. The pipeline:

1. **Ingests** raw HTTP request data into rotating staging tables via IIS
2. **Parses** CompanyID and PiXLID from the URL and moves data to `ASP_PiXL_New`
3. **Filters** data through a hygiene pass (bot UA filtering, domain blacklists, page-type blacklists) into `PiXLCRM`
4. **Deduplicates** visitors by IP + UserAgent + Company + PiXL into `CRM_Match`
5. **Resolves identity** through three tiers of matching against AutoConsumer reference data:
   - Direct IP match
   - Supplemental geo-proximity match (692m radius)
   - Cookie/UID-based match (company-specific)
6. **Records visit timestamps** in `CRM_Match_Dates` (workaround for PiXLCRM's size)
7. **Calculates** time-on-site and promotes qualified leads to opportunities

**Scale:** ~17.6 billion rows across active tables, ~2+ TB total. The pipeline processes approximately 6 million raw records per day at current volume.

**Pipeline cadence:** The core "Update CRM" job runs every 1 minute with 8 sequential steps. Staging rotation runs every 5 minutes.

---

## 2. Pipeline Architecture

### 2.1 Data Flow Diagram

```
                    ┌──────────────────────┐
                    │    Client Websites    │
                    │   (tracking pixel)    │
                    └──────────┬───────────┘
                               │ HTTP GET /_SMART.GIF/{CompanyID}/{PiXLID}?params
                               ▼
                    ┌──────────────────────┐
                    │     IIS / ASP.NET    │
                    │  (captures headers)  │
                    └──────────┬───────────┘
                               │ writes raw headers
                               ▼
              ┌────────────────────────────────────┐
              │  ASP_PiXL_Staging 1│2│3│4 (rotate)  │    ← Every 5 min rotation
              └────────────────┬───────────────────┘
                               │ M1SP_Update_ASP_PiXL_New_from_Staging
                               │ (ParseCompanyAndPiXLID, quote escaping)
                               ▼
              ┌────────────────────────────────────┐
              │       ASP_PiXL_New (1.06B rows)     │    ← 280 GB, Columnstore
              │       "Raw feed with IDs"            │
              └────────────────┬───────────────────┘
                               │ SP_Insert_Into_CRM_from_PiXL_Data
                               │ (UA filter, domain blacklist, page blacklist,
                               │  device/browser/OS parsing, domain validation)
                               ▼
              ┌────────────────────────────────────┐
              │       PiXLCRM (7.79B rows)          │    ← 624 GB, Columnstore
              │       "Filtered + transformed"       │
              └────────────────┬───────────────────┘
                               │ SP_Insert_Into_CRM_Match_from_CRM
                               │ (aggregate by IP+UA+Co+PiXL, cap 35 pageviews)
                               ▼
              ┌────────────────────────────────────┐
              │      CRM_Match (1.63B rows)         │    ← 504 GB, B-tree
              │      "Unique visitors"               │
              └────────┬──────────┬────────────────┘
                       │          │
          ┌────────────┘          └─────────────────┐
          ▼                                         ▼
   ┌──────────────┐                          ┌──────────────┐
   │ IP Match (3) │                          │ Supp Geo (4) │
   │  AutoConsumer │                         │  IPGEO + AC   │
   │  IP_Clean     │                         │  692m radius  │
   └──────┬───────┘                          └──────┬───────┘
          │                                         │
          ├────────────────┬────────────────────────┘
          │                │
          │    ┌───────────┴──────────────┐
          │    │ Inception (5) / UID (6)  │
          │    │ Cookie/identifier match  │
          │    │ (company-specific)       │
          │    └───────────┬──────────────┘
          │                │
          ▼                ▼
   ┌──────────────────────────────────────┐
   │  CRM_Match.ReferenceRecordID = AC ID │    ← Identity resolved
   └──────────────────┬───────────────────┘
                      │ SP_Insert_Into_CRM_Match_Dates_from_CRM
                      ▼
   ┌──────────────────────────────────────┐
   │   CRM_Match_Dates (1.70B rows)       │    ← 297 GB
   │   "Visit timestamps for matched"     │
   └──────────────────┬───────────────────┘
                      │ SP_Update_CRM_Match_Time_On_Site
                      ▼
   ┌──────────────────────────────────────┐
   │   CRM_Match.TotalTimeOnSite          │    ← Calculated from page deltas
   └──────────────────────────────────────┘

   Supporting:
   ┌──────────────────────┐    ┌────────────────────────┐
   │  SP_UpdateCRM_Trigger │    │ M1SP_CleanseBotMatches │
   │  Lead → Opportunity   │    │ ≥60 hits/day cleanup   │
   │  (every 12h)          │    │ (daily 1AM)            │
   └──────────────────────┘    └────────────────────────┘
```

### 2.2 The Orchestrator: "Update CRM" Job

The `Update CRM` SQL Agent job is the heartbeat of the entire pipeline. It executes **every 1 minute**, running 8 steps sequentially:

| Step | Stored Procedure | Duration Concern |
|:----:|:----------------|:-----------------|
| 1 | `SP_Insert_Into_CRM_from_PiXL_Data` | Scans 6 hours of ASP_PiXL_New per run |
| 2 | `SP_Insert_Into_CRM_Match_from_CRM` | Scans 6 hours of PiXLCRM per run |
| 3 | `SP_Update_CRM_Match_Reference_RecordID` | Joins CRM_Match to AutoConsumer |
| 4 | `SP_Update_CRM_Match_Reference_RecordID_Supp` | Spatial join with IPGEO |
| 5 | `M1SP_ProcessInceptionPixlData` | 60-min window, CompanyId 12718 only |
| 6 | `SP_Update_CRM_Match_Reference_RecordID_UID` | 60-min window, CompanyId 12730 only |
| 7 | `SP_Insert_Into_CRM_Match_Dates_from_CRM` | Anti-join on existing dates |
| 8 | `SP_Update_CRM_Match_Time_On_Site` | Self-join on page timestamps |

All 8 steps must complete within 1 minute or the next invocation queues behind the current one.

### 2.3 Pre-Pipeline: Staging Ingestion

**SP:** `M1SP_Update_ASP_PiXL_New_from_Staging`  
**Schedule:** Every 5 minutes  

**Staging rotation algorithm:**
```
Current table = (DATEPART(MINUTE, GETDATE()) / 5) % 4
  → 0 maps to table 4
  → 1 maps to table 1
  → 2 maps to table 2
  → 3 maps to table 3

Truncate table = 2 slots behind current
```

This ensures IIS is always writing to a different staging table than the one being read, with a buffer table between them.

---

## 3. Pipeline Stages — Detailed Breakdown

### 3.0 Stage 0: IIS → Staging Tables

**Source:** Tracking pixel HTTP requests (`/_SMART.GIF/{CompanyID}/{PiXLID}?params`)  
**Destination:** `ASP_PiXL_Staging1` through `ASP_PiXL_Staging4`  

IIS captures raw HTTP headers from the tracking pixel request and writes them into the current staging table. The URL path encodes the CompanyID and PiXLID as 5-digit zero-padded integers.

**Staging table schema (identical across all 4):**

| Column | Type | Nullable | Notes |
|:-------|:-----|:--------:|:------|
| RecordID | int | NO | Identity / sequence |
| timestamp | datetime2 | NO | Request time |
| timestamp2 | datetime2 | NO | Secondary timestamp |
| REMOTE_ADDR | varchar(200) | NO | Client IP address |
| HTTP_USER_AGENT | varchar(500) | NO | Browser user agent string |
| HTTP_REFERER | varchar(5000) | YES | Page that linked to the pixel |
| HTTP_REFERER_ROOT | varchar(500) | YES | Root domain of referer |
| HTTP_REFERER_QUERY | varchar(3000) | YES | Query string of referer |
| HTTP_X_ORIGINAL_URL | varchar(4000) | NO | The actual pixel URL with CompanyID/PiXLID |
| HTTP_X_ORIGINAL_URL_ROOT | varchar(4000) | YES | Root of original URL |
| HTTP_DNT | varchar(200) | YES | Do Not Track header |
| HTTP_COOKIE | varchar(200) | YES | Cookie header |
| HTTP_CLIENT_IP | varchar(200) | YES | Client IP (proxy) |
| HTTP_FORWARDED | varchar(200) | YES | X-Forwarded-For |
| HTTP_FROM | varchar(200) | YES | From header |
| HTTP_PROXY_CONNECTION | varchar(200) | YES | Proxy connection |
| HTTP_VIA | varchar(200) | YES | Via header (proxies) |
| HTTP_X_MCPROXYFILTER | varchar(200) | YES | Proxy filter |
| HTTP_X_TARGET_PROXY | varchar(200) | YES | Target proxy |
| HTTP_X_REQUESTED_WITH | varchar(200) | YES | AJAX indicator |
| BROWSER_Browser | varchar(200) | YES | Server-parsed browser |
| BROWSER_MobileDeviceModel | varchar(200) | YES | Server-parsed device |
| BROWSER_Platform | varchar(200) | YES | Server-parsed platform |
| HTTP_ACCEPT_LANGUAGE | varchar(300) | YES | Accept-Language header |

**24 columns, no CompanyID or PiXLID** — those are parsed during the move to `ASP_PiXL_New`.

**SP logic (`M1SP_Update_ASP_PiXL_New_from_Staging`):**
1. Drop and recreate `v_Current_Staging` view pointing to the current staging table
2. `INSERT INTO ASP_PiXL_New` with `OUTER APPLY dbo.ParseCompanyAndPiXLID()` to extract CompanyID/PiXLID from `HTTP_X_ORIGINAL_URL`
3. Only rows where both CompanyID and PiXLID parse successfully are inserted
4. Double-quotes in referer fields are escaped (`REPLACE(HTTP_REFERER, '"', '""')`)
5. Truncate the staging table 2 slots behind current

**Notable:** Staging uses `datetime2`, ASP_PiXL_New uses `datetime` — implicit precision loss on insert.

---

### 3.1 Stage 1: ASP_PiXL_New → PiXLCRM

**SP:** `SP_Insert_Into_CRM_from_PiXL_Data`  
**Schedule:** Every 1 minute (Update CRM, Step 1)  
**Source:** `ASP_PiXL_New` (1.06B rows, 280 GB)  
**Destination:** `PiXLCRM` (7.79B rows, 624 GB)  

This is the primary hygiene and transformation step. It reads the last 6 hours of raw data, applies filtering, transforms columns, and inserts into PiXLCRM.

**Processing steps:**

1. **Build temp table `#ASP`** from `ASP_PiXL_New` with:
   - 6-hour lookback window
   - Empty pixel exclusion (`/_SMART.GIF` with no params)
   - Deduplication via `NOT EXISTS` against PiXLCRM by RecordID
   - User agent prefix filter (`Mozilla%`)
   - User agent length filter (`LEN > 30`)
   - URL decoding via `CLR.dbo.M1CLR_Decode_URI` and `CLR.dbo.M1CLR_Decode_URI_oref`

2. **INSERT into PiXLCRM** with:
   - JOIN to `PiXL` table for campaign configuration
   - 13-pattern domain blacklist on `HTTP_REFERER_ROOT`
   - 12-pattern page-type blacklist on `HTTP_REFERER`
   - Duplicate blacklist re-applied to `HTTP_X_ORIGINAL_URL` when `?ref=` parameter is present
   - Domain validation check (referer matches PiXL's registered domain)
   - Device/Browser/OS classification via CASE statements
   - Referer cascade logic (decoded URI → HTTP_REFERER → PiXL URL fallback)
   - Status flag derivation from PiXL table (IsPaused, IsSuspended, IsDisabled)

See [Section 10: Hygiene & Filtering Logic](#10-hygiene--filtering-logic) for the complete filter list.

---

### 3.2 Stage 2: PiXLCRM → CRM_Match

**SP:** `SP_Insert_Into_CRM_Match_from_CRM`  
**Schedule:** Every 1 minute (Update CRM, Step 2)  
**Source:** `PiXLCRM` (7.79B rows)  
**Destination:** `CRM_Match` (1.63B rows, 504 GB)  

Creates unique visitor records by aggregating PiXLCRM data.

**Logic:**
1. Select from PiXLCRM where `CreationDate` is within the last 6 hours
2. Group by `IP + UserAgent + CompanyId + PiXLId`
3. For each new combination not already in CRM_Match, insert with:
   - `FirstSeen` = earliest timestamp
   - `PageViews` = COUNT, capped at 35
   - `CKey` extracted from `Request_URI` if it contains `visitorID=`
   - ProspectTypeID, Browser, OS, Device carried forward
4. The composite PK `(CompanyID, PiXLID, IP, UserAgent)` naturally deduplicates

**Key detail:** `UserAgent` (varchar(500)) is part of the primary key. A browser update changes the UA and creates a "new" visitor for the same person/IP.

---

### 3.3 Stage 3: Standard IP Match

**SP:** `SP_Update_CRM_Match_Reference_RecordID`  
**Schedule:** Every 1 minute (Update CRM, Step 3)  
**Operates on:** `CRM_Match` where `ReferenceRecordID IS NULL`  
**Reference data:** `SmartPiXL.dbo.AutoConsumer.IP_Clean`  

This is the primary identity resolution step — matching visitor IPs against known consumer records.

**Algorithm:**
1. Select unmatched CRM_Match records from the last 6 hours
2. Join PiXLCRM for recent activity context
3. Join `AutoConsumer` by `IP = IP_Clean`
4. Exclude VPN-flagged records (`VPN_Flag IS NOT NULL`)
5. Apply geo-radius filter via `ATLAS` postal centroids (PiXL zip vs consumer zip)
6. Build suppression list (`#Suppression`) preventing duplicate assignments per Company/PiXL/IP
7. Dual `ROW_NUMBER()` dedup:
   - Partition by visitor (Company+PiXL+IP+UA) → pick best match by RecordCount DESC
   - Partition by reference record (RecordID) → pick best visitor by RecordCount DESC
8. Update `CRM_Match.ReferenceRecordID` with the winning AutoConsumer RecordID

**Company exclusions (hardcoded in WHERE clause):**

| CompanyId | Exclusion Rule |
|:---------:|:--------------|
| 12345 | PiXLId 1 and 29 excluded; PiXLId 66 force-included |
| 12445 | PiXLId 1 excluded |
| 12598 | PiXLId 6 excluded |
| 12606 | All PiXLIds excluded |
| 12718 | All excluded (handled by Inception match, Stage 5) |
| 12730 | All excluded (handled by UID match, Stage 6) |

**Additional hardcoded elements:**
- ~36 specific IP addresses blacklisted for CompanyId 12679 / PiXLId 1
- Fallback zipcode `33309` (Fort Lauderdale) when PiXL has no zip
- Forced assignment: `ReferenceRecordID = 518006700` for CompanyId 12420 / PiXLId 98
- IP prefix exclusion: IPs starting with `3.` or `4.` are excluded

---

### 3.4 Stage 4: Supplemental Geo Match

**SP:** `SP_Update_CRM_Match_Reference_RecordID_Supp`  
**Schedule:** Every 1 minute (Update CRM, Step 4)  
**Operates on:** `CRM_Match` where `ReferenceRecordID IS NULL` (still unmatched after Stage 3)  
**Reference data:** `IPGEO.dbo.IP_Location_New` + `AutoConsumer`  

For visitors that couldn't be matched by direct IP, this step attempts a geographic proximity match.

**Algorithm:**
1. Only processes companies in `CRM_Match_Supplemental_Whitelist`
2. Join unmatched CRM_Match records to `IPGEO.dbo.IP_Location_New` to get IP lat/lon
3. Spatial join against `AutoConsumer` records within **692.01792 meters (0.43 miles)**
4. Only `AutoConsumer` records with `PPM_Indicator IS NOT NULL` are eligible (premium records)
5. When multiple candidates exist: `ORDER BY newID()` — **random selection** (non-deterministic)
6. Update `CRM_Match.ReferenceRecordID` and set `SupplementaryMatch = 1`

**Company exclusions (hardcoded):** CompanyIds 12718, 12730, and CompanyId 12784 PiXLId 4

**Same fallback zipcode `33309`** when PiXL has no zip configured.

---

### 3.5 Stage 5: Inception Cookie Match

**SP:** `M1SP_ProcessInceptionPixlData`  
**Schedule:** Every 1 minute (Update CRM, Step 5)  
**Scope:** CompanyId 12718 only  
**Tracking table:** `CRM_Match_InceptionPiXL` (29.3M rows)  

Handles identity resolution using a cookie value from the pixel URL for the Inception/PureCars product line.

**Algorithm:**
1. Extract cookie from `Request_URI`: `SUBSTRING(Request_URI, CHARINDEX('&rel=', ...) + 5, 46)`
2. Extract session: `SUBSTRING(Request_URI, CHARINDEX('&ses=', ...) + 5, 32)`
3. **Bot filtering:** `NOT EXISTS` against `UserAgentsandBots WHERE Bot = 1` — this is the **only step in the entire pipeline** that uses this table
4. Insert new cookie/IP pairs into `CRM_Match_InceptionPiXL`
5. Attempt direct IP match against `AutoUpdate.dbo.AutoConsumer.IP_Clean` (note: different database than Stage 3)
6. If no IP match, fall back to geo-proximity (same 692m radius pattern)
7. Propagate matched `ReferenceRecordID` back to main `CRM_Match`

**Lookback window:** 60 minutes (shorter than the 6 hours used in Stages 1-4)

**Rate-controlled variant:** `M1SP_ProcessInceptionPixlData_Supp` — targets 38-44% match rate. Runs iteratively (up to 10 loops), widening the candidate pool to include consumers with vehicles 4-9 years old. The geo radius scales with PiXL's configured radius: `1609.344 * 0.43 * @Radius/25`. Currently **DISABLED** in SQL Agent.

---

### 3.6 Stage 6: UID Match

**SP:** `SP_Update_CRM_Match_Reference_RecordID_UID`  
**Schedule:** Every 1 minute (Update CRM, Step 6)  
**Scope:** CompanyId 12730 only  
**Tracking table:** `CRM_Match_UID`  

Handles identity resolution using a UID (cookie/identifier) extracted from the URL for the PureInfluencer product line.

**Algorithm:**
1. Extract UID via `CLR.dbo.M1CLR_Decode_URI` — first 20 characters (`@UIDLength = 20`)
2. Write UID to `PiXLCRM.CKey`
3. Insert new UID/IP pairs into `CRM_Match_UID`
4. Attempt direct IP match against `AutoUpdate.dbo.AutoConsumer.IP_Clean`
5. If no IP match, fall back to geo-proximity (spatial index created on temp table at runtime)
6. Propagate matches back to main `CRM_Match`

**Lookback window:** 60 minutes  
**CLR dependency:** `CLR.dbo.M1CLR_Decode_URI` — cross-database .NET CLR assembly

---

### 3.7 Stage 7: CRM_Match_Dates Population

**SP:** `SP_Insert_Into_CRM_Match_Dates_from_CRM`  
**Schedule:** Every 1 minute (Update CRM, Step 7)  
**Source:** `PiXLCRM` + `CRM_Match`  
**Destination:** `CRM_Match_Dates` (1.70B rows, 297 GB)  

This table was created as a **workaround** because PiXLCRM grew too large to query efficiently. It records individual visit timestamps for matched visitors.

**Logic:**
1. Join PiXLCRM (last 6 hours) to CRM_Match where `ReferenceRecordID IS NOT NULL`
2. Anti-join against existing `CRM_Match_Dates` to avoid duplicates
3. Insert with: Timestamp, MatchDate (from PiXLCRM.CreationDate), MatchReferer, MatchCompanyID, MatchPiXLID, MatchCRMID, and status flags (IsActive/IsPaused/IsSuspended/IsDisabled)

---

### 3.8 Stage 8: Time-on-Site Calculation

**SP:** `SP_Update_CRM_Match_Time_On_Site`  
**Schedule:** Every 1 minute (Update CRM, Step 8)  
**Operates on:** `CRM_Match_Dates` → writes to `CRM_Match.TotalTimeOnSite`  

Calculates the total time a matched visitor spent on site by analyzing consecutive page-hit timestamps.

**Algorithm:**
1. Select CRM_Match records where `TToS_Update_Date IS NULL` (never calculated) PLUS top 100,000 records with `TToS_Update_Date` within 1 day (recompute recent)
2. For each match, pull CRM_Match_Dates rows (up to 200 most recent page hits)
3. Use `ROW_NUMBER()` to order page hits by timestamp
4. Self-join to find consecutive page pairs: `CRM1.MatchDate < CRM2.MatchDate`, take nearest
5. Calculate `DATEDIFF(second, CRM1.MatchDate, CRM2.MatchDate)`
6. Cap each page-to-page transition at **300 seconds** (5 minutes)
7. Sum all capped deltas = `TotalTimeOnSite`
8. **If no valid page pairs exist:** assign `CONVERT(INT, ABS(CHECKSUM(NewId())) % 14)` — a **random value 0-13 seconds**
9. Update `CRM_Match.TotalTimeOnSite` and set `TToS_Update_Date = GETDATE()`

---

## 4. Table Reference

### 4.1 Core Pipeline Tables

| Table | Rows | Size | Storage | PK / Clustered Index |
|:------|-----:|-----:|:--------|:---------------------|
| ASP_PiXL_Staging1–4 | rotating | tiny | Heap | RecordID (int identity) |
| ASP_PiXL_New | 1,056,454,757 | 279.70 GB | Columnstore (CCI) | ClusteredColumnStoreIndex-20250328 |
| PiXLCRM | 7,788,548,685 | 624.47 GB | Columnstore (CCI) | ClusteredColumnStoreIndex-20210507 |
| CRM_Match | 1,627,992,963 | 503.75 GB | B-tree | PK_CRM_Match_New (CompanyID, PiXLID, IP, UserAgent) |
| CRM_Match_Dates | 1,695,226,335 | 296.97 GB | B-tree | PK_CRM_Match_Dates_NEW (RecordID identity) |
| PiXL | 5,610 | tiny | B-tree | PK_COMPANY_PiXL_1 (CompanyId, PiXLId) |
| Company | 467 | tiny | B-tree | PK_Company_1 (CompanyID identity) |

### 4.2 Column Schemas

#### ASP_PiXL_New (26 columns)

| Column | Type | Nullable | Notes |
|:-------|:-----|:--------:|:------|
| RecordID | bigint | NO | Identity (PK equivalent in columnstore) |
| timestamp | datetime | YES | Request time (note: datetime, not datetime2) |
| timestamp2 | datetime | YES | Secondary timestamp |
| REMOTE_ADDR | varchar(200) | YES | Client IP |
| HTTP_USER_AGENT | varchar(500) | YES | User agent string |
| HTTP_REFERER | varchar(5000) | YES | Referring page URL |
| HTTP_REFERER_ROOT | varchar(500) | YES | Root domain of referer |
| HTTP_REFERER_QUERY | varchar(3000) | YES | Referer query string |
| HTTP_X_ORIGINAL_URL | varchar(4000) | YES | Pixel URL with IDs and params |
| HTTP_X_ORIGINAL_URL_ROOT | varchar(4000) | YES | Root of pixel URL |
| HTTP_DNT | varchar(200) | YES | Do Not Track |
| HTTP_COOKIE | varchar(5000) | YES | Cookies (5000 in APN vs 200 in staging) |
| HTTP_CLIENT_IP | varchar(200) | YES | Client IP via proxy |
| HTTP_FORWARDED | varchar(200) | YES | X-Forwarded-For |
| HTTP_FROM | varchar(200) | YES | From header |
| HTTP_PROXY_CONNECTION | varchar(200) | YES | Proxy connection |
| HTTP_VIA | varchar(200) | YES | Via (proxy hops) |
| HTTP_X_MCPROXYFILTER | varchar(200) | YES | Proxy filter |
| HTTP_X_TARGET_PROXY | varchar(200) | YES | Target proxy |
| HTTP_X_REQUESTED_WITH | varchar(200) | YES | AJAX indicator |
| BROWSER_Browser | varchar(200) | YES | Server-parsed browser |
| BROWSER_MobileDeviceModel | varchar(200) | YES | Server-parsed device |
| BROWSER_Platform | varchar(200) | YES | Server-parsed platform |
| HTTP_ACCEPT_LANGUAGE | varchar(300) | YES | Language preference |
| CompanyID | int | YES | Parsed from URL |
| PiXLID | int | YES | Parsed from URL |

#### PiXLCRM (23 columns)

| Column | Type | Nullable | Notes |
|:-------|:-----|:--------:|:------|
| PiXLCRMId | bigint | NO | Identity (PK equivalent) |
| IP | varchar(15) | YES | Client IP (truncated from 200 to 15) |
| CompanyId | int | NO | |
| PiXLId | int | NO | |
| Http_Referer | varchar(5000) | YES | Referer (cascaded: decoded URI → raw → PiXL URL) |
| Http_Referer_Root | varchar(5000) | YES | Root domain (same cascade) |
| CreationDate | datetime | YES | Original request timestamp |
| PiXLRecordId | bigint | YES | FK-by-convention to ASP_PiXL_New.RecordID |
| IsActive | bit | YES | |
| IsPaused | bit | YES | Derived from PiXL.StatusID = 6 |
| IsSuspended | bit | YES | Derived from PiXL.SuspendedID = 4 |
| IsDisabled | bit | YES | Derived from PiXL.StatusID IN (3,5) |
| DateIsActive | datetime | YES | |
| LastUpdated | datetime | YES | |
| DateUpdated | datetime | YES | |
| ProspectTypeID | varchar(2) | YES | Lead type code |
| Device | varchar(100) | YES | Parsed: Mobile/Desktop/Tablet/Other |
| Browser | varchar(100) | YES | Parsed: Chrome/Safari/Firefox/IE/etc. |
| OS | varchar(100) | YES | Parsed: Windows/OSX/Linux/IOS/etc. |
| UserAgent | varchar(3000) | YES | Full UA string (expanded from 500 to 3000) |
| CKey | varchar(64) | YES | Cookie/visitor key (populated later by UID match) |
| Request_URI | varchar(4000) | YES | Decoded URI, only if external to PiXL domain |
| DomainValidation | bit | NO | 1 = referer matches PiXL's registered domain |

#### CRM_Match (17 columns)

| Column | Type | Nullable | Notes |
|:-------|:-----|:--------:|:------|
| CompanyID | int | NO | **PK part 1** |
| PiXLID | int | NO | **PK part 2** |
| IP | varchar(15) | NO | **PK part 3** |
| UserAgent | varchar(500) | NO | **PK part 4** |
| RecordID | bigint | NO | Auto-assigned identity |
| FirstSeen | datetime | NO | Earliest appearance |
| ReferenceRecordID | int | YES | **The match result** — AutoConsumer record ID |
| PageViews | int | YES | Capped at 35 |
| TotalTimeOnSite | int | YES | Calculated by Stage 8 |
| ProspectTypeID | varchar(2) | YES | Lead/Opportunity code |
| DateUpdated | datetime | YES | |
| Browser | varchar(100) | YES | |
| OS | varchar(50) | YES | |
| Device | varchar(50) | YES | |
| CKey | varchar(62) | YES | Cookie/visitor key |
| SupplementaryMatch | bit | NO | 1 = matched via geo proximity, not direct IP |
| TToS_Update_Date | datetime | YES | When time-on-site was last calculated |

#### CRM_Match_Dates (13 columns)

| Column | Type | Nullable | Notes |
|:-------|:-----|:--------:|:------|
| RecordID | bigint | NO | Identity PK |
| Timestamp | datetime | NO | |
| MatchRecordID | bigint | NO | FK-by-convention to CRM_Match |
| MatchDate | datetime | NO | The visit timestamp |
| MatchReferer | varchar(5000) | YES | Referer at time of visit |
| MatchCompanyID | int | NO | |
| MatchPiXLID | int | NO | |
| MatchCRMID | bigint | YES | FK-by-convention to PiXLCRM |
| IsActive | bit | YES | |
| IsPaused | bit | YES | |
| IsSuspended | bit | YES | |
| IsDisabled | bit | YES | |
| Request_URI | varchar(1000) | YES | External page visited |

#### PiXL (46 columns — key columns only)

| Column | Type | Notes |
|:-------|:-----|:------|
| CompanyId | int | **PK part 1** |
| PiXLId | int | **PK part 2** |
| PiXLName | varchar(500) | Display name |
| SmartPiXL | varchar(2000) | Pixel code snippet |
| PiXLNew / PiXLLegacy | varchar(1000) | Pixel URLs |
| PiXLURL | varchar(1000) | Registered tracking URL |
| PiXLDomain | varchar(8000) | Registered domain(s) for validation |
| Zipcode | varchar | PiXL location zip |
| Radius | int | Match radius (miles) |
| Nationwide | bit | Nationwide flag (overrides radius) |
| NumberPage | int | Min page views for opportunity trigger |
| TimeSite | time | Min time-on-site for opportunity trigger |
| IncomeRefInitial / IncomeRefFinal | decimal | Income range filter |
| InferredCS | varchar(500) | Semicolon-delimited credit score buckets |
| NetWorth / Married / Children / Gender | varchar | Demographic filters |
| StatusId | int | Active/Paused/Disabled status |
| SuspendedId | int | Suspension status |
| PiXLLatitude / PiXLLongitude | decimal | Geo coordinates |
| PiXLAddress/City/State/ZipCode | varchar | Physical address |

#### Company (38 columns — key columns only)

| Column | Type | Notes |
|:-------|:-----|:------|
| CompanyID | int | **PK** (identity) |
| CompanyName | varchar(100) | |
| CompanyTypeId | int | FK to CompanyType |
| ParentCompanyId | int | Self-referencing FK |
| OriginalParentCoId | int | Self-referencing FK |
| StatusId | int | FK to Status |
| IsActive | bit | |
| PortalURL | varchar | Client portal URL |
| NAICS_SIC / NAICS_Code / SIC_Code | varchar | Industry classification |

### 4.3 Match-Specific Tables

| Table | Rows | Purpose |
|:------|-----:|:--------|
| CRM_Match_InceptionPiXL | 29,315,000 | Cookie/session tracking for Inception match (CompanyId 12718) |
| CRM_Match_UID | small | UID tracking for PureInfluencer match (CompanyId 12730) |
| CRM_Match_Supplemental_Whitelist | small | Companies eligible for supplemental geo match |
| CRM_Match_Suppression | small | Global suppression list — matched records excluded from export |
| CRM_Match_12718_Bots | 79,988 | Bot matches quarantined specifically for CompanyId 12718 |
| UniqueVisitorCounts | varies | Daily unique visitor rollup per Company/PiXL |
| UpdatePixlCRMJobTable | varies | Job queue for batch soft-delete operations |

### 4.4 Bot / User Agent Tables

| Table | Rows | Size | Connected to Pipeline? | Notes |
|:------|-----:|-----:|:----------------------:|:------|
| whatismybrowser_useragent_Optimized | 134,945,012 | 24.03 GB | **NO** | 39 columns: full UA classification (software_name, software_type, hardware_type, OS, layout_engine, capabilities, etc.) |
| whatismybrowser_useragent | 134,945,012 | 9.38 GB | **NO** | Identical schema to Optimized — possibly an unoptimized copy |
| UserAgentsandBots | 10,907,326 | 2.24 GB | **Only Stage 5** | 3 columns: UserAgent, FirstSeen, **Bot (bit)** |
| UATable | 6,697,311 | 1.48 GB | **Batch only** | HTTP_USER_AGENT, Platform Name, Device Type — populated by M1SP_GetUserAgents/UpdateUserAgents |
| UATable_DEV | 6,697,311 | 2.56 GB | **No** | Dev copy of UATable |
| UATable_Staging | 0 | — | **No** | Empty staging table for UA batch processing |
| BadUAs | 0 | — | **No** | Empty — apparently never used |

### 4.5 Reference / External Tables (Cross-DB)

| Table | Database | Rows (est.) | Used By |
|:------|:---------|:------------|:--------|
| AutoConsumer (IP_Clean column) | SmartPiXL | large | Stage 3: Standard IP match |
| AutoConsumer (IP_Clean column) | AutoUpdate | large | Stages 5-6: Inception/UID match |
| IP_Location_New | IPGEO | large | Stages 4-6: Geo-proximity matching |
| ATLAS (postal centroids) | (via join) | — | Stages 3-4: Geo-radius validation |
| M1CLR_Decode_URI | CLR | — | Stages 1, 6: URL decoding (.NET CLR) |
| M1CLR_Decode_URI_oref | CLR | — | Stage 1: URL decoding (original ref variant) |

**Important note:** Stages 3 and 5-6 use **different AutoConsumer tables in different databases** (`SmartPiXL.dbo.AutoConsumer` vs `AutoUpdate.dbo.AutoConsumer`). These may or may not be synchronized.

### 4.6 Archive / Backup Tables

| Table | Rows | Size | Notes |
|:------|-----:|-----:|:------|
| ASP_PiXL_New_Archive | 2,301,011,424 | 269.74 GB | Main archive |
| ASP_PiXL_New_OLD | 833,512,681 | 81.82 GB | Legacy backup |
| ASP_PiXL_New_1_27_2022_BAK | 560,516,427 | 25.96 GB | Point-in-time backup |
| ASP_PiXL_New_6_16_2022 | 528,764,215 | 16.57 GB | Point-in-time backup |
| pixlcrm_Archive | 679,798,133 | 55.12 GB | PiXLCRM archive |
| PiXLCRM_NEW | 60,000,000 | 21.88 GB | Migration/staging copy |
| CRM_Match_OLD_2023 | 410,095,943 | 13.53 GB | 2023 archive |
| CRM_Match_OLD | 363,947,862 | 6.15 GB | Older archive |
| CRM_Match_Dates_Archive | 237,912,268 | 16.75 GB | Dates archive |
| CRM_Match_Dates_OLDEE | 379,778,463 | 8.09 GB | Older dates archive |
| **Total archive/backup** | **~6.36B rows** | **~515 GB** | Dead weight on same server |

Additional backup/one-off tables: `pixlbackup_1_13_2026`, `pixl_backup_7_21_2025`, `PiXL_Backup_8_2_2018`, `PiXL_Backup_8_6_2018`, `PixlAccessBackup`, `PixlAccess1232020`, `CompanySettings_BG_Backup_6_20_2023`, various invoice backups, `Week_21`–`Week_22_4`, `Month_5`–`Month_6`, `File12549_1_Raw`, `File12549_1_Dedupe`, `BG_12687_132`.

### 4.7 Configuration / Lookup Tables

| Table | Rows | Purpose |
|:------|-----:|:--------|
| CRMProspectTypes | 10 | Lead type codes (Lead, Opportunity, etc.) |
| CompanyType | 6 | Company classification |
| CompanyBillingRate | 1 | Billing rate config |
| CRM_Match_Supplemental_Whitelist | small | Companies eligible for geo supplemental match |
| CRM_Match_Suppression | small | Global opt-out / suppression list |
| LC_IP_Logger_GlobalOptOut | 651,579 | Global IP opt-out log |
| LC_IP_Logger_GlobalOptOut_RequestTypes | 5 | Opt-out request type lookup |
| PiXLPortalPrivacyURLs | 636 | Privacy policy URLs per PiXL |
| PiXLAccessSelection | 69 | Access control config |
| PiXLAccess | 49,151 | User access permissions |

---

## 5. Index Reference

### 5.1 Current Indexes

#### ASP_PiXL_New
| Index | Type | Key Columns | Included |
|:------|:-----|:------------|:---------|
| ClusteredColumnStoreIndex-20250328-133120 | **Clustered Columnstore** | — | All 26 columns |

No B-tree indexes. All queries use columnstore segment elimination.

#### PiXLCRM
| Index | Type | Key Columns | Included |
|:------|:-----|:------------|:---------|
| ClusteredColumnStoreIndex-20210507-140123 | **Clustered Columnstore** | — | All 23 columns |

No B-tree indexes. Same as ASP_PiXL_New.

#### CRM_Match
| Index | Type | Key Columns | Included |
|:------|:-----|:------------|:---------|
| PK_CRM_Match_New | Clustered, Unique, PK | CompanyID, PiXLID, IP, UserAgent | — |
| DashboardIndex | Nonclustered | RecordID | ProspectTypeID, Browser, OS, Device, CompanyID, PiXLID |

#### CRM_Match_Dates
| Index | Type | Key Columns | Included |
|:------|:-----|:------------|:---------|
| PK_CRM_Match_Dates_NEW | Clustered, Unique, PK | RecordID (identity) | — |

No nonclustered indexes. Queries against MatchRecordID, MatchCompanyID, MatchPiXLID rely on full scans.

#### PiXL
| Index | Type | Key Columns |
|:------|:-----|:------------|
| PK_COMPANY_PiXL_1 | Clustered, Unique, PK | CompanyId, PiXLId |
| NonClusteredIndex-20180614 | Nonclustered | PiXLId, PiXLURL, StatusId, CompanyId |

#### Company
| Index | Type | Key Columns |
|:------|:-----|:------------|
| PK_Company_1 | Clustered, Unique, PK | CompanyID |

### 5.2 Index Fragmentation State

> Captured February 12, 2026 using `sys.dm_db_index_physical_stats` with `LIMITED` mode.

| Table | Index | Type | Fragmentation | Pages |
|:------|:------|:-----|:-------------|------:|
| **CRM_Match** | PK_CRM_Match_New | Clustered | **70.4%** | 63,600,000 |
| **CRM_Match** | DashboardIndex | Nonclustered | 24.0% | 44,900,000 |
| **CRM_Match_Dates** | PK_CRM_Match_Dates_NEW | Clustered | **30.8%** | 38,800,000 |
| **ASP_PiXL_New** | ClusteredColumnStoreIndex | CCI | **57.9%** | 2,600,000 |
| **CRM_Match_UID** | PK | Clustered | **94.2%** | 95,000 |

The `Rebuild SmartPixl Indexes` Agent job is **DISABLED**. `M1SP_Trim_CRM_Match` (which contained index rebuild logic) is **entirely commented out**. These indexes have not been maintained.

---

## 6. Stored Procedure Reference

### 6.1 Core ETL Procedures

| Procedure | Called By | Purpose |
|:----------|:---------|:--------|
| `M1SP_Update_ASP_PiXL_New_from_Staging` | Agent: "Update ASP_PiXL_New" (5 min) | Staging tables → ASP_PiXL_New with CompanyID/PiXLID parsing |
| `SP_Insert_Into_CRM_from_PiXL_Data` | Agent: "Update CRM" Step 1 (1 min) | ASP_PiXL_New → PiXLCRM with hygiene filtering |
| `SP_Insert_Into_CRM_Match_from_CRM` | Agent: "Update CRM" Step 2 (1 min) | PiXLCRM → CRM_Match unique visitor dedup |
| `SP_Insert_Into_CRM_Match_Dates_from_CRM` | Agent: "Update CRM" Step 7 (1 min) | PiXLCRM + CRM_Match → CRM_Match_Dates |
| `SP_Update_CRM_Match_Time_On_Site` | Agent: "Update CRM" Step 8 (1 min) | Calculate time-on-site from page deltas |

### 6.2 Match Procedures

| Procedure | Called By | Scope | Match Type |
|:----------|:---------|:------|:-----------|
| `SP_Update_CRM_Match_Reference_RecordID` | Step 3 (1 min) | All companies (with exclusions) | Direct IP → AutoConsumer |
| `SP_Update_CRM_Match_Reference_RecordID_Supp` | Step 4 (1 min) | Whitelisted companies | Geo-proximity (692m) |
| `M1SP_ProcessInceptionPixlData` | Step 5 (1 min) | CompanyId 12718 only | Cookie + IP + geo fallback |
| `M1SP_ProcessInceptionPixlData_Supp` | Agent: "Process Supp PureCars" (DISABLED) | CompanyId 12718 only | Rate-controlled geo (38-44% target) |
| `SP_Update_CRM_Match_Reference_RecordID_UID` | Step 6 (1 min) | CompanyId 12730 only | UID + IP + geo fallback |
| `SP_UpdateCRM_Trigger` | Agent: "Update CRM Trigger" (12h) | All matched records | Lead → Opportunity promotion |

### 6.3 Maintenance Procedures

| Procedure | Called By | Status | Purpose |
|:----------|:---------|:------:|:--------|
| `M1SP_CleanseBotMatches` | Agent: "Cleanse Bot Matches" (daily 1AM) | ENABLED | Delete CRM_Match_Dates where ≥60 hits/day/RecordID |
| `M1SP_Archive_ASP_PiXL_NEW` | Agent: "Archive ASP_PiXL_NEW" (daily 1AM) | ENABLED | Move old ASP_PiXL_New to archive |
| `M1SP_Trim_CRM_Match` | Agent: "Trim CRM_Match" (daily) | ENABLED but **no-op** (code commented out) | Was: delete unmatched records >60 days + rebuild indexes |
| `M1SP_RebuildIndexes` | Agent: "Rebuild SmartPixl Indexes" (daily 1AM) | **DISABLED** | Index maintenance |
| `UpdatePixlCRMRecords` | Agent: "UPDATE PIXLCRM" (daily) | **DISABLED** | Batch soft-delete via job queue |
| `M1SP_FixMissingPiXLCode` | Agent: every 1 min | ENABLED | Repair missing PiXL codes on creation |
| `M1SP_CreateUniqueVisitorsCounts` | Agent: "Generate Match Counts" (daily 2AM) | ENABLED | Daily unique visitor rollup |
| `M1SP_preCalculate_Dashdb_PViewsOpportunities` | Agent: "Precompute Dashboard" (hourly) | ENABLED | Dashboard pre-aggregation |

### 6.4 Export / Reporting Procedures

| Procedure | Purpose |
|:----------|:--------|
| `SP_PiXLMatch` | Billing/export: counts unique/total via `GetCRMRecords_Raw` TVF, inserts into `FlatFileBillingCounts` |
| `M1SP_Exp_Pixl_Raw` | CSV-export: calls `GetCRMRecords_Raw`, outputs 230+ `QUOTENAME`-wrapped columns |
| `SP_EXP_PiXL_DEDUPE` | Empty stub (no logic) |
| `SP_EXP_PiXL_DEDUPE_V2` | Full dedupe export: PiXLCRM → AutoConsumer → PiXL → CRM_Match → ATLAS geo-radius. `OPTION(RECOMPILE)` |
| `SP_EXP_PiXL_DEDUPE_V3` | Similar to V2: drops CreationDate output, fixes end-date, uses `OPTION(FORCE ORDER)` |
| `SP_EXP_PiXL_DEDUPE_V4` | **Does not exist** on this server |
| `M1SP_GetUserAgents` | Batch: truncates `UATable_Staging`, selects top 1000 unprocessed UAs from `UATable_DEV` |
| `M1SP_UpdateUserAgents` | Batch: joins `UATable_Staging` back to `UATable_DEV`, updates Platform/Device Type |

### 6.5 Functions (UDFs / TVFs)

| Function | Type | Used By | Purpose |
|:---------|:-----|:--------|:--------|
| `ParseCompanyAndPiXLID` | Inline TVF | Stage 0 (`M1SP_Update_ASP_PiXL_New_from_Staging`) | Parses `/{5-digit CompanyID}/{5-digit PiXLID}` from URL path |
| `SMPX_fnSplit` | TVF | `SP_UpdateCRM_Trigger` | Splits semicolon-delimited credit score bucket strings |
| `CLR.dbo.M1CLR_Decode_URI` | CLR scalar | Stage 1, Stage 6 | URL-decodes the pixel URL |
| `CLR.dbo.M1CLR_Decode_URI_oref` | CLR scalar | Stage 1 | URL-decodes original ref variant |
| `GetCRMRecords_Raw` | TVF | Export SPs (`SP_PiXLMatch`, `M1SP_Exp_Pixl_Raw`) | Returns flattened match data for export |

---

## 7. SQL Agent Jobs

### 7.1 Enabled Jobs

| Job | Schedule | Frequency | Purpose |
|:----|:---------|:----------|:--------|
| **Update ASP_PiXL_New** | 11:00–10:59 (24h) | Every 5 min | Staging → ASP_PiXL_New |
| **Update CRM** (8 steps) | 00:00–23:59 | Every 1 min | **Entire pipeline** |
| Update CRM Trigger | 00:00–23:59 | Every 12 hours | Lead → Opportunity promotion |
| Cleanse Bot Matches | 01:00 | Once daily | ≥60 hits/day cleanup |
| Generate Match Counts | 02:00 | Once daily | Daily unique visitor counts |
| Archive ASP_PiXL_NEW | 01:00 | Once daily | Archive old raw data |
| Trim CRM_Match | 00:00 | Once daily | **No-op — SP is commented out** |
| Fix Busted Pixls on Creation | 00:00–23:59 | Every 1 min | Repair missing PiXL codes |
| Precompute Dashboard for M1 | 00:00–23:59 | Every 1 hour | Dashboard aggregations |
| Update IPGEO IP's from PiXL Data | 05:00 | Once daily | Sync new IPs to IPGEO DB |
| Update 2.5 Pixls | 00:00–23:59 | Every 1 hour (weekly) | PiXL 2.5 code updates |

### 7.2 Disabled Jobs

| Job | Was Schedule | Purpose | Why It Matters |
|:----|:------------|:--------|:---------------|
| **Rebuild SmartPixl Indexes** | Daily 1AM | Index maintenance | CRM_Match is 70.4% fragmented |
| Process Supplemental for PureCars | Every 30 min | Rate-controlled inception match (38-44%) | PureCars supplemental match not running |
| UPDATE PIXLCRM | Daily | Batch soft-delete records | PiXLCRM never gets pruned |
| UpdateTTOS | Every 15 min | Extra time-on-site calculation runs | Only runs within Update CRM now |
| Truncate PiXL Staging 1 | Hourly at :50 | Redundant staging truncation | SP handles this internally |
| Truncate PiXL Staging 2 | Hourly at :05 | Redundant staging truncation | SP handles this internally |
| Truncate PiXL Staging 3 | Hourly at :20 | Redundant staging truncation | SP handles this internally |
| Truncate PiXL Staging 4 | Hourly at :35 | Redundant staging truncation | SP handles this internally |

### 7.3 Job Step Detail: "Update CRM"

| Step | SP | Database |
|:----:|:---|:---------|
| 1 | `exec SP_Insert_Into_CRM_from_PiXL_Data` | SmartPiXL |
| 2 | `exec SP_Insert_Into_CRM_Match_from_CRM` | SmartPiXL |
| 3 | `exec SP_Update_CRM_Match_Reference_RecordID` | SmartPiXL |
| 4 | `exec SP_Update_CRM_Match_Reference_RecordID_Supp` | SmartPiXL |
| 5 | `exec M1SP_ProcessInceptionPixlData` | SmartPiXL |
| 6 | `exec SP_Update_CRM_Match_Reference_RecordID_UID` | SmartPiXL |
| 7 | `exec SP_Insert_Into_CRM_Match_Dates_from_CRM` | SmartPiXL |
| 8 | `exec SP_Update_CRM_Match_Time_On_Site` | SmartPiXL |

---

## 8. Cross-Database Dependencies

| Database | State | What SmartPiXL Uses |
|:---------|:------|:--------------------|
| **SmartPiXL** | ONLINE | Primary database — all pipeline tables |
| **AutoUpdate** | ONLINE | `AutoConsumer.IP_Clean` for Inception/UID match (Stages 5-6), OptOuts processing |
| **IPGEO** | ONLINE | `IP_Location_New` for geo-proximity matching (Stages 4-6) |
| **CLR** | ONLINE | `M1CLR_Decode_URI` / `M1CLR_Decode_URI_oref` — .NET CLR URL decoding (Stages 1, 6) |
| **M1_Appends** | ONLINE | Index rebuild jobs |
| **IPA** | ONLINE | Index rebuild jobs |
| **Hydra** | ONLINE | Index rebuild jobs |
| **PiXLDebug** | ONLINE | `PiXLApiHistory` — API call logging and trimming |
| **SmartPiXL_2_DEV** | ONLINE | Development/rebuild environment |

**Critical cross-DB references in SP code:**
- `SmartPiXL.dbo.AutoConsumer` — used by Stage 3 (standard IP match)
- `AutoUpdate.dbo.AutoConsumer` — used by Stages 5-6 (Inception/UID match) — **different database, potentially different data**
- `IPGEO.dbo.IP_Location_New` — used by Stages 4-6 (geo-proximity)
- `CLR.dbo.M1CLR_Decode_URI` — used by Stages 1, 6 (URL decoding)

---

## 9. Foreign Keys & Constraints

### Existing Foreign Keys

| Child Table | Child Column | Parent Table | Parent Column |
|:-----------|:-------------|:-------------|:--------------|
| **PiXL** | **CompanyId** | **Company** | **CompanyID** |
| Company | CompanyTypeId | CompanyType | CompanyTypeID |
| Company | StatusId | Status | StatusId |
| Company | ParentCompanyId | Company | CompanyID (self-ref) |
| Company | OriginalParentCoId | Company | CompanyID (self-ref) |
| Users | CompanyId | Company | CompanyID |
| Billing | CompanyId | Company | CompanyID |
| CompanyBalances | CompanyId | Company | CompanyID |

### Notable Absence of Foreign Keys

The following pipeline tables have **NO foreign key constraints**:
- `ASP_PiXL_New` — no FK to PiXL or Company
- `PiXLCRM` — no FK to ASP_PiXL_New, PiXL, or Company
- `CRM_Match` — no FK to PiXLCRM, PiXL, or Company
- `CRM_Match_Dates` — no FK to CRM_Match, PiXLCRM, or Company

All relationships are enforced by **convention only** (matching column names in JOIN clauses).

### Triggers

**No triggers** exist on any pipeline table (ASP_PiXL_New, PiXLCRM, CRM_Match, CRM_Match_Dates, PiXL, Company).

---

## 10. Hygiene & Filtering Logic

### 10.1 Stage 0 Filters (Staging → ASP_PiXL_New)

| Filter | Logic | Purpose |
|:-------|:------|:--------|
| URL parsing | `ParseCompanyAndPiXLID()` returns NULL → row dropped | Only valid `/{5-digit}/{5-digit}` URLs pass |
| Quote escaping | `REPLACE(HTTP_REFERER, '"', '""')` | Escape double-quotes in referer fields |

No other filtering at this stage.

### 10.2 Stage 1 Pre-Filters (Building #ASP Temp Table)

These filters are applied when building the `#ASP` temp table from `ASP_PiXL_New`:

| # | Filter | Column | Logic | Purpose |
|:-:|:-------|:-------|:------|:--------|
| 1 | Time window | timestamp | `> DATEADD(hour, -6, getdate())` | Only last 6 hours |
| 2 | Empty pixel | HTTP_X_ORIGINAL_URL | `!= '/_SMART.GIF'` | Exclude bare tracking pixel hits (heartbeat/empty) |
| 3 | Already processed | RecordID | `NOT EXISTS (PiXLCRM WHERE PiXLRecordId = RecordID)` | Deduplication |
| 4 | UA prefix | HTTP_USER_AGENT | `LIKE 'Mozilla%'` | Only real browser UA strings |
| 5 | UA length | HTTP_USER_AGENT | `LEN() > 30` | Exclude stubby bot UA strings |

### 10.3 Stage 1 Blacklists (INSERT WHERE Clause)

#### Domain Blacklist (applied to `HTTP_REFERER_ROOT`)

| # | Pattern | Reason |
|:-:|:--------|:-------|
| 1 | `https://www.imore%` | Tech blog — false referral |
| 2 | `https://smart-pixl%` | Internal SmartPiXL domain — self-referral |
| 3 | `https://passiveincomemd%` | Blog — irrelevant referral |
| 4 | `https://my2.siteimprove%` | SEO crawler/tool |
| 5 | `https://mail.twc%` | Webmail client |
| 6 | `https://howtomakemyblog%` | Spam blog |
| 7 | `https://www.sogou%` | Chinese search engine bot |
| 8 | `https://info%` | Generic info pages |
| 9 | `https://www.glasply%` | Spam domain |
| 10 | `https://s3.amazonaws%` / `http://s3.amazonaws%` | AWS S3 — automated/bot traffic |
| 11 | `https://m.facebook%` / `http://m.facebook%` | Facebook in-app browser |
| 12 | `%localhost%` | Local development traffic |
| 13 | `file%` | Local file:// protocol |

#### Page-Type Blacklist (applied to `HTTP_REFERER`)

| # | Pattern | Exceptions | Purpose |
|:-:|:--------|:-----------|:--------|
| 1 | `%confirm%` | — | Confirmation/thank-you pages |
| 2 | `%account%` | — | Account/login areas |
| 3 | `%profile%` | — | Profile pages |
| 4 | `%login%` | — | Login pages |
| 5 | `%portal%` | — | Portal/dashboard areas |
| 6 | `%fe_dc_uuid%` | — | Internal tracking parameter |
| 7 | `%approval%` | `%georgesapproval%`, `%cavenderapproved%` | Approval flows (dealer names whitelisted) |
| 8 | `%//%/%thank%` | — | Thank-you pages (post-form) |
| 9 | `%dev/%` | — | Dev/staging environments |
| 10 | `%thank%.%.%` | `%jonathan%` | Thank-you subdomains (dealer whitelisted) |
| 11 | `%//%/%form%` | `%perform%`, `%formaker%` | Form pages (substring exceptions) |
| 12 | `%form%.%.%` | `%perform%`, `%formaker%` | Form subdomains (same exceptions) |

#### Duplicate Application to HTTP_X_ORIGINAL_URL

When `HTTP_X_ORIGINAL_URL` contains `?ref=`, the **entire domain + page blacklist above is re-applied** to `HTTP_X_ORIGINAL_URL` as well, because the `ref=` parameter encodes the actual referring page. Minor difference: the `cavenderapproved` exception is absent from the X_ORIGINAL_URL version.

### 10.4 Domain Validation Logic

The `DomainValidation` column is set to `1` (valid) when:

1. `HTTP_REFERER_ROOT` contains `PiXL.PiXLDomain`, **OR**
2. The original URL has `?ref=` AND the decoded URI contains `PiXL.PiXLDomain`
3. **AND** the referer does NOT contain `batchleads.io`
4. **OR** special exception: `CompanyID = 12345 AND PiXLID IN (70, 72, 73, 74, 75, 76, 77)` — always pass domain validation

### 10.5 Column Transforms in Stage 1

| Output Column | Transform Logic |
|:-------------|:---------------|
| IP | `ASP.REMOTE_ADDR` (truncated from varchar(200) to varchar(15)) |
| Http_Referer | Cascade: `?ref=` decoded URI → `HTTP_REFERER` → PiXL URL (with `https://` prefix if missing) |
| Http_Referer_Root | Same cascade as Http_Referer |
| IsPaused | `PiXL.StatusID = 6` → 1 |
| IsSuspended | `PiXL.SuspendedID = 4` → 1 |
| IsDisabled | `PiXL.StatusID IN (3, 5)` → 1 |
| Device | CASE: `MobileDeviceModel != ''` → Mobile; `Platform LIKE '%Win%' OR '%Mac%' OR '%Linux%' OR '%Unix%'` → Desktop; `Platform LIKE '%Tablet%'` → Tablet; ELSE → Other |
| Browser | CASE: `Browser LIKE '%IE%'` → InternetExplorer; `Browser LIKE '%Mozilla%'` → Safari; `Browser LIKE '%Chrome%'` → Chrome; `Browser LIKE '%Firefox%'` → Firefox; ELSE → Browser value |
| OS | CASE: `Platform LIKE '%Mac%'` → OSX; `Platform LIKE '%Linux%'` → Linux; `Platform LIKE '%Unix%'` → UNIX; `Platform LIKE '%iPhone%' OR '%iPad%' OR '%ios%'` → IOS; `Platform LIKE '%Win%'` → Windows; etc. |
| CKey | Empty string `''` (populated later by UID/Inception match) |
| Request_URI | Decoded URI, only if it does NOT contain the PiXL's own domain |
| DomainValidation | See Section 10.4 |

### 10.6 Bot Detection (Current State)

**What exists:**

| Detection Method | Where Applied | Effectiveness |
|:----------------|:-------------|:-------------|
| `Mozilla%` UA prefix | Stage 1 (pre-filter) | Catches non-browser bots (cURL, Python-requests, etc.) |
| `LEN(UA) > 30` | Stage 1 (pre-filter) | Catches stubby/empty bot UAs |
| `UserAgentsandBots.Bot = 1` | **Stage 5 only** (Inception) | Full UA classification — but only for CompanyId 12718 |
| `≥60 hits/day/RecordID` | Daily cleanup job | Retroactive — removes bot-like activity after the fact |
| `VPN_Flag IS NOT NULL` exclusion | Stage 3 (IP match) | Only prevents matching, doesn't remove from CRM_Match |

**What is NOT used in the pipeline but exists on the server:**

| Table | Rows | Potential Use |
|:------|-----:|:-------------|
| `whatismybrowser_useragent_Optimized` | 135M | Full software classification (name, type, hardware, engine) |
| `whatismybrowser_useragent` | 135M | Same schema (appears to be a duplicate) |
| `UserAgentsandBots` | 10.9M | UA → Bot (bit) lookup — only used by Stage 5 |
| `UATable` | 6.7M | Platform Name, Device Type classification |

---

## 11. Match Logic Deep Dive

### 11.1 Standard IP Match Algorithm

**SP:** `SP_Update_CRM_Match_Reference_RecordID`

```
Input: CRM_Match records (last 6 hours) where ReferenceRecordID IS NULL
Output: CRM_Match.ReferenceRecordID = AutoConsumer.RecordID

1. Scope: Active PiXLCRM records from last 6 hours
2. Exclude: VPN-flagged IPs, hardcoded company exclusions, hardcoded IP blacklist
3. Join: CRM_Match.IP = SmartPiXL.dbo.AutoConsumer.IP_Clean
4. Geo filter: PiXL zip vs consumer zip, validated via ATLAS postal centroids within PiXL.Radius
   - Fallback zip: 33309 (Fort Lauderdale)
   - Fallback radius: 9999 miles (effectively no filter)
5. Build suppression: Prevent same reference record → same Company/PiXL/IP combo
6. Rank: ROW_NUMBER() PARTITION BY (Company+PiXL+IP+UA) ORDER BY RecordCount DESC
7. Rank: ROW_NUMBER() PARTITION BY (ReferenceRecordID) ORDER BY RecordCount DESC
8. Take: RowNumber = 1 from both partitions
9. Update: CRM_Match.ReferenceRecordID = winning AutoConsumer.RecordID
```

### 11.2 Supplemental Geo Match Algorithm

**SP:** `SP_Update_CRM_Match_Reference_RecordID_Supp`

```
Input: CRM_Match records still unmatched (ReferenceRecordID IS NULL) AND company in whitelist
Output: CRM_Match.ReferenceRecordID + SupplementaryMatch = 1

1. Scope: Unmatched CRM_Match + PiXLCRM from last 6 hours
2. Company filter: Must be in CRM_Match_Supplemental_Whitelist
3. IP geolocation: Join IP to IPGEO.dbo.IP_Location_New → get lat/lon
4. Spatial match: AutoConsumer records within 692.01792 meters (0.43 miles)
   - Only PPM_Indicator IS NOT NULL (premium records)
5. When multiple candidates: ORDER BY newID() → random selection
6. Update: CRM_Match.ReferenceRecordID + set SupplementaryMatch = 1
```

### 11.3 Inception Cookie Match Algorithm

**SP:** `M1SP_ProcessInceptionPixlData`

```
Input: PiXLCRM records for CompanyId 12718 (last 60 minutes)
Output: CRM_Match.ReferenceRecordID + CRM_Match_InceptionPiXL populated

1. Extract cookie: SUBSTRING(Request_URI, CHARINDEX('&rel=', ...) + 5, 46)
2. Extract session: SUBSTRING(Request_URI, CHARINDEX('&ses=', ...) + 5, 32)
3. Bot filter: NOT EXISTS (UserAgentsandBots WHERE Bot = 1)
4. Insert new cookie/IP pairs into CRM_Match_InceptionPiXL
5. Direct IP match: IP = AutoUpdate.dbo.AutoConsumer.IP_Clean
   - Note: uses AutoUpdate database, not SmartPiXL
6. If no IP match: geo-proximity fallback (692m, PPM_Indicator required)
   - Creates spatial index on temp table at runtime
7. Propagate: Copy ReferenceRecordID from CRM_Match_InceptionPiXL → CRM_Match
```

### 11.4 UID Match Algorithm

**SP:** `SP_Update_CRM_Match_Reference_RecordID_UID`

```
Input: PiXLCRM records for CompanyId 12730 (last 60 minutes)
Output: CRM_Match.ReferenceRecordID + CRM_Match_UID populated

1. Decode URL: CLR.dbo.M1CLR_Decode_URI(Request_URI)
2. Extract UID: First 20 characters of decoded URL
3. Write UID to PiXLCRM.CKey
4. Insert new UID/IP pairs into CRM_Match_UID
5. Direct IP match: IP = AutoUpdate.dbo.AutoConsumer.IP_Clean
6. If no IP match: geo-proximity fallback (692m)
   - Creates spatial index on temp table at runtime
7. Propagate: Copy ReferenceRecordID from CRM_Match_UID → CRM_Match
```

### 11.5 Lead-to-Opportunity Promotion ("CRM Trigger")

**SP:** `SP_UpdateCRM_Trigger`  
**Schedule:** Every 12 hours  

Promotes CRM_Match records from Lead (`ProspectTypeId = 'L'`) to Opportunity (`ProspectTypeId = 'O'`) when the matched consumer meets ALL of the PiXL's campaign filter criteria:

| Filter | Source | Logic |
|:-------|:-------|:------|
| Geo proximity | PiXL.Radius + ATLAS | Consumer within PiXL's radius (fallback 9999 miles) |
| Page views | PiXL.NumberPage | `CRM_Match.PageViews >= PiXL.NumberPage` |
| Time on site | PiXL.TimeSite | `CRM_Match.TotalTimeOnSite >= DATEDIFF(second, 0, PiXL.TimeSite)` |
| Income | PiXL.IncomeRefInitial/Final | Consumer income BETWEEN range |
| Credit score | PiXL.InferredCS | Consumer CS in semicolon-delimited bucket list (parsed via `SMPX_fnSplit`) |
| Net worth | PiXL.NetWorth | Consumer net worth matches (Y/N/U flags) |
| Gender | PiXL.Gender | Consumer gender matches (M/F/U combinations) |
| Marital status | PiXL.Married | Consumer married status matches (Y/N/U) |
| Children | PiXL.Children | Consumer children flag matches (Y/N/U) |
| Recency | CRM_Match_Dates | Activity within last 26 hours |

Only processes matched records (has `ReferenceRecordID`), active/non-paused/non-suspended, with `DomainValidation = 1`.

---

## 12. Appendix: All Hardcoded Values

### Company Exclusions (in SP source code)

| CompanyId | PiXL Exclusions | Found In |
|:---------:|:---------------|:---------|
| 12345 | PiXLId 1, 29 excluded; PiXLId 66 force-included | `SP_Update_CRM_Match_Reference_RecordID` |
| 12345 | PiXLId 70, 72, 73, 74, 75, 76, 77 force domain validation | `SP_Insert_Into_CRM_from_PiXL_Data` |
| 12420 | PiXLId 98 — forced to ReferenceRecordID 518006700 | `SP_Update_CRM_Match_Reference_RecordID` |
| 12445 | PiXLId 1 excluded | `SP_Update_CRM_Match_Reference_RecordID` |
| 12598 | PiXLId 6 excluded | `SP_Update_CRM_Match_Reference_RecordID` |
| 12606 | All PiXLIds excluded | `SP_Update_CRM_Match_Reference_RecordID` |
| 12679 | PiXLId 1 — ~36 specific IPs blacklisted | `SP_Update_CRM_Match_Reference_RecordID` |
| 12718 | All excluded from standard match (Inception handles) | `SP_Update_CRM_Match_Reference_RecordID`, `SP_Update_CRM_Match_Reference_RecordID_Supp` |
| 12730 | All excluded from standard match (UID handles) | `SP_Update_CRM_Match_Reference_RecordID`, `SP_Update_CRM_Match_Reference_RecordID_Supp` |
| 12784 | PiXLId 4 excluded from supplemental | `SP_Update_CRM_Match_Reference_RecordID_Supp` |

### Hardcoded Fallback Defaults

| Value | Used As | Found In |
|:------|:--------|:---------|
| `33309` | Fallback zipcode (Fort Lauderdale, FL) | 5+ SPs (standard match, supplemental, inception, UID, trigger) |
| `9999` | Fallback radius in miles (no geo filter) | `SP_UpdateCRM_Trigger` |
| `518006700` | Forced ReferenceRecordID for 12420/98 | `SP_Update_CRM_Match_Reference_RecordID` |
| `692.01792` meters | Supplemental geo-match radius (0.43 miles) | `SP_Update_CRM_Match_Reference_RecordID_Supp`, Inception, UID |
| `1609.344 * 0.43 * @Radius/25` | Dynamic Inception supplemental radius | `M1SP_ProcessInceptionPixlData_Supp` |

### Hardcoded Thresholds

| Value | Purpose | Found In |
|:------|:--------|:---------|
| 35 | Max page views per CRM_Match record | `SP_Insert_Into_CRM_Match_from_CRM` |
| 300 seconds | Max single page-to-page transition time | `SP_Update_CRM_Match_Time_On_Site` |
| 60 hits/day | Bot detection threshold | `M1SP_CleanseBotMatches` |
| 38% min / 44% max | Inception target match rate | `M1SP_ProcessInceptionPixlData_Supp` |
| 10 | Max iterations for rate-controlled match | `M1SP_ProcessInceptionPixlData_Supp` |
| 26 hours | CRM Trigger recency window | `SP_UpdateCRM_Trigger` |
| 6 hours | Standard lookback window (Stages 1-4, 7) | Most pipeline SPs |
| 60 minutes | Inception/UID lookback window (Stages 5-6) | `M1SP_ProcessInceptionPixlData`, `SP_Update_CRM_Match_Reference_RecordID_UID` |
| 20 characters | UID extraction length | `SP_Update_CRM_Match_Reference_RecordID_UID` |
| 46 characters | Inception cookie extraction length | `M1SP_ProcessInceptionPixlData` |
| 32 characters | Inception session extraction length | `M1SP_ProcessInceptionPixlData` |
| 0–13 seconds | Fabricated time-on-site when no page pairs | `SP_Update_CRM_Match_Time_On_Site` |
| 200 | Max page hits considered for time-on-site | `SP_Update_CRM_Match_Time_On_Site` |
| 100,000 | Max records recomputed per TToS run | `SP_Update_CRM_Match_Time_On_Site` |
| 4–9 years | Vehicle age range for Inception supplemental | `M1SP_ProcessInceptionPixlData_Supp` |

### Domain & Page Blacklists (in SP source code)

See [Section 10.3](#103-stage-1-blacklists-insert-where-clause) for the complete list of 13 domain patterns and 12 page-type patterns.

### IP Blacklists (in SP source code)

- ~36 specific IPs hardcoded for CompanyId 12679 / PiXLId 1 in `SP_Update_CRM_Match_Reference_RecordID`
- IP prefix exclusion: `NOT LIKE '[34].%'` (IPs starting with 3. or 4.) in supplemental match

### Additional Domain Blacklist

- `batchleads.io` — excluded from domain validation in `SP_Insert_Into_CRM_from_PiXL_Data`

---

*End of document. Generated from live inspection of Xavier (162.255.138.254) SmartPiXL database, February 12, 2026.*
