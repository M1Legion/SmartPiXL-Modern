-- ============================================================================
-- Migration 36: Rename PiXL.Pixel → PiXL.Settings
-- 
-- RATIONALE:
--   "Pixel" is the generic term. "PiXL" is our brand. Having a table called
--   PiXL.Pixel is confusing — it stores per-pixel *settings* (CompanyId, 
--   domain config, etc.), not pixel data. PiXL.Settings is self-documenting.
--
-- SAFETY: Idempotent. Checks for existence before renaming.
--         Creates a synonym PiXL.Pixel → PiXL.Settings so old queries
--         continue to work during the transition period.
-- ============================================================================

SET NOCOUNT ON;
GO

-- ── Step 1: Rename the table ─────────────────────────────────────────────────
IF OBJECT_ID('PiXL.Pixel', 'U') IS NOT NULL
   AND OBJECT_ID('PiXL.Settings', 'U') IS NULL
BEGIN
    EXEC sp_rename 'PiXL.Pixel', 'Settings', 'OBJECT';
    PRINT 'Renamed PiXL.Pixel → PiXL.Settings';
END
ELSE IF OBJECT_ID('PiXL.Settings', 'U') IS NOT NULL
    PRINT 'PiXL.Settings already exists — skipping rename.';
ELSE
    PRINT 'WARNING: PiXL.Pixel not found and PiXL.Settings does not exist!';
GO

-- ── Step 2: Create backward-compatibility synonym ────────────────────────────
-- Old queries referencing PiXL.Pixel will transparently resolve to PiXL.Settings.
-- Remove this synonym once all code and queries are updated.
IF OBJECT_ID('PiXL.Pixel', 'SN') IS NULL
   AND OBJECT_ID('PiXL.Settings', 'U') IS NOT NULL
BEGIN
    CREATE SYNONYM PiXL.Pixel FOR PiXL.Settings;
    PRINT 'Created synonym PiXL.Pixel → PiXL.Settings (backward compat)';
END
GO

-- ── Step 3: Rename any indexes that reference "Pixel" ────────────────────────
-- Common pattern: IX_Pixel_* or PK_Pixel_*
DECLARE @idxName NVARCHAR(128), @newName NVARCHAR(128), @sql NVARCHAR(500);
DECLARE idx_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT i.name
    FROM sys.indexes i
    JOIN sys.tables t ON i.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = 'PiXL' AND t.name = 'Settings'
      AND i.name LIKE '%Pixel%';

OPEN idx_cursor;
FETCH NEXT FROM idx_cursor INTO @idxName;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @newName = REPLACE(@idxName, 'Pixel', 'Settings');
    SET @sql = N'EXEC sp_rename ''PiXL.Settings.' + @idxName + N''', ''' + @newName + N''', ''INDEX'';';
    EXEC sp_executesql @sql;
    PRINT 'Renamed index: ' + @idxName + ' → ' + @newName;
    FETCH NEXT FROM idx_cursor INTO @idxName;
END
CLOSE idx_cursor;
DEALLOCATE idx_cursor;
GO

-- ── Verification ─────────────────────────────────────────────────────────────
IF OBJECT_ID('PiXL.Settings', 'U') IS NOT NULL
    PRINT '✓ PiXL.Settings table exists';
IF OBJECT_ID('PiXL.Pixel', 'SN') IS NOT NULL
    PRINT '✓ PiXL.Pixel synonym exists (backward compat)';

SELECT 'PiXL.Settings columns' AS [Check], c.name, t.name AS [type], c.max_length
FROM sys.columns c
JOIN sys.types t ON c.system_type_id = t.system_type_id AND c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID('PiXL.Settings')
ORDER BY c.column_id;
GO
