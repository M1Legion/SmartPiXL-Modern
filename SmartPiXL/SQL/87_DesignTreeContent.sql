-- ============================================================================
-- 87_DesignTreeContent.sql
-- Fill Owner / DescMarketing / DescManagement across all active design nodes,
-- and seed Design.CodeLink rows pointing at real repo paths.
-- Idempotent: safe to re-run; uses MERGE-style UPDATE only where fields blank.
-- ============================================================================

USE SmartPiXL;
SET NOCOUNT ON;

-- ----------------------------------------------------------------------------
-- 1. OWNER — assign by system branch
-- ----------------------------------------------------------------------------
UPDATE Health.Node SET Owner = 'platform'      WHERE IsActive=1 AND Slug = 'smartpixl' AND (Owner IS NULL OR Owner='');
UPDATE Health.Node SET Owner = 'edge-team'     WHERE IsActive=1 AND (Slug = 'edge'     OR Slug LIKE 'edge.%')     AND (Owner IS NULL OR Owner='');
UPDATE Health.Node SET Owner = 'forge-team'    WHERE IsActive=1 AND (Slug = 'forge'    OR Slug LIKE 'forge.%')    AND (Owner IS NULL OR Owner='');
UPDATE Health.Node SET Owner = 'sentinel-team' WHERE IsActive=1 AND (Slug = 'sentinel' OR Slug LIKE 'sentinel.%') AND (Owner IS NULL OR Owner='');

-- ----------------------------------------------------------------------------
-- 2. MARKETING + MANAGEMENT copy for PLATFORM / SYSTEM / SUBSYSTEM / COMPONENT
-- ----------------------------------------------------------------------------
-- Helper: only update if field is currently null/empty.
DECLARE @copy TABLE (Slug VARCHAR(200) PRIMARY KEY, Mkt NVARCHAR(600), Mgmt NVARCHAR(600));

INSERT INTO @copy VALUES
-- PLATFORM
('smartpixl',
 N'SmartPiXL is the traffic intelligence platform that turns every website visit into rich, fraud-resistant intelligence — even when cookies are blocked.',
 N'Core revenue surface. Full outage stops all data capture, enrichment, dashboards, and reporting. Three cooperating processes on one Windows host.'),

-- SYSTEMS
('edge',
 N'The always-on capture layer that responds in microseconds to every tracking pixel from every browser.',
 N'IIS-hosted hot path. Any Edge outage means lost traffic data with no recovery — failover JSONL mitigates downstream pipe loss only.'),
('forge',
 N'The intelligence engine that turns raw hits into enriched, stitched, business-ready visitor profiles.',
 N'Windows Service running enrichment workers, SQL writer, ETL, and background IP acquisition. Down = no new identity matches or dashboards.'),
('sentinel',
 N'The operations and insights portal: health dashboards, live metrics, documentation, and design surface.',
 N'Windows Service on port 7500. Outage does not impact data capture but blinds operations — no health tree, no Atlas, no BrilliantPiXL.'),

-- FORGE SUBSYSTEMS
('forge.f1-ingest',
 N'The inbound gateway that accepts every enriched hit from Edge over a high-throughput named pipe.',
 N'F1 stage. Bottleneck risk if enrichment channel fills; Edge then falls back to JSONL.'),
('forge.f2-enrichment',
 N'16 enrichment services work in parallel to classify, stitch, score, and interpret every visitor signal.',
 N'F2 stage. Adaptive worker pool — core CPU cost. Slowdown here backs up the channel and eventually triggers failover.'),
('forge.f3-sql-writer',
 N'High-throughput writer that streams enriched hits into SQL in efficient batches.',
 N'F3 stage. SqlBulkCopy to PiXL.Parsed. Circuit-breaker on SQL failure writes to failover JSONL; replay on next healthy window.'),
('forge.f4-failover',
 N'The safety net that captures every hit to disk when SQL is unavailable and replays them the moment it recovers.',
 N'F4 stage. JSONL files under Forge Failover directory; unified replay covers Edge-side, Forge-side, and dead-letter.'),
('forge.f5-etl',
 N'The identity-resolution engine that links visits into visitor journeys every minute.',
 N'F5 stage. Runs usp_MatchVisits and usp_MatchLegacyVisits every 60s. Lag here delays dashboards and reporting.'),
('forge.f6-background-ip',
 N'Background enrichment of IP metadata (DNS, WHOIS/ASN) off the hot path.',
 N'F6 stage. Non-blocking — delay does not stall intake, but slows signal completeness.'),
('forge.f7-data-sync',
 N'Scheduled refresh of reference data: customer settings, IP databases, and OS end-of-life lifecycles.',
 N'F7 stage. Daily / hourly jobs. Stale reference data slowly degrades classification accuracy.'),
('forge.f7-data-sync.ip-data-acquisition',
 N'Refreshes the IP intelligence dataset (IPtoASN and DB-IP Lite) used by every classification service.',
 N'Scheduled monthly and daily imports. Import failure silently degrades IP-based signals after ~14 days.'),
('forge.f7-data-sync.os-eol-acquisition',
 N'Keeps operating-system end-of-life data fresh for device age and risk scoring.',
 N'Daily fetch from endoflife.date API for windows, macos, ios, android, ipados.'),

-- SENTINEL SUBSYSTEMS
('sentinel.s1-infra-watch',
 N'Continuously watches the hosting infrastructure itself: SQL, Windows services, IIS, and Sentinel''s own health.',
 N'First line of defense. All Sentinel dashboards depend on these probes being truthful.'),
('sentinel.s2-health-aggregation',
 N'Rolls Edge, Forge, and local probe signals into one unified health tree.',
 N'Powers the Tron dashboard. Degraded aggregation = degraded visibility; data still flows.'),
('sentinel.s3-dashboard-api',
 N'Backs the Tron operations dashboard and the Pipeline Explorer with live data endpoints.',
 N'Serves SPAs and their JSON feeds; outage blinds ops but does not touch capture.'),
('sentinel.s4-atlas',
 N'The documentation and architecture portal with live metrics woven into the narrative.',
 N'Renders markdown docs with live numbers from SQL. Read-only; outage is cosmetic.'),
('sentinel.s5-brilliantpixl-metrics',
 N'Executive-facing analytics dashboard: human vs. bot, channels, fingerprint coverage, lead quality, geography.',
 N'Management surface. Driven from PiXL.Parsed. Heavy queries — watch cache hit rate.');

UPDATE n
   SET DescMarketing  = CASE WHEN n.DescMarketing  IS NULL OR n.DescMarketing  = '' THEN c.Mkt  ELSE n.DescMarketing  END,
       DescManagement = CASE WHEN n.DescManagement IS NULL OR n.DescManagement = '' THEN c.Mgmt ELSE n.DescManagement END
FROM Health.Node n
JOIN @copy c ON c.Slug = n.Slug;

-- ----------------------------------------------------------------------------
-- 3. PROBE DEFAULTS — derive marketing/management from Description
-- ----------------------------------------------------------------------------
UPDATE Health.Node
   SET DescMarketing  = N'Verifies that ' + LOWER(LEFT(Description,1)) + SUBSTRING(Description,2,4000) + N'.'
 WHERE IsActive=1 AND NodeType='probe' AND (DescMarketing IS NULL OR DescMarketing='')
       AND Description IS NOT NULL AND LEN(Description) > 0;

UPDATE Health.Node
   SET DescManagement = N'Health probe. Green = ' + Description + N'. Red triggers alert.'
 WHERE IsActive=1 AND NodeType='probe' AND (DescManagement IS NULL OR DescManagement='')
       AND Description IS NOT NULL AND LEN(Description) > 0;

-- ----------------------------------------------------------------------------
-- 4. CODE LINKS — seed primary file per node (verified repo paths)
-- ----------------------------------------------------------------------------
DECLARE @links TABLE (Slug VARCHAR(200), Kind VARCHAR(20), Path NVARCHAR(500), IsPrimary BIT, Notes NVARCHAR(200));

INSERT INTO @links VALUES
-- EDGE
('edge',                                      'code','SmartPiXL/Program.cs',                                                     1, N'Edge host + Kestrel wiring'),
('edge.http-listener',                        'code','SmartPiXL/Program.cs',                                                     1, N'Kestrel/IIS request pipeline'),
('edge.capture-pipeline',                     'code','SmartPiXL/Services/TrackingCaptureService.cs',                             1, NULL),
('edge.pipe-client',                          'code','SmartPiXL/Services/PipeClientService.cs',                                  1, NULL),
('edge.jsonl-failover',                       'code','SmartPiXL/Services/JsonlFailoverService.cs',                               1, NULL),

-- FORGE
('forge',                                     'code','SmartPiXL.Forge/Program.cs',                                               1, N'Forge service host'),
('forge.f1-ingest',                           'code','SmartPiXL.Forge/Services/PipeListenerService.cs',                          1, NULL),
('forge.f1-ingest.pipe-listener',             'code','SmartPiXL.Forge/Services/PipeListenerService.cs',                          1, NULL),
('forge.f1-ingest.enrichment-channel',        'code','SmartPiXL.Forge/Services/ForgeChannels.cs',                                1, N'Channel<T> declarations'),
('forge.f2-enrichment',                       'code','SmartPiXL.Forge/Services/EnrichmentPipelineService.cs',                    1, N'Adaptive worker pool'),
('forge.f2-enrichment.worker-pool',           'code','SmartPiXL.Forge/Services/EnrichmentPipelineService.cs',                    1, NULL),
('forge.f2-enrichment.ua-parsing',            'code','SmartPiXL.Forge/Services/Enrichments/UaParsingService.cs',                 1, NULL),
('forge.f2-enrichment.bot-ua-detection',      'code','SmartPiXL.Forge/Services/Enrichments/BotUaDetectionService.cs',            1, NULL),
('forge.f2-enrichment.dns-lookup',            'code','SmartPiXL.Forge/Services/Enrichments/DnsLookupService.cs',                 1, NULL),
('forge.f2-enrichment.whois-asn',             'code','SmartPiXL.Forge/Services/Enrichments/WhoisAsnService.cs',                  1, NULL),
('forge.f2-enrichment.maxmind-geo',           'code','SmartPiXL.Forge/Services/Enrichments/MaxMindGeoService.cs',                1, NULL),
('forge.f2-enrichment.dead-internet',         'code','SmartPiXL.Forge/Services/Enrichments/DeadInternetService.cs',              1, NULL),
('forge.f2-enrichment.behavioral-replay',     'code','SmartPiXL.Forge/Services/Enrichments/BehavioralReplayService.cs',          1, NULL),
('forge.f2-enrichment.cross-customer-intel',  'code','SmartPiXL.Forge/Services/Enrichments/CrossCustomerIntelService.cs',        1, NULL),
('forge.f2-enrichment.session-stitching',     'code','SmartPiXL.Forge/Services/Enrichments/SessionStitchingService.cs',          1, NULL),
('forge.f2-enrichment.ip-classification',     'code','SmartPiXL.Shared/Services/IpClassificationService.cs',                     1, NULL),
('forge.f2-enrichment.contradiction-matrix',  'code','SmartPiXL.Forge/Services/Enrichments/ContradictionMatrixService.cs',       1, NULL),
('forge.f2-enrichment.device-affluence',      'code','SmartPiXL.Forge/Services/Enrichments/DeviceAffluenceService.cs',           1, NULL),
('forge.f2-enrichment.device-age-estimation', 'code','SmartPiXL.Forge/Services/Enrichments/DeviceAgeEstimationService.cs',       1, NULL),
('forge.f2-enrichment.geographic-arbitrage',  'code','SmartPiXL.Forge/Services/Enrichments/GeographicArbitrageService.cs',       1, NULL),
('forge.f2-enrichment.gpu-tier-reference',    'code','SmartPiXL.Forge/Services/Enrichments/GpuTierReference.cs',                 1, NULL),
('forge.f2-enrichment.lead-quality-scoring',  'code','SmartPiXL.Forge/Services/Enrichments/LeadQualityScoringService.cs',        1, NULL),
('forge.f3-sql-writer',                       'code','SmartPiXL.Forge/Services/SqlBulkCopyWriterService.cs',                     1, NULL),
('forge.f3-sql-writer.bulk-copy',             'code','SmartPiXL.Forge/Services/SqlBulkCopyWriterService.cs',                     1, NULL),
('forge.f3-sql-writer.bulk-copy',             'code','SmartPiXL.Forge/Services/ParsedBulkInsertService.cs',                      0, N'Companion writer'),
('forge.f4-failover',                         'code','SmartPiXL.Forge/Services/ForgeFailoverWriter.cs',                          1, NULL),
('forge.f4-failover.failover-writer',         'code','SmartPiXL.Forge/Services/ForgeFailoverWriter.cs',                          1, NULL),
('forge.f4-failover.replay-service',          'code','SmartPiXL.Forge/Services/ForgeReplayService.cs',                           1, NULL),
('forge.f5-etl',                              'code','SmartPiXL.Forge/Services/EtlBackgroundService.cs',                         1, NULL),
('forge.f6-background-ip',                    'code','SmartPiXL.Forge/Services/BackgroundIpEnrichmentService.cs',                1, NULL),
('forge.f6-background-ip.dns-enrichment',     'code','SmartPiXL.Forge/Services/BackgroundIpEnrichmentService.cs',                1, NULL),
('forge.f6-background-ip.whois-enrichment',   'code','SmartPiXL.Forge/Services/BackgroundIpEnrichmentService.cs',                1, NULL),
('forge.f7-data-sync',                        'code','SmartPiXL.Forge/Services/CompanyPiXLSyncService.cs',                       1, NULL),
('forge.f7-data-sync.company-pixel-sync',     'code','SmartPiXL.Forge/Services/CompanyPiXLSyncService.cs',                       1, NULL),
('forge.f7-data-sync.ip-data-acquisition',    'code','SmartPiXL.Forge/Services/IpDataAcquisitionService.cs',                     1, NULL),
('forge.f7-data-sync.ip-data-acquisition.iptoasn', 'code','SmartPiXL.Forge/Services/IpDataAcquisitionService.cs',                1, NULL),
('forge.f7-data-sync.ip-data-acquisition.dbip',    'code','SmartPiXL.Forge/Services/IpDataAcquisitionService.cs',                1, NULL),
('forge.f7-data-sync.os-eol-acquisition',                 'code','SmartPiXL.Forge/Services/Enrichments/OsEndOfLifeService.cs',   1, NULL),
('forge.f7-data-sync.os-eol-acquisition.data-loaded',     'code','SmartPiXL.Forge/Services/Enrichments/OsEndOfLifeService.cs',   1, NULL),
('forge.f7-data-sync.os-eol-acquisition.all-products',    'code','SmartPiXL.Forge/Services/Enrichments/OsEndOfLifeService.cs',   1, NULL),

-- SENTINEL
('sentinel',                                  'code','SmartPiXL.Sentinel/Program.cs',                                            1, NULL),
('sentinel.sql-connectivity',                 'code','SmartPiXL.Sentinel/Services/InfraHealthService.cs',                        1, NULL),
('sentinel.windows-services',                 'code','SmartPiXL.Sentinel/Services/InfraHealthService.cs',                        1, NULL),
('sentinel.iis-reachability',                 'code','SmartPiXL.Sentinel/Services/InfraHealthService.cs',                        1, NULL),
('sentinel.self',                             'code','SmartPiXL.Sentinel/Program.cs',                                            1, NULL),
('sentinel.s1-infra-watch',                   'code','SmartPiXL.Sentinel/Services/InfraHealthService.cs',                        1, NULL),
('sentinel.s2-health-aggregation',            'code','SmartPiXL.Sentinel/Services/HealthTreeService.cs',                         1, NULL),
('sentinel.s2-health-aggregation.tree-build', 'code','SmartPiXL.Sentinel/Services/HealthTreeService.cs',                         1, NULL),
('sentinel.s2-health-aggregation.edge-reachable','code','SmartPiXL.Sentinel/Services/HttpEdgeHealthClient.cs',                   1, NULL),
('sentinel.s2-health-aggregation.forge-reachable','code','SmartPiXL.Sentinel/Services/HealthTreeService.cs',                     1, N'Forge HTTP probe lives here'),
('sentinel.s3-dashboard-api',                 'code','SmartPiXL.Sentinel/Endpoints/DashboardEndpoints.cs',                       1, NULL),
('sentinel.s3-dashboard-api.dash-endpoints',  'code','SmartPiXL.Sentinel/Endpoints/DashboardEndpoints.cs',                       1, NULL),
('sentinel.s3-dashboard-api.tron-spa',        'code','SmartPiXL.Sentinel/wwwroot/tron.html',                                     1, NULL),
('sentinel.s3-dashboard-api.pipeline-spa',    'code','SmartPiXL.Sentinel/wwwroot/pipeline.html',                                 1, NULL),
('sentinel.s4-atlas',                         'code','SmartPiXL.Sentinel/Services/MarkdownAtlasService.cs',                      1, NULL),
('sentinel.s4-atlas.markdown-loader',         'code','SmartPiXL.Sentinel/Services/MarkdownAtlasService.cs',                      1, NULL),
('sentinel.s4-atlas.live-metrics',            'code','SmartPiXL.Sentinel/Services/MarkdownAtlasService.cs',                      1, NULL),
('sentinel.s4-atlas.atlas-spa',               'code','SmartPiXL.Sentinel/Endpoints/AtlasEndpoints.cs',                           1, NULL),
('sentinel.s5-brilliantpixl-metrics',         'code','SmartPiXL.Sentinel/Endpoints/BrilliantPiXLEndpoints.cs',                   1, NULL),
('sentinel.s5-brilliantpixl-metrics.brilliantpixl-spa','code','SmartPiXL.Sentinel/wwwroot/brilliantpixl.html',                   1, NULL),
('sentinel.s5-brilliantpixl-metrics.js-metrics-api','code','SmartPiXL.Sentinel/Endpoints/BrilliantPiXLEndpoints.cs',             1, NULL);

-- Resolve slugs → node ids and insert (skip duplicates via UQ(NodeId,Path))
INSERT INTO Design.CodeLink (NodeId, Kind, Path, IsPrimary, Notes)
SELECT n.NodeId, l.Kind, l.Path, l.IsPrimary, l.Notes
FROM @links l
JOIN Health.Node n ON n.Slug = l.Slug AND n.IsActive = 1
WHERE NOT EXISTS (
    SELECT 1 FROM Design.CodeLink x WHERE x.NodeId = n.NodeId AND x.Path = l.Path
);

-- ----------------------------------------------------------------------------
-- 5. REPORT
-- ----------------------------------------------------------------------------
SELECT 'Nodes with Owner'        AS Metric, COUNT(*) AS N FROM Health.Node WHERE IsActive=1 AND Owner IS NOT NULL
UNION ALL SELECT 'Nodes with Marketing',  COUNT(*) FROM Health.Node WHERE IsActive=1 AND DescMarketing  IS NOT NULL AND LEN(DescMarketing)>0
UNION ALL SELECT 'Nodes with Management', COUNT(*) FROM Health.Node WHERE IsActive=1 AND DescManagement IS NOT NULL AND LEN(DescManagement)>0
UNION ALL SELECT 'CodeLink rows total',   COUNT(*) FROM Design.CodeLink;

PRINT '87_DesignTreeContent.sql complete.';
