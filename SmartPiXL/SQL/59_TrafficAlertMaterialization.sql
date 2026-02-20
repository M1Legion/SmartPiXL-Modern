-- =====================================================================
-- Migration 59: TrafficAlert Materialization Procs + Dashboard Views
-- Phase 9 of SmartPiXL workplan
--
-- Creates:
--   ETL.usp_MaterializeVisitorScores  — per-visit scoring (watermark-based)
--   ETL.usp_MaterializeCustomerSummary — daily/weekly/monthly rollups
--   dbo.vw_TrafficAlert_VisitorDetail  — single-visitor scoring breakdown
--   dbo.vw_TrafficAlert_CustomerOverview — customer summary with quality
--   dbo.vw_TrafficAlert_Trend          — time-series of customer metrics
--
-- Scoring algorithms:
--   Mouse Authenticity (0-100): entropy + timing CV + speed CV + move count
--                               + replay detection + scroll contradiction
--   Session Quality (0-100):   page count + duration + navigation pattern
--   Composite Quality (0-100): weighted blend of all signals
--
-- Uses separate UPDATE + INSERT instead of MERGE for performance.
--
-- Design doc reference: §7 (TrafficAlert Subsystem)
-- =====================================================================
SET QUOTED_IDENTIFIER ON;
GO
USE SmartPiXL;
GO

PRINT '--- 59: TrafficAlert Materialization ---';
GO

-- =====================================================================
-- Step 1: ETL.usp_MaterializeVisitorScores
--
-- Called after usp_ParseNewHits. Reads PiXL.Visit + PiXL.Parsed for
-- newly parsed visits, computes Mouse Authenticity, Session Quality,
-- and Composite Quality, then inserts into TrafficAlert.VisitorScore.
--
-- Watermark: ETL.Watermark WHERE ProcessName = 'MaterializeVisitorScores'
-- Tracks PiXL.Visit.VisitID (same identity key space as ParseNewHits).
-- =====================================================================
CREATE OR ALTER PROCEDURE ETL.usp_MaterializeVisitorScores
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ProcessName VARCHAR(50) = 'MaterializeVisitorScores';
    DECLARE @StartTime DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @LastId BIGINT;
    DECLARE @MaxId BIGINT;
    DECLARE @RowsInserted INT = 0;

    -- Get watermark
    SELECT @LastId = LastProcessedId FROM ETL.Watermark WHERE ProcessName = @ProcessName;
    SELECT @MaxId = MAX(VisitID) FROM PiXL.Visit;

    -- Nothing new
    IF @MaxId IS NULL OR @MaxId <= @LastId
    BEGIN
        SELECT 0 AS RowsMaterialized, 0 AS ElapsedMs;
        RETURN;
    END;

    -- Stage new visits into a temp table with computed derived scores
    SELECT
        v.VisitID,
        v.DeviceId,
        v.CompanyID,
        v.ReceivedAt,

        -- Raw scores from PiXL Script + Forge
        p.BotScore,
        p.AnomalyScore,
        p.CombinedThreatScore,
        p.LeadQualityScore,
        p.AffluenceSignal,
        p.CulturalConsistencyScore  AS CulturalConsistency,
        p.ContradictionCount,

        -- =========================================================
        -- Mouse Authenticity Score (0-100)
        -- 100 = clearly human mouse behavior
        --   0 = clearly automated / no mouse data
        --
        -- Components (weighted sum, capped at 100):
        --   Entropy signal    (30 pts) — high entropy = human
        --   Timing CV         (20 pts) — variable timing = human
        --   Speed CV          (15 pts) — variable speed = human
        --   Move count        (15 pts) — sufficient moves = human
        --   No replay         (10 pts) — not a replayed recording
        --   No scroll conflict (10 pts) — scroll behavior consistent
        -- =========================================================
        CASE
            WHEN p.MoveCountBucket IS NULL OR p.MoveCountBucket = '0' THEN 0
            ELSE
                -- Entropy: 0-30 pts. MouseEntropy 0-100 maps to 0-30.
                CASE
                    WHEN p.MouseEntropy IS NULL THEN 0
                    WHEN p.MouseEntropy >= 70 THEN 30
                    WHEN p.MouseEntropy >= 40 THEN 20
                    WHEN p.MouseEntropy >= 20 THEN 10
                    ELSE 5
                END
                -- Timing CV: 0-20 pts. MoveTimingCV is a percentage (0-200+).
                -- High CV = variable timing = human. Low CV = metronomic = bot.
                + CASE
                    WHEN p.MoveTimingCV IS NULL THEN 0
                    WHEN p.MoveTimingCV >= 80 THEN 20
                    WHEN p.MoveTimingCV >= 50 THEN 15
                    WHEN p.MoveTimingCV >= 25 THEN 10
                    ELSE 3
                END
                -- Speed CV: 0-15 pts. Same logic — variable speed = human.
                + CASE
                    WHEN p.MoveSpeedCV IS NULL THEN 0
                    WHEN p.MoveSpeedCV >= 80 THEN 15
                    WHEN p.MoveSpeedCV >= 50 THEN 12
                    WHEN p.MoveSpeedCV >= 25 THEN 8
                    ELSE 3
                END
                -- Move count: 0-15 pts. Based on MoveCountBucket.
                + CASE
                    WHEN p.MoveCountBucket IN ('51+', '50+') THEN 15
                    WHEN p.MoveCountBucket IN ('21-50') THEN 12
                    WHEN p.MoveCountBucket IN ('11-20') THEN 8
                    WHEN p.MoveCountBucket IN ('6-10') THEN 5
                    WHEN p.MoveCountBucket IN ('1-5') THEN 3
                    ELSE 0
                END
                -- No replay: 10 pts if not flagged as replay
                + CASE
                    WHEN p.ReplayDetected = 1 THEN 0
                    ELSE 10
                END
                -- No scroll contradiction: 10 pts if consistent
                + CASE
                    WHEN p.ScrollContradiction = 1 THEN 0
                    ELSE 10
                END
        END AS MouseAuthenticity,

        -- =========================================================
        -- Session Quality Score (0-100)
        -- 100 = deep engaged session
        --   0 = single page bounce / no session data
        --
        -- Components:
        --   Page count    (40 pts) — more pages = more engaged
        --   Duration      (40 pts) — longer = more engaged (caps at 5min)
        --   Multi-page    (20 pts) — bonus for not bouncing
        -- =========================================================
        CASE
            WHEN p.SessionPageCount IS NULL OR p.SessionPageCount = 0 THEN 0
            ELSE
                -- Page count: 0-40 pts
                CASE
                    WHEN p.SessionPageCount >= 10 THEN 40
                    WHEN p.SessionPageCount >= 5  THEN 30
                    WHEN p.SessionPageCount >= 3  THEN 20
                    WHEN p.SessionPageCount >= 2  THEN 12
                    ELSE 5   -- single page
                END
                -- Duration: 0-40 pts (based on SessionDurationSec)
                + CASE
                    WHEN p.SessionDurationSec IS NULL THEN 0
                    WHEN p.SessionDurationSec >= 300 THEN 40   -- 5+ minutes
                    WHEN p.SessionDurationSec >= 120 THEN 30   -- 2+ minutes
                    WHEN p.SessionDurationSec >= 60  THEN 20   -- 1+ minute
                    WHEN p.SessionDurationSec >= 15  THEN 10   -- 15+ seconds
                    ELSE 3
                END
                -- Multi-page bonus: 20 pts if > 1 page
                + CASE
                    WHEN p.SessionPageCount >= 2 THEN 20
                    ELSE 0
                END
        END AS SessionQuality,

        -- =========================================================
        -- Composite Quality Score (0-100)
        -- Master score: higher = better quality visitor.
        --
        -- Weighted formula:
        --   InvertedBotScore * 0.25  (bot score 0=good, invert it)
        --   MouseAuth       * 0.20
        --   SessionQuality  * 0.15
        --   LeadQuality     * 0.15
        --   Cultural        * 0.10
        --   NoContradictions* 0.10
        --   Affluence bonus   0.05
        --
        -- We compute this as a separate column below after we have
        -- MouseAuthenticity and SessionQuality. Done via a subquery.
        -- =========================================================
        CAST(NULL AS INT) AS CompositeQuality_Placeholder  -- computed below

    INTO #NewScores
    FROM PiXL.Visit v
    JOIN PiXL.Parsed p ON v.VisitID = p.SourceId
    WHERE v.VisitID > @LastId
      AND v.VisitID <= @MaxId
      AND v.DeviceId IS NOT NULL;

    -- Now compute CompositeQuality using the derived scores
    UPDATE #NewScores
    SET CompositeQuality_Placeholder =
        CASE
            WHEN BotScore IS NULL AND MouseAuthenticity = 0 AND SessionQuality = 0 THEN NULL
            ELSE
                -- Inverted bot score (0-100, where 100 = no bot signals)
                CAST(
                    ISNULL(CASE WHEN BotScore >= 100 THEN 0 ELSE 100 - BotScore END, 50) * 0.25
                    + ISNULL(MouseAuthenticity, 0) * 0.20
                    + ISNULL(SessionQuality, 0) * 0.15
                    + ISNULL(LeadQualityScore, 50) * 0.15
                    + ISNULL(CulturalConsistency, 50) * 0.10
                    + CASE
                        WHEN ISNULL(ContradictionCount, 0) = 0 THEN 100 * 0.10
                        WHEN ContradictionCount <= 2 THEN 50 * 0.10
                        ELSE 0
                      END
                    + CASE
                        WHEN AffluenceSignal = 'HIGH' THEN 5
                        WHEN AffluenceSignal = 'MID'  THEN 3
                        ELSE 0
                      END
                AS INT)
        END;

    -- Insert new scores (no duplicates — watermark guarantees we only process new visits)
    INSERT INTO TrafficAlert.VisitorScore (
        VisitId, DeviceId, CompanyID, ReceivedAt,
        BotScore, AnomalyScore, CombinedThreatScore,
        LeadQualityScore, AffluenceSignal, CulturalConsistency, ContradictionCount,
        SessionQuality, MouseAuthenticity, CompositeQuality,
        MaterializedAt
    )
    SELECT
        VisitID, DeviceId, CompanyID, ReceivedAt,
        BotScore, AnomalyScore, CombinedThreatScore,
        LeadQualityScore, AffluenceSignal, CulturalConsistency, ContradictionCount,
        SessionQuality, MouseAuthenticity, CompositeQuality_Placeholder,
        SYSUTCDATETIME()
    FROM #NewScores;

    SET @RowsInserted = @@ROWCOUNT;

    -- Update watermark
    UPDATE ETL.Watermark
    SET LastProcessedId = @MaxId,
        LastRunAt = SYSUTCDATETIME(),
        RowsProcessed = RowsProcessed + @RowsInserted
    WHERE ProcessName = @ProcessName;

    -- Cleanup
    DROP TABLE #NewScores;

    SELECT @RowsInserted AS RowsMaterialized,
           DATEDIFF(MILLISECOND, @StartTime, SYSUTCDATETIME()) AS ElapsedMs;
END;
GO

PRINT '  Created ETL.usp_MaterializeVisitorScores';
GO

-- =====================================================================
-- Step 2: ETL.usp_MaterializeCustomerSummary
--
-- Daily batch proc. Computes/refreshes daily, weekly, monthly rollups
-- for each company from TrafficAlert.VisitorScore.
--
-- Uses separate DELETE + INSERT for period rows (not MERGE) because
-- we always want to fully recompute the summary for affected periods.
-- Weekly and monthly rows are recomputed whenever any daily data
-- in that period changes.
--
-- Watermark: tracks the max VisitorScoreId processed to know which
-- companies need their summaries refreshed.
-- =====================================================================
CREATE OR ALTER PROCEDURE ETL.usp_MaterializeCustomerSummary
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ProcessName VARCHAR(50) = 'MaterializeCustomerSummary';
    DECLARE @StartTime DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @LastId BIGINT;
    DECLARE @MaxId BIGINT;
    DECLARE @RowsAffected INT = 0;

    -- Get watermark
    SELECT @LastId = LastProcessedId FROM ETL.Watermark WHERE ProcessName = @ProcessName;
    SELECT @MaxId = MAX(VisitorScoreId) FROM TrafficAlert.VisitorScore;

    -- Nothing new
    IF @MaxId IS NULL OR @MaxId <= @LastId
    BEGIN
        SELECT 0 AS RowsMaterialized, 0 AS ElapsedMs;
        RETURN;
    END;

    -- Find which companies + dates have new data since last run
    SELECT DISTINCT CompanyID, CAST(ReceivedAt AS DATE) AS HitDate
    INTO #AffectedDays
    FROM TrafficAlert.VisitorScore
    WHERE VisitorScoreId > @LastId
      AND VisitorScoreId <= @MaxId;

    -- =====================================================
    -- DAILY rollup (PeriodType = 'D')
    -- Delete affected daily rows, then reinsert.
    -- =====================================================
    DELETE cs
    FROM TrafficAlert.CustomerSummary cs
    JOIN #AffectedDays ad ON cs.CompanyID = ad.CompanyID
                          AND cs.PeriodStart = ad.HitDate
                          AND cs.PeriodType = 'D';

    INSERT INTO TrafficAlert.CustomerSummary (
        CompanyID, PeriodStart, PeriodType,
        TotalHits, BotHits, HumanHits, UnknownHits,
        BotPercent, AvgBotScore, AvgLeadQuality, AvgCompositeQuality,
        AvgMouseAuthenticity, AvgSessionQuality,
        UniqueDevices, UniqueIPs, MatchedVisitors, CrossCustomerPollutionRate,
        AvgSessionDepth, AvgSessionDuration, DeadInternetIndex,
        MaterializedAt
    )
    SELECT
        vs.CompanyID,
        CAST(vs.ReceivedAt AS DATE)     AS PeriodStart,
        'D'                             AS PeriodType,

        COUNT_BIG(*)                    AS TotalHits,
        SUM(CASE WHEN vs.BotScore >= 70 THEN 1 ELSE 0 END) AS BotHits,
        SUM(CASE WHEN vs.BotScore < 50 AND vs.MouseAuthenticity >= 50 THEN 1 ELSE 0 END) AS HumanHits,
        SUM(CASE WHEN vs.BotScore BETWEEN 50 AND 69
                   OR (vs.BotScore < 50 AND (vs.MouseAuthenticity IS NULL OR vs.MouseAuthenticity < 50))
                 THEN 1 ELSE 0 END) AS UnknownHits,

        CASE WHEN COUNT_BIG(*) = 0 THEN 0
             ELSE CAST(100.0 * SUM(CASE WHEN vs.BotScore >= 70 THEN 1 ELSE 0 END) / COUNT_BIG(*) AS DECIMAL(5,2))
        END AS BotPercent,

        AVG(CAST(vs.BotScore AS DECIMAL(5,2)))              AS AvgBotScore,
        AVG(CAST(vs.LeadQualityScore AS DECIMAL(5,2)))      AS AvgLeadQuality,
        AVG(CAST(vs.CompositeQuality AS DECIMAL(5,2)))      AS AvgCompositeQuality,
        AVG(CAST(vs.MouseAuthenticity AS DECIMAL(5,2)))     AS AvgMouseAuthenticity,
        AVG(CAST(vs.SessionQuality AS DECIMAL(5,2)))        AS AvgSessionQuality,

        COUNT(DISTINCT vs.DeviceId)     AS UniqueDevices,
        COUNT(DISTINCT v.IpId)          AS UniqueIPs,
        COUNT(DISTINCT m.IndividualKey)    AS MatchedVisitors,

        -- Cross-customer pollution: % of devices that hit other companies in same period
        CASE WHEN COUNT(DISTINCT vs.DeviceId) = 0 THEN 0
             ELSE CAST(100.0 *
                COUNT(DISTINCT CASE
                    WHEN xc.DeviceId IS NOT NULL THEN vs.DeviceId
                END) / COUNT(DISTINCT vs.DeviceId) AS DECIMAL(5,2))
        END AS CrossCustomerPollutionRate,

        -- Session depth: avg pages per session (from Parsed)
        AVG(CAST(p.SessionPageCount AS DECIMAL(5,2)))   AS AvgSessionDepth,
        AVG(p.SessionDurationSec)                       AS AvgSessionDuration,

        -- Dead Internet Index: % of definite bots + zero-mouse
        CASE WHEN COUNT_BIG(*) = 0 THEN 0
             ELSE CAST(100.0 * (
                SUM(CASE WHEN vs.BotScore >= 70 THEN 1 ELSE 0 END)
                + SUM(CASE WHEN vs.BotScore < 70 AND vs.MouseAuthenticity = 0 THEN 1 ELSE 0 END)
             ) / COUNT_BIG(*) AS INT)
        END AS DeadInternetIndex,

        SYSUTCDATETIME() AS MaterializedAt

    FROM TrafficAlert.VisitorScore vs
    JOIN #AffectedDays ad ON vs.CompanyID = ad.CompanyID
                          AND CAST(vs.ReceivedAt AS DATE) = ad.HitDate
    JOIN PiXL.Visit v ON vs.VisitId = v.VisitID
    LEFT JOIN PiXL.Match m ON vs.DeviceId = m.DeviceId
    LEFT JOIN PiXL.Parsed p ON v.VisitID = p.SourceId
    -- Cross-customer: does this device appear in other companies on the same day?
    LEFT JOIN (
        SELECT DISTINCT vs2.DeviceId
        FROM TrafficAlert.VisitorScore vs2
        JOIN #AffectedDays ad2 ON CAST(vs2.ReceivedAt AS DATE) = ad2.HitDate
        GROUP BY vs2.DeviceId
        HAVING COUNT(DISTINCT vs2.CompanyID) >= 2
    ) xc ON vs.DeviceId = xc.DeviceId
    GROUP BY vs.CompanyID, CAST(vs.ReceivedAt AS DATE);

    SET @RowsAffected = @@ROWCOUNT;

    -- =====================================================
    -- WEEKLY rollup (PeriodType = 'W')
    -- Monday-based weeks. Recompute for affected weeks.
    -- =====================================================
    ;WITH AffectedWeeks AS (
        SELECT DISTINCT
            CompanyID,
            DATEADD(DAY, -(DATEPART(WEEKDAY, HitDate) + @@DATEFIRST - 2) % 7, HitDate) AS WeekStart
        FROM #AffectedDays
    )
    DELETE cs
    FROM TrafficAlert.CustomerSummary cs
    JOIN AffectedWeeks aw ON cs.CompanyID = aw.CompanyID
                          AND cs.PeriodStart = aw.WeekStart
                          AND cs.PeriodType = 'W';

    ;WITH AffectedWeeks AS (
        SELECT DISTINCT
            CompanyID,
            DATEADD(DAY, -(DATEPART(WEEKDAY, HitDate) + @@DATEFIRST - 2) % 7, HitDate) AS WeekStart
        FROM #AffectedDays
    )
    INSERT INTO TrafficAlert.CustomerSummary (
        CompanyID, PeriodStart, PeriodType,
        TotalHits, BotHits, HumanHits, UnknownHits,
        BotPercent, AvgBotScore, AvgLeadQuality, AvgCompositeQuality,
        AvgMouseAuthenticity, AvgSessionQuality,
        UniqueDevices, UniqueIPs, MatchedVisitors, CrossCustomerPollutionRate,
        AvgSessionDepth, AvgSessionDuration, DeadInternetIndex,
        MaterializedAt
    )
    SELECT
        d.CompanyID,
        d.PeriodStart       AS PeriodStart,
        'W'                 AS PeriodType,
        SUM(d.TotalHits),
        SUM(d.BotHits),
        SUM(d.HumanHits),
        SUM(d.UnknownHits),
        CASE WHEN SUM(d.TotalHits) = 0 THEN 0
             ELSE CAST(100.0 * SUM(d.BotHits) / SUM(d.TotalHits) AS DECIMAL(5,2))
        END,
        AVG(d.AvgBotScore),
        AVG(d.AvgLeadQuality),
        AVG(d.AvgCompositeQuality),
        AVG(d.AvgMouseAuthenticity),
        AVG(d.AvgSessionQuality),
        -- For unique counts we can't just SUM the daily uniques (would double-count).
        -- We approximate with MAX(daily) * 1.0 — true uniques require re-query.
        -- This is acceptable for summary trends; detail views query VisitorScore directly.
        MAX(d.UniqueDevices),
        MAX(d.UniqueIPs),
        MAX(d.MatchedVisitors),
        AVG(d.CrossCustomerPollutionRate),
        AVG(d.AvgSessionDepth),
        AVG(d.AvgSessionDuration),
        AVG(d.DeadInternetIndex),
        SYSUTCDATETIME()
    FROM TrafficAlert.CustomerSummary d
    JOIN AffectedWeeks aw ON d.CompanyID = aw.CompanyID
                          AND d.PeriodStart >= aw.WeekStart
                          AND d.PeriodStart < DATEADD(DAY, 7, aw.WeekStart)
                          AND d.PeriodType = 'D'
    GROUP BY d.CompanyID, d.PeriodStart;

    SET @RowsAffected = @RowsAffected + @@ROWCOUNT;

    -- =====================================================
    -- MONTHLY rollup (PeriodType = 'M')
    -- Calendar months. Same pattern as weekly.
    -- =====================================================
    ;WITH AffectedMonths AS (
        SELECT DISTINCT
            CompanyID,
            DATEFROMPARTS(YEAR(HitDate), MONTH(HitDate), 1) AS MonthStart
        FROM #AffectedDays
    )
    DELETE cs
    FROM TrafficAlert.CustomerSummary cs
    JOIN AffectedMonths am ON cs.CompanyID = am.CompanyID
                           AND cs.PeriodStart = am.MonthStart
                           AND cs.PeriodType = 'M';

    ;WITH AffectedMonths AS (
        SELECT DISTINCT
            CompanyID,
            DATEFROMPARTS(YEAR(HitDate), MONTH(HitDate), 1) AS MonthStart
        FROM #AffectedDays
    )
    INSERT INTO TrafficAlert.CustomerSummary (
        CompanyID, PeriodStart, PeriodType,
        TotalHits, BotHits, HumanHits, UnknownHits,
        BotPercent, AvgBotScore, AvgLeadQuality, AvgCompositeQuality,
        AvgMouseAuthenticity, AvgSessionQuality,
        UniqueDevices, UniqueIPs, MatchedVisitors, CrossCustomerPollutionRate,
        AvgSessionDepth, AvgSessionDuration, DeadInternetIndex,
        MaterializedAt
    )
    SELECT
        d.CompanyID,
        am.MonthStart       AS PeriodStart,
        'M'                 AS PeriodType,
        SUM(d.TotalHits),
        SUM(d.BotHits),
        SUM(d.HumanHits),
        SUM(d.UnknownHits),
        CASE WHEN SUM(d.TotalHits) = 0 THEN 0
             ELSE CAST(100.0 * SUM(d.BotHits) / SUM(d.TotalHits) AS DECIMAL(5,2))
        END,
        AVG(d.AvgBotScore),
        AVG(d.AvgLeadQuality),
        AVG(d.AvgCompositeQuality),
        AVG(d.AvgMouseAuthenticity),
        AVG(d.AvgSessionQuality),
        MAX(d.UniqueDevices),
        MAX(d.UniqueIPs),
        MAX(d.MatchedVisitors),
        AVG(d.CrossCustomerPollutionRate),
        AVG(d.AvgSessionDepth),
        AVG(d.AvgSessionDuration),
        AVG(d.DeadInternetIndex),
        SYSUTCDATETIME()
    FROM TrafficAlert.CustomerSummary d
    JOIN AffectedMonths am ON d.CompanyID = am.CompanyID
                           AND d.PeriodStart >= am.MonthStart
                           AND d.PeriodStart < DATEADD(MONTH, 1, am.MonthStart)
                           AND d.PeriodType = 'D'
    GROUP BY d.CompanyID, am.MonthStart;

    SET @RowsAffected = @RowsAffected + @@ROWCOUNT;

    -- Update watermark
    UPDATE ETL.Watermark
    SET LastProcessedId = @MaxId,
        LastRunAt = SYSUTCDATETIME(),
        RowsProcessed = RowsProcessed + @RowsAffected
    WHERE ProcessName = @ProcessName;

    -- Cleanup
    DROP TABLE #AffectedDays;

    SELECT @RowsAffected AS RowsMaterialized,
           DATEDIFF(MILLISECOND, @StartTime, SYSUTCDATETIME()) AS ElapsedMs;
END;
GO

PRINT '  Created ETL.usp_MaterializeCustomerSummary';
GO

-- =====================================================================
-- Step 3: Dashboard View — Visitor Detail
-- Single-visitor full scoring breakdown with all signals.
-- =====================================================================
CREATE OR ALTER VIEW dbo.vw_TrafficAlert_VisitorDetail
AS
SELECT
    vs.VisitorScoreId,
    vs.VisitId,
    vs.DeviceId,
    vs.CompanyID,
    c.CompanyName,
    vs.ReceivedAt,

    -- Client-side scores
    vs.BotScore,
    vs.AnomalyScore,
    vs.CombinedThreatScore,

    -- Forge scores
    vs.LeadQualityScore,
    vs.AffluenceSignal,
    vs.CulturalConsistency,
    vs.ContradictionCount,

    -- Derived scores
    vs.SessionQuality,
    vs.MouseAuthenticity,
    vs.CompositeQuality,

    -- Classification bucket
    CASE
        WHEN vs.BotScore >= 70 THEN 'Definite Bot'
        WHEN vs.BotScore >= 50 THEN 'Likely Bot'
        WHEN vs.CompositeQuality >= 70 THEN 'High Quality Human'
        WHEN vs.CompositeQuality >= 40 THEN 'Medium Quality'
        WHEN vs.CompositeQuality IS NOT NULL THEN 'Low Quality'
        ELSE 'Unscored'
    END AS QualityBucket,

    -- Device context
    d.DeviceHash,
    d.PrimaryBrowser,
    d.PrimaryOS,
    d.AffluenceSignal   AS DeviceAffluence,
    d.CompanyCount       AS DeviceCompanyCount,

    -- IP context
    ip.IPAddress,
    ip.GeoCountry,
    ip.GeoCity,
    ip.IsDatacenter,

    vs.MaterializedAt

FROM TrafficAlert.VisitorScore vs
JOIN PiXL.Visit v ON vs.VisitId = v.VisitID
LEFT JOIN PiXL.Device d ON vs.DeviceId = d.DeviceId
LEFT JOIN PiXL.IP ip ON v.IpId = ip.IpId
LEFT JOIN PiXL.Company c ON vs.CompanyID = c.CompanyID;
GO

PRINT '  Created dbo.vw_TrafficAlert_VisitorDetail';
GO

-- =====================================================================
-- Step 4: Dashboard View — Customer Overview
-- Per-customer summary with latest quality metrics.
-- =====================================================================
CREATE OR ALTER VIEW dbo.vw_TrafficAlert_CustomerOverview
AS
SELECT
    cs.CompanyID,
    c.CompanyName,
    cs.PeriodStart,
    cs.PeriodType,
    cs.TotalHits,
    cs.BotHits,
    cs.HumanHits,
    cs.UnknownHits,
    cs.BotPercent,
    cs.AvgBotScore,
    cs.AvgLeadQuality,
    cs.AvgCompositeQuality,
    cs.AvgMouseAuthenticity,
    cs.AvgSessionQuality,
    cs.UniqueDevices,
    cs.UniqueIPs,
    cs.MatchedVisitors,
    cs.CrossCustomerPollutionRate,
    cs.AvgSessionDepth,
    cs.AvgSessionDuration,
    cs.DeadInternetIndex,

    -- Quality grade
    CASE
        WHEN cs.BotPercent >= 80 THEN 'F'
        WHEN cs.BotPercent >= 60 THEN 'D'
        WHEN cs.BotPercent >= 40 THEN 'C'
        WHEN cs.BotPercent >= 20 THEN 'B'
        ELSE 'A'
    END AS QualityGrade,

    cs.MaterializedAt

FROM TrafficAlert.CustomerSummary cs
LEFT JOIN PiXL.Company c ON cs.CompanyID = c.CompanyID;
GO

PRINT '  Created dbo.vw_TrafficAlert_CustomerOverview';
GO

-- =====================================================================
-- Step 5: Dashboard View — Customer Trend
-- Time-series of daily customer metrics for charting.
-- =====================================================================
CREATE OR ALTER VIEW dbo.vw_TrafficAlert_Trend
AS
SELECT
    cs.CompanyID,
    c.CompanyName,
    cs.PeriodStart,
    cs.PeriodType,
    cs.TotalHits,
    cs.BotPercent,
    cs.AvgCompositeQuality,
    cs.AvgLeadQuality,
    cs.DeadInternetIndex,
    cs.UniqueDevices,
    cs.AvgMouseAuthenticity,
    cs.AvgSessionQuality,

    -- Week-over-week / period-over-period change (computed with LAG)
    LAG(cs.BotPercent) OVER (PARTITION BY cs.CompanyID, cs.PeriodType ORDER BY cs.PeriodStart) AS PrevBotPercent,
    LAG(cs.AvgCompositeQuality) OVER (PARTITION BY cs.CompanyID, cs.PeriodType ORDER BY cs.PeriodStart) AS PrevCompositeQuality,
    LAG(cs.TotalHits) OVER (PARTITION BY cs.CompanyID, cs.PeriodType ORDER BY cs.PeriodStart) AS PrevTotalHits,

    -- Trend direction
    CASE
        WHEN LAG(cs.BotPercent) OVER (PARTITION BY cs.CompanyID, cs.PeriodType ORDER BY cs.PeriodStart) IS NULL THEN 'NEW'
        WHEN cs.BotPercent > LAG(cs.BotPercent) OVER (PARTITION BY cs.CompanyID, cs.PeriodType ORDER BY cs.PeriodStart) + 5 THEN 'WORSENING'
        WHEN cs.BotPercent < LAG(cs.BotPercent) OVER (PARTITION BY cs.CompanyID, cs.PeriodType ORDER BY cs.PeriodStart) - 5 THEN 'IMPROVING'
        ELSE 'STABLE'
    END AS QualityTrend

FROM TrafficAlert.CustomerSummary cs
LEFT JOIN PiXL.Company c ON cs.CompanyID = c.CompanyID;
GO

PRINT '  Created dbo.vw_TrafficAlert_Trend';
GO

-- =====================================================================
-- Step 6: Verification
-- =====================================================================
IF OBJECT_ID('ETL.usp_MaterializeVisitorScores', 'P') IS NOT NULL
    PRINT '  OK: ETL.usp_MaterializeVisitorScores exists';
ELSE
    PRINT '  ERROR: ETL.usp_MaterializeVisitorScores missing!';

IF OBJECT_ID('ETL.usp_MaterializeCustomerSummary', 'P') IS NOT NULL
    PRINT '  OK: ETL.usp_MaterializeCustomerSummary exists';
ELSE
    PRINT '  ERROR: ETL.usp_MaterializeCustomerSummary missing!';

IF OBJECT_ID('dbo.vw_TrafficAlert_VisitorDetail', 'V') IS NOT NULL
    PRINT '  OK: dbo.vw_TrafficAlert_VisitorDetail exists';
ELSE
    PRINT '  ERROR: dbo.vw_TrafficAlert_VisitorDetail missing!';

IF OBJECT_ID('dbo.vw_TrafficAlert_CustomerOverview', 'V') IS NOT NULL
    PRINT '  OK: dbo.vw_TrafficAlert_CustomerOverview exists';
ELSE
    PRINT '  ERROR: dbo.vw_TrafficAlert_CustomerOverview missing!';

IF OBJECT_ID('dbo.vw_TrafficAlert_Trend', 'V') IS NOT NULL
    PRINT '  OK: dbo.vw_TrafficAlert_Trend exists';
ELSE
    PRINT '  ERROR: dbo.vw_TrafficAlert_Trend missing!';
GO

PRINT '--- 59: TrafficAlert materialization complete ---';
GO
