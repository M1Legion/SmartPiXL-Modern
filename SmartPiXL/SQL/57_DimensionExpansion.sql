-- ============================================================================
-- Migration 57: Dimension Table Expansion (Phase 8)
-- ============================================================================
-- Expands the normalized dimension tables with enrichment-derived columns:
--
--   PiXL.Device:
--     - AffluenceSignal, GpuTier, DeviceAgeYears (from Forge Tier 2/3)
--     - PrimaryBrowser, PrimaryOS (most common UA components for this device)
--     - FeatureBitmapValue (aggregated from parsed data)
--     - CompanyCount (how many companies this device visits)
--
--   PiXL.IP:
--     - MaxMind fields (geo from MaxMind, distinct from IPAPI geo)
--     - ReverseDNS, ReverseDNSCloud
--     - Subnet24 (computed via dbo.GetSubnet24 CLR function)
--     - SubnetReputationId FK → PiXL.SubnetReputation
--
--   PiXL.Visit:
--     - SessionId, SessionHitNumber (from Forge session stitching)
--     - BotScore, AnomalyScore (denormalized for fast dashboard queries)
--     - LeadQualityScore (from Forge Tier 2)
--
-- Design doc reference: §8.3 item 3 (Device Lifecycle), step 10 of Phase 8
-- ============================================================================
USE SmartPiXL;
GO

SET NOCOUNT ON;
GO

PRINT '--- 57: Dimension Table Expansion ---';
GO

-- =====================================================================
-- Part A: PiXL.Device expansion
-- =====================================================================
PRINT '  === PiXL.Device ===';
GO

-- Affluence signal from Forge Tier 2
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Device') AND name = N'AffluenceSignal'
)
BEGIN
    ALTER TABLE PiXL.Device ADD AffluenceSignal VARCHAR(4) NULL;
    PRINT '  Added PiXL.Device.AffluenceSignal (VARCHAR(4) NULL) — LOW|MID|HIGH';
END
ELSE PRINT '  PiXL.Device.AffluenceSignal already exists — skipped';
GO

-- GPU tier from Forge Tier 2
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Device') AND name = N'GpuTier'
)
BEGIN
    ALTER TABLE PiXL.Device ADD GpuTier VARCHAR(4) NULL;
    PRINT '  Added PiXL.Device.GpuTier (VARCHAR(4) NULL) — LOW|MID|HIGH';
END
ELSE PRINT '  PiXL.Device.GpuTier already exists — skipped';
GO

-- Device age from Forge Tier 3
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Device') AND name = N'DeviceAgeYears'
)
BEGIN
    ALTER TABLE PiXL.Device ADD DeviceAgeYears INT NULL;
    PRINT '  Added PiXL.Device.DeviceAgeYears (INT NULL)';
END
ELSE PRINT '  PiXL.Device.DeviceAgeYears already exists — skipped';
GO

-- Primary browser (most frequently seen browser for this device)
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Device') AND name = N'PrimaryBrowser'
)
BEGIN
    ALTER TABLE PiXL.Device ADD PrimaryBrowser VARCHAR(100) NULL;
    PRINT '  Added PiXL.Device.PrimaryBrowser (VARCHAR(100) NULL)';
END
ELSE PRINT '  PiXL.Device.PrimaryBrowser already exists — skipped';
GO

-- Primary OS
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Device') AND name = N'PrimaryOS'
)
BEGIN
    ALTER TABLE PiXL.Device ADD PrimaryOS VARCHAR(100) NULL;
    PRINT '  Added PiXL.Device.PrimaryOS (VARCHAR(100) NULL)';
END
ELSE PRINT '  PiXL.Device.PrimaryOS already exists — skipped';
GO

-- Feature bitmap (aggregated — most recent value from Parsed)
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Device') AND name = N'FeatureBitmapValue'
)
BEGIN
    ALTER TABLE PiXL.Device ADD FeatureBitmapValue INT NULL;
    PRINT '  Added PiXL.Device.FeatureBitmapValue (INT NULL)';
END
ELSE PRINT '  PiXL.Device.FeatureBitmapValue already exists — skipped';
GO

-- Company count (how many distinct companies this device visits)
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Device') AND name = N'CompanyCount'
)
BEGIN
    ALTER TABLE PiXL.Device ADD CompanyCount INT NOT NULL DEFAULT 0;
    PRINT '  Added PiXL.Device.CompanyCount (INT NOT NULL DEFAULT 0)';
END
ELSE PRINT '  PiXL.Device.CompanyCount already exists — skipped';
GO

-- =====================================================================
-- Part B: PiXL.IP expansion
-- =====================================================================
PRINT '  === PiXL.IP ===';
GO

-- MaxMind geo (separate from IPAPI geo already on the table)
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.IP') AND name = N'MaxMindCountry'
)
BEGIN
    ALTER TABLE PiXL.IP ADD MaxMindCountry VARCHAR(2) NULL;
    PRINT '  Added PiXL.IP.MaxMindCountry (VARCHAR(2) NULL)';
END
ELSE PRINT '  PiXL.IP.MaxMindCountry already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.IP') AND name = N'MaxMindCity'
)
BEGIN
    ALTER TABLE PiXL.IP ADD MaxMindCity VARCHAR(100) NULL;
    PRINT '  Added PiXL.IP.MaxMindCity (VARCHAR(100) NULL)';
END
ELSE PRINT '  PiXL.IP.MaxMindCity already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.IP') AND name = N'MaxMindASN'
)
BEGIN
    ALTER TABLE PiXL.IP ADD MaxMindASN INT NULL;
    PRINT '  Added PiXL.IP.MaxMindASN (INT NULL)';
END
ELSE PRINT '  PiXL.IP.MaxMindASN already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.IP') AND name = N'MaxMindASNOrg'
)
BEGIN
    ALTER TABLE PiXL.IP ADD MaxMindASNOrg VARCHAR(200) NULL;
    PRINT '  Added PiXL.IP.MaxMindASNOrg (VARCHAR(200) NULL)';
END
ELSE PRINT '  PiXL.IP.MaxMindASNOrg already exists — skipped';
GO

-- Reverse DNS
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.IP') AND name = N'ReverseDNS'
)
BEGIN
    ALTER TABLE PiXL.IP ADD ReverseDNS VARCHAR(500) NULL;
    PRINT '  Added PiXL.IP.ReverseDNS (VARCHAR(500) NULL)';
END
ELSE PRINT '  PiXL.IP.ReverseDNS already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.IP') AND name = N'ReverseDNSCloud'
)
BEGIN
    ALTER TABLE PiXL.IP ADD ReverseDNSCloud BIT NULL;
    PRINT '  Added PiXL.IP.ReverseDNSCloud (BIT NULL)';
END
ELSE PRINT '  PiXL.IP.ReverseDNSCloud already exists — skipped';
GO

-- Subnet24 — /24 subnet for this IP
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.IP') AND name = N'Subnet24'
)
BEGIN
    ALTER TABLE PiXL.IP ADD Subnet24 VARCHAR(18) NULL;
    PRINT '  Added PiXL.IP.Subnet24 (VARCHAR(18) NULL)';
END
ELSE PRINT '  PiXL.IP.Subnet24 already exists — skipped';
GO

-- Subnet reputation FK
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.IP') AND name = N'SubnetReputationId'
)
BEGIN
    ALTER TABLE PiXL.IP ADD SubnetReputationId INT NULL;
    PRINT '  Added PiXL.IP.SubnetReputationId (INT NULL)';

    -- FK to PiXL.SubnetReputation (created in migration 48)
    IF OBJECT_ID('PiXL.SubnetReputation', 'U') IS NOT NULL
    BEGIN
        ALTER TABLE PiXL.IP ADD CONSTRAINT FK_IP_SubnetReputation
            FOREIGN KEY (SubnetReputationId)
            REFERENCES PiXL.SubnetReputation(SubnetReputationId);
        PRINT '  Added FK_IP_SubnetReputation';
    END
END
ELSE PRINT '  PiXL.IP.SubnetReputationId already exists — skipped';
GO

-- Index on Subnet24 for subnet-level queries
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('PiXL.IP')
      AND name = 'IX_IP_Subnet24'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_IP_Subnet24
        ON PiXL.IP (Subnet24)
        INCLUDE (IPAddress, GeoCountryCode, IsDatacenter)
        WHERE Subnet24 IS NOT NULL;

    PRINT '  Created index IX_IP_Subnet24';
END
GO

-- =====================================================================
-- Part C: PiXL.Visit expansion
-- =====================================================================
PRINT '  === PiXL.Visit ===';
GO

-- Session stitching fields (denormalized from Parsed for fast session queries)
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Visit') AND name = N'SessionId'
)
BEGIN
    ALTER TABLE PiXL.Visit ADD SessionId VARCHAR(36) NULL;
    PRINT '  Added PiXL.Visit.SessionId (VARCHAR(36) NULL)';
END
ELSE PRINT '  PiXL.Visit.SessionId already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Visit') AND name = N'SessionHitNumber'
)
BEGIN
    ALTER TABLE PiXL.Visit ADD SessionHitNumber INT NULL;
    PRINT '  Added PiXL.Visit.SessionHitNumber (INT NULL)';
END
ELSE PRINT '  PiXL.Visit.SessionHitNumber already exists — skipped';
GO

-- Bot score (denormalized — avoids JOIN to Parsed for common dashboard filter)
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Visit') AND name = N'BotScore'
)
BEGIN
    ALTER TABLE PiXL.Visit ADD BotScore INT NULL;
    PRINT '  Added PiXL.Visit.BotScore (INT NULL)';
END
ELSE PRINT '  PiXL.Visit.BotScore already exists — skipped';
GO

-- Anomaly score (denormalized)
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Visit') AND name = N'AnomalyScore'
)
BEGIN
    ALTER TABLE PiXL.Visit ADD AnomalyScore INT NULL;
    PRINT '  Added PiXL.Visit.AnomalyScore (INT NULL)';
END
ELSE PRINT '  PiXL.Visit.AnomalyScore already exists — skipped';
GO

-- Lead quality score (from Forge Tier 2)
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Visit') AND name = N'LeadQualityScore'
)
BEGIN
    ALTER TABLE PiXL.Visit ADD LeadQualityScore INT NULL;
    PRINT '  Added PiXL.Visit.LeadQualityScore (INT NULL)';
END
ELSE PRINT '  PiXL.Visit.LeadQualityScore already exists — skipped';
GO

-- Session-based indexes on Visit
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('PiXL.Visit')
      AND name = 'IX_Visit_SessionId'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_Visit_SessionId
        ON PiXL.Visit (SessionId)
        INCLUDE (DeviceId, ReceivedAt, SessionHitNumber)
        WHERE SessionId IS NOT NULL;

    PRINT '  Created index IX_Visit_SessionId';
END
GO

-- Bot score filtered index on Visit for fast dashboard filtering
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('PiXL.Visit')
      AND name = 'IX_Visit_BotScore_High'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_Visit_BotScore_High
        ON PiXL.Visit (BotScore DESC, ReceivedAt DESC)
        INCLUDE (CompanyID, PiXLID, DeviceId, IpId)
        WHERE BotScore >= 50;

    PRINT '  Created filtered index IX_Visit_BotScore_High (BotScore >= 50)';
END
GO

-- =====================================================================
-- Part D: Update ETL.usp_ParseNewHits with bitmap population (Phase 8E)
-- =====================================================================
-- Adds a new ETL phase to populate bitmap columns using CLR functions.
-- This runs after the main parse phases and before dimension table
-- upserts, so the bitmap values are available for device aggregation.
-- =====================================================================

-- We update the proc in the next migration pass — the bitmap population
-- is added as Phase 8E in the existing ETL proc by re-creating it.
-- For now, bitmap columns will be NULL until the ETL proc is updated
-- and re-run. This is acceptable since ETL is paused during rebuild.

-- =====================================================================
-- Part E: Verification
-- =====================================================================
DECLARE @deviceCols INT = 0, @ipCols INT = 0, @visitCols INT = 0;

SELECT @deviceCols = COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('PiXL.Device');
SELECT @ipCols = COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('PiXL.IP');
SELECT @visitCols = COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('PiXL.Visit');

PRINT '  PiXL.Device columns: ' + CAST(@deviceCols AS VARCHAR(10));
PRINT '  PiXL.IP columns: ' + CAST(@ipCols AS VARCHAR(10));
PRINT '  PiXL.Visit columns: ' + CAST(@visitCols AS VARCHAR(10));

-- Verify specific new columns
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL.Device') AND name = 'AffluenceSignal')
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL.Device') AND name = 'CompanyCount')
    PRINT '  OK: PiXL.Device enrichment columns present';
ELSE
    PRINT '  WARNING: PiXL.Device enrichment columns incomplete';

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL.IP') AND name = 'Subnet24')
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL.IP') AND name = 'MaxMindCountry')
    PRINT '  OK: PiXL.IP enrichment columns present';
ELSE
    PRINT '  WARNING: PiXL.IP enrichment columns incomplete';

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL.Visit') AND name = 'SessionId')
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL.Visit') AND name = 'BotScore')
    PRINT '  OK: PiXL.Visit enrichment columns present';
ELSE
    PRINT '  WARNING: PiXL.Visit enrichment columns incomplete';
GO

PRINT '--- 57: Dimension expansion complete ---';
GO
