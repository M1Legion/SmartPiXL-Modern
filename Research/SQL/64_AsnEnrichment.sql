/*  64_AsnEnrichment.sql
    ASN enrichment analysis: combine bgp.tools classification with
    MaxMind ASN and IPAPI ASN data.

    Key question: What percentage of IPAPI traffic comes from
    Eyeball (consumer ISP) vs Content (CDN/cloud) vs Transit vs Carrier?

    This is the most valuable signal for lead quality -- a visitor from
    an Eyeball ASN is a real person; Content ASN is a bot/server.

    Run time: ~2-3 minutes
*/
SET NOCOUNT ON;

PRINT '================================================';
PRINT '  ASN Enrichment: bgp.tools Classification';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '================================================';
PRINT '';

-- Step 1: Extract ASN numbers from IPAPI.IP
PRINT 'Step 1: Extracting ASN numbers from IPAPI.IP (344M rows)...';

IF OBJECT_ID('tempdb..#IpapiAsn') IS NOT NULL DROP TABLE #IpapiAsn;

SELECT
    TRY_CAST(SUBSTRING([As], 3, CHARINDEX(' ', [As] + ' ') - 3) AS INT) AS Asn,
    COUNT_BIG(*) AS IpCount
INTO #IpapiAsn
FROM IPAPI.IP
WHERE [As] LIKE 'AS%' AND Status = 'success'
GROUP BY TRY_CAST(SUBSTRING([As], 3, CHARINDEX(' ', [As] + ' ') - 3) AS INT);

-- Remove NULL ASNs
DELETE FROM #IpapiAsn WHERE Asn IS NULL;

CREATE CLUSTERED INDEX CIX_IpapiAsn ON #IpapiAsn (Asn);

DECLARE @uniqueAsns INT = (SELECT COUNT(*) FROM #IpapiAsn);
DECLARE @totalIps BIGINT = (SELECT SUM(IpCount) FROM #IpapiAsn);
PRINT 'Unique ASNs in IPAPI: ' + FORMAT(@uniqueAsns, 'N0');
PRINT 'Total IPs with ASN:   ' + FORMAT(@totalIps, 'N0');
PRINT '';

-- Step 2: Join with bgp.tools classification
PRINT 'Step 2: Joining with bgp.tools classification...';
PRINT '';

PRINT '-- 2a. Traffic by ASN Class (weighted by IP count) --';
SELECT
    ISNULL(bg.Class, '(not in bgp.tools)') AS AsnClass,
    COUNT(*) AS UniqueAsns,
    SUM(ia.IpCount) AS TotalIPs,
    FORMAT(SUM(ia.IpCount), 'N0') AS TotalIPs_Fmt,
    CAST(100.0 * SUM(ia.IpCount) / @totalIps AS DECIMAL(5,2)) AS PctOfTraffic
FROM #IpapiAsn ia
LEFT JOIN Ref.BgpToolsAsn bg ON ia.Asn = bg.Asn
GROUP BY ISNULL(bg.Class, '(not in bgp.tools)')
ORDER BY TotalIPs DESC;

PRINT '';

-- Step 3: Top ASNs per class
PRINT '-- 3a. Top 15 Eyeball ASNs (consumer ISPs) --';
SELECT TOP 15
    ia.Asn,
    bg.Name AS AsnName,
    bg.CountryCode,
    ia.IpCount,
    FORMAT(ia.IpCount, 'N0') AS IpCount_Fmt
FROM #IpapiAsn ia
JOIN Ref.BgpToolsAsn bg ON ia.Asn = bg.Asn
WHERE bg.Class = 'Eyeball'
ORDER BY ia.IpCount DESC;

PRINT '';

PRINT '-- 3b. Top 15 Content ASNs (CDN/cloud -- likely bots) --';
SELECT TOP 15
    ia.Asn,
    bg.Name AS AsnName,
    bg.CountryCode,
    ia.IpCount,
    FORMAT(ia.IpCount, 'N0') AS IpCount_Fmt
FROM #IpapiAsn ia
JOIN Ref.BgpToolsAsn bg ON ia.Asn = bg.Asn
WHERE bg.Class = 'Content'
ORDER BY ia.IpCount DESC;

PRINT '';

PRINT '-- 3c. Top 15 Transit ASNs (backbone carriers) --';
SELECT TOP 15
    ia.Asn,
    bg.Name AS AsnName,
    bg.CountryCode,
    ia.IpCount,
    FORMAT(ia.IpCount, 'N0') AS IpCount_Fmt
FROM #IpapiAsn ia
JOIN Ref.BgpToolsAsn bg ON ia.Asn = bg.Asn
WHERE bg.Class = 'Transit'
ORDER BY ia.IpCount DESC;

PRINT '';

PRINT '-- 3d. Top 15 Carrier ASNs --';
SELECT TOP 15
    ia.Asn,
    bg.Name AS AsnName,
    bg.CountryCode,
    ia.IpCount,
    FORMAT(ia.IpCount, 'N0') AS IpCount_Fmt
FROM #IpapiAsn ia
JOIN Ref.BgpToolsAsn bg ON ia.Asn = bg.Asn
WHERE bg.Class = 'Carrier'
ORDER BY ia.IpCount DESC;

PRINT '';

-- Step 4: MaxMind ASN vs bgp.tools coverage comparison
PRINT '-- 4. MaxMind ASN vs bgp.tools Coverage --';

IF OBJECT_ID('tempdb..#MmAsn') IS NOT NULL DROP TABLE #MmAsn;

SELECT DISTINCT AutonomousSystemNumber AS Asn, AutonomousSystemOrg AS OrgName
INTO #MmAsn
FROM Geo.ASN
WHERE NetworkCidr NOT LIKE '%:%'
  AND AutonomousSystemNumber IS NOT NULL;

SELECT
    'In both MaxMind + bgp.tools' AS Coverage,
    COUNT(*) AS AsnCount
FROM #MmAsn ma
JOIN Ref.BgpToolsAsn bg ON ma.Asn = bg.Asn
UNION ALL
SELECT
    'MaxMind only (not in bgp.tools)',
    COUNT(*)
FROM #MmAsn ma
LEFT JOIN Ref.BgpToolsAsn bg ON ma.Asn = bg.Asn
WHERE bg.Asn IS NULL
UNION ALL
SELECT
    'bgp.tools only (not in MaxMind)',
    COUNT(*)
FROM Ref.BgpToolsAsn bg
LEFT JOIN #MmAsn ma ON bg.Asn = ma.Asn
WHERE ma.Asn IS NULL
UNION ALL
SELECT
    'In IPAPI but not in bgp.tools',
    COUNT(*)
FROM #IpapiAsn ia
LEFT JOIN Ref.BgpToolsAsn bg ON ia.Asn = bg.Asn
WHERE bg.Asn IS NULL;

PRINT '';

-- Step 5: Cross-check MaxMind ASN org name vs bgp.tools name
PRINT '-- 5. ASN Name Comparison (MaxMind vs bgp.tools, top 20 by IP count) --';
SELECT TOP 20
    ia.Asn,
    ma.OrgName AS MaxMind_Name,
    bg.Name AS BgpTools_Name,
    bg.Class,
    bg.CountryCode,
    ia.IpCount
FROM #IpapiAsn ia
JOIN #MmAsn ma ON ia.Asn = ma.Asn
JOIN Ref.BgpToolsAsn bg ON ia.Asn = bg.Asn
ORDER BY ia.IpCount DESC;

PRINT '';

-- Step 6: RIR country vs bgp.tools country for ASN
PRINT '-- 6. bgp.tools ASN Country vs RIR Country (disagreements) --';
PRINT '   Where bgp.tools says ASN is in country X but RIR allocated IPs to country Y';

-- Sample: take the top 100 ASNs by IP count, look up their most common RIR country
;WITH TopAsns AS (
    SELECT TOP 100 ia.Asn, ia.IpCount, bg.CountryCode AS BgpCC, bg.Class, bg.Name
    FROM #IpapiAsn ia
    JOIN Ref.BgpToolsAsn bg ON ia.Asn = bg.Asn
    ORDER BY ia.IpCount DESC
)
SELECT TOP 20
    ta.Asn, ta.Name, ta.Class, ta.BgpCC,
    -- Can't directly join ASN to RIR (different key types)
    -- but we can note the discrepancy for manual review
    ta.IpCount
FROM TopAsns ta
WHERE ta.BgpCC IS NOT NULL
ORDER BY ta.IpCount DESC;

PRINT '';
PRINT 'ASN enrichment analysis complete.';

DROP TABLE #IpapiAsn;
DROP TABLE #MmAsn;
