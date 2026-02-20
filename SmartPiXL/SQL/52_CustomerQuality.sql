-- ============================================================================
-- Migration 52: Customer Traffic Quality Trending View (Phase 8)
-- ============================================================================
-- Creates dbo.vw_Dash_CustomerQuality — per company per month traffic
-- quality metrics. Powers customer-facing reports:
--   "Your bot traffic dropped 12% this month"
--   "Your lead quality score improved"
--
-- Design doc reference: §8.3 item 4 (Customer Traffic Quality Trending)
-- ============================================================================
USE SmartPiXL;
GO

SET NOCOUNT ON;
GO

PRINT '--- 52: Customer Traffic Quality ---';
GO

-- =====================================================================
-- Step 1: Monthly customer quality view
-- =====================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_CustomerQuality
AS
SELECT
    p.CompanyID,
    p.PiXLID,
    DATEFROMPARTS(YEAR(p.ReceivedAt), MONTH(p.ReceivedAt), 1)   AS MonthStart,

    -- Volume
    COUNT_BIG(*)                                                AS TotalHits,

    -- Bot metrics
    AVG(CAST(p.BotScore AS DECIMAL(5,2)))                       AS AvgBotScore,
    CASE WHEN COUNT_BIG(*) = 0 THEN 0 ELSE
        CAST(100.0 * SUM(CASE WHEN p.BotScore >= 50 THEN 1 ELSE 0 END)
            / COUNT_BIG(*) AS DECIMAL(5,2))
    END                                                         AS BotPct,

    -- Unique visitors (by device)
    COUNT(DISTINCT v.DeviceId)                                  AS UniqueDevices,

    -- Unique IPs
    COUNT(DISTINCT v.IpId)                                      AS UniqueIPs,

    -- Human traffic (BotScore < 30 AND has behavioral signals)
    SUM(CASE
        WHEN ISNULL(p.BotScore, 0) < 30
         AND (ISNULL(p.MouseMoveCount, 0) > 0 OR ISNULL(p.UserScrolled, 0) = 1)
        THEN 1 ELSE 0
    END)                                                        AS DefiniteHumanHits,

    -- Avg anomaly score
    AVG(CAST(p.AnomalyScore AS DECIMAL(5,2)))                   AS AvgAnomalyScore,

    -- Matched visitors (have MatchEmail)
    SUM(CASE WHEN v.MatchEmail IS NOT NULL THEN 1 ELSE 0 END)  AS MatchedVisitors

FROM PiXL.Parsed p
JOIN PiXL.Visit v ON p.SourceId = v.VisitID
GROUP BY
    p.CompanyID,
    p.PiXLID,
    DATEFROMPARTS(YEAR(p.ReceivedAt), MONTH(p.ReceivedAt), 1);
GO

-- =====================================================================
-- Step 2: Verification
-- =====================================================================
IF OBJECT_ID('dbo.vw_Dash_CustomerQuality', 'V') IS NOT NULL
    PRINT '  OK: dbo.vw_Dash_CustomerQuality exists';
ELSE
    PRINT '  ERROR: dbo.vw_Dash_CustomerQuality missing!';
GO

PRINT '--- 52: Customer quality complete ---';
GO
