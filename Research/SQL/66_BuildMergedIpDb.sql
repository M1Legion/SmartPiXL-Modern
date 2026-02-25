/*  66_BuildMergedIpDb.sql
    Build Ref.MergedIpRange -- a unified free IP intelligence table
    combining the best field from each source:

    Backbone: DB-IP City Lite (3.7M ranges, finest granularity)
    Country:  Consensus vote (DB-IP + MaxMind + RIR), RIR tiebreaker
    Region:   MaxMind preferred (95.8% US accuracy), DB-IP fallback
    City:     MaxMind preferred (73.9% US accuracy), DB-IP fallback
    Lat/Lon:  DB-IP preferred (avg 290km vs 522km proximity)
    Zip:      MaxMind only
    ASN:      MaxMind ASN table (cleanest integers)
    ASN Org:  bgp.tools preferred (freshest), MaxMind fallback
    ASN Class: bgp.tools only (Eyeball/Content/Transit/Carrier)
    Timezone: MaxMind only

    Run time: ~15-25 minutes (3.7M x 3 range lookups)
*/
SET NOCOUNT ON;

PRINT '================================================';
PRINT '  Building Merged IP Intelligence Table';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '================================================';
PRINT '';

-- ============================================================
-- Step 1: Pre-compute MaxMind integer ranges + location data
-- ============================================================
PRINT 'Step 1: Building MaxMind CityBlock integer ranges...';

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
PRINT '  MaxMind CityBlock ranges: ' + FORMAT(@mmCount, 'N0');
GO

-- ============================================================
-- Step 2: Pre-compute MaxMind ASN integer ranges
-- ============================================================
PRINT 'Step 2: Building MaxMind ASN integer ranges...';

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
PRINT '  MaxMind ASN ranges: ' + FORMAT(@mmaCount, 'N0');
PRINT '';
GO

-- ============================================================
-- Step 3: Create the merged table
-- ============================================================
PRINT 'Step 3: Creating Ref.MergedIpRange...';

IF OBJECT_ID('Ref.MergedIpRange') IS NOT NULL DROP TABLE Ref.MergedIpRange;

CREATE TABLE Ref.MergedIpRange (
    RangeId         INT IDENTITY(1,1) NOT NULL,
    StartInt        BIGINT NOT NULL,
    EndInt          BIGINT NOT NULL,
    StartIp         VARCHAR(15)  NOT NULL,
    EndIp           VARCHAR(15)  NOT NULL,
    -- Country (consensus)
    CountryCode     CHAR(2)      NULL,
    CountrySource   VARCHAR(30)  NULL,   -- 'Consensus', 'RIR-tiebreak', 'MaxMind-only', etc.
    CountrySources  TINYINT      NULL,   -- how many sources agreed
    Continent       CHAR(2)      NULL,
    -- Location (best-of-breed)
    Region          NVARCHAR(100) NULL,
    City            NVARCHAR(150) NULL,
    PostalCode      VARCHAR(20)  NULL,
    Latitude        DECIMAL(9,4) NULL,
    Longitude       DECIMAL(9,4) NULL,
    AccuracyRadius  INT          NULL,
    Timezone        VARCHAR(60)  NULL,
    -- ASN (MaxMind number + bgp.tools enrichment)
    Asn             INT          NULL,
    AsnOrg          VARCHAR(300) NULL,
    AsnClass        VARCHAR(20)  NULL,   -- Eyeball, Content, Transit, Carrier, Unknown
    AsnCountryCode  CHAR(2)      NULL,
    -- RIR metadata
    RirRegistry     VARCHAR(10)  NULL,
    RirDateAllocated CHAR(8)     NULL,
    -- Provenance
    BuiltAt         DATETIME2    NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_MergedIpRange PRIMARY KEY CLUSTERED (StartInt)
);

PRINT '  Table created.';
PRINT '';
GO

-- ============================================================
-- Step 4: Populate the merged table in batches
-- For each DB-IP range, lookup MaxMind City, MaxMind ASN,
-- RIR delegation, and bgp.tools classification.
-- Uses the range midpoint for lookups.
-- Batches of 200K avoid tempdb/memory pressure.
-- ============================================================
PRINT 'Step 4: Populating merged table in 200K batches...';

DECLARE @batchSize INT = 200000;
DECLARE @offset    INT = 0;
DECLARE @rowsDone  INT = 0;
DECLARE @dbipTotal INT = (SELECT COUNT(*) FROM Ref.DbipCityLite);
PRINT '  DB-IP ranges to process: ' + FORMAT(@dbipTotal, 'N0');

WHILE @offset < @dbipTotal
BEGIN
    INSERT INTO Ref.MergedIpRange (
    StartInt, EndInt, StartIp, EndIp,
    CountryCode, CountrySource, CountrySources, Continent,
    Region, City, PostalCode, Latitude, Longitude, AccuracyRadius, Timezone,
    Asn, AsnOrg, AsnClass, AsnCountryCode,
    RirRegistry, RirDateAllocated
)
SELECT
    d.StartInt,
    d.EndInt,
    d.StartIp,
    d.EndIp,

    -- ── Country: consensus vote ──
    CASE
        -- All 3 agree
        WHEN d.CountryCode = mm.MM_CC AND d.CountryCode = rir.RIR_CC
            THEN d.CountryCode
        -- 2 of 3 agree (majority wins)
        WHEN d.CountryCode = mm.MM_CC
            THEN d.CountryCode
        WHEN d.CountryCode = rir.RIR_CC
            THEN d.CountryCode
        WHEN mm.MM_CC = rir.RIR_CC AND mm.MM_CC IS NOT NULL
            THEN mm.MM_CC
        -- Tiebreaker: all disagree, RIR is authoritative for allocation
        WHEN rir.RIR_CC IS NOT NULL
            THEN d.CountryCode  -- use DB-IP (more granular than RIR allocation)
        -- Only DB-IP + MaxMind
        WHEN mm.MM_CC IS NOT NULL
            THEN d.CountryCode  -- DB-IP has better coverage
        -- Fallback
        ELSE d.CountryCode
    END,

    -- Country source label
    CASE
        WHEN d.CountryCode = mm.MM_CC AND d.CountryCode = rir.RIR_CC
            THEN 'All-3-agree'
        WHEN d.CountryCode = mm.MM_CC
            THEN 'DBIP+MM-agree'
        WHEN d.CountryCode = rir.RIR_CC
            THEN 'DBIP+RIR-agree'
        WHEN mm.MM_CC = rir.RIR_CC AND mm.MM_CC IS NOT NULL
            THEN 'MM+RIR-agree'
        WHEN rir.RIR_CC IS NOT NULL AND mm.MM_CC IS NOT NULL
            THEN '3-way-split'
        WHEN mm.MM_CC IS NOT NULL
            THEN 'DBIP+MM-only'
        WHEN rir.RIR_CC IS NOT NULL
            THEN 'DBIP+RIR-only'
        ELSE 'DBIP-only'
    END,

    -- CountrySources: how many sources have data for this range
    CASE WHEN d.CountryCode IS NOT NULL THEN 1 ELSE 0 END
    + CASE WHEN mm.MM_CC IS NOT NULL THEN 1 ELSE 0 END
    + CASE WHEN rir.RIR_CC IS NOT NULL THEN 1 ELSE 0 END,

    -- Continent: DB-IP has it natively
    COALESCE(d.Continent, mm.MM_Continent),

    -- ── Region: MaxMind preferred (better string accuracy) ──
    COALESCE(mm.MM_Region, d.Region),

    -- ── City: MaxMind preferred (better name accuracy) ──
    COALESCE(mm.MM_City, d.City),

    -- ── Zip: MaxMind only ──
    mm.MM_Zip,

    -- ── Lat/Lon: DB-IP preferred (better proximity) ──
    COALESCE(d.Latitude, mm.MM_Lat),
    COALESCE(d.Longitude, mm.MM_Lon),

    -- AccuracyRadius: MaxMind only
    mm.MM_AccRadius,

    -- Timezone: MaxMind only
    mm.MM_Timezone,

    -- ── ASN: from MaxMind ASN table ──
    mma.MMA_Asn,

    -- ── ASN Org: bgp.tools preferred (fresher), MaxMind fallback ──
    COALESCE(bgp.Name, mma.MMA_AsnOrg),

    -- ── ASN Class: bgp.tools only ──
    COALESCE(bgp.Class, 'Unknown'),

    -- ── ASN Country: bgp.tools ──
    bgp.CountryCode,

    -- ── RIR ──
    rir.RIR_Registry,
    rir.RIR_DateAlloc

FROM (
    SELECT *
    FROM Ref.DbipCityLite
    ORDER BY StartInt
    OFFSET @offset ROWS FETCH NEXT @batchSize ROWS ONLY
) d
-- MaxMind city lookup on range midpoint
OUTER APPLY (
    SELECT TOP 1
        MM_CC, MM_Region, MM_City, MM_Zip, MM_Lat, MM_Lon,
        MM_AccRadius, MM_Timezone, MM_Continent
    FROM #MM
    WHERE (d.StartInt + d.EndInt) / 2 BETWEEN StartInt AND EndInt
    ORDER BY (EndInt - StartInt)
) mm
-- MaxMind ASN lookup on range midpoint
OUTER APPLY (
    SELECT TOP 1 MMA_Asn, MMA_AsnOrg
    FROM #MMA
    WHERE (d.StartInt + d.EndInt) / 2 BETWEEN StartInt AND EndInt
    ORDER BY (EndInt - StartInt)
) mma
-- RIR delegation lookup on range midpoint
OUTER APPLY (
    SELECT TOP 1
        CountryCode AS RIR_CC,
        Registry    AS RIR_Registry,
        DateAllocated AS RIR_DateAlloc
    FROM Ref.RirDelegation
    WHERE (d.StartInt + d.EndInt) / 2 BETWEEN StartInt AND EndInt
    ORDER BY (EndInt - StartInt)
) rir
-- bgp.tools: join by ASN number (not range)
LEFT JOIN Ref.BgpToolsAsn bgp ON mma.MMA_Asn = bgp.Asn;

    SET @rowsDone += @@ROWCOUNT;
    SET @offset += @batchSize;
    PRINT '  Batch done: ' + FORMAT(@rowsDone, 'N0') + ' / ' + FORMAT(@dbipTotal, 'N0') + '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
END

DECLARE @merged INT = (SELECT COUNT(*) FROM Ref.MergedIpRange);
PRINT '  Merged rows: ' + FORMAT(@merged, 'N0');
PRINT '';
GO

-- ============================================================
-- Step 5: Create useful indexes
-- ============================================================
PRINT 'Step 5: Creating indexes...';

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

PRINT '  Indexes created.';
PRINT '';
GO

-- ============================================================
-- Step 6: Summary statistics
-- ============================================================
PRINT '================================================';
PRINT '  MERGED TABLE STATISTICS';
PRINT '================================================';
PRINT '';

DECLARE @total INT = (SELECT COUNT(*) FROM Ref.MergedIpRange);

PRINT '-- 6a. Overall coverage --';
SELECT
    @total AS TotalRanges,
    SUM(CAST(EndInt - StartInt + 1 AS BIGINT)) AS TotalIPs,
    FORMAT(SUM(CAST(EndInt - StartInt + 1 AS BIGINT)), 'N0') AS TotalIPs_Fmt,
    CAST(100.0 * SUM(CAST(EndInt - StartInt + 1 AS BIGINT)) / 4294967296.0 AS DECIMAL(5,2)) AS PctIPv4Space
FROM Ref.MergedIpRange;

PRINT '';
PRINT '-- 6b. Country source breakdown --';
SELECT
    CountrySource,
    COUNT(*) AS Ranges,
    SUM(CAST(EndInt - StartInt + 1 AS BIGINT)) AS IPs,
    CAST(100.0 * COUNT(*) / @total AS DECIMAL(5,2)) AS PctRanges
FROM Ref.MergedIpRange
GROUP BY CountrySource
ORDER BY Ranges DESC;

PRINT '';
PRINT '-- 6c. Field coverage --';
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
PRINT '-- 6d. ASN class distribution (by IP count) --';
SELECT
    AsnClass,
    COUNT(*) AS Ranges,
    SUM(CAST(EndInt - StartInt + 1 AS BIGINT)) AS IPs,
    FORMAT(SUM(CAST(EndInt - StartInt + 1 AS BIGINT)), 'N0') AS IPs_Fmt
FROM Ref.MergedIpRange
GROUP BY AsnClass
ORDER BY IPs DESC;

PRINT '';
PRINT '-- 6e. Top 10 countries by IP count --';
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
PRINT '-- 6f. Confidence distribution --';
SELECT
    CountrySources AS SourcesAgreeing,
    COUNT(*) AS Ranges,
    CAST(100.0 * COUNT(*) / @total AS DECIMAL(5,2)) AS Pct
FROM Ref.MergedIpRange
GROUP BY CountrySources
ORDER BY CountrySources DESC;

PRINT '';

-- cleanup temp tables
DROP TABLE #MM;
DROP TABLE #MMA;

PRINT 'Merged IP intelligence table built successfully.';
PRINT 'Table: Ref.MergedIpRange  Rows: ' + FORMAT(@total, 'N0');
