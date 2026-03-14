/*
    45_EliminatePiXLConfig.sql
    ===========================
    Eliminates the AI-drift PiXL.Config table by merging its unique columns
    into the legitimate PiXL.Settings table (synced from Xavier production).

    PROBLEM:
      PiXL.Config was an AI-generated table with VARCHAR(100) CompanyID/PiXLID
      keys — the only table in the schema using string identifiers instead of
      INT foreign keys. It stored per-pixel configuration (match-type gating,
      bot exclusions, retention) but duplicated the purpose of PiXL.Settings.

    SOLUTION:
      1. Add Config's 10 unique columns to PiXL.Settings (and its temporal
         history table REF.PiXL_History)
      2. Update ETL.usp_MatchLegacyVisits to JOIN PiXL.Settings (INT=INT)
         instead of PiXL.Config (CAST to VARCHAR — eliminated)
      3. Drop vw_PiXL_ConfigWithDefaults (no longer needed)
      4. Drop PiXL.Config

    COLUMNS ADDED TO PiXL.Settings:
      ExcludeLocalIP    BIT DEFAULT 0   — Exclude local/private IPs
      ExcludeAudioFP    BIT DEFAULT 0   — Exclude audio fingerprinting
      ExcludeCanvasFP   BIT DEFAULT 0   — Exclude canvas fingerprinting
      ExcludeWebGLFP    BIT DEFAULT 0   — Exclude WebGL fingerprinting
      ExcludeBots       BIT DEFAULT 0   — Exclude bot traffic
      BotScoreThreshold INT DEFAULT 10  — Bot score cutoff
      RetentionDays     INT DEFAULT 365 — Data retention period
      MatchEmail        BIT DEFAULT 1   — Enable email identity resolution
      MatchIP           BIT DEFAULT 1   — Enable IP household matching
      MatchGeo          BIT DEFAULT 1   — Enable geo proximity matching

    IMPACT:
      - ETL.usp_MatchVisits: Already didn't reference Config (live version)
      - ETL.usp_MatchLegacyVisits: Updated to use PiXL.Settings (INT join)
      - vw_PiXL_ConfigWithDefaults: Dropped (no consumers)
      - CompanyPiXLSyncService (Xavier sync): Unaffected — only syncs
        Xavier-sourced columns, new columns retain their defaults

    PREREQUISITES:
      - PiXL.Settings must exist as a temporal table with REF.PiXL_History
      - No active transactions on PiXL.Settings during temporal disable

    Run on:  localhost\SQL2025  →  SmartPiXL
    Date:    2026-02-19
    Session: 22
*/

USE SmartPiXL;
GO

SET NOCOUNT ON;
GO

-- =========================================================================
-- 1. DISABLE TEMPORAL VERSIONING (required to ALTER the table)
-- =========================================================================
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE object_id = OBJECT_ID('PiXL.Settings')
      AND temporal_type = 2  -- SYSTEM_VERSIONED
)
BEGIN
    ALTER TABLE PiXL.Settings SET (SYSTEM_VERSIONING = OFF);
    PRINT 'Temporal versioning disabled on PiXL.Settings';
END
GO


-- =========================================================================
-- 2. ADD CONFIGURATION COLUMNS TO PiXL.Settings
-- =========================================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL.Settings') AND name = 'ExcludeLocalIP')
BEGIN
    ALTER TABLE PiXL.Settings ADD
        ExcludeLocalIP    BIT NOT NULL CONSTRAINT DF_Settings_ExcludeLocalIP    DEFAULT 0,
        ExcludeAudioFP    BIT NOT NULL CONSTRAINT DF_Settings_ExcludeAudioFP    DEFAULT 0,
        ExcludeCanvasFP   BIT NOT NULL CONSTRAINT DF_Settings_ExcludeCanvasFP   DEFAULT 0,
        ExcludeWebGLFP    BIT NOT NULL CONSTRAINT DF_Settings_ExcludeWebGLFP    DEFAULT 0,
        ExcludeBots       BIT NOT NULL CONSTRAINT DF_Settings_ExcludeBots       DEFAULT 0,
        BotScoreThreshold INT NOT NULL CONSTRAINT DF_Settings_BotScoreThreshold DEFAULT 10,
        RetentionDays     INT NOT NULL CONSTRAINT DF_Settings_RetentionDays     DEFAULT 365,
        MatchEmail        BIT NOT NULL CONSTRAINT DF_Settings_MatchEmail        DEFAULT 1,
        MatchIP           BIT NOT NULL CONSTRAINT DF_Settings_MatchIP           DEFAULT 1,
        MatchGeo          BIT NOT NULL CONSTRAINT DF_Settings_MatchGeo          DEFAULT 1;
    PRINT 'Added 10 configuration columns to PiXL.Settings';
END
ELSE PRINT 'Configuration columns already exist on PiXL.Settings';
GO


-- =========================================================================
-- 3. ADD SAME COLUMNS TO HISTORY TABLE (required for temporal re-enable)
-- =========================================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('REF.PiXL_History') AND name = 'ExcludeLocalIP')
BEGIN
    ALTER TABLE REF.PiXL_History ADD
        ExcludeLocalIP    BIT NOT NULL DEFAULT 0,
        ExcludeAudioFP    BIT NOT NULL DEFAULT 0,
        ExcludeCanvasFP   BIT NOT NULL DEFAULT 0,
        ExcludeWebGLFP    BIT NOT NULL DEFAULT 0,
        ExcludeBots       BIT NOT NULL DEFAULT 0,
        BotScoreThreshold INT NOT NULL DEFAULT 10,
        RetentionDays     INT NOT NULL DEFAULT 365,
        MatchEmail        BIT NOT NULL DEFAULT 1,
        MatchIP           BIT NOT NULL DEFAULT 1,
        MatchGeo          BIT NOT NULL DEFAULT 1;
    PRINT 'Added 10 configuration columns to REF.PiXL_History';
END
ELSE PRINT 'Configuration columns already exist on REF.PiXL_History';
GO


-- =========================================================================
-- 4. RE-ENABLE TEMPORAL VERSIONING
-- =========================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE object_id = OBJECT_ID('PiXL.Settings')
      AND temporal_type = 2
)
BEGIN
    ALTER TABLE PiXL.Settings SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = REF.PiXL_History));
    PRINT 'Temporal versioning re-enabled on PiXL.Settings → REF.PiXL_History';
END
GO


-- =========================================================================
-- 5. DROP THE OBSOLETE VIEW
-- =========================================================================
DROP VIEW IF EXISTS dbo.vw_PiXL_ConfigWithDefaults;
PRINT 'Dropped vw_PiXL_ConfigWithDefaults (no longer needed)';
GO


-- =========================================================================
-- 6. UPDATE ETL.usp_MatchLegacyVisits — use PiXL.Settings instead of Config
--    Key change: LEFT JOIN PiXL.Settings s ON s.CompanyId = v.CompanyID
--    Eliminates the CAST(INT AS VARCHAR(100)) ugliness
-- =========================================================================
CREATE OR ALTER PROCEDURE [ETL].[usp_MatchLegacyVisits]
    @BatchSize INT = 5000
AS
BEGIN
    SET NOCOUNT ON;
    SET QUOTED_IDENTIFIER ON;

    -- 1. READ WATERMARK
    DECLARE @LastId BIGINT, @MaxId BIGINT;
    SELECT @LastId = LastProcessedId
    FROM ETL.MatchWatermark
    WHERE ProcessName = 'MatchLegacyVisits';

    DECLARE @MaxMatchVisitId BIGINT = (
        SELECT ISNULL(MAX(LatestVisitID), 0) FROM PiXL.Match WHERE MatchType = 'ip'
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

    -- 2. BUILD CANDIDATE SET — gated by PiXL.Settings.MatchIP
    CREATE TABLE #Candidates (
        VisitID         BIGINT          NOT NULL  PRIMARY KEY,
        CompanyID       INT             NOT NULL,
        PiXLID          INT             NOT NULL,
        DeviceId        BIGINT          NULL,
        IpId            BIGINT          NOT NULL,
        IPAddress       VARCHAR(50)     NOT NULL,
        ReceivedAt      DATETIME2(3)    NOT NULL,
        IndividualKey   VARCHAR(35)     NULL,
        AddressKey      VARCHAR(35)     NULL
    );

    INSERT INTO #Candidates (VisitID, CompanyID, PiXLID, DeviceId, IpId,
                              IPAddress, ReceivedAt)
    SELECT
        v.VisitID, v.CompanyID, v.PiXLID, v.DeviceId, v.IpId,
        ip.IPAddress, v.ReceivedAt
    FROM PiXL.Visit v
    JOIN PiXL.IP ip ON v.IpId = ip.IpId
    LEFT JOIN PiXL.Settings s
        ON s.CompanyId = v.CompanyID
       AND s.PiXLId   = v.PiXLID
    WHERE v.VisitID > @LastId AND v.VisitID <= @MaxId
      AND v.HitType = 'legacy'
      AND v.MatchEmail IS NULL
      AND v.IpId IS NOT NULL
      -- Gate: only include visits whose PiXL allows IP matching.
      -- NULL Settings row → ISNULL defaults to 1 (enabled).
      AND ISNULL(s.MatchIP, 1) = 1;

    DECLARE @CandidateCount INT = @@ROWCOUNT;

    -- 3. RESOLVE AGAINST AUTOCONSUMER — IP MATCH
    IF @CandidateCount > 0
    BEGIN
        CREATE TABLE #IpMatch (
            IPAddress       VARCHAR(50) NOT NULL PRIMARY KEY,
            IndividualKey   VARCHAR(35) NULL,
            AddressKey      VARCHAR(35) NULL
        );

        ;WITH IpMaxRecord AS (
            SELECT c.IPAddress, MAX(ac.RecordID) AS MaxRecordID
            FROM (SELECT DISTINCT IPAddress FROM #Candidates) c
            CROSS APPLY (
                SELECT TOP 1 ac.RecordID
                FROM AutoUpdate.dbo.AutoConsumer ac
                WHERE ac.IP = c.IPAddress
                  AND ac.IP IS NOT NULL AND ac.IP <> ''
                ORDER BY ac.RecordID DESC
            ) ac
            GROUP BY c.IPAddress
        )
        INSERT INTO #IpMatch (IPAddress, IndividualKey, AddressKey)
        SELECT imr.IPAddress, ac.IndividualKey, ac.AddressKey
        FROM IpMaxRecord imr
        JOIN AutoUpdate.dbo.AutoConsumer ac ON ac.RecordID = imr.MaxRecordID;

        UPDATE c SET
            c.IndividualKey = im.IndividualKey,
            c.AddressKey    = im.AddressKey
        FROM #Candidates c
        JOIN #IpMatch im ON c.IPAddress = im.IPAddress;

        DROP TABLE #IpMatch;
    END

    DECLARE @MatchedCount INT = (
        SELECT COUNT(*) FROM #Candidates WHERE IndividualKey IS NOT NULL
    );

    -- 4. MERGE INTO PiXL.Match — deduplicated by CompanyID/PiXLID/IP
    IF @CandidateCount > 0
    BEGIN
        MERGE PiXL.Match AS target
        USING (
            SELECT
                CompanyID,
                PiXLID,
                'ip'        AS MatchType,
                IPAddress   AS MatchKey,
                MAX(IndividualKey)  AS IndividualKey,
                MAX(AddressKey)     AS AddressKey,
                MAX(DeviceId)       AS DeviceId,
                MAX(IpId)           AS IpId,
                MIN(VisitID)        AS FirstVisitID,
                MAX(VisitID)        AS LatestVisitID,
                MIN(ReceivedAt)     AS FirstSeen,
                MAX(ReceivedAt)     AS LastSeen,
                COUNT(*)            AS BatchHitCount
            FROM #Candidates
            GROUP BY CompanyID, PiXLID, IPAddress
        ) AS source
        ON target.CompanyID = source.CompanyID
           AND target.PiXLID = source.PiXLID
           AND target.MatchType = source.MatchType
           AND target.MatchKey = source.MatchKey

        WHEN MATCHED THEN UPDATE SET
            LatestVisitID = source.LatestVisitID,
            LastSeen      = source.LastSeen,
            HitCount      = target.HitCount + source.BatchHitCount,
            IndividualKey = COALESCE(target.IndividualKey, source.IndividualKey),
            AddressKey    = COALESCE(target.AddressKey, source.AddressKey),
            MatchedAt     = CASE
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
            source.FirstVisitID, source.LatestVisitID,
            source.FirstSeen, source.LastSeen,
            source.BatchHitCount,
            CASE WHEN source.IndividualKey IS NOT NULL THEN SYSUTCDATETIME() END
        );
    END

    -- 5. UPDATE WATERMARK
    UPDATE ETL.MatchWatermark SET
        LastProcessedId = @MaxId,
        LastRunAt       = SYSUTCDATETIME(),
        RowsProcessed   = RowsProcessed + @CandidateCount,
        RowsMatched     = RowsMatched + @MatchedCount
    WHERE ProcessName = 'MatchLegacyVisits';

    DROP TABLE #Candidates;

    -- 6. RETURN RESULTS
    SELECT @CandidateCount AS RowsProcessed,
           @MatchedCount   AS RowsMatched,
           @LastId + 1     AS FromId,
           @MaxId          AS ToId;
END;
GO


-- =========================================================================
-- 7. DROP PiXL.Config — the AI-drift table
-- =========================================================================
IF OBJECT_ID('PiXL.Config', 'U') IS NOT NULL
BEGIN
    DROP TABLE PiXL.Config;
    PRINT 'Dropped PiXL.Config (AI-drift table eliminated)';
END
GO


-- =========================================================================
-- VERIFICATION
-- =========================================================================
PRINT '';
PRINT '=========================================================================';
PRINT ' 45_EliminatePiXLConfig.sql — COMPLETE';
PRINT '';
PRINT ' DROPPED:';
PRINT '   PiXL.Config table (AI drift — varchar keys)';
PRINT '   dbo.vw_PiXL_ConfigWithDefaults view';
PRINT '';
PRINT ' ADDED TO PiXL.Settings:';
PRINT '   ExcludeLocalIP, ExcludeAudioFP, ExcludeCanvasFP, ExcludeWebGLFP';
PRINT '   ExcludeBots, BotScoreThreshold, RetentionDays';
PRINT '   MatchEmail, MatchIP, MatchGeo';
PRINT '';
PRINT ' UPDATED:';
PRINT '   ETL.usp_MatchLegacyVisits — now JOINs PiXL.Settings (INT=INT)';
PRINT '';
PRINT ' UNAFFECTED:';
PRINT '   ETL.usp_MatchVisits — already didn''t reference Config';
PRINT '   CompanyPiXLSyncService — only syncs Xavier columns';
PRINT '=========================================================================';
GO
