-- ============================================================================
-- Migration 72: Dashboard Snapshot Tables
-- 
-- Creates Ops.DashboardSnapshot (single-row instant ops view) and
-- Ops.HourlyStats (incremental hourly rollup for charts).
-- Plus usp_Dash_WriteSnapshot to populate from Forge every 60s.
-- ============================================================================

-- Ensure Ops schema exists (created by earlier migrations, but safe to repeat)
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'Ops')
    EXEC('CREATE SCHEMA Ops');
GO

-- ============================================================================
-- Ops.DashboardSnapshot — Single-row table, overwritten every 60s by Forge.
-- Dashboard reads SELECT TOP 1 ... ORDER BY SnapshotId DESC for <1ms response.
-- ============================================================================
IF OBJECT_ID('Ops.DashboardSnapshot', 'U') IS NULL
BEGIN
    CREATE TABLE Ops.DashboardSnapshot
    (
        SnapshotId          BIGINT IDENTITY(1,1) PRIMARY KEY,
        SnapshotUtc         DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),

        -- Pipeline health (from DMVs + watermarks)
        RawRows             BIGINT NOT NULL DEFAULT 0,
        ParsedRows          BIGINT NOT NULL DEFAULT 0,
        DeviceRows          BIGINT NOT NULL DEFAULT 0,
        IpRows              BIGINT NOT NULL DEFAULT 0,
        VisitRows           BIGINT NOT NULL DEFAULT 0,
        MatchRows           BIGINT NOT NULL DEFAULT 0,

        -- Flow metrics
        HitsLastHour        INT NOT NULL DEFAULT 0,
        HitsLast5Min        INT NOT NULL DEFAULT 0,
        LastInsertUtc       DATETIME2(3) NULL,
        ParseWatermark      BIGINT NOT NULL DEFAULT 0,
        DimWatermark        BIGINT NOT NULL DEFAULT 0,
        MaxRawId            BIGINT NOT NULL DEFAULT 0,
        MaxParsedSourceId   BIGINT NOT NULL DEFAULT 0,
        ParseLag            BIGINT NOT NULL DEFAULT 0,
        DimLag              BIGINT NOT NULL DEFAULT 0,

        -- 24h aggregates
        Hits24h             BIGINT NOT NULL DEFAULT 0,
        UniqueIPs24h        INT NOT NULL DEFAULT 0,
        UniqueDevices24h    INT NOT NULL DEFAULT 0,
        BotHits24h          BIGINT NOT NULL DEFAULT 0,
        HumanHits24h        BIGINT NOT NULL DEFAULT 0,
        AvgBotScore24h      DECIMAL(5,2) NOT NULL DEFAULT 0,

        -- Match stats
        MatchesPending      INT NOT NULL DEFAULT 0,
        MatchesResolved24h  INT NOT NULL DEFAULT 0,

        -- Service status (written by Sentinel, read by dashboard)
        EdgeStatus          VARCHAR(20) NOT NULL DEFAULT 'Unknown',
        ForgeStatus         VARCHAR(20) NOT NULL DEFAULT 'Unknown',
        SqlStatus           VARCHAR(20) NOT NULL DEFAULT 'Unknown'
    );

    PRINT 'Created Ops.DashboardSnapshot';
END
GO

-- ============================================================================
-- Ops.HourlyStats — Append-only hourly rollup for trend charts.
-- Forge appends one row per hour. Dashboard reads last N hours.
-- ============================================================================
IF OBJECT_ID('Ops.HourlyStats', 'U') IS NULL
BEGIN
    CREATE TABLE Ops.HourlyStats
    (
        HourlyStatsId       BIGINT IDENTITY(1,1) PRIMARY KEY,
        HourUtc             DATETIME2(0) NOT NULL,

        TotalHits           BIGINT NOT NULL DEFAULT 0,
        UniqueIPs           INT NOT NULL DEFAULT 0,
        UniqueDevices       INT NOT NULL DEFAULT 0,
        BotHits             BIGINT NOT NULL DEFAULT 0,
        HumanHits           BIGINT NOT NULL DEFAULT 0,
        AvgBotScore         DECIMAL(5,2) NOT NULL DEFAULT 0,
        MatchCount          INT NOT NULL DEFAULT 0,
        NewDevices          INT NOT NULL DEFAULT 0,
        NewIPs              INT NOT NULL DEFAULT 0,
        ParsedRows          BIGINT NOT NULL DEFAULT 0,

        CreatedUtc          DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),

        CONSTRAINT UQ_HourlyStats_Hour UNIQUE (HourUtc)
    );

    CREATE NONCLUSTERED INDEX IX_HourlyStats_HourUtc
        ON Ops.HourlyStats (HourUtc DESC);

    PRINT 'Created Ops.HourlyStats';
END
GO

-- ============================================================================
-- usp_Dash_WriteSnapshot — Called by Forge every 60s.
-- Uses DMVs for row counts (instant) + watermarks + lightweight 24h aggregates.
-- PiXL.Raw is DEAD. Forge writes directly to PiXL.Parsed (decision D14/FD16).
-- ============================================================================
CREATE OR ALTER PROCEDURE dbo.usp_Dash_WriteSnapshot
AS
BEGIN
    SET NOCOUNT ON;

    -- Row counts from DMVs (instant, no table scan)
    DECLARE @ParsedRows BIGINT, @DeviceRows BIGINT,
            @IpRows BIGINT, @VisitRows BIGINT, @MatchRows BIGINT;

    SELECT @ParsedRows = SUM(p.rows)
    FROM sys.partitions p
    JOIN sys.tables t ON p.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = 'PiXL' AND t.name = 'Parsed' AND p.index_id IN (0,1);

    SELECT @DeviceRows = SUM(p.rows)
    FROM sys.partitions p
    JOIN sys.tables t ON p.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = 'PiXL' AND t.name = 'Device' AND p.index_id IN (0,1);

    SELECT @IpRows = SUM(p.rows)
    FROM sys.partitions p
    JOIN sys.tables t ON p.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = 'PiXL' AND t.name = 'IP' AND p.index_id IN (0,1);

    SELECT @VisitRows = SUM(p.rows)
    FROM sys.partitions p
    JOIN sys.tables t ON p.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = 'PiXL' AND t.name = 'Visit' AND p.index_id IN (0,1);

    SELECT @MatchRows = SUM(p.rows)
    FROM sys.partitions p
    JOIN sys.tables t ON p.object_id = t.object_id
    JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = 'PiXL' AND t.name = 'Match' AND p.index_id IN (0,1);

    -- Watermarks (ProcessDimensions is the active watermark, ParseNewHits is dead)
    DECLARE @DimWM BIGINT, @MaxParsedId BIGINT;

    SELECT @DimWM = ISNULL(LastProcessedId, 0)
    FROM ETL.Watermark WHERE ProcessName = 'ProcessDimensions';

    SELECT @MaxParsedId = ISNULL(MAX(SourceId), 0) FROM PiXL.Parsed;

    -- Flow: last insert from PiXL.Parsed (the landing table)
    DECLARE @LastInsert DATETIME2(3);
    SELECT @LastInsert = MAX(ReceivedAt) FROM PiXL.Parsed WITH (NOLOCK);

    -- Estimate hits/hour and hits/5min from Parsed SourceId delta
    DECLARE @HitsLastHour INT = 0, @HitsLast5Min INT = 0;
    DECLARE @PrevMaxParsedId BIGINT = 0, @PrevSnapshotUtc DATETIME2(3);

    SELECT TOP 1
        @PrevMaxParsedId = MaxParsedSourceId,
        @PrevSnapshotUtc = SnapshotUtc
    FROM Ops.DashboardSnapshot
    ORDER BY SnapshotId DESC;

    IF @PrevSnapshotUtc IS NOT NULL AND @PrevMaxParsedId > 0
    BEGIN
        DECLARE @ElapsedSec FLOAT = DATEDIFF(SECOND, @PrevSnapshotUtc, SYSUTCDATETIME());
        IF @ElapsedSec > 0
        BEGIN
            DECLARE @RowDelta BIGINT = @MaxParsedId - @PrevMaxParsedId;
            DECLARE @RatePerSec FLOAT = @RowDelta / @ElapsedSec;
            SET @HitsLastHour = CAST(@RatePerSec * 3600 AS INT);
            SET @HitsLast5Min = CAST(@RatePerSec * 300 AS INT);
        END
    END

    -- 24h aggregates using covering index on PiXL.Parsed
    DECLARE @Cutoff24h DATETIME2(3) = DATEADD(HOUR, -24, SYSUTCDATETIME());
    DECLARE @Hits24h BIGINT = 0, @UIPs24h INT = 0, @UDevs24h INT = 0,
            @BotHits24h BIGINT = 0, @HumanHits24h BIGINT = 0,
            @AvgBot24h DECIMAL(5,2) = 0;

    SELECT
        @Hits24h = COUNT_BIG(*),
        @UIPs24h = APPROX_COUNT_DISTINCT(IPAddress),
        @UDevs24h = APPROX_COUNT_DISTINCT(CanvasFingerprint),
        @BotHits24h = SUM(CASE WHEN BotScore >= 50 THEN 1 ELSE 0 END),
        @HumanHits24h = SUM(CASE WHEN BotScore < 50 THEN 1 ELSE 0 END),
        @AvgBot24h = ISNULL(AVG(CAST(BotScore AS DECIMAL(5,2))), 0)
    FROM PiXL.Parsed WITH (NOLOCK)
    WHERE ReceivedAt >= @Cutoff24h;

    -- Match stats (pending = unmatched visits in last 24h, resolved = matched in 24h)
    DECLARE @MatchPending INT = 0, @MatchResolved24h INT = 0;

    SELECT @MatchPending = COUNT(*)
    FROM PiXL.Visit WITH (NOLOCK)
    WHERE MatchEmail IS NULL
      AND CreatedAt >= @Cutoff24h;

    SELECT @MatchResolved24h = COUNT(*)
    FROM PiXL.Match WITH (NOLOCK)
    WHERE MatchedAt >= @Cutoff24h;

    -- Insert snapshot
    -- RawRows and MaxRawId kept as 0 for backward compat (table still has columns)
    -- ParseWatermark now stores DimWatermark (the only active parse-stage watermark)
    -- ParseLag = 0 (no Raw->Parsed lag in merged pipeline)
    INSERT INTO Ops.DashboardSnapshot
    (
        RawRows, ParsedRows, DeviceRows, IpRows, VisitRows, MatchRows,
        HitsLastHour, HitsLast5Min, LastInsertUtc,
        ParseWatermark, DimWatermark, MaxRawId, MaxParsedSourceId,
        ParseLag, DimLag,
        Hits24h, UniqueIPs24h, UniqueDevices24h,
        BotHits24h, HumanHits24h, AvgBotScore24h,
        MatchesPending, MatchesResolved24h
    )
    VALUES
    (
        0, ISNULL(@ParsedRows, 0), ISNULL(@DeviceRows, 0),
        ISNULL(@IpRows, 0), ISNULL(@VisitRows, 0), ISNULL(@MatchRows, 0),
        @HitsLastHour, @HitsLast5Min, @LastInsert,
        ISNULL(@DimWM, 0), ISNULL(@DimWM, 0),
        0, ISNULL(@MaxParsedId, 0),
        0,
        ISNULL(@MaxParsedId, 0) - ISNULL(@DimWM, 0),
        @Hits24h, @UIPs24h, @UDevs24h,
        @BotHits24h, @HumanHits24h, @AvgBot24h,
        @MatchPending, @MatchResolved24h
    );

    -- Prune old snapshots (keep last 1440 = ~24 hours at 60s intervals)
    DELETE FROM Ops.DashboardSnapshot
    WHERE SnapshotId < (
        SELECT MIN(SnapshotId) FROM (
            SELECT TOP 1440 SnapshotId
            FROM Ops.DashboardSnapshot
            ORDER BY SnapshotId DESC
        ) AS keep
    );

    -- Return the just-written snapshot for confirmation
    SELECT TOP 1 * FROM Ops.DashboardSnapshot ORDER BY SnapshotId DESC;
END
GO

-- ============================================================================
-- usp_Dash_WriteHourlyStats — Called by Forge, appends current hour's stats.
-- Uses MERGE to upsert so it's safe to call multiple times per hour.
-- ============================================================================
CREATE OR ALTER PROCEDURE dbo.usp_Dash_WriteHourlyStats
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @HourUtc DATETIME2(0) = DATEADD(HOUR, DATEDIFF(HOUR, 0, SYSUTCDATETIME()), 0);
    DECLARE @HourStart DATETIME2(3) = @HourUtc;
    DECLARE @HourEnd DATETIME2(3) = DATEADD(HOUR, 1, @HourUtc);

    DECLARE @TotalHits BIGINT, @UIPs INT, @UDevs INT,
            @BotHits BIGINT, @HumanHits BIGINT, @AvgBot DECIMAL(5,2),
            @MatchCount INT, @NewDevices INT, @NewIPs INT, @ParsedRows BIGINT;

    SELECT
        @TotalHits = COUNT_BIG(*),
        @UIPs = APPROX_COUNT_DISTINCT(IPAddress),
        @UDevs = APPROX_COUNT_DISTINCT(CanvasFingerprint),
        @BotHits = SUM(CASE WHEN BotScore >= 50 THEN 1 ELSE 0 END),
        @HumanHits = SUM(CASE WHEN BotScore < 50 THEN 1 ELSE 0 END),
        @AvgBot = ISNULL(AVG(CAST(BotScore AS DECIMAL(5,2))), 0),
        @ParsedRows = COUNT_BIG(*)
    FROM PiXL.Parsed WITH (NOLOCK)
    WHERE ParsedAt >= @HourStart AND ParsedAt < @HourEnd;

    SELECT @MatchCount = COUNT(*)
    FROM PiXL.Match
    WHERE MatchedAt >= @HourStart AND MatchedAt < @HourEnd;

    SELECT @NewDevices = COUNT(*)
    FROM PiXL.Device WITH (NOLOCK)
    WHERE FirstSeen >= @HourStart AND FirstSeen < @HourEnd;

    SELECT @NewIPs = COUNT(*)
    FROM PiXL.IP WITH (NOLOCK)
    WHERE FirstSeen >= @HourStart AND FirstSeen < @HourEnd;

    MERGE Ops.HourlyStats AS tgt
    USING (SELECT @HourUtc AS HourUtc) AS src ON tgt.HourUtc = src.HourUtc
    WHEN MATCHED THEN
        UPDATE SET
            TotalHits = ISNULL(@TotalHits, 0),
            UniqueIPs = ISNULL(@UIPs, 0),
            UniqueDevices = ISNULL(@UDevs, 0),
            BotHits = ISNULL(@BotHits, 0),
            HumanHits = ISNULL(@HumanHits, 0),
            AvgBotScore = ISNULL(@AvgBot, 0),
            MatchCount = ISNULL(@MatchCount, 0),
            NewDevices = ISNULL(@NewDevices, 0),
            NewIPs = ISNULL(@NewIPs, 0),
            ParsedRows = ISNULL(@ParsedRows, 0)
    WHEN NOT MATCHED THEN
        INSERT (HourUtc, TotalHits, UniqueIPs, UniqueDevices, BotHits, HumanHits,
                AvgBotScore, MatchCount, NewDevices, NewIPs, ParsedRows)
        VALUES (@HourUtc, ISNULL(@TotalHits, 0), ISNULL(@UIPs, 0), ISNULL(@UDevs, 0),
                ISNULL(@BotHits, 0), ISNULL(@HumanHits, 0), ISNULL(@AvgBot, 0),
                ISNULL(@MatchCount, 0), ISNULL(@NewDevices, 0), ISNULL(@NewIPs, 0),
                ISNULL(@ParsedRows, 0));
END
GO

PRINT 'Migration 72: Dashboard snapshot tables and procedures created.';
GO
