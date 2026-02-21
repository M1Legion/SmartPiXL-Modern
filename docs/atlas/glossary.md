---
subsystem: glossary
title: SmartPiXL Glossary
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - architecture/overview
---

# Glossary

## Atlas Public

| Term | Meaning |
|------|---------|
| **SmartPiXL** | M1's visitor intelligence platform — identifies and enriches website visitor data in real time without cookies |
| **Pixel** | A tiny invisible image embedded on a webpage that triggers data collection when a visitor loads the page |
| **Visitor** | Anyone who loads a page containing a SmartPiXL pixel |
| **Enrichment** | Additional intelligence added to visitor data — geography, device type, behavior analysis |
| **Bot** | Automated software that visits websites. SmartPiXL detects and flags bot traffic. |
| **Fingerprint** | A unique device identifier derived from browser and hardware characteristics — no cookies required |
| **Traffic Alert** | A notification when unusual patterns are detected in a customer's visitor traffic |

## Atlas Internal

| Term | Meaning |
|------|---------|
| **Data point** | One atomic piece of information about a visitor. Indivisible. Example: `screen width = 1920 pixels` |
| **Field** | A named key in the data. A field may carry a single data point or multiple packed together. Example: `botSignals=webdriver,selenium,cdp` is one field containing 3 data points |
| **Signal** | A named indicator that informs a detection decision. Each signal has a score weight. Example: `webdriver` (+10 points) |
| **Enrichment** | Data added server-side (not from the browser). Enrichments are prefixed with `_srv_` internally. Example: country code from IP lookup |
| **Score** | A weighted integer accumulated from individual signals. Not raw data — it's derived. Example: `botScore=35` |
| **PiXL Script** | JavaScript code that runs in the visitor's browser and collects 159 data fields |
| **Edge** | The web server that receives pixel hits (under 10ms per request) |
| **Forge** | The enrichment engine that adds intelligence to raw visitor data |
| **Sentinel** | The dashboard service (under development) |
| **DeviceHash** | A composite identifier for a device — combines canvas, WebGL, audio, GPU, and platform data |
| **ETL** | Extract-Transform-Load — the process that converts raw data into structured, queryable records |

## Atlas Technical

| Term | Meaning | Implementation |
|------|---------|---------------|
| **Data point** | One atomic piece of information about a visitor | A single `data.*` assignment in PiXLScript.cs |
| **Field** | A named key sent in the query string | `data.sw`, `data.botSignals` — 159 total fields |
| **Signal** | Named indicator with a score weight inside composite fields | `webdriver` inside `botSignals`, weight defined in PiXL Script |
| **Column** | A SQL column. "Field" and "column" are interchangeable in SQL context | `PiXL.Parsed.ScreenWidth` |
| **Enrichment** | Server-side data point, prefixed `_srv_` in query string | `_srv_geoCC=US` appended by Edge/Forge |
| **Score** | Derived integer from signal weights | `botScore = sum(signal weights)` |
| **PiXL.Raw** | 9-column raw capture table — `SqlBulkCopy` target | `CompanyID, PiXLID, IPAddress, RequestPath, QueryString, HeadersJson, UserAgent, Referer, ReceivedAt` |
| **PiXL.Parsed** | 300+ column parsed table — ETL output | One row per hit, all fields extracted from QueryString |
| **DeviceHash** | SHA-256 of 5 fingerprint fields | `HASHBYTES('SHA2_256', CanvasFP + WebGlFP + AudioFP + WebGlRenderer + Platform)` |
| **Channel\<T\>** | `System.Threading.Channels` bounded producer-consumer queue | `BoundedChannelFullMode.DropOldest` for back-pressure |
| **JSONL** | JSON Lines format — one JSON object per line | Used for failover files and pipe communication |
| **Named pipe** | `NamedPipeServerStream` / `NamedPipeClientStream` IPC | Pipe name: `SmartPiXL-Enrichment` |
| **Watermark** | ETL pattern tracking last processed row ID | `ETL.Watermark.LastProcessedId` |

## Atlas Private

All terms from Technical tier plus:

| Term | Meaning | Notes |
|------|---------|-------|
| **ThreadStatic SB** | `[ThreadStatic]` StringBuilder in `TrackingCaptureService` | Per-thread reuse for zero-alloc. NOT safe from `Task.Run` callers. |
| **CidrTrie** | Radix trie for CIDR range matching in `DatacenterIpService` | Custom implementation, `stackalloc`-based lookup |
| **SearchValues\<char\>** | .NET 10 SIMD-accelerated character set search | Used in header JSON escaping for escape character detection |
| **Volatile.Read swap** | Lock-free reference swap pattern | `DatacenterIpService` — publish new trie, atomically swap reference |
| **ForgeChannels** | DI wrapper holding two `Channel<TrackingData>` instances | `Enrichment` (pipe→enrichment) and `SqlWriter` (enrichment→SQL) |
| **Xavier** | Legacy server `192.168.88.35` running SQL 2017 | Source of IPAPI geo data and Company/Pixel configs. Temporary sync target. |
| **AutoConsumer** | Xavier's legacy visitor-to-contact matching system | Source of `PiXL.Match` data (emails, names, addresses) |
| **Worker-Deprecated** | Old background service project, now read-only | Kept as reference for porting to Forge. Delete in Phase 10. |
| **DropOldest** | `BoundedChannelFullMode.DropOldest` — silently drops oldest item when channel is full | Conscious trade-off: back-pressure without blocking Edge. Theoretical data loss under extreme load. |
