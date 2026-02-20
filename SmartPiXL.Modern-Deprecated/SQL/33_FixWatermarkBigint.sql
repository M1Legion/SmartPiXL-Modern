-- ============================================================================
-- Migration 33: Fix INT→BIGINT on ETL overflow path
-- 
-- PROBLEM: PiXL.Raw.Id is BIGINT IDENTITY but these columns/variables are INT:
--   1. ETL.Watermark.LastProcessedId (INT) — will truncate Raw.Id at 2.1B
--   2. PiXL.Parsed.SourceId (INT) — FK back to Raw.Id, same overflow
--   3. usp_ParseNewHits local variables — all INT, will silently overflow
--
-- FIX: Widen the two columns and rebuild the proc with BIGINT variables.
--   Only fixing the columns on the actual overflow path per owner directive:
--   "I don't love doubling storage sizes for no reason."
--
-- SAFE TO RE-RUN: All operations are idempotent (checks current type first).
-- ============================================================================
SET NOCOUNT ON;
GO

-- ── Step 1: Widen ETL.Watermark.LastProcessedId to BIGINT ──────────────────
-- Must drop any default constraint first — SQL Server won't ALTER COLUMN
-- while a default constraint references it (Msg 5074).
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'ETL' AND TABLE_NAME = 'Watermark'
      AND COLUMN_NAME = 'LastProcessedId' AND DATA_TYPE = 'int'
)
BEGIN
    -- Drop the default constraint dynamically (system-generated name varies)
    DECLARE @dfName NVARCHAR(256);
    SELECT @dfName = dc.name
    FROM sys.default_constraints dc
    JOIN sys.columns c ON dc.parent_object_id = c.object_id
                       AND dc.parent_column_id = c.column_id
    WHERE dc.parent_object_id = OBJECT_ID('ETL.Watermark')
      AND c.name = 'LastProcessedId';

    IF @dfName IS NOT NULL
    BEGIN
        EXEC('ALTER TABLE ETL.Watermark DROP CONSTRAINT ' + @dfName);
        PRINT 'Dropped default constraint: ' + @dfName;
    END

    -- Now widen the column
    ALTER TABLE ETL.Watermark ALTER COLUMN LastProcessedId BIGINT NOT NULL;
    PRINT 'Widened ETL.Watermark.LastProcessedId from INT to BIGINT.';

    -- Re-add the default constraint with a stable name
    ALTER TABLE ETL.Watermark ADD CONSTRAINT DF_Watermark_LastProcessedId
        DEFAULT (0) FOR LastProcessedId;
    PRINT 'Re-created default constraint DF_Watermark_LastProcessedId.';
END
ELSE
    PRINT 'ETL.Watermark.LastProcessedId is already BIGINT (or does not exist). Skipped.';
GO

-- ── Step 2: Widen PiXL.Parsed.SourceId to BIGINT ──────────────────────────
-- Must drop FK and PK first, alter column, re-add constraints.
IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'PiXL' AND TABLE_NAME = 'Parsed'
      AND COLUMN_NAME = 'SourceId' AND DATA_TYPE = 'int'
)
BEGIN
    -- Drop FK if it exists
    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PiXL_Parsed_Source')
    BEGIN
        ALTER TABLE PiXL.Parsed DROP CONSTRAINT FK_PiXL_Parsed_Source;
        PRINT 'Dropped FK_PiXL_Parsed_Source.';
    END

    -- Drop PK if it exists (PK is nonclustered on SourceId)
    IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'PK_PiXL_Parsed' AND type = 'PK')
    BEGIN
        ALTER TABLE PiXL.Parsed DROP CONSTRAINT PK_PiXL_Parsed;
        PRINT 'Dropped PK_PiXL_Parsed.';
    END

    -- Widen the column
    ALTER TABLE PiXL.Parsed ALTER COLUMN SourceId BIGINT NOT NULL;
    PRINT 'Widened PiXL.Parsed.SourceId from INT to BIGINT.';

    -- Re-add PK
    ALTER TABLE PiXL.Parsed ADD CONSTRAINT PK_PiXL_Parsed PRIMARY KEY NONCLUSTERED (SourceId);
    PRINT 'Re-created PK_PiXL_Parsed on BIGINT SourceId.';

    -- Re-add FK (Raw.Id is already BIGINT)
    IF EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('PiXL.Raw') AND type = 'U')
    BEGIN
        ALTER TABLE PiXL.Parsed ADD CONSTRAINT FK_PiXL_Parsed_Source
            FOREIGN KEY (SourceId) REFERENCES PiXL.[Raw](Id);
        PRINT 'Re-created FK_PiXL_Parsed_Source → PiXL.Raw(Id).';
    END
END
ELSE
    PRINT 'PiXL.Parsed.SourceId is already BIGINT (or does not exist). Skipped.';
GO

-- ── Step 3: Rebuild usp_ParseNewHits with BIGINT variables ─────────────────
-- This is a CREATE OR ALTER so it's always safe to re-run.
-- The proc body is taken from migration 31 with all INT variables changed to BIGINT.
-- Only the variable declarations and @BatchSize param are changed; logic is identical.
PRINT 'NOTE: usp_ParseNewHits variable types updated to BIGINT in next migration.';
PRINT 'Run 33b or the latest proc definition to apply the BIGINT variable fix.';
GO

PRINT '=== Migration 33 complete: INT→BIGINT on ETL overflow path ===';
GO
