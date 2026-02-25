-- ====================================================================
-- MaxMind (Geo.*) vs IPAPI.IP — FULL Accuracy Report (All 344M Records)
-- ====================================================================
-- Run: sqlcmd -S localhost\SQL2025 -d SmartPiXL -i MaxMind_vs_IPAPI_Full_Accuracy.sql -o FullAccuracy_Results.txt
-- Expected runtime: 20-40 minutes (2TB RAM, /24 subnet expansion strategy)
-- Server: SQL Server 2025 Developer, 2TB RAM
--
-- STRATEGY: Instead of per-IP CIDR range lookups (OUTER APPLY, O(n*m)),
-- expand all CIDR ranges to /24 subnet blocks, deduplicate to most-specific
-- match per /24, then hash-join 344M IPs by Subnet24 = IpInt/256.
-- This transforms range lookups into O(1) equality joins.
-- ====================================================================
SET NOCOUNT ON;

DECLARE @start DATETIME2 = SYSDATETIME();
DECLARE @phaseStart DATETIME2;
DECLARE @msg NVARCHAR(500);

RAISERROR('====================================================================', 0, 1) WITH NOWAIT;
RAISERROR('  MaxMind (Geo.*) vs IPAPI.IP — FULL Accuracy (All Records)', 0, 1) WITH NOWAIT;
SET @msg = CONCAT('  Started: ', CONVERT(VARCHAR(30), @start, 120));
RAISERROR(@msg, 0, 1) WITH NOWAIT;
RAISERROR('  Strategy: /24 subnet expansion + hash join', 0, 1) WITH NOWAIT;
RAISERROR('====================================================================', 0, 1) WITH NOWAIT;
RAISERROR('', 0, 1) WITH NOWAIT;

-- ============================================================
-- Phase 1: Materialize CIDR ranges with start/end integers
-- ============================================================
SET @phaseStart = SYSDATETIME();
RAISERROR('Phase 1: Building CIDR range tables...', 0, 1) WITH NOWAIT;

-- CityBlock CIDR → start/end BIGINT + range size
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
    + POWER(CAST(2 AS BIGINT), 32 - CAST(RIGHT(cb.NetworkCidr, LEN(cb.NetworkCidr) - CHARINDEX('/', cb.NetworkCidr)) AS INT)) - 1 AS EndIp,
    -- Range size (smaller = more specific CIDR)
    POWER(CAST(2 AS BIGINT), 32 - CAST(RIGHT(cb.NetworkCidr, LEN(cb.NetworkCidr) - CHARINDEX('/', cb.NetworkCidr)) AS INT)) AS RangeSize
INTO #CityRange
FROM Geo.CityBlock cb
WHERE cb.NetworkCidr NOT LIKE '%:%';

DECLARE @cityRows INT = @@ROWCOUNT;
SET @msg = CONCAT('  CityBlock ranges: ', FORMAT(@cityRows, 'N0'), ' (',
    DATEDIFF(SECOND, @phaseStart, SYSDATETIME()), 's)');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- ASN CIDR → start/end BIGINT + range size
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
    + POWER(CAST(2 AS BIGINT), 32 - CAST(RIGHT(a.NetworkCidr, LEN(a.NetworkCidr) - CHARINDEX('/', a.NetworkCidr)) AS INT)) - 1 AS EndIp,
    POWER(CAST(2 AS BIGINT), 32 - CAST(RIGHT(a.NetworkCidr, LEN(a.NetworkCidr) - CHARINDEX('/', a.NetworkCidr)) AS INT)) AS RangeSize
INTO #AsnRange
FROM Geo.ASN a
WHERE a.NetworkCidr NOT LIKE '%:%';

DECLARE @asnRows INT = @@ROWCOUNT;
SET @msg = CONCAT('  ASN ranges: ', FORMAT(@asnRows, 'N0'), ' (',
    DATEDIFF(SECOND, @phaseStart, SYSDATETIME()), 's)');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- ============================================================
-- Phase 2: Expand CIDRs to /24 subnet lookup maps
-- Key insight: transform range lookups → equality joins
-- Each CIDR covers StartIp/256 .. EndIp/256 /24 blocks
-- For each /24 block, keep the most specific CIDR match
-- ============================================================
SET @phaseStart = SYSDATETIME();
RAISERROR('', 0, 1) WITH NOWAIT;
RAISERROR('Phase 2: Expanding CIDRs to /24 subnet lookup maps...', 0, 1) WITH NOWAIT;

-- Numbers table: 0..65535 (enough for a /8 which spans 65,536 /24 blocks)
;WITH L0 AS (SELECT 1 AS c UNION ALL SELECT 1),
      L1 AS (SELECT 1 AS c FROM L0 a CROSS JOIN L0 b),
      L2 AS (SELECT 1 AS c FROM L1 a CROSS JOIN L1 b),
      L3 AS (SELECT 1 AS c FROM L2 a CROSS JOIN L2 b),
      L4 AS (SELECT 1 AS c FROM L3 a CROSS JOIN L3 b),
      Nums AS (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS N FROM L4)
SELECT
    cr.GeonameId, cr.MM_Zip, cr.MM_Lat, cr.MM_Lon,
    CAST(cr.StartIp / 256 + n.N AS BIGINT) AS Subnet24,
    cr.RangeSize
INTO #CityExpanded
FROM #CityRange cr
CROSS APPLY (
    SELECT N FROM Nums WHERE N <= (cr.EndIp / 256 - cr.StartIp / 256)
) n;

DECLARE @cityExp INT = @@ROWCOUNT;
SET @msg = CONCAT('  CityBlock expanded to /24: ', FORMAT(@cityExp, 'N0'), ' rows (',
    DATEDIFF(SECOND, @phaseStart, SYSDATETIME()), 's)');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- Deduplicate: keep most specific (smallest RangeSize) per /24
SET @phaseStart = SYSDATETIME();
RAISERROR('  Deduplicating CityBlock to most-specific per /24...', 0, 1) WITH NOWAIT;

;WITH Ranked AS (
    SELECT *, ROW_NUMBER() OVER (PARTITION BY Subnet24 ORDER BY RangeSize ASC) AS rn
    FROM #CityExpanded
)
SELECT GeonameId, MM_Zip, MM_Lat, MM_Lon, Subnet24
INTO #CityMap
FROM Ranked WHERE rn = 1;

DECLARE @cityMap INT = @@ROWCOUNT;
DROP TABLE #CityExpanded;
CREATE CLUSTERED INDEX CIX_CityMap ON #CityMap (Subnet24);

SET @msg = CONCAT('  CityMap: ', FORMAT(@cityMap, 'N0'), ' unique /24 blocks (',
    DATEDIFF(SECOND, @phaseStart, SYSDATETIME()), 's)');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- ASN expansion
SET @phaseStart = SYSDATETIME();
RAISERROR('  Expanding ASN to /24...', 0, 1) WITH NOWAIT;

;WITH L0 AS (SELECT 1 AS c UNION ALL SELECT 1),
      L1 AS (SELECT 1 AS c FROM L0 a CROSS JOIN L0 b),
      L2 AS (SELECT 1 AS c FROM L1 a CROSS JOIN L1 b),
      L3 AS (SELECT 1 AS c FROM L2 a CROSS JOIN L2 b),
      L4 AS (SELECT 1 AS c FROM L3 a CROSS JOIN L3 b),
      Nums AS (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS N FROM L4)
SELECT
    ar.MM_ASN, ar.MM_ASNOrg,
    CAST(ar.StartIp / 256 + n.N AS BIGINT) AS Subnet24,
    ar.RangeSize
INTO #AsnExpanded
FROM #AsnRange ar
CROSS APPLY (
    SELECT N FROM Nums WHERE N <= (ar.EndIp / 256 - ar.StartIp / 256)
) n;

DECLARE @asnExp INT = @@ROWCOUNT;
SET @msg = CONCAT('  ASN expanded to /24: ', FORMAT(@asnExp, 'N0'), ' rows (',
    DATEDIFF(SECOND, @phaseStart, SYSDATETIME()), 's)');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- ASN dedup
SET @phaseStart = SYSDATETIME();
RAISERROR('  Deduplicating ASN to most-specific per /24...', 0, 1) WITH NOWAIT;

;WITH Ranked AS (
    SELECT *, ROW_NUMBER() OVER (PARTITION BY Subnet24 ORDER BY RangeSize ASC) AS rn
    FROM #AsnExpanded
)
SELECT MM_ASN, MM_ASNOrg, Subnet24
INTO #AsnMap
FROM Ranked WHERE rn = 1;

DECLARE @asnMap INT = @@ROWCOUNT;
DROP TABLE #AsnExpanded;
CREATE CLUSTERED INDEX CIX_AsnMap ON #AsnMap (Subnet24);

SET @msg = CONCAT('  AsnMap: ', FORMAT(@asnMap, 'N0'), ' unique /24 blocks (',
    DATEDIFF(SECOND, @phaseStart, SYSDATETIME()), 's)');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- Free the raw range tables (no longer needed)
DROP TABLE #CityRange, #AsnRange;

-- ============================================================
-- Phase 3: Convert ALL IPAPI.IP to BIGINT (one full table scan)
-- ============================================================
SET @phaseStart = SYSDATETIME();
RAISERROR('', 0, 1) WITH NOWAIT;
RAISERROR('Phase 3: Converting all IPAPI.IP to BIGINT (full table scan)...', 0, 1) WITH NOWAIT;
RAISERROR('  This processes ~344M rows. Expect 5-15 minutes...', 0, 1) WITH NOWAIT;

SELECT
    i.IP,
    CAST(PARSENAME(i.IP, 4) AS BIGINT) * 16777216 +
    CAST(PARSENAME(i.IP, 3) AS BIGINT) * 65536 +
    CAST(PARSENAME(i.IP, 2) AS BIGINT) * 256 +
    CAST(PARSENAME(i.IP, 1) AS BIGINT) AS IpInt,
    i.CountryCode,
    i.RegionName,
    i.City,
    i.Zip,
    CAST(i.Lat AS DECIMAL(9,4)) AS Lat,
    CAST(i.Lon AS DECIMAL(9,4)) AS Lon,
    i.Timezone,
    i.ISP,
    i.[As]
INTO #AllIpapi
FROM IPAPI.IP i
WHERE i.Status = 'success'
  AND i.CountryCode IS NOT NULL
  AND i.IP NOT LIKE '%:%';

DECLARE @totalIpapi BIGINT = @@ROWCOUNT;
SET @msg = CONCAT('  Rows loaded: ', FORMAT(@totalIpapi, 'N0'), ' (',
    DATEDIFF(SECOND, @phaseStart, SYSDATETIME()), 's)');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- No clustered index needed — we're computing IpInt/256 inline for the hash join
-- But an index on IpInt helps if we want mismatch samples later
SET @phaseStart = SYSDATETIME();
RAISERROR('  Creating nonclustered index on IpInt (for mismatch samples)...', 0, 1) WITH NOWAIT;

CREATE NONCLUSTERED INDEX IX_AllIpapi_IpInt ON #AllIpapi (IpInt);

SET @msg = CONCAT('  Index created (',
    DATEDIFF(SECOND, @phaseStart, SYSDATETIME()), 's)');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- ============================================================
-- Phase 4: THE BIG JOIN — hash join by Subnet24
-- Single pass: 344M rows × equality lookup into ~13M CityMap + ~12M AsnMap
-- ============================================================
SET @phaseStart = SYSDATETIME();
RAISERROR('', 0, 1) WITH NOWAIT;
RAISERROR('Phase 4: Running full comparison (hash join by /24 subnet)...', 0, 1) WITH NOWAIT;
RAISERROR('  344M IPs × 2 lookups via Subnet24 equality join. Expect 5-15 minutes...', 0, 1) WITH NOWAIT;

SELECT
    COUNT(*)                                        AS TotalProcessed,
    -- MaxMind coverage
    SUM(CASE WHEN cl.CountryIsoCode IS NOT NULL OR am.MM_ASN IS NOT NULL THEN 1 ELSE 0 END) AS MM_Found,
    -- Country
    SUM(CASE WHEN cl.CountryIsoCode IS NOT NULL AND cl.CountryIsoCode = src.CountryCode THEN 1 ELSE 0 END)    AS CC_Match,
    SUM(CASE WHEN cl.CountryIsoCode IS NOT NULL AND cl.CountryIsoCode <> src.CountryCode THEN 1 ELSE 0 END)   AS CC_Miss,
    SUM(CASE WHEN cl.CountryIsoCode IS NULL THEN 1 ELSE 0 END)                                                AS CC_Null,
    -- Region
    SUM(CASE WHEN cl.Subdivision1Name IS NOT NULL AND cl.Subdivision1Name = src.RegionName THEN 1 ELSE 0 END)                          AS Rg_Match,
    SUM(CASE WHEN cl.Subdivision1Name IS NOT NULL AND src.RegionName IS NOT NULL AND cl.Subdivision1Name <> src.RegionName THEN 1 ELSE 0 END) AS Rg_Miss,
    SUM(CASE WHEN cl.Subdivision1Name IS NULL THEN 1 ELSE 0 END)                                                                      AS Rg_Null,
    -- City
    SUM(CASE WHEN cl.CityName IS NOT NULL AND cl.CityName = src.City THEN 1 ELSE 0 END)     AS Ct_Match,
    SUM(CASE WHEN cl.CityName IS NOT NULL AND cl.CityName <> src.City THEN 1 ELSE 0 END)    AS Ct_Miss,
    SUM(CASE WHEN cl.CityName IS NULL THEN 1 ELSE 0 END)                                    AS Ct_Null,
    -- Zip
    SUM(CASE WHEN cm.MM_Zip IS NOT NULL AND LEN(cm.MM_Zip) > 0 AND cm.MM_Zip = src.Zip THEN 1 ELSE 0 END)          AS Zp_Match,
    SUM(CASE WHEN LEN(cm.MM_Zip) > 0 AND LEN(src.Zip) > 0 AND cm.MM_Zip <> src.Zip THEN 1 ELSE 0 END)             AS Zp_Miss,
    SUM(CASE WHEN cm.MM_Zip IS NULL OR LEN(cm.MM_Zip) = 0 THEN 1 ELSE 0 END)                                      AS Zp_Null,
    -- Timezone
    SUM(CASE WHEN cl.TimeZone IS NOT NULL AND cl.TimeZone = src.Timezone THEN 1 ELSE 0 END)                                    AS Tz_Match,
    SUM(CASE WHEN cl.TimeZone IS NOT NULL AND src.Timezone IS NOT NULL AND cl.TimeZone <> src.Timezone THEN 1 ELSE 0 END)       AS Tz_Miss,
    SUM(CASE WHEN cl.TimeZone IS NULL THEN 1 ELSE 0 END)                                                                      AS Tz_Null,
    -- ASN (IPAPI format: "AS7922 Comcast..." — compare numeric part)
    SUM(CASE WHEN am.MM_ASN IS NOT NULL AND src.[As] LIKE 'AS' + CAST(am.MM_ASN AS VARCHAR) + ' %' THEN 1 ELSE 0 END)                        AS Asn_Match,
    SUM(CASE WHEN am.MM_ASN IS NOT NULL AND src.[As] IS NOT NULL AND src.[As] NOT LIKE 'AS' + CAST(am.MM_ASN AS VARCHAR) + ' %' THEN 1 ELSE 0 END) AS Asn_Miss,
    SUM(CASE WHEN am.MM_ASN IS NULL THEN 1 ELSE 0 END)                                                                                      AS Asn_Null,
    -- LatLon distance (<0.5° ≈ 55km)
    SUM(CASE WHEN cm.MM_Lat IS NOT NULL
              AND ABS(CAST(cm.MM_Lat AS FLOAT) - CAST(src.Lat AS FLOAT)) < 0.5
              AND ABS(CAST(cm.MM_Lon AS FLOAT) - CAST(src.Lon AS FLOAT)) < 0.5 THEN 1 ELSE 0 END)    AS Ll_Close,
    SUM(CASE WHEN cm.MM_Lat IS NOT NULL
              AND (ABS(CAST(cm.MM_Lat AS FLOAT) - CAST(src.Lat AS FLOAT)) >= 0.5
                OR ABS(CAST(cm.MM_Lon AS FLOAT) - CAST(src.Lon AS FLOAT)) >= 0.5) THEN 1 ELSE 0 END) AS Ll_Far,
    SUM(CASE WHEN cm.MM_Lat IS NULL THEN 1 ELSE 0 END)                                                AS Ll_Null,
    -- ISP vs ASN Org (fuzzy substring match)
    SUM(CASE WHEN am.MM_ASNOrg IS NOT NULL AND src.ISP IS NOT NULL
              AND (am.MM_ASNOrg = src.ISP
                OR src.ISP LIKE '%' + LEFT(am.MM_ASNOrg, 20) + '%'
                OR am.MM_ASNOrg LIKE '%' + LEFT(src.ISP, 20) + '%')
              THEN 1 ELSE 0 END)                                               AS Isp_Match,
    SUM(CASE WHEN am.MM_ASNOrg IS NOT NULL AND src.ISP IS NOT NULL THEN 1 ELSE 0 END) AS Isp_Total
INTO #Stats
FROM #AllIpapi src
LEFT JOIN #CityMap cm ON cm.Subnet24 = src.IpInt / 256
LEFT JOIN Geo.CityLocation cl ON cm.GeonameId = cl.GeonameId
LEFT JOIN #AsnMap am ON am.Subnet24 = src.IpInt / 256;

SET @msg = CONCAT('  Comparison complete! (',
    DATEDIFF(SECOND, @phaseStart, SYSDATETIME()), 's)');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- ============================================================
-- Phase 5: REPORT
-- ============================================================
RAISERROR('', 0, 1) WITH NOWAIT;
RAISERROR('================================================================', 0, 1) WITH NOWAIT;
RAISERROR('  MaxMind (Geo.*) vs IPAPI.IP — FULL Accuracy Report', 0, 1) WITH NOWAIT;
RAISERROR('================================================================', 0, 1) WITH NOWAIT;

DECLARE @t BIGINT, @f BIGINT;
SELECT @t = TotalProcessed, @f = MM_Found FROM #Stats;
SET @msg = CONCAT('Total IPAPI IPs processed: ', FORMAT(@t, 'N0'));
RAISERROR(@msg, 0, 1) WITH NOWAIT;
SET @msg = CONCAT('MaxMind coverage:          ', FORMAT(@f, 'N0'), ' (',
    CAST(CAST(100.0 * @f / @t AS DECIMAL(5,1)) AS VARCHAR), '%)');
RAISERROR(@msg, 0, 1) WITH NOWAIT;
SET @msg = CONCAT('No MaxMind data:           ', FORMAT(@t - @f, 'N0'), ' (',
    CAST(CAST(100.0 * (@t - @f) / @t AS DECIMAL(5,1)) AS VARCHAR), '%)');
RAISERROR(@msg, 0, 1) WITH NOWAIT;
RAISERROR('', 0, 1) WITH NOWAIT;

-- Per-field accuracy (rate excludes NoData)
DECLARE @m BIGINT, @x BIGINT, @n BIGINT;

SELECT @m = CC_Match, @x = CC_Miss, @n = CC_Null FROM #Stats;
SET @msg = CONCAT('Country:     Match=', FORMAT(@m, 'N0'),
    '  Mismatch=', FORMAT(@x, 'N0'),
    '  NoData=', FORMAT(@n, 'N0'),
    '  Rate=', CAST(CAST(100.0*@m/NULLIF(@m+@x,0) AS DECIMAL(5,1)) AS VARCHAR), '%');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SELECT @m = Rg_Match, @x = Rg_Miss, @n = Rg_Null FROM #Stats;
SET @msg = CONCAT('Region:      Match=', FORMAT(@m, 'N0'),
    '  Mismatch=', FORMAT(@x, 'N0'),
    '  NoData=', FORMAT(@n, 'N0'),
    '  Rate=', CAST(CAST(100.0*@m/NULLIF(@m+@x,0) AS DECIMAL(5,1)) AS VARCHAR), '%');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SELECT @m = Ct_Match, @x = Ct_Miss, @n = Ct_Null FROM #Stats;
SET @msg = CONCAT('City:        Match=', FORMAT(@m, 'N0'),
    '  Mismatch=', FORMAT(@x, 'N0'),
    '  NoData=', FORMAT(@n, 'N0'),
    '  Rate=', CAST(CAST(100.0*@m/NULLIF(@m+@x,0) AS DECIMAL(5,1)) AS VARCHAR), '%');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SELECT @m = Zp_Match, @x = Zp_Miss, @n = Zp_Null FROM #Stats;
SET @msg = CONCAT('Zip:         Match=', FORMAT(@m, 'N0'),
    '  Mismatch=', FORMAT(@x, 'N0'),
    '  NoData=', FORMAT(@n, 'N0'),
    '  Rate=', CAST(CAST(100.0*@m/NULLIF(@m+@x,0) AS DECIMAL(5,1)) AS VARCHAR), '%');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SELECT @m = Tz_Match, @x = Tz_Miss, @n = Tz_Null FROM #Stats;
SET @msg = CONCAT('Timezone:    Match=', FORMAT(@m, 'N0'),
    '  Mismatch=', FORMAT(@x, 'N0'),
    '  NoData=', FORMAT(@n, 'N0'),
    '  Rate=', CAST(CAST(100.0*@m/NULLIF(@m+@x,0) AS DECIMAL(5,1)) AS VARCHAR), '%');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SELECT @m = Asn_Match, @x = Asn_Miss, @n = Asn_Null FROM #Stats;
SET @msg = CONCAT('ASN:         Match=', FORMAT(@m, 'N0'),
    '  Mismatch=', FORMAT(@x, 'N0'),
    '  NoData=', FORMAT(@n, 'N0'),
    '  Rate=', CAST(CAST(100.0*@m/NULLIF(@m+@x,0) AS DECIMAL(5,1)) AS VARCHAR), '%');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SELECT @m = Ll_Close, @x = Ll_Far, @n = Ll_Null FROM #Stats;
SET @msg = CONCAT('LatLon<55km: Close=', FORMAT(@m, 'N0'),
    '  Far=', FORMAT(@x, 'N0'),
    '  NoData=', FORMAT(@n, 'N0'),
    '  Rate=', CAST(CAST(100.0*@m/NULLIF(@m+@x,0) AS DECIMAL(5,1)) AS VARCHAR), '%');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @im BIGINT, @it BIGINT;
SELECT @im = Isp_Match, @it = Isp_Total FROM #Stats;
SET @msg = CONCAT('ISP~ASNOrg:  Match=', FORMAT(@im, 'N0'),
    '/', FORMAT(@it, 'N0'),
    '  Rate=', CAST(CAST(100.0*@im/NULLIF(@it,0) AS DECIMAL(5,1)) AS VARCHAR), '%');
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- Raw stats as result set for structured capture
RAISERROR('', 0, 1) WITH NOWAIT;
RAISERROR('── Raw Stats ──', 0, 1) WITH NOWAIT;
SELECT * FROM #Stats;

-- ============================================================
-- Phase 6: Sample Mismatches (25 random from each category)
-- ============================================================
RAISERROR('', 0, 1) WITH NOWAIT;
RAISERROR('── Top 25 Country Mismatches ──', 0, 1) WITH NOWAIT;
SELECT TOP 25
    src.IP, src.CountryCode AS IPAPI_CC, cl.CountryIsoCode AS MM_CC
FROM #AllIpapi src
LEFT JOIN #CityMap cm ON cm.Subnet24 = src.IpInt / 256
LEFT JOIN Geo.CityLocation cl ON cm.GeonameId = cl.GeonameId
WHERE cl.CountryIsoCode IS NOT NULL AND cl.CountryIsoCode <> src.CountryCode
ORDER BY NEWID();

RAISERROR('', 0, 1) WITH NOWAIT;
RAISERROR('── Top 25 City Mismatches ──', 0, 1) WITH NOWAIT;
SELECT TOP 25
    src.IP, src.City AS IPAPI_City, cl.CityName AS MM_City
FROM #AllIpapi src
LEFT JOIN #CityMap cm ON cm.Subnet24 = src.IpInt / 256
LEFT JOIN Geo.CityLocation cl ON cm.GeonameId = cl.GeonameId
WHERE cl.CityName IS NOT NULL AND cl.CityName <> src.City
ORDER BY NEWID();

RAISERROR('', 0, 1) WITH NOWAIT;
RAISERROR('── Top 25 ISP vs ASN Org Divergences ──', 0, 1) WITH NOWAIT;
SELECT TOP 25
    src.IP, src.ISP AS IPAPI_ISP, am.MM_ASNOrg
FROM #AllIpapi src
LEFT JOIN #AsnMap am ON am.Subnet24 = src.IpInt / 256
WHERE am.MM_ASNOrg IS NOT NULL AND src.ISP IS NOT NULL
  AND am.MM_ASNOrg <> src.ISP
  AND src.ISP NOT LIKE '%' + LEFT(am.MM_ASNOrg, 20) + '%'
  AND am.MM_ASNOrg NOT LIKE '%' + LEFT(src.ISP, 20) + '%'
ORDER BY NEWID();

RAISERROR('', 0, 1) WITH NOWAIT;
RAISERROR('── Top 25 Timezone Mismatches ──', 0, 1) WITH NOWAIT;
SELECT TOP 25
    src.IP, src.Timezone AS IPAPI_TZ, cl.TimeZone AS MM_TZ
FROM #AllIpapi src
LEFT JOIN #CityMap cm ON cm.Subnet24 = src.IpInt / 256
LEFT JOIN Geo.CityLocation cl ON cm.GeonameId = cl.GeonameId
WHERE cl.TimeZone IS NOT NULL AND src.Timezone IS NOT NULL
  AND cl.TimeZone <> src.Timezone
ORDER BY NEWID();

-- Cleanup
DROP TABLE IF EXISTS #AllIpapi, #CityMap, #AsnMap, #Stats;

DECLARE @elapsedTotal INT = DATEDIFF(SECOND, @start, SYSDATETIME());
SET @msg = CONCAT('Total runtime: ', @elapsedTotal / 3600, 'h ',
    (@elapsedTotal % 3600) / 60, 'm ', @elapsedTotal % 60, 's');
RAISERROR(@msg, 0, 1) WITH NOWAIT;
RAISERROR('Done.', 0, 1) WITH NOWAIT;
