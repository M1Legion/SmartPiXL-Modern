---
subsystem: fingerprinting
title: Device Fingerprinting
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - subsystems/pixl-script
  - subsystems/bot-detection
  - subsystems/identity-resolution
  - database/sql-features
---

# Device Fingerprinting

## Atlas Public

SmartPiXL identifies returning visitors without relying on cookies. As privacy tools evolve and more users block or clear cookies, traditional tracking becomes less effective. SmartPiXL uses **device fingerprinting** — a technique that recognizes a visitor's unique device based on hardware and software characteristics.

**How it works:** Every device has a unique combination of screen resolution, GPU, installed fonts, browser capabilities, and audio processing. SmartPiXL captures these signals and combines them into a device identifier that persists across sessions, even if the visitor clears cookies or uses incognito mode.

**Key capabilities:**
- Recognize returning visitors across multiple sessions
- Track the same device across different pages on your website
- Detect when the same device visits multiple SmartPiXL-enabled websites
- Identify anti-detect browsers that attempt to forge device identities
- Work in all major browsers without any special configuration

**Privacy-responsible design:** Device fingerprinting identifies devices, not people. SmartPiXL never accesses cameras, microphones, or personal data. The fingerprint is a one-way hash — it can identify a returning device but cannot be reverse-engineered to reveal personal information.

## Atlas Internal

### What a Fingerprint Actually Is

A fingerprint is a hash code generated from specific browser and hardware characteristics. Think of it like a car's unique combination of make, model, year, color, and license plate — no single attribute is unique, but the combination is.

SmartPiXL generates fingerprints from five core components:

| Component | What It Captures | Why It's Unique |
|-----------|-----------------|-----------------|
| Canvas hash | How the browser renders text and shapes | Different GPUs/OSes render slightly differently |
| WebGL hash | GPU parameters and capabilities | Each GPU model has a different capability profile |
| Audio hash | How the browser processes audio samples | Different audio stacks produce different outputs |
| GPU renderer | The actual GPU model string | e.g., "NVIDIA GeForce RTX 4090" |
| Platform | Operating system identifier | e.g., "Win32", "MacIntel" |

These five values are combined into a `DeviceHash` — a single identifier stored in our database. When the same combination appears again (even hours, days, or weeks later), we recognize it as the same device.

### Fingerprint Accuracy

**Strengths:**
- Desktop browsers: very high uniqueness (screen + GPU + fonts is usually enough)
- Chrome/Edge: best coverage (most APIs available)
- Multi-monitor setups: the `screenExtended` field adds another dimension

**Limitations:**
- Mobile devices: lower uniqueness (many iPhones share identical hardware)
- Tor Browser: intentionally reduces fingerprint uniqueness
- Anti-detect browsers: rotate fingerprint components to avoid detection (but SmartPiXL detects this — see Bot Detection)
- Privacy browsers (Brave): inject noise into canvas/WebGL (SmartPiXL detects and flags this)

### What "Cross-Customer" Means

Because SmartPiXL tracks `DeviceHash` globally (not per-customer), the same device visiting a car dealership on Monday and a mortgage broker on Wednesday produces the same fingerprint. This is one of SmartPiXL's core differentiators — no other pixel tracking solution provides cross-domain, cross-customer device tracking.

## Atlas Technical

### DeviceHash Construction

The `DeviceHash` is a SHA-256 hash of five concatenated fields, computed in SQL during ETL:

```sql
DeviceHash = HASHBYTES('SHA2_256',
    ISNULL(CanvasFP, '') +
    ISNULL(WebGlFP, '') +
    ISNULL(AudioHash, '') +
    ISNULL(WebGlRenderer, '') +
    ISNULL(Platform, ''))
```

Stored as `VARBINARY(32)` on `PiXL.Device`. The dimensional table `PiXL.Device` is keyed by `DeviceHash` — one row per unique device across all customers, all time.

### Vector Similarity (SQL Server 2025)

Beyond exact `DeviceHash` matching, SmartPiXL uses 64-dimensional vectors for **approximate device matching**:

```sql
ALTER TABLE PiXL.Device ADD FingerprintVector VECTOR(64) NULL;
```

Device characteristics (screen dims, cores, memory, color depth, feature bitmap, etc.) are encoded as a 64-dimensional vector with values normalized to 0-1. `VECTOR_DISTANCE('cosine', ...)` finds similar devices even when one characteristic changed (e.g., visitor updated their browser → new DeviceHash, but vector similarity of 0.95+).

A 32-dimensional `UaVector VECTOR(32)` exists for User-Agent drift detection — bot operators rotate UAs with minor variations, and cosine similarity catches these clusters.

### Evasion Detection

Three layers detect fingerprint manipulation:

1. **Noise injection** — Canvas and audio consistency tests run the same operation twice. Different results = noise injection = privacy tool.
2. **Spoofing detection** — Font method mismatch (`offsetWidth` vs `getBoundingClientRect`), getter property validation on Navigator, cross-realm toString comparison.
3. **Variation tracking** — `FingerprintStabilityService` on the Edge tracks per-IP fingerprint history over 24 hours. 3+ unique fingerprints from the same IP = anti-detect browser rotating identities.

### PiXL.Device Table

| Column | Type | Purpose |
|--------|------|---------|
| DeviceId | BIGINT PK | Auto-increment |
| DeviceHash | VARBINARY(32) UQ | SHA-256 of 5 fingerprint fields |
| FirstSeen | DATETIME2 | First observation |
| LastSeen | DATETIME2 | Most recent observation |
| HitCount | INT | Total observations |
| FingerprintVector | VECTOR(64) | Normalized device characteristics |
| UaVector | VECTOR(32) | User-Agent drift detection |
| AffluenceSignal | VARCHAR(4) | LOW/MID/HIGH |
| GpuTier | VARCHAR(4) | LOW/MID/HIGH |
| DeviceAgeYears | INT | Estimated from GPU release year |

## Atlas Private

### Why SHA-256 for DeviceHash (Not MurmurHash3)

The CLR provides `dbo.MurmurHash3` for non-crypto hashing (10x faster, better distribution at small output sizes). DeviceHash still uses `HASHBYTES('SHA2_256')` because:
1. It's computed once per device during ETL (not on the hot path)
2. 32-byte output prevents collisions across millions of devices
3. MurmurHash3 is 128-bit (16 bytes) — higher collision probability at our device count
4. Changing the hash function would invalidate all existing DeviceHash values and break PiXL.Visit foreign keys

For new hashing use cases (fingerprint bucketing, consistent partitioning), MurmurHash3 is preferred.

### Vector Distance SQL Server 2025 Bug

`VECTOR_DISTANCE()` works correctly with inline `CAST('[...]' AS VECTOR(N))` operands but fails with column references in CROSS JOIN/APPLY queries: "Internal Query Processor Error: The query processor could not produce a query plan." This is a confirmed SQL Server 2025 RTM-GDR bug. The workaround is to use inline CAST. Expected to be fixed in a future CU.

The vector columns are in place and populated by ETL, but vector similarity queries are limited until the bug is patched.

### FingerprintStabilityService Internals

Uses `IMemoryCache` with 1-hour sliding expiration. Cache key is the IP address. Each entry stores: set of unique fingerprint hashes, observation count, 5-minute rate counter.

Detection layers:
- **Layer 1**: 3+ unique fingerprints from same IP = `fpAlert=1` (anti-detect browser)
- **Layer 2**: 50+ observations from same IP = volume alert; 20+ in 5 minutes = rate alert

Outputs: `_srv_fpAlert`, `_srv_fpObs`, `_srv_fpUniq`, `_srv_fpRate5m`

**Known fragility**: The 1-hour sliding expiration means fingerprint variation detection resets hourly. An anti-detect browser that rotates fingerprints slowly (1 per 2 hours) would evade this check. The SQL-level device lifecycle analysis (Phase 8) catches this with longitudinal analysis, but there's a gap between the edge-level real-time detection and the SQL-level historical detection.

### Graph-Based Identity Resolution

`Graph.Device`, `Graph.Person`, and `Graph.IpAddress` node tables enable multi-hop traversal:

```sql
-- Person → Devices → IPs → other Devices → other People
SELECT Person.Email, Device.DeviceHash,
       LAST_VALUE(IpAddress.IP) WITHIN GROUP (GRAPH PATH)
FROM Graph.Person, Graph.ResolvesTo, Graph.Device,
     Graph.UsesIP FOR PATH, Graph.IpAddress FOR PATH
WHERE MATCH(Person<-(ResolvesTo)-Device-(UsesIP)->IpAddress+)
```

This enables: "Show me every device this person used, every IP those devices touched, and every other person who shared those devices or IPs." Identity resolution that would require 50+ lines of recursive CTEs, done in a single graph query.
