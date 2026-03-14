-- ============================================================================
-- Migration 68: Convert HourlyRollup from unbounded view to date-ranged SP
--
-- PROBLEM: vw_Dash_HourlyRollup scans all PiXL.Parsed rows from last 30 days
--          (100M+ rows) even when the dashboard only needs 72 hours.
--          Consistently times out at 30s.
--
-- FIX: Create usp_Dash_HourlyRollup SP that accepts @Hours parameter and only
--      scans the needed date range. 72 hours * ~3K hits/hour = ~216K rows vs
--      30 days * 100K+ hits/hour = 72M+ rows.
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ── Phase 1: Create the stored procedure ────────────────────────────────────
IF OBJECT_ID('dbo.usp_Dash_HourlyRollup', 'P') IS NULL
    PRINT '>> Creating usp_Dash_HourlyRollup...'
ELSE
    PRINT '>> Updating usp_Dash_HourlyRollup...'
GO

CREATE OR ALTER PROCEDURE dbo.usp_Dash_HourlyRollup
    @Hours INT = 72
AS
BEGIN
    SET NOCOUNT ON;

    -- Clamp to reasonable range
    IF @Hours < 1 SET @Hours = 1;
    IF @Hours > 720 SET @Hours = 720;

    DECLARE @Cutoff DATETIME2 = DATEADD(HOUR, -@Hours, GETUTCDATE());

    SELECT
        DATEADD(HOUR, DATEDIFF(HOUR, 0, ReceivedAt), 0)        AS HourBucket,
        COUNT(*)                                                 AS TotalHits,
        SUM(CASE WHEN BotScore >= 50 THEN 1 ELSE 0 END)        AS BotHits,
        SUM(CASE WHEN BotScore >= 80 THEN 1 ELSE 0 END)        AS HighRiskHits,
        SUM(CASE WHEN BotScore < 20 OR BotScore IS NULL THEN 1 ELSE 0 END) AS LikelyHumanHits,
        COUNT(DISTINCT CanvasFingerprint)                        AS UniqueFingerprints,
        COUNT(DISTINCT IPAddress)                                AS UniqueIPs,
        AVG(CAST(BotScore AS FLOAT))                            AS AvgBotScore,
        AVG(CAST(CombinedThreatScore AS FLOAT))                 AS AvgThreatScore,
        MAX(BotScore)                                            AS MaxBotScore,
        SUM(CASE WHEN WebDriverDetected = 1 THEN 1 ELSE 0 END) AS WebDriverHits,
        SUM(CASE WHEN CanvasEvasionDetected = 1 THEN 1 ELSE 0 END) AS CanvasEvasionHits,
        SUM(CASE WHEN EvasionToolsDetected IS NOT NULL THEN 1 ELSE 0 END) AS EvasionToolHits,
        SUM(CASE WHEN IsSynthetic = 1 THEN 1 ELSE 0 END)       AS SyntheticHits
    FROM PiXL.Parsed WITH (NOLOCK)
    WHERE ReceivedAt >= @Cutoff
    GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, ReceivedAt), 0)
    ORDER BY HourBucket DESC;
END;
GO

PRINT '>> usp_Dash_HourlyRollup created successfully.'
GO
