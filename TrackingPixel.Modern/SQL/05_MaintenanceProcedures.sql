-- =============================================
-- SmartPiXL Database - MAINTENANCE PROCEDURES
-- 
-- Run this after initial setup to add data lifecycle management.
-- These procedures handle:
--   - Data archival (move old data to archive table)
--   - Raw data purge (delete processed raw data)
--   - Materialization with concurrency protection
--   - Statistics and monitoring queries
--
-- Schedule Recommendations:
--   - sp_MaterializePiXLData_Safe: Every 5 minutes (SQL Agent)
--   - sp_PurgeRawData: Daily at 3 AM
--   - sp_ArchivePiXLData: Weekly on Sunday at 2 AM
--
-- Last Updated: 2026-02-02
-- =============================================

USE SmartPixl;
GO

-- =============================================
-- SECTION 1: ARCHIVE TABLE
-- Stores historical data for compliance/analysis
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PiXL_Archive')
BEGIN
    CREATE TABLE dbo.PiXL_Archive (
        Id              BIGINT NOT NULL,
        SourceId        INT NOT NULL,
        CompanyID       NVARCHAR(100),
        PiXLID          NVARCHAR(100),
        IPAddress       NVARCHAR(50),
        ReceivedAt      DATETIME2,
        
        -- Screen
        ScreenWidth     INT,
        ScreenHeight    INT,
        ViewportWidth   INT,
        ViewportHeight  INT,
        ColorDepth      INT,
        PixelRatio      DECIMAL(5,2),
        
        -- Device
        [Platform]      NVARCHAR(50),
        CPUCores        INT,
        DeviceMemory    DECIMAL(5,2),
        GPU             NVARCHAR(500),
        
        -- Fingerprints
        CanvasFingerprint   NVARCHAR(100),
        WebGLFingerprint    NVARCHAR(100),
        AudioFingerprint    NVARCHAR(100),
        MathFingerprint     NVARCHAR(200),
        
        -- Location/Time
        Timezone        NVARCHAR(100),
        [Language]      NVARCHAR(50),
        
        -- Page
        PageURL         NVARCHAR(2000),
        PageReferrer    NVARCHAR(2000),
        Domain          NVARCHAR(200),
        
        -- Flags
        DarkModePreferred   BIT,
        CookiesEnabled      BIT,
        WebDriverDetected   BIT,
        
        -- IP Classification (if populated)
        IpType          NVARCHAR(20)    NULL,
        ShouldGeolocate BIT             NULL,
        
        -- Geo Data (if populated)
        Country         NVARCHAR(100)   NULL,
        Region          NVARCHAR(100)   NULL,
        City            NVARCHAR(100)   NULL,
        PostalCode      NVARCHAR(20)    NULL,
        Latitude        DECIMAL(9,6)    NULL,
        Longitude       DECIMAL(9,6)    NULL,
        
        -- Original processing time
        MaterializedAt  DATETIME2,
        
        -- Archive metadata
        ArchivedAt      DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    ) ON [SmartPixl];
    
    -- Minimal indexes for archive (read-only, rare access)
    CREATE CLUSTERED INDEX IX_PiXL_Archive_ReceivedAt 
    ON dbo.PiXL_Archive(ReceivedAt) ON [SmartPixl];
    
    CREATE INDEX IX_PiXL_Archive_Company 
    ON dbo.PiXL_Archive(CompanyID, ReceivedAt) ON [SmartPixl];
    
    PRINT 'Table PiXL_Archive created.';
END
GO

-- =============================================
-- SECTION 2: SAFE MATERIALIZATION PROCEDURE
-- Prevents concurrent execution with app lock
-- =============================================
IF OBJECT_ID('dbo.sp_MaterializePiXLData_Safe', 'P') IS NOT NULL
    DROP PROCEDURE dbo.sp_MaterializePiXLData_Safe;
GO

CREATE PROCEDURE dbo.sp_MaterializePiXLData_Safe
    @BatchSize INT = 10000  -- Process in batches to reduce lock time
AS
BEGIN
    SET NOCOUNT ON;
    
    -- Prevent concurrent execution
    DECLARE @LockResult INT;
    EXEC @LockResult = sp_getapplock 
        @Resource = 'MaterializePiXLData', 
        @LockMode = 'Exclusive', 
        @LockTimeout = 0;  -- Don't wait, just exit if locked
    
    IF @LockResult < 0
    BEGIN
        SELECT 
            0 AS RecordsMaterialized,
            'Another materialization is in progress' AS Status;
        RETURN;
    END
    
    DECLARE @TotalMaterialized INT = 0;
    DECLARE @BatchMaterialized INT = 1;
    DECLARE @LastProcessedId INT;
    DECLARE @StartTime DATETIME2 = GETUTCDATE();
    
    BEGIN TRY
        WHILE @BatchMaterialized > 0
        BEGIN
            -- Get last processed ID
            SELECT @LastProcessedId = ISNULL(MAX(SourceId), 0) FROM dbo.PiXL_Materialized;
            
            -- Insert batch
            INSERT INTO dbo.PiXL_Materialized (
                SourceId, CompanyID, PiXLID, IPAddress, ReceivedAt,
                ScreenWidth, ScreenHeight, ViewportWidth, ViewportHeight, ColorDepth, PixelRatio,
                [Platform], CPUCores, DeviceMemory, GPU,
                CanvasFingerprint, WebGLFingerprint, AudioFingerprint, MathFingerprint,
                Timezone, [Language],
                PageURL, PageReferrer, Domain,
                DarkModePreferred, CookiesEnabled, WebDriverDetected
            )
            SELECT TOP (@BatchSize)
                Id, CompanyID, PiXLID, IPAddress, ReceivedAt,
                ScreenWidth, ScreenHeight, ViewportWidth, ViewportHeight, ColorDepth, PixelRatio,
                [Platform], CPUCores, DeviceMemory, GPU,
                CanvasFingerprint, WebGLFingerprint, AudioFingerprint, MathFingerprint,
                Timezone, [Language],
                PageURL, PageReferrer, Domain,
                DarkModePreferred, CookiesEnabled, WebDriverDetected
            FROM dbo.vw_PiXL_Parsed
            WHERE Id > @LastProcessedId
            ORDER BY Id;
            
            SET @BatchMaterialized = @@ROWCOUNT;
            SET @TotalMaterialized = @TotalMaterialized + @BatchMaterialized;
            
            -- Yield to other operations between batches
            IF @BatchMaterialized = @BatchSize
                WAITFOR DELAY '00:00:00.050';
        END
        
        SELECT 
            @TotalMaterialized AS RecordsMaterialized,
            'Success' AS Status,
            DATEDIFF(MILLISECOND, @StartTime, GETUTCDATE()) AS DurationMs;
    END TRY
    BEGIN CATCH
        SELECT 
            @TotalMaterialized AS RecordsMaterialized,
            ERROR_MESSAGE() AS Status;
        THROW;
    END CATCH
    
    -- Release lock
    EXEC sp_releaseapplock @Resource = 'MaterializePiXLData';
END
GO

PRINT 'Procedure sp_MaterializePiXLData_Safe created.';
GO

-- =============================================
-- SECTION 3: PURGE RAW DATA PROCEDURE
-- Deletes materialized raw data older than retention period
-- =============================================
IF OBJECT_ID('dbo.sp_PurgeRawData', 'P') IS NOT NULL
    DROP PROCEDURE dbo.sp_PurgeRawData;
GO

CREATE PROCEDURE dbo.sp_PurgeRawData
    @DaysToKeep INT = 7,       -- Keep raw data for 7 days (for reprocessing if needed)
    @BatchSize INT = 5000,     -- Delete in batches to reduce lock time
    @MaxBatches INT = 100      -- Safety limit per execution
AS
BEGIN
    SET NOCOUNT ON;
    
    DECLARE @CutoffDate DATETIME2 = DATEADD(DAY, -@DaysToKeep, GETUTCDATE());
    DECLARE @MaxMaterializedId INT = (SELECT ISNULL(MAX(SourceId), 0) FROM dbo.PiXL_Materialized);
    DECLARE @TotalDeleted INT = 0;
    DECLARE @BatchDeleted INT = 1;
    DECLARE @BatchCount INT = 0;
    
    WHILE @BatchDeleted > 0 AND @BatchCount < @MaxBatches
    BEGIN
        DELETE TOP (@BatchSize) FROM dbo.PiXL_Test
        WHERE Id <= @MaxMaterializedId
          AND ReceivedAt < @CutoffDate;
        
        SET @BatchDeleted = @@ROWCOUNT;
        SET @TotalDeleted = @TotalDeleted + @BatchDeleted;
        SET @BatchCount = @BatchCount + 1;
        
        -- Yield between batches
        IF @BatchDeleted = @BatchSize
            WAITFOR DELAY '00:00:00.100';
    END
    
    SELECT 
        @TotalDeleted AS RecordsPurged,
        @CutoffDate AS CutoffDate,
        @BatchCount AS BatchesProcessed,
        CASE WHEN @BatchCount >= @MaxBatches THEN 'MoreRemaining' ELSE 'Complete' END AS Status;
END
GO

PRINT 'Procedure sp_PurgeRawData created.';
GO

-- =============================================
-- SECTION 4: ARCHIVE DATA PROCEDURE
-- Moves old materialized data to archive table
-- =============================================
IF OBJECT_ID('dbo.sp_ArchivePiXLData', 'P') IS NOT NULL
    DROP PROCEDURE dbo.sp_ArchivePiXLData;
GO

CREATE PROCEDURE dbo.sp_ArchivePiXLData
    @DaysToKeep INT = 90,      -- Keep in main table for 90 days
    @BatchSize INT = 5000,     -- Archive in batches
    @MaxBatches INT = 200      -- Safety limit per execution
AS
BEGIN
    SET NOCOUNT ON;
    
    DECLARE @CutoffDate DATETIME2 = DATEADD(DAY, -@DaysToKeep, GETUTCDATE());
    DECLARE @TotalArchived INT = 0;
    DECLARE @BatchArchived INT = 1;
    DECLARE @BatchCount INT = 0;
    
    WHILE @BatchArchived > 0 AND @BatchCount < @MaxBatches
    BEGIN
        BEGIN TRANSACTION;
        
        -- Copy to archive
        INSERT INTO dbo.PiXL_Archive (
            Id, SourceId, CompanyID, PiXLID, IPAddress, ReceivedAt,
            ScreenWidth, ScreenHeight, ViewportWidth, ViewportHeight, ColorDepth, PixelRatio,
            [Platform], CPUCores, DeviceMemory, GPU,
            CanvasFingerprint, WebGLFingerprint, AudioFingerprint, MathFingerprint,
            Timezone, [Language],
            PageURL, PageReferrer, Domain,
            DarkModePreferred, CookiesEnabled, WebDriverDetected,
            IpType, ShouldGeolocate,
            Country, Region, City, PostalCode, Latitude, Longitude,
            MaterializedAt
        )
        SELECT TOP (@BatchSize)
            Id, SourceId, CompanyID, PiXLID, IPAddress, ReceivedAt,
            ScreenWidth, ScreenHeight, ViewportWidth, ViewportHeight, ColorDepth, PixelRatio,
            [Platform], CPUCores, DeviceMemory, GPU,
            CanvasFingerprint, WebGLFingerprint, AudioFingerprint, MathFingerprint,
            Timezone, [Language],
            PageURL, PageReferrer, Domain,
            DarkModePreferred, CookiesEnabled, WebDriverDetected,
            IpType, ShouldGeolocate,
            Country, Region, City, PostalCode, Latitude, Longitude,
            MaterializedAt
        FROM dbo.PiXL_Materialized
        WHERE ReceivedAt < @CutoffDate
        ORDER BY Id;
        
        SET @BatchArchived = @@ROWCOUNT;
        
        -- Delete archived records
        IF @BatchArchived > 0
        BEGIN
            DELETE TOP (@BatchSize) FROM dbo.PiXL_Materialized
            WHERE ReceivedAt < @CutoffDate;
        END
        
        COMMIT TRANSACTION;
        
        SET @TotalArchived = @TotalArchived + @BatchArchived;
        SET @BatchCount = @BatchCount + 1;
        
        -- Yield between batches
        IF @BatchArchived = @BatchSize
            WAITFOR DELAY '00:00:00.100';
    END
    
    SELECT 
        @TotalArchived AS RecordsArchived,
        @CutoffDate AS CutoffDate,
        @BatchCount AS BatchesProcessed,
        CASE WHEN @BatchCount >= @MaxBatches THEN 'MoreRemaining' ELSE 'Complete' END AS Status;
END
GO

PRINT 'Procedure sp_ArchivePiXLData created.';
GO

-- =============================================
-- SECTION 5: MONITORING/STATISTICS PROCEDURES
-- =============================================
IF OBJECT_ID('dbo.sp_PiXLStatistics', 'P') IS NOT NULL
    DROP PROCEDURE dbo.sp_PiXLStatistics;
GO

CREATE PROCEDURE dbo.sp_PiXLStatistics
AS
BEGIN
    SET NOCOUNT ON;
    
    -- Table sizes
    SELECT 
        'TableSizes' AS Category,
        t.name AS TableName,
        SUM(p.rows) AS RowCount,
        CAST(ROUND((SUM(a.total_pages) * 8) / 1024.0, 2) AS DECIMAL(10,2)) AS SizeMB
    FROM sys.tables t
    INNER JOIN sys.indexes i ON t.object_id = i.object_id
    INNER JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
    INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
    WHERE t.name IN ('PiXL_Test', 'PiXL_Materialized', 'PiXL_Archive')
    GROUP BY t.name;
    
    -- Materialization lag
    SELECT 
        'MaterializationLag' AS Category,
        (SELECT MAX(Id) FROM dbo.PiXL_Test) AS MaxRawId,
        (SELECT ISNULL(MAX(SourceId), 0) FROM dbo.PiXL_Materialized) AS MaxMaterializedId,
        (SELECT MAX(Id) FROM dbo.PiXL_Test) - 
            ISNULL((SELECT MAX(SourceId) FROM dbo.PiXL_Materialized), 0) AS UnmaterializedRecords;
    
    -- Recent insert rate (last hour, per minute)
    SELECT 
        'InsertRate' AS Category,
        DATEADD(MINUTE, DATEDIFF(MINUTE, 0, ReceivedAt), 0) AS MinuteBucket,
        COUNT(*) AS RecordsPerMinute
    FROM dbo.PiXL_Test WITH (NOLOCK)
    WHERE ReceivedAt >= DATEADD(HOUR, -1, GETUTCDATE())
    GROUP BY DATEADD(MINUTE, DATEDIFF(MINUTE, 0, ReceivedAt), 0)
    ORDER BY MinuteBucket DESC;
    
    -- Top companies today
    SELECT TOP 10
        'TopCompaniesToday' AS Category,
        CompanyID,
        COUNT(*) AS PixelFires
    FROM dbo.PiXL_Test WITH (NOLOCK)
    WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)
    GROUP BY CompanyID
    ORDER BY COUNT(*) DESC;
END
GO

PRINT 'Procedure sp_PiXLStatistics created.';
GO

-- =============================================
-- SECTION 6: INDEX MAINTENANCE PROCEDURE
-- =============================================
IF OBJECT_ID('dbo.sp_PiXLIndexMaintenance', 'P') IS NOT NULL
    DROP PROCEDURE dbo.sp_PiXLIndexMaintenance;
GO

CREATE PROCEDURE dbo.sp_PiXLIndexMaintenance
    @FragmentationThreshold FLOAT = 30.0  -- Rebuild if > 30% fragmented
AS
BEGIN
    SET NOCOUNT ON;
    
    DECLARE @SQL NVARCHAR(MAX);
    DECLARE @IndexName NVARCHAR(256);
    DECLARE @TableName NVARCHAR(256);
    DECLARE @Fragmentation FLOAT;
    
    DECLARE IndexCursor CURSOR FOR
        SELECT 
            OBJECT_NAME(ips.object_id) AS TableName,
            i.name AS IndexName,
            ips.avg_fragmentation_in_percent
        FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ips
        INNER JOIN sys.indexes i ON ips.object_id = i.object_id AND ips.index_id = i.index_id
        WHERE OBJECT_NAME(ips.object_id) IN ('PiXL_Test', 'PiXL_Materialized', 'PiXL_Archive')
          AND ips.avg_fragmentation_in_percent > @FragmentationThreshold
          AND ips.page_count > 1000  -- Only indexes with significant pages
          AND i.name IS NOT NULL;
    
    OPEN IndexCursor;
    FETCH NEXT FROM IndexCursor INTO @TableName, @IndexName, @Fragmentation;
    
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @SQL = 'ALTER INDEX [' + @IndexName + '] ON dbo.[' + @TableName + '] REBUILD WITH (ONLINE = ON)';
        
        BEGIN TRY
            PRINT 'Rebuilding ' + @IndexName + ' on ' + @TableName + ' (' + CAST(@Fragmentation AS VARCHAR(10)) + '% fragmented)';
            EXEC sp_executesql @SQL;
        END TRY
        BEGIN CATCH
            -- If ONLINE rebuild fails (non-Enterprise), try offline
            SET @SQL = 'ALTER INDEX [' + @IndexName + '] ON dbo.[' + @TableName + '] REBUILD';
            EXEC sp_executesql @SQL;
        END CATCH
        
        FETCH NEXT FROM IndexCursor INTO @TableName, @IndexName, @Fragmentation;
    END
    
    CLOSE IndexCursor;
    DEALLOCATE IndexCursor;
    
    PRINT 'Index maintenance complete.';
END
GO

PRINT 'Procedure sp_PiXLIndexMaintenance created.';
GO

-- =============================================
-- VERIFICATION
-- =============================================
PRINT '';
PRINT '============================================';
PRINT 'Maintenance Procedures Installation Complete!';
PRINT '============================================';
PRINT '';
PRINT 'Schedule Recommendations:';
PRINT '  - sp_MaterializePiXLData_Safe: Every 5 minutes';
PRINT '  - sp_PurgeRawData: Daily at 3 AM';
PRINT '  - sp_ArchivePiXLData: Weekly on Sunday at 2 AM';
PRINT '  - sp_PiXLIndexMaintenance: Weekly on Sunday at 4 AM';
PRINT '  - sp_PiXLStatistics: On-demand for monitoring';
PRINT '';
GO
