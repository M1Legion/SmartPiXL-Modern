/*
    29_AtlasDocsSchema.sql
    ─────────────────────────────────────────────────────────
    Creates the Docs schema and tables for the Atlas documentation portal.
    
    Atlas serves 4 audience tiers from SQL-backed content:
        Tier 1  PitchHtml        – Investors / clients
        Tier 2  ManagementHtml   – Leadership / partners  
        Tier 3  TechnicalHtml    – Developers
        Tier 4  WalkthroughHtml  – Founder / operational catch-up
    
    Server: localhost\SQL2025
    Database: SmartPiXL
    Date: 2026-02-16
*/

SET NOCOUNT ON;

-- ─── Schema ─────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'Docs')
    EXEC('CREATE SCHEMA Docs');
GO

-- ─── Docs.Section ───────────────────────────────────────
-- Hierarchical content tree. Each row is one section of the Atlas portal.
-- ParentSectionId = NULL means top-level section.
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('Docs.Section') AND type = 'U')
BEGIN
    CREATE TABLE Docs.Section
    (
        SectionId         int           IDENTITY(1,1) NOT NULL,
        ParentSectionId   int           NULL,
        Slug              varchar(100)  NOT NULL,
        Title             varchar(200)  NOT NULL,
        IconClass         varchar(50)   NULL,
        SortOrder         int           NOT NULL DEFAULT 0,
        
        -- Tier 1: Investors, sales, non-technical clients
        PitchHtml         nvarchar(max) NULL,
        
        -- Tier 2: Internal leadership, technical managers, partners
        ManagementHtml    nvarchar(max) NULL,
        
        -- Tier 3: Developers joining the team
        TechnicalHtml     nvarchar(max) NULL,
        
        -- Tier 4: Founder / operational catch-up, chronological narrative
        WalkthroughHtml   nvarchar(max) NULL,
        
        -- Primary Mermaid diagram definition (rendered client-side by Mermaid.js)
        MermaidDiagram    nvarchar(max) NULL,
        
        LastUpdated       datetime2     NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedBy         varchar(100)  NOT NULL DEFAULT 'system',
        
        CONSTRAINT PK_Docs_Section PRIMARY KEY CLUSTERED (SectionId),
        CONSTRAINT FK_Docs_Section_Parent FOREIGN KEY (ParentSectionId) REFERENCES Docs.Section(SectionId),
        CONSTRAINT UQ_Docs_Section_Slug UNIQUE (Slug)
    );
END;
GO

-- ─── Docs.SystemStatus ─────────────────────────────────
-- Source of truth for what's built vs planned.
-- Maps to roadmap phases and real systems/features.
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('Docs.SystemStatus') AND type = 'U')
BEGIN
    CREATE TABLE Docs.SystemStatus
    (
        SystemId          int           IDENTITY(1,1) NOT NULL,
        SystemName        varchar(100)  NOT NULL,
        Phase             varchar(50)   NULL,
        Status            varchar(20)   NOT NULL DEFAULT 'Planned',  -- Planned | InProgress | Complete | Live
        SectionId         int           NULL,
        LastVerified      datetime2     NOT NULL DEFAULT SYSUTCDATETIME(),
        VerifiedBy        varchar(100)  NOT NULL DEFAULT 'system',
        Notes             nvarchar(500) NULL,
        
        CONSTRAINT PK_Docs_SystemStatus PRIMARY KEY CLUSTERED (SystemId),
        CONSTRAINT FK_Docs_SystemStatus_Section FOREIGN KEY (SectionId) REFERENCES Docs.Section(SectionId),
        CONSTRAINT UQ_Docs_SystemStatus_Name UNIQUE (SystemName),
        CONSTRAINT CK_Docs_SystemStatus_Status CHECK (Status IN ('Planned', 'InProgress', 'Complete', 'Live'))
    );
END;
GO

-- ─── Docs.Metric ───────────────────────────────────────
-- Display metrics on the portal. Some are static text, some are live SQL queries.
-- QuerySql must return a single scalar value. Executed at page load.
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('Docs.Metric') AND type = 'U')
BEGIN
    CREATE TABLE Docs.Metric
    (
        MetricId          int           IDENTITY(1,1) NOT NULL,
        SectionId         int           NULL,         -- NULL = global/hero metric
        Label             varchar(100)  NOT NULL,
        StaticValue       varchar(200)  NULL,         -- Used if QuerySql is NULL
        QuerySql          nvarchar(max) NULL,         -- SELECT returning one scalar
        FormatHint        varchar(20)   NOT NULL DEFAULT 'text',  -- number | percentage | duration | text
        SortOrder         int           NOT NULL DEFAULT 0,
        
        CONSTRAINT PK_Docs_Metric PRIMARY KEY CLUSTERED (MetricId),
        CONSTRAINT FK_Docs_Metric_Section FOREIGN KEY (SectionId) REFERENCES Docs.Section(SectionId)
    );
END;
GO

-- ─── Seed: Top-Level Sections ───────────────────────────
-- These are the major sections of the Atlas portal.
-- Content will be populated by the Atlas agent.

IF NOT EXISTS (SELECT 1 FROM Docs.Section WHERE Slug = 'overview')
BEGIN
    SET IDENTITY_INSERT Docs.Section ON;

    INSERT INTO Docs.Section (SectionId, ParentSectionId, Slug, Title, IconClass, SortOrder, PitchHtml, UpdatedBy)
    VALUES
        (1, NULL, 'overview',           'SmartPiXL Overview',           'globe',        1,
         N'<p>SmartPiXL identifies who visits your website, even without cookies or logins. Our pixel collects 100+ signals from each visit and matches them against the largest US consumer database to deliver real names, emails, and addresses in real time.</p>',
         'agent:atlas'),
        
        (2, NULL, 'data-collection',    'Data Collection',              'fingerprint',  2,
         N'<p>A lightweight JavaScript tag collects over 100 unique signals from every browser visit, canvas rendering, WebGL capabilities, audio fingerprints, installed fonts, and dozens more, all without storing anything on the visitor''s device.</p>',
         'agent:atlas'),
        
        (3, NULL, 'ingestion-pipeline', 'Ingestion Pipeline',          'bolt',         3,
         N'<p>Every pixel hit is captured, enriched with IP intelligence and geolocation, and written to the database in under 10 milliseconds. Our zero-allocation architecture handles thousands of concurrent requests without breaking a sweat.</p>',
         'agent:atlas'),
        
        (4, NULL, 'etl-processing',     'ETL & Data Warehousing',       'database',     4,
         N'<p>Raw tracking data is parsed, normalized, and warehoused every 60 seconds. A star-schema design with device, IP, and visit dimensions enables instant analytics across 175 enriched columns.</p>',
         'agent:atlas'),
        
        (5, NULL, 'identity-resolution','Identity Resolution',          'user-check',   5,
         N'<p>SmartPiXL matches anonymous visitors against a 275-million-record consumer database using multi-strategy identity resolution, IP matching, geographic proximity, cookie correlation, and unique identifiers, to turn anonymous traffic into actionable leads.</p>',
         'agent:atlas'),
        
        (6, NULL, 'bot-detection',      'Bot Detection & Evasion',      'shield',       6,
         N'<p>Sophisticated bot detection goes beyond user-agent strings. We analyze fingerprint stability, canvas noise patterns, behavioral signals, datacenter IP ranges, and timing anomalies to separate real visitors from automated traffic.</p>',
         'agent:atlas'),
        
        (7, NULL, 'ip-geolocation',     'IP Geolocation',              'map-pin',      7,
         N'<p>Real-time IP geolocation powered by a 342-million-row database with sub-second lookups. Two-tier caching ensures zero latency for repeat visitors while staying current with daily incremental syncs.</p>',
         'agent:atlas'),
        
        (8, NULL, 'dashboard',          'Real-Time Dashboard',          'monitor',      8,
         N'<p>A live operational dashboard provides instant visibility into traffic patterns, bot detection rates, pipeline health, and system performance, updated every 10 seconds with zero page reloads.</p>',
         'agent:atlas'),
        
        (9, NULL, 'ai-tooling',         'AI Agent Ecosystem',           'cpu',          9,
         N'<p>SmartPiXL is built and maintained by a coordinated team of 18 specialized AI agents, each with defined responsibilities, tool access, and collaboration rules. Custom instructions, reusable prompts, and cross-agent sync protocols ensure consistent, high-quality output across every part of the system.</p>',
         'agent:atlas');

    SET IDENTITY_INSERT Docs.Section OFF;
END;
GO

-- ─── Seed: System Statuses ──────────────────────────────
-- Mirror of ROADMAP.md, this is the canonical source of truth going forward.

IF NOT EXISTS (SELECT 1 FROM Docs.SystemStatus WHERE SystemName = '.NET 10 Minimal API Server')
BEGIN
    INSERT INTO Docs.SystemStatus (SystemName, Phase, Status, SectionId, VerifiedBy, Notes)
    VALUES
        ('.NET 10 Minimal API Server',     'Phase 1: Core Server',                 'Live',       1, 'agent:atlas', 'Program.cs, Minimal APIs, Kestrel'),
        ('PiXLScript Fingerprinting',      'Phase 1: Core Server',                 'Live',       2, 'agent:atlas', '100+ JS signals, IIFE, Image() pixel fire'),
        ('DatabaseWriterService',          'Phase 1: Core Server',                 'Live',       3, 'agent:atlas', 'Channel<T> → SqlBulkCopy into PiXL.Test'),
        ('TrackingCaptureService',         'Phase 1: Core Server',                 'Live',       3, 'agent:atlas', 'HTTP request → TrackingData parser'),
        ('FileTrackingLogger',             'Phase 1: Core Server',                 'Live',       NULL, 'agent:atlas', 'Async daily rolling log files'),
        ('Code Quality Pass',              'Phase 2: Code Quality',                'Complete',   NULL, 'agent:atlas', 'StringBuilder, compiled regex, zero-alloc'),
        ('IpClassificationService',        'Phase 3: IP & Fingerprint Enrichment', 'Live',       6, 'agent:atlas', 'Datacenter / residential / reserved IP classification'),
        ('DatacenterIpService',            'Phase 3: IP & Fingerprint Enrichment', 'Live',       6, 'agent:atlas', 'AWS/GCP IP range downloader, background refresh'),
        ('IpBehaviorService',              'Phase 3: IP & Fingerprint Enrichment', 'Live',       6, 'agent:atlas', 'Subnet /24 velocity & rapid-fire timing'),
        ('FingerprintStabilityService',    'Phase 3: IP & Fingerprint Enrichment', 'Live',       6, 'agent:atlas', 'Per-IP fingerprint variation scoring'),
        ('Star-Schema Database',           'Phase 4: Database Schema & ETL',       'Live',       4, 'agent:atlas', 'PiXL.Device, PiXL.IP, PiXL.Visit, PiXL.Match'),
        ('PiXL.Parsed Warehouse',          'Phase 4: Database Schema & ETL',       'Live',       4, 'agent:atlas', '~175 columns materialized from raw hits'),
        ('ETL.usp_ParseNewHits',           'Phase 4: Database Schema & ETL',       'Live',       4, 'agent:atlas', 'Watermark-based incremental parse'),
        ('ETL.usp_MatchVisits',            'Phase 4: Database Schema & ETL',       'Live',       5, 'agent:atlas', 'Identity resolution vs AutoConsumer (275M+ unique consumers)'),
        ('EtlBackgroundService',           'Phase 4: Database Schema & ETL',       'Live',       4, 'agent:atlas', 'Runs both ETL procs every 60s'),
        ('Tron Dashboard',                 'Phase 5: Dashboard',                   'Live',       8, 'agent:atlas', '11 API endpoints, 10 SQL views, Three.js 3D'),
        ('InfraHealthService',             'Phase 5: Dashboard',                   'Live',       8, 'agent:atlas', 'Windows services, SQL health, IIS, data flow probes'),
        ('Evasion Countermeasures',        'Phase 6: Evasion Countermeasures',     'Live',       6, 'agent:atlas', '10 countermeasures: canvas noise, audio, behavioral, stealth'),
        ('Canvas Fingerprinting',          'Phase 6: Evasion Countermeasures',     'Live',       2, 'agent:atlas', 'Multi-canvas cross-validation for noise detection'),
        ('GeoCacheService',                'Phase 7: IP Geolocation',              'Live',       7, 'agent:atlas', 'Two-tier cache: ConcurrentDict + MemoryCache, 342M-row IPAPI.IP'),
        ('IpApiSyncService',               'Phase 7: IP Geolocation',              'Live',       7, 'agent:atlas', 'Daily watermark-based incremental sync from Xavier'),
        ('Legacy PiXL Support',            'Phase 8: Legacy Support',              'InProgress', NULL, 'agent:atlas', 'P0, Accept legacy _SMART.GIF hits from Xavier'),
        ('CompanyPixelSyncService',        'Phase 9: Company/Pixel Sync',          'InProgress', NULL, 'agent:atlas', 'P0, Mirror Company/PiXL config from Xavier'),
        ('Legacy Match Process',           'Backlog',                              'Planned',    NULL, 'agent:atlas', 'P1, IP + UA match for legacy hits'),
        ('BotDetectionService',            'Backlog',                              'Planned',    6, 'agent:atlas', 'P2, Consolidated bot scoring from all signals'),
        ('TLS Fingerprinting',             'Backlog',                              'Planned',    NULL, 'agent:atlas', 'P3, JA3/JA4 via reverse proxy'),
        ('White-Label Support',            'Backlog',                              'Planned',    NULL, 'agent:atlas', 'P3, Custom domains, branded dashboards'),
        ('AI Agent Ecosystem',             'Phase 10: AI Tooling',                 'Live',       9, 'agent:atlas', '18 agents, 5 instructions, 4 prompts, cross-agent sync'),
        ('Atlas Documentation Portal',     'Phase 10: AI Tooling',                 'Live',       9, 'agent:atlas', 'SQL-backed 4-tier progressive disclosure portal'),
        ('Cross-Agent Sync Protocol',      'Phase 10: AI Tooling',                 'Live',       9, 'agent:atlas', 'atlas-sync.instructions.md, auto-update docs on feature completion'),
        ('RAG Presentation Assistant',      'Backlog',                              'Planned',    9, 'agent:atlas', 'P3, Local LLM on RTX 4090, RAG over codebase + Atlas + live metrics for demo co-pilot'),
        ('MSSQL 2025 Vector Analytics',     'Backlog',                              'Planned',    NULL, 'agent:atlas', 'P3, Native VECTOR type for embedding tracking data, similarity search, anomaly detection');
END;
GO

-- ─── Seed: Global Metrics ───────────────────────────────
-- Hero metrics shown at the top of the Atlas portal.
-- QuerySql returns a single scalar. NULL QuerySql = use StaticValue.

IF NOT EXISTS (SELECT 1 FROM Docs.Metric WHERE Label = 'Fingerprint Signals')
BEGIN
    INSERT INTO Docs.Metric (SectionId, Label, StaticValue, QuerySql, FormatHint, SortOrder)
    VALUES
        -- Global hero metrics
        (NULL, 'Fingerprint Signals',    '100+',  NULL, 'text', 1),
        (NULL, 'Consumer Database Size', '275M+', NULL, 'text', 2),
        (NULL, 'Total Hits Captured',     NULL,
         N'SELECT FORMAT(COUNT(*), ''N0'') FROM PiXL.Parsed WITH (NOLOCK)',
         'number', 3),
        (NULL, 'Parsed Columns',         '175',   NULL, 'text', 4),
        (NULL, 'ETL Cadence',            '60s',   NULL, 'text', 5),
        
        -- Section-specific metrics
        (3, 'Hits Today', NULL,
         N'SELECT FORMAT(COUNT(*), ''N0'') FROM PiXL.Parsed WITH (NOLOCK) WHERE ReceivedAt >= CAST(GETDATE() AS date)',
         'number', 1),
        (4, 'Last ETL Run', NULL,
         N'SELECT FORMAT(LastRunAt, ''HH:mm:ss'') FROM ETL.Watermark WHERE ProcessName = ''ParseNewHits''',
         'text', 1),
        (4, 'ETL Watermark', NULL,
         N'SELECT FORMAT(LastProcessedId, ''N0'') FROM ETL.Watermark WHERE ProcessName = ''ParseNewHits''',
         'number', 2),
        (5, 'Matched Visitors', NULL,
         N'SELECT FORMAT(COUNT(*), ''N0'') FROM PiXL.Match WITH (NOLOCK)',
         'number', 1),
        (6, 'Unique Devices', NULL,
         N'SELECT FORMAT(COUNT(*), ''N0'') FROM PiXL.Device WITH (NOLOCK)',
         'number', 1),
        (7, 'Geo IP Records', NULL,
         N'SELECT FORMAT(COUNT(*), ''N0'') FROM IPAPI.IP WITH (NOLOCK)',
         'number', 1),
        
        -- AI Tooling metrics
        (9, 'Custom Agents',        '18',   NULL, 'number', 1),
        (9, 'Instruction Files',    '5',    NULL, 'number', 2),
        (9, 'Reusable Prompts',     '4',    NULL, 'number', 3);
END;
GO

PRINT 'Atlas Docs schema created and seeded successfully.';
GO
