---
subsystem: traffic-alerts
title: Traffic Alerts
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - subsystems/bot-detection
  - subsystems/enrichment-pipeline
  - subsystems/etl
  - architecture/sentinel
  - database/schema-map
---

# Traffic Alerts

## Atlas Public

SmartPiXL's Traffic Alert system gives you a clear, actionable picture of your website traffic quality. Instead of raw numbers, you get quality grades that tell you:

- **How authentic is my traffic?** — A composite quality score (0-100) for every visitor
- **What percentage is real?** — Bot vs. human breakdown with confidence levels
- **Is my traffic quality improving or declining?** — Trend analysis over days, weeks, and months
- **Which visitors are high-value leads?** — Lead quality scoring that identifies real prospects

**Quality Grades:**
Traffic Alert assigns letter grades to your traffic quality. A-grade traffic means authentic visitors with genuine engagement. D-grade traffic has high bot percentages, suspicious behavior patterns, or signs of automated scraping.

Your Traffic Alert dashboard shows these grades alongside actionable metrics — you see exactly where quality issues exist and how they trend over time.

## Atlas Internal

### Two Core Tables

**VisitorScore** — One row per visit with composite scoring:

| Score | Range | What It Measures |
|-------|-------|-----------------|
| Bot Score | 0-100+ | Automated behavior signals (higher = more bot-like) |
| Lead Quality Score | 0-100 | Positive human engagement signals (higher = better prospect) |
| Mouse Authenticity | 0-100 | How human-like the mouse behavior is |
| Session Quality | 0-100 | Multi-page engagement depth |
| Composite Quality | 0-100 | Weighted blend of all scores (master signal) |

Additional fields:
- Anomaly Score — Technical inconsistency count
- Combined Threat Score — Unified threat metric from browser
- Affluence Signal — LOW / MID / HIGH device classification
- Cultural Consistency — Geographic arbitrage detection (0-100)
- Contradiction Count — Impossible device configuration flags

**CustomerSummary** — Per-customer per-period aggregates:

| Metric | Purpose |
|--------|---------|
| Total / Bot / Human / Unknown Hits | Volume breakdown |
| Bot Percent | What percentage of traffic is automated |
| Avg Bot Score | Mean bot signal intensity |
| Avg Lead Quality | Mean prospect quality |
| Avg Composite Quality | Overall traffic health |
| Avg Mouse Authenticity | Behavioral quality |
| Avg Session Quality | Engagement depth |
| Unique Devices / IPs | Visitor diversity |
| Matched Visitors | How many were identified |
| Dead Internet Index | Overall traffic authenticity (0-100) |

Periods: Daily (D), Weekly (W), Monthly (M) — enabling trend analysis at all granularities.

### How Scores Are Computed

**Mouse Authenticity (0-100)** breaks down into:
- Mouse entropy (30 pts) — High entropy = human randomness
- Timing variability (20 pts) — Variable intervals between moves = human
- Speed variability (15 pts) — Variable movement speed = human
- Move count (15 pts) — Sufficient movement data exists
- No replay (10 pts) — Not a replayed recorded movement
- No scroll conflict (10 pts) — Scroll behavior is consistent with mouse data

**Session Quality (0-100)** considers:
- Page count — Multiple pages = deeper engagement
- Session duration — Longer sessions = real interest
- Navigation pattern — Varied page visits vs. single-page bounce

**Composite Quality (0-100)** blends everything:
- Mouse authenticity, session quality, lead quality score
- Bot score (negative influence)
- Contradiction count (negative influence)
- Cultural consistency

### Sentinel API Endpoints

The Sentinel process exposes the Traffic Alert data as a REST API:

| Endpoint | Data Source |
|----------|-----------|
| `GET /api/traffic-alert/visitors` | Paginated visitor detail |
| `GET /api/traffic-alert/visitors/{id}` | Single visitor scoring breakdown |
| `GET /api/traffic-alert/customers` | Customer overview with quality grades |
| `GET /api/traffic-alert/trend` | Time-series of customer metrics |

All endpoints read from materialized views — no heavy computation at query time.

## Atlas Technical

### TrafficAlert Schema

```sql
-- TrafficAlert.VisitorScore (per-visit scoring)
CREATE TABLE TrafficAlert.VisitorScore (
    VisitorScoreId      BIGINT IDENTITY PK,
    VisitId             BIGINT FK → PiXL.Visit,
    DeviceId            BIGINT FK → PiXL.Device,
    CompanyID           INT,

    -- Client-side scores
    BotScore            INT NULL,
    AnomalyScore        INT NULL,
    CombinedThreatScore INT NULL,

    -- Forge enrichment scores
    LeadQualityScore    INT NULL,
    AffluenceSignal     VARCHAR(4) NULL,    -- LOW|MID|HIGH
    CulturalConsistency INT NULL,           -- 0-100
    ContradictionCount  INT NULL,

    -- Computed composite scores
    SessionQuality      INT NULL,           -- 0-100
    MouseAuthenticity   INT NULL,           -- 0-100
    CompositeQuality    INT NULL,           -- 0-100 (master score)

    ReceivedAt          DATETIME2(3),
    MaterializedAt      DATETIME2(3) DEFAULT SYSUTCDATETIME()
);
```

### Materialization Procedure — `usp_MaterializeVisitorScores`

Watermark-based: reads new PiXL.Visit rows, joins to PiXL.Parsed for the scoring input fields, computes the three derived scores, and inserts into `TrafficAlert.VisitorScore`.

**Mouse Authenticity scoring algorithm:**

```sql
-- Entropy: 0-30 pts
CASE WHEN MouseEntropy >= 70 THEN 30
     WHEN MouseEntropy >= 40 THEN 20
     WHEN MouseEntropy >= 20 THEN 10
     ELSE 5 END
+
-- Timing CV: 0-20 pts
CASE WHEN TimingCV > 0.5 THEN 20
     WHEN TimingCV > 0.3 THEN 15
     WHEN TimingCV > 0.1 THEN 10
     ELSE 0 END
+
-- Speed CV: 0-15 pts
-- ...similar bucketing...
+
-- Move count: 0-15 pts
CASE WHEN MoveCountBucket >= 100 THEN 15
     WHEN MoveCountBucket >= 50 THEN 10
     ELSE 5 END
+
-- No replay: 10 pts if ReplayDetected = 0
-- No scroll conflict: 10 pts if ScrollContradiction = 0
```

### Dashboard Views

```sql
-- vw_TrafficAlert_VisitorDetail — Single visitor with all scores
-- vw_TrafficAlert_CustomerOverview — Customer summary with quality grades  
-- vw_TrafficAlert_Trend — Time-series of customer metrics
```

These views join VisitorScore → Visit → Device → Parsed to produce denormalized read models for the Sentinel API.

### Indexes

| Index | Columns | Purpose |
|-------|---------|---------|
| `UQ_VisitorScore_VisitId` | VisitId (unique) | 1:1 visit lookup |
| `IX_VisitorScore_Company_ReceivedAt` | CompanyID, ReceivedAt + INCLUDE | Dashboard date-range queries |
| `IX_VisitorScore_LowQuality` | CompanyID, CompositeQuality WHERE < 30 | Filtered index for threat alerts |
| `IX_VisitorScore_DeviceId` | DeviceId + INCLUDE | Device-level scoring history |
| `IX_CustomerSummary_Period` | PeriodType, PeriodStart DESC + INCLUDE | Trend queries |

### CustomerSummary Materialization

`usp_MaterializeCustomerSummary` runs daily, computing aggregates from VisitorScore:

```sql
INSERT INTO TrafficAlert.CustomerSummary
SELECT CompanyID, @Today, 'D',
    COUNT(*), SUM(CASE WHEN BotScore > 30 THEN 1 ELSE 0 END),
    SUM(CASE WHEN BotScore <= 30 THEN 1 ELSE 0 END),
    -- ...aggregate calculations...
FROM TrafficAlert.VisitorScore
WHERE ReceivedAt >= @Today AND ReceivedAt < @Tomorrow
GROUP BY CompanyID;
```

Uses separate INSERT + UPDATE instead of MERGE for performance (MERGE has known performance issues with large datasets in SQL Server).

## Atlas Private

### Scoring Calibration

The scoring weights are hand-tuned based on domain knowledge:

| Score | Calibration Status | Known Issues |
|-------|-------------------|-------------|
| Bot Score | Well-calibrated | Works well for known bot patterns; misses novel bots |
| Mouse Authenticity | Well-calibrated | Entropy thresholds (70/40/20) validated against labeled bot data |
| Session Quality | Needs tuning | Currently penalizes single-page visits too heavily — legitimate landing page traffic scores low |
| Composite Quality | Acceptable | Weights were set during development, not validated at scale |
| Lead Quality | New, unvalidated | Deployed in Phase 5, no production validation yet |

### The "Dead Internet Index"

The per-customer Dead Internet Index (0-100) measures how much of a customer's traffic appears artificial:

- 0 = Healthy traffic (diverse devices, genuine engagement, low bot rate)
- 100 = All traffic appears automated (high bot rate, no mouse activity, datacenter IPs, replayed behaviors)

Formula components:
- Bot hit percentage (weighted 3×)
- Mouse activity rate (percentage of visits with mouse moves)
- Device diversity (unique devices / total hits)
- Datacenter IP percentage
- Contradiction rate
- Replay rate

This metric is sensitive to customer segment. A B2B SaaS company with 70% of traffic from datacenter IPs might score 40 even though the traffic is legitimate (developers accessing from corporate networks). Interpretation requires customer context.

### Materialization Timing

The materialization chain runs after the main ETL:

```
usp_ParseNewHits (60s cycle)
    → usp_MatchVisits
    → usp_MaterializeVisitorScores
    → usp_MaterializeCustomerSummary (daily only)
```

`usp_MaterializeVisitorScores` runs every cycle but only processes visits that have completed parsing. If parsing falls behind, scoring is delayed but not skipped.

`usp_MaterializeCustomerSummary` runs once per day — it summarizes the full day's VisitorScore data. Weekly and monthly summaries are computed on the appropriate day boundaries.

### Filtered Index Strategy

The `IX_VisitorScore_LowQuality` filtered index (`WHERE CompositeQuality < 30`) is specifically designed for the threat alert dashboard — "show me all suspicious visitors for customer X." Only ~5-10% of rows qualify, so the filtered index is much smaller than a full index and dramatically faster for this common query pattern.

If the CompositeQuality threshold changes (e.g., to < 40), the index needs to be dropped and recreated with the new filter. This is a manual operation documented in the troubleshooting guide.

### No MERGE in Materialization

The materialization procs use separate UPDATE + INSERT instead of MERGE. This is deliberate:

1. SQL Server's MERGE has known bugs with concurrent access (potential data loss)
2. MERGE generates worse execution plans for large batch operations
3. UPDATE + INSERT with NOT EXISTS is clearer and more predictable

The watermark pattern already prevents duplicate processing, so MERGE's upsert semantics aren't needed.
