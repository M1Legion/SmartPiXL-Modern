-- ============================================================================
-- Migration 57: Merge PiXL.Raw fields into PiXL.Parsed
-- ============================================================================
-- Adds the two nvarchar(max) raw fields (QueryString, HeadersJson) to
-- PiXL.Parsed so the Forge can write all columns in a single BulkCopy.
-- Creates a SEQUENCE for SourceId generation (replaces Raw.Id dependency).
-- Drops non-essential indexes to maximize insert throughput.
--
-- RATIONALE:
--   The two-step pipeline (Raw → Parsed) was an artifact of the original ETL
--   design where SQL scalar UDFs parsed the query string. Now that .NET does
--   the parsing (~1µs/row), writing to Raw first is pointless overhead:
--     Before: Forge → BulkCopy → Raw → ParsedBulkInsertService → Parsed
--     After:  Forge → Parse inline → BulkCopy → Parsed (single write)
--
-- WHAT THIS CHANGES:
--   1. Adds QueryString NVARCHAR(MAX) NULL to PiXL.Parsed
--   2. Adds HeadersJson NVARCHAR(MAX) NULL to PiXL.Parsed
--   3. Creates PiXL.HitSequence for auto-generating SourceId values
--   4. Adds DEFAULT on SourceId bound to the sequence
--   5. Drops 7 non-essential indexes (keep clustered + PK + company indexes)
--   6. Adds 'ProcessDimensions' watermark for Phase 9-13 processing
--
-- WHAT THIS DOES NOT CHANGE:
--   • PiXL.Raw remains intact (137M rows, legacy views reference it)
--   • All existing PiXL.Parsed data stays (existing rows get NULL for new cols)
--   • All views/procs that reference PiXL.Parsed by column name keep working
--
-- ROLLBACK:
--   ALTER TABLE PiXL.Parsed DROP CONSTRAINT DF_Parsed_SourceId;
--   DROP SEQUENCE PiXL.HitSequence;
--   ALTER TABLE PiXL.Parsed DROP COLUMN QueryString;
--   ALTER TABLE PiXL.Parsed DROP COLUMN HeadersJson;
--   (then recreate dropped indexes from 56_ParsedColumnExpansion.sql)
-- ============================================================================
USE SmartPiXL;
GO

SET NOCOUNT ON;
GO

PRINT '--- 57: Merge PiXL.Raw fields into PiXL.Parsed ---';
GO

-- =====================================================================
-- Step 1: Add raw fields to PiXL.Parsed
-- =====================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed') AND name = N'QueryString'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD QueryString NVARCHAR(MAX) NULL;
    PRINT '  Added PiXL.Parsed.QueryString (NVARCHAR(MAX) NULL)';
END
ELSE PRINT '  PiXL.Parsed.QueryString already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed') AND name = N'HeadersJson'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD HeadersJson NVARCHAR(MAX) NULL;
    PRINT '  Added PiXL.Parsed.HeadersJson (NVARCHAR(MAX) NULL)';
END
ELSE PRINT '  PiXL.Parsed.HeadersJson already exists — skipped';
GO

-- =====================================================================
-- Step 2: Create SEQUENCE for SourceId generation
-- =====================================================================
-- Starts past the current max Raw.Id so new Parsed rows never collide
-- with SourceId values already written from the Raw pipeline.
-- CACHE 1000 reduces sequence contention under concurrent BulkCopy.

IF NOT EXISTS (
    SELECT 1 FROM sys.sequences
    WHERE schema_id = SCHEMA_ID('PiXL') AND name = 'HitSequence'
)
BEGIN
    DECLARE @maxRawId BIGINT = (SELECT ISNULL(MAX(Id), 0) FROM PiXL.Raw);
    DECLARE @start BIGINT = @maxRawId + 1000;
    DECLARE @sql NVARCHAR(500) = N'CREATE SEQUENCE PiXL.HitSequence AS BIGINT START WITH '
        + CAST(@start AS NVARCHAR(20))
        + N' INCREMENT BY 1 CACHE 1000;';
    EXEC sp_executesql @sql;
    PRINT '  Created PiXL.HitSequence starting at ' + CAST(@start AS VARCHAR(20));
END
ELSE PRINT '  PiXL.HitSequence already exists — skipped';
GO

-- =====================================================================
-- Step 3: Add DEFAULT constraint binding SourceId to the sequence
-- =====================================================================
-- When SqlBulkCopy omits SourceId from column mappings, SQL Server
-- uses NEXT VALUE FOR PiXL.HitSequence automatically.

IF NOT EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE parent_object_id = OBJECT_ID('PiXL.Parsed')
      AND name = 'DF_Parsed_SourceId'
)
BEGIN
    ALTER TABLE PiXL.Parsed
        ADD CONSTRAINT DF_Parsed_SourceId
        DEFAULT NEXT VALUE FOR PiXL.HitSequence FOR SourceId;
    PRINT '  Added DEFAULT constraint DF_Parsed_SourceId (SEQUENCE)';
END
ELSE PRINT '  DF_Parsed_SourceId already exists — skipped';
GO

-- =====================================================================
-- Step 4: Drop non-essential indexes for insert throughput
-- =====================================================================
-- Keeping:  CIX_PiXL_Parsed_ReceivedAt (clustered)
--           PK_PiXL_Parsed (nonclustered PK)
--           IX_Parsed_Company_ReceivedAt (primary dashboard filter)
--           IX_Parsed_Company (company+pixl+date filter)
-- Dropping: 7 analysis indexes that impede insert speed.
--           These can be recreated later as filtered indexes on a
--           reporting schedule or moved to indexed views.

DROP INDEX IF EXISTS IX_Parsed_FeatureBitmap ON PiXL.Parsed;
PRINT '  Dropped IX_Parsed_FeatureBitmap';

DROP INDEX IF EXISTS IX_Parsed_BotScore_High ON PiXL.Parsed;
PRINT '  Dropped IX_Parsed_BotScore_High';

DROP INDEX IF EXISTS IX_Parsed_CanvasFP ON PiXL.Parsed;
PRINT '  Dropped IX_Parsed_CanvasFP';

DROP INDEX IF EXISTS IX_Parsed_BotScore ON PiXL.Parsed;
PRINT '  Dropped IX_Parsed_BotScore';

DROP INDEX IF EXISTS IX_Parsed_IP ON PiXL.Parsed;
PRINT '  Dropped IX_Parsed_IP';

DROP INDEX IF EXISTS IX_Parsed_Synthetic ON PiXL.Parsed;
PRINT '  Dropped IX_Parsed_Synthetic';

DROP INDEX IF EXISTS IX_Parsed_DashHealth ON PiXL.Parsed;
PRINT '  Dropped IX_Parsed_DashHealth';
GO

-- =====================================================================
-- Step 5: Add ProcessDimensions watermark
-- =====================================================================
-- Separate watermark for Phase 9-13 processing against PiXL.Parsed.
-- Starts at the current ParseNewHits watermark so existing rows aren't
-- reprocessed.

IF NOT EXISTS (SELECT 1 FROM ETL.Watermark WHERE ProcessName = 'ProcessDimensions')
BEGIN
    DECLARE @currentWatermark BIGINT = (
        SELECT ISNULL(LastProcessedId, 0)
        FROM ETL.Watermark
        WHERE ProcessName = 'ParseNewHits'
    );
    INSERT INTO ETL.Watermark (ProcessName, LastProcessedId, RowsProcessed)
    VALUES ('ProcessDimensions', @currentWatermark, 0);
    PRINT '  Added ProcessDimensions watermark at ' + CAST(@currentWatermark AS VARCHAR(20));
END
ELSE PRINT '  ProcessDimensions watermark already exists — skipped';
GO

PRINT '--- 57: Migration complete ---';
GO
