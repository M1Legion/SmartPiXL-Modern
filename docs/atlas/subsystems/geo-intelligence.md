---
subsystem: geo-intelligence
title: Geographic Intelligence
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - subsystems/enrichment-pipeline
  - subsystems/bot-detection
  - database/schema-map
---

# Geographic Intelligence

## Atlas Public

SmartPiXL pinpoints where your visitors are located — not just their country, but their city, region, ISP, and network type. This geographic intelligence powers features like:

- **Local targeting** — See which cities generate the most traffic and tailor outreach accordingly
- **Regional analysis** — Understand geographic trends in visitor behavior and engagement
- **Network quality** — Know whether visitors are on residential, mobile, or datacenter connections
- **VPN/Proxy detection** — Identify visitors who are masking their true location

SmartPiXL uses multiple data sources for geographic intelligence, cross-referencing results for maximum accuracy. If one source is uncertain, others fill the gap.

## Atlas Internal

### Three Geographic Data Sources

SmartPiXL doesn't rely on a single geographic database — it combines three:

| Source | Coverage | Accuracy | Speed | Cost |
|--------|----------|----------|-------|------|
| **MaxMind GeoIP2** | Global, 99%+ IP coverage | City-level for most of North America/Europe | ~1μs (offline database) | Licensed |
| **IPAPI Pro** | Global, 99%+ IP coverage | City-level, ISP, proxy/VPN flags | 50-200ms (HTTP API) | Per-request |
| **IPAPI Sync (Xavier)** | 342M+ IP ranges pre-synced | Historical data, broad coverage | Instant (SQL lookup) | One-time sync |

### How They Work Together

1. **MaxMind runs first** — Every request gets an immediate offline lookup. Returns country, region, city, coordinates, ASN, and organization. ~1 microsecond, no network call.

2. **IPAPI runs second** — For new or stale IPs, calls the IPAPI Pro API. Returns ISP, proxy/VPN detection, mobile network flag, and reverse hostname. Rate-limited at 500 requests/minute.

3. **Xavier historical data** — 342M+ pre-synced IP range records provide baseline coverage for IPs that IPAPI hasn't seen yet. The Forge's `IpApiSyncService` periodically syncs between Xavier (legacy SQL 2017 server) and SmartPiXL's IPAPI schema.

### Geographic Arbitrage Detection

SmartPiXL goes beyond "where is this IP?" to ask "does this visitor's entire digital fingerprint match their claimed location?" This is Geographic Arbitrage detection:

- A visitor's IP says they're in New York, but their browser fonts are French, their language is set to Vietnamese, and their timezone is GMT+7
- This strongly suggests a VPN or proxy — the visitor is likely in Vietnam, not New York
- SmartPiXL generates a "cultural consistency score" (0-100) that quantifies how well the visitor's cultural fingerprint matches their IP-reported location

This is particularly valuable for detecting sophisticated bot operations that use residential proxy networks to appear local.

### Future: Zipcode Polygon Matching (Phase 8)

The `Geo` schema will contain Census Bureau ZCTA (ZIP Code Tabulation Areas) polygon data. This enables:
- Precise zipcode assignment from latitude/longitude coordinates
- Distance calculations between visitors and business locations
- Geographic clustering analysis

## Atlas Technical

### Edge Geographic Services

**`GeoCacheService`** — Two-tier caching for IP → geographic data:

1. `IMemoryCache` (L1) — In-process, 1-hour sliding expiration, ~10K entries
2. IPAPI.IPGeo table (L2) — SQL lookup for 342M+ pre-synced ranges

Cache key: IP address string. Cache miss → MaxMind offline lookup (always available).

**`IpClassificationService`** — Three-valued classification applied in-memory on the Edge:

| Result | Meaning | Source |
|--------|---------|--------|
| `Datacenter` | Cloud/hosting IP (AWS, GCP, Azure) | CidrTrie of 8,500+ ranges |
| `Residential` | Consumer ISP | Default (not datacenter, not mobile) |
| `Mobile` | Mobile carrier IP | IPAPI `mobile` flag |

### Forge Geographic Enrichments

**Step 4: `MaxMindGeoService`** — Wraps MaxMind.GeoIP2 NuGet package:

```csharp
var mmResult = _maxMindGeo.Lookup(record.IPAddress);
// Returns: CountryCode, Region, City, Latitude, Longitude, Asn, AsnOrg
```

Output params: `_srv_mmCC`, `_srv_mmReg`, `_srv_mmCity`, `_srv_mmLat`, `_srv_mmLon`, `_srv_mmASN`, `_srv_mmASNOrg`

The GeoIP2 database file is read into memory at startup. Lookups are pure in-memory binary search — no I/O, no network.

**Step 5: `IpApiLookupService`** — HTTP client to ip-api.com Pro API:

```csharp
var ipapiResult = await _ipApiLookup.LookupAsync(record.IPAddress, ct);
// Returns: CountryCode, Isp, IsProxy, IsMobile, Reverse, Asn
```

Output params: `_srv_ipapiCC`, `_srv_ipapiISP`, `_srv_ipapiProxy`, `_srv_ipapiMobile`, `_srv_ipapiReverse`, `_srv_ipapiASN`

Rate limiting: `SemaphoreSlim` throttle at 500 req/min. Known IPs (already in IPAPI.IPGeo) skip the live lookup.

**Step 6: `WhoisAsnService`** — WHOIS query for ASN data (supplementary):

```csharp
if (!mmResult.Asn.HasValue)
{
    var whoisResult = await _whoisAsn.LookupAsync(record.IPAddress, ct);
    // Returns: Asn, Organization
}
```

Only runs when MaxMind lacks ASN data (~1% of lookups).

**Step 11: `GeographicArbitrageService`** — Cultural consistency analysis:

Compares geo-IP location against cultural browser signals:

| Signal | Example | Expectation for US IP |
|--------|---------|----------------------|
| `lang` | `en-US` | English variant |
| `fonts` | `Arial, Helvetica, ...` | Western font set |
| `tz` | `America/New_York` | US timezone |
| `numberFormat` | `1,234.56` | Period decimal separator |
| `tzLocale` | `en` | English locale |
| `voices` | `Microsoft David` | English TTS voices |

Output: `_srv_culturalScore` (0-100), `_srv_culturalFlags` (comma-separated mismatches)

### Database Schema — IPAPI

```sql
IPAPI.IPGeo (
    IpGeoId     BIGINT IDENTITY PK,
    IpFrom      BIGINT,              -- IP range start (integer)
    IpTo        BIGINT,              -- IP range end (integer)
    CountryCode CHAR(2),
    Region      NVARCHAR(100),
    City        NVARCHAR(100),
    Isp         NVARCHAR(200),
    IsProxy     BIT,
    IsMobile    BIT,
    SyncedAt    DATETIME2(3)
)
-- Index: IX_IPGeo_Range (IpFrom, IpTo) for integer-bucket range lookups
```

The integer bucket pattern: IPs are stored as BIGINT ranges (`IpFrom`, `IpTo`). Lookup converts the query IP to integer and finds the containing range. This is O(log n) with the clustered index.

### XavierSync (`IpApiSyncService`)

The Forge syncs IPAPI data from Xavier (legacy server 192.168.88.35, SQL 2017):

- `CompanyPiXLSyncService` — Syncs customer pixel configurations
- `IpApiSyncService` — Syncs IPAPI.IPGeo ranges from Xavier's IPGEO database (342M+ rows)

These run on startup and then periodically. Xavier is the temporary bridge while the legacy system is decommissioned.

### Geo Enrichment ETL (`usp_EnrichParsedGeo`)

After `usp_ParseNewHits`, a separate ETL procedure enriches PiXL.Parsed with geographic data from IPAPI.IPGeo:

```sql
-- Convert IP to integer, look up range
UPDATE p SET
    p.GeoCountry = g.CountryCode,
    p.GeoRegion = g.Region,
    p.GeoCity = g.City
FROM PiXL.Parsed p
INNER JOIN IPAPI.IPGeo g
    ON dbo.IpToInt(p.IPAddress) BETWEEN g.IpFrom AND g.IpTo
WHERE p.SourceId > @LastId AND p.SourceId <= @MaxId;
```

### Future: Geo.Zipcode Polygon Table (Phase 8)

```sql
-- Migration 55: Census ZCTA polygons
Geo.Zipcode (
    ZipcodeId   INT IDENTITY PK,
    Zipcode     CHAR(5),
    Centroid    GEOGRAPHY,
    Boundary    GEOGRAPHY,       -- polygon from Census ZCTA shapefile
    State       CHAR(2),
    Population  INT
)
```

Enables: `SELECT z.Zipcode FROM Geo.Zipcode z WHERE z.Boundary.STContains(GEOGRAPHY::Point(@lat, @lon, 4326)) = 1`

## Atlas Private

### MaxMind Accuracy Limitations

MaxMind GeoIP2 city-level accuracy varies dramatically by region:

| Region | City Accuracy | Notes |
|--------|--------------|-------|
| North America | 80-85% | Good in urban areas, weak in rural |
| Western Europe | 75-80% | Strong in UK/DE/FR, weaker in Eastern Europe |
| Asia | 60-70% | Country is reliable, city is unreliable |
| Africa | 40-50% | Country-level only in many regions |
| Mobile IPs | 20-30% | Mobile carriers use centralized egress points |

Never present MaxMind city-level data as "visitor location" without qualification. It's "approximate location based on IP address."

### IPAPI Pro Rate Limiting Reality

The 500 req/min cap is the contract limit. In practice:
- If we burst to 600 req/min briefly, IPAPI doesn't immediately block — they respond with HTTP 429 after a grace period
- The `SemaphoreSlim` in `IpApiLookupService` prevents this, but if the Forge restarts under load, the initial burst of new IPs can approach the limit
- Known IPs skip the live lookup (they're in IPAPI.IPGeo already), so steady-state traffic rarely hits the cap

Cost: IPAPI Pro is ~$50/month for 500K requests/month. Current usage: ~50K requests/month (most IPs are repeats covered by the pre-synced database).

### Cultural Arbitrage Score Interpretation

The `_srv_culturalScore` is simple weighted sum, not ML:

| Signal Mismatch | Point Deduction |
|----------------|----------------|
| Timezone doesn't match country | -30 |
| Language doesn't match country | -25 |
| Font set inconsistent with platform + country | -20 |
| Number format mismatch | -15 |
| TTS voices mismatch | -10 |

Starting from 100, each mismatch subtracts points. Score < 50 = strong VPN/proxy indicator.

Known false positives:
- Expats (US citizen living in Japan — US browser settings, Japan IP)
- International business travelers (UK fonts, Germany IP)
- Multi-language speakers (French person with English browser settings)

The score is a signal, not a verdict. It feeds into the composite threat assessment alongside bot score, contradiction count, and lead quality score.

### Xavier Sync Technical Debt

The Xavier sync is needed because:
1. Xavier hosts the legacy IPGEO database (342M+ rows) — too large to bulk-load at once
2. Xavier hosts the AutoUpdate database with AutoConsumer records
3. Xavier hosts customer pixel configurations

Once the Forge has its own complete dataset:
- Xavier sync can be disabled
- `IpApiSyncService` and `CompanyPiXLSyncService` can be removed
- Target: disable Xavier sync when SmartPiXL has 6+ months of independent IP data accumulation
