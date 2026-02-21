---
subsystem: etl-procedures
title: ETL Stored Procedures
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - subsystems/etl
  - database/schema-map
  - subsystems/identity-resolution
  - subsystems/traffic-alerts
---

# ETL Stored Procedures

## Atlas Public

SmartPiXL's data processing engine runs automatically in the background, transforming raw visitor data into structured intelligence every 60 seconds. No manual data processing is required — everything from field extraction to identity resolution to quality scoring is handled by the automated pipeline.

## Atlas Internal

### The Four Core Procedures

| Procedure | Runs | Purpose | Typical Duration |
|-----------|------|---------|-----------------|
| `usp_ParseNewHits` | Every 60s | Extracts 300+ fields from raw data, creates device/IP/visit records | 2-5s per 10K rows |
| `usp_MatchVisits` | Every 60s | Links visits with emails to known contacts | < 1s per 10K rows |
| `usp_MaterializeVisitorScores` | Every 60s | Computes composite quality scores | 1-2s |
| `usp_MaterializeCustomerSummary` | Daily | Aggregates customer quality metrics | 5-10s |

### Three Maintenance Procedures

| Procedure | Schedule | Purpose |
|-----------|----------|---------|
| `usp_PurgeRawData` | Daily 3 AM | Deletes processed raw data |
| `usp_IndexMaintenance` | Weekly Sunday 4 AM | Rebuilds fragmented indexes |
| `usp_PipelineStatistics` | On demand | Reports pipeline health |

### How Watermarks Work

Each procedure tracks a "watermark" — the ID of the last processed record. On each run:
1. Read the watermark → "I last processed up to ID 50,000"
2. Find the max ID in the source table → "New data goes up to ID 51,200"
3. Process IDs 50,001 through 51,200
4. Update the watermark to 51,200

If the procedure crashes, the watermark wasn't updated, so the next run reprocesses the same batch. No data is lost and no data is processed twice.

## Atlas Technical

### ETL.usp_ParseNewHits

**13 phases** in a single transaction:

```sql
CREATE OR ALTER PROCEDURE ETL.usp_ParseNewHits
    @BatchSize INT = 10000
AS
BEGIN
    -- Watermark read + self-healing
    SELECT @LastId = LastProcessedId FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits';
    DECLARE @MaxParsedId INT = (SELECT ISNULL(MAX(SourceId), 0) FROM PiXL.Parsed);
    IF @MaxParsedId > @LastId SET @LastId = @MaxParsedId;
    
    BEGIN TRANSACTION;
    
    -- Phase 1: INSERT — Server + Screen + Locale (~30 fields)
    INSERT INTO PiXL.Parsed (...) SELECT ... FROM PiXL.Test WHERE Id > @LastId AND Id <= @MaxId;
    
    -- Phase 2: UPDATE — Browser + GPU + Fingerprints (~26 fields)
    UPDATE PiXL.Parsed SET ... WHERE SourceId > @LastId AND SourceId <= @MaxId;
    
    -- Phases 3-8: UPDATE — additional field groups
    -- Phase 9: COMPUTE DeviceHash
    UPDATE PiXL.Parsed SET DeviceHash = HASHBYTES('SHA2_256', CONCAT_WS('|', ...))
    
    -- Phase 10: MERGE PiXL.Device
    MERGE PiXL.Device AS tgt USING (SELECT DISTINCT DeviceHash FROM PiXL.Parsed ...) AS src
        ON tgt.DeviceHash = src.DeviceHash
        WHEN MATCHED THEN UPDATE SET LastSeen = ..., HitCount = HitCount + 1
        WHEN NOT MATCHED THEN INSERT (...) VALUES (...);
    
    -- Phase 11: MERGE PiXL.IP
    MERGE PiXL.IP AS tgt USING (...) ON tgt.IPAddress = src.IPAddress ...
    
    -- Phase 12: Extract _cp_* → JSON
    UPDATE PiXL.Visit SET ClientParamsJson = (SELECT JSON_OBJECTAGG(...))
    
    -- Phase 13: INSERT PiXL.Visit
    INSERT INTO PiXL.Visit (...) SELECT ... FROM PiXL.Parsed ...
    
    -- Update watermark
    UPDATE ETL.Watermark SET LastProcessedId = @MaxId, RowsProcessed += @Inserted
    WHERE ProcessName = 'ParseNewHits';
    
    COMMIT TRANSACTION;
END
```

**Field extraction pattern** — All fields use `dbo.GetQueryParam()` (CLR function):

```sql
TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sw') AS INT)      -- Integer fields
dbo.GetQueryParam(p.QueryString, 'tz')                         -- String fields
TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pd') AS DECIMAL(5,2))  -- Decimal fields
```

**Phase grouping rationale:**

| Phase | Field Domain | Approx. UDF Calls |
|-------|-------------|-------------------|
| 1 (INSERT) | Server, Screen, Locale | ~30 |
| 2 (UPDATE) | Browser, GPU, Fingerprints | ~26 |
| 3 (UPDATE) | Mouse, Keyboard, Input | ~18 |
| 4 (UPDATE) | Connection, Battery, Hardware | ~20 |
| 5 (UPDATE) | Bot Signals, Evasion | ~15 |
| 6 (UPDATE) | Referrer, UTM, Page | ~20 |
| 7 (UPDATE) | WebRTC, Accessibility, Privacy | ~15 |
| 8 (UPDATE) | Media, Codec, Performance | ~20 |
| 9 (COMPUTE) | DeviceHash (SHA-256) | 1 |
| 10 (MERGE) | PiXL.Device upsert | — |
| 11 (MERGE) | PiXL.IP upsert | — |
| 12 (EXTRACT) | _cp_* → JSON_OBJECTAGG | — |
| 13 (INSERT) | PiXL.Visit fact table | — |

### ETL.usp_MatchVisits

```sql
CREATE OR ALTER PROCEDURE ETL.usp_MatchVisits
    @BatchSize INT = 10000
AS
BEGIN
    -- Watermark from ETL.MatchWatermark (separate from ParseNewHits)
    SELECT @LastId = LastProcessedId FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits';
    
    -- Build candidate set: visits with non-null MatchEmail
    CREATE TABLE #Candidates (...);
    INSERT INTO #Candidates
        SELECT v.VisitID, ..., v.MatchEmail, LOWER(LTRIM(RTRIM(v.MatchEmail)))
        FROM PiXL.Visit v WHERE v.VisitID > @LastId AND v.VisitID <= @MaxId
        AND v.MatchEmail IS NOT NULL;
    
    -- Normalize emails: lowercase, trim
    UPDATE #Candidates SET NormalizedEmail = LOWER(LTRIM(RTRIM(RawEmail)));
    
    -- Lookup AutoConsumer by email → get IndividualKey/AddressKey
    UPDATE c SET
        c.IndividualKey = ac.IndividualKey,
        c.AddressKey = ac.AddressKey
    FROM #Candidates c
    INNER JOIN AutoUpdate.dbo.AutoConsumer ac
        ON ac.EMail = c.NormalizedEmail;
    
    -- INSERT/UPDATE PiXL.Match using separate operations (not MERGE)
    -- ...
    
    -- Advance watermark (covers ALL visit IDs, not just those with emails)
    UPDATE ETL.MatchWatermark SET LastProcessedId = @MaxId;
END
```

### ETL.usp_MaterializeVisitorScores

See [traffic-alerts.md](../subsystems/traffic-alerts.md) for the scoring algorithm. Key implementation:

- Watermark: `ETL.Watermark WHERE ProcessName = 'MaterializeVisitorScores'`
- Joins: PiXL.Visit → PiXL.Parsed for scoring input fields
- Computes: MouseAuthenticity, SessionQuality, CompositeQuality
- Output: INSERT into TrafficAlert.VisitorScore

### ETL.usp_MaterializeCustomerSummary

Daily batch: aggregates VisitorScore data per CompanyID per period (D/W/M).

```sql
INSERT INTO TrafficAlert.CustomerSummary (CompanyID, PeriodStart, PeriodType, ...)
SELECT CompanyID, @Today, 'D',
    COUNT(*),
    SUM(CASE WHEN BotScore > 30 THEN 1 ELSE 0 END),
    AVG(CAST(BotScore AS DECIMAL(5,2))),
    AVG(CAST(CompositeQuality AS DECIMAL(5,2))),
    COUNT(DISTINCT DeviceId),
    COUNT(DISTINCT VisitId WHERE EXISTS in PiXL.Match),
    ...
FROM TrafficAlert.VisitorScore
WHERE ReceivedAt >= @PeriodStart AND ReceivedAt < @PeriodEnd
GROUP BY CompanyID;
```

### Maintenance Procedures

**ETL.usp_PurgeRawData** — Deletes PiXL.Raw (PiXL.Test) rows that have been processed:

```sql
DELETE FROM PiXL.Test WHERE Id <= @WatermarkId AND ReceivedAt < @CutoffDate;
```

Safe: only deletes rows the watermark has passed AND that are older than the retention window.

**ETL.usp_IndexMaintenance** — Checks fragmentation and rebuilds/reorganizes:
- Fragmentation > 30% → REBUILD
- Fragmentation 10-30% → REORGANIZE
- Fragmentation < 10% → Skip

**ETL.usp_PipelineStatistics** — Returns dashboard-friendly health metrics:
- Current watermark positions
- Rows pending (source max - watermark)
- Processing rate (rows/minute)
- Last run timestamps

## Atlas Private

### Why Separate Watermarks

`usp_ParseNewHits` uses `ETL.Watermark` and `usp_MatchVisits` uses `ETL.MatchWatermark` (separate table). This is because:

1. Parse and Match have different ID spaces (Parse tracks PiXL.Raw.Id, Match tracks PiXL.Visit.VisitID)
2. Match can't run until Parse creates the Visit records
3. Match's watermark advances over ALL visit IDs (not just those with emails) to avoid re-scanning rows without emails

### MERGE vs INSERT/UPDATE

Phase 10 (Device) and Phase 11 (IP) use MERGE — they need upsert semantics for the dimension tables. Match was originally MERGE but was changed to separate INSERT + UPDATE because:

1. MERGE has known SQL Server bugs with concurrent access
2. The match table's update logic (incrementing HitCount, updating LatestVisitID) is more readable as separate statements
3. Performance difference is negligible at match volume

### AutoConsumer Cross-Database Join

`usp_MatchVisits` joins to `AutoUpdate.dbo.AutoConsumer` — a different database on the same instance. This cross-database join requires:
- The Forge's service account must have db_datareader on AutoUpdate
- AutoUpdate is on the SQL2025 instance (it was migrated from Xavier)
- Future: AutoConsumer should be moved to SmartPiXL database directly

### GetQueryParam CLR Performance

Each call to `dbo.GetQueryParam(QueryString, ParamName)` does a full scan of the querystring to find the parameter. For Phase 1 with ~30 calls per row on a batch of 10K rows, that's 300K function calls, each scanning a ~4KB string.

Total work: 300K × 4KB = ~1.2GB of string scanning per batch. At SQL Server's UTF-16 string processing speed (~2 GB/s), this takes ~0.6 seconds — acceptable.

However, this scales linearly with batch size. At 100K rows/batch, it would take ~6 seconds just for Phase 1. If this becomes a bottleneck, the alternative is to build a single-pass parser that extracts all params in one scan of the querystring.

### Transaction Isolation

The ParseNewHits transaction runs under READ COMMITTED (default). During the 2-5 second transaction window:

- PiXL.Parsed is being INSERT+UPDATE'd (lock on the batch rows)
- PiXL.Device and PiXL.IP are MERGE targets (brief exclusive locks)
- PiXL.Visit is being INSERT'd (lock on new rows)

Dashboard queries that read PiXL.Visit or PiXL.Device may experience brief blocking during the ETL window. For dashboard workloads, READ COMMITTED SNAPSHOT ISOLATION (RCSI) could be enabled to eliminate blocking at the cost of tempdb usage.

### Parse Phase Extension Protocol

When adding new browser fields to the PiXL Script:

1. Add column(s) to PiXL.Parsed (new migration)
2. Add extraction to the appropriate phase in usp_ParseNewHits (or create Phase 14+)
3. Update migration numbering
4. Test with: `EXEC ETL.usp_ParseNewHits @BatchSize = 100` on a small batch

Do NOT add columns to PiXL.Raw — the raw table stays at 9 columns. All new fields go into the querystring and are extracted by the ETL.
