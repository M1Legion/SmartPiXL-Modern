-- ============================================================================
-- Migration 34: Fix Email Match MERGE duplicate-key crash
--
-- PROBLEM: ETL.usp_MatchVisits MERGE source can contain duplicate rows for
--   the same (CompanyID, PiXLID, NormalizedEmail) when multiple visits from
--   the same email arrive in one batch window. SQL Server throws error 8672:
--   "The MERGE statement attempted to UPDATE or DELETE the same row more 
--   than once. This happens when a target row matches more than one source row."
--
-- FIX: Add GROUP BY to the MERGE source subquery so each email key appears
--   exactly once. Uses MAX(VisitID) / MAX(ReceivedAt) to keep the latest
--   visit, and COALESCE + MAX for identity keys (first non-null wins).
--
-- SAFE TO RE-RUN: CREATE OR ALTER PROCEDURE is idempotent.
-- AUDIT REF: Ch09 Finding #2 (HIGH), Ch20 Top 10 #2
-- ============================================================================
SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE [ETL].[usp_MatchVisits]
    @BatchSize INT = 10000
AS
BEGIN
    SET NOCOUNT ON;

    -- =========================================================================
    -- 1. READ WATERMARK — incremental over PiXL.Visit.VisitID
    -- =========================================================================
    DECLARE @LastId BIGINT, @MaxId BIGINT;
    SELECT @LastId = LastProcessedId FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits';
    SELECT @MaxId  = MAX(VisitID) FROM PiXL.Visit;

    IF @MaxId IS NULL OR @MaxId <= @LastId
    BEGIN
        SELECT 0 AS RowsProcessed, 0 AS RowsMatched, @LastId AS FromId, @LastId AS ToId;
        RETURN;
    END

    IF @MaxId > @LastId + @BatchSize SET @MaxId = @LastId + @BatchSize;

    -- =========================================================================
    -- 2. COLLECT EMAIL CANDIDATES
    -- =========================================================================
    CREATE TABLE #Candidates (
        CompanyID       NVARCHAR(64),
        PiXLID          NVARCHAR(64),
        DeviceId        BIGINT,
        IpId            BIGINT,
        VisitID         BIGINT,
        ReceivedAt      DATETIME2,
        MatchEmail      NVARCHAR(320),
        NormalizedEmail  NVARCHAR(320),
        IndividualKey   BIGINT NULL,
        AddressKey      BIGINT NULL
    );

    INSERT INTO #Candidates (CompanyID, PiXLID, DeviceId, IpId, VisitID,
        ReceivedAt, MatchEmail, NormalizedEmail)
    SELECT v.CompanyID, v.PiXLID, v.DeviceId, v.IpId, v.VisitID,
        v.ReceivedAt, v.MatchEmail,
        LOWER(LTRIM(RTRIM(v.MatchEmail)))
    FROM PiXL.Visit v
    WHERE v.VisitID > @LastId AND v.VisitID <= @MaxId
      AND v.MatchEmail IS NOT NULL
      AND LEN(v.MatchEmail) > 5
      AND v.MatchEmail LIKE '%_@_%.__%';

    DECLARE @CandidateCount INT = @@ROWCOUNT;

    -- =========================================================================
    -- 3. RESOLVE AGAINST AUTOCONSUMER — EMAIL MATCH
    -- =========================================================================
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

    -- =========================================================================
    -- 4. MERGE INTO PiXL.Match — DEDUPLICATED SOURCE
    -- =========================================================================
    -- GROUP BY collapses multiple visits from the same email into one source row.
    -- MAX(VisitID) keeps the latest visit. COALESCE(MAX(...)) picks the first
    -- non-null identity key. This prevents error 8672 on duplicate match keys.

    IF @CandidateCount > 0
    BEGIN
        MERGE PiXL.Match AS target
        USING (
            SELECT CompanyID, PiXLID,
                   'email' AS MatchType,
                   NormalizedEmail AS MatchKey,
                   MAX(IndividualKey) AS IndividualKey,
                   MAX(AddressKey) AS AddressKey,
                   MAX(DeviceId) AS DeviceId,
                   MAX(IpId) AS IpId,
                   MAX(VisitID) AS VisitID,
                   MAX(ReceivedAt) AS ReceivedAt,
                   COUNT(*) AS BatchHitCount
            FROM #Candidates
            WHERE NormalizedEmail IS NOT NULL
            GROUP BY CompanyID, PiXLID, NormalizedEmail
        ) AS source
        ON target.CompanyID = source.CompanyID
           AND target.PiXLID = source.PiXLID
           AND target.MatchType = source.MatchType
           AND target.MatchKey = source.MatchKey

        WHEN MATCHED THEN UPDATE SET
            LatestVisitID = source.VisitID,
            LastSeen = source.ReceivedAt,
            HitCount = target.HitCount + source.BatchHitCount,
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
            source.BatchHitCount,
            CASE WHEN source.IndividualKey IS NOT NULL THEN SYSUTCDATETIME() END
        );
    END

    -- =========================================================================
    -- 5. UPDATE WATERMARK
    -- =========================================================================
    UPDATE ETL.MatchWatermark SET
        LastProcessedId = @MaxId,
        LastRunAt = SYSUTCDATETIME(),
        RowsProcessed = RowsProcessed + @CandidateCount,
        RowsMatched = RowsMatched + @MatchedCount
    WHERE ProcessName = 'MatchVisits';

    -- =========================================================================
    -- 6. RETURN RESULTS
    -- =========================================================================
    DROP TABLE #Candidates;

    SELECT @CandidateCount AS RowsProcessed,
           @MatchedCount AS RowsMatched,
           @LastId + 1 AS FromId,
           @MaxId AS ToId;
END
GO

PRINT '=== Migration 34 complete: Fixed email MERGE duplicate-key crash (GROUP BY dedup) ===';
GO
