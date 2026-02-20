-- ============================================================================
-- Migration 49: Session Reconstruction Views (Phase 8)
-- ============================================================================
-- Creates dbo.vw_Dash_Sessions — SQL-side session reconstruction using
-- window functions on PiXL.Visit.
--
-- The Forge does real-time session stitching (Phase 5, _srv_sessionId).
-- SQL sees COMPLETE sessions after the fact, with full enrichment data.
-- Both perspectives are valuable:
--   Forge: "this is hit #3 in an active session"
--   SQL:   "this session had 7 pages, lasted 4 min, bounced? no"
--
-- Design doc reference: §8.3 item 5 (Session Reconstruction)
-- ============================================================================
USE SmartPiXL;
GO

SET NOCOUNT ON;
GO

PRINT '--- 49: Session Reconstruction Views ---';
GO

-- =====================================================================
-- Step 1: Session reconstruction view
-- =====================================================================
-- Session definition: same DeviceId, same CompanyID, gap < 30 minutes.
-- Uses LAG() to detect session boundaries, then SUM() over flags
-- to assign session numbers. Final aggregation gives session metrics.
-- =====================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_Sessions
AS
WITH SessionBoundaries AS (
    SELECT
        v.VisitID,
        v.CompanyID,
        v.PiXLID,
        v.DeviceId,
        v.IpId,
        v.ReceivedAt,
        v.HitType,
        -- Detect session boundary: gap > 30 min from previous hit
        CASE
            WHEN DATEDIFF(MINUTE,
                LAG(v.ReceivedAt) OVER (
                    PARTITION BY v.DeviceId, v.CompanyID
                    ORDER BY v.ReceivedAt
                ),
                v.ReceivedAt
            ) > 30
            OR LAG(v.ReceivedAt) OVER (
                    PARTITION BY v.DeviceId, v.CompanyID
                    ORDER BY v.ReceivedAt
                ) IS NULL
            THEN 1
            ELSE 0
        END AS IsNewSession
    FROM PiXL.Visit v
    WHERE v.DeviceId IS NOT NULL
),
SessionNumbered AS (
    SELECT
        sb.*,
        SUM(sb.IsNewSession) OVER (
            PARTITION BY sb.DeviceId, sb.CompanyID
            ORDER BY sb.ReceivedAt
            ROWS UNBOUNDED PRECEDING
        ) AS SessionNum
    FROM SessionBoundaries sb
)
SELECT
    sn.CompanyID,
    sn.PiXLID,
    sn.DeviceId,
    sn.SessionNum,
    MIN(sn.ReceivedAt)                                      AS SessionStart,
    MAX(sn.ReceivedAt)                                      AS SessionEnd,
    COUNT_BIG(*)                                            AS PageCount,
    DATEDIFF(SECOND, MIN(sn.ReceivedAt), MAX(sn.ReceivedAt)) AS DurationSec,
    CASE WHEN COUNT_BIG(*) = 1 THEN 1 ELSE 0 END           AS IsBounce,
    MIN(sn.VisitID)                                         AS EntryVisitID,
    MAX(sn.VisitID)                                         AS ExitVisitID
FROM SessionNumbered sn
GROUP BY sn.CompanyID, sn.PiXLID, sn.DeviceId, sn.SessionNum;
GO

-- =====================================================================
-- Step 2: Session summary view (per company)
-- =====================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_SessionSummary
AS
SELECT
    s.CompanyID,
    CAST(s.SessionStart AS DATE)                            AS SessionDate,
    COUNT_BIG(*)                                            AS TotalSessions,
    AVG(CAST(s.PageCount AS DECIMAL(10,2)))                 AS AvgPagesPerSession,
    AVG(CAST(s.DurationSec AS DECIMAL(10,2)))               AS AvgDurationSec,
    SUM(CAST(s.IsBounce AS BIGINT))                         AS BounceCount,
    CASE
        WHEN COUNT_BIG(*) = 0 THEN 0
        ELSE CAST(100.0 * SUM(CAST(s.IsBounce AS BIGINT)) / COUNT_BIG(*) AS DECIMAL(5,2))
    END                                                     AS BounceRatePct
FROM dbo.vw_Dash_Sessions s
GROUP BY s.CompanyID, CAST(s.SessionStart AS DATE);
GO

-- =====================================================================
-- Step 3: Verification
-- =====================================================================
IF OBJECT_ID('dbo.vw_Dash_Sessions', 'V') IS NOT NULL
    PRINT '  OK: dbo.vw_Dash_Sessions exists';
ELSE
    PRINT '  ERROR: dbo.vw_Dash_Sessions missing!';

IF OBJECT_ID('dbo.vw_Dash_SessionSummary', 'V') IS NOT NULL
    PRINT '  OK: dbo.vw_Dash_SessionSummary exists';
ELSE
    PRINT '  ERROR: dbo.vw_Dash_SessionSummary missing!';
GO

PRINT '--- 49: Session views complete ---';
GO
