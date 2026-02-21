---
subsystem: identity-resolution
title: Identity Resolution
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - subsystems/fingerprinting
  - subsystems/enrichment-pipeline
  - subsystems/etl
  - database/sql-features
---

# Identity Resolution

## Atlas Public

When the same person visits a customer's website from different devices, browsers, or over time, traditional analytics sees multiple anonymous visitors. SmartPiXL's identity resolution connects these fragmented visits into a unified visitor profile.

**How SmartPiXL identifies returning visitors:**
- **Device fingerprinting** — Over 80 browser signals create a unique device identifier that persists across sessions
- **Email matching** — When a visitor fills out a form, SmartPiXL links all prior anonymous visits to their identity
- **Cross-device linking** — The same person on a phone and laptop is recognized as one visitor when they share identifying signals
- **Cookie-independent** — SmartPiXL doesn't rely on cookies, so clearing browser data or using incognito mode doesn't break identification

**What this means for customers:**
- See a visitor's complete journey across sessions and devices
- Know which anonymous visitors later became leads
- Understand true engagement depth — not just page views per session, but visits per person over weeks

## Atlas Internal

### How Identity Resolution Works

Identity resolution happens in two stages:

**Stage 1: Device Matching (Real-Time)**
Every visit is assigned a DeviceHash — a fingerprint derived from the visitor's hardware and browser configuration. The ETL process matches incoming visits to known devices. If the DeviceHash matches an existing device record, the visit is linked. If not, a new device record is created.

**Stage 2: Contact Matching (ETL)**
When a visitor submits a form with an email address, SmartPiXL's ETL links that email to the device. All prior visits from that device — even anonymous ones — are retroactively associated with the contact. This is the "AutoConsumer" concept: the consumer reveals themselves, and SmartPiXL connects the dots backwards.

### Match Types

| Type | How It Works | Confidence |
|------|-------------|------------|
| **Email** | Form submission contains email → matched to AutoConsumer | 100% |
| **DeviceHash** | Exact fingerprint match across visits | Very High |
| **Vector Similarity** | SQL Server VECTOR_DISTANCE finds similar-but-not-identical fingerprints | High |
| **Cross-Device Graph** | Graph traversal links devices that share identifiers (email, subnet, behavioral patterns) | Medium |

### Matching Volume

The ETL processes matching every 60 seconds. A typical cycle:
- Processes 100-1,000 new visits
- Matches 60-80% to existing devices
- Creates new device records for the remainder
- Links 5-15% of visits to known contacts (higher for returning customers with forms)

### Privacy Note

SmartPiXL's identity resolution works within the visitor's website experience. It does NOT:
- Track visitors across the public internet
- Buy or use third-party data
- Store personal information beyond what the visitor voluntarily provides (email via form submission)
- Use cookies for identification

## Atlas Technical

### Database Schema

**PiXL.Match** — The identity resolution join table:

```sql
PiXL.Match (
    MatchID         BIGINT IDENTITY PK,
    VisitID         BIGINT FK → PiXL.Visit,
    DeviceId        BIGINT FK → PiXL.Device,
    MatchType       VARCHAR(20),     -- 'Email', 'DeviceHash', 'Vector', 'Graph'
    MatchConfidence DECIMAL(5,2),    -- 0-100%
    MatchedAt       DATETIME2(3),
    ConsumerId      BIGINT NULL FK → PiXL.AutoConsumer
)
```

**PiXL.AutoConsumer** — Contact records created from form submissions:

```sql
PiXL.AutoConsumer (
    ConsumerId      BIGINT IDENTITY PK,
    CompanyID       INT,
    Email           NVARCHAR(320),
    DeviceId        BIGINT FK → PiXL.Device,
    FirstSeenAt     DATETIME2(3),
    LastSeenAt      DATETIME2(3)
)
```

### ETL Matching Process — `usp_MatchVisits`

The stored procedure runs after `usp_ParseNewHits` and performs matching in priority order:

1. **Email Match** — If the parsed visit contains an email field (from form capture), look up or create an AutoConsumer record. Link all visits from the same DeviceId to this consumer.

2. **DeviceHash Match** — Match the visit's DeviceHash to PiXL.Device. If found, create a Match record with `MatchType = 'DeviceHash'`.

3. **Vector Similarity** — For visits without an exact DeviceHash match, use `VECTOR_DISTANCE('cosine', v.FingerprintVector, d.FingerprintVector)` to find similar devices. Threshold: cosine distance < 0.15.

4. **Graph Traversal** — For remaining unmatched visits, traverse the Graph.DeviceNode → Graph.IdentityEdge graph to find connected devices.

### Graph-Based Identity Resolution (SQL Server 2025)

```sql
-- Schema
Graph.DeviceNode    AS NODE    (DeviceId, DeviceHash, FirstSeen, LastSeen)
Graph.IdentityEdge  AS EDGE    (Confidence, RelationType, CreatedAt)

-- Traversal query
SELECT d2.DeviceId, e.Confidence, e.RelationType
FROM Graph.DeviceNode d1,
     Graph.IdentityEdge e,
     Graph.DeviceNode d2
WHERE MATCH(d1-(e)->d2)
  AND d1.DeviceId = @SourceDeviceId
  AND e.Confidence >= 0.70;
```

Edge types (`RelationType`):
- `SameEmail` — Same email submitted from two devices
- `SameSubnet` — Same /24 subnet with overlapping sessions
- `SimilarVector` — VECTOR_DISTANCE < 0.15
- `SharedSession` — Session stitching linked two device hashes

### Session Stitching (Forge — Step 7)

`SessionStitchingService` maintains an in-memory session graph keyed by DeviceHash:

```csharp
var sessionResult = _sessionStitching.RecordHit(deviceHash, pagePath);
// Returns: SessionId, HitNumber, DurationSec, PageCount
```

Session boundary: 30-minute inactivity timeout. Output params feed into both PiXL.Visit creation and TrafficAlert scoring.

### Cross-Customer Intelligence (Forge — Step 8)

`CrossCustomerIntelService` tracks IP+FP combinations across customer boundaries:

```csharp
var crossResult = _crossCustomerIntel.RecordHit(record.IPAddress, deviceHash, record.CompanyID);
// Returns: DistinctCompanies, WindowMinutes, IsAlert
```

Alert threshold: same IP+FP hitting 3+ distinct CompanyIDs within 5 minutes.

### Vector Infrastructure (SQL Server 2025)

```sql
-- PiXL.Device FingerprintVector column
ALTER TABLE PiXL.Device ADD FingerprintVector VECTOR(64) NULL;

-- Similarity search
SELECT DeviceId, VECTOR_DISTANCE('cosine', @InputVector, FingerprintVector) AS Distance
FROM PiXL.Device
WHERE VECTOR_DISTANCE('cosine', @InputVector, FingerprintVector) < 0.15
ORDER BY Distance;
```

The fingerprint vector is generated by the `FingerprintStabilityService` in the Edge from 64 normalized browser signal values.

## Atlas Private

### Identity Resolution Accuracy

The matching pipeline has different accuracy profiles:

| Match Type | False Positive Rate | False Negative Rate | Notes |
|-----------|-------------------|-------------------|----|
| Email | ~0% | N/A (explicit) | Only fires on form submission |
| DeviceHash | ~2% | ~15% | FP rate from shared devices (family computers); FN from browser updates changing fingerprint |
| Vector | ~5% | ~30% | Threshold 0.15 is conservative; could be tuned per customer |
| Graph | ~8% | ~40% | Graph noise from shared networks; 2-hop limit prevents contamination |

### The AutoConsumer Problem

The email-based matching creates a one-to-one mapping: Email → DeviceId → All visits from that device. This works well for:
- B2B: Employee fills out form on work laptop → all prior visits from that laptop are linked
- Lead gen: Contact submits form → retroactive journey visible

It breaks for:
- Shared devices: Two people use the same work computer → one email gets all visits
- Multiple emails: Same person uses work + personal email → two separate AutoConsumer records

We don't attempt to solve shared devices — the DeviceHash is correct, and the email association is "whoever filled out the form." This is the right behavior for B2B use cases.

### VECTOR_DISTANCE Known Bug

SQL Server 2025 preview has a known issue where `VECTOR_DISTANCE` in a `WHERE` clause can't be parameterized in certain query plan shapes. The workaround is to use a subquery or CTE:

```sql
-- Fails intermittently:
WHERE VECTOR_DISTANCE('cosine', @v, FingerprintVector) < 0.15

-- Workaround (always works):
;WITH Candidates AS (
    SELECT DeviceId, VECTOR_DISTANCE('cosine', @v, FingerprintVector) AS Dist
    FROM PiXL.Device
)
SELECT * FROM Candidates WHERE Dist < 0.15;
```

This is logged in the implementation log and should be revisited when SQL Server 2025 goes GA.

### Graph Contamination Risk

Unrestricted graph traversal would link every device on the internet through transitive connections. SmartPiXL limits this:

1. **2-hop maximum** — Only direct connections and one intermediate node
2. **Confidence threshold** — Only edges with confidence ≥ 0.70
3. **Same-customer constraint** — Graph traversal only connects devices within the same CompanyID
4. **Edge decay** — Identity edges older than 90 days have confidence reduced by 50%

Even with these safeguards, large customers with many shared-network visitors can develop dense graph clusters. The `DeadInternetService` (step 14) monitors this — a high dead internet index with high graph density is a signal of bot contamination.

### Session Stitching Memory

`SessionStitchingService` keeps all active sessions in a `ConcurrentDictionary`. Active sessions expire after 30 minutes of inactivity. At scale:

- 10,000 concurrent sessions × ~200 bytes/session = ~2MB — negligible
- Memory pressure comes from the DeviceHash keys (strings), not the session data
- The dictionary is never pruned during runtime — expired sessions are lazily evicted on next access

If the Forge restarts, all in-memory sessions are lost. This means the first hit after restart creates new sessions for all visitors. This is acceptable — a Forge restart is operationally rare, and session boundary errors for one 60-second cycle are tolerable.

### CrossCustomerIntel Sliding Window

The sliding window uses a `ConcurrentDictionary<string, List<(int CompanyId, DateTime)>>` keyed by `IP:FP`. The list is pruned on read (entries older than the window are removed). This means memory grows proportional to `distinct_IPs × window_minutes / avg_inter_arrival_time`.

At 100K unique IPs/day with a 5-minute window: ~500 active entries at any time × ~100 bytes = negligible.

The cross-customer alert is one of SmartPiXL's strongest bot detection signals. A human rarely visits 3+ different SmartPiXL customers in 5 minutes. But a scraper hitting the web systematically can easily trigger this.
