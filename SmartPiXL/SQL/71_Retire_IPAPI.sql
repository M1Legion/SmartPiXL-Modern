-- ============================================================================
-- 71_Retire_IPAPI.sql
-- Retires the IPAPI schema and migrates sync logs to IPInfo.ImportLog.
--
-- WHAT THIS DOES:
--   1. Migrates IPAPI.SyncLog rows to IPInfo.ImportLog (preserves history)
--   2. Drops IPAPI.usp_EnrichGeo (replaced by IPInfo.usp_EnrichGeo)
--   3. Drops IPAPI.SyncLog (replaced by IPInfo.ImportLog)
--   4. Renames ETL.usp_EnrichParsedGeo to call IPInfo instead of IPAPI
--   5. Removes IpApiSync watermark from ETL.Watermark
--
-- DOES NOT DROP:
--   - IPAPI.IP table (344M rows) — kept as historical reference until
--     IPInfo range tables are validated. Can be dropped after validation.
--   - Geo columns on PiXL.IP / PiXL.Parsed — these are populated by
--     IPInfo.usp_EnrichGeo going forward.
--
-- Prerequisites:
--   - 70_IPInfo_Schema.sql must run first
--
-- Safe to re-run: all operations are idempotent.
-- ============================================================================

SET NOCOUNT ON;
PRINT '=== 71_Retire_IPAPI.sql ===';
PRINT '';

-- ============================================================================
-- PHASE 1: Migrate IPAPI.SyncLog data to IPInfo.ImportLog
-- ============================================================================
PRINT '--- Phase 1: Migrate SyncLog data ---';

IF OBJECT_ID('IPAPI.SyncLog', 'U') IS NOT NULL
AND OBJECT_ID('IPInfo.ImportLog', 'U') IS NOT NULL
BEGIN
    DECLARE @migrated INT;

    INSERT INTO IPInfo.ImportLog (SourceId, SyncType, StartedAt, CompletedAt, RowsImported, RowsDeleted, DurationMs, ErrorMessage)
    SELECT
        NULL,         -- SourceId not tracked in old table
        SyncType,
        StartedAt,
        CompletedAt,
        RowsInserted, -- maps to RowsImported
        ISNULL(RowsDeleted, 0),
        DurationMs,
        ErrorMessage
    FROM IPAPI.SyncLog
    WHERE NOT EXISTS (
        -- Prevent duplicate migration on re-run
        SELECT 1 FROM IPInfo.ImportLog il
        WHERE il.SyncType = IPAPI.SyncLog.SyncType
          AND il.StartedAt = IPAPI.SyncLog.StartedAt
    );

    SET @migrated = @@ROWCOUNT;
    PRINT '  Migrated ' + CAST(@migrated AS VARCHAR(10)) + ' rows from IPAPI.SyncLog to IPInfo.ImportLog';
END
ELSE IF OBJECT_ID('IPAPI.SyncLog', 'U') IS NULL
    PRINT '  IPAPI.SyncLog does not exist — nothing to migrate';
ELSE
    PRINT '  IPInfo.ImportLog does not exist — run 70_IPInfo_Schema.sql first';
GO

-- ============================================================================
-- PHASE 2: Drop IPAPI.usp_EnrichGeo (replaced by IPInfo.usp_EnrichGeo)
-- ============================================================================
PRINT '--- Phase 2: Drop IPAPI.usp_EnrichGeo ---';

IF OBJECT_ID('IPAPI.usp_EnrichGeo', 'P') IS NOT NULL
BEGIN
    DROP PROCEDURE IPAPI.usp_EnrichGeo;
    PRINT '  Dropped IPAPI.usp_EnrichGeo';
END
ELSE
    PRINT '  IPAPI.usp_EnrichGeo does not exist — skipped';
GO

-- ============================================================================
-- PHASE 3: Drop IPAPI.SyncLog (replaced by IPInfo.ImportLog)
-- ============================================================================
PRINT '--- Phase 3: Drop IPAPI.SyncLog ---';

IF OBJECT_ID('IPAPI.SyncLog', 'U') IS NOT NULL
BEGIN
    DROP TABLE IPAPI.SyncLog;
    PRINT '  Dropped IPAPI.SyncLog';
END
ELSE
    PRINT '  IPAPI.SyncLog does not exist — skipped';
GO

-- ============================================================================
-- PHASE 4: Remove IpApiSync watermark
-- ============================================================================
PRINT '--- Phase 4: Remove IpApiSync watermark ---';

IF EXISTS (SELECT 1 FROM ETL.Watermark WHERE ProcessName = 'IpApiSync')
BEGIN
    DELETE FROM ETL.Watermark WHERE ProcessName = 'IpApiSync';
    PRINT '  Removed IpApiSync watermark from ETL.Watermark';
END
ELSE
    PRINT '  IpApiSync watermark does not exist — skipped';
GO

-- ============================================================================
-- PHASE 5: Replace ETL.usp_EnrichParsedGeo to use IPInfo instead of IPAPI
-- ============================================================================
PRINT '--- Phase 5: Redirect ETL geo enrichment to IPInfo ---';
GO

CREATE OR ALTER PROCEDURE ETL.usp_EnrichParsedGeo
    @BatchSize INT = 10000
AS
BEGIN
    SET NOCOUNT ON;

    -- ==========================================================================
    -- Delegates to IPInfo.usp_EnrichGeo which handles all geo enrichment
    -- from the new IPInfo range tables (replaces IPAPI.IP JOINs).
    -- ==========================================================================
    EXEC IPInfo.usp_EnrichGeo @BatchSize = @BatchSize;
END;
GO

PRINT '  Redirected ETL.usp_EnrichParsedGeo → IPInfo.usp_EnrichGeo';
PRINT '';

-- ============================================================================
-- NOTE: IPAPI.IP (344M rows) is intentionally NOT dropped.
-- Keep as historical reference until IPInfo accuracy is validated.
-- Manual drop: DROP TABLE IPAPI.IP;
-- ============================================================================

PRINT 'NOTE: IPAPI.IP table kept as historical reference.';
PRINT '      Drop manually after IPInfo validation: DROP TABLE IPAPI.IP;';
PRINT '';
PRINT '=== 71_Retire_IPAPI.sql complete ===';
