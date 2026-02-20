-- ============================================================================
-- vw_Dash_PipelineHealth — Single-row snapshot of full pipeline health
-- Powers the Tron dashboard pipeline visualization and health metrics.
-- Covers: PiXL.Raw, Parsed, Device, IP, Visit, Match
--         ETL.Watermark (ParseNewHits), ETL.MatchWatermark (MatchVisits)
-- ============================================================================
-- Usage:  SELECT * FROM vw_Dash_PipelineHealth
-- Called by: InfraHealthService.ProbePipelineAsync()
--            /api/dash/pipeline endpoint
-- ============================================================================

SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER VIEW [dbo].[vw_Dash_PipelineHealth] AS
SELECT
    -- Table row counts
    (SELECT COUNT(*)          FROM PiXL.Raw)      AS TestRows,
    (SELECT COUNT(*)          FROM PiXL.Parsed)   AS ParsedRows,
    (SELECT COUNT(*)          FROM PiXL.Device)   AS DeviceRows,
    (SELECT COUNT(*)          FROM PiXL.IP)       AS IpRows,
    (SELECT COUNT(*)          FROM PiXL.Visit)    AS VisitRows,
    (SELECT COUNT(*)          FROM PiXL.Match)    AS MatchRows,

    -- Max IDs (watermark comparison)
    (SELECT MAX(Id)           FROM PiXL.Raw)      AS MaxTestId,
    (SELECT MAX(SourceId)     FROM PiXL.Parsed)   AS MaxParsedSourceId,
    (SELECT MAX(VisitID)      FROM PiXL.Visit)    AS MaxVisitId,
    (SELECT MAX(MatchId)      FROM PiXL.Match)    AS MaxMatchId,

    -- ETL ParseNewHits watermark
    (SELECT LastProcessedId   FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits')   AS ParseWatermark,
    (SELECT RowsProcessed     FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits')   AS ParseTotalProcessed,
    (SELECT LastRunAt         FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits')   AS ParseLastRunAt,

    -- ETL MatchVisits watermark
    (SELECT LastProcessedId   FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits')  AS MatchWatermark,
    (SELECT RowsProcessed     FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits')  AS MatchTotalProcessed,
    (SELECT RowsMatched       FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits')  AS MatchTotalMatched,
    (SELECT LastRunAt         FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits')  AS MatchLastRunAt,

    -- Match resolution stats
    (SELECT COUNT(*) FROM PiXL.Match WHERE IndividualKey IS NOT NULL)    AS MatchesResolved,
    (SELECT COUNT(*) FROM PiXL.Match WHERE IndividualKey IS NULL)        AS MatchesPending,

    -- Visit with email (match-eligible)
    (SELECT COUNT(*) FROM PiXL.Visit WHERE MatchEmail IS NOT NULL)       AS VisitsWithEmail,

    -- Computed lags
    (SELECT MAX(Id) FROM PiXL.Raw) - 
        ISNULL((SELECT LastProcessedId FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits'), 0)
            AS ParseLag,

    ISNULL((SELECT MAX(VisitID) FROM PiXL.Visit), 0) -
        ISNULL((SELECT LastProcessedId FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits'), 0)
            AS MatchLag,

    -- Latest timestamps per table
    (SELECT MAX(ReceivedAt) FROM PiXL.Raw)      AS TestLatest,
    (SELECT MAX(ParsedAt)   FROM PiXL.Parsed)   AS ParsedLatest,
    (SELECT MAX(LastSeen)   FROM PiXL.Device)    AS DeviceLatest,
    (SELECT MAX(LastSeen)   FROM PiXL.IP)        AS IpLatest,
    (SELECT MAX(CreatedAt)  FROM PiXL.Visit)     AS VisitLatest,
    (SELECT MAX(LastSeen)   FROM PiXL.Match)     AS MatchLatest,

    -- Device uniqueness stats
    (SELECT COUNT(DISTINCT DeviceId) FROM PiXL.Visit WHERE DeviceId IS NOT NULL) AS UniqueDevicesInVisits,
    (SELECT COUNT(DISTINCT IpId)     FROM PiXL.Visit WHERE IpId IS NOT NULL)     AS UniqueIpsInVisits;
GO
