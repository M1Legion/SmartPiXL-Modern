# SmartPiXL Adversarial Review

**Review Date:** 2026-03-13  
**Scope:** Full-stack audit — Edge, Forge, Sentinel, SQL Server, enrichment pipeline  
**Status:** Active rebuild, Phase 10. Core pipeline (Edge → Forge → Raw → Parsed) is LIVE.

---

## System Health Summary (Live Verification 2026-03-13)

| Metric | Value | Status |
|--------|-------|--------|
| PiXL.Raw row count | 105,835,838 | LIVE — growing at ~54/s |
| PiXL.Parsed row count | 105,834,863 | LIVE — parse gap only 516 rows |
| Raw → Parsed latency | ~20 seconds | Healthy |
| Edge failover files | 4 files, 49.8 MB | UNREPLAYED since Feb 21-26 |
| MaxMind enrichment fill rate | 4 / 147,566 (0.003%) | **BROKEN** |
| Geo (IPAPI) enrichment fill rate | 0 / 147,566 (0%) | **BROKEN** |
| BotScore enrichment fill rate | 0 / 147,566 (0%) | **BROKEN** |
| UA parsing fill rate | 146,772 / 147,566 (99.5%) | Healthy |
| LeadQualityScore fill rate | 147,566 / 147,566 (100%) | Healthy |
| DeviceAge fill rate | 131,000 / 147,566 (88.8%) | Healthy |
| KnownBot fill rate | 6,879 / 147,566 (4.7%) | Expected (bot-only) |
| TrafficAlert.VisitorScore | 611 rows, last run 2026-02-20 | **21 DAYS STALE** |
| TrafficAlert.CustomerSummary | 8 rows, last run 2026-02-20 | **21 DAYS STALE** |
| IpApiSync watermark | 0, never ran | **NEVER RAN** |
| IPAPI.IP Last_Seen_Index | 95% fragmented | **NEEDS REBUILD** |
| Windows services | All 4 running (SQL, IIS, Forge, Sentinel) | Healthy |

---

## Critical Issues

### CRIT-001: Edge Failover Files Unreplayed (49.8 MB data loss risk)

**Impact:** ~49.8 MB of enriched tracking records sitting on disk, not ingested into the database.

| File | Size | Date |
|------|------|------|
| failover_2026_02_21.jsonl | 32.9 MB | Feb 21 |
| failover_2026_02_22.jsonl | 10.8 MB | Feb 22 |
| failover_2026_02_25.jsonl | 3.6 MB | Feb 25 |
| failover_2026_02_26.jsonl | 2.4 MB | Feb 26 |

**Root cause:** `FailoverCatchupService` is one of the 8 disabled Forge services (disabled in Session 15 during the Forge gut-and-rebuild). These files were written when the named pipe was unavailable during that period.

**Resolution:** Re-enable `FailoverCatchupService` to replay these files. Verify the JSONL format is compatible with the current pipeline schema before replaying. If the Forge was gut-rebuilt since these files were written, the field names/format may have changed — test with a single file first.

---

### CRIT-002: Geo Enrichment Completely Broken (0% fill rate)

**Impact:** No records in the pipeline are getting geographic enrichment. Both IPAPI-based (`GeoCountry`, `GeoCity`, etc.) and MaxMind-based (`MaxMindCountry`, `MaxMindCity`, etc.) enrichment are dead.

**Evidence (Edge log, 2026-03-13):**
```
[WARNING] Geo lookup failed for 64.62.197.46: Execution Timeout Expired. The timeout period elapsed prior to completion of the operation or the server is not responding.
```
The Edge log shows continuous geo lookup timeouts throughout the day. Every geo lookup attempt is timing out against the IPAPI.IP table (344M rows).

**Analysis:**
- The IPAPI.IP clustered index (pk_IP on IP column) is only 7.7% fragmented — this should support fast point lookups.
- The Last_Seen_Index is 95% fragmented — while not used for geo lookups, this level of fragmentation can cause page-level contention.
- The issue may be: (a) connection pool exhaustion from too many concurrent geo queries, (b) blocking from ETL writes, (c) query timeout set too low for occasional slow queries, or (d) the geo lookup query is scanning rather than seeking.

**Resolution:**
1. Rebuild the `Last_Seen_Index` on `IPAPI.IP` (95% fragmentation wastes I/O budget)
2. Investigate the GeoCacheService query — verify it uses a parameterized point lookup (`WHERE IP = @ip`)
3. Check connection pool sizing and command timeout for the geo lookup path
4. Consider whether MaxMind enrichment should move entirely into the Forge (Tier 1) rather than Edge

---

### CRIT-003: MaxMind Enrichment Not Appearing on Records

**Impact:** MaxMind geographic data (country, city, lat/lon, ASN) is present on only 4 out of 147,566 recent records. This enrichment was designed as a Tier 1 Forge service.

**Evidence:**
```sql
-- SourceId > 106,400,000 (~last 147K records)
HasMaxMind = 4  -- essentially zero
```

The last record with MaxMind data was SourceId 106,524,675 (US / Dallas). All records after that have NULL MaxMind columns.

**Root cause:** The MaxMindGeoService in the Forge may have lost its database file path, license key, or the GeoLite2 database may have expired. The design doc specifies MaxMind should run as a Tier 1 enrichment in the Forge (fast, local file-based lookup), completely independent of the IPAPI SQL-based geo enrichment.

**Resolution:**
1. Check Forge logs for MaxMind initialization errors
2. Verify `GeoLite2-City.mmdb` file exists and is up to date
3. Confirm MaxMindGeoService is registered and running in the Forge enrichment pipeline
4. Test with a manual IP lookup to verify the database loads

---

## Warnings

### WARN-001: Nuclear Cache Eviction in GeoCacheService

**Description:** The GeoCacheService uses `MemoryCache` with a hard item-count limit. When the cache is full, it evicts ALL entries at once (nuclear eviction) rather than using LRU or statistical sampling.

**Impact:** Periodic latency spikes when the entire geo cache is cleared simultaneously, forcing all lookups to hit the database until the cache refills.

**Resolution:** Replace the nuclear eviction strategy with a time-based expiration (TTL per entry) or an LRU eviction policy. Consider using `ConcurrentDictionary<string, GeoResult>` with a periodic sweep of oldest entries (aligned with the zero-alloc hot path design philosophy).

---

### WARN-002: StringBuilder Per-Record Allocation in EnrichmentPipelineService

**Description:** The `EnrichmentPipelineService` in the Forge creates a new `StringBuilder` for every record processed. At ~54 records/second sustained, this generates ~4.7M allocations per day.

**Impact:** Unnecessary GC pressure. While not critical for throughput, this violates the project's zero-allocation hot path design philosophy.

**Resolution:** Pool `StringBuilder` instances using `ObjectPool<StringBuilder>` or pre-allocate a thread-local `StringBuilder` and reset it between records.

---

### WARN-003: IP-API Key Hardcoded

**Description:** The IP-API service key is hardcoded in source rather than loaded from configuration.

**Impact:** Security risk — the key would be exposed if the repository were ever made public. Also prevents rotation without a code change and rebuild.

**Resolution:** Move the key to `appsettings.json` (dev) and environment variables or user secrets (production). Reference via `IConfiguration` or the existing `ForgeSettings` configuration class.

---

### WARN-004: TrafficAlert Materialization Stale (21 Days)

**Description:** The `MaterializeVisitorScores` and `MaterializeCustomerSummary` ETL watermarks haven't advanced since 2026-02-20.

**Evidence:**
| Process | LastProcessedId | LastRunAt |
|---------|----------------|-----------|
| MaterializeVisitorScores | 2,699,707 | 2026-02-20 |
| MaterializeCustomerSummary | 612 | 2026-02-20 |

**Impact:** The TrafficAlert tables are 21 days behind. Any Sentinel dashboard or API endpoint reading from these tables shows stale data.

**Resolution:** These procs need to be called periodically. Options: (a) re-enable the `EtlBackgroundService` in Forge, or (b) schedule them via SQL Agent, or (c) add them to the Sentinel's background processing. The design doc places ETL in the Forge, so option (a) is architecturally correct.

---

### WARN-005: ETL MERGE Regressions (BUG-E1 / BUG-E2 / BUG-E3)

**Description:** Previous QA found MERGE statement issues in the ETL stored procedures:

- **BUG-E1:** `usp_ParseNewHits` MERGE target may have non-deterministic match when a single SourceId appears in both the source batch and existing Parsed table.
- **BUG-E2:** `usp_EnrichParsedGeo` MERGE can over-write newer data with older data when the same IP appears multiple times in IPAPI.IP (duplicates from import batches).
- **BUG-E3:** Visit materialization MERGE can create orphan Visit records when the session window spans a batch boundary.

**Impact:** Potential data quality issues — duplicate or overwritten enrichment data, orphan visits.

**Resolution:** Audit each MERGE proc. Replace with INSERT ... WHERE NOT EXISTS patterns where possible (avoids MERGE determinism issues). For BUG-E2, add a ROW_NUMBER() OVER (PARTITION BY IP ORDER BY LastSeen DESC) to select the latest IPAPI record.

---

### WARN-006: 8 Disabled Forge Services

**Description:** The following Forge services were disabled during the Session 15 rebuild and have not been re-enabled:

| # | Service | Purpose |
|---|---------|---------|
| 1 | FailoverCatchupService | Replay JSONL failover files |
| 2 | EtlBackgroundService | Periodic ETL proc execution |
| 3 | IpApiSyncService | Sync IP geolocation from Xavier |
| 4 | CompanyPiXLSyncService | Sync company/pixel config from Xavier |
| 5 | EmailNotificationService | Alert emails |
| 6 | InfraHealthService | Infrastructure health checks |
| 7 | SelfHealingService | Auto-remediation |
| 8 | MaintenanceSchedulerService | Scheduled maintenance tasks |

**Impact:** Multiple subsystems are non-operational: failover replay, ETL materialization, Xavier sync, health monitoring, alerting.

**Resolution:** Re-enable services incrementally, verifying each one works with the current codebase before enabling the next. Suggested order:
1. `FailoverCatchupService` (replay 49.8MB of unreplayed data)
2. `EtlBackgroundService` (fix the 21-day TrafficAlert stale data)
3. `InfraHealthService` + `SelfHealingService` (monitoring)
4. `CompanyPiXLSyncService` + `IpApiSyncService` (Xavier integration)
5. `MaintenanceSchedulerService`
6. `EmailNotificationService`

---

## Improvement Opportunities

### IMP-001: DatabaseWriterService May Be Dead Code

**Description:** The `DatabaseWriterService` in Edge was the original direct-to-SQL write path before named pipes were implemented. If all records now flow through the Forge pipe, this service may be dead code.

**Verify:** Check if `DatabaseWriterService` is still registered in DI and if any code path calls it. If it's only used as a pipe-failure fallback, document that. If it's truly dead code, remove it.

---

### IMP-002: No Tests for ParsedRecordParser / ParsedBulkInsertService

**Description:** The `ParsedBulkInsertService` was built in Session 20 (25x ETL speedup) and replaces the SQL UDF-based parsing. There are no dedicated unit tests for the .NET-side parsing logic.

**Impact:** This is the component that maps 300+ fields from Raw query strings into Parsed columns. If a field mapping is wrong, it silently produces NULL data. This is the highest-risk untested component.

**Resolution:** Create `ParsedBulkInsertServiceTests.cs` covering:
- Known good query strings → expected parsed output for all 300+ fields
- Edge cases: URL-encoded values, empty values, missing params, double-encoded, special characters
- Round-trip: generate a query string from known data → parse it → verify all values match

---

### IMP-003: Documentation Drift

**Description:** Several areas where documentation doesn't match reality:
- The design doc refers to `PiXL.Config` which was eliminated in Session 22
- The design doc mentions a `Tier` column in PiXL.Parsed which was eliminated in Session 22
- Some enrichment service descriptions in the design doc don't match their current implementation after the Three-Lane Architecture refactor (Session 17-18)

**Resolution:** Schedule a documentation sync pass:
1. Update `BRILLIANT-PIXL-DESIGN.md` to remove PiXL.Config references
2. Update enrichment architecture section to reflect Three-Lane layout
3. Update schema sections to match current column inventory
4. Update the `copilot-instructions.md` if any references are stale

---

## Verified Solid (14 Items)

These patterns and implementations were audited and confirmed correct:

| # | Item | Status |
|---|------|--------|
| 1 | All public classes are `sealed` | Verified |
| 2 | Zero runtime `Regex` — all use `[GeneratedRegex]` | Verified |
| 3 | Zero LINQ in hot path (Edge capture + Forge enrichment) | Verified |
| 4 | `Channel<T>` used correctly for pipe → enrichment backpressure | Verified |
| 5 | `SqlBulkCopy` with custom `DbDataReader` for Raw writes | Verified |
| 6 | `ITrackingLogger` used everywhere (not `ILogger<T>`) | Verified |
| 7 | Named pipe JSON-line protocol with newline delimiter | Verified |
| 8 | JSONL failover on both Edge and Forge sides | Verified |
| 9 | 43-byte transparent GIF served inline (no file I/O) | Verified |
| 10 | CompanyID/PiXLID migrated from VARCHAR to INT across all layers | Verified |
| 11 | Connection strings consistent across all config files | Verified |
| 12 | PiXL Script field count (159 fields) matches Raw parsing | Verified |
| 13 | Thread-safe singleton services use `ConcurrentDictionary` | Verified |
| 14 | ParsedBulkInsertService Span-based parsing replaces SQL UDFs (25x speedup) | Verified |

---

## Test Coverage Assessment

| Area | Coverage | Risk |
|------|----------|------|
| Edge enrichment services (12) | Good — individual test files exist | Low |
| Forge Tier 1 enrichments | Good — MaxMind, WHOIS, DNS, UAParser tested | Low |
| Forge Tier 2 enrichments | Good — Cross-customer, lead scoring, session stitching | Low |
| Forge Tier 3 enrichments | Good — Behavioral replay, contradiction matrix, device age | Low |
| ParsedBulkInsertService | **NONE** | **HIGH** — maps 300+ fields |
| ParsedRecordParser | **NONE** | **HIGH** — span-based field extraction |
| FailoverCatchupService replay | **NONE** | Medium — replay path untested |
| Named pipe integration (Edge↔Forge) | **NONE** | Medium — integration test gap |
| ETL stored procedures | Manual only | Medium — MERGE issues (BUG-E1/E2/E3) |
| Sentinel endpoints | Basic QA | Low |
| SQL CLR functions | Covered (SqlClrFunctionTests.cs) | Low |

---

## Priority Action Plan

### Immediate (This Session)

1. **CRIT-002 + CRIT-003: Fix Geo/MaxMind enrichment** — This affects every record being ingested. Zero geo data means the entire enrichment pipeline is producing incomplete records. Investigate GeoCacheService timeouts and MaxMind file path issues.

2. **WARN-006 → FailoverCatchupService:** Re-enable to replay 49.8 MB of unreplayed data (CRIT-001).

3. **WARN-004 → EtlBackgroundService:** Re-enable to fix 21-day-stale TrafficAlert materialization.

### Short Term

4. **WARN-005: ETL MERGE audit** — Fix BUG-E1/E2/E3 before they cause data quality issues at scale.

5. **IMP-002: ParsedBulkInsertService tests** — Cover the highest-risk untested component.

6. **WARN-002: StringBuilder pooling** — Quick win for GC pressure.

### Medium Term

7. **WARN-001: GeoCacheService eviction strategy** — Replace nuclear eviction.

8. **WARN-003: IP-API key externalization** — Move to config/secrets.

9. **WARN-006 → Remaining services:** Re-enable InfraHealth, SelfHealing, Xavier sync, Maintenance, Email.

10. **IMP-003: Documentation sync** — Update design doc, copilot instructions.

---

## Dashboard Alert Correlation

The Tron Operations dashboard alerts map directly to these findings:

| Dashboard Alert | Root Cause | Review Item |
|-----------------|------------|-------------|
| "15 Active Errors in log" | Geo lookup timeouts in Edge log | CRIT-002 |
| "ETL Lag High" | Parse gap fluctuates (currently 516 — healthy, but spikes when geo queries block) | CRIT-002 |
| "Xavier Company Sync Stale" (23d) | CompanyPiXLSyncService disabled | WARN-006 |
| "Xavier PiXL Config Sync Stale" (23d) | CompanyPiXLSyncService disabled + PiXL.Config eliminated | WARN-006 + IMP-003 |
| "Infrastructure Critical" | InfraHealthService + SelfHealingService disabled | WARN-006 |
| "Xavier IP Geolocation Sync Failed" | IpApiSyncService disabled, watermark at 0 | WARN-006 |

**Every single dashboard alert traces back to either broken geo enrichment (CRIT-002) or disabled Forge services (WARN-006).**
