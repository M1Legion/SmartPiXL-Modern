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
| 1 | **Edge HTTP capture** (SmartPiXL project) | **IN PROGRESS** | — |
| 2 | PiXL Script (browser JavaScript) | not started | — |
| 3 | Named pipe transport + failover | not started | — |
| 4 | Forge pipeline (channels, workers, SQL write) | not started | — |
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

### Owner Decisions

| File | Decision | Notes |
|------|----------|-------|
| Program.cs | — | |
| appsettings.json | — | |
| TrackingEndpoints.cs | — | |
| InternalEndpoints.cs | — | |
| TrackingCaptureService.cs | — | |
| PipeClientService.cs | — | |
| JsonlFailoverService.cs | — | |
| DatabaseWriterService.cs | — | **722 lines, not registered, dead code** |
| DatacenterIpService.cs | — | |
| CidrTrie.cs | — | |
| IpClassificationService.cs | — | |
| IpBehaviorService.cs | — | |
| FingerprintStabilityService.cs | — | |
| GeoCacheService.cs | — | |
