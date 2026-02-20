/*
    28_RenameTestToRaw_Partition.sql
    =================================
    Renames PiXL.Test → PiXL.Raw and adds monthly date partitioning with
    tiered compression:
      - Records < 3 months old  → NONE (max write throughput for SqlBulkCopy)
      - Records 3–6 months old  → ROW compression (~30% savings, minimal CPU)
      - Records > 6 months old  → PAGE compression (~50–60% savings)

    PiXL.Raw is the permanent raw ingest table — data is NEVER deleted.

    CHANGES:
      1. Rename PiXL.Test → PiXL.Raw (sp_rename, metadata-only)
      2. Upgrade Id column from INT → BIGINT IDENTITY (future-proofing)
      3. Create partition function + scheme on ReceivedAt (monthly boundaries)
      4. Rebuild clustered index on the partition scheme
      5. Create maintenance proc ETL.usp_ManageRawCompression to adjust
         compression tiers monthly
      6. Update all views/procs that reference PiXL.Test
      7. Update ETL.usp_PurgeRawData → safe no-op (PiXL.Raw is never purged)

    PREREQUISITES:
      - SQL/17B_CreateSchemas.sql (PiXL schema exists, PiXL.Test exists)
      - SQL/20_ETLPhases9to13.sql (ETL.usp_ParseNewHits)
      - SQL/05_MaintenanceProcedures.sql (ETL.usp_PurgeRawData)

    TARGET: SQL Server 2025 (17.0.1050.2)
    Run on: SmartPiXL database, localhost\SQL2025
    Date:   2026-02-15
*/

USE SmartPiXL;
GO

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- =========================================================================
-- 0. DATABASE-LEVEL SETTING: QUOTED_IDENTIFIER ON
-- =========================================================================
-- Required because PiXL.Parsed has a filtered index (IX_Parsed_CanvasFP)
-- which mandates QUOTED_IDENTIFIER ON for all DML. Setting it at the DB
-- level ensures all connections (ADO.NET, sqlcmd, SSMS) default to ON.
IF (SELECT is_quoted_identifier_on FROM sys.databases WHERE name = DB_NAME()) = 0
BEGIN
    DECLARE @dbName NVARCHAR(128) = DB_NAME();
    EXEC('ALTER DATABASE [' + @dbName + '] SET QUOTED_IDENTIFIER ON');
    PRINT 'Set database QUOTED_IDENTIFIER = ON.';
END
ELSE
    PRINT 'Database QUOTED_IDENTIFIER already ON.';
GO

-- =========================================================================
-- 1. RENAME PiXL.Test → PiXL.Raw
-- =========================================================================
-- sp_rename is metadata-only — instant regardless of table size.
IF OBJECT_ID('PiXL.Test') IS NOT NULL AND OBJECT_ID('PiXL.Raw') IS NULL
BEGIN
    EXEC sp_rename 'PiXL.Test', 'Raw', 'OBJECT';
    PRINT 'Renamed PiXL.Test → PiXL.Raw';
END
ELSE IF OBJECT_ID('PiXL.Raw') IS NOT NULL
    PRINT 'PiXL.Raw already exists — skipping rename.';
ELSE
    PRINT 'ERROR: PiXL.Test does not exist!';
GO


-- =========================================================================
-- 2. UPGRADE Id: INT → BIGINT (while table is small)
-- =========================================================================
-- INT maxes at ~2.1B. At scale (10K hits/day = 3.6M/yr), we'd hit it in
-- ~580 years, but BIGINT is cheap insurance and matches the documentation.
-- ALTER COLUMN on a small table (~1K rows) is instant.
IF EXISTS (
    SELECT 1 FROM sys.columns c
    WHERE c.object_id = OBJECT_ID('PiXL.Raw')
      AND c.name = 'Id'
      AND TYPE_NAME(c.system_type_id) = 'int'
)
BEGIN
    -- Drop the FK from PiXL.Parsed.SourceId → PiXL.Raw.Id first
    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PiXL_Parsed_Source')
    BEGIN
        ALTER TABLE PiXL.Parsed DROP CONSTRAINT FK_PiXL_Parsed_Source;
        PRINT 'Dropped FK_PiXL_Parsed_Source (will re-add after type upgrade).';
    END

    -- Drop the old PK constraint first (has auto-generated name)
    DECLARE @pkName NVARCHAR(128);
    SELECT @pkName = i.name
    FROM sys.indexes i
    WHERE i.object_id = OBJECT_ID('PiXL.Raw')
      AND i.is_primary_key = 1;

    IF @pkName IS NOT NULL
    BEGIN
        EXEC('ALTER TABLE PiXL.Raw DROP CONSTRAINT [' + @pkName + ']');
        PRINT 'Dropped old PK: ' + @pkName;
    END

    -- Change INT → BIGINT
    ALTER TABLE PiXL.[Raw] ALTER COLUMN Id BIGINT NOT NULL;
    PRINT 'Upgraded PiXL.Raw.Id from INT to BIGINT.';

    -- Also upgrade PiXL.Parsed.SourceId to match (drop/recreate dependent indexes)
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('PiXL.Parsed') AND name = 'CIX_PiXL_Parsed_ReceivedAt')
        DROP INDEX CIX_PiXL_Parsed_ReceivedAt ON PiXL.Parsed;
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('PiXL.Parsed') AND name = 'PK_PiXL_Parsed')
        ALTER TABLE PiXL.Parsed DROP CONSTRAINT PK_PiXL_Parsed;

    ALTER TABLE PiXL.Parsed ALTER COLUMN SourceId BIGINT NOT NULL;
    PRINT 'Upgraded PiXL.Parsed.SourceId from INT to BIGINT.';

    CREATE UNIQUE CLUSTERED INDEX CIX_PiXL_Parsed_ReceivedAt ON PiXL.Parsed (ReceivedAt, SourceId);
    ALTER TABLE PiXL.Parsed ADD CONSTRAINT PK_PiXL_Parsed PRIMARY KEY NONCLUSTERED (SourceId);
    PRINT 'Recreated PiXL.Parsed indexes with BIGINT SourceId.';

    -- Re-add PK (will be replaced by partitioned index below)
    ALTER TABLE PiXL.[Raw] ADD CONSTRAINT PK_Raw_Id PRIMARY KEY CLUSTERED (Id);
    PRINT 'Re-added PK_Raw_Id (temporary — will be replaced by partitioned index).';
END
ELSE
    PRINT 'PiXL.Raw.Id is already BIGINT (or table missing) — skipping.';
GO


-- =========================================================================
-- 3. RESEED IDENTITY to BIGINT
-- =========================================================================
-- The IDENTITY property can't change type via ALTER COLUMN, but since we
-- already have data, we just need the column to BE BIGINT. The IDENTITY
-- seed/increment is preserved by ALTER COLUMN. Verify:
IF OBJECT_ID('PiXL.Raw') IS NOT NULL
BEGIN
    DECLARE @maxId BIGINT = (SELECT ISNULL(MAX(Id), 0) FROM PiXL.[Raw]);
    DBCC CHECKIDENT ('PiXL.Raw', RESEED, @maxId);
    PRINT 'Reseeded PiXL.Raw identity to ' + CAST(@maxId AS VARCHAR(20));
END
GO


-- =========================================================================
-- 4. PARTITION FUNCTION + SCHEME (monthly boundaries)
-- =========================================================================
-- Create monthly boundaries from 2026-01-01 through 2028-12-01 (3 years).
-- ETL.usp_ManageRawCompression extends these automatically.
-- All partitions go to PRIMARY filegroup (single-filegroup deployment).

IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = 'pf_Raw_Monthly')
BEGIN
    CREATE PARTITION FUNCTION pf_Raw_Monthly (DATETIME2(7))
    AS RANGE RIGHT FOR VALUES (
        '2026-01-01', '2026-02-01', '2026-03-01', '2026-04-01',
        '2026-05-01', '2026-06-01', '2026-07-01', '2026-08-01',
        '2026-09-01', '2026-10-01', '2026-11-01', '2026-12-01',
        '2027-01-01', '2027-02-01', '2027-03-01', '2027-04-01',
        '2027-05-01', '2027-06-01', '2027-07-01', '2027-08-01',
        '2027-09-01', '2027-10-01', '2027-11-01', '2027-12-01',
        '2028-01-01', '2028-02-01', '2028-03-01', '2028-04-01',
        '2028-05-01', '2028-06-01', '2028-07-01', '2028-08-01',
        '2028-09-01', '2028-10-01', '2028-11-01', '2028-12-01'
    );
    PRINT 'Created partition function pf_Raw_Monthly (2026-01 through 2028-12).';
END
ELSE
    PRINT 'Partition function pf_Raw_Monthly already exists.';
GO

IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = 'ps_Raw_Monthly')
BEGIN
    -- All partitions on PRIMARY filegroup (single-filegroup deployment).
    -- COUNT: 36 boundaries = 37 partitions. ALL TO maps all to the same FG.
    CREATE PARTITION SCHEME ps_Raw_Monthly
    AS PARTITION pf_Raw_Monthly
    ALL TO ([PRIMARY]);
    PRINT 'Created partition scheme ps_Raw_Monthly (all PRIMARY).';
END
ELSE
    PRINT 'Partition scheme ps_Raw_Monthly already exists.';
GO


-- =========================================================================
-- 5. REBUILD CLUSTERED INDEX ON PARTITION SCHEME
-- =========================================================================
-- Must drop the existing PK (non-partitioned) and create a new one that
-- includes ReceivedAt (required for partition alignment).
-- NOTE: A partitioned PK must include the partitioning column.
IF OBJECT_ID('PiXL.Raw') IS NOT NULL
BEGIN
    -- Drop existing PK if present
    DECLARE @pkName2 NVARCHAR(128);
    SELECT @pkName2 = i.name FROM sys.indexes i
    WHERE i.object_id = OBJECT_ID('PiXL.Raw') AND i.is_primary_key = 1;

    IF @pkName2 IS NOT NULL
    BEGIN
        EXEC('ALTER TABLE PiXL.[Raw] DROP CONSTRAINT [' + @pkName2 + ']');
        PRINT 'Dropped PK for partition rebuild: ' + @pkName2;
    END

    -- Create partitioned clustered index on (ReceivedAt, Id).
    -- ReceivedAt first for partition elimination on time-range queries.
    -- Id second for uniqueness within each month partition.
    -- NOT a PK constraint — IDENTITY guarantees uniqueness, and the ETL
    -- watermark queries by Id range which will use the nonclustered IX below.
    CREATE CLUSTERED INDEX CIX_Raw_ReceivedAt
        ON PiXL.[Raw] (ReceivedAt, Id)
        ON ps_Raw_Monthly (ReceivedAt);
    PRINT 'Created partitioned clustered index CIX_Raw_ReceivedAt.';

    -- Nonclustered unique index on Id for watermark-based ETL lookups + FK ref.
    -- The ETL proc does WHERE Id > @LastId AND Id <= @MaxId — this index
    -- serves those seeks without scanning the full clustered index.
    -- Non-aligned (on PRIMARY, not on partition scheme) because a unique index
    -- on a partitioned table requires the partition column in its key, but Id
    -- alone guarantees uniqueness via IDENTITY.
    CREATE UNIQUE NONCLUSTERED INDEX UIX_Raw_Id
        ON PiXL.[Raw] (Id)
        ON [PRIMARY];
    PRINT 'Created unique nonclustered index UIX_Raw_Id (non-aligned).';

    -- Re-add FK from PiXL.Parsed.SourceId → PiXL.Raw.Id (dropped in step 2)
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PiXL_Parsed_Source')
    BEGIN
        ALTER TABLE PiXL.Parsed
            ADD CONSTRAINT FK_PiXL_Parsed_Source
            FOREIGN KEY (SourceId) REFERENCES PiXL.[Raw] (Id);
        PRINT 'Re-added FK_PiXL_Parsed_Source → PiXL.Raw.Id.';
    END
END
GO


-- =========================================================================
-- 6. COMPRESSION TIER MAINTENANCE PROC
-- =========================================================================
-- Adjusts compression on a per-partition basis:
--   Partitions > 6 months old → PAGE compression
--   Partitions 3–6 months old → ROW compression
--   Recent partitions (< 3 months) → NONE
-- Run monthly via SQL Agent or Task Scheduler.
CREATE OR ALTER PROCEDURE [ETL].[usp_ManageRawCompression]
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @now         DATETIME2 = SYSUTCDATETIME();
    DECLARE @cutoff6mo   DATETIME2 = DATEADD(MONTH, -6, @now);
    DECLARE @cutoff3mo   DATETIME2 = DATEADD(MONTH, -3, @now);
    DECLARE @partNum     INT;
    DECLARE @upperBound  SQL_VARIANT;
    DECLARE @boundaryDt  DATETIME2;
    DECLARE @currentComp TINYINT;    -- 0=NONE, 1=ROW, 2=PAGE
    DECLARE @targetComp  TINYINT;
    DECLARE @sql         NVARCHAR(200);
    DECLARE @changes     INT = 0;

    -- Cursor over all partitions of the clustered index
    DECLARE part_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT p.partition_number, p.data_compression
        FROM sys.partitions p
        WHERE p.object_id = OBJECT_ID('PiXL.Raw')
          AND p.index_id = 1   -- Clustered index
        ORDER BY p.partition_number;

    OPEN part_cursor;
    FETCH NEXT FROM part_cursor INTO @partNum, @currentComp;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- Get the UPPER boundary of this partition (= next boundary value).
        -- For RANGE RIGHT, partition N holds values >= boundary[N-1] AND < boundary[N].
        -- The last partition has no upper boundary (holds all future dates).
        SET @upperBound = NULL;
        SELECT @upperBound = prv.value
        FROM sys.partition_range_values prv
        JOIN sys.partition_functions pf ON prv.function_id = pf.function_id
        WHERE pf.name = 'pf_Raw_Monthly'
          AND prv.boundary_id = @partNum;

        IF @upperBound IS NULL
        BEGIN
            -- This is the last partition (or beyond all boundaries) — always NONE
            SET @targetComp = 0;
        END
        ELSE
        BEGIN
            SET @boundaryDt = CAST(@upperBound AS DATETIME2);

            -- If the upper boundary is older than 6 months → PAGE
            -- If older than 3 months → ROW
            -- Otherwise → NONE
            SET @targetComp = CASE
                WHEN @boundaryDt <= @cutoff6mo THEN 2  -- PAGE
                WHEN @boundaryDt <= @cutoff3mo THEN 1  -- ROW
                ELSE 0                                  -- NONE
            END;
        END

        -- Only rebuild if the current compression doesn't match target
        IF @currentComp <> @targetComp
        BEGIN
            SET @sql = N'ALTER INDEX CIX_Raw_ReceivedAt ON PiXL.[Raw] REBUILD PARTITION = '
                + CAST(@partNum AS NVARCHAR(10))
                + N' WITH (DATA_COMPRESSION = '
                + CASE @targetComp WHEN 2 THEN N'PAGE' WHEN 1 THEN N'ROW' ELSE N'NONE' END
                + N')';

            BEGIN TRY
                EXEC sp_executesql @sql;
                SET @changes = @changes + 1;
                PRINT 'Partition ' + CAST(@partNum AS VARCHAR(5))
                    + ': ' + CASE @currentComp WHEN 0 THEN 'NONE' WHEN 1 THEN 'ROW' WHEN 2 THEN 'PAGE' ELSE '?' END
                    + ' → ' + CASE @targetComp WHEN 0 THEN 'NONE' WHEN 1 THEN 'ROW' WHEN 2 THEN 'PAGE' ELSE '?' END;
            END TRY
            BEGIN CATCH
                PRINT 'ERROR on partition ' + CAST(@partNum AS VARCHAR(5)) + ': ' + ERROR_MESSAGE();
            END CATCH
        END

        FETCH NEXT FROM part_cursor INTO @partNum, @currentComp;
    END

    CLOSE part_cursor;
    DEALLOCATE part_cursor;

    -- Also extend the partition function if we're within 6 months of the last boundary.
    DECLARE @lastBoundary DATETIME2 = (
        SELECT MAX(CAST(prv.value AS DATETIME2))
        FROM sys.partition_range_values prv
        JOIN sys.partition_functions pf ON prv.function_id = pf.function_id
        WHERE pf.name = 'pf_Raw_Monthly'
    );

    IF @lastBoundary IS NOT NULL AND @lastBoundary < DATEADD(MONTH, 6, @now)
    BEGIN
        DECLARE @nextMonth DATETIME2 = DATEADD(MONTH, 1, @lastBoundary);
        DECLARE @extendCount INT = 0;

        WHILE @nextMonth <= DATEADD(MONTH, 12, @now)
        BEGIN
            -- Mark next filegroup for the scheme before splitting
            ALTER PARTITION SCHEME ps_Raw_Monthly NEXT USED [PRIMARY];
            ALTER PARTITION FUNCTION pf_Raw_Monthly() SPLIT RANGE (@nextMonth);
            SET @extendCount = @extendCount + 1;
            SET @nextMonth = DATEADD(MONTH, 1, @nextMonth);
        END

        IF @extendCount > 0
            PRINT 'Extended partition function by ' + CAST(@extendCount AS VARCHAR(5)) + ' months.';
    END

    SELECT @changes AS PartitionsChanged;
END;
GO

PRINT 'Created ETL.usp_ManageRawCompression (monthly compression tier management).';
GO


-- =========================================================================
-- 7. RUN INITIAL COMPRESSION PASS
-- =========================================================================
-- All existing data is from 2026-02 — should be NONE (< 3 months old).
EXEC ETL.usp_ManageRawCompression;
GO


-- =========================================================================
-- 8. UPDATE ALL VIEWS THAT REFERENCE PiXL.Test → PiXL.Raw
-- =========================================================================
PRINT '';
PRINT '--- Updating views: PiXL.Test → PiXL.Raw ---';

DECLARE @viewName NVARCHAR(128);
DECLARE @def NVARCHAR(MAX);
DECLARE @schema NVARCHAR(128);

DECLARE view_cursor CURSOR FOR
SELECT s.name, v.name
FROM sys.views v
JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE OBJECT_DEFINITION(v.object_id) LIKE '%PiXL.Test%'
   OR OBJECT_DEFINITION(v.object_id) LIKE '%PiXL].[Test]%'
ORDER BY s.name, v.name;

OPEN view_cursor;
FETCH NEXT FROM view_cursor INTO @schema, @viewName;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @def = OBJECT_DEFINITION(OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@viewName)));
    IF @def IS NOT NULL
    BEGIN
        SET @def = REPLACE(@def, 'PiXL.Test',      'PiXL.Raw');
        SET @def = REPLACE(@def, '[PiXL].[Test]',   '[PiXL].[Raw]');
        -- Handle quoted variants
        SET @def = REPLACE(@def, '''Test''',         '''Raw''');

        -- Convert CREATE → ALTER
        SET @def = REPLACE(@def, 'CREATE   VIEW', 'ALTER VIEW');
        SET @def = REPLACE(@def, 'CREATE VIEW',   'ALTER VIEW');

        BEGIN TRY
            EXEC sp_executesql @def;
            PRINT '  OK: ' + @schema + '.' + @viewName;
        END TRY
        BEGIN CATCH
            PRINT '  FAILED: ' + @schema + '.' + @viewName + ' — ' + ERROR_MESSAGE();
        END CATCH
    END
    FETCH NEXT FROM view_cursor INTO @schema, @viewName;
END

CLOSE view_cursor;
DEALLOCATE view_cursor;
GO


-- =========================================================================
-- 9. UPDATE ALL PROCS THAT REFERENCE PiXL.Test → PiXL.Raw
-- =========================================================================
PRINT '';
PRINT '--- Updating stored procedures: PiXL.Test → PiXL.Raw ---';

DECLARE @procSchema NVARCHAR(128);
DECLARE @procName   NVARCHAR(128);
DECLARE @procDef    NVARCHAR(MAX);

DECLARE proc_cursor CURSOR FOR
SELECT s.name, p.name
FROM sys.procedures p
JOIN sys.schemas s ON p.schema_id = s.schema_id
WHERE OBJECT_DEFINITION(p.object_id) LIKE '%PiXL.Test%'
   OR OBJECT_DEFINITION(p.object_id) LIKE '%PiXL].[Test]%'
ORDER BY s.name, p.name;

OPEN proc_cursor;
FETCH NEXT FROM proc_cursor INTO @procSchema, @procName;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @procDef = OBJECT_DEFINITION(OBJECT_ID(QUOTENAME(@procSchema) + '.' + QUOTENAME(@procName)));
    IF @procDef IS NOT NULL
    BEGIN
        SET @procDef = REPLACE(@procDef, 'PiXL.Test',      'PiXL.Raw');
        SET @procDef = REPLACE(@procDef, '[PiXL].[Test]',   '[PiXL].[Raw]');

        -- Convert CREATE → ALTER (handle spacing variants)
        SET @procDef = REPLACE(@procDef, 'CREATE   PROCEDURE', 'ALTER PROCEDURE');
        SET @procDef = REPLACE(@procDef, 'CREATE PROCEDURE',   'ALTER PROCEDURE');
        SET @procDef = REPLACE(@procDef, 'CREATE   OR ALTER PROCEDURE', 'ALTER PROCEDURE');
        SET @procDef = REPLACE(@procDef, 'CREATE OR ALTER PROCEDURE',   'ALTER PROCEDURE');

        BEGIN TRY
            EXEC sp_executesql @procDef;
            PRINT '  OK: ' + @procSchema + '.' + @procName;
        END TRY
        BEGIN CATCH
            PRINT '  FAILED: ' + @procSchema + '.' + @procName + ' — ' + ERROR_MESSAGE();
        END CATCH
    END
    FETCH NEXT FROM proc_cursor INTO @procSchema, @procName;
END

CLOSE proc_cursor;
DEALLOCATE proc_cursor;
GO


-- =========================================================================
-- 10. REPLACE usp_PurgeRawData WITH A SAFE NO-OP
-- =========================================================================
-- PiXL.Raw is never purged. Replace the proc with a no-op that logs intent.
CREATE OR ALTER PROCEDURE [ETL].[usp_PurgeRawData]
    @DaysToKeep     INT = 7,
    @BatchSize      INT = 5000,
    @MaxBatches     INT = 200
AS
BEGIN
    SET NOCOUNT ON;
    -- PiXL.Raw is the permanent raw ingest table. Data is NEVER deleted.
    -- This proc is retained as a stub for backward compatibility with any
    -- scheduled jobs that still call it. It does nothing.
    PRINT 'PiXL.Raw is permanent storage — purge is disabled.';
    SELECT 0 AS RecordsPurged, 'Disabled' AS [Status];
END;
GO

PRINT 'ETL.usp_PurgeRawData replaced with safe no-op (PiXL.Raw is never purged).';
GO


-- =========================================================================
-- VALIDATION
-- =========================================================================
PRINT '';
PRINT '========================================';
PRINT 'VALIDATION';
PRINT '========================================';

-- Table exists with new name
IF OBJECT_ID('PiXL.Raw') IS NOT NULL PRINT 'OK: PiXL.Raw exists';
ELSE PRINT 'FAIL: PiXL.Raw missing!';

IF OBJECT_ID('PiXL.Test') IS NULL PRINT 'OK: PiXL.Test gone (renamed)';
ELSE PRINT 'WARN: PiXL.Test still exists!';

-- Partitioned?
SELECT
    'PiXL.Raw partitions' AS [Check],
    COUNT(*) AS PartitionCount,
    SUM(p.rows) AS TotalRows
FROM sys.partitions p
WHERE p.object_id = OBJECT_ID('PiXL.Raw') AND p.index_id = 1;

-- Compression status
SELECT
    p.partition_number,
    p.rows,
    p.data_compression_desc AS Compression,
    CAST(prv.value AS DATETIME2) AS UpperBoundary
FROM sys.partitions p
LEFT JOIN sys.partition_range_values prv
    ON prv.function_id = (SELECT function_id FROM sys.partition_functions WHERE name = 'pf_Raw_Monthly')
    AND prv.boundary_id = p.partition_number
WHERE p.object_id = OBJECT_ID('PiXL.Raw') AND p.index_id = 1
  AND p.rows > 0
ORDER BY p.partition_number;

-- Id column type
SELECT c.name, TYPE_NAME(c.system_type_id) AS data_type
FROM sys.columns c
WHERE c.object_id = OBJECT_ID('PiXL.Raw') AND c.name = 'Id';

PRINT '';
PRINT '=========================================================================';
PRINT ' 28_RenameTestToRaw_Partition.sql — COMPLETE';
PRINT '';
PRINT ' PiXL.Test renamed to PiXL.Raw (permanent raw ingest, NEVER deleted)';
PRINT ' Monthly partitioning on ReceivedAt with tiered compression:';
PRINT '   < 3 months  → NONE  (max write throughput)';
PRINT '   3-6 months  → ROW   (~30% space savings)';
PRINT '   > 6 months  → PAGE  (~50-60% space savings)';
PRINT '';
PRINT ' Run ETL.usp_ManageRawCompression monthly to adjust tiers.';
PRINT ' Schedule: e.g., 1st of each month at 4 AM via SQL Agent.';
PRINT '=========================================================================';
GO
