-- ============================================================================
-- Migration 69: Email Match Gating + Match Type Visibility
--
-- 1. Set MatchEmail = 0 for legacy PiXLs (they fire image pixels, not JS, so 
--    they can't collect email — plus they don't pay for email resolution)
-- 2. Update usp_MatchVisits to gate on PiXL.Settings.MatchEmail and exclude
--    legacy PiXLs from email-based identity resolution
-- 3. Create usp_Dash_MatchBreakdown — per-company match-type counts with
--    entitlement flags so legacy clients see what they pay for vs. what
--    each match type would provide if they upgraded
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ════════════════════════════════════════════════════════════════════════════
-- Phase 1: Disable email matching for all legacy PiXLs
-- ════════════════════════════════════════════════════════════════════════════
DECLARE @LegacyCount INT;

UPDATE PiXL.Settings
SET MatchEmail = 0
WHERE PiXLLegacy IS NOT NULL
  AND MatchEmail = 1;

SET @LegacyCount = @@ROWCOUNT;
PRINT '>> Phase 1: Set MatchEmail=0 for ' + CAST(@LegacyCount AS VARCHAR) + ' legacy PiXLs';
GO

-- ════════════════════════════════════════════════════════════════════════════
-- Phase 2: Rebuild usp_MatchVisits with proper gating
--
-- Changes from original:
--   a) JOIN to PiXL.Settings — gate on MatchEmail = 1
--   b) Exclude legacy PiXLs (PiXLLegacy IS NOT NULL)
--   c) Both checks ensure legacy clients never get email match rows
-- ════════════════════════════════════════════════════════════════════════════
PRINT '>> Phase 2: Rebuilding ETL.usp_MatchVisits with Settings gating...'
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
    -- 2. COLLECT EMAIL CANDIDATES — gated by Settings.MatchEmail
    -- =========================================================================
    CREATE TABLE #Candidates (
        CompanyID       INT,
        PiXLID          INT,
        DeviceId        BIGINT,
        IpId            BIGINT,
        VisitID         BIGINT,
        ReceivedAt      DATETIME2,
        MatchEmail      NVARCHAR(320),
        NormalizedEmail NVARCHAR(320),
        IndividualKey   VARCHAR(35) NULL,
        AddressKey      VARCHAR(35) NULL
    );

    INSERT INTO #Candidates (CompanyID, PiXLID, DeviceId, IpId, VisitID,
        ReceivedAt, MatchEmail, NormalizedEmail)
    SELECT v.CompanyID, v.PiXLID, v.DeviceId, v.IpId, v.VisitID,
        v.ReceivedAt, v.MatchEmail,
        LOWER(LTRIM(RTRIM(v.MatchEmail)))
    FROM PiXL.Visit v
    LEFT JOIN PiXL.Settings s
        ON s.CompanyId = v.CompanyID
       AND s.PiXLId   = v.PiXLID
    WHERE v.VisitID > @LastId AND v.VisitID <= @MaxId
      AND v.MatchEmail IS NOT NULL
      AND LEN(v.MatchEmail) > 5
      AND v.MatchEmail LIKE '%_@_%.__%'
      -- Gate: PiXL must have email matching enabled (default=1 for modern)
      AND ISNULL(s.MatchEmail, 1) = 1
      -- Belt & suspenders: exclude legacy PiXLs even if MatchEmail somehow = 1
      AND s.PiXLLegacy IS NULL;

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
                   MIN(VisitID) AS FirstVisitID,
                   MAX(VisitID) AS LatestVisitID,
                   MIN(ReceivedAt) AS FirstSeen,
                   MAX(ReceivedAt) AS LastSeen,
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
            LatestVisitID = source.LatestVisitID,
            LastSeen = source.LastSeen,
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
            source.FirstVisitID, source.LatestVisitID,
            source.FirstSeen, source.LastSeen,
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

    DROP TABLE #Candidates;

    SELECT @CandidateCount AS RowsProcessed,
           @MatchedCount   AS RowsMatched,
           @LastId + 1     AS FromId,
           @MaxId          AS ToId;
END;
GO

PRINT '>> Phase 2: usp_MatchVisits rebuilt with Settings gating.'
GO

-- ════════════════════════════════════════════════════════════════════════════
-- Phase 3: Match Breakdown Dashboard SP
--
-- Returns per-company match-type counts with entitlement flags.
-- Legacy clients see: IP matches (entitled) + email/geo counts (uplift).
-- Modern clients see: all match types (entitled).
-- ════════════════════════════════════════════════════════════════════════════
PRINT '>> Phase 3: Creating usp_Dash_MatchBreakdown...'
GO

CREATE OR ALTER PROCEDURE dbo.usp_Dash_MatchBreakdown
AS
BEGIN
    SET NOCOUNT ON;

    -- Per-company match counts by type, with entitlement flags
    SELECT
        c.CompanyID,
        c.CompanyName,

        -- Is this company legacy-only? (any PiXL has PiXLLegacy set)
        CAST(CASE WHEN MAX(CASE WHEN s.PiXLLegacy IS NOT NULL THEN 1 ELSE 0 END) = 1
             THEN 1 ELSE 0 END AS BIT) AS IsLegacy,

        -- Match counts by type
        SUM(CASE WHEN m.MatchType = 'ip'    THEN 1 ELSE 0 END)   AS IpMatches,
        SUM(CASE WHEN m.MatchType = 'email' THEN 1 ELSE 0 END)   AS EmailMatches,

        -- Resolved counts by type (have IndividualKey)
        SUM(CASE WHEN m.MatchType = 'ip'    AND m.IndividualKey IS NOT NULL THEN 1 ELSE 0 END) AS IpResolved,
        SUM(CASE WHEN m.MatchType = 'email' AND m.IndividualKey IS NOT NULL THEN 1 ELSE 0 END) AS EmailResolved,

        -- Geo-enriched count (ip matches resolved via geo proximity)
        SUM(CASE WHEN m.MatchType = 'ip' AND m.ConfidenceScore IS NOT NULL THEN 1 ELSE 0 END) AS GeoEnriched,

        -- Entitlement flags (what this company pays for)
        -- Legacy companies are NOT entitled to email matching
        CAST(MAX(CAST(ISNULL(s.MatchIP, 1) AS INT)) AS BIT)    AS EntitledIp,
        CAST(CASE
            WHEN MAX(CASE WHEN s.PiXLLegacy IS NOT NULL THEN 1 ELSE 0 END) = 1 THEN 0
            ELSE MAX(CAST(ISNULL(s.MatchEmail, 1) AS INT))
        END AS BIT) AS EntitledEmail,
        CAST(MAX(CAST(ISNULL(s.MatchGeoSupplemental, 0) AS INT)) AS BIT) AS EntitledGeo,

        -- Total hits for context
        SUM(m.HitCount) AS TotalHits,
        COUNT(*)        AS TotalMatchRows
    FROM PiXL.Company c
    JOIN PiXL.Settings s ON s.CompanyId = c.CompanyID
    LEFT JOIN PiXL.Match m
        ON m.CompanyID = c.CompanyID
    WHERE c.IsActive = 1
    GROUP BY c.CompanyID, c.CompanyName
    HAVING COUNT(m.MatchId) > 0
    ORDER BY SUM(CASE WHEN m.MatchType = 'ip' THEN 1 ELSE 0 END) DESC;
END;
GO

PRINT '>> Phase 3: usp_Dash_MatchBreakdown created.'
GO

-- ════════════════════════════════════════════════════════════════════════════
-- Phase 4: Verify state
-- ════════════════════════════════════════════════════════════════════════════
SELECT
    'Legacy PiXLs with MatchEmail=0' AS Check_,
    COUNT(*) AS Cnt
FROM PiXL.Settings
WHERE PiXLLegacy IS NOT NULL AND MatchEmail = 0
UNION ALL
SELECT
    'Legacy PiXLs with MatchEmail=1',
    COUNT(*)
FROM PiXL.Settings
WHERE PiXLLegacy IS NOT NULL AND MatchEmail = 1
UNION ALL
SELECT
    'Modern PiXLs with MatchEmail=1',
    COUNT(*)
FROM PiXL.Settings
WHERE PiXLLegacy IS NULL AND MatchEmail = 1;
GO
