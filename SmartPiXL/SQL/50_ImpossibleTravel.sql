-- ============================================================================
-- Migration 50: Impossible Travel Detection View (Phase 8)
-- ============================================================================
-- Creates dbo.vw_Dash_ImpossibleTravel — same DeviceHash appearing from
-- two different GeoCountries within a configurable time window.
--
-- Uses window functions on PiXL.Visit joined to PiXL.IP for geo data.
-- Applies integer bucket pattern for coarse geo filtering where applicable.
--
-- Device in New York at 10 AM and London at 10:30 AM = VPN, shared
-- credentials, or credential stuffing.
--
-- Design doc reference: §8.3 item 1 (Impossible Travel Detection)
-- ============================================================================
USE SmartPiXL;
GO

SET NOCOUNT ON;
GO

PRINT '--- 50: Impossible Travel Detection ---';
GO

-- =====================================================================
-- Step 1: Impossible travel detection view
-- =====================================================================
-- Detects the same device appearing from different countries in a
-- short time window. Uses LAG() to find consecutive visits from
-- different geolocations.
--
-- Integer bucket geo: lat*100/lon*100 buckets for coarse distance check.
-- If buckets differ significantly (>5 units ≈ >55 km), it's likely
-- impossible travel. Only flag if time gap < 4 hours (240 min).
-- =====================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_ImpossibleTravel
AS
WITH VisitGeo AS (
    SELECT
        v.VisitID,
        v.CompanyID,
        v.PiXLID,
        v.DeviceId,
        v.ReceivedAt,
        ip.IPAddress,
        ip.GeoCountry,
        ip.GeoCountryCode,
        ip.GeoCity,
        ip.GeoLat,
        ip.GeoLon,
        -- Integer bucket for coarse geo (lat*100, lon*100)
        CAST(ip.GeoLat * 100 AS INT) AS LatBucket,
        CAST(ip.GeoLon * 100 AS INT) AS LonBucket,
        -- Previous visit info for the same device
        LAG(v.ReceivedAt) OVER (
            PARTITION BY v.DeviceId ORDER BY v.ReceivedAt
        ) AS PrevReceivedAt,
        LAG(ip.GeoCountryCode) OVER (
            PARTITION BY v.DeviceId ORDER BY v.ReceivedAt
        ) AS PrevCountryCode,
        LAG(ip.GeoCity) OVER (
            PARTITION BY v.DeviceId ORDER BY v.ReceivedAt
        ) AS PrevCity,
        LAG(ip.GeoLat) OVER (
            PARTITION BY v.DeviceId ORDER BY v.ReceivedAt
        ) AS PrevLat,
        LAG(ip.GeoLon) OVER (
            PARTITION BY v.DeviceId ORDER BY v.ReceivedAt
        ) AS PrevLon,
        LAG(ip.IPAddress) OVER (
            PARTITION BY v.DeviceId ORDER BY v.ReceivedAt
        ) AS PrevIPAddress
    FROM PiXL.Visit v
    JOIN PiXL.IP ip ON v.IpId = ip.IpId
    WHERE v.DeviceId IS NOT NULL
      AND ip.GeoCountryCode IS NOT NULL
)
SELECT
    vg.VisitID,
    vg.CompanyID,
    vg.PiXLID,
    vg.DeviceId,
    vg.ReceivedAt                   AS CurrentTime,
    vg.IPAddress                    AS CurrentIP,
    vg.GeoCountryCode               AS CurrentCountry,
    vg.GeoCity                      AS CurrentCity,
    vg.GeoLat                       AS CurrentLat,
    vg.GeoLon                       AS CurrentLon,
    vg.PrevReceivedAt               AS PreviousTime,
    vg.PrevIPAddress                AS PreviousIP,
    vg.PrevCountryCode              AS PreviousCountry,
    vg.PrevCity                     AS PreviousCity,
    vg.PrevLat                      AS PreviousLat,
    vg.PrevLon                      AS PreviousLon,
    DATEDIFF(MINUTE, vg.PrevReceivedAt, vg.ReceivedAt) AS GapMinutes,
    -- Coarse distance estimate: bucket difference (each bucket ≈ 1.1 km at equator)
    ABS(vg.LatBucket - CAST(vg.PrevLat * 100 AS INT))
      + ABS(vg.LonBucket - CAST(vg.PrevLon * 100 AS INT)) AS BucketDistance
FROM VisitGeo vg
WHERE vg.PrevCountryCode IS NOT NULL
  -- Different country
  AND vg.GeoCountryCode <> vg.PrevCountryCode
  -- Within 4-hour window (impossible physical travel for different countries)
  AND DATEDIFF(MINUTE, vg.PrevReceivedAt, vg.ReceivedAt) <= 240
  -- Positive time gap (not reordered data)
  AND vg.PrevReceivedAt < vg.ReceivedAt;
GO

-- =====================================================================
-- Step 2: Verification
-- =====================================================================
IF OBJECT_ID('dbo.vw_Dash_ImpossibleTravel', 'V') IS NOT NULL
    PRINT '  OK: dbo.vw_Dash_ImpossibleTravel exists';
ELSE
    PRINT '  ERROR: dbo.vw_Dash_ImpossibleTravel missing!';
GO

PRINT '--- 50: Impossible travel complete ---';
GO
