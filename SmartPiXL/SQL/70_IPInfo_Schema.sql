-- ============================================================================
-- 70_IPInfo_Schema.sql
-- Creates the IPInfo schema — a normalized, self-maintained IP enrichment
-- database built from free public data sources. Replaces the legacy IPAPI
-- schema (which depended on ip-api.com via Xavier IPGEO sync).
--
-- DATA SOURCES:
--   Tier 1 — Geo:       MaxMind GeoLite2 (.mmdb, in-memory), DB-IP Lite City,
--                        IP2Location LITE DB11
--   Tier 2 — ASN:       IPtoASN (PDDL), RIR Delegated (5 registries),
--                        BGP.tools ASN list
--   Tier 3 — Proxy:     IP2Proxy LITE PX11
--   Tier 4 — Datacenter: AWS/GCP/Azure/Cloudflare CIDR ranges
--   Tier 5 — GeoFeed:   OpenGeoFeed (RFC 8805, ISP self-corrections)
--
-- IP STORAGE:
--   All IP addresses stored as VARBINARY(16) in network byte order.
--   IPv4 = 4 bytes, IPv6 = 16 bytes. AddrFamily discriminator (4 or 6).
--   Helper functions convert to/from dotted-quad and integer formats.
--
-- LOOKUP PATTERN:
--   SELECT TOP 1 * FROM IPInfo.GeoRange
--   WHERE AddrFamily = 4 AND IpStart <= @ipBin AND IpEnd >= @ipBin
--   ORDER BY IpStart DESC;
--   → Clustered index seek O(log n), sub-millisecond.
--
-- IMPORT PATTERN:
--   Load → staging table → rebuild indexes → sp_rename swap (zero-downtime).
--
-- Prerequisites:
--   - SmartPiXL database
--
-- Safe to re-run: all DDL is idempotent.
-- ============================================================================

SET NOCOUNT ON;
PRINT '=== 70_IPInfo_Schema.sql ===';
PRINT '';

-- ============================================================================
-- SCHEMA
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'IPInfo')
BEGIN
    EXEC('CREATE SCHEMA IPInfo');
    PRINT '  Created schema IPInfo';
END
ELSE
    PRINT '  Schema IPInfo already exists — skipped';
GO

-- ============================================================================
-- HELPER FUNCTIONS
-- ============================================================================

PRINT '--- Helper Functions ---';
GO

-- Convert dotted-quad IPv4 string to 4-byte VARBINARY(16).
-- Uses PARSENAME which treats '.' as a separator.
-- Returns NULL for invalid input or IPv6 (handled separately).
CREATE OR ALTER FUNCTION IPInfo.fn_IpToBinary(@ip VARCHAR(45))
RETURNS VARBINARY(16)
WITH SCHEMABINDING
AS
BEGIN
    IF @ip IS NULL RETURN NULL;

    -- IPv6 detection: contains ':'
    IF CHARINDEX(':', @ip) > 0
        RETURN NULL;  -- IPv6 parsing requires CLR or extended logic

    -- IPv4: parse 4 octets via PARSENAME (treats '.' as separator)
    DECLARE @p4 VARCHAR(3) = PARSENAME(@ip, 4);  -- first octet
    DECLARE @p3 VARCHAR(3) = PARSENAME(@ip, 3);
    DECLARE @p2 VARCHAR(3) = PARSENAME(@ip, 2);
    DECLARE @p1 VARCHAR(3) = PARSENAME(@ip, 1);  -- last octet

    IF @p4 IS NULL OR @p3 IS NULL OR @p2 IS NULL OR @p1 IS NULL
        RETURN NULL;

    RETURN CAST(
        CAST(CAST(@p4 AS TINYINT) AS BINARY(1)) +
        CAST(CAST(@p3 AS TINYINT) AS BINARY(1)) +
        CAST(CAST(@p2 AS TINYINT) AS BINARY(1)) +
        CAST(CAST(@p1 AS TINYINT) AS BINARY(1))
    AS VARBINARY(16));
END;
GO

-- Convert 4-byte VARBINARY back to dotted-quad string.
CREATE OR ALTER FUNCTION IPInfo.fn_BinaryToIp(@bin VARBINARY(16))
RETURNS VARCHAR(45)
WITH SCHEMABINDING
AS
BEGIN
    IF @bin IS NULL RETURN NULL;

    IF DATALENGTH(@bin) = 4
        RETURN CAST(CAST(SUBSTRING(@bin, 1, 1) AS TINYINT) AS VARCHAR(3)) + '.' +
               CAST(CAST(SUBSTRING(@bin, 2, 1) AS TINYINT) AS VARCHAR(3)) + '.' +
               CAST(CAST(SUBSTRING(@bin, 3, 1) AS TINYINT) AS VARCHAR(3)) + '.' +
               CAST(CAST(SUBSTRING(@bin, 4, 1) AS TINYINT) AS VARCHAR(3));

    -- IPv6: 16 bytes → colon-hex (simplified, no :: compression)
    IF DATALENGTH(@bin) = 16
        RETURN CONVERT(VARCHAR(4), SUBSTRING(@bin,  1, 2), 2) + ':' +
               CONVERT(VARCHAR(4), SUBSTRING(@bin,  3, 2), 2) + ':' +
               CONVERT(VARCHAR(4), SUBSTRING(@bin,  5, 2), 2) + ':' +
               CONVERT(VARCHAR(4), SUBSTRING(@bin,  7, 2), 2) + ':' +
               CONVERT(VARCHAR(4), SUBSTRING(@bin,  9, 2), 2) + ':' +
               CONVERT(VARCHAR(4), SUBSTRING(@bin, 11, 2), 2) + ':' +
               CONVERT(VARCHAR(4), SUBSTRING(@bin, 13, 2), 2) + ':' +
               CONVERT(VARCHAR(4), SUBSTRING(@bin, 15, 2), 2);

    RETURN NULL;
END;
GO

-- Convert IP2Location/DB-IP integer format to 4-byte VARBINARY.
-- IP2Location stores IPv4 as unsigned 32-bit integer.
CREATE OR ALTER FUNCTION IPInfo.fn_IntToBinary(@ipInt BIGINT)
RETURNS VARBINARY(16)
WITH SCHEMABINDING
AS
BEGIN
    IF @ipInt IS NULL OR @ipInt < 0 OR @ipInt > 4294967295 RETURN NULL;
    RETURN CAST(CAST(@ipInt AS INT) AS BINARY(4));
END;
GO

PRINT '  Created helper functions: fn_IpToBinary, fn_BinaryToIp, fn_IntToBinary';
GO

-- ============================================================================
-- DIMENSION TABLES
-- ============================================================================

PRINT '--- Dimension Tables ---';

-- ── DataSource: Registry of all IP data sources ──────────────────────────
IF OBJECT_ID('IPInfo.DataSource', 'U') IS NULL
BEGIN
    CREATE TABLE IPInfo.DataSource
    (
        SourceId       TINYINT      NOT NULL,
        Name           VARCHAR(50)  NOT NULL,
        Url            VARCHAR(500) NULL,
        License        VARCHAR(100) NULL,
        UpdateFreq     VARCHAR(20)  NULL,

        CONSTRAINT PK_IPInfo_DataSource PRIMARY KEY CLUSTERED (SourceId)
    );

    INSERT INTO IPInfo.DataSource (SourceId, Name, Url, License, UpdateFreq) VALUES
    ( 1, 'MaxMind GeoLite2',  'https://dev.maxmind.com/geoip/geolite2/',             'CC BY-SA 4.0',  'Twice weekly'),
    ( 2, 'DB-IP Lite',        'https://db-ip.com/db/download/ip-to-city-lite',        'CC BY 4.0',     'Monthly'),
    ( 3, 'IP2Location LITE',  'https://lite.ip2location.com/',                        'CC BY-SA 4.0',  'Monthly'),
    ( 4, 'IPtoASN',           'https://iptoasn.com/',                                 'PDDL v1.0',     'Hourly'),
    ( 5, 'RIR Delegated',     'https://www.nro.net/about/rirs/',                      'Public',         'Daily'),
    ( 6, 'BGP.tools',         'https://bgp.tools/',                                   'Public',         'Periodic'),
    ( 7, 'IP2Proxy LITE',     'https://lite.ip2location.com/ip2proxy-lite',           'CC BY-SA 4.0',  'Bi-weekly'),
    ( 8, 'AWS IP Ranges',     'https://ip-ranges.amazonaws.com/ip-ranges.json',       'Public',         'Live'),
    ( 9, 'GCP IP Ranges',     'https://www.gstatic.com/ipranges/cloud.json',          'Public',         'Live'),
    (10, 'Azure IP Ranges',   'https://www.microsoft.com/en-us/download/details.aspx?id=56519', 'Public', 'Weekly'),
    (11, 'Cloudflare Ranges', 'https://www.cloudflare.com/ips/',                      'Public',         'Live'),
    (12, 'OpenGeoFeed',       'https://opengeofeed.org/',                             'Public',         'Daily');

    PRINT '  Created IPInfo.DataSource with 12 seed rows';
END
ELSE
    PRINT '  IPInfo.DataSource already exists — skipped';
GO

-- ── ASN: Autonomous System metadata ──────────────────────────────────────
-- Populated from BGP.tools (class), RIR delegated (registry, date), IPtoASN (org).
IF OBJECT_ID('IPInfo.ASN', 'U') IS NULL
BEGIN
    CREATE TABLE IPInfo.ASN
    (
        AsnNumber      INT          NOT NULL,
        Name           VARCHAR(256) NULL,
        Organization   VARCHAR(256) NULL,
        AsnClass       VARCHAR(20)  NULL,      -- Eyeball, Transit, Content, Enterprise, Unknown
        CountryCode    CHAR(2)      NULL,
        RegistryName   VARCHAR(10)  NULL,      -- ARIN, RIPE, APNIC, LACNIC, AFRINIC
        AssignedDate   DATE         NULL,
        SourceId       TINYINT      NOT NULL DEFAULT 6,

        CONSTRAINT PK_IPInfo_ASN PRIMARY KEY CLUSTERED (AsnNumber),
        CONSTRAINT FK_IPInfo_ASN_Source FOREIGN KEY (SourceId) REFERENCES IPInfo.DataSource(SourceId)
    );

    PRINT '  Created IPInfo.ASN';
END
ELSE
    PRINT '  IPInfo.ASN already exists — skipped';
GO

-- ── ImportLog: Audit trail for all data imports ──────────────────────────
-- Also used by CompanyPiXLSyncService (replaces IPAPI.SyncLog).
IF OBJECT_ID('IPInfo.ImportLog', 'U') IS NULL
BEGIN
    CREATE TABLE IPInfo.ImportLog
    (
        ImportId       INT           NOT NULL IDENTITY(1,1),
        SourceId       TINYINT       NULL,
        SyncType       VARCHAR(50)   NULL,     -- 'GeoRange', 'AsnRange', 'ProxyRange',
                                               -- 'DatacenterRange', 'ASN', 'Company', 'PiXL'
        StartedAt      DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CompletedAt    DATETIME2(3)  NULL,
        RowsImported   INT           NOT NULL DEFAULT 0,
        RowsUpdated    INT           NOT NULL DEFAULT 0,
        RowsDeleted    INT           NOT NULL DEFAULT 0,
        DurationMs     INT           NULL,
        FileHash       VARCHAR(64)   NULL,     -- SHA-256 of source file (skip-if-unchanged)
        FileName       VARCHAR(256)  NULL,
        ErrorMessage   VARCHAR(2000) NULL,

        CONSTRAINT PK_IPInfo_ImportLog PRIMARY KEY CLUSTERED (ImportId),
        CONSTRAINT FK_IPInfo_ImportLog_Source FOREIGN KEY (SourceId) REFERENCES IPInfo.DataSource(SourceId)
    );

    PRINT '  Created IPInfo.ImportLog';
END
ELSE
    PRINT '  IPInfo.ImportLog already exists — skipped';
GO

-- ============================================================================
-- RANGE TABLES — IP range → attribute lookups
-- ============================================================================
-- All range tables share the same structure:
--   IpStart/IpEnd: VARBINARY(16) in network byte order
--   AddrFamily: 4 (IPv4) or 6 (IPv6)
--   SourceId: FK to DataSource
--   Clustered index optimized for range lookup: (AddrFamily, IpStart)
--
-- Import pattern: load into _New table, rebuild indexes, sp_rename swap.
-- ============================================================================

PRINT '--- Range Tables ---';

-- ── GeoRange: IP range → geographic location ─────────────────────────────
-- Sources: DB-IP Lite City, IP2Location LITE DB11, OpenGeoFeed
IF OBJECT_ID('IPInfo.GeoRange', 'U') IS NULL
BEGIN
    CREATE TABLE IPInfo.GeoRange
    (
        IpStart        VARBINARY(16) NOT NULL,
        IpEnd          VARBINARY(16) NOT NULL,
        AddrFamily     TINYINT       NOT NULL,  -- 4 or 6
        SourceId       TINYINT       NOT NULL,
        CountryCode    CHAR(2)       NULL,
        Region         VARCHAR(128)  NULL,       -- state/province name
        RegionCode     VARCHAR(10)   NULL,       -- ISO 3166-2 subdivision code (e.g., "VA")
        City           VARCHAR(128)  NULL,
        PostalCode     VARCHAR(20)   NULL,
        Latitude       DECIMAL(9,6)  NULL,
        Longitude      DECIMAL(9,6)  NULL,
        Timezone       VARCHAR(64)   NULL,
        Continent      CHAR(2)       NULL,

        CONSTRAINT PK_IPInfo_GeoRange PRIMARY KEY NONCLUSTERED (IpStart, SourceId),
        CONSTRAINT FK_IPInfo_GeoRange_Source FOREIGN KEY (SourceId) REFERENCES IPInfo.DataSource(SourceId)
    );

    -- Clustered index for range lookup: seek to IpStart <= @ip, verify IpEnd >= @ip
    CREATE CLUSTERED INDEX IX_IPInfo_GeoRange_Lookup
        ON IPInfo.GeoRange (AddrFamily, IpStart)
        INCLUDE (IpEnd);

    -- Filtered index for IPv4-only lookups (the hot path)
    CREATE NONCLUSTERED INDEX IX_IPInfo_GeoRange_V4_Lookup
        ON IPInfo.GeoRange (IpStart, IpEnd)
        INCLUDE (CountryCode, Region, RegionCode, City, PostalCode, Latitude, Longitude, Timezone)
        WHERE AddrFamily = 4;

    PRINT '  Created IPInfo.GeoRange with range-lookup indexes';
END
ELSE
    PRINT '  IPInfo.GeoRange already exists — skipped';
GO

-- ── AsnRange: IP range → ASN assignment ──────────────────────────────────
-- Sources: IPtoASN (primary), RIR Delegated (supplemental)
IF OBJECT_ID('IPInfo.AsnRange', 'U') IS NULL
BEGIN
    CREATE TABLE IPInfo.AsnRange
    (
        IpStart        VARBINARY(16) NOT NULL,
        IpEnd          VARBINARY(16) NOT NULL,
        AddrFamily     TINYINT       NOT NULL,
        SourceId       TINYINT       NOT NULL,
        AsnNumber      INT           NOT NULL,
        CountryCode    CHAR(2)       NULL,
        AsnDescription VARCHAR(256)  NULL,      -- from IPtoASN inline description

        CONSTRAINT PK_IPInfo_AsnRange PRIMARY KEY NONCLUSTERED (IpStart, SourceId),
        CONSTRAINT FK_IPInfo_AsnRange_Source FOREIGN KEY (SourceId) REFERENCES IPInfo.DataSource(SourceId),
        CONSTRAINT FK_IPInfo_AsnRange_ASN FOREIGN KEY (AsnNumber) REFERENCES IPInfo.ASN(AsnNumber)
    );

    CREATE CLUSTERED INDEX IX_IPInfo_AsnRange_Lookup
        ON IPInfo.AsnRange (AddrFamily, IpStart)
        INCLUDE (IpEnd);

    PRINT '  Created IPInfo.AsnRange with range-lookup indexes';
END
ELSE
    PRINT '  IPInfo.AsnRange already exists — skipped';
GO

-- ── ProxyRange: IP range → proxy/VPN classification ──────────────────────
-- Source: IP2Proxy LITE PX11
IF OBJECT_ID('IPInfo.ProxyRange', 'U') IS NULL
BEGIN
    CREATE TABLE IPInfo.ProxyRange
    (
        IpStart        VARBINARY(16) NOT NULL,
        IpEnd          VARBINARY(16) NOT NULL,
        AddrFamily     TINYINT       NOT NULL,
        SourceId       TINYINT       NOT NULL DEFAULT 7,
        ProxyType      VARCHAR(3)    NULL,      -- PUB, VPN, DCH, SES, RES, CPN, EPN
        CountryCode    CHAR(2)       NULL,
        Region         VARCHAR(128)  NULL,
        City           VARCHAR(128)  NULL,
        ISP            VARCHAR(256)  NULL,
        Domain         VARCHAR(256)  NULL,
        UsageType      VARCHAR(11)   NULL,      -- COM, ORG, GOV, EDU, MIL, ISP, CDN, DCH, SES, RSV
        Threat         VARCHAR(128)  NULL,
        IsResidential  BIT           NULL,
        Provider       VARCHAR(256)  NULL,
        LastSeen       DATE          NULL,

        CONSTRAINT PK_IPInfo_ProxyRange PRIMARY KEY NONCLUSTERED (IpStart, SourceId),
        CONSTRAINT FK_IPInfo_ProxyRange_Source FOREIGN KEY (SourceId) REFERENCES IPInfo.DataSource(SourceId)
    );

    CREATE CLUSTERED INDEX IX_IPInfo_ProxyRange_Lookup
        ON IPInfo.ProxyRange (AddrFamily, IpStart)
        INCLUDE (IpEnd);

    PRINT '  Created IPInfo.ProxyRange with range-lookup indexes';
END
ELSE
    PRINT '  IPInfo.ProxyRange already exists — skipped';
GO

-- ── DatacenterRange: IP range → cloud/datacenter provider ────────────────
-- Sources: AWS, GCP, Azure, Cloudflare CIDR ranges
IF OBJECT_ID('IPInfo.DatacenterRange', 'U') IS NULL
BEGIN
    CREATE TABLE IPInfo.DatacenterRange
    (
        IpStart        VARBINARY(16) NOT NULL,
        IpEnd          VARBINARY(16) NOT NULL,
        AddrFamily     TINYINT       NOT NULL,
        SourceId       TINYINT       NOT NULL,
        Provider       VARCHAR(20)   NOT NULL,  -- AWS, GCP, Azure, Cloudflare
        Region         VARCHAR(64)   NULL,       -- cloud region (us-east-1, etc)
        Service        VARCHAR(64)   NULL,       -- cloud service (EC2, S3, etc)

        CONSTRAINT PK_IPInfo_DatacenterRange PRIMARY KEY NONCLUSTERED (IpStart, SourceId),
        CONSTRAINT FK_IPInfo_DatacenterRange_Source FOREIGN KEY (SourceId) REFERENCES IPInfo.DataSource(SourceId)
    );

    CREATE CLUSTERED INDEX IX_IPInfo_DatacenterRange_Lookup
        ON IPInfo.DatacenterRange (AddrFamily, IpStart)
        INCLUDE (IpEnd);

    PRINT '  Created IPInfo.DatacenterRange with range-lookup indexes';
END
ELSE
    PRINT '  IPInfo.DatacenterRange already exists — skipped';
GO

-- ============================================================================
-- LOOKUP PROCEDURE — Consolidated IP lookup across all range tables
-- ============================================================================

PRINT '--- Lookup Procedure ---';
GO

CREATE OR ALTER PROCEDURE IPInfo.usp_LookupIp
    @IpAddress VARCHAR(45)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ipBin VARBINARY(16) = IPInfo.fn_IpToBinary(@IpAddress);
    IF @ipBin IS NULL
    BEGIN
        SELECT 'InvalidIP' AS Status, @IpAddress AS IpAddress;
        RETURN;
    END

    DECLARE @af TINYINT = CASE WHEN DATALENGTH(@ipBin) = 4 THEN 4 ELSE 6 END;

    -- Private/reserved range check
    DECLARE @octet1 TINYINT = CAST(SUBSTRING(@ipBin, 1, 1) AS TINYINT);
    DECLARE @octet2 TINYINT = CAST(SUBSTRING(@ipBin, 2, 1) AS TINYINT);
    IF @af = 4 AND (
        @octet1 = 10                                          -- 10.0.0.0/8
        OR (@octet1 = 172 AND @octet2 BETWEEN 16 AND 31)     -- 172.16.0.0/12
        OR (@octet1 = 192 AND @octet2 = 168)                 -- 192.168.0.0/16
        OR @octet1 = 127                                      -- 127.0.0.0/8
        OR @octet1 = 0                                        -- 0.0.0.0/8
    )
    BEGIN
        SELECT 'PrivateRange' AS Status, @IpAddress AS IpAddress;
        RETURN;
    END

    -- Geo lookup
    SELECT TOP 1
        'Success'   AS Status,
        @IpAddress  AS IpAddress,
        g.CountryCode, g.Region, g.RegionCode, g.City,
        g.PostalCode, g.Latitude, g.Longitude, g.Timezone, g.Continent,
        a.AsnNumber, a.AsnDescription,
        asn.Name AS AsnName, asn.AsnClass, asn.Organization AS AsnOrg,
        p.ProxyType, p.ISP, p.UsageType, p.Threat, p.IsResidential, p.Provider AS ProxyProvider,
        dc.Provider AS DatacenterProvider, dc.Region AS DatacenterRegion, dc.Service AS DatacenterService
    FROM (SELECT 1 AS _) AS dummy
    OUTER APPLY (
        SELECT TOP 1 *
        FROM IPInfo.GeoRange
        WHERE AddrFamily = @af AND IpStart <= @ipBin AND IpEnd >= @ipBin
        ORDER BY IpStart DESC
    ) g
    OUTER APPLY (
        SELECT TOP 1 *
        FROM IPInfo.AsnRange
        WHERE AddrFamily = @af AND IpStart <= @ipBin AND IpEnd >= @ipBin
        ORDER BY IpStart DESC
    ) a
    LEFT JOIN IPInfo.ASN asn ON a.AsnNumber = asn.AsnNumber
    OUTER APPLY (
        SELECT TOP 1 *
        FROM IPInfo.ProxyRange
        WHERE AddrFamily = @af AND IpStart <= @ipBin AND IpEnd >= @ipBin
        ORDER BY IpStart DESC
    ) p
    OUTER APPLY (
        SELECT TOP 1 *
        FROM IPInfo.DatacenterRange
        WHERE AddrFamily = @af AND IpStart <= @ipBin AND IpEnd >= @ipBin
        ORDER BY IpStart DESC
    ) dc;
END;
GO

PRINT '  Created IPInfo.usp_LookupIp';
GO

-- ============================================================================
-- ETL ENRICHMENT — Replaces IPAPI.IP JOIN in ETL.usp_EnrichParsedGeo
-- ============================================================================

PRINT '--- ETL Enrichment Procedure ---';
GO

CREATE OR ALTER PROCEDURE IPInfo.usp_EnrichGeo
    @BatchSize INT = 10000
AS
BEGIN
    SET NOCOUNT ON;

    -- ======================================================================
    -- Step 1: Enrich PiXL.Parsed from IPInfo.GeoRange
    -- Targets rows where GeoCountry IS NULL.
    -- Uses MaxMind _srv_mm* query params first (already populated by Forge),
    -- falls back to IPInfo.GeoRange for any gaps.
    -- ======================================================================
    DECLARE @ParsedEnriched INT = 0;

    -- 1a: Fill from _srv_mm* params (Forge already populated these inline)
    UPDATE TOP (@BatchSize) pp SET
        pp.GeoCountryCode = dbo.GetQueryParam(src.QueryString, '_srv_mmCC'),
        pp.GeoRegion      = dbo.GetQueryParam(src.QueryString, '_srv_mmReg'),
        pp.GeoCity        = dbo.GetQueryParam(src.QueryString, '_srv_mmCity'),
        pp.GeoZip         = dbo.GetQueryParam(src.QueryString, '_srv_mmZip'),
        pp.GeoLat         = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_mmLat') AS DECIMAL(9,4)),
        pp.GeoLon         = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_mmLon') AS DECIMAL(9,4)),
        pp.GeoTimezone    = dbo.GetQueryParam(src.QueryString, '_srv_mmTZ'),
        pp.GeoISP         = NULL,  -- No ISP from MaxMind; filled in Step 1c
        pp.GeoTzMismatch  = CASE
            WHEN pp.Timezone IS NOT NULL
                 AND dbo.GetQueryParam(src.QueryString, '_srv_mmTZ') IS NOT NULL
                 AND pp.Timezone <> dbo.GetQueryParam(src.QueryString, '_srv_mmTZ')
            THEN 1 ELSE 0
        END
    FROM PiXL.Parsed pp
    INNER JOIN PiXL.Raw src ON pp.SourceId = src.Id
    WHERE pp.GeoCountryCode IS NULL
      AND src.QueryString LIKE '%_srv_mmCC=%';

    SET @ParsedEnriched = @@ROWCOUNT;

    -- 1b: Fallback to IPInfo.GeoRange for rows without _srv_mm* params
    DECLARE @GeoFallback INT = 0;

    UPDATE TOP (@BatchSize) pp SET
        pp.GeoCountryCode = g.CountryCode,
        pp.GeoRegion      = g.Region,
        pp.GeoCity        = g.City,
        pp.GeoZip         = g.PostalCode,
        pp.GeoLat         = g.Latitude,
        pp.GeoLon         = g.Longitude,
        pp.GeoTimezone    = g.Timezone
    FROM PiXL.Parsed pp
    CROSS APPLY (
        SELECT TOP 1 CountryCode, Region, City, PostalCode, Latitude, Longitude, Timezone
        FROM IPInfo.GeoRange gr
        WHERE gr.AddrFamily = 4
          AND gr.IpStart <= IPInfo.fn_IpToBinary(pp.IPAddress)
          AND gr.IpEnd   >= IPInfo.fn_IpToBinary(pp.IPAddress)
        ORDER BY gr.IpStart DESC
    ) g
    WHERE pp.GeoCountryCode IS NULL
      AND pp.IPAddress IS NOT NULL
      AND CHARINDEX(':', pp.IPAddress) = 0;  -- IPv4 only for now

    SET @GeoFallback = @@ROWCOUNT;

    -- 1c: ISP enrichment from IPInfo.AsnRange + IPInfo.ASN
    DECLARE @IspEnriched INT = 0;

    UPDATE TOP (@BatchSize) pp SET
        pp.GeoISP = COALESCE(asn.Organization, a.AsnDescription)
    FROM PiXL.Parsed pp
    CROSS APPLY (
        SELECT TOP 1 AsnNumber, AsnDescription
        FROM IPInfo.AsnRange ar
        WHERE ar.AddrFamily = 4
          AND ar.IpStart <= IPInfo.fn_IpToBinary(pp.IPAddress)
          AND ar.IpEnd   >= IPInfo.fn_IpToBinary(pp.IPAddress)
        ORDER BY ar.IpStart DESC
    ) a
    LEFT JOIN IPInfo.ASN asn ON a.AsnNumber = asn.AsnNumber
    WHERE pp.GeoISP IS NULL
      AND pp.IPAddress IS NOT NULL
      AND CHARINDEX(':', pp.IPAddress) = 0;

    SET @IspEnriched = @@ROWCOUNT;

    -- ======================================================================
    -- Step 2: Enrich PiXL.IP from IPInfo range tables
    -- ======================================================================
    DECLARE @IpEnriched INT = 0;

    UPDATE TOP (@BatchSize) pip SET
        pip.GeoCountryCode = g.CountryCode,
        pip.GeoCountry     = g.CountryCode,  -- placeholder; full name from dimension table later
        pip.GeoRegion      = g.Region,
        pip.GeoCity        = g.City,
        pip.GeoZip         = g.PostalCode,
        pip.GeoLat         = g.Latitude,
        pip.GeoLon         = g.Longitude,
        pip.GeoTimezone    = g.Timezone,
        pip.GeoISP         = COALESCE(asn.Organization, a.AsnDescription),
        pip.GeoOrg         = asn.Name,
        pip.GeoIsProxy     = CASE WHEN p.ProxyType IS NOT NULL THEN 1 ELSE 0 END,
        pip.GeoIsMobile    = NULL,  -- No free source for mobile flag
        pip.GeoLastUpdated = SYSUTCDATETIME()
    FROM PiXL.IP pip
    CROSS APPLY (
        SELECT TOP 1 CountryCode, Region, City, PostalCode, Latitude, Longitude, Timezone
        FROM IPInfo.GeoRange gr
        WHERE gr.AddrFamily = 4
          AND gr.IpStart <= IPInfo.fn_IpToBinary(pip.IPAddress)
          AND gr.IpEnd   >= IPInfo.fn_IpToBinary(pip.IPAddress)
        ORDER BY gr.IpStart DESC
    ) g
    OUTER APPLY (
        SELECT TOP 1 AsnNumber, AsnDescription
        FROM IPInfo.AsnRange ar
        WHERE ar.AddrFamily = 4
          AND ar.IpStart <= IPInfo.fn_IpToBinary(pip.IPAddress)
          AND ar.IpEnd   >= IPInfo.fn_IpToBinary(pip.IPAddress)
        ORDER BY ar.IpStart DESC
    ) a
    LEFT JOIN IPInfo.ASN asn ON a.AsnNumber = asn.AsnNumber
    OUTER APPLY (
        SELECT TOP 1 ProxyType
        FROM IPInfo.ProxyRange pr
        WHERE pr.AddrFamily = 4
          AND pr.IpStart <= IPInfo.fn_IpToBinary(pip.IPAddress)
          AND pr.IpEnd   >= IPInfo.fn_IpToBinary(pip.IPAddress)
        ORDER BY pr.IpStart DESC
    ) p
    WHERE pip.GeoCountry IS NULL;

    SET @IpEnriched = @@ROWCOUNT;

    SELECT @ParsedEnriched AS ParsedFromMaxMind,
           @GeoFallback AS ParsedFromGeoRange,
           @IspEnriched AS ParsedIspEnriched,
           @IpEnriched AS IpEnriched;
END;
GO

PRINT '  Created IPInfo.usp_EnrichGeo';
GO

-- ============================================================================
-- IMPORT PROCEDURES — Bulk-load from CSV/TSV files
-- ============================================================================

PRINT '--- Import Procedures ---';
GO

-- Atomic table swap: rename current → _Old, staging → current, drop _Old.
CREATE OR ALTER PROCEDURE IPInfo.usp_SwapTable
    @SchemaName  VARCHAR(20),
    @TableName   VARCHAR(50),
    @StagingName VARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @fullCurrent VARCHAR(100) = @SchemaName + '.' + @TableName;
    DECLARE @fullStaging VARCHAR(100) = @SchemaName + '.' + @StagingName;
    DECLARE @fullOld     VARCHAR(100) = @SchemaName + '.' + @TableName + '_Old';
    DECLARE @oldName     VARCHAR(50) = @TableName + '_Old';

    -- Drop _Old if leftover from failed swap
    IF OBJECT_ID(@fullOld, 'U') IS NOT NULL
        EXEC('DROP TABLE ' + @fullOld);

    -- Rename current → _Old
    IF OBJECT_ID(@fullCurrent, 'U') IS NOT NULL
        EXEC sp_rename @fullCurrent, @oldName;

    -- Rename staging → current
    EXEC sp_rename @fullStaging, @TableName;

    -- Drop _Old
    IF OBJECT_ID(@fullOld, 'U') IS NOT NULL
        EXEC('DROP TABLE ' + @fullOld);
END;
GO

PRINT '  Created IPInfo.usp_SwapTable';
PRINT '';
PRINT '=== 70_IPInfo_Schema.sql complete ===';
