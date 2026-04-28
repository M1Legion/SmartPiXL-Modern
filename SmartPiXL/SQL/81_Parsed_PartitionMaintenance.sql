-- ============================================================================
-- 81_Parsed_PartitionMaintenance.sql
-- ============================================================================
-- Installs ETL.usp_MaintainParsedPartitions and a SQL Agent job that runs it
-- daily. The proc ensures PF_PiXL_Parsed_Monthly always has at least
-- @LookAheadMonths future boundaries so new-month inserts never land in an
-- unexpected partition.
--
-- Safe to re-run: all creates are idempotent; the job is dropped and
-- recreated on each execution.
-- ============================================================================

SET XACT_ABORT ON;
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
GO

USE SmartPiXL;
GO

-- ----------------------------------------------------------------------------
-- 1. Stored procedure
-- ----------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE ETL.usp_MaintainParsedPartitions
    @LookAheadMonths INT = 3,       -- Keep this many future month boundaries
    @Verbose         BIT = 1        -- 1 = PRINT progress, 0 = silent
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Guardrails: verify the function exists and is the one we expect
    IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = 'PF_PiXL_Parsed_Monthly')
    BEGIN
        RAISERROR('PF_PiXL_Parsed_Monthly does not exist. Run 80_Parsed_Partitioning.sql first.', 16, 1);
        RETURN;
    END

    -- What's the latest existing boundary?
    DECLARE @maxBoundary DATETIME2(7);
    SELECT @maxBoundary = MAX(CAST(prv.value AS DATETIME2(7)))
    FROM sys.partition_functions pf
    JOIN sys.partition_range_values prv ON prv.function_id = pf.function_id
    WHERE pf.name = 'PF_PiXL_Parsed_Monthly';

    -- Target latest boundary = first of (current month + @LookAheadMonths)
    DECLARE @targetBoundary DATETIME2(7) =
        DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1);
    SET @targetBoundary = DATEADD(MONTH, @LookAheadMonths, @targetBoundary);

    IF @Verbose = 1
        PRINT CONCAT('[', SYSUTCDATETIME(), '] MaintainParsedPartitions: max=',
                     CONVERT(CHAR(10), @maxBoundary, 23),
                     ', target=', CONVERT(CHAR(10), @targetBoundary, 23));

    -- Add missing future boundaries one month at a time.
    DECLARE @next DATETIME2(7) = DATEADD(MONTH, 1, @maxBoundary);
    DECLARE @added INT = 0;

    WHILE @next <= @targetBoundary
    BEGIN
        -- NEXT USED tells the scheme which filegroup the new partition lives on.
        -- All partitions currently live on [SmartPiXL]; change here if you add
        -- cold-storage filegroups. PRIMARY in this database is intentionally
        -- small (10 GB) — never target it for Parsed data.
        ALTER PARTITION SCHEME PS_PiXL_Parsed_Monthly NEXT USED [SmartPiXL];

        -- SPLIT RANGE adds a new boundary value to the function.
        DECLARE @split NVARCHAR(200) =
            N'ALTER PARTITION FUNCTION PF_PiXL_Parsed_Monthly() SPLIT RANGE (' +
            QUOTENAME(CONVERT(CHAR(27), @next, 126), '''') + N');';
        EXEC sp_executesql @split;

        IF @Verbose = 1
            PRINT CONCAT('[', SYSUTCDATETIME(), '] Added boundary ', CONVERT(CHAR(10), @next, 23));

        SET @added += 1;
        SET @next = DATEADD(MONTH, 1, @next);
    END

    IF @Verbose = 1
        PRINT CONCAT('[', SYSUTCDATETIME(), '] MaintainParsedPartitions: added ', @added, ' boundary(ies)');

    -- Report current state
    SELECT
        partition_number,
        CAST(CASE WHEN partition_number = 1 THEN NULL
                  ELSE LAG(CAST(prv.value AS DATETIME2(7))) OVER (ORDER BY p.partition_number)
             END AS DATETIME2(0))                    AS RangeStart,
        CAST(prv.value AS DATETIME2(0))              AS RangeEnd,
        p.rows                                       AS RowCount_est
    FROM sys.partitions p
    LEFT JOIN sys.partition_range_values prv
        ON prv.function_id = (SELECT function_id FROM sys.partition_functions WHERE name='PF_PiXL_Parsed_Monthly')
       AND prv.boundary_id = p.partition_number
    WHERE p.object_id = OBJECT_ID('PiXL.Parsed')
      AND p.index_id  = 1
    ORDER BY p.partition_number;
END
GO

PRINT 'Created ETL.usp_MaintainParsedPartitions';
GO

-- ----------------------------------------------------------------------------
-- 2. SQL Agent job (runs daily at 02:00 UTC)
-- ----------------------------------------------------------------------------
-- Runs daily (not monthly) so a missed run never leaves us with no future
-- partition. It's a no-op on days when the look-ahead window is already
-- satisfied (99% of days).
-- ----------------------------------------------------------------------------

USE msdb;
GO

IF EXISTS (SELECT 1 FROM dbo.sysjobs WHERE name = 'SmartPiXL: Maintain Parsed Partitions')
    EXEC dbo.sp_delete_job @job_name = N'SmartPiXL: Maintain Parsed Partitions', @delete_unused_schedule = 1;
GO

DECLARE @jobId UNIQUEIDENTIFIER;

EXEC dbo.sp_add_job
    @job_name        = N'SmartPiXL: Maintain Parsed Partitions',
    @description     = N'Ensures PF_PiXL_Parsed_Monthly has 3 future month boundaries. Runs daily.',
    @category_name   = N'Database Maintenance',
    @enabled         = 1,
    @notify_level_eventlog = 2,     -- on failure
    @job_id          = @jobId OUTPUT;

EXEC dbo.sp_add_jobstep
    @job_id          = @jobId,
    @step_name       = N'Run ETL.usp_MaintainParsedPartitions',
    @subsystem       = N'TSQL',
    @command         = N'EXEC ETL.usp_MaintainParsedPartitions @LookAheadMonths = 3, @Verbose = 1;',
    @database_name   = N'SmartPiXL',
    @on_success_action = 1,         -- quit reporting success
    @on_fail_action    = 2,         -- quit reporting failure
    @retry_attempts  = 2,
    @retry_interval  = 5;           -- minutes

EXEC dbo.sp_add_jobschedule
    @job_id          = @jobId,
    @name            = N'Daily at 02:00 UTC',
    @freq_type       = 4,           -- daily
    @freq_interval   = 1,
    @active_start_time = 020000;    -- HHMMSS

EXEC dbo.sp_add_jobserver
    @job_id          = @jobId,
    @server_name     = N'(LOCAL)';
GO

PRINT 'Installed SQL Agent job: SmartPiXL: Maintain Parsed Partitions';
GO

-- Run it once now so we confirm the proc works and boundaries are topped up.
USE SmartPiXL;
GO
EXEC ETL.usp_MaintainParsedPartitions @LookAheadMonths = 3, @Verbose = 1;
GO
