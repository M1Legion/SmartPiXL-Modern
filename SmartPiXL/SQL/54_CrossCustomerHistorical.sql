-- ============================================================================
-- Migration 54: Cross-Customer Historical Intelligence View (Phase 8)
-- ============================================================================
-- Creates dbo.vw_Dash_CrossCustomer — devices hitting 5+ different companies
-- historically. These are scrapers, researchers, or competitor spies.
--
-- This is one of SmartPiXL's competitive moats — no single-customer
-- tracking pixel can see this. SmartPiXL sees traffic across ALL customers.
--
-- Design doc reference: §8.3 item 6 (Cross-Customer Intelligence)
-- ============================================================================
USE SmartPiXL;
GO

SET NOCOUNT ON;
GO

PRINT '--- 54: Cross-Customer Historical Intelligence ---';
GO

-- =====================================================================
-- Step 1: Cross-customer device view
-- =====================================================================
-- Devices that have visited 5+ different companies historically.
-- Threshold of 5 catches systematic scrapers while avoiding false
-- positives from users who legitimately visit multiple sites.
-- =====================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_CrossCustomer
AS
WITH DeviceCompanyCounts AS (
    SELECT
        v.DeviceId,
        COUNT(DISTINCT v.CompanyID)  AS CompanyCount,
        COUNT_BIG(*)                 AS TotalHits,
        MIN(v.ReceivedAt)            AS FirstSeen,
        MAX(v.ReceivedAt)            AS LastSeen,
        COUNT(DISTINCT v.IpId)       AS UniqueIPs
    FROM PiXL.Visit v
    WHERE v.DeviceId IS NOT NULL
    GROUP BY v.DeviceId
    HAVING COUNT(DISTINCT v.CompanyID) >= 5
)
SELECT
    dcc.DeviceId,
    d.DeviceHash,
    dcc.CompanyCount,
    dcc.TotalHits,
    dcc.FirstSeen,
    dcc.LastSeen,
    dcc.UniqueIPs,
    DATEDIFF(DAY, dcc.FirstSeen, dcc.LastSeen)   AS ActiveDays,
    d.HitCount                                   AS LifetimeHitCount
FROM DeviceCompanyCounts dcc
JOIN PiXL.Device d ON dcc.DeviceId = d.DeviceId;
GO

-- =====================================================================
-- Step 2: Cross-customer detail — which companies did each device visit?
-- =====================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_CrossCustomerDetail
AS
SELECT
    v.DeviceId,
    v.CompanyID,
    COUNT_BIG(*)                AS HitsForCompany,
    MIN(v.ReceivedAt)           AS FirstVisit,
    MAX(v.ReceivedAt)           AS LastVisit
FROM PiXL.Visit v
WHERE v.DeviceId IS NOT NULL
  AND v.DeviceId IN (
      SELECT v2.DeviceId
      FROM PiXL.Visit v2
      WHERE v2.DeviceId IS NOT NULL
      GROUP BY v2.DeviceId
      HAVING COUNT(DISTINCT v2.CompanyID) >= 5
  )
GROUP BY v.DeviceId, v.CompanyID;
GO

-- =====================================================================
-- Step 3: Verification
-- =====================================================================
IF OBJECT_ID('dbo.vw_Dash_CrossCustomer', 'V') IS NOT NULL
    PRINT '  OK: dbo.vw_Dash_CrossCustomer exists';
ELSE
    PRINT '  ERROR: dbo.vw_Dash_CrossCustomer missing!';

IF OBJECT_ID('dbo.vw_Dash_CrossCustomerDetail', 'V') IS NOT NULL
    PRINT '  OK: dbo.vw_Dash_CrossCustomerDetail exists';
ELSE
    PRINT '  ERROR: dbo.vw_Dash_CrossCustomerDetail missing!';
GO

PRINT '--- 54: Cross-customer complete ---';
GO
