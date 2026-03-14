SET NOCOUNT ON;
GO

-- ============================================================================
-- Migration 65: Add legacy IP match watermarks to usp_Dash_PipelineHealth
-- ============================================================================
-- The pipeline SP only surfaced the email match watermark (MatchVisits).
-- This update adds the legacy IP match watermark (MatchLegacyVisits) so the
-- dashboard can show the full ETL pipeline: Parse → Visit → Email Match → IP Match.
-- ============================================================================

CREATE OR ALTER PROCEDURE dbo.usp_Dash_PipelineHealth
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE
        @RawRows        bigint,
        @ParsedRows     bigint,
        @DeviceRows     bigint,
        @IpRows         bigint,
        @VisitRows      bigint,
        @MatchRows      bigint,
        @MaxRawId       bigint,
        @MaxParsedSrcId bigint,
        @MaxVisitId     bigint,
        @MaxMatchId     bigint,
        -- Parse watermark
        @ParseWM        bigint,
        @ParseTotal     bigint,
        @ParseLastRun   datetime2,
        -- Email match watermark
        @EmailWM        bigint,
        @EmailTotal     bigint,
        @EmailMatched   bigint,
        @EmailLastRun   datetime2,
        -- Legacy IP match watermark
        @LegacyWM       bigint,
        @LegacyTotal    bigint,
        @LegacyMatched  bigint,
        @LegacyLastRun  datetime2,
        -- Match aggregates
        @Resolved       bigint,
        @Pending        bigint,
        @UniqueIndiv    bigint,
        @MatchLatest    datetime2,
        -- Timestamps
        @RawLatest      datetime2,
        @ParsedLatest   datetime2,
        @DeviceLatest   datetime,
        @IpLatest       datetime,
        @VisitLatest    datetime2,
        -- Lag
        @ParseLag       bigint,
        @EmailLag       bigint,
        @LegacyLag      bigint,
        -- Match type breakdown
        @IpMatches      bigint,
        @EmailMatches   bigint,
        @IpResolved     bigint,
        @EmailResolved  bigint;

    -- DMV row counts (instant)
    SELECT @RawRows    = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Raw')    AND index_id IN (0,1);
    SELECT @ParsedRows = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Parsed') AND index_id IN (0,1);
    SELECT @DeviceRows = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Device') AND index_id IN (0,1);
    SELECT @IpRows     = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.IP')     AND index_id IN (0,1);
    SELECT @VisitRows  = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Visit')  AND index_id IN (0,1);
    SELECT @MatchRows  = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Match')  AND index_id IN (0,1);

    -- MAX Ids (clustered index backward scan, instant)
    SELECT @MaxRawId       = MAX(Id)       FROM PiXL.Raw    WITH (NOLOCK);
    SELECT @MaxParsedSrcId = MAX(SourceId) FROM PiXL.Parsed WITH (NOLOCK);
    SELECT @MaxVisitId     = MAX(VisitID)  FROM PiXL.Visit  WITH (NOLOCK);
    SELECT @MaxMatchId     = MAX(MatchId)  FROM PiXL.Match  WITH (NOLOCK);

    -- Parse watermark
    SELECT @ParseWM = LastProcessedId, @ParseTotal = RowsProcessed, @ParseLastRun = LastRunAt
    FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits';

    -- Email match watermark
    SELECT @EmailWM = LastProcessedId, @EmailTotal = RowsProcessed,
           @EmailMatched = RowsMatched, @EmailLastRun = LastRunAt
    FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits';

    -- Legacy IP match watermark
    SELECT @LegacyWM = LastProcessedId, @LegacyTotal = RowsProcessed,
           @LegacyMatched = RowsMatched, @LegacyLastRun = LastRunAt
    FROM ETL.MatchWatermark WHERE ProcessName = 'MatchLegacyVisits';

    -- Match aggregates (single scan ~100ms)
    SELECT
        @Resolved    = SUM(CASE WHEN IndividualKey IS NOT NULL THEN 1 ELSE 0 END),
        @Pending     = SUM(CASE WHEN IndividualKey IS NULL     THEN 1 ELSE 0 END),
        @UniqueIndiv = APPROX_COUNT_DISTINCT(IndividualKey),
        @MatchLatest = MAX(LastSeen)
    FROM PiXL.Match WITH (NOLOCK);

    -- Match type breakdown
    SELECT
        @IpMatches     = SUM(CASE WHEN MatchType = 'ip'    THEN 1 ELSE 0 END),
        @EmailMatches  = SUM(CASE WHEN MatchType = 'email' THEN 1 ELSE 0 END),
        @IpResolved    = SUM(CASE WHEN MatchType = 'ip'    AND IndividualKey IS NOT NULL THEN 1 ELSE 0 END),
        @EmailResolved = SUM(CASE WHEN MatchType = 'email' AND IndividualKey IS NOT NULL THEN 1 ELSE 0 END)
    FROM PiXL.Match WITH (NOLOCK);

    -- Timestamps
    SELECT @RawLatest    = MAX(ReceivedAt) FROM PiXL.Raw    WITH (NOLOCK);
    SELECT @ParsedLatest = MAX(ReceivedAt) FROM PiXL.Parsed WITH (NOLOCK);
    SELECT @DeviceLatest = MAX(LastSeen)   FROM PiXL.Device WITH (NOLOCK);
    SELECT @IpLatest     = MAX(LastSeen)   FROM PiXL.IP     WITH (NOLOCK);
    SELECT TOP 1 @VisitLatest = CreatedAt  FROM PiXL.Visit  WITH (NOLOCK) ORDER BY VisitID DESC;

    -- Lag calculations
    SET @ParseLag  = @MaxRawId - ISNULL(@ParseWM, 0);
    SET @EmailLag  = ISNULL(@MaxVisitId, 0) - ISNULL(@EmailWM, 0);
    SET @LegacyLag = ISNULL(@MaxVisitId, 0) - ISNULL(@LegacyWM, 0);

    SELECT
        -- Table counts
        @RawRows        AS RawRows,         @ParsedRows     AS ParsedRows,
        @DeviceRows     AS DeviceRows,      @IpRows         AS IpRows,
        @VisitRows      AS VisitRows,       @MatchRows      AS MatchRows,
        -- Max IDs
        @MaxRawId       AS MaxRawId,        @MaxParsedSrcId AS MaxParsedSourceId,
        @MaxVisitId     AS MaxVisitId,      @MaxMatchId     AS MaxMatchId,
        -- Parse watermark
        @ParseWM        AS ParseWatermark,  @ParseTotal     AS ParseTotalProcessed,
        @ParseLastRun   AS ParseLastRunAt,
        -- Email match watermark
        @EmailWM        AS EmailMatchWatermark,
        @EmailTotal     AS EmailMatchProcessed,
        @EmailMatched   AS EmailMatchMatched,
        @EmailLastRun   AS EmailMatchLastRunAt,
        -- Legacy IP match watermark
        @LegacyWM       AS LegacyMatchWatermark,
        @LegacyTotal    AS LegacyMatchProcessed,
        @LegacyMatched  AS LegacyMatchMatched,
        @LegacyLastRun  AS LegacyMatchLastRunAt,
        -- Match aggregates
        @Resolved       AS MatchesResolved, @Pending        AS MatchesPending,
        @UniqueIndiv    AS UniqueIndividuals,
        -- Match type breakdown
        @IpMatches      AS IpMatchCount,    @EmailMatches   AS EmailMatchCount,
        @IpResolved     AS IpResolved,      @EmailResolved  AS EmailResolved,
        -- Lag
        @ParseLag       AS ParseLag,
        @EmailLag       AS EmailMatchLag,
        @LegacyLag      AS LegacyMatchLag,
        -- Timestamps
        @RawLatest      AS RawLatest,       @ParsedLatest   AS ParsedLatest,
        @DeviceLatest   AS DeviceLatest,    @IpLatest       AS IpLatest,
        @VisitLatest    AS VisitLatest,     @MatchLatest    AS MatchLatest,
        @DeviceRows     AS UniqueDevicesInVisits,
        @IpRows         AS UniqueIpsInVisits;
END;
GO

-- Benchmark
DECLARE @d datetime2;
SELECT @d = SYSUTCDATETIME();
EXEC dbo.usp_Dash_PipelineHealth;
PRINT 'SP Pipeline (updated): ' + CAST(DATEDIFF(ms,@d,SYSUTCDATETIME()) AS VARCHAR) + 'ms';
GO
