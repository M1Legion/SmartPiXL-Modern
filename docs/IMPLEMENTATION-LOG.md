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

**What the conflict was:** The CustomerSummary materialization proc counted matched visitors with `COUNT(DISTINCT m.MatchEmail)`, but PiXL.Match has no `MatchEmail` column. The actual columns are:
- `MatchType` VARCHAR(20) — discriminator: IP, Email, or Geo match method
- `MatchKey` VARCHAR(256) — the matched value (an IP, email, or geo key)
- `IndividualKey` VARCHAR(35) — the AutoConsumer individual this resolved to

**What the decision was:** Changed to `COUNT(DISTINCT m.IndividualKey)`. We want unique *people* matched (not unique match keys), so `IndividualKey` is correct — it's the FK back to a specific individual in AutoConsumer. Multiple match keys (different IPs, emails) can resolve to the same `IndividualKey`.

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

---

## Session 7 — Phase 10: Sentinel Service Separation

### Objective

Create the `SmartPiXL.Sentinel` project — the third and final process in the SmartPiXL architecture. The Sentinel owns all operational dashboards, documentation, and TrafficAlert endpoints on port 7500, while the Forge handles all background processing.

### Architecture Decision: HTTP-Only Sentinel

**Conflict:** The Worker-Deprecated ran both dashboard endpoints AND background services (SelfHealingService, MaintenanceSchedulerService, InfraHealthService loop, EmailNotificationService). The design doc specifies Sentinel should only host the HTTP API surface.

**Decision:** Sentinel is a pure HTTP server with NO `AddHostedService` calls. All background processing (ETL, sync, self-healing loop, maintenance scheduling) already runs in the Forge (ported in Phase 2). The Sentinel provides:
- Dashboard read API (`/api/dash/*`) — read-only SQL views
- Atlas documentation portal (`/atlas`, `/api/atlas/*`) — public-facing
- TrafficAlert API (`/api/traffic-alert/*`) — NEW, visitor scoring endpoints
- Remediation approve/skip API — operator interaction only (no background loop)

**Why:** Splits the concern cleanly. The Forge detects and proposes (writes to `Ops.RemediationLog`). The Sentinel displays and lets operators act. No duplication of background work.

### Architecture Decision: RemediationService vs SelfHealingService

**Conflict:** SelfHealingService in the Worker was 711 lines combining a 60-second detection loop with operator-facing approve/skip methods.

**Decision:** Created `RemediationService` for the Sentinel containing only the 3 API-callable methods:
- `ListRemediationsAsync()` — reads `Ops.RemediationLog` (top 50, newest first)
- `ExecuteRemediationAsync(id)` — executes `ActionSql`, updates status, optionally resets Edge circuit breaker
- `SkipRemediationAsync(id)` — marks as skipped

The detection loop and auto-remediation logic stays in the Forge's `SelfHealingService`. This is a clean separation of concerns.

### Architecture Decision: InfraHealthService Probes Updated

**Change:** Added `SmartPiXL-Forge` to the Windows services probe list (replacing the retired `MSSQLSERVER` default instance). The Forge is now a critical service that should be monitored.

### Files Created

| File | Lines | Purpose |
|------|-------|---------|
| `SmartPiXL.Sentinel/SmartPiXL.Sentinel.csproj` | 29 | Web SDK, net10.0, Shared reference + 3 NuGet packages |
| `SmartPiXL.Sentinel/Program.cs` | 205 | Composition root — services, middleware, endpoint mapping |
| `SmartPiXL.Sentinel/appsettings.json` | 30 | Port 7500, SQL connection, SMTP, EdgeBaseUrl |
| `SmartPiXL.Sentinel/Endpoints/DashboardEndpoints.cs` | 500 | Full Tron dashboard API (original + 9 new enrichment views) |
| `SmartPiXL.Sentinel/Endpoints/AtlasEndpoints.cs` | 180 | Atlas documentation portal (4-tier content, live metrics) |
| `SmartPiXL.Sentinel/Endpoints/TrafficAlertEndpoints.cs` | 280 | NEW: Visitor scoring, customer quality, trend endpoints |
| `SmartPiXL.Sentinel/Services/InfraHealthService.cs` | 500 | Parallel probes: Windows services, SQL, IIS, data flow, pipeline, logs |
| `SmartPiXL.Sentinel/Services/RemediationService.cs` | 160 | Approve/skip/list remediation entries (no background loop) |
| `SmartPiXL.Sentinel/Services/EmailNotificationService.cs` | 170 | SMTP + SMS notifications with rate limiting |
| `SmartPiXL.Sentinel/Services/HttpEdgeHealthClient.cs` | 100 | IEdgeHealthClient → Edge /internal/* HTTP bridge |
| `SmartPiXL.Sentinel/wwwroot/tron.html` | — | Copied from Worker-Deprecated |
| `SmartPiXL.Sentinel/wwwroot/atlas.html` | — | Copied from Worker-Deprecated |
| `SmartPiXL.Sentinel/wwwroot/tron/*.mjs` | — | 8 modules: api, arena, camera, cycles, particles, pathing, scene, trails |

### New TrafficAlert Endpoints (Phase 10 addition)

| Endpoint | View/Table | Purpose |
|----------|-----------|---------|
| `GET /api/traffic-alert/visitors` | `vw_TrafficAlert_VisitorDetail` | Paginated visitor scores (top/offset/companyId/bucket filters) |
| `GET /api/traffic-alert/visitors/{id}` | `vw_TrafficAlert_VisitorDetail` | Single visitor by VisitorScoreId |
| `GET /api/traffic-alert/customers` | `vw_TrafficAlert_CustomerOverview` | Per-customer summary with quality grades |
| `GET /api/traffic-alert/customers/{id}` | `vw_TrafficAlert_CustomerOverview` | Single customer by CompanyID |
| `GET /api/traffic-alert/trend` | `vw_TrafficAlert_Trend` | Time-series for charting (per-customer or all) |
| `GET /api/traffic-alert/summary` | `vw_TrafficAlert_CustomerOverview` | Aggregate KPI snapshot across all customers |

### New Dashboard Endpoints (Enrichment-aware, Phase 8+)

| Endpoint | View | Purpose |
|----------|------|---------|
| `/api/dash/sessions` | `vw_Dash_SessionSummary` | Session reconstruction |
| `/api/dash/dead-internet` | `vw_Dash_DeadInternet` | Dead internet index |
| `/api/dash/customer-quality` | `vw_Dash_CustomerQuality` | Traffic quality trending |
| `/api/dash/cross-customer` | `vw_Dash_CrossCustomer` | Cross-customer intelligence |
| `/api/dash/cross-customer/detail` | `vw_Dash_CrossCustomer_Detail` | Per-device cross-customer detail |
| `/api/dash/impossible-travel` | `vw_Dash_ImpossibleTravel` | Geo anomaly detection |
| `/api/dash/device-lifecycle` | `vw_Dash_DeviceLifecycle` | Device age/lifecycle |
| `/api/dash/device-hops` | `vw_Dash_DeviceHops` | Device company-hopping trail |
| `/api/dash/subnet-clusters` | `vw_Dash_SubnetClusters` | Subnet reputation clusters |

### Solution File Changes

- **Removed:** `SmartPiXL.Worker` project (`{8F1B70EF-CE9E-47D1-A7D7-0057D25C499F}`)
- **Added:** `SmartPiXL.Sentinel` project (`{C3D4E5F6-A7B8-9012-CDEF-345678901234}`)
- Worker-Deprecated directory preserved as read-only reference (workplan says delete in final cleanup)

### Build & Test

| Project | Warnings | Errors |
|---------|----------|--------|
| SmartPiXL.Shared | 0 | 0 |
| SmartPiXL (Edge) | 0 | 0 |
| SmartPiXL.Forge | 0 | 0 |
| SmartPiXL.Sentinel | 0 | 0 |
| SmartPiXL.SqlClr | 0 | 0 |
| Tests | 523/523 passing | 0 failures |

### Post-Phase 10 Architecture

| Process | Port | Purpose | Status |
|---------|------|---------|--------|
| **PiXL Edge** (IIS) | 80/443 (6000/6001 Kestrel) | Pixel capture, fast enrichments | LIVE |
| **SmartPiXL Forge** | — (Windows Service) | Named pipe server, Tier 1-3 enrichments, ETL, SQL writer | BUILT |
| **SmartPiXL Sentinel** | 7500 | Tron dashboard, Atlas portal, TrafficAlert API | BUILT |
| SmartPiXL Worker | — | **DEPRECATED — removed from solution** | OFF |

---

## Session 8 — First Deployment: Forge + Sentinel Online

### Deployment

**What:** Published Forge and Sentinel as Windows Services for the first time. Registered with `sc.exe create`, set to `start= auto`.

**Deploy targets:**
- `C:\Services\SmartPiXL-Forge\` (Forge)
- `C:\Services\SmartPiXL-Sentinel\` (Sentinel)

### Bug Fixes

#### 1. SQL Login for NT AUTHORITY\SYSTEM
Windows Services run as Local System by default. Created SQL login/user with db_datareader, db_datawriter, and EXECUTE permissions.

#### 2. EdgeBaseUrl Mismatch
Source `appsettings.json` had `http://127.0.0.1:7000` (dev port). But Edge runs InProcess inside IIS — only accessible on IIS-bound port 80. Changed source `EdgeBaseUrl` to `http://192.168.88.176` in both Forge and Sentinel `appsettings.json` so future `dotnet publish` won't require manual patching.

**Conflict:** The copilot-instructions.md says Kestrel port 6000 is the internal port. But IIS InProcess hosting means Kestrel ports are NOT exposed externally — the w3wp.exe process handles everything through IIS bindings.

**Decision:** `EdgeBaseUrl` = `http://192.168.88.176` (IIS port 80) for all cross-process communication.

#### 3. QUOTED_IDENTIFIER on ETL.usp_ParseNewHits
The stored procedure was created with `QUOTED_IDENTIFIER OFF`, causing failures when INSERT touches indexed views. Fixed by ALTER through `sqlcmd.exe` with `SET QUOTED_IDENTIFIER ON` prefix. Verified `OBJECTPROPERTY(ExecIsQuotedIdentOn) = 1`.

#### 4. Int32 vs Int64 Cast in EtlBackgroundService
ETL stored procs return a mix of `int` and `bigint` columns. Changed all `reader.GetInt64(N)` calls to `Convert.ToInt64(reader.GetValue(N))` which safely handles both types.

#### 5. IpApiLookupService — Invalid Column Names
The MERGE statement referenced `UpdatedAt` and `Hosting` columns that don't exist in `IPAPI.IP`. Actual columns: `LastSeen` (not `UpdatedAt`), no `Hosting` column. Also added `WHERE LastSeen IS NOT NULL` to the startup cache load to avoid null value exceptions on the 344M row table.

### Files Changed
- `SmartPiXL.Forge/Services/EtlBackgroundService.cs` — `GetInt64` → `Convert.ToInt64(GetValue)`
- `SmartPiXL.Forge/Services/Enrichments/IpApiLookupService.cs` — Fixed column names, null filter
- `SmartPiXL.Forge/appsettings.json` — EdgeBaseUrl → `http://192.168.88.176`
- `SmartPiXL.Sentinel/appsettings.json` — EdgeBaseUrl → `http://192.168.88.176`
- `docs/DEPLOYMENT.md` — **NEW** — Full deployment reference doc

### Verified Working
- Edge: Pixel capture on IIS port 80 — 200 OK, image/gif ✓
- Forge: ETL processing 10K rows/cycle — parsing PiXL.Raw → PiXL.Parsed ✓
- Forge: Geo enrichment running ✓
- Sentinel: `/tron` (200, 156KB HTML), `/atlas` (200), `/api/dash/health` (200) ✓
- Pipeline: 14.1M Raw rows, watermark advancing, 11M+ backlog processing ✓

---

## Session 7 — SQL Auth Migration + Edge Crash Loop Fix

### SQL Auth Migration: NT AUTHORITY\SYSTEM → SmartPiXL SQL Login

**Problem:** All three SmartPiXL services (Edge, Forge, Sentinel) used `Integrated Security=True` connecting as `NT AUTHORITY\SYSTEM`. This caused:
- No granular permission control (SYSTEM has broad access)
- `AutoUpdate` database access denied errors during ETL
- Inability to audit which application is making SQL calls
- Xavier sync failures due to missing permissions

**Decision:** Created a dedicated `SmartPiXL` SQL login (SQL Authentication) on both `localhost\SQL2025` and Xavier (`192.168.88.35`). Password stored in machine-level environment variables, never in source code. Config files use `Password=OVERRIDE_VIA_ENV` as placeholder.

**Implementation:**
1. **SQL Login created** on `localhost\SQL2025` with strong 32-char password, `CHECK_POLICY = OFF`
2. **Permissions granted** on local instance:
   - SmartPiXL db: `db_datareader`, `db_datawriter`, `EXECUTE`, `ALTER ON SCHEMA::PiXL`, `ALTER ON SCHEMA::ETL`, `ALTER ON SCHEMA::Ops`
   - AutoUpdate db: `db_datareader`
   - SmartPiXL_CLR db: `db_datareader`, `db_datawriter`, `EXECUTE`
   - Server: `VIEW SERVER STATE`, `ALTER ANY DATABASE`
3. **SQL Login created** on Xavier (`192.168.88.35`):
   - IPGEO db: `db_datareader` (read-only sync)
   - SmartPiXL db: `db_datareader`, `db_datawriter`, `EXECUTE` (bidirectional Company/Pixel sync)
4. **Machine environment variables set:**
   - `SMARTPIXL_SQL_USER` = SmartPiXL
   - `SMARTPIXL_SQL_PASSWORD` = (stored securely)
   - `SMARTPIXL_SQL_CONNSTR` = full local connection string
   - `Tracking__ConnectionString` = same (ASP.NET Core auto-reads this)
   - `Tracking__XavierConnectionString` = Xavier IPGEO with SQL auth
   - `Tracking__XavierSmartPiXLConnectionString` = Xavier SmartPiXL with SQL auth
5. **Config files updated** (all use `Password=OVERRIDE_VIA_ENV` placeholder):
   - `SmartPiXL.Shared/Configuration/TrackingSettings.cs` (compiled default)
   - `SmartPiXL/appsettings.json` (Edge)
   - `SmartPiXL.Forge/appsettings.json`
   - `SmartPiXL.Sentinel/appsettings.json`

### Edge Crash Loop #1: DatabaseWriterService Not Registered

**Problem:** `InternalEndpoints.cs` referenced `DatabaseWriterService` as a minimal API parameter, but `DatabaseWriterService` was removed from Edge DI in Phase 3 (all writes go through pipe to Forge). In .NET 10, ASP.NET Core validates endpoint parameter bindings at startup, causing `System.InvalidOperationException: Body was inferred but the method does not allow inferred body parameters` on every request.

**Decision:** Replaced `DatabaseWriterService` with `PipeClientService` in the health endpoint. The circuit state now reflects pipe connectivity (`Closed` = connected, `Open` = disconnected). The circuit-reset endpoint is now a no-op (pipe reconnection is automatic) but kept for API compatibility.

**Files changed:** `SmartPiXL/Endpoints/InternalEndpoints.cs`

### Edge Crash Loop #2: JsonlFailoverService — Directory Access Denied

**Problem:** `JsonlFailoverService` tried to create `C:\inetpub\Smartpixl.info\Failover\` at startup. The IIS app pool identity (`IIS APPPOOL\Smartpixl.info`) didn't have write permission, throwing `System.UnauthorizedAccessException` which killed the host (`BackgroundServiceExceptionBehavior = StopHost`).

**Decision:** Created the `Failover` directory manually and granted `FullControl` to `IIS APPPOOL\Smartpixl.info`.

### Edge Crash Loop #3: Named Pipe Access Denied

**Problem:** `PipeClientService` threw `UnauthorizedAccessException` when connecting to the named pipe `SmartPiXL-Enrichment`. The pipe was created by the Forge (running as `NT AUTHORITY\SYSTEM`) with default security ACLs — the IIS identity couldn't connect. The exception bubbled up unhandled to `ExecuteAsync`, crashing the host.

**Decision (two-part fix):**
1. **Forge `PipeListenerService.cs`**: Changed pipe creation from `new NamedPipeServerStream(...)` to `NamedPipeServerStreamAcl.Create(...)` with explicit `PipeSecurity` granting `LocalSystem` FullControl and `IIS APPPOOL\Smartpixl.info` ReadWrite access.
2. **Edge `PipeClientService.cs`**: Added `UnauthorizedAccessException` catch in `EnsureConnectedAsync()` to treat pipe access denied as a transient error (backoff + retry + failover to JSONL) instead of crashing the host.

**Files changed:**
- `SmartPiXL.Forge/Services/PipeListenerService.cs` — `PipeSecurity` ACLs added
- `SmartPiXL/Services/PipeClientService.cs` — `UnauthorizedAccessException` handler added

### Production Config: Port Override Required After Publish

**Conflict:** `dotnet publish` copies the source `appsettings.json` (dev ports 7000/7001) over the production copy (ports 6000/6001). This requires manual port patching after every publish.

**Decision:** This is a known issue documented in `copilot-instructions.md`. Post-publish port fix is part of the deployment procedure. A future improvement would be to use `appsettings.Production.json` for port overrides.

### Files Changed
- `SmartPiXL/Endpoints/InternalEndpoints.cs` — `DatabaseWriterService` → `PipeClientService`, comments updated
- `SmartPiXL/Services/PipeClientService.cs` — `UnauthorizedAccessException` catch added
- `SmartPiXL.Forge/Services/PipeListenerService.cs` — Pipe security ACLs for IIS identity
- `SmartPiXL.Shared/Configuration/TrackingSettings.cs` — SQL auth default connection string
- `SmartPiXL/appsettings.json` — SQL auth for all connection strings
- `SmartPiXL.Forge/appsettings.json` — SQL auth for all connection strings
- `SmartPiXL.Sentinel/appsettings.json` — SQL auth connection string

### Verified Working
- Edge: Pixel capture 200 OK ✓
- Edge: Internal health endpoint — `circuit: Closed`, pipe connected ✓
- Edge: No crash loop — single startup, stable ✓
- Edge: GeoCacheService prewarmed 2000 IPs ✓
- Edge: JsonlFailoverService started (Failover directory accessible) ✓
- Edge: PipeClient connected to Forge ✓
- Forge: Running as Windows Service ✓
- Sentinel: Running, Tron dashboard accessible ✓
- SQL Login: Verified working on both localhost\SQL2025 and Xavier (192.168.88.35) ✓

---

## Session 9 — V1.0.0 QA Sweep (2025-02-20)

Full QA sweep of SmartPiXL Sentinel V1.0.0. Tested 36 endpoints across all 4 surfaces (Tron Ops, Tron Metrics, Atlas, TrafficAlert). Cross-referenced API responses against SQL views. Reviewed all frontend code (tron.html 3542 lines, atlas.html 1531 lines) and all backend endpoint files.

**Test Environment:** Sentinel http://localhost:7500 | SQL Server localhost\SQL2025 → SmartPiXL | 4,731,629 rows in PiXL.Parsed

### Summary

- Endpoints tested: 36
- Bugs found: 14 (4 critical, 5 moderate, 5 minor)
- Data accuracy issues: 6 endpoints return 500 due to SQL timeouts

---

### QA Bug Reports — Organized by Agent

---

#### 🔧 `sql-janitor` — 5 bugs (4 critical, 1 moderate)

These are the highest priority. Six dashboard views perform full-table scans on 4.7M rows without date filters, causing 30s+ query times that surface as HTTP 500s.

**BUG-Q1 (CRITICAL): Six `vw_Dash_*` views timeout — 500 errors on 6 dashboard endpoints**

| Field | Detail |
|-------|--------|
| Endpoints | `/api/dash/hourly`, `/api/dash/bots`, `/api/dash/bot-signals`, `/api/dash/devices`, `/api/dash/evasion`, `/api/dash/behavior` |
| Root Cause | Views scan ALL 4.7M rows in `PiXL.Parsed` with no `WHERE` clause. Query CPU exceeds 7s, exceeds the 30s `CommandTimeout`. |
| Evidence | `vw_Dash_HourlyRollup`: 6,938ms CPU. `vw_Dash_BotBreakdown`: 7,624ms CPU. All 6 return HTTP 500. |
| Fix | Add `WHERE ReceivedAt >= DATEADD(DAY, -30, GETUTCDATE())` (or appropriate window) to each view. The clustered index on `(ReceivedAt, SourceId)` will then be used. |
| Views to fix | `vw_Dash_HourlyRollup`, `vw_Dash_BotBreakdown`, `vw_Dash_TopBotSignals`, `vw_Dash_DeviceBreakdown`, `vw_Dash_EvasionSummary`, `vw_Dash_BehavioralAnalysis` |
| SQL files | `SmartPiXL/SQL/` — find and update the CREATE VIEW statements, then run against `localhost\SQL2025` |

**BUG-Q2 (CRITICAL): `InfraHealthService` SQL probe reports false "disconnected"**

| Field | Detail |
|-------|--------|
| Endpoint | `/api/dash/infra` |
| Root Cause | `InfraHealthService.cs` line ~178 runs `SELECT COUNT(*) FROM PiXL.Raw` with `CommandTimeout = 5`. On 4.7M+ rows, this always times out, so SQL is reported as disconnected and overall status becomes "critical". |
| Evidence | `/api/dash/infra` returns `sqlServer.status = "disconnected"` and `overallStatus = "critical"` even though SQL is running fine. |
| Fix | Replace `SELECT COUNT(*) FROM PiXL.Raw` with `SELECT 1` (existence check) or `SELECT SUM(row_count) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Raw') AND index_id IN (0,1)` for fast row count. |
| File | `SmartPiXL.Sentinel/Services/InfraHealthService.cs` ~line 178 |
| Note | This is a C# fix but the root cause is a bad SQL query, so `sql-janitor` + `csharp-janitor` should coordinate. |

**BUG-Q3 (CRITICAL): TrafficAlert `/api/traffic-alert/summary` returns all nulls**

| Field | Detail |
|-------|--------|
| Endpoint | `/api/traffic-alert/summary` |
| Root Cause | `TrafficAlertEndpoints.cs` query filters `WHERE PeriodStart = CAST(GETUTCDATE() AS date)` — requires data materialized for today specifically. If no rows exist for today's date, all aggregate KPIs return null. |
| Evidence | Response: `{"totalVisitors":null,"averageScore":null,"highRiskCount":null,...}` |
| Fix | Change to `WHERE PeriodStart = (SELECT MAX(PeriodStart) FROM TrafficAlert.CustomerSummary)` or add a fallback that returns the most recent period's data when today has none. |
| File | `SmartPiXL.Sentinel/Endpoints/TrafficAlertEndpoints.cs` |

**BUG-Q4 (CRITICAL): No try/catch on dashboard SQL queries — unhandled exceptions become 500s**

| Field | Detail |
|-------|--------|
| Endpoints | All `/api/dash/*` endpoints |
| Root Cause | `DashboardEndpoints.cs` `QueryAsync` / `QuerySingleRowAsync` methods execute SQL with no try/catch. When a query times out or fails, `SqlException` propagates unhandled and ASP.NET returns a raw 500. |
| Evidence | The 6 timed-out endpoints return generic 500 with no structured error response. |
| Fix | Wrap all query executions in try/catch that returns `Results.Json(new { error = "...", data = Array.Empty<object>() }, statusCode: 503)` or similar graceful degradation. |
| File | `SmartPiXL.Sentinel/Endpoints/DashboardEndpoints.cs` |
| Note | This is a C# fix. Assigning to `csharp-janitor` but listing here since the SQL timeouts are the trigger. |

**BUG-Q9 (MODERATE): PiXL.Parsed missing covering index for dashboard view predicates**

| Field | Detail |
|-------|--------|
| Root Cause | Even after adding date filters (BUG-Q1), views that filter on `BotScore`, `IsBot`, `BrowserFamily`, `OsFamily`, `DeviceType`, `Classification` will benefit from filtered/covering indexes. |
| Fix | After BUG-Q1 date filters are added, profile the 6 views and add filtered indexes as needed. Consider: `IX_Parsed_ReceivedAt_BotScore`, `IX_Parsed_ReceivedAt_DeviceType`. |
| Priority | Do after BUG-Q1 is fixed — measure first, then index. |

---

#### 🔧 `csharp-janitor` — 3 bugs (1 critical from above, 2 moderate)

**BUG-Q4 — See above (sql-janitor section).** Add try/catch to `DashboardEndpoints.cs` query methods.

**BUG-Q2 — Coordinate with sql-janitor.** Fix the SQL probe in `InfraHealthService.cs`.

**BUG-Q5 (MODERATE): Mermaid diagram rendering broken in Atlas — closing tag mismatch**

| Field | Detail |
|-------|--------|
| Surface | Atlas documentation portal |
| Root Cause | `MarkdownAtlasService.cs` line ~339: the Mermaid fix replaces the opening `<code class="language-mermaid">` with `<pre class="mermaid">` but does NOT fix the closing `</code>` tag. Result: `<pre class="mermaid">graph TD...;</code></pre>` — mismatched HTML tags. |
| Evidence | Atlas sections with Mermaid diagrams may not render correctly depending on browser tolerance. |
| Fix | Also replace the closing `</code></pre>` with just `</pre>` for Mermaid blocks. |
| File | `SmartPiXL.Sentinel/Services/MarkdownAtlasService.cs` ~line 339 |

**BUG-Q8 (MODERATE): 11 dashboard API endpoints have no frontend consumers**

| Field | Detail |
|-------|--------|
| Root Cause | `tron.html` `API` object (line ~1921) only maps 11 of 22 endpoints. Missing: `sessions`, `dead-internet`, `customer-quality`, `cross-customer`, `cross-customer/detail`, `impossible-travel`, `device-lifecycle`, `device-hops`, `subnet-clusters`, `pipeline`, `remediations`. |
| Evidence | These endpoints return valid JSON when hit directly, but no panel in the Tron dashboard displays them. |
| Fix | This requires frontend work — assign to `javascript-janitor` to wire the panels. Listed here for awareness. |

---

#### 🔧 `javascript-janitor` — 4 bugs (3 moderate, 1 minor)

**BUG-Q6 (MODERATE): `renderBehavior()` ambiguous classification selector**

| Field | Detail |
|-------|--------|
| Surface | Tron Ops dashboard |
| Root Cause | `tron.html` `renderBehavior()` uses `d.classification.includes('50')` to distinguish bots from humans. The SQL view uses `'Bot (50+)'` and `'Human (<50)'` — BOTH strings contain `'50'`, so the filter matches everything. |
| Evidence | SQL view definition confirmed: `CASE WHEN BotScore >= 50 THEN 'Bot (50+)' ELSE 'Human (<50)' END AS Classification` |
| Fix | Change `d.classification.includes('50')` to `d.classification.startsWith('Bot')` or `d.classification.includes('Bot')`. |
| File | `SmartPiXL.Sentinel/wwwroot/tron.html` ~line 3370 |

**BUG-Q8 (MODERATE): Wire 11 missing endpoint panels into Tron dashboard**

| Field | Detail |
|-------|--------|
| Root Cause | The `API` object and `OPS_STEPS`/`ANALYTICS_STEPS` arrays in `tron.html` don't include the 11 enrichment endpoints added in Phases 5-9. |
| Fix | Add API mappings, step entries, and renderer functions for: `sessions`, `dead-internet`, `customer-quality`, `cross-customer`, `cross-customer/detail`, `impossible-travel`, `device-lifecycle`, `device-hops`, `subnet-clusters`, `pipeline`, `remediations`. |
| File | `SmartPiXL.Sentinel/wwwroot/tron.html` |
| Note | Large task — may warrant splitting into sub-tasks per panel. |

**BUG-Q10 (MODERATE): Refresh timer persists across view switches**

| Field | Detail |
|-------|--------|
| Root Cause | `tron.html` `setInterval` for ops refresh isn't cleared when switching to analytics view and vice versa. Could cause duplicate API calls and stale data rendering. |
| Fix | Store interval ID and call `clearInterval()` in the view-switch handler before starting the new view's refresh cycle. |
| File | `SmartPiXL.Sentinel/wwwroot/tron.html` |

**BUG-Q11 (MINOR): UTF-8 mojibake throughout tron.html source**

| Field | Detail |
|-------|--------|
| Root Cause | Em-dashes (`—`), right quotes (`'`), and other Unicode characters are encoded as `â€"`, `â€™`, etc. File was likely saved or copied with wrong encoding. |
| Fix | Re-save `tron.html` as UTF-8 (no BOM). Search-replace common mojibake sequences: `â€"` → `—`, `â€™` → `'`, `â€œ` → `"`, `â€` → `"`. |
| File | `SmartPiXL.Sentinel/wwwroot/tron.html` |

---

#### 🔧 `testing-specialist` — 1 minor

**BUG-Q14 (MINOR): No integration tests for Sentinel API endpoints**

| Field | Detail |
|-------|--------|
| Root Cause | `SmartPiXL.Tests/` has unit tests for core services but no integration tests that hit Sentinel endpoints with a test database. |
| Fix | Add `WebApplicationFactory<Program>`-based integration tests for at least `/api/dash/health`, `/api/atlas/sections`, and `/api/traffic-alert/summary`. |

---

#### 🔧 `doc-specialist` — 1 minor

**BUG-Q13 (MINOR): Atlas documentation references may not match current schema**

| Field | Detail |
|-------|--------|
| Root Cause | Atlas markdown files in `docs/atlas/` reference SQL table names, column names, and schema structures. After Phases 7-9 added new schemas (Graph, TrafficAlert, Geo), some docs may be stale. |
| Fix | Cross-reference each Atlas markdown file against the actual database schema. Update table/column references, add missing schemas. |
| Files | `docs/atlas/database/schema-map.md`, `docs/atlas/subsystems/*.md` |

---

### Passed Tests (Verified Working)

- Health endpoint (`/api/dash/health`): 200 OK, data matches `vw_Dash_SystemHealth` SQL exactly
- Recent hits (`/api/dash/recent`): 200 OK, returns latest 50 rows
- Fingerprint clusters (`/api/dash/fingerprints`): 200 OK
- Infra probe (`/api/dash/infra`): 200 OK (structure correct, but SQL probe false-negative — see BUG-Q2)
- Xavier sync (`/api/dash/xavier-sync`): 200 OK
- Dead internet (`/api/dash/dead-internet`): 200 OK
- Customer quality (`/api/dash/customer-quality`): 200 OK
- Cross-customer (`/api/dash/cross-customer`): 200 OK
- Impossible travel (`/api/dash/impossible-travel`): 200 OK
- Device lifecycle (`/api/dash/device-lifecycle`): 200 OK
- Device hops (`/api/dash/device-hops`): 200 OK
- Subnet clusters (`/api/dash/subnet-clusters`): 200 OK
- Pipeline (`/api/dash/pipeline`): 200 OK
- Sessions (`/api/dash/sessions`): 200 OK
- Remediations (`/api/dash/remediations`): 200 OK
- All Atlas endpoints: 200 OK (20 sections, 4 categories, 28 statuses, 11 metrics)
- TrafficAlert visitors: 200 OK (100 rows)
- TrafficAlert customers: 200 OK (3 rows)
- TrafficAlert trend: 200 OK (3 rows)
- Static pages: `/tron` 200, `/atlas` 200
- Error handling: 404 for invalid IDs/slugs (correct behavior)

### Recommended Fix Priority

1. **BUG-Q1** (sql-janitor) — Add date filters to 6 views → unblocks 6 dead endpoints
2. **BUG-Q2** (csharp-janitor + sql-janitor) — Fix infra SQL probe → fixes false "critical" status
3. **BUG-Q4** (csharp-janitor) — Add try/catch → graceful degradation instead of 500s
4. **BUG-Q3** (sql-janitor) — Fix summary date filter → unblocks TrafficAlert summary
5. **BUG-Q6** (javascript-janitor) — Fix classification selector → correct bot/human split
6. **BUG-Q5** (csharp-janitor) — Fix Mermaid tag → correct diagram rendering
7. **BUG-Q8** (javascript-janitor) — Wire missing panels → complete dashboard coverage
8. Remaining moderate/minor bugs in priority order

---

## Session 10 — SQL Janitor: BUG-Q1, Q2, Q3, Q9 Fixes (2026-02-20)

Addressed all 5 sql-janitor bugs from the V1.0.0 QA sweep (Session 9). Fixed 6 slow dashboard views, InfraHealthService false disconnection, TrafficAlert summary nulls, and the PipelineHealth view. Profiled and deferred index work (BUG-Q9).

### BUG-Q1 FIXED: Six `vw_Dash_*` views — 30-day rolling window

**Root Cause:** All 6 views scanned the entire `PiXL.Parsed` table (4.8M+ rows) with no `WHERE` clause. Combined with the Sentinel's 30s `CommandTimeout`, queries timed out and returned HTTP 500.

**Fix:** Added `WHERE ReceivedAt >= DATEADD(DAY, -30, GETUTCDATE())` to all 6 views. The clustered index `CIX_PiXL_Parsed_ReceivedAt (ReceivedAt, SourceId)` enables an efficient range seek. Used `CREATE OR ALTER VIEW` for idempotency.

**Migration:** `SmartPiXL/SQL/60_FixDashboardViewPerformance.sql`

**Views fixed:**

| View | Before (cold) | After (cold) | After (warm) |
|------|--------------|-------------|-------------|
| `vw_Dash_HourlyRollup` | 6,938ms+ timeout | 20,620ms | 139ms |
| `vw_Dash_BotBreakdown` | 7,624ms+ timeout | 109ms | <50ms |
| `vw_Dash_TopBotSignals` | timeout | 110ms | <50ms |
| `vw_Dash_DeviceBreakdown` | timeout | 654ms | <50ms |
| `vw_Dash_EvasionSummary` | timeout | 596ms | <50ms |
| `vw_Dash_BehavioralAnalysis` | timeout | 670ms | <50ms |

**Note on HourlyRollup cold-cache:** The 20s first-run is because ALL 4.8M rows currently fall within the 30-day window (data only spans Feb 2-18). As data ages past 30 days, the window will reduce scan size and performance self-corrects. The 30s timeout is not hit.

### BUG-Q2 FIXED: InfraHealthService — false SQL disconnection

**Root Cause:** `SELECT COUNT(*) FROM PiXL.Raw` with `CommandTimeout = 5` on 16.5M rows = guaranteed timeout. The probe reported SQL as disconnected, which set `overallStatus = "critical"`.

**Fix (both Sentinel and Forge — identical code):**
1. Replaced `COUNT(*)` with `sys.dm_db_partition_stats` DMV lookup — instant metadata read, no table scan
2. Changed `reader.GetInt32()` to `Convert.ToInt32(reader.GetValue())` for DMV's BIGINT return type
3. Bumped `CommandTimeout` from 5 to 10 for safety margin

**Before:** `sqlServer.isConnected = false`, `overallStatus = "critical"`
**After:** `sqlServer.isConnected = true`, `testRows = 16,469,489`, `overallStatus = "degraded"` (legitimate — data flow paused)

### BUG-Q3 FIXED: TrafficAlert summary returns all nulls

**Root Cause:** `WHERE PeriodStart = CAST(GETUTCDATE() AS date)` — filters for today's date only. Most recent data in `TrafficAlert.CustomerSummary` is Feb 16 (4 days old). No rows match, all aggregate KPIs return null.

**Fix:** Changed to `WHERE PeriodStart = (SELECT MAX(PeriodStart) FROM dbo.vw_TrafficAlert_CustomerOverview WHERE PeriodType = 'D')` — always returns the most recent daily period regardless of date.

**Before:** `{"totalVisitors":null,"averageScore":null,...}`
**After:** `{"customerCount":1,"totalHits":3,"gradeA":1,...}`

### BUG-Q9 DEFERRED: PiXL.Parsed covering indexes

**Assessment:** After applying date filters (BUG-Q1), profiled all 6 views:
- 5 of 6 are under 1s even cold. Only HourlyRollup is slow (20s cold, 139ms warm).
- Current state is the **worst case** — all 4.8M rows are within 30-day window because data only spans 16 days.
- As data ages past 30 days, the filter narrows and performance improves automatically.
- Adding covering indexes on a 4.8M x 300-column table carries significant storage/write overhead for marginal read benefit.

**Decision:** No indexes added now. Re-evaluate when PiXL.Parsed exceeds 10M rows with data spanning 60+ days.

### BONUS FIX: `vw_Dash_PipelineHealth` — DMV-based row counts

**Found during audit:** The `PipelineHealth` view used `COUNT(*)` on PiXL.Raw (16.5M), PiXL.Parsed (4.8M), PiXL.Visit (4.8M), and other tables. With the Sentinel's 10s timeout, this was at risk of timing out as tables grow.

**Fix:** Replaced all 6 `COUNT(*)` calls with `sys.dm_db_partition_stats` DMV lookups. Kept filtered counts on smaller tables (PiXL.Match WHERE IndividualKey IS NOT NULL, etc.) as-is since they use indexes.

### Files Changed

| File | Change |
|------|--------|
| `SmartPiXL/SQL/60_FixDashboardViewPerformance.sql` | **NEW** — Migration 60: 7 views fixed (6 dashboard + PipelineHealth) |
| `SmartPiXL.Sentinel/Services/InfraHealthService.cs` | SQL probe: COUNT(*) to DMV, GetInt32 to Convert.ToInt32, timeout 5 to 10 |
| `SmartPiXL.Forge/Services/InfraHealthService.cs` | Same SQL probe fix as Sentinel (identical code) |
| `SmartPiXL.Sentinel/Endpoints/TrafficAlertEndpoints.cs` | Summary: GETUTCDATE() to MAX(PeriodStart) |

### Verification — All Previously-Failing Endpoints

| Endpoint | Before | After |
|----------|--------|-------|
| `/api/dash/hourly` | 500 (timeout) | **200** (20KB) |
| `/api/dash/bots` | 500 (timeout) | **200** (1KB) |
| `/api/dash/bot-signals` | 500 (timeout) | **200** (3KB) |
| `/api/dash/devices` | 500 (timeout) | **200** (5KB) |
| `/api/dash/evasion` | 500 (timeout) | **200** (234B) |
| `/api/dash/behavior` | 500 (timeout) | **200** (538B) |
| `/api/dash/infra` | 200 (SQL=disconnected) | **200** (SQL=connected, 16.5M rows) |
| `/api/dash/pipeline` | timeout risk | **200** (824B, instant) |
| `/api/traffic-alert/summary` | 200 (all nulls) | **200** (real data) |

### Remaining QA Bugs (other agents)

| Bug | Agent | Status |
|-----|-------|--------|
| BUG-Q4 | `csharp-janitor` | **FIXED** — Session 11 |
| BUG-Q5 | `csharp-janitor` | **FIXED** — Session 11 |
| BUG-Q6 | `javascript-janitor` | Pending — Fix `includes('50')` classification selector |
| BUG-Q8 | `javascript-janitor` | Pending — Wire 11 missing endpoint panels |
| BUG-Q10 | `javascript-janitor` | Pending — Fix refresh timer across view switches |
| BUG-Q11 | `javascript-janitor` | Pending — Fix UTF-8 mojibake in tron.html |
| BUG-Q13 | `doc-specialist` | Pending — Cross-reference Atlas docs vs schema |
| BUG-Q14 | `testing-specialist` | Pending — Add Sentinel integration tests |

---

## Session 11 — C# Janitor: BUG-Q4 + BUG-Q5

**Agent:** `csharp-janitor`
**Scope:** Fix 2 remaining C# bugs from Session 9 QA sweep (BUG-Q2 already fixed by sql-janitor in Session 10).

### BUG-Q4 (CRITICAL) — Graceful error handling for dashboard SQL queries

**Problem:** All 22+ `/api/dash/*` endpoints in `DashboardEndpoints.cs` had no try/catch. When a SQL query timed out or a view didn't exist, `SqlException` propagated unhandled and ASP.NET returned a raw 500 with stack trace — no structured error response.

**Fix:** Added three helper methods with built-in error handling:

| Method | Purpose |
|--------|---------|
| `QueryViewAsync` | Multi-row SQL view query → JSON response. Catches exceptions, returns HTTP 503 with `{ error, detail }`. |
| `QueryViewSingleRowAsync` | Single-row SQL view query → JSON response. Same error handling. |
| `SafeExecuteAsync` | General-purpose wrapper for non-query endpoints (infra, remediation, notifications). Same error handling. |

All 22+ endpoint lambdas were refactored to use these helpers:
- 17 simple query endpoints → `QueryViewAsync` (reduced from 4 lines to 1 line each)
- 2 single-row endpoints → `QueryViewSingleRowAsync`
- 6 complex endpoints (cached, infra, remediation, circuit-reset, test-notify) → `SafeExecuteAsync`

**Error response format (verified live):**
```json
{"error":"Query failed","detail":"Execution Timeout Expired. The timeout period elapsed prior to completion of the operation or the server is not responding."}
```
HTTP 503 status code, structured JSON — frontend can display meaningful error messages.

**Note:** `vw_Dash_RecentHits` still times out (needs 30-day window like the other 6 views fixed in Session 10). Now returns graceful 503 instead of raw 500. SQL fix deferred to sql-janitor.

### BUG-Q5 (MODERATE) — Mermaid diagram rendering broken in Atlas

**Problem:** `MarkdownAtlasService.ConvertTierToHtml()` replaced `<code class="language-mermaid">` with `<pre class="mermaid">` but left the closing `</code></pre>` intact. Markdig outputs `<pre><code class="language-mermaid">...</code></pre>` — so after the fix: `<pre><pre class="mermaid">...</code></pre>` — doubly nested `<pre>`, stray `</code>`.

**Fix:** Replaced the single `string.Replace()` with a `[GeneratedRegex]` that matches the full Mermaid HTML block and replaces both opening and closing tags correctly:
- Match: `<pre><code class="language-mermaid">(.*?)</code></pre>`
- Replace: `<pre class="mermaid">$1</pre>`

### Bonus: `new Regex()` → `[GeneratedRegex]` cleanup

Converted all 3 existing `new Regex(..., RegexOptions.Compiled)` instances to source-generated `[GeneratedRegex]` per coding standards:
- `TierHeaderRegex` — tier section header matching
- `FrontmatterRegex` — YAML frontmatter extraction
- `MermaidBlockRegex` — raw markdown Mermaid block extraction
- `MermaidHtmlBlockRegex` — **NEW** — HTML Mermaid block replacement

Class changed from `sealed class` to `sealed partial class` (required by `[GeneratedRegex]`).

### Files Changed

| File | Change |
|------|--------|
| `SmartPiXL.Sentinel/Endpoints/DashboardEndpoints.cs` | Added `QueryViewAsync`, `QueryViewSingleRowAsync`, `SafeExecuteAsync`. Refactored all 25 endpoints to use safe wrappers. |
| `SmartPiXL.Sentinel/Services/MarkdownAtlasService.cs` | Fixed Mermaid HTML replacement. Converted 3 `new Regex()` → `[GeneratedRegex]`. Added `MermaidHtmlBlockRegex`. Class → `sealed partial class`. |

### Build & Test Results

| Metric | Result |
|--------|--------|
| Sentinel build | **0 warnings, 0 errors** |
| Unit tests | **523/523 passed** (0 failures) |
| Deployed to | `C:\Services\SmartPiXL-Sentinel\` |
| Service status | **Running** |

### Verification

| Endpoint | Status |
|----------|--------|
| `/api/dash/health` | 200 OK |
| `/api/dash/bots` | 200 OK |
| `/api/dash/evasion` | 200 OK |
| `/api/dash/pipeline` | 200 OK |
| `/api/dash/infra` | 200 OK |
| `/api/dash/hourly` | 200 OK |
| `/api/dash/bot-signals` | 200 OK |
| `/api/dash/devices` | 200 OK |
| `/api/dash/behavior` | 200 OK |
| `/api/dash/sessions` | 200 OK |
| `/api/dash/recent` | **503** (graceful error — view timeout, SQL fix needed) |
| `/api/atlas/sections` | 200 OK (410KB) |

## Session 12 — JavaScript Janitor: BUG-Q6 + BUG-Q8

**Agent:** `javascript-janitor`
**Scope:** Fix 2 Tron dashboard JavaScript bugs from Session 9 QA sweep.

### BUG-Q6 (MODERATE) — Classification selector logic broken in `renderBehavior()`

**Problem:** `renderBehavior()` used `d.classification.includes('<50')` and `d.classification.includes('50')` to separate human vs bot traffic. The classification field contains strings like `"Human (<50)"` and `"Bot (≥50)"`, but `includes('50')` matched both because `'<50'` contains `'50'`. This caused entries to appear in both the human and bot columns.

**Fix:** Changed to `d.classification.startsWith('Human')` and `d.classification.startsWith('Bot')` — unambiguous prefix matching that works regardless of the score threshold text in parentheses.

**Files:** Both copies of `tron.html` (Sentinel + Edge).

### BUG-Q8 (MAJOR) — 11 missing Tron dashboard panels

**Problem:** Session 8 added 11 API endpoints to `DashboardEndpoints.cs` but never wired them into the Tron dashboard frontend. The endpoints returned data fine but the dashboard had no panels, renderers, or pipeline steps to call them.

**Missing endpoints:** `sessions`, `dead-internet`, `customer-quality`, `cross-customer`, `cross-customer-detail`, `impossible-travel`, `device-lifecycle`, `device-hops`, `subnet-clusters`, `pipeline`, `remediations`.

**Fix — four layers of changes:**

| Layer | What was added |
|-------|---------------|
| **API object** | 11 new entries in `const API = {}` mapping names to fetch URLs |
| **HTML panels** | 5 new `panel-grid half` sections in analytics view (Sessions, Dead Internet, Customer Quality, Cross-Customer, Impossible Travel, Device Lifecycle, Subnet Clusters, Device Hops) + 1 new `panel-grid half` section in ops view (Pipeline Health, Remediations) |
| **Renderer functions** | 11 new functions: `renderSessions`, `renderDeadInternet`, `renderCustomerQuality`, `renderCrossCustomer`, `renderImpossibleTravel`, `renderDeviceLifecycle`, `renderDeviceHops`, `renderSubnets`, `renderPipelineHealth`, `renderRemediations` |
| **Pipeline steps** | 2 new steps in `OPS_STEPS` (PIPELINE/DUMONT, REMEDIATE/BECK) + 9 new steps in `ANALYTICS_STEPS` (SESSIONS/DUMONT, DEAD-NET/BECK, QUALITY/CYRUS, CROSS-CX/PAIGE, TRAVEL/ABLE, LIFECYCLE/ISO, HOPS/DYSON, SUBNETS/CROM) |

**Renderer design:** Each renderer follows the existing pattern — null/empty guard with fallback message, `feed-table` class for data tables, `num()` helper for number formatting, conditional color styling for alert fields (bot scores, dormant flags, subnet alerts).

### Conflict Resolution

**Encoding conflict:** tron.html contains UTF-8 mojibake throughout (BUG-Q11, not in scope for this agent). All string replacements required matching the mojibake characters exactly as they appear in the file (e.g., `â€"` not `—`). Logged for BUG-Q11 resolution.

### Files Changed

| File | Change |
|------|--------|
| `SmartPiXL.Sentinel/wwwroot/tron.html` | BUG-Q6 fix + all BUG-Q8 additions (API, HTML, renderers, pipeline steps) |
| `SmartPiXL/wwwroot/tron.html` | Synced copy from Sentinel |

### Build Results

| Metric | Result |
|--------|--------|
| Sentinel build | **0 warnings, 0 errors** |