---
subsystem: architecture-overview
title: System Architecture
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - architecture/data-flow
  - architecture/edge
  - architecture/forge
  - architecture/sentinel
---

# System Architecture

## Atlas Public

SmartPiXL is a next-generation visitor intelligence platform that identifies and enriches website visitor data in real time — without cookies. When someone visits your website, SmartPiXL instantly captures over 200 data points about their device, browser, and behavior, then enriches that data with geographic, corporate, and behavioral intelligence.

The result: you know who's visiting, where they're from, what device they're using, whether they're a real person or a bot, and whether you've seen them before — all within milliseconds, all without disrupting their experience.

**Key capabilities:**
- **Cookieless identification** — works even when visitors block cookies or use privacy tools
- **Real-time enrichment** — geographic, behavioral, and device intelligence added instantly
- **Bot detection** — over 80 signals distinguish real visitors from automated traffic
- **Cross-session tracking** — recognize returning visitors across multiple visits
- **Zero visitor impact** — invisible to the end user, no performance degradation

## Atlas Internal

SmartPiXL runs as three separate processes that work together:

### The Three Processes

1. **PiXL Edge** (the web server) — This is the front door. When a visitor loads a page with our tracking pixel, the Edge server receives the data, performs quick checks (is this IP from a datacenter? have we seen this fingerprint before?), and returns a tiny invisible image. Response time: under 10 milliseconds. The visitor never notices.

2. **SmartPiXL Forge** (the enrichment engine) — This is the brain. The Edge passes visitor data to the Forge through an internal connection. The Forge takes its time — it looks up the visitor's IP in geolocation databases, checks if the user agent matches a known bot, performs DNS lookups, and runs behavioral analysis. When it's done, it writes the fully enriched record to the database.

3. **SmartPiXL Sentinel** (the dashboard) — This is the face. Sentinel serves the Tron operations dashboard (for us) and the Atlas portal (for customers). It reads from the enriched database and presents the data visually. Currently in development (Phase 10).

### Data Durability

If the Forge goes down temporarily, the Edge doesn't lose data. It writes records to files on disk and the Forge catches up when it restarts. Zero data loss under any failure scenario.

### Current Status

| Component | Status |
|-----------|--------|
| PiXL Edge | Live, serving production traffic |
| SmartPiXL Forge | Built, Phase 2 complete |
| SmartPiXL Sentinel | Planned for Phase 10 |
| Database | Live, SQL Server 2025 |

## Atlas Technical

SmartPiXL is a 3-process .NET 10 application targeting SQL Server 2025:

### Process Architecture

| Process | Project | Runtime | Hosting | Purpose |
|---------|---------|---------|---------|---------|
| **PiXL Edge** | `SmartPiXL/` | ASP.NET Core (InProcess) | IIS `w3wp.exe` | HTTP pixel capture, 12 fast enrichments |
| **SmartPiXL Forge** | `SmartPiXL.Forge/` | Worker Service | Windows Service | Named pipe server, Tier 1-3 enrichments, ETL, SQL writer |
| **SmartPiXL Sentinel** | `SmartPiXL.Sentinel/` | ASP.NET Core | Windows Service (port 7500) | Tron ops, Atlas portal (Phase 10) |
| **Shared Library** | `SmartPiXL.Shared/` | Class Library | Referenced by all | Models, configuration, interfaces (zero NuGet deps) |

### Inter-Process Communication

```
Edge → NamedPipeClientStream("SmartPiXL-Enrichment") → Forge NamedPipeServerStream
  Payload: TrackingData record serialized as JSON line
  Failover: JSONL file to Failover/ directory if pipe unavailable
  Catch-up: FailoverCatchupService reads JSONL on Forge restart
```

### Request Pipeline (Edge)

The Edge request pipeline is zero-allocation on the hot path:

1. `TrackingEndpoints.CaptureAndEnqueue()` — receives `_SMART.GIF` request
2. `TrackingCaptureService` — parses HTTP request via `Span<T>`, builds `TrackingData`
3. 12 fast enrichments (~5μs total):
   - `FingerprintStabilityService` — per-IP fingerprint variation detection
   - `IpBehaviorService` — subnet /24 velocity + rapid-fire timing
   - `DatacenterIpService` — AWS/GCP/Azure CIDR range matching
   - `IpClassificationService` — 12-category IP classification
   - `GeoCacheService` — two-tier in-memory geo cache
4. `PipeClientService.TrySend()` → named pipe to Forge (or JSONL failover)
5. Return 43-byte transparent GIF

### Enrichment Pipeline (Forge)

Records flow through `Channel<TrackingData>` queues:

```
PipeListener → Channel → EnrichmentPipeline → Channel → SqlBulkCopyWriter → PiXL.Raw
```

Enrichment tiers:
- **Tier 1** (library-based): NetCrawlerDetect, UAParser, DnsClient, MaxMind, IPAPI, WHOIS
- **Tier 2** (cross-request): Cross-customer intel, lead scoring, session stitching, affluence
- **Tier 3** (asymmetric): Cultural arbitrage, device age, contradiction matrix, behavioral replay

### Database Schema

| Schema | Tables | Purpose |
|--------|--------|---------|
| `PiXL` | Raw, Parsed, Device, IP, Visit, Match, Config, Company, Pixel, SubnetReputation | Domain |
| `ETL` | Watermark, MatchWatermark + stored procedures | Pipeline |
| `IPAPI` | IP (342M+ rows) | Geolocation |
| `TrafficAlert` | VisitorScore, CustomerSummary | Scoring |
| `Graph` | Device, Person, IpAddress (NODE + EDGE) | Identity resolution |
| `Geo` | Zipcode (Census ZCTA polygons) | Geographic |

### Port Assignments

| Instance | HTTP | HTTPS |
|----------|------|-------|
| IIS (Production) | 6000 | 6001 |
| Dev (`dotnet run`) | 7000 | 7001 |
| Sentinel | 7500 | — |

## Atlas Private

### Why Three Processes?

The split exists because of timing budgets. The Edge must return in <10ms — it can't wait for DNS lookups (~50ms), IPAPI calls (~200ms), or WHOIS queries (~500ms). The Forge has unlimited time. The Sentinel doesn't process data at all — it just reads and displays.

The named pipe was chosen over HTTP because it's same-machine IPC with zero network overhead. `NamedPipeServerStream` / `NamedPipeClientStream` with JSON lines gives us simple, debuggable, and durable communication. The JSONL failover ensures zero data loss even if the Forge crashes — records queue on disk and get replayed.

### Hot Path Internals

`TrackingCaptureService` uses a `[ThreadStatic]` StringBuilder to avoid allocations. This is safe because IIS request threads are the only callers. If someone ever adds a `Task.Run` that calls the capture service, it'll corrupt the StringBuilder silently. The service also uses `SearchValues<char>` for SIMD-accelerated character scanning during header JSON escaping.

`DatacenterIpService` does lock-free reference swaps via `Volatile.Read` + `Interlocked.CompareExchange`. It downloads AWS/GCP CIDR ranges weekly and builds a new `CidrTrie` data structure, then atomically swaps the reference. No locks on the read path — every request checks datacenter ranges with zero contention.

`IpClassificationService.Classify()` is a pure static function with zero instance state. It classifies IPs into 12 categories using bit manipulation and range comparisons on the raw IP bytes. No memory allocation, no branching on strings.

### Known Fragilities

- **ThreadStatic StringBuilder** — safe only because of IIS threading model. Would break under `Task.Run` callers.
- **FingerprintStabilityService** uses `IMemoryCache` with 1-hour sliding expiration. Fingerprint variation detection resets every hour. Fine for bot detection, insufficient for long-session tracking.
- **GeoCacheService** two-tier cache (ConcurrentDictionary + IMemoryCache) can grow unbounded in the ConcurrentDictionary tier during traffic spikes. Needs periodic pruning or a bounded eviction policy.
- **ForgeChannels** uses `BoundedChannelFullMode.DropOldest` — under extreme load, the oldest records are silently dropped. This is a conscious trade-off: back-pressure without blocking the Edge. But it means data loss is theoretically possible under sustained overload.

### Build & Deployment Notes

- `dotnet publish` **clobbers web.config** — always verify `hostingModel="inprocess"` and `maxQueryString="16384"` after publish
- IIS `appsettings.json` uses ports 6000/6001, dev uses 7000/7001 — mixing these causes silent failures
- The IIS app pool identity (`IIS APPPOOL\Smartpixl.info`) needs SQL login with `db_datareader`, `db_datawriter`, and `EXECUTE` permissions
- Worker-Deprecated is in the solution as read-only reference. It has build errors from the namespace rename (intentional). Delete in Phase 10.
