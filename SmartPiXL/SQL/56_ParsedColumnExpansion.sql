-- ============================================================================
-- Migration 56: PiXL.Parsed Column Expansion + Bitmap Columns (Phase 8)
-- ============================================================================
-- Two parts:
--   A) Bitmap columns — FeatureBitmapValue, AccessibilityBitmapValue,
--      BotBitmapValue, EvasionBitmapValue. These are regular INT columns
--      (not computed columns) populated by the ETL proc.
--
--   B) Additional indexes for the new enrichment columns added by
--      migrations 42-44.
--
-- CONFLICT DECISION (logged in IMPLEMENTATION-LOG.md):
--   The workplan specifies "computed bitmap columns using CLR functions:
--   FeatureBitmapValue AS dbo.FeatureBitmap(...) PERSISTED". However,
--   the CLR functions are deployed in SmartPiXL_CLR and accessed via
--   synonyms in SmartPiXL.dbo. SQL Server cannot PERSIST computed columns
--   that reference cross-database synonyms because schema binding cannot
--   follow synonyms, and non-persisted computed CLR columns evaluated at
--   query time add overhead to every read. Regular INT columns populated
--   once by the ETL proc are more robust: no cross-database dependency
--   in the schema, no per-read CLR invocation, no synonym fragility.
--   The values are immutable after parse — a bitmap of detected features
--   doesn't change retroactively.
--
-- Design doc reference: §8.3 items 6, 15 (Bitmaps)
-- ============================================================================
USE SmartPiXL;
GO

SET NOCOUNT ON;
GO

PRINT '--- 56: PiXL.Parsed Column Expansion + Bitmap Columns ---';
GO

-- =====================================================================
-- Step 1: Add bitmap columns as regular INT columns
-- =====================================================================

-- Feature bitmap: 17 browser feature detection flags → single INT
-- Bits: ls(0), ss(1), idb(2), caches(3), ww(4), swk(5), wasm(6),
--        webgl(7), webgl2(8), canvas(9), touchEvent(10), pointerEvent(11),
--        mediaDevices(12), clipboard(13), speechSynth(14), chromeObj(15),
--        chromeRuntime(16)
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed') AND name = N'FeatureBitmapValue'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD FeatureBitmapValue INT NULL;
    PRINT '  Added PiXL.Parsed.FeatureBitmapValue (INT NULL)';
END
ELSE PRINT '  PiXL.Parsed.FeatureBitmapValue already exists — skipped';
GO

-- Accessibility bitmap: 9 accessibility/preference flags → single INT
-- Bits: darkMode(0), lightMode(1), reducedMotion(2), reducedData(3),
--        contrast(4), forcedColors(5), invertedColors(6), hover(7),
--        standalone(8)
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed') AND name = N'AccessibilityBitmapValue'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD AccessibilityBitmapValue INT NULL;
    PRINT '  Added PiXL.Parsed.AccessibilityBitmapValue (INT NULL)';
END
ELSE PRINT '  PiXL.Parsed.AccessibilityBitmapValue already exists — skipped';
GO

-- Bot bitmap: 20 bot detection signals → single INT
-- Used for fast WHERE BotBitmapValue & X > 0 queries
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed') AND name = N'BotBitmapValue'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD BotBitmapValue INT NULL;
    PRINT '  Added PiXL.Parsed.BotBitmapValue (INT NULL)';
END
ELSE PRINT '  PiXL.Parsed.BotBitmapValue already exists — skipped';
GO

-- Evasion bitmap: 8 evasion detection signals → single INT
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed') AND name = N'EvasionBitmapValue'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD EvasionBitmapValue INT NULL;
    PRINT '  Added PiXL.Parsed.EvasionBitmapValue (INT NULL)';
END
ELSE PRINT '  PiXL.Parsed.EvasionBitmapValue already exists — skipped';
GO

-- =====================================================================
-- Step 2: Create indexes for analysis queries
-- =====================================================================

-- Bitmap index for device clustering — GROUP BY FeatureBitmapValue
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('PiXL.Parsed')
      AND name = 'IX_Parsed_FeatureBitmap'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_Parsed_FeatureBitmap
        ON PiXL.Parsed (FeatureBitmapValue)
        INCLUDE (CompanyID, ReceivedAt, BotScore)
        WHERE FeatureBitmapValue IS NOT NULL;

    PRINT '  Created filtered index IX_Parsed_FeatureBitmap';
END
GO

-- Bot score filtered index for dashboard queries
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('PiXL.Parsed')
      AND name = 'IX_Parsed_BotScore_High'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_Parsed_BotScore_High
        ON PiXL.Parsed (BotScore DESC, ReceivedAt DESC)
        INCLUDE (CompanyID, PiXLID, IPAddress)
        WHERE BotScore >= 50;

    PRINT '  Created filtered index IX_Parsed_BotScore_High (BotScore >= 50)';
END
GO

-- Company + ReceivedAt covering index for common dashboard filter
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('PiXL.Parsed')
      AND name = 'IX_Parsed_Company_ReceivedAt'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_Parsed_Company_ReceivedAt
        ON PiXL.Parsed (CompanyID, ReceivedAt DESC)
        INCLUDE (PiXLID, IPAddress, BotScore, AnomalyScore, MouseMoveCount, UserScrolled);

    PRINT '  Created index IX_Parsed_Company_ReceivedAt';
END
GO

-- =====================================================================
-- Step 3: Verification
-- =====================================================================
DECLARE @bitmapCols INT = 0;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL.Parsed') AND name = 'FeatureBitmapValue')
    SET @bitmapCols += 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL.Parsed') AND name = 'AccessibilityBitmapValue')
    SET @bitmapCols += 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL.Parsed') AND name = 'BotBitmapValue')
    SET @bitmapCols += 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL.Parsed') AND name = 'EvasionBitmapValue')
    SET @bitmapCols += 1;

IF @bitmapCols = 4
    PRINT '  OK: All 4 bitmap columns present on PiXL.Parsed';
ELSE
    PRINT '  ERROR: Expected 4 bitmap columns, found ' + CAST(@bitmapCols AS VARCHAR(10));

-- Total column count
DECLARE @totalCols INT;
SELECT @totalCols = COUNT(*)
FROM sys.columns WHERE object_id = OBJECT_ID('PiXL.Parsed');
PRINT '  PiXL.Parsed total columns: ' + CAST(@totalCols AS VARCHAR(10));
GO

PRINT '--- 56: Parsed column expansion complete ---';
GO
