-- =============================================
-- SmartPiXL Database — MAINTENANCE PROCEDURES
--
-- Targets the PiXL.* / ETL.* schema layout (post-17B).
-- These procedures handle:
--   - Data archival  (PiXL.Parsed → PiXL.Archive)
--   - Raw data purge (PiXL.Test rows already parsed)
--   - Statistics / monitoring
--   - Index maintenance
--
-- Schedule Recommendations (SQL Agent or Windows Task Scheduler):
--   - ETL.usp_PurgeRawData        : Daily at 3 AM
--   - ETL.usp_ArchiveParsedData   : Weekly on Sunday at 2 AM
--   - ETL.usp_IndexMaintenance    : Weekly on Sunday at 4 AM
--   - ETL.usp_PipelineStatistics  : On demand
--
-- Prerequisites: 17B_CreateSchemas.sql (PiXL / ETL schemas exist)
-- Last Updated: 2026-02-13
-- =============================================

USE SmartPiXL;
GO

-- =============================================
-- SECTION 1: ARCHIVE TABLE
-- Cold storage for parsed data past its retention window.
-- Same column set as PiXL.Parsed, plus ArchivedAt metadata.
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id
               WHERE s.name = 'PiXL' AND t.name = 'Archive')
BEGIN
    CREATE TABLE PiXL.[Archive] (
        -- Identity / source link
        Id              BIGINT          NOT NULL,   -- PK from PiXL.Parsed
        SourceId        INT             NOT NULL,   -- FK back to PiXL.Test.Id

        -- Core dimensions
        CompanyID       VARCHAR(100),
        PiXLID          VARCHAR(100),
        IPAddress       VARCHAR(50),
        ReceivedAt      DATETIME2,

        -- Screen
        ScreenWidth     INT,
        ScreenHeight    INT,
        ViewportWidth   INT,
        ViewportHeight  INT,
        ColorDepth      INT,
        PixelRatio      DECIMAL(5,2),

        -- Device
        [Platform]      VARCHAR(50),
        CPUCores        INT,
        DeviceMemory    DECIMAL(5,2),
        GPU             VARCHAR(500),

        -- Fingerprints
        CanvasFingerprint   VARCHAR(100),
        WebGLFingerprint    VARCHAR(100),
        AudioFingerprint    VARCHAR(100),
        MathFingerprint     VARCHAR(200),

        -- Location / Time
        Timezone        VARCHAR(100),
        [Language]      VARCHAR(50),

        -- Page
        PageURL         VARCHAR(2000),
        PageReferrer    VARCHAR(2000),
        Domain          VARCHAR(200),

        -- Flags
        DarkModePreferred   BIT,
        CookiesEnabled      BIT,
        WebDriverDetected   BIT,

        -- IP classification
        IpType          VARCHAR(20)     NULL,
        ShouldGeolocate BIT             NULL,

        -- Geo
        Country         VARCHAR(100)    NULL,
        Region          VARCHAR(100)    NULL,
        City            VARCHAR(100)    NULL,
        PostalCode      VARCHAR(20)     NULL,
        Latitude        DECIMAL(9,6)    NULL,
        Longitude       DECIMAL(9,6)    NULL,

        -- Bot / threat scores
        BotScore        INT             NULL,
        AnomalyScore    INT             NULL,
        CombinedThreatScore INT         NULL,

        -- Timestamps
        ParsedAt        DATETIME2       NULL,
        ArchivedAt      DATETIME2       NOT NULL DEFAULT GETUTCDATE()
    );

    CREATE CLUSTERED INDEX IX_Archive_ReceivedAt
        ON PiXL.[Archive](ReceivedAt);

    CREATE NONCLUSTERED INDEX IX_Archive_Company
        ON PiXL.[Archive](CompanyID, ReceivedAt);

    PRINT 'Created PiXL.Archive table.';
END
GO

-- =============================================
-- SECTION 2: PURGE RAW DATA
-- Deletes rows from PiXL.Test that have already been parsed
-- (Id <= ETL.Watermark.LastProcessedId) and are older than
-- the retention window.
-- =============================================
IF OBJECT_ID('ETL.usp_PurgeRawData', 'P') IS NOT NULL
    DROP PROCEDURE ETL.usp_PurgeRawData;
GO

CREATE PROCEDURE ETL.usp_PurgeRawData
    @DaysToKeep     INT = 7,        -- Keep raw rows for N days after parsing
    @BatchSize      INT = 5000,     -- Delete in batches to limit lock duration
    @MaxBatches     INT = 200       -- Safety cap per execution
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @CutoffDate     DATETIME2 = DATEADD(DAY, -@DaysToKeep, GETUTCDATE());
    DECLARE @WatermarkId    BIGINT;
    DECLARE @TotalDeleted   INT = 0;
    DECLARE @BatchDeleted   INT = 1;
    DECLARE @BatchCount     INT = 0;

    -- Only purge rows the ETL has already processed
    SELECT @WatermarkId = LastProcessedId FROM ETL.Watermark
    WHERE ProcessName = 'ParseNewHits';

    IF @WatermarkId IS NULL
    BEGIN
        PRINT 'No watermark found — nothing to purge.';
        RETURN;
    END

    WHILE @BatchDeleted > 0 AND @BatchCount < @MaxBatches
    BEGIN
        DELETE TOP (@BatchSize)
        FROM PiXL.Test
        WHERE Id <= @WatermarkId
          AND ReceivedAt < @CutoffDate;

        SET @BatchDeleted = @@ROWCOUNT;
        SET @TotalDeleted = @TotalDeleted + @BatchDeleted;
        SET @BatchCount   = @BatchCount + 1;

        IF @BatchDeleted = @BatchSize
            WAITFOR DELAY '00:00:00.100';   -- yield between batches
    END

    SELECT
        @TotalDeleted   AS RecordsPurged,
        @CutoffDate     AS CutoffDate,
        @WatermarkId    AS WatermarkAtExecution,
        @BatchCount     AS BatchesProcessed,
        CASE WHEN @BatchCount >= @MaxBatches
             THEN 'MoreRemaining' ELSE 'Complete' END AS [Status];
END
GO

PRINT 'Created ETL.usp_PurgeRawData.';
GO

-- =============================================
-- SECTION 3: ARCHIVE PARSED DATA
-- Moves old rows from PiXL.Parsed → PiXL.Archive, then deletes
-- the source rows.  Runs inside explicit transactions per batch.
-- =============================================
IF OBJECT_ID('ETL.usp_ArchiveParsedData', 'P') IS NOT NULL
    DROP PROCEDURE ETL.usp_ArchiveParsedData;
GO

CREATE PROCEDURE ETL.usp_ArchiveParsedData
    @DaysToKeep     INT = 90,       -- Rows older than 90 days get archived
    @BatchSize      INT = 5000,
    @MaxBatches     INT = 200
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @CutoffDate     DATETIME2 = DATEADD(DAY, -@DaysToKeep, GETUTCDATE());
    DECLARE @TotalArchived  INT = 0;
    DECLARE @BatchArchived  INT = 1;
    DECLARE @BatchCount     INT = 0;

    WHILE @BatchArchived > 0 AND @BatchCount < @MaxBatches
    BEGIN
        BEGIN TRANSACTION;

        -- Copy to archive
        INSERT INTO PiXL.[Archive] (
            Id, SourceId, CompanyID, PiXLID, IPAddress, ReceivedAt,
            ScreenWidth, ScreenHeight, ViewportWidth, ViewportHeight,
            ColorDepth, PixelRatio,
            [Platform], CPUCores, DeviceMemory, GPU,
            CanvasFingerprint, WebGLFingerprint, AudioFingerprint, MathFingerprint,
            Timezone, [Language],
            PageURL, PageReferrer, Domain,
            DarkModePreferred, CookiesEnabled, WebDriverDetected,
            IpType, ShouldGeolocate,
            Country, Region, City, PostalCode, Latitude, Longitude,
            BotScore, AnomalyScore, CombinedThreatScore,
            ParsedAt
        )
        SELECT TOP (@BatchSize)
            Id, SourceId, CompanyID, PiXLID, IPAddress, ReceivedAt,
            ScreenWidth, ScreenHeight, ViewportWidth, ViewportHeight,
            ColorDepth, PixelRatio,
            [Platform], CPUCores, DeviceMemory, GPU,
            CanvasFingerprint, WebGLFingerprint, AudioFingerprint, MathFingerprint,
            Timezone, [Language],
            PageURL, PageReferrer, Domain,
            DarkModePreferred, CookiesEnabled, WebDriverDetected,
            IpType, ShouldGeolocate,
            Country, Region, City, PostalCode, Latitude, Longitude,
            BotScore, AnomalyScore, CombinedThreatScore,
            ParsedAt
        FROM PiXL.Parsed
        WHERE ReceivedAt < @CutoffDate
        ORDER BY Id;

        SET @BatchArchived = @@ROWCOUNT;

        -- Delete the rows we just copied
        IF @BatchArchived > 0
        BEGIN
            ;WITH Batch AS (
                SELECT TOP (@BatchSize) Id
                FROM PiXL.Parsed
                WHERE ReceivedAt < @CutoffDate
                ORDER BY Id
            )
            DELETE FROM PiXL.Parsed
            WHERE Id IN (SELECT Id FROM Batch);
        END

        COMMIT TRANSACTION;

        SET @TotalArchived = @TotalArchived + @BatchArchived;
        SET @BatchCount    = @BatchCount + 1;

        IF @BatchArchived = @BatchSize
            WAITFOR DELAY '00:00:00.100';
    END

    SELECT
        @TotalArchived  AS RecordsArchived,
        @CutoffDate     AS CutoffDate,
        @BatchCount     AS BatchesProcessed,
        CASE WHEN @BatchCount >= @MaxBatches
             THEN 'MoreRemaining' ELSE 'Complete' END AS [Status];
END
GO

PRINT 'Created ETL.usp_ArchiveParsedData.';
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

    -- 2. ETL lag (un-parsed rows)
    SELECT
        'ETL_Lag'                       AS Category,
        (SELECT MAX(Id) FROM PiXL.Test)                             AS MaxTestId,
        (SELECT LastProcessedId FROM ETL.Watermark
         WHERE ProcessName = 'ParseNewHits')                        AS WatermarkId,
        (SELECT MAX(Id) FROM PiXL.Test)
            - ISNULL((SELECT LastProcessedId FROM ETL.Watermark
                      WHERE ProcessName = 'ParseNewHits'), 0)      AS UnparsedRows;

    -- 3. Insert rate — last hour, per minute
    SELECT
        'InsertRate'                                AS Category,
        DATEADD(MINUTE, DATEDIFF(MINUTE, 0, ReceivedAt), 0) AS MinuteBucket,
        COUNT(*)                                    AS RecordsPerMinute
    FROM PiXL.Test WITH (NOLOCK)
    WHERE ReceivedAt >= DATEADD(HOUR, -1, GETUTCDATE())
    GROUP BY DATEADD(MINUTE, DATEDIFF(MINUTE, 0, ReceivedAt), 0)
    ORDER BY MinuteBucket DESC;

    -- 4. Top companies today
    SELECT TOP 10
        'TopCompaniesToday'     AS Category,
        CompanyID,
        COUNT(*)                AS PixelFires
    FROM PiXL.Test WITH (NOLOCK)
    WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)
    GROUP BY CompanyID
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
