/*
    27_MatchTypeConfig.sql
    =======================
    Adds per-PiXL match-type gating columns to PiXL.Config and updates
    ETL.usp_MatchVisits to respect them.

    NEW COLUMNS ON PiXL.Config:
      MatchEmail  BIT NOT NULL DEFAULT 1  — Allow email-based identity resolution
      MatchIP     BIT NOT NULL DEFAULT 1  — Allow IP-based household matching
      MatchGeo    BIT NOT NULL DEFAULT 1  — Allow geo-proximity matching (future)

    Default = 1 (enabled) preserves existing behaviour for PiXLs with no
    explicit config.  Restricted PiXLs (e.g. DEMO) get specific columns set to 0.

    ALSO:
      - Updates the vw_PiXL_ConfigWithDefaults view to surface the new columns.
      - Updates ETL.usp_MatchVisits to skip email matching when MatchEmail = 0.
      - Inserts/updates a DEMO config row with MatchEmail = 1, MatchIP = 0, MatchGeo = 0.

    PREREQUISITES:
      - SQL/10_PiXLConfiguration.sql  (PiXL.Config table)
      - SQL/23_MatchVisits.sql        (ETL.usp_MatchVisits)

    TARGET: SQL Server 2025 (17.0.1050.2)
    Run on: SmartPiXL database, localhost\SQL2025
    Date:   2026-02-15
*/

USE SmartPiXL;
GO

SET NOCOUNT ON;
GO

-- =========================================================================
-- 1. ADD MATCH-TYPE COLUMNS TO PiXL.Config
-- =========================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('PiXL.Config') AND name = 'MatchEmail'
)
BEGIN
    ALTER TABLE PiXL.Config ADD MatchEmail BIT NOT NULL CONSTRAINT DF_Config_MatchEmail DEFAULT 1;
    PRINT 'Added PiXL.Config.MatchEmail (default 1).';
END
ELSE PRINT 'PiXL.Config.MatchEmail already exists.';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('PiXL.Config') AND name = 'MatchIP'
)
BEGIN
    ALTER TABLE PiXL.Config ADD MatchIP BIT NOT NULL CONSTRAINT DF_Config_MatchIP DEFAULT 1;
    PRINT 'Added PiXL.Config.MatchIP (default 1).';
END
ELSE PRINT 'PiXL.Config.MatchIP already exists.';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('PiXL.Config') AND name = 'MatchGeo'
)
BEGIN
    ALTER TABLE PiXL.Config ADD MatchGeo BIT NOT NULL CONSTRAINT DF_Config_MatchGeo DEFAULT 1;
    PRINT 'Added PiXL.Config.MatchGeo (default 1).';
END
ELSE PRINT 'PiXL.Config.MatchGeo already exists.';
GO


-- =========================================================================
-- 2. UPSERT DEMO CONFIG — email-only matching
-- =========================================================================
IF EXISTS (SELECT 1 FROM PiXL.Config WHERE CompanyID = 'DEMO' AND PiXLID = 'demo-pixl')
BEGIN
    UPDATE PiXL.Config SET
        MatchEmail = 1,
        MatchIP    = 0,
        MatchGeo   = 0,
        UpdatedAt  = GETUTCDATE(),
        Notes      = 'Demo pixel — email matching only'
    WHERE CompanyID = 'DEMO' AND PiXLID = 'demo-pixl';
    PRINT 'Updated DEMO/demo-pixl config: MatchEmail=1, MatchIP=0, MatchGeo=0.';
END
ELSE
BEGIN
    INSERT INTO PiXL.Config (CompanyID, PiXLID, MatchEmail, MatchIP, MatchGeo, Notes)
    VALUES ('DEMO', 'demo-pixl', 1, 0, 0, 'Demo pixel — email matching only');
    PRINT 'Inserted DEMO/demo-pixl config: MatchEmail=1, MatchIP=0, MatchGeo=0.';
END
GO


-- =========================================================================
-- 3. UPDATE vw_PiXL_ConfigWithDefaults VIEW
-- =========================================================================
IF OBJECT_ID('dbo.vw_PiXL_ConfigWithDefaults', 'V') IS NOT NULL
    DROP VIEW dbo.vw_PiXL_ConfigWithDefaults;
GO

CREATE VIEW dbo.vw_PiXL_ConfigWithDefaults AS
SELECT DISTINCT
    t.CompanyID,
    t.PiXLID,
    -- Data collection exclusions
    ISNULL(c.ExcludeLocalIP, 0)     AS ExcludeLocalIP,
    ISNULL(c.ExcludeAudioFP, 0)     AS ExcludeAudioFP,
    ISNULL(c.ExcludeCanvasFP, 0)    AS ExcludeCanvasFP,
    ISNULL(c.ExcludeWebGLFP, 0)     AS ExcludeWebGLFP,
    ISNULL(c.ExcludeBots, 0)        AS ExcludeBots,
    ISNULL(c.BotScoreThreshold, 10) AS BotScoreThreshold,
    ISNULL(c.RetentionDays, 365)    AS RetentionDays,
    -- Match-type gating (default = all enabled)
    ISNULL(c.MatchEmail, 1)         AS MatchEmail,
    ISNULL(c.MatchIP, 1)            AS MatchIP,
    ISNULL(c.MatchGeo, 1)           AS MatchGeo,
    -- Meta
    CASE WHEN c.ConfigId IS NULL THEN 1 ELSE 0 END AS IsDefaultConfig
FROM PiXL.Parsed t
LEFT JOIN PiXL.Config c
    ON t.CompanyID = c.CompanyID
    AND t.PiXLID = c.PiXLID;
GO

PRINT 'View vw_PiXL_ConfigWithDefaults recreated with MatchEmail/MatchIP/MatchGeo.';
GO


-- =========================================================================
-- 4. UPDATE ETL.usp_MatchVisits — respect MatchEmail flag
-- =========================================================================
CREATE OR ALTER PROCEDURE [ETL].[usp_MatchVisits]
    @BatchSize INT = 10000
AS
BEGIN
    SET NOCOUNT ON;
    SET QUOTED_IDENTIFIER ON;

    -- =====================================================================
    -- 1. READ WATERMARK
    -- =====================================================================
    DECLARE @LastId BIGINT, @MaxId BIGINT;
    SELECT @LastId = LastProcessedId FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits';

    -- Self-healing: if Match has rows with VisitIDs beyond the watermark
    DECLARE @MaxMatchVisitId BIGINT = (
        SELECT ISNULL(MAX(LatestVisitID), 0) FROM PiXL.Match
    );
    IF @MaxMatchVisitId > @LastId SET @LastId = @MaxMatchVisitId;

    SELECT @MaxId = MAX(VisitID) FROM PiXL.Visit;
    IF @MaxId IS NULL OR @MaxId <= @LastId
    BEGIN
        SELECT 0 AS RowsProcessed, 0 AS RowsMatched,
               @LastId AS FromId, @LastId AS ToId;
        RETURN;
    END;
    IF @MaxId > @LastId + @BatchSize SET @MaxId = @LastId + @BatchSize;


    -- =====================================================================
    -- 2. BUILD CANDIDATE SET
    -- =====================================================================
    -- Only Visit rows whose PiXL has MatchEmail enabled (or no config row,
    -- which defaults to enabled).  Raw watermark still advances for all rows.

    CREATE TABLE #Candidates (
        VisitID         BIGINT          NOT NULL  PRIMARY KEY,
        CompanyID       INT             NOT NULL,
        PiXLID          INT             NOT NULL,
        DeviceId        BIGINT          NULL,
        IpId            BIGINT          NULL,
        ReceivedAt      DATETIME2(3)    NOT NULL,
        RawEmail        NVARCHAR(200)   NOT NULL,
        NormalizedEmail VARCHAR(200)    NULL,
        IndividualKey   VARCHAR(35)     NULL,
        AddressKey      VARCHAR(35)     NULL
    );

    INSERT INTO #Candidates (VisitID, CompanyID, PiXLID, DeviceId, IpId,
                              ReceivedAt, RawEmail, NormalizedEmail)
    SELECT
        v.VisitID, v.CompanyID, v.PiXLID, v.DeviceId, v.IpId,
        v.ReceivedAt, v.MatchEmail,
        LOWER(LTRIM(RTRIM(v.MatchEmail)))
    FROM PiXL.Visit v
    LEFT JOIN PiXL.Config cfg
        ON cfg.CompanyID = CAST(v.CompanyID AS VARCHAR(100))
       AND cfg.PiXLID   = CAST(v.PiXLID   AS VARCHAR(100))
    WHERE v.VisitID > @LastId AND v.VisitID <= @MaxId
      AND v.MatchEmail IS NOT NULL
      AND LEN(v.MatchEmail) > 5
      AND v.MatchEmail LIKE '%_@_%.__%'
      -- Gate: only include visits whose PiXL allows email matching.
      -- NULL cfg row (no config) → ISNULL defaults to 1 (enabled).
      AND ISNULL(cfg.MatchEmail, 1) = 1;

    DECLARE @CandidateCount INT = @@ROWCOUNT;


    -- =====================================================================
    -- 3. RESOLVE AGAINST AUTOCONSUMER — EMAIL MATCH
    -- =====================================================================
    IF @CandidateCount > 0
    BEGIN
        UPDATE c SET
            c.IndividualKey = ac.IndividualKey,
            c.AddressKey    = ac.AddressKey
        FROM #Candidates c
        CROSS APPLY (
            SELECT TOP 1 ac.IndividualKey, ac.AddressKey
            FROM AutoUpdate.dbo.AutoConsumer ac
            WHERE ac.EMail = c.NormalizedEmail
            ORDER BY ac.RecordID DESC
        ) ac;
    END

    DECLARE @MatchedCount INT = (
        SELECT COUNT(*) FROM #Candidates WHERE IndividualKey IS NOT NULL
    );


    -- =====================================================================
    -- 4. MERGE INTO PiXL.Match
    -- =====================================================================
    IF @CandidateCount > 0
    BEGIN
        MERGE PiXL.Match AS target
        USING (
            SELECT CompanyID, PiXLID,
                   'email' AS MatchType,
                   NormalizedEmail AS MatchKey,
                   IndividualKey, AddressKey,
                   DeviceId, IpId,
                   VisitID, ReceivedAt
            FROM #Candidates
            WHERE NormalizedEmail IS NOT NULL
        ) AS source
        ON target.CompanyID = source.CompanyID
           AND target.PiXLID = source.PiXLID
           AND target.MatchType = source.MatchType
           AND target.MatchKey = source.MatchKey

        WHEN MATCHED THEN UPDATE SET
            LatestVisitID = source.VisitID,
            LastSeen = source.ReceivedAt,
            HitCount = target.HitCount + 1,
            IndividualKey = COALESCE(target.IndividualKey, source.IndividualKey),
            AddressKey = COALESCE(target.AddressKey, source.AddressKey),
            MatchedAt = CASE
                WHEN target.IndividualKey IS NULL AND source.IndividualKey IS NOT NULL
                    THEN SYSUTCDATETIME()
                ELSE target.MatchedAt
            END

        WHEN NOT MATCHED THEN INSERT (
            CompanyID, PiXLID, MatchType, MatchKey,
            IndividualKey, AddressKey,
            DeviceId, IpId,
            FirstVisitID, LatestVisitID,
            FirstSeen, LastSeen,
            HitCount, MatchedAt
        ) VALUES (
            source.CompanyID, source.PiXLID, source.MatchType, source.MatchKey,
            source.IndividualKey, source.AddressKey,
            source.DeviceId, source.IpId,
            source.VisitID, source.VisitID,
            source.ReceivedAt, source.ReceivedAt,
            1,
            CASE WHEN source.IndividualKey IS NOT NULL THEN SYSUTCDATETIME() END
        );
    END


    -- =====================================================================
    -- 5. UPDATE WATERMARK
    -- =====================================================================
    UPDATE ETL.MatchWatermark SET
        LastProcessedId = @MaxId,
        LastRunAt = SYSUTCDATETIME(),
        RowsProcessed = RowsProcessed + @CandidateCount,
        RowsMatched = RowsMatched + @MatchedCount
    WHERE ProcessName = 'MatchVisits';


    -- =====================================================================
    -- 6. RETURN RESULTS
    -- =====================================================================
    DROP TABLE #Candidates;

    SELECT @CandidateCount AS RowsProcessed,
           @MatchedCount AS RowsMatched,
           @LastId + 1 AS FromId,
           @MaxId AS ToId;
END;
GO

PRINT 'ETL.usp_MatchVisits updated — now respects PiXL.Config.MatchEmail flag.';
GO

PRINT '';
PRINT '=========================================================================';
PRINT ' 27_MatchTypeConfig.sql — COMPLETE';
PRINT '';
PRINT ' Added to PiXL.Config:';
PRINT '   MatchEmail  BIT DEFAULT 1  (email identity resolution)';
PRINT '   MatchIP     BIT DEFAULT 1  (IP household matching)';
PRINT '   MatchGeo    BIT DEFAULT 1  (geo proximity matching)';
PRINT '';
PRINT ' DEMO/demo-pixl configured: MatchEmail=1, MatchIP=0, MatchGeo=0';
PRINT '';
PRINT ' ETL.usp_MatchVisits now filters by MatchEmail flag.';
PRINT ' Future procs for IP/Geo matching will filter by MatchIP/MatchGeo.';
PRINT '=========================================================================';
GO
