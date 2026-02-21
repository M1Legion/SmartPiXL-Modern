---
subsystem: etl
title: ETL Pipeline
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - architecture/data-flow
  - architecture/forge
  - subsystems/identity-resolution
  - database/etl-procedures
  - database/schema-map
---

# ETL Pipeline

## Atlas Public

SmartPiXL's ETL (Extract, Transform, Load) pipeline automatically processes raw visitor data into structured, queryable intelligence. Every 60 seconds, new data is parsed, enriched, and organized so it's ready for analysis within minutes of a visitor interaction.

**What the ETL delivers:**
- **Structured profiles** — Raw browser data is separated into 300+ distinct fields for precise querying
- **Device records** — Each unique device is identified and tracked across visits
- **IP intelligence** — Network addresses are enriched with geolocation and metadata
- **Visit history** — Individual visits are linked to devices and contacts
- **Contact matching** — Form submissions retroactively link anonymous visits to known individuals
- **Traffic quality scores** — Composite scoring identifies bots, high-value leads, and traffic anomalies

All processing is automated and continuous — no manual data manipulation required.

## Atlas Internal

### ETL Cycle — What Happens Every 60 Seconds

The Forge runs the ETL cycle automatically:

1. **Parse New Hits** (`usp_ParseNewHits`) — Reads unprocessed records from PiXL.Raw (9 columns), extracts 300+ individual fields into PiXL.Parsed, creates/updates device and IP records, creates visit records. This is the heavy-lift operation.

2. **Match Visits** (`usp_MatchVisits`) — Looks at visits with email addresses, matches them to known contacts in AutoConsumer, creates identity links in PiXL.Match. This is how anonymous visitors become known leads.

3. **Materialize Visitor Scores** (`usp_MaterializeVisitorScores`) — Computes composite quality scores (mouse authenticity, session quality, composite quality) and writes to TrafficAlert.VisitorScore.

4. **Materialize Customer Summary** (`usp_MaterializeCustomerSummary`) — Aggregates daily/weekly/monthly quality metrics per customer into TrafficAlert.CustomerSummary.

### Data Flow

```
PiXL.Raw (9 columns)
    ↓ usp_ParseNewHits (13 phases)
PiXL.Parsed (300+ columns)
PiXL.Device (one per unique device)
PiXL.IP (one per unique IP address)
PiXL.Visit (one per visit — the fact table)
    ↓ usp_MatchVisits
PiXL.Match (links visits to contacts)
    ↓ usp_MaterializeVisitorScores
TrafficAlert.VisitorScore (per-visit quality scores)
    ↓ usp_MaterializeCustomerSummary
TrafficAlert.CustomerSummary (per-customer period aggregates)
```

### Watermark Pattern

Every ETL procedure tracks its progress using a watermark — a record of the last processed ID. If the procedure crashes mid-run:
- The watermark hasn't been updated (it's updated at the end)
- Next run picks up from the same point
- Self-healing: if the target table has rows beyond the watermark (partial commit), the watermark auto-advances

This guarantees exactly-once processing with crash recovery.

### Volume Expectations

| Metric | Typical | Peak |
|--------|---------|------|
| PiXL.Raw rows per minute | 100-1,000 | 10,000+ during campaigns |
| Parse time per batch (10K rows) | 2-5 seconds | 8 seconds (complex sessions) |
| Match time per batch | < 1 second | 2 seconds |
| Parsed columns populated | ~160 per typical web visit | 300+ for full-featured browsers |

## Atlas Technical

### usp_ParseNewHits — 13 Phases

The parsing procedure runs as a single transaction with 13 sequential phases:

| Phase | Operation | Approx. Fields |
|-------|-----------|----------------|
| 1 | INSERT — Server + Screen + Locale | ~30 fields |
| 2 | UPDATE — Browser + GPU + Fingerprints | ~26 fields |
| 3 | UPDATE — Mouse + Keyboard + Input behavior | ~18 fields |
| 4 | UPDATE — Connection + Battery + Hardware | ~20 fields |
| 5 | UPDATE — Bot Signals + Evasion | ~15 fields |
| 6 | UPDATE — Referrer + UTM + Page metadata | ~20 fields |
| 7 | UPDATE — WebRTC + Accessibility + Privacy | ~15 fields |
| 8 | UPDATE — Media + Codec + Performance | ~20 fields |
| 9 | COMPUTE — DeviceHash from 5 fingerprint fields | SHA-256 |
| 10 | MERGE — PiXL.Device (upsert by DeviceHash) | 1 row per device |
| 11 | MERGE — PiXL.IP (upsert by IPAddress) | 1 row per IP |
| 12 | EXTRACT — `_cp_*` client params → JSON | JSON_OBJECTAGG |
| 13 | INSERT — PiXL.Visit (fact table, 1:1 with Parsed) | ~20 FK fields |

All field extraction uses the CLR function `dbo.GetQueryParam(QueryString, 'paramName')`:

```sql
TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sw') AS INT)  -- Screen width
dbo.GetQueryParam(p.QueryString, 'tz')                     -- Timezone string
```

### PiXL.Raw → Parsed Mapping

PiXL.Raw has 9 columns. The full visitor payload is carried in `QueryString`:

| Raw Column | Content |
|-----------|---------|
| `Id` | Identity PK (BIGINT) |
| `CompanyID` | Customer identifier |
| `PiXLID` | Pixel configuration ID |
| `IPAddress` | Visitor IP |
| `UserAgent` | Browser User-Agent |
| `Referer` | HTTP Referer header |
| `QueryString` | 159 browser fields + 45+ `_srv_*` enrichment fields |
| `RequestPath` | URL path of the tracking pixel |
| `ReceivedAt` | Server-side timestamp |

PiXL.Parsed expands the QueryString into 300+ typed columns (INT, VARCHAR, DECIMAL, etc.) using `TRY_CAST` for safe type conversion.

### DeviceHash Computation (Phase 9)

Computed in SQL from 5 fingerprint fields:

```sql
HASHBYTES('SHA2_256',
    CONCAT_WS('|',
        CanvasFingerprint,
        AudioFingerprint,
        WebGLFingerprint,
        FontList,
        ScreenResolution
    ))
```

### Watermark Self-Healing

```sql
-- If Parsed has rows beyond the watermark (partial commit recovery)
DECLARE @MaxParsedId INT = (SELECT ISNULL(MAX(SourceId), 0) FROM PiXL.Parsed);
IF @MaxParsedId > @LastId SET @LastId = @MaxParsedId;
```

This handles the case where the transaction committed the INSERT into Parsed but crashed before updating the watermark.

### EtlBackgroundService (Forge)

The `EtlBackgroundService` in the Forge project runs the ETL cycle:

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    await RunEtlCycleAsync(stoppingToken);
    await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
}
```

Each cycle executes the stored procedures via `SqlCommand`:
1. `EXEC ETL.usp_ParseNewHits @BatchSize = 10000`
2. `EXEC ETL.usp_MatchVisits @BatchSize = 10000`
3. `EXEC ETL.usp_MaterializeVisitorScores`
4. `EXEC ETL.usp_MaterializeCustomerSummary`

### Batch Sizing

Default batch size: 10,000 rows. Configurable via `ForgeSettings.EtlBatchSize`. If > 10,000 rows are pending, the next cycle picks up where the previous left off (watermark-based).

### Maintenance Procedures

| Procedure | Schedule | Purpose |
|-----------|----------|---------|
| `ETL.usp_PurgeRawData` | Daily 3 AM | Deletes PiXL.Raw rows that are already processed (Id ≤ watermark) and older than retention period |
| `ETL.usp_IndexMaintenance` | Weekly Sunday 4 AM | Rebuilds/reorganizes indexes based on fragmentation |
| `ETL.usp_PipelineStatistics` | On demand | Returns pipeline health metrics (watermark positions, lag, throughput) |

## Atlas Private

### Phase 1 INSERT vs Phase 2-8 UPDATEs

The ETL uses INSERT in Phase 1 to create the Parsed row with the first batch of fields, then UPDATEs for Phases 2-8. This is a SQL Server optimization:

- One INSERT with 300+ columns would be extremely wide and hard to maintain
- Multiple UPDATEs allow grouping logically related fields
- SQL Server's in-memory page handling means the UPDATEs are efficient (same page, no I/O between phases)
- Each phase can be independently verified and debugged

The trade-off: 8 passes over the batch instead of 1. At 10K rows, the difference is ~2s vs ~3s — acceptable.

### dbo.GetQueryParam CLR Function

The querystring parsing is done by a CLR scalar function (SQL CLR, net48 assembly in SmartPiXL_CLR database). This was chosen over T-SQL string manipulation because:

1. T-SQL `CHARINDEX`/`SUBSTRING` chains are unreadable at scale (159+ params)
2. CLR can use `Span<char>` for zero-allocation parsing (but net48 CLR uses `String.IndexOf` — Span is not available in net48)
3. Single function call per param extraction, inlined by CLR
4. ~10× faster than equivalent T-SQL for repeated extraction from the same string

The CLR function is in `SmartPiXL.SqlClr/Functions/`. It takes `(NVARCHAR(MAX) queryString, NVARCHAR(200) paramName)` and returns `NVARCHAR(2000)`.

### JSON_OBJECTAGG (Phase 12)

Phase 12 extracts `_cp_*` (client params — arbitrary key-value pairs from the visitor) into a SQL Server 2025 native `json` column:

```sql
SELECT JSON_OBJECTAGG(key : value)
FROM STRING_SPLIT(p.QueryString, '&')
CROSS APPLY (VALUES (
    SUBSTRING(value, 1, CHARINDEX('=', value) - 1),
    SUBSTRING(value, CHARINDEX('=', value) + 1, LEN(value))
)) AS kv(key, value)
WHERE key LIKE '_cp_%'
```

This is a SQL Server 2025-specific feature. The `json` type stores the result in a binary-optimized format (not text JSON).

### Watermark Edge Cases

1. **Watermark ahead of max Raw ID** — Happens if PiXL.Raw was truncated or restored from backup. Fix: `UPDATE ETL.Watermark SET LastProcessedId = 0 WHERE ProcessName = 'ParseNewHits'`

2. **Multiple Forge instances** — If two Forge instances run simultaneously (misconfiguration), they'd both read the same watermark and process duplicates. The MERGE in Phases 10-11 (Device, IP) handles duplicates gracefully. Phase 1 INSERT would create duplicate Parsed rows. This is why Forge should only ever be a single instance.

3. **BIGINT watermark** — Migration 33 changed the watermark from INT to BIGINT. At 10K rows/minute, INT would overflow after ~4 years. BIGINT gives 1.4 million more years.

### Transaction Size

The entire batch (up to 10K rows) runs in a single transaction. This means:
- Lock escalation can occur on PiXL.Parsed → table lock → blocks other queries during the ~3s parse window
- If the transaction fails midway, all work for that batch is rolled back (watermark doesn't advance)
- The self-healing check (`@MaxParsedId > @LastId`) handles the edge case where the COMMIT succeeds but the watermark UPDATE fails (extremely rare — they're in the same transaction, but theoretically possible with connection drops)

### Performance at Scale

At current volume (~1,000 rows/minute), the 60-second ETL cycle processes ~1,000 rows in ~1-2 seconds with plenty of headroom. The system is designed for 10× growth without architectural changes.

At 10K rows/minute, the batch size becomes the bottleneck — each cycle would need to process 10K rows, taking ~3-5s. The 60-second interval provides 55+ seconds of buffer.

At 100K rows/minute, the procedure would need to run continuously (processing ~100K rows per batch, taking ~30s), with only 30 seconds of buffer. This would require either reducing batch latency (parallel phases) or running the ETL more frequently.
