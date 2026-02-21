---
subsystem: forge
title: SmartPiXL Forge
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - architecture/overview
  - architecture/data-flow
  - architecture/edge
  - subsystems/enrichment-pipeline
  - subsystems/failover
  - database/etl-procedures
---

# SmartPiXL Forge

## Atlas Public

The SmartPiXL Forge is our enrichment engine — the component that transforms raw visitor data into actionable intelligence. While the Edge captures data in milliseconds, the Forge takes its time to perform deep analysis with no time constraints.

**What the Forge does for you:**

- **Geographic verification** — confirms where your visitors actually are, not just where they say they are
- **Advanced bot detection** — checks visitors against databases of 10,000+ known bot patterns
- **Device intelligence** — identifies the actual device, operating system, and browser in structured detail
- **Cross-customer intelligence** — detects when the same visitor is hitting multiple websites simultaneously (a strong bot indicator)
- **Behavioral analysis** — detects replayed mouse movements and cultural fingerprint inconsistencies
- **Lead quality scoring** — rates each visitor's likelihood of being a genuine prospect

The Forge runs 24/7 as a background service, continuously processing and enriching every visitor record.

## Atlas Internal

### What the Forge Does

Think of the Edge as the receptionist and the Forge as the back office. The Edge takes the call quickly, the Forge does the research.

The Forge is a Windows Service that receives visitor records from the Edge and adds intelligence through three tiers of enrichment:

**Tier 1 — Library Lookups (milliseconds)**
Quick lookups against local databases and APIs:
- Is this user agent a known bot? (10,000+ patterns)
- What browser, OS, and device is this? (structured parsing)
- What does the reverse DNS say? (ISP vs cloud hosting)
- MaxMind GeoIP2 — precise offline geographic data
- IPAPI Pro — additional geo fields for new IPs
- WHOIS — what organization owns this IP block?

**Tier 2 — Cross-Request Intelligence (real-time)**
The Forge sees ALL traffic across ALL customers simultaneously. This gives it unique visibility:
- Same fingerprint hitting 5+ different customers in 5 minutes? → Bot
- Session tracking across pages (page count, duration, navigation pattern)
- Device affluence estimation (high-end GPU + 4K screen + macOS = high-value visitor)
- Lead quality scoring (reverse of bot scoring — how human is this visitor?)

**Tier 3 — Advanced Detection**
Things no other visitor tracking platform does:
- Cultural fingerprint analysis (French fonts + Vietnamese language + US IP = VPN)
- Device age estimation (old GPU + new browser + datacenter IP = likely bot in Docker)
- Impossible combination detection (mobile user agent + 4K screen + mouse movements = impossible)
- Behavioral replay detection (identical mouse paths from different devices = recorded behavior)
- Dead Internet Index (per-customer bot traffic percentage trend)

### Background Operations

Beyond enrichment, the Forge also handles:
- **ETL processing** — converts raw records to structured data every 60 seconds
- **Data maintenance** — old record purging, index rebuilding
- **Infrastructure health** — monitors SQL Server, IIS, and services
- **Self-healing** — automatic remediation of common issues (restart IIS pool, reset stuck processes)
- **Data synchronization** — keeps company/pixel configuration in sync with Xavier

### How It Gets Data

The Edge sends records through a named pipe (think of it as a direct internal connection between the two processes). If the Forge is restarting, records queue as files on disk and get processed when the Forge comes back. No data is ever lost.

## Atlas Technical

### Project Structure

`SmartPiXL.Forge/` — a `Microsoft.NET.Sdk.Worker` project targeting `net10.0`:

```
SmartPiXL.Forge/
├── Program.cs                           # Composition root, DI registration
├── appsettings.json                     # Connection strings, pipe config, intervals
└── Services/
    ├── ForgeChannels.cs                 # DI wrapper for 2 Channel<TrackingData>
    ├── PipeListenerService.cs           # Named pipe server (BackgroundService)
    ├── EnrichmentPipelineService.cs     # 15-step enrichment chain
    ├── SqlBulkCopyWriterService.cs      # PiXL.Raw writer via SqlBulkCopy
    ├── FailoverCatchupService.cs        # JSONL file catch-up (60s interval)
    ├── EtlBackgroundService.cs          # usp_ParseNewHits + usp_MatchVisits
    ├── IpApiSyncService.cs              # Xavier IPGEO delta sync
    ├── CompanyPiXLSyncService.cs        # Xavier Company/Pixel MERGE
    ├── SelfHealingService.cs            # Automated remediation loop
    ├── InfraHealthService.cs            # Infrastructure health probes
    ├── MaintenanceSchedulerService.cs   # Daily purge, weekly index rebuild
    ├── EmailNotificationService.cs      # SMTP + SMS ops alerts
    ├── HttpEdgeHealthClient.cs          # IEdgeHealthClient via HTTP
    └── Enrichments/
        ├── BotUaDetectionService.cs     # Tier 1: NetCrawlerDetect wrapper
        ├── UaParsingService.cs          # Tier 1: UAParser + DeviceDetector.NET
        ├── DnsLookupService.cs          # Tier 1: DnsClient reverse PTR
        ├── MaxMindGeoService.cs         # Tier 1: GeoLite2 .mmdb offline lookup
        ├── IpApiLookupService.cs        # Tier 1: IPAPI Pro real-time (conditional)
        ├── WhoisAsnService.cs           # Tier 1: WHOIS ASN/org lookup
        ├── CrossCustomerIntelService.cs # Tier 2: Multi-customer hit tracking
        ├── SessionStitchingService.cs   # Tier 2: Fingerprint-keyed sessions
        ├── DeviceAffluenceService.cs    # Tier 2: GPU/hardware affluence signal
        ├── GpuTierReference.cs          # Tier 2: ~50 GPU patterns → tier
        ├── LeadQualityScoringService.cs # Tier 2: 9-signal lead quality (0-100)
        ├── GeographicArbitrageService.cs# Tier 3: 7-signal cultural consistency
        ├── CulturalReference.cs         # Tier 3: Font/language/TZ reference data
        ├── DeviceAgeEstimationService.cs# Tier 3: GPU release year + anomalies
        ├── ContradictionMatrixService.cs# Tier 3: 13 cross-signal rules
        ├── BehavioralReplayService.cs   # Tier 3: FNV-1a mouse path hashing
        └── DeadInternetService.cs       # Tier 3: Per-company bot index
```

### NuGet Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.Data.SqlClient` | 6.1.4 | SQL Server connectivity |
| `Microsoft.Extensions.Hosting.WindowsServices` | 10.0.2 | Windows Service hosting |
| `System.ServiceProcess.ServiceController` | 10.0.2 | Service state management |
| `Microsoft.Extensions.Http` | 10.0.0 | `HttpClient` DI for Edge health |
| `NetCrawlerDetect` | 1.2.113 | Bot/crawler UA detection |
| `UAParser` | 3.1.47 | User-agent parsing |
| `DeviceDetector.NET` | 6.4.7 | Device type/model/brand |
| `DnsClient` | 1.8.0 | Async reverse DNS |
| `MaxMind.GeoIP2` | 5.4.1 | Offline geo (.mmdb) |
| `Whois` | 3.0.1 | WHOIS ASN/org lookups |
| `MathNet.Numerics` | 5.0.0 | Statistical analysis |
| `FuzzySharp` | 2.0.2 | Fuzzy string matching |

### Enrichment Pipeline (15 Steps)

The `EnrichmentPipelineService` runs steps sequentially per record:

| Step | Service | Tier | Appends |
|------|---------|------|---------|
| 1 | BotUaDetectionService | 1 | `_srv_knownBot`, `_srv_botName` |
| 2 | UaParsingService | 1 | `_srv_browser`, `_srv_browserVer`, `_srv_os`, `_srv_osVer`, `_srv_deviceType/Model/Brand` |
| 3 | DnsLookupService | 1 | `_srv_rdns`, `_srv_rdnsCloud` |
| 4 | MaxMindGeoService | 1 | `_srv_mmCC/Reg/City/Lat/Lon/ASN/ASNOrg` |
| 5 | IpApiLookupService | 1 | `_srv_ipapiCC/ISP/Proxy/Mobile/Reverse/ASN` (conditional — new/stale IPs only) |
| 6 | WhoisAsnService | 1 | `_srv_whoisASN`, `_srv_whoisOrg` (conditional — if MaxMind ASN empty) |
| 7 | SessionStitchingService | 2 | `_srv_sessionId/HitNum/DurationSec/Pages` |
| 8 | CrossCustomerIntelService | 2 | `_srv_crossCustHits/Window/Alert` |
| 9 | DeviceAffluenceService | 2 | `_srv_affluence`, `_srv_gpuTier` |
| 10 | ContradictionMatrixService | 3 | `_srv_contradictions`, `_srv_contradictionList` |
| 11 | GeographicArbitrageService | 3 | `_srv_culturalScore`, `_srv_culturalFlags` |
| 12 | DeviceAgeEstimationService | 3 | `_srv_deviceAge`, `_srv_deviceAgeAnomaly` |
| 13 | BehavioralReplayService | 3 | `_srv_replayDetected`, `_srv_replayMatchFP` |
| 14 | DeadInternetService | 3 | `_srv_deadInternetIdx` |
| 15 | LeadQualityScoringService | 2* | `_srv_leadScore` |

*Lead scoring runs last because it consumes Tier 3 outputs (contradiction count, timezone match).

### Configuration

Two configuration bindings:
- `IOptions<TrackingSettings>` (from `"Tracking"` section) — connection strings, Xavier sync, SMTP
- `IOptions<ForgeSettings>` (from `"Forge"` section) — pipe name, channel capacities, failover dir, enrichment toggles

### Deployment

```powershell
Stop-Service -Name "SmartPiXL-Forge"
dotnet publish -c Release -o "C:\Services\SmartPiXL-Forge"
Start-Service -Name "SmartPiXL-Forge"
```

Runs as `NT AUTHORITY\SYSTEM` — requires SQL login with `db_datareader`, `db_datawriter`, `EXECUTE`.

## Atlas Private

### `ForgeChannels` Disambiguation Problem

Standard DI can't distinguish two `Channel<TrackingData>` registrations. The solution is `ForgeChannels.cs` — a simple wrapper class holding both channels:
- `Enrichment` — pipe listener → enrichment pipeline (capacity from `ForgeSettings.PipeChannelCapacity`)
- `SqlWriter` — enrichment pipeline → SQL writer (capacity from `ForgeSettings.SqlWriterChannelCapacity`)

Both use `BoundedChannelFullMode.DropOldest`. Registered as singleton. Each service takes `ForgeChannels` in its constructor and reads the channel it needs.

### Xavier Sync — Temporary Architecture

Three sync services bridge SmartPiXL and Xavier (192.168.88.35, SQL Server 2017):

| Service | Direction | Interval | Purpose |
|---------|-----------|----------|---------|
| `IpApiSyncService` | Xavier → SmartPiXL | Daily | Delta sync from `IPGEO.dbo.IP_Location_New` to `IPAPI.IP` |
| `CompanyPiXLSyncService` | Xavier → SmartPiXL | Every 6h | MERGE into `PiXL.Company` + `PiXL.Pixel` |

These are **temporary** — they exist only because Xavier's legacy front-end is currently the client-facing product. When a new front-end replaces Xavier, SmartPiXL becomes authoritative and these syncs are decommissioned. Timeline is extended but not permanent.

Xavier connection strings use `Encrypt=True;TrustServerCertificate=True` because Xavier's SQL Server doesn't present its custom certificate (it's in the cert store but not configured in SQL Server Configuration Manager). The `TrustServerCertificate=True` is acceptable for the private network.

SQL auth on Xavier uses environment variables (`SMARTPIXL_SQL_USER` / `SMARTPIXL_SQL_PASS`) — if present, `Program.cs` rewrites `Integrated Security=True` to `User Id=...;Password=...` in the Xavier connection strings. This enables cross-machine auth without domain accounts.

### `IpApiLookupService` — Rate Limiting Strategy

IPAPI Pro allows ~30 requests/minute. The service uses a `SemaphoreSlim` + 2100ms delay to stay at ~28.5 req/min. At startup, it loads all known IPs from `IPAPI.IP` (where `LastSeen IS NOT NULL`) into a `ConcurrentDictionary<string, DateTime>`. Each incoming record checks the dictionary — if the IP is known and fresh (<90 days), the API call is skipped entirely.

This replaces the legacy 2AM batch job that blasted 6M rows at IPAPI daily. The Forge approach only calls IPAPI for genuinely new IPs, reducing API cost by ~95%.

Known issue: the initial load queries `IPAPI.IP` which has 344M+ rows. The `WHERE LastSeen IS NOT NULL` filter narrows this substantially, but startup can take 30-60 seconds depending on SQL performance.

### `SelfHealingService` — Detection + Remediation

Runs a 60-second detection loop checking:
- Edge health via `HttpEdgeHealthClient` (`/internal/health`)
- SQL connectivity
- ETL watermark progress (stuck = watermark hasn't advanced in N cycles)
- Forge pipeline depth (channel depths growing = writer bottleneck)

Proposed remediations are written to `Ops.RemediationLog` with SQL commands. The Sentinel's `RemediationService` provides the approve/skip API. Auto-execution is configurable per remediation type.

### Startup Registration Order

In `Program.cs`, services are registered and started in order. The order matters:
1. `ForgeChannels` (singleton, created first)
2. `MaxMindGeoService` (loads .mmdb files — needed by pipeline)
3. `IpApiLookupService` (loads known IP cache — needed by pipeline)
4. Enrichment services (all singletons, no startup I/O)
5. `SqlBulkCopyWriterService` (hosted, starts draining SQL channel)
6. `EnrichmentPipelineService` (hosted, starts draining enrichment channel)
7. `PipeListenerService` (hosted, starts accepting pipe connections — must be last to avoid filling channels before downstream is ready)
8. `FailoverCatchupService` (hosted, 60s timer)
9. Background services (ETL, sync, health, maintenance)
