---
subsystem: sql-features
title: SQL Server 2025 Features
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - database/schema-map
  - subsystems/identity-resolution
  - subsystems/fingerprinting
---

# SQL Server 2025 Features

## Atlas Public

SmartPiXL leverages the latest SQL Server 2025 capabilities to deliver faster, more accurate visitor intelligence. Advanced database features enable capabilities that weren't possible with traditional database technology — from fuzzy device matching to multi-hop identity graphs.

## Atlas Internal

### What SQL Server 2025 Enables

| Feature | What It Does | SmartPiXL Benefit |
|---------|-------------|-------------------|
| **Native JSON type** | Stores JSON in binary-optimized format | 30% smaller storage, indexed JSON queries |
| **Vector similarity** | Finds similar items by comparing numerical vectors | Matches visitors even when browser fingerprints change slightly |
| **Graph tables** | Node/edge model with traversal queries | Multi-hop identity resolution (Device → Person → Other Devices) |
| **JSON_OBJECTAGG** | Aggregates key-value pairs into JSON objects | Efficient custom parameter extraction |
| **CLR assemblies** | .NET code running inside SQL Server | High-performance query string parsing (10× faster than T-SQL) |

### Why These Matter

**Without vectors**: A visitor who updates their browser loses their identity — the fingerprint changes and they appear as a new visitor. **With vectors**: SmartPiXL detects that 63 of 64 fingerprint signals match and recognizes the visitor despite the minor change.

**Without graph tables**: Identity resolution is limited to direct matches. **With graph tables**: SmartPiXL can traverse chains — "This device submitted an email → that email was also used on another device → that device used the same IP as a third device" — creating a richer identity graph.

## Atlas Technical

### Native JSON Type

SQL Server 2025 introduces a native `json` data type (not NVARCHAR-based):

```sql
-- PiXL.Visit.ClientParamsJson is native json type
ClientParamsJson    JSON NULL
```

Benefits:
- Binary-optimized storage (~30% smaller than NVARCHAR JSON)
- Built-in validation (rejects malformed JSON on INSERT)
- Enables `CREATE JSON INDEX` for indexed path queries

**JSON INDEX:**

```sql
CREATE JSON INDEX IX_PiXL_Visit_ClientParams
    ON PiXL.Visit (ClientParamsJson)
    FOR ('$.email', '$.hid');
```

This allows indexed seeks on specific JSON paths:

```sql
SELECT VisitID FROM PiXL.Visit
WHERE JSON_VALUE(ClientParamsJson, '$.email') = 'user@example.com';
-- Uses JSON INDEX instead of full table scan
```

Requirements: Table must have a clustered primary key (PiXL.Visit.VisitID).

**JSON_OBJECTAGG:**

```sql
-- Phase 12 of usp_ParseNewHits: extract _cp_* params into JSON
SELECT JSON_OBJECTAGG(key : value)
FROM STRING_SPLIT(p.QueryString, '&')
CROSS APPLY (VALUES (...)) AS kv(key, value)
WHERE key LIKE '_cp_%'
```

Aggregates arbitrary key-value pairs into a single JSON object — replaces manual string concatenation.

### Vector Similarity (VECTOR Type)

SQL Server 2025 introduces native `VECTOR(n)` type and `VECTOR_DISTANCE()` function:

```sql
-- PiXL.Device vectors
FingerprintVector   VECTOR(64) NULL    -- 64-dimension fingerprint
UaVector            VECTOR(32) NULL    -- 32-dimension UA drift detection
```

**VECTOR_DISTANCE function:**

```sql
SELECT DeviceId,
    VECTOR_DISTANCE('cosine', @InputVector, FingerprintVector) AS Distance
FROM PiXL.Device
WHERE FingerprintVector IS NOT NULL
ORDER BY Distance;
```

Distance metrics: `cosine`, `dot_product`, `euclidean`

**64 Fingerprint Vector Dimensions:**

| Dimensions | Signal Categories |
|-----------|------------------|
| 1-8 | Screen dimensions (width, height, available, viewport — normalized to 0-1) |
| 9-12 | Hardware (cores, memory, color depth, pixel ratio) |
| 13-20 | Feature bitmap (8 features encoded as 0/1 dimensions) |
| 21-24 | Timezone offset, language hash, font count, plugin count |
| 25-32 | Canvas/audio/WebGL fingerprint hashes (quantized) |
| 33-48 | Bot signal encoding (16 dimensions for bot/evasion signals) |
| 49-64 | Reserved for future expansion |

All values normalized to 0-1 range for consistent distance calculation.

**32 UA Drift Vector Dimensions:**

Encodes User-Agent components for drift detection:
- Browser family (one-hot encoded: Chrome, Firefox, Safari, Edge)
- Browser major version (normalized)
- OS family (one-hot encoded)
- OS version (normalized)
- Device type (one-hot: desktop, mobile, tablet)

Used to detect bot operators who rotate User-Agents slightly between requests.

### Graph Tables (NODE/EDGE)

SQL Server 2025 graph tables enable traversal queries with `MATCH` syntax:

**Node tables:**

```sql
CREATE TABLE Graph.Device   (...) AS NODE;
CREATE TABLE Graph.Person   (...) AS NODE;
CREATE TABLE Graph.IpAddress (...) AS NODE;
```

**Edge tables:**

```sql
CREATE TABLE Graph.ResolvesTo AS EDGE (
    Confidence      FLOAT,
    RelationType    VARCHAR(50),    -- SameEmail, SameSubnet, SimilarVector
    CreatedAt       DATETIME2(3)
);

CREATE TABLE Graph.UsesIP AS EDGE (
    FirstSeen       DATETIME2(3),
    LastSeen        DATETIME2(3),
    HitCount        INT DEFAULT 1
);
```

**MATCH traversal:**

```sql
-- Find all devices linked to a person
SELECT d.DeviceId, d.DeviceHash, r.Confidence
FROM Graph.Person p, Graph.ResolvesTo r, Graph.Device d
WHERE MATCH(p-(r)->d)
  AND p.Email = 'user@example.com';

-- Multi-hop: Person → Device → IP → Other Devices
SELECT d2.DeviceId, d2.DeviceHash
FROM Graph.Person p,
     Graph.ResolvesTo r1,
     Graph.Device d1,
     Graph.UsesIP u1,
     Graph.IpAddress ip,
     Graph.UsesIP u2,
     Graph.Device d2
WHERE MATCH(p-(r1)->d1-(u1)->ip<-(u2)-d2)
  AND p.Email = 'target@example.com'
  AND d1.DeviceId <> d2.DeviceId;
```

### SQL CLR (.NET Framework 4.8)

SQL Server 2025 CLR runtime uses .NET Framework v4.0.30319 (net48). Modern .NET (net10) assemblies are rejected.

**CLR Database: SmartPiXL_CLR**

Isolated in a separate database for security:
- Certificate-based signing (NOT TRUSTWORTHY)
- Certificate in `master` → Login with UNSAFE ASSEMBLY permission
- Synonyms in SmartPiXL.dbo → SmartPiXL_CLR.dbo for transparent access

**10 CLR Functions:**

| Function | Signature | Purpose |
|----------|-----------|---------|
| `GetQueryParam` | `(NVARCHAR(MAX), NVARCHAR(200)) → NVARCHAR(2000)` | Extract param from querystring |
| `GetSubnet24` | `(VARCHAR(50)) → VARCHAR(50)` | IPv4 → /24 subnet string |
| `RegexExtract` | `(NVARCHAR(MAX), NVARCHAR(200)) → NVARCHAR(2000)` | Regex group extraction |
| `RegexMatch` | `(NVARCHAR(MAX), NVARCHAR(200)) → BIT` | Regex boolean match |
| `FeatureBitmap` | `(17 BIT params) → INT` | 17 browser features → bitmap |
| `AccessibilityBitmap` | `(9 BIT params) → INT` | 9 accessibility flags → bitmap |
| `BotBitmap` | `(20 BIT params) → INT` | 20 bot signals → bitmap |
| `EvasionBitmap` | `(8 BIT params) → INT` | 8 evasion signals → bitmap |
| `MurmurHash3` | `(NVARCHAR(MAX)) → BINARY(16)` | 128-bit non-crypto hash |
| `JaroWinkler` | `(NVARCHAR(200), NVARCHAR(200)) → FLOAT` | Fuzzy string similarity |

Usage via synonyms:

```sql
-- All work from SmartPiXL database context
SELECT dbo.GetQueryParam(QueryString, 'sw') FROM PiXL.Test;
SELECT dbo.GetSubnet24('192.168.1.100');  -- Returns '192.168.1'
SELECT dbo.JaroWinkler('John Smith', 'Jon Smyth');  -- Returns ~0.89
```

## Atlas Private

### VECTOR_DISTANCE Known Bugs

SQL Server 2025 RTM-GDR (17.0.1050.2) has a query processor bug:

```sql
-- FAILS: "Internal Query Processor Error"
DECLARE @v VECTOR(64) = CAST('[...]' AS VECTOR(64));
SELECT VECTOR_DISTANCE('cosine', @v, FingerprintVector) FROM PiXL.Device;

-- WORKS: Inline CAST
SELECT VECTOR_DISTANCE('cosine',
    CAST('[...]' AS VECTOR(64)),
    FingerprintVector) FROM PiXL.Device;
```

The bug manifests when using VECTOR variables or CROSS JOIN with VECTOR columns. The workaround is inline CAST expressions or CTE-based queries.

Expected fix: next CU or service pack. Track via SQL Server feedback portal.

### CLR net48 Limitation

SQL Server 2025 CLR is stuck on .NET Framework 4.8 — it cannot load modern .NET assemblies. This means:
- No Span<T> in CLR functions (net48 doesn't support it)
- No modern C# features (records, pattern matching, etc.)
- GetQueryParam uses `String.IndexOf` instead of `Span<char>` search

The SmartPiXL.SqlClr project targets net48 specifically. It's the only project in the solution that isn't net10. The project file has `<TargetFramework>net48</TargetFramework>`.

Impact: CLR function performance is good (~10× faster than T-SQL string parsing) but not as fast as it could be with modern .NET string handling.

### JSON Type Binary Format

The native `json` type stores data in a binary-optimized format:
- Key names are dictionary-encoded (each unique key stored once)
- Values are typed (numbers stored as binary numbers, not strings)
- Path lookups are O(n) in the number of keys (not string parsing)

This is why `CREATE JSON INDEX` works — the binary format enables efficient indexing of specific paths without full document parsing.

### Graph Table Internals

Graph NODE tables have a hidden `$node_id` column (system-generated). EDGE tables have hidden `$from_id` and `$to_id` columns that reference node IDs. The `MATCH` syntax is syntactic sugar for these hidden column joins.

Performance consideration: Graph traversal is not magic — it's still relational joins under the hood. A 3-hop traversal is three self-joins on the edge table. At scale (millions of edges), this can be expensive. SmartPiXL limits traversal to 2 hops with confidence thresholds to keep queries fast.

### Certificate Renewal

The CLR signing certificate expires 2036-12-31. Before that date:
1. Create a new certificate with extended expiry
2. Sign the assembly with the new certificate
3. Drop the old certificate's login and user
4. Create new login and user from the new certificate
5. Re-deploy the assembly

This is a ~10 minute operation but requires coordination because the CLR functions are used by every ETL cycle.

### Missing Vector Index

SQL Server 2025 doesn't yet support vector-specific indexes (like HNSW or IVFFlat in PostgreSQL). `VECTOR_DISTANCE` queries do a full table scan of PiXL.Device. At current volume (~100K devices), this is fast enough (~50ms). At 1M+ devices, a filtered/partial approach will be needed:

```sql
-- Workaround: pre-filter by other columns to reduce scan set
SELECT DeviceId, VECTOR_DISTANCE('cosine', @v, FingerprintVector) AS Dist
FROM PiXL.Device
WHERE LastSeen > DATEADD(DAY, -90, GETUTCDATE())  -- Only recent devices
  AND FingerprintVector IS NOT NULL;
```

Microsoft has announced vector index support for a future CU. When available, it will be added to PiXL.Device.FingerprintVector.
