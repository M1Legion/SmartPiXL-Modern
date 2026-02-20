-- =====================================================================
-- Migration 58: TrafficAlert Schema — Visitor Scoring + Customer Summary
-- Phase 9 of SmartPiXL workplan
--
-- Creates:
--   TrafficAlert schema
--   TrafficAlert.VisitorScore   — per-visit composite quality scores
--   TrafficAlert.CustomerSummary — per-customer per-period aggregates
--   Indexes for dashboard queries
--
-- Design doc reference: §7 (TrafficAlert Subsystem)
-- =====================================================================
SET QUOTED_IDENTIFIER ON;
GO
USE SmartPiXL;
GO

PRINT '--- 58: TrafficAlert Schema ---';
GO

-- =====================================================================
-- Step 1: Create TrafficAlert schema
-- =====================================================================
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'TrafficAlert')
BEGIN
    EXEC('CREATE SCHEMA TrafficAlert');
    PRINT '  Created TrafficAlert schema';
END
ELSE
    PRINT '  TrafficAlert schema already exists — skipped';
GO

-- =====================================================================
-- Step 2: TrafficAlert.VisitorScore
-- Per-visit composite scoring table. One row per visit.
-- Populated by ETL.usp_MaterializeVisitorScores after ParseNewHits.
-- =====================================================================
IF OBJECT_ID('TrafficAlert.VisitorScore', 'U') IS NULL
BEGIN
    CREATE TABLE TrafficAlert.VisitorScore
    (
        VisitorScoreId      BIGINT          NOT NULL IDENTITY(1,1),
        VisitId             BIGINT          NOT NULL,
        DeviceId            BIGINT          NOT NULL,
        CompanyID           INT             NOT NULL,

        -- From PiXL Script (client-side)
        BotScore            INT             NULL,
        AnomalyScore        INT             NULL,
        CombinedThreatScore INT             NULL,

        -- From Forge enrichments
        LeadQualityScore    INT             NULL,
        AffluenceSignal     VARCHAR(4)      NULL,       -- LOW|MID|HIGH
        CulturalConsistency INT             NULL,       -- 0-100
        ContradictionCount  INT             NULL,

        -- Derived composite scores (computed by materialization proc)
        SessionQuality      INT             NULL,       -- 0-100
        MouseAuthenticity   INT             NULL,       -- 0-100
        CompositeQuality    INT             NULL,       -- 0-100 (master score)

        -- Metadata
        ReceivedAt          DATETIME2(3)    NOT NULL,
        MaterializedAt      DATETIME2(3)    NOT NULL DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_TrafficAlert_VisitorScore PRIMARY KEY CLUSTERED (VisitorScoreId),
        CONSTRAINT FK_VisitorScore_Visit FOREIGN KEY (VisitId) REFERENCES PiXL.Visit(VisitID),
        CONSTRAINT FK_VisitorScore_Device FOREIGN KEY (DeviceId) REFERENCES PiXL.Device(DeviceId)
    );

    PRINT '  Created TrafficAlert.VisitorScore';
END
ELSE
    PRINT '  TrafficAlert.VisitorScore already exists — skipped';
GO

-- =====================================================================
-- Step 3: VisitorScore indexes
-- =====================================================================
-- Fast lookup by VisitId (1:1 relationship)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('TrafficAlert.VisitorScore') AND name = 'UQ_VisitorScore_VisitId')
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UQ_VisitorScore_VisitId
        ON TrafficAlert.VisitorScore (VisitId);
    PRINT '  Created unique index UQ_VisitorScore_VisitId';
END
GO

-- Company + ReceivedAt for dashboard date-range queries
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('TrafficAlert.VisitorScore') AND name = 'IX_VisitorScore_Company_ReceivedAt')
BEGIN
    CREATE NONCLUSTERED INDEX IX_VisitorScore_Company_ReceivedAt
        ON TrafficAlert.VisitorScore (CompanyID, ReceivedAt)
        INCLUDE (BotScore, CompositeQuality, LeadQualityScore, MouseAuthenticity);
    PRINT '  Created index IX_VisitorScore_Company_ReceivedAt';
END
GO

-- High-threat filter (CompositeQuality < 30)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('TrafficAlert.VisitorScore') AND name = 'IX_VisitorScore_LowQuality')
BEGIN
    CREATE NONCLUSTERED INDEX IX_VisitorScore_LowQuality
        ON TrafficAlert.VisitorScore (CompanyID, CompositeQuality)
        INCLUDE (BotScore, ContradictionCount, MouseAuthenticity, ReceivedAt)
        WHERE CompositeQuality < 30;
    PRINT '  Created filtered index IX_VisitorScore_LowQuality (CompositeQuality < 30)';
END
GO

-- DeviceId lookup for device-level scoring history
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('TrafficAlert.VisitorScore') AND name = 'IX_VisitorScore_DeviceId')
BEGIN
    CREATE NONCLUSTERED INDEX IX_VisitorScore_DeviceId
        ON TrafficAlert.VisitorScore (DeviceId)
        INCLUDE (CompanyID, CompositeQuality, ReceivedAt);
    PRINT '  Created index IX_VisitorScore_DeviceId';
END
GO

-- =====================================================================
-- Step 4: TrafficAlert.CustomerSummary
-- Per-customer per-period aggregate table.
-- Populated by ETL.usp_MaterializeCustomerSummary (daily batch).
-- =====================================================================
IF OBJECT_ID('TrafficAlert.CustomerSummary', 'U') IS NULL
BEGIN
    CREATE TABLE TrafficAlert.CustomerSummary
    (
        CustomerSummaryId       BIGINT          NOT NULL IDENTITY(1,1),
        CompanyID               INT             NOT NULL,
        PeriodStart             DATE            NOT NULL,
        PeriodType              CHAR(1)         NOT NULL,   -- D=daily, W=weekly, M=monthly

        -- Volume metrics
        TotalHits               INT             NOT NULL DEFAULT 0,
        BotHits                 INT             NOT NULL DEFAULT 0,
        HumanHits               INT             NOT NULL DEFAULT 0,
        UnknownHits             INT             NOT NULL DEFAULT 0,

        -- Quality metrics
        BotPercent              DECIMAL(5,2)    NULL,
        AvgBotScore             DECIMAL(5,2)    NULL,
        AvgLeadQuality          DECIMAL(5,2)    NULL,
        AvgCompositeQuality     DECIMAL(5,2)    NULL,
        AvgMouseAuthenticity    DECIMAL(5,2)    NULL,
        AvgSessionQuality       DECIMAL(5,2)    NULL,

        -- Engagement metrics
        UniqueDevices           INT             NOT NULL DEFAULT 0,
        UniqueIPs               INT             NOT NULL DEFAULT 0,
        MatchedVisitors         INT             NOT NULL DEFAULT 0,   -- matched via PiXL.Match
        CrossCustomerPollutionRate DECIMAL(5,2) NULL,

        -- Session metrics
        AvgSessionDepth         DECIMAL(5,2)    NULL,       -- avg pages per session
        AvgSessionDuration      INT             NULL,       -- avg seconds per session

        -- Dead Internet Index for this period
        DeadInternetIndex       INT             NULL,       -- 0-100

        -- Metadata
        MaterializedAt          DATETIME2(3)    NOT NULL DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_TrafficAlert_CustomerSummary PRIMARY KEY CLUSTERED (CustomerSummaryId),
        CONSTRAINT UQ_CustomerSummary_Key UNIQUE (CompanyID, PeriodStart, PeriodType)
    );

    PRINT '  Created TrafficAlert.CustomerSummary';
END
ELSE
    PRINT '  TrafficAlert.CustomerSummary already exists — skipped';
GO

-- =====================================================================
-- Step 5: CustomerSummary indexes
-- =====================================================================
-- Period-type + date range for trend queries
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('TrafficAlert.CustomerSummary') AND name = 'IX_CustomerSummary_Period')
BEGIN
    CREATE NONCLUSTERED INDEX IX_CustomerSummary_Period
        ON TrafficAlert.CustomerSummary (PeriodType, PeriodStart DESC)
        INCLUDE (CompanyID, TotalHits, BotPercent, AvgCompositeQuality, DeadInternetIndex);
    PRINT '  Created index IX_CustomerSummary_Period';
END
GO

-- =====================================================================
-- Step 6: Watermark entries for the new ETL procs
-- =====================================================================
IF NOT EXISTS (SELECT 1 FROM ETL.Watermark WHERE ProcessName = 'MaterializeVisitorScores')
BEGIN
    INSERT INTO ETL.Watermark (ProcessName, LastProcessedId, RowsProcessed)
    VALUES ('MaterializeVisitorScores', 0, 0);
    PRINT '  Added watermark: MaterializeVisitorScores';
END
GO

IF NOT EXISTS (SELECT 1 FROM ETL.Watermark WHERE ProcessName = 'MaterializeCustomerSummary')
BEGIN
    INSERT INTO ETL.Watermark (ProcessName, LastProcessedId, RowsProcessed)
    VALUES ('MaterializeCustomerSummary', 0, 0);
    PRINT '  Added watermark: MaterializeCustomerSummary';
END
GO

-- =====================================================================
-- Step 7: Verification
-- =====================================================================
IF OBJECT_ID('TrafficAlert.VisitorScore', 'U') IS NOT NULL
    PRINT '  OK: TrafficAlert.VisitorScore exists';
ELSE
    PRINT '  ERROR: TrafficAlert.VisitorScore missing!';

IF OBJECT_ID('TrafficAlert.CustomerSummary', 'U') IS NOT NULL
    PRINT '  OK: TrafficAlert.CustomerSummary exists';
ELSE
    PRINT '  ERROR: TrafficAlert.CustomerSummary missing!';

IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'TrafficAlert')
    PRINT '  OK: TrafficAlert schema exists';
ELSE
    PRINT '  ERROR: TrafficAlert schema missing!';
GO

PRINT '--- 58: TrafficAlert schema complete ---';
GO
