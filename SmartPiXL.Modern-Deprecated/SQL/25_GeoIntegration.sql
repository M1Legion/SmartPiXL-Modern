-- ============================================================================
-- 25_GeoIntegration.sql
-- Adds geo columns to PiXL.Parsed and PiXL.IP, creates IPAPI sync watermark,
-- and updates ETL.usp_ParseNewHits to JOIN IPAPI.IP for geo enrichment.
--
-- Prerequisites:
--   - IPAPI schema and IPAPI.IP table (created during backfill)
--   - PiXL.Parsed table (16_MaterializedParsedTable.sql)
--   - PiXL.IP table (19_DeviceIpVisitMatchTables.sql)
--   - ETL.Watermark table (20_ETLPhases9to13.sql)
--
-- Safe to re-run: all DDL is idempotent (IF NOT EXISTS / IF COL_LENGTH IS NULL).
-- ============================================================================

SET NOCOUNT ON;
PRINT '=== 25_GeoIntegration.sql ===';
PRINT '';

-- ============================================================================
-- PHASE 1: Add geo columns to PiXL.IP (denormalized from IPAPI.IP during ETL)
-- ============================================================================
PRINT '--- Phase 1: Add geo columns to PiXL.IP ---';

IF COL_LENGTH('PiXL.IP', 'GeoCountry') IS NULL
BEGIN
    ALTER TABLE PiXL.IP ADD
        GeoCountry        VARCHAR(99)   NULL,
        GeoCountryCode    VARCHAR(10)   NULL,
        GeoRegion         VARCHAR(99)   NULL,
        GeoCity           VARCHAR(99)   NULL,
        GeoZip            VARCHAR(20)   NULL,
        GeoLat            DECIMAL(9,4)  NULL,
        GeoLon            DECIMAL(9,4)  NULL,
        GeoTimezone       VARCHAR(50)   NULL,
        GeoISP            VARCHAR(200)  NULL,
        GeoOrg            VARCHAR(200)  NULL,
        GeoIsProxy        BIT           NULL,
        GeoIsMobile       BIT           NULL,
        GeoLastUpdated    DATETIME2(3)  NULL;    -- When geo was last refreshed from IPAPI
    PRINT '  Added 13 geo columns to PiXL.IP';
END
ELSE
    PRINT '  PiXL.IP geo columns already exist — skipped';
GO

-- ============================================================================
-- PHASE 2: Add geo columns to PiXL.Parsed (populated during ETL via IPAPI.IP)
-- ============================================================================
PRINT '--- Phase 2: Add geo columns to PiXL.Parsed ---';

IF COL_LENGTH('PiXL.Parsed', 'GeoCountry') IS NULL
BEGIN
    ALTER TABLE PiXL.Parsed ADD
        GeoCountry        VARCHAR(99)   NULL,
        GeoCountryCode    VARCHAR(10)   NULL,
        GeoRegion         VARCHAR(99)   NULL,
        GeoCity           VARCHAR(99)   NULL,
        GeoZip            VARCHAR(20)   NULL,
        GeoLat            DECIMAL(9,4)  NULL,
        GeoLon            DECIMAL(9,4)  NULL,
        GeoTimezone       VARCHAR(50)   NULL,
        GeoISP            VARCHAR(200)  NULL,
        GeoTzMismatch     BIT           NULL;    -- 1 = client TZ ≠ IP-derived TZ
    PRINT '  Added 10 geo columns to PiXL.Parsed';
END
ELSE
    PRINT '  PiXL.Parsed geo columns already exist — skipped';
GO

-- ============================================================================
-- PHASE 3: IPAPI sync watermark in ETL.Watermark
-- ============================================================================
PRINT '--- Phase 3: IPAPI sync watermark ---';

IF NOT EXISTS (SELECT 1 FROM ETL.Watermark WHERE ProcessName = 'IpApiSync')
BEGIN
    INSERT INTO ETL.Watermark (ProcessName, LastProcessedId, RowsProcessed)
    VALUES ('IpApiSync', 0, 0);
    PRINT '  Inserted IpApiSync watermark';
END
ELSE
    PRINT '  IpApiSync watermark already exists — skipped';
GO

-- ============================================================================
-- PHASE 4: Create IPAPI.SyncLog table for operational tracking
-- ============================================================================
PRINT '--- Phase 4: IPAPI.SyncLog ---';

IF OBJECT_ID('IPAPI.SyncLog', 'U') IS NULL
BEGIN
    CREATE TABLE IPAPI.SyncLog
    (
        SyncId          INT           NOT NULL  IDENTITY(1,1),
        StartedAt       DATETIME2(3)  NOT NULL  DEFAULT SYSUTCDATETIME(),
        CompletedAt     DATETIME2(3)  NULL,
        RowsInserted    INT           NOT NULL  DEFAULT 0,
        RowsUpdated     INT           NOT NULL  DEFAULT 0,
        WatermarkBefore DATETIME2(3)  NULL,
        WatermarkAfter  DATETIME2(3)  NULL,
        DurationMs      INT           NULL,
        ErrorMessage    VARCHAR(2000) NULL,

        CONSTRAINT PK_IPAPI_SyncLog PRIMARY KEY CLUSTERED (SyncId)
    );
    PRINT '  Created IPAPI.SyncLog';
END
ELSE
    PRINT '  IPAPI.SyncLog already exists — skipped';
GO

-- ============================================================================
-- PHASE 5: Create usp_EnrichGeo — Backfills geo on PiXL.IP from IPAPI.IP
-- Called by IpApiSyncService after each sync, and by ETL as needed.
-- ============================================================================
PRINT '--- Phase 5: IPAPI.usp_EnrichGeo ---';
GO

CREATE OR ALTER PROCEDURE IPAPI.usp_EnrichGeo
    @BatchSize INT = 10000
AS
BEGIN
    SET NOCOUNT ON;

    -- Update PiXL.IP rows that have NULL geo but exist in IPAPI.IP
    UPDATE TOP (@BatchSize) pip SET
        pip.GeoCountry     = ipa.Country,
        pip.GeoCountryCode = ipa.CountryCode,
        pip.GeoRegion      = ipa.RegionName,
        pip.GeoCity        = ipa.City,
        pip.GeoZip         = LEFT(ipa.Zip, 20),
        pip.GeoLat         = TRY_CAST(ipa.Lat AS DECIMAL(9,4)),
        pip.GeoLon         = TRY_CAST(ipa.Lon AS DECIMAL(9,4)),
        pip.GeoTimezone    = ipa.Timezone,
        pip.GeoISP         = LEFT(ipa.ISP, 200),
        pip.GeoOrg         = LEFT(ipa.Org, 200),
        pip.GeoIsProxy     = CASE WHEN ipa.Proxy = 'true' THEN 1
                                  WHEN ipa.Proxy = 'false' THEN 0
                                  ELSE NULL END,
        pip.GeoIsMobile    = CASE WHEN ipa.Mobile = 'true' THEN 1
                                  WHEN ipa.Mobile = 'false' THEN 0
                                  ELSE NULL END,
        pip.GeoLastUpdated = SYSUTCDATETIME()
    FROM PiXL.IP pip
    INNER JOIN IPAPI.IP ipa ON pip.IPAddress = ipa.IP
    WHERE pip.GeoCountry IS NULL
      AND ipa.Status = 'success';

    SELECT @@ROWCOUNT AS RowsEnriched;
END;
GO

PRINT '  Created IPAPI.usp_EnrichGeo';
PRINT '';
PRINT '=== 25_GeoIntegration.sql complete ===';
