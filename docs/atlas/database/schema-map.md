---
subsystem: schema-map
title: Database Schema Map
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - subsystems/etl
  - subsystems/identity-resolution
  - subsystems/traffic-alerts
  - database/etl-procedures
  - database/sql-features
---

# Database Schema Map

## Atlas Public

SmartPiXL stores visitor intelligence in a structured database designed for fast querying and long-term analytics. Your data is organized into purpose-built tables that separate raw capture from processed intelligence, enabling both real-time dashboards and deep historical analysis.

**What's stored:**
- Complete visitor profiles with 300+ data points per visit
- Device fingerprints for returning visitor identification
- Geographic and network intelligence per IP address
- Identity resolution linking anonymous visits to known contacts
- Traffic quality scores and customer health metrics

All data is retained according to your organization's policies and is queryable through the SmartPiXL API and dashboard interfaces.

## Atlas Internal

### Database Overview

| Property | Value |
|----------|-------|
| **Instance** | SQL Server 2025 Developer |
| **Database** | SmartPiXL |
| **CLR Database** | SmartPiXL_CLR (separate, for CLR assemblies) |
| **Key Tables** | ~15 core tables across 6 schemas |
| **Row Volume** | PiXL.Raw grows ~1,000 rows/minute (varies by traffic) |

### Schema Organization

SmartPiXL uses 6 schemas to organize tables by domain:

| Schema | Purpose | Key Tables |
|--------|---------|------------|
| **PiXL** | Core domain — raw data, parsed fields, devices, IPs, visits, matches | Raw, Parsed, Device, IP, Visit, Match, AutoConsumer, Settings, Company |
| **ETL** | Pipeline control — watermarks, procedures | Watermark, MatchWatermark |
| **IPAPI** | IP geolocation — synced from Xavier | IPGeo (342M+ rows) |
| **TrafficAlert** | Visitor scoring + customer summaries | VisitorScore, CustomerSummary |
| **Graph** | Identity resolution graph tables | Device (node), Person (node), IpAddress (node), ResolvesTo (edge), UsesIP (edge) |
| **Geo** | Zipcode polygon data (from Census ZCTA) | Zipcode |

### Data Flow Through Tables

```
PiXL.Raw (capture)
    ↓ ETL.usp_ParseNewHits
PiXL.Parsed (300+ columns)
PiXL.Device (device dimension)
PiXL.IP (IP dimension)
PiXL.Visit (fact table)
    ↓ ETL.usp_MatchVisits
PiXL.Match (identity links)
    ↓ ETL.usp_MaterializeVisitorScores
TrafficAlert.VisitorScore
    ↓ ETL.usp_MaterializeCustomerSummary
TrafficAlert.CustomerSummary
```

### Table Sizes (Typical Production)

| Table | Rows | Growth Rate | Notes |
|-------|------|-------------|-------|
| PiXL.Raw | Purged daily | ~1,000/min | Deleted after ETL processes them |
| PiXL.Parsed | Millions | ~1,000/min | 300+ columns, largest table |
| PiXL.Device | 100K-1M | Slow (unique devices) | Clustered on DeviceHash |
| PiXL.IP | 100K-1M | Slow (unique IPs) | Clustered on IPAddress |
| PiXL.Visit | Millions | ~1,000/min | 1:1 with Parsed |
| PiXL.Match | Hundreds of thousands | 5-15% of visits | Identity resolution output |
| IPAPI.IPGeo | 342M+ | Synced from Xavier | IP range lookup reference |
| TrafficAlert.VisitorScore | Millions | ~1,000/min | Per-visit scoring |
| TrafficAlert.CustomerSummary | Thousands | Daily/weekly/monthly | Aggregated quality metrics |

## Atlas Technical

### PiXL Schema — Core Tables

#### PiXL.Raw (Capture Table)

The landing table for all incoming visitor data. 9 columns only.

```sql
PiXL.Raw (
    Id              BIGINT IDENTITY PK,
    CompanyID       INT,
    PiXLID          INT,
    IPAddress       VARCHAR(50),
    UserAgent       NVARCHAR(1000),
    Referer         NVARCHAR(2000),
    QueryString     NVARCHAR(MAX),      -- 159 browser fields + _srv_* enrichments
    RequestPath     NVARCHAR(500),
    ReceivedAt      DATETIME2(3)
)
```

Notes:
- Written by `SqlBulkCopyWriterService` (Forge) or `DatabaseWriterService` (Edge fallback)
- QueryString carries the entire payload — browser fields and Forge enrichments
- Purged daily after ETL processing (watermark-safe)
- Formerly named `PiXL.Test` (renamed via migration 28)

#### PiXL.Parsed (Expanded Fields)

300+ typed columns extracted from Raw.QueryString by `usp_ParseNewHits`:

```sql
PiXL.Parsed (
    SourceId            BIGINT PK,          -- = PiXL.Raw.Id
    CompanyID           INT,
    PiXLID              INT,
    IPAddress           VARCHAR(50),
    ReceivedAt          DATETIME2(3),
    RequestPath         NVARCHAR(500),
    ServerUserAgent     NVARCHAR(1000),
    ServerReferer       NVARCHAR(2000),
    IsSynthetic         BIT,
    Tier                INT,
    
    -- Screen & Display (~15 columns)
    ScreenWidth         INT, ScreenHeight INT, ScreenAvailWidth INT, ScreenAvailHeight INT,
    ViewportWidth       INT, ViewportHeight INT, OuterWidth INT, OuterHeight INT,
    ScreenX             INT, ScreenY INT, ColorDepth INT, PixelRatio DECIMAL(5,2),
    ScreenOrientation   VARCHAR(30), ScreenExtended BIT,
    
    -- Locale & Timezone (~10 columns)
    Timezone            VARCHAR(100), TimezoneOffsetMins INT, ClientTimestampMs BIGINT,
    TimezoneLocale      VARCHAR(20), DateFormatSample VARCHAR(100),
    NumberFormatSample   VARCHAR(50), RelativeTimeSample VARCHAR(100),
    Language            VARCHAR(30), LanguageList VARCHAR(500),
    
    -- Browser & GPU (~12 columns)
    BrowserName         VARCHAR(100), BrowserVersion VARCHAR(50),
    GPU                 NVARCHAR(200), GpuVendor VARCHAR(100),
    CanvasFingerprint   VARCHAR(50), AudioFingerprint VARCHAR(50),
    WebGLFingerprint    VARCHAR(100), FontList VARCHAR(2000),
    
    -- ... 250+ more columns grouped by domain ...
    
    -- Forge enrichment fields (from _srv_* params)
    KnownBot            BIT, BotName NVARCHAR(200),
    Browser             NVARCHAR(100), BrowserVer NVARCHAR(50),
    OS                  NVARCHAR(100), OSVer NVARCHAR(50),
    DeviceType          VARCHAR(30), DeviceModel NVARCHAR(100), DeviceBrand NVARCHAR(100),
    RdnsHostname        NVARCHAR(200), RdnsCloud BIT,
    MmCountryCode       CHAR(2), MmRegion NVARCHAR(100), MmCity NVARCHAR(100),
    -- ... 30+ more Forge enrichment columns ...
    
    -- Computed columns
    DeviceHash          VARBINARY(32)   -- SHA-256 of 5 fingerprint fields
)
```

#### PiXL.Device (Device Dimension)

```sql
PiXL.Device (
    DeviceId            BIGINT IDENTITY PK (NONCLUSTERED),
    DeviceHash          VARBINARY(32) UNIQUE CLUSTERED,
    FirstSeen           DATETIME2(3),
    LastSeen            DATETIME2(3),
    HitCount            INT DEFAULT 1,
    FingerprintVector   VECTOR(64) NULL,    -- For similarity matching
    UaVector            VECTOR(32) NULL     -- For UA drift detection
)
```

Clustered on DeviceHash for MERGE performance. Nonclustered PK for FK references.

#### PiXL.IP (IP Dimension)

```sql
PiXL.IP (
    IpId                BIGINT IDENTITY PK (NONCLUSTERED),
    IPAddress           VARCHAR(50) UNIQUE CLUSTERED,
    IpType              VARCHAR(20),        -- Public/Private/CGNAT/Loopback
    IsDatacenter        BIT,
    DatacenterProvider  VARCHAR(20),        -- AWS/GCP/NULL
    FirstSeen           DATETIME2(3),
    LastSeen            DATETIME2(3),
    HitCount            INT DEFAULT 1
)
```

#### PiXL.Visit (Fact Table)

```sql
PiXL.Visit (
    VisitID             BIGINT PK CLUSTERED,    -- = PiXL.Raw.Id
    CompanyID           INT,
    PiXLID              INT,
    DeviceId            BIGINT FK → PiXL.Device,
    IpId                BIGINT FK → PiXL.IP,
    ReceivedAt          DATETIME2(3),
    ClientParamsJson    JSON NULL,              -- _cp_* params as native json
    MatchEmail          NVARCHAR(200) NULL,     -- Extracted from _cp_email
    CreatedAt           DATETIME2(3)
)
-- JSON INDEX on ClientParamsJson for ($.email, $.hid) paths
-- Filtered index on MatchEmail WHERE NOT NULL
```

#### PiXL.Match (Identity Resolution)

```sql
PiXL.Match (
    MatchId             BIGINT IDENTITY PK,
    CompanyID           INT,
    PiXLID              INT,
    MatchType           VARCHAR(20),            -- 'email', 'ip', 'geo'
    MatchKey            VARCHAR(256),           -- The matched value
    IndividualKey       VARCHAR(35),            -- → AutoConsumer
    AddressKey          VARCHAR(35),            -- → AutoConsumer
    DeviceId            BIGINT FK,
    IpId                BIGINT FK,
    FirstVisitID        BIGINT FK → PiXL.Visit,
    LatestVisitID       BIGINT FK → PiXL.Visit,
    FirstSeen           DATETIME2(3),
    LastSeen            DATETIME2(3),
    HitCount            INT DEFAULT 1,
    ConfidenceScore     FLOAT NULL,
    MatchedAt           DATETIME2(3) NULL
)
```

### ETL Schema

```sql
ETL.Watermark (
    ProcessName         VARCHAR(50) PK,
    LastProcessedId     BIGINT DEFAULT 0,
    RowsProcessed       BIGINT DEFAULT 0,
    LastRunAt           DATETIME2(3)
)

ETL.MatchWatermark (
    ProcessName         VARCHAR(50) PK,
    LastProcessedId     BIGINT DEFAULT 0
)
```

### IPAPI Schema

```sql
IPAPI.IPGeo (
    IpGeoId             BIGINT IDENTITY PK,
    IpFrom              BIGINT,             -- IP range start (integer)
    IpTo                BIGINT,             -- IP range end (integer)
    CountryCode         CHAR(2),
    Region              NVARCHAR(100),
    City                NVARCHAR(100),
    Isp                 NVARCHAR(200),
    IsProxy             BIT,
    IsMobile            BIT,
    SyncedAt            DATETIME2(3)
)
-- Integer bucket pattern: IP → INT → range lookup
```

### Graph Schema (SQL Server 2025)

```sql
-- NODE tables
Graph.Device    AS NODE  (DeviceId BIGINT, DeviceHash VARBINARY(32))
Graph.Person    AS NODE  (Email VARCHAR(256), IndividualKey VARCHAR(35))
Graph.IpAddress AS NODE  (IPAddress VARCHAR(50), FirstSeen, LastSeen)

-- EDGE tables
Graph.ResolvesTo AS EDGE (Confidence FLOAT, RelationType VARCHAR(50), CreatedAt)
Graph.UsesIP     AS EDGE (FirstSeen, LastSeen, HitCount INT)
```

Traversal: `MATCH(d-(e)->p)` syntax for multi-hop identity resolution.

### TrafficAlert Schema

See [traffic-alerts.md](../subsystems/traffic-alerts.md) for full schema details.

### Configuration Tables

```sql
PiXL.Company (CompanyID INT PK, CompanyName, Active BIT, CreatedAt)
PiXL.Settings (SettingsId INT PK, CompanyID FK, ScriptVersion, ...)
```

### Index Strategy

| Pattern | Purpose | Examples |
|---------|---------|---------|
| Clustered on natural key | MERGE performance | PiXL.Device → DeviceHash, PiXL.IP → IPAddress |
| Nonclustered PK | FK reference performance | DeviceId, IpId |
| Filtered indexes | Skip NULL FKs, low-quality records | Visit.DeviceId WHERE NOT NULL, VisitorScore WHERE CompositeQuality < 30 |
| JSON INDEX | Path-based JSON queries | Visit.ClientParamsJson for $.email, $.hid |
| Covering INCLUDE | Dashboard query optimization | CustomerSummary period queries |

## Atlas Private

### PiXL.Test vs PiXL.Raw Naming

The table is still physically named `PiXL.Test` in the database (legacy from early development). Migration 28 was supposed to rename it to `PiXL.Raw`, but the rename was deferred to avoid breaking the running ETL. All code and documentation should refer to it as `PiXL.Raw`, but the actual table name in SQL is `PiXL.Test`.

The ETL proc references `PiXL.Test` in its queries. This is a known debt item — rename needs coordinated deployment with ETL proc update.

### PiXL.Parsed Column Count

The 300+ columns in PiXL.Parsed are split across 8 UPDATE phases (plus the initial INSERT). This means:
- adding a new field requires modifying the correct phase
- Column ordering in the table definition doesn't match query ordering
- Migrations 42-44 added Forge Tier 1/2/3 columns; migration 56 expanded Parsed further

The wide table design was deliberate: SQL Server handles wide tables well with row-overflow and LOB storage. An EAV (entity-attribute-value) pattern was rejected because:
1. Analytics queries on typed columns are 10-100× faster than JSON_VALUE
2. Schema enforcement catches data quality issues at insert time
3. Column statistics enable query optimizer intelligence

### IPAPI.IPGeo 342M+ Rows

This table is the largest in the database. It's synced from Xavier via `IpApiSyncService`:
- Full initial sync: ~4 hours via SqlBulkCopy with batch size 100K
- Incremental sync: every 6 hours, only new/updated ranges
- Growth: ~100K new ranges/month from IPAPI Pro

The integer bucket pattern (`IpFrom BIGINT, IpTo BIGINT`) is critical for lookup performance. The alternative (storing as VARCHAR and parsing) would be 50× slower for range queries.

### Graph Table Population Gap

As of V1 completion, graph tables are created but sparsely populated. The ETL doesn't yet have a dedicated "populate graph" step. Graph nodes are created by:
1. DeviceHash → Graph.Device (during usp_ParseNewHits Phase 10)
2. Email match → Graph.Person (during usp_MatchVisits)

Graph edges are created by:
1. Device → Person (email match)
2. Device → IP (visit record)

Missing: Cross-device graph edges based on behavioral similarity, subnet co-occurrence, and vector distance. This is Phase 10+ work.

### Partitioning Strategy

Migration 28 sets up partitioning on PiXL.Parsed by SourceId ranges (100M rows per partition). This enables:
- Partition elimination for watermark-range queries
- Partition-level index maintenance
- Future partition switching for archival

Current partition count: typically 1-2 (depending on total volume). At 1M rows/day, the first partition boundary is reached after ~100 days.

### Missing FK on PiXL.Parsed

PiXL.Parsed.SourceId references PiXL.Raw.Id, but there's no FK constraint. This is intentional:
- Raw rows are purged daily after processing
- An FK would prevent purging (child rows in Parsed reference parent rows in Raw)
- The relationship is maintained by ETL logic, not by database constraint
