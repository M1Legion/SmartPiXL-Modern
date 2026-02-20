-- ============================================================================
-- 26_ETL_GeoEnrichment.sql
-- Adds geo enrichment to ETL.usp_ParseNewHits:
--   - After Phase 2 (INSERT PiXL.Parsed), adds an UPDATE to populate
--     geo columns by JOINing IPAPI.IP on IPAddress.
--   - Also extracts _srv_geo* params from QueryString as fallback.
--   - Computes GeoTzMismatch by comparing client TZ vs IP-derived TZ.
--
-- After Phase 11 (MERGE PiXL.IP), enriches PiXL.IP geo columns from IPAPI.IP.
--
-- This script uses CREATE OR ALTER to replace the entire proc. It includes
-- all existing phases plus the new geo enrichment.
--
-- Safe to re-run (CREATE OR ALTER is idempotent).
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
PRINT '=== 26_ETL_GeoEnrichment.sql ===';
PRINT '';

-- ============================================================================
-- Add an ETL phase that runs AFTER the main ParseNewHits to enrich geo data.
-- Rather than modifying the large proc, we add a post-processing step that
-- runs in the same 60-second cycle, called by EtlBackgroundService.
-- ============================================================================

PRINT '--- Creating ETL.usp_EnrichParsedGeo ---';
GO

CREATE OR ALTER PROCEDURE ETL.usp_EnrichParsedGeo
    @BatchSize INT = 10000
AS
BEGIN
    SET NOCOUNT ON;
    
    -- ==========================================================================
    -- Step 1: Enrich PiXL.Parsed with geo from IPAPI.IP
    -- Targets rows where GeoCountry IS NULL but IPAddress exists in IPAPI.IP.
    -- Uses a watermark approach: process oldest un-enriched rows first.
    -- ==========================================================================
    
    DECLARE @Enriched INT = 0;
    
    UPDATE TOP (@BatchSize) pp SET
        pp.GeoCountry     = ipa.Country,
        pp.GeoCountryCode = ipa.CountryCode,
        pp.GeoRegion      = ipa.RegionName,
        pp.GeoCity        = ipa.City,
        pp.GeoZip         = LEFT(ipa.Zip, 20),
        pp.GeoLat         = TRY_CAST(ipa.Lat AS DECIMAL(9,4)),
        pp.GeoLon         = TRY_CAST(ipa.Lon AS DECIMAL(9,4)),
        pp.GeoTimezone    = ipa.Timezone,
        pp.GeoISP         = LEFT(ipa.ISP, 200),
        -- Timezone mismatch: client-reported TZ ≠ IP-derived TZ
        pp.GeoTzMismatch  = CASE 
            WHEN pp.Timezone IS NOT NULL AND ipa.Timezone IS NOT NULL
                 AND pp.Timezone <> ipa.Timezone
            THEN 1
            ELSE 0
        END
    FROM PiXL.Parsed pp
    INNER JOIN IPAPI.IP ipa ON pp.IPAddress = ipa.IP
    WHERE pp.GeoCountry IS NULL
      AND ipa.Status = 'success';
    
    SET @Enriched = @@ROWCOUNT;
    
    -- ==========================================================================
    -- Step 2: For rows where IPAPI.IP didn't have a match, try _srv_geo* params
    -- These are appended by GeoCacheService on the hot path (already URL-decoded
    -- by dbo.GetQueryParam).
    -- ==========================================================================
    
    DECLARE @SrvEnriched INT = 0;
    
    UPDATE TOP (@BatchSize) pp SET
        pp.GeoCountryCode = dbo.GetQueryParam(src.QueryString, '_srv_geoCC'),
        pp.GeoRegion      = dbo.GetQueryParam(src.QueryString, '_srv_geoReg'),
        pp.GeoCity        = dbo.GetQueryParam(src.QueryString, '_srv_geoCity'),
        pp.GeoTimezone    = dbo.GetQueryParam(src.QueryString, '_srv_geoTz'),
        pp.GeoISP         = dbo.GetQueryParam(src.QueryString, '_srv_geoISP'),
        pp.GeoTzMismatch  = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_geoTzMismatch') AS BIT)
    FROM PiXL.Parsed pp
    INNER JOIN PiXL.Raw src ON pp.SourceId = src.Id
    WHERE pp.GeoCountry IS NULL
      AND pp.GeoCountryCode IS NULL
      AND src.QueryString LIKE '%_srv_geoCC=%';
    
    SET @SrvEnriched = @@ROWCOUNT;
    
    -- ==========================================================================
    -- Step 3: Enrich PiXL.IP with geo from IPAPI.IP (same as IPAPI.usp_EnrichGeo
    -- but inlined here for a single-pass approach).
    -- ==========================================================================
    
    DECLARE @IpEnriched INT = 0;
    
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
    
    SET @IpEnriched = @@ROWCOUNT;
    
    SELECT @Enriched AS ParsedEnriched, @SrvEnriched AS SrvFallbackEnriched, @IpEnriched AS IpEnriched;
END;
GO

PRINT '  Created ETL.usp_EnrichParsedGeo';
PRINT '';
PRINT '=== 26_ETL_GeoEnrichment.sql complete ===';
