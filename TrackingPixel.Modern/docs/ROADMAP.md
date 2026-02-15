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

### Phase 7: IP Geolocation
- [x] `GeoCacheService` — two-tier (ConcurrentDictionary + MemoryCache) non-blocking geo lookup against 342M-row `IPAPI.IP` table
- [x] `IpApiSyncService` — daily watermark-based incremental sync from Xavier (`192.168.88.35`) `IPGEO.dbo.ip_location_new` → local `IPAPI.IP`
- [x] Geo enrichment integrated into `CaptureAndEnqueue` pipeline (before SQL insert)
- [x] Geo-timezone mismatch bot signal (`_srv_tzMismatch`)
- [x] `ETL.usp_EnrichParsedGeo` — backfills geo columns on `PiXL.Parsed` from `IPAPI.IP`
- [x] `IPAPI` schema, `IPAPI.IP` table, `IPAPI.SyncLog` audit table (SQL/25, SQL/26)

---

## In Progress

### Phase 8: Legacy PiXL Support ⬅ P0
**Priority:** P0 — Active development

SmartPiXL is an upgrade to the existing Xavier tracking platform. Legacy pixels deployed to clients must continue to work. See `docs/LEGACY_SUPPORT.md` for full design.

| Task | Effort | Status |
|------|--------|--------|
| Remove `queryString.Length > 10` gate — accept all `_SMART.GIF` hits | 1 h | Not started |
| Add `_SMART.js` route — serves PiXLScript from same URL pattern as legacy | 1 h | Not started |
| Hit-type detection (`_srv_hitType=legacy\|modern`) in `CaptureAndEnqueue` | 1 h | Not started |
| Populate `Referer` from `?ref=` param for legacy `<script>` style hits | 30 min | Not started |
| Add `HitType` column to `PiXL.Parsed` and `PiXL.Visit` | 1 h | Not started |
| Update `ETL.usp_ParseNewHits` to populate `HitType` | 1 h | Not started |
| Update `vw_Dash_*` views for legacy vs modern hit reporting | 2 h | Not started |
| Xavier traffic forwarding documentation | 1 h | Not started |

### Phase 9: Company/Pixel Sync from Xavier ⬅ P0
**Priority:** P0 — Active development

Xavier remains the system of record for Company/Pixel configuration. Changes must sync to SmartPiXL.

| Task | Effort | Status |
|------|--------|--------|
| `CompanyPixelSyncService` — `BackgroundService` syncing from `Xavier.SmartPixl.dbo.Company` and `dbo.PiXL` | 3–4 h | Not started |
| `ETL.CompanyPixelSyncLog` table for audit trail | 30 min | Not started |
| Config additions to `TrackingSettings` (`CompanyPixelSyncIntervalMinutes`) | 30 min | Not started |
| Register service in `Program.cs` | 15 min | Not started |
| Verify sync preserves local-only columns (`ClientParams`, `Notes`, `SysStartTime`, `SysEndTime`) | 1 h | Not started |

---

## Backlog

### Legacy Match Process
**Priority:** P1 — After legacy ingestion is live

| Task | Effort | Status |
|------|--------|--------|
| Design legacy match strategy (IP + UA + timestamp window against AutoConsumer) | Design | Not started |
| `ETL.usp_MatchLegacyVisits` stored procedure | 3–4 h | Not started |
| Wire legacy match into `EtlBackgroundService` loop | 1 h | Not started |
| Dashboard comparison: legacy match yield vs modern match yield per company | 2 h | Not started |

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

### White-Label Support
**Priority:** P3 — Future

| Task | Effort | Status |
|------|--------|--------|
| Custom domain routing (e.g., `dashboard-datatracker.com`) for white-label clients | Design | Not started |
| Branded pixel URLs and dashboard themes | Design | Not started |

---

## Notes

- **IP-API compatibility:** The geo cache approach maintains compatibility with the existing IP-API batch process. New IPs not in cache still queue for IP-API resolution.
- **IPv6 readiness:** `IpClassificationService` already handles IPv4, IPv6, and IPv4-mapped IPv6 addresses.
- **RESERVED_IP_RANGES.md** is the reference for all classified IP ranges.
- **Legacy pixel format:** `<img src=".../{companyId}/{pixlId}_SMART.GIF">` (server-side data only). Modern pixel: `<script src=".../{companyId}/{pixlId}_SMART.js">` (90+ JS data points). One-tag upgrade path.
- **Xavier is the system of record** for Company and Pixel configuration. `CompanyPixelSyncService` mirrors changes every 15 minutes. SmartPiXL adds local-only columns (`ClientParams`, temporal columns) that are not overwritten by sync.
- **Legacy traffic forwarding:** Xavier's `Default.aspx.cs` can forward live production hits to SmartPiXL (`192.168.88.176`) via a fire-and-forget HTTP request on the same LAN. See `docs/LEGACY_SUPPORT.md`.
