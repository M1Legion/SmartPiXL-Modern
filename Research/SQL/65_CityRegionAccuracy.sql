/*  65_CityRegionAccuracy.sql
    City + Region accuracy comparison across MaxMind, DB-IP, and IPAPI.

    Uses the same 1M sample strategy as 63_CountryConsensus.
    Compares: exact city match, region match, and lat/lon proximity.

    Run time: ~5-8 minutes
*/
SET NOCOUNT ON;

PRINT '================================================';
PRINT '  City + Region Accuracy: 3-Source Comparison';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '================================================';
PRINT '';

-- Step 1: Sample 50K US + 50K non-US from IPAPI (stratified)
PRINT 'Step 1: Sampling 50K US + 50K non-US IPs (TABLESAMPLE)...';

IF OBJECT_ID('tempdb..#Sample') IS NOT NULL DROP TABLE #Sample;

-- US sample (primary market)
SELECT TOP 50000
    IP,
    CountryCode AS IPAPI_CC,
    RegionName  AS IPAPI_Region,
    City        AS IPAPI_City,
    Zip         AS IPAPI_Zip,
    TRY_CAST(Lat AS DECIMAL(9,4)) AS IPAPI_Lat,
    TRY_CAST(Lon AS DECIMAL(9,4)) AS IPAPI_Lon,
    CAST(PARSENAME(IP, 4) AS BIGINT) * 16777216 +
    CAST(PARSENAME(IP, 3) AS BIGINT) * 65536 +
    CAST(PARSENAME(IP, 2) AS BIGINT) * 256 +
    CAST(PARSENAME(IP, 1) AS BIGINT) AS IpInt,
    CAST('US' AS VARCHAR(6)) AS SampleGroup
INTO #Sample
FROM IPAPI.IP TABLESAMPLE (0.1 PERCENT)
WHERE CountryCode = 'US' AND Status = 'success'
  AND City IS NOT NULL AND City <> '';

INSERT INTO #Sample
SELECT TOP 50000
    IP,
    CountryCode,
    RegionName,
    City,
    Zip,
    TRY_CAST(Lat AS DECIMAL(9,4)),
    TRY_CAST(Lon AS DECIMAL(9,4)),
    CAST(PARSENAME(IP, 4) AS BIGINT) * 16777216 +
    CAST(PARSENAME(IP, 3) AS BIGINT) * 65536 +
    CAST(PARSENAME(IP, 2) AS BIGINT) * 256 +
    CAST(PARSENAME(IP, 1) AS BIGINT),
    'Non-US'
FROM IPAPI.IP TABLESAMPLE (0.1 PERCENT)
WHERE CountryCode <> 'US' AND CountryCode IS NOT NULL AND CountryCode <> ''
  AND Status = 'success'
  AND City IS NOT NULL AND City <> '';

CREATE CLUSTERED INDEX CIX_Sample ON #Sample (IpInt);

DECLARE @sampleCount INT = (SELECT COUNT(*) FROM #Sample);
PRINT 'Total sample: ' + FORMAT(@sampleCount, 'N0');
PRINT '';
GO

-- Step 2: Pre-compute MaxMind ranges
PRINT 'Step 2: Building MaxMind integer ranges...';

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
    cb.PostalCode AS MM_Zip,
    cb.Latitude AS MM_Lat,
    cb.Longitude AS MM_Lon
INTO #MM
FROM Geo.CityBlock cb
JOIN Geo.CityLocation cl ON cb.GeonameId = cl.GeonameId AND cl.LocaleCode = 'en'
WHERE cb.NetworkCidr NOT LIKE '%:%';

CREATE CLUSTERED INDEX CIX_MM ON #MM (StartInt);

DECLARE @mmCount INT = (SELECT COUNT(*) FROM #MM);
PRINT 'MaxMind ranges: ' + FORMAT(@mmCount, 'N0');
PRINT '';
GO

-- Step 3: Range-join
PRINT 'Step 3: Range-joining MaxMind + DB-IP to sample...';

IF OBJECT_ID('tempdb..#Comp') IS NOT NULL DROP TABLE #Comp;

CREATE TABLE #Comp (
    IP            VARCHAR(15),
    IpInt         BIGINT,
    SampleGroup   VARCHAR(6),
    IPAPI_CC      VARCHAR(50),
    IPAPI_Region  NVARCHAR(200),
    IPAPI_City    NVARCHAR(200),
    IPAPI_Zip     VARCHAR(20),
    IPAPI_Lat     DECIMAL(9,4),
    IPAPI_Lon     DECIMAL(9,4),
    MM_CC         VARCHAR(10),
    MM_Region     NVARCHAR(200),
    MM_City       NVARCHAR(200),
    MM_Zip        VARCHAR(20),
    MM_Lat        DECIMAL(9,4),
    MM_Lon        DECIMAL(9,4),
    DBIP_CC       VARCHAR(10),
    DBIP_Region   NVARCHAR(200),
    DBIP_City     NVARCHAR(200),
    DBIP_Lat      DECIMAL(9,4),
    DBIP_Lon      DECIMAL(9,4)
);

INSERT INTO #Comp
SELECT
    s.IP, s.IpInt, s.SampleGroup,
    s.IPAPI_CC, s.IPAPI_Region, s.IPAPI_City, s.IPAPI_Zip,
    s.IPAPI_Lat, s.IPAPI_Lon,
    mm.MM_CC, mm.MM_Region, mm.MM_City, mm.MM_Zip, mm.MM_Lat, mm.MM_Lon,
    dbip.DBIP_CC, dbip.DBIP_Region,
    dbip.DBIP_City, dbip.DBIP_Lat, dbip.DBIP_Lon
FROM #Sample s
OUTER APPLY (
    SELECT TOP 1 MM_CC, MM_Region, MM_City, MM_Zip, MM_Lat, MM_Lon
    FROM #MM
    WHERE s.IpInt BETWEEN StartInt AND EndInt
    ORDER BY (EndInt - StartInt)
) mm
OUTER APPLY (
    SELECT TOP 1 CountryCode AS DBIP_CC, Region AS DBIP_Region, City AS DBIP_City,
           CAST(Latitude AS DECIMAL(9,4)) AS DBIP_Lat,
           CAST(Longitude AS DECIMAL(9,4)) AS DBIP_Lon
    FROM Ref.DbipCityLite
    WHERE s.IpInt BETWEEN StartInt AND EndInt
    ORDER BY (EndInt - StartInt)
) dbip;

DECLARE @compTotal INT = (SELECT COUNT(*) FROM #Comp);
PRINT 'Comparison rows: ' + FORMAT(@compTotal, 'N0');
PRINT '';
GO

-- Step 4: City match rates
PRINT '================================================';
PRINT '  CITY ACCURACY';
PRINT '================================================';
PRINT '';

PRINT '-- 4a. Exact City Match (case-insensitive) --';
SELECT
    SampleGroup,
    COUNT(*) AS Total,
    -- MaxMind city
    SUM(CASE WHEN UPPER(MM_City) = UPPER(IPAPI_City) THEN 1 ELSE 0 END) AS MM_CityMatch,
    CAST(100.0 * SUM(CASE WHEN UPPER(MM_City) = UPPER(IPAPI_City) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN MM_City IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)) AS MM_CityPct,
    -- DB-IP city
    SUM(CASE WHEN UPPER(DBIP_City) = UPPER(IPAPI_City) THEN 1 ELSE 0 END) AS DBIP_CityMatch,
    CAST(100.0 * SUM(CASE WHEN UPPER(DBIP_City) = UPPER(IPAPI_City) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN DBIP_City IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)) AS DBIP_CityPct
FROM #Comp
GROUP BY SampleGroup
UNION ALL
SELECT
    'Overall',
    COUNT(*),
    SUM(CASE WHEN UPPER(MM_City) = UPPER(IPAPI_City) THEN 1 ELSE 0 END),
    CAST(100.0 * SUM(CASE WHEN UPPER(MM_City) = UPPER(IPAPI_City) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN MM_City IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)),
    SUM(CASE WHEN UPPER(DBIP_City) = UPPER(IPAPI_City) THEN 1 ELSE 0 END),
    CAST(100.0 * SUM(CASE WHEN UPPER(DBIP_City) = UPPER(IPAPI_City) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN DBIP_City IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2))
FROM #Comp;

PRINT '';

-- Step 5: Region match rates
PRINT '-- 5. Region Match --';
SELECT
    SampleGroup,
    COUNT(*) AS Total,
    SUM(CASE WHEN UPPER(MM_Region) = UPPER(IPAPI_Region) THEN 1 ELSE 0 END) AS MM_RegionMatch,
    CAST(100.0 * SUM(CASE WHEN UPPER(MM_Region) = UPPER(IPAPI_Region) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN MM_Region IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)) AS MM_RegionPct,
    SUM(CASE WHEN UPPER(DBIP_Region) = UPPER(IPAPI_Region) THEN 1 ELSE 0 END) AS DBIP_RegionMatch,
    CAST(100.0 * SUM(CASE WHEN UPPER(DBIP_Region) = UPPER(IPAPI_Region) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN DBIP_Region IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)) AS DBIP_RegionPct
FROM #Comp
GROUP BY SampleGroup
UNION ALL
SELECT
    'Overall',
    COUNT(*),
    SUM(CASE WHEN UPPER(MM_Region) = UPPER(IPAPI_Region) THEN 1 ELSE 0 END),
    CAST(100.0 * SUM(CASE WHEN UPPER(MM_Region) = UPPER(IPAPI_Region) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN MM_Region IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)),
    SUM(CASE WHEN UPPER(DBIP_Region) = UPPER(IPAPI_Region) THEN 1 ELSE 0 END),
    CAST(100.0 * SUM(CASE WHEN UPPER(DBIP_Region) = UPPER(IPAPI_Region) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN DBIP_Region IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2))
FROM #Comp;

PRINT '';

-- Step 6: Zip code match (MaxMind only -- DB-IP doesn't have zip)
PRINT '-- 6. Zip Code Match (MaxMind only, US only) --';
SELECT
    SUM(CASE WHEN MM_Zip = IPAPI_Zip THEN 1 ELSE 0 END) AS ExactZipMatch,
    SUM(CASE WHEN MM_Zip IS NOT NULL AND IPAPI_Zip IS NOT NULL THEN 1 ELSE 0 END) AS BothHaveZip,
    CAST(100.0 * SUM(CASE WHEN MM_Zip = IPAPI_Zip THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN MM_Zip IS NOT NULL AND IPAPI_Zip IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)) AS ZipMatchPct,
    -- 3-digit zip prefix match
    SUM(CASE WHEN LEFT(MM_Zip, 3) = LEFT(IPAPI_Zip, 3) THEN 1 ELSE 0 END) AS Zip3Match,
    CAST(100.0 * SUM(CASE WHEN LEFT(MM_Zip, 3) = LEFT(IPAPI_Zip, 3) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN MM_Zip IS NOT NULL AND IPAPI_Zip IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)) AS Zip3MatchPct
FROM #Comp
WHERE SampleGroup = 'US'
  AND IPAPI_Zip IS NOT NULL AND IPAPI_Zip <> '';

PRINT '';

-- Step 7: Lat/Lon proximity analysis
-- Haversine approximation using equirectangular projection (good enough for small distances)
PRINT '-- 7. Lat/Lon Proximity (km) --';
PRINT '   Using equirectangular approximation';

;WITH Distances AS (
    SELECT
        SampleGroup,
        -- MaxMind distance from IPAPI
        CASE WHEN MM_Lat IS NOT NULL AND IPAPI_Lat IS NOT NULL
             THEN 111.0 * SQRT(
                POWER(CAST(MM_Lat AS FLOAT) - CAST(IPAPI_Lat AS FLOAT), 2) +
                POWER((CAST(MM_Lon AS FLOAT) - CAST(IPAPI_Lon AS FLOAT)) *
                      COS(RADIANS((CAST(MM_Lat AS FLOAT) + CAST(IPAPI_Lat AS FLOAT)) / 2.0)), 2)
             )
             ELSE NULL END AS MM_DistKm,
        -- DB-IP distance from IPAPI
        CASE WHEN DBIP_Lat IS NOT NULL AND IPAPI_Lat IS NOT NULL
             THEN 111.0 * SQRT(
                POWER(CAST(DBIP_Lat AS FLOAT) - CAST(IPAPI_Lat AS FLOAT), 2) +
                POWER((CAST(DBIP_Lon AS FLOAT) - CAST(IPAPI_Lon AS FLOAT)) *
                      COS(RADIANS((CAST(DBIP_Lat AS FLOAT) + CAST(IPAPI_Lat AS FLOAT)) / 2.0)), 2)
             )
             ELSE NULL END AS DBIP_DistKm
    FROM #Comp
)
SELECT
    SampleGroup,
    COUNT(*) AS Total,
    -- MaxMind distance buckets
    SUM(CASE WHEN MM_DistKm < 10 THEN 1 ELSE 0 END) AS MM_Under10km,
    SUM(CASE WHEN MM_DistKm < 25 THEN 1 ELSE 0 END) AS MM_Under25km,
    SUM(CASE WHEN MM_DistKm < 50 THEN 1 ELSE 0 END) AS MM_Under50km,
    SUM(CASE WHEN MM_DistKm < 100 THEN 1 ELSE 0 END) AS MM_Under100km,
    CAST(AVG(MM_DistKm) AS DECIMAL(8,1)) AS MM_AvgKm,
    -- DB-IP distance buckets
    SUM(CASE WHEN DBIP_DistKm < 10 THEN 1 ELSE 0 END) AS DBIP_Under10km,
    SUM(CASE WHEN DBIP_DistKm < 25 THEN 1 ELSE 0 END) AS DBIP_Under25km,
    SUM(CASE WHEN DBIP_DistKm < 50 THEN 1 ELSE 0 END) AS DBIP_Under50km,
    SUM(CASE WHEN DBIP_DistKm < 100 THEN 1 ELSE 0 END) AS DBIP_Under100km,
    CAST(AVG(DBIP_DistKm) AS DECIMAL(8,1)) AS DBIP_AvgKm
FROM Distances
GROUP BY SampleGroup
UNION ALL
SELECT
    'Overall',
    COUNT(*),
    SUM(CASE WHEN MM_DistKm < 10 THEN 1 ELSE 0 END),
    SUM(CASE WHEN MM_DistKm < 25 THEN 1 ELSE 0 END),
    SUM(CASE WHEN MM_DistKm < 50 THEN 1 ELSE 0 END),
    SUM(CASE WHEN MM_DistKm < 100 THEN 1 ELSE 0 END),
    CAST(AVG(MM_DistKm) AS DECIMAL(8,1)),
    SUM(CASE WHEN DBIP_DistKm < 10 THEN 1 ELSE 0 END),
    SUM(CASE WHEN DBIP_DistKm < 25 THEN 1 ELSE 0 END),
    SUM(CASE WHEN DBIP_DistKm < 50 THEN 1 ELSE 0 END),
    SUM(CASE WHEN DBIP_DistKm < 100 THEN 1 ELSE 0 END),
    CAST(AVG(DBIP_DistKm) AS DECIMAL(8,1))
FROM Distances;

PRINT '';

-- Step 8: City consensus (where sources disagree)
PRINT '-- 8. City Disagreement Analysis --';
PRINT '   Where MaxMind and DB-IP give different cities, which is closer to IPAPI?';

;WITH CityDisagree AS (
    SELECT
        IPAPI_City, MM_City, DBIP_City,
        CASE WHEN UPPER(MM_City) = UPPER(IPAPI_City) THEN 1 ELSE 0 END AS MM_Right,
        CASE WHEN UPPER(DBIP_City) = UPPER(IPAPI_City) THEN 1 ELSE 0 END AS DBIP_Right
    FROM #Comp
    WHERE MM_City IS NOT NULL AND DBIP_City IS NOT NULL
      AND UPPER(MM_City) <> UPPER(DBIP_City)  -- they disagree
)
SELECT
    'MaxMind right, DB-IP wrong' AS Outcome,
    COUNT(*) AS Cnt
FROM CityDisagree WHERE MM_Right = 1 AND DBIP_Right = 0
UNION ALL
SELECT 'DB-IP right, MaxMind wrong',
    COUNT(*)
FROM CityDisagree WHERE DBIP_Right = 1 AND MM_Right = 0
UNION ALL
SELECT 'Both wrong (neither matches IPAPI)',
    COUNT(*)
FROM CityDisagree WHERE MM_Right = 0 AND DBIP_Right = 0;

PRINT '';

-- Step 9: Top city mismatches deep-dive (US only)
PRINT '-- 9. Top 20 US City Mismatches (MM vs IPAPI where DB-IP agrees with IPAPI) --';
PRINT '   These are cases where MaxMind is likely wrong';
SELECT TOP 20
    IPAPI_City, IPAPI_Region, MM_City, MM_Region, DBIP_City,
    COUNT(*) AS Cnt
FROM #Comp
WHERE SampleGroup = 'US'
  AND UPPER(MM_City) <> UPPER(IPAPI_City)
  AND UPPER(DBIP_City) = UPPER(IPAPI_City)
  AND MM_City IS NOT NULL
GROUP BY IPAPI_City, IPAPI_Region, MM_City, MM_Region, DBIP_City
ORDER BY Cnt DESC;

PRINT '';
PRINT 'City/Region accuracy analysis complete.';

DROP TABLE #Sample;
DROP TABLE #MM;
DROP TABLE #Comp;
