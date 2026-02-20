-- =============================================
-- Migration 38: Drop PiXL.Archive + usp_ArchiveParsedData
--
-- Owner directive: "I can think of no reason for [PiXL.Archive]."
-- PiXL.Raw is the permanent archive. The Archive table schema (35 cols)
-- is stale vs PiXL.Parsed (175+ cols) — the proc would fail if run.
-- Also: fix usp_PurgeRawData to reference PiXL.Raw (not PiXL.Test).
--
-- Safe to run multiple times (all drops are conditional).
-- =============================================
USE SmartPiXL;
GO

-- 1. Drop the archive procedure
IF OBJECT_ID('ETL.usp_ArchiveParsedData', 'P') IS NOT NULL
BEGIN
    DROP PROCEDURE ETL.usp_ArchiveParsedData;
    PRINT 'Dropped ETL.usp_ArchiveParsedData.';
END
ELSE
    PRINT 'ETL.usp_ArchiveParsedData does not exist — skipping.';
GO

-- 2. Drop the archive table
IF OBJECT_ID('PiXL.Archive', 'U') IS NOT NULL
BEGIN
    DROP TABLE PiXL.[Archive];
    PRINT 'Dropped PiXL.Archive.';
END
ELSE
    PRINT 'PiXL.Archive does not exist — skipping.';
GO

-- 3. Fix usp_PurgeRawData: PiXL.Test → PiXL.Raw
--    The proc deletes rows from PiXL.Raw that ETL has already processed.
CREATE OR ALTER PROCEDURE ETL.usp_PurgeRawData
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
        FROM PiXL.Raw
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

PRINT 'Migration 38 complete: Archive dropped, usp_PurgeRawData updated to PiXL.Raw.';
GO
