---
subsystem: data-flow
title: Data Flow & Request Lifecycle
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - architecture/overview
  - architecture/edge
  - architecture/forge
  - subsystems/pixl-script
  - subsystems/enrichment-pipeline
  - subsystems/failover
---

# Data Flow & Request Lifecycle

## Atlas Public

When a visitor loads any page on your website, SmartPiXL captures a complete picture of that visit in under half a second — invisibly, silently, and without any impact on page performance.

Here's what happens:

1. **The page loads normally.** SmartPiXL's lightweight script runs quietly in the background while your visitor browses. It gathers over 200 data points from the browser — screen size, device type, language preferences, and behavioral signals like mouse movement patterns.

2. **Data is sent instantly.** The collected information is transmitted as a single tiny image request (just 43 bytes). Your visitor never sees it, and it doesn't slow down their experience.

3. **Intelligence is added in real time.** Our servers immediately enrich the raw data with geographic location, device identification, bot detection signals, and behavioral analysis. This all happens automatically — there's nothing to configure.

4. **Results are available immediately.** Within seconds, the enriched visitor record is in your dashboard, ready for analysis. You can see who visited, where they're from, what device they used, and whether they're likely a real person or automated traffic.

**The entire process is invisible to visitors and adds zero perceptible latency to your website.**

## Atlas Internal

### How Data Moves Through SmartPiXL

The data pipeline has four stages. Understanding these stages helps you answer customer questions about timing, accuracy, and data availability.

**Stage 1: Browser Collection (~80-500ms)**

A small JavaScript file runs in the visitor's browser and collects 159 named data fields. Most fields are captured instantly (screen size, browser info, features), but some require a brief async window — audio fingerprinting, storage estimates, battery level. The script uses a 500ms ceiling, but most data is ready in under 100ms.

**Stage 2: Fast Enrichment (<10ms)**

When the data arrives at our web server (Edge), 12 quick enrichment checks run before we even respond to the browser:
- Is this a known datacenter IP? (AWS, GCP)
- Have we seen this fingerprint before?
- Is this IP hitting us too fast? (bot behavior)
- What country/city is this IP from? (cached lookup)
- Does the browser's timezone match the IP's geography?

These are all in-memory checks — no external API calls, no database queries on the critical path.

**Stage 3: Deep Enrichment (async, 1-5 seconds)**

The enriched record is passed to our Forge engine for heavyweight analysis:
- Reverse DNS lookup — is the IP from a residential ISP or a cloud server?
- MaxMind GeoIP2 — precise offline geographic lookup
- Bot database check — 10,000+ known bot user-agent patterns
- User-agent parsing — structured browser/OS/device identification
- Cross-customer intelligence — is this visitor hitting multiple SmartPiXL customers simultaneously?
- Behavioral analysis — mouse movement patterns, cultural fingerprint consistency

**Stage 4: Database & ETL (every 60 seconds)**

Fully enriched records land in our raw data table. Every 60 seconds, the ETL pipeline parses each record into 300+ typed columns and updates our dimensional model (Device, IP, Visit, Match tables). This is where identity resolution happens — linking anonymous visitors to known contacts.

### Timing Summary

| Stage | When | Duration | Visitor Impact |
|-------|------|----------|---------------|
| Browser collection | On page load | 80-500ms | None |
| Fast enrichment | On data receipt | <10ms | None |
| Deep enrichment | After response | 1-5 seconds | None (async) |
| ETL parsing | Every 60 seconds | Batch | None |

### Data Durability

If the Forge engine is temporarily unavailable (restart, update, crash), data is written to files on disk. When the Forge comes back, it catches up automatically. **There is zero data loss under any failure scenario.**

## Atlas Technical

### Request Lifecycle

```
Browser                  Edge (IIS w3wp.exe)              Forge (Windows Service)         SQL Server 2025
  │                            │                                    │                          │
  ├──GET _SMART.js────────────►│                                    │                          │
  │◄──PiXL Script JS──────────┤                                    │                          │
  │                            │                                    │                          │
  │  [500ms collection window] │                                    │                          │
  │  159 fields collected      │                                    │                          │
  │                            │                                    │                          │
  ├──GET _SMART.GIF?data=...──►│                                    │                          │
  │                            ├──TrackingCaptureService.Parse()     │                          │
  │                            ├──FingerprintStabilityService        │                          │
  │                            ├──IpBehaviorService                  │                          │
  │                            ├──DatacenterIpService (CidrTrie)     │                          │
  │                            ├──IpClassificationService            │                          │
  │                            ├──GeoCacheService                    │                          │
  │                            ├──TZ mismatch check                  │                          │
  │                            ├──Build _srv_* params                │                          │
  │                            │                                    │                          │
  │◄──43-byte GIF──────────────┤                                    │                          │
  │                            │                                    │                          │
  │                            ├──PipeClientService.TryEnqueue()     │                          │
  │                            │     │                               │                          │
  │                            │     ├──NamedPipe ──────────────────►│                          │
  │                            │     │   (JSON line)                 ├──Tier 1: BotUA, UA Parse │
  │                            │     │                               ├──Tier 1: DNS, MaxMind    │
  │                            │     │                               ├──Tier 1: IPAPI, WHOIS    │
  │                            │     │                               ├──Tier 2: CrossCustomer   │
  │                            │     │                               ├──Tier 2: Sessions        │
  │                            │     │                               ├──Tier 2: Affluence       │
  │                            │     │                               ├──Tier 2: Lead Score      │
  │                            │     │                               ├──Tier 3: Contradictions  │
  │                            │     │                               ├──Tier 3: Cultural        │
  │                            │     │                               ├──Tier 3: DeviceAge       │
  │                            │     │                               ├──Tier 3: Replay          │
  │                            │     │                               ├──Tier 3: DeadInternet    │
  │                            │     │                               │                          │
  │                            │     │                               ├──SqlBulkCopy────────────►│ PiXL.Raw
  │                            │     │                               │                          │
  │                            │     │                               │  [Every 60s]             │
  │                            │     │                               ├──ETL.usp_ParseNewHits───►│ PiXL.Parsed
  │                            │     │                               │                          │ PiXL.Device
  │                            │     │                               │                          │ PiXL.IP
  │                            │     │                               │                          │ PiXL.Visit
  │                            │     │                               ├──ETL.usp_MatchVisits────►│ PiXL.Match
```

### Failover Path

```
Edge                         Disk                        Forge
  │                            │                            │
  ├──Pipe unavailable──────────│                            │
  ├──JsonlFailoverService      │                            │
  │    TryEnqueue()            │                            │
  │     │                      │                            │
  │     ├──Write JSON line────►│ Failover/                  │
  │     │                      │ failover_yyyy_MM_dd.jsonl  │
  │                            │                            │
  │                            │    [Forge restarts]        │
  │                            │                            │
  │                            │◄──FailoverCatchupService───┤
  │                            │   Reads, processes,        │
  │                            │   archives files           │
  │                            │                            │
  │                            │                     ──────►│ PiXL.Raw
```

### Data Record Structure

The `TrackingData` record flows through the entire pipeline as a sealed record with 9 fields:

| Field | Type | Source |
|-------|------|--------|
| `CompanyID` | string | URL path segment (e.g., `/ACME/page_SMART.GIF` → `ACME`) |
| `PiXLID` | string | URL path segment (e.g., `/ACME/page_SMART.GIF` → `page`) |
| `IPAddress` | string | `HttpContext.Connection.RemoteIpAddress` |
| `RequestPath` | string | Full URL path |
| `QueryString` | string | All 159 data fields + `_srv_*` enrichments as key=value pairs |
| `HeadersJson` | string | Serialized request headers (JSON) |
| `UserAgent` | string | `User-Agent` header |
| `Referer` | string | `Referer` header |
| `ReceivedAt` | DateTime | UTC timestamp of receipt |

The QueryString grows as each enrichment service appends `_srv_*` parameters. By the time it reaches SQL, it contains both the 159 browser-collected fields and all server-side enrichments.

### Channel Architecture

The Forge uses two bounded `Channel<TrackingData>` instances (wrapped in `ForgeChannels`):

1. **Enrichment Channel** — `PipeListenerService` → `EnrichmentPipelineService`
   - Capacity: configurable via `ForgeSettings.PipeChannelCapacity`
   - Full mode: `DropOldest`

2. **SQL Writer Channel** — `EnrichmentPipelineService` → `SqlBulkCopyWriterService`
   - Capacity: configurable via `ForgeSettings.SqlWriterChannelCapacity`
   - Full mode: `DropOldest`

### ETL Pipeline

Two stored procedures run every 60 seconds via `EtlBackgroundService`:

1. **`ETL.usp_ParseNewHits`** — 13-phase watermark-driven batch:
   - Phase 1: INSERT from PiXL.Raw → PiXL.Parsed (screen, navigator, canvas fields)
   - Phase 2-7: UPDATE passes for WebGL, audio, fonts, network, storage, performance, etc.
   - Phase 8: IP classification and geo enrichment
   - Phase 8B: Tier 1 `_srv_*` params (bot, UA parse, DNS, MaxMind, WHOIS)
   - Phase 8C: Tier 2 `_srv_*` params (cross-customer, lead score, sessions, affluence)
   - Phase 8D: Tier 3 `_srv_*` params (contradictions, cultural, device age, replay, dead internet)
   - Phase 9-13: Bot scoring, evasion, cross-signals, behavior, dimensional updates

2. **`ETL.usp_MatchVisits`** — Identity resolution against AutoConsumer

## Atlas Private

### Why the QueryString is the Carrier

All enrichment data rides in `TrackingData.QueryString` as `_srv_*` key-value pairs rather than in separate fields on the TrackingData record. This was a deliberate architectural decision:

- **PiXL.Raw stays at 9 columns** — `SqlBulkCopy` column mapping is hardcoded by ordinal. Adding columns would require syncing the custom `DbDataReader`, the `SqlBulkCopy` mapping, and the table schema. The QueryString approach means the Forge just appends text.
- **ETL is the parser** — `dbo.GetQueryParam()` extracts any parameter by name. Adding a new enrichment means: append `_srv_foo=bar` in the Forge → add `dbo.GetQueryParam(qs, '_srv_foo')` to the ETL proc → add the column to PiXL.Parsed. No C# changes to the writer.
- **Backward compatibility** — old records (pre-Forge enrichment) simply don't have the `_srv_*` params. `GetQueryParam` returns NULL. No migration needed for historical data.

The trade-off: QueryStrings can get long (4-8KB with full enrichment). The `maxQueryString="16384"` in `web.config` and `VARCHAR(MAX)` on `PiXL.Raw.QueryString` handle this, but log files become verbose.

### `PipeClientService` Internals

The pipe client uses `Channel<TrackingData>` with `BoundedChannelFullMode.DropOldest` for the hot path — `TryEnqueue` is a lock-free CAS operation, no blocking. The background loop drains the channel:

1. Read from channel
2. Serialize to UTF-8 JSON via `System.Text.Json`
3. Write to `NamedPipeClientStream`
4. On pipe failure: exponential backoff (1s → 2s → 4s → ... → 30s cap)
5. During backoff: all records go to `JsonlFailoverService`
6. On shutdown: drain pipe first, JSONL failover for remainder

The workplan originally specified `ValueTask EnqueueAsync()` but the implementation uses `bool TryEnqueue()` — consistent with the zero-allocation hot path philosophy and the existing `DatabaseWriterService.TryQueue` pattern.

### `SqlBulkCopyWriterService` Internals

Uses a custom `DbDataReader` implementation (not `DataTable`) for `SqlBulkCopy`. The reader exposes the 9 PiXL.Raw columns by ordinal number. This avoids all intermediate allocation — no DataRow, no boxing of column values. The writer batches records from the channel and writes in configurable batch sizes.

Circuit breaker pattern: if SQL is down, records go to a dead-letter JSONL file. When the circuit resets (configurable timeout), writing resumes.

### ETL Performance Characteristics

`usp_ParseNewHits` processes ~10,000 rows per cycle under normal load. Each phase uses `dbo.GetQueryParam()` — a scalar UDF called once per parameter per row. With 300+ columns, that's 300+ UDF calls per row. At scale (14M+ raw rows), the watermark-based batch limit (`@BatchSize = 10000`) prevents runaway execution.

Known issue: SQL Server's optimizer doesn't inline scalar UDFs well. The ETL proc's Phase 1 (INSERT with 40+ GetQueryParam calls) is the bottleneck. A future optimization would be to replace `GetQueryParam` with `OPENJSON` on a JSON-formatted query string, or use `STRING_SPLIT` with a custom parser. But the current approach works at our throughput and changing it risks the stability of 13 interdependent phases.
