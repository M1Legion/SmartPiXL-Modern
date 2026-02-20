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
