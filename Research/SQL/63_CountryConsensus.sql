/*  63_CountryConsensus.sql
    Compare country codes across all 4 sources:
      - IPAPI.IP (paid ground truth, 344M individual IPs)
      - Geo.CityBlock + CityLocation (MaxMind GeoLite2, CIDR ranges)
      - Ref.DbipCityLite (DB-IP free, CIDR-like ranges)
      - Ref.RirDelegation (authoritative RIR allocations)

    Strategy: TABLESAMPLE 1M random IPs from IPAPI.IP, compute IpInt,
    then range-join each source and compare country codes.
    Uses GO batch separators so temp-table creation is isolated.

    Run time: ~3-5 minutes (range joins on 1M sample)
*/
SET NOCOUNT ON;

PRINT '================================================';
PRINT '  Country Consensus: 4-Source Comparison';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '================================================';
PRINT '';

-- Step 1: Sample 1M IPs from IPAPI, compute integer
PRINT 'Step 1: Sampling 1M IPs from IPAPI.IP...';

IF OBJECT_ID('tempdb..#Sample') IS NOT NULL DROP TABLE #Sample;

SELECT TOP 100000
    IP,
    CountryCode AS IPAPI_CC,
    RegionName  AS IPAPI_Region,
    City        AS IPAPI_City,
    CAST(PARSENAME(IP, 4) AS BIGINT) * 16777216 +
    CAST(PARSENAME(IP, 3) AS BIGINT) * 65536 +
    CAST(PARSENAME(IP, 2) AS BIGINT) * 256 +
    CAST(PARSENAME(IP, 1) AS BIGINT) AS IpInt,
    -- Extract ASN number from 'AS##### OrgName' format
    CASE WHEN [As] LIKE 'AS%'
         THEN TRY_CAST(SUBSTRING([As], 3, CHARINDEX(' ', [As] + ' ') - 3) AS INT)
         ELSE NULL END AS IPAPI_Asn
INTO #Sample
FROM IPAPI.IP TABLESAMPLE (0.1 PERCENT)
WHERE CountryCode IS NOT NULL AND CountryCode <> ''
  AND Status = 'success';

CREATE CLUSTERED INDEX CIX_Sample ON #Sample (IpInt);

DECLARE @sampleCount INT = (SELECT COUNT(*) FROM #Sample);
PRINT 'Sampled ' + FORMAT(@sampleCount, 'N0') + ' IPs';
PRINT '';
GO

-- Step 2: Join MaxMind GeoLite2 (CityBlock + CityLocation)
PRINT 'Step 2: Joining MaxMind CityBlock...';

-- Pre-compute MaxMind ranges as integers for efficient joining
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
    cl.CountryIsoCode AS MM_CC,
    cl.Subdivision1Name AS MM_Region,
    cl.CityName AS MM_City,
    cl.ContinentCode AS MM_Continent
INTO #MM
FROM Geo.CityBlock cb
JOIN Geo.CityLocation cl ON cb.GeonameId = cl.GeonameId AND cl.LocaleCode = 'en'
WHERE cb.NetworkCidr NOT LIKE '%:%'
  AND cl.CountryIsoCode IS NOT NULL;

CREATE CLUSTERED INDEX CIX_MM ON #MM (StartInt);

DECLARE @mmCount INT = (SELECT COUNT(*) FROM #MM);
PRINT 'MaxMind ranges: ' + FORMAT(@mmCount, 'N0');
PRINT '';
GO

-- Step 3: Range-join all sources to sample
PRINT 'Step 3: Range-joining all sources...';

IF OBJECT_ID('tempdb..#Comparison') IS NOT NULL DROP TABLE #Comparison;

CREATE TABLE #Comparison (
    IP             VARCHAR(15),
    IpInt          BIGINT,
    IPAPI_CC       VARCHAR(50),
    IPAPI_Region   NVARCHAR(200),
    IPAPI_City     NVARCHAR(200),
    IPAPI_Asn      INT,
    MM_CC          VARCHAR(10),
    MM_Region      NVARCHAR(200),
    MM_City        NVARCHAR(200),
    MM_Continent   VARCHAR(10),
    DBIP_CC        VARCHAR(10),
    DBIP_Region    NVARCHAR(200),
    DBIP_City      NVARCHAR(200),
    DBIP_Continent VARCHAR(10),
    RIR_CC         VARCHAR(10),
    RIR_Registry   VARCHAR(20)
);

INSERT INTO #Comparison
SELECT
    s.IP,
    s.IpInt,
    s.IPAPI_CC,
    s.IPAPI_Region,
    s.IPAPI_City,
    s.IPAPI_Asn,
    mm.MM_CC,
    mm.MM_Region,
    mm.MM_City,
    mm.MM_Continent,
    dbip.DBIP_CC,
    dbip.DBIP_Region,
    dbip.DBIP_City,
    dbip.DBIP_Continent,
    rir.RIR_CC,
    rir.RIR_Registry
FROM #Sample s
OUTER APPLY (
    SELECT TOP 1 MM_CC, MM_Region, MM_City, MM_Continent
    FROM #MM
    WHERE s.IpInt BETWEEN StartInt AND EndInt
    ORDER BY (EndInt - StartInt)
) mm
OUTER APPLY (
    SELECT TOP 1 CountryCode AS DBIP_CC, Region AS DBIP_Region, City AS DBIP_City, Continent AS DBIP_Continent
    FROM Ref.DbipCityLite
    WHERE s.IpInt BETWEEN StartInt AND EndInt
    ORDER BY (EndInt - StartInt)
) dbip
OUTER APPLY (
    SELECT TOP 1 CountryCode AS RIR_CC, Registry AS RIR_Registry
    FROM Ref.RirDelegation
    WHERE s.IpInt BETWEEN StartInt AND EndInt
    ORDER BY (EndInt - StartInt)
) rir;

DECLARE @compCount INT = (SELECT COUNT(*) FROM #Comparison);
PRINT 'Comparison rows: ' + FORMAT(@compCount, 'N0');
PRINT '';
GO

-- Step 4: Country match analysis
PRINT '================================================';
PRINT '  COUNTRY CODE ACCURACY (vs IPAPI as baseline)';
PRINT '================================================';
PRINT '';

DECLARE @total INT = (SELECT COUNT(*) FROM #Comparison);

PRINT '-- 4a. Overall Match Rates --';
SELECT
    @total AS SampleSize,
    SUM(CASE WHEN MM_CC IS NOT NULL THEN 1 ELSE 0 END) AS MM_Coverage,
    SUM(CASE WHEN DBIP_CC IS NOT NULL THEN 1 ELSE 0 END) AS DBIP_Coverage,
    SUM(CASE WHEN RIR_CC IS NOT NULL THEN 1 ELSE 0 END) AS RIR_Coverage,
    -- Match rates (where source has data)
    SUM(CASE WHEN MM_CC = IPAPI_CC THEN 1 ELSE 0 END) AS MM_Match,
    SUM(CASE WHEN DBIP_CC = IPAPI_CC THEN 1 ELSE 0 END) AS DBIP_Match,
    SUM(CASE WHEN RIR_CC = IPAPI_CC THEN 1 ELSE 0 END) AS RIR_Match
FROM #Comparison;

PRINT '';
PRINT '-- 4b. Match Percentages --';
SELECT
    'MaxMind vs IPAPI' AS [Comparison],
    SUM(CASE WHEN MM_CC = IPAPI_CC THEN 1 ELSE 0 END) AS Matches,
    SUM(CASE WHEN MM_CC IS NOT NULL AND MM_CC <> IPAPI_CC THEN 1 ELSE 0 END) AS Mismatches,
    SUM(CASE WHEN MM_CC IS NULL THEN 1 ELSE 0 END) AS NoData,
    CAST(100.0 * SUM(CASE WHEN MM_CC = IPAPI_CC THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN MM_CC IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)) AS MatchPct
FROM #Comparison
UNION ALL
SELECT
    'DB-IP vs IPAPI',
    SUM(CASE WHEN DBIP_CC = IPAPI_CC THEN 1 ELSE 0 END),
    SUM(CASE WHEN DBIP_CC IS NOT NULL AND DBIP_CC <> IPAPI_CC THEN 1 ELSE 0 END),
    SUM(CASE WHEN DBIP_CC IS NULL THEN 1 ELSE 0 END),
    CAST(100.0 * SUM(CASE WHEN DBIP_CC = IPAPI_CC THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN DBIP_CC IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2))
FROM #Comparison
UNION ALL
SELECT
    'RIR vs IPAPI',
    SUM(CASE WHEN RIR_CC = IPAPI_CC THEN 1 ELSE 0 END),
    SUM(CASE WHEN RIR_CC IS NOT NULL AND RIR_CC <> IPAPI_CC THEN 1 ELSE 0 END),
    SUM(CASE WHEN RIR_CC IS NULL THEN 1 ELSE 0 END),
    CAST(100.0 * SUM(CASE WHEN RIR_CC = IPAPI_CC THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN RIR_CC IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2))
FROM #Comparison
UNION ALL
SELECT
    'MaxMind vs DB-IP',
    SUM(CASE WHEN MM_CC = DBIP_CC THEN 1 ELSE 0 END),
    SUM(CASE WHEN MM_CC IS NOT NULL AND DBIP_CC IS NOT NULL AND MM_CC <> DBIP_CC THEN 1 ELSE 0 END),
    SUM(CASE WHEN MM_CC IS NULL OR DBIP_CC IS NULL THEN 1 ELSE 0 END),
    CAST(100.0 * SUM(CASE WHEN MM_CC = DBIP_CC THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN MM_CC IS NOT NULL AND DBIP_CC IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2))
FROM #Comparison;
GO

PRINT '';

-- Step 5: Consensus voting (where sources disagree)
PRINT '-- 5. Country Consensus Voting --';
PRINT '   Cases where at least one source disagrees with IPAPI';
PRINT '';

-- Where MaxMind disagrees with IPAPI but DB-IP agrees with one of them
PRINT '-- 5a. MaxMind disagrees with IPAPI: who does DB-IP side with? --';
SELECT
    CASE
        WHEN DBIP_CC = IPAPI_CC THEN 'DB-IP agrees with IPAPI (MM wrong)'
        WHEN DBIP_CC = MM_CC    THEN 'DB-IP agrees with MaxMind (IPAPI wrong?)'
        WHEN DBIP_CC IS NULL    THEN 'DB-IP has no data'
        ELSE 'DB-IP disagrees with both (3-way split)'
    END AS Verdict,
    COUNT(*) AS Cnt
FROM #Comparison
WHERE MM_CC IS NOT NULL AND MM_CC <> IPAPI_CC
GROUP BY
    CASE
        WHEN DBIP_CC = IPAPI_CC THEN 'DB-IP agrees with IPAPI (MM wrong)'
        WHEN DBIP_CC = MM_CC    THEN 'DB-IP agrees with MaxMind (IPAPI wrong?)'
        WHEN DBIP_CC IS NULL    THEN 'DB-IP has no data'
        ELSE 'DB-IP disagrees with both (3-way split)'
    END
ORDER BY Cnt DESC;

PRINT '';

-- Where RIR disagrees with IPAPI
PRINT '-- 5b. RIR disagrees with IPAPI: who does MaxMind side with? --';
SELECT
    CASE
        WHEN MM_CC = IPAPI_CC THEN 'MaxMind agrees with IPAPI (RIR allocation differs)'
        WHEN MM_CC = RIR_CC   THEN 'MaxMind agrees with RIR (IPAPI wrong?)'
        WHEN MM_CC IS NULL    THEN 'MaxMind has no data'
        ELSE 'MaxMind disagrees with both'
    END AS Verdict,
    COUNT(*) AS Cnt
FROM #Comparison
WHERE RIR_CC IS NOT NULL AND RIR_CC <> IPAPI_CC
GROUP BY
    CASE
        WHEN MM_CC = IPAPI_CC THEN 'MaxMind agrees with IPAPI (RIR allocation differs)'
        WHEN MM_CC = RIR_CC   THEN 'MaxMind agrees with RIR (IPAPI wrong?)'
        WHEN MM_CC IS NULL    THEN 'MaxMind has no data'
        ELSE 'MaxMind disagrees with both'
    END
ORDER BY Cnt DESC;
GO

PRINT '';

-- Step 6: Country-level breakdown of mismatches
PRINT '-- 6. Top 20 Country Mismatches (MaxMind vs IPAPI) --';
SELECT TOP 20
    IPAPI_CC,
    MM_CC,
    COUNT(*) AS Cnt
FROM #Comparison
WHERE MM_CC IS NOT NULL AND MM_CC <> IPAPI_CC
GROUP BY IPAPI_CC, MM_CC
ORDER BY Cnt DESC;

PRINT '';

PRINT '-- 7. Top 20 Country Mismatches (DB-IP vs IPAPI) --';
SELECT TOP 20
    IPAPI_CC,
    DBIP_CC,
    COUNT(*) AS Cnt
FROM #Comparison
WHERE DBIP_CC IS NOT NULL AND DBIP_CC <> IPAPI_CC
GROUP BY IPAPI_CC, DBIP_CC
ORDER BY Cnt DESC;

PRINT '';

-- Step 7: All-agree vs any-disagree summary
PRINT '-- 8. Agreement Summary --';
DECLARE @total INT = (SELECT COUNT(*) FROM #Comparison);
SELECT
    CASE
        WHEN MM_CC = IPAPI_CC AND DBIP_CC = IPAPI_CC AND RIR_CC = IPAPI_CC
            THEN 'All 4 agree'
        WHEN MM_CC = IPAPI_CC AND DBIP_CC = IPAPI_CC
            THEN 'MM+DBIP+IPAPI agree (RIR differs or NULL)'
        WHEN MM_CC = IPAPI_CC
            THEN 'MM+IPAPI agree (DBIP differs or NULL)'
        WHEN DBIP_CC = IPAPI_CC
            THEN 'DBIP+IPAPI agree (MM differs or NULL)'
        WHEN MM_CC IS NULL AND DBIP_CC IS NULL
            THEN 'Only IPAPI has data'
        ELSE 'Nobody agrees with IPAPI'
    END AS Agreement,
    COUNT(*) AS Cnt,
    CAST(100.0 * COUNT(*) / @total AS DECIMAL(5,2)) AS Pct
FROM #Comparison
GROUP BY
    CASE
        WHEN MM_CC = IPAPI_CC AND DBIP_CC = IPAPI_CC AND RIR_CC = IPAPI_CC
            THEN 'All 4 agree'
        WHEN MM_CC = IPAPI_CC AND DBIP_CC = IPAPI_CC
            THEN 'MM+DBIP+IPAPI agree (RIR differs or NULL)'
        WHEN MM_CC = IPAPI_CC
            THEN 'MM+IPAPI agree (DBIP differs or NULL)'
        WHEN DBIP_CC = IPAPI_CC
            THEN 'DBIP+IPAPI agree (MM differs or NULL)'
        WHEN MM_CC IS NULL AND DBIP_CC IS NULL
            THEN 'Only IPAPI has data'
        ELSE 'Nobody agrees with IPAPI'
    END
ORDER BY Cnt DESC;

PRINT '';
PRINT 'Country consensus analysis complete.';

-- Cleanup
DROP TABLE #Sample;
DROP TABLE #MM;
DROP TABLE #Comparison;
