-- ====================================================================
-- 61_AuthoritativeIpSources.sql
-- Creates Ref schema + tables for free authoritative IP reference data
-- Run: sqlcmd -S localhost\SQL2025 -d SmartPiXL -i 61_AuthoritativeIpSources.sql
-- ====================================================================
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ============================================================
-- Schema
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'Ref')
    EXEC('CREATE SCHEMA Ref');
GO

-- ============================================================
-- Ref.ImportLog — tracks when each source was last refreshed
-- ============================================================
IF OBJECT_ID('Ref.ImportLog', 'U') IS NULL
CREATE TABLE Ref.ImportLog (
    ImportId        INT IDENTITY(1,1) PRIMARY KEY,
    SourceName      VARCHAR(50) NOT NULL,       -- 'RIR', 'BgpToolsAsn', 'Ip2Location', 'DbipCityLite', 'Geofeed'
    ImportedAt      DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
    RowsLoaded      INT NOT NULL DEFAULT 0,
    RowsReplaced    INT NOT NULL DEFAULT 0,     -- rows in table before truncate
    DurationMs      INT NULL,
    Notes           VARCHAR(500) NULL
);
GO

-- ============================================================
-- Ref.RirDelegation — RIR country allocation (ARIN/RIPE/APNIC/LACNIC/AFRINIC)
-- Source: ftp://{registry}/pub/stats/{registry}/delegated-{registry}-extended-latest
-- Format: registry|CC|ipv4|startIp|hostCount|date|status|opaqueId
-- ~300K IPv4 rows across all 5 registries
-- ============================================================
IF OBJECT_ID('Ref.RirDelegation', 'U') IS NULL
CREATE TABLE Ref.RirDelegation (
    Registry        VARCHAR(10)  NOT NULL,      -- arin, ripencc, apnic, lacnic, afrinic
    CountryCode     CHAR(2)      NOT NULL,      -- ISO 3166-1 alpha-2
    StartIp         VARCHAR(15)  NOT NULL,      -- Dotted-decimal range start
    HostCount       INT          NOT NULL,       -- Number of hosts in block (NOT prefix length)
    StartInt        BIGINT       NOT NULL,       -- Computed: IP as integer for range joins
    EndInt          BIGINT       NOT NULL,       -- Computed: StartInt + HostCount - 1
    DateAllocated   CHAR(8)      NULL,           -- YYYYMMDD or 00000000
    Status          VARCHAR(20)  NOT NULL,       -- allocated, assigned, reserved, available
    OpaqueId        VARCHAR(50)  NULL            -- Opaque registrant identifier
);
GO

-- Clustered on StartInt for range lookups
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('Ref.RirDelegation') AND name = 'CIX_RirDelegation_StartInt')
    CREATE CLUSTERED INDEX CIX_RirDelegation_StartInt ON Ref.RirDelegation (StartInt);
GO

-- Covering index for country lookups by range
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('Ref.RirDelegation') AND name = 'IX_RirDelegation_Range')
    CREATE NONCLUSTERED INDEX IX_RirDelegation_Range ON Ref.RirDelegation (StartInt, EndInt) INCLUDE (CountryCode, Registry, Status);
GO

-- ============================================================
-- Ref.BgpToolsAsn — ASN classification from bgp.tools
-- Source: https://bgp.tools/asns.csv
-- Format: asn,name,class,cc
-- ~120K rows, class = Eyeball/Transit/Content/Unknown
-- ============================================================
IF OBJECT_ID('Ref.BgpToolsAsn', 'U') IS NULL
CREATE TABLE Ref.BgpToolsAsn (
    Asn             INT          NOT NULL PRIMARY KEY,
    Name            VARCHAR(300) NOT NULL,
    Class           VARCHAR(20)  NOT NULL,       -- Eyeball, Transit, Content, Unknown
    CountryCode     CHAR(2)      NULL             -- Registration country (from bgp.tools)
);
GO

-- ============================================================
-- Ref.Ip2LocationRange — IP2Location LITE DB11
-- Source: https://lite.ip2location.com/ (free registration required)
-- Format: CSV with IP ranges as integers, country, region, city, lat, lon, zip, timezone
-- ~8M IPv4 rows
-- ============================================================
IF OBJECT_ID('Ref.Ip2LocationRange', 'U') IS NULL
CREATE TABLE Ref.Ip2LocationRange (
    StartInt        BIGINT       NOT NULL,       -- IP range start as integer
    EndInt          BIGINT       NOT NULL,       -- IP range end as integer
    CountryCode     CHAR(2)      NOT NULL,
    Country         VARCHAR(100) NOT NULL,
    Region          VARCHAR(150) NOT NULL,
    City            VARCHAR(150) NOT NULL,
    Latitude        DECIMAL(9,6) NOT NULL,
    Longitude       DECIMAL(9,6) NOT NULL,
    Zip             VARCHAR(30)  NOT NULL,
    Timezone        VARCHAR(50)  NOT NULL        -- UTC offset format e.g. "+08:00" (not IANA)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('Ref.Ip2LocationRange') AND name = 'CIX_Ip2Location_StartInt')
    CREATE CLUSTERED INDEX CIX_Ip2Location_StartInt ON Ref.Ip2LocationRange (StartInt);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('Ref.Ip2LocationRange') AND name = 'IX_Ip2Location_Range')
    CREATE NONCLUSTERED INDEX IX_Ip2Location_Range ON Ref.Ip2LocationRange (StartInt, EndInt) INCLUDE (CountryCode, City, Zip, Timezone);
GO

-- ============================================================
-- Ref.DbipCityLite — DB-IP free city database
-- Source: https://download.db-ip.com/free/dbip-city-lite-{YYYY}-{MM}.csv.gz
-- Format: startIp,endIp,continent,countryCode,region,city,latitude,longitude
-- ~8M rows, no registration required, CC BY 4.0
-- ============================================================
IF OBJECT_ID('Ref.DbipCityLite', 'U') IS NULL
CREATE TABLE Ref.DbipCityLite (
    StartIp         VARCHAR(45)  NOT NULL,       -- Dotted-decimal (IPv4) or hex (IPv6)
    EndIp           VARCHAR(45)  NOT NULL,
    StartInt        BIGINT       NULL,            -- Computed for IPv4, NULL for IPv6
    EndInt          BIGINT       NULL,
    Continent       CHAR(2)      NOT NULL,        -- AF, AN, AS, EU, NA, OC, SA
    CountryCode     CHAR(2)      NOT NULL,
    Region          VARCHAR(150) NOT NULL,
    City            VARCHAR(150) NOT NULL,
    Latitude        DECIMAL(9,6) NOT NULL,
    Longitude       DECIMAL(9,6) NOT NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('Ref.DbipCityLite') AND name = 'CIX_DbipCityLite_StartInt')
    CREATE CLUSTERED INDEX CIX_DbipCityLite_StartInt ON Ref.DbipCityLite (StartInt);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('Ref.DbipCityLite') AND name = 'IX_DbipCityLite_Range')
    CREATE NONCLUSTERED INDEX IX_DbipCityLite_Range ON Ref.DbipCityLite (StartInt, EndInt) INCLUDE (CountryCode, City, Region);
GO

-- ============================================================
-- Ref.Geofeed — RFC 8805 operator-declared IP locations
-- Source: Aggregated from RIPE/ARIN WHOIS geofeed references
-- Format: ip_prefix,country_code,region,city,zip
-- Sparse but authoritative — network operators declaring their own IP locations
-- ============================================================
IF OBJECT_ID('Ref.Geofeed', 'U') IS NULL
CREATE TABLE Ref.Geofeed (
    NetworkCidr     VARCHAR(50)  NOT NULL,       -- CIDR notation e.g. "1.0.0.0/24"
    StartInt        BIGINT       NOT NULL,
    EndInt          BIGINT       NOT NULL,
    CountryCode     CHAR(2)      NOT NULL,
    Region          VARCHAR(100) NULL,            -- ISO 3166-2 subdivision or free text
    City            VARCHAR(100) NULL,
    Zip             VARCHAR(30)  NULL,
    Source          VARCHAR(200) NULL              -- Which geofeed file/operator
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('Ref.Geofeed') AND name = 'CIX_Geofeed_StartInt')
    CREATE CLUSTERED INDEX CIX_Geofeed_StartInt ON Ref.Geofeed (StartInt);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('Ref.Geofeed') AND name = 'IX_Geofeed_Range')
    CREATE NONCLUSTERED INDEX IX_Geofeed_Range ON Ref.Geofeed (StartInt, EndInt) INCLUDE (CountryCode, Region, City);
GO

-- ============================================================
-- Summary
-- ============================================================
PRINT 'Ref schema + 6 tables created:';
PRINT '  Ref.ImportLog        — import audit trail';
PRINT '  Ref.RirDelegation    — RIR country allocations (~300K rows)';
PRINT '  Ref.BgpToolsAsn      — ASN classification (~120K rows)';
PRINT '  Ref.Ip2LocationRange — IP2Location LITE DB11 (~8M rows)';
PRINT '  Ref.DbipCityLite     — DB-IP city database (~8M rows)';
PRINT '  Ref.Geofeed          — RFC 8805 operator geofeeds (sparse)';
PRINT '';
PRINT 'Next: Run import scripts from Research/imports/ to populate.';
GO
