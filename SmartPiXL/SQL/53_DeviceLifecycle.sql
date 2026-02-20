-- ============================================================================
-- Migration 53: Device Lifecycle View (Phase 8)
-- ============================================================================
-- Creates dbo.vw_Dash_DeviceLifecycle — THE core value proposition of
-- the normalized schema. The legacy system couldn't track a device across
-- domains because everything was one flat row with duplication.
--
-- Now, with Device and IP decoupled from Visit:
--   - Return frequency (median days between visits per device)
--   - Customer hop pattern (which companies does this device visit?)
--   - Fingerprint drift (UA changes over time = real user, not bot)
--   - Dormancy detection (60+ day gap then returns = lead re-engagement)
--
-- Design doc reference: §8.3 item 3 (Device Lifecycle)
-- ============================================================================
USE SmartPiXL;
GO

SET NOCOUNT ON;
GO

PRINT '--- 53: Device Lifecycle ---';
GO

-- =====================================================================
-- Step 1: Device lifecycle overview
-- =====================================================================
-- Per-device: lifespan, visit frequency, company reach, dormancy status
-- =====================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_DeviceLifecycle
AS
WITH DeviceVisitGaps AS (
    SELECT
        v.DeviceId,
        v.ReceivedAt,
        -- Days since previous visit for this device
        DATEDIFF(DAY,
            LAG(v.ReceivedAt) OVER (PARTITION BY v.DeviceId ORDER BY v.ReceivedAt),
            v.ReceivedAt
        ) AS DaysSincePrev
    FROM PiXL.Visit v
    WHERE v.DeviceId IS NOT NULL
),
DeviceGapStats AS (
    SELECT
        dvg.DeviceId,
        -- Median approximation via PERCENTILE_CONT
        PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY dvg.DaysSincePrev)
            OVER (PARTITION BY dvg.DeviceId) AS MedianDaysBetweenVisits,
        AVG(CAST(dvg.DaysSincePrev AS DECIMAL(10,2)))
            OVER (PARTITION BY dvg.DeviceId) AS AvgDaysBetweenVisits,
        MAX(dvg.DaysSincePrev)
            OVER (PARTITION BY dvg.DeviceId) AS MaxGapDays
    FROM DeviceVisitGaps dvg
    WHERE dvg.DaysSincePrev IS NOT NULL    -- Exclude first visit (no LAG)
)
SELECT DISTINCT
    d.DeviceId,
    d.DeviceHash,
    d.FirstSeen,
    d.LastSeen,
    d.HitCount,
    DATEDIFF(DAY, d.FirstSeen, d.LastSeen)                  AS LifespanDays,

    -- How many distinct companies has this device visited?
    (SELECT COUNT(DISTINCT v2.CompanyID)
     FROM PiXL.Visit v2
     WHERE v2.DeviceId = d.DeviceId)                        AS CompanyCount,

    -- Return frequency (median days between visits)
    gs.MedianDaysBetweenVisits,
    gs.AvgDaysBetweenVisits,
    gs.MaxGapDays,

    -- Dormancy: last seen > 60 days ago
    CASE
        WHEN DATEDIFF(DAY, d.LastSeen, SYSUTCDATETIME()) > 60 THEN 1
        ELSE 0
    END                                                     AS IsDormant,

    -- Days since last seen
    DATEDIFF(DAY, d.LastSeen, SYSUTCDATETIME())             AS DaysSinceLastSeen,

    -- UA drift indicator: how many distinct UAs has this device used?
    (SELECT COUNT(DISTINCT p2.ClientUserAgent)
     FROM PiXL.Parsed p2
     JOIN PiXL.Visit v3 ON p2.SourceId = v3.VisitID
     WHERE v3.DeviceId = d.DeviceId
       AND p2.ClientUserAgent IS NOT NULL)                  AS DistinctUserAgents

FROM PiXL.Device d
LEFT JOIN DeviceGapStats gs ON d.DeviceId = gs.DeviceId;
GO

-- =====================================================================
-- Step 2: Device customer hop pattern
-- =====================================================================
-- Shows which companies each device visits — cross-customer intelligence
-- =====================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_DeviceCustomerHops
AS
SELECT
    v.DeviceId,
    d.DeviceHash,
    v.CompanyID,
    COUNT_BIG(*)                AS HitCount,
    MIN(v.ReceivedAt)           AS FirstSeen,
    MAX(v.ReceivedAt)           AS LastSeen,
    COUNT(DISTINCT v.IpId)      AS UniqueIPs
FROM PiXL.Visit v
JOIN PiXL.Device d ON v.DeviceId = d.DeviceId
WHERE v.DeviceId IS NOT NULL
GROUP BY v.DeviceId, d.DeviceHash, v.CompanyID;
GO

-- =====================================================================
-- Step 3: Verification
-- =====================================================================
IF OBJECT_ID('dbo.vw_Dash_DeviceLifecycle', 'V') IS NOT NULL
    PRINT '  OK: dbo.vw_Dash_DeviceLifecycle exists';
ELSE
    PRINT '  ERROR: dbo.vw_Dash_DeviceLifecycle missing!';

IF OBJECT_ID('dbo.vw_Dash_DeviceCustomerHops', 'V') IS NOT NULL
    PRINT '  OK: dbo.vw_Dash_DeviceCustomerHops exists';
ELSE
    PRINT '  ERROR: dbo.vw_Dash_DeviceCustomerHops missing!';
GO

PRINT '--- 53: Device lifecycle complete ---';
GO
