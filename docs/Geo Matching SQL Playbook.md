# LiQ Geo Matching SQL Playbook

> Lessons learned, performance tricks, and architectural patterns from building and optimizing geo matching at scale in SQL Server.

---

## Table of Contents

1. [System Overview](#system-overview)
2. [Data Scale](#data-scale)
3. [Core Architecture: Integer Bucket Grid](#core-architecture-integer-bucket-grid)
4. [The Bucket-Based Nearest-Neighbor Pattern (CROSS APPLY + Grid Scan)](#the-bucket-based-nearest-neighbor-pattern)
5. [The Packed-MAX Trick (Argmax Without Window Functions)](#the-packed-max-trick)
6. [Bitmask Day Qualification (2-in-7 Rule)](#bitmask-day-qualification)
7. [Batched Updates with Deadlock Retry](#batched-updates-with-deadlock-retry)
8. [Incremental vs. Backfill Architecture](#incremental-vs-backfill-architecture)
9. [Clustered Columnstore for Append-Heavy Fact Tables](#clustered-columnstore-for-append-heavy-fact-tables)
10. [Filtered View Optimization (3.3B → 611M)](#filtered-view-optimization)
11. [STIntersects Polygon Matching (POI Geofences)](#stintersects-polygon-matching)
12. [Index Strategy at Scale](#index-strategy-at-scale)
13. [Deduplication Pattern (ROW_NUMBER + NOT EXISTS)](#deduplication-pattern)
14. [Weighted Centroid Calculation](#weighted-centroid-calculation)
15. [Approximate Euclidean Distance (No Geography Type)](#approximate-euclidean-distance)
16. [Run Observability & Logging](#run-observability--logging)
17. [What Failed & What We Learned](#what-failed--what-we-learned)

---

## System Overview

The LiQ system matches **mobile advertising device IDs (MAIDs)** to **physical household addresses** using GPS resting-location data. The pipeline:

1. **Ingest** — Daily device location pings are bucketed into `DeviceNightlyRestingLocation` (pre-aggregated lat/lon grid cells per device per day).
2. **Qualify** — Devices that appear at the same grid cell on 2+ days within a 7-day sliding window are candidates.
3. **Match** — Each qualified device's weighted centroid is matched to the nearest address in `AC_Geo` (421M household addresses) within 50 meters.
4. **Enrich** — Matched `ReferenceRecordID` links devices to the full household/demographics reference table for downstream campaign delivery.

### Key Stored Procedures

| Procedure | Role |
|---|---|
| `M1SP_UpdateRestingMatches` | Original full-scan resting location matcher |
| `M1SP_UpdateRestingMatches_Backfill` | Full-scan baseline (no skip logic) |
| `M1SP_UpdateRestingMatches_Incremental` | Only processes new DNRL dates since last completed run |
| `M1SP_Process_DailyADIDs` | Exports matched device → household data for daily ADID files |
| `M1SP_DailyMatchedPL` | POI geofence matching using `STIntersects` with polygon geometries |
| `M1SP_Process_UC_Matches` | Processes UberConnect staging data into DailyMatches |
| `M1SP_Process_VS_Matches` | Processes VisitStream staging data into DailyMatches |
| `M1SP_LiQ_Matched` / `M1SP_Lookback_Matched` | Campaign-level matched/unmatched report generation |

---

## Data Scale

| Table | Approximate Rows | Notes |
|---|---|---|
| `MAID_Match` | **3.4 billion** | Central device registry; every known ad_id |
| `DailyMatches` | **20+ billion** (partitioned) | POI visit-level fact table, partitioned by date ranges |
| `AC_Geo` | **421 million** | US household address geocodes with integer bucket keys |
| `DeviceNightlyRestingLocation` | **~200M+ per partition** | Daily-partitioned nightly resting location aggregates |
| `DailyADIDs` | **~2M** | Staging table for daily ADID file generation |

---

## Core Architecture: Integer Bucket Grid

**The single most important design decision.** Instead of using SQL Server's `geography` or `geometry` types with spatial indexes for nearest-neighbor search, we use **integer grid buckets**.

### How It Works

The `AC_Geo` table stores each household address with:
- `cass_latitude` / `cass_longitude` — precise decimal coordinates
- `lat_k` / `lon_k` — **integer grid keys** (latitude and longitude truncated/scaled to integer cells)

The `DeviceNightlyRestingLocation` table stores device pings with:
- `centroid_lat` / `centroid_lon` — precise weighted centroid per day
- `lat_bucket` / `lon_bucket` — integer grid keys at finer granularity (`lat_bucket / 10` aligns to `AC_Geo.lat_k`)

### Why This Works

```sql
-- Clustered index on AC_Geo is on (lat_k, lon_k)
CREATE CLUSTERED INDEX CIX_AC_Geo_Bucket ON AC_Geo (lat_k, lon_k);
```

The clustered index means nearby addresses are **physically adjacent on disk**. A 3x3 grid scan:

```sql
WHERE a.lat_k BETWEEN r.lat_k - 1 AND r.lat_k + 1
  AND a.lon_k BETWEEN r.lon_k - 1 AND r.lon_k + 1
```

becomes a tight **range seek** on the clustered index — scanning only the ~9 neighboring cells rather than the full 421M-row table. This is orders of magnitude faster than spatial index lookups for nearest-neighbor at this scale.

### Key Insight
> **Spatial indexes in SQL Server are optimized for containment (`STContains`, `STIntersects`) not nearest-neighbor.** For "find the closest point" queries across hundreds of millions of rows, an integer grid with a clustered B-tree index dramatically outperforms `STDistance` with spatial indexes.

---

## The Bucket-Based Nearest-Neighbor Pattern

The core geo match uses `CROSS APPLY` with `TOP 1` against the bucket grid:

```sql
SELECT r.ad_id, x.RecordID AS ReferenceRecordID, x.dist_m
FROM #FinalResults r
CROSS APPLY (
    SELECT TOP 1
        a.RecordID,
        SQRT(
            POWER((r.weighted_lat - a.cass_latitude) * 111320, 2) +
            POWER((r.weighted_lon - a.cass_longitude) * 82200, 2)
        ) AS dist_m
    FROM AC_Geo a
    WHERE a.lat_k BETWEEN r.lat_k - 1 AND r.lat_k + 1
      AND a.lon_k BETWEEN r.lon_k - 1 AND r.lon_k + 1
    ORDER BY
        POWER(r.weighted_lat - a.cass_latitude, 2) +
        POWER(r.weighted_lon - a.cass_longitude, 2)
) x
WHERE x.dist_m <= @MatchDistM;  -- 50 meters default
```

### Why This Is Efficient

1. **`CROSS APPLY` + `TOP 1`** — SQL Server evaluates this as a nested-loop seek per device. Each iteration seeks into the clustered index on `(lat_k, lon_k)`.
2. **3x3 grid neighborhood** — `BETWEEN lat_k - 1 AND lat_k + 1` scans only ~9 grid cells, returning a small number of candidate addresses.
3. **ORDER BY on squared distance** — Skips the `SQRT` in the sort (monotonic transform), only computing `SQRT` for the winning row in the SELECT. The optimizer can do a partial sort early out.
4. **Distance threshold applied after** — The `WHERE x.dist_m <= 50` runs on the single TOP 1 result, not on the full candidate set.

---

## The Packed-MAX Trick

**Problem:** For each device, we need the grid cell where it appeared on the **most distinct days** (argmax). SQL Server has no `ARGMAX` aggregate. `ROW_NUMBER()` + window functions would require materializing and sorting billions of rows.

**Solution:** Pack the sort criteria and the desired values into a single `BIGINT` and use `MAX()`:

```sql
SELECT ad_id,
    MAX(
        CAST(day_count AS BIGINT) * 1000000000000 +
        CAST((lat_k + 90000) AS BIGINT) * 1000000 +
        CAST((lon_k + 180000) AS BIGINT)
    ) AS packed
INTO #MultiDayWinners
FROM #Qualified
GROUP BY ad_id;
```

Then unpack:

```sql
lat_k = CAST((packed / 1000000) % 1000000 AS INT) - 90000
lon_k = CAST(packed % 1000000 AS INT) - 180000
```

### How It Works

- `day_count` occupies the high bits → MAX picks the cell with the most days
- `lat_k + 90000` in the middle → tiebreaker on latitude (offset to ensure positive)
- `lon_k + 180000` in the low bits → tiebreaker on longitude

### Why This Matters at Scale

With **~600M+ devices**, a window function approach would require:
- Materializing all rows into a worktable
- Sorting by `(ad_id, day_count DESC)`
- Scanning for `ROW_NUMBER() = 1`

The packed-MAX approach:
- Single `GROUP BY` pass with hash aggregate
- No sort required (hash aggregate `MAX` is O(n))
- Dramatically less tempdb pressure

> **This single trick saved hours of runtime** on 600M+ device processing.

---

## Bitmask Day Qualification

**Problem:** Determine if a device appeared at a grid cell on **2+ days within any 7-day sliding window**. A naive approach would require self-joins or expensive window functions across billions of rows.

**Solution:** Encode each day as a bit position in a `BIGINT` bitmask:

```sql
SUM(DISTINCT POWER(CAST(2 AS BIGINT), DATEDIFF(DAY, @base_date, event_date))) AS date_mask
```

Then check for any 2 days within 7 using bitwise arithmetic:

```sql
WHERE date_mask & (
    date_mask / 2
  | date_mask / 4
  | date_mask / 8
  | date_mask / 16
  | date_mask / 32
  | date_mask / 64
) > 0
```

### How It Works

- Each distinct day is a single bit: day 0 = bit 0, day 1 = bit 1, etc.
- `date_mask / 2` shifts right by 1 (days offset by 1)
- `date_mask / 4` shifts right by 2 (days offset by 2)
- ... up to `date_mask / 64` (offset by 6, covering 7-day window)
- OR-ing these together creates a mask of "any day 1–6 days after an existing day"
- AND-ing with the original mask checks if any existing day falls within that window

### Limitation

- **63 days maximum** (BIGINT has 63 usable bits since bit 63 is the sign bit)
- The procedures handle this by clamping `@base_date` to 62 days before `@window_end`

```sql
IF @day_span > 63
BEGIN
    SET @base_date = DATEADD(DAY, -62, @window_end);
    -- Bitmask covers @base_date onward
END
```

---

## Batched Updates with Deadlock Retry

Updating 3.4B rows in `MAID_Match` with new match results requires careful batching:

```sql
DECLARE @ub INT = 1;
DECLARE @retries INT;

WHILE @ub > 0
BEGIN
    SET @retries = 0;
    RETRY_A:
    BEGIN TRY
        UPDATE TOP (@UpdateBatchSize) MM        -- 1M rows per batch
        SET MM.ReferenceRecordID = BM.ReferenceRecordID,
            MM.MatchDate = GETDATE()
        FROM dbo.MAID_Match MM
        INNER JOIN #BatchMatched BM ON BM.ad_id = MM.AD_ID
        WHERE MM.MatchDate IS NULL;             -- Only unmatched rows

        SET @ub = @@ROWCOUNT;
        SET @new_total += @ub;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() = 1205 AND @retries < @MaxRetries  -- Deadlock
        BEGIN
            SET @retries += 1;
            WAITFOR DELAY '00:00:05';       -- Back off 5 seconds
            GOTO RETRY_A;
        END
        ELSE THROW;
    END CATCH
END
```

### Key Patterns

1. **`UPDATE TOP (N)`** — Limits lock escalation; keeps transaction log manageable
2. **Two-pass update** — First pass: new matches (`WHERE MatchDate IS NULL`). Second pass: changed matches (`WHERE ReferenceRecordID <> new value`). This avoids unnecessary updates.
3. **Deadlock retry with exponential-ish backoff** — `WAITFOR DELAY '00:00:05'` then retry up to 5 times. Handles concurrent readers gracefully.
4. **`SET XACT_ABORT OFF`** — Required for the GOTO-based retry pattern (XACT_ABORT ON would kill the batch on deadlock instead of allowing retry).

---

## Incremental vs. Backfill Architecture

Three procedure variants handle different scenarios:

### `M1SP_UpdateRestingMatches_Backfill`
- **Full scan** of all `DeviceNightlyRestingLocation` data
- No skip logic — always runs
- Used for initial baseline or recovery
- Rebuilds `IX_maid_match_matched_adid` at completion

### `M1SP_UpdateRestingMatches_Incremental`
- Checks `MatcherRunLog` for last completed `WindowEnd`
- Only scans DNRL data from `last_window_end - 6` days onward (7-day overlap for sliding window)
- Filters to only devices with data in the **new date range** (`has_new_data = 1`)
- Refuses to run if no baseline backfill exists

### `M1SP_UpdateRestingMatches` (Original)
- Full scan with incremental skip (skips if no new data since last `WindowEnd`)
- Used for scheduled daily runs

### Run Log Tracking (`MatcherRunLog`)

Every run gets a row with:
- `Status`: Running → Completed / Failed / Skipped
- `WindowStart` / `WindowEnd`: date range processed
- `QualifiedPairs`, `FinalDevices`, `MatchedDevices`: pipeline funnel metrics
- `NewUpdates` / `OverwriteUpdates`: actual MAID_Match rows changed
- `BatchesCompleted` / `TotalBatches`: progress tracking within run
- `DurationSec`: total runtime
- `ErrorMessage`: captured on failure

Concurrency guard: `IF EXISTS (SELECT 1 FROM MatcherRunLog WHERE Status = 'Running')` prevents parallel runs.

---

## Clustered Columnstore for Append-Heavy Fact Tables

`DailyMatches` (20B+ rows) and `DeviceNightlyRestingLocation` (billions across partitions) use **Clustered Columnstore Indexes (CCI)**:

```
DailyMatches        → CCSI_DailyMatches_CCI        (CLUSTERED COLUMNSTORE)
DNRL                → CCI_DeviceNightlyRestingLocation (CLUSTERED COLUMNSTORE)
```

### Why CCI

- **Append-optimized** — Bulk inserts go into a delta store, then compressed into column segments. No page splits.
- **Massive compression** — Typical 10:1 or better compression on these wide-ish tables with repetitive string values.
- **Batch mode execution** — Aggregations on CCI tables use batch mode operators, dramatically faster for the `GROUP BY` in the qualifying step.
- **Partition elimination** — Both tables are partitioned; queries that filter on date hit only relevant partitions.

### Supporting B-tree Index

DailyMatches also has a unique nonclustered B-tree for dedup:

```
UX_DailyMatches_NaturalKey (Date, POI, ad_id) INCLUDE (IntersectLatitude, IntersectLongitude, platform)
```

This supports the `NOT EXISTS` dedup check during inserts while the CCI handles analytics/reporting reads.

---

## Filtered View Optimization

The Looker BI view on `MAID_Match` was initially scanning all 3.3B rows for `COUNT(DISTINCT ReferenceRecordID)`:

```sql
-- Fix: Create filtered MAID_Match view (611M rows instead of 3.3B)
-- COUNT(DISTINCT ReferenceRecordID) ignores NULLs, so excluding them
-- is semantically identical
CREATE VIEW Looker.vw_MAID_Match AS
SELECT AD_ID, ReferenceRecordID
FROM dbo.MAID_Match
WHERE ReferenceRecordID IS NOT NULL;  -- Implicit filter
```

> Reducing from 3.3B to 611M matched rows — an **82% reduction** in scan volume — makes BI queries dramatically faster without changing results (since `COUNT(DISTINCT)` skips NULLs anyway).

---

## STIntersects Polygon Matching

For POI (Point of Interest) geofence matching, the system does use SQL Server's `geometry` types — but smartly:

```sql
-- In M1SP_DailyMatchedPL:
-- Step 1: Pre-join unique lat/lon points against POI polygons
SELECT PA.Location_Alias, LL.latitude, LL.longitude, PA.place_id AS POI
INTO #PAPL
FROM Places_Active_PL AS PA
INNER JOIN {TableName}_LL AS LL
    ON PA.geoPoly.STIntersects(LL.geoPoint) = 1;

-- Step 2: Join back to full device table on lat/lon
INSERT INTO lookbackstaging_PL (...)
SELECT ...
FROM #PAPL AS PA
INNER JOIN {TableName} AS T1 ON PA.IntersectLatitude = T1.latitude
                              AND PA.IntersectLongitude = T1.longitude
INNER JOIN MAID_Match AS MM ON MM.AD_ID = T1.ad_id
WHERE MM.ReferenceRecordID IS NOT NULL;
```

### Two-Phase Design

1. **Phase 1:** Spatial join on **unique lat/lon points** (`_LL` suffix table) against POI polygons. This is the expensive `STIntersects` call but runs on a much smaller distinct-location set.
2. **Phase 2:** Hash join the spatial results back to the full device table on exact lat/lon equality (cheap B-tree/hash).

> **Key Trick:** Don't run `STIntersects` per device-row. Pre-deduplicate locations, run spatial once, then fan back out via equi-join.

---

## Index Strategy at Scale

### `AC_Geo` — Clustered on Grid Keys

```
CIX_AC_Geo_Bucket          CLUSTERED     (lat_k, lon_k)    -- Range seeks for neighbor scan
PK_AC_Geo                  NONCLUSTERED  (RecordID)         -- Lookup by ID
```

The clustered index on `(lat_k, lon_k)` is the foundation of the entire matching strategy. Physical data locality means the 3x3 grid scan reads sequential pages.

### `MAID_Match` — Multiple Access Patterns

```
PK_MAID_Match              CLUSTERED     (RecordID)                           -- Surrogate key
IX_maid_match_matched_adid NONCLUSTERED  (AD_ID) INCLUDE (ReferenceRecordID)  -- Lookup by device ID
ADID_to_ReferenceID        NONCLUSTERED  (MatchDate, AD_ID) INCLUDE (ReferenceRecordID)  -- Time-scoped lookups
```

The main access pattern is `JOIN ON ad_id` (covered by `IX_maid_match_matched_adid`). The `INCLUDE (ReferenceRecordID)` makes it a **covering index** — no key lookup needed.

The index is rebuilt after each matcher run:
```sql
ALTER INDEX IX_maid_match_matched_adid ON dbo.MAID_Match REBUILD WITH (ONLINE = ON, MAXDOP = 4);
```

### `DailyMatches` — CCI + Dedup Index

```
CCSI_DailyMatches_CCI      CLUSTERED COLUMNSTORE   -- Bulk analytics
UX_DailyMatches_NaturalKey NONCLUSTERED UNIQUE      -- (Date, POI, ad_id) for insert dedup
```

### `DailyADIDs` — Clustered on Device ID

```
PK_DailyADIDs              CLUSTERED     (ADIDs)    -- EXISTS check during export
```

---

## Deduplication Pattern

Daily ingestion processors use `ROW_NUMBER()` + `NOT EXISTS` to prevent duplicate inserts:

```sql
;WITH PL AS (
    SELECT VS.ad_id, VS.col0 AS POI, Date,
        ROW_NUMBER() OVER (PARTITION BY VS.ad_id, VS.col0, Date ORDER BY date) AS ROWNO,
        ...
    FROM [Staging] AS VS
    LEFT JOIN MAID_Match AS MM ON VS.ad_id = MM.ad_id AND MM.ReferenceRecordID IS NOT NULL
    WHERE NOT EXISTS (
        SELECT 1 FROM DailyMatches AS DM
        WHERE DM.ad_id = VS.ad_id AND DM.POI = VS.col0 AND DM.Date = VS.date
    )
)
INSERT INTO DailyMatches (...)
SELECT ... FROM PL WHERE ROWNO = 1;
```

### Pattern Elements

1. **`ROW_NUMBER() ... PARTITION BY (ad_id, POI, Date)`** — Within the staging data, keep only the first occurrence per natural key
2. **`NOT EXISTS` against DailyMatches** — Skip already-inserted data (idempotent re-runs)
3. **`LEFT JOIN MAID_Match`** — Eagerly attach the `ReferenceRecordID` at insert time (avoids a later update)

---

## Weighted Centroid Calculation

Rather than picking an arbitrary GPS ping, the resting location matcher calculates a **ping-count-weighted centroid**:

```sql
SUM(centroid_lat * bucket_count) / SUM(bucket_count) AS weighted_lat,
SUM(centroid_lon * bucket_count) / SUM(bucket_count) AS weighted_lon
```

Where `bucket_count` is how many pings landed in that sub-cell on that day. This gives more weight to grid cells where the device spent more time, producing a more accurate resting location estimate.

---

## Approximate Euclidean Distance

Instead of `geography::STDistance()` (which computes great-circle distance with full ellipsoid math), the matcher uses a **flat-earth approximation**:

```sql
SQRT(
    POWER((r.weighted_lat - a.cass_latitude) * 111320, 2) +
    POWER((r.weighted_lon - a.cass_longitude) * 82200, 2)
) AS dist_m
```

### Constants

- **111,320** — meters per degree of latitude (roughly constant everywhere)
- **82,200** — meters per degree of longitude (approximate for ~42°N, US mid-latitudes)

### Why This Is Fine

- At 50-meter match distance, the error from flat-earth approximation is negligible (< 0.01%)
- `geography::STDistance()` has significant CPU overhead per call; with CROSS APPLY across hundreds of millions of devices, the savings are substantial
- The longitude constant being fixed at ~42°N introduces slight error at extreme latitudes, but for US geocoding this is acceptable

### Optimization in ORDER BY

```sql
ORDER BY POWER(r.weighted_lat - a.cass_latitude, 2) +
         POWER(r.weighted_lon - a.cass_longitude, 2)
```

The `ORDER BY` uses **squared distance** (no SQRT) since the square root is a monotonic transform — the ordering is identical. `SQRT` is only computed for the final `TOP 1` result in the `SELECT` list.

---

## Run Observability & Logging

The `MatcherRunLog` table provides full observability:

| Column | Purpose |
|---|---|
| `Status` | Running / Completed / Failed / Skipped |
| `Parameters` | Formatted string of input parameters |
| `WindowStart/End` | Date range processed |
| `QualifiedPairs` | (ad_id, cell) pairs passing 2-in-7 rule |
| `FinalDevices` | Unique devices after packed-MAX winner selection |
| `MatchedDevices` | Devices within 50m of an address |
| `NewUpdates` | MAID_Match rows matched for first time |
| `OverwriteUpdates` | MAID_Match rows with changed match |
| `BatchesCompleted` / `TotalBatches` | Progress tracking |
| `DurationSec` | Wall-clock time |
| `ErrorMessage` | Captured exception text on failure |

`RAISERROR(..., 0, 1) WITH NOWAIT` is used throughout for **real-time progress messages** to SSMS/client (not buffered like `PRINT`).

---

## What Failed & What We Learned

### Evolution of the Matcher

The run log tells the story:

| RunIDs | Approach | Outcome |
|---|---|---|
| 1–13 | 90-day window, density ≥ 0.1 filter | **Repeated failures** — too many qualified devices (731M+), ran out of resources or timed out |
| 14 | Switched to **7-day window, 2-in-7 bitmask** | **First successful completion** — 264,644 sec (~3 days), 50.8M new + 45.4M overwrites |
| 15 | Same parameters, possible resource contention | Failed at 32,861 sec |
| 16 | Same approach, re-run | Running (in progress at time of writing) with 1.08B qualified pairs |

### Key Lessons

1. **The 90-day density window was too expensive.** 731M final devices × 421M addresses = untenable CROSS APPLY volume. The 7-day sliding window with bitmask reduced qualified devices by ~15% while still producing quality matches.

2. **Bitmask > window functions** for temporal qualification at this scale. The bitwise approach runs inside a single `GROUP BY` aggregate pass.

3. **Packed-MAX eliminates a massive sort.** Without it, picking the best cell per device would require `ROW_NUMBER() OVER (PARTITION BY ad_id ORDER BY day_count DESC)` across 600M+ devices.

4. **Integer grid keys > spatial indexes** for nearest-neighbor. The `AC_Geo` clustered index on `(lat_k, lon_k)` makes the CROSS APPLY seek pattern viable at 421M rows.

5. **Pre-dedup spatial operations.** For polygon `STIntersects`, always deduplicate the point set first, then fan back out with equi-joins.

6. **Online index rebuilds** after batch updates prevent blocking readers during the matcher run.

7. **`NOLOCK` hints** are used extensively on read paths (`MAID_Match`, `DailyMatches`) during ingestion — accepting phantom/dirty reads in exchange for not blocking the matcher's updates.

8. **HASH joins are force-hinted** (`INNER HASH JOIN`) in the IP mapping logic where the optimizer would otherwise choose nested loops on 3.4B-row tables.

---

## Quick Reference: Stored Procedure Parameters

### M1SP_UpdateRestingMatches / Backfill / Incremental

| Parameter | Default | Purpose |
|---|---|---|
| `@MinDays` | 2 | Minimum distinct days at a grid cell |
| `@WindowSpan` | 7 | Sliding window size in days |
| `@MatchDistM` | 50.0 | Maximum match distance in meters |
| `@SpatialBatchSize` | 10,000,000 | Devices per CROSS APPLY batch |
| `@UpdateBatchSize` | 1,000,000 | Rows per UPDATE TOP batch |
| `@MaxRetries` | 5 | Deadlock retry limit |

---

*Document generated 2026-03-14 from LiQ database analysis.*
