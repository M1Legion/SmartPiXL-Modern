-- ============================================================================
-- 82_UserGeolocation.sql
-- ============================================================================
-- Adds 13 columns to PiXL.Parsed capturing the W3C Geolocation API data
-- returned by the browser when a user grants the permission prompt.
--
-- Columns are nullable — most hits will have NULL here because (a) prompt
-- is only fired after 3s of engagement, (b) users can deny, (c) many
-- browsers pre-deny after a prior denial for this origin.
--
-- Populated by:
--   * PiXLScript.cs — generates the geolocation capture JS and fires a
--     separate beacon (_geo_followup=1) with the _usr_* params once the
--     browser resolves (grant, deny, timeout, etc).
--   * ParsedRecordParser.cs — reads _usr_* params into these columns on
--     every hit; geo followup hits carry the meaningful values, regular
--     hits leave them NULL.
--   * Derived fields (UserClockSkewMs, UserGeoVsIpKm) are computed in the
--     parser when source values are present.
--
-- Safe to run BEFORE or AFTER 80_Parsed_Partitioning.sql. This is a
-- metadata-only ALTER TABLE — SQL Server does not touch row data when
-- adding nullable columns, so runtime is seconds even at 381M rows.
-- ============================================================================

SET XACT_ABORT ON;
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
GO

USE SmartPiXL;
GO

PRINT CONCAT('[', SYSUTCDATETIME(), '] Adding user-geolocation columns to PiXL.Parsed');
GO

-- Idempotent: skip columns that already exist so this file can be re-run
-- without error after a partial apply.
IF COL_LENGTH('PiXL.Parsed', 'UserLat') IS NULL
    ALTER TABLE PiXL.Parsed ADD UserLat              DECIMAL(9,6)  NULL;
GO
IF COL_LENGTH('PiXL.Parsed', 'UserLon') IS NULL
    ALTER TABLE PiXL.Parsed ADD UserLon              DECIMAL(9,6)  NULL;
GO
IF COL_LENGTH('PiXL.Parsed', 'UserAccuracyM') IS NULL
    ALTER TABLE PiXL.Parsed ADD UserAccuracyM        INT           NULL;
GO
IF COL_LENGTH('PiXL.Parsed', 'UserAltitudeM') IS NULL
    ALTER TABLE PiXL.Parsed ADD UserAltitudeM        DECIMAL(9,2)  NULL;
GO
IF COL_LENGTH('PiXL.Parsed', 'UserAltitudeAccuracyM') IS NULL
    ALTER TABLE PiXL.Parsed ADD UserAltitudeAccuracyM INT          NULL;
GO
IF COL_LENGTH('PiXL.Parsed', 'UserHeadingDeg') IS NULL
    ALTER TABLE PiXL.Parsed ADD UserHeadingDeg       DECIMAL(5,2)  NULL;
GO
IF COL_LENGTH('PiXL.Parsed', 'UserSpeedMps') IS NULL
    ALTER TABLE PiXL.Parsed ADD UserSpeedMps         DECIMAL(7,2)  NULL;
GO
IF COL_LENGTH('PiXL.Parsed', 'UserGeoTimestamp') IS NULL
    ALTER TABLE PiXL.Parsed ADD UserGeoTimestamp     DATETIME2(3)  NULL;
GO
-- Status vocabulary (all lowercase, populated by the JS capture):
--   granted       — user clicked Allow this session
--   denied        — user clicked Block this session
--   pre_denied    — permissions.query returned 'denied' (user blocked previously)
--   unavailable   — PositionError.code = 2 (hardware/network couldn't fix)
--   timeout       — PositionError.code = 3 (fix took longer than our timeout)
--   no_response   — prompt displayed but user ignored it past our cap
--   not_supported — navigator.geolocation undefined (ancient browser, lockdown)
--   blocked_policy — Permissions-Policy header on host site disallows geolocation
IF COL_LENGTH('PiXL.Parsed', 'UserGeoStatus') IS NULL
    ALTER TABLE PiXL.Parsed ADD UserGeoStatus        VARCHAR(20)   NULL;
GO
-- PositionError.code: 1=PERMISSION_DENIED, 2=POSITION_UNAVAILABLE, 3=TIMEOUT
IF COL_LENGTH('PiXL.Parsed', 'UserGeoErrorCode') IS NULL
    ALTER TABLE PiXL.Parsed ADD UserGeoErrorCode     TINYINT       NULL;
GO
-- (server ReceivedAt ms) - (client geolocation timestamp ms). Same idea as
-- the classic client-clock-skew check but derived from the geolocation API
-- timestamp which some evasion tools forget to spoof separately from Date.now().
IF COL_LENGTH('PiXL.Parsed', 'UserClockSkewMs') IS NULL
    ALTER TABLE PiXL.Parsed ADD UserClockSkewMs      INT           NULL;
GO
-- True when the 3-second watchPosition observed speed>0.5 m/s or a non-null
-- heading, indicating the user was actively moving (walking, driving).
IF COL_LENGTH('PiXL.Parsed', 'UserGeoIsMoving') IS NULL
    ALTER TABLE PiXL.Parsed ADD UserGeoIsMoving      BIT           NULL;
GO
-- Haversine distance between user-granted lat/lon and MaxMind lat/lon, in km.
-- This is the VPN/proxy detection gold: >500km mismatch with high accuracy is
-- near-proof of a VPN (browser reports true Wi-Fi/GPS position while IP geo
-- is elsewhere).
IF COL_LENGTH('PiXL.Parsed', 'UserGeoVsIpKm') IS NULL
    ALTER TABLE PiXL.Parsed ADD UserGeoVsIpKm        INT           NULL;
GO

PRINT CONCAT('[', SYSUTCDATETIME(), '] Column add complete. Verifying:');

SELECT name, system_type_name = TYPE_NAME(user_type_id),
       max_length, precision, scale, is_nullable
FROM sys.columns
WHERE object_id = OBJECT_ID('PiXL.Parsed')
  AND name LIKE 'UserGeo%'  OR name LIKE 'User%'
ORDER BY column_id;
GO

PRINT CONCAT('[', SYSUTCDATETIME(), '] Done. After applying, deploy the updated Edge (PiXLScript.cs) and Forge (ParsedRecordParser.cs) builds.');
GO
