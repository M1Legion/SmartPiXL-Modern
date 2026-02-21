---
subsystem: enrichment-pipeline
title: Enrichment Pipeline
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - architecture/forge
  - architecture/data-flow
  - subsystems/bot-detection
  - subsystems/geo-intelligence
  - subsystems/identity-resolution
---

# Enrichment Pipeline

## Atlas Public

Every visitor interaction captured by SmartPiXL goes through a multi-stage intelligence pipeline that transforms a simple page view into a rich profile of who the visitor is, what device they're using, where they're located, and how they're behaving.

**What the pipeline delivers:**
- **Identity enrichment** — Parsed device type, browser, OS, and model from every visit
- **Geographic intelligence** — City-level location, ISP, network type, timezone, and cultural context
- **Behavioral analysis** — Session stitching, mouse movement authenticity, multi-page engagement tracking
- **Quality scoring** — A 0-100 lead quality score that separates real prospects from noise
- **Threat detection** — Known bot identification, impossible device configurations, behavioral replay detection

All of this happens automatically in the background — no configuration needed. Data enters the pipeline as a raw visit and exits as an enriched intelligence record ready for analysis.

## Atlas Internal

### How Enrichment Works

When a visitor hits a customer's page, the web server (Edge) captures the request and performs fast, in-memory checks (~5 microseconds). It then passes the record to the Forge — a separate background service — which runs 15 enrichment steps organized in three tiers:

| Tier | Steps | Purpose | Speed |
|------|-------|---------|-------|
| **Tier 1: Library Lookups** | 1-6 | Known-data matching (bots, UA parsing, DNS, geo) | Fast — mostly in-memory |
| **Tier 2: Cross-Request Intel** | 7-9 | Patterns across visits (sessions, cross-customer, affluence) | Fast — in-memory aggregates |
| **Tier 3: Asymmetric Detection** | 10-14 | Adversarial detection (contradictions, cultural, replay) | Fast — algorithmic, no I/O |
| **Final Scoring** | 15 | Lead quality composite score | Instant — consumes prior results |

### The 15 Enrichment Steps

| # | Name | What It Does |
|---|------|-------------|
| 1 | Bot/Crawler Detection | Checks User-Agent against 10,000+ known bot patterns |
| 2 | UA Parsing | Extracts browser, OS, device type, model, brand |
| 3 | Reverse DNS | Looks up hostname, detects cloud-hosted origins (AWS, GCP) |
| 4 | MaxMind Geo | Offline geographic lookup — city, region, country, coordinates, ASN |
| 5 | IPAPI Lookup | Real-time IP intelligence — ISP, proxy/VPN detection, mobile network |
| 6 | WHOIS ASN | Supplementary network ownership data (only when MaxMind lacks ASN) |
| 7 | Session Stitching | Groups page views into sessions (30-minute timeout) |
| 8 | Cross-Customer Intel | Detects same visitor hitting multiple customers in minutes |
| 9 | Device Affluence | Classifies device as low/mid/high end based on hardware signals |
| 10 | Contradiction Matrix | Flags impossible device configurations (mobile UA + mouse + 4K screen) |
| 11 | Geographic Arbitrage | Checks cultural fingerprint consistency (fonts, language, timezone vs IP location) |
| 12 | Device Age Estimation | Triangulates GPU + OS + browser age to estimate device vintage |
| 13 | Behavioral Replay | Detects replayed mouse recordings via path hashing |
| 14 | Dead Internet Index | Per-customer aggregate bot/engagement/diversity health score |
| 15 | Lead Quality Scoring | 0-100 composite score from positive human signals |

### Important: Enrichment is Append-Only

The Forge never modifies the original data. It appends enrichment results as `_srv_*` parameters to the existing record. This means:
- Original browser data is always preserved
- You can always see what the browser reported vs. what the server added
- Enrichment failures don't lose original data

## Atlas Technical

### Architecture

```
PipeListenerService → ForgeChannels.Enrichment → EnrichmentPipelineService
                                                          ↓
FailoverCatchupService ────────────────────────→ ForgeChannels.Enrichment
                                                          ↓
                                                 ForgeChannels.SqlWriter
                                                          ↓
                                                 SqlBulkCopyWriterService → PiXL.Raw
```

Two Channel<TrackingData> instances connect the pipeline stages:

| Channel | Producer(s) | Consumer | Purpose |
|---------|------------|----------|---------|
| `ForgeChannels.Enrichment` | PipeListenerService, FailoverCatchupService | EnrichmentPipelineService | Records awaiting enrichment |
| `ForgeChannels.SqlWriter` | EnrichmentPipelineService | SqlBulkCopyWriterService | Enriched records awaiting write |

Both channels are bounded with `BoundedChannelFullMode.Wait` and `SingleReader = true`. The enrichment channel has high capacity for burst absorption.

### Pipeline Implementation

`EnrichmentPipelineService` is a `BackgroundService` that reads from the enrichment channel via `ReadAllAsync`. Each record passes through all 15 steps sequentially:

```csharp
var enriched = await EnrichRecordAsync(record, stoppingToken);
_sqlWriterChannel.Writer.TryWrite(enriched);
```

Every enrichment appends `_srv_*` parameters to `TrackingData.QueryString` using `AppendParam()`:

```csharp
qs = AppendParam(qs, "_srv_browser", Uri.EscapeDataString(uaResult.Browser));
```

The enriched record is returned via `record with { QueryString = qs }` — immutable record semantics with `with` expression.

### Enrichment Service Details

#### Tier 1: Library Lookups

| Service | Library | Key Output Params |
|---------|---------|------------------|
| `BotUaDetectionService` | NetCrawlerDetect | `_srv_knownBot`, `_srv_botName` |
| `UaParsingService` | UAParser + DeviceDetector.NET | `_srv_browser`, `_srv_browserVer`, `_srv_os`, `_srv_osVer`, `_srv_deviceType`, `_srv_deviceModel`, `_srv_deviceBrand` |
| `DnsLookupService` | DnsClient | `_srv_rdns`, `_srv_rdnsCloud` |
| `MaxMindGeoService` | MaxMind.GeoIP2 | `_srv_mmCC`, `_srv_mmReg`, `_srv_mmCity`, `_srv_mmLat`, `_srv_mmLon`, `_srv_mmASN`, `_srv_mmASNOrg` |
| `IpApiLookupService` | HTTP to ip-api.com/pro | `_srv_ipapiCC`, `_srv_ipapiISP`, `_srv_ipapiProxy`, `_srv_ipapiMobile`, `_srv_ipapiReverse`, `_srv_ipapiASN` |
| `WhoisAsnService` | Whois.NET | `_srv_whoisASN`, `_srv_whoisOrg` |

#### Tier 2: Cross-Request Intelligence

| Service | State | Key Output Params |
|---------|-------|------------------|
| `SessionStitchingService` | In-memory session graph, 30-min timeout | `_srv_sessionId`, `_srv_sessionHitNum`, `_srv_sessionDurationSec`, `_srv_sessionPages` |
| `CrossCustomerIntelService` | Sliding window per IP+FP | `_srv_crossCustHits`, `_srv_crossCustWindow`, `_srv_crossCustAlert` |
| `DeviceAffluenceService` | GPU tier reference table | `_srv_affluence` (LOW/MID/HIGH), `_srv_gpuTier` |

#### Tier 3: Asymmetric Detection

| Service | Method | Key Output Params |
|---------|--------|------------------|
| `ContradictionMatrixService` | 13 cross-signal rules, `stackalloc` | `_srv_contradictions`, `_srv_contradictionList` |
| `GeographicArbitrageService` | Cultural fingerprint vs geo | `_srv_culturalScore`, `_srv_culturalFlags` |
| `DeviceAgeEstimationService` | GPU+OS+browser age triangulation | `_srv_deviceAge`, `_srv_deviceAgeAnomaly` |
| `BehavioralReplayService` | FNV-1a path hash, cross-FP matching | `_srv_replayDetected`, `_srv_replayMatchFP` |
| `DeadInternetService` | Per-customer aggregate index | `_srv_deadInternetIdx` |

#### Final Scoring

| Service | Inputs | Output |
|---------|--------|--------|
| `LeadQualityScoringService` | IsResidentialIp, HasConsistentFingerprint, MouseEntropy, FontCount, HasCleanCanvas, HasMatchingTimezone, SessionHitNumber, IsKnownBot, ContradictionCount | `_srv_leadScore` (0-100) |

### QueryParamReader

All enrichment services read values from the querystring via `QueryParamReader` — a zero-allocation parser that implements `ReadOnlySpan<char>`-based searching over the raw querystring:

```csharp
var gpu = QueryParamReader.Get(qs, "gpu");
var cores = QueryParamReader.GetInt(qs, "cores");
var mouseEntropy = QueryParamReader.GetDouble(qs, "mouseEntropy");
```

Methods: `Get()`, `GetInt()`, `GetDouble()`, `GetBool()` — all return nullable types for missing params.

### NuGet Dependencies (Forge)

| Package | Version | Purpose |
|---------|---------|---------|
| NetCrawlerDetect | latest | Bot UA detection |
| UAParser | latest | Browser/OS parsing |
| DeviceDetector.NET | latest | Device type/model/brand |
| DnsClient | latest | Async reverse DNS |
| MaxMind.GeoIP2 | latest | Offline geographic lookup |
| Whois.NET | latest | WHOIS/ASN queries |

### Startup Registration

Services are registered in the Forge's `Program.cs` in dependency order. All enrichment services are singletons. The `ForgeChannels` instance is also a singleton, constructed with explicit capacity values from `ForgeSettings`.

## Atlas Private

### Pipeline Performance Characteristics

The pipeline is single-threaded per record (sequential enrichment) but achieves throughput through pipeline parallelism — while one record is being enriched, the SQL writer is bulk-inserting previous records.

I/O-bound enrichments have timeouts:
- DNS lookup: 2-second timeout
- IPAPI: 1-second timeout (rate-limited at 500 req/min for Pro)
- WHOIS: 3-second timeout
- MaxMind: synchronous (~1μs in-memory)

If any enrichment throws, the exception is caught per-record and the record is skipped. The pipeline never stops for a single bad record.

### The QueryString-as-Carrier Pattern

Every enrichment result is appended to `TrackingData.QueryString` as `_srv_*` parameters. This is architecturally intentional:

1. **PiXL.Raw has only 9 columns** — adding a column for every enrichment would mean 50+ columns and constant schema migrations
2. **The ETL (usp_ParseNewHits) already extracts params** — it parses the querystring into PiXL.Parsed's 300+ columns, so Forge enrichments automatically flow into the parsed table
3. **Append-only is safe** — enrichments can't corrupt original browser data since they use the `_srv_` prefix namespace

The downside: string concatenation per enrichment. Each `AppendParam()` creates a new string. For 15 enrichments × ~3 params each ≈ 45 string concatenations per record. This is acceptable because the Forge is I/O-bound (DNS, HTTP lookups), not CPU-bound. Pre-allocated StringBuilder would save allocations but add complexity for negligible gain at current volume.

### IPAPI Rate Limiting

`IpApiLookupService` is the only enrichment that calls an external paid API. Rate limiting:
- 500 requests/minute for ip-api.com Pro
- IPs already in IPAPI.IPGeo (342M+ rows synced from Xavier) skip the live lookup
- New IPs are queued and rate-limited via a `SemaphoreSlim`
- Results are cached in-memory and synced back to IPAPI.IPGeo

### WHOIS Conditional Execution

WHOIS lookup only runs if MaxMind didn't return an ASN. This is a bandwidth optimization — MaxMind covers ~99% of IPs, and WHOIS is slow + unreliable. The conditional check:

```csharp
if (!mmResult.Asn.HasValue)
{
    var whoisResult = await _whoisAsn.LookupAsync(record.IPAddress, ct);
    // ...
}
```

### Channel Backpressure

`ForgeChannels.Enrichment` uses `BoundedChannelFullMode.Wait`, meaning the pipe listener blocks when the enrichment channel is full. This is correct behavior — the pipe listener should apply backpressure to the Edge rather than dropping records.

However, `SqlBulkCopyWriterService` uses `TryWrite` to write to the SQL channel, which means if SQL is down and the channel fills up, records are dropped with a warning log. This is a deliberate trade-off: SQL being down shouldn't block enrichment processing for records that can still be enriched. The dropped records exist in PiXL.Raw (they were already written by the Edge's failover path) and can be re-enriched.

### LeadQualityScoring Position

The lead quality scorer runs last (step 15) because it consumes values produced by earlier steps:
- `MouseEntropy` → from browser
- `ContradictionCount` → from step 10
- `IsKnownBot` → from step 1
- `HasMatchingTimezone` → from step 11

If the lead scorer ran earlier, these values would be zero/null and the score would be inaccurate.
