---
subsystem: edge
title: PiXL Edge
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - architecture/overview
  - architecture/data-flow
  - subsystems/pixl-script
  - subsystems/bot-detection
  - subsystems/failover
---

# PiXL Edge

## Atlas Public

The PiXL Edge is the front door of SmartPiXL's intelligence platform. It's the component that receives visitor data from your website and processes it in real time.

**What makes it special:**

- **Blazing fast** — responds in under 10 milliseconds, so your website performance is never affected
- **Always analyzing** — performs 12 instant intelligence checks on every single visit before even storing the data
- **Rock-solid reliable** — if any downstream system is temporarily unavailable, the Edge never loses a single data point

When a visitor hits your page, the Edge captures the data, performs instant analysis (datacenter IP detection, fingerprint recognition, geographic lookup), and passes the enriched record downstream for deep analysis — all before the visitor's browser has finished rendering the page.

## Atlas Internal

### What the Edge Does

The Edge is an ASP.NET Core application running inside IIS. It handles two types of requests:

1. **`_SMART.js`** — serves the PiXL tracking script to the visitor's browser
2. **`_SMART.GIF`** — receives the collected data and does something useful with it

When the GIF request arrives, the Edge runs 12 fast enrichment checks before responding. These checks are all in-memory — no external API calls, no database queries. They answer questions like:

- Is this a datacenter IP? (AWS, Google Cloud)
- Have we seen this fingerprint from this IP before? Is it varying suspiciously?
- Is this IP or subnet hitting us unusually fast?
- What country/city is this IP from? (cached lookup)
- Does the visitor's timezone match their IP's geography?

After enrichment, the Edge fires the data to the Forge for deep analysis and returns a 43-byte transparent GIF. Total time: under 10 milliseconds.

### What It Doesn't Do

The Edge does NOT write to the database. That's the Forge's job. The Edge's only two output paths are:
1. Named pipe → Forge (primary)
2. JSONL file on disk (failover if Forge is unavailable)

This separation means the Edge is extremely fast and resilient — it never blocks on database contention or network timeouts.

### Fast Enrichment Summary

| Check | What It Detects | Speed |
|-------|----------------|-------|
| Fingerprint stability | Anti-detect browsers (3+ unique fingerprints from one IP in 24h) | ~1μs |
| IP behavior | Bot farms (3+ IPs from same /24 subnet in 5 min) | ~1μs |
| Rapid-fire detection | Automation (2+ hits from same IP in 15 seconds) | ~1μs |
| Datacenter IP | Cloud-hosted bots (AWS, GCP — 8,500 CIDR ranges) | ~1μs |
| IP classification | Reserved/private/CGNAT/multicast/loopback ranges | ~0.5μs |
| Geo cache | Country, region, city, timezone from IP | ~0.5μs |
| Timezone mismatch | VPN/proxy signals (browser says EST, IP geolocates to Tokyo) | ~0.1μs |

## Atlas Technical

### Hosting Model

The Edge runs **InProcess** inside IIS via `AspNetCoreModuleV2`. This means the .NET application lives inside the `w3wp.exe` process — there's no reverse proxy or inter-process communication with Kestrel. This is critical because:

- InProcess model gives access to the real client IP via `HttpContext.Connection.RemoteIpAddress`
- Out-of-process (Kestrel behind IIS) only sees `127.0.0.1` unless `X-Forwarded-For` is configured
- We learned this the hard way during initial development

The `web.config` must contain `hostingModel="inprocess"` and extended query string limits:
```xml
<requestLimits maxQueryString="16384" maxUrl="8192" />
```

### Endpoint Routing

| Pattern | Handler | Purpose |
|---------|---------|---------|
| `/{company}/{pixl}_SMART.GIF` | `TrackingEndpoints.CaptureAndEnqueue` | Data capture |
| `/{company}/{pixl}_SMART.js` | `TrackingEndpoints.ServeScript` | Script delivery |
| `/internal/health` | `InternalEndpoints` | Health check (Forge/Sentinel probe target) |
| `/internal/circuit-reset` | `InternalEndpoints` | Reset DB circuit breaker |
| `/internal/geo-cache/clear` | `InternalEndpoints` | Flush geo cache |

### Service Architecture

All services are `sealed` singletons registered in DI:

| Service | State | Hot Path? | Purpose |
|---------|-------|-----------|---------|
| `TrackingCaptureService` | Stateless | Yes | Parse HTTP → `TrackingData` (ThreadStatic SB) |
| `FingerprintStabilityService` | Per-IP (IMemoryCache, 24h) | Yes | Fingerprint variation tracking |
| `IpBehaviorService` | Per-IP/subnet (ConcurrentDict) | Yes | /24 velocity + rapid-fire timing |
| `DatacenterIpService` | Global (CidrTrie, weekly refresh) | Yes | AWS/GCP CIDR range matching |
| `IpClassificationService` | Stateless (static method) | Yes | Bitwise IPv4 classification |
| `GeoCacheService` | Per-IP (ConcurrentDict + IMemoryCache) | Yes | Two-tier geo cache → async SQL queue |
| `PipeClientService` | Singleton (Channel + NamedPipe) | Enqueue only | Named pipe writer to Forge |
| `JsonlFailoverService` | Singleton (Channel + FileStream) | Enqueue only | JSONL disk writer (failover) |

### Configuration

From `appsettings.json` under `"Tracking"` section, bound to `IOptions<TrackingSettings>`:

| Key | Default (Dev) | Default (Prod/IIS) | Purpose |
|-----|--------------|-------------------|---------|
| `ConnectionString` | `localhost\SQL2025` | `localhost\SQL2025` | SQL Server (used by GeoCacheService only) |
| `PipeName` | `SmartPiXL-Enrichment` | `SmartPiXL-Enrichment` | Named pipe identifier |
| `FailoverDirectory` | `Failover` | `Failover` | JSONL failover location |

### The 43-Byte GIF

The Edge responds with a pre-allocated `ReadOnlyMemory<byte>` containing a 1x1 transparent GIF89a. Headers include `Cache-Control: no-store` to prevent browser caching (each page load must send fresh data). The response is returned immediately after the enriched record is enqueued — the pipe write and any failover happen asynchronously.

## Atlas Private

### Hot Path Performance Anatomy

The entire `CaptureAndEnqueue` method (~150 lines in `TrackingEndpoints.cs`) is zero-allocation on the hot path. Here's how:

**`TrackingCaptureService`** — Uses `[ThreadStatic] static StringBuilder` for building the output. IIS guarantees one request per thread (InProcess model), so the StringBuilder is safe to reuse without any locking. The service also uses `[GeneratedRegex]` for source-generated regex and `SearchValues<char>` for SIMD-accelerated escape character detection in header JSON serialization.

**`DatacenterIpService`** — The `CidrTrie` is a custom binary prefix trie. IP lookup is O(32) — walk 32 bits, one at a time. The CIDR ranges (~8,500 from AWS and GCP) are downloaded weekly as JSON from AWS's published IP ranges and GCP's cloud.json. A new trie is built in the background and the reference is swapped atomically via `Volatile.Read` / `Interlocked.CompareExchange`. Zero reader-side locking.

**`IpClassificationService`** — Pure static function. Parses IP into 4 bytes using `Span<T>`, then classifies against 16 reserved ranges using integer comparison and bit operations:
- `10.0.0.0/8` → Private
- `172.16.0.0/12` → Private
- `192.168.0.0/16` → Private
- `100.64.0.0/10` → CGNAT
- `127.0.0.0/8` → Loopback
- `169.254.0.0/16` → LinkLocal
- `224.0.0.0/4` → Multicast
- etc.

No allocations, no instances, no branching on strings.

**`GeoCacheService`** — Two-tier design:
1. Hot tier: `ConcurrentDictionary<string, GeoResult>` — lock-free reads, O(1)
2. TTL tier: `IMemoryCache` with 1-hour sliding expiration — eviction
3. Miss path: enqueue IP to `Channel<string>` (single background reader queries `IPAPI.IP` table)

First hit from a new IP returns empty geo (enqueued for async lookup). Second hit gets cached geo. This means the first visit from a new IP won't have geo data — an acceptable trade-off for zero blocking on the hot path.

**Known fragility**: The ConcurrentDictionary hot tier has no size limit. Under a DDoS or scraping attack with unique IPs, it grows unbounded. A periodic pruning strategy (remove entries not accessed in 2+ hours) should be added.

### `PipeClientService` Connection Lifecycle

1. On startup: attempt `NamedPipeClientStream.ConnectAsync(1000ms timeout)`
2. On success: JSON-line-serialize each record from the channel, write + flush to pipe
3. On pipe break: set `_nextReconnectAttempt` with exponential backoff (1s, 2s, 4s, 8s, 16s, 30s cap)
4. During backoff window: all channel records are redirected to `JsonlFailoverService`
5. After backoff expires: attempt reconnect. If success → resume pipe writes. If fail → backoff again.
6. On shutdown (`StopAsync`): drain remaining channel items to pipe if connected, otherwise to JSONL.

The workplan originally specified `ValueTask EnqueueAsync` (direct pipe write on request thread) but this was changed to `bool TryEnqueue` backed by `Channel<T>` — a lock-free CAS operation with zero allocation, consistent with the hot path philosophy. The background loop handles serialization and I/O.

### `DatabaseWriterService` — Orphaned

`DatabaseWriterService.cs` still exists in the project but is NOT registered in DI. The Edge never writes to SQL directly. When the design was finalized (Phase 3), the owner explicitly stated: "Edge should NEVER write to SQL directly. Its only two paths are pipe-to-Forge and JSONL-failover-to-disk." The file can be deleted; it's kept for reference only.

### Deployment Gotchas

1. `dotnet publish` **overwrites `web.config`** — must verify `hostingModel="inprocess"` and `maxQueryString="16384"` after every publish
2. IIS `appsettings.json` uses ports **6000/6001** (Kestrel internal). Dev uses **7000/7001**. Mixing these causes IIS to bind on the wrong port and fail silently.
3. The IIS app pool identity (`IIS APPPOOL\Smartpixl.info`) needs a SQL Server login with `db_datareader`, `db_datawriter`, and `EXECUTE` on the `SmartPiXL` database — even though the Edge doesn't write to Raw, the `GeoCacheService` reads from `IPAPI.IP`.
4. `EdgeBaseUrl` in Forge and Sentinel configs must be `http://192.168.88.176` (IIS port 80), NOT `http://127.0.0.1:6000` — because InProcess hosting means Kestrel ports aren't exposed externally.
