/*  67_MergedVsIpapi.sql
    Compare the merged free IP DB (Ref.MergedIpRange) against
    IPAPI.IP (paid source, 344M individual IPs).

    Goal: Prove the free merged DB is competitive with paid data.
    Sample 100K from IPAPI, look up in merged table, compare everything.

    Run time: ~2-4 minutes
*/
SET NOCOUNT ON;

PRINT '================================================';
PRINT '  Merged Free DB vs IPAPI (Paid) Comparison';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '================================================';
PRINT '';

-- ============================================================
-- Step 1: Sample from IPAPI
-- ============================================================
PRINT 'Step 1: Sampling 100K IPs from IPAPI...';

IF OBJECT_ID('tempdb..#Sample') IS NOT NULL DROP TABLE #Sample;

SELECT TOP 100000
    IP,
    CountryCode AS IPAPI_CC,
    RegionName  AS IPAPI_Region,
    City        AS IPAPI_City,
    Zip         AS IPAPI_Zip,
    TRY_CAST(Lat AS DECIMAL(9,4)) AS IPAPI_Lat,
    TRY_CAST(Lon AS DECIMAL(9,4)) AS IPAPI_Lon,
    Timezone    AS IPAPI_Tz,
    ISP         AS IPAPI_ISP,
    [Org]       AS IPAPI_Org,
    CASE WHEN [As] LIKE 'AS%'
         THEN TRY_CAST(SUBSTRING([As], 3, CHARINDEX(' ', [As] + ' ') - 3) AS INT)
         ELSE NULL END AS IPAPI_Asn,
    CASE WHEN Mobile = 'True' THEN 1 ELSE 0 END AS IPAPI_Mobile,
    CASE WHEN Proxy = 'True' THEN 1 ELSE 0 END AS IPAPI_Proxy,
    CAST(PARSENAME(IP, 4) AS BIGINT) * 16777216 +
    CAST(PARSENAME(IP, 3) AS BIGINT) * 65536 +
    CAST(PARSENAME(IP, 2) AS BIGINT) * 256 +
    CAST(PARSENAME(IP, 1) AS BIGINT) AS IpInt,
    CASE WHEN CountryCode = 'US' THEN 'US' ELSE 'Non-US' END AS Segment
INTO #Sample
FROM IPAPI.IP TABLESAMPLE (0.1 PERCENT)
WHERE CountryCode IS NOT NULL AND CountryCode <> ''
  AND Status = 'success';

CREATE CLUSTERED INDEX CIX_Sample ON #Sample (IpInt);

DECLARE @sc INT = (SELECT COUNT(*) FROM #Sample);
PRINT '  Sampled: ' + FORMAT(@sc, 'N0');
PRINT '';
GO

-- ============================================================
-- Step 2: Join to merged table
-- ============================================================
PRINT 'Step 2: Looking up each IP in Ref.MergedIpRange...';

IF OBJECT_ID('tempdb..#Comp') IS NOT NULL DROP TABLE #Comp;

CREATE TABLE #Comp (
    IP              VARCHAR(15),
    IpInt           BIGINT,
    Segment         VARCHAR(6),
    -- IPAPI fields
    IPAPI_CC        VARCHAR(50),
    IPAPI_Region    NVARCHAR(200),
    IPAPI_City      NVARCHAR(200),
    IPAPI_Zip       VARCHAR(20),
    IPAPI_Lat       DECIMAL(9,4),
    IPAPI_Lon       DECIMAL(9,4),
    IPAPI_Tz        NVARCHAR(100),
    IPAPI_ISP       NVARCHAR(200),
    IPAPI_Org       NVARCHAR(200),
    IPAPI_Asn       INT,
    IPAPI_Mobile    BIT,
    IPAPI_Proxy     BIT,
    -- Merged fields
    M_CC            CHAR(2),
    M_CCSource      VARCHAR(30),
    M_CCSources     TINYINT,
    M_Region        NVARCHAR(100),
    M_City          NVARCHAR(150),
    M_Zip           VARCHAR(20),
    M_Lat           DECIMAL(9,4),
    M_Lon           DECIMAL(9,4),
    M_Tz            VARCHAR(60),
    M_Asn           INT,
    M_AsnOrg        VARCHAR(300),
    M_AsnClass      VARCHAR(20),
    M_RirReg        VARCHAR(10)
);

INSERT INTO #Comp
SELECT
    s.IP, s.IpInt, s.Segment,
    s.IPAPI_CC, s.IPAPI_Region, s.IPAPI_City, s.IPAPI_Zip,
    s.IPAPI_Lat, s.IPAPI_Lon, s.IPAPI_Tz, s.IPAPI_ISP, s.IPAPI_Org,
    s.IPAPI_Asn, s.IPAPI_Mobile, s.IPAPI_Proxy,
    m.CountryCode, m.CountrySource, m.CountrySources,
    m.Region, m.City, m.PostalCode,
    m.Latitude, m.Longitude, m.Timezone,
    m.Asn, m.AsnOrg, m.AsnClass, m.RirRegistry
FROM #Sample s
OUTER APPLY (
    SELECT TOP 1 CountryCode, CountrySource, CountrySources,
        Region, City, PostalCode, Latitude, Longitude, Timezone,
        Asn, AsnOrg, AsnClass, RirRegistry
    FROM Ref.MergedIpRange WITH (FORCESEEK)
    WHERE StartInt <= s.IpInt AND EndInt >= s.IpInt
    ORDER BY StartInt DESC
) m;

DECLARE @compCt INT = (SELECT COUNT(*) FROM #Comp);
DECLARE @hitCt INT = (SELECT COUNT(*) FROM #Comp WHERE M_CC IS NOT NULL);
PRINT '  Comparison rows: ' + FORMAT(@compCt, 'N0');
PRINT '  Merged DB hit rate: ' + FORMAT(@hitCt, 'N0') + ' / ' + FORMAT(@compCt, 'N0')
      + ' (' + CAST(CAST(100.0 * @hitCt / @compCt AS DECIMAL(5,2)) AS VARCHAR) + '%)';
PRINT '';
GO

-- ============================================================
-- Step 3: Country accuracy
-- ============================================================
PRINT '================================================';
PRINT '  COUNTRY ACCURACY: Merged vs IPAPI';
PRINT '================================================';
PRINT '';

DECLARE @total INT = (SELECT COUNT(*) FROM #Comp WHERE M_CC IS NOT NULL);

PRINT '-- 3a. Overall country match --';
SELECT
    @total AS WithMergedData,
    SUM(CASE WHEN M_CC = IPAPI_CC THEN 1 ELSE 0 END) AS CountryMatch,
    CAST(100.0 * SUM(CASE WHEN M_CC = IPAPI_CC THEN 1 ELSE 0 END) / @total AS DECIMAL(5,2)) AS MatchPct
FROM #Comp
WHERE M_CC IS NOT NULL;

PRINT '';
PRINT '-- 3b. Country match by confidence level --';
SELECT
    M_CCSources AS SourcesAgreed,
    M_CCSource AS HowDecided,
    COUNT(*) AS Cnt,
    SUM(CASE WHEN M_CC = IPAPI_CC THEN 1 ELSE 0 END) AS Matches,
    CAST(100.0 * SUM(CASE WHEN M_CC = IPAPI_CC THEN 1 ELSE 0 END) /
         NULLIF(COUNT(*), 0) AS DECIMAL(5,2)) AS MatchPct
FROM #Comp
WHERE M_CC IS NOT NULL
GROUP BY M_CCSources, M_CCSource
ORDER BY Cnt DESC;

PRINT '';
PRINT '-- 3c. Top 15 country mismatches --';
SELECT TOP 15
    IPAPI_CC, M_CC, M_CCSource, COUNT(*) AS Cnt
FROM #Comp
WHERE M_CC IS NOT NULL AND M_CC <> IPAPI_CC
GROUP BY IPAPI_CC, M_CC, M_CCSource
ORDER BY Cnt DESC;
GO

-- ============================================================
-- Step 4: City & Region accuracy
-- ============================================================
PRINT '';
PRINT '================================================';
PRINT '  CITY + REGION ACCURACY: Merged vs IPAPI';
PRINT '================================================';
PRINT '';

PRINT '-- 4a. City match (case-insensitive) --';
SELECT
    Segment,
    COUNT(*) AS Total,
    SUM(CASE WHEN UPPER(M_City) = UPPER(IPAPI_City) THEN 1 ELSE 0 END) AS CityMatch,
    CAST(100.0 * SUM(CASE WHEN UPPER(M_City) = UPPER(IPAPI_City) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN M_City IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)) AS CityMatchPct,
    SUM(CASE WHEN M_City IS NOT NULL THEN 1 ELSE 0 END) AS HasCity
FROM #Comp
GROUP BY Segment
UNION ALL
SELECT
    'Overall', COUNT(*),
    SUM(CASE WHEN UPPER(M_City) = UPPER(IPAPI_City) THEN 1 ELSE 0 END),
    CAST(100.0 * SUM(CASE WHEN UPPER(M_City) = UPPER(IPAPI_City) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN M_City IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)),
    SUM(CASE WHEN M_City IS NOT NULL THEN 1 ELSE 0 END)
FROM #Comp;

PRINT '';
PRINT '-- 4b. Region match --';
SELECT
    Segment,
    COUNT(*) AS Total,
    SUM(CASE WHEN UPPER(M_Region) = UPPER(IPAPI_Region) THEN 1 ELSE 0 END) AS RegionMatch,
    CAST(100.0 * SUM(CASE WHEN UPPER(M_Region) = UPPER(IPAPI_Region) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN M_Region IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)) AS RegionMatchPct
FROM #Comp
GROUP BY Segment
UNION ALL
SELECT
    'Overall', COUNT(*),
    SUM(CASE WHEN UPPER(M_Region) = UPPER(IPAPI_Region) THEN 1 ELSE 0 END),
    CAST(100.0 * SUM(CASE WHEN UPPER(M_Region) = UPPER(IPAPI_Region) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN M_Region IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2))
FROM #Comp;

PRINT '';
PRINT '-- 4c. Zip match (US only) --';
SELECT
    SUM(CASE WHEN M_Zip = IPAPI_Zip THEN 1 ELSE 0 END) AS ExactZip,
    SUM(CASE WHEN LEFT(M_Zip, 3) = LEFT(IPAPI_Zip, 3) THEN 1 ELSE 0 END) AS Zip3Match,
    SUM(CASE WHEN M_Zip IS NOT NULL AND IPAPI_Zip IS NOT NULL AND IPAPI_Zip <> '' THEN 1 ELSE 0 END) AS BothHaveZip,
    CAST(100.0 * SUM(CASE WHEN M_Zip = IPAPI_Zip THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN M_Zip IS NOT NULL AND IPAPI_Zip IS NOT NULL AND IPAPI_Zip <> '' THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)) AS ExactPct,
    CAST(100.0 * SUM(CASE WHEN LEFT(M_Zip, 3) = LEFT(IPAPI_Zip, 3) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN M_Zip IS NOT NULL AND IPAPI_Zip IS NOT NULL AND IPAPI_Zip <> '' THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)) AS Zip3Pct
FROM #Comp
WHERE Segment = 'US';
GO

-- ============================================================
-- Step 5: Lat/Lon proximity
-- ============================================================
PRINT '';
PRINT '================================================';
PRINT '  LAT/LON PROXIMITY: Merged vs IPAPI';
PRINT '================================================';
PRINT '';

;WITH Distances AS (
    SELECT
        Segment,
        CASE WHEN M_Lat IS NOT NULL AND IPAPI_Lat IS NOT NULL
             THEN 111.0 * SQRT(
                POWER(CAST(M_Lat AS FLOAT) - CAST(IPAPI_Lat AS FLOAT), 2) +
                POWER((CAST(M_Lon AS FLOAT) - CAST(IPAPI_Lon AS FLOAT)) *
                      COS(RADIANS((CAST(M_Lat AS FLOAT) + CAST(IPAPI_Lat AS FLOAT)) / 2.0)), 2)
             )
             ELSE NULL END AS DistKm
    FROM #Comp
)
SELECT
    Segment,
    COUNT(*) AS Total,
    SUM(CASE WHEN DistKm IS NOT NULL THEN 1 ELSE 0 END) AS HasBothLatLon,
    SUM(CASE WHEN DistKm < 10 THEN 1 ELSE 0 END) AS Under10km,
    SUM(CASE WHEN DistKm < 25 THEN 1 ELSE 0 END) AS Under25km,
    SUM(CASE WHEN DistKm < 50 THEN 1 ELSE 0 END) AS Under50km,
    SUM(CASE WHEN DistKm < 100 THEN 1 ELSE 0 END) AS Under100km,
    CAST(AVG(DistKm) AS DECIMAL(8,1)) AS AvgKm
FROM Distances
GROUP BY Segment
UNION ALL
SELECT
    'Overall', COUNT(*),
    SUM(CASE WHEN DistKm IS NOT NULL THEN 1 ELSE 0 END),
    SUM(CASE WHEN DistKm < 10 THEN 1 ELSE 0 END),
    SUM(CASE WHEN DistKm < 25 THEN 1 ELSE 0 END),
    SUM(CASE WHEN DistKm < 50 THEN 1 ELSE 0 END),
    SUM(CASE WHEN DistKm < 100 THEN 1 ELSE 0 END),
    CAST(AVG(DistKm) AS DECIMAL(8,1))
FROM Distances;
GO

-- ============================================================
-- Step 6: ASN accuracy
-- ============================================================
PRINT '';
PRINT '================================================';
PRINT '  ASN ACCURACY: Merged vs IPAPI';
PRINT '================================================';
PRINT '';

PRINT '-- 6a. ASN number match --';
SELECT
    SUM(CASE WHEN M_Asn IS NOT NULL THEN 1 ELSE 0 END) AS MergedHasAsn,
    SUM(CASE WHEN IPAPI_Asn IS NOT NULL THEN 1 ELSE 0 END) AS IpapiHasAsn,
    SUM(CASE WHEN M_Asn = IPAPI_Asn THEN 1 ELSE 0 END) AS AsnMatch,
    CAST(100.0 * SUM(CASE WHEN M_Asn = IPAPI_Asn THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN M_Asn IS NOT NULL AND IPAPI_Asn IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)) AS AsnMatchPct
FROM #Comp;

PRINT '';
PRINT '-- 6b. ASN class distribution for sample --';
SELECT
    M_AsnClass,
    COUNT(*) AS Cnt,
    CAST(100.0 * COUNT(*) / (SELECT COUNT(*) FROM #Comp) AS DECIMAL(5,2)) AS Pct
FROM #Comp
GROUP BY M_AsnClass
ORDER BY Cnt DESC;

PRINT '';
PRINT '-- 6c. Mobile IPs by ASN class --';
PRINT '   IPAPI marks IP as mobile; does bgp.tools ASN class agree?';
SELECT
    COALESCE(M_AsnClass, '(no data)') AS AsnClass,
    SUM(CASE WHEN IPAPI_Mobile = 1 THEN 1 ELSE 0 END) AS MobileCount,
    COUNT(*) AS Total,
    CAST(100.0 * SUM(CASE WHEN IPAPI_Mobile = 1 THEN 1 ELSE 0 END) /
         NULLIF(COUNT(*), 0) AS DECIMAL(5,2)) AS MobilePct
FROM #Comp
GROUP BY COALESCE(M_AsnClass, '(no data)')
ORDER BY MobileCount DESC;

PRINT '';
PRINT '-- 6d. Proxy IPs by ASN class --';
SELECT
    COALESCE(M_AsnClass, '(no data)') AS AsnClass,
    SUM(CASE WHEN IPAPI_Proxy = 1 THEN 1 ELSE 0 END) AS ProxyCount,
    COUNT(*) AS Total,
    CAST(100.0 * SUM(CASE WHEN IPAPI_Proxy = 1 THEN 1 ELSE 0 END) /
         NULLIF(COUNT(*), 0) AS DECIMAL(5,2)) AS ProxyPct
FROM #Comp
GROUP BY COALESCE(M_AsnClass, '(no data)')
ORDER BY ProxyCount DESC;
GO

-- ============================================================
-- Step 7: Timezone match
-- ============================================================
PRINT '';
PRINT '-- 7. Timezone match --';
SELECT
    SUM(CASE WHEN M_Tz IS NOT NULL THEN 1 ELSE 0 END) AS MergedHasTz,
    SUM(CASE WHEN M_Tz = IPAPI_Tz THEN 1 ELSE 0 END) AS TzMatch,
    CAST(100.0 * SUM(CASE WHEN M_Tz = IPAPI_Tz THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN M_Tz IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)) AS TzMatchPct
FROM #Comp;

PRINT '';

-- ============================================================
-- Step 8: Scorecard summary
-- ============================================================
PRINT '================================================';
PRINT '  FINAL SCORECARD: Free Merged DB vs IPAPI';
PRINT '================================================';
PRINT '';

DECLARE @t INT = (SELECT COUNT(*) FROM #Comp WHERE M_CC IS NOT NULL);

SELECT
    'Country match' AS Metric,
    CAST(100.0 * SUM(CASE WHEN M_CC = IPAPI_CC THEN 1 ELSE 0 END) / @t AS DECIMAL(5,2)) AS Score,
    'vs 100% (IPAPI is baseline)' AS Note
FROM #Comp WHERE M_CC IS NOT NULL
UNION ALL
SELECT
    'City match (US)',
    CAST(100.0 * SUM(CASE WHEN UPPER(M_City) = UPPER(IPAPI_City) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN M_City IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)),
    'MaxMind city names'
FROM #Comp WHERE Segment = 'US'
UNION ALL
SELECT
    'Region match (US)',
    CAST(100.0 * SUM(CASE WHEN UPPER(M_Region) = UPPER(IPAPI_Region) THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN M_Region IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)),
    'MaxMind region names'
FROM #Comp WHERE Segment = 'US'
UNION ALL
SELECT
    'ASN match',
    CAST(100.0 * SUM(CASE WHEN M_Asn = IPAPI_Asn THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN M_Asn IS NOT NULL AND IPAPI_Asn IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)),
    'MaxMind ASN numbers'
FROM #Comp
UNION ALL
SELECT
    'Timezone match',
    CAST(100.0 * SUM(CASE WHEN M_Tz = IPAPI_Tz THEN 1 ELSE 0 END) /
         NULLIF(SUM(CASE WHEN M_Tz IS NOT NULL THEN 1 ELSE 0 END), 0) AS DECIMAL(5,2)),
    'MaxMind timezone'
FROM #Comp;

PRINT '';
PRINT '-- What the free DB adds that IPAPI misses: --';
SELECT
    'ASN classification' AS Feature,
    'bgp.tools: Eyeball / Content / Transit / Carrier' AS Source,
    'IPAPI has no equivalent' AS Advantage
UNION ALL
SELECT
    'RIR allocation data',
    'ARIN/RIPE/APNIC/LACNIC/AFRINIC delegations',
    'Shows original IP assignment, date allocated'
UNION ALL
SELECT
    'Country confidence score',
    'Consensus of 3 independent sources',
    'IPAPI gives one answer with no confidence indicator'
UNION ALL
SELECT
    'Accuracy radius',
    'MaxMind GeoLite2',
    'IPAPI gives coordinates but no error estimate';

PRINT '';
PRINT 'Comparison complete.';

DROP TABLE #Sample;
DROP TABLE #Comp;
