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
| D19 | **Health Tree — organizing principle for SmartPiXL** | Hierarchical tree: Platform → System → Subsystem → Component → Probe. Probes are leaves (binary health). Parents aggregate as ratios. Metrics are continuous; health is derived boolean. Tree is a living document, defined now, filled by walkthroughs. See Health Tree section. | 2026-03-23 |

---

## Health Tree

The single source of truth for how SmartPiXL is organized, monitored, and explained.
Every node in this tree is either a **container** (has children, health = ratio) or a **probe** (leaf, health = 1 or 0). Metrics are continuous measurements (counters, timings). Health is a derived binary judgment that consumes metrics. The tree doesn't replace ForgeMetrics — it sits on top of it.

### Design Principles

**Naming taxonomy.** Fixed vocabulary by depth — no "sub-sub-subsystem" language:

| Depth | Term | Example |
|-------|------|---------|
| 0 | **Platform** | SmartPiXL |
| 1 | **System** | Forge, Edge, Sentinel |
| 2 | **Subsystem** | Data Sync, Enrichment Engine, SQL Writer |
| 3 | **Component** | IpDataAcquisitionService, CompanyPiXLSyncService |
| 4 | **Probe** | IPtoASN import, DB-IP import |

Depth varies by branch. The tree is asymmetric and that's correct — depth reflects actual complexity, not a structural requirement. Refer to nodes by name, not by depth. "Bloom," not "the renderer's post-processing subsystem's bloom component."

**Probe definition — the smallest unit worth measuring.** A probe (leaf node) must satisfy ALL four criteria:

1. **Independently failable** — It can break while its siblings stay healthy.
2. **Independently diagnosable** — When it fails, you know *what* broke without investigating siblings.
3. **Independently actionable** — There's a specific thing you'd do to fix it.
4. **Not further decomposable** into meaningfully separate failure modes that you'd act on differently.

Test: *"Would I page myself differently depending on which child failed?"* If yes, decompose further. If no, it's a leaf.

**Health vs. Metrics.** Two layers, not one:
- **Metrics** = continuous measurements (ForgeMetrics Lanes 1-5). Answer: *"How is it performing?"*
- **Health** = derived boolean per probe. Each probe has a **health function** — a rule that turns recent metrics into 1 or 0. Answer: *"Is it working?"*

**Ratio aggregation.** Each non-leaf node stores (healthy, total):

    Parent health = Σ(child healthy) / Σ(child total)

This cascades naturally. Forge 8.75/9 + Sentinel 3.2/4 + Edge 5/5 = **16.95/18** platform-wide. One number tells the whole story. Drill down to find what's missing. Display thresholds:
- 100% = green
- 50–99% = yellow (degraded)  
- <50% = red (critical)

### The Tree

Living document. Nodes marked `(?)` have not been walked yet. Each walkthrough fills in its branch. The tree grows with the project.

```
SmartPiXL (platform)                               16.95/18 (example)
│
├── Edge (system)                                   health = 5/5 components
│   ├── HTTP Listener                               probe: Kestrel responding
│   ├── Capture Pipeline                            probe: requests → TrackingData succeeding
│   ├── Pipe Client                                 probe: connected to Forge pipe
│   ├── JSONL Failover                              probe: writable, not actively failing over
│   └── Edge Enrichments                            health = 5/5 probes
│       ├── DatacenterIp                            probe: CIDR ranges loaded
│       ├── IpClassification                        probe: classifying (stateless → always 1)
│       ├── IpBehavior                              probe: tracking state
│       ├── FingerprintStability                    probe: tracking state
│       └── GeoCache                                probe: cache populated, SQL reachable
│
├── Forge (system)                                  health = 9/9 subsystems
│   ├── F1: Ingest                                  health = 2/2
│   │   ├── Pipe Listener                           probe: accepting connections
│   │   └── Enrichment Channel                      probe: not full, consumers alive
│   │
│   ├── F2: Enrichment Engine                       health = ?/? (per-service probes)
│   │   ├── Worker Pool                             probe: workers alive, processing
│   │   ├── UaParsing                               probe: stateless → always 1
│   │   ├── BotUaDetection                          probe: stateless → always 1
│   │   ├── DnsLookup                               probe: recent lookups succeeding
│   │   ├── WhoisAsn                                probe: recent lookups succeeding
│   │   ├── MaxMindGeo                              probe: last refresh < 25h
│   │   ├── IpClassification                        probe: stateless → always 1
│   │   ├── ContradictionMatrix                     probe: stateless → always 1
│   │   ├── DeviceAffluence                         probe: stateless → always 1
│   │   ├── DeviceAgeEstimation                     probe: stateless → always 1
│   │   ├── DeadInternet                            probe: stateless → always 1
│   │   ├── GeographicArbitrage                     probe: stateless → always 1
│   │   ├── BehavioralReplay                        probe: stateless → always 1
│   │   ├── CrossCustomerIntel                      probe: stateless → always 1
│   │   ├── SessionStitching                        probe: stateless → always 1
│   │   ├── GpuTierReference                        probe: stateless → always 1
│   │   └── LeadQualityScoring                      probe: stateless → always 1
│   │
│   ├── F3: SQL Writer                              health = 1/1
│   │   └── BulkCopy                                probe: batches writing, no failures
│   │
│   ├── F4: Failover & Replay                       health = 2/2
│   │   ├── Failover Writer                         probe: writable (disk ok → always 1)
│   │   └── Replay Service                          probe: no stuck files > 1h old
│   │
│   ├── F5: ETL Pipeline                            health = 2/2
│   │   ├── MatchVisits                             probe: last run < 2min ago
│   │   └── MatchLegacyVisits                       probe: last run < 2min ago
│   │
│   ├── F6: Background IP                           health = 2/2
│   │   ├── DNS Enrichment                          probe: lookups processing
│   │   └── WHOIS Enrichment                        probe: lookups processing
│   │
│   ├── F7: Data Sync                               health = 2/2
│   │   ├── Company/Pixel Sync                      probe: last sync < 7h, 0 failures
│   │   └── IP Data Acquisition                     health = 2/2
│   │       ├── IPtoASN                             probe: last import < 26h
│   │       └── DB-IP                               probe: last import < 35 days
│   │
│   ├── F8: Ops & Health (?)                        not walked — structure TBD
│   │
│   └── F9: Infrastructure (?)                      not walked — structure TBD
│
└── Sentinel (system) (?)                           not walked — structure TBD
    ├── Tron Dashboard (?)                          probe: responding, data fresh
    ├── Atlas Docs (?)                              probe: responding
    └── TrafficAlert API (?)                        probe: responding
```

### MetricsLane → Tree Mapping

ForgeMetrics lanes map to subtrees. Each lane provides the raw data that probe health functions consume.

| Lane | Counters | Tree Node(s) |
|------|----------|--------------|
| 1 — Pipe→Channel | PipeCount, PipeTotalTicks, PipeDrops | F1: Ingest |
| 2 — Enrichment→Channel | EnrichCount, EnrichTotalTicks, EnrichDrops | F2: Enrichment Engine |
| 3 — SQL→DB | SqlCount, SqlBatchCount, SqlFailures | F3: SQL Writer |
| 3b — Background IP | BgIpEnqueued, BgIpProcessed, BgIpDnsLookups, BgIpWhoisLookups | F6: Background IP |
| 4 — Data Sync | SyncCompanyCycles, SyncPixelCycles, SyncFailures, SyncDurationTicks | F7: Company/Pixel Sync |
| 5 — IP Acquisition | IpAcqCycles, IpAcqAsnRows, IpAcqGeoRows, IpAcqSkipped, IpAcqFailures | F7: IP Data Acquisition |
| (future) | F8 Ops counters TBD | F8: Ops & Health |
| — | Failover/Replay counters | F4: Failover & Replay |
| — | Pipe connect/disconnect | F1: Ingest |

### Open Questions

- **Stateless enrichments:** 12 of 17 enrichment probes are "stateless → always 1." Should these be individual probes, or collapsed into a single "Stateless Enrichments" probe that checks "did the enrichment pipeline run without exceptions"? Individual probes are more precise but mostly noise (they're always green). Collapsed is cleaner but loses granularity if one is ever made stateful.
- **Edge enrichments vs. Forge enrichments:** IpClassification exists in both Edge and Forge. Should the tree show it once or twice? Currently shown in both because they're separate service instances with separate failure modes.
- **SQL schema, CLR, test suite (subsystems 5, 14, 15):** These aren't runtime services — they don't have "health." Should they exist in the tree at all, or is the tree strictly for runtime components? The walkthroughs still cover them, but they might not be tree nodes.
- **Sentinel structure:** Only sketched with 3 guesses. Actual structure TBD in subsystem 10 walkthrough.

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
| F4 | Failover & Replay | ForgeFailoverWriter, ForgeReplayService, JsonlFailoverService | **DONE** | FD23 |
| F5 | ETL Pipeline | ParsedBulkInsertService, EtlBackgroundService | **DONE** | FD24–FD32 |
| F6 | Background IP | BackgroundIpEnrichmentService | **complete** | All 7 observations resolved (FD33-FD37) |
| F7 | Data Sync | CompanyPiXLSyncService, IpDataAcquisitionService | **complete** | IPAPI deleted, MERGE→UPDATE+INSERT, ForgeMetrics added |
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

#### Observation Severity

| # | Severity | Summary |
|---|----------|----------|
| O1 | Nitpick | No idempotency on partial replay — duplicates possible but failover is rare and non-destructive |
| O2 | Nitpick | Edge failover uses indented JSON — 2× bytes, but pipe outages are infrequent |
| O3 | Nitpick | Emergency write returns true on failure — acceptable trade-off, prevents tight retry loops |
| O4 | Nitpick | Partial sub-batch orphan records — related to O1, same low-risk assessment |
| O5 | Nitpick | Dead-letter raw lines — `//`-prefixed comment lines are skipped on replay anyway |
| O6 | **Minor** | No replay metrics in ForgeMetrics — fixed (FD23) |
| O7 | Nitpick | Redundant directory resolution — trivial cleanup, deferred |

#### Owner Decisions

**FD23 — Fix O6: Add replay metrics to ForgeMetrics.** Owner: "Keep the observations and questions you found and just fix O6 for now." Severity assessment: 1 minor real issue (O6), 6 nitpicks. All observations remain documented for future reference.

**Implementation:** Added `RecordReplay(int recordCount)` to `ForgeMetrics` with `_replayFiles` and `_replayRecords` counters. Added `ReplayFiles` and `ReplayRecords` to `MetricsSnapshot`. Updated `Format()` to show `REPLAY {n} file(s) {n} rec` when replay activity occurs. Updated `ForgeReplayService.ReplayDirectoryToSqlAsync` and `ReplayDirectoryToEnrichmentAsync` to call `_metrics.RecordReplay()` after each successful file replay.

**Answers to Questions:**
- Q1 (progress tracking): Deferred. Current "retry entire file" is acceptable — failover is rare, duplicates are non-destructive.
- Q2 (indented JSON): Deferred. Low-impact optimization for rare event.
- Q3 (replay metrics): **Yes — implemented as FD23.**
- Q4 (directory consolidation): Deferred. Trivial cleanup, not urgent.

### F5 — ETL Pipeline

**Scope:** `ParsedBulkInsertService.cs` (350 lines) + `EtlBackgroundService.cs` (199 lines)

**What it does:** Two background services that handle post-ingest data processing. `EtlBackgroundService` is the **active** service — it runs Phase 9–13 dimension processing (Device/IP/Visit upserts) and identity resolution (email, IP, geo matching) every 60 seconds. `ParsedBulkInsertService` is **disabled** — it was the original Raw → Parsed backfill pipeline, now replaced by the merged pipeline (SqlBulkCopyWriterService writes directly to Parsed).

#### Architecture

```
ACTIVE — EtlBackgroundService (every 60s):
  ├─ Phase 9-13: RunDimensionProcessingAsync()
  │   ├─ Read 'ProcessDimensions' watermark from ETL.Watermark
  │   ├─ Read MAX(SourceId) from PiXL.Parsed
  │   ├─ Loop in 50K batches: EXEC ETL.usp_ProcessDimensions @FromId, @ToId
  │   └─ Advance watermark after each batch
  ├─ EXEC ETL.usp_MatchVisits @BatchSize=1000        (email identity resolution)
  ├─ EXEC ETL.usp_MatchLegacyVisits @BatchSize=5000  (legacy IP matching)
  └─ EXEC ETL.usp_MatchGeoVisits @BatchSize=2000     (geo proximity matching)

DISABLED — ParsedBulkInsertService (commented out in Program.cs):
  ├─ Read 'ParseNewHits' watermark from ETL.Watermark
  ├─ Read PiXL.Raw batch (50K rows)
  ├─ Parse QueryString in .NET via ParsedRecordParser
  ├─ SqlBulkCopy → PiXL.Parsed (with crash-recovery dedup via HashSet)
  └─ EXEC ETL.usp_ProcessDimensions + advance watermark
```

#### File Inventory

| File | Lines | Status | Purpose | Dependencies |
|------|-------|--------|---------|--------------|
| `EtlBackgroundService.cs` | 199 | **Active** | Dimension processing (Phase 9-13) + identity resolution | TrackingSettings, ITrackingLogger |
| `ParsedBulkInsertService.cs` | 350 | **Disabled** | Historical Raw → Parsed backfill (can re-enable) | TrackingSettings, ITrackingLogger |

**Total: 549 lines across 2 files (199 active).**

#### EtlBackgroundService — Key Design Points

**Watermark-driven dimensions:** Uses a separate `ProcessDimensions` watermark (distinct from `ParseNewHits`). Reads MAX(SourceId) from PiXL.Parsed, then processes 50K rows per batch via `ETL.usp_ProcessDimensions(@FromId, @ToId)`. This decouples dimension processing from ingest — the merged pipeline writes to Parsed and the ETL service catches up asynchronously.

**Identity resolution sequence:** After dimensions, three match procs run sequentially with different batch sizes: `usp_MatchVisits` (1000), `usp_MatchLegacyVisits` (5000), `usp_MatchGeoVisits` (2000). Batch sizes probably tuned to their respective query costs — email matching is more expensive per row than simple IP matching.

**Fixed 60s interval:** Wait happens *after* the entire ETL cycle completes, so actual period is `cycle_time + 60s`. No overlap protection needed because there's only one instance.

**Single connection:** One SqlConnection for the entire cycle (dimensions + 3 match procs). Connection stays open for the full cycle duration.

#### ParsedBulkInsertService — Key Design Points

**Disabled but compilable.** Commented out in Program.cs with explanation: "The Forge now writes directly to PiXL.Parsed via SqlBulkCopyWriterService (merged pipeline). Can be re-enabled for backfilling historical data."

**Crash recovery:** Before BulkCopy, reads existing SourceIds in Parsed for the target range into a HashSet. Skips rows already present. This handles the case where a previous batch wrote to Parsed but the watermark wasn't advanced (proc failure / crash).

**No HeadersJson:** Passes `null` for the `headersJson` parameter to `ParsedRecordParser.Parse()` because PiXL.Raw doesn't store headers. This means backfilled records will have no header-derived columns — acceptable since headers are a recent addition to the ingest pipeline.

**Direct watermark management:** Unlike EtlBackgroundService which relies on the proc, ParsedBulkInsertService manually advances the `ParseNewHits` watermark via direct SQL UPDATE after the proc completes. This avoids the "self-healing range collision" where the proc might advance watermark past the pre-parsed range.

**Detailed telemetry:** Logs per-batch timing breakdown (read/parse/bulk/etl ms), total rate, remaining rows, and ETA. Useful for monitoring backfill progress.

#### Observations

| # | Severity | Summary |
|---|----------|---------|
| O1 | Nitpick | Match procs run every cycle even when no new dimensions |
| O2 | Nitpick | Dimension processing results silently discarded |
| O3 | Nitpick | Repeated proc call boilerplate across 3 match procs |
| O4 | Minor | `ParsedBulkInsertService` has stale comment referencing `usp_ParseNewHits` |
| O5 | Nitpick | No error handling per-batch in dimension loop |

**O1. Match procs run every cycle regardless of dimension progress.** [Severity: Nitpick] The three match procs execute every 60s even when `RunDimensionProcessingAsync` found no new rows. These procs have their own internal watermarks so they'll short-circuit quickly when there's nothing to do, but it's unnecessary SQL round trips when idle. Could skip match procs when no dimensions were processed, but the overhead is negligible.

**O2. Dimension processing results discarded in EtlBackgroundService.** [Severity: Nitpick] `EtlBackgroundService.RunDimensionProcessingAsync` calls `ExecuteNonQueryAsync`, so proc output (devices, IPs, visits created) is not captured. `ParsedBulkInsertService.CallDimensionsAndAdvanceWatermarkAsync` uses `ExecuteReaderAsync` and logs the counts. The EtlBackgroundService version only logs total rows processed per cycle, not the breakdown. Low priority — the total log message is sufficient for monitoring.

**O3. Repeated boilerplate for match proc calls.** [Severity: Nitpick] The 3 match procs in `RunEtlAsync` follow identical patterns: create command, set proc name, add @BatchSize, execute reader, read 2 values, log. Could extract a helper like `CallMatchProcAsync(conn, procName, batchSize, label, ct)`. Pure code style — works as-is.

**O4. Stale comment in ParsedBulkInsertService header.** [Severity: Minor] The header comment (line 22-26) still references "Call ETL.usp_ParseNewHits" as step 5 of the pipeline, but the actual code calls `ETL.usp_ProcessDimensions` directly and manages the watermark itself. The `usp_ParseNewHits` integration was removed during the merged pipeline work but the comment wasn't updated. Misleading for anyone reading the code fresh.

**O5. No per-batch error handling in dimension loop.** [Severity: Nitpick] If `ETL.usp_ProcessDimensions` throws on batch N of 10, the entire `RunDimensionProcessingAsync` exits. The watermark is already advanced for batches 1 through N-1 (each batch advances it), so only batch N is lost. The outer `ExecuteAsync` catch logs the error and the next 60s cycle retries from the correct watermark. Effective enough.

#### Questions

**Q1.** The `ParsedBulkInsertService` header comment still describes the pre-merge workflow (step 5 calls `usp_ParseNewHits`). Should I fix the comment to accurately reflect the current code (calls `usp_ProcessDimensions` directly + manual watermark advance)? This is trivially fixable.

**Q2.** EtlBackgroundService uses `ExecuteNonQueryAsync` for `usp_ProcessDimensions`, losing the proc's output (device/IP/visit counts). ParsedBulkInsertService uses `ExecuteReaderAsync` and logs them. Should the active service (EtlBackgroundService) match the more informative approach and log per-batch Device/IP/Visit counts?

#### Owner Decisions

**FD24 — C# stays as proc scheduler; all ETL logic in stored procedures.** Owner: "This is big batch work and that's what SQL is good at." C# services call procs, manage watermarks, and handle scheduling. No batch-level business logic in .NET.

**FD25 — Replace all MERGEs with INSERT + UPDATE.** Owner: "I detest MERGE in SQL. It's too slow for me in almost all use cases." Verified: The live `ETL.usp_ProcessDimensions` proc already uses UPDATE + INSERT (no MERGE). The MERGE pattern only remains in the old `ETL.usp_ParseNewHits` proc body (in `20_ETLPhases9to13.sql`) which is the historical parsing proc, not the active dimension processing path. No code change needed — already done.

**FD26 — PiXL.IP = single source of truth (behavioral + enriched).** Owner: "We could just feed it into the PiXL.IP table and THAT is the source of truth." Verified: PiXL.IP already has 29 columns including Geo*, MaxMind*, ReverseDNS*, Subnet*. The table is already designed as the unified IP truth. What's missing: the IPInfo range tables that feed enrichment data into it.

**FD27 — Surrogate BIGINT keys for joins.** Already in place — PiXL.IP.IpId is BIGINT IDENTITY with NONCLUSTERED PK, IPAddress is VARCHAR(50) UNIQUE CLUSTERED.

**FD28 — IPInfo range tables = internal plumbing for enrichment.** Deploy `70_IPInfo_Schema.sql` to create IPInfo.GeoRange, IPInfo.AsnRange, IPInfo.ProxyRange, IPInfo.DatacenterRange, plus dimension tables (DataSource, ASN), ImportLog, helper functions, and lookup/enrichment procs.

**FD29 — Deploy 70_IPInfo_Schema.sql + enable IpDataAcquisitionService.** The script was committed (0754394) but never executed. Deploy it now so IpDataAcquisitionService stops failing silently.

**FD30 — Re-enrichment pass after data source updates.** IPInfo.usp_EnrichGeo already exists in the script — it updates PiXL.IP geo columns from range tables. Will be callable after each data import cycle.

**FD31 — Replace MaxMind .mmdb with in-memory range table lookups.** Owner: "Why load the inferior MaxMind file into memory when we can just load the superior IP data into memory?" Create `IpRangeLookupService` that loads IPInfo.GeoRange + IPInfo.AsnRange into sorted arrays at startup and performs binary search lookups. Same result type as MaxMindGeoService, same call pattern, better data.

**FD32 — Hot reload via IpDataAcquisitionService → IpRangeLookupService.ReloadAsync().** Owner: "If the server is online for a year and we do monthly updates and only load the data on launch, we never load the updated IP data." After each data import cycle completes, IpDataAcquisitionService calls `ReloadAsync()` which loads fresh data into new arrays and does an atomic `Interlocked.Exchange` pointer swap. Zero-downtime, no locks needed for reads.

**Answers to Questions:**
- Q1 (stale ParsedBulkInsertService comment): Deferred as a nitpick. Will fix when next touching the file.
- Q2 (ExecuteNonQueryAsync → ExecuteReaderAsync): Deferred. Good improvement but not urgent — dimension counts are logged at the proc level anyway.

---

### F6 — Background IP Enrichment (Lane 3)

**Scope:** `BackgroundIpEnrichmentService.cs` (219 lines) + `DnsLookupService.cs` (176 lines) + `WhoisAsnService.cs` (151 lines) + `BoundedCache.cs` (151 lines)

**What it does:** Lane 3 of the three-lane enrichment architecture. DNS reverse lookups and WHOIS ASN queries run in background I/O workers, off the enrichment hot path. The pipeline calls `Enqueue(ip)` fire-and-forget; background workers populate per-service caches asynchronously. Subsequent records for the same IP hit the cache inline at zero latency via `TryGetCached()`.

#### Architecture

```
PIPELINE HOT PATH (per record, Lane 1):
  1. MaxMind geo/ASN lookup (in-memory, ~1μs)
  2. _backgroundIp.Enqueue(ip)           ← fire-and-forget
  3. _dnsLookup.TryGetCached(ip)         ← 0μs cache read (null on miss)
  4. _whoisAsn.TryGetCached(ip)          ← 0μs cache read (null on miss)

BACKGROUND (Lane 3):
  Channel<string> _ipChannel (50K bounded, DropOldest)
  └─ N workers (default 8, configurable via ForgeSettings.BackgroundIpWorkerCount)
      ├─ DnsLookupService.LookupAsync(ip)     → populates DNS cache (2s timeout)
      └─ WhoisAsnService.LookupAsync(ip)      → populates WHOIS cache (5s timeout)
                                                 (only when MaxMind has CC but no ASN)

CACHE-AHEAD PATTERN:
  1st hit for IP:  cache miss → no _srv_* params → background starts lookup
  2nd+ hit for IP: cache hit  → _srv_* params appended inline at 0 latency
  IP cardinality ~38% unique per 100K → 62% inline cache hit rate from start
```

#### File Inventory

| File | Lines | Purpose | Dependencies |
|------|-------|---------|--------------|
| `BackgroundIpEnrichmentService.cs` | 219 | Lane 3 coordinator: dedup, channel, N workers | ForgeSettings, DnsLookupService?, WhoisAsnService?, MaxMindGeoService?, ITrackingLogger |
| `DnsLookupService.cs` | 176 | Reverse DNS (PTR) + cloud provider pattern detection | DnsClient (NuGet), ITrackingLogger |
| `WhoisAsnService.cs` | 151 | WHOIS ASN/org lookup (supplementary to MaxMind) | Whois (NuGet), ITrackingLogger |
| `BoundedCache.cs` | 151 | Hybrid time+count eviction cache (replaces nuclear Clear()) | None |

**Total: 697 lines across 4 files.**

#### BackgroundIpEnrichmentService — Key Design Points

**Deduplication:** `BoundedCache<string, byte>` (500K max, 250K evict target, 30min age) tracks all IPs already enqueued. Only genuinely new IPs trigger background lookups. Dedup skips are counted via `_duplicatesSkipped` for metrics.

**Channel design:** `Channel.CreateBounded<string>(50_000)` with `DropOldest`. Never blocks the pipeline — if the channel is full, oldest IPs are silently dropped. Tradeoff: dropped IPs won't get cache-warmed, but they'll be looked up next time they appear.

**Private IP filtering:** Enqueue skips `10.*`, `192.168.*`, `127.*`, `172.*` — no external enrichment possible for private IPs.

**Worker model:** N concurrent `Task.Run` workers (default 8, configurable). Each worker reads from the channel via `ReadAllAsync`, performs DNS then conditional WHOIS, and logs progress every 1,000 IPs. Exceptions per-IP are caught and logged as Debug (non-fatal, service continues).

**Conditional WHOIS:** Workers only call WhoisAsnService when MaxMind has a country code but no ASN for that IP. This avoids expensive 5s WHOIS queries for IPs that MaxMind already fully covers.

**Periodic eviction:** 5-minute Timer checks `_seen.Count > MaxEntries` and calls `_seen.Evict()`. BoundedCache uses hybrid strategy: Phase 1 removes entries older than 30min, Phase 2 caps at 250K by timestamp if still over.

**Graceful shutdown:** Workers exit cleanly on cancellation. Shutdown log reports total enqueued/processed/dedup-skipped counts.

#### DnsLookupService — Key Design Points

**Two-tier caching:** Application-level `ConcurrentDictionary<string, DnsLookupResult>` (200K max) + DnsClient internal TTL-based cache. Application cache stores both hits and misses (NXDOMAIN/timeout = default) to prevent repeated 2s DNS timeouts for the same IP.

**Cloud detection:** 7 `[GeneratedRegex]` patterns match cloud provider hostnames: AWS (ec2-*, compute.amazonaws.com), GCP (googleusercontent.com), Azure (cloudapp.azure.com), DigitalOcean, Akamai/Linode, Cloudflare, OVH/Hetzner/Scaleway.

**Params appended:** `_srv_rdns={hostname}`, `_srv_rdnsCloud=1` (if cloud pattern matches).

**Nuclear eviction (open issue):** Still uses `_cache.Clear()` at 200K entries instead of BoundedCache. Causes full cache miss storm across all workers.

#### WhoisAsnService — Key Design Points

**Supplementary enrichment:** Only called when MaxMind has country code but no ASN — the background worker checks `!mmResult.Asn.HasValue && mmResult.CountryCode is not null` before calling WHOIS.

**WHOIS parsing:** `ExtractField()` scans raw WHOIS text for field names (OriginAS, origin, OrgName, org-name, descr) with case-insensitive matching.

**Params appended:** `_srv_whoisASN={as_number}`, `_srv_whoisOrg={organization}`.

**Nuclear eviction (open issue):** Same as DnsLookupService — `_cache.Clear()` at 200K instead of BoundedCache.

#### BoundedCache — Key Design Points

**Purpose:** Replaces nuclear `Clear()` with intelligent eviction. Introduced for BackgroundIpEnrichmentService's dedup cache, but DnsLookupService and WhoisAsnService haven't been migrated yet.

**Hybrid eviction strategy:**
- Phase 1: Remove entries older than `MaxAge` (stale data no longer relevant)
- Phase 2: If still over `MaxEntries`, keep only newest `EvictTarget` entries by timestamp

**Thread-safety:** All operations lock-free via `ConcurrentDictionary`. Concurrent reads/writes continue during eviction. Multiple simultaneous `Evict()` calls are harmless.

**Timestamp mechanism:** `Environment.TickCount64` — monotonic, no DateTime allocation, ~100ns resolution. Stored per entry in the dictionary value tuple.

#### Integration with Enrichment Pipeline

The pipeline integration happens in `EnrichmentPipelineService.EnrichRecord()`:

1. **Line ~360:** `_dnsLookup.TryGetCached(ip)` — cache-only read, appends `_srv_rdns*` on hit
2. **Line ~399:** `_backgroundIp.Enqueue(ip)` — fire-and-forget to Lane 3 channel
3. **Line ~413:** `_whoisAsn.TryGetCached(ip)` — cache-only read, appends `_srv_whois*` on hit (only when MaxMind lacks ASN)

The key design insight: the pipeline **never waits** for DNS/WHOIS results. First record for a new IP gets no `_srv_rdns*`/`_srv_whois*` params. Background workers populate caches asynchronously. Second+ records for the same IP get the params via zero-latency cache hits.

#### Data Flow

**No database interaction.** This subsystem is purely in-memory:
- **Inputs:** External DNS servers (2s timeout), WHOIS servers (5s timeout), MaxMind .mmdb files (in-memory trie)
- **Outputs:** In-memory ConcurrentDictionary caches in DnsLookupService and WhoisAsnService
- **Pipeline reads:** `TryGetCached()` calls during Lane 1 enrichment

#### Observations

| # | Severity | Summary |
|---|----------|---------|
| O1 | **Minor** | DnsLookupService still uses nuclear `Clear()` instead of BoundedCache |
| O2 | **Minor** | WhoisAsnService still uses nuclear `Clear()` instead of BoundedCache |
| O3 | Nitpick | MaxMindGeoService also uses nuclear `Clear()` at 200K |
| O4 | Nitpick | BackgroundIpEnrichmentService dedup cache and service caches have separate eviction |
| O5 | Nitpick | 172.* filter is overly broad — catches non-private 172.0-15.* and 172.32-255.* |
| O6 | Nitpick | No metrics exposed to ForgeMetrics or Pipeline Explorer |
| O7 | Nitpick | WHOIS uses `Task.Run(() => whois.Lookup(...))` — sync-over-async wrapping |

**O1. DnsLookupService nuclear `Clear()`.** [Severity: Minor] At 200K entries, `_cache.Clear()` wipes the entire DNS cache. All workers simultaneously experience cache misses and fire 2s DNS queries. With 8 workers processing at ~0.6 IPs/sec, refilling 200K entries takes hours. The BoundedCache hybrid eviction pattern already exists in the codebase and was built exactly for this scenario. DnsLookupService should use it.

**O2. WhoisAsnService nuclear `Clear()`.** [Severity: Minor] Same issue as O1. At 200K entries, entire WHOIS cache is wiped. WHOIS queries are even slower (5s) so the refill storm is worse than DNS.

**O3. MaxMindGeoService nuclear `Clear()`.** [Severity: Nitpick] Same pattern but lower severity — MaxMind lookups are in-memory trie traversals (~1μs), so the cache miss storm after Clear() costs microseconds per miss rather than seconds. Still, BoundedCache would be a cleaner approach.

**O4. Disconnect between dedup and service caches.** [Severity: Nitpick] The dedup cache (`_seen` in BackgroundIpEnrichmentService) evicts every 5 minutes with BoundedCache hybrid strategy. But DnsLookupService and WhoisAsnService caches use independent nuclear Clear() at 200K. After dedup eviction, an IP might be re-enqueued and the background worker re-queries DNS/WHOIS only to find the result already in the service cache. Or conversely, after a service cache nuke, the dedup cache still considers the IP "seen" and won't re-queue it. In practice this causes no data loss — LookupAsync re-queries on cache miss regardless of dedup state — but it means some IPs that were recently dedup-evicted won't get their DNS/WHOIS refreshed until the dedup cache evicts them.

**O5. Overly broad 172.* private IP filter.** [Severity: Nitpick] `ip.StartsWith("172.")` skips all 172.x.x.x addresses. RFC 1918 private range is only 172.16.0.0–172.31.255.255. IPs like 172.0.0.1 through 172.15.255.255 and 172.32.0.0+ are public and would be incorrectly skipped. At typical traffic volumes this affects a small fraction of IPs. Correct check would be: parse the second octet and only skip if 16–31.

**O6. No ForgeMetrics integration.** [Severity: Nitpick] The service logs progress to ITrackingLogger but doesn't record metrics in ForgeMetrics. Pipeline Explorer has no visibility into Lane 3 queue depth, hit/miss rates, or throughput. The internal `_enqueued`, `_processed`, `_duplicatesSkipped` counters exist but aren't exposed.

**O7. WHOIS sync-over-async.** [Severity: Nitpick] `WhoisAsnService.LookupCoreAsync` wraps the synchronous `whois.Lookup()` in `Task.Run()`. This works (offloads to thread pool) but consumes a thread pool thread per concurrent WHOIS query. With 8 workers each potentially running a 5s WHOIS query, that's 8 thread pool threads blocked. Acceptable given the server has 144 logical processors, but a native async WHOIS library would be cleaner.

#### Questions

**Q1.** O1/O2 — Should we migrate DnsLookupService and WhoisAsnService caches to BoundedCache? The pattern is already proven in BackgroundIpEnrichmentService's dedup cache. This would eliminate nuclear Clear() storms. Straightforward refactor — replace `ConcurrentDictionary` with `BoundedCache`, remove the `if (Count >= Max) Clear()` check, add periodic `Evict()` call.

**Q2.** O5 — Fix the 172.* filter to properly check the RFC 1918 range (172.16.0.0–172.31.255.255)? The same overly broad filter exists in both BackgroundIpEnrichmentService.Enqueue() and WhoisAsnService.LookupAsync().

**Q3.** O6 — Should ForgeMetrics track Lane 3 metrics (queue depth, enqueued, processed, dedup skips)? This would give Pipeline Explorer visibility into background enrichment health.

#### Observation Severity

| # | Severity | Summary |
|---|----------|---------|
| O1 | **Minor** | DNS cache nuclear Clear() — migrate to BoundedCache |
| O2 | **Minor** | WHOIS cache nuclear Clear() — migrate to BoundedCache |
| O3 | Nitpick | MaxMind cache nuclear Clear() — low impact (μs lookups) |
| O4 | Nitpick | Dedup vs service cache eviction disconnect — no data loss |
| O5 | Nitpick | 172.* filter too broad — small fraction of IPs affected |
| O6 | Nitpick | No ForgeMetrics integration — internal counters exist but unexposed |
| O7 | Nitpick | WHOIS sync-over-async — acceptable given server resources |

#### Owner Decisions

**FD33.** O1/O2/O3 — YES, migrate all enrichment caches to BoundedCache. DnsLookupService, WhoisAsnService, and MaxMindGeoService all converted from nuclear `Clear()` to `BoundedCache<TKey, TValue>` with hybrid eviction (200K max, 100K target, 30min age). BackgroundIpEnrichmentService now orchestrates periodic eviction for all service caches on its 5-minute timer.

**FD34.** O4 — RESOLVED. With all service caches on BoundedCache and eviction coordinated from BackgroundIpEnrichmentService, the dedup/service cache disconnect is eliminated.

**FD35.** O5 — YES, fix the 172.* filter. Both `BackgroundIpEnrichmentService.Enqueue()` and `WhoisAsnService.LookupAsync()` now use `IsPrivate172()` which correctly checks RFC 1918 range 172.16.0.0–172.31.255.255 by parsing the second octet.

**FD36.** O6 — YES, integrate ForgeMetrics. Lane 3 now reports: `BgIpEnqueued`, `BgIpProcessed`, `BgIpDupSkipped`, `BgIpDnsLookups`, `BgIpWhoisLookups`, `BgIpChannelDepth`, `BgIpDedupCacheSize`. MetricsReporterService samples depths and the `LANE3-IP` line appears in the 10s metrics log.

**FD37.** O7 — Addressed via SQL-backed WHOIS cache. Added `IPAPI.WhoisCache` table (73_WhoisCache.sql) with upsert/load/cleanup procs. WhoisAsnService pre-warms from SQL on startup (last 30 days) and persists fresh results fire-and-forget. Service restarts no longer require hours of re-querying external WHOIS servers.

**Answers to Questions:**
- Q1: YES — all three service caches migrated to BoundedCache (FD33).
- Q2: YES — 172.* filter corrected to RFC 1918 range (FD35).
- Q3: YES — ForgeMetrics integration complete (FD36).

---

### F7 — Data Sync

**Scope:** 2 service files (~1,100 lines total) + configuration + SQL migrations

| File | Lines | Purpose | Status |
|------|-------|---------|--------|
| `CompanyPiXLSyncService.cs` | ~680 | Xavier Company/Pixel → local PiXL.Company + PiXL.Settings | **ACTIVE** — temporary bridge |
| `IpDataAcquisitionService.cs` | ~450 | Free public IP data → IPInfo schema (IPtoASN, DB-IP) | **ACTIVE** — replacement for retired IpApiSync |

**What it does:** Keeps local reference data synchronized with upstream sources. Two distinct sync paths:

1. **Company/Pixel Sync** — Every 6 hours, pulls Company (511 rows) and PiXL/Settings (5,691 rows) from Xavier (192.168.88.35) via full-table UPDATE+INSERT+DELETE. This is the only remaining dependency on the Xavier production database. Company syncs first (parent FK), then Pixel (child FK). Logs to `IPInfo.ImportLog`.

2. **IP Data Acquisition** — Daily at 01:00 UTC, downloads free public IP data (IPtoASN TSV, DB-IP City Lite CSV) from external URLs. Parses and bulk-loads into staging tables, then does atomic `sp_rename` swap for zero-downtime. Currently imports 441K ASN ranges and 8M geo ranges. Triggers `IpRangeLookupService.ReloadAsync()` after import so enrichment uses fresh data immediately.

#### Architecture

```
COMPANY/PIXEL PATH (every 6h):
  Xavier dbo.Company (511 rows)
    → SqlBulkCopy → #CompanyStaging (temp table)
    → UPDATE + INSERT + DELETE → PiXL.Company
  Xavier dbo.PiXL (5,691 rows)
    → SqlBulkCopy → #PixelStaging (temp table)
    → UPDATE + INSERT + DELETE → PiXL.Settings
  Both logged → IPInfo.ImportLog
  Both metered → ForgeMetrics Lane 4 (DATASYNC)

IP DATA PATH (daily 01:00 UTC):
  iptoasn.com/data/ip2asn-v4.tsv.gz
    → DownloadIfChanged (HTTP If-Modified-Since)
    → Parse TSV → DataTable → SqlBulkCopy → IPInfo.AsnRange_Staging
    → Index → sp_rename atomic swap → IPInfo.AsnRange
  download.db-ip.com/free/dbip-city-lite-YYYY-MM.csv.gz
    → DownloadIfChanged → Parse CSV → DataTable → SqlBulkCopy
    → IPInfo.GeoRange_Staging → atomic swap → IPInfo.GeoRange
  Both logged → IPInfo.ImportLog
  Cycle metered → ForgeMetrics Lane 5 (IPACQ)
  → IpRangeLookupService.ReloadAsync() (hot-reload in-memory)
```

#### File Details

**CompanyPiXLSyncService.cs (~680 lines) — TEMPORARY BRIDGE**
BackgroundService syncing Company and PiXL configuration from Xavier. Key features:
- **UPDATE + INSERT + DELETE:** Both tables are small enough that a complete snapshot + reconcile is simpler and safer than change tracking. Uses `#temp` staging tables + `SqlBulkCopy` to pull, then UPDATE matched rows, INSERT new rows, DELETE removed rows (no MERGE).
- **FK-aware ordering:** Company (parent) always syncs before Pixel (child) to avoid FK violations.
- **Deadlock retry:** `WithDeadlockRetryAsync()` — exponential backoff with random jitter on SQL error 1205, up to 3 attempts.
- **Guard validation:** `GuardTablesExistAsync()` — checks target tables exist before attempting sync (catches broken migrations).
- **Demo row protection:** Both DELETE statements have `WHERE t.CompanyId <> 12344`, protecting the Atlas demo company from being deleted by sync.
- **Email alerts:** Optional `EmailNotificationService` injection for guard failure notifications.
- **Audit logging:** Every sync cycle logs start/completion to `IPInfo.ImportLog` with insert/update/delete counts and duration.
- **ForgeMetrics:** Lane 4 — company/pixel cycle counts, row counts (ins/upd/del), duration, failures.
- **Stagger delay:** 3-minute startup delay to avoid contention with other background services.

**IpDataAcquisitionService.cs (~450 lines) — ACTIVE**
BackgroundService replacing the retired IpApiSyncService. Key features:
- **HTTP conditional download:** Uses `If-Modified-Since` header to skip re-downloads when upstream files haven't changed.
- **SHA-256 dedup:** File-hash comparison prevents redundant imports.
- **Streaming parse:** GZip-decompressed TSV/CSV parsed line-by-line with 100K-row batch flushes to keep memory bounded.
- **Atomic table swap:** Staging table → clustered index → `sp_rename` → drop old. Zero query downtime.
- **ASN dimension upsert:** After AsnRange import, inserts any new ASN numbers into `IPInfo.ASN` dimension table.
- **Hot reload:** After all imports, calls `_ipRangeLookup.ReloadAsync()` so enrichment services use fresh data without restart.
- **Configurable schedule:** `ForgeSettings.IpDataAcquisitionHourUtc` (default 1 = 01:00 UTC).
- **ForgeMetrics:** Lane 5 — cycle counts, ASN/geo/cloud row counts, skipped sources, duration, failures.

#### Configuration

| Setting | Location | Value | Purpose |
|---------|----------|-------|---------|
| `XavierSmartPiXLConnectionString` | `TrackingSettings` | `Server=192.168.88.35;...` | Xavier Company/Pixel source |
| `SyncIntervalHours` | `TrackingSettings` | `6` | Company/Pixel sync frequency |
| `IpDataAcquisitionHourUtc` | `ForgeSettings` | `1` | Daily IP data download hour |
| `IpDataDirectory` | `ForgeSettings` | `IpData` | Local cache directory for downloaded files |

#### SQL Dependencies

| Table/Object | Schema | Purpose |
|-------------|--------|---------|
| `PiXL.Company` | PiXL | Sync target — 511 customer rows from Xavier |
| `PiXL.Settings` | PiXL | Sync target — 5,691 pixel configurations from Xavier |
| `IPInfo.ImportLog` | IPInfo | Audit trail for all sync/import operations |
| `IPInfo.AsnRange` | IPInfo | Live ASN-to-IP-range lookup (441K rows, swap-loaded) |
| `IPInfo.GeoRange` | IPInfo | Live geo-to-IP-range lookup (8M rows, swap-loaded) |
| `IPInfo.ASN` | IPInfo | ASN dimension table (name, org, source) |
| `IPInfo.DataSource` | IPInfo | Reference table for data source metadata |

#### Live Data (2026-03-23)

Recent `IPInfo.ImportLog` entries show both sync paths are healthy:
- Company/Pixel syncing every 6h (~94-107ms Company, ~422-531ms Pixel, 0 errors)
- AsnRange importing daily at 01:00 UTC (~441K rows in ~4s)
- GeoRange: 8M rows loaded (DB-IP Lite monthly)

#### Observations

**O1 (minor) — IpApiSyncService.cs is a dead tombstone file.**
13-line file with only a comment block. The replacement (`IpDataAcquisitionService`) has been active for weeks. The tomb file clutters the Services directory and appears in the F7 index, creating confusion about what's actually running, because IpApiSyncService.cs isn't in Program.cs DI registration at all.

**O2 (minor) — CompanyPiXLSyncService has no ForgeMetrics integration.**
No counters for sync duration, row counts, or failure tracking. The only visibility is log messages and `IPInfo.ImportLog` entries. The Tron dashboard can't show sync health without metrics.

**O3 (nitpick) — IpDataAcquisitionService has no ForgeMetrics integration.**
Same as O2 — import cycle duration, row counts, and skip/failure events are invisible to the dashboard. IPInfo.ImportLog is the only audit trail, but it's not surfaced in real-time metrics.

**O4 (minor) — CompanyPiXLSyncService MERGE updates ALL columns unconditionally.**
Both MERGE statements update every column regardless of whether values changed. For 511 + 5,691 rows every 6 hours this is trivially cheap, but the `RowsUpdated` counter in ImportLog always shows the full table count (510/5690) even when nothing actually changed. This makes "did anything change?" impossible to answer from the log alone.

**O5 (question) — Cloud provider range imports are missing.**
The `IpDataAcquisitionService` header documents AWS, GCP, Azure, and Cloudflare range sources, but `RunAllImportsAsync()` only calls `ImportIpToAsnAsync()` and `ImportDbIpCityLiteAsync()`. The cloud provider ranges are noted as "already loaded by DatacenterIpService at startup" — are they duplicated between in-memory-only and SQL, or is DatacenterIpService the sole source? Should these be imported into `IPInfo.DatacenterRange` for ETL enrichment too?

**O6 (minor) — `TrustServerCertificate=True` in Xavier connection string.**
The `TrackingSettings.cs` comments document this is needed because Xavier's SQL Server 2017 isn't configured to present its custom cert. This is a known security debt item. Is there a plan to fix Xavier's cert configuration, or will the sync be decommissioned before that matters?

**O7 (nitpick) — Two `[Obsolete]` properties in TrackingSettings for retired IPGEO sync.**
`XavierConnectionString` and `IpApiSyncHourUtc` are marked `[Obsolete]` and retained "to avoid config binding errors." If no config file still references them, they can be removed. If config files do reference them, the `[Obsolete]` warning fires during build but removal would cause a runtime exception.

#### Questions for Owner

- **Q1:** Delete `IpApiSyncService.cs` tombstone? It's 13 lines of comments, not registered in DI, and the replacement has been running for weeks.
- **Q2:** Should CompanyPiXLSyncService and IpDataAcquisitionService get ForgeMetrics counters (sync duration, rows, failures) for Tron dashboard visibility? Same pattern as F6 Lane 3 metrics.
- **Q3:** The cloud provider imports (AWS/GCP/Azure/Cloudflare) — should those go into SQL `IPInfo.DatacenterRange` too, or is the `DatacenterIpService` in-memory-only approach sufficient?
- **Q4:** What's the timeline for decommissioning CompanyPiXLSyncService? Is there a front-end replacement project in progress, or is this bridge indefinite?

#### Owner Decisions (FD38–FD47)

**FD38 (O1/O7/Q1) — Delete IpApiSyncService.cs and all IPAPI sync remnants.** "We're done with that test." Deleted the tombstone file, removed `XavierConnectionString` and `IpApiSyncHourUtc` `[Obsolete]` properties from `TrackingSettings.cs`, cleaned stale `IpApiSyncService` references from `IEdgeHealthClient.cs` doc comments.

**FD39 (O2/Q2) — Add ForgeMetrics to CompanyPiXLSyncService.** YES. Lane 4 counters added: company/pixel cycle counts, insert/update/delete row counts, duration ticks, failure counter. Injected via constructor DI.

**FD40 (O3/Q2) — Add ForgeMetrics to IpDataAcquisitionService.** YES — "arguably even more important because this subsystem has several moving parts to monitor." Lane 5 counters added: cycle count, ASN/geo/cloud row counts, skipped sources, duration ticks, failure counter. Import methods changed from `Task` to `Task<int>` to surface row counts.

**FD41 (O4) — Eliminate MERGE everywhere, forever.** "I fucking hate MERGE and I don't want to use it anywhere ever for any reason." Both Company and Pixel MERGE blocks replaced with UPDATE+INSERT+DELETE pattern. Three separate statements with `@@ROWCOUNT` tracking. Semantically equivalent but avoids MERGE race conditions, non-deterministic plans, and deadlock-prone execution. This is a platform-wide ban — no new MERGE statements anywhere.

**FD42 (O5/Q3) — Cloud provider ranges: SQL cold storage, .NET loads into RAM.** Architecture principle: "SQL is cold storage for keeping the data up to date and cached well. But the work is done in RAM on the .NET side." 2TB RAM available, ~500GB on the NUMA node. Cloud provider SQL import deferred — needs more detailed design discussion.

**FD43 (O6) — TrustServerCertificate=True stays until Xavier decommissioned.** No cert fix planned for Xavier. The sync service will be retired before cert configuration matters.

**FD44 (Q4) — CompanyPiXLSyncService timeline.** Months minimum. Owner is sole developer. No formal decommission date; depends on front-end replacement progress.

**FD45 — Hierarchical health tree concept (new).** Owner proposed a composite health check pattern: leaf subsystems report binary 1/0 health, parent nodes aggregate as ratios (e.g., "Data Sync 2/2", "IpDataAcquisition 2/3"). Maps the existing ForgeMetrics + subsystem structure into a standardized tree. ~~Design deferred until F1-F9 walkthroughs complete and the full tree shape is known.~~ → Superseded by FD46.

**FD46 — Health tree is the organizing principle, defined NOW, filled incrementally.** Owner corrected the deferral: "After F8 and F9 there's subsystems 5-15. I don't really get doing this AFTER all that." The tree is a living design document that starts with known structure (F1-F7, Edge, Sentinel surface area) and grows as walkthroughs fill in branches. F8 (Ops & Health) should be *guided by* the tree concept, not walked blind and later restructured. See **Health Tree** section below for the full design.
