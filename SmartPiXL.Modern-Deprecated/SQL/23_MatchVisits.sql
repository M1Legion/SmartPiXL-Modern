/*
    23_MatchVisits.sql
    ===================
    Creates ETL.usp_MatchVisits — the identity resolution stored procedure.
    
    This proc reads PiXL.Visit rows (by VisitID watermark), normalizes the
    email from MatchEmail, looks up AutoConsumer for IndividualKey/AddressKey,
    and MERGEs the results into PiXL.Match.
    
    PREREQUISITES:
      - SQL/19_DeviceIpVisitMatchTables.sql (PiXL.Visit, PiXL.Match, ETL.MatchWatermark)
      - SQL/22_AutoConsumerEmailIndex.sql (IX_AutoConsumer_EMail — critical for performance)
      - AutoUpdate database with dbo.AutoConsumer table on same instance
    
    TARGET: SQL Server 2025 (17.0.1050.2)
    
    Run on: SmartPiXL database, localhost\SQL2025
    Date:   2026-02-15
*/

USE SmartPiXL;
GO

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE [ETL].[usp_MatchVisits]
    @BatchSize INT = 10000
AS
BEGIN
    SET NOCOUNT ON;
    SET QUOTED_IDENTIFIER ON;

    -- =========================================================================
    -- 1. READ WATERMARK
    -- =========================================================================
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


    -- =========================================================================
    -- 2. BUILD CANDIDATE SET
    -- =========================================================================
    -- Only Visit rows with a non-null MatchEmail that looks like an email.
    -- The watermark advances even for rows without email (no re-processing).

    CREATE TABLE #Candidates (
        VisitID         BIGINT          NOT NULL  PRIMARY KEY,
        CompanyID       INT             NOT NULL,
        PiXLID          INT             NOT NULL,
        DeviceId        BIGINT          NULL,
        IpId            BIGINT          NULL,
        ReceivedAt      DATETIME2(3)    NOT NULL,
        RawEmail        NVARCHAR(200)   NOT NULL,
        NormalizedEmail VARCHAR(200)    NULL,      -- LOWER + LTRIM + RTRIM
        IndividualKey   VARCHAR(35)     NULL,      -- From AutoConsumer
        AddressKey      VARCHAR(35)     NULL       -- From AutoConsumer
    );

    INSERT INTO #Candidates (VisitID, CompanyID, PiXLID, DeviceId, IpId,
                              ReceivedAt, RawEmail, NormalizedEmail)
    SELECT
        v.VisitID, v.CompanyID, v.PiXLID, v.DeviceId, v.IpId,
        v.ReceivedAt, v.MatchEmail,
        -- Normalize: lowercase, trim whitespace
        LOWER(LTRIM(RTRIM(v.MatchEmail)))
    FROM PiXL.Visit v
    WHERE v.VisitID > @LastId AND v.VisitID <= @MaxId
      AND v.MatchEmail IS NOT NULL
      AND LEN(v.MatchEmail) > 5              -- a@b.co minimum
      AND v.MatchEmail LIKE '%_@_%.__%';     -- basic email shape

    DECLARE @CandidateCount INT = @@ROWCOUNT;


    -- =========================================================================
    -- 3. RESOLVE AGAINST AUTOCONSUMER — EMAIL MATCH
    -- =========================================================================
    -- CROSS APPLY + TOP 1 + ORDER BY RecordID DESC = "most recent record wins"
    -- This pattern leverages IX_AutoConsumer_EMail for a seek + 1 row fetch.
    --
    -- We retrieve IndividualKey (person identity) and AddressKey (household).
    -- IndividualKey groups all AC records for the same person (~1.22 per key).

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
    -- 4. MERGE INTO PiXL.Match
    -- =========================================================================
    -- Upsert pattern: new visitors get INSERT, returning visitors get UPDATE.
    -- The clustered index on (CompanyID, PiXLID, MatchType, MatchKey) makes
    -- the MERGE ON clause a clustered index seek.

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
            -- Only fill IndividualKey if we didn't have one before
            IndividualKey = COALESCE(target.IndividualKey, source.IndividualKey),
            AddressKey = COALESCE(target.AddressKey, source.AddressKey),
            -- Set MatchedAt only when IndividualKey is first resolved
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


    -- =========================================================================
    -- 5. UPDATE WATERMARK
    -- =========================================================================
    -- The watermark advances to @MaxId regardless of whether any emails existed.
    -- This prevents re-scanning rows that have no email.

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
END;
GO

PRINT 'ETL.usp_MatchVisits created (identity resolution via AutoConsumer email matching)';
GO
