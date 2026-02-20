/*
    32_LegacyIpMatching.sql
    ========================
    Implements IP-based identity resolution for legacy pixel hits.

    Legacy hits (forwarded from Xavier via HTTP forward) have no JavaScript
    fingerprinting data and no MatchEmail — they only have IP, User-Agent,
    Referer, and server-side enrichment. This migration creates the
    infrastructure to match those visits against AutoConsumer by IP address.

    Components:
      1. Nonclustered filtered index on AutoConsumer.IP
         (420M+ rows, ~250M with IP — takes 15-45 minutes ONLINE)
      2. MatchWatermark row for 'MatchLegacyVisits'
      3. ETL.usp_MatchLegacyVisits stored procedure

    PREREQUISITES:
      - AutoUpdate database must exist on the same SQL Server instance
      - IX_AutoConsumer_EMail should already exist (22_AutoConsumerEmailIndex.sql)
      - Sufficient tempdb space (~20 GB for sort operations on IP index)
      - Run during low-traffic period if possible (index build is ONLINE)

    IMPORTANT:
      - The IP index touches AutoUpdate.dbo.AutoConsumer (we don't own it)
      - ONLINE = ON avoids blocking concurrent reads/writes
      - PiXL.Config.MatchIP gates which company/pixel combos participate
        (defaults to enabled when no Config row exists, same as MatchEmail)

    Run on: localhost\SQL2025
    Date:   2026-02-17
*/


-- =========================================================================
-- PART 1: AutoConsumer IP Index
-- =========================================================================
USE AutoUpdate;
GO

SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

-- Sanity check: verify IP column coverage before building the index
DECLARE @TotalRows BIGINT, @IpRows BIGINT;
SELECT @TotalRows = COUNT_BIG(*) FROM dbo.AutoConsumer WITH (NOLOCK);
SELECT @IpRows = COUNT_BIG(*) FROM dbo.AutoConsumer WITH (NOLOCK) WHERE IP IS NOT NULL AND IP <> '';

PRINT '=== AutoConsumer IP Index ===';
PRINT '  Total rows:    ' + CAST(@TotalRows AS VARCHAR(20));
PRINT '  Rows with IP:  ' + CAST(@IpRows AS VARCHAR(20));
PRINT '  Coverage:      ' + CAST(CAST(@IpRows * 100.0 / NULLIF(@TotalRows, 0) AS DECIMAL(5,1)) AS VARCHAR(10)) + '%';
PRINT '';

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.AutoConsumer')
      AND name = 'IX_AutoConsumer_IP'
)
BEGIN
    PRINT '  IX_AutoConsumer_IP already exists - skipped';
END
ELSE
BEGIN
    PRINT '  Creating IX_AutoConsumer_IP (filtered, ONLINE)...';
    PRINT '  This may take 15-45 minutes depending on I/O throughput.';

    CREATE NONCLUSTERED INDEX IX_AutoConsumer_IP
        ON dbo.AutoConsumer (IP)
        INCLUDE (IndividualKey, AddressKey, RecordID)
        WHERE IP IS NOT NULL AND IP <> ''
        WITH (ONLINE = ON, SORT_IN_TEMPDB = ON, MAXDOP = 4);

    PRINT '  IX_AutoConsumer_IP created successfully.';
END
GO

-- Verify
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.AutoConsumer')
      AND name = 'IX_AutoConsumer_IP'
)
    PRINT '  OK: IX_AutoConsumer_IP exists';
ELSE
    PRINT '  ERROR: IX_AutoConsumer_IP not found!';
GO


-- =========================================================================
-- PART 2: MatchWatermark Row for Legacy IP Matching
-- =========================================================================
USE SmartPiXL;
GO

IF NOT EXISTS (
    SELECT 1 FROM ETL.MatchWatermark WHERE ProcessName = 'MatchLegacyVisits'
)
BEGIN
    INSERT INTO ETL.MatchWatermark (ProcessName, LastProcessedId, LastRunAt, RowsProcessed, RowsMatched)
    VALUES ('MatchLegacyVisits', 0, '2000-01-01', 0, 0);
    PRINT 'Inserted MatchWatermark row for MatchLegacyVisits (starting from 0)';
END
ELSE
    PRINT 'MatchWatermark row for MatchLegacyVisits already exists';
GO


-- =========================================================================
-- PART 3: ETL.usp_MatchLegacyVisits — IP-based identity resolution
-- =========================================================================

IF OBJECT_ID('ETL.usp_MatchLegacyVisits', 'P') IS NOT NULL
    DROP PROCEDURE ETL.usp_MatchLegacyVisits;
GO

CREATE PROCEDURE [ETL].[usp_MatchLegacyVisits]
    @BatchSize INT = 5000
AS
BEGIN
    SET NOCOUNT ON;
    SET QUOTED_IDENTIFIER ON;

    -- =====================================================================
    -- 1. READ WATERMARK
    -- =====================================================================
    DECLARE @LastId BIGINT, @MaxId BIGINT;
    SELECT @LastId = LastProcessedId
    FROM ETL.MatchWatermark
    WHERE ProcessName = 'MatchLegacyVisits';

    -- Self-healing: if Match has IP-type rows with VisitIDs beyond watermark
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


    -- =====================================================================
    -- 2. BUILD CANDIDATE SET
    -- =====================================================================
    -- Legacy visits without MatchEmail, with a resolved IP address.
    -- Gated by PiXL.Config.MatchIP (defaults to enabled when no config row).

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
    LEFT JOIN PiXL.Config cfg
        ON cfg.CompanyID = CAST(v.CompanyID AS VARCHAR(100))
       AND cfg.PiXLID   = CAST(v.PiXLID   AS VARCHAR(100))
    WHERE v.VisitID > @LastId AND v.VisitID <= @MaxId
      AND v.HitType = 'legacy'
      AND v.MatchEmail IS NULL
      AND v.IpId IS NOT NULL
      -- Gate: only include visits whose PiXL allows IP matching.
      -- NULL cfg row (no config) -> ISNULL defaults to 1 (enabled).
      AND ISNULL(cfg.MatchIP, 1) = 1;

    DECLARE @CandidateCount INT = @@ROWCOUNT;


    -- =====================================================================
    -- 3. RESOLVE AGAINST AUTOCONSUMER — IP MATCH
    -- =====================================================================
    -- Two-phase approach for performance:
    -- Phase A: Collect distinct IPs, find max RecordID per IP via
    --          CROSS APPLY TOP 1 ORDER BY RecordID DESC (uses
    --          IX_AutoConsumer_IP filtered index seek).
    -- Phase B: Point-lookup by RecordID (clustered PK) to get keys.
    --
    -- This is faster than a per-row CROSS APPLY with inline resolution
    -- because it deduplicates IPs first (many visits share one IP).

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

        -- Apply resolved keys back to candidates
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


    -- =====================================================================
    -- 4. MERGE INTO PiXL.Match
    -- =====================================================================
    -- MatchType = 'ip', MatchKey = IPAddress.
    -- Unique constraint: (CompanyID, PiXLID, MatchType, MatchKey).
    --
    -- CRITICAL: Deduplicate the source by (CompanyID, PiXLID, IPAddress)
    -- because the same IP can appear in multiple visits within one batch.
    -- Without GROUP BY, MERGE throws "attempted to update same row twice".

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


    -- =====================================================================
    -- 5. UPDATE WATERMARK
    -- =====================================================================
    UPDATE ETL.MatchWatermark SET
        LastProcessedId = @MaxId,
        LastRunAt       = SYSUTCDATETIME(),
        RowsProcessed   = RowsProcessed + @CandidateCount,
        RowsMatched     = RowsMatched + @MatchedCount
    WHERE ProcessName = 'MatchLegacyVisits';


    -- =====================================================================
    -- 6. RETURN RESULTS
    -- =====================================================================
    DROP TABLE #Candidates;

    SELECT @CandidateCount AS RowsProcessed,
           @MatchedCount   AS RowsMatched,
           @LastId + 1     AS FromId,
           @MaxId          AS ToId;
END;
GO


-- =========================================================================
-- Verify
-- =========================================================================
IF OBJECT_ID('ETL.usp_MatchLegacyVisits', 'P') IS NOT NULL
    PRINT 'OK: ETL.usp_MatchLegacyVisits created';
ELSE
    PRINT 'ERROR: ETL.usp_MatchLegacyVisits not found!';

IF EXISTS (SELECT 1 FROM ETL.MatchWatermark WHERE ProcessName = 'MatchLegacyVisits')
    PRINT 'OK: MatchWatermark row exists for MatchLegacyVisits';
ELSE
    PRINT 'ERROR: MatchWatermark row not found!';
GO
