---

# Brilliant PiXL — Implementation Plan

> **Authoritative implementation roadmap.** All architectural decisions finalized by platform owner on 2026-02-19.
> This document is the work order for Copilot custom agents executing each phase.
> Design source of truth: BRILLIANT-PIXL-DESIGN.md

---

## Architectural Decisions (Locked)

These decisions are final. Do not re-ask. Do not deviate.

| Decision | Resolution | Rationale |
|----------|-----------|-----------|
| **Forge is a new project** | Create `SmartPiXL.Forge/` from scratch | Worker is deprecated reference. The Forge has one core purpose: receive enriched records from IIS, perform slow enrichments, write to SQL. |
| **Worker is OFF** | Stop `SmartPiXL-Worker` Windows Service immediately. Keep project in solution as read-only reference. Delete when Sentinel service is built in Phase 10. | Two-week-old dev project, nothing in the DB matters except ~2000 real hits among millions of legacy data. |
| **Dashboards are DOWN during rebuild** | Tron ops, Tron metrics, and Atlas are offline during Phases 1–9. They will be rebuilt as their own `SmartPiXL.Sentinel` service in Phase 10. | Dashboards need redesign based on new enrichment data anyway. Dashboards are not part of the hot path. |
| **ETL is OFF during rebuild** | No ETL runs until Forge Phase 2 is verified. | Worker is off. The Forge owns ETL exclusively. No dual-running risk. |
| **IPC: Named pipe** | IIS Edge → `NamedPipeClientStream` → Forge `NamedPipeServerStream`. JSONL failover to disk if pipe unavailable. | Design doc §5.2 specifies this exactly. Zero data loss under any failure mode. |
| **CLR: Separate database** | `SmartPiXL_CLR` database with its own CLR filegroup and modified permissions. Cross-database calls from `SmartPiXL`. | Owner's standard practice. Isolates CLR's `TRUSTWORTHY`/assembly-signing requirements from the production database. |
| **CLR: .NET Framework 4.8** | Target `net48` for the CLR assembly project. **VALIDATED (2026-02-20):** SQL Server 2025 RTM-GDR (17.0.1050.2) CLR host is `.NET Framework v4.0.30319`, NOT modern .NET. .NET 10 assemblies rejected with "references assembly 'system.runtime, version=10.0.0.0'". Use `<LangVersion>latest</LangVersion>` for modern C# syntax compiled to Framework IL. | Owner directive was to FAFO — we did, and .NET 10 doesn't work. net48 with latest C# syntax is the ceiling. |
| **MaxMind: Confirmed viable** | MaxMind GeoLite2 `.mmdb` files successfully downloaded and loaded into SmartPiXL DB in external testing. Include MaxMind as primary offline geo. AB test against IPAPI when enrichment pipeline is live. | Open question resolved. IPAPI Pro remains as supplement for fields MaxMind doesn't cover (proxy, mobile carrier, ASN detail). |
| **GPU reference table** | Start with tier-based approach (GPU generation → age bucket). RTX 5xxx = recent/high-value, RTX 2xxx = ~10 years old/budget. Refine with live data. Do NOT attempt real-time pricing. | Exact GPU pricing is non-trivial and may not be feasible in real-time. Tier classification still provides affluence signal value. |
| **Phasing: Sequential** | Complete and verify each phase before starting the next. | Owner directive. Lower risk, clear progress tracking. |

---

## Process Architecture (Post-Rebuild)

```
Visitor Browser → PiXL Script (157+2 fields, 500ms window)
     │
     ▼ _SMART.GIF request
IIS (PiXL Edge) — in-process w3wp.exe
     │  Parse HTTP + 12 fast enrichments (~5μs) + _srv_* params
     │  Return 43-byte transparent GIF
     │
     ▼ Named pipe (enriched TrackingData record)
SmartPiXL Forge — Windows Service
     │  Tier 1 enrichments (IPAPI, NetCrawlerDetect, UAParser, DnsClient, MaxMind, WHOIS)
     │  Tier 2 enrichments (cross-customer, lead scoring, sessions, affluence)
     │  Tier 3 enrichments (cultural consistency, device age, contradiction matrix, replay, dead internet)
     │  SqlBulkCopy → PiXL.Raw
     │  ETL every 60s → PiXL.Parsed + Device/IP/Visit/Match
     │  Background services (IpApiSync, CompanySync, SelfHealing, Maintenance)
     │
     ▼ (deferred — Phase 10)
SmartPiXL Sentinel — Windows Service (port 7500)
     │  Tron Operations, Tron Metrics, Atlas Portal
     │  /api/dash/*, /api/atlas/*, /api/traffic-alert/*
```

**Failover path (pipe unavailable):**
```
IIS Edge → JSONL file to Failover/ directory (durable on disk)
Forge restarts → FailoverCatchupService reads JSONL → enrichment pipeline → SQL
```

---

## Current State Summary

| Area | Status | Completeness |
|------|--------|-------------|
| PiXL Script | 155/157 fields implemented | 98.7% |
| IIS Fast Pass (12 enrichment steps) | All 12 implemented | 100% |
| Forge | Does not exist | 0% |
| NuGet enrichment libraries | 0 of 8 installed | 0% |
| SQL schema (core: Raw, Parsed, Device, IP, Visit, Match) | All exist, 40 migrations | 100% |
| SQL advanced (CLR, vectors, graph, subnet reputation) | CLR deployed (10 functions), vectors (VECTOR(64)+VECTOR(32)), graph (3 nodes, 2 edges). Subnet reputation pending Phase 8. | 75% |
| TrafficAlert subsystem | Not implemented | 0% |
| Worker (to be deprecated) | Fully functional, running ETL/sync/health/dashboards | 100% (deprecated) |
| Dashboards (Tron + Atlas) | Both functional | 100% (going offline) |
| Test coverage | 7 test files covering ~5 core services | ~40% |

---

## Reference Files

Agents must read these before beginning any phase:

| File | Purpose |
|------|---------|
| BRILLIANT-PIXL-DESIGN.md | Design source of truth (1,191 lines) |
| csharp.instructions.md | C# coding standards (zero-alloc hot paths, sealed classes, Channel\<T\>, ITrackingLogger) |
| sql.instructions.md | SQL conventions (schema prefixes, watermark ETL, numbered migrations) |
| copilot-instructions.md | Deployment architecture, critical files, port assignments |
| TrackingData.cs | 9-column PiXL.Raw record structure |
| 39_FixParseNewHitsBigint.sql | Authoritative ETL proc (13-phase watermark-driven batch) |

---

## Phase 1 — PiXL Script: Final 2 Fields + SQL Schema

**Scope:** Add `screenExtended` and `mousePath` to the browser script, add corresponding columns to `PiXL.Parsed`, update `ETL.usp_ParseNewHits` to parse them.

**Effort:** Small. No dependencies.

### Steps

1. **PiXL Script changes** in PiXLScript.cs:
   - Add `data.screenExtended` — sync read of `window.screen.isExtended`, output as `1` or `0`. Place in the Screen & Display section (near `data.sy` around line 337).
   - Add `data.mousePath` — after the mouse statistical analysis code (~line 1095), serialize the existing 50-point `moves[]` array as a compact `x,y,t|x,y,t|...` string. Assign to `data.mousePath`. Cap total string length to prevent query string bloat (e.g., 2000 chars).

2. **SQL schema** — migration script `SmartPiXL/SQL/41_ScreenExtendedMousePath.sql`:
   - Add to `PiXL.Parsed`:
     - `ScreenExtended BIT NULL` (in the Screen & Display column group)
     - `MousePath VARCHAR(2000) NULL` (in the Behavioral column group)
   - Update `ETL.usp_ParseNewHits`:
     - Phase 1 (INSERT): Add `ScreenExtended = TRY_CAST(dbo.GetQueryParam(p.QueryString, 'screenExtended') AS BIT)`
     - Phase 7 (UPDATE — Behavioral): Add `MousePath = dbo.GetQueryParam(src.QueryString, 'mousePath')`

3. **Unit tests** in PiXLScriptTests.cs:
   - Test that generated JS contains `data.screenExtended` assignment
   - Test that generated JS contains `data.mousePath` assignment
   - Test that `mousePath` encoding produces expected `x,y,t|x,y,t` format

4. **Design doc update** — Update BRILLIANT-PIXL-DESIGN.md:
   - Move `screenExtended` and `mousePath` from "Data Points to ADD" into their inventory table sections
   - Update field count from 157 → 159

### Verification
- `dotnet test` — all PiXL script tests pass
- Run `ETL.usp_ParseNewHits` against test data — `PiXL.Parsed.ScreenExtended` and `PiXL.Parsed.MousePath` columns populate correctly
- Inspect generated JS output for both new `data.*` assignments

---

## Phase 2 — Forge Project: Foundation

**Scope:** Create the new `SmartPiXL.Forge` project. Build named pipe server, enrichment pipeline shell, JSONL failover catch-up, SQL writer. Port all background services from Worker (reference only — Worker stays off).

**Effort:** Large. This is the structural foundation for everything that follows.

### Steps

1. **Create project** `SmartPiXL.Forge/`:
   - `SmartPiXL.Forge.csproj` — `net10.0`, `UseWindowsService()`, reference SmartPiXL.Shared
   - NuGet: `Microsoft.Data.SqlClient`, `Microsoft.Extensions.Hosting.WindowsServices`, `System.ServiceProcess.ServiceController`
   - `Program.cs` — composition root, DI registration, `UseWindowsService()`, Kestrel disabled (no HTTP — pipe only + background services)
   - `appsettings.json` — connection string (`localhost\SQL2025`, `SmartPiXL`), Xavier connection strings, pipe name config, EdgeBaseUrl, SMTP, sync intervals, maintenance schedules
   - Add to SmartPixl.sln

2. **Named pipe server** — `Services/PipeListenerService.cs`:
   - `sealed class PipeListenerService : BackgroundService`
   - Hosts `NamedPipeServerStream` (pipe name configurable, default `SmartPiXL-Enrichment`)
   - Accepts connections from IIS Edge, reads serialized `TrackingData` records (use `System.Text.Json` UTF-8 deserialization)
   - Enqueues to internal `Channel<TrackingData>` (bounded, 50,000 capacity)
   - Auto-reconnects on pipe break. Multiple concurrent pipe instances for throughput.

3. **Enrichment pipeline shell** — `Services/EnrichmentPipelineService.cs`:
   - `sealed class EnrichmentPipelineService : BackgroundService`
   - Reads from the pipe Channel
   - Placeholder enrichment chain (Phases 4-6 will add real enrichments)
   - After enrichment, enqueues to SQL writer Channel

4. **SQL writer** — `Services/SqlBulkCopyWriterService.cs`:
   - Port from DatabaseWriterService.cs (711 lines)
   - `Channel<TrackingData>` drain → `SqlBulkCopy` → `PiXL.Raw`
   - Keep circuit breaker pattern, dead-letter JSONL, custom `DbDataReader`
   - Keep ordinal-based column mapping for the 9 PiXL.Raw columns

5. **JSONL failover catch-up** — `Services/FailoverCatchupService.cs`:
   - `sealed class FailoverCatchupService : BackgroundService`
   - 60-second timer, scans configured failover directory for `.jsonl` files
   - Deserializes `TrackingData` records, feeds into enrichment pipeline Channel
   - Deletes processed files after successful pipeline ingestion
   - Handles partial files (line-by-line processing, skip malformed lines)

6. **Port background services from Worker** (Worker project is read-only reference):
   - `Services/EtlBackgroundService.cs` — calls `ETL.usp_ParseNewHits` + `ETL.usp_MatchVisits` every 60s (reference: EtlBackgroundService.cs, 138 lines)
   - `Services/IpApiSyncService.cs` — Xavier → IPAPI.IP daily sync with deadlock retry (reference: 486 lines)
   - `Services/CompanyPiXLSyncService.cs` — Xavier → PiXL.Company/Settings MERGE every 6h (reference: 713 lines)
   - `Services/SelfHealingService.cs` — health monitoring + auto-remediation (reference: 711 lines)
   - `Services/InfraHealthService.cs` — Windows services, SQL, IIS, app metrics probes (reference: 591 lines)
   - `Services/MaintenanceSchedulerService.cs` — daily purge + weekly index rebuild (reference: 253 lines)
   - `Services/EmailNotificationService.cs` — SMTP + SMS ops alerts (reference: 198 lines)
   - `Services/HttpEdgeHealthClient.cs` — IEdgeHealthClient via HTTP to Edge (reference: 101 lines)

7. **Shared library additions** (if needed):
   - `SmartPiXL.Shared/Configuration/ForgeSettings.cs` — pipe name, failover directory, enrichment toggles
   - Or extend existing `TrackingSettings.cs` with Forge-specific sections

### Convention Reminders for Agents
- Every class is `sealed` unless designed for inheritance
- Use `ITrackingLogger`, never `Microsoft.Extensions.Logging.ILogger`
- Use `Channel<T>` (bounded) for all producer-consumer queues
- Use custom `DbDataReader` for `SqlBulkCopy`, never `DataTable`
- Follow file naming: `{Name}Service.cs` for services, `{Domain}Endpoints.cs` for endpoints

### Verification
- Forge builds cleanly, starts as Windows Service
- Pipe listener accepts test connections (write a simple pipe client test)
- JSONL catch-up processes test `.jsonl` files
- ETL runs on schedule (test with existing PiXL.Raw data)
- All ported services start without error
- `dotnet test` — existing tests still pass (no regressions in Shared)

---

## Phase 3 — IIS Edge: Pipe Client + JSONL Durability

**Scope:** Rewire the Edge to send enriched records to the Forge via named pipe instead of writing to SQL directly. Add JSONL failover for durability.

**Effort:** Medium. Modifies existing Edge code, does NOT touch enrichment logic.

### Steps

1. **Pipe client** — `SmartPiXL/Services/PipeClientService.cs`:
   - `sealed class PipeClientService` — singleton, maintains `NamedPipeClientStream` connection to the Forge
   - `ValueTask EnqueueAsync(TrackingData record)` — serialize to UTF-8 JSON, write to pipe
   - Auto-reconnect on pipe loss with exponential backoff
   - Register in DI in `Program.cs`

2. **JSONL failover writer** — `SmartPiXL/Services/JsonlFailoverService.cs`:
   - `sealed class JsonlFailoverService` — writes `TrackingData` as one JSON line per record to `Failover/` directory
   - Daily rolling files: `failover_2026_02_19.jsonl`
   - Called by `PipeClientService` when pipe is unavailable
   - Uses `Channel<TrackingData>` internally → single background writer thread (no file contention)

3. **Modify `TrackingEndpoints.cs`** — `CaptureAndEnqueue` method (~line 295-448):
   - After building the enriched query string and constructing the `TrackingData` record:
   - Replace `DatabaseWriterService.EnqueueAsync()` call with `PipeClientService.EnqueueAsync()`
   - `PipeClientService` internally: try pipe → if unavailable, try JSONL failover → if both fail, dead-letter (should be near-impossible)
   - GIF response is still returned immediately — pipe write is fire-and-forget from the request's perspective

4. **Reduce `DatabaseWriterService` role**:
   - `DatabaseWriterService` remains in the Edge project but is no longer the primary write path
   - Kept as a dead-letter handler of absolute last resort
   - Consider making it optional/conditional via config toggle

5. **Configuration** — appsettings.json:
   - Add `Tracking.PipeName` (default: `SmartPiXL-Enrichment`)
   - Add `Tracking.FailoverDirectory` (default: `Failover/`)
   - Add `Tracking.UsePipe` (boolean, default: `true` — allows falling back to direct SQL for testing)

6. **Integration test**: Fire test pixel hits → verify records flow through pipe → Forge receives → SQL has rows

### Verification
- Deploy Edge + Forge in dev
- Fire test `_SMART.GIF` requests via browser or `Invoke-WebRequest`
- Confirm `PiXL.Raw` receives rows via the Forge (not direct Edge write)
- Stop the Forge → fire more hits → confirm `.jsonl` files appear in `Failover/`
- Restart the Forge → confirm catch-up processes the JSONL files → rows appear in `PiXL.Raw`
- Zero data loss across all failure scenarios

---

## Phase 4 — Tier 1 Enrichments (Day 1 Libraries)

**Scope:** Install 8 approved NuGet packages. Build 6 enrichment services. Wire into the enrichment pipeline. Add SQL columns + ETL parsing.

**Effort:** Large. Each enrichment is a discrete, testable service.

**Design doc reference:** §5.3, §6.1

### Steps

1. **Install NuGet packages** in `SmartPiXL.Forge.csproj`:
   - `NetCrawlerDetect` — bot/crawler UA detection
   - `UAParser` — structured UA parsing (Google's regex DB)
   - `DeviceDetector.NET` — deep device identification (10,000+ patterns, IoT/TV/console)
   - `DnsClient` — pure .NET async DNS resolver
   - `MaxMind.GeoIP2` — offline geo database (~1μs lookup)
   - `Whois.NET` — WHOIS/ASN lookups
   - `MathNet.Numerics` — statistical analysis (used by Tier 2/3 scoring, install now)
   - `FuzzySharp` — fuzzy string matching (used by CLR fallback + near-duplicate UA detection)

2. **Enrichment services** in `Services/Enrichments/`:

   **a. `IpApiLookupService.cs`** — real-time IPAPI Pro for new/stale IPs
   - At startup: load all known IPs from `IPAPI.IP` into `HashSet<string>`
   - On each record: check if IP is in HashSet and fresh (< 90 days). If yes, skip. If no, call `https://pro.ip-api.com/json/{ip}?key=oJC4NplwJaCnbWw`
   - Batch parallel calls when multiple new IPs queue up
   - Write results to `IPAPI.IP` table + add to HashSet
   - Respect rate limits (IPAPI Pro allows 30 req/min on basic tier)
   - Appends: `_srv_ipapiCC`, `_srv_ipapiISP`, `_srv_ipapiProxy`, `_srv_ipapiMobile`, `_srv_ipapiReverse`, `_srv_ipapiASN`

   **b. `BotUaDetectionService.cs`** — wraps NetCrawlerDetect
   - `bool IsCrawler(string userAgent)` + `string? GetMatchedBot(string userAgent)`
   - Appends: `_srv_knownBot=1`, `_srv_botName={name}`

   **c. `UaParsingService.cs`** — wraps UAParser + DeviceDetector.NET
   - Parse raw UA → structured fields
   - Appends: `_srv_browser`, `_srv_browserVer`, `_srv_os`, `_srv_osVer`, `_srv_deviceType`, `_srv_deviceModel`, `_srv_deviceBrand`
   - DeviceDetector.NET second pass for IoT/TV/console/car browser classification

   **d. `DnsLookupService.cs`** — wraps DnsClient
   - Async reverse DNS: PTR record for IP → hostname
   - Cloud hostname pattern detection (regex for `ec2-`, `compute.amazonaws.com`, `googleusercontent.com`, etc.)
   - Appends: `_srv_rdns={hostname}`, `_srv_rdnsCloud=1` (if cloud pattern matched)
   - Timeout: 2 seconds per lookup, don't block pipeline

   **e. `MaxMindGeoService.cs`** — wraps MaxMind.GeoIP2
   - Load `.mmdb` file at startup (GeoLite2-City, GeoLite2-ASN, GeoLite2-Country — all 3 confirmed available)
   - Offline lookup (~1μs). Primary geo source.
   - Appends: `_srv_mmCC`, `_srv_mmReg`, `_srv_mmCity`, `_srv_mmLat`, `_srv_mmLon`, `_srv_mmASN`, `_srv_mmASNOrg`
   - Falls back to existing IPAPI data if MaxMind returns null/incomplete
   - Weekly `.mmdb` file refresh (MaxMind updates weekly)

   **f. `WhoisAsnService.cs`** — wraps Whois.NET
   - ASN/organization lookup for IPs not covered by existing AWS/GCP CIDR trie or MaxMind ASN
   - Appends: `_srv_whoisASN`, `_srv_whoisOrg`
   - Low priority per record — can be async with delayed update

3. **Wire into `EnrichmentPipelineService.cs`**:
   - Each record flows through: BotUaDetection → UaParsing → DnsLookup → MaxMindGeo → IpApiLookup (conditional) → WhoisAsn
   - Each service appends its `_srv_*` params to `TrackingData.QueryString`
   - Pipeline is sequential per record but the service processes records concurrently via Channel

4. **SQL schema** — migration script `SmartPiXL/SQL/42_ForgeTier1Columns.sql`:
   - Add columns to `PiXL.Parsed`:
     - `KnownBot BIT NULL`, `BotName VARCHAR(200) NULL`
     - `ParsedBrowser VARCHAR(100) NULL`, `ParsedBrowserVersion VARCHAR(50) NULL`
     - `ParsedOS VARCHAR(100) NULL`, `ParsedOSVersion VARCHAR(50) NULL`
     - `ParsedDeviceType VARCHAR(50) NULL`, `ParsedDeviceModel VARCHAR(100) NULL`, `ParsedDeviceBrand VARCHAR(100) NULL`
     - `ReverseDNS VARCHAR(500) NULL`, `ReverseDNSCloud BIT NULL`
     - `MaxMindCountry CHAR(2) NULL`, `MaxMindRegion VARCHAR(100) NULL`, `MaxMindCity VARCHAR(200) NULL`
     - `MaxMindLat DECIMAL(9,6) NULL`, `MaxMindLon DECIMAL(9,6) NULL`
     - `MaxMindASN INT NULL`, `MaxMindASNOrg VARCHAR(200) NULL`
     - `WhoisASN VARCHAR(50) NULL`, `WhoisOrg VARCHAR(200) NULL`
   - Update `ETL.usp_ParseNewHits` — add a new Phase 8B (or extend Phase 8) to parse all `_srv_*` Tier 1 params via `dbo.GetQueryParam()`

5. **Unit tests** in SmartPiXL.Tests:
   - `BotUaDetectionServiceTests.cs` — known bot UAs flagged, human UAs clean
   - `UaParsingServiceTests.cs` — structured parsing of Chrome/Firefox/Safari/Edge/mobile UAs
   - `DnsLookupServiceTests.cs` — mock DNS responses, cloud pattern detection
   - `MaxMindGeoServiceTests.cs` — known IP → expected geo result
   - Integration test: full pipeline end-to-end with mock enrichment data

### Verification
- Process a test record through full pipeline
- Inspect `PiXL.Raw.QueryString` — contains all new `_srv_*` params
- Run `ETL.usp_ParseNewHits` — `PiXL.Parsed` has populated Tier 1 columns
- Each enrichment service has passing unit tests
- MaxMind lookup returns correct geo for known test IPs
- NetCrawlerDetect correctly flags Googlebot, Bingbot, etc.

---

## Phase 5 — Tier 2 Enrichments (Cross-Request Intelligence)

**Scope:** Build stateful enrichment services that leverage the Forge's unique position: sees all customers, all traffic, in real-time.

**Effort:** Large. In-memory sliding window data structures.

**Design doc reference:** §5.4, §7.2

### Steps

1. **`Services/Enrichments/CrossCustomerIntelService.cs`**:
   - `ConcurrentDictionary<(string IP, string FP), CrossCustomerTracker>` — sliding window
   - Track: which CompanyIDs has this IP+FP combination hit in the last N minutes?
   - Same IP+FP hitting 3+ different companies in 5 minutes = bot signal
   - Same IP+FP hitting 10+ companies in 1 hour = definite scraper
   - Appends: `_srv_crossCustHits={count}`, `_srv_crossCustWindow={minutes}`, `_srv_crossCustAlert=1`
   - Memory management: evict entries older than 2 hours

2. **`Services/Enrichments/LeadQualityScoringService.cs`**:
   - Reverse of bot scoring — accumulate positive signals
   - Scoring inputs: residential IP (not datacenter, not proxy), consistent fingerprint (fpUniq=1), real mouse entropy (>threshold), 3+ detected fonts, clean canvas (no noise/evasion), matching timezone (no `_srv_geoTzMismatch`), valid session (2+ pages), no bot signals, no contradictions
   - Score 0-100, weighted sum
   - Appends: `_srv_leadScore={0-100}`

3. **`Services/Enrichments/SessionStitchingService.cs`**:
   - In-memory session graph keyed by composite fingerprint (canvasFP+audioHash or DeviceHash)
   - New session if: first hit from this FP, or gap > 30 minutes since last hit
   - Track: page sequence, hit count, total duration, entry page, last page
   - Appends: `_srv_sessionId={guid}`, `_srv_sessionHitNum={N}`, `_srv_sessionDurationSec={seconds}`, `_srv_sessionPages={count}`
   - Memory management: finalize and evict sessions after 30-minute inactivity

4. **`Services/Enrichments/DeviceAffluenceService.cs`**:
   - Input fields: `gpu` (GPU renderer), `cores` (hardware concurrency), `mem` (device memory), `sw`/`sh` (screen resolution), `plt`/`uaPlatform` (platform)
   - GPU tier classification: reference table mapping GPU model substrings to tier (HIGH: RTX 4xxx/5xxx, RX 7xxx, M3/M4; MID: RTX 3xxx, GTX 1xxx, RX 6xxx, M1/M2; LOW: Intel HD/UHD, SwiftShader, llvmpipe, GT 1030)
   - Combined signal: high GPU + 8+ cores + 8+ GB RAM + 4K screen + macOS = HIGH affluence; Intel HD + 4 cores + 4 GB + 1366x768 = LOW
   - Appends: `_srv_affluence=LOW|MID|HIGH`, `_srv_gpuTier=LOW|MID|HIGH`

5. **SQL schema** — migration script `SmartPiXL/SQL/43_ForgeTier2Columns.sql`:
   - Add to `PiXL.Parsed`:
     - `CrossCustomerHits INT NULL`, `CrossCustomerAlert BIT NULL`
     - `LeadQualityScore INT NULL`
     - `SessionId VARCHAR(36) NULL`, `SessionHitNumber INT NULL`, `SessionDurationSec INT NULL`, `SessionPageCount INT NULL`
     - `AffluenceSignal VARCHAR(4) NULL`, `GpuTier VARCHAR(4) NULL`
   - Update `ETL.usp_ParseNewHits` to parse Tier 2 `_srv_*` params

6. **GPU tier reference** — `Services/Enrichments/GpuTierReference.cs`:
   - Static lookup: dictionary of GPU model substrings → tier
   - Start with ~50 common GPU patterns (Nvidia RTX/GTX/GT families, AMD RX families, Intel HD/UHD/Arc, Apple M-series, SwiftShader, llvmpipe, Mesa)
   - Loaded from embedded resource or config file for easy updates
   - Will be refined with live data

7. **Unit tests** for each service

### Verification
- Process a sequence of test records with known fingerprints hitting multiple CompanyIDs → cross-customer alert fires at threshold
- Session stitching produces correct sessionIds, page counts, and durations for a simulated multi-page visit
- Lead scoring returns HIGH for a clean residential visitor, LOW for a datacenter bot
- Affluence signal correctly maps known GPU models to tiers

---

## Phase 6 — Tier 3 Enrichments (Asymmetric Detection)

**Scope:** The most algorithmically complex enrichments — things no one else in the pixel tracking space does.

**Effort:** Large. Heavy algorithmic design.

**Design doc reference:** §5.5

### Steps

1. **`Services/Enrichments/GeographicArbitrageService.cs`**:
   - Cross-reference cultural fingerprint fields against IP-derived geography:
     - `fonts` — Windows fonts on a macOS IP? Mac fonts on a Linux IP?
     - `lang`/`langs` — language tags consistent with geo country?
     - `dateFormat`/`numberFormat` — locale formatting consistent with geo?
     - `tzLocale` — calendar system, numbering system consistent with geo?
     - `voices` — speech synthesis voices consistent with platform + geo?
   - Cultural consistency score 0-100 (MathNet.Numerics for statistical weighting)
   - Example: French fonts + Vietnamese language + Persian calendar on a "US" IP = score 15 (inconsistent = likely VPN)
   - Appends: `_srv_culturalScore={0-100}`, `_srv_culturalFlags={comma-separated anomalies}`

2. **`Services/Enrichments/DeviceAgeEstimationService.cs`**:
   - GPU model → release year lookup (from GPU tier reference built in Phase 5, extended with approximate release years)
   - Cross-reference: modern browser version + old GPU + datacenter IP + zero mouse = headless bot in Docker
   - Cross-reference: old OS version + old GPU + residential IP + mouse movement = legitimate old machine
   - Appends: `_srv_deviceAge={years}`, `_srv_deviceAgeAnomaly=1` (if age contradicts behavioral pattern)

3. **`Services/Enrichments/ContradictionMatrixService.cs`**:
   - Rule engine: list of `(condition, expectedRange, signal)` tuples
   - Contradiction rules from design doc §5.5:
     - Mobile UA + 2560x1440 + mouse moves = impossible
     - macOS + DirectX GPU string = impossible
     - Battery API present + macOS Safari = impossible (Safari doesn't expose Battery API)
     - Touch points > 0 + no touch events support = impossible
     - Desktop UA + screen < 600px wide = suspicious
     - Linux + Apple fonts = impossible
   - Extensible: rules loaded from config or embedded resource
   - Appends: `_srv_contradictions={count}`, `_srv_contradictionList={comma-separated rule names}`

4. **`Services/Enrichments/BehavioralReplayService.cs`**:
   - Input: `mousePath` field (from Phase 1)
   - Hash the raw path string (use MurmurHash3 or similar fast non-crypto hash)
   - Maintain `ConcurrentDictionary<uint, (string Fingerprint, DateTime Seen)>` of recent path hashes
   - Same path hash from a DIFFERENT fingerprint = replayed recorded behavior
   - Same path hash from the SAME fingerprint = same user revisiting (not suspicious)
   - Appends: `_srv_replayDetected=1`, `_srv_replayMatchFP={original fingerprint}`
   - Memory management: evict entries older than 1 hour

5. **`Services/Enrichments/DeadInternetService.cs`**:
   - Per-customer per-hour sliding window aggregation:
     - Total hits, hits with `botScore > 0`, hits with `mouseMoves = 0`, hits from datacenter IPs, hits with contradictions, hits with replay detected
     - Unique fingerprints / total hits ratio
     - Cross-customer pollination rate (from Phase 5's CrossCustomerIntelService)
   - Track trends over time (in-memory, last 24 hours)
   - Appends: `_srv_deadInternetIdx={0-100}` (per customer, not per hit — attach to all hits for that customer)

6. **SQL schema** — migration script `SmartPiXL/SQL/44_ForgeTier3Columns.sql`:
   - Add to `PiXL.Parsed`:
     - `CulturalConsistencyScore INT NULL`, `CulturalFlags VARCHAR(500) NULL`
     - `DeviceAgeYears INT NULL`, `DeviceAgeAnomaly BIT NULL`
     - `ContradictionCount INT NULL`, `ContradictionList VARCHAR(500) NULL`
     - `ReplayDetected BIT NULL`, `ReplayMatchFingerprint VARCHAR(200) NULL`
     - `DeadInternetIndex INT NULL`
   - Update `ETL.usp_ParseNewHits` for Tier 3 params

7. **Unit tests** — adversarial test cases:
   - VPN user: French fonts + Vietnamese lang + US IP → low cultural score
   - Old GPU + new OS + datacenter IP → device age anomaly
   - Mobile UA + 4K screen + mouse moves → contradiction fires
   - Replayed mouse path from different fingerprint → replay detected
   - Known impossible combinations from design doc cross-signal tables

### Verification
- Each detection fires correctly for adversarial test cases
- Each detection does NOT fire for legitimate edge cases (real user with unusual setup)
- Contradiction matrix covers all rules from design doc §5.5
- Cultural consistency score is reasonable for known geo+culture combinations

---

## Phase 7 — SQL: CLR Assembly + Advanced Infrastructure

**Scope:** Create self-contained CLR database, build and deploy 5 CLR functions, add vector and graph table infrastructure.

**Effort:** Medium-Large. Isolated from application code.

**Design doc reference:** §8.3 items 3, 4, 6, 7, 8, plus vector (item 2) and graph (item 10)

### Steps

1. **Create CLR database** — migration script `SmartPiXL/SQL/45_CLR_Database.sql`:
   ```
   CREATE DATABASE SmartPiXL_CLR;
   -- Add CLR filegroup
   ALTER DATABASE SmartPiXL_CLR ADD FILEGROUP CLR_FG;
   ALTER DATABASE SmartPiXL_CLR ADD FILE (...) TO FILEGROUP CLR_FG;
   ```
   - Enable CLR at instance level: `EXEC sp_configure 'clr enabled', 1; RECONFIGURE;`
   - Certificate-based assembly signing (NOT `TRUSTWORTHY` — stricter security):
     - Create certificate in `master`
     - Create login from certificate with `UNSAFE ASSEMBLY` permission
     - Create user from certificate in `SmartPiXL_CLR`
   - Create synonyms or cross-database wrapper functions in `SmartPiXL` for access

2. **Build CLR assembly** — new project `SmartPiXL.SqlClr/`:
   - `SmartPiXL.SqlClr.csproj` — target `net10.0` (validate SQL 2025 compatibility; fallback to `net9.0` or `net8.0` if .NET 10 assemblies aren't loadable)
   - **This project CANNOT reference SmartPiXL.Shared** (CLR assemblies must be self-contained)
   - No NuGet dependencies (CLR assemblies should be minimal)

3. **CLR functions** in `SmartPiXL.SqlClr/Functions/`:

   **a. `GetSubnet24.cs`**:
   ```csharp
   [SqlFunction(IsDeterministic = true, IsPrecise = true)]
   public static SqlString GetSubnet24(SqlString ipAddress) { ... }
   ```
   - Input: `'192.168.1.100'` → Output: `'192.168.1.0/24'`
   - Span-based parsing, zero allocation

   **b. `RegexFunctions.cs`**:
   ```csharp
   [SqlFunction(IsDeterministic = true)]
   public static SqlString RegexExtract(SqlString input, SqlString pattern, SqlInt32 group) { ... }
   [SqlFunction(IsDeterministic = true)]
   public static SqlBoolean RegexMatch(SqlString input, SqlString pattern) { ... }
   ```
   - Compiled regex with caching for repeated patterns
   - Unlocks: domain extraction from URLs, email validation, bot signal parsing, URL path pattern matching

   **c. `FeatureBitmap.cs`**:
   ```csharp
   [SqlFunction(IsDeterministic = true, IsPrecise = true)]
   public static SqlInt32 FeatureBitmap(SqlBoolean ls, SqlBoolean ss, ...) { ... }
   ```
   - 17 boolean inputs → single 32-bit integer
   - Also: `AccessibilityBitmap` (9 bits), `BotBitmap` (top 20 signals), `EvasionBitmap` (8+ bits)

   **d. `MurmurHash3.cs`**:
   ```csharp
   [SqlFunction(IsDeterministic = true, IsPrecise = true)]
   public static SqlBinary MurmurHash3(SqlString input) { ... }
   ```
   - Non-crypto hash: ~200ns vs ~2μs for SHA2
   - Use for DeviceHash, fingerprint bucketing, consistent partitioning

   **e. `FuzzyMatch.cs`** (JaroWinkler + Levenshtein):
   ```csharp
   [SqlFunction(IsDeterministic = true)]
   public static SqlDouble JaroWinklerDistance(SqlString s1, SqlString s2) { ... }
   ```
   - Fallback fuzzy matching if vector UA similarity doesn't cover all cases
   - Build now, may not use if vectors work well (design doc: "test vectors first")

4. **Deploy assembly** to `SmartPiXL_CLR`:
   - `CREATE ASSEMBLY SmartPiXL_ClrFunctions FROM '{path}' WITH PERMISSION_SET = SAFE;`
   - Create T-SQL wrapper functions in `SmartPiXL_CLR.dbo`
   - Create synonyms in `SmartPiXL.dbo` pointing to CLR functions:
     ```sql
     CREATE SYNONYM dbo.GetSubnet24 FOR SmartPiXL_CLR.dbo.GetSubnet24;
     ```

5. **Vector infrastructure** — migration script `SmartPiXL/SQL/46_VectorInfrastructure.sql`:
   - `ALTER TABLE PiXL.Device ADD FingerprintVector VECTOR(64) NULL`
   - ETL or Forge logic to encode device characteristics as a 64-dimensional vector (screen dims, cores, memory, color depth, feature bitmap, etc. — each normalized to 0-1 range)
   - Test query: `SELECT VECTOR_DISTANCE('cosine', d1.FingerprintVector, d2.FingerprintVector)` for two known similar devices
   - **Note:** UA drift vector (`UaVector VECTOR(32)`) to be specified before work begins per owner directive

6. **Graph table infrastructure** — migration script `SmartPiXL/SQL/47_GraphTables.sql`:
   - Node tables: `Graph.Device AS NODE`, `Graph.Person AS NODE`, `Graph.IpAddress AS NODE`
   - Edge tables: `Graph.UsesIP AS EDGE` (Device→IP), `Graph.ResolvesTo AS EDGE` (Device→Person)
   - Population: ETL step or trigger from `PiXL.Visit` inserts
   - Test query: multi-hop `MATCH` traversal — Person → Devices → IPs → other Devices → other People

### .NET 10 CLR Validation Step — COMPLETED
- **Result (2026-02-20):** .NET 10 assemblies CANNOT be loaded. SQL Server 2025 RTM-GDR CLR host is `.NET Framework v4.0.30319`. .NET 10 assembly rejected: `references assembly 'system.runtime, version=10.0.0.0'`. Dropped directly to `net48` (Framework 4.x is the only compatible target). Documented in design doc and IMPLEMENTATION-LOG.
- Assembly requires `PERMISSION_SET = UNSAFE` for `Regex` + `ConcurrentDictionary`. Security via certificate-based signing in master.
- All 10 CLR functions deployed and verified via cross-database synonyms.

### Verification
- All CLR functions callable from `SmartPiXL` via synonyms
- `SELECT dbo.GetSubnet24('192.168.1.100')` → `'192.168.1.0/24'`
- `SELECT dbo.RegexExtract('https://example.com/path', '://([^/]+)', 1)` → `'example.com'`
- `SELECT dbo.FeatureBitmap(1,1,1,1,1,1,1,1,1,1,0,0,1,1,1,1,1)` → expected bitmap integer
- Vector distance query returns reasonable similarity scores for test data
- Graph `MATCH` traversal returns expected identity chains

---

## Phase 8 — SQL: Analysis Features + Schema Expansion

**Scope:** Build all 13 SQL analysis features from design doc §8.3. Pure T-SQL views, tables, and procs. Geo.Zipcode polygon table from Census ZCTA shapefiles.

**Effort:** Medium. Pure SQL work, no application code changes.

**Design doc reference:** §8.3 priority list (items 1-13), §8.4 (Zipcode polygons)

### Steps (in design doc priority order)

1. **Subnet reputation** — script `48_SubnetReputation.sql`:
   - Create `PiXL.SubnetReputation` table: Subnet24 VARCHAR(18), UniqueIPs INT, UniqueDevices INT, TotalHits INT, AvgBotScore DECIMAL(5,2), BotPercent DECIMAL(5,2), LastUpdated DATETIME2
   - Create `ETL.usp_UpdateSubnetReputation` — daily aggregation proc using `dbo.GetSubnet24()` CLR function
   - Index on Subnet24 for Forge lookups

2. **Session reconstruction views** — script `49_SessionViews.sql`:
   - `dbo.vw_Dash_Sessions` — stitch PiXL.Visit records into sessions (same DeviceId, gap < 30 min, window functions)
   - Pages per session, duration, bounce rate, entry/exit pages
   - Complements Forge real-time sessions (Phase 5) — SQL sees complete sessions after the fact

3. **Impossible travel detection** — script `50_ImpossibleTravel.sql`:
   - `dbo.vw_Dash_ImpossibleTravel` — same DeviceHash, two different GeoCountries within configurable time window
   - Window functions on `PiXL.Visit` joined to `PiXL.IP`
   - Uses integer bucket pattern for geo comparison where applicable (design doc §8.3 item 14 — coarse filter before precise geo)

4. **Dead Internet Index** — script `51_DeadInternetIndex.sql`:
   - `dbo.vw_Dash_DeadInternet` — per customer per week: definite-bot %, zero-mouse %, likely-human %
   - Trend over time for visualization

5. **Customer traffic quality** — script `52_CustomerQuality.sql`:
   - `dbo.vw_Dash_CustomerQuality` — per company per month: total hits, avg bot score, bot %, unique visitors, lead quality distribution

6. **Device lifecycle** — script `53_DeviceLifecycle.sql`:
   - `dbo.vw_Dash_DeviceLifecycle` — return frequency (median days between visits), customer hop pattern, fingerprint drift, dormancy detection (60+ day gap)

7. **Cross-customer intelligence (historical)** — script `54_CrossCustomerHistorical.sql`:
   - `dbo.vw_Dash_CrossCustomer` — devices hitting 5+ different companies historically

8. **Geo.Zipcode table** — script `55_GeoZipcode.sql`:
   - Create `Geo` schema
   - Create `Geo.Zipcode` table with: Zipcode CHAR(5), State CHAR(2), City, CentroidLat/Lon, LatBucket100/LonBucket100 (computed, persisted), Boundary GEOGRAPHY, AreaSqMi, Population
   - Integer bucket index for fast coarse matching
   - Spatial index on Boundary for `STContains`/`STIntersects`
   - Import script for Census ZCTA shapefile data (separate step — requires shapefile download)

9. **Expand PiXL.Parsed** — script `56_ParsedColumnExpansion.sql`:
   - Add any remaining Tier 1/2/3 columns not already added in migrations 42-44
   - Add computed bitmap columns using CLR functions:
     - `FeatureBitmapValue AS dbo.FeatureBitmap(LocalStorage, SessionStorage, IndexedDB, ...)` PERSISTED
     - `AccessibilityBitmapValue AS dbo.AccessibilityBitmap(DarkMode, LightMode, ReducedMotion, ...)` PERSISTED
   - Final column count: 300+ (design doc: "collect everything, PiXL.Parsed is a research table")

10. **Update dimensional model** — script `57_DimensionExpansion.sql`:
    - Add enrichment-derived columns to `PiXL.Device` (affluence signal, GPU tier, device age)
    - Add enrichment-derived columns to `PiXL.IP` (MaxMind geo, reverse DNS, subnet reputation FK)
    - Update `PiXL.Visit` with Forge enrichment fields

### Verification
- Each view returns results against existing data (or planted test data)
- Subnet reputation table populates via `ETL.usp_UpdateSubnetReputation`
- Impossible travel query catches test data with contradictory geo+time
- All migration scripts run clean on `SmartPiXL` database
- ETL processes new columns without error
- Bitmap computed columns calculate correctly

---

## Phase 9 — TrafficAlert Subsystem

**Scope:** Combine all enrichment outputs into a unified traffic quality scoring and reporting system. SQL schema + materialization + API-ready structure.

**Effort:** Medium. Builds on all prior phases.

**Design doc reference:** §7

### Steps

1. **SQL schema** — script `58_TrafficAlertSchema.sql`:
   - Create `TrafficAlert` schema
   - `TrafficAlert.VisitorScore` — per-visitor per-visit composite scores:
     - `VisitId BIGINT FK → PiXL.Visit`, `DeviceId BIGINT FK → PiXL.Device`
     - `BotScore INT`, `AnomalyScore INT`, `CombinedThreatScore INT` (from PiXL script)
     - `LeadQualityScore INT`, `AffluenceSignal VARCHAR(4)`, `CulturalConsistency INT` (from the Forge)
     - `ContradictionCount INT`, `SessionQuality INT`, `MouseAuthenticity INT` (derived)
     - `CompositeQuality INT` — single 0-100 score combining all signals
   - `TrafficAlert.CustomerSummary` — per-customer per-period aggregates:
     - `CompanyID VARCHAR(50)`, `PeriodStart DATE`, `PeriodType CHAR(1)` (D/W/M)
     - `TotalHits INT`, `BotHits INT`, `HumanHits INT`, `UnknownHits INT`
     - `BotPercent DECIMAL(5,2)`, `AvgLeadQuality DECIMAL(5,2)`
     - `UniqueVisitors INT`, `CrossCustomerPollutionRate DECIMAL(5,2)`
     - `AvgSessionDepth DECIMAL(5,2)`, `AvgSessionDuration INT`
     - `DeadInternetIndex INT`

2. **Materialization** — script `59_TrafficAlertMaterialization.sql`:
   - `ETL.usp_MaterializeVisitorScores` — called after `usp_ParseNewHits`, populates `TrafficAlert.VisitorScore` from `PiXL.Parsed` + `PiXL.Visit`
   - `ETL.usp_MaterializeCustomerSummary` — daily/weekly/monthly rollup into `TrafficAlert.CustomerSummary`
   - Wire into `EtlBackgroundService` as additional ETL steps

3. **Mouse Authenticity scoring** (derived metric):
   - Combines: mouse entropy, timing CV, speed CV, move count, replay detection, scroll contradiction
   - Score 0-100: 100 = clearly human, 0 = clearly automated
   - Computed in the materialization proc

4. **Session Quality scoring** (derived metric):
   - Combines: session page count, session duration, navigation pattern (linear vs random)
   - Score 0-100: multi-page session with increasing engagement = high quality
   - Computed in the materialization proc

5. **Dashboard views** (ready for Phase 10's Sentinel service):
   - `dbo.vw_TrafficAlert_VisitorDetail` — single-visitor full scoring breakdown
   - `dbo.vw_TrafficAlert_CustomerOverview` — customer summary with trend
   - `dbo.vw_TrafficAlert_Trend` — time-series of customer metrics

### Verification
- `TrafficAlert.VisitorScore` populates correctly from enriched PiXL.Parsed data
- `TrafficAlert.CustomerSummary` shows meaningful aggregates across test data
- Composite quality score produces reasonable values (high for clean visitors, low for bots)
- Mouse authenticity and session quality derived scores align with expected outcomes

---

## Phase 10 — Sentinel Service Separation (Deferred)

**Scope:** Extract Tron (ops + metrics) and Atlas into their own Windows Service. Rebuild dashboards to display all new enrichment and TrafficAlert data. Delete deprecated Worker project.

**Effort:** Medium-Large. Deferred until Phases 1-9 are complete.

**This phase is not detailed here** because the dashboard rebuild depends on:
- What the final enrichment data looks like from Phases 4-6
- Which TrafficAlert metrics are most useful (Phase 9)
- How the data flows through the system in practice
- Owner review of what the dashboards should show now that the underlying data model has expanded from ~175 to 300+ columns

When this phase begins:
1. Create `SmartPiXL.Sentinel/` project — Windows Service, Kestrel on port 7500
2. Rebuild Tron + Atlas with new enrichment-aware panels
3. Add TrafficAlert API endpoints (`/api/traffic-alert/*`)
4. Move dashboard views and endpoints from Worker reference
5. Delete SmartPiXL.Worker-Deprecated project from solution
6. Register as Windows Service: `SmartPiXL-Sentinel`

---

## Migration Script Summary

| # | Script | Phase | Purpose |
|---|--------|-------|---------|
| 41 | `41_ScreenExtendedMousePath.sql` | 1 | 2 new PiXL.Parsed columns + ETL update |
| 42 | `42_ForgeTier1Columns.sql` | 4 | ~18 Tier 1 enrichment columns + ETL update |
| 43 | `43_ForgeTier2Columns.sql` | 5 | ~8 Tier 2 enrichment columns + ETL update |
| 44 | `44_ForgeTier3Columns.sql` | 6 | ~8 Tier 3 enrichment columns + ETL update |
| 45 | `45_CLR_Database.sql` | 7 | SmartPiXL_CLR database + CLR enable + assembly |
| 46 | `46_VectorInfrastructure.sql` | 7 | FingerprintVector VECTOR(64) on PiXL.Device |
| 47 | `47_GraphTables.sql` | 7 | Graph schema + node/edge tables |
| 48 | `48_SubnetReputation.sql` | 8 | PiXL.SubnetReputation + daily aggregation |
| 49 | `49_SessionViews.sql` | 8 | Session reconstruction dashboard view |
| 50 | `50_ImpossibleTravel.sql` | 8 | Impossible travel detection view |
| 51 | `51_DeadInternetIndex.sql` | 8 | Dead Internet Index view |
| 52 | `52_CustomerQuality.sql` | 8 | Customer traffic quality trending view |
| 53 | `53_DeviceLifecycle.sql` | 8 | Device lifecycle analysis view |
| 54 | `54_CrossCustomerHistorical.sql` | 8 | Cross-customer intelligence view |
| 55 | `55_GeoZipcode.sql` | 8 | Geo.Zipcode polygon table + spatial index |
| 56 | `56_ParsedColumnExpansion.sql` | 8 | Bitmap computed columns + remaining columns |
| 57 | `57_DimensionExpansion.sql` | 8 | Device/IP/Visit enrichment columns |
| 58 | `58_TrafficAlertSchema.sql` | 9 | TrafficAlert.VisitorScore + CustomerSummary |
| 59 | `59_TrafficAlertMaterialization.sql` | 9 | Materialization procs + ETL wiring |

---

## Agent Assignment Guide

Each phase maps to a clear agent skill profile:

| Phase | Primary Skills Needed | Key Files to Read First |
|-------|----------------------|------------------------|
| 1 | JavaScript (browser APIs), SQL (ALTER TABLE, ETL proc), C# (Razor template) | `PiXLScript.cs`, 39_FixParseNewHitsBigint.sql |
| 2 | C# (.NET 10 Windows Service, named pipes, Channel\<T\>, BackgroundService) | All Worker `Services/*.cs` (reference), `TrackingData.cs`, `DatabaseWriterService.cs` |
| 3 | C# (named pipe client, fire-and-forget patterns, JSONL serialization) | `TrackingEndpoints.cs`, `DatabaseWriterService.cs`, Phase 2 pipe server code |
| 4 | C# (NuGet integration, async HTTP clients, DNS, file-based databases) | Design doc §5.3, §6.1. MaxMind/IPAPI/DNS library docs |
| 5 | C# (ConcurrentDictionary, sliding windows, in-memory state, scoring algorithms) | Design doc §5.4, Phase 4 enrichment pattern |
| 6 | C# (rule engines, statistical scoring, hash-based detection, MathNet.Numerics) | Design doc §5.5, Phase 5 enrichment pattern |
| 7 | SQL (CLR assembly creation, certificate signing, VECTOR type, graph tables), C# (SQL CLR functions) | Design doc §8.3, SQL 2025 CLR docs |
| 8 | SQL (views, window functions, spatial indexes, GEOGRAPHY type, integer bucket pattern) | Design doc §8.3-8.4, existing `vw_Dash_*` views |
| 9 | SQL (materialization procs, scoring algorithms in T-SQL) + C# (ETL wiring) | Design doc §7, Phase 8 views |

---

## What's NOT Changing (from design doc §12)

- `PiXL.Raw` schema (9 columns) — all enrichment rides in the QueryString as `_srv_*` params
- ETL pipeline backbone (`usp_ParseNewHits` / `usp_MatchVisits` / `usp_EnrichParsedGeo`)
- IIS in-process hosting model
- Fire-and-forget GIF response — visitor never waits for enrichment
- IPAPI Pro subscription — stays as a data source
- IIS Fast Pass 12-step enrichment (Phase 3 only changes what happens AFTER enrichment — pipe instead of direct SQL)

---

## Items Deferred / TBD (Owner Will Clarify Before Work Begins)

| Item | Status | Blocks Phase |
|------|--------|-------------|
| UA drift vector column spec (VECTOR(32) or other) | Owner will specify | 7 |
| Full-Text Indexing evaluation for fuzzy UA matching | Owner will specify | 7 |
| Census ZCTA shapefile download + import process | Research task | 8 |
| ML.NET / Accord.NET bot probability model | 6+ months out (design doc §5.6) | None |
| LLM/Ollama integration | 6+ months out, hardware-constrained | None |