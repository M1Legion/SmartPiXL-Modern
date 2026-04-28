# SmartPiXL Go-Live Implementation Plan

**Status:** Active plan. Authoritative until the design tree supersedes it.
**Owner:** BGluckman (M1 Legion)
**Drafted:** 2026-04-23
**Context:** Project moved from research to pre-go-live. A BrilliantPiXL front-end is coming as soon as another project finishes. We are consolidating an emergent architecture into an intentional one.

---

## Locked architectural decisions

These are the calls we have made and will not revisit without explicit reason.

| # | Decision | Notes |
|---|---|---|
| 1 | **DB platform:** Postgres 17/18 post-migration | Phase 0 remains on MSSQL 2025. Migration is Phase 2. |
| 2 | **Transport:** Custom embedded HitLog | Replaces named pipe + Edge failover + Forge failover + replay. Single topic, append-only segmented file log with CRC + per-consumer offsets. |
| 3 | **Stitch key:** `HitId` | 128-bit (UUIDv4), generated in the PiXL script, carried on every beacon (main, geo followup, future delta beacons). **Not `SessionId`** — `Session` in SmartPiXL means a multi-page visit. |
| 4 | **Stitching:** Forge in-memory `HitId`-keyed buffer | Merges beacons before the SQL write. Late deltas apply via upsert proc. One row per hit in the landing table. |
| 5 | **Forge process split:** three Windows services | Ingest / Enrich / Batch. Split driven by the design tree's subsystem boundaries. |
| 6 | **Metrics:** DB table + Sentinel reads | Retire file-based metric logging. |
| 7 | **Protocol versioning:** `_pv` field on every beacon + HitLog envelope version | Simple integer bump. Parser dispatches. |
| 8 | **Design tree in DB:** `design.node`, `design.code_link`, `design.probe_binding`, `design.decision` | Source of truth. Sentinel renders. |
| 9 | **CLR:** Delete during Postgres migration | All 5 functions are trivially replaceable with native Postgres. |
| 10 | **Rollback:** previous build directory per service | `C:\Services\<svc>.prev\`, swap on deploy. |

## Rejected alternatives (so we don't re-debate them)

- **SQL view or post-merge UPDATE for geo stitching.** Rejected — adds DB-side complexity for something Forge can do for free.
- **Holding main beacon at Edge until geo resolves.** Rejected — Edge must stay a firehose.
- **NATS JetStream or other third-party broker.** Rejected — no paid/external dependencies; embedded broker is scoped tight enough to own.
- **ClickHouse / separate event store alongside SQL.** Rejected — LiQ proves MSSQL (and by extension Postgres) can handle the volume we need.
- **Horizontal scaling.** Rejected — M1 runs products on one box for a decade. Not our pattern.

## Mental model — design tree (by Brandon)

Five-level hierarchy, every dev conversation is framed against it:

```
Platform → System → Subsystem → Component → Probe (or leaf Component)
```

### Leaf node rules

A node is a leaf iff ALL four rules hold:

1. **Failable** — Can it fail independently while its siblings remain healthy?
2. **Diagnosable** — When it fails, can you pinpoint what broke without investigating other nodes?
3. **Actionable** — Is there a concrete, specific remediation when it fails?
4. **Atomic** — Does it represent exactly one failure mode with no distinct sub-concerns?

Not every leaf is a probe. Probes are the health-check surfaces; non-probe leaves are components that don't need continuous monitoring but still satisfy the four rules.

---

## Phases

Phase ordering prioritizes: (a) keep BrilliantPiXL hits flowing to MSSQL as long as possible, (b) stabilize geo capture NOW, (c) migrate heavy plumbing during the window before go-live.

### Phase 0 — Stabilize geo capture on current MSSQL stack **(CURRENT)**

Goal: One row per pixel hit with geo merged in-memory in Forge. No schema churn in SQL. No transport changes. No Postgres yet.

**Tasks:**
- [X] Add `HitId` generation to `PiXLScript.cs` (`crypto.randomUUID()` or fallback).
- [X] Include `HitId` on the main beacon AND on the geo-followup beacon.
- [X] Edge passes `HitId` through unmodified (no parsing logic needed — it is just another QS param).
- [X] Forge parser reads `HitId` from `_hit_id` QS param into a new `PiXL.Parsed.HitId` column (`UNIQUEIDENTIFIER` nullable).
- [X] New `GeoStitchBuffer` in Forge keyed on `HitId`:
  - Main arrives first, no geo → park 20s.
  - Geo arrives while main parked → merge `_usr_*` into main QS, flush to SqlWriter channel, remove from buffer.
  - Main timer expires → flush unmerged (denied / no-response / bounced).
  - Geo arrives with no parked main → second mini buffer (pending-geo, same 20s window) for cross-arrival-order cases.
  - Sweep thread at 1 Hz.
  - Metrics: `stitch_merged_inline`, `stitch_flushed_unmerged`, `stitch_orphan_geo`, `stitch_buffer_depth`.
- [X] Update `ParsedRecordParser` to skip writing geo-followup as its own row (follow-up without a main that we can stitch is a rare tail case — park in pending-geo or drop based on telemetry).
- [X] Graceful shutdown: buffer contents flush to SQL writer channel (unmerged) during `StopAsync`.
- [X] Extend Playwright test harness to verify **exactly one row** per page visit and that the geo fields are present on that row.
- [ ] Deploy to prod on this box.

**Exit criteria:** `tools/test-pixl-tag.js` reports one `PiXL.Parsed` row per visit, with both base fingerprint AND `UserLat`/`UserLon`/`UserGeoStatus=granted` populated on the same row.

**Out of scope for Phase 0:**
- Transport changes (still named pipe)
- Postgres
- Forge process split
- Delta beacons
- Schema normalization
- Metrics to DB

---

### Phase 1 — Design tree, seeded on MSSQL

Goal: Turn the mental model into queryable data. Start eating our own dog food.

**Tasks:**
- [ ] Create schema `design` on MSSQL (portable SQL — will move to Postgres in Phase 2 with minimal change).
- [ ] Tables: `design.Node`, `design.CodeLink`, `design.ProbeBinding`, `design.Decision`.
- [ ] Seed every existing system/subsystem/component/probe we've identified.
- [ ] Seed the locked decisions above as `design.Decision` rows.
- [ ] Sentinel page `/design` rendering the tree (expand/collapse, filter by status).
- [ ] Sentinel page `/design/<nodeId>` rendering: description + code links (clickable to GitHub path) + probe status + ADRs.
- [ ] Drift report (dashboard panel): nodes without code links, code files not claimed, probes without nodes.

**Exit criteria:** Every file under `SmartPiXL/`, `SmartPiXL.Forge/`, `SmartPiXL.Sentinel/`, `SmartPiXL.Shared/` is claimed by at least one design node. Every probe currently firing is bound to a leaf node.

**Side effect:** Everything in `docs/` that duplicates the tree becomes deletable. We'll do that cleanup at the end of Phase 1.

---

### Phase 2 — Postgres migration

Goal: Kill the paid-license dependency before go-live. Do this while the platform is still small.

**Tasks:**
- [ ] Install Postgres 17 (or 18 if available) alongside MSSQL on this box.
- [ ] Port connection strings and driver layer: swap `Microsoft.Data.SqlClient` → `Npgsql` in Edge / Forge / Sentinel.
- [ ] Create Postgres schemas matching current MSSQL schemas (`PiXL`, `ETL`, `IPAPI`, `TrafficAlert`, `Graph`, `Geo`, `design`, `metrics`).
- [ ] Port stored procedures T-SQL → PL/pgSQL. Grep-and-replace first pass, edge cases hand-fixed.
- [ ] Replace the 5 CLR functions with native Postgres equivalents:
  - `RegexFunctions` → Postgres `~` / `regexp_match`
  - `MurmurHash3` → `pgcrypto` or plpgsql port
  - `GetSubnet24` → native `inet`/`cidr` (`set_masklen`)
  - `FuzzyMatch` → `pg_trgm` / `fuzzystrmatch`
  - `FeatureBitmaps` → native `bit varying`
- [ ] Migrate `PiXL.Parsed` data. Decision: bulk `COPY` export from MSSQL → `COPY FROM STDIN BINARY` into Postgres using `Npgsql.NpgsqlBinaryImporter`. Partition-by-partition to avoid one giant transaction.
- [ ] Swap `SqlBulkCopyWriterService` → `PgsqlBulkCopyWriterService` using `Npgsql`'s binary COPY.
- [ ] Sentinel rebuilt to read Postgres.
- [ ] MSSQL stays on the box in read-only mode for a bake-in window, then uninstalled along with SQL 2019.
- [ ] PGMS (Brandon's custom Postgres UI) remains our admin tool of record.

**Exit criteria:** All hits land in Postgres. All dashboards read Postgres. MSSQL is no longer on the hit-write path. `SmartPiXL.SqlClr` project deleted.

---

### Phase 3 — HitLog (embedded broker)

Goal: Replace named pipe + failover + replay with one coherent mechanism.

**Design spec — `PixlLog` (embedded library):**

- Single producer-side topic: `hits`.
- Append-only segmented files under `C:\PixlLog\hits\`: `00000001.log`, `00000002.log`, ...
- Segment rotation at size boundary (config, default 64 MB) OR age boundary (config, default 1 hour), whichever first.
- Message envelope: `[4-byte length][4-byte CRC32][1-byte envelope version=1][2-byte payload version][N bytes payload]`.
- Producer API:
  ```csharp
  public interface IPixlLogProducer {
      ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
      ValueTask FlushAsync(CancellationToken ct); // explicit fsync
  }
  ```
  - Batches writes in memory, fsyncs every N ms or N KB.
  - Never blocks — if disk is full, throws; caller (Edge) decides.
- Consumer API:
  ```csharp
  public interface IPixlLogConsumer {
      IAsyncEnumerable<PixlLogMessage> ReadAsync(CancellationToken ct);
      ValueTask CommitAsync(long messageId, CancellationToken ct);
  }
  ```
  - Reads from last committed offset forward.
  - On torn-write CRC mismatch at segment tail, treats segment as ended and moves to next.
  - Offset persisted to `C:\PixlLog\consumers\<name>.offset` every N ms or on clean shutdown.
- Retention: sweep thread deletes segments older than `max(retention_days, all_consumers_past_it)`.
- Single-box, single-process embedded. No network, no replication, no auth.

**Tasks:**
- [ ] `PixlLog/` project created in the solution.
- [ ] Write unit tests FIRST: torn-write recovery, CRC rejection, segment rotation, concurrent producer, consumer offset round-trip, clean shutdown.
- [ ] Edge `PipeClientService` → `PixlLogProducerService`.
- [ ] Forge `PipeListenerService` → `PixlLogConsumerService` (in Ingest service after Phase 4).
- [ ] Delete `ForgeFailoverWriter`, `ForgeReplayService`, Edge's JSONL failover code.
- [ ] Migration helper: reads any existing `Failover/*.jsonl` files at first startup and writes them into the new log.

**Exit criteria:** No code references `NamedPipe*` types. `Failover/` directory is empty and removed from the codebase.

---

### Phase 4 — Forge process split

Goal: One failure ≠ all-services failure. Follows the design tree's subsystem boundaries.

**Target layout:**

| Service | Windows name | Subsystems | Restart policy |
|---|---|---|---|
| Ingest | `SmartPiXL-Forge-Ingest` | Ingest, Resilience | Rare — never during traffic |
| Enrich | `SmartPiXL-Forge-Enrich` | Enrichment Data | Free — hot-reloadable |
| Batch | `SmartPiXL-Forge-Batch` | Batch, Observability | Free — scheduled work |

**Tasks:**
- [ ] Split `SmartPiXL.Forge` project into `.Ingest`, `.Enrich`, `.Batch` (or keep one csproj and select by command-line flag; lean toward three projects for clarity).
- [ ] Shared library `SmartPiXL.Forge.Core` for types used by all three.
- [ ] Inter-service communication: Postgres tables + `LISTEN/NOTIFY` where low-latency handoff is needed.
- [ ] Health endpoints: each service exposes `GET /health` on its own port.
- [ ] Design tree nodes bound to each service's probes.

**Exit criteria:** Restarting `Enrich` or `Batch` does not pause `Ingest`.

---

### Phase 5 — Metrics + observability on Postgres

Goal: Structured metrics queryable in SQL, no file parsing.

**Tasks:**
- [ ] `metrics.service_health` table: `(ts timestamptz, service text, component text, probe text, value_json jsonb)`.
- [ ] Partitioned by day, auto-truncate at 30 days.
- [ ] `ForgeMetrics` writes rows via `Npgsql` batch every 5s.
- [ ] Fallback in-memory ring buffer when Postgres is unavailable (rare — this service is on the same box).
- [ ] Sentinel dashboards pull from `metrics.service_health` joined to `design.probe_binding`.
- [ ] Retire `FileTrackingLogger` metric path. (File logger stays for text app logs.)

**Exit criteria:** Every leaf-node probe writes to `metrics.service_health`. No metrics data written to files.

---

### Phase 6 — Delta beacons

Goal: Observe a visit as it unfolds rather than as a single snapshot.

Leverages the `HitId` foundation laid in Phase 0.

**Candidate delta patterns:**
- Anti-fingerprint repeat polling (detect spoofers rewriting `navigator.*` between reads).
- Engagement telemetry (dwell, scroll, mouse activity at t=15s, 30s, 60s, ...).
- Late-binding Client Hints.
- `performance.getEntriesByType('resource')` snapshot at page-load-complete.
- Staged permissions enumeration.

**Tasks:**
- [ ] Script adds `_delta=N` tagging where N is the delta sequence (0 = main, 1+ = subsequent).
- [ ] Forge stitch buffer extended: flush main to SQL at T+15s, apply late deltas via `usp_apply_delta_beacon(hit_id, delta_json)` upsert.
- [ ] Anti-fingerprint delta first (highest differentiation value).
- [ ] Engagement second.
- [ ] Others as justified.

**Exit criteria:** At least one delta pattern in production, with detection metrics visible in Sentinel.

---

### Phase 7 — Entropy analysis + schema normalization

Goal: Trim `PiXL.Parsed` from a capture-everything wide table to a deliberate, normalized store with lookup tables and surrogate keys.

**Tasks:**
- [ ] Once 30+ days of real BrilliantPiXL hits exist, run entropy analysis per column.
- [ ] Identify low-entropy columns (UA strings, browser names, OS names, ...). Promote to lookup tables with surrogate `int` keys.
- [ ] Identify zero-entropy / dead-weight columns. Drop.
- [ ] Identify high-entropy columns worth keeping raw (canvas fingerprints, DeviceHash, HitId). Leave in place.
- [ ] Add `design.Node` entries for every new lookup table.
- [ ] ETL migration rewrites historical rows.

**Exit criteria:** `PiXL.Parsed` column count drops meaningfully (target: <100 columns). Every retained column has a `design.Node` describing why it exists.

---

## Phase 0 — detailed task list (what we're doing right now)

| Step | File | Action |
|---|---|---|
| 1 | `SmartPiXL/Scripts/PiXLScript.cs` | Add `var hitId = (crypto.randomUUID ? crypto.randomUUID() : fallbackUuid())` at top of template; include in `data._hit_id` on main + geo followup payloads. |
| 2 | `SmartPiXL.Shared/Models/TrackingData.cs` | Add `HitId` property (Guid?). |
| 3 | `SmartPiXL.Forge/Services/ParsedRecordParser.cs` | Read `_hit_id` → `HitId` column. |
| 4 | `SmartPiXL/SQL/83_HitId.sql` | `ALTER TABLE PiXL.Parsed ADD HitId UNIQUEIDENTIFIER NULL; CREATE INDEX IX_Parsed_HitId ON PiXL.Parsed(HitId) WHERE HitId IS NOT NULL;` |
| 5 | `SmartPiXL.Forge/Services/Enrichments/GeoStitchBuffer.cs` (new) | `ConcurrentDictionary<Guid,PendingEntry>` with 20 s sweep. Two buckets: pending-main, pending-geo. |
| 6 | `SmartPiXL.Forge/Services/EnrichmentPipelineService.cs` | After enrichment, route records through `GeoStitchBuffer` before handing to SQL writer channel. |
| 7 | `SmartPiXL.Forge/Services/ForgeMetrics.cs` | Add stitch counters. |
| 8 | `tools/test-pixl-tag.js` | Assert exactly one `_SMART.DATA` POST arrives with `_usr_geo_status=granted` AND all the usual fingerprint fields; assert no unstitched pair. (Harness asserts via Forge metrics or via a brief Postgres query after the test.) |
| 9 | Deploy | Edge + Forge publish sequence, pool restart, service restart. |
| 10 | Verify | Run harness. Run SQL query: `SELECT COUNT(*) FROM PiXL.Parsed WHERE HitId IS NOT NULL AND UserGeoStatus='granted'` should match main-row count from harness. |

---

## Open questions to resolve during Phase 0 execution

1. **Stitch window duration.** Proposed 20 s. Re-evaluate after first deploy against real prompt response times.
2. **Geo-without-main tail case.** Current plan: pending-geo mini buffer. Alternative: write as a standalone row with just the geo fields, no stitch. Decide based on observed frequency.
3. **Cross-beacon identifier.** Currently follow-up carries ua, lang, tz, sw, sh, pd, deviceHash, _srv_sessionId. With `HitId` in place, all that is redundant fallback; decide whether to keep or strip for payload savings.

---

## Document disposition after design tree is authoritative

Phase 1 exit includes a cleanup pass:

- This file → migrated into `design.Decision` rows, then moved to `docs/archive/`.
- `docs/SmartPiXL Authoritative WorkPlan .md` → archive (pre-consolidation plan).
- `docs/ADVERSARIAL-REVIEW.md` → archive (drove this plan; retained for context).
- `docs/BRILLIANT-PIXL-DESIGN.md` → evaluated; anything current survives as design nodes, rest archived.
- `docs/IMPLEMENTATION-LOG.md` → archive (historical record, no longer source of truth).
- `docs/SUBSYSTEM-WALKTHROUGH.md` → deleted once every subsystem has a design node with equivalent description.
- `docs/atlas/` → evaluated per-file; the design tree + Sentinel pages replace most of it.

Archived files remain in git history; no data is lost, just moved out of the live tree.
