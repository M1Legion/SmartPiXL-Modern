SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- ============================================================================
-- 74_HealthTreeSchema.sql — Health Tree metadata tables
-- ============================================================================
-- Creates the Health schema and Health.Node table for the hierarchical
-- health tree. Each node represents a platform, system, subsystem,
-- component, or probe. Probes are leaves with binary health (1/0).
-- Parent nodes aggregate as ratios: healthy/total.
--
-- The Metadata column uses SQL Server 2025 native JSON type for extensible
-- per-node data (source mapping, icons, health function descriptions, etc.)
--
-- Run once on: localhost\SQL2025 / SmartPiXL
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'Health')
    EXEC('CREATE SCHEMA Health');
GO

-- Drop and recreate for idempotency during development
IF OBJECT_ID('Health.Node', 'U') IS NOT NULL
    DROP TABLE Health.Node;
GO

CREATE TABLE Health.Node
(
    NodeId      INT IDENTITY(1,1) NOT NULL,
    ParentId    INT NULL,
    Slug        NVARCHAR(150) NOT NULL,
    Name        NVARCHAR(200) NOT NULL,
    NodeType    NVARCHAR(20) NOT NULL,        -- platform, system, subsystem, component, probe
    Description NVARCHAR(500) NULL,
    Metadata    NVARCHAR(MAX) NULL,           -- JSON: { source, sourceProbeName, sourceSubsystem, icon, healthFunction }
    SortOrder   INT NOT NULL DEFAULT 0,
    IsActive    BIT NOT NULL DEFAULT 1,

    CONSTRAINT PK_Health_Node PRIMARY KEY CLUSTERED (NodeId),
    CONSTRAINT FK_Health_Node_Parent FOREIGN KEY (ParentId) REFERENCES Health.Node(NodeId),
    CONSTRAINT UQ_Health_Node_Slug UNIQUE (Slug),
    CONSTRAINT CK_Health_Node_Type CHECK (NodeType IN ('platform','system','subsystem','component','probe'))
);
GO

-- Index for parent lookups (tree traversal)
CREATE NONCLUSTERED INDEX IX_Health_Node_ParentId ON Health.Node(ParentId) WHERE ParentId IS NOT NULL;
GO

-- ============================================================================
-- SEED DATA — Full health tree from SUBSYSTEM-WALKTHROUGH.md
-- ============================================================================
-- Metadata JSON fields:
--   source:           "edge" | "forge" | "sentinel" | null (container)
--   sourceProbeName:  Probe name in the health report (case-sensitive match)
--   sourceSubsystem:  For Forge probes, the SubsystemReport.Name value
--   healthFunction:   Human-readable description of what makes this probe healthy
--   icon:             CSS icon class (future UI use)
-- ============================================================================

SET IDENTITY_INSERT Health.Node ON;

-- ── Platform ────────────────────────────────────────────────────────────────
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (1, NULL, 'smartpixl', 'SmartPiXL', 'platform', 'Browser fingerprinting and traffic intelligence platform', NULL, 0);

-- ── Systems ─────────────────────────────────────────────────────────────────
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (2, 1, 'edge', 'Edge', 'system', 'IIS-hosted pixel capture hot path (PiXL Edge)', '{"icon":"bolt"}', 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (3, 1, 'forge', 'Forge', 'system', 'Background Windows Service for enrichment pipeline and data processing', '{"icon":"hammer"}', 2);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (4, 1, 'sentinel', 'Sentinel', 'system', 'Operations dashboard and documentation portal', '{"icon":"shield"}', 3);

-- ── Edge Probes (4 probes, direct children of Edge) ────────────────────────
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (10, 2, 'edge.http-listener', 'HTTP Listener', 'probe',
    'Kestrel responding — always healthy if edge process is running',
    '{"source":"edge","sourceProbeName":"HTTP Listener","healthFunction":"Always 1 if process is up"}', 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (11, 2, 'edge.capture-pipeline', 'Capture Pipeline', 'probe',
    'Requests to TrackingData conversion with error rate monitoring',
    '{"source":"edge","sourceProbeName":"Capture Pipeline","healthFunction":"Error rate < 5%, grace period 30s after startup"}', 2);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (12, 2, 'edge.pipe-client', 'Pipe Client', 'probe',
    'Named pipe connection to Forge',
    '{"source":"edge","sourceProbeName":"Pipe Client","healthFunction":"Pipe connected to Forge"}', 3);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (13, 2, 'edge.jsonl-failover', 'JSONL Failover', 'probe',
    'Disk-based failover when pipe is unavailable',
    '{"source":"edge","sourceProbeName":"JSONL Failover","healthFunction":"No write errors (emergency writes OK)"}', 4);

-- ── Forge Subsystems ────────────────────────────────────────────────────────

-- F1: Ingest
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (20, 3, 'forge.f1-ingest', 'F1: Ingest', 'subsystem', 'Named pipe server and enrichment channel intake', NULL, 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (21, 20, 'forge.f1-ingest.pipe-listener', 'Pipe Listener', 'probe',
    'Named pipe server accepting connections from Edge',
    '{"source":"forge","sourceSubsystem":"F1: Ingest","sourceProbeName":"Pipe Listener","healthFunction":"Accepting connections"}', 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (22, 20, 'forge.f1-ingest.enrichment-channel', 'Enrichment Channel', 'probe',
    'Channel<T> between pipe listener and enrichment workers',
    '{"source":"forge","sourceSubsystem":"F1: Ingest","sourceProbeName":"Enrichment Channel","healthFunction":"Not full, consumers alive"}', 2);

-- F2: Enrichment Engine
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (30, 3, 'forge.f2-enrichment', 'F2: Enrichment Engine', 'subsystem', 'Adaptive worker pool with 16 enrichment services', NULL, 2);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (31, 30, 'forge.f2-enrichment.worker-pool', 'Worker Pool', 'probe',
    'Adaptive enrichment workers processing records',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"Worker Pool","healthFunction":"Workers alive, processing"}', 1);

-- Stateful probes (cache/state that can degrade)
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (32, 30, 'forge.f2-enrichment.ua-parsing', 'UaParsing', 'probe',
    'User-agent parsing with bounded cache',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"UaParsing","healthFunction":"BoundedCache <= 50K, parse succeeding"}', 2);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (33, 30, 'forge.f2-enrichment.bot-ua-detection', 'BotUaDetection', 'probe',
    'Bot and crawler detection with bounded cache',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"BotUaDetection","healthFunction":"BoundedCache <= 50K, detection succeeding"}', 3);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (34, 30, 'forge.f2-enrichment.dns-lookup', 'DnsLookup', 'probe',
    'Reverse DNS lookup cache',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"DnsLookup","healthFunction":"DNS servers reachable, cache healthy"}', 4);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (35, 30, 'forge.f2-enrichment.whois-asn', 'WhoisAsn', 'probe',
    'WHOIS ASN lookup cache',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"WhoisAsn","healthFunction":"WHOIS servers reachable, cache healthy"}', 5);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (36, 30, 'forge.f2-enrichment.maxmind-geo', 'MaxMindGeo', 'probe',
    'MaxMind GeoIP2 database lookups',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"MaxMindGeo","healthFunction":".mmdb loaded, cache <= 200K"}', 6);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (37, 30, 'forge.f2-enrichment.dead-internet', 'DeadInternet', 'probe',
    'Dead Internet Theory detection patterns',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"DeadInternet","healthFunction":"Memory bounded, eviction running"}', 7);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (38, 30, 'forge.f2-enrichment.behavioral-replay', 'BehavioralReplay', 'probe',
    'Behavioral replay detection for automated browsing patterns',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"BehavioralReplay","healthFunction":"Memory bounded, eviction running"}', 8);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (39, 30, 'forge.f2-enrichment.cross-customer-intel', 'CrossCustomerIntel', 'probe',
    'Cross-customer intelligence tracking',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"CrossCustomerIntel","healthFunction":"Memory bounded, tracker count stable"}', 9);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (40, 30, 'forge.f2-enrichment.session-stitching', 'SessionStitching', 'probe',
    'Session stitching across page views',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"SessionStitching","healthFunction":"Memory bounded, sessions evicting"}', 10);

-- Stateless probes (always healthy)
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (41, 30, 'forge.f2-enrichment.ip-classification', 'IpClassification', 'probe',
    'IP classification (datacenter, residential, mobile, etc.)',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"IpClassification","healthFunction":"Stateless — always 1"}', 11);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (42, 30, 'forge.f2-enrichment.contradiction-matrix', 'ContradictionMatrix', 'probe',
    'Signal contradiction detection (conflicting device/network signals)',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"ContradictionMatrix","healthFunction":"Stateless — always 1"}', 12);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (43, 30, 'forge.f2-enrichment.device-affluence', 'DeviceAffluence', 'probe',
    'Device affluence scoring from hardware signals',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"DeviceAffluence","healthFunction":"Stateless — always 1"}', 13);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (44, 30, 'forge.f2-enrichment.device-age-estimation', 'DeviceAgeEstimation', 'probe',
    'Device age estimation from browser and OS versions',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"DeviceAgeEstimation","healthFunction":"Stateless — always 1"}', 14);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (45, 30, 'forge.f2-enrichment.geographic-arbitrage', 'GeographicArbitrage', 'probe',
    'Geographic arbitrage detection (VPN/proxy indicators)',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"GeographicArbitrage","healthFunction":"Stateless — always 1"}', 15);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (46, 30, 'forge.f2-enrichment.gpu-tier-reference', 'GpuTierReference', 'probe',
    'GPU tier classification from WebGL renderer string',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"GpuTierReference","healthFunction":"Stateless — always 1"}', 16);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (47, 30, 'forge.f2-enrichment.lead-quality-scoring', 'LeadQualityScoring', 'probe',
    'Lead quality composite scoring',
    '{"source":"forge","sourceSubsystem":"F2: Enrichment Engine","sourceProbeName":"LeadQualityScoring","healthFunction":"Stateless — always 1"}', 17);

-- F3: SQL Writer
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (50, 3, 'forge.f3-sql-writer', 'F3: SQL Writer', 'subsystem', 'SqlBulkCopy from enrichment channel to PiXL.Parsed', NULL, 3);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (51, 50, 'forge.f3-sql-writer.bulk-copy', 'BulkCopy', 'probe',
    'SqlBulkCopy batches writing to PiXL.Parsed',
    '{"source":"forge","sourceSubsystem":"F3: SQL Writer","sourceProbeName":"BulkCopy","healthFunction":"Error rate < 5%"}', 1);

-- F4: Failover & Replay
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (55, 3, 'forge.f4-failover', 'F4: Failover & Replay', 'subsystem', 'Forge-side failover persistence and replay', NULL, 4);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (56, 55, 'forge.f4-failover.failover-writer', 'Failover Writer', 'probe',
    'Enriched JSONL failover writer for SQL circuit breaker events',
    '{"source":"forge","sourceSubsystem":"F4: Failover & Replay","sourceProbeName":"Failover Writer","healthFunction":"Disk writable (always 1 unless disk fails)"}', 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (57, 55, 'forge.f4-failover.replay-service', 'Replay Service', 'probe',
    'Unified replay for Edge failover + Forge failover + dead-letter',
    '{"source":"forge","sourceSubsystem":"F4: Failover & Replay","sourceProbeName":"Replay Service","healthFunction":"No stuck files > 1h old"}', 2);

-- F5: ETL Pipeline
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (60, 3, 'forge.f5-etl', 'F5: ETL Pipeline', 'subsystem', 'Identity resolution ETL (MatchVisits every 60s)', NULL, 5);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (61, 60, 'forge.f5-etl.match-visits', 'MatchVisits', 'probe',
    'usp_MatchVisits identity resolution stored procedure',
    '{"source":"forge","sourceSubsystem":"F5: ETL Pipeline","sourceProbeName":"MatchVisits","healthFunction":"Last run < 2min ago"}', 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (62, 60, 'forge.f5-etl.match-legacy-visits', 'MatchLegacyVisits', 'probe',
    'usp_MatchLegacyVisits identity resolution for legacy records',
    '{"source":"forge","sourceSubsystem":"F5: ETL Pipeline","sourceProbeName":"MatchLegacyVisits","healthFunction":"Last run < 2min ago"}', 2);

-- F6: Background IP
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (65, 3, 'forge.f6-background-ip', 'F6: Background IP', 'subsystem', 'Off-hot-path DNS and WHOIS enrichment workers', NULL, 6);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (66, 65, 'forge.f6-background-ip.dns-enrichment', 'DNS Enrichment', 'probe',
    'Background DNS reverse lookup workers',
    '{"source":"forge","sourceSubsystem":"F6: Background IP","sourceProbeName":"DNS Enrichment","healthFunction":"Lookups processing"}', 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (67, 65, 'forge.f6-background-ip.whois-enrichment', 'WHOIS Enrichment', 'probe',
    'Background WHOIS ASN lookup workers',
    '{"source":"forge","sourceSubsystem":"F6: Background IP","sourceProbeName":"WHOIS Enrichment","healthFunction":"Lookups processing"}', 2);

-- F7: Data Sync
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (70, 3, 'forge.f7-data-sync', 'F7: Data Sync', 'subsystem', 'Company/PiXL sync and IP data acquisition', NULL, 7);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (71, 70, 'forge.f7-data-sync.company-pixel-sync', 'Company/Pixel Sync', 'probe',
    'Xavier → PiXL.Company/PiXL.Settings sync every 6h',
    '{"source":"forge","sourceSubsystem":"F7: Data Sync","sourceProbeName":"Company/Pixel Sync","healthFunction":"Last sync < 7h, 0 failures"}', 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (72, 70, 'forge.f7-data-sync.ip-data-acquisition', 'IP Data Acquisition', 'component', 'Daily acquisition of public IP data (IPtoASN, DB-IP Lite)', NULL, 2);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (73, 72, 'forge.f7-data-sync.ip-data-acquisition.iptoasn', 'IPtoASN', 'probe',
    'IPtoASN daily import into IPInfo schema',
    '{"source":"forge","sourceSubsystem":"F7: Data Sync","sourceProbeName":"IPtoASN","healthFunction":"Last import < 26h"}', 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (74, 72, 'forge.f7-data-sync.ip-data-acquisition.dbip', 'DB-IP', 'probe',
    'DB-IP Lite monthly import into IPInfo schema',
    '{"source":"forge","sourceSubsystem":"F7: Data Sync","sourceProbeName":"DB-IP","healthFunction":"Last import < 26h"}', 2);

-- ── Sentinel Probes ─────────────────────────────────────────────────────────
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (80, 4, 'sentinel.sql-connectivity', 'SQL Connectivity', 'probe',
    'SQL Server connection test and basic queries',
    '{"source":"sentinel","healthFunction":"Connection opens, test query succeeds"}', 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (81, 4, 'sentinel.windows-services', 'Windows Services', 'probe',
    'Critical Windows services running (SQL Server, IIS, Forge, Sentinel)',
    '{"source":"sentinel","healthFunction":"All critical services in Running state"}', 2);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (82, 4, 'sentinel.iis-reachability', 'IIS Reachability', 'probe',
    'IIS Edge site responds to HTTP probe',
    '{"source":"sentinel","healthFunction":"HTTP HEAD returns 2xx/3xx"}', 3);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder)
VALUES (83, 4, 'sentinel.self', 'Self', 'probe',
    'Sentinel process health (always 1 if responding)',
    '{"source":"sentinel","healthFunction":"Always 1 if process is up"}', 4);

SET IDENTITY_INSERT Health.Node OFF;
GO

-- ============================================================================
-- VERIFICATION
-- ============================================================================
SELECT NodeType, COUNT(*) AS Cnt FROM Health.Node GROUP BY NodeType ORDER BY
    CASE NodeType WHEN 'platform' THEN 1 WHEN 'system' THEN 2 WHEN 'subsystem' THEN 3 WHEN 'component' THEN 4 WHEN 'probe' THEN 5 END;

SELECT n.Slug, n.Name, n.NodeType, p.Slug AS ParentSlug
FROM Health.Node n
LEFT JOIN Health.Node p ON p.NodeId = n.ParentId
ORDER BY n.NodeId;
GO
