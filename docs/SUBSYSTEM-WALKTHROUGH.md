# SmartPiXL Subsystem Walkthrough

**Purpose:** Go through every subsystem, understand what it does, why it exists, and whether it has drift. No code gets written until decisions are made.

**Rules:**
1. One subsystem at a time.
2. For each file: what it does, why it exists, who asked for it, dependencies, honest assessment.
3. Owner decides: **keep**, **simplify**, **remove**, or **think about it**.
4. No building. No fixing. Just inventory and understanding.

---

## Subsystem Index

| # | Subsystem | Status | Decision |
|---|-----------|--------|----------|
| 1 | **Edge HTTP capture** (SmartPiXL project) | **COMPLETE** | Capture-only, enrichment moved to Forge |
| 2 | PiXL Script (browser JavaScript) | **COMPLETE** | 8 implementation batches, NUglify, all tests pass |
| 3 | Named pipe transport + failover | **COMPLETE** | Zero-data-loss failover chain, dead-letter, .processed retention |
| 4 | **Forge pipeline** (sub-subsystem walkthrough) | **IN PROGRESS** | Broken into F1–F9 sub-subsystems below |
| 5 | SQL schema (tables, indexes, relationships) | not started | — |
| 6 | ETL stored procedures | not started | — |
| 7 | Enrichment services (×15) | not started | — |
| 8 | Background IP enrichment (Lane 3) | not started | — |
| 9 | IPAPI/Xavier sync | not started | — |
| 10 | Sentinel (dashboards, Atlas, TrafficAlert) | not started | — |
| 11 | Failover chain (Edge JSONL, Forge JSONL, dead letter, replay) | not started | — |
| 12 | Synthetic data generator | not started | — |
| 13 | Shared library (SmartPiXL.Shared) | not started | — |
| 14 | SQL CLR functions | not started | — |
| 15 | Test suite | not started | — |

---

## Design Decisions Log

Decisions made during walkthroughs that affect architecture.

| # | Decision | Context | Date |
|---|----------|---------|------|
| D1 | Merge PiXL.Raw into PiXL.Parsed — single table with QueryString + HeadersJson preserved | Raw exists only because legacy Xavier wrote raw data to SQL before parsing. Forge parses in .NET before writing, so the staging table adds complexity with no benefit. | 2026-03-17 |
| D2 | Use SQL Server 2025 native `json` type for HeadersJson | Binary JSON format, faster JSON_VALUE/JSON_QUERY, validates on insert. Headers are small (~500 bytes), rarely queried. | 2026-03-17 |
| D3 | Replace DJB2 hash with SHA-256 (SubtleCrypto) for heavy FPs, raw data for short strings | DJB2 is 32-bit, collisions at ~65K inputs. Canvas/audio/WebGL render data too large to send raw → SHA-256 (256-bit). WebGL params, fonts, etc. sent raw. | 2026-03-17 |
| D4 | Add noise detection to ALL fingerprint fields, not just canvas/audio | If noise is detected, flag the record and mark that specific field as unreliable for identity resolution. Noise detection = anti-FP tool detected. | 2026-03-17 |
| D5 | Study FingerprintJS techniques, implement independently | Don't use as library (ad-blocked by name), don't copy code (signatured). Study what/why, implement our own way with different structure. | 2026-03-17 |
| D6 | Minify PiXL Script via NUglify | NUglify 1.21.17 minifies at startup, cached. Reduces size ~15-20%. Full obfuscation deferred (variable names are already short). | 2026-03-17 |
| D7 | Client-side DeviceHash computation | `hashStr()` of canvasFP+fonts+gpu+webglFP+audioHash. Forge already reads `deviceHash` param for session stitching. ETL recomputes SHA-256 server-side for PiXL.Device. | 2026-03-17 |
| D8 | Feature/Permissions Policy detection | `document.permissionsPolicy.allowedFeatures()` — fingerprint signal + explains when APIs are unavailable. | 2026-03-17 |
| D9 | Never silently drop data — all overflow goes to failover | PipeClientService channel overflow → JSONL failover (was: DropOldest silently lost data). JsonlFailoverService gets emergency sync write as last resort. | 2026-03-19 |
| D10 | Dead-letter malformed lines instead of skipping | All JSON parse failures in FailoverCatchup, PipeListener, and ForgeFailoverWriter write raw line to `dead_letter_*.jsonl`. Source data is never lost. | 2026-03-19 |
| D11 | Rename .processed instead of deleting failover files | Both Edge and Forge failover files are renamed to `.processed` after replay. Deleted after 7-day retention. Protects against in-flight channel/SQL crashes. | 2026-03-19 |
| D12 | Single absolute failover directory | Edge + Forge both use `C:\inetpub\Smartpixl.info\Failover` as absolute path. Eliminates relative-path mismatch between dev/prod base directories. | 2026-03-19 |
| D13 | Channel capacity 50K (was 10K) | Owner: "10k is too small a window." All channels in Edge + Forge increased to 50K. | 2026-03-19 |
| D14 | Merge PiXL.Raw into single table — eliminate two-step SQL load | PiXL.Raw (9 cols) → ParsedBulkInsert → PiXL.Parsed (229 cols) is two SQL loads for no benefit. Forge already parses in .NET. Merged table keeps QueryString + HeadersJson + 229 parsed columns. Eliminates ParsedBulkInsertService, ETL.Watermark for parse, and Raw↔Parsed JOINs. Raw QS preserved for re-parse capability. | 2026-03-19 |
| D15 | Enrichment tiers are code convention, not SQL metadata | T1=stateless lookups, T2=stateful tracking, T3=multi-signal, T4=per-IP history, Final=scoring. Defined in EnrichmentPipelineService comments, not in SQL. A SQL tier/service registry table is overengineering — enrichment services don't query SQL for their own identity. Dashboard metrics belong in dedicated aggregation views, not a service catalog table. | 2026-03-19 |
| D16 | Enrichments are non-optional (constructor params required) | All 18 enrichment services + BackgroundIpEnrichmentService are required DI registrations. Null-check guards removed from EnrichRecord. EnableEnrichments toggle kept as master kill switch. | 2026-03-19 |
| D17 | Hybrid dedup eviction (time + count) | BackgroundIpEnrichmentService eviction: every 5min, remove >30min stale entries first, then cap at 500K keeping 250K most recent. Replaces nuclear `Clear()` that killed cache effectiveness during traffic swings. | 2026-03-19 |
| D18 | Dead-letter format unified to JSONL | All failover/dead-letter persistence uses JSONL (one JSON object per line). Legacy JSON array files handled as fallback in ForgeReplayService. | 2026-03-19 |

---

## 1. Edge HTTP Capture (SmartPiXL project)

**What it is:** The IIS-hosted ASP.NET Core app that receives browser hits and sends them to Forge.

### File Inventory

| File | Lines | Purpose | Origin | Assessment |
|------|-------|---------|--------|------------|
| `Program.cs` | 267 | Composition root, Kestrel config, middleware, DI registration | Core | See notes below |
| `appsettings.json` | 58 | Dev config: ports, connection string, BaseUrl, pipe name | Core | Has settings for services that don't run here (SMTP, purge hours) |
| `web.config` | 19 | IIS InProcess hosting config | Core | Gets overwritten by `dotnet publish` |
| `SmartPiXL.csproj` | — | Project file | Core | — |
| **Endpoints/** | | | | |
| `TrackingEndpoints.cs` | 559 | HTTP routes: JS serving, GIF/DATA capture, ClearDot, bot trap, CORS | Core | Contains 6 inline enrichment calls on hot path |
| `InternalEndpoints.cs` | 91 | /internal/health, /internal/circuit-reset, /internal/geo-cache/clear | Agent-built for Forge↔Edge probing | Clean, small |
| **Services/** | | | | |
| `TrackingCaptureService.cs` | 292 | Parse HTTP request → TrackingData (IP, CompanyID, headers JSON, QS) | Core | Clean after BUG-001 XFF fix |
| `PipeClientService.cs` | 338 | Named pipe client → Forge, Channel\<T\> queue, JSONL failover fallback | Core | Not yet read in detail |
| `JsonlFailoverService.cs` | 123 | JSONL daily-rolling file writer when pipe is down | Core | Not yet read |
| `DatabaseWriterService.cs` | **722** | Direct SQL BulkCopy writer | Agent-built | **DEAD CODE — not registered in DI** |
| `DatacenterIpService.cs` | 190 | Downloads AWS+GCP CIDR ranges, checks IPs | Agent-built | Edge enrichment |
| `CidrTrie.cs` | 166 | IP prefix trie for CIDR matching | Agent-built (supports DatacenterIpService) | Edge enrichment |
| `IpClassificationService.cs` | 520 | Classifies IPs: Public/Private/Loopback/CGNAT/LinkLocal | Agent-built | Edge enrichment |
| `IpBehaviorService.cs` | 310 | Subnet /24 velocity + rapid-fire timing detection | Agent-built | Edge enrichment |
| `FingerprintStabilityService.cs` | 208 | Per-IP fingerprint variation tracking (canvas/WebGL/audio) | Agent-built | Edge enrichment |
| `GeoCacheService.cs` | 402 | In-memory geo cache backed by IPAPI.IP SQL lookups | Agent-built | Edge enrichment, ties to IPAPI dependency |
| **Scripts/** | | | | |
| `PiXLScript.cs` | 1195 | C# template → browser JavaScript (~52KB served) | Core | Separate subsystem (#2) |

**Total Edge code: ~5,460 lines across 16 files. Of that, 722 lines (DatabaseWriterService) are dead code.**

### Walkthrough Notes — What I Found

#### The Core Flow (3 files, ~1,100 lines)

The essential data path is simple:

```
Browser HTTP request
  → TrackingCaptureService.CaptureFromRequest() [292 lines]
      Extracts: CompanyID (int), PiXLID (int), IP (from TCP socket), 
      UserAgent, Referer, QueryString, HeadersJson
      Returns: TrackingData record
  → CaptureAndEnqueue() in TrackingEndpoints.cs [~150 lines of the 559]
      Runs 6 enrichment services inline, appends _srv_* params to QueryString
      Returns: enriched TrackingData
  → PipeClientService.TryEnqueue() [338 lines]
      Sends JSON to Forge via named pipe
      Falls back to JsonlFailoverService if pipe is down
```

**That's the core.** Everything else is enrichment bolted onto the hot path.

#### The 6 Edge Enrichment Services (5 files, ~1,796 lines)

These all run inline in `CaptureAndEnqueue()` on every single hit:

| Service | Lines | What it does | Params it adds |
|---------|-------|-------------|----------------|
| FingerprintStabilityService | 208 | Tracks canvas/WebGL/audio FP variation per IP (24h window) | `_srv_fpAlert`, `_srv_fpObs`, `_srv_fpUniq`, `_srv_fpRate5m` |
| IpBehaviorService | 310 | Subnet /24 velocity + rapid-fire timing | `_srv_subnetIps`, `_srv_subnetHits`, `_srv_hitsIn15s`, `_srv_lastGapMs`, `_srv_subSecDupe`, `_srv_subnetAlert`, `_srv_rapidFire` |
| DatacenterIpService (+CidrTrie) | 190+166 | AWS/GCP CIDR range check | `_srv_dc=AWS` or `_srv_dc=GCP` |
| IpClassificationService | 520 | Public/Private/Loopback/CGNAT classification | `_srv_ipType={byte}` |
| GeoCacheService | 402 | In-memory geo from IPAPI.IP SQL lookup | `_srv_geoCC`, `_srv_geoReg`, `_srv_geoCity`, `_srv_geoTz`, `_srv_geoISP`, `_srv_geoProxy`, `_srv_geoMobile`, `_srv_geoTzMismatch` |

These services exist because they need to see **streaming per-IP state** (e.g., "this IP has sent 5 different canvas fingerprints in the last hour" or "this /24 subnet has 12 unique IPs hitting in 15 seconds"). That state tracking requires seeing every hit in real time, which the Edge does but the Forge also does.

**The question is:** Could the Forge do all of this instead? The Forge already sees every record and already has 15 enrichment services. The only argument for Edge-side is lower latency to the response — but the response is a 43-byte GIF or 204, delivered BEFORE enrichment completes (enrichment runs in CaptureAndEnqueue, but the response is returned immediately after).

Wait — actually, looking at the code again: `CaptureAndEnqueue` runs **synchronously** before returning the GIF. So enrichment DOES add latency to the HTTP response. Moving it to Forge would make Edge responses instant.

#### Dead Code: DatabaseWriterService (722 lines)

Program.cs comment: `"NOTE: DatabaseWriterService is no longer registered in the Edge. The Edge NEVER writes to SQL directly."`

File still exists. 722 lines of unused code.

#### Config Bloat

Edge's appsettings.json has settings for SMTP, purge schedules, index maintenance, sync intervals — none of which the Edge uses. These come from the shared `TrackingSettings` class.

#### Things That Look Clean

- **TrackingCaptureService** — Tight SIMD JSON builder, stateless, zero-alloc hot path, proper int.TryParse on CompanyID/PiXLID.
- **InternalEndpoints** — 91 lines, loopback-only, does what it says.
- **Security headers** — HSTS, CSP, X-Frame-Options, Permissions-Policy, Accept-CH for Client Hints.
- **sendBeacon POST** — Clean: reads body, rewrites QueryString, reuses CaptureAndEnqueue.

### Questions for Owner

1. **Edge enrichment services (6 services, ~1,796 lines):** Did you spec these, or did they grow from conversations? The Forge handles enrichment — should the Edge just capture and forward?

2. **GeoCacheService (402 lines):** Queries IPAPI.IP from the Edge. With MaxMind running in Forge and IPAPI's future uncertain, does this service still make sense?

3. **DatabaseWriterService (722 lines):** Dead code, not registered. Remove?

4. **Config bloat:** Should TrackingSettings be split so Edge only sees Edge-relevant settings?

### Owner Decisions (2026-03-17)

**Owner philosophy:** Edge should be as lightweight as possible. Capture and forward. No SQL. No enrichment. "Do its work so fast that no one has a clue it's there."

**Key insight:** All 6 enrichment services are correct in spirit and built to spec, but they belong in the Forge, not the Edge. "The Forge is called the forge because that's where we're taking raw data and forging it with enrichments."

| File | Decision | Status |
|------|----------|--------|
| Program.cs | **Simplify** — strip enrichment DI registrations | ✅ Done |
| appsettings.json | **Simplify** | Deferred (Edge settings split) |
| TrackingEndpoints.cs | **Simplify** — remove all enrichment from CaptureAndEnqueue | ✅ Done |
| InternalEndpoints.cs | **Keep** — removed geo-cache/clear endpoint | ✅ Done |
| TrackingCaptureService.cs | **Keep** — added 10 new headers to capture | ✅ Done |
| PipeClientService.cs | **Keep** | No change needed |
| JsonlFailoverService.cs | **Keep** | No change needed |
| DatabaseWriterService.cs | **DELETED** | ✅ Removed 2026-03-17 |
| DatacenterIpService.cs | **Moved to Forge** (Services/Enrichments/) | ✅ Done |
| CidrTrie.cs | **Moved to Forge** (Services/Enrichments/) | ✅ Done |
| IpClassificationService.cs | **Moved to Shared** (Services/) | ✅ Done |
| IpBehaviorService.cs | **Moved to Forge** (Services/Enrichments/) | ✅ Done |
| FingerprintStabilityService.cs | **Moved to Forge** (Services/Enrichments/) | ✅ Done |
| GeoCacheService.cs | **DELETED from Edge** | ✅ Done |
| DatabaseWriterServiceTests.cs | **DELETED** (orphaned test) | ✅ Done |

#### Owner comments (verbatim, for future reference)
- **Edge philosophy:** "as lightweight as possible. Do its work so fast that no one has a clue it's there."
- **Enrichment placement:** "THIS IS ALL FORGE WORK. The Forge is called the forge because that's where we're taking raw data and forging it with enrichments before loading it into SQL."
- **IpBehaviorService:** "I do like the idea that we can pre-compute some likely behavioral statistics" and "having this data in the forge allows for much bigger time windows." Concerns about SQL-backed approach vs in-memory: "if it's entirely sql-based, that's just sql at that point." Also: "what happens if the service goes down?"
- **FingerprintStabilityService:** "I also know this service is also built to be so fast that we lose nothing by keeping it."
- **GeoCacheService:** "I do not want this happening at all." Concept could work in Forge against MaxMind (not IPAPI.IP) for real-time traffic invalidation: "if the geocacheservice can help provide useful real-time uplift pre-insert, then the forge should do this work, but do it against maxmind, not IPAPI.IP."
- **IpClassificationService:** Owner "finds it beautiful" — zero cost to keep, useful as Forge pre-filter for MaxMind lookups.
- **Headers:** "My general style is to get everything I can and once I see how it looks with real data, I can assess entropy with real numbers rather than assumptions."
- **Code quality:** "The really nice thing about how I like my .NET code is that we can keep any of it and it's all incredibly performant so the cost to keep is basically zero. I'm just neurotic about having code that I don't need."

#### Implementation notes (2026-03-17)
- 10 new headers added: Accept, Accept-Encoding, Connection, Origin, X-Requested-With, Sec-CH-UA-Full-Version-List, Sec-CH-UA-WoW64, Sec-CH-Prefers-Color-Scheme, Sec-CH-Prefers-Reduced-Motion, Priority
- Total HeaderKeysToCapture: 28 (was 18)
- CaptureAndEnqueue rewritten to capture-only: parse → hit type → forward to pipe
- Moved services not yet wired into Forge DI — that's Forge subsystem work
- Added `Microsoft.Extensions.Caching.Memory` package to Forge csproj for moved services
- Added `using SmartPiXL.Services` to DatacenterIpService for ITrackingLogger access
- Updated FingerprintStabilityServiceTests using to point to Forge namespace

#### Open items from walkthrough (all Forge-side — subsystems #4/#7)
- Wire moved enrichment services into Forge's EnrichmentPipelineService
- Forge-side geo: evaluate MaxMind-based real-time traffic invalidation (Bangladesh IP → Minnesota dealer example)
- AB test MaxMind vs IPAPI accuracy before decommissioning IPAPI

---

## 2. PiXL Script (Browser JavaScript)

**What it is:** A JavaScript template (`PiXLScript.cs`) served to browsers that collects ~120 fingerprint/behavioral/bot signals and sends them to Edge.

**File:** `SmartPiXL/Scripts/PiXLScript.cs` — 1,195 lines (C# wrapper + JS template)

### Assessment Summary

**Doing well:** Fingerprint diversity (canvas+WebGL+audio+fonts+math+CSS+error), anti-evasion layering (consistency checks, cross-realm toString, getter inspection, timing attacks), 30+ bot detection checks with weighted scoring, cross-signal correlation, sendBeacon+Image fallback, 500ms timeout guarantee, error reporting.

**Doing poorly:** Canvas text says "SmartPiXL" (ad-blocker magnet), not minified (~30KB), uses deprecated `performance.timing`, font detection causes layout thrashing on mobile, DJB2 hash has collision risk, mouse collection window too short for useful data, `speechSynthesis.getVoices()` called synchronously (returns empty), WebRTC connection never closed (resource leak).

### Owner Decisions (2026-03-17)

#### Zero-Entropy Fields — REMOVE
These fields always return the same value in every browser. Removed to reduce payload size and avoid re-adding later.

| Field | Value (always) | Reason for removal |
|-------|---------------|-------------------|
| `navigator.javaEnabled()` | `false` | Java applets dead since ~2017 |
| `navigator.appName` | `"Netscape"` | Legacy constant, never varies |
| `navigator.appCodeName` | `"Mozilla"` | Legacy constant, never varies |
| `navigator.product` | `"Gecko"` | Legacy constant, never varies |
| `navigator.productSub` | `"20030107"` (Chrome) or `"20100101"` (Firefox) | Near-zero entropy (2 values) |
| `navigator.vendorSub` | `""` (empty) | Always empty |
| `navigator.getBattery()` | varies | Deprecated, removed from Firefox/Safari, minimal entropy |
| `navigator.getGamepads()` | usually empty | Security warning risk, near-zero entropy. #1 goal is stealth. |
| `document.compatMode` | `"CSS1Compat"` | Always the same on any modern page |
| `eval.toString()` check | n/a | CSP risk on customer sites, redundant with Function.prototype.toString check |

#### Fields to KEEP (with reasons)
| Field | Reason |
|-------|--------|
| `document.title` | Direct business value — reported to customers (page they visited) |
| `location.hash` | Session state value. Strip auth tokens in Forge parsing (token=, access_token=, key=, secret=, password=, auth= patterns). |
| `location.href`, `document.referrer`, `location.pathname` | Core customer-facing data. Customers need page/referrer reporting. |

#### Hashing — Decision D3
- Replace DJB2 (32-bit, collision-prone) with SubtleCrypto SHA-256 (256-bit) for canvas, audio, WebGL render
- Send raw data where short enough (WebGL params, fonts, Math FP)
- Owner: "I recall one of your compatriots mentioned that hashes are a little too lossy"

#### Noise Detection — Decision D4
- Add noise/consistency checks to ALL fingerprint-producing tests (canvas ✓, audio ✓, WebGL render NEW, fonts partial ✓)
- When noise detected: (a) flag as anti-FP tool user, (b) mark that specific field as not viable for fingerprinting
- Owner: "any field that returns different values from external noise we can then also indicate that field for that record is not viable for fingerprinting"

#### Canvas Text
- Remove "SmartPiXL" branding — replace with generic pangram
- Do NOT randomize text per hit (would make fingerprints unmatchable across visits)
- Owner initially wanted randomization, corrected after understanding that same text → same render → GPU-unique FP

#### New Signals to Add
1. WebGL render fingerprint (draw 3D scene, read pixels, noise-check)
2. `matchMedia` hardware queries (color-gamut, dynamic-range, overflow-block)
3. CSS rendering fingerprint (border-radius, scrollbar width, anti-aliasing)
4. `Intl` collation fingerprint
5. Keyboard layout (`navigator.keyboard.getLayoutMap()`)
6. Permissions API state querying (camera, mic, geo, notifications, push)
7. Feature/Permissions Policy detection
8. `navigator.scheduling.isInputPending`
9. Client-side DeviceHash computation
10. Canvas emoji rendering fingerprint
11. More Client Hints (all available)
12. More bot detection checks (scheduling API, Worklet patterns, stack trace format, Intl.v8BreakIterator, chrome.loadTimes/csi)

#### Fixes
- Replace `performance.timing` with Navigation Timing Level 2
- Adaptive font complexity: 10 fonts on mobile (<768px screen), 30 on desktop
- Fix `speechSynthesis.getVoices()` to listen for `voiceschanged` event
- Close RTCPeerConnection after use (keep attempt, rarely returns real data)
- Extend collection window: 500ms → 1.5s with `visibilitychange` early-send
- Minify + light obfuscation as build step

#### WebRTC / Local IP
- Modern browsers return mDNS placeholders, not real IPs. No workaround exists.
- Keep the attempt (if a real IP comes back, it's a strong signal of old/misconfigured browser)
- Fix the resource leak (close connection)

#### Owner Comments (verbatim)
- **General philosophy:** "My general style is to get everything I can and once I see how it looks with real data, I can assess entropy with real numbers rather than assumptions."
- **On noise detection:** "First that a field is being artificially manipulated, which means they're using anti fingerprinting tech in general. So that's a flag we can easily set based on the noise diff alone."
- **On audio tricks:** "I'm shocked this is a thing that works at all. I want us to find weird ways to calculate fingerprints, then do them twice to check for consistency."
- **On local IP:** "I am not even sure how we can leverage this later, but I fucking love this idea more than I have the capacity to articulate."
- **On timezone:** "We can use locale and time against an IP's geo in the forge before loading the data to SQL and immediately indicate probably a bot because of a big timezone mismatch."
- **On FingerprintJS:** "well fuck.... How is this the first time I'm hearing about this?"
- **On stealth:** "#1 goal is to not be noticed."
- **On minification:** "I do like the idea of making the script 70% smaller and harder to detect/parse."
- **On section 18:** Did not understand browser→server data transmission. After explanation: understood that all data exists only in browser JS memory and MUST be transmitted via HTTP to reach Edge. sendBeacon = POST (preferred), Image = GET fallback. Same-domain callback required for CORS compliance.
- **On gamepads:** "I don't want to risk security warnings at all. So just get rid of it."
- **On location.hash:** Keep but strip auth tokens in Forge. Owner: "opening this door is massive scope creep and not a problem the edge needs to solve."

---

## 3. Named Pipe Transport + Failover

**What it is:** The inter-process communication (IPC) layer that moves TrackingData records from the Edge (IIS) to the Forge (Windows Service) via a Windows named pipe, with multi-layer JSONL failover for zero data loss.

### File Inventory

| File | Lines | Side | Purpose | Assessment |
|------|-------|------|---------|------------|
| `PipeClientService.cs` | 338 | Edge | Named pipe client, Channel<T> queue, batch flush, failover delegation | Solid — batch flush fixed original 70/s bottleneck |
| `JsonlFailoverService.cs` | 168 | Edge | JSONL daily rolling file writer when pipe is down | Clean, simple, now with emergency sync write |
| `PipeListenerService.cs` | 382 | Forge | Named pipe server (N concurrent instances), JSON→TrackingData | Solid — hardcoded app pool name noted |
| `FailoverCatchupService.cs` | 270 | Forge | Timer-based JSONL replay into enrichment channel | Solid — now with dead-letter + .processed retention |
| `ForgeFailoverWriter.cs` | 297 | Forge | JSONL writer for when SQL is down (enriched records) | Solid — now with dead-letter + .processed |
| `ForgeChannels.cs` | 48 | Forge | Two bounded Channel<TrackingData> connecting pipeline stages | Glue, clean |
| `TrackingData.cs` | 102 | Shared | Sealed record, 9 properties mapping to PiXL.Raw | Envelope type for all pipe/channel transit |

**Total: ~1,605 lines across 7 files.**

### Data Flow

**Happy path:** Edge HTTP → TrackingCaptureService → PipeClientService.TryEnqueue (lock-free) → batch loop (512 max, 25ms fill window) → single pipe flush → PipeListenerService → ForgeChannels.Enrichment → EnrichmentPipelineService

**Pipe down (Edge failover):** PipeClientService detects pipe unavailable → exponential backoff (1s→30s cap) → records route to JsonlFailoverService → daily JSONL files in Failover/ → FailoverCatchupService replays on Forge restart (60s scan interval) → records enter enrichment pipeline normally

**SQL down (Forge failover):** EnrichmentPipelineService cannot write to SqlWriter channel → ForgeFailoverWriter persists enriched records to ForgeFailover/ → SqlBulkCopyWriterService replays when SQL recovers

**Channel overflow (Edge):** PipeClientService channel full → TryWrite returns false → record goes to JsonlFailoverService → if *that* channel is also full → emergency synchronous write to disk

### Owner Decisions (2026-03-19)

| Topic | Decision | Details |
|-------|----------|---------|
| Named pipes | **Keep** | "I like the latency and locality of named pipes a lot actually." |
| Auto-recovery | **Keep** | "building it the way we did lets the pipeline auto-recover/heal to some degree and I think that's sexy AF." |
| DropOldest | **Changed to failover** | "I don't want to lose any of the incoming data at all and this is a stupid reason to lose it. If records are going to be dropped, then they go into a failover file that gets read back in later." |
| Channel capacity | **50K** (was 10K) | "Maybe 10k is too small a window." |
| Batch size 512 | **Keep for now** | "512 just sounds astronomically small to me. No action needed now, just awareness." Owner prefers 125K+ in bigger projects but acknowledges traffic level doesn't warrant it. |
| Failover file deletion | **Changed to .processed rename** | "if the data only exists in ram and it fails to get loaded into its destination then we need to save the data to try again later. Saving the source data is paramount because once it is lost, it's gone forever." |
| Malformed JSON lines | **Dead-letter** | Owner on PiXL.Raw philosophy: "Even things that don't parse nicely could be kept. I feel like we could bolster this behavior to never lose any records." |
| Failover directory | **Single absolute path** | Owner: "This should certainly be one directory." Both Edge + Forge now use `C:\inetpub\Smartpixl.info\Failover`. |
| App pool name | **Note for future** | Hardcoded `"IIS APPPOOL\\Smartpixl.info"` in PipeListenerService. Owner: "consistent for now, but I suspect there will be a time we need to change to smartpixl.com in 6 months to a year." |

### Implementation Notes (2026-03-19)

**Changes made:**
- `PipeClientService.cs`: Channel mode `DropOldest` → `DropWrite`. `TryEnqueue()` now routes to JSONL failover when channel is full instead of silently dropping.
- `JsonlFailoverService.cs`: Channel mode `DropOldest` → `DropWrite`. Added `EmergencyWriteToDisk()` — synchronous last-resort write when even the failover channel is saturated.
- `PipeListenerService.cs`: Malformed JSON lines now written to `dead_letter_*.jsonl` instead of skipped. Added `WriteToDeadLetter()` method.
- `FailoverCatchupService.cs`: Processed files renamed to `.processed` instead of deleted. Added `CleanupProcessedFiles()` — deletes `.processed` files older than 7 days. Malformed lines dead-lettered.
- `ForgeFailoverWriter.cs`: `DeleteFile()` replaced with `MarkFileProcessed()` (rename to `.processed`). Malformed lines dead-lettered during replay.
- `SqlBulkCopyWriterService.cs`: Updated to call `MarkFileProcessed()` instead of `DeleteFile()`.
- `appsettings.json` (both): `FailoverDirectory` → absolute `C:\inetpub\Smartpixl.info\Failover`, `QueueCapacity` → 50000.
- `TrackingSettings.cs` + `ForgeSettings.cs`: Compiled defaults updated to match.

**Build:** 0 warnings, 0 errors. **Tests:** 522/522 pass.

#### Owner Comments (verbatim)
- **Named pipes:** "I like the latency and locality of named pipes a lot actually."
- **Auto-recovery:** "I also need to have some way to recover data that fails any part of the pre-SQL path. Building it the way we did lets the pipeline auto-recover/heal to some degree and I think that's sexy AF."
- **DropOldest:** "Honestly it doesn't sound right. I don't want to lose any of the incoming data at all and this is a stupid reason to lose it."
- **Source data preservation:** "if the data only exists in ram and it fails to get loaded into its destination (edge to forge or forge to SQL) then we need to save the data to try again later. Saving the source data is paramount because once it is lost, it's gone forever."
- **Dead-letter philosophy:** "the old idea for pixl.raw maintained a raw copy of the headers so even things that don't parse nicely could be kept. I feel like we could bolster this behavior to never lose any records vis a vi failover files."
- **Failover directory:** "This should certainly be one directory."
- **App pool name:** "Smartpixl.info sounds like a good app pool name for a smartpixl.info website to me. It's consistent for now, but I suspect there will be a time we need to change to smartpixl.com in 6 months to a year."

---

## 4. Forge Pipeline (Sub-Subsystem Walkthrough)

**What it is:** The Windows Service that receives TrackingData from the Edge via named pipe, enriches records through a 15-step chain, writes to SQL, and runs ETL. 42 files, ~11,400 lines.

**Why sub-subsystems?** Owner: "It's too big. We need a better separation of concerns." The Forge is walked through in 9 sub-subsystems (F1–F9), each reviewed independently with observations and decisions.

### Forge Sub-Subsystem Index

| # | Sub-system | Files | Status | Decision |
|---|-----------|-------|--------|----------|
| F1 | **Ingest & Channels** | PipeListenerService, ForgeChannels | **DONE** | FD5–FD8 |
| F2 | Enrichment Engine | EnrichmentPipelineService + 17 services | **DONE** | FD9–FD15 |
| F3 | SQL Writer | SqlBulkCopyWriterService, ParsedRecordParser | **DONE** | FD16–FD22 |
| F4 | Failover & Replay | ForgeFailoverWriter, ForgeReplayService, JsonlFailoverService | **REVIEW** | See below |
| F5 | ETL Pipeline | ParsedBulkInsertService, EtlBackgroundService | not started | — |
| F6 | Background IP | BackgroundIpEnrichmentService | not started | — |
| F7 | Data Sync | IpApiSyncService, CompanyPiXLSyncService | not started | — |
| F8 | Ops & Health | SelfHealingService, MaintenanceScheduler, EmailNotification, InfraHealth | not started | — |
| F9 | Infrastructure | ForgeSettings, ForgeMetrics, MetricsReporter, NumaHelper, Program.cs | not started | — |

### Pre-Walkthrough Decisions (2026-03-19)

These decisions were made from the Forge-level review before drilling into sub-subsystems:

| # | Topic | Decision | Details |
|---|-------|----------|---------|
| FD1 | ForgeSettings defaults | **Aligned** | SqlWriterChannelCapacity 10K→50K, EnrichmentWorkerCount 8→32, NumaNode -1→3. Code defaults now match production appsettings.json. |
| FD2 | Dead-letter format | **Unified to JSONL** | SqlBulkCopyWriterService.WriteDeadLetterAsync → StreamWriter per-line. Extension .json→.jsonl. ForgeReplayService simplified: JSONL primary, JSON array legacy fallback. |
| FD3 | Dedup eviction | **Hybrid** | BackgroundIpEnrichmentService: ConcurrentDictionary<string, long> (TickCount64 timestamps). Every 5min: remove >30min stale, then cap 500K→250K by recency. Replaces nuclear Clear(). |
| FD4 | Enrichments | **Non-optional** | All 18 services required in constructor (no `= null`). Null-check guards removed from EnrichRecord. EnableEnrichments toggle kept as master kill switch. |

---

### F1. Ingest & Channels

**What it is:** The front door of the Forge — named pipe server instances that accept connections from the Edge and deserialize TrackingData into the enrichment channel. Plus the two bounded channels that connect pipeline stages.

#### File Inventory

| File | Lines | Purpose | Origin | Assessment |
|------|-------|---------|--------|------------|
| `PipeListenerService.cs` | 228 | Named pipe server — N concurrent instances, JSON deserialize, dead-letter | Core | Solid, clean |
| `ForgeChannels.cs` | 50 | Two bounded Channel\<TrackingData\> connecting pipeline stages | Core | Minimal, correct |
| `ForgeMetrics.cs` | 273 | Lock-free pipeline timing counters (3 stages), atomic snapshot-and-reset | Core | Excellent design |

**Total: 551 lines across 3 files.**

#### Data Flow

```
Edge (PipeClientService) ──pipe──→ PipeListenerService (N instances)
                                       │
                                       ├─ good JSON ─→ ForgeChannels.Enrichment (50K bounded, Wait)
                                       │                    │
                                       │                    ├─→ EnrichmentPipelineService (32 workers)
                                       │                    │
                                       │                    └─→ ForgeChannels.SqlWriter (50K bounded, Wait)
                                       │                             │
                                       │                             └─→ SqlBulkCopyWriterService
                                       │
                                       └─ bad JSON ──→ dead_letter_YYYY_MM_DD.jsonl
```

#### PipeListenerService — How It Works

**Lifecycle:**
1. `ExecuteAsync` spawns `MaxConcurrentPipeInstances` (default 4) pipe server tasks
2. Each task runs an outer loop: create pipe → wait for connection → read records → disconnect → repeat
3. Inner loop (`ReadRecordsAsync`): reads lines via `StreamReader.ReadLineAsync`, deserializes JSON, writes to enrichment channel with 5s timeout

**Security (Pipe ACL):**
- `LocalSystem` → FullControl (Forge runs as LocalSystem)
- `IIS APPPOOL\Smartpixl.info` → ReadWrite (Edge's app pool identity)
- Pipe direction: `In` (Forge is read-only)
- Created via `NamedPipeServerStreamAcl.Create()` — proper Windows security

**Error handling:**
- Malformed JSON → dead-letter file (never crashes listener)
- Channel full timeout (5s) → record dropped, logged (Edge failover handles persistence)
- IOException → 1s delay, reconnect
- General exception → 2s delay, reconnect
- Cancellation → clean break

#### ForgeChannels — Structure

| Channel | Capacity | FullMode | SingleReader | Writers | Reader |
|---------|----------|----------|--------------|---------|--------|
| Enrichment | 50,000 | Wait | false | PipeListener, ForgeReplayService | EnrichmentPipelineService (32 workers) |
| SqlWriter | 50,000 | Wait | true | EnrichmentPipelineService | SqlBulkCopyWriterService |

Both channels use `BoundedChannelFullMode.Wait` — provides natural backpressure from SQL all the way back to the Edge's pipe client. When SQL slows down, the SqlWriter channel fills, enrichment workers block, the Enrichment channel fills, pipe listeners block, and the Edge's pipe writes start timing out, triggering Edge-side JSONL failover. No data loss at any stage.

#### ForgeMetrics — What It Tracks

**Stage enum:** `PipeDeserialize`, `Enrichment`, `SqlBulkCopy`

**Per-stage counters (Interlocked, lock-free):**
- Count, TotalTicks, MinTicks, MaxTicks, Drops

**Cross-cutting:**
- FailoverCount, EnrichmentChannelDepth, SqlWriterChannelDepth

**Timer:** `Stopwatch.GetTimestamp()` — ~100ns resolution. Snapshot-and-reset every reporting window (MetricsReporterService calls `Snapshot()` which atomically reads and zeroes all counters).

#### Observations

**O1. Hardcoded IIS app pool name.** `"IIS APPPOOL\\Smartpixl.info"` at PipeListenerService L114. If the app pool name changes (which you noted will happen when the domain moves to smartpixl.com), pipe connections silently fail. Should this come from ForgeSettings?

**O2. Hardcoded 5s channel-write timeout.** PipeListenerService L184 creates a 5-second `CancellationTokenSource` for channel writes. On a momentary SQL backup where the enrichment channel is near-full, 5s may not be enough. The record gets dropped and logged. Edge failover handles it, but it's a configurable parameter that isn't configurable. Worth making a ForgeSettings property?

**O3. Dead-letter writes are synchronous under lock.** `WriteToDeadLetter()` uses `lock` + `File.AppendAllText`. Fine for rare malformed JSON. If an attacker sent malformed JSON at high volume, all 4 pipe reader tasks would contend on this lock. Low risk in practice (pipe is ACL-locked to the local app pool), but worth noting.

**O4. Metrics stage gap.** ForgeMetrics tracks 3 stages: PipeDeserialize, Enrichment, SqlBulkCopy. There's no stage for "time spent waiting to write to channel" or "time record sat in channel queue." Channel depth is sampled, but queue wait time per record is invisible. If latency spikes, you can't tell whether it's enrichment taking long or channel congestion.

**O5. No pipe health metric.** There's no counter for "number of active pipe connections" or "pipe reconnects per window." If the Edge is failing to connect (wrong app pool name, ACL issue), the Forge has no visibility into it. The pipe listener just silently sits in `WaitForConnectionAsync`.

#### Questions

**Q1.** App pool name — make configurable via ForgeSettings, or leave hardcoded with a comment for the smartpixl.com migration?

**Q2.** Channel-write timeout — make configurable, or is 5s reasonable enough to leave hardcoded? At 350K hits/hr peak, a record dropped here is caught by Edge failover anyway.

**Q3.** Should ForgeMetrics add a `PipeConnections` counter (incremented on connect, decremented on disconnect) for ops visibility? Or is that overkill since you can check pipe health from the Edge side?

**Q4.** The backpressure chain (SQL → SqlWriter CH → Enrichment → Enrichment CH → Pipe → Edge) is elegant but has one gap: if the Enrichment channel is full and a pipe listener drops a record (5s timeout), the Edge writes it to JSONL failover, and ForgeReplayService picks it up later. But replay goes back into the Enrichment channel — which is still full. Is there a starvation risk where replay records keep getting re-queued into a full channel?

#### F1 Owner Decisions

| ID | Question | Decision | Implementation |
|----|----------|----------|----------------|
| FD5 | Q1: Hardcoded app pool name | Keep hardcoded with TODO comment for domain migration (~6 months). Not worth configuring since it only changes with infrastructure moves. | Added `// TODO: Update pool name to "Smartpixl.com" when domain migrates` in PipeListenerService.cs |
| FD6 | Q2: Channel-write timeout | Make configurable. "I don't like magic numbers, especially when we don't know the system performance under load." | Added `PipeChannelWriteTimeoutMs` to ForgeSettings (default 5000), used in PipeListenerService instead of hardcoded `TimeSpan.FromSeconds(5)` |
| FD7 | Q3: Pipe connection counter | Add it via ForgeMetrics — cheap Interlocked counters, good ops visibility. Tracks connect/disconnect per reporting window so you can see if Edge is connecting/recycling unexpectedly. | Added `PipeConnects`/`PipeDisconnects` counters to ForgeMetrics, recorded in PipeListenerService, included in MetricsSnapshot and Format() |
| FD8 | Q4: Replay starvation risk | Design is sound — no change needed. ForgeReplayService already has a 60s scan interval (natural backoff). Future optimization: check channel depth before replay. The worst case is JSONL files sit on disk longer, not data loss. | No code change. Documented for future consideration. |

---

### F2 — Enrichment Engine

**Scope:** `EnrichmentPipelineService.cs` (~620 lines) + 18 enrichment services (~3,500 lines total) + `BackgroundIpEnrichmentService` (Lane 3)

**What it does:** Reads `TrackingData` records from the enrichment channel, runs 15 sequential enrichment steps across 3 tiers, appends `_srv_*` query string params, and writes enriched records to the SQL writer channel. If the SQL writer channel is full, records go to Forge failover (JSONL on disk).

#### Architecture

```
Enrichment Channel → [4-32 adaptive workers] → EnrichRecord() → SQL Writer Channel
                          ↓ (if channel full)
                     ForgeFailoverWriter (JSONL)
```

Each worker reads one record at a time, calls `EnrichRecord()` synchronously (no await points), and writes the result. Workers are adaptive: 4 minimum, scales up when channel depth exceeds 1,000 items, scales down after 30s idle.

**Three-Lane Architecture:**
- **Lane 1 (inline):** 12 CPU/memory-only services run synchronously inside each worker. No I/O. ~5ms total best case.
- **Lane 2 (SQL ETL):** IPAPI geo enrichment via batch SQL JOIN (runs later in ETL, not in this pipeline).
- **Lane 3 (background):** DNS + WHOIS run in `BackgroundIpEnrichmentService` with 4 I/O workers. Pipeline reads their caches synchronously (zero-latency on cache hit, miss returns nothing).

#### Enrichment Chain (15 steps)

| Step | Tier | Service | Output Params | Strategy |
|------|------|---------|---------------|----------|
| 1 | T1 | BotUaDetection | `_srv_knownBot`, `_srv_botName` | Regex match (ConcurrentDict cache, 50K cap, nuclear eviction) |
| 2 | T1 | UaParsing | `_srv_browser`, `_srv_browserVer`, `_srv_os`, `_srv_osVer`, `_srv_deviceType`, `_srv_deviceModel`, `_srv_deviceBrand` | UAParser + DeviceDetector.NET (ConcurrentDict cache, 50K cap, nuclear eviction) |
| 3 | T1 | DnsLookup | `_srv_rdns`, `_srv_rdnsCloud` | Cache-only read (Lane 3 populates) |
| 4 | T1 | MaxMindGeo | `_srv_mmCC`, `_srv_mmReg`, `_srv_mmCity`, `_srv_mmLat`, `_srv_mmLon`, `_srv_mmASN`, `_srv_mmASNOrg`, `_srv_mmZip`, `_srv_mmTZ` | Offline .mmdb trie (~1μs, ConcurrentDict cache, 200K cap) |
| — | — | Lane 3 Enqueue | — | `_backgroundIp.Enqueue(ip)` — fire-and-forget |
| 5 | T1 | DatacenterIp | `_srv_datacenter`, `_srv_dcProvider` | CIDR trie O(32), volatile swap on weekly refresh |
| 6 | T1 | WhoisAsn | `_srv_whoisASN`, `_srv_whoisOrg` | Cache-only read (Lane 3 populates). Only when MaxMind has CC but no ASN. |
| 7 | T2 | FingerprintStability | `_srv_fpStable`, `_srv_fpUnique`, `_srv_fpAlert`, `_srv_fpHighVolume`, `_srv_fpExtremeVolume`, `_srv_fpHighRate` | Per-IP IMemoryCache (24h TTL), per-entry lock |
| 8 | T2 | IpBehavior | `_srv_subnetVelocity`, `_srv_subnetUniqueIps`, `_srv_rapidFire`, `_srv_subSecondDup` | Subnet /24 velocity + rapid-fire timing (IMemoryCache) |
| 9 | T2 | SessionStitching | `_srv_sessionId`, `_srv_sessionHitNum`, `_srv_sessionDurationSec`, `_srv_sessionPages` | Per-deviceHash in-memory sessions, 30min timeout, 2min eviction sweep |
| 10 | T2 | CrossCustomerIntel | `_srv_crossCustHits`, `_srv_crossCustWindow`, `_srv_crossCustAlert` | IP+FP+company sliding window (2h max, 5min alert) |
| 11 | T2 | DeviceAffluence | `_srv_affluence`, `_srv_gpuTier` | Stateless scoring from GPU/CPU/RAM/screen/platform |
| 12 | T3 | ContradictionMatrix | `_srv_contradictions`, `_srv_contradictionList` | ~15 rules evaluated (stateless, no short-circuit) |
| 13 | T3 | GeographicArbitrage | `_srv_culturalScore`, `_srv_culturalFlags` | Font/language/timezone/number-format consistency (stateless) |
| 14 | T3 | DeviceAgeEstimation | `_srv_deviceAge`, `_srv_deviceAgeAnomaly` | GPU/OS/browser release year triangulation (stateless) |
| 15 | T3 | BehavioralReplay | `_srv_replayDetected`, `_srv_replayMatchFP` | FNV-1a mouse path hashing, cross-FP replay (ConcurrentDict, 1h TTL) |
| 16 | T3 | DeadInternet | `_srv_deadInternetIdx` | Per-customer/hour bot+engagement+diversity aggregate (0-100) |
| 17 | Final | LeadQualityScoring | `_srv_leadScore` | 0-100 human signal score consuming all upstream results |

#### Observations

**O1. Nuclear cache eviction in BotUaDetection and UaParsing.** Both services use `_cache.Clear()` when their `ConcurrentDictionary` reaches 50K entries. This nukes the entire cache instantly — every User-Agent must be re-parsed. With 32 concurrent workers, all of them simultaneously experience cache misses (~400μs per UA for bot detection, ~2-5ms for UA parsing). We already fixed this exact pattern in `BackgroundIpEnrichmentService` during the Forge-level walkthrough (hybrid time+count eviction, evict oldest half instead of clearing). Same fix should apply here.

**O2. Missing `_metrics.RecordFailover()` when SQL writer channel rejects records.** In `RunWorkerAsync` ([EnrichmentPipelineService.cs line ~304](SmartPiXL.Forge/Services/EnrichmentPipelineService.cs#L304)), when `TryWrite` fails, the record goes to `_failoverWriter.Append()` but `RecordFailover()` is never called. The operator has zero visibility in the metrics window that the SQL writer is backed up and records are going to disk. This is a metric gap.

**O3. Comment numbering is wrong in EnrichRecord.** Steps 8-9 appear twice: IpBehavior is "8", SessionStitching is "9", then CrossCustomerIntel is also "8", DeviceAffluence is also "9". The actual execution order is sequential (7→8→9→10→11→...) but the comments create confusion. Minor, but it's the kind of thing that trips you up when reading the code six months from now.

**O4. Lane 3 background workers hardcoded at 4.** `BackgroundIpEnrichmentService` uses 4 I/O workers to warm DNS and WHOIS caches. Each query takes 2-5s, so throughput is ~0.6-1.4 unique IPs/sec. At your traffic levels, if even 10% of hits are unique IPs, that's ~10 unique IPs/sec at peak. The 4 workers can't keep up, so the cache-ahead pattern degrades — more records see cache misses on their *next* hit because the background hasn't caught up yet. Worker count should be configurable via `ForgeSettings`.

**O5. GeographicArbitrage returns perfect score (100) for null country.** When MaxMind can't determine the country, `GeographicArbitrageService.Analyze()` returns `CulturalScore=100` (perfect match). This means an IP with unknown geography gets a *clean* cultural score, which feeds into `LeadQualityScoring` as if everything checks out. This is backwards — unknown geo should be suspicious, not perfect.

**O6. WHOIS timeout results are permanently cached.** `WhoisAsnService` caches timeout failures as `default(WhoisResult)` with no TTL or retry. If a WHOIS server is temporarily down (seconds or minutes), any IPs queried during that window permanently lose their WHOIS data — even after the server recovers. The only fix today is to restart Forge (which clears the in-memory cache).

**O7. Adaptive scaling is conservative (+1 per 5s).** The monitor checks channel depth every 5 seconds and adds 1 worker per cycle when depth >1000. At 100K records/sec, 1000 items = 10ms of work. A traffic burst can fill the channel and stall before scaling reacts. Going from 4 to 32 workers at +1 per 5s takes 2 minutes and 20 seconds of sustained backpressure. At peak traffic, this means the first few minutes after a burst starts will see heavy channel backpressure and potential record drops.

#### Questions

**Q1.** O1 — Should I apply the same hybrid eviction pattern (evict stale + cap to half by recency) to BotUaDetection and UaParsing? This is the same fix we already made to BackgroundIpEnrichmentService. The alternative is LRU, which is more complex but keeps hot entries better.

**Q2.** O5 — When a record has no country (MaxMind can't geo-locate the IP), should the cultural score be LOW (suspicious, e.g. 50 or 30) instead of 100 (perfect)? My reasoning: if we can't locate a visitor geographically, that's suspicious — a legitimate residential IP almost always has a country. Unknown geo is a signal, not an absence of signal.

**Q3.** O4 — Make Lane 3 worker count configurable via `ForgeSettings`? Right now it's hardcoded at 4. I'd suggest something like `BackgroundIpWorkerCount` with a default of 8 or 16, since your server has 144 logical processors and these workers are I/O-bound (waiting on DNS/WHOIS responses), not CPU-bound.

**Q4.** O7 — The scaling is very gentle (+1 worker per 5s). Two options: (a) make it more aggressive (e.g., double when depth >5,000, add +4 when >10,000), or (b) just start at a higher minimum worker count so there's less distance to cover. Given your server's resources, is there any reason NOT to just start with more workers (e.g., `MinEnrichmentWorkers: 16` instead of 4)?

#### F2 Owner Decisions

| ID | Question | Decision | Implementation |
|----|----------|----------|----------------|
| FD9 | O1/Q1: Nuclear cache eviction | Apply shared hybrid eviction. Extract `BoundedCache<TKey, TValue>` into SmartPiXL.Shared so all caches use one code path. | Created `BoundedCache<TKey, TValue>` in SmartPiXL.Shared/Services/. Refactored BotUaDetectionService, UaParsingService, and BackgroundIpEnrichmentService to use it. All three now evict stale+cap instead of Clear(). |
| FD10 | O2: Missing RecordFailover() | Fix the metric gap. | Added `_metrics.RecordFailover()` in RunWorkerAsync when TryWrite fails. |
| FD11 | O3: Wrong comment numbering | Fix. | Corrected header (1-17) and inline comments (10=CrossCustomer, 11=DeviceAffluence, 12-17=T3+Final). Removed phantom "IpApiLookup" step 5 from header. |
| FD12 | O4/Q3: Lane 3 worker count | Make configurable via ForgeSettings, default 8. NUMA already handled (process-wide pinning covers all threads). | Added `BackgroundIpWorkerCount` to ForgeSettings (default 8). BackgroundIpEnrichmentService reads from settings instead of hardcoded 4. |
| FD13 | O5/Q2: Geo scoring for null country | **DEFERRED** — directly related to IP Info DB concept. Will address during IP data strategy discussion. | No change. |
| FD14 | O6: WHOIS permanent caching | **DEFERRED** — will be addressed during IP Info DB work. | No change. |
| FD15 | O7/Q4: Scaling aggressiveness | Monitor interval reduced from 5s to 1s (lightweight enough). MinEnrichmentWorkers raised to 8. All worker pool defaults set to 8. All Forge threads share NUMA node 3. | Changed `checkIntervalMs` from 5000→1000, `scaleDownIdleChecks` from 6→30 (keeps 30s idle window). MinEnrichmentWorkers default 4→8 in ForgeSettings + appsettings.json. |

---

### F3 — SQL Writer

**Scope:** `SqlBulkCopyWriterService.cs` (609 lines) + `TrackingDataReader` inner class + `ForgeChannels.SqlWriter` channel

**What it does:** Single consumer of the SQL Writer channel. Drains enriched `TrackingData` records into batches, writes them to `PiXL.Raw` via `SqlBulkCopy`, and manages a 3-state circuit breaker for SQL availability. When SQL is down, all channel contents drain to JSONL failover on disk.

#### Architecture

```
ForgeChannels.SqlWriter (50K bounded, SingleReader=true)
         │
         ▼
┌──────────────────────────────────────────────────────────────────┐
│  SqlBulkCopyWriterService                                        │
│                                                                  │
│  Batch fill: greedy drain + 50ms fill window                     │
│  Max batch: 100 records (TrackingSettings.BatchSize)             │
│                                                                  │
│  ┌──────────────────────────────────┐                            │
│  │  CIRCUIT BREAKER                 │                            │
│  │  Closed → Open → HalfOpen → ... │                            │
│  └──────────┬───────────────────────┘                            │
│             │                                                    │
│  ┌──[Closed]──────────────────┐   ┌──[Open]──────────────────┐  │
│  │ SqlBulkCopy → PiXL.Raw    │   │ DrainChannelToFailover() │  │
│  │ Retry: 3× (1s/2s/4s)     │   │ → ForgeFailoverWriter    │  │
│  │ Exhausted → WriteDeadLetter│   │ Sleep 1s, check cooldown │  │
│  └────────────────────────────┘   └──────────────────────────┘  │
│             │                                                    │
│  ┌──[HalfOpen]────────────────┐                                  │
│  │ Try one batch              │                                  │
│  │ Success → Closed           │                                  │
│  │ Failure → Open (restart 2m)│                                  │
│  └────────────────────────────┘                                  │
└──────────────────────────────────────────────────────────────────┘
```

#### File Inventory

| File | Lines | Purpose | Dependencies | Assessment |
|------|-------|---------|--------------|------------|
| `SqlBulkCopyWriterService.cs` | 609 | Channel consumer, batching, circuit breaker, SqlBulkCopy, dead-letter | ForgeChannels, ForgeFailoverWriter, ForgeMetrics, TrackingSettings | Solid — see observations below |
| `ForgeChannels.cs` (SqlWriter half) | ~20 | `Channel.CreateBounded<TrackingData>` with `SingleReader=true`, `FullMode=Wait` | TrackingSettings (capacity) | Clean |

#### Key Design Points

**Zero-allocation DbDataReader.** `TrackingDataReader` wraps `List<TrackingData>` directly — `SqlBulkCopy` calls `Read()` + `GetValue()` which index into the list by ordinal. No `DataTable`, no `DataRow`, no intermediate allocations. For 100-record batches, this saves ~100 allocations per batch. Used by both the writer and `ForgeReplayService` for direct replay.

**Batch fill window.** After greedy-draining whatever's available, the writer waits up to 50ms for more items (`BatchFillWindow`). This prevents writing 1-record batches during low traffic while keeping latency under control. At 70 rec/s, this collects ~3-4 records instead of writing 70 single-record batches per second.

**Circuit breaker trip conditions:**
- SQL error 1105 (filegroup full) or 9002 (transaction log full): *immediate* trip
- 2 consecutive *batch* failures (any error): trip
- Error 1205 (deadlock): logged but does NOT trip — retry handles it

**When circuit is Open:** The writer calls `DrainChannelToFailover()` which TryReads the channel in 100-record mini-batches and writes them to `ForgeFailoverWriter`. This prevents the channel from filling up (which would backpressure the enrichment pipeline). After 2 minutes (`HalfOpenCooldown`), it transitions to HalfOpen and probes with one real batch.

**Graceful shutdown:** `DrainChannelAsync()` drains remaining channel items. If the circuit is open, items go directly to failover. If the circuit is closed, it tries SQL. If there's still overflow after the deadline, everything goes to failover as last resort, then `_failoverWriter.Flush()`.

#### Observations

**O1. Two-step SQL write is still live despite D14.** Decision D14 (from the Forge-level walkthrough) says "Merge PiXL.Raw into single table — eliminate two-step SQL load." The current code still writes to `PiXL.Raw` (9 columns) and relies on `ParsedBulkInsertService` (F5) to later parse QueryStrings and BulkCopy them into `PiXL.Parsed` (229 columns). D14 says this double-write is unnecessary because Forge already parses in .NET. This is deferred implementation work, not a code bug — flagging for tracking. When D14 is implemented, `SqlBulkCopyWriterService` would use `ParsedRecordParser` + `ParsedDataReader` to write directly to the merged table (~229+ columns) instead of the 9-column PiXL.Raw.

**O2. BatchSize default 100 is very conservative for Forge.** `TrackingSettings.BatchSize` defaults to 100. This was the right default for the Edge (sub-1000 RPS, direct HTTP serving), but the Forge receives pre-enriched records through a bounded channel. The Forge could handle 1,000–5,000 per batch easily since:
- Records are already in memory (no HTTP backpressure concern)
- SqlBulkCopy is more efficient with larger batches (less per-batch TDS overhead)
- The 50ms fill window already tries to collect more, but caps at 100
- With 32 enrichment workers feeding at ~5ms per record, that's ~6,400 records/sec entering the channel — the writer can barely keep up at 100-record batches with `BulkCopyTimeout=60s`

**O3. No per-batch size metric.** `ForgeMetrics.Record(Stage.SqlBulkCopy, ts, batch.Count)` records timing and count, but there's no visibility into whether batches are consistently small (underperforming) or consistently full (healthy throughput). A histogram or running average of batch fill % (actual/max) would immediately reveal if the batch fill window is working.

**O4. `DrainChannelToFailover` allocates a new `List<TrackingData>(100)` every call.** When the circuit is open, the service calls `DrainChannelToFailover()` in a tight loop (drain → sleep 1s → drain → sleep 1s). Each call allocates a new list. Under sustained SQL outage, this runs hundreds of times. The list should be allocated once and reused with `Clear()`, same as the main batch.

**O5. Dead-letter directory is relative (`AppContext.BaseDirectory + "DeadLetter"`).** This puts dead-letters in `C:\Services\SmartPiXL-Forge\DeadLetter\` in production. `ForgeFailoverWriter` writes to a separate, configurable failover directory. `ForgeReplayService` scans both `./ForgeFailover/` and `./DeadLetter/`. Three different directory paths for preservation — it works but is scattered. D12 established that all failover should use a single absolute directory, but dead-letter wasn't included.

**O6. `ClassifyAndTrip` for error 1205 (deadlock) logs but doesn't return early from `WriteBatchAsync`.** In `ClassifyAndTrip`, the deadlock case logs a warning and returns. But back in `WriteBatchAsync`, the flow after `ClassifyAndTrip` calls `OnBatchFailure()` unconditionally. So a deadlock (which is transient and should be retried) still increments the consecutive failure counter. Two consecutive deadlocks would trip the circuit breaker, even though deadlocks are almost always resolved on retry. The catch clause `when (IsCircuitTripError(sqlEx))` fires first for 1105/9002 — but 1205 doesn't match `IsCircuitTripError`, so it falls through to the general `SqlException` catch. The deadlock handling in `ClassifyAndTrip` is dead code; it can never be reached because the method is only called from catch clauses that already matched specific error codes.

**O7. Connection not pooled/reused across batches.** Each batch creates a new `SqlBulkCopy(_settings.ConnectionString)` instance, which means each batch gets a fresh connection from the pool (or opens one). ADO.NET connection pooling handles this, but there's no explicit connection management — no `SqlConnection` lifetime control. The `using var bulkCopy = new SqlBulkCopy(connString)` shorthand opens and closes a connection per batch. At high throughput (100+ batches/sec), this relies entirely on the ADO.NET pool manager, and there's no visibility into pool exhaustion or contention.

#### Questions

**Q1.** O2 — Should the Forge override `BatchSize` to something larger? The 100 default makes sense for the Edge but is conservative for a channel-draining service. Options: (a) separate `ForgeBatchSize` in ForgeSettings, (b) override in Forge's `appsettings.json`, or (c) just increase the shared default. What batch size feels right — 1,000? 5,000?

**Q2.** O4 — Quick fix: allocate the drain batch once in `ExecuteAsync` scope and pass it into `DrainChannelToFailover(batch)`. Same pattern as the main batch. Want that done?

**Q3.** O5 — Should dead-letter files live under the same absolute failover directory from D12 (`C:\inetpub\Smartpixl.info\Failover\DeadLetter\`)? That would consolidate all preservation files in one tree.

**Q4.** O6 — The deadlock classification code in `ClassifyAndTrip` is unreachable. Should I (a) remove it (simplify), or (b) restructure the catch clauses so deadlocks are actually retried without incrementing the consecutive failure counter?

**Q5.** O1/D14 — The PiXL.Raw → PiXL.Parsed two-step load is the major architecture decision. When you're ready to implement D14 (merge into single table), the change touches `SqlBulkCopyWriterService` (use `ParsedRecordParser` instead of 9-column write), `ParsedBulkInsertService` (eliminated entirely), ETL watermarks, and the SQL schema. Flagging for awareness — not asking for a decision now.

#### F3 Owner Decisions

| ID | Question | Decision | Implementation |
|----|----------|----------|----------------|
| FD16 | Q5/O1: Merge PiXL.Raw into single table | **DONE.** Owner: *"I thought we fixed this already. Combine pixl.raw's raw fields into pixl.parsed and lets just have the forge do one insert into pixl.parsed rather than into raw, then select from raw, then into parsed. That design was so stupid."* Also Q5: *"Keep the columns it has and also have the raw fields from pixl.raw so we're able to re-parse fields that we aren't currently aware of in the future."* Created fresh PiXL.Parsed (231 cols), old table renamed to Parsed_Archive (137M rows). | 58_FreshParsedTable.sql: DROP old constraints/indexes → RENAME to Parsed_Archive → CREATE fresh PiXL.Parsed (231 cols incl QueryString+HeadersJson, 4 indexes) → RESET HitSequence to 1 → ZERO all watermarks. SqlBulkCopyWriterService completely rewritten: inline ParsedRecordParser.Parse() → 230-column BulkCopy direct to PiXL.Parsed. ParsedBulkInsertService disabled. |
| FD17 | Q1/O2: Forge batch size | **ForgeBatchSize = 500 in ForgeSettings.** Owner: *"Make it less conservative. The hardware on here is going to waste, lets use it."* On sizing: *"My personal default for batch sizes is 150k for bulkcopy on other systems. My guess is to bump it up to 500-1000. I just don't have enough data coming in to test and learn proper values yet."* Started at 500, will tune up with more traffic data. | Added `ForgeBatchSize` to ForgeSettings (default 500). SqlBulkCopyWriterService reads from ForgeSettings. appsettings.json updated. |
| FD18 | Q2/O4: DrainChannelToFailover allocation | **Fixed.** Owner: *"I agree."* | `_drainBuffer` allocated once as class field (`new(500)`), reused with `Clear()` per drain cycle. |
| FD19 | O3: Batch fill metric | **Added.** Owner: *"Thank you for surfacing this concern. Lets add metrics for this so they can be reported on to the dashboard when we get to that."* | Added `RecordBatchFill(int actual, int max)` to ForgeMetrics. SqlBulkCopyWriterService calls it after each batch. Pipeline Explorer displays fill %. |
| FD20 | Q3/O5: Dead-letter directory | **Consolidated to absolute path.** Owner: *"Funny, I thought we already agreed to make this one absolute path for consistency."* Q3: *"Yes please. I very much want them all in one place."* | Added `DeadLetterDirectory` to ForgeSettings (default `C:\Services\SmartPiXL-Forge\DeadLetter`). All three preservation directories now absolute via appsettings.json. |
| FD21 | Q4/O6: Deadlock classification | **Restructured.** Owner: *"I would prefer a sensible restructure, because I think it's fine to retry on a deadlock esp with fast batches like we have."* | Separate catch clause for `IsDeadlock(sqlEx)`: logs warning, calls `InvalidateConnection()`, retries via `continue` — does NOT increment `_consecutiveBatchFailures` or call `ClassifyAndTrip()`. Circuit breaker only trips on 1105/9002 or 2+ consecutive non-deadlock failures. |
| FD22 | O7: Connection reuse | **Implemented.** Owner: *"This is not ok. We're going to re-use connections or use connection pooling, but creating new connections per batch is not acceptable."* | SqlBulkCopyWriterService maintains `_conn` field. `EnsureConnectionAsync()` returns existing open connection or creates new one. `SqlBulkCopy(conn)` reuses the connection. `InvalidateConnection()` disposes on failure for reconnect on next batch. |

---

### F4 — Failover & Replay

**Scope:** `ForgeFailoverWriter.cs` (258 lines) + `ForgeReplayService.cs` (399 lines) + `JsonlFailoverService.cs` (145 lines)

**What it does:** Three-tier failover hierarchy ensuring zero data loss across the entire pipeline. When any downstream component is unavailable (named pipe, enrichment channel, SQL), records are persisted to JSONL files on disk and automatically replayed when the component recovers.

#### Architecture

```
EDGE (pipe unavailable):
  PipeClientService ──fail──→ JsonlFailoverService.TryEnqueue()
                                    │
                                    ├─ lock-free Channel<T> (bounded, DropWrite)
                                    │     └─→ Background writer → failover_{yyyy_MM_dd}.jsonl
                                    │
                                    └─ channel full → EmergencyWriteToDisk() (sync, locked)
                                          └─→ failover_emergency_{yyyy_MM_dd}.jsonl

FORGE (SQL unavailable / channel full):
  EnrichmentPipelineService ──TryWrite fails──→ ForgeFailoverWriter.Append()
  SqlBulkCopyWriterService  ──circuit open───→ ForgeFailoverWriter.AppendBatch()
                                                    │
                                                    └─→ failover_{yyyyMMdd_HHmmss}_{guid}.jsonl
                                                         (rotates every 10K records)

  SqlBulkCopyWriterService  ──retries exhausted──→ DeadLetter/
                                                       └─→ dead_letter_{yyyy_MM_dd}.jsonl

REPLAY (unified background service, every 60s):
  ForgeReplayService.ScanAndReplayAsync():
    1. Forge failover dir → enriched → SqlBulkCopy direct (bypass enrichment)
    2. Dead-letter dir    → enriched → SqlBulkCopy direct (bypass enrichment)
    3. Edge failover dir  → un-enriched → Enrichment channel (full Tier 1-3)
    4. Cleanup .processed files > 7 days across all directories
```

#### File Inventory

| File | Lines | Purpose | Process | Dependencies |
|------|-------|---------|---------|--------------|
| `ForgeFailoverWriter.cs` | 258 | Thread-safe JSONL writer for enriched records | Forge | ForgeMetrics, ITrackingLogger |
| `ForgeReplayService.cs` | 399 | Unified replay orchestrator — 3 directories, 2 paths | Forge | ForgeChannels, ForgeSettings, TrackingSettings, ForgeFailoverWriter, ForgeMetrics |
| `JsonlFailoverService.cs` | 145 | Edge-side failover when named pipe unavailable | Edge | TrackingSettings, ITrackingLogger |

**Total: 802 lines across 3 files.**

#### Failover Tier Comparison

| Tier | Trigger | Writer | Records Are | File Pattern | Replay Path |
|------|---------|--------|-------------|--------------|-------------|
| Edge failover | Pipe to Forge unavailable | `JsonlFailoverService` | **Un-enriched** (raw request data) | `failover_{yyyy_MM_dd}.jsonl` | → Enrichment channel (full Tier 1-3) |
| Forge failover | SQL circuit open or enrichment channel full | `ForgeFailoverWriter` | **Enriched** (all `_srv_*` params) | `failover_{yyyyMMdd_HHmmss}_{guid}.jsonl` | → SqlBulkCopy direct (no re-enrichment) |
| Dead-letter | Batch retry exhaustion (3 attempts failed) | `SqlBulkCopyWriterService` | **Enriched** | `dead_letter_{yyyy_MM_dd}.jsonl` | → SqlBulkCopy direct |

#### ForgeFailoverWriter — Key Design Points

**Thread safety:** `lock (_gate)` on all write operations. Acceptable because failover is exceptional (SQL outage), not hot-path. JSON serialization happens *outside* the lock for `Append()` (one record at a time), minimizing lock hold time.

**File rotation:** After every write, checks `_recordsInCurrentFile >= MaxRecordsPerFile` (10K). If exceeded, closes current writer and next write opens a new file. GUIDs in filename prevent collisions on rapid rotation.

**AutoFlush:** `StreamWriter.AutoFlush = true` — every line is flushed to disk immediately. Costs throughput but guarantees no data loss on process crash during failover.

**Metrics integration:** Every record appended calls `_metrics.RecordFailover()`. Operators see failover count in the Pipeline Explorer immediately during outages.

**Dead-letter preservation:** When `ReadFile()` encounters malformed JSON lines, they're written to `dead_letter_{date}.jsonl` with a `// Source:` comment tracking the origin file and timestamp — malformed data is never silently dropped.

#### ForgeReplayService — Key Design Points

**Smart routing:** The critical design choice — based on which directory a file came from, the service knows whether records need enrichment or not:
- Forge failover / dead-letter → already enriched → direct SqlBulkCopy (fast)
- Edge failover → raw request data → enrichment channel (full pipeline processing)

**Format detection:** `ReadFileAdaptive()` tries JSONL first, falls back to JSON array for legacy dead-letter files created before the format unification (FD2).

**Graceful retry:** SQL write failures leave the file untouched for the next 60s scan cycle. No in-memory retry loop — the scan interval provides natural backoff.

**Channel timeout:** When enqueuing un-enriched records to the enrichment channel, each record has a 30s timeout. If the channel stays full for 30s, the file is left for the next cycle. This prevents replay from blocking indefinitely when the pipeline is saturated.

**File lifecycle:** `.jsonl` → replay → `.jsonl.processed` → cleanup after 7 days. The 7-day retention preserves source data for debugging while preventing unbounded disk growth.

**Batch sub-splitting:** Large files are sub-batched to `ForgeSettings.ForgeBatchSize` (500) for SqlBulkCopy. Each sub-batch gets 3 retry attempts with 1s/2s/4s delays.

#### JsonlFailoverService — Key Design Points

**Two-path write model:**
1. **Normal path:** `TryWrite` to bounded channel → single background writer consumes → daily rolling JSONL file. Lock-free, no contention.
2. **Emergency path:** When channel is full (`TryWrite` fails), synchronous `EmergencyWriteToDisk()` under `_emergencyWriteLock` writes directly to `failover_emergency_{date}.jsonl`. This is the absolute last safety net — accepts lock cost because it only fires under extreme backpressure.

**Daily rolling:** New file at midnight UTC. `_currentDate` tracks the active day; when the date string changes, the current writer is disposed and a new file is opened.

**Drain on shutdown:** After the channel reader completes, `while (TryRead)` drains any remaining records before disposing the writer. No data left in the channel at shutdown.

#### Observations

**O1. No idempotency tracking on replay.** `WriteBatchesToSqlAsync` sub-batches a file into ForgeBatchSize chunks. If sub-batch 2 of 5 fails, the method returns `false` and the entire file is retried next cycle. Sub-batch 1 was already written to SQL — those records will be duplicated on the next replay. For enriched records, there's no dedup key or upsert logic. At current traffic volumes (~60 rec/s), this is low-risk (failover is rare), but under a scenario where SQL flaps (circuit open → half-open → writes partially → fails again), it could produce duplicates.

**O2. Edge failover uses indented JSON.** `JsonlFailoverService.WriteRecordAsync` uses default `JsonSerializer.Serialize()` which produces indented JSON. ForgeFailoverWriter correctly uses `WriteIndented = false` (compact). During an extended pipe outage at 60 rec/s, Edge failover writes ~2× the bytes it needs to per record. At scale this wastes disk I/O and storage.

**O3. Emergency write returns true even on failure.** `EmergencyWriteToDisk` catches exceptions and logs "CRITICAL" but `TryEnqueue` always returns `true` regardless. The caller (`PipeClientService`) thinks the record was saved, but it may have been lost if the disk write failed. In practice, disk failure during emergency write is extremely unlikely (the system has bigger problems if the disk is failing), and always returning `true` prevents the caller from retrying in a tight loop. Acceptable trade-off, but worth documenting.

**O4. Partial sub-batch failure leaves orphan records.** Related to O1 — when a partial replay writes some sub-batches to SQL then fails, the file stays for retry. On retry, all sub-batches (including already-written ones) are replayed again. A simple fix would be per-file progress tracking (e.g., write a `.progress` file with the last successful batch offset), but this adds complexity. Alternative: SourceId SEQUENCE + `IGNORE_DUP_KEY` index on SourceId could silently absorb duplicates at the SQL level.

**O5. ForgeReplayService scans dead-letter to SQL without re-enrichment.** Dead-letter files are assumed to contain enriched records. This is correct for the normal flow (batch retry exhaustion after enrichment). However, if `ForgeFailoverWriter.ReadFile()` encounters a malformed line, it dead-letters the raw line text — which may be un-enriched data from a different failure path. On the next scan, this dead-lettered line would be replayed directly to SQL without enrichment. Low risk because dead-letter files from ReadFile() contain JSON comment lines (prefixed with `//`) which `ReadAsJsonl` skips.

**O6. No file-level metrics in ForgeReplayService.** The service logs file-level replay counts but doesn't record them in ForgeMetrics. Operators can see failover write counts (via `RecordFailover()`) but have no metric for "how many files are pending replay" or "how many records were replayed this cycle." The Pipeline Explorer shows failover count but not replay recovery.

**O7. JsonlFailoverService has redundant directory resolution.** The directory path is resolved twice — once in the constructor (stored as `_resolvedDirectory` for emergency writes) and again at the top of `ExecuteAsync`. Both paths use the same `Path.IsPathRooted` logic. Should consolidate to use `_resolvedDirectory` in both places.

#### Questions

**Q1.** O1/O4 — Should we add a simple progress-tracking mechanism (`.progress` sidecar file) to avoid duplicate writes on partial replay? Or is the current "retry entire file" approach acceptable given that failover is rare and duplicates are non-destructive (same data, same enrichment)?

**Q2.** O2 — Quick fix: pass `JsonSerializerOptions { WriteIndented = false }` in JsonlFailoverService to match ForgeFailoverWriter. Worth doing?

**Q3.** O6 — Should ForgeMetrics track replay counts (files processed, records replayed per cycle)? This would let the Pipeline Explorer show recovery progress during/after outages.

**Q4.** O7 — Consolidate the directory resolution to use `_resolvedDirectory` everywhere in JsonlFailoverService? Trivial cleanup.
