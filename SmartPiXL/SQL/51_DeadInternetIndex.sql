-- ============================================================================
-- Migration 51: Dead Internet Index View (Phase 8)
-- ============================================================================
-- Creates dbo.vw_Dash_DeadInternet — per customer per week breakdown of
-- traffic into definite-bot, zero-mouse (likely bot), and likely-human.
--
-- SmartPiXL is uniquely positioned to measure this because it sees raw
-- traffic across a diverse set of websites before ad networks filter it.
--
-- Trend over time shows whether a customer's traffic quality is improving
-- or degrading — and at a macro level, whether the internet is getting
-- more or less "dead" (automated).
--
-- Design doc reference: §8.3 item 7 (Dead Internet Index)
-- ============================================================================
USE SmartPiXL;
GO

SET NOCOUNT ON;
GO

PRINT '--- 51: Dead Internet Index ---';
GO

-- =====================================================================
-- Step 1: Dead Internet Index view
-- =====================================================================
-- Categories:
--   Definite Bot:   BotScore >= 70
--   Zero-Mouse Bot: BotScore < 70 AND MouseMoveCount = 0 AND UserScrolled = 0
--   Likely Human:   Has mouse/scroll activity AND BotScore < 50
--   Ambiguous:      Everything else
-- =====================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_DeadInternet
AS
SELECT
    p.CompanyID,
    DATEADD(WEEK, DATEDIFF(WEEK, 0, p.ReceivedAt), 0) AS WeekStart,
    COUNT_BIG(*)                                        AS TotalHits,

    -- Definite bot: high bot score
    SUM(CASE WHEN p.BotScore >= 70 THEN 1 ELSE 0 END)  AS DefiniteBotHits,

    -- Zero-mouse: no bot score but no mouse activity either (suspicious)
    SUM(CASE
        WHEN ISNULL(p.BotScore, 0) < 70
         AND ISNULL(p.MouseMoveCount, 0) = 0
         AND ISNULL(p.UserScrolled, 0) = 0
        THEN 1 ELSE 0
    END)                                                AS ZeroMouseHits,

    -- Likely human: has behavioral signals and low bot score
    SUM(CASE
        WHEN ISNULL(p.BotScore, 0) < 50
         AND (ISNULL(p.MouseMoveCount, 0) > 0 OR ISNULL(p.UserScrolled, 0) = 1)
        THEN 1 ELSE 0
    END)                                                AS LikelyHumanHits,

    -- Percentages
    CASE WHEN COUNT_BIG(*) = 0 THEN 0 ELSE
        CAST(100.0 * SUM(CASE WHEN p.BotScore >= 70 THEN 1 ELSE 0 END)
            / COUNT_BIG(*) AS DECIMAL(5,2))
    END                                                 AS DefiniteBotPct,

    CASE WHEN COUNT_BIG(*) = 0 THEN 0 ELSE
        CAST(100.0 * SUM(CASE
            WHEN ISNULL(p.BotScore, 0) < 70
             AND ISNULL(p.MouseMoveCount, 0) = 0
             AND ISNULL(p.UserScrolled, 0) = 0
            THEN 1 ELSE 0 END)
            / COUNT_BIG(*) AS DECIMAL(5,2))
    END                                                 AS ZeroMousePct,

    CASE WHEN COUNT_BIG(*) = 0 THEN 0 ELSE
        CAST(100.0 * SUM(CASE
            WHEN ISNULL(p.BotScore, 0) < 50
             AND (ISNULL(p.MouseMoveCount, 0) > 0 OR ISNULL(p.UserScrolled, 0) = 1)
            THEN 1 ELSE 0 END)
            / COUNT_BIG(*) AS DECIMAL(5,2))
    END                                                 AS LikelyHumanPct

FROM PiXL.Parsed p
GROUP BY p.CompanyID, DATEADD(WEEK, DATEDIFF(WEEK, 0, p.ReceivedAt), 0);
GO

-- =====================================================================
-- Step 2: Verification
-- =====================================================================
IF OBJECT_ID('dbo.vw_Dash_DeadInternet', 'V') IS NOT NULL
    PRINT '  OK: dbo.vw_Dash_DeadInternet exists';
ELSE
    PRINT '  ERROR: dbo.vw_Dash_DeadInternet missing!';
GO

PRINT '--- 51: Dead Internet Index complete ---';
GO
