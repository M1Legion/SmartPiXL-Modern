---
title: "F2: Enrichment Engine"
node: forge.f2-enrichment
nodeType: subsystem
status: current
related:
  - systems/forge
  - subsystems/f1-ingest
  - subsystems/f3-sql-writer
---

# F2: Enrichment Engine

## Atlas Public

The Enrichment Engine is the analytical core of SmartPiXL. Every visitor record passes through 16 specialized intelligence services that add geographic, behavioral, device, and threat data — transforming a simple page view into a comprehensive visitor profile.

**What the engine delivers:**
- **Bot detection** — 80+ signals from browser, network, and behavior layers
- **Device fingerprinting** — cookieless visitor identification across sessions
- **Geographic intelligence** — city-level location from multiple data sources
- **Behavioral analysis** — mouse movement authenticity, session engagement patterns
- **Quality scoring** — 0–100 composite score separating real prospects from noise
- **Cross-customer intelligence** — detects visitors appearing across multiple websites

## Atlas Internal

### The 16 Enrichment Services

Records flow through an adaptive worker pool that runs all 16 services sequentially per record:

| # | Service | What It Does |
|---|---------|-------------|
| 1 | **UaParsing** | Extracts browser, OS, device type, model, brand from user-agent |
| 2 | **BotUaDetection** | Checks UA against 10,000+ known bot/crawler patterns |
| 3 | **DnsLookup** | Reverse DNS — detects cloud-hosted origins (AWS, GCP hostnames) |
| 4 | **WhoisAsn** | ASN ownership lookup — supplements MaxMind when ASN data is missing |
| 5 | **MaxMindGeo** | Offline GeoIP2 — city, region, country, coordinates, ISP |
| 6 | **DeadInternet** | Dead Internet Theory scoring — per-customer bot traffic trend |
| 7 | **BehavioralReplay** | Detects replayed/recorded mouse movements via path hashing |
| 8 | **CrossCustomerIntel** | Same visitor hitting multiple SmartPiXL customers in minutes |
| 9 | **SessionStitching** | Groups page views into sessions (30-minute timeout) |
| 10 | **IpClassification** | Classifies IP as datacenter, residential, mobile, CGNAT, etc. |
| 11 | **ContradictionMatrix** | Flags impossible device configurations (mobile UA + 4K + mouse) |
| 12 | **DeviceAffluence** | Classifies device value tier from hardware signals (LOW/MID/HIGH) |
| 13 | **DeviceAgeEstimation** | Triangulates GPU + OS + browser versions to estimate device age |
| 14 | **GeographicArbitrage** | Cultural fingerprint consistency (fonts, language, timezone vs IP) |
| 15 | **GpuTierReference** | GPU classification from WebGL renderer string |
| 16 | **LeadQualityScoring** | 0–100 composite score from positive human signals |

### Three Enrichment Tiers

**Tier 1: Library Lookups** (services 1–6)
Quick lookups against local databases and caches. Mostly in-memory, sub-millisecond each.

**Tier 2: Cross-Request Intelligence** (services 7–9)
Patterns that emerge across multiple visits. Uses in-memory aggregates maintained by the Forge.

**Tier 3: Asymmetric Detection** (services 10–15)
Adversarial detection — catches sophisticated bots and spoofed devices by finding contradictions between different signal dimensions.

**Final Scoring** (service 16)
Consumes results from all prior services to produce a composite lead quality score.

### Append-Only Design

The Forge never modifies original browser data. Server-side enrichment results are added as `_srv_*` parameters. This means:
- Original browser data is always preserved
- You can always compare what the browser reported vs. what the server determined
- Enrichment failures don't corrupt original records

### Bot Score Ranges

| Score | Classification | Typical Cause |
|-------|---------------|--------------|
| 0 | Clean | No bot signals detected |
| 1–15 | Low suspicion | Minor anomalies |
| 15–30 | Moderate | Multiple indicators (datacenter IP + low mouse activity) |
| 30–50 | High | Strong patterns (known bot UA + headless indicators) |
| 50+ | Confirmed bot | Overwhelming evidence (webdriver + automation markers) |

## Atlas Technical

### Worker Pool Architecture

`EnrichmentPipelineService` reads from `ForgeChannels.Enrichment` and processes records through all 16 services. Workers are adaptive — the pool scales based on channel depth and processing throughput.

```
ForgeChannels.Enrichment (Channel<TrackingData>)
  → Worker Pool (adaptive count)
    → UaParsingService.EnrichAsync()
    → BotUaDetectionService.EnrichAsync()
    → DnsLookupService.EnrichAsync()
    → ... (16 services in sequence)
  → ForgeChannels.SqlWriter (Channel<TrackingData>)
```

### Caching Strategy

Several services maintain bounded in-memory caches to avoid redundant lookups:
- **UaParsing** — UA string cache (most visitors share common UAs)
- **BotUaDetection** — Bot pattern match cache
- **DnsLookup** — Reverse DNS cache (DNS lookups are slow; cache is critical)
- **MaxMindGeo** — GeoIP result cache per IP
- **WhoisAsn** — ASN lookup cache

### Health Probes

F2 exposes 17 health probes — one per enrichment service plus the worker pool:

| Probe | What It Checks |
|-------|---------------|
| **Worker Pool** | Adaptive enrichment workers processing records |
| **UaParsing** | UA parsing with bounded cache |
| **BotUaDetection** | Bot/crawler detection with bounded cache |
| **DnsLookup** | Reverse DNS lookup cache |
| **WhoisAsn** | WHOIS ASN lookup cache |
| **MaxMindGeo** | MaxMind GeoIP2 database lookups |
| **DeadInternet** | Dead Internet Theory detection |
| **BehavioralReplay** | Behavioral replay detection |
| **CrossCustomerIntel** | Cross-customer intelligence tracking |
| **SessionStitching** | Session stitching across page views |
| **IpClassification** | IP classification |
| **ContradictionMatrix** | Signal contradiction detection |
| **DeviceAffluence** | Device affluence scoring |
| **DeviceAgeEstimation** | Device age estimation |
| **GeographicArbitrage** | Geographic arbitrage detection |
| **GpuTierReference** | GPU tier classification |
| **LeadQualityScoring** | Lead quality composite scoring |

## Atlas Private

### Key Files

| File | Purpose |
|------|---------|
| `SmartPiXL.Forge/Services/EnrichmentPipelineService.cs` | Worker pool orchestration |
| `SmartPiXL.Shared/Services/UaParsingService.cs` | Service 1: UA parsing |
| `SmartPiXL.Shared/Services/BotUaDetectionService.cs` | Service 2: Bot detection |
| `SmartPiXL.Shared/Services/DnsLookupService.cs` | Service 3: DNS |
| `SmartPiXL.Shared/Services/WhoisAsnService.cs` | Service 4: WHOIS |
| `SmartPiXL.Shared/Services/MaxMindGeoService.cs` | Service 5: GeoIP |
| `SmartPiXL.Shared/Services/DeadInternetService.cs` | Service 6: Dead Internet |
| `SmartPiXL.Shared/Services/BehavioralReplayService.cs` | Service 7: Replay detection |
| `SmartPiXL.Shared/Services/CrossCustomerIntelService.cs` | Service 8: Cross-customer |
| `SmartPiXL.Shared/Services/SessionStitchingService.cs` | Service 9: Sessions |
| `SmartPiXL.Shared/Services/IpClassificationService.cs` | Service 10: IP classification |
| `SmartPiXL.Shared/Services/ContradictionMatrixService.cs` | Service 11: Contradictions |
| `SmartPiXL.Shared/Services/DeviceAffluenceService.cs` | Service 12: Affluence |
| `SmartPiXL.Shared/Services/DeviceAgeEstimationService.cs` | Service 13: Device age |
| `SmartPiXL.Shared/Services/GeographicArbitrageService.cs` | Service 14: Geo arbitrage |
| `SmartPiXL.Shared/Services/GpuTierReferenceService.cs` | Service 15: GPU tier |
| `SmartPiXL.Shared/Services/LeadQualityScoringService.cs` | Service 16: Lead scoring |

### Adding a New Enrichment Service

1. Create a service class implementing the enrichment interface in `SmartPiXL.Shared/Services/`
2. Register in Forge's `Program.cs`
3. Add to the enrichment pipeline sequence in `EnrichmentPipelineService`
4. Add a probe row to `Health.Node` for monitoring
5. Add unit tests in `SmartPiXL.Tests/`
