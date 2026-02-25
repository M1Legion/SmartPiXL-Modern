/*  66_BuildMergedIpDb_v3.sql
    Build Ref.MergedIpRange -- a unified free IP intelligence table

    STRATEGY: /24 prefix expansion for equi-joins (no CROSS APPLY)
    ---------------------------------------------------------------
    Instead of doing per-row CROSS APPLY range lookups (O(N*logM)),
    expand MaxMind/ASN/RIR ranges into /24 blocks (one row per /24
    prefix covered). Then do fast equi-joins on Prefix24 = MidInt/256.

    Since MaxMind CIDRs don't overlap, each /24 maps to at most one
    MaxMind range. Sub-/24 ranges (multiple per /24) are handled by
    an additional MidInt BETWEEN filter after the equi-join.

    Phase 1: Insert DB-IP backbone (3.7M rows, ~10s)
    Phase 2: Build MaxMind City /24 lookup (~12M rows, ~30s)
    Phase 3: Update MaxMind City via equi-join
    Phase 4: Build MaxMind ASN /24 lookup (~12M rows, ~30s)
    Phase 5: Update MaxMind ASN via equi-join
    Phase 6: Build RIR /24 lookup (~12M rows, ~30s)
    Phase 7: Update RIR via equi-join
    Phase 8: Update bgp.tools ASN class (simple JOIN)
    Phase 9: Compute country consensus + best-of-breed
    Phase 10: Drop staging columns + indexes + stats

    Expected run time: ~5-15 minutes
*/
SET NOCOUNT ON;

PRINT '================================================';
PRINT '  Building Merged IP Intelligence Table (v3)';
PRINT '  /24 prefix expansion for equi-joins';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '================================================';
PRINT '';

-- ============================================================
-- Phase 1: Insert DB-IP backbone
-- ============================================================
PRINT 'Phase 1: Inserting DB-IP backbone...';

IF OBJECT_ID('Ref.MergedIpRange') IS NOT NULL DROP TABLE Ref.MergedIpRange;
GO

CREATE TABLE Ref.MergedIpRange (
    RangeId         INT IDENTITY(1,1) NOT NULL,
    StartInt        BIGINT NOT NULL,
    EndInt          BIGINT NOT NULL,
    MidInt          BIGINT NOT NULL,
    Prefix24        INT    NOT NULL,       -- MidInt / 256  (equi-join key)
    StartIp         VARCHAR(15)  NOT NULL,
    EndIp           VARCHAR(15)  NOT NULL,
    -- Country fields (populated in Phase 9)
    CountryCode     CHAR(2)      NULL,
    CountrySource   VARCHAR(30)  NULL,
    CountrySources  TINYINT      NULL,
    Continent       CHAR(2)      NULL,
    -- Per-source country (staging for consensus)
    DBIP_CC         CHAR(2)      NULL,
    MM_CC           CHAR(2)      NULL,
    RIR_CC          CHAR(2)      NULL,
    -- Location
    Region          NVARCHAR(100) NULL,
    City            NVARCHAR(150) NULL,
    PostalCode      VARCHAR(20)  NULL,
    Latitude        DECIMAL(9,4) NULL,
    Longitude       DECIMAL(9,4) NULL,
    AccuracyRadius  INT          NULL,
    Timezone        VARCHAR(60)  NULL,
    -- Per-source location (staging for best-of-breed)
    DBIP_Region     NVARCHAR(100) NULL,
    DBIP_City       NVARCHAR(150) NULL,
    DBIP_Lat        DECIMAL(9,4) NULL,
    DBIP_Lon        DECIMAL(9,4) NULL,
    MM_Region       NVARCHAR(100) NULL,
    MM_City         NVARCHAR(150) NULL,
    MM_Zip          VARCHAR(20)  NULL,
    MM_Lat          DECIMAL(9,4) NULL,
    MM_Lon          DECIMAL(9,4) NULL,
    MM_AccRadius    INT          NULL,
    MM_Timezone     VARCHAR(60)  NULL,
    MM_Continent    CHAR(2)      NULL,
    -- ASN
    Asn             INT          NULL,
    AsnOrg          VARCHAR(300) NULL,
    AsnClass        VARCHAR(20)  NULL,
    AsnCountryCode  CHAR(2)      NULL,
    -- RIR
    RirRegistry     VARCHAR(10)  NULL,
    RirDateAllocated CHAR(8)     NULL,
    -- Provenance
    BuiltAt         DATETIME2    NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_MergedIpRange PRIMARY KEY CLUSTERED (StartInt)
);
GO

INSERT INTO Ref.MergedIpRange (
    StartInt, EndInt, MidInt, Prefix24, StartIp, EndIp,
    DBIP_CC, Continent, DBIP_Region, DBIP_City, DBIP_Lat, DBIP_Lon
)
SELECT
    StartInt, EndInt,
    (StartInt + EndInt) / 2,
    CAST((StartInt + EndInt) / 2 / 256 AS INT),
    StartIp, EndIp,
    CountryCode, Continent, Region, City, Latitude, Longitude
FROM Ref.DbipCityLite;

DECLARE @total INT = (SELECT COUNT(*) FROM Ref.MergedIpRange);
PRINT '  Inserted: ' + FORMAT(@total, 'N0') + ' ranges';

-- Index on Prefix24 for the equi-joins
CREATE NONCLUSTERED INDEX IX_Merged_Prefix24
    ON Ref.MergedIpRange (Prefix24) INCLUDE (MidInt, RangeId);

PRINT '  Prefix24 index created.';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '';
GO

-- ============================================================
-- Phase 2: Build MaxMind City /24 expansion
-- ============================================================
PRINT 'Phase 2: Expanding MaxMind City to /24 blocks...';

IF OBJECT_ID('tempdb..#MM24') IS NOT NULL DROP TABLE #MM24;

SELECT
    CAST(gs.value AS INT)  AS Prefix24,
    mm.SInt                AS MM_StartInt,
    mm.EInt                AS MM_EndInt,
    cl.CountryIsoCode      AS MM_CC,
    cl.Subdivision1Name    AS MM_Region,
    cl.CityName            AS MM_City,
    cb.PostalCode          AS MM_Zip,
    cb.Latitude            AS MM_Lat,
    cb.Longitude           AS MM_Lon,
    cb.AccuracyRadius      AS MM_AccRadius,
    cl.TimeZone            AS MM_Timezone,
    cl.ContinentCode       AS MM_Continent
INTO #MM24
FROM Geo.CityBlock cb
JOIN Geo.CityLocation cl ON cb.GeonameId = cl.GeonameId AND cl.LocaleCode = 'en'
CROSS APPLY (
    SELECT
        CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 4) AS BIGINT) * 16777216 +
        CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 3) AS BIGINT) * 65536 +
        CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 2) AS BIGINT) * 256 +
        CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 1) AS BIGINT) AS SInt,
        CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 4) AS BIGINT) * 16777216 +
        CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 3) AS BIGINT) * 65536 +
        CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 2) AS BIGINT) * 256 +
        CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 1) AS BIGINT)
        + POWER(CAST(2 AS BIGINT), 32 - CAST(RIGHT(cb.NetworkCidr, LEN(cb.NetworkCidr) - CHARINDEX('/', cb.NetworkCidr)) AS INT)) - 1 AS EInt
) mm
CROSS APPLY GENERATE_SERIES(CAST(mm.SInt / 256 AS BIGINT), CAST(mm.EInt / 256 AS BIGINT)) gs
WHERE cb.NetworkCidr NOT LIKE '%:%';

CREATE CLUSTERED INDEX CIX_MM24 ON #MM24 (Prefix24, MM_StartInt);

DECLARE @mm24 INT = (SELECT COUNT(*) FROM #MM24);
PRINT '  /24 rows: ' + FORMAT(@mm24, 'N0');
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '';
GO

-- ============================================================
-- Phase 3: Update MaxMind City (equi-join on Prefix24)
-- For /24 blocks with multiple sub-ranges, use MidInt filter
-- ============================================================
PRINT 'Phase 3: Joining MaxMind City data...';

UPDATE m
SET m.MM_CC        = mm24.MM_CC,
    m.MM_Region    = mm24.MM_Region,
    m.MM_City      = mm24.MM_City,
    m.MM_Zip       = mm24.MM_Zip,
    m.MM_Lat       = mm24.MM_Lat,
    m.MM_Lon       = mm24.MM_Lon,
    m.MM_AccRadius = mm24.MM_AccRadius,
    m.MM_Timezone  = mm24.MM_Timezone,
    m.MM_Continent = mm24.MM_Continent
FROM Ref.MergedIpRange m
JOIN #MM24 mm24 ON mm24.Prefix24 = m.Prefix24
    AND m.MidInt >= mm24.MM_StartInt
    AND m.MidInt <= mm24.MM_EndInt;

DECLARE @mmJoined INT = @@ROWCOUNT;
PRINT '  Updated: ' + FORMAT(@mmJoined, 'N0') + ' rows with MaxMind city';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);

DROP TABLE #MM24;
PRINT '  #MM24 dropped.';
PRINT '';
GO

-- ============================================================
-- Phase 4: Build MaxMind ASN /24 expansion
-- ============================================================
PRINT 'Phase 4: Expanding MaxMind ASN to /24 blocks...';

IF OBJECT_ID('tempdb..#MMA24') IS NOT NULL DROP TABLE #MMA24;

SELECT
    CAST(gs.value AS INT)  AS Prefix24,
    mm.SInt                AS MMA_StartInt,
    mm.EInt                AS MMA_EndInt,
    a.AutonomousSystemNumber AS MMA_Asn,
    a.AutonomousSystemOrg    AS MMA_AsnOrg
INTO #MMA24
FROM Geo.ASN a
CROSS APPLY (
    SELECT
        CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 4) AS BIGINT) * 16777216 +
        CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 3) AS BIGINT) * 65536 +
        CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 2) AS BIGINT) * 256 +
        CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 1) AS BIGINT) AS SInt,
        CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 4) AS BIGINT) * 16777216 +
        CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 3) AS BIGINT) * 65536 +
        CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 2) AS BIGINT) * 256 +
        CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 1) AS BIGINT)
        + POWER(CAST(2 AS BIGINT), 32 - CAST(RIGHT(a.NetworkCidr, LEN(a.NetworkCidr) - CHARINDEX('/', a.NetworkCidr)) AS INT)) - 1 AS EInt
) mm
CROSS APPLY GENERATE_SERIES(CAST(mm.SInt / 256 AS BIGINT), CAST(mm.EInt / 256 AS BIGINT)) gs
WHERE a.NetworkCidr NOT LIKE '%:%'
  AND a.AutonomousSystemNumber IS NOT NULL;

CREATE CLUSTERED INDEX CIX_MMA24 ON #MMA24 (Prefix24, MMA_StartInt);

DECLARE @mma24 INT = (SELECT COUNT(*) FROM #MMA24);
PRINT '  /24 rows: ' + FORMAT(@mma24, 'N0');
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '';
GO

-- ============================================================
-- Phase 5: Update MaxMind ASN (equi-join on Prefix24)
-- ============================================================
PRINT 'Phase 5: Joining MaxMind ASN data...';

UPDATE m
SET m.Asn    = mma24.MMA_Asn,
    m.AsnOrg = mma24.MMA_AsnOrg
FROM Ref.MergedIpRange m
JOIN #MMA24 mma24 ON mma24.Prefix24 = m.Prefix24
    AND m.MidInt >= mma24.MMA_StartInt
    AND m.MidInt <= mma24.MMA_EndInt;

DECLARE @mmaJoined INT = @@ROWCOUNT;
PRINT '  Updated: ' + FORMAT(@mmaJoined, 'N0') + ' rows with MaxMind ASN';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);

DROP TABLE #MMA24;
PRINT '  #MMA24 dropped.';
PRINT '';
GO

-- ============================================================
-- Phase 6: Build RIR /24 expansion
-- ============================================================
PRINT 'Phase 6: Expanding RIR delegations to /24 blocks...';

IF OBJECT_ID('tempdb..#RIR24') IS NOT NULL DROP TABLE #RIR24;

SELECT
    CAST(gs.value AS INT) AS Prefix24,
    r.StartInt            AS RIR_StartInt,
    r.EndInt              AS RIR_EndInt,
    r.CountryCode         AS RIR_CC,
    r.Registry            AS RIR_Registry,
    r.DateAllocated       AS RIR_DateAlloc
INTO #RIR24
FROM Ref.RirDelegation r
CROSS APPLY GENERATE_SERIES(CAST(r.StartInt / 256 AS BIGINT), CAST(r.EndInt / 256 AS BIGINT)) gs;

CREATE CLUSTERED INDEX CIX_RIR24 ON #RIR24 (Prefix24, RIR_StartInt);

DECLARE @rir24 INT = (SELECT COUNT(*) FROM #RIR24);
PRINT '  /24 rows: ' + FORMAT(@rir24, 'N0');
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '';
GO

-- ============================================================
-- Phase 7: Update RIR (equi-join on Prefix24)
-- ============================================================
PRINT 'Phase 7: Joining RIR delegation data...';

UPDATE m
SET m.RIR_CC           = rir24.RIR_CC,
    m.RirRegistry      = rir24.RIR_Registry,
    m.RirDateAllocated = rir24.RIR_DateAlloc
FROM Ref.MergedIpRange m
JOIN #RIR24 rir24 ON rir24.Prefix24 = m.Prefix24
    AND m.MidInt >= rir24.RIR_StartInt
    AND m.MidInt <= rir24.RIR_EndInt;

DECLARE @rirJoined INT = @@ROWCOUNT;
PRINT '  Updated: ' + FORMAT(@rirJoined, 'N0') + ' rows with RIR data';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);

DROP TABLE #RIR24;
PRINT '  #RIR24 dropped.';
PRINT '';
GO

-- ============================================================
-- Phase 8: Update bgp.tools ASN class (simple JOIN on Asn)
-- ============================================================
PRINT 'Phase 8: Joining bgp.tools ASN class + org...';

UPDATE m
SET m.AsnOrg        = COALESCE(bgp.Name, m.AsnOrg),
    m.AsnClass      = COALESCE(bgp.Class, 'Unknown'),
    m.AsnCountryCode = bgp.CountryCode
FROM Ref.MergedIpRange m
JOIN Ref.BgpToolsAsn bgp ON m.Asn = bgp.Asn
WHERE m.Asn IS NOT NULL;

DECLARE @bgpRows INT = @@ROWCOUNT;
PRINT '  bgp.tools updated: ' + FORMAT(@bgpRows, 'N0') + ' rows';

-- Set Unknown for rows with ASN but no bgp.tools match
UPDATE Ref.MergedIpRange SET AsnClass = 'Unknown'
WHERE Asn IS NOT NULL AND AsnClass IS NULL;

PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '';
GO

-- ============================================================
-- Phase 9: Compute country consensus + best-of-breed fields
-- ============================================================
PRINT 'Phase 9: Computing country consensus + field selection...';

UPDATE Ref.MergedIpRange
SET
    CountryCode = CASE
        WHEN DBIP_CC = MM_CC AND DBIP_CC = RIR_CC THEN DBIP_CC
        WHEN DBIP_CC = MM_CC                       THEN DBIP_CC
        WHEN DBIP_CC = RIR_CC                      THEN DBIP_CC
        WHEN MM_CC = RIR_CC AND MM_CC IS NOT NULL  THEN MM_CC
        ELSE COALESCE(DBIP_CC, MM_CC, RIR_CC)
    END,
    CountrySource = CASE
        WHEN DBIP_CC = MM_CC AND DBIP_CC = RIR_CC           THEN 'All-3-agree'
        WHEN DBIP_CC = MM_CC                                 THEN 'DBIP+MM-agree'
        WHEN DBIP_CC = RIR_CC                                THEN 'DBIP+RIR-agree'
        WHEN MM_CC = RIR_CC AND MM_CC IS NOT NULL            THEN 'MM+RIR-agree'
        WHEN DBIP_CC IS NOT NULL AND MM_CC IS NOT NULL AND RIR_CC IS NOT NULL THEN '3-way-split'
        WHEN DBIP_CC IS NOT NULL AND MM_CC IS NOT NULL       THEN 'DBIP+MM-only'
        WHEN DBIP_CC IS NOT NULL AND RIR_CC IS NOT NULL      THEN 'DBIP+RIR-only'
        ELSE 'DBIP-only'
    END,
    CountrySources = CASE WHEN DBIP_CC IS NOT NULL THEN 1 ELSE 0 END
                   + CASE WHEN MM_CC IS NOT NULL THEN 1 ELSE 0 END
                   + CASE WHEN RIR_CC IS NOT NULL THEN 1 ELSE 0 END,
    Continent = COALESCE(Continent, MM_Continent),
    Region    = COALESCE(MM_Region, DBIP_Region),
    City      = COALESCE(MM_City, DBIP_City),
    PostalCode = MM_Zip,
    Latitude  = COALESCE(DBIP_Lat, MM_Lat),
    Longitude = COALESCE(DBIP_Lon, MM_Lon),
    AccuracyRadius = MM_AccRadius,
    Timezone  = MM_Timezone;

DECLARE @consensus INT = @@ROWCOUNT;
PRINT '  Consensus computed for ' + FORMAT(@consensus, 'N0') + ' rows';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '';
GO

-- ============================================================
-- Phase 10: Drop staging columns, create indexes, stats
-- ============================================================
PRINT 'Phase 10: Cleaning up + creating indexes...';

ALTER TABLE Ref.MergedIpRange DROP COLUMN DBIP_CC;
ALTER TABLE Ref.MergedIpRange DROP COLUMN MM_CC;
ALTER TABLE Ref.MergedIpRange DROP COLUMN RIR_CC;
ALTER TABLE Ref.MergedIpRange DROP COLUMN DBIP_Region;
ALTER TABLE Ref.MergedIpRange DROP COLUMN DBIP_City;
ALTER TABLE Ref.MergedIpRange DROP COLUMN DBIP_Lat;
ALTER TABLE Ref.MergedIpRange DROP COLUMN DBIP_Lon;
ALTER TABLE Ref.MergedIpRange DROP COLUMN MM_Region;
ALTER TABLE Ref.MergedIpRange DROP COLUMN MM_City;
ALTER TABLE Ref.MergedIpRange DROP COLUMN MM_Zip;
ALTER TABLE Ref.MergedIpRange DROP COLUMN MM_Lat;
ALTER TABLE Ref.MergedIpRange DROP COLUMN MM_Lon;
ALTER TABLE Ref.MergedIpRange DROP COLUMN MM_AccRadius;
ALTER TABLE Ref.MergedIpRange DROP COLUMN MM_Timezone;
ALTER TABLE Ref.MergedIpRange DROP COLUMN MM_Continent;

PRINT '  Staging columns dropped.';
GO

CREATE NONCLUSTERED INDEX IX_MergedIpRange_EndInt
    ON Ref.MergedIpRange (EndInt)
    INCLUDE (StartInt, CountryCode, Region, City, Latitude, Longitude, Asn, AsnClass);

CREATE NONCLUSTERED INDEX IX_MergedIpRange_Country
    ON Ref.MergedIpRange (CountryCode)
    INCLUDE (Region, City, Asn, AsnClass);

CREATE NONCLUSTERED INDEX IX_MergedIpRange_Asn
    ON Ref.MergedIpRange (Asn)
    INCLUDE (CountryCode, AsnClass, AsnOrg);

CREATE NONCLUSTERED INDEX IX_MergedIpRange_MidInt
    ON Ref.MergedIpRange (MidInt);

PRINT '  Indexes created.';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '';
GO

-- ============================================================
-- Phase 11: Summary statistics
-- ============================================================
PRINT '================================================';
PRINT '  MERGED TABLE STATISTICS';
PRINT '================================================';
PRINT '';

DECLARE @total INT = (SELECT COUNT(*) FROM Ref.MergedIpRange);

PRINT '-- Overall coverage --';
SELECT
    @total AS TotalRanges,
    SUM(CAST(EndInt - StartInt + 1 AS BIGINT)) AS TotalIPs,
    FORMAT(SUM(CAST(EndInt - StartInt + 1 AS BIGINT)), 'N0') AS TotalIPs_Fmt,
    CAST(100.0 * SUM(CAST(EndInt - StartInt + 1 AS BIGINT)) / 4294967296.0 AS DECIMAL(5,2)) AS PctIPv4Space
FROM Ref.MergedIpRange;

PRINT '';
PRINT '-- Country source breakdown --';
SELECT
    CountrySource,
    COUNT(*) AS Ranges,
    SUM(CAST(EndInt - StartInt + 1 AS BIGINT)) AS IPs,
    CAST(100.0 * COUNT(*) / @total AS DECIMAL(5,2)) AS PctRanges
FROM Ref.MergedIpRange
GROUP BY CountrySource
ORDER BY Ranges DESC;

PRINT '';
PRINT '-- Field coverage --';
SELECT
    SUM(CASE WHEN CountryCode IS NOT NULL THEN 1 ELSE 0 END) AS HasCountry,
    SUM(CASE WHEN Region IS NOT NULL THEN 1 ELSE 0 END) AS HasRegion,
    SUM(CASE WHEN City IS NOT NULL THEN 1 ELSE 0 END) AS HasCity,
    SUM(CASE WHEN PostalCode IS NOT NULL THEN 1 ELSE 0 END) AS HasZip,
    SUM(CASE WHEN Latitude IS NOT NULL THEN 1 ELSE 0 END) AS HasLatLon,
    SUM(CASE WHEN Asn IS NOT NULL THEN 1 ELSE 0 END) AS HasAsn,
    SUM(CASE WHEN AsnClass IS NOT NULL AND AsnClass <> 'Unknown' THEN 1 ELSE 0 END) AS HasAsnClass,
    SUM(CASE WHEN Timezone IS NOT NULL THEN 1 ELSE 0 END) AS HasTimezone,
    SUM(CASE WHEN RirRegistry IS NOT NULL THEN 1 ELSE 0 END) AS HasRir
FROM Ref.MergedIpRange;

PRINT '';
PRINT '-- ASN class distribution (by IP count) --';
SELECT
    COALESCE(AsnClass, '(no ASN)') AS AsnClass,
    COUNT(*) AS Ranges,
    SUM(CAST(EndInt - StartInt + 1 AS BIGINT)) AS IPs,
    FORMAT(SUM(CAST(EndInt - StartInt + 1 AS BIGINT)), 'N0') AS IPs_Fmt
FROM Ref.MergedIpRange
GROUP BY AsnClass
ORDER BY IPs DESC;

PRINT '';
PRINT '-- Top 10 countries by IP count --';
SELECT TOP 10
    CountryCode,
    COUNT(*) AS Ranges,
    SUM(CAST(EndInt - StartInt + 1 AS BIGINT)) AS IPs,
    FORMAT(SUM(CAST(EndInt - StartInt + 1 AS BIGINT)), 'N0') AS IPs_Fmt
FROM Ref.MergedIpRange
WHERE CountryCode IS NOT NULL
GROUP BY CountryCode
ORDER BY IPs DESC;

PRINT '';
PRINT '-- Confidence distribution --';
SELECT
    CountrySources AS SourcesAgreeing,
    COUNT(*) AS Ranges,
    CAST(100.0 * COUNT(*) / @total AS DECIMAL(5,2)) AS Pct
FROM Ref.MergedIpRange
GROUP BY CountrySources
ORDER BY CountrySources DESC;

PRINT '';
PRINT '================================================';
PRINT '  COMPLETE';
PRINT '  Ref.MergedIpRange: ' + FORMAT(@total, 'N0') + ' ranges';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '================================================';
