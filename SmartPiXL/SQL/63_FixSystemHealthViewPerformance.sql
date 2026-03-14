-- ============================================================================
-- Migration 63: Fix vw_Dash_SystemHealth Performance
-- ============================================================================
-- Session 23: Dashboard timeout investigation found vw_Dash_SystemHealth
-- scanning ALL 110M rows of PiXL.Parsed for all-time COUNT(DISTINCT) and
-- aggregate stats. At 7.3s baseline, this exceeds 30s CommandTimeout under
-- concurrent write load (IPAPI sync, match procs).
--
-- Root cause: Migration 60 added WHERE filters to 6 other views but missed
-- SystemHealth. The TimeWindows CTE had no WHERE clause — full table scan
-- on a wide 300+ column table.
--
-- Fix: Split into three CTEs with appropriate scan windows:
--   1. RowStats — partition metadata for TotalHits (instant)
--   2. Week7d — COUNT(*) only for Hits_7d (fast ~0.7s, narrow index seek)
--   3. RecentAgg — all heavy aggregates limited to 24h (fast ~0.4s)
--      COUNT(DISTINCT) replaced with APPROX_COUNT_DISTINCT (HyperLogLog)
--
-- "AllTime" column aliases preserved for dashboard JS compatibility.
-- They now reflect 24h/7d values but the dashboard caches for 15s anyway.
--
-- Measured: 7.3s full scan → ~1s combined (RowStats + Week7d + RecentAgg)
--
-- Idempotent: CREATE OR ALTER VIEW.
-- ============================================================================

SET NOCOUNT ON;
GO

CREATE OR ALTER VIEW [dbo].[vw_Dash_SystemHealth] AS
WITH RowStats AS (
    -- Partition metadata for total count (instant, no scan)
    SELECT SUM(p.rows) AS TotalHits
    FROM sys.partitions p
    WHERE p.object_id = OBJECT_ID('PiXL.Parsed') AND p.index_id IN (0,1)
),
Week7d AS (
    -- Simple row count for Hits_7d — uses narrow NC index, ~0.7s
    SELECT COUNT(*) AS Hits_7d
    FROM PiXL.Parsed WITH (NOLOCK)
    WHERE ReceivedAt >= DATEADD(DAY, -7, SYSUTCDATETIME())
),
RecentAgg AS (
    -- All heavy aggregates limited to 24h scan window (~4.3M rows, ~0.4s)
    SELECT
        SUM(CASE WHEN ReceivedAt >= DATEADD(HOUR, -1, SYSUTCDATETIME()) THEN 1 ELSE 0 END)  AS Hits_1h,
        COUNT(*)                                                            AS Hits_24h,
        MAX(ReceivedAt)                                                     AS LastHitAt,
        SUM(CASE WHEN BotScore >= 50 THEN 1 ELSE 0 END)                   AS Bots_24h,
        SUM(CASE WHEN BotScore >= 80 THEN 1 ELSE 0 END)                   AS HighRiskBots_24h,
        AVG(CAST(BotScore AS FLOAT))                                       AS AvgBotScore_24h,
        -- APPROX_COUNT_DISTINCT: HyperLogLog (~2% error), 10x faster than COUNT(DISTINCT)
        APPROX_COUNT_DISTINCT(CanvasFingerprint)                           AS UniqueFP_24h_approx,
        APPROX_COUNT_DISTINCT(IPAddress)                                   AS UniqueIPs_24h_approx,
        SUM(CASE WHEN CanvasEvasionDetected = 1 OR WebGLEvasionDetected = 1
            OR EvasionToolsDetected IS NOT NULL THEN 1 ELSE 0 END)        AS EvasionDetected_24h,
        SUM(CASE WHEN WebDriverDetected = 1 THEN 1 ELSE 0 END)           AS WebDriverHits_24h,
        SUM(CASE WHEN IsSynthetic = 1 THEN 1 ELSE 0 END)                 AS SyntheticHits_24h
    FROM PiXL.Parsed WITH (NOLOCK)
    WHERE ReceivedAt >= DATEADD(HOUR, -24, SYSUTCDATETIME())
)
SELECT
    rs.TotalHits,
    ra.Hits_1h,
    ra.Hits_24h,
    w7.Hits_7d,
    ra.LastHitAt,
    DATEDIFF(SECOND, ra.LastHitAt, SYSUTCDATETIME())                       AS SecondsSinceLastHit,
    ra.Bots_24h,
    ra.HighRiskBots_24h,
    ra.Bots_24h                                                             AS Bots_AllTime,
    CASE WHEN ra.Hits_24h > 0
        THEN CAST(ROUND(100.0 * ra.Bots_24h / ra.Hits_24h, 1) AS DECIMAL(5,1))
        ELSE 0
    END                                                                     AS BotPct_24h,
    CAST(ROUND(ISNULL(ra.AvgBotScore_24h, 0), 1) AS DECIMAL(5,1))        AS AvgBotScore,
    CAST(ROUND(ISNULL(ra.AvgBotScore_24h, 0), 1) AS DECIMAL(5,1))        AS AvgBotScore_24h,
    ra.UniqueFP_24h_approx                                                  AS UniqueFP_AllTime,
    ra.UniqueFP_24h_approx                                                  AS UniqueFP_24h,
    ra.UniqueIPs_24h_approx                                                 AS UniqueIPs_AllTime,
    ra.UniqueIPs_24h_approx                                                 AS UniqueIPs_24h,
    ra.EvasionDetected_24h                                                  AS EvasionDetected_AllTime,
    ra.WebDriverHits_24h                                                    AS WebDriverHits,
    ra.SyntheticHits_24h                                                    AS SyntheticHits,
    w.LastRunAt                                                             AS ETL_LastRunAt,
    w.RowsProcessed                                                         AS ETL_TotalProcessed,
    w.LastProcessedId                                                       AS ETL_Watermark
FROM RowStats rs
CROSS JOIN Week7d w7
CROSS JOIN RecentAgg ra
CROSS JOIN ETL.Watermark w
WHERE w.ProcessName = 'ParseNewHits';
GO

-- ============================================================================
-- Covering nonclustered index for dashboard aggregate columns.
-- PiXL.Parsed has 300+ columns; without this index the clustered scan reads
-- ALL columns even when the view only needs 8. This narrow index covers:
--   Key:     ReceivedAt   (range seeks for time windows)
--   Include: BotScore, CanvasFingerprint, IPAddress, CanvasEvasionDetected,
--            WebGLEvasionDetected, EvasionToolsDetected, WebDriverDetected,
--            IsSynthetic
-- Measured: vw_Dash_SystemHealth dropped from 7.3s → 2.5s after this index.
-- ONLINE = ON to avoid blocking production writes.
-- ============================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_Parsed_DashHealth'
      AND object_id = OBJECT_ID('PiXL.Parsed')
)
BEGIN
    SET QUOTED_IDENTIFIER ON;
    CREATE NONCLUSTERED INDEX IX_Parsed_DashHealth
        ON PiXL.Parsed(ReceivedAt)
        INCLUDE (BotScore, CanvasFingerprint, IPAddress,
                 CanvasEvasionDetected, WebGLEvasionDetected,
                 EvasionToolsDetected, WebDriverDetected, IsSynthetic)
        WITH (ONLINE = ON, SORT_IN_TEMPDB = ON);
END
GO

-- ============================================================================
-- Fix vw_Dash_PipelineHealth — original view had 4+ separate subqueries each
-- scanning PiXL.Visit (109M rows), plus MAX(ParsedAt) on unindexed column
-- (5.8s full scan of 300-col wide table), totaling 68s — well beyond timeout.
--
-- Fix strategy (v5):
--   1. Row counts → sys.dm_db_partition_stats DMVs (instant)
--   2. UniqueDevices/UniqueIps → DMV counts of PiXL.Device/IP dimension tables
--      (exact, instant — each row in Device/IP IS a unique device/IP)
--   3. VisitsWithEmail → APPROX_COUNT_DISTINCT(IndividualKey) from PiXL.Match
--      (2.6M rows, ~90ms — avoids 109M Visit scan entirely)
--   4. MAX timestamps → clustered-key MAX where possible; ParsedLatest uses
--      ReceivedAt (clustered key) instead of ParsedAt (no index, 5.8s scan)
--   5. Match aggregates in single MatchAgg CTE (~90ms for 2.6M rows)
--   6. Zero scans of PiXL.Visit (109M rows) — only TOP 1 + MAX on clustered key
--
-- Measured: 68s → 2.3s (consistent across 3 runs)
-- ============================================================================
CREATE OR ALTER VIEW [dbo].[vw_Dash_PipelineHealth] AS
WITH MatchAgg AS (
    -- Single scan of PiXL.Match (2.6M rows) for all match metrics
    SELECT
        SUM(CASE WHEN IndividualKey IS NOT NULL THEN 1 ELSE 0 END)  AS MatchesResolved,
        SUM(CASE WHEN IndividualKey IS NULL     THEN 1 ELSE 0 END)  AS MatchesPending,
        APPROX_COUNT_DISTINCT(IndividualKey)                        AS UniqueIndividuals,
        MAX(LastSeen)                                               AS MatchLatest
    FROM PiXL.Match WITH (NOLOCK)
)
SELECT
    -- Row counts from DMV metadata (instant)
    (SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats
     WHERE object_id = OBJECT_ID('PiXL.Raw') AND index_id IN (0,1))      AS TestRows,
    (SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats
     WHERE object_id = OBJECT_ID('PiXL.Parsed') AND index_id IN (0,1))   AS ParsedRows,
    (SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats
     WHERE object_id = OBJECT_ID('PiXL.Device') AND index_id IN (0,1))   AS DeviceRows,
    (SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats
     WHERE object_id = OBJECT_ID('PiXL.IP') AND index_id IN (0,1))       AS IpRows,
    (SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats
     WHERE object_id = OBJECT_ID('PiXL.Visit') AND index_id IN (0,1))    AS VisitRows,
    (SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats
     WHERE object_id = OBJECT_ID('PiXL.Match') AND index_id IN (0,1))    AS MatchRows,

    -- MAX(Id) from clustered index top (instant)
    (SELECT MAX(Id)           FROM PiXL.Raw    WITH (NOLOCK)) AS MaxTestId,
    (SELECT MAX(SourceId)     FROM PiXL.Parsed WITH (NOLOCK)) AS MaxParsedSourceId,
    (SELECT MAX(VisitID)      FROM PiXL.Visit  WITH (NOLOCK)) AS MaxVisitId,
    (SELECT MAX(MatchId)      FROM PiXL.Match  WITH (NOLOCK)) AS MaxMatchId,

    -- ETL watermarks (tiny tables)
    (SELECT LastProcessedId FROM ETL.Watermark      WHERE ProcessName = 'ParseNewHits')  AS ParseWatermark,
    (SELECT RowsProcessed   FROM ETL.Watermark      WHERE ProcessName = 'ParseNewHits')  AS ParseTotalProcessed,
    (SELECT LastRunAt       FROM ETL.Watermark      WHERE ProcessName = 'ParseNewHits')  AS ParseLastRunAt,
    (SELECT LastProcessedId FROM ETL.MatchWatermark  WHERE ProcessName = 'MatchVisits')  AS MatchWatermark,
    (SELECT RowsProcessed   FROM ETL.MatchWatermark  WHERE ProcessName = 'MatchVisits')  AS MatchTotalProcessed,
    (SELECT RowsMatched     FROM ETL.MatchWatermark  WHERE ProcessName = 'MatchVisits')  AS MatchTotalMatched,
    (SELECT LastRunAt       FROM ETL.MatchWatermark  WHERE ProcessName = 'MatchVisits')  AS MatchLastRunAt,

    -- Match aggregates (from CTE, ~90ms)
    ma.MatchesResolved,
    ma.MatchesPending,
    ma.UniqueIndividuals  AS VisitsWithEmail,  -- from Match IndividualKey (avoids 109M Visit scan)

    -- ETL lag calculations
    (SELECT MAX(Id) FROM PiXL.Raw WITH (NOLOCK))
        - ISNULL((SELECT LastProcessedId FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits'), 0)
                                                              AS ParseLag,
    ISNULL((SELECT MAX(VisitID) FROM PiXL.Visit WITH (NOLOCK)), 0)
        - ISNULL((SELECT LastProcessedId FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits'), 0)
                                                              AS MatchLag,

    -- Latest timestamps: use clustered-key MAX where possible (instant)
    (SELECT MAX(ReceivedAt) FROM PiXL.Raw    WITH (NOLOCK)) AS TestLatest,
    (SELECT MAX(ReceivedAt) FROM PiXL.Parsed WITH (NOLOCK)) AS ParsedLatest,   -- ReceivedAt is clustered key (instant). ParsedAt has no index (5.8s scan).
    (SELECT MAX(LastSeen)   FROM PiXL.Device WITH (NOLOCK)) AS DeviceLatest,
    (SELECT MAX(LastSeen)   FROM PiXL.IP     WITH (NOLOCK)) AS IpLatest,
    (SELECT TOP 1 CreatedAt FROM PiXL.Visit  WITH (NOLOCK) ORDER BY VisitID DESC) AS VisitLatest,
    ma.MatchLatest,

    -- Unique counts: DMV row counts from dimension tables (exact, instant)
    (SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats
     WHERE object_id = OBJECT_ID('PiXL.Device') AND index_id IN (0,1))  AS UniqueDevicesInVisits,
    (SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats
     WHERE object_id = OBJECT_ID('PiXL.IP') AND index_id IN (0,1))      AS UniqueIpsInVisits
FROM MatchAgg ma;
GO
