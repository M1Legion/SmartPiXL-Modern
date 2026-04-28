-- ============================================================================
-- 88_DesignTreeContentRev.sql
-- Considered rewrite of design-tree copy per three audiences:
--   MARKETING   — hype backed by real systems. Only where hype is warranted.
--   MANAGEMENT  — factual description of the real system. Populate everywhere.
--   DEVELOPER   — things a dev needs to know (gotchas, contracts, files).
--                 Only where there is something substantive to say.
-- Safe to re-run: resets to curated values.
-- ============================================================================

USE SmartPiXL;
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

-- ----------------------------------------------------------------------------
-- 0. RESET — clear auto-generated content so curated text wins
-- ----------------------------------------------------------------------------
UPDATE Health.Node SET DescMarketing = NULL WHERE IsActive=1;
UPDATE Health.Node SET DescDeveloper = NULL WHERE IsActive=1;
-- Management is kept for now; curated table below will overwrite it.

-- ============================================================================
-- 1. MARKETING — hype-worthy nodes only
-- ============================================================================
DECLARE @mkt TABLE (Slug VARCHAR(200) PRIMARY KEY, Copy NVARCHAR(800));

INSERT INTO @mkt VALUES
('smartpixl',
 N'SmartPiXL turns every website visit into rich, fraud-resistant intelligence — even when cookies are blocked, tracking is disabled, and browsers are lying. Three cooperating engines working in microseconds to give you the truth behind the traffic.'),

('edge',
 N'A hot-path capture layer engineered for microsecond response times. Never drops a hit, never blocks the browser, and keeps capturing even when the rest of the world is on fire.'),

('forge',
 N'The intelligence engine. Sixteen enrichment services run in parallel to classify, stitch, score, and interpret every visitor signal — turning raw pixel hits into business-ready visitor profiles.'),

('sentinel',
 N'Mission-control for the platform: real-time health, live documentation, executive analytics, and a designer surface — all in one Tron-styled portal.'),

('forge.f2-enrichment',
 N'Sixteen parallel enrichment services cross-check device, network, behavior, geography, and historical context. Bots are flagged, sessions are stitched, lead quality is scored — all before the record even hits SQL.'),

('forge.f4-failover',
 N'Zero data loss by design. If SQL goes away, every enriched hit is captured to disk; when SQL returns, replay is automatic, ordered, and idempotent.'),

('forge.f5-etl',
 N'Identity resolution every sixty seconds. Connects visits into journeys, fingerprints into people, sessions into stories — without cookies.'),

('sentinel.s4-atlas',
 N'Living documentation that pulls live metrics from SQL into the narrative. Not a wiki — a system that reads itself and tells you the truth right now.'),

('sentinel.s5-brilliantpixl-metrics',
 N'Executive-facing analytics at a glance: human vs. bot, channel mix, fingerprint coverage, lead quality, geographic reach — refreshed live from the pipeline.'),

('forge.f2-enrichment.dead-internet',
 N'Detects the fingerprints of Dead Internet traffic: AI-generated scrolls, headless clusters, synthetic engagement patterns masquerading as human visits.'),

('forge.f2-enrichment.lead-quality-scoring',
 N'Composite scoring that distills sixteen enrichment signals into a single number your sales team can actually use.'),

('forge.f2-enrichment.contradiction-matrix',
 N'Spots visitors whose signals do not add up — iPhone user-agent on a Linux device, Japan IP on en-US keyboard, residential ISP running a datacenter browser. If it does not make sense, the matrix catches it.'),

('forge.f2-enrichment.cross-customer-intel',
 N'Every customer benefits from every other customer. When a bad actor hits one site, every other SmartPiXL-protected site inherits that intelligence instantly.'),

('forge.f2-enrichment.behavioral-replay',
 N'Catches the tell-tale fingerprints of automated browsing — scripted scroll cadences, impossible click precision, the patterns humans never actually make.'),

('forge.f2-enrichment.device-affluence',
 N'Infers purchasing power from hardware signals: flagship GPUs, retina displays, premium mobile chipsets. Quality prospects leave quality fingerprints.'),

('forge.f2-enrichment.geographic-arbitrage',
 N'Flags the mismatch between where a visitor claims to be and where they actually are — VPN exit nodes, residential proxies, geographic tunneling.'),

('forge.f2-enrichment.session-stitching',
 N'Stitches multiple page views into a coherent visitor journey using device fingerprint, IP, and behavioral signals — no cookies required.');

UPDATE n SET DescMarketing = m.Copy
FROM Health.Node n JOIN @mkt m ON m.Slug = n.Slug;

-- ============================================================================
-- 2. MANAGEMENT — factual "what this is" for every active node
-- ============================================================================
DECLARE @mgmt TABLE (Slug VARCHAR(200) PRIMARY KEY, Copy NVARCHAR(800));

INSERT INTO @mgmt VALUES
-- PLATFORM
('smartpixl',
 N'The whole platform. Three Windows processes (IIS Edge + Forge service + Sentinel service) on one host writing to SQL Server 2025. Total outage stops all data flow; partial outages degrade gracefully.'),

-- SYSTEMS
('edge',
 N'IIS-hosted ASP.NET Core site that answers every /_SMART.GIF request. Parses the URL, runs fast enrichments, and forwards the hit to Forge over a named pipe. Falls back to disk JSONL if the pipe is down.'),
('forge',
 N'Windows Service running the enrichment pipeline, SQL writer, failover, ETL, and background IP enrichment. Reads from the named pipe, writes to PiXL.Parsed via SqlBulkCopy.'),
('sentinel',
 N'Windows Service on port 7500 that hosts the operations dashboards, documentation portal, metrics, and design editor. Read-only against SQL; does not participate in data flow.'),

-- FORGE SUBSYSTEMS
('forge.f1-ingest',
 N'Stage 1 of Forge. Named pipe server accepts connections from Edge, deserializes hits, pushes them onto the enrichment channel. Backpressure from the channel eventually causes Edge to fall back to JSONL.'),
('forge.f2-enrichment',
 N'Stage 2 of Forge. Adaptive pool of enrichment workers reading from the ingest channel and running all 16 enrichment services in sequence per hit. CPU-heavy stage.'),
('forge.f3-sql-writer',
 N'Stage 3 of Forge. Reads enriched hits from the post-enrichment channel and flushes them to PiXL.Parsed in batches via SqlBulkCopy.'),
('forge.f4-failover',
 N'Stage 4 of Forge. Writes JSONL files when SQL is unavailable, plus replays any unreplayed files (from Edge, Forge, or dead-letter) when the system is healthy.'),
('forge.f5-etl',
 N'Stage 5 of Forge. Background ETL that runs usp_MatchVisits and usp_MatchLegacyVisits every 60 seconds to link new hits into visitor identities.'),
('forge.f6-background-ip',
 N'Stage 6 of Forge. Off-hot-path workers that fill in reverse-DNS and WHOIS ASN data for IPs that were not cached at enrichment time.'),
('forge.f7-data-sync',
 N'Stage 7 of Forge. Scheduled jobs that sync customer/PiXL settings from Xavier and import IP and OS reference datasets.'),
('forge.f7-data-sync.ip-data-acquisition',
 N'Imports the two public IP datasets (IPtoASN and DB-IP Lite) into the IPInfo schema on a schedule.'),
('forge.f7-data-sync.os-eol-acquisition',
 N'Daily fetch of OS end-of-life data from endoflife.date for the five tracked products (windows, macos, ios, android, ipados).'),

-- SENTINEL SUBSYSTEMS
('sentinel.s1-infra-watch',
 N'Local probes against the host itself — SQL connectivity, Windows service states, IIS reachability, and Sentinel''s own process health.'),
('sentinel.s2-health-aggregation',
 N'Polls Edge and Forge health endpoints, combines them with local probes, builds the unified Health.Node tree that powers the Tron dashboard.'),
('sentinel.s3-dashboard-api',
 N'Endpoints and static SPAs for the Tron operations dashboard and the Pipeline Explorer. Read-only data API backed by dashboard cache.'),
('sentinel.s4-atlas',
 N'Markdown-driven documentation portal that interleaves live metrics with written content. Read-only against SQL.'),
('sentinel.s5-brilliantpixl-metrics',
 N'Management-facing analytics dashboard computed from PiXL.Parsed. Heavier SQL than other Sentinel surfaces; uses cache warmer.'),

-- FORGE COMPONENTS
-- (ip-data-acquisition and os-eol-acquisition share their slugs with their subsystems
--  so no separate entries needed — the subsystem rows above cover them.)

-- EDGE PROBES
('edge.http-listener',
 N'Kestrel listener under IIS InProcess. If the Edge process is up, this probe is green.'),
('edge.capture-pipeline',
 N'Request-to-TrackingData conversion: parses URL, runs fast enrichments (IP classification, UA), hands off to the pipe client.'),
('edge.pipe-client',
 N'Named-pipe client that serializes each hit and writes it to Forge''s PipeListener.'),
('edge.jsonl-failover',
 N'Fallback writer that persists hits to disk when the pipe is unavailable. Forge replays these files on its next healthy cycle.'),

-- F1
('forge.f1-ingest.pipe-listener',
 N'Named-pipe server accepting concurrent Edge connections and deserializing each hit into the ingest channel.'),
('forge.f1-ingest.enrichment-channel',
 N'Bounded Channel<T> between PipeListener and the enrichment worker pool. Backpressure here is the primary flow-control signal.'),

-- F2
('forge.f2-enrichment.worker-pool',
 N'Adaptive pool of Task workers draining the ingest channel, running the enrichment pipeline on each hit, writing to the post-enrichment channel.'),
('forge.f2-enrichment.ua-parsing',
 N'User-agent parser with a bounded cache keyed by UA string.'),
('forge.f2-enrichment.bot-ua-detection',
 N'Bot classifier based on UA string patterns, with a bounded cache.'),
('forge.f2-enrichment.dns-lookup',
 N'Reverse-DNS cache hot-path lookup. Misses defer to the F6 background worker; no synchronous DNS on the enrichment path.'),
('forge.f2-enrichment.whois-asn',
 N'WHOIS ASN cache hot-path lookup. Misses defer to F6.'),
('forge.f2-enrichment.maxmind-geo',
 N'MaxMind GeoIP2 in-memory reader returning country, city, lat/lon for each IP.'),
('forge.f2-enrichment.dead-internet',
 N'Dead Internet Theory signal scorer: flags AI-generated and synthetic traffic fingerprints.'),
('forge.f2-enrichment.behavioral-replay',
 N'Behavioral replay detector for automated browsing patterns (timing, cadence, precision).'),
('forge.f2-enrichment.cross-customer-intel',
 N'Cross-customer intelligence tracker — flags IPs and fingerprints seen misbehaving on other SmartPiXL-protected sites.'),
('forge.f2-enrichment.session-stitching',
 N'Stitches page views into session identities using device fingerprint, IP, and time windows.'),
('forge.f2-enrichment.ip-classification',
 N'Classifies IPs as datacenter, residential, mobile, VPN, Tor, hosting, or corporate.'),
('forge.f2-enrichment.contradiction-matrix',
 N'Cross-checks device/network/geographic signals for inconsistencies (e.g. iOS UA on x86 GPU).'),
('forge.f2-enrichment.device-affluence',
 N'Scores device affluence from GPU tier, display density, and hardware capability signals.'),
('forge.f2-enrichment.device-age-estimation',
 N'Estimates device age from browser version, OS version, and hardware markers.'),
('forge.f2-enrichment.geographic-arbitrage',
 N'Flags IP-geo vs. claimed-timezone/locale mismatches (VPN/proxy/tunnel indicators).'),
('forge.f2-enrichment.gpu-tier-reference',
 N'Classifies WebGL renderer strings into GPU tiers (low/mid/high/flagship).'),
('forge.f2-enrichment.lead-quality-scoring',
 N'Composite score combining the other 15 enrichment outputs into a single lead-quality number.'),

-- F3
('forge.f3-sql-writer.bulk-copy',
 N'SqlBulkCopy flushing enriched hits into PiXL.Parsed in configurable batches.'),

-- F4
('forge.f4-failover.failover-writer',
 N'Writes enriched-hit JSONL to disk when SQL is unavailable (circuit-breaker open).'),
('forge.f4-failover.replay-service',
 N'Unified replayer: drains Edge failover files, Forge failover files, and the dead-letter queue back into the pipeline.'),

-- F5
('forge.f5-etl.match-visits',
 N'Runs usp_MatchVisits every 60 seconds to resolve recent hits to visitor identities.'),
('forge.f5-etl.match-legacy-visits',
 N'Runs usp_MatchLegacyVisits every 60 seconds to fold in hits that came via the Xavier pipeline.'),

-- F6
('forge.f6-background-ip.dns-enrichment',
 N'Background workers that resolve reverse-DNS for IPs that missed the enrichment-path cache.'),
('forge.f6-background-ip.whois-enrichment',
 N'Background workers that resolve WHOIS ASN for IPs that missed the enrichment-path cache.'),

-- F7
('forge.f7-data-sync.company-pixel-sync',
 N'Pulls PiXL.Company and PiXL.Settings from the Xavier legacy database every 6 hours.'),
('forge.f7-data-sync.ip-data-acquisition.iptoasn',
 N'Daily import of the IPtoASN public dataset into IPInfo schema.'),
('forge.f7-data-sync.ip-data-acquisition.dbip',
 N'Monthly import of the DB-IP Lite public dataset into IPInfo schema.'),
('forge.f7-data-sync.os-eol-acquisition.data-loaded',
 N'Indicates the OS EOL service has loaded lifecycle data for at least one product.'),
('forge.f7-data-sync.os-eol-acquisition.all-products',
 N'Indicates all five tracked OS products (windows, macos, ios, android, ipados) are loaded.'),

-- SENTINEL PROBES
('sentinel.self',                                          N'Sentinel process liveness. If this responds, Sentinel is up.'),
('sentinel.sql-connectivity',                              N'SQL connection test and a basic query against SmartPiXL.'),
('sentinel.windows-services',                              N'Status of the four critical services: SQL Server, IIS, Forge, Sentinel.'),
('sentinel.iis-reachability',                              N'HTTP probe that the Edge IIS site is answering.'),
('sentinel.s2-health-aggregation.edge-reachable',          N'HTTP probe against Edge''s internal health endpoint.'),
('sentinel.s2-health-aggregation.forge-reachable',         N'HTTP probe against Forge''s health endpoint.'),
('sentinel.s2-health-aggregation.tree-build',              N'Full health-tree build completes successfully and decoration finishes.'),
('sentinel.s3-dashboard-api.dash-endpoints',               N'Dashboard data API returns valid JSON.'),
('sentinel.s3-dashboard-api.tron-spa',                     N'Tron operations dashboard HTML serves correctly.'),
('sentinel.s3-dashboard-api.pipeline-spa',                 N'Pipeline Explorer HTML serves correctly.'),
('sentinel.s4-atlas.atlas-spa',                            N'Atlas documentation portal HTML serves correctly.'),
('sentinel.s4-atlas.markdown-loader',                      N'Markdown atlas service has loaded at least one section.'),
('sentinel.s4-atlas.live-metrics',                         N'Atlas metrics endpoint returns data.'),
('sentinel.s5-brilliantpixl-metrics.brilliantpixl-spa',    N'BrilliantPiXL dashboard HTML serves correctly.'),
('sentinel.s5-brilliantpixl-metrics.js-metrics-api',       N'BrilliantPiXL JSON endpoint returns current JS-hit analytics.');

UPDATE n SET DescManagement = m.Copy
FROM Health.Node n JOIN @mgmt m ON m.Slug = n.Slug;

-- ============================================================================
-- 3. DEVELOPER — gotchas, contracts, file pointers. Only where warranted.
-- ============================================================================
DECLARE @dev TABLE (Slug VARCHAR(200) PRIMARY KEY, Copy NVARCHAR(1600));

INSERT INTO @dev VALUES
-- PLATFORM / SYSTEMS
('smartpixl',
 N'Three processes, one host, one SQL instance (localhost\SQL2025). Dev IS live; there is no staging. Connection strings, ports, and IIS bindings live in appsettings.json for each project AND in the deployed copies under C:\inetpub\Smartpixl.info\ (Edge) and C:\Services\ (Forge, Sentinel). When changing config, update all relevant locations — see /memories/repo/owner-principles.md.'),

('edge',
 N'IIS InProcess hosting. web.config is owned by IIS and gets clobbered on every `dotnet publish` — if you change it, also update the csproj PublishIISSettings. Ports in appsettings.json are ignored under InProcess; Sentinel reaches Edge via 127.0.0.1:80 loopback. Failover directory: C:\inetpub\Smartpixl.info\Failover\.'),

('forge',
 N'Windows Service, standalone .NET 10 host. Kestrel listens on 127.0.0.1:7100 for health only. All hit flow is via named pipe from Edge and SqlBulkCopy to SQL. If the service is stopped, Edge will begin writing JSONL failover files that Forge replays on next start.'),

('sentinel',
 N'Windows Service on 7500. SentinelAccessControl.IsAllowed(ctx) guards every write endpoint; add your new endpoints behind this check. Static assets served from wwwroot. All read endpoints should be cached — see DashboardCacheWarmerService for the pattern.'),

-- FORGE SUBSYSTEMS
('forge.f1-ingest',
 N'Named pipe name "SmartPiXL.Pipe". Server side accepts concurrent client connections up to the configured max. Messages are length-prefixed JSON. If the enrichment channel is full, PipeListener applies backpressure by not reading from the pipe, which stalls Edge writes and eventually triggers Edge JSONL failover.'),

('forge.f2-enrichment',
 N'Worker pool is adaptive; do not hard-code worker count. Each worker drains one ingest record, runs all enrichment services in sequence, and pushes to the post-enrichment channel. Services must be thread-safe AND stateless — shared state belongs in BoundedCache. Per-record budget target: < 5ms p95.'),

('forge.f3-sql-writer',
 N'SqlBulkCopy against PiXL.Parsed. Batch size and timeout in appsettings. On SQL failure, opens the circuit breaker which diverts the channel to F4 failover writer. Circuit resets on probe success.'),

('forge.f4-failover',
 N'Three failover sources handled by ONE replay service: Edge JSONL (from Edge Failover/), Forge JSONL (from Forge Failover/), and the dead-letter queue. Replay is ordered by file mtime, idempotent by HitId, and skips files < 30s old to avoid racing Edge still writing.'),

('forge.f5-etl',
 N'Two stored procs on a 60s cadence: usp_MatchVisits (modern pipeline) and usp_MatchLegacyVisits (Xavier data). Both use ETL.Watermark to track progress; reset with UPDATE ETL.Watermark SET LastProcessedId=0 WHERE ProcessName=''ParseNewHits''. Long-running matches signal backup pressure in the visit table.'),

('forge.f6-background-ip',
 N'Off hot-path. Pulls unresolved IPs from a work queue and updates the IP cache tables. Failure here silently degrades DNS/WHOIS coverage — no dashboard alarm unless the queue length diverges.'),

('forge.f7-data-sync',
 N'Scheduled: CompanyPiXLSyncService every 6h; IpDataAcquisitionService daily (IPtoASN) / monthly (DB-IP); OsEndOfLifeService daily. All schedules are in appsettings; missed runs retry on next interval, no catch-up.'),

-- SENTINEL SUBSYSTEMS
('sentinel.s1-infra-watch',
 N'InfraHealthService runs local probes on a timer. SQL Server service must be named "MSSQL$SQL2025". IIS check uses WebAdministration module via PowerShell — runs as the Sentinel service account which must have local admin (historical footgun).'),

('sentinel.s2-health-aggregation',
 N'HealthTreeService builds the tree in two passes: structure (from Health.Node table, cached), then decoration (from Edge/Forge HTTP polls). Call InvalidateTreeStructure() after any Health.Node write or you will serve a stale tree.'),

('sentinel.s3-dashboard-api',
 N'All endpoints accept cached reads. Do not add direct SQL queries in endpoint handlers — go through the dashboard cache layer. SPA HTML files are served with no-cache headers so refreshes always pick up deploys.'),

('sentinel.s4-atlas',
 N'MarkdownAtlasService loads all .md files from docs/atlas/ into memory at startup. Live-metric placeholders in markdown use {{metric:key}} syntax and are resolved at render time. Hot-reload is NOT implemented — a doc change requires a service restart.'),

('sentinel.s5-brilliantpixl-metrics',
 N'Heaviest SQL in Sentinel. Every metric endpoint goes through dashboard cache with 60s TTL by default. New metrics: put the query behind cache, not in the handler.'),

-- EDGE PROBES
('edge.capture-pipeline',
 N'TrackingCaptureService.Capture() is the entry point. Fast enrichments are IP classification and UA parsing; everything heavy defers to Forge. Per-request budget target: < 2ms p95.'),

('edge.pipe-client',
 N'PipeClientService keeps a persistent NamedPipeClientStream. On broken pipe, increments a failure counter; after threshold, routes writes to JsonlFailoverService instead of reconnecting tightly.'),

('edge.jsonl-failover',
 N'Writes one JSON object per line to a rolling file in Failover/. Files are rotated by size and age. Forge''s replay service picks them up — do not delete files out from under it.'),

-- F1 PROBES
('forge.f1-ingest.pipe-listener',
 N'PipeListenerService accepts N concurrent streams (configurable). Each stream gets its own Task that reads length-prefixed JSON and pushes onto ForgeChannels.IngestChannel. Do not add synchronous work here — every millisecond is backpressure on Edge.'),

('forge.f1-ingest.enrichment-channel',
 N'Channel<ParsedRecord> with bounded capacity. If this fills, PipeListener stops reading and Edge falls back to JSONL. Channel size lives in appsettings — tuning this is the primary knob for burst tolerance vs. memory.'),

-- F2 PROBES
('forge.f2-enrichment.worker-pool',
 N'Adaptive worker count reacts to channel depth. Services are injected per-worker — assume they are called from many threads concurrently. Caches must use BoundedCache; ad-hoc Dictionary<,> will leak.'),

('forge.f2-enrichment.ua-parsing',
 N'BoundedCache<string, ParsedUserAgent> keyed on the raw UA string. Cold lookups are regex-heavy — keep the cache size generous.'),

('forge.f2-enrichment.bot-ua-detection',
 N'Pattern list is compiled once at startup; update the patterns file to change detection. Cache is bounded by UA string count, not by bot population.'),

('forge.f2-enrichment.dns-lookup',
 N'Hot path does cache-only lookup; misses go to the F6 background worker. Do NOT add System.Net.Dns calls here — it will serialize the entire enrichment pool on the slowest resolver.'),

('forge.f2-enrichment.whois-asn',
 N'Same pattern as dns-lookup: cache-only hot path, async miss handling on F6.'),

('forge.f2-enrichment.maxmind-geo',
 N'In-memory MaxMind reader loaded from the GeoLite2 file. Reader is thread-safe but the underlying file handle is per-process — a file swap requires a service restart.'),

('forge.f2-enrichment.dead-internet',
 N'DeadInternetService scores per-hit; result is attached as enrichment fields. Eviction is LRU-bounded. Tests in SmartPiXL.Tests/DeadInternetServiceTests.cs cover the scoring rubric.'),

('forge.f2-enrichment.behavioral-replay',
 N'BehavioralReplayService keeps a bounded recent-hit buffer for pattern comparison. IDisposable — the service lifecycle is owned by DI. Do not new() one up.'),

('forge.f2-enrichment.cross-customer-intel',
 N'Backed by a SQL lookup against cross-customer IP/fingerprint intel with a TTL cache in front. SQL miss latency dominates if the cache is undersized.'),

('forge.f2-enrichment.session-stitching',
 N'Stitching window and key composition (device fingerprint + IP + UA) live in appsettings. Expired sessions are evicted on a timer — count in the health probe should stay bounded.'),

('forge.f2-enrichment.ip-classification',
 N'IpClassificationService backed by CidrTrie for range lookups against loaded IPInfo data. Trie is rebuilt on IP data reload — synchronized via a swap pointer; no lock on hot path.'),

('forge.f2-enrichment.contradiction-matrix',
 N'Matrix rules are data-driven; new contradictions ship as rule rows, not code changes. Per-hit cost is O(rules × signals); keep the rule set tight.'),

('forge.f2-enrichment.device-affluence',
 N'Scoring model uses GPU tier, display pixel ratio, and hardware concurrency. Update the scoring weights in appsettings — do not bake them into code.'),

('forge.f2-enrichment.device-age-estimation',
 N'Depends on OS EOL data from forge.f7-data-sync.os-eol-acquisition. If OS EOL data is not loaded, this service returns null and downstream scoring adjusts.'),

('forge.f2-enrichment.geographic-arbitrage',
 N'Compares claimed locale / timezone against MaxMind geo. Requires geo probe to be green — if MaxMind is not loaded, this service short-circuits to null.'),

('forge.f2-enrichment.gpu-tier-reference',
 N'Static reference table compiled into GpuTierReference.cs. Adding a new GPU = adding an entry and re-deploying. No runtime loading.'),

('forge.f2-enrichment.lead-quality-scoring',
 N'Runs LAST in the pipeline because it consumes outputs from the other 15 services. Scoring weights live in appsettings under LeadQuality. Null inputs are treated as 0.5 (unknown), not as missing.'),

-- F3 PROBES
('forge.f3-sql-writer.bulk-copy',
 N'SqlBulkCopy against PiXL.Parsed. Batch size in appsettings. SQL failure opens the circuit breaker (per-host counter); breaker resets on probe success. ParsedBulkInsertService is a companion writer for specific shapes.'),

-- F4 PROBES
('forge.f4-failover.failover-writer',
 N'Writes JSONL to Forge Failover/. File rotation by size and age. Marker file .writing is written during active writes so replay skips in-progress files.'),

('forge.f4-failover.replay-service',
 N'Drains three directories in priority order: Edge Failover, Forge Failover, dead-letter. Per-file: re-parse, re-enqueue to enrichment channel, delete on success. Idempotent because HitId is unique in PiXL.Parsed.'),

-- F5 PROBES
('forge.f5-etl.match-visits',
 N'usp_MatchVisits in SQL. Uses ETL.Watermark[ProcessName=''MatchVisits'']. Long runtime usually means the visit table is growing faster than the match rate — check for missing indexes or lock contention.'),

('forge.f5-etl.match-legacy-visits',
 N'usp_MatchLegacyVisits against Xavier-sourced data. Separate watermark row. Only relevant while the Xavier pipeline is still active.'),

-- F6 PROBES
('forge.f6-background-ip.dns-enrichment',
 N'Pulls from an IP work queue, does System.Net.Dns lookups with a timeout, upserts into the DNS cache table. Bounded concurrency; one failing resolver will NOT stall the whole pool.'),

('forge.f6-background-ip.whois-enrichment',
 N'Same pattern as DNS: queue-driven, bounded concurrency, WHOIS lookups with timeout, upsert into ASN cache.'),

-- F7 PROBES
('forge.f7-data-sync.company-pixel-sync',
 N'6-hour timer. Reads from Xavier (legacy SQL) and upserts into PiXL.Company / PiXL.Settings. Connection string for Xavier is separate — check appsettings CompanySync section.'),

('forge.f7-data-sync.ip-data-acquisition.iptoasn',
 N'Daily download from IPtoASN public dataset; staged into a temp table then swapped via sp_rename to IPInfo target. Swap is atomic — readers see the old or new, never a mix.'),

('forge.f7-data-sync.ip-data-acquisition.dbip',
 N'Monthly DB-IP Lite import. Same staged-swap pattern as IPtoASN. Large — run during off-hours if manually triggered.'),

('forge.f7-data-sync.os-eol-acquisition',
 N'OsEndOfLifeService.IsLoaded is the health flag. Hits endoflife.date API daily for five products; caches locally on disk as fallback for API outage.'),

('forge.f7-data-sync.os-eol-acquisition.data-loaded',
 N'True when OsEndOfLifeService.GetProducts().Any() is true.'),

('forge.f7-data-sync.os-eol-acquisition.all-products',
 N'True when all five tracked products (windows, macos, ios, android, ipados) have lifecycle data loaded.'),

-- SENTINEL PROBES (skipping truly trivial ones: self, iis-reachability, sql-connectivity, windows-services,
--                 SPA HTML probes, edge-reachable, forge-reachable)
('sentinel.s2-health-aggregation.tree-build',
 N'Full tree build runs on every /api/health-tree hit unless cached. Decoration phase is the slow part (HTTP polls to Edge and Forge). Cache TTL in appsettings under HealthTree.CacheSeconds.'),

('sentinel.s4-atlas.markdown-loader',
 N'MarkdownAtlasService.GetSections().Count must be > 0. Loader runs once at startup from docs/atlas/; zero sections usually means the docs folder is missing from the publish output.'),

('sentinel.s4-atlas.live-metrics',
 N'Metrics placeholders are resolved per-render. If SQL is down, placeholders render as em-dashes, not errors. Adding a new metric: register it in MarkdownAtlasService''s metric provider map.'),

('sentinel.s3-dashboard-api.dash-endpoints',
 N'Contract: all endpoints return JSON with { ok, data, ts } envelope. Error cases return 200 with ok=false, NOT 4xx/5xx — Tron expects the envelope shape.'),

('sentinel.s5-brilliantpixl-metrics.js-metrics-api',
 N'Endpoint queries PiXL.Parsed with time-window filters. All queries MUST go through dashboard cache (BrilliantPiXLEndpoints wraps them with a 60s TTL). Do not bypass — heavy SQL here can spike SQL Server CPU.');

UPDATE n SET DescDeveloper = d.Copy
FROM Health.Node n JOIN @dev d ON d.Slug = n.Slug;

-- ============================================================================
-- 4. REPORT
-- ============================================================================
SELECT 'Nodes total'                AS Metric, COUNT(*) AS N FROM Health.Node WHERE IsActive=1
UNION ALL SELECT 'With Marketing',   COUNT(*)             FROM Health.Node WHERE IsActive=1 AND DescMarketing  IS NOT NULL
UNION ALL SELECT 'With Management',  COUNT(*)             FROM Health.Node WHERE IsActive=1 AND DescManagement IS NOT NULL
UNION ALL SELECT 'With Developer',   COUNT(*)             FROM Health.Node WHERE IsActive=1 AND DescDeveloper  IS NOT NULL;

PRINT '88_DesignTreeContentRev.sql complete.';
