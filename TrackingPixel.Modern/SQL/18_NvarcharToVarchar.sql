-- ============================================================================
-- Migration 18: NVARCHAR → VARCHAR Conversion + Missing Indexes
-- ============================================================================
-- Every string column in PiXL_Test, PiXL_Parsed, PiXL_Config, and 
-- ETL_Watermark was NVARCHAR despite containing only ASCII data (IPs, URLs,
-- fingerprint hashes, user agents, JSON). NVARCHAR uses 2 bytes/char vs 
-- VARCHAR's 1 byte — double the storage and wider index keys for zero benefit.
--
-- This migration:
--   1. Converts all non-MAX string columns to VARCHAR (same lengths)
--   2. Leaves QueryString/HeadersJson as NVARCHAR(MAX) — raw ingest, never 
--      indexed, never a lookup column. MAX is correct for those.
--   3. Leaves Notes columns as NVARCHAR — human-authored text could contain 
--      Unicode legitimately.
--   4. Creates the missing clustered index and dashboard indexes on PiXL_Parsed
--      (defined in migration 16 but never applied to live DB).
--
-- Prerequisites: PiXL_Parsed had NO string columns in any index (only PK on 
-- SourceId INT). PiXL_Test had only clustered PK on Id INT. No index drops
-- needed for the non-constrained ALTER operations.
--
-- Safe to run repeatedly — all operations are idempotent.
-- Applied: 2025-06-30
-- ============================================================================

USE SmartPiXL;
GO

SET NOCOUNT ON;
PRINT '=== Migration 18: NVARCHAR → VARCHAR + Missing Indexes ===';
PRINT 'Started at: ' + CONVERT(VARCHAR(30), GETDATE(), 121);
GO

-- ============================================================================
-- 1. PiXL_Test — Convert 6 non-MAX columns
-- ============================================================================
-- Skipping QueryString (MAX) and HeadersJson (MAX) — raw ingest, correct as-is

PRINT '';
PRINT '--- PiXL_Test ---';

ALTER TABLE dbo.PiXL_Test ALTER COLUMN CompanyID   VARCHAR(100)  NULL;
ALTER TABLE dbo.PiXL_Test ALTER COLUMN PiXLID      VARCHAR(100)  NULL;
ALTER TABLE dbo.PiXL_Test ALTER COLUMN IPAddress    VARCHAR(50)   NULL;
ALTER TABLE dbo.PiXL_Test ALTER COLUMN RequestPath  VARCHAR(500)  NULL;
ALTER TABLE dbo.PiXL_Test ALTER COLUMN UserAgent    VARCHAR(2000) NULL;
ALTER TABLE dbo.PiXL_Test ALTER COLUMN Referer      VARCHAR(2000) NULL;

PRINT 'PiXL_Test: 6 columns converted to VARCHAR';
GO

-- ============================================================================
-- 2. PiXL_Parsed — Convert 72 string columns (2 Notes columns stay NVARCHAR)
-- ============================================================================
-- PiXL_Parsed was a HEAP with only PK_PiXL_Parsed (nonclustered on SourceId INT).
-- No string columns were in any index — safe to ALTER directly.

PRINT '';
PRINT '--- PiXL_Parsed ---';

-- Identity & Server Context (6 cols)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN CompanyID          VARCHAR(100)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN PiXLID             VARCHAR(100)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN IPAddress           VARCHAR(50)   NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN RequestPath         VARCHAR(500)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN ServerUserAgent     VARCHAR(2000) NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN ServerReferer       VARCHAR(2000) NULL;

-- Screen & Display (1 col)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN ScreenOrientation   VARCHAR(50)   NULL;

-- Locale & Internationalization (7 cols)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN Timezone            VARCHAR(100)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN TimezoneLocale      VARCHAR(200)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN DateFormatSample    VARCHAR(200)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN NumberFormatSample  VARCHAR(200)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN RelativeTimeSample  VARCHAR(200)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN Language            VARCHAR(50)   NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN LanguageList        VARCHAR(500)  NULL;

-- Browser & Navigator (9 cols)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN Platform            VARCHAR(100)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN Vendor              VARCHAR(200)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN ClientUserAgent     VARCHAR(2000) NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN NavigatorProduct    VARCHAR(50)   NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN NavigatorProductSub VARCHAR(50)   NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN NavigatorVendorSub  VARCHAR(200)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN AppName             VARCHAR(100)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN AppVersion          VARCHAR(500)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN AppCodeName         VARCHAR(100)  NULL;

-- GPU & WebGL (3 cols)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN GPURenderer         VARCHAR(500)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN GPUVendor           VARCHAR(200)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN WebGLParameters     VARCHAR(2000) NULL;

-- Fingerprint Signals (8 cols)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN CanvasFingerprint   VARCHAR(200)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN WebGLFingerprint    VARCHAR(200)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN AudioFingerprintSum VARCHAR(200)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN AudioFingerprintHash VARCHAR(200) NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN MathFingerprint     VARCHAR(200)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN ErrorFingerprint    VARCHAR(200)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN CSSFontVariantHash  VARCHAR(200)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN DetectedFonts       VARCHAR(4000) NULL;

-- Plugins & MIME (2 cols)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN PluginListDetailed  VARCHAR(4000) NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN MimeTypeList        VARCHAR(4000) NULL;

-- Speech & Input (2 cols)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN SpeechVoices        VARCHAR(4000) NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN ConnectedGamepads   VARCHAR(1000) NULL;

-- Network & Connection (4 cols)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN WebRTCLocalIP       VARCHAR(50)   NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN ConnectionType      VARCHAR(50)   NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN DownlinkMax         VARCHAR(50)   NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN NetworkType         VARCHAR(50)   NULL;

-- Privacy (1 col)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN DoNotTrack          VARCHAR(50)   NULL;

-- Accessibility (1 col)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN PointerType         VARCHAR(20)   NULL;

-- Document State (4 cols)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN DocumentCharset     VARCHAR(50)   NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN DocumentCompatMode  VARCHAR(50)   NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN DocumentReadyState  VARCHAR(50)   NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN DocumentVisibility  VARCHAR(50)   NULL;

-- Page Context (7 cols)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN PageURL             VARCHAR(2000) NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN PageReferrer        VARCHAR(2000) NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN PageTitle           VARCHAR(1000) NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN PageDomain          VARCHAR(500)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN PagePath            VARCHAR(1000) NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN PageHash            VARCHAR(500)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN PageProtocol        VARCHAR(20)   NULL;

-- Bot Detection (1 col)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN BotSignalsList      VARCHAR(4000) NULL;

-- Evasion Detection (2 cols)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN EvasionToolsDetected    VARCHAR(1000) NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN ProxyBlockedProperties  VARCHAR(1000) NULL;

-- Client Hints / UA-CH (8 cols)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN UA_Architecture     VARCHAR(50)   NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN UA_Bitness          VARCHAR(10)   NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN UA_Model            VARCHAR(200)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN UA_PlatformVersion  VARCHAR(100)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN UA_FullVersionList  VARCHAR(500)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN UA_Platform         VARCHAR(50)   NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN UA_Brands           VARCHAR(500)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN UA_FormFactor       VARCHAR(100)  NULL;

-- Browser-Specific (2 cols)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN Firefox_OSCPU       VARCHAR(200)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN Firefox_BuildID     VARCHAR(100)  NULL;

-- Advanced Fingerprint Stability (1 col)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN CanvasConsistency   VARCHAR(50)   NULL;

-- Behavioral Biometrics (2 cols)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN MoveCountBucket     VARCHAR(20)   NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN BehavioralFlags     VARCHAR(200)  NULL;

-- Cross-Signal & Stealth Detection (3 cols)
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN StealthPluginSignals VARCHAR(500) NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN EvasionSignalsV2    VARCHAR(500)  NULL;
ALTER TABLE dbo.PiXL_Parsed ALTER COLUMN CrossSignalFlags    VARCHAR(500)  NULL;

PRINT 'PiXL_Parsed: 72 columns converted to VARCHAR';
GO

-- ============================================================================
-- 3. PiXL_Config — Convert CompanyID and PiXLID (leave Notes as NVARCHAR)
-- ============================================================================
-- UQ_PiXL_Config unique constraint on (CompanyID, PiXLID) must be dropped first.

PRINT '';
PRINT '--- PiXL_Config ---';

IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_PiXL_Config')
    ALTER TABLE dbo.PiXL_Config DROP CONSTRAINT UQ_PiXL_Config;

ALTER TABLE dbo.PiXL_Config ALTER COLUMN CompanyID VARCHAR(100) NOT NULL;
ALTER TABLE dbo.PiXL_Config ALTER COLUMN PiXLID    VARCHAR(100) NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_PiXL_Config')
    ALTER TABLE dbo.PiXL_Config ADD CONSTRAINT UQ_PiXL_Config UNIQUE (CompanyID, PiXLID);

PRINT 'PiXL_Config: 2 columns converted to VARCHAR (Notes stays NVARCHAR), UQ recreated';
GO

-- ============================================================================
-- 4. ETL_Watermark — Convert ProcessName (ASCII process names only)
-- ============================================================================
-- ProcessName is the PK — need to drop the auto-named PK, alter, and recreate
-- with a clean deterministic name.

PRINT '';
PRINT '--- ETL_Watermark ---';

-- Drop whatever PK exists (auto-name varies per install)
IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID('ETL_Watermark') AND type = 'PK')
BEGIN
    DECLARE @pkName NVARCHAR(200);
    SELECT @pkName = name FROM sys.key_constraints 
    WHERE parent_object_id = OBJECT_ID('ETL_Watermark') AND type = 'PK';
    EXEC('ALTER TABLE dbo.ETL_Watermark DROP CONSTRAINT [' + @pkName + ']');
END
GO

ALTER TABLE dbo.ETL_Watermark ALTER COLUMN ProcessName VARCHAR(100) NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'PK_ETL_Watermark')
    ALTER TABLE dbo.ETL_Watermark ADD CONSTRAINT PK_ETL_Watermark PRIMARY KEY CLUSTERED (ProcessName);

PRINT 'ETL_Watermark: ProcessName converted to VARCHAR(100), PK recreated as PK_ETL_Watermark';
GO

-- ============================================================================
-- 5. Create missing clustered index on PiXL_Parsed
-- ============================================================================
-- Migration 16 defined this but it was never applied. PiXL_Parsed was a HEAP
-- which means all queries did full table scans.

PRINT '';
PRINT '--- Missing Indexes ---';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'CIX_PiXL_Parsed_ReceivedAt' AND object_id = OBJECT_ID('PiXL_Parsed'))
BEGIN
    CREATE UNIQUE CLUSTERED INDEX CIX_PiXL_Parsed_ReceivedAt 
        ON dbo.PiXL_Parsed (ReceivedAt, SourceId);
    PRINT 'Created clustered index CIX_PiXL_Parsed_ReceivedAt on (ReceivedAt, SourceId)';
END
ELSE
    PRINT 'CIX_PiXL_Parsed_ReceivedAt already exists';
GO

-- ============================================================================
-- 6. Create missing dashboard indexes (defined in migration 16, never applied)
-- ============================================================================
-- These power the vw_Dash_* views for the Tron dashboard.

-- Bot score queries: "show me all bots", "bot score distribution"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Parsed_BotScore' AND object_id = OBJECT_ID('PiXL_Parsed'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Parsed_BotScore
        ON dbo.PiXL_Parsed (BotScore)
        INCLUDE (BotSignalsList, Platform, CompanyID, ReceivedAt, CombinedThreatScore);
    PRINT 'Created IX_Parsed_BotScore';
END;

-- Client filtering: "show me Company X's data"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Parsed_Company' AND object_id = OBJECT_ID('PiXL_Parsed'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Parsed_Company
        ON dbo.PiXL_Parsed (CompanyID, PiXLID, ReceivedAt DESC)
        INCLUDE (BotScore, IPAddress, CanvasFingerprint);
    PRINT 'Created IX_Parsed_Company';
END;

-- Synthetic filter: "exclude test traffic" (most dashboard queries add this)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Parsed_Synthetic' AND object_id = OBJECT_ID('PiXL_Parsed'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Parsed_Synthetic
        ON dbo.PiXL_Parsed (IsSynthetic, ReceivedAt DESC)
        INCLUDE (BotScore, CanvasFingerprint, Platform, CompanyID);
    PRINT 'Created IX_Parsed_Synthetic';
END;

-- Fingerprint lookups: "unique visitors", "fingerprint history"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Parsed_CanvasFP' AND object_id = OBJECT_ID('PiXL_Parsed'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Parsed_CanvasFP
        ON dbo.PiXL_Parsed (CanvasFingerprint)
        INCLUDE (ReceivedAt, IPAddress, Platform, BotScore)
        WHERE CanvasFingerprint IS NOT NULL;
    PRINT 'Created IX_Parsed_CanvasFP';
END;

-- IP lookups: "all hits from this IP"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Parsed_IP' AND object_id = OBJECT_ID('PiXL_Parsed'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Parsed_IP
        ON dbo.PiXL_Parsed (IPAddress, ReceivedAt DESC)
        INCLUDE (CanvasFingerprint, BotScore, CompanyID);
    PRINT 'Created IX_Parsed_IP';
END;

PRINT '';
PRINT 'Dashboard indexes created';
GO

-- ============================================================================
-- 7. Verification
-- ============================================================================

PRINT '';
PRINT '=== Verification ===';

-- Count remaining NVARCHAR columns (should only be Notes + MAX columns)
SELECT 
    t.name AS TableName,
    c.name AS ColumnName,
    ty.name AS TypeName,
    CASE WHEN c.max_length = -1 THEN 'MAX' 
         ELSE CAST(c.max_length/2 AS VARCHAR(10)) END AS Len
FROM sys.columns c
JOIN sys.tables t ON c.object_id = t.object_id
JOIN sys.types ty ON c.system_type_id = ty.system_type_id AND c.user_type_id = ty.user_type_id
WHERE ty.name = 'nvarchar'
  AND t.name IN ('PiXL_Test','PiXL_Parsed','PiXL_Config','ETL_Watermark')
ORDER BY t.name, c.column_id;

-- Show index count on PiXL_Parsed
SELECT COUNT(*) AS IndexCount 
FROM sys.indexes 
WHERE object_id = OBJECT_ID('PiXL_Parsed') AND type > 0;

PRINT '';
PRINT 'Migration 18 complete at: ' + CONVERT(VARCHAR(30), GETDATE(), 121);
GO
