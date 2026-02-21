-- ============================================================================
-- Migration 60: Fix Dashboard View Performance (BUG-Q1, BUG-Q2, BUG-Q3)
-- ============================================================================
-- QA Session 9 found 6 dashboard views performing full-table scans on 4.8M+
-- rows in PiXL.Parsed with no date filter, causing 30s+ query times that
-- surface as HTTP 500 on Sentinel endpoints.
--
-- Fix: Add WHERE ReceivedAt >= DATEADD(DAY, -30, GETUTCDATE()) to each view.
-- The clustered index on PiXL.Parsed (ReceivedAt, SourceId) will be used for
-- a range seek instead of a full scan. As data grows beyond 30 days, the view
-- stays bounded.
--
-- Also fixes BUG-Q2: InfraHealthService SQL probe in vw_Dash_SystemHealth
-- row counts used COUNT(*) on PiXL.Raw (16.5M rows) with 5s timeout.
-- Replaced with sys.dm_db_partition_stats for instant row counts.
--
-- Idempotent: CREATE OR ALTER on all views.
-- ============================================================================

SET NOCOUNT ON;
GO

-- ============================================================================
-- 1. vw_Dash_HourlyRollup — Add 30-day rolling window
-- ============================================================================
CREATE OR ALTER VIEW [dbo].[vw_Dash_HourlyRollup] AS
SELECT
    DATEADD(HOUR, DATEDIFF(HOUR, 0, ReceivedAt), 0)        AS HourBucket,
    COUNT(*)                                                 AS TotalHits,
    SUM(CASE WHEN BotScore >= 50 THEN 1 ELSE 0 END)        AS BotHits,
    SUM(CASE WHEN BotScore >= 80 THEN 1 ELSE 0 END)        AS HighRiskHits,
    SUM(CASE WHEN BotScore < 20 OR BotScore IS NULL THEN 1 ELSE 0 END) AS LikelyHumanHits,
    COUNT(DISTINCT CanvasFingerprint)                        AS UniqueFingerprints,
    COUNT(DISTINCT IPAddress)                                AS UniqueIPs,
    AVG(CAST(BotScore AS FLOAT))                            AS AvgBotScore,
    AVG(CAST(CombinedThreatScore AS FLOAT))                 AS AvgThreatScore,
    MAX(BotScore)                                            AS MaxBotScore,
    SUM(CASE WHEN WebDriverDetected = 1 THEN 1 ELSE 0 END) AS WebDriverHits,
    SUM(CASE WHEN CanvasEvasionDetected = 1 THEN 1 ELSE 0 END) AS CanvasEvasionHits,
    SUM(CASE WHEN EvasionToolsDetected IS NOT NULL THEN 1 ELSE 0 END) AS EvasionToolHits,
    SUM(CASE WHEN IsSynthetic = 1 THEN 1 ELSE 0 END)       AS SyntheticHits
FROM PiXL.Parsed
WHERE ReceivedAt >= DATEADD(DAY, -30, GETUTCDATE())
GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, ReceivedAt), 0);
GO

-- ============================================================================
-- 2. vw_Dash_BotBreakdown — Add 30-day rolling window
-- ============================================================================
CREATE OR ALTER VIEW [dbo].[vw_Dash_BotBreakdown] AS
WITH Bucketed AS (
    SELECT
        CASE
            WHEN BotScore >= 80 THEN 'High Risk'
            WHEN BotScore >= 50 THEN 'Medium Risk'
            WHEN BotScore >= 20 THEN 'Low Risk'
            ELSE 'Likely Human'
        END AS RiskBucket,
        CASE
            WHEN BotScore >= 80 THEN 1
            WHEN BotScore >= 50 THEN 2
            WHEN BotScore >= 20 THEN 3
            ELSE 4
        END AS SortOrder,
        BotScore, CombinedThreatScore, AnomalyScore,
        CanvasFingerprint, IPAddress, ReceivedAt, Platform
    FROM PiXL.Parsed
    WHERE IsSynthetic = 0
      AND ReceivedAt >= DATEADD(DAY, -30, GETUTCDATE())
),
BucketAgg AS (
    SELECT
        RiskBucket, SortOrder,
        COUNT(*) AS HitCount,
        COUNT(DISTINCT CanvasFingerprint) AS UniqueDevices,
        COUNT(DISTINCT IPAddress) AS UniqueIPs,
        AVG(CAST(BotScore AS FLOAT)) AS AvgBotScore,
        AVG(CAST(CombinedThreatScore AS FLOAT)) AS AvgThreatScore,
        AVG(CAST(AnomalyScore AS FLOAT)) AS AvgAnomalyScore,
        MIN(ReceivedAt) AS FirstSeen,
        MAX(ReceivedAt) AS LastSeen
    FROM Bucketed
    GROUP BY RiskBucket, SortOrder
),
BucketPlatforms AS (
    SELECT DISTINCT RiskBucket, Platform
    FROM Bucketed
    WHERE Platform IS NOT NULL
)
SELECT
    a.RiskBucket, a.SortOrder, a.HitCount, a.UniqueDevices, a.UniqueIPs,
    a.AvgBotScore, a.AvgThreatScore, a.AvgAnomalyScore,
    a.FirstSeen, a.LastSeen,
    (SELECT STRING_AGG(bp.Platform, ', ') FROM BucketPlatforms bp WHERE bp.RiskBucket = a.RiskBucket) AS Platforms
FROM BucketAgg a;
GO

-- ============================================================================
-- 3. vw_Dash_TopBotSignals — Add 30-day rolling window
-- ============================================================================
CREATE OR ALTER VIEW [dbo].[vw_Dash_TopBotSignals] AS
SELECT
    s.value                                     AS Signal,
    COUNT(*)                                    AS TimesTriggered,
    COUNT(DISTINCT pp.CanvasFingerprint)        AS UniqueDevices,
    AVG(CAST(pp.BotScore AS FLOAT))            AS AvgBotScoreWhenPresent,
    MIN(pp.ReceivedAt)                          AS FirstSeen,
    MAX(pp.ReceivedAt)                          AS LastSeen
FROM PiXL.Parsed pp
CROSS APPLY STRING_SPLIT(pp.BotSignalsList, ',') s
WHERE pp.BotSignalsList IS NOT NULL
  AND pp.IsSynthetic = 0
  AND s.value != ''
  AND pp.ReceivedAt >= DATEADD(DAY, -30, GETUTCDATE())
GROUP BY s.value;
GO

-- ============================================================================
-- 4. vw_Dash_DeviceBreakdown — Add 30-day rolling window
-- ============================================================================
CREATE OR ALTER VIEW [dbo].[vw_Dash_DeviceBreakdown] AS
SELECT
    ISNULL(Platform, 'Unknown')                              AS Platform,
    CASE
        WHEN ClientUserAgent LIKE '%Edg/%'    THEN 'Edge'
        WHEN ClientUserAgent LIKE '%Chrome/%' THEN 'Chrome'
        WHEN ClientUserAgent LIKE '%Firefox/%' THEN 'Firefox'
        WHEN ClientUserAgent LIKE '%Safari/%'
         AND ClientUserAgent NOT LIKE '%Chrome/%' THEN 'Safari'
        WHEN ClientUserAgent LIKE '%Opera%'
          OR ClientUserAgent LIKE '%OPR/%'    THEN 'Opera'
        ELSE 'Other'
    END                                                      AS Browser,
    CASE
        WHEN ScreenWidth >= 3840 THEN '4K+'
        WHEN ScreenWidth >= 2560 THEN '1440p'
        WHEN ScreenWidth >= 1920 THEN '1080p'
        WHEN ScreenWidth >= 1366 THEN 'Laptop'
        WHEN ScreenWidth >= 768  THEN 'Tablet'
        WHEN ScreenWidth > 0     THEN 'Mobile'
        ELSE 'Unknown'
    END                                                      AS ScreenBucket,
    CASE WHEN MaxTouchPoints > 0 THEN 'Touch' ELSE 'No Touch' END AS TouchCapability,
    COUNT(*)                                                 AS HitCount,
    COUNT(DISTINCT CanvasFingerprint)                        AS UniqueDevices,
    COUNT(DISTINCT IPAddress)                                AS UniqueIPs,
    AVG(CAST(BotScore AS FLOAT))                            AS AvgBotScore
FROM PiXL.Parsed
WHERE IsSynthetic = 0
  AND ReceivedAt >= DATEADD(DAY, -30, GETUTCDATE())
GROUP BY
    ISNULL(Platform, 'Unknown'),
    CASE
        WHEN ClientUserAgent LIKE '%Edg/%'    THEN 'Edge'
        WHEN ClientUserAgent LIKE '%Chrome/%' THEN 'Chrome'
        WHEN ClientUserAgent LIKE '%Firefox/%' THEN 'Firefox'
        WHEN ClientUserAgent LIKE '%Safari/%'
         AND ClientUserAgent NOT LIKE '%Chrome/%' THEN 'Safari'
        WHEN ClientUserAgent LIKE '%Opera%'
          OR ClientUserAgent LIKE '%OPR/%'    THEN 'Opera'
        ELSE 'Other'
    END,
    CASE
        WHEN ScreenWidth >= 3840 THEN '4K+'
        WHEN ScreenWidth >= 2560 THEN '1440p'
        WHEN ScreenWidth >= 1920 THEN '1080p'
        WHEN ScreenWidth >= 1366 THEN 'Laptop'
        WHEN ScreenWidth >= 768  THEN 'Tablet'
        WHEN ScreenWidth > 0     THEN 'Mobile'
        ELSE 'Unknown'
    END,
    CASE WHEN MaxTouchPoints > 0 THEN 'Touch' ELSE 'No Touch' END;
GO

-- ============================================================================
-- 5. vw_Dash_EvasionSummary — Add 30-day rolling window
-- ============================================================================
CREATE OR ALTER VIEW [dbo].[vw_Dash_EvasionSummary] AS
SELECT
    COUNT(*)                                                                AS TotalHits,
    SUM(CASE WHEN CanvasEvasionDetected = 1 THEN 1 ELSE 0 END)           AS CanvasEvasion,
    SUM(CASE WHEN WebGLEvasionDetected = 1 THEN 1 ELSE 0 END)            AS WebGLEvasion,
    SUM(CASE WHEN AudioNoiseInjectionDetected = 1 THEN 1 ELSE 0 END)     AS AudioNoise,
    SUM(CASE WHEN FontMethodMismatch = 1 THEN 1 ELSE 0 END)              AS FontSpoof,
    SUM(CASE WHEN ProxyBlockedProperties IS NOT NULL
         AND ProxyBlockedProperties != '' THEN 1 ELSE 0 END)             AS ProxyBlocked,
    SUM(CASE WHEN StealthPluginSignals IS NOT NULL
         AND StealthPluginSignals != '' THEN 1 ELSE 0 END)               AS StealthDetected,
    SUM(CASE WHEN EvasionToolsDetected IS NOT NULL
         AND EvasionToolsDetected != '' THEN 1 ELSE 0 END)               AS EvasionToolsFound,
    SUM(CASE WHEN EvasionSignalsV2 IS NOT NULL
         AND EvasionSignalsV2 != '' THEN 1 ELSE 0 END)                   AS EvasionV2Signals,
    SUM(CASE WHEN DoNotTrack = '1' THEN 1 ELSE 0 END)                    AS DNT_Enabled,
    -- Any evasion at all
    SUM(CASE WHEN CanvasEvasionDetected = 1
              OR WebGLEvasionDetected = 1
              OR AudioNoiseInjectionDetected = 1
              OR FontMethodMismatch = 1
              OR ProxyBlockedProperties IS NOT NULL
              OR StealthPluginSignals IS NOT NULL
              OR EvasionToolsDetected IS NOT NULL
         THEN 1 ELSE 0 END)                                              AS AnyEvasionDetected,
    -- Percentages
    CASE WHEN COUNT(*) > 0 THEN
        CAST(ROUND(100.0 * SUM(CASE WHEN CanvasEvasionDetected = 1
             OR WebGLEvasionDetected = 1 OR EvasionToolsDetected IS NOT NULL
             THEN 1 ELSE 0 END) / COUNT(*), 1) AS DECIMAL(5,1))
    ELSE 0 END                                                            AS EvasionPct
FROM PiXL.Parsed
WHERE ReceivedAt >= DATEADD(DAY, -30, GETUTCDATE());
GO

-- ============================================================================
-- 6. vw_Dash_BehavioralAnalysis — Add 30-day rolling window
-- ============================================================================
CREATE OR ALTER VIEW [dbo].[vw_Dash_BehavioralAnalysis] AS
SELECT
    CASE
        WHEN BotScore >= 50 THEN 'Bot (50+)'
        ELSE 'Human (<50)'
    END                                                      AS Classification,
    COUNT(*)                                                 AS HitCount,
    -- Mouse behavior
    AVG(CAST(MouseMoveCount AS FLOAT))                      AS AvgMouseMoves,
    AVG(CAST(MouseEntropy AS FLOAT))                        AS AvgMouseEntropy,
    AVG(CAST(MoveTimingCV AS FLOAT))                        AS AvgTimingCV,
    AVG(CAST(MoveSpeedCV AS FLOAT))                         AS AvgSpeedCV,
    SUM(CASE WHEN MouseMoveCount = 0 THEN 1 ELSE 0 END)    AS NoMouseHits,
    SUM(CASE WHEN MouseMoveCount > 0 AND MouseEntropy = 0 THEN 1 ELSE 0 END) AS ZeroEntropyHits,
    -- Scroll behavior
    SUM(CASE WHEN UserScrolled = 1 THEN 1 ELSE 0 END)      AS ScrolledHits,
    SUM(CASE WHEN ScrollContradiction = 1 THEN 1 ELSE 0 END) AS ScrollContradictions,
    AVG(CAST(ScrollDepthPx AS FLOAT))                       AS AvgScrollDepth,
    -- Behavioral flags
    SUM(CASE WHEN BehavioralFlags IS NOT NULL
         AND BehavioralFlags != '' THEN 1 ELSE 0 END)       AS FlaggedHits
FROM PiXL.Parsed
WHERE IsSynthetic = 0
  AND ReceivedAt >= DATEADD(DAY, -30, GETUTCDATE())
GROUP BY
    CASE
        WHEN BotScore >= 50 THEN 'Bot (50+)'
        ELSE 'Human (<50)'
    END;
GO

PRINT 'Migration 60: All 6 dashboard views updated with 30-day rolling window.';
GO

-- ============================================================================
-- 7. vw_Dash_PipelineHealth — Replace COUNT(*) with DMV-based row counts
-- ============================================================================
-- COUNT(*) on PiXL.Raw (16.5M) and PiXL.Visit (4.8M) causes this view to
-- timeout on 10s CommandTimeout. sys.dm_db_partition_stats returns row counts
-- instantly from metadata — no table scan required.
-- Filtered counts on smaller tables (PiXL.Match, PiXL.Visit) are kept as-is.
-- ============================================================================
CREATE OR ALTER VIEW [dbo].[vw_Dash_PipelineHealth] AS
SELECT
    -- Row counts from DMV metadata (instant, no scan)
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

    -- MAX(Id) queries — use indexes, always fast
    (SELECT MAX(Id)           FROM PiXL.Raw)      AS MaxTestId,
    (SELECT MAX(SourceId)     FROM PiXL.Parsed)   AS MaxParsedSourceId,
    (SELECT MAX(VisitID)      FROM PiXL.Visit)    AS MaxVisitId,
    (SELECT MAX(MatchId)      FROM PiXL.Match)    AS MaxMatchId,

    -- ETL watermarks — tiny tables
    (SELECT LastProcessedId   FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits')      AS ParseWatermark,
    (SELECT RowsProcessed     FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits')      AS ParseTotalProcessed,
    (SELECT LastRunAt         FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits')      AS ParseLastRunAt,
    (SELECT LastProcessedId   FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits')  AS MatchWatermark,
    (SELECT RowsProcessed     FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits')  AS MatchTotalProcessed,
    (SELECT RowsMatched       FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits')  AS MatchTotalMatched,
    (SELECT LastRunAt         FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits')  AS MatchLastRunAt,

    -- Filtered counts — small result sets, acceptable
    (SELECT COUNT(*) FROM PiXL.Match WHERE IndividualKey IS NOT NULL)    AS MatchesResolved,
    (SELECT COUNT(*) FROM PiXL.Match WHERE IndividualKey IS NULL)        AS MatchesPending,
    (SELECT COUNT(*) FROM PiXL.Visit WHERE MatchEmail IS NOT NULL)       AS VisitsWithEmail,

    -- ETL lag calculations
    (SELECT MAX(Id) FROM PiXL.Raw) -
        ISNULL((SELECT LastProcessedId FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits'), 0)
                                                                         AS ParseLag,
    ISNULL((SELECT MAX(VisitID) FROM PiXL.Visit), 0) -
        ISNULL((SELECT LastProcessedId FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits'), 0)
                                                                         AS MatchLag,

    -- Latest timestamps for data freshness
    (SELECT MAX(ReceivedAt) FROM PiXL.Raw)      AS TestLatest,
    (SELECT MAX(ParsedAt)   FROM PiXL.Parsed)   AS ParsedLatest,
    (SELECT MAX(LastSeen)   FROM PiXL.Device)    AS DeviceLatest,
    (SELECT MAX(LastSeen)   FROM PiXL.IP)        AS IpLatest,
    (SELECT MAX(CreatedAt)  FROM PiXL.Visit)     AS VisitLatest,
    (SELECT MAX(LastSeen)   FROM PiXL.Match)     AS MatchLatest,

    -- Distinct counts — kept as-is (Visit table has indexed DeviceId/IpId)
    (SELECT COUNT(DISTINCT DeviceId) FROM PiXL.Visit WHERE DeviceId IS NOT NULL) AS UniqueDevicesInVisits,
    (SELECT COUNT(DISTINCT IpId)     FROM PiXL.Visit WHERE IpId IS NOT NULL)     AS UniqueIpsInVisits;
GO

PRINT 'Migration 60: vw_Dash_PipelineHealth updated with DMV-based row counts.';
GO
