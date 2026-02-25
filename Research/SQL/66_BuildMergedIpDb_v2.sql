/*  66_BuildMergedIpDb_v2.sql
    Build Ref.MergedIpRange -- a unified free IP intelligence table
    combining the best field from each source.

    PHASED APPROACH (avoids triple-OUTER-APPLY bottleneck):
      Phase 1: Bulk insert DB-IP backbone (3.7M ranges, ~10s)
      Phase 2: Pre-compute MaxMind integer range temp tables (~20s)
      Phase 3: Update MaxMind City fields in 100K batches
      Phase 4: Update MaxMind ASN fields in 100K batches
      Phase 5: Update RIR fields in 100K batches
      Phase 6: Update bgp.tools ASN class (simple JOIN on Asn)
      Phase 7: Compute country consensus + source labels
      Phase 8: Create indexes + stats

    Run time: ~10-20 minutes
*/
SET NOCOUNT ON;

PRINT '================================================';
PRINT '  Building Merged IP Intelligence Table (v2)';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '================================================';
PRINT '';

-- ============================================================
-- Phase 1: Insert DB-IP backbone
-- ============================================================
PRINT 'Phase 1: Inserting DB-IP backbone...';

IF OBJECT_ID('Ref.MergedIpRange') IS NOT NULL DROP TABLE Ref.MergedIpRange;

CREATE TABLE Ref.MergedIpRange (
    RangeId         INT IDENTITY(1,1) NOT NULL,
    StartInt        BIGINT NOT NULL,
    EndInt          BIGINT NOT NULL,
    MidInt          BIGINT NOT NULL,      -- pre-computed midpoint for lookups
    StartIp         VARCHAR(15)  NOT NULL,
    EndIp           VARCHAR(15)  NOT NULL,
    -- Country fields (populated in Phase 7)
    CountryCode     CHAR(2)      NULL,    -- final consensus
    CountrySource   VARCHAR(30)  NULL,    -- 'All-3-agree', 'DBIP+MM-agree', etc.
    CountrySources  TINYINT      NULL,    -- how many sources had data
    Continent       CHAR(2)      NULL,
    -- Per-source country (for consensus calc)
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
    -- Per-source location (for best-of-breed selection in Phase 7)
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

PRINT '  Table created. Inserting DB-IP backbone...';

INSERT INTO Ref.MergedIpRange (
    StartInt, EndInt, MidInt, StartIp, EndIp,
    DBIP_CC, Continent, DBIP_Region, DBIP_City, DBIP_Lat, DBIP_Lon
)
SELECT
    StartInt, EndInt,
    (StartInt + EndInt) / 2,
    StartIp, EndIp,
    CountryCode, Continent, Region, City, Latitude, Longitude
FROM Ref.DbipCityLite;

DECLARE @total INT = (SELECT COUNT(*) FROM Ref.MergedIpRange);
PRINT '  Inserted: ' + FORMAT(@total, 'N0') + ' ranges';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '';
GO

-- ============================================================
-- Phase 2: Pre-compute MaxMind integer range temp tables
-- ============================================================
PRINT 'Phase 2: Building MaxMind temp tables...';

IF OBJECT_ID('tempdb..#MM') IS NOT NULL DROP TABLE #MM;

SELECT
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 4) AS BIGINT) * 16777216 +
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 3) AS BIGINT) * 65536 +
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 2) AS BIGINT) * 256 +
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 1) AS BIGINT) AS StartInt,
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 4) AS BIGINT) * 16777216 +
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 3) AS BIGINT) * 65536 +
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 2) AS BIGINT) * 256 +
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 1) AS BIGINT)
    + POWER(CAST(2 AS BIGINT), 32 - CAST(RIGHT(cb.NetworkCidr, LEN(cb.NetworkCidr) - CHARINDEX('/', cb.NetworkCidr)) AS INT)) - 1 AS EndInt,
    cl.CountryIsoCode  AS MM_CC,
    cl.Subdivision1Name AS MM_Region,
    cl.CityName        AS MM_City,
    cb.PostalCode      AS MM_Zip,
    cb.Latitude        AS MM_Lat,
    cb.Longitude       AS MM_Lon,
    cb.AccuracyRadius  AS MM_AccRadius,
    cl.TimeZone        AS MM_Timezone,
    cl.ContinentCode   AS MM_Continent
INTO #MM
FROM Geo.CityBlock cb
JOIN Geo.CityLocation cl ON cb.GeonameId = cl.GeonameId AND cl.LocaleCode = 'en'
WHERE cb.NetworkCidr NOT LIKE '%:%';

CREATE CLUSTERED INDEX CIX_MM ON #MM (StartInt);

DECLARE @mmCount INT = (SELECT COUNT(*) FROM #MM);
PRINT '  MaxMind CityBlock: ' + FORMAT(@mmCount, 'N0') + ' ranges';

IF OBJECT_ID('tempdb..#MMA') IS NOT NULL DROP TABLE #MMA;

SELECT
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 4) AS BIGINT) * 16777216 +
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 3) AS BIGINT) * 65536 +
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 2) AS BIGINT) * 256 +
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 1) AS BIGINT) AS StartInt,
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 4) AS BIGINT) * 16777216 +
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 3) AS BIGINT) * 65536 +
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 2) AS BIGINT) * 256 +
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 1) AS BIGINT)
    + POWER(CAST(2 AS BIGINT), 32 - CAST(RIGHT(a.NetworkCidr, LEN(a.NetworkCidr) - CHARINDEX('/', a.NetworkCidr)) AS INT)) - 1 AS EndInt,
    a.AutonomousSystemNumber AS MMA_Asn,
    a.AutonomousSystemOrg    AS MMA_AsnOrg
INTO #MMA
FROM Geo.ASN a
WHERE a.NetworkCidr NOT LIKE '%:%'
  AND a.AutonomousSystemNumber IS NOT NULL;

CREATE CLUSTERED INDEX CIX_MMA ON #MMA (StartInt);

DECLARE @mmaCount INT = (SELECT COUNT(*) FROM #MMA);
PRINT '  MaxMind ASN: ' + FORMAT(@mmaCount, 'N0') + ' ranges';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '';
GO

-- ============================================================
-- Phase 3: Update MaxMind City fields in batches
-- Uses CROSS APPLY on batches from MergedIpRange joined to #MM
-- ============================================================
PRINT 'Phase 3: Updating MaxMind City fields (100K batches)...';

DECLARE @batchSize INT = 100000;
DECLARE @minId INT = 1;
DECLARE @maxId INT = (SELECT MAX(RangeId) FROM Ref.MergedIpRange);
DECLARE @rowsDone INT = 0;

WHILE @minId <= @maxId
BEGIN
    UPDATE m
    SET m.MM_CC       = mm.MM_CC,
        m.MM_Region   = mm.MM_Region,
        m.MM_City     = mm.MM_City,
        m.MM_Zip      = mm.MM_Zip,
        m.MM_Lat      = mm.MM_Lat,
        m.MM_Lon      = mm.MM_Lon,
        m.MM_AccRadius = mm.MM_AccRadius,
        m.MM_Timezone  = mm.MM_Timezone,
        m.MM_Continent = mm.MM_Continent
    FROM Ref.MergedIpRange m
    CROSS APPLY (
        SELECT TOP 1
            MM_CC, MM_Region, MM_City, MM_Zip, MM_Lat, MM_Lon,
            MM_AccRadius, MM_Timezone, MM_Continent
        FROM #MM
        WHERE StartInt <= m.MidInt
          AND EndInt   >= m.MidInt
        ORDER BY StartInt DESC
    ) mm
    WHERE m.RangeId >= @minId
      AND m.RangeId <  @minId + @batchSize;

    SET @rowsDone += @@ROWCOUNT;
    SET @minId += @batchSize;
    PRINT '  MM City: ' + FORMAT(@rowsDone, 'N0') + ' updated  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
END

PRINT '  Phase 3 complete: ' + FORMAT(@rowsDone, 'N0') + ' rows with MaxMind city data';
PRINT '';
GO

-- ============================================================
-- Phase 4: Update MaxMind ASN fields in batches
-- ============================================================
PRINT 'Phase 4: Updating MaxMind ASN fields (100K batches)...';

DECLARE @batchSize INT = 100000;
DECLARE @minId INT = 1;
DECLARE @maxId INT = (SELECT MAX(RangeId) FROM Ref.MergedIpRange);
DECLARE @rowsDone INT = 0;

WHILE @minId <= @maxId
BEGIN
    UPDATE m
    SET m.Asn    = mma.MMA_Asn,
        m.AsnOrg = mma.MMA_AsnOrg
    FROM Ref.MergedIpRange m
    CROSS APPLY (
        SELECT TOP 1 MMA_Asn, MMA_AsnOrg
        FROM #MMA
        WHERE StartInt <= m.MidInt
          AND EndInt   >= m.MidInt
        ORDER BY StartInt DESC
    ) mma
    WHERE m.RangeId >= @minId
      AND m.RangeId <  @minId + @batchSize;

    SET @rowsDone += @@ROWCOUNT;
    SET @minId += @batchSize;
    PRINT '  MM ASN: ' + FORMAT(@rowsDone, 'N0') + ' updated  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
END

PRINT '  Phase 4 complete: ' + FORMAT(@rowsDone, 'N0') + ' rows with MaxMind ASN';
PRINT '';
GO

-- ============================================================
-- Phase 5: Update RIR fields in batches
-- ============================================================
PRINT 'Phase 5: Updating RIR fields (100K batches)...';

DECLARE @batchSize INT = 100000;
DECLARE @minId INT = 1;
DECLARE @maxId INT = (SELECT MAX(RangeId) FROM Ref.MergedIpRange);
DECLARE @rowsDone INT = 0;

WHILE @minId <= @maxId
BEGIN
    UPDATE m
    SET m.RIR_CC          = rir.CountryCode,
        m.RirRegistry     = rir.Registry,
        m.RirDateAllocated = rir.DateAllocated
    FROM Ref.MergedIpRange m
    CROSS APPLY (
        SELECT TOP 1 CountryCode, Registry, DateAllocated
        FROM Ref.RirDelegation
        WHERE StartInt <= m.MidInt
          AND EndInt   >= m.MidInt
        ORDER BY StartInt DESC
    ) rir
    WHERE m.RangeId >= @minId
      AND m.RangeId <  @minId + @batchSize;

    SET @rowsDone += @@ROWCOUNT;
    SET @minId += @batchSize;
    PRINT '  RIR: ' + FORMAT(@rowsDone, 'N0') + ' updated  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
END

PRINT '  Phase 5 complete: ' + FORMAT(@rowsDone, 'N0') + ' rows with RIR data';
PRINT '';
GO

-- ============================================================
-- Phase 6: Update bgp.tools ASN class (simple JOIN on Asn)
-- ============================================================
PRINT 'Phase 6: Updating bgp.tools ASN class + org...';

UPDATE m
SET m.AsnOrg        = COALESCE(bgp.Name, m.AsnOrg),
    m.AsnClass      = COALESCE(bgp.Class, 'Unknown'),
    m.AsnCountryCode = bgp.CountryCode
FROM Ref.MergedIpRange m
JOIN Ref.BgpToolsAsn bgp ON m.Asn = bgp.Asn
WHERE m.Asn IS NOT NULL;

DECLARE @bgpRows INT = @@ROWCOUNT;
PRINT '  Updated: ' + FORMAT(@bgpRows, 'N0') + ' rows with bgp.tools enrichment';

-- Set Unknown for rows with ASN but no bgp.tools match
UPDATE Ref.MergedIpRange
SET AsnClass = 'Unknown'
WHERE Asn IS NOT NULL AND AsnClass IS NULL;

PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '';
GO

-- ============================================================
-- Phase 7: Compute country consensus + best-of-breed fields
-- ============================================================
PRINT 'Phase 7: Computing country consensus + field selection...';

UPDATE Ref.MergedIpRange
SET
    -- Country: consensus vote
    CountryCode = CASE
        WHEN DBIP_CC = MM_CC AND DBIP_CC = RIR_CC THEN DBIP_CC
        WHEN DBIP_CC = MM_CC                       THEN DBIP_CC
        WHEN DBIP_CC = RIR_CC                      THEN DBIP_CC
        WHEN MM_CC = RIR_CC AND MM_CC IS NOT NULL  THEN MM_CC
        ELSE COALESCE(DBIP_CC, MM_CC, RIR_CC)
    END,

    -- Country source label
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

    -- How many sources had data
    CountrySources = CASE WHEN DBIP_CC IS NOT NULL THEN 1 ELSE 0 END
                   + CASE WHEN MM_CC IS NOT NULL THEN 1 ELSE 0 END
                   + CASE WHEN RIR_CC IS NOT NULL THEN 1 ELSE 0 END,

    -- Continent: DB-IP has it natively, MaxMind fallback
    Continent = COALESCE(Continent, MM_Continent),

    -- Region: MaxMind preferred (better string accuracy)
    Region = COALESCE(MM_Region, DBIP_Region),

    -- City: MaxMind preferred (better name accuracy)
    City = COALESCE(MM_City, DBIP_City),

    -- Zip: MaxMind only
    PostalCode = MM_Zip,

    -- Lat/Lon: DB-IP preferred (better proximity)
    Latitude  = COALESCE(DBIP_Lat, MM_Lat),
    Longitude = COALESCE(DBIP_Lon, MM_Lon),

    -- AccuracyRadius: MaxMind only
    AccuracyRadius = MM_AccRadius,

    -- Timezone: MaxMind only
    Timezone = MM_Timezone;

DECLARE @consensus INT = @@ROWCOUNT;
PRINT '  Consensus computed for ' + FORMAT(@consensus, 'N0') + ' rows';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '';
GO

-- ============================================================
-- Phase 8: Drop staging columns, create indexes
-- ============================================================
PRINT 'Phase 8: Cleaning up staging columns + creating indexes...';

-- Drop the per-source staging columns (no longer needed)
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

-- Range lookup index (for IP-to-range queries)
CREATE NONCLUSTERED INDEX IX_MergedIpRange_EndInt
    ON Ref.MergedIpRange (EndInt)
    INCLUDE (StartInt, CountryCode, Region, City, Latitude, Longitude, Asn, AsnClass);

-- Country lookup
CREATE NONCLUSTERED INDEX IX_MergedIpRange_Country
    ON Ref.MergedIpRange (CountryCode)
    INCLUDE (Region, City, Asn, AsnClass);

-- ASN lookup
CREATE NONCLUSTERED INDEX IX_MergedIpRange_Asn
    ON Ref.MergedIpRange (Asn)
    INCLUDE (CountryCode, AsnClass, AsnOrg);

-- MidInt index for joins
CREATE NONCLUSTERED INDEX IX_MergedIpRange_MidInt
    ON Ref.MergedIpRange (MidInt);

PRINT '  Indexes created.';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '';
GO

-- ============================================================
-- Phase 9: Summary statistics
-- ============================================================
PRINT '================================================';
PRINT '  MERGED TABLE STATISTICS';
PRINT '================================================';
PRINT '';

DECLARE @total INT = (SELECT COUNT(*) FROM Ref.MergedIpRange);

PRINT '-- 9a. Overall coverage --';
SELECT
    @total AS TotalRanges,
    SUM(CAST(EndInt - StartInt + 1 AS BIGINT)) AS TotalIPs,
    FORMAT(SUM(CAST(EndInt - StartInt + 1 AS BIGINT)), 'N0') AS TotalIPs_Fmt,
    CAST(100.0 * SUM(CAST(EndInt - StartInt + 1 AS BIGINT)) / 4294967296.0 AS DECIMAL(5,2)) AS PctIPv4Space
FROM Ref.MergedIpRange;

PRINT '';
PRINT '-- 9b. Country source breakdown --';
SELECT
    CountrySource,
    COUNT(*) AS Ranges,
    SUM(CAST(EndInt - StartInt + 1 AS BIGINT)) AS IPs,
    CAST(100.0 * COUNT(*) / @total AS DECIMAL(5,2)) AS PctRanges
FROM Ref.MergedIpRange
GROUP BY CountrySource
ORDER BY Ranges DESC;

PRINT '';
PRINT '-- 9c. Field coverage --';
SELECT
    SUM(CASE WHEN CountryCode IS NOT NULL THEN 1 ELSE 0 END) AS HasCountry,
    SUM(CASE WHEN Region IS NOT NULL THEN 1 ELSE 0 END) AS HasRegion,
    SUM(CASE WHEN City IS NOT NULL THEN 1 ELSE 0 END) AS HasCity,
    SUM(CASE WHEN PostalCode IS NOT NULL THEN 1 ELSE 0 END) AS HasZip,
    SUM(CASE WHEN Latitude IS NOT NULL THEN 1 ELSE 0 END) AS HasLatLon,
    SUM(CASE WHEN Asn IS NOT NULL THEN 1 ELSE 0 END) AS HasAsn,
    SUM(CASE WHEN AsnClass <> 'Unknown' THEN 1 ELSE 0 END) AS HasAsnClass,
    SUM(CASE WHEN Timezone IS NOT NULL THEN 1 ELSE 0 END) AS HasTimezone,
    SUM(CASE WHEN RirRegistry IS NOT NULL THEN 1 ELSE 0 END) AS HasRir
FROM Ref.MergedIpRange;

PRINT '';
PRINT '-- 9d. ASN class distribution (by IP count) --';
SELECT
    AsnClass,
    COUNT(*) AS Ranges,
    SUM(CAST(EndInt - StartInt + 1 AS BIGINT)) AS IPs,
    FORMAT(SUM(CAST(EndInt - StartInt + 1 AS BIGINT)), 'N0') AS IPs_Fmt
FROM Ref.MergedIpRange
GROUP BY AsnClass
ORDER BY IPs DESC;

PRINT '';
PRINT '-- 9e. Top 10 countries by IP count --';
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
PRINT '-- 9f. Confidence distribution --';
SELECT
    CountrySources AS SourcesAgreeing,
    COUNT(*) AS Ranges,
    CAST(100.0 * COUNT(*) / @total AS DECIMAL(5,2)) AS Pct
FROM Ref.MergedIpRange
GROUP BY CountrySources
ORDER BY CountrySources DESC;

PRINT '';

-- cleanup temp tables
IF OBJECT_ID('tempdb..#MM') IS NOT NULL DROP TABLE #MM;
IF OBJECT_ID('tempdb..#MMA') IS NOT NULL DROP TABLE #MMA;

PRINT '================================================';
PRINT '  COMPLETE';
PRINT '  Ref.MergedIpRange: ' + FORMAT(@total, 'N0') + ' ranges';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '================================================';
