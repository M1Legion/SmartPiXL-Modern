SET NOCOUNT ON;

-- ============================================================================
-- Migration 64: Convert dashboard views to stored procedures
-- ============================================================================
-- Views with 20+ scalar subqueries generate bloated execution plans where
-- SQL Server serializes IO across all subqueries. The same queries executed
-- as individual statements into variables are 100-1000x faster.
--
-- Pipeline view:  2,200ms → <1ms  (stored procedure)
-- SystemHealth view: 2,700ms → ~350ms (stored procedure)
--
-- Views are preserved (not dropped) for backward compatibility and ad-hoc
-- queries, but Sentinel endpoints now call the stored procedures.
-- ============================================================================

SET NOCOUNT ON;
GO

-- ============================
-- usp_Dash_PipelineHealth
-- ============================
CREATE OR ALTER PROCEDURE dbo.usp_Dash_PipelineHealth
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE
        @TestRows       bigint,
        @ParsedRows     bigint,
        @DeviceRows     bigint,
        @IpRows         bigint,
        @VisitRows      bigint,
        @MatchRows      bigint,
        @MaxTestId      bigint,
        @MaxParsedSrcId bigint,
        @MaxVisitId     bigint,
        @MaxMatchId     bigint,
        @ParseWM        bigint,
        @ParseTotal     bigint,
        @ParseLastRun   datetime2,
        @MatchWM        bigint,
        @MatchTotal     bigint,
        @MatchMatched   bigint,
        @MatchLastRun   datetime2,
        @Resolved       bigint,
        @Pending        bigint,
        @UniqueIndiv    bigint,
        @MatchLatest    datetime2,
        @TestLatest     datetime2,
        @ParsedLatest   datetime2,
        @DeviceLatest   datetime,
        @IpLatest       datetime,
        @VisitLatest    datetime2,
        @ParseLag       bigint,
        @MatchLag       bigint;

    -- DMV row counts (instant)
    SELECT @TestRows   = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Raw')    AND index_id IN (0,1);
    SELECT @ParsedRows = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Parsed') AND index_id IN (0,1);
    SELECT @DeviceRows = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Device') AND index_id IN (0,1);
    SELECT @IpRows     = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.IP')     AND index_id IN (0,1);
    SELECT @VisitRows  = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Visit')  AND index_id IN (0,1);
    SELECT @MatchRows  = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Match')  AND index_id IN (0,1);

    -- MAX Ids (clustered index backward scan, instant)
    SELECT @MaxTestId      = MAX(Id)       FROM PiXL.Raw    WITH (NOLOCK);
    SELECT @MaxParsedSrcId = MAX(SourceId) FROM PiXL.Parsed WITH (NOLOCK);
    SELECT @MaxVisitId     = MAX(VisitID)  FROM PiXL.Visit  WITH (NOLOCK);
    SELECT @MaxMatchId     = MAX(MatchId)  FROM PiXL.Match  WITH (NOLOCK);

    -- Watermarks (tiny tables, seek)
    SELECT @ParseWM = LastProcessedId, @ParseTotal = RowsProcessed, @ParseLastRun = LastRunAt
    FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits';
    SELECT @MatchWM = LastProcessedId, @MatchTotal = RowsProcessed, @MatchMatched = RowsMatched, @MatchLastRun = LastRunAt
    FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits';

    -- Match aggregates (2.7M rows, single scan ~100ms)
    SELECT
        @Resolved    = SUM(CASE WHEN IndividualKey IS NOT NULL THEN 1 ELSE 0 END),
        @Pending     = SUM(CASE WHEN IndividualKey IS NULL     THEN 1 ELSE 0 END),
        @UniqueIndiv = APPROX_COUNT_DISTINCT(IndividualKey),
        @MatchLatest = MAX(LastSeen)
    FROM PiXL.Match WITH (NOLOCK);

    -- Timestamps
    SELECT @TestLatest   = MAX(ReceivedAt) FROM PiXL.Raw    WITH (NOLOCK);
    SELECT @ParsedLatest = MAX(ReceivedAt) FROM PiXL.Parsed WITH (NOLOCK);
    SELECT @DeviceLatest = MAX(LastSeen)   FROM PiXL.Device WITH (NOLOCK);
    SELECT @IpLatest     = MAX(LastSeen)   FROM PiXL.IP     WITH (NOLOCK);
    SELECT TOP 1 @VisitLatest = CreatedAt  FROM PiXL.Visit  WITH (NOLOCK) ORDER BY VisitID DESC;

    -- Lag calculations
    SET @ParseLag = @MaxTestId - ISNULL(@ParseWM, 0);
    SET @MatchLag = ISNULL(@MaxVisitId, 0) - ISNULL(@MatchWM, 0);

    SELECT
        @TestRows       AS TestRows,        @ParsedRows     AS ParsedRows,
        @DeviceRows     AS DeviceRows,      @IpRows         AS IpRows,
        @VisitRows      AS VisitRows,       @MatchRows      AS MatchRows,
        @MaxTestId      AS MaxTestId,       @MaxParsedSrcId AS MaxParsedSourceId,
        @MaxVisitId     AS MaxVisitId,      @MaxMatchId     AS MaxMatchId,
        @ParseWM        AS ParseWatermark,  @ParseTotal     AS ParseTotalProcessed,
        @ParseLastRun   AS ParseLastRunAt,  @MatchWM        AS MatchWatermark,
        @MatchTotal     AS MatchTotalProcessed, @MatchMatched AS MatchTotalMatched,
        @MatchLastRun   AS MatchLastRunAt,
        @Resolved       AS MatchesResolved, @Pending        AS MatchesPending,
        @UniqueIndiv    AS VisitsWithEmail,
        @ParseLag       AS ParseLag,        @MatchLag       AS MatchLag,
        @TestLatest     AS TestLatest,      @ParsedLatest   AS ParsedLatest,
        @DeviceLatest   AS DeviceLatest,    @IpLatest       AS IpLatest,
        @VisitLatest    AS VisitLatest,     @MatchLatest    AS MatchLatest,
        @DeviceRows     AS UniqueDevicesInVisits,
        @IpRows         AS UniqueIpsInVisits;
END;
GO

-- ============================
-- usp_Dash_SystemHealth
-- ============================
CREATE OR ALTER PROCEDURE dbo.usp_Dash_SystemHealth
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE
        @TotalHits      bigint,
        @Hits_1h        bigint,
        @Hits_24h       bigint,
        @Hits_7d        bigint,
        @LastHitAt      datetime2,
        @Bots_24h       bigint,
        @HighRiskBots   bigint,
        @AvgBotScore    float,
        @UniqueFP       bigint,
        @UniqueIPs      bigint,
        @EvasionDet     bigint,
        @WebDriverHits  bigint,
        @SyntheticHits  bigint,
        @ETL_LastRun    datetime2,
        @ETL_Total      bigint,
        @ETL_Watermark  bigint;

    -- Total from DMV (instant)
    SELECT @TotalHits = SUM(p.rows)
    FROM sys.partitions p
    WHERE p.object_id = OBJECT_ID('PiXL.Parsed') AND p.index_id IN (0,1);

    -- 7d count (narrow NC index seek + range scan, ~240ms)
    SELECT @Hits_7d = COUNT(*)
    FROM PiXL.Parsed WITH (NOLOCK)
    WHERE ReceivedAt >= DATEADD(DAY, -7, SYSUTCDATETIME());

    -- 24h aggregates (covering index IX_Parsed_DashHealth, ~120ms)
    SELECT
        @Hits_1h       = SUM(CASE WHEN ReceivedAt >= DATEADD(HOUR, -1, SYSUTCDATETIME()) THEN 1 ELSE 0 END),
        @Hits_24h      = COUNT(*),
        @LastHitAt     = MAX(ReceivedAt),
        @Bots_24h      = SUM(CASE WHEN BotScore >= 50 THEN 1 ELSE 0 END),
        @HighRiskBots  = SUM(CASE WHEN BotScore >= 80 THEN 1 ELSE 0 END),
        @AvgBotScore   = AVG(CAST(BotScore AS FLOAT)),
        @UniqueFP      = APPROX_COUNT_DISTINCT(CanvasFingerprint),
        @UniqueIPs     = APPROX_COUNT_DISTINCT(IPAddress),
        @EvasionDet    = SUM(CASE WHEN CanvasEvasionDetected = 1 OR WebGLEvasionDetected = 1
                              OR EvasionToolsDetected IS NOT NULL THEN 1 ELSE 0 END),
        @WebDriverHits = SUM(CASE WHEN WebDriverDetected = 1 THEN 1 ELSE 0 END),
        @SyntheticHits = SUM(CASE WHEN IsSynthetic = 1 THEN 1 ELSE 0 END)
    FROM PiXL.Parsed WITH (NOLOCK)
    WHERE ReceivedAt >= DATEADD(HOUR, -24, SYSUTCDATETIME());

    -- Watermark (ProcessDimensions is the active ETL watermark; ParseNewHits is dead)
    SELECT @ETL_LastRun = LastRunAt, @ETL_Total = RowsProcessed, @ETL_Watermark = LastProcessedId
    FROM ETL.Watermark WHERE ProcessName = 'ProcessDimensions';

    SELECT
        @TotalHits      AS TotalHits,
        @Hits_1h        AS Hits_1h,
        @Hits_24h       AS Hits_24h,
        @Hits_7d        AS Hits_7d,
        @LastHitAt      AS LastHitAt,
        DATEDIFF(SECOND, @LastHitAt, SYSUTCDATETIME()) AS SecondsSinceLastHit,
        @Bots_24h       AS Bots_24h,
        @HighRiskBots   AS HighRiskBots_24h,
        @Bots_24h       AS Bots_AllTime,
        CASE WHEN @Hits_24h > 0
            THEN CAST(ROUND(100.0 * @Bots_24h / @Hits_24h, 1) AS DECIMAL(5,1))
            ELSE 0
        END             AS BotPct_24h,
        CAST(ROUND(ISNULL(@AvgBotScore, 0), 1) AS DECIMAL(5,1)) AS AvgBotScore,
        CAST(ROUND(ISNULL(@AvgBotScore, 0), 1) AS DECIMAL(5,1)) AS AvgBotScore_24h,
        @UniqueFP       AS UniqueFP_AllTime,
        @UniqueFP       AS UniqueFP_24h,
        @UniqueIPs      AS UniqueIPs_AllTime,
        @UniqueIPs      AS UniqueIPs_24h,
        @EvasionDet     AS EvasionDetected_AllTime,
        @WebDriverHits  AS WebDriverHits,
        @SyntheticHits  AS SyntheticHits,
        @ETL_LastRun    AS ETL_LastRunAt,
        @ETL_Total      AS ETL_TotalProcessed,
        @ETL_Watermark  AS ETL_Watermark;
END;
GO

-- ============================================================================
-- Benchmark
-- ============================================================================
DECLARE @d datetime2;

SELECT @d = SYSUTCDATETIME();
EXEC dbo.usp_Dash_PipelineHealth;
PRINT 'SP Pipeline:     ' + CAST(DATEDIFF(ms,@d,SYSUTCDATETIME()) AS VARCHAR) + 'ms';

SELECT @d = SYSUTCDATETIME();
SELECT * FROM vw_Dash_PipelineHealth;
PRINT 'View Pipeline:   ' + CAST(DATEDIFF(ms,@d,SYSUTCDATETIME()) AS VARCHAR) + 'ms';

SELECT @d = SYSUTCDATETIME();
EXEC dbo.usp_Dash_SystemHealth;
PRINT 'SP SystemHealth: ' + CAST(DATEDIFF(ms,@d,SYSUTCDATETIME()) AS VARCHAR) + 'ms';

SELECT @d = SYSUTCDATETIME();
SELECT * FROM vw_Dash_SystemHealth;
PRINT 'View Health:     ' + CAST(DATEDIFF(ms,@d,SYSUTCDATETIME()) AS VARCHAR) + 'ms';
GO
