/*  62_SourceOverview.sql
    Quick inventory of all IP intelligence sources + coverage stats.
    Run time: ~30 seconds
*/
SET NOCOUNT ON;

PRINT '========================================';
PRINT '  IP Intelligence Source Overview';
PRINT '  ' + CONVERT(VARCHAR(20), GETDATE(), 120);
PRINT '========================================';
PRINT '';

-- 1. Row counts across all sources
PRINT '-- 1. Source Row Counts --';
SELECT src AS [Source], rows AS [Rows], notes AS [Notes]
FROM (VALUES
    ('IPAPI.IP (paid, ip-api.com)',     (SELECT COUNT_BIG(*) FROM IPAPI.IP), 'Individual IPs with full geo'),
    ('Geo.CityBlock (MaxMind GeoLite2)',(SELECT COUNT_BIG(*) FROM Geo.CityBlock WHERE NetworkCidr NOT LIKE '%:%'), 'CIDR ranges, IPv4 only'),
    ('Geo.ASN (MaxMind GeoLite2)',      (SELECT COUNT_BIG(*) FROM Geo.ASN WHERE NetworkCidr NOT LIKE '%:%'), 'CIDR ranges, IPv4 only'),
    ('Geo.CityLocation (MaxMind)',      (SELECT COUNT_BIG(*) FROM Geo.CityLocation), 'Geoname location details'),
    ('Ref.RirDelegation (5 RIRs)',      (SELECT COUNT_BIG(*) FROM Ref.RirDelegation), 'Authoritative IP allocations'),
    ('Ref.BgpToolsAsn (bgp.tools)',     (SELECT COUNT_BIG(*) FROM Ref.BgpToolsAsn), 'ASN classification'),
    ('Ref.DbipCityLite (DB-IP)',        (SELECT COUNT_BIG(*) FROM Ref.DbipCityLite), 'Free city-level geo ranges')
) AS v(src, rows, notes)
ORDER BY rows DESC;

PRINT '';

-- 2. RIR delegation by registry
PRINT '-- 2. RIR Delegation by Registry --';
SELECT Registry,
       COUNT(*) AS Allocations,
       SUM(CAST(EndInt - StartInt + 1 AS BIGINT)) AS TotalIPs,
       FORMAT(SUM(CAST(EndInt - StartInt + 1 AS BIGINT)), 'N0') AS TotalIPs_Fmt
FROM Ref.RirDelegation
GROUP BY Registry
ORDER BY TotalIPs DESC;

PRINT '';

-- 3. bgp.tools ASN class distribution
PRINT '-- 3. bgp.tools ASN Classification --';
SELECT Class,
       COUNT(*) AS AsnCount,
       FORMAT(COUNT(*), 'N0') AS AsnCount_Fmt
FROM Ref.BgpToolsAsn
GROUP BY Class
ORDER BY AsnCount DESC;

PRINT '';

-- 4. DB-IP continent distribution
PRINT '-- 4. DB-IP City Lite by Continent --';
SELECT Continent,
       COUNT(*) AS Ranges,
       SUM(CAST(EndInt - StartInt + 1 AS BIGINT)) AS TotalIPs,
       FORMAT(SUM(CAST(EndInt - StartInt + 1 AS BIGINT)), 'N0') AS TotalIPs_Fmt
FROM Ref.DbipCityLite
WHERE StartInt IS NOT NULL
GROUP BY Continent
ORDER BY TotalIPs DESC;

PRINT '';

-- 5. MaxMind vs DB-IP range count comparison
PRINT '-- 5. Range Granularity Comparison --';
SELECT 'MaxMind CityBlock' AS [Source], COUNT(*) AS IPv4Ranges FROM Geo.CityBlock WHERE NetworkCidr NOT LIKE '%:%'
UNION ALL
SELECT 'DB-IP City Lite', COUNT(*) FROM Ref.DbipCityLite WHERE StartInt IS NOT NULL
UNION ALL
SELECT 'MaxMind ASN', COUNT(*) FROM Geo.ASN WHERE NetworkCidr NOT LIKE '%:%'
UNION ALL
SELECT 'RIR Delegation', COUNT(*) FROM Ref.RirDelegation;

PRINT '';

-- 6. IPAPI.IP coverage by country (top 20)
PRINT '-- 6. IPAPI Top 20 Countries --';
SELECT TOP 20 CountryCode, COUNT_BIG(*) AS IPs
FROM IPAPI.IP
WHERE CountryCode IS NOT NULL AND CountryCode <> ''
GROUP BY CountryCode
ORDER BY IPs DESC;

PRINT '';
PRINT 'Source overview complete.';
