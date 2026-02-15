# SmartPiXL — Roadmap

Last Updated: February 15, 2026

---

## Completed

### Phase 1: Core Server
- [x] .NET 10 Minimal API server (`Program.cs`)
- [x] Fingerprinting script — 100+ data points via `<script>` tag
- [x] `DatabaseWriterService` — `Channel<T>` → `SqlBulkCopy` into `PiXL.Test`
- [x] `TrackingCaptureService` — HTTP request → `TrackingData` parser
- [x] `FileTrackingLogger` — async daily rolling log files
- [x] 1,000+ test records generated via Playwright

### Phase 2: Code Quality
- [x] StringBuilder for string loops, using declarations, local functions
- [x] Removed dead code and duplicate config
- [x] Naming consistency (CompanyID/PiXLID)
- [x] Dev ports standardized to 7000/7001, production 6000/6001
- [x] Compiled regex, stack allocation, zero-allocation patterns

### Phase 3: IP & Fingerprint Enrichment
- [x] `IpClassificationService` — datacenter / residential / reserved IP classification
- [x] `DatacenterIpService` — AWS/GCP IP range downloader (background refresh)
- [x] `IpBehaviorService` — subnet /24 velocity & rapid-fire timing detection
- [x] `FingerprintStabilityService` — per-IP fingerprint variation scoring
- [x] `IpClassification` model and enum
- [x] Server-side enrichment pipeline wired into `TrackingEndpoints.CaptureAndEnqueue`

### Phase 4: Database Schema & ETL
- [x] `PiXL` and `ETL` schemas created (SQL/17B)
- [x] Normalized star schema: `PiXL.Device`, `PiXL.IP`, `PiXL.Visit`, `PiXL.Match` (SQL/19)
- [x] `PiXL.Parsed` materialized warehouse table (~175 columns, replaces the old view)
- [x] `ETL.usp_ParseNewHits` — parses raw `PiXL.Test` rows into `PiXL.Parsed`
- [x] `ETL.usp_MatchVisits` — identity resolution against `AutoConsumer` (421M rows)
- [x] `EtlBackgroundService` — runs both ETL procs every 60 seconds
- [x] `AutoConsumer` email index (SQL/22) — `IX_AutoConsumer_EMail`
- [x] `dbo.vw_Dash_PipelineHealth` view (SQL/24)
- [x] Evasion countermeasure columns (SQL/11)
- [x] IP behavior signal columns (SQL/17)
- [x] NVARCHAR → VARCHAR migration for non-Unicode columns (SQL/18)
- [x] Backfill Visit/Device/IP dimension tables (SQL/21)

### Phase 5: Dashboard
- [x] Tron 3D WebGL dashboard (`wwwroot/tron.html`, Three.js)
- [x] `DashboardEndpoints` — 11 JSON endpoints under `/api/dash/*`
- [x] 10 SQL views (`vw_Dash_*`) powering all dashboard panels
- [x] `InfraHealthService` — probes Windows services, SQL health, IIS, data flow
- [x] Pipeline health panel (row counts, watermarks, lag, freshness)
- [x] Localhost-only access control (`RequireLoopback`)

### Phase 6: Evasion Countermeasures
- [x] 10 vulnerability countermeasures documented and implemented
- [x] Canvas noise detection (multi-canvas cross-validation)
- [x] Audio fingerprint noise detection
- [x] Behavioral analysis (mouse entropy, timing patterns)
- [x] Stealth plugin detection
- [x] Anti-detect browser detection (fingerprint stability)
- [x] Datacenter IP detection
- [x] Font spoofing hardening

---

## Backlog

### IP Geolocation Integration
**Priority:** P1 — Next major feature

| Task | Effort | Status |
|------|--------|--------|
| Geo cache lookup service (`GeoCacheService`) against existing 342M-row IP geo table | 3–4 h | Not started |
| Expand `TrackingData` with geo fields (Country, Region, City, Lat/Lon, Timezone, ISP) | 1 h | Not started |
| Integrate geo lookup into enrichment pipeline (before SQL insert) | 2–3 h | Not started |
| Geo-timezone mismatch bot signal | 2 h | Not started |
| Queue unresolved IPs for IP-API batch | 2–3 h | Not started |

### Bot Detection Enhancement
**Priority:** P2

| Task | Effort | Status |
|------|--------|--------|
| Known bot user-agent detection (compiled regex patterns for Googlebot, Bingbot, headless browsers, etc.) | 2–3 h | Not started |
| `BotDetectionService` consolidating all bot signals into a single score | 3–4 h | Not started |

### SQL Server 2025 JSON Enhancements
**Priority:** P2

| Task | Effort | Status |
|------|--------|--------|
| Migrate `PiXL.Test.HeadersJson` from `NVARCHAR(MAX)` to native `json` type | 1 h | Not started |
| Add more `JSON INDEX` paths on `PiXL.Visit.ClientParamsJson` as query patterns emerge | As needed | Not started |

### Mobile Ad ID Integration
**Priority:** P3 — Future

| Task | Effort | Status |
|------|--------|--------|
| Design cross-system join (SmartPiXL visitors ↔ mobile ad IDs via shared IP + time window) | Design | Not started |

### TLS Fingerprinting
**Priority:** P3 — Future

| Task | Effort | Status |
|------|--------|--------|
| JA3/JA4 fingerprinting via reverse proxy (requires Nginx or HAProxy in front of IIS) | Design | Not started |

---

## Notes

- **IP-API compatibility:** The planned geo cache approach maintains compatibility with the existing IP-API batch process. New IPs not in cache will still queue for IP-API resolution.
- **IPv6 readiness:** `IpClassificationService` already handles IPv4, IPv6, and IPv4-mapped IPv6 addresses.
- **RESERVED_IP_RANGES.md** is the reference for all classified IP ranges.
