-- ====================================================================
-- MaxMind (Geo.*) vs IPAPI.IP Accuracy Comparison — Full Report
-- Run: sqlcmd -S localhost\SQL2025 -d SmartPiXL -i MaxMind_vs_IPAPI_Accuracy.sql
-- ====================================================================
SET NOCOUNT ON;

-- Step 1: Sample 1,000 IPs from PiXL.IP that also exist in IPAPI.IP
SELECT TOP 1000 
    p.IPAddress,
    i.CountryCode  AS IPAPI_CC,
    i.RegionName   AS IPAPI_Region,
    i.City         AS IPAPI_City,
    i.Zip          AS IPAPI_Zip,
    CAST(i.Lat AS DECIMAL(9,4)) AS IPAPI_Lat,
    CAST(i.Lon AS DECIMAL(9,4)) AS IPAPI_Lon,
    i.Timezone     AS IPAPI_TZ,
    i.ISP          AS IPAPI_ISP,
    i.[As]         AS IPAPI_AS
INTO #Sample
FROM PiXL.IP p
JOIN IPAPI.IP i ON p.IPAddress = i.IP COLLATE Latin1_General_BIN2
WHERE i.Status = 'success' AND i.CountryCode IS NOT NULL AND i.City IS NOT NULL
ORDER BY NEWID();

PRINT 'Sample built: ' + CAST(@@ROWCOUNT AS VARCHAR) + ' IPs';

-- Step 2: Convert IPs to BIGINT for CIDR range matching
ALTER TABLE #Sample ADD IpInt BIGINT;

UPDATE #Sample SET IpInt = 
    CAST(PARSENAME(IPAddress, 4) AS BIGINT) * 16777216 +
    CAST(PARSENAME(IPAddress, 3) AS BIGINT) * 65536 +
    CAST(PARSENAME(IPAddress, 2) AS BIGINT) * 256 +
    CAST(PARSENAME(IPAddress, 1) AS BIGINT);

PRINT 'IP integers computed';

-- Step 3: Materialize Geo.CityBlock CIDR ranges as start/end integers (IPv4 only)
SELECT 
    cb.GeonameId,
    cb.PostalCode AS MM_Zip,
    cb.Latitude   AS MM_Lat,
    cb.Longitude  AS MM_Lon,
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 4) AS BIGINT) * 16777216 +
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 3) AS BIGINT) * 65536 +
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 2) AS BIGINT) * 256 +
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 1) AS BIGINT) AS StartIp,
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 4) AS BIGINT) * 16777216 +
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 3) AS BIGINT) * 65536 +
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 2) AS BIGINT) * 256 +
    CAST(PARSENAME(LEFT(cb.NetworkCidr, CHARINDEX('/', cb.NetworkCidr) - 1), 1) AS BIGINT)
    + POWER(CAST(2 AS BIGINT), 32 - CAST(RIGHT(cb.NetworkCidr, LEN(cb.NetworkCidr) - CHARINDEX('/', cb.NetworkCidr)) AS INT)) - 1 AS EndIp
INTO #CityRange
FROM Geo.CityBlock cb
WHERE cb.NetworkCidr NOT LIKE '%:%';

CREATE INDEX IX_CityRange ON #CityRange (StartIp, EndIp);
PRINT 'CityBlock ranges materialized: ' + CAST(@@ROWCOUNT AS VARCHAR) + ' rows';

-- Step 4: Materialize Geo.ASN CIDR ranges
SELECT 
    a.AutonomousSystemNumber AS MM_ASN,
    a.AutonomousSystemOrg    AS MM_ASNOrg,
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 4) AS BIGINT) * 16777216 +
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 3) AS BIGINT) * 65536 +
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 2) AS BIGINT) * 256 +
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 1) AS BIGINT) AS StartIp,
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 4) AS BIGINT) * 16777216 +
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 3) AS BIGINT) * 65536 +
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 2) AS BIGINT) * 256 +
    CAST(PARSENAME(LEFT(a.NetworkCidr, CHARINDEX('/', a.NetworkCidr) - 1), 1) AS BIGINT)
    + POWER(CAST(2 AS BIGINT), 32 - CAST(RIGHT(a.NetworkCidr, LEN(a.NetworkCidr) - CHARINDEX('/', a.NetworkCidr)) AS INT)) - 1 AS EndIp
INTO #AsnRange
FROM Geo.ASN a
WHERE a.NetworkCidr NOT LIKE '%:%';

CREATE INDEX IX_AsnRange ON #AsnRange (StartIp, EndIp);
PRINT 'ASN ranges materialized: ' + CAST(@@ROWCOUNT AS VARCHAR) + ' rows';

-- Step 5: Join everything — look up each sample IP in both MaxMind and compare
SELECT
    s.IPAddress,
    s.IPAPI_CC,     cl.CountryIsoCode AS MM_CC,
    s.IPAPI_Region, cl.Subdivision1Name AS MM_Region,
    s.IPAPI_City,   cl.CityName AS MM_City,
    s.IPAPI_Zip,    cr.MM_Zip,
    s.IPAPI_Lat,    cr.MM_Lat,
    s.IPAPI_Lon,    cr.MM_Lon,
    s.IPAPI_TZ,     cl.TimeZone AS MM_TZ,
    s.IPAPI_ISP,    ar.MM_ASNOrg,
    s.IPAPI_AS,     ar.MM_ASN
INTO #Results
FROM #Sample s
OUTER APPLY (
    SELECT TOP 1 cr2.GeonameId, cr2.MM_Zip, cr2.MM_Lat, cr2.MM_Lon
    FROM #CityRange cr2
    WHERE s.IpInt BETWEEN cr2.StartIp AND cr2.EndIp
    ORDER BY (cr2.EndIp - cr2.StartIp)
) cr
LEFT JOIN Geo.CityLocation cl ON cr.GeonameId = cl.GeonameId
OUTER APPLY (
    SELECT TOP 1 ar2.MM_ASN, ar2.MM_ASNOrg
    FROM #AsnRange ar2
    WHERE s.IpInt BETWEEN ar2.StartIp AND ar2.EndIp
    ORDER BY (ar2.EndIp - ar2.StartIp)
) ar;

PRINT 'Results joined: ' + CAST(@@ROWCOUNT AS VARCHAR) + ' rows';

-- ====================================================================
-- Step 6: REPORT
-- ====================================================================
PRINT '';
PRINT '================================================================';
PRINT '  MaxMind (Geo.*) vs IPAPI.IP — Accuracy Report';
PRINT '================================================================';

DECLARE @total INT = (SELECT COUNT(*) FROM #Results);
PRINT 'Sample: ' + CAST(@total AS VARCHAR) + ' IPs (PiXL.IP intersect IPAPI.IP)';
PRINT '';

-- Country Code
DECLARE @cc_match INT, @cc_miss INT, @cc_null INT;
SELECT @cc_match = SUM(CASE WHEN MM_CC = IPAPI_CC THEN 1 ELSE 0 END),
       @cc_miss  = SUM(CASE WHEN MM_CC IS NOT NULL AND MM_CC <> IPAPI_CC THEN 1 ELSE 0 END),
       @cc_null  = SUM(CASE WHEN MM_CC IS NULL THEN 1 ELSE 0 END)
FROM #Results;
PRINT 'Country:     Match=' + CAST(@cc_match AS VARCHAR(10)) + 
      '  Mismatch=' + CAST(@cc_miss AS VARCHAR(10)) + 
      '  NoData=' + CAST(@cc_null AS VARCHAR(10)) +
      '  Rate=' + CAST(CAST(100.0*@cc_match/NULLIF(@cc_match+@cc_miss,0) AS DECIMAL(5,1)) AS VARCHAR) + '%';

-- Region
DECLARE @rg_match INT, @rg_miss INT, @rg_null INT;
SELECT @rg_match = SUM(CASE WHEN MM_Region = IPAPI_Region THEN 1 ELSE 0 END),
       @rg_miss  = SUM(CASE WHEN MM_Region IS NOT NULL AND IPAPI_Region IS NOT NULL AND MM_Region <> IPAPI_Region THEN 1 ELSE 0 END),
       @rg_null  = SUM(CASE WHEN MM_Region IS NULL THEN 1 ELSE 0 END)
FROM #Results;
PRINT 'Region:      Match=' + CAST(@rg_match AS VARCHAR(10)) + 
      '  Mismatch=' + CAST(@rg_miss AS VARCHAR(10)) + 
      '  NoData=' + CAST(@rg_null AS VARCHAR(10)) +
      '  Rate=' + CAST(CAST(100.0*@rg_match/NULLIF(@rg_match+@rg_miss,0) AS DECIMAL(5,1)) AS VARCHAR) + '%';

-- City
DECLARE @ct_match INT, @ct_miss INT, @ct_null INT;
SELECT @ct_match = SUM(CASE WHEN MM_City = IPAPI_City THEN 1 ELSE 0 END),
       @ct_miss  = SUM(CASE WHEN MM_City IS NOT NULL AND MM_City <> IPAPI_City THEN 1 ELSE 0 END),
       @ct_null  = SUM(CASE WHEN MM_City IS NULL THEN 1 ELSE 0 END)
FROM #Results;
PRINT 'City:        Match=' + CAST(@ct_match AS VARCHAR(10)) + 
      '  Mismatch=' + CAST(@ct_miss AS VARCHAR(10)) + 
      '  NoData=' + CAST(@ct_null AS VARCHAR(10)) +
      '  Rate=' + CAST(CAST(100.0*@ct_match/NULLIF(@ct_match+@ct_miss,0) AS DECIMAL(5,1)) AS VARCHAR) + '%';

-- Zip
DECLARE @zp_match INT, @zp_miss INT, @zp_null INT;
SELECT @zp_match = SUM(CASE WHEN MM_Zip = IPAPI_Zip AND LEN(MM_Zip) > 0 THEN 1 ELSE 0 END),
       @zp_miss  = SUM(CASE WHEN LEN(MM_Zip) > 0 AND LEN(IPAPI_Zip) > 0 AND MM_Zip <> IPAPI_Zip THEN 1 ELSE 0 END),
       @zp_null  = SUM(CASE WHEN MM_Zip IS NULL OR LEN(MM_Zip) = 0 THEN 1 ELSE 0 END)
FROM #Results;
PRINT 'Zip:         Match=' + CAST(@zp_match AS VARCHAR(10)) + 
      '  Mismatch=' + CAST(@zp_miss AS VARCHAR(10)) + 
      '  NoData=' + CAST(@zp_null AS VARCHAR(10)) +
      '  Rate=' + CAST(CAST(100.0*@zp_match/NULLIF(@zp_match+@zp_miss,0) AS DECIMAL(5,1)) AS VARCHAR) + '%';

-- Timezone
DECLARE @tz_match INT, @tz_miss INT, @tz_null INT;
SELECT @tz_match = SUM(CASE WHEN MM_TZ = IPAPI_TZ THEN 1 ELSE 0 END),
       @tz_miss  = SUM(CASE WHEN MM_TZ IS NOT NULL AND IPAPI_TZ IS NOT NULL AND MM_TZ <> IPAPI_TZ THEN 1 ELSE 0 END),
       @tz_null  = SUM(CASE WHEN MM_TZ IS NULL THEN 1 ELSE 0 END)
FROM #Results;
PRINT 'Timezone:    Match=' + CAST(@tz_match AS VARCHAR(10)) + 
      '  Mismatch=' + CAST(@tz_miss AS VARCHAR(10)) + 
      '  NoData=' + CAST(@tz_null AS VARCHAR(10)) +
      '  Rate=' + CAST(CAST(100.0*@tz_match/NULLIF(@tz_match+@tz_miss,0) AS DECIMAL(5,1)) AS VARCHAR) + '%';

-- ASN (compare AS number: IPAPI "AS7922 ..." vs MaxMind int 7922)
DECLARE @asn_match INT, @asn_miss INT, @asn_null INT;
SELECT @asn_match = SUM(CASE WHEN MM_ASN IS NOT NULL AND IPAPI_AS LIKE 'AS' + CAST(MM_ASN AS VARCHAR) + ' %' THEN 1 ELSE 0 END),
       @asn_miss  = SUM(CASE WHEN MM_ASN IS NOT NULL AND IPAPI_AS IS NOT NULL AND IPAPI_AS NOT LIKE 'AS' + CAST(MM_ASN AS VARCHAR) + ' %' THEN 1 ELSE 0 END),
       @asn_null  = SUM(CASE WHEN MM_ASN IS NULL THEN 1 ELSE 0 END)
FROM #Results;
PRINT 'ASN:         Match=' + CAST(@asn_match AS VARCHAR(10)) + 
      '  Mismatch=' + CAST(@asn_miss AS VARCHAR(10)) + 
      '  NoData=' + CAST(@asn_null AS VARCHAR(10)) +
      '  Rate=' + CAST(CAST(100.0*@asn_match/NULLIF(@asn_match+@asn_miss,0) AS DECIMAL(5,1)) AS VARCHAR) + '%';

-- Lat/Lon distance (< 0.5 degrees ≈ ~55km = "close")
DECLARE @close INT, @far INT, @geo_null INT;
SELECT @close    = SUM(CASE WHEN MM_Lat IS NOT NULL AND ABS(CAST(MM_Lat AS FLOAT) - CAST(IPAPI_Lat AS FLOAT)) < 0.5 
                         AND ABS(CAST(MM_Lon AS FLOAT) - CAST(IPAPI_Lon AS FLOAT)) < 0.5 THEN 1 ELSE 0 END),
       @far      = SUM(CASE WHEN MM_Lat IS NOT NULL AND (ABS(CAST(MM_Lat AS FLOAT) - CAST(IPAPI_Lat AS FLOAT)) >= 0.5 
                         OR ABS(CAST(MM_Lon AS FLOAT) - CAST(IPAPI_Lon AS FLOAT)) >= 0.5) THEN 1 ELSE 0 END),
       @geo_null = SUM(CASE WHEN MM_Lat IS NULL THEN 1 ELSE 0 END)
FROM #Results;
PRINT 'LatLon<55km: Match=' + CAST(@close AS VARCHAR(10)) + 
      '  Far=' + CAST(@far AS VARCHAR(10)) + 
      '  NoData=' + CAST(@geo_null AS VARCHAR(10)) +
      '  Rate=' + CAST(CAST(100.0*@close/NULLIF(@close+@far,0) AS DECIMAL(5,1)) AS VARCHAR) + '%';

-- ISP vs ASN Org (substring match)
DECLARE @isp_match INT, @isp_total INT;
SELECT @isp_match = SUM(CASE WHEN MM_ASNOrg IS NOT NULL AND IPAPI_ISP IS NOT NULL 
                          AND (MM_ASNOrg = IPAPI_ISP 
                            OR IPAPI_ISP LIKE '%' + LEFT(MM_ASNOrg, 20) + '%' 
                            OR MM_ASNOrg LIKE '%' + LEFT(IPAPI_ISP, 20) + '%')
                          THEN 1 ELSE 0 END),
       @isp_total = SUM(CASE WHEN MM_ASNOrg IS NOT NULL AND IPAPI_ISP IS NOT NULL THEN 1 ELSE 0 END)
FROM #Results;
PRINT 'ISP~ASNOrg:  Match=' + CAST(@isp_match AS VARCHAR(10)) + 
      '/' + CAST(@isp_total AS VARCHAR(10)) +
      '  Rate=' + CAST(CAST(100.0*@isp_match/NULLIF(@isp_total,0) AS DECIMAL(5,1)) AS VARCHAR) + '%';

PRINT '';
PRINT '── Top 10 Country Mismatches ──';
SELECT TOP 10 IPAddress, IPAPI_CC, MM_CC 
FROM #Results WHERE MM_CC IS NOT NULL AND MM_CC <> IPAPI_CC;

PRINT '';
PRINT '── Top 10 City Mismatches ──';
SELECT TOP 10 IPAddress, IPAPI_City, MM_City 
FROM #Results WHERE MM_City IS NOT NULL AND MM_City <> IPAPI_City;

PRINT '';
PRINT '── Top 10 ISP vs ASN Org Divergences ──';
SELECT TOP 10 IPAddress, IPAPI_ISP, MM_ASNOrg 
FROM #Results 
WHERE MM_ASNOrg IS NOT NULL AND IPAPI_ISP IS NOT NULL 
  AND MM_ASNOrg <> IPAPI_ISP 
  AND IPAPI_ISP NOT LIKE '%' + LEFT(MM_ASNOrg, 20) + '%' 
  AND MM_ASNOrg NOT LIKE '%' + LEFT(IPAPI_ISP, 20) + '%';

PRINT '';
PRINT '── Top 10 Timezone Mismatches ──';
SELECT TOP 10 IPAddress, IPAPI_TZ, MM_TZ
FROM #Results WHERE MM_TZ IS NOT NULL AND IPAPI_TZ IS NOT NULL AND MM_TZ <> IPAPI_TZ;

-- Cleanup
DROP TABLE #Sample, #CityRange, #AsnRange, #Results;
PRINT '';
PRINT 'Done.';
