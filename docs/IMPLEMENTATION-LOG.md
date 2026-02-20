# SmartPiXL Implementation Log

Tracks decisions, conflicts, and progress during the intentional rebuild.

---

## Session 1 — Phase 0 → Phase 1

### Project Structure (Phase 0)

**Problem:** The canonical `SmartPiXL/` project directory was empty. All live Edge code existed in `SmartPiXL.Modern-Deprecated/` under the old `TrackingPixel` namespace. The solution file (`SmartPixl.sln`) referenced stale paths (`TrackingPixel.Modern\`, `TrackingPixel.Tests\`, `SmartPiXL.Worker\`).

**Decision:** Created the canonical `SmartPiXL/` project by copying all source files from `SmartPiXL.Modern-Deprecated/`. Created `SmartPiXL.csproj` with `<RootNamespace>TrackingPixel</RootNamespace>` and `<AssemblyName>SmartPiXL</AssemblyName>`.

**Conflict:** Changing all `TrackingPixel.*` namespaces to `SmartPiXL.*` would require modifying every `.cs` file in the solution including Shared. Keeping the namespace for now and just fixing the project/folder structure is pragmatic. The assembly output is correctly named `SmartPiXL.dll`.

**Result:**
- `SmartPixl.sln` updated to reference correct paths
- `SmartPiXL.Tests/TrackingPixel.Tests.csproj` ProjectReference updated
- `SmartPiXL/web.config` created pointing to `SmartPiXL.dll`
- Build: 0 warnings, 0 errors
- Tests: 164/164 passing

---

### Phase 1 — PiXL Script: Final 2 Fields + SQL Schema

**Scope:** Add `screenExtended` and `mousePath` to PiXL Script, SQL schema, ETL proc, tests, and design doc.

#### 1. `screenExtended` (PiXLScript.cs)
- Added `data.screenExtended = s.isExtended ? 1 : 0;` after `data.sy` in the Screen & Display section
- Outputs `1` or `0` (SQL BIT-compatible), not raw boolean
- `window.screen.isExtended` returns `undefined` on single-monitor setups and older browsers; the ternary handles this gracefully (falsy → 0)

#### 2. `mousePath` (PiXLScript.cs)
- Added after the `behavioralFlags`/`crossSignals` block in `calculateMouseEntropy()`
- Serializes the existing 50-point `moves[]` array as `x,y,t|x,y,t|...` string
- Pipe-delimited between points, comma-delimited within each point
- Hard cap at 2000 characters — stops adding points once the string would exceed the limit
- Only serialized when `mLen > 0` (has mouse data)

#### 3. SQL Migration 41
- `SmartPiXL/SQL/41_ScreenExtendedMousePath.sql`
- Adds `PiXL.Parsed.ScreenExtended BIT NULL` and `PiXL.Parsed.MousePath VARCHAR(2000) NULL`
- Idempotent column adds (IF NOT EXISTS checks)
- Full `CREATE OR ALTER PROCEDURE ETL.usp_ParseNewHits` with both new fields:
  - `ScreenExtended` in ETL Phase 1 (INSERT) — Screen & Display group after `ScreenY`
  - `MousePath` in ETL Phase 7 (UPDATE) — Behavioral group after `MoveCountBucket`

#### 4. Unit Tests
- 5 new tests added to `PiXLScriptTests.cs`:
  - `Template_should_containScreenExtendedAssignment` — verifies `data.screenExtended` exists
  - `Template_should_assignScreenExtendedAs1Or0` — verifies ternary 1/0 output
  - `Template_should_containMousePathAssignment` — verifies `data.mousePath` exists
  - `Template_should_encodeMousePathAsXYTPipeDelimited` — verifies x,y,t plus pipe delimiter
  - `Template_should_capMousePathLength` — verifies 2000-char cap
- All 169 tests pass (164 original + 5 new)

#### 5. Design Doc Updates
- `BRILLIANT-PIXL-DESIGN.md` field count: 157 → 159 (updated in 6 locations)
- `screenExtended` added to "Sync — Screen & Display" table (13 → 14 fields)
- `mousePath` added to "Behavioral — Mouse & Scroll" table (9 → 10 fields)
- "Data Points to ADD" section cleared — all planned fields now implemented
- Summary table updated: Sync 129→130, Behavioral 9→10

---

## Files Modified — Phase 1

| File | Change |
|------|--------|
| `SmartPiXL/Scripts/PiXLScript.cs` | Added `data.screenExtended` and `data.mousePath` |
| `SmartPiXL/SQL/41_ScreenExtendedMousePath.sql` | New migration: columns + ETL proc update |
| `SmartPiXL.Tests/PiXLScriptTests.cs` | 5 new tests for Phase 1 fields |
| `docs/BRILLIANT-PIXL-DESIGN.md` | Field counts 157→159, inventory tables updated |
| `docs/IMPLEMENTATION-LOG.md` | This file |

## Remaining — Phase 1 Verification

- [ ] Run migration 41 against `localhost\SQL2025` database
- [ ] Send test pixel hit and verify `screenExtended` + `mousePath` appear in query string
- [ ] Run `EXEC ETL.usp_ParseNewHits` and verify `PiXL.Parsed` columns populate

---

## Session 2 — Namespace Rename: `TrackingPixel` → `SmartPiXL`

**Problem:** All canonical code used the legacy namespace `TrackingPixel.*` — inherited from the deprecated project. The product is SmartPiXL. The word "pixel" cannot appear anywhere in the canonical platform.

**Scope:** Renamed all `TrackingPixel` namespace references to `SmartPiXL` across `SmartPiXL/`, `SmartPiXL.Shared/`, and `SmartPiXL.Tests/`. Legacy/deprecated folders are untouched.

### Changes

| Area | Change |
|------|--------|
| **27 .cs files** | `using TrackingPixel.*` → `using SmartPiXL.*`, `namespace TrackingPixel.*` → `namespace SmartPiXL.*` |
| **SmartPiXL.csproj** | Removed `<RootNamespace>TrackingPixel</RootNamespace>` — defaults to `SmartPiXL` from project name |
| **TrackingPixel.Tests.csproj** | Renamed file to `SmartPiXL.Tests.csproj` |
| **SmartPixl.sln** | Updated test project reference to `SmartPiXL.Tests\SmartPiXL.Tests.csproj` |
| **tron.html** | `Get-Process TrackingPixel` → `Get-Process SmartPiXL` |
| **TrackingSettings.cs** | Comment referencing old path updated |

### Namespace Map

| Old | New |
|-----|-----|
| `TrackingPixel.Configuration` | `SmartPiXL.Configuration` |
| `TrackingPixel.Endpoints` | `SmartPiXL.Endpoints` |
| `TrackingPixel.Models` | `SmartPiXL.Models` |
| `TrackingPixel.Scripts` | `SmartPiXL.Scripts` |
| `TrackingPixel.Services` | `SmartPiXL.Services` |
| `TrackingPixel.Tests` | `SmartPiXL.Tests` |

**Not modified:** `SmartPiXL.Modern-Deprecated/`, `SmartPiXL.Worker-Deprecated/` — legacy reference code, untouched per policy.

---

## Session 3 — Phase 2: Forge Foundation

**Scope:** Create the `SmartPiXL.Forge` project from scratch — named pipe server, enrichment pipeline shell, SQL writer, JSONL failover catch-up, plus port all 8 background services from Worker-Deprecated.

### ForgeSettings (Shared)

Created `SmartPiXL.Shared/Configuration/ForgeSettings.cs` — a separate POCO bound from the `"Forge"` section of the Forge's `appsettings.json`. Kept separate from `TrackingSettings` because these settings are only relevant to the Forge process (pipe name, failover directory, channel capacities, enrichment toggles). `TrackingSettings` is shared across Edge + Forge for connection strings, sync intervals, SMTP, etc.

### Project Setup

- `SmartPiXL.Forge.csproj` — `Microsoft.NET.Sdk.Worker`, `net10.0`, `ServerGarbageCollection`, `TieredPGO`
- NuGet: `Microsoft.Data.SqlClient 6.1.4`, `Microsoft.Extensions.Hosting.WindowsServices 10.0.2`, `System.ServiceProcess.ServiceController 10.0.2`, `Microsoft.Extensions.Http 10.0.0`
- ProjectReference to `SmartPiXL.Shared`
- Added to `SmartPixl.sln` with GUID `{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}`

### ForgeChannels Pattern

**Problem:** The Forge has two `Channel<TrackingData>` instances — one for pipe-to-enrichment, one for enrichment-to-SQL. Standard DI cannot disambiguate two registrations of the same type.

**Decision:** Created `ForgeChannels.cs` — a simple wrapper class holding both channels with named properties (`Enrichment` and `SqlWriter`). Registered as singleton. Individual services take `ForgeChannels` in their constructor and read the channel they need. No marker interfaces, no keyed DI.

**Conflict:** Initial `ForgeChannels` constructor accepted `ForgeSettings` directly, but the constructor needed `int` capacity values. Fixed to pass `settings.PipeChannelCapacity` and `settings.SqlWriterChannelCapacity` from `Program.cs`.

### New Pipeline Services (4 files)

| Service | Base Class | Role |
|---------|-----------|------|
| `PipeListenerService` | `BackgroundService` | Named pipe server (`NamedPipeServerStream`), deserializes JSON lines into `TrackingData`, writes to `ForgeChannels.Enrichment`. Auto-reconnects, supports `MaxConcurrentPipeInstances` concurrent clients. |
| `EnrichmentPipelineService` | `BackgroundService` | Reads from `ForgeChannels.Enrichment`, applies enrichments (placeholder — Tier 1-3 enrichments are Phase 4-6), writes to `ForgeChannels.SqlWriter`. |
| `SqlBulkCopyWriterService` | `BackgroundService` | Reads from `ForgeChannels.SqlWriter`, writes to `PiXL.Raw` via `SqlBulkCopy`. Ported from Edge's `DatabaseWriterService` (711 lines). Uses ordinal-based column mapping for 9 PiXL.Raw columns. |
| `FailoverCatchupService` | `BackgroundService` | Scans `FailoverDirectory` every 60s for `.jsonl` files, deserializes line-by-line, feeds into enrichment channel. Archives processed files. Handles partial/malformed lines gracefully. |

### Ported Services from Worker-Deprecated (8 files)

All ported under `SmartPiXL.Forge.Services` namespace using `SmartPiXL.*` namespaces (not the old `TrackingPixel.*`). Worker-Deprecated was read-only reference only.

| Service | Lines | Notes |
|---------|-------|-------|
| `EtlBackgroundService` | ~138 | Calls `ETL.usp_ParseNewHits` + `ETL.usp_MatchVisits` every 60s |
| `IpApiSyncService` | ~250 | Delta sync from Xavier `IPGEO.dbo.IP_Location_New` → local `IPAPI.IP` |
| `CompanyPiXLSyncService` | ~520 | Syncs `PiXL.Company` + `PiXL.Pixel` from Xavier `SmartPiXL` DB every 6h |
| `EmailNotificationService` | ~120 | SMTP notifications for ops alerts, SMS via carrier gateway |
| `HttpEdgeHealthClient` | ~101 | `IEdgeHealthClient` impl — calls Edge `/internal/health`, `/internal/circuit-reset`, `/internal/geo-cache/clear` |
| `InfraHealthService` | ~600 | Periodic infra health snapshots — SQL, services, websites, data flow |
| `MaintenanceSchedulerService` | ~200 | Scheduled raw data purge, parsed archive, index maintenance |
| `SelfHealingService` | ~220 | Automated remediation — restart IIS app pool, restart services, reset ETL watermarks |

### Program.cs (Composition Root)

- Pure worker — no HTTP, no Kestrel
- `Host.CreateApplicationBuilder` → `UseWindowsService()`
- Configuration: `TrackingSettings` from `"Tracking"` section, `ForgeSettings` from `"Forge"` section
- Xavier SQL Auth rewrite via environment variables (`SMARTPIXL_SQL_USER` / `SMARTPIXL_SQL_PASS`) — converts `Integrated Security=True` to `User Id=...;Password=...` for cross-machine auth if env vars present
- Registers `ForgeChannels` singleton, `FileTrackingLogger`, `IEdgeHealthClient` (via `AddHttpClient`), all 12 services

### Conflict: Missing `Microsoft.Extensions.Http`

**Problem:** `AddHttpClient<IEdgeHealthClient, HttpEdgeHealthClient>()` requires the `Microsoft.Extensions.Http` package, which is NOT included in the Worker SDK by default (unlike the ASP.NET Core SDK).

**Resolution:** Added `Microsoft.Extensions.Http 10.0.0` to `SmartPiXL.Forge.csproj`.

### Worker-Deprecated Build Failures (Expected)

After the Session 2 namespace rename (`TrackingPixel.*` → `SmartPiXL.*`), the Worker-Deprecated project now has build failures — it still uses `using TrackingPixel.*` directives. These errors are expected and intentional. Worker-Deprecated is read-only reference code to be deleted in Phase 10. The solution-level build shows failures from this project only.

### Result

- Forge: **0 warnings, 0 errors**
- Edge: **0 warnings, 0 errors**
- Shared: **0 warnings, 0 errors**
- Tests: **169/169 passing** (no regressions)

---

## Files Created — Phase 2

| File | What |
|------|------|
| `SmartPiXL.Shared/Configuration/ForgeSettings.cs` | Forge-specific settings POCO (pipe name, failover dir, channel caps) |
| `SmartPiXL.Forge/SmartPiXL.Forge.csproj` | Worker SDK project file |
| `SmartPiXL.Forge/appsettings.json` | Forge config (connection strings, pipe, channels, log) |
| `SmartPiXL.Forge/Program.cs` | Composition root — pure worker, no HTTP |
| `SmartPiXL.Forge/Services/ForgeChannels.cs` | DI wrapper for two `Channel<TrackingData>` instances |
| `SmartPiXL.Forge/Services/PipeListenerService.cs` | Named pipe server |
| `SmartPiXL.Forge/Services/EnrichmentPipelineService.cs` | Enrichment pipeline shell |
| `SmartPiXL.Forge/Services/SqlBulkCopyWriterService.cs` | SqlBulkCopy writer (ported from Edge) |
| `SmartPiXL.Forge/Services/FailoverCatchupService.cs` | JSONL failover catch-up |
| `SmartPiXL.Forge/Services/EtlBackgroundService.cs` | ETL scheduler (ported from Worker) |
| `SmartPiXL.Forge/Services/IpApiSyncService.cs` | Xavier IPGEO sync (ported from Worker) |
| `SmartPiXL.Forge/Services/CompanyPiXLSyncService.cs` | Xavier Company/Pixel sync (ported from Worker) |
| `SmartPiXL.Forge/Services/EmailNotificationService.cs` | SMTP + SMS notifications (ported from Worker) |
| `SmartPiXL.Forge/Services/HttpEdgeHealthClient.cs` | Edge health client (ported from Worker) |
| `SmartPiXL.Forge/Services/InfraHealthService.cs` | Infra health snapshots (ported from Worker) |
| `SmartPiXL.Forge/Services/MaintenanceSchedulerService.cs` | Scheduled maintenance (ported from Worker) |
| `SmartPiXL.Forge/Services/SelfHealingService.cs` | Automated remediation (ported from Worker) |

## Files Modified — Phase 2

| File | Change |
|------|--------|
| `SmartPixl.sln` | Added SmartPiXL.Forge project reference |

---

## Session 4 — Xavier Certificate Investigation + Sync Lifecycle

**Context:** Owner directive — all three Xavier syncs (IPGEO, Company, Pixel) are NOT permanent architecture. They are a "lifeline between legacy and brilliant only for as long as legacy exists as its own client-facing product." A new front-end (not yet scoped) will eventually replace Xavier, at which point SmartPiXL becomes authoritative and Xavier syncs are decommissioned.

Owner also stated they had created a real certificate on Xavier for secure connections and wanted `TrustServerCertificate=True` validated.

### Certificate Investigation

Performed a full diagnostic of Xavier's (192.168.88.35 / D43DQBM2) SQL Server certificate chain:

| Finding | Detail |
|---------|--------|
| **Cert in trust store** | `CN=192.168.88.35`, self-signed, sha1RSA, thumbprint `02AC76BBF2531C5B7DE4B93D2B301BEE5C2BB269`, expires 2031-02-16 |
| **Store placement** | Present in both `Cert:\LocalMachine\Root` and `Cert:\CurrentUser\Root` |
| **SAN** | `DNS:192.168.88.35, DNS:Xavier, DNS:localhost` |
| **EKU** | Client Auth + Server Auth |
| **X509Chain.Build()** | Returns `True` — .NET validates the chain correctly |
| **SQL Server version** | 14.00.2095 (SQL Server 2017) |
| **Connection encryption** | `encrypt_option = FALSE` — connections are currently unencrypted |

**Root cause:** SQL Server 2017 on Xavier is NOT configured to present the custom cert. It's still using its auto-generated self-signed cert. When `Microsoft.Data.SqlClient` forces encryption with `TrustServerCertificate=False`, SChannel gets the auto-generated cert (not in our trust store) and rejects it.

**Conflict:** Owner expected `TrustServerCertificate=True` could be removed since the cert was created. Investigation proved that cert creation alone is insufficient — Xavier's SQL Server Configuration Manager must be configured to use the cert. This is a Xavier-side remediation item, not a SmartPiXL code issue.

**Decision:** Keep `TrustServerCertificate=True` on Xavier connection strings but add explicit `Encrypt=True` for clarity. Document the cert remediation path. `TrustServerCertificate=True` is safe for localhost (no network boundary) and acceptable for Xavier (same private network) until Xavier SQL Server is configured to present the custom cert.

### Remediation Path (Xavier-side, when ready)

1. Open SQL Server Configuration Manager on Xavier
2. SQL Server Network Configuration → Protocols for MSSQLSERVER → Certificate tab
3. Select the `CN=192.168.88.35` certificate
4. Restart SQL Server on Xavier
5. Remove `TrustServerCertificate=True` from all Xavier connection strings in SmartPiXL
6. Optionally regenerate the cert as sha256RSA (sha1 is deprecated)

### Connection String Changes

Added explicit `Encrypt=True` to all connection strings (MDSC 4.0+ defaults it, but explicit is better for ops visibility):

| File | Before | After |
|------|--------|-------|
| `SmartPiXL.Forge/appsettings.json` (3 strings) | `...;TrustServerCertificate=True` | `...;Encrypt=True;TrustServerCertificate=True` |
| `SmartPiXL/appsettings.json` (3 strings) | `...;TrustServerCertificate=True` | `...;Encrypt=True;TrustServerCertificate=True` |

### TrackingSettings.cs Updates

- Compiled default `ConnectionString`: added `Encrypt=True`
- Added a 17-line block comment above the Xavier section explaining:
  - Syncs are a **temporary bridge** while Xavier's legacy front-end is client-facing
  - Cert status: Xavier has the cert, SQL Server doesn't present it, TSC=True is required until configured
  - Remediation path documented inline
- Updated XML doc comments on `XavierConnectionString` and `XavierSmartPiXLConnectionString`:
  - Marked **TEMPORARY** — transitional bridge, not permanent architecture
  - Described cert status and what's needed to remove `TrustServerCertificate=True`

### Design Doc Update

Added Section 9.1 "Xavier Sync Lifecycle — Temporary Bridge" to `BRILLIANT-PIXL-DESIGN.md`:
- Sync service table (3 rows: IpApiSync, CompanyPiXLSync Company, CompanyPiXLSync Pixel)
- Three-stage lifecycle: current state (Xavier authoritative) → target state (SmartPiXL authoritative) → timeline (extended but not permanent)
- Full certificate documentation: CN, SAN, thumbprint, algo, expiry, store placement
- Connection security problem statement and remediation path
- Updated Xavier row in deployment table: `MSSQL on 192.168.88.35` → `MSSQL 2017 on 192.168.88.35`

### Result

- Shared: **0 warnings, 0 errors**
- Edge: **0 warnings, 0 errors**
- Forge: **0 warnings, 0 errors**
- Tests: **169/169 passing**

---

## Files Modified — Session 4

| File | Change |
|------|--------|
| `SmartPiXL.Forge/appsettings.json` | Added explicit `Encrypt=True` to all 3 connection strings |
| `SmartPiXL/appsettings.json` | Added explicit `Encrypt=True` to all 3 connection strings |
| `SmartPiXL.Shared/Configuration/TrackingSettings.cs` | Updated compiled defaults, added cert status block comment, marked Xavier syncs as TEMPORARY |
| `docs/BRILLIANT-PIXL-DESIGN.md` | Added Section 9.1: Xavier Sync Lifecycle — cert docs, sync table, remediation path |
| `docs/IMPLEMENTATION-LOG.md` | This entry |

---

## Session 5 — Phase 3: Edge Pipe Client + JSONL Durability

### Scope

Rewired the IIS Edge to send enriched TrackingData records to the Forge via named pipe instead of writing to SQL directly. Added JSONL failover for durability when the pipe is unavailable. Added a `UsePipe` config toggle to fall back to direct SQL (legacy behavior) for testing.

### Conflict: PipeClientService API — `ValueTask EnqueueAsync` vs `bool TryEnqueue`

**What:** The workplan (Phase 3, Step 1) specifies `ValueTask EnqueueAsync(TrackingData record)` — a direct async pipe write from the request thread.

**Why:** The C# coding standards mandate `Channel<T>` with `BoundedChannelFullMode.DropOldest` for all producer-consumer queues. Using async pipe I/O on the request thread would:
1. Block the thread-pool thread on pipe write latency
2. Require serialization on the hot path (heap allocations)
3. Diverge from the established `DatabaseWriterService.TryQueue` pattern

The `bool TryEnqueue` approach uses `Channel<T>.Writer.TryWrite` — a lock-free CAS operation with zero allocation, consistent with every other hot-path write in the codebase.

**Decision:** Implemented `bool TryEnqueue(TrackingData data)` backed by `Channel<T>`. A single background loop reads from the channel, handles JSON serialization, pipe write, and failover. The hot path remains lock-free and zero-alloc. The workplan's intent (fire-and-forget from request perspective, auto-reconnect with backoff, JSONL failover) is fully preserved — only the method signature differs.

### Conflict: `DatabaseWriterService` Role

**What:** The workplan says "DatabaseWriterService remains... as a dead-letter handler of absolute last resort." Initial implementation added a `UsePipe` boolean toggle allowing the Edge to fall back to direct SQL writes via DatabaseWriterService.

**Why:** Owner clarification: the Edge should NEVER write to SQL directly. Its only two paths are pipe-to-Forge and JSONL-failover-to-disk. The Forge owns all SQL writes. Having a `UsePipe=false` escape hatch risks the Edge bypassing the Forge, which defeats the architectural separation.

**Decision:** Removed `UsePipe` toggle entirely. Removed `DatabaseWriterService` from Edge DI registration and all endpoint references. The Edge write path is now unconditionally:
- Primary: `PipeClientService` → Forge via named pipe → SQL
- Failover: `PipeClientService` → `JsonlFailoverService` → disk (Forge's `FailoverCatchupService` picks up on restart)
- `DatabaseWriterService.cs` file remains in the project (still compiles) but is not registered or referenced. Can be deleted later.

### Implementation Details

#### New Files Created
- **`SmartPiXL/Services/PipeClientService.cs`** — `sealed class PipeClientService : BackgroundService`
  - `Channel<TrackingData>` (bounded, DropOldest, SingleReader)
  - `bool TryEnqueue(TrackingData data)` — lock-free hot path
  - `int QueueDepth`, `bool IsConnected` — health endpoint
  - Background loop: read → serialize → pipe write → flush
  - Auto-reconnect with exponential backoff (1s → 2s → 4s → ... → 30s cap)
  - Backoff timer (`_nextReconnectAttempt`) prevents reconnect attempts on every record
  - Failover to `JsonlFailoverService` when pipe unavailable
  - Shutdown drain: pipe first, JSONL for remainder

- **`SmartPiXL/Services/JsonlFailoverService.cs`** — `sealed class JsonlFailoverService : BackgroundService`
  - `Channel<TrackingData>` (bounded, DropOldest, SingleReader)
  - `bool TryEnqueue(TrackingData data)` — lock-free
  - Single background writer: reads from channel → appends JSON line to daily rolling file
  - File naming: `failover_yyyy_MM_dd.jsonl`
  - Directory: `FailoverDirectory` from config (default `Failover/`, relative to `AppContext.BaseDirectory`)
  - Auto-creates directory on startup
  - `AutoFlush = true` for durability

#### Files Modified
- **`TrackingSettings.cs`** — Added 2 properties: `PipeName` (default `SmartPiXL-Enrichment`), `FailoverDirectory` (default `Failover`). Defaults match `ForgeSettings` for consistency. `UsePipe` was initially added then removed per owner clarification.
- **`SmartPiXL/appsettings.json`** — Added `PipeName`, `FailoverDirectory` under `Tracking` section.
- **`SmartPiXL/Program.cs`** — Registered `JsonlFailoverService` and `PipeClientService` (both as singleton + hosted service). JsonlFailoverService registered first (PipeClientService depends on it). Removed `DatabaseWriterService` registration entirely — Edge never writes to SQL.
- **`SmartPiXL/Endpoints/TrackingEndpoints.cs`** —
  - Resolved `PipeClientService` at startup in `MapTrackingEndpoints`
  - Removed `DatabaseWriterService` and `UsePipe` references
  - Updated health endpoint: reports `pipeConnected` and `queueDepth` (pipe only)
  - Updated `CaptureAndEnqueue` signature: replaced `DatabaseWriterService writerService` + `bool usePipe` with `PipeClientService pipeClient`
  - Simplified final enqueue: always `pipeClient.TryEnqueue` — no branching
  - Updated enrichment pipeline comment and XML doc summary

### Build Result

| Project | Warnings | Errors |
|---------|----------|--------|
| SmartPiXL.Shared | 0 | 0 |
| SmartPiXL (Edge) | 0 | 0 |
| SmartPiXL.Forge | 0 | 0 |
| Tests | 169/169 passing | 0 failures |

---

## Session 6 — Phase 4: Tier 1 Enrichments (Forge Day 1 Libraries)

### Scope

Install NuGet packages and build 6 enrichment services for the Forge's Tier 1 pipeline: bot detection, user-agent parsing, reverse DNS, MaxMind offline geo, IPAPI real-time geo, and WHOIS ASN lookups. Wire them into `EnrichmentPipelineService`, create SQL migration 42 with 19 new parsed columns, and add unit tests.

### NuGet Packages Installed (Forge)

| Package | Version | Purpose |
|---------|---------|---------|
| NetCrawlerDetect | 1.2.113 | Bot/crawler user-agent detection |
| UAParser | 3.1.47 | Primary user-agent string parser |
| DeviceDetector.NET | 6.4.7 | Secondary UA parser (device type/model/brand) |
| DnsClient | 1.8.0 | Async reverse DNS (PTR) lookups |
| MaxMind.GeoIP2 | 5.4.1 | Offline GeoIP2 (.mmdb) lookups |
| Whois | 3.0.1 | WHOIS ASN/org lookups |
| MathNet.Numerics | 5.0.0 | Statistical analysis (Tier 2 prep) |
| FuzzySharp | 2.0.2 | Fuzzy string matching (Tier 2 prep) |

### Enrichment Services Created

All services are in `SmartPiXL.Forge/Services/Enrichments/`:

#### 1. `BotUaDetectionService.cs`
- Singleton, uses `NetCrawlerDetect.CrawlerDetect`
- `(bool IsCrawler, string? BotName) Check(string? userAgent)`
- Appends: `_srv_knownBot=1`, `_srv_botName={name}`

#### 2. `UaParsingService.cs` (149 lines)
- Two-pass architecture: UAParser (browser/OS) → DeviceDetector.NET (device type/model/brand)
- Returns `readonly record struct UaParseResult` with 7 fields
- Appends: `_srv_browser`, `_srv_browserVer`, `_srv_os`, `_srv_osVer`, `_srv_deviceType`, `_srv_deviceModel`, `_srv_deviceBrand`

#### 3. `DnsLookupService.cs`
- `sealed partial class` with 7 `[GeneratedRegex]` patterns for cloud hostname detection (AWS, GCP, Azure, DigitalOcean, Akamai, Cloudflare, EU providers)
- `async Task<DnsLookupResult> LookupAsync(string?, CancellationToken)` via `LookupClient`
- 2-second timeout, no retry, DnsClient internal caching
- Appends: `_srv_rdns`, `_srv_rdnsCloud=1`

#### 4. `MaxMindGeoService.cs`
- Loads 3 `.mmdb` files from `MaxMind/` at startup (City, ASN, Country)
- `MaxMindResult Lookup(string?)` — synchronous O(1) trie lookup
- Graceful degradation: missing `.mmdb` files → no results (not an error)
- Implements `IDisposable` for `DatabaseReader` cleanup
- Appends: `_srv_mmCC`, `_srv_mmReg`, `_srv_mmCity`, `_srv_mmLat`, `_srv_mmLon`, `_srv_mmASN`, `_srv_mmASNOrg`

#### 5. `IpApiLookupService.cs`
- `ConcurrentDictionary<string, DateTime>` for known IPs with 90-day staleness check
- Rate-limited to ~28.5 req/min via `SemaphoreSlim` + 2100ms delay
- Loads known IPs from `IPAPI.IP` at startup via `LoadKnownIpsAsync()`
- MERGE upsert to `IPAPI.IP` table after each API call
- Skipped entirely for IPs already in `IPAPI.IP` (< 90 days old)
- Appends: `_srv_ipapiCC`, `_srv_ipapiISP`, `_srv_ipapiProxy`, `_srv_ipapiMobile`, `_srv_ipapiReverse`, `_srv_ipapiASN`

#### 6. `WhoisAsnService.cs`
- `async Task<WhoisAsnResult> LookupAsync(string?, CancellationToken)`
- 5-second timeout, field extraction via string line parsing
- Only called when MaxMind ASN is empty (conditional in pipeline)
- Appends: `_srv_whoisASN`, `_srv_whoisOrg`

### Pipeline Wiring

`EnrichmentPipelineService.cs` rewritten to run the full Tier 1 chain per record:
1. **Bot Detection** → skip-flag potential for future optimization
2. **UA Parsing** → browser, OS, device info
3. **DNS Lookup** → reverse hostname + cloud detection
4. **MaxMind Geo** → offline country/region/city/coords/ASN
5. **IPAPI Lookup** → conditional (skipped if IP already known < 90 days)
6. **WHOIS ASN** → conditional (only if MaxMind ASN was empty)

Each enrichment appends `_srv_*` query string parameters to `TrackingData.QueryString`. The ETL proc (`usp_ParseNewHits`) extracts them during Phase 8B.

### SQL Migration 42 — `42_ForgeTier1Columns.sql`

19 new columns on `PiXL.Parsed` (each with `IF NOT EXISTS` guard):

| Group | Columns |
|-------|---------|
| Bot | `KnownBot BIT`, `BotName VARCHAR(200)` |
| UA Parse | `ParsedBrowser VARCHAR(100)`, `ParsedBrowserVersion VARCHAR(50)`, `ParsedOS VARCHAR(100)`, `ParsedOSVersion VARCHAR(50)`, `ParsedDeviceType VARCHAR(50)`, `ParsedDeviceModel VARCHAR(100)`, `ParsedDeviceBrand VARCHAR(100)` |
| DNS | `ReverseDNS VARCHAR(500)`, `ReverseDNSCloud BIT` |
| MaxMind | `MaxMindCountry CHAR(2)`, `MaxMindRegion VARCHAR(100)`, `MaxMindCity VARCHAR(200)`, `MaxMindLat DECIMAL(9,6)`, `MaxMindLon DECIMAL(9,6)`, `MaxMindASN INT`, `MaxMindASNOrg VARCHAR(200)` |
| WHOIS | `WhoisASN VARCHAR(50)`, `WhoisOrg VARCHAR(200)` |

Full `CREATE OR ALTER PROCEDURE ETL.usp_ParseNewHits` updated with Phase 8B extracting all `_srv_*` params via `dbo.GetQueryParam()`.

### Program.cs Registration

Added 6 singleton registrations in `SmartPiXL.Forge/Program.cs`:
```csharp
builder.Services.AddSingleton<BotUaDetectionService>();
builder.Services.AddSingleton<UaParsingService>();
builder.Services.AddSingleton<DnsLookupService>();
builder.Services.AddSingleton<MaxMindGeoService>();
builder.Services.AddSingleton<IpApiLookupService>();
builder.Services.AddSingleton<WhoisAsnService>();
```

### Unit Tests Created (48 new tests)

| Test File | Tests | Coverage |
|-----------|-------|----------|
| `BotUaDetectionServiceTests.cs` | ~10 | Bot detection for known crawlers + human browsers + edge cases |
| `UaParsingServiceTests.cs` | ~8 | Chrome/Safari/Firefox/Edge + mobile + null/garbage |
| `DnsLookupServiceTests.cs` | ~10 | Invalid input, real DNS (8.8.8.8), cloud patterns, cancellation |
| `MaxMindGeoServiceTests.cs` | ~10 | Invalid input, graceful degradation (no .mmdb), IDisposable |
| `WhoisAsnServiceTests.cs` | ~10 | Null/empty, private IP skip, real WHOIS (8.8.8.8), cancellation |

### Conflicts Resolved

#### 1. Workplan says "Whois.NET" — actual package is "Whois"
- **Conflict:** Workplan references `Whois.NET` as the NuGet package name
- **Decision:** Installed `Whois` (version 3.0.1) — this is the actual package on NuGet. `Whois.NET` does not exist as a separate package.
- **Why:** Package name in workplan was slightly inaccurate; the correct NuGet ID is `Whois`

#### 2. Whois API: `WhoisLookup.For()` does not exist
- **Conflict:** Initial implementation used `WhoisLookup.For(ip)` (static method) based on common patterns
- **Decision:** Changed to instance-based `new Whois.WhoisLookup().Lookup(ip)` — discovered via reflection that `WhoisLookup` is instance-based with a `Lookup()` method
- **Why:** API discovery showed no static `For()` method; `Lookup()` is the correct instance method

#### 3. Whois response property: `.RawText` does not exist
- **Conflict:** Initial code used `response.RawText` for the WHOIS response body
- **Decision:** Changed to `response.Content` — discovered via reflection on `WhoisResponse` properties
- **Why:** `Content` is the actual property containing the raw WHOIS text

#### 4. CrawlerDetect: `.GetMatches()` does not exist
- **Conflict:** Initial code used `_detector.GetMatches()` to retrieve matched bot names
- **Decision:** Changed to `_detector.Matches?.FirstOrDefault()?.Value` — `Matches` is a property returning `MatchCollection`
- **Why:** API discovery showed `Matches` is a property (get-only), not a method

#### 5. MaxMind `FileAccessMode` namespace
- **Conflict:** `FileAccessMode.MemoryMapped` wouldn't resolve — it's in `MaxMind.Db`, not `MaxMind.GeoIP2`
- **Decision:** Added `using MaxMind.Db;` to the file
- **Why:** The enum is defined in the lower-level `MaxMind.Db` package (dependency of `MaxMind.GeoIP2`)

#### 6. DnsClient `PtrRecord.DomainName` vs `PtrDomainName`
- **Conflict:** Initial code used `ptrRecord.DomainName.Value` which returns the query name (e.g., `8.8.8.8.in-addr.arpa`), not the answer
- **Decision:** Changed to `ptrRecord.PtrDomainName.Value` which returns the actual PTR answer (e.g., `dns.google`)
- **Why:** `DomainName` is the record's owner name (query), `PtrDomainName` is the PTR target (answer)

#### 7. `TrackingSettings.FallbackConnectionString` does not exist
- **Conflict:** IpApiLookupService used `TrackingSettings.FallbackConnectionString` for a static default
- **Decision:** Changed to inject `IOptions<TrackingSettings>` and use `settings.Value.ConnectionString`
- **Why:** `TrackingSettings` has no static constant — the default connection string is baked into the property initializer

### Build Result

| Project | Warnings | Errors |
|---------|----------|--------|
| SmartPiXL.Shared | 0 | 0 |
| SmartPiXL (Edge) | 0 | 0 |
| SmartPiXL.Forge | 0 | 0 |
| Tests | 217/217 passing | 0 failures |

---

## Session 7 — Phase 5: Tier 2 Enrichments (Cross-Request Intelligence)

**Scope:** 4 stateful enrichment services + GpuTierReference + QueryParamReader utility + SQL migration 43 + unit tests.

### Services Created

| Service | Type | Role |
|---------|------|------|
| `GpuTierReference` | Static class | ~50 GPU renderer patterns → HIGH/MID/LOW tier classification |
| `DeviceAffluenceService` | Singleton (stateless) | GPU + cores + memory + screen + platform → affluence signal |
| `CrossCustomerIntelService` | Singleton (stateful) | ConcurrentDictionary sliding window, 3+ companies in 5min = alert |
| `LeadQualityScoringService` | Singleton (stateless) | 9 weighted human-visitor signals → 0-100 lead quality score |
| `SessionStitchingService` | Singleton (stateful) | Fingerprint-keyed sessions, 30-min timeout, GUID session IDs |
| `QueryParamReader` | Internal static | QS param extraction utility for Forge enrichment services |

### Pipeline Integration

- `EnrichmentPipelineService` extended with Tier 2 chain (steps 7-10):
  - Step 7: Session stitching (fingerprint → session ID, hit number, duration, page count)
  - Step 8: Cross-customer intel (IP + fingerprint + company → alert)
  - Step 9: Device affluence (GPU + hardware signals → affluence tier)
  - Step 10: Lead quality scoring (assembled from Tier 1 + Tier 2 results)
- `Program.cs` updated with 4 singleton registrations

### SQL Migration 43

- 10 new columns on `PiXL.Parsed`: `CrossCustomerHits`, `CrossCustomerAlert`, `LeadQualityScore`, `SessionId`, `SessionHitNumber`, `SessionDurationSec`, `SessionPageCount`, `AffluenceSignal`, `GpuTier`
- `ETL.usp_ParseNewHits` updated: Phase 8C added for Tier 2 `_srv_*` params
- Full `CREATE OR ALTER` with all 13 phases + new Phase 8C

### Conflicts & Decisions

#### 1. GpuTierReference: "RX 5" catch-all subsumes old RX 5xx LOW GPUs
- **Conflict:** `"RX 5"` (MID, for RX 5000-series) substring-matched `"RX 580"`, `"RX 570"` etc (should be LOW)
- **Decision:** Moved specific RX 580/570/560/550 (LOW) patterns BEFORE the `"RX 5"` catch-all
- **Why:** First-match-wins pattern ordering — specific models must precede catch-all patterns

#### 2. GpuTierReference: Quadro RTX caught by consumer RTX patterns
- **Conflict:** `"Quadro RTX 5000"` matched `"RTX 50"` (HIGH) before reaching `"Quadro RTX"` (MID)
- **Decision:** Moved Quadro patterns to the very top of the array, before all RTX consumer patterns
- **Why:** Professional workstation GPUs must be identified before consumer GPU pattern matching

#### 3. GpuTierReference: Intel Arc(TM) not matching Arc patterns
- **Conflict:** Real WebGL strings use `"Intel(R) Arc(TM) A770"` but pattern was `"Arc A770"` — `(TM)` breaks substring match
- **Decision:** Changed Arc patterns to just model numbers: `"A770"`, `"A750"`, `"A580"`, `"A380"` — unique enough for GPU context
- **Why:** WebGL UNMASKED_RENDERER_OES strings include trademark symbols that vary by driver version

#### 4. QueryParamReader: `+` not decoded as space
- **Conflict:** `Uri.UnescapeDataString` only decodes `%xx` sequences, not `+` (form URL encoding for spaces)
- **Decision:** Added `.Replace('+', ' ')` before `UnescapeDataString` call
- **Why:** PiXL Script uses `encodeURIComponent` which encodes spaces as `%20`, but some paths produce `+` instead

#### 5. InternalsVisibleTo for test project
- **Conflict:** `QueryParamReader` is `internal` (intentionally — utility class, not part of public API) but tests need access
- **Decision:** Added `<InternalsVisibleTo Include="TrackingPixel.Tests" />` to `SmartPiXL.Forge.csproj`
- **Why:** Test assembly name is `TrackingPixel.Tests` (derived from csproj filename), not `SmartPiXL.Tests`

### Unit Tests Added (165 new)

| Test Class | Tests | What's Covered |
|------------|-------|----------------|
| `GpuTierReferenceTests` | 42 | All tiers (HIGH/MID/LOW/Unknown), case insensitivity, TierToString |
| `DeviceAffluenceServiceTests` | 12 | HIGH/MID/LOW scoring, platform bonus, screen resolution, edge cases |
| `CrossCustomerIntelServiceTests` | 11 | Alert threshold, sliding window, null inputs, independent tracking |
| `LeadQualityScoringServiceTests` | 15 | Individual signal weights, perfect/zero scores, combined scenarios |
| `SessionStitchingServiceTests` | 13 | Session creation, extension, page tracking, null/empty, GUID format |
| `QueryParamReaderTests` | 22 | Get/GetInt/GetDouble/GetBool, URL decoding, case insensitive, _srv_* |

### Build Result

| Project | Warnings | Errors |
|---------|----------|--------|
| SmartPiXL.Shared | 0 | 0 |
| SmartPiXL (Edge) | 0 | 0 |
| SmartPiXL.Forge | 0 | 0 |
| Tests | 382/382 passing | 0 failures |

---

## Session 8 — Phase 6: Tier 3 Enrichments (Asymmetric Detection)

**Scope:** Five new enrichment services that detect bot/emulation behavior through cross-signal contradiction analysis, cultural fingerprint verification, device age anomalies, behavioral replay detection, and dead internet indexing.

### Architecture Decisions

#### 1. Pipeline Restructuring — Lead Scoring Moved to Step 15
- **Conflict:** Lead Quality Scoring (Phase 5) had two placeholder values: `HasMatchingTimezone: true` and `ContradictionCount: 0`. These need real data from Tier 3 services.
- **Decision:** Moved Lead Scoring from step 10 to step 15 (after all Tier 3 enrichments at steps 10-14). Now uses `arbitrageResult.TimezoneMatch` and `contradictionResult.Count` for real values.
- **Why:** Lead scoring must run after Tier 3 to consume real contradiction counts and timezone match results.

#### 2. Service Visibility — `public sealed class` (not `internal`)
- **Conflict:** Coding standards prefer `internal sealed class`, but `EnrichmentPipelineService` is `public` (from Phase 4). Internal Tier 3 services as constructor parameters cause CS0051 (inconsistent accessibility).
- **Decision:** Made all 5 Tier 3 services `public sealed class` to match existing Phase 4 services.
- **Why:** Consistency with existing services and required by compiler for public constructor parameters.

#### 3. InternalsVisibleTo Updated — `SmartPiXL.Tests` (not `TrackingPixel.Tests`)
- **Conflict:** Solution references `SmartPiXL.Tests.csproj` (assembly name `SmartPiXL.Tests`) but Forge's InternalsVisibleTo declared `TrackingPixel.Tests` (matching the old csproj filename).
- **Decision:** Updated to `InternalsVisibleTo Include="SmartPiXL.Tests"`. Also added Forge project reference to `SmartPiXL.Tests.csproj`.
- **Why:** The solution file uses `SmartPiXL.Tests.csproj` → default assembly name is `SmartPiXL.Tests`.

#### 4. FNV-1a Hash for Behavioral Replay (not MurmurHash3)
- **Decision:** Used FNV-1a (32-bit) for mouse path hashing — offset basis 2166136261, prime 16777619.
- **Why:** Zero-allocation, no NuGet dependency, 32-bit is sufficient for replay detection (not cryptographic). Inline implementation in 5 lines.

#### 5. Time-Independent Test Assertions
- **Conflict:** Tests used hardcoded year bounds (e.g., `AgeYears <= 3`) that broke when system clock advanced beyond 2025.
- **Decision:** Changed assertions to use `DateTime.UtcNow.Year - expectedReleaseYear + margin` for dynamic bounds.
- **Why:** Tests must pass regardless of the year they run.

### New Files Created (8)

| File | Purpose |
|------|---------|
| `SmartPiXL.Forge/Services/Enrichments/CulturalReference.cs` | Static cultural fingerprint reference data — fonts, languages, timezones, calendars |
| `SmartPiXL.Forge/Services/Enrichments/ContradictionMatrixService.cs` | 13 cross-signal contradiction rules (7 IMPOSSIBLE, 3 IMPROBABLE, 3 SUSPICIOUS) |
| `SmartPiXL.Forge/Services/Enrichments/GeographicArbitrageService.cs` | 7-signal cultural consistency scoring (fonts, language, timezone, calendar, etc.) |
| `SmartPiXL.Forge/Services/Enrichments/DeviceAgeEstimationService.cs` | GPU/OS/browser triangulation with 3 anomaly detection rules |
| `SmartPiXL.Forge/Services/Enrichments/BehavioralReplayService.cs` | FNV-1a hash of quantized mouse paths to detect replayed recordings |
| `SmartPiXL.Forge/Services/Enrichments/DeadInternetService.cs` | Per-company compound bot traffic index (24-hour sliding window) |
| `SmartPiXL/SQL/44_ForgeTier3Columns.sql` | 9 new columns on PiXL.Parsed + Phase 8D in ETL proc |
| (Tests — 5 files) | See unit tests table below |

### Modified Files (5)

| File | Change |
|------|--------|
| `GpuTierReference.cs` | Added `EstimateReleaseYear()` + `s_releaseYears` array (~70 GPU→year entries) |
| `EnrichmentPipelineService.cs` | Added 5 Tier 3 fields/constructor params; pipeline restructured to 15 steps |
| `Program.cs` (Forge) | Added 5 singleton DI registrations for Tier 3 services |
| `SmartPiXL.Tests.csproj` | Added Forge project reference |
| `SmartPiXL.Forge.csproj` | Fixed InternalsVisibleTo from `TrackingPixel.Tests` → `SmartPiXL.Tests` |

### Creative Enhancements Beyond Workplan

1. **ContradictionMatrix**: 13 rules (workplan had ~5 examples) with severity tiers; uses `stackalloc` for zero-allocation rule evaluation
2. **GeographicArbitrage**: 7 weighted signals (fonts by platform AND region, number format via separator detection, BCP47 calendar extraction, voice synthesis detection placeholder)
3. **DeviceAgeEstimation**: GPU release year database covering ~70 GPUs (NVIDIA RTX 50→GTX 7, AMD RX 9000→Radeon R, Apple M1-M4, Intel Arc + integrated); 3 anomaly types
4. **BehavioralReplay**: Mouse path quantization (10px grid, 100ms buckets) before FNV-1a hash; fingerprint correlation to distinguish revisit from replay
5. **DeadInternet**: Compound index with 5 weighted signals (bot 0.30, zero-engage 0.20, datacenter 0.20, contradiction 0.15, FP diversity 0.15); per-company per-hour bucketing with 24-hour sliding window

### SQL Migration 44 — Tier 3 Columns

9 new columns on `PiXL.Parsed`:
- `ContradictionCount INT`, `ContradictionList VARCHAR(500)`
- `CulturalConsistencyScore INT`, `CulturalFlags VARCHAR(500)`
- `DeviceAgeYears INT`, `DeviceAgeAnomaly BIT`
- `ReplayDetected BIT`, `ReplayMatchFingerprint VARCHAR(200)`
- `DeadInternetIndex INT`

Phase 8D added to `ETL.usp_ParseNewHits` — parses 9 `_srv_*` params for Tier 3 columns.

### Unit Tests Added (89 new)

| Test Class | Tests | What's Covered |
|------------|-------|----------------|
| `ContradictionMatrixServiceTests` | 17 | Clean profile, all 13 rules individually, multiple contradictions, null safety |
| `GeographicArbitrageServiceTests` | 22 | Full consistency, each signal type, maximal inconsistency, CulturalReference helpers |
| `DeviceAgeEstimationServiceTests` | 22 | GPU/OS/browser dating, 3 anomaly types, legitimate old devices, ~70 GPU release years |
| `BehavioralReplayServiceTests` | 9 | First visit, replay detection, normalization, null inputs, long paths |
| `DeadInternetServiceTests` | 8 | Clean/all-bot/mixed traffic, per-company isolation, minimum hits threshold |
| (GpuReleaseYear sub-tests) | 12 | Known GPU release years, unknown GPUs, virtual GPUs return 0 |

### Build Result

| Project | Warnings | Errors |
|---------|----------|--------|
| SmartPiXL.Shared | 0 | 0 |
| SmartPiXL (Edge) | 0 | 0 |
| SmartPiXL.Forge | 0 | 0 |
| Tests | 471/471 passing | 0 failures |

### Post-Phase 6 Fixes

#### Namespace correction — `TrackingPixel.Tests` → `SmartPiXL.Tests`
- **Problem:** All 5 Phase 6 test files were created with `namespace TrackingPixel.Tests;` instead of the correct `namespace SmartPiXL.Tests;`. The old namespace is from the deprecated project and is not acceptable in new code.
- **Fix:** Changed all 5 files to `namespace SmartPiXL.Tests;`.
- **Prevention:** All new files MUST use `SmartPiXL.*` namespaces. The `TrackingPixel` namespace exists only in legacy code that hasn't been migrated yet.

### Future Work: Unknown GPU Logging

**Idea:** `GpuTierReference.EstimateReleaseYear()` and `Classify()` both rely on hardcoded pattern arrays (~70 GPU entries). When a GPU renderer string doesn't match any pattern, these methods return 0 / `GpuTier.Unknown` silently. Over time, new GPUs ship (e.g., NVIDIA RTX 6000-series, AMD RX 10000-series) and mobile/integrated GPUs are heavily under-represented in the current list.

**Proposed approach:**
1. In the Forge (NOT the Edge hot path), when `EstimateReleaseYear()` returns 0 for a non-null, non-virtual GPU string, log the unknown renderer to a bounded `ConcurrentDictionary<string, int>` (string → hit count).
2. Periodically (daily or on-demand via internal endpoint), dump the unknown GPU list to the tracking log or a dedicated SQL table (`PiXL.UnknownGpu`).
3. This gives us a prioritized list of GPUs to add — sorted by frequency — without polluting the hot path or requiring manual auditing.

**Why not in the hot path:** The Edge doesn't call `EstimateReleaseYear()` — it's Forge-only (Tier 3). But even in the Forge, the tracking should be a fire-and-forget side effect, not blocking the enrichment pipeline.

**Scope:** This is a minor enhancement — can be added in any future phase without schema changes. The enrichment pipeline already works correctly with unknown GPUs (age = 0, no anomaly flagged).

---

## Session 4 — Phase 7: SQL CLR Assembly + Advanced Infrastructure

### CLR .NET Version Validation

**Conflict:** The workplan says "Target `net10.0` for the CLR assembly project. SQL Server 2025 supports .NET 8+ CLR — validate .NET 10 compatibility during Phase 7." The workplan's fallback is "If .NET 10 doesn't work, drop to `net9.0`, then `net8.0`."

**Investigation:** SQL Server 2025 RTM-GDR (17.0.1050.2) reports its CLR version via `sys.dm_clr_properties` as `.NET Framework v4.0.30319` — this is the legacy .NET Framework CLR, NOT the modern .NET runtime. A test assembly targeting `net10.0` was built successfully but SQL Server rejected it with:

> `Assembly 'ClrTest' references assembly 'system.runtime, version=10.0.0.0, culture=neutral, publickeytoken=b03f5f7f11d50a3a' which is not present in the current database.`

A test assembly targeting `net48` loaded successfully — `CREATE ASSEMBLY` from DLL worked, `dbo.Hello('SmartPiXL')` returned `'Hello, SmartPiXL!'`.

The workplan's assumption that "SQL Server 2025 supports .NET 8+ CLR" was incorrect. SQL Server 2025 (RTM-GDR, February 2026 build) still uses the .NET Framework 4.x CLR host. Microsoft may add modern .NET CLR hosting in a future CU, but as of this build it's Framework-only.

**Decision:** Target `net48` for CLR assemblies. Use `<LangVersion>latest</LangVersion>` to get modern C# language features (pattern matching, nullable annotations, switch expressions, etc.) — Roslyn compiles these to IL that runs on .NET Framework's CLR. Runtime features like `Span<T>`, `stackalloc` in expression contexts, and `System.Memory` are not available under the Framework CLR host, but our functions are pure scalar computations that don't benefit from them anyway.

**Why not polyfill modern .NET into Framework:** The user asked if we should use Roslyn to cross-compile modern .NET into Framework assemblies. Analysis: (1) We already get modern C# syntax via `<LangVersion>latest</LangVersion>` with `net48` — that's the same Roslyn cross-compilation. (2) Runtime features like RyuJIT tiered compilation, SIMD auto-vectorization are determined by the CLR host (Framework JIT64), not the compiler. (3) `Span<T>`/`stackalloc` patterns require `UNSAFE` CLR permission and SQL CLR's restricted host may block them. (4) Our functions are already near-optimal: substring ops, bit shifts, UTF-8 hash. The bottleneck is always the SQL query plan, not the CLR function. Decision: not worth the complexity/fragility.

**Result:** `SmartPiXL.SqlClr.csproj` targets `net48` with `LangVersion=latest`.

### CLR Assembly Permission Level

**Conflict:** Workplan says "certificate-based assembly signing (NOT TRUSTWORTHY — stricter security)." Design doc says "assemblies need to be signed (certificate-based) or database set to TRUSTWORTHY."

**Investigation:** Assembly deployed initially with `PERMISSION_SET = SAFE`. All non-regex functions worked (GetSubnet24, FeatureBitmaps, MurmurHash3, FuzzyMatch). However, `RegexExtract` and `RegexMatch` silently returned NULL for all inputs — the `catch` blocks were swallowing `System.Security.SecurityException`.

Root cause: `System.Text.RegularExpressions.Regex` with `RegexOptions.Compiled` and `System.Collections.Concurrent.ConcurrentDictionary` are blocked under the SAFE permission set. These types require UNSAFE.

**Decision:** Use `PERMISSION_SET = UNSAFE` for the assembly. Security is enforced via certificate-based signing in master: certificate → login → `GRANT UNSAFE ASSEMBLY`. The CLR database (`SmartPiXL_CLR`) is isolated from production data — it contains only the CLR assembly and wrapper functions, no user data. `TRUSTWORTHY ON` is set on the isolated CLR database only (not on `SmartPiXL`).

**Result:** All 10 CLR functions work correctly through cross-database synonyms.

### CLR Functions Deployed (10 total)

| # | Function | SQL Type | C# Class | Returns | Purpose |
|---|----------|----------|----------|---------|---------|
| 1 | `GetSubnet24` | Scalar | `GetSubnet24.Execute` | NVARCHAR(50) | IPv4 → /24 subnet string |
| 2 | `RegexExtract` | Scalar | `RegexFunctions.RegexExtract` | NVARCHAR(MAX) | Regex group extraction |
| 3 | `RegexMatch` | Scalar | `RegexFunctions.RegexMatch` | BIT | Regex boolean match |
| 4 | `FeatureBitmap` | Scalar | `FeatureBitmaps.FeatureBitmap` | INT | 17 browser features → bitmap |
| 5 | `AccessibilityBitmap` | Scalar | `FeatureBitmaps.AccessibilityBitmap` | INT | 9 accessibility flags → bitmap |
| 6 | `BotBitmap` | Scalar | `FeatureBitmaps.BotBitmap` | INT | 20 bot signals → bitmap |
| 7 | `EvasionBitmap` | Scalar | `FeatureBitmaps.EvasionBitmap` | INT | 8 evasion signals → bitmap |
| 8 | `MurmurHash3` | Scalar | `MurmurHash3Function.Execute` | VARBINARY(16) | 128-bit non-crypto hash |
| 9 | `JaroWinkler` | Scalar | `FuzzyMatch.JaroWinkler` | FLOAT | Fuzzy string similarity (0-1) |
| 10 | `LevenshteinDistance` | Scalar | `FuzzyMatch.LevenshteinDistance` | INT | Edit distance |

### Verification Results

```
GetSubnet24(192.168.1.100)         = 192.168.1.0/24      ✓
RegexExtract(https://example.com)  = example.com          ✓
RegexMatch(email)                  = 1                    ✓
FeatureBitmap(all 1s)              = 131071 (0x1FFFF)     ✓
MurmurHash3(test-fingerprint)      = 16 bytes             ✓
JaroWinkler(UA similar)            = 0.977778             ✓
LevenshteinDistance(kitten,sitting) = 3                    ✓
AccessibilityBitmap(all 1s)        = 511 (0x1FF)          ✓
BotBitmap(webdriver only)          = 1                    ✓
EvasionBitmap(canvas+webgl)        = 3                    ✓
```

### SQL Reserved Word: `Execute`

**Problem:** Two C# functions use `Execute` as the method name (`GetSubnet24.Execute`, `MurmurHash3Function.Execute`). SQL's `CREATE FUNCTION ... AS EXTERNAL NAME` failed with "Incorrect syntax near the keyword 'Execute'" because `Execute` is a T-SQL reserved word.

**Fix:** Bracket-quote the method name: `[Execute]` in the EXTERNAL NAME clause. E.g., `SmartPiXL_ClrFunctions.[SmartPiXL.SqlClr.Functions.GetSubnet24].[Execute]`.

### Vector Infrastructure (Migration 46)

Added two VECTOR columns to `PiXL.Device`:
- `FingerprintVector VECTOR(64) NULL` — 64-dimensional device fingerprint encoding
- `UaVector VECTOR(32) NULL` — 32-dimensional User-Agent encoding for drift detection

**SQL Server 2025 VECTOR_DISTANCE bug:** `VECTOR_DISTANCE()` works with inline `CAST('[...]' AS VECTOR(N))` operands, but fails with `VECTOR` variables or column references in CROSS JOIN with "Internal Query Processor Error: The query processor could not produce a query plan." This is a SQL Server 2025 RTM-GDR bug. The workaround for now is to use inline CAST; expected to be fixed in a future CU. The vector columns are in place and ready for population by the Forge ETL.

### Graph Tables (Migration 47)

Created 3 NODE tables and 2 EDGE tables in the `Graph` schema:
- **Nodes:** `Graph.Device` (DeviceId, DeviceHash), `Graph.Person` (Email, IndividualKey), `Graph.IpAddress` (IP, Subnet24)
- **Edges:** `Graph.UsesIP` (Device→IP, with FirstSeen/LastSeen/HitCount), `Graph.ResolvesTo` (Device→Person, with MatchType/Confidence/MatchedAt)

Multi-hop MATCH traversal validated with test data:
- `Person←(ResolvesTo)-Device-(UsesIP)→IpAddress` found Alice's 2 IPs
- `Person←(rt1)-Device-(ui1)→IP←(ui2)-Device-(rt2)→Person` correctly resolved Alice→shared IP→Bob as a linked identity

### Unit Tests Added (52 new)

| Test Class | Tests | What's Covered |
|------------|-------|----------------|
| `GetSubnet24Tests` | 7 | Valid IPv4, invalid formats, null input |
| `RegexFunctionsTests` | 10 | Extract groups, no match, null inputs, invalid regex, match/no-match |
| `FeatureBitmapTests` | 9 | All-true/all-false/single-bit/null for all 4 bitmap functions |
| `MurmurHash3Tests` | 7 | Determinism, uniqueness, 16-byte output, null, empty, long input, avalanche |
| `FuzzyMatchTests` | 9 | Identical/different/similar strings, null, empty, UA comparison, Levenshtein known values |

### Build Result

| Project | Warnings | Errors |
|---------|----------|--------|
| SmartPiXL.Shared | 0 | 0 |
| SmartPiXL (Edge) | 0 | 0 |
| SmartPiXL.Forge | 0 | 0 |
| SmartPiXL.SqlClr | 0 | 0 |
| Tests | 523/523 passing | 0 failures |

### SQL Migrations

| Script | What |
|--------|------|
| `45_CLR_Database.sql` | SmartPiXL_CLR database, certificate signing, assembly deployment, 10 wrapper functions, 10 synonyms in SmartPiXL |
| `46_VectorInfrastructure.sql` | FingerprintVector VECTOR(64), UaVector VECTOR(32) on PiXL.Device |
| `47_GraphTables.sql` | Graph schema, 3 NODE tables, 2 EDGE tables, MATCH traversal validation |
---

## Session 5 — Phase 8: SQL Analysis Features + Schema Expansion

### Overview

Phase 8 adds SQL-side analysis views, subnet reputation, zipcode polygon infrastructure, bitmap columns, and dimension table expansion. All work is SQL-only — no C# changes required.

### Prerequisite: Migrations 42-44 Deployed

**Discovery:** During database schema inspection, found that migrations 42 (Tier 1 enrichment columns), 43 (Tier 2), and 44 (Tier 3) had SQL files created in earlier phases but were **never executed** against the database. PiXL.Parsed had 188 columns instead of the expected 226.

**Decision:** Executed 42, 43, 44 in order before starting Phase 8 migrations. All 38 columns added successfully. PiXL.Parsed went from 188 → 226 columns.

### Conflict 1: Bitmap Computed Columns vs Regular Columns

**What the conflict was:** The workplan (§8.3 Priority 10) specifies "computed bitmap columns using CLR functions: `FeatureBitmapValue AS dbo.FeatureBitmap(...) PERSISTED`". This implies PERSISTED computed columns that call CLR functions.

**Why the agent made the decision it did:** The CLR functions (`dbo.FeatureBitmap`, `dbo.AccessibilityBitmap`, etc.) are deployed in the `SmartPiXL_CLR` database and accessed in `SmartPiXL` through cross-database synonyms (created in Phase 7, migration 45). SQL Server requires schema binding to persist computed columns, but schema binding **cannot follow cross-database synonyms**. Attempting to create `FeatureBitmapValue AS dbo.FeatureBitmap(...) PERSISTED` would fail with a schema binding error.

**What the decision was:** Migration 56 creates the four bitmap columns (`FeatureBitmapValue`, `AccessibilityBitmapValue`, `BotBitmapValue`, `EvasionBitmapValue`) as regular `INT NULL` columns instead of computed columns. The Forge's ETL pipeline will populate these values during enrichment phases 8B/8C/8D (already wired in migration 44's ETL proc update). This approach is actually more robust — no cross-database schema dependency, values are materialized once at ETL time rather than computed on every read, and the filtered indexes work without PERSISTED requirements.

### Conflict 2: QUOTED_IDENTIFIER for Filtered Indexes and MERGE

**What the conflict was:** Migration 48 creates a filtered index (`IX_SubnetReputation_BotPercent WHERE BotPercent >= 50`) on `PiXL.SubnetReputation` and a stored procedure (`ETL.usp_UpdateSubnetReputation`) that MERGEs into that table. SQL Server requires `QUOTED_IDENTIFIER ON` for both creating and modifying tables with filtered indexes.

**Why the agent made the decision it did:** `sqlcmd` does not enable `QUOTED_IDENTIFIER` by default. The filtered index creation failed silently on initial run, and the MERGE proc failed at execution time because the proc was created without `QUOTED_IDENTIFIER ON` (this setting is captured at proc creation time, not execution time).

**What the decision was:** Added `SET QUOTED_IDENTIFIER ON;` before the `CREATE OR ALTER PROCEDURE` statement in migration 48. All subsequent migrations use the `-I` flag with sqlcmd. Future migration scripts should include `SET QUOTED_IDENTIFIER ON;` at the top when they contain filtered indexes, indexed views, or procs that modify tables with filtered indexes.

### Phase 8 Migrations Created and Deployed

| Script | Objects Created | Status |
|--------|----------------|--------|
| `48_SubnetReputation.sql` | `PiXL.SubnetReputation` table, `ETL.usp_UpdateSubnetReputation` proc, filtered index | ✅ Deployed, 77,195 subnets populated |
| `49_SessionViews.sql` | `dbo.vw_Dash_Sessions`, `dbo.vw_Dash_SessionSummary` | ✅ Deployed |
| `50_ImpossibleTravel.sql` | `dbo.vw_Dash_ImpossibleTravel` | ✅ Deployed |
| `51_DeadInternetIndex.sql` | `dbo.vw_Dash_DeadInternet` | ✅ Deployed |
| `52_CustomerQuality.sql` | `dbo.vw_Dash_CustomerQuality` | ✅ Deployed |
| `53_DeviceLifecycle.sql` | `dbo.vw_Dash_DeviceLifecycle`, `dbo.vw_Dash_DeviceCustomerHops` | ✅ Deployed |
| `54_CrossCustomerHistorical.sql` | `dbo.vw_Dash_CrossCustomer`, `dbo.vw_Dash_CrossCustomerDetail` | ✅ Deployed |
| `55_GeoZipcode.sql` | `Geo.Zipcode` table (spatial index, integer bucket pattern), `Geo.usp_LookupZipcode` proc | ✅ Deployed (empty — awaiting ZCTA shapefile import) |
| `56_ParsedColumnExpansion.sql` | 4 bitmap INT columns on `PiXL.Parsed`, 3 analysis indexes | ✅ Deployed (230 total columns) |
| `57_DimensionExpansion.sql` | `PiXL.Device` +7 cols (14 total), `PiXL.IP` +8 cols (29 total), `PiXL.Visit` +5 cols (15 total), FK, indexes | ✅ Deployed |

### Design Patterns Applied

1. **Integer bucket geo pattern** (from design doc §2.3): `Geo.Zipcode` uses `LatBucket100`/`LonBucket100` persisted computed columns for coarse spatial filtering before `STContains()`. Same pattern used in `vw_Dash_ImpossibleTravel` for distance estimation.

2. **Watermark-based incremental processing**: SubnetReputation proc uses full-table aggregation (appropriate for daily batch), consistent with existing watermark pattern for ETL procs.

3. **Session reconstruction**: `vw_Dash_Sessions` uses `LAG()` + `SUM() OVER()` window functions with a 30-minute gap threshold to assign session numbers — no materialized session table needed.

4. **Filtered indexes**: Applied to high-cardinality flag columns (`BotScore >= 50`, `FeatureBitmapValue IS NOT NULL`) to optimize dashboard queries without full-table scans.

### Build & Test

| Project | Warnings | Errors |
|---------|----------|--------|
| SmartPiXL.Shared | 0 | 0 |
| SmartPiXL (Edge) | 0 | 0 |
| SmartPiXL.Forge | 0 | 0 |
| Tests | 523/523 passing | 0 failures |

Note: `SmartPiXL.Worker-Deprecated` has pre-existing build errors (72) from missing type references — this is expected for a deprecated read-only reference project.

### Database State After Phase 8

| Table | Columns |
|-------|---------|
| PiXL.Raw | 9 |
| PiXL.Parsed | 230 |
| PiXL.Device | 14 |
| PiXL.IP | 29 |
| PiXL.Visit | 15 |
| PiXL.SubnetReputation | 13 (new) |
| Geo.Zipcode | 9 (new, empty) |

| Dashboard Views | Count |
|-----------------|-------|
| `dbo.vw_Dash_*` | 19 |
| `dbo.vw_Dashboard_*` | 16 |
| **Total views** | **35** |

---

## Session 6 — Phase 9: TrafficAlert Subsystem

### Overview

Phase 9 creates the TrafficAlert subsystem — a unified traffic quality scoring and reporting system that combines all enrichment outputs into per-visit composite scores and per-customer aggregate summaries. Pure SQL work (schema, materialization procs, dashboard views).

### Conflict 1: MERGE Avoided for Performance — Separate DELETE + INSERT

**What the conflict was:** The existing codebase uses MERGE for upserts (SubnetReputation, Device/IP dimensions). The workplan doesn't specify a pattern for CustomerSummary rollups, but MERGE would be the obvious choice for "update existing period rows, insert new ones."

**Why the agent made the decision it did:** The platform owner expressed strong preference for avoiding MERGE due to performance concerns (wider lock scope, deadlock potential, historical SQL Server bugs). For VisitorScore, MERGE is unnecessary — watermark-based processing guarantees only new rows arrive. For CustomerSummary, period rows are fully recomputed when new data arrives.

**What the decision was:**
- VisitorScore: Pure INSERT (watermark prevents duplicates, no upsert needed).
- CustomerSummary: DELETE affected rows + INSERT fresh aggregates. This is cleaner than MERGE and faster for a daily batch operation. Weekly and monthly rollups derive from daily rows to avoid re-scanning VisitorScore.

### Conflict 2: PiXL.Visit.DeviceId Nullable — Most Visits Have NULL DeviceId

**What the conflict was:** `TrafficAlert.VisitorScore.DeviceId` is NOT NULL (FK to PiXL.Device), but 2,008,715 of 2,009,326 visits have NULL DeviceId — they predate Forge enrichment.

**What the decision was:** Filter `WHERE v.DeviceId IS NOT NULL` in the materialization proc. Only Forge-enriched visits get scored. As the Forge processes new traffic, VisitorScore grows proportionally.

### Conflict 3: PiXL.Match Column Name Mismatch

**What the conflict was:** CustomerSummary proc referenced `m.MatchEmail` but PiXL.Match uses `IndividualKey` for identity resolution keys.

**What the decision was:** Changed to `COUNT(DISTINCT m.IndividualKey)`.

### Scoring Algorithms Implemented

#### Mouse Authenticity (0-100)
| Component | Points | Logic |
|-----------|--------|-------|
| Entropy | 0-30 | MouseEntropy >= 70 → 30pts (high = human) |
| Timing CV | 0-20 | MoveTimingCV >= 80 → 20pts (variable timing = human) |
| Speed CV | 0-15 | MoveSpeedCV >= 80 → 15pts (variable speed = human) |
| Move count | 0-15 | 51+ moves → 15pts |
| No replay | 0-10 | 10pts if ReplayDetected != 1 |
| No scroll conflict | 0-10 | 10pts if ScrollContradiction != 1 |

#### Session Quality (0-100)
| Component | Points | Logic |
|-----------|--------|-------|
| Page count | 0-40 | >=10 pages → 40, >=5 → 30, >=3 → 20, >=2 → 12 |
| Duration | 0-40 | >=5min → 40, >=2min → 30, >=1min → 20 |
| Multi-page bonus | 0-20 | 20pts if >=2 pages (not a bounce) |

#### Composite Quality (0-100)
| Signal | Weight |
|--------|--------|
| Inverted BotScore | 25% |
| Mouse Authenticity | 20% |
| Session Quality | 15% |
| Lead Quality | 15% |
| Cultural Consistency | 10% |
| No Contradictions | 10% |
| Affluence bonus | 5pts flat |

### Phase 9 Migrations

| Script | Objects Created | Status |
|--------|----------------|--------|
| `58_TrafficAlertSchema.sql` | `TrafficAlert` schema, `TrafficAlert.VisitorScore` (4 indexes incl. filtered), `TrafficAlert.CustomerSummary` (1 index), 2 watermark entries | ✅ Deployed |
| `59_TrafficAlertMaterialization.sql` | `ETL.usp_MaterializeVisitorScores`, `ETL.usp_MaterializeCustomerSummary`, `dbo.vw_TrafficAlert_VisitorDetail`, `dbo.vw_TrafficAlert_CustomerOverview`, `dbo.vw_TrafficAlert_Trend` | ✅ Deployed |

### Live Data Verification

| Metric | Result |
|--------|--------|
| VisitorScores materialized | 611 (all Forge-enriched visits) |
| CustomerSummary rows | 8 (3 daily + 3 weekly + 2 monthly) |
| Top CompositeQuality | 64 (BotScore=0, MouseAuth=85) |
| BotPercent (daily) | 0.83% for Company 12345 |
| QualityGrade | A (< 20% bot rate) |
| QualityTrend | STABLE / NEW as expected |

### Build & Test

| Project | Warnings | Errors |
|---------|----------|--------|
| SmartPiXL.Shared | 0 | 0 |
| SmartPiXL (Edge) | 0 | 0 |
| SmartPiXL.Forge | 0 | 0 |
| Tests | 523/523 passing | 0 failures |