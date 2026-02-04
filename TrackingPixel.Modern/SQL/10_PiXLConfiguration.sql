-- =============================================
-- SmartPiXL - PiXL Configuration Table
-- 
-- PURPOSE:
-- Stores per-PiXL settings that control how data is processed.
-- Raw data is ALWAYS collected (no per-request DB lookups).
-- Settings here control what happens during ETL/materialization.
--
-- USE CASES:
-- 1. Client doesn't want WebRTC local IP data → ExcludeLocalIP = 1
--    ETL will NULL out localIp field for this PiXL's records
--
-- 2. Healthcare client needs minimal fingerprinting → various exclusions
--
-- 3. Client wants bot traffic excluded entirely → ExcludeBots = 1
--    ETL will skip records with botScore > threshold
--
-- Last Updated: 2026-02-02
-- =============================================

USE SmartPixl;
GO

-- =============================================
-- PiXL CONFIGURATION TABLE
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PiXL_Config')
BEGIN
    CREATE TABLE dbo.PiXL_Config (
        ConfigId            INT IDENTITY(1,1) PRIMARY KEY,
        CompanyID           NVARCHAR(100)   NOT NULL,
        PiXLID              NVARCHAR(100)   NOT NULL,
        
        -- Data Collection Exclusions (applied during ETL/materialization)
        ExcludeLocalIP      BIT             NOT NULL DEFAULT 0,  -- NULL out WebRTC localIp
        ExcludeAudioFP      BIT             NOT NULL DEFAULT 0,  -- NULL out audio fingerprint
        ExcludeCanvasFP     BIT             NOT NULL DEFAULT 0,  -- NULL out canvas fingerprint
        ExcludeWebGLFP      BIT             NOT NULL DEFAULT 0,  -- NULL out WebGL fingerprint
        
        -- Bot Handling
        ExcludeBots         BIT             NOT NULL DEFAULT 0,  -- Skip records with botScore > BotScoreThreshold
        BotScoreThreshold   INT             NOT NULL DEFAULT 10, -- Records with botScore >= this are considered bots
        
        -- Data Retention (for cleanup jobs)
        RetentionDays       INT             NOT NULL DEFAULT 365,
        
        -- Audit
        CreatedAt           DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
        UpdatedAt           DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
        Notes               NVARCHAR(500)   NULL,  -- "Healthcare client - HIPAA audit requirement"
        
        -- Ensure unique per company/pixl
        CONSTRAINT UQ_PiXL_Config UNIQUE (CompanyID, PiXLID)
    );
    
    PRINT 'Table PiXL_Config created.';
END
ELSE
BEGIN
    PRINT 'Table PiXL_Config already exists.';
END
GO

-- =============================================
-- EXAMPLE CONFIGURATIONS
-- =============================================
/*
-- Standard E-commerce client (collect everything)
INSERT INTO dbo.PiXL_Config (CompanyID, PiXLID, Notes)
VALUES ('ACME', 'homepage', 'Standard config - full data collection');

-- Healthcare client (minimal fingerprinting, no local IP)
INSERT INTO dbo.PiXL_Config (CompanyID, PiXLID, ExcludeLocalIP, ExcludeAudioFP, Notes)
VALUES ('HealthCo', 'patient-portal', 'HIPAA compliant - excluded WebRTC and audio');

-- Client with bot problems (exclude high-score bot traffic)
INSERT INTO dbo.PiXL_Config (CompanyID, PiXLID, ExcludeBots, BotScoreThreshold, Notes)
VALUES ('BigRetail', 'checkout', 'Bot filtering enabled - threshold 8');
*/

-- =============================================
-- VIEW: Get config for a PiXL (with defaults)
-- Returns config if exists, otherwise default settings
-- =============================================
IF OBJECT_ID('dbo.vw_PiXL_ConfigWithDefaults', 'V') IS NOT NULL
    DROP VIEW dbo.vw_PiXL_ConfigWithDefaults;
GO

CREATE VIEW dbo.vw_PiXL_ConfigWithDefaults AS
SELECT DISTINCT
    t.CompanyID,
    t.PiXLID,
    ISNULL(c.ExcludeLocalIP, 0)     AS ExcludeLocalIP,
    ISNULL(c.ExcludeAudioFP, 0)     AS ExcludeAudioFP,
    ISNULL(c.ExcludeCanvasFP, 0)    AS ExcludeCanvasFP,
    ISNULL(c.ExcludeWebGLFP, 0)     AS ExcludeWebGLFP,
    ISNULL(c.ExcludeBots, 0)        AS ExcludeBots,
    ISNULL(c.BotScoreThreshold, 10) AS BotScoreThreshold,
    ISNULL(c.RetentionDays, 365)    AS RetentionDays,
    CASE WHEN c.ConfigId IS NULL THEN 1 ELSE 0 END AS IsDefaultConfig
FROM dbo.PiXL_Test t
LEFT JOIN dbo.PiXL_Config c 
    ON t.CompanyID = c.CompanyID 
    AND t.PiXLID = c.PiXLID;
GO

PRINT 'View vw_PiXL_ConfigWithDefaults created.';
GO

-- =============================================
-- PROCEDURE: Apply config during materialization
-- This is called by sp_MaterializePiXLData (or your ETL)
-- =============================================
IF OBJECT_ID('dbo.fn_ShouldExcludeRecord', 'FN') IS NOT NULL
    DROP FUNCTION dbo.fn_ShouldExcludeRecord;
GO

CREATE FUNCTION dbo.fn_ShouldExcludeRecord(
    @CompanyID NVARCHAR(100),
    @PiXLID NVARCHAR(100),
    @BotScore INT
)
RETURNS BIT
AS
BEGIN
    DECLARE @ExcludeBots BIT, @Threshold INT;
    
    SELECT 
        @ExcludeBots = ISNULL(ExcludeBots, 0),
        @Threshold = ISNULL(BotScoreThreshold, 10)
    FROM dbo.PiXL_Config
    WHERE CompanyID = @CompanyID AND PiXLID = @PiXLID;
    
    -- No config = don't exclude
    IF @ExcludeBots IS NULL
        RETURN 0;
    
    -- Check if this record should be excluded based on bot score
    IF @ExcludeBots = 1 AND @BotScore >= @Threshold
        RETURN 1;
    
    RETURN 0;
END
GO

PRINT 'Function fn_ShouldExcludeRecord created.';
GO

-- =============================================
-- PROCEDURE: Get data masking rules for a PiXL
-- Call this from ETL to know what fields to NULL out
-- =============================================
IF OBJECT_ID('dbo.sp_GetPiXLDataMaskingRules', 'P') IS NOT NULL
    DROP PROCEDURE dbo.sp_GetPiXLDataMaskingRules;
GO

CREATE PROCEDURE dbo.sp_GetPiXLDataMaskingRules
    @CompanyID NVARCHAR(100),
    @PiXLID NVARCHAR(100)
AS
BEGIN
    SET NOCOUNT ON;
    
    SELECT 
        ISNULL(ExcludeLocalIP, 0)   AS ExcludeLocalIP,
        ISNULL(ExcludeAudioFP, 0)   AS ExcludeAudioFP,
        ISNULL(ExcludeCanvasFP, 0)  AS ExcludeCanvasFP,
        ISNULL(ExcludeWebGLFP, 0)   AS ExcludeWebGLFP,
        ISNULL(ExcludeBots, 0)      AS ExcludeBots,
        ISNULL(BotScoreThreshold, 10) AS BotScoreThreshold
    FROM dbo.PiXL_Config
    WHERE CompanyID = @CompanyID AND PiXLID = @PiXLID;
    
    -- If no config, return defaults (all zeros = collect everything)
    IF @@ROWCOUNT = 0
    BEGIN
        SELECT 
            0 AS ExcludeLocalIP,
            0 AS ExcludeAudioFP,
            0 AS ExcludeCanvasFP,
            0 AS ExcludeWebGLFP,
            0 AS ExcludeBots,
            10 AS BotScoreThreshold;
    END
END
GO

PRINT 'Procedure sp_GetPiXLDataMaskingRules created.';
GO

PRINT '';
PRINT '=============================================';
PRINT 'PiXL Configuration schema installed.';
PRINT '';
PRINT 'HOW TO USE:';
PRINT '1. Insert rows into PiXL_Config for clients needing custom settings';
PRINT '2. During ETL/materialization, call sp_GetPiXLDataMaskingRules';
PRINT '3. Apply the exclusions by NULLing fields or skipping records';
PRINT '';
PRINT 'ARCHITECTURE:';
PRINT '- JS script collects EVERYTHING (no per-request DB calls)';
PRINT '- Raw data goes into PiXL_Test as-is';
PRINT '- ETL/materialization applies PiXL_Config rules';
PRINT '- PiXL_Materialized has filtered/masked data per client config';
PRINT '=============================================';
GO
