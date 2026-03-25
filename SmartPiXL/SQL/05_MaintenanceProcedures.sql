-- =============================================
-- SmartPiXL Database — MAINTENANCE PROCEDURES
--
-- Targets the PiXL.* / ETL.* schema layout (post-17B).
-- These procedures handle:
--   - Statistics / monitoring
--   - Index maintenance
--
-- Schedule Recommendations (SQL Agent or Windows Task Scheduler):
--   - ETL.usp_IndexMaintenance    : Weekly on Sunday at 4 AM
--   - ETL.usp_PipelineStatistics  : On demand
--
-- Prerequisites: 17B_CreateSchemas.sql (PiXL / ETL schemas exist)
-- Last Updated: 2026-02-13
-- =============================================

USE SmartPiXL;
GO

-- (SECTION 1 REMOVED — PiXL.Archive dropped per migration 38.)
GO

-- (SECTION 2 REMOVED — usp_PurgeRawData dropped. PiXL.Raw has been removed.)
GO

-- =============================================
-- SECTION 4: PIPELINE STATISTICS
-- Quick health check: table sizes, ETL lag, insert rate,
-- top companies.
-- =============================================
IF OBJECT_ID('ETL.usp_PipelineStatistics', 'P') IS NOT NULL
    DROP PROCEDURE ETL.usp_PipelineStatistics;
GO

CREATE PROCEDURE ETL.usp_PipelineStatistics
AS
BEGIN
    SET NOCOUNT ON;

    -- 1. Table sizes
    SELECT
        'TableSizes'                    AS Category,
        s.name + '.' + t.name          AS TableName,
        SUM(p.rows)                     AS [RowCount],
        CAST(ROUND(SUM(a.total_pages) * 8 / 1024.0, 2) AS DECIMAL(10,2)) AS SizeMB
    FROM sys.tables t
    JOIN sys.schemas s           ON t.schema_id = s.schema_id
    JOIN sys.indexes i           ON t.object_id = i.object_id
    JOIN sys.partitions p        ON i.object_id = p.object_id AND i.index_id = p.index_id
    JOIN sys.allocation_units a  ON p.partition_id = a.container_id
    WHERE s.name IN ('PiXL', 'ETL')
    GROUP BY s.name, t.name
    ORDER BY SizeMB DESC;

    -- 2. ETL lag (un-processed rows)
    SELECT
        'ETL_Lag'                       AS Category,
        (SELECT MAX(SourceId) FROM PiXL.Parsed)                     AS MaxParsedSourceId,
        (SELECT LastProcessedId FROM ETL.Watermark
         WHERE ProcessName = 'ProcessDimensions')                   AS WatermarkId,
        ISNULL((SELECT MAX(SourceId) FROM PiXL.Parsed), 0)
            - ISNULL((SELECT LastProcessedId FROM ETL.Watermark
                      WHERE ProcessName = 'ProcessDimensions'), 0)  AS UnprocessedRows;

    -- 3. Insert rate — last hour, per minute
    SELECT
        'InsertRate'                                AS Category,
        DATEADD(MINUTE, DATEDIFF(MINUTE, 0, ReceivedAt), 0) AS MinuteBucket,
        COUNT(*)                                    AS RecordsPerMinute
    FROM PiXL.Parsed WITH (NOLOCK)
    WHERE ReceivedAt >= DATEADD(HOUR, -1, GETUTCDATE())
    GROUP BY DATEADD(MINUTE, DATEDIFF(MINUTE, 0, ReceivedAt), 0)
    ORDER BY MinuteBucket DESC;

    -- 4. Top companies today
    SELECT TOP 10
        'TopCompaniesToday'     AS Category,
        CompanyId,
        COUNT(*)                AS PixelFires
    FROM PiXL.Parsed WITH (NOLOCK)
    WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)
    GROUP BY CompanyId
    ORDER BY COUNT(*) DESC;
END
GO

PRINT 'Created ETL.usp_PipelineStatistics.';
GO

-- =============================================
-- SECTION 5: INDEX MAINTENANCE
-- Rebuilds fragmented indexes across all PiXL / ETL tables.
-- =============================================
IF OBJECT_ID('ETL.usp_IndexMaintenance', 'P') IS NOT NULL
    DROP PROCEDURE ETL.usp_IndexMaintenance;
GO

CREATE PROCEDURE ETL.usp_IndexMaintenance
    @FragmentationThreshold FLOAT = 30.0    -- Rebuild if > 30% fragmented
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @SQL            NVARCHAR(MAX);
    DECLARE @SchemaName     NVARCHAR(128);
    DECLARE @TableName      NVARCHAR(128);
    DECLARE @IndexName      NVARCHAR(256);
    DECLARE @Fragmentation  FLOAT;

    DECLARE IndexCursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT
            s.name              AS SchemaName,
            OBJECT_NAME(ips.object_id) AS TableName,
            i.name              AS IndexName,
            ips.avg_fragmentation_in_percent
        FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ips
        JOIN sys.indexes i  ON ips.object_id = i.object_id AND ips.index_id = i.index_id
        JOIN sys.tables  t  ON ips.object_id = t.object_id
        JOIN sys.schemas s  ON t.schema_id   = s.schema_id
        WHERE s.name IN ('PiXL', 'ETL')
          AND ips.avg_fragmentation_in_percent > @FragmentationThreshold
          AND ips.page_count > 1000
          AND i.name IS NOT NULL;

    OPEN IndexCursor;
    FETCH NEXT FROM IndexCursor INTO @SchemaName, @TableName, @IndexName, @Fragmentation;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        BEGIN TRY
            -- Try ONLINE rebuild first (Enterprise / Developer edition)
            PRINT 'Rebuilding [' + @IndexName + '] on '
                  + QUOTENAME(@SchemaName) + '.' + QUOTENAME(@TableName)
                  + ' (' + CAST(CAST(@Fragmentation AS DECIMAL(5,1)) AS VARCHAR(10)) + '% frag)';
            SET @SQL = 'ALTER INDEX ' + QUOTENAME(@IndexName)
                     + ' ON ' + QUOTENAME(@SchemaName) + '.' + QUOTENAME(@TableName)
                     + ' REBUILD WITH (ONLINE = ON)';
            EXEC sp_executesql @SQL;
        END TRY
        BEGIN CATCH
            -- Fall back to offline rebuild
            SET @SQL = 'ALTER INDEX ' + QUOTENAME(@IndexName)
                     + ' ON ' + QUOTENAME(@SchemaName) + '.' + QUOTENAME(@TableName)
                     + ' REBUILD';
            EXEC sp_executesql @SQL;
        END CATCH

        FETCH NEXT FROM IndexCursor INTO @SchemaName, @TableName, @IndexName, @Fragmentation;
    END

    CLOSE IndexCursor;
    DEALLOCATE IndexCursor;

    PRINT 'Index maintenance complete.';
END
GO

PRINT 'Created ETL.usp_IndexMaintenance.';
GO
