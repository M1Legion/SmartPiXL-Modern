/*
    19_DeviceIpVisitMatchTables.sql
    ================================
    Creates the 4 normalized dimension/fact tables for the MVP ETL pipeline:
      - PiXL.Device   (global device dimension — platform-wide)
      - PiXL.IP       (global IP dimension — platform-wide)
      - PiXL.Visit    (fact table, 1:1 with PiXL.Parsed)
      - PiXL.Match    (identity resolution output)
    
    Also creates ETL.MatchWatermark for the match proc's independent watermark.
    
    PREREQUISITES:
      - SQL/17B_CreateSchemas.sql   (PiXL and ETL schemas must exist)
      - SQL/18_CompanyAndPiXLTables.sql  (PiXL.Company and PiXL.Pixel must exist)
    
    TARGET: SQL Server 2025 (17.0.1050.2) — uses native json data type and CREATE JSON INDEX.
    
    Run on: SmartPiXL database, localhost\SQL2025
    Date:   2026-02-14
*/

USE SmartPiXL;
GO

SET NOCOUNT ON;
GO

PRINT '=== Phase 1: PiXL.Device (Global Device Dimension) ===';
GO

IF OBJECT_ID('PiXL.Device', 'U') IS NULL
BEGIN
    CREATE TABLE PiXL.Device
    (
        DeviceId          BIGINT          NOT NULL  IDENTITY(1,1),
        DeviceHash        VARBINARY(32)   NOT NULL,
        FirstSeen         DATETIME2(3)    NOT NULL  DEFAULT SYSUTCDATETIME(),
        LastSeen          DATETIME2(3)    NOT NULL  DEFAULT SYSUTCDATETIME(),
        HitCount          INT             NOT NULL  DEFAULT 1,

        -- Nonclustered PK on surrogate key (for FK references from PiXL.Visit/Match)
        CONSTRAINT PK_PiXL_Device PRIMARY KEY NONCLUSTERED (DeviceId),

        -- Clustered on natural key — MERGE target seeks by DeviceHash
        CONSTRAINT UQ_PiXL_Device_Hash UNIQUE CLUSTERED (DeviceHash)
    );

    PRINT '  Created PiXL.Device';
END
ELSE
    PRINT '  PiXL.Device already exists — skipped';
GO


PRINT '=== Phase 2: PiXL.IP (Global IP Dimension) ===';
GO

IF OBJECT_ID('PiXL.IP', 'U') IS NULL
BEGIN
    CREATE TABLE PiXL.IP
    (
        IpId                BIGINT          NOT NULL  IDENTITY(1,1),
        IPAddress           VARCHAR(50)     NOT NULL,
        IpType              VARCHAR(20)     NULL,       -- Public/Private/CGNAT/Loopback/etc.
        IsDatacenter        BIT             NULL,       -- From DatacenterIpService
        DatacenterProvider  VARCHAR(20)     NULL,       -- AWS/GCP/NULL
        FirstSeen           DATETIME2(3)    NOT NULL  DEFAULT SYSUTCDATETIME(),
        LastSeen            DATETIME2(3)    NOT NULL  DEFAULT SYSUTCDATETIME(),
        HitCount            INT             NOT NULL  DEFAULT 1,

        -- Nonclustered PK on surrogate key (for FK references)
        CONSTRAINT PK_PiXL_IP PRIMARY KEY NONCLUSTERED (IpId),

        -- Clustered on natural key — MERGE target seeks by IPAddress
        CONSTRAINT UQ_PiXL_IP_Address UNIQUE CLUSTERED (IPAddress)
    );

    PRINT '  Created PiXL.IP';
END
ELSE
    PRINT '  PiXL.IP already exists — skipped';
GO


PRINT '=== Phase 3: PiXL.Visit (Fact Table — 1:1 with PiXL.Parsed) ===';
GO

IF OBJECT_ID('PiXL.Visit', 'U') IS NULL
BEGIN
    CREATE TABLE PiXL.Visit
    (
        -- PK — same value as PiXL.Parsed.SourceId / PiXL.Test.Id (single chain of identity)
        VisitID           BIGINT          NOT NULL,
        CompanyID         INT             NOT NULL,       -- Denormalized for partitioning/querying
        PiXLID            INT             NOT NULL,       -- Denormalized
        DeviceId          BIGINT          NULL,           -- FK → PiXL.Device
        IpId              BIGINT          NULL,           -- FK → PiXL.IP
        ReceivedAt        DATETIME2(3)    NOT NULL,       -- Denormalized for time queries

        -- SQL Server 2025 native json type — pre-parsed binary storage,
        -- built-in validation, ~30% smaller than NVARCHAR, enables CREATE JSON INDEX.
        -- Contains extracted _cp_* params as a JSON object.
        ClientParamsJson  JSON            NULL,

        -- Regular column (NOT computed) — populated by ETL Phase 12 via
        -- JSON_VALUE(ClientParamsJson, '$.email'). Must be a regular column
        -- because SQL Server prohibits filtered indexes on computed columns (Msg 10609).
        MatchEmail        NVARCHAR(200)   NULL,

        CreatedAt         DATETIME2(3)    NOT NULL  DEFAULT SYSUTCDATETIME(),

        -- Clustered PK on VisitID — required for CREATE JSON INDEX (which demands
        -- a clustered primary key). VisitID is monotonically increasing (same identity
        -- chain as PiXL.Test.Id), so insert order is inherently time-ordered.
        CONSTRAINT PK_PiXL_Visit PRIMARY KEY CLUSTERED (VisitID),

        -- Foreign keys to dimension tables
        CONSTRAINT FK_Visit_Device FOREIGN KEY (DeviceId) REFERENCES PiXL.Device(DeviceId),
        CONSTRAINT FK_Visit_IP     FOREIGN KEY (IpId)     REFERENCES PiXL.IP(IpId)
    );

    -- Time-based lookups (nonclustered — clustered is on VisitID for JSON INDEX)
    CREATE NONCLUSTERED INDEX IX_PiXL_Visit_ReceivedAt
        ON PiXL.Visit (ReceivedAt, VisitID);

    -- Company + PiXL + time — most common query filter
    CREATE NONCLUSTERED INDEX IX_PiXL_Visit_Company
        ON PiXL.Visit (CompanyID, PiXLID, ReceivedAt);

    -- Device lookups (filtered — skip NULL DeviceId rows)
    CREATE NONCLUSTERED INDEX IX_PiXL_Visit_Device
        ON PiXL.Visit (DeviceId)
        WHERE DeviceId IS NOT NULL;

    -- IP lookups (filtered — skip NULL IpId rows)
    CREATE NONCLUSTERED INDEX IX_PiXL_Visit_IP
        ON PiXL.Visit (IpId)
        WHERE IpId IS NOT NULL;

    -- MatchEmail filtered index — enables the match proc's watermark scan
    -- to seek only rows with email addresses. This is a regular column (not computed),
    -- which is required because SQL Server forbids filtered indexes on computed columns.
    CREATE NONCLUSTERED INDEX IX_PiXL_Visit_MatchEmail
        ON PiXL.Visit (MatchEmail)
        INCLUDE (VisitID, CompanyID, PiXLID, DeviceId, IpId, ReceivedAt)
        WHERE MatchEmail IS NOT NULL;

    PRINT '  Created PiXL.Visit with 6 indexes (PK + 5 nonclustered)';
END
ELSE
    PRINT '  PiXL.Visit already exists — skipped';
GO

-- Native JSON Index — requires:
--   1. The column to be json type (not NVARCHAR)
--   2. A clustered PRIMARY KEY on the table (not just a clustered index)
-- Enables indexed seeks for JSON_VALUE/JSON_PATH_EXISTS/JSON_CONTAINS queries
-- on the targeted paths. One index covers multiple JSON paths.
IF OBJECT_ID('PiXL.Visit', 'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes 
       WHERE object_id = OBJECT_ID('PiXL.Visit') AND name = 'IX_PiXL_Visit_ClientParams'
   )
BEGIN
    CREATE JSON INDEX IX_PiXL_Visit_ClientParams
        ON PiXL.Visit (ClientParamsJson)
        FOR ('$.email', '$.hid');

    PRINT '  Created JSON INDEX IX_PiXL_Visit_ClientParams for ($.email, $.hid)';
END
ELSE
    PRINT '  JSON INDEX IX_PiXL_Visit_ClientParams already exists or table missing — skipped';
GO


PRINT '=== Phase 4: PiXL.Match (Identity Resolution) ===';
GO

IF OBJECT_ID('PiXL.Match', 'U') IS NULL
BEGIN
    CREATE TABLE PiXL.Match
    (
        MatchId             BIGINT          NOT NULL  IDENTITY(1,1),
        CompanyID           INT             NOT NULL,
        PiXLID              INT             NOT NULL,
        MatchType           VARCHAR(20)     NOT NULL,     -- 'email', 'ip', 'geo' — extensible
        MatchKey            VARCHAR(256)    NOT NULL,     -- The matched value: email, IP, zip
        IndividualKey       VARCHAR(35)     NULL,         -- → AutoConsumer.IndividualKey (email/IP matches)
        AddressKey          VARCHAR(35)     NULL,         -- → AutoConsumer.AddressKey (geo matches)
        DeviceId            BIGINT          NULL,         -- FK → PiXL.Device (device at first match)
        IpId                BIGINT          NULL,         -- FK → PiXL.IP (IP at first match)
        FirstVisitID        BIGINT          NOT NULL,     -- FK → PiXL.Visit(VisitID) — first hit
        LatestVisitID       BIGINT          NOT NULL,     -- FK → PiXL.Visit(VisitID) — most recent hit
        FirstSeen           DATETIME2(3)    NOT NULL,
        LastSeen            DATETIME2(3)    NOT NULL,
        HitCount            INT             NOT NULL  DEFAULT 1,
        ConfidenceScore     FLOAT           NULL,         -- For future scoring
        MatchedAt           DATETIME2(3)    NULL,         -- When IndividualKey/AddressKey was resolved

        -- Nonclustered PK on surrogate key
        CONSTRAINT PK_PiXL_Match PRIMARY KEY NONCLUSTERED (MatchId),

        -- Foreign keys
        CONSTRAINT FK_Match_Device FOREIGN KEY (DeviceId) REFERENCES PiXL.Device(DeviceId),
        CONSTRAINT FK_Match_IP FOREIGN KEY (IpId) REFERENCES PiXL.IP(IpId),
        CONSTRAINT FK_Match_FirstVisit FOREIGN KEY (FirstVisitID) REFERENCES PiXL.Visit(VisitID),
        CONSTRAINT FK_Match_LatestVisit FOREIGN KEY (LatestVisitID) REFERENCES PiXL.Visit(VisitID)
    );

    -- Clustered on the dedup key — MERGE target seeks by (CompanyID, PiXLID, MatchType, MatchKey)
    CREATE UNIQUE CLUSTERED INDEX CIX_PiXL_Match
        ON PiXL.Match (CompanyID, PiXLID, MatchType, MatchKey);

    -- IndividualKey lookups (filtered — only resolved matches)
    CREATE NONCLUSTERED INDEX IX_PiXL_Match_IndKey
        ON PiXL.Match (IndividualKey)
        WHERE IndividualKey IS NOT NULL;

    -- AddressKey lookups (filtered — for future geo matches)
    CREATE NONCLUSTERED INDEX IX_PiXL_Match_AddrKey
        ON PiXL.Match (AddressKey)
        WHERE AddressKey IS NOT NULL;

    -- Device lookups (filtered — only matches with device info)
    CREATE NONCLUSTERED INDEX IX_PiXL_Match_Device
        ON PiXL.Match (DeviceId)
        WHERE DeviceId IS NOT NULL;

    -- Recent matches (for dashboard/reporting queries)
    CREATE NONCLUSTERED INDEX IX_PiXL_Match_LastSeen
        ON PiXL.Match (LastSeen DESC)
        INCLUDE (CompanyID, PiXLID, MatchType);

    PRINT '  Created PiXL.Match with 5 indexes';
END
ELSE
    PRINT '  PiXL.Match already exists — skipped';
GO


PRINT '=== Phase 5: ETL.MatchWatermark ===';
GO

IF OBJECT_ID('ETL.MatchWatermark', 'U') IS NULL
BEGIN
    CREATE TABLE ETL.MatchWatermark
    (
        ProcessName         NVARCHAR(100)   NOT NULL  PRIMARY KEY,
        LastProcessedId     BIGINT          NOT NULL  DEFAULT 0,
        LastRunAt           DATETIME2       NULL,
        RowsProcessed       BIGINT          NOT NULL  DEFAULT 0,
        RowsMatched         BIGINT          NOT NULL  DEFAULT 0    -- Extra counter for match rates
    );

    -- Seed the watermark
    INSERT INTO ETL.MatchWatermark (ProcessName, LastProcessedId, LastRunAt, RowsProcessed, RowsMatched)
    VALUES ('MatchVisits', 0, NULL, 0, 0);

    PRINT '  Created ETL.MatchWatermark and seeded MatchVisits watermark';
END
ELSE
    PRINT '  ETL.MatchWatermark already exists — skipped';
GO


PRINT '=== Phase 6: Validation ===';
GO

-- Verify all objects exist
DECLARE @errors INT = 0;

IF OBJECT_ID('PiXL.Device', 'U') IS NULL BEGIN PRINT '  ERROR: PiXL.Device missing!'; SET @errors += 1; END
IF OBJECT_ID('PiXL.IP', 'U') IS NULL BEGIN PRINT '  ERROR: PiXL.IP missing!'; SET @errors += 1; END
IF OBJECT_ID('PiXL.Visit', 'U') IS NULL BEGIN PRINT '  ERROR: PiXL.Visit missing!'; SET @errors += 1; END
IF OBJECT_ID('PiXL.Match', 'U') IS NULL BEGIN PRINT '  ERROR: PiXL.Match missing!'; SET @errors += 1; END
IF OBJECT_ID('ETL.MatchWatermark', 'U') IS NULL BEGIN PRINT '  ERROR: ETL.MatchWatermark missing!'; SET @errors += 1; END

-- Verify FK relationships exist
DECLARE @fkCount INT;
SELECT @fkCount = COUNT(*) FROM sys.foreign_keys
WHERE name IN ('FK_Visit_Device','FK_Visit_IP','FK_Match_Device','FK_Match_IP','FK_Match_FirstVisit','FK_Match_LatestVisit');

IF @fkCount = 6
    PRINT '  OK: All 6 FK constraints present';
ELSE
BEGIN
    PRINT '  ERROR: Expected 6 FK constraints, found ' + CAST(@fkCount AS VARCHAR(10));
    SET @errors += 1;
END

-- Verify JSON index exists on PiXL.Visit
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes 
    WHERE object_id = OBJECT_ID('PiXL.Visit') AND name = 'IX_PiXL_Visit_ClientParams'
)
BEGIN
    PRINT '  ERROR: JSON INDEX IX_PiXL_Visit_ClientParams missing!';
    SET @errors += 1;
END

-- Verify ClientParamsJson is json type (not nvarchar)
IF EXISTS (
    SELECT 1 FROM sys.columns c
    JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID('PiXL.Visit') 
      AND c.name = 'ClientParamsJson'
      AND t.name = 'json'
)
    PRINT '  OK: ClientParamsJson is native json type';
ELSE IF OBJECT_ID('PiXL.Visit', 'U') IS NOT NULL
BEGIN
    PRINT '  WARNING: ClientParamsJson is NOT json type!';
    SET @errors += 1;
END

-- Verify MatchEmail is NOT a computed column
IF EXISTS (
    SELECT 1 FROM sys.computed_columns
    WHERE object_id = OBJECT_ID('PiXL.Visit') AND name = 'MatchEmail'
)
BEGIN
    PRINT '  ERROR: MatchEmail is a computed column — should be a regular column!';
    SET @errors += 1;
END
ELSE IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('PiXL.Visit') AND name = 'MatchEmail'
)
    PRINT '  OK: MatchEmail is a regular column (not computed)';

-- Verify MatchWatermark is seeded
IF EXISTS (SELECT 1 FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits')
    PRINT '  OK: MatchVisits watermark seeded';
ELSE
BEGIN
    PRINT '  ERROR: MatchVisits watermark not found!';
    SET @errors += 1;
END

IF @errors = 0
    PRINT '  ALL VALIDATIONS PASSED';
ELSE
    PRINT '  ' + CAST(@errors AS VARCHAR(10)) + ' VALIDATION ERROR(S) — review above';
GO

-- Quick functional test: INSERT a test row into PiXL.Visit with json type
-- (this verifies the json type accepts valid JSON and rejects invalid JSON)
PRINT '';
PRINT '=== Phase 7: Functional Smoke Test ===';
GO

BEGIN TRY
    -- Test 1: Verify json type accepts valid JSON
    DECLARE @testJson JSON = '{"email":"smoke@test.com","hid":"12345"}';
    PRINT '  OK: json type accepts valid JSON object';

    -- Test 2: Verify JSON_VALUE works on json type
    DECLARE @extracted NVARCHAR(200) = JSON_VALUE(@testJson, '$.email');
    IF @extracted = 'smoke@test.com'
        PRINT '  OK: JSON_VALUE extracts correctly from json type';
    ELSE
        PRINT '  WARNING: JSON_VALUE returned unexpected value: ' + ISNULL(@extracted, 'NULL');

    -- Test 3: Verify invalid JSON is rejected
    BEGIN TRY
        DECLARE @badJson JSON = 'not valid json';
        PRINT '  WARNING: json type accepted invalid input!';
    END TRY
    BEGIN CATCH
        PRINT '  OK: json type correctly rejects invalid JSON (Msg ' + CAST(ERROR_NUMBER() AS VARCHAR(10)) + ')';
    END CATCH

    -- Test 4: Verify JSON_PATH_EXISTS works
    IF JSON_PATH_EXISTS(@testJson, '$.email') = 1
        PRINT '  OK: JSON_PATH_EXISTS works on json type';
    ELSE
        PRINT '  WARNING: JSON_PATH_EXISTS returned unexpected result';
END TRY
BEGIN CATCH
    PRINT '  ERROR in smoke test: ' + ERROR_MESSAGE();
END CATCH
GO

PRINT '';
PRINT '=== 19_DeviceIpVisitMatchTables.sql — Complete ===';
GO
