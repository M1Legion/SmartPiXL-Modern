-- ===========================================================================
-- 17B_CreateSchemas.sql — Phase 0: Schema Creation & Table Migration
-- Moves existing objects from dbo to PiXL/ETL schemas
-- Date: 2026-02-13
--
-- MAPPING:
--   dbo.PiXL_Test       → PiXL.Test
--   dbo.PiXL_Parsed     → PiXL.Parsed
--   dbo.PiXL_Config     → PiXL.Config
--   dbo.PiXL_Device     → PiXL.Device
--   dbo.Company          → PiXL.Company
--   dbo.PiXL (table)    → PiXL.Pixel
--   dbo.ETL_Watermark   → ETL.Watermark
--   dbo.usp_ParseNewHits → ETL.usp_ParseNewHits
--
-- STAYS IN dbo: GetQueryParam, GetDeviceType, CountListItems,
--               sp_MaterializePiXLData, Company_History, Company_Old,
--               PiXL_History, PiXL_old, PiXL_Materialized, all vw_* views
-- ===========================================================================
USE SmartPiXL;
GO

PRINT '========================================';
PRINT 'Phase 0: Schema Creation & Table Migration';
PRINT '========================================';
GO

-- =============================================
-- Step 0.1: Create Schemas
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'PiXL')
    EXEC('CREATE SCHEMA PiXL AUTHORIZATION dbo');
PRINT 'Created schema: PiXL';
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'ETL')
    EXEC('CREATE SCHEMA ETL AUTHORIZATION dbo');
PRINT 'Created schema: ETL';
GO

-- =============================================
-- Step 0.2: Transfer Objects to New Schemas
-- (metadata-only, instant — no data movement)
-- =============================================

-- PiXL schema: all pixel/tracking domain objects
ALTER SCHEMA PiXL TRANSFER dbo.PiXL_Test;
PRINT 'Transferred: dbo.PiXL_Test → PiXL.PiXL_Test';

ALTER SCHEMA PiXL TRANSFER dbo.PiXL_Parsed;
PRINT 'Transferred: dbo.PiXL_Parsed → PiXL.PiXL_Parsed';

ALTER SCHEMA PiXL TRANSFER dbo.PiXL_Config;
PRINT 'Transferred: dbo.PiXL_Config → PiXL.PiXL_Config';

ALTER SCHEMA PiXL TRANSFER dbo.PiXL_Device;
PRINT 'Transferred: dbo.PiXL_Device → PiXL.PiXL_Device';

ALTER SCHEMA PiXL TRANSFER dbo.Company;
PRINT 'Transferred: dbo.Company → PiXL.Company';

ALTER SCHEMA PiXL TRANSFER dbo.PiXL;
PRINT 'Transferred: dbo.PiXL → PiXL.PiXL';
GO

-- ETL schema: pipeline infrastructure
ALTER SCHEMA ETL TRANSFER dbo.ETL_Watermark;
PRINT 'Transferred: dbo.ETL_Watermark → ETL.ETL_Watermark';

ALTER SCHEMA ETL TRANSFER dbo.usp_ParseNewHits;
PRINT 'Transferred: dbo.usp_ParseNewHits → ETL.usp_ParseNewHits';
GO

-- =============================================
-- Step 0.3: Rename Objects to Strip Redundant Prefixes
-- (ALTER SCHEMA TRANSFER keeps original object name)
-- =============================================

EXEC sp_rename 'PiXL.PiXL_Test',    'Test',      'OBJECT';
EXEC sp_rename 'PiXL.PiXL_Parsed',  'Parsed',    'OBJECT';
EXEC sp_rename 'PiXL.PiXL_Config',  'Config',    'OBJECT';
EXEC sp_rename 'PiXL.PiXL_Device',  'Device',    'OBJECT';
EXEC sp_rename 'PiXL.PiXL',         'Pixel',     'OBJECT';
EXEC sp_rename 'ETL.ETL_Watermark',  'Watermark', 'OBJECT';
-- PiXL.Company — no prefix to strip
-- ETL.usp_ParseNewHits — no ETL_ prefix to strip
PRINT 'Renamed all objects to strip redundant prefixes';
GO

-- =============================================
-- Step 0.4: Fix All Views (dynamic SQL)
-- Replace old dbo-qualified table references with new schema names
-- =============================================
PRINT '';
PRINT '--- Updating views ---';

DECLARE @viewName NVARCHAR(128);
DECLARE @def NVARCHAR(MAX);

DECLARE view_cursor CURSOR FOR
SELECT name FROM sys.views WHERE schema_id = SCHEMA_ID('dbo')
ORDER BY name;

OPEN view_cursor;
FETCH NEXT FROM view_cursor INTO @viewName;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @def = OBJECT_DEFINITION(OBJECT_ID('dbo.' + @viewName));
    
    IF @def IS NOT NULL
    BEGIN
        -- Replace fully-qualified table references
        SET @def = REPLACE(@def, 'dbo.PiXL_Parsed',  'PiXL.Parsed');
        SET @def = REPLACE(@def, 'dbo.PiXL_Test',    'PiXL.Test');
        SET @def = REPLACE(@def, 'dbo.ETL_Watermark', 'ETL.Watermark');
        SET @def = REPLACE(@def, 'dbo.PiXL_Config',  'PiXL.Config');
        SET @def = REPLACE(@def, 'dbo.PiXL_Device',  'PiXL.Device');

        -- Handle INFORMATION_SCHEMA queries that may filter by old table name
        -- (e.g., vw_PiXL_ColumnMap)
        SET @def = REPLACE(@def, '''PiXL_Parsed''',  '''Parsed''');
        SET @def = REPLACE(@def, '''PiXL_Test''',    '''Test''');

        -- Replace CREATE VIEW variants with ALTER VIEW
        SET @def = REPLACE(@def, 'CREATE   VIEW', 'ALTER VIEW');
        SET @def = REPLACE(@def, 'CREATE VIEW',   'ALTER VIEW');
        
        BEGIN TRY
            EXEC sp_executesql @def;
            PRINT '  OK: ' + @viewName;
        END TRY
        BEGIN CATCH
            PRINT '  FAILED: ' + @viewName + ' — ' + ERROR_MESSAGE();
        END CATCH
    END
    
    FETCH NEXT FROM view_cursor INTO @viewName;
END

CLOSE view_cursor;
DEALLOCATE view_cursor;
GO

-- =============================================
-- Step 0.5: Fix usp_ParseNewHits Stored Procedure
-- Replace table references in proc body
-- =============================================
PRINT '';
PRINT '--- Updating ETL.usp_ParseNewHits ---';

DECLARE @procDef NVARCHAR(MAX);
SET @procDef = OBJECT_DEFINITION(OBJECT_ID('ETL.usp_ParseNewHits'));

IF @procDef IS NOT NULL
BEGIN
    -- Replace table references in body
    SET @procDef = REPLACE(@procDef, 'dbo.PiXL_Parsed',  'PiXL.Parsed');
    SET @procDef = REPLACE(@procDef, 'dbo.PiXL_Test',    'PiXL.Test');
    SET @procDef = REPLACE(@procDef, 'dbo.ETL_Watermark', 'ETL.Watermark');

    -- Replace CREATE PROCEDURE with ALTER PROCEDURE + correct schema
    -- After TRANSFER, SQL Server may store either [dbo] or [ETL] in the header
    SET @procDef = REPLACE(@procDef, 'CREATE   PROCEDURE [dbo].[usp_ParseNewHits]', 'ALTER PROCEDURE [ETL].[usp_ParseNewHits]');
    SET @procDef = REPLACE(@procDef, 'CREATE PROCEDURE [dbo].[usp_ParseNewHits]',   'ALTER PROCEDURE [ETL].[usp_ParseNewHits]');
    SET @procDef = REPLACE(@procDef, 'CREATE   PROCEDURE [ETL].[usp_ParseNewHits]', 'ALTER PROCEDURE [ETL].[usp_ParseNewHits]');
    SET @procDef = REPLACE(@procDef, 'CREATE PROCEDURE [ETL].[usp_ParseNewHits]',   'ALTER PROCEDURE [ETL].[usp_ParseNewHits]');

    BEGIN TRY
        EXEC sp_executesql @procDef;
        PRINT '  OK: ETL.usp_ParseNewHits';
    END TRY
    BEGIN CATCH
        PRINT '  FAILED: ETL.usp_ParseNewHits — ' + ERROR_MESSAGE();
    END CATCH
END
ELSE
    PRINT '  WARNING: Could not read proc definition';
GO

-- =============================================
-- Step 0.6: Fix sp_MaterializePiXLData if it references old names
-- =============================================
IF OBJECT_ID('dbo.sp_MaterializePiXLData') IS NOT NULL
BEGIN
    PRINT '';
    PRINT '--- Updating sp_MaterializePiXLData ---';

    DECLARE @matDef NVARCHAR(MAX);
    SET @matDef = OBJECT_DEFINITION(OBJECT_ID('dbo.sp_MaterializePiXLData'));
    
    IF @matDef LIKE '%PiXL_Parsed%' OR @matDef LIKE '%PiXL_Test%' OR @matDef LIKE '%ETL_Watermark%'
    BEGIN
        SET @matDef = REPLACE(@matDef, 'dbo.PiXL_Parsed',  'PiXL.Parsed');
        SET @matDef = REPLACE(@matDef, 'dbo.PiXL_Test',    'PiXL.Test');
        SET @matDef = REPLACE(@matDef, 'dbo.ETL_Watermark', 'ETL.Watermark');
        SET @matDef = REPLACE(@matDef, 'CREATE   PROCEDURE', 'ALTER PROCEDURE');
        SET @matDef = REPLACE(@matDef, 'CREATE PROCEDURE',   'ALTER PROCEDURE');
        
        BEGIN TRY
            EXEC sp_executesql @matDef;
            PRINT '  OK: sp_MaterializePiXLData';
        END TRY
        BEGIN CATCH
            PRINT '  FAILED: sp_MaterializePiXLData — ' + ERROR_MESSAGE();
        END CATCH
    END
    ELSE
        PRINT '  SKIP: sp_MaterializePiXLData (no old references found)';
END
GO

-- =============================================
-- VALIDATION
-- =============================================
PRINT '';
PRINT '========================================';
PRINT 'VALIDATION';
PRINT '========================================';

-- Check schemas exist
IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'PiXL') PRINT 'OK: PiXL schema exists';
ELSE PRINT 'FAIL: PiXL schema missing!';

IF EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'ETL') PRINT 'OK: ETL schema exists';
ELSE PRINT 'FAIL: ETL schema missing!';

-- Check tables are in correct schemas
IF OBJECT_ID('PiXL.Test')      IS NOT NULL PRINT 'OK: PiXL.Test exists';       ELSE PRINT 'FAIL: PiXL.Test missing!';
IF OBJECT_ID('PiXL.Parsed')    IS NOT NULL PRINT 'OK: PiXL.Parsed exists';     ELSE PRINT 'FAIL: PiXL.Parsed missing!';
IF OBJECT_ID('PiXL.Config')    IS NOT NULL PRINT 'OK: PiXL.Config exists';     ELSE PRINT 'FAIL: PiXL.Config missing!';
IF OBJECT_ID('PiXL.Device')    IS NOT NULL PRINT 'OK: PiXL.Device exists';     ELSE PRINT 'FAIL: PiXL.Device missing!';
IF OBJECT_ID('PiXL.Company')   IS NOT NULL PRINT 'OK: PiXL.Company exists';    ELSE PRINT 'FAIL: PiXL.Company missing!';
IF OBJECT_ID('PiXL.Pixel')     IS NOT NULL PRINT 'OK: PiXL.Pixel exists';      ELSE PRINT 'FAIL: PiXL.Pixel missing!';
IF OBJECT_ID('ETL.Watermark')  IS NOT NULL PRINT 'OK: ETL.Watermark exists';   ELSE PRINT 'FAIL: ETL.Watermark missing!';
IF OBJECT_ID('ETL.usp_ParseNewHits') IS NOT NULL PRINT 'OK: ETL.usp_ParseNewHits exists'; ELSE PRINT 'FAIL: ETL.usp_ParseNewHits missing!';

-- Verify old names are gone
IF OBJECT_ID('dbo.PiXL_Test')       IS NULL PRINT 'OK: dbo.PiXL_Test gone';       ELSE PRINT 'WARN: dbo.PiXL_Test still exists!';
IF OBJECT_ID('dbo.PiXL_Parsed')     IS NULL PRINT 'OK: dbo.PiXL_Parsed gone';     ELSE PRINT 'WARN: dbo.PiXL_Parsed still exists!';
IF OBJECT_ID('dbo.ETL_Watermark')   IS NULL PRINT 'OK: dbo.ETL_Watermark gone';   ELSE PRINT 'WARN: dbo.ETL_Watermark still exists!';
IF OBJECT_ID('dbo.usp_ParseNewHits') IS NULL PRINT 'OK: dbo.usp_ParseNewHits gone'; ELSE PRINT 'WARN: dbo.usp_ParseNewHits still exists!';
IF OBJECT_ID('dbo.Company')         IS NULL PRINT 'OK: dbo.Company gone';         ELSE PRINT 'WARN: dbo.Company still exists!';

-- Utility functions still in dbo
IF OBJECT_ID('dbo.GetQueryParam')    IS NOT NULL PRINT 'OK: dbo.GetQueryParam still in dbo'; ELSE PRINT 'FAIL: GetQueryParam missing!';
IF OBJECT_ID('dbo.GetDeviceType')    IS NOT NULL PRINT 'OK: dbo.GetDeviceType still in dbo';  ELSE PRINT 'FAIL: GetDeviceType missing!';
IF OBJECT_ID('dbo.CountListItems')   IS NOT NULL PRINT 'OK: dbo.CountListItems still in dbo'; ELSE PRINT 'FAIL: CountListItems missing!';
GO

-- Data integrity checks
PRINT '';
PRINT '--- Data Integrity ---';

SELECT 'PiXL.Test' AS [Table], COUNT(*) AS [Rows] FROM PiXL.Test
UNION ALL SELECT 'PiXL.Parsed',  COUNT(*) FROM PiXL.Parsed
UNION ALL SELECT 'ETL.Watermark', COUNT(*) FROM ETL.Watermark
UNION ALL SELECT 'PiXL.Company', COUNT(*) FROM PiXL.Company
UNION ALL SELECT 'PiXL.Pixel',   COUNT(*) FROM PiXL.Pixel
UNION ALL SELECT 'PiXL.Device',  COUNT(*) FROM PiXL.Device
UNION ALL SELECT 'PiXL.Config',  COUNT(*) FROM PiXL.Config;
GO

-- Quick smoke test: run the ETL proc in dry-run mode
PRINT '';
PRINT '--- ETL Proc Smoke Test ---';
BEGIN TRY
    EXEC ETL.usp_ParseNewHits @BatchSize = 1;
    PRINT 'OK: ETL.usp_ParseNewHits executed successfully';
END TRY
BEGIN CATCH
    PRINT 'FAIL: ETL.usp_ParseNewHits — ' + ERROR_MESSAGE();
END CATCH
GO

PRINT '';
PRINT 'Phase 0 migration complete.';
GO
