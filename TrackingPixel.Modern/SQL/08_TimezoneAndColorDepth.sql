-- =============================================
-- SmartPiXL Patch: Timezone + ColorDepth Enhancement
-- 
-- FIXES:
--   1. LocalReceivedAt - Computed from ReceivedAt + TimezoneOffsetMins
--   2. ColorDepth added to views - Bot detection signal
--   3. ColorDepthAnomaly flag - 0, undefined, or mismatched values
--
-- Run AFTER 07_DashboardViews.sql
-- =============================================

USE SmartPixl;
GO

-- =============================================
-- UPDATED: Dashboard LiveFeed with Local Time + ColorDepth
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_LiveFeed', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_LiveFeed;
GO

CREATE VIEW dbo.vw_Dashboard_LiveFeed AS
SELECT TOP 100
    p.Id,
    
    -- UTC timestamp (what's stored in DB)
    p.ReceivedAt,
    
    -- Local timestamp (computed from client's timezone offset)
    -- tzo is in minutes (positive = behind UTC, e.g., EST = 300)
    -- To get local time: ReceivedAt - offset
    DATEADD(
        MINUTE, 
        -ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tzo') AS INT), 0),
        p.ReceivedAt
    ) AS LocalReceivedAt,
    
    -- Timezone info
    dbo.GetQueryParam(p.QueryString, 'tz') AS Timezone,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tzo') AS INT) AS TimezoneOffsetMins,
    
    p.IPAddress,
    p.CompanyID,
    p.PiXLID,
    
    -- Device
    dbo.GetDeviceType(
        TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sw') AS INT),
        TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touch') AS INT),
        TRY_CAST(dbo.GetQueryParam(p.QueryString, 'uaMobile') AS INT)
    ) AS DeviceType,
    
    -- Screen
    CONCAT(
        ISNULL(dbo.GetQueryParam(p.QueryString, 'sw'), '?'), 'x',
        ISNULL(dbo.GetQueryParam(p.QueryString, 'sh'), '?')
    ) AS Screen,
    
    -- ColorDepth (Bot Detection Signal)
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cd') AS INT) AS ColorDepth,
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cd') AS INT) IS NULL THEN 1  -- undefined = bot
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cd') AS INT) = 0 THEN 1       -- 0 = headless
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cd') AS INT) NOT IN (24, 30, 32, 48) THEN 1  -- unusual
        ELSE 0
    END AS ColorDepthAnomaly,
    
    -- Bot risk
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) AS BotScore,
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) >= 80 THEN 'High Risk'
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) >= 50 THEN 'Medium Risk'
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) >= 20 THEN 'Low Risk'
        ELSE 'Likely Human'
    END AS RiskBucket,
    
    -- Evasion
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvasEvasion') AS INT) = 1 
          OR TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglEvasion') AS INT) = 1 THEN 1
        ELSE 0
    END AS EvasionDetected,
    
    -- Fingerprint preview
    LEFT(dbo.GetQueryParam(p.QueryString, 'canvasFP'), 8) AS CanvasFPPreview,
    
    -- Domain
    dbo.GetQueryParam(p.QueryString, 'domain') AS Domain

FROM dbo.PiXL_Test p
ORDER BY p.ReceivedAt DESC;
GO


-- =============================================
-- UPDATED: Bot Details with ColorDepth
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_BotDetails', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_BotDetails;
GO

CREATE VIEW dbo.vw_Dashboard_BotDetails AS
SELECT 
    Id,
    ReceivedAt,
    
    -- Local time
    DATEADD(
        MINUTE, 
        -ISNULL(TRY_CAST(dbo.GetQueryParam(QueryString, 'tzo') AS INT), 0),
        ReceivedAt
    ) AS LocalReceivedAt,
    
    IPAddress,
    CompanyID,
    
    -- Bot score and bucket
    TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) AS BotScore,
    dbo.GetQueryParam(QueryString, 'botSignals') AS BotSignals,
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 80 THEN 'High Risk'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 50 THEN 'Medium Risk'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 20 THEN 'Low Risk'
        ELSE 'Likely Human'
    END AS RiskBucket,
    
    -- Key bot indicators
    TRY_CAST(dbo.GetQueryParam(QueryString, 'webdr') AS INT) AS WebDriverDetected,
    TRY_CAST(dbo.GetQueryParam(QueryString, 'chromeObj') AS INT) AS ChromeObjectPresent,
    TRY_CAST(dbo.GetQueryParam(QueryString, 'chromeRuntime') AS INT) AS ChromeRuntimePresent,
    TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) AS ScriptExecMs,
    
    -- ColorDepth (NEW BOT SIGNAL)
    TRY_CAST(dbo.GetQueryParam(QueryString, 'cd') AS INT) AS ColorDepth,
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'cd') AS INT) IS NULL THEN 'Missing'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'cd') AS INT) = 0 THEN 'Zero (Bot)'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'cd') AS INT) NOT IN (24, 30, 32, 48) THEN 'Unusual'
        ELSE 'Normal'
    END AS ColorDepthStatus,
    
    -- Evasion signals
    TRY_CAST(dbo.GetQueryParam(QueryString, 'canvasEvasion') AS INT) AS CanvasEvasion,
    TRY_CAST(dbo.GetQueryParam(QueryString, 'webglEvasion') AS INT) AS WebGLEvasion,
    dbo.GetQueryParam(QueryString, 'evasionDetected') AS EvasionTools,
    
    -- Timing (fast = bot)
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 10 THEN 'Too Fast (<10ms)'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 50 THEN 'Suspicious (10-50ms)'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 500 THEN 'Normal'
        ELSE 'Slow'
    END AS TimingBucket,
    
    -- Device info
    dbo.GetDeviceType(
        TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT),
        TRY_CAST(dbo.GetQueryParam(QueryString, 'touch') AS INT),
        TRY_CAST(dbo.GetQueryParam(QueryString, 'uaMobile') AS INT)
    ) AS DeviceType,
    dbo.GetQueryParam(QueryString, 'ua') AS UserAgent,
    
    -- Fingerprints for correlation
    LEFT(dbo.GetQueryParam(QueryString, 'canvasFP'), 8) AS CanvasFP,
    LEFT(dbo.GetQueryParam(QueryString, 'webglFP'), 8) AS WebGLFP

FROM dbo.PiXL_Test
WHERE ReceivedAt >= DATEADD(DAY, -7, GETUTCDATE());  -- Last 7 days
GO


-- =============================================
-- NEW VIEW: Color Depth Anomaly Report
-- Per management request: identify bots via ColorDepth
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_ColorDepthAnomalies', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_ColorDepthAnomalies;
GO

CREATE VIEW dbo.vw_Dashboard_ColorDepthAnomalies AS
SELECT 
    TRY_CAST(dbo.GetQueryParam(QueryString, 'cd') AS INT) AS ColorDepth,
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'cd') AS INT) IS NULL THEN 'Missing (Bot Likely)'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'cd') AS INT) = 0 THEN 'Zero (Headless)'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'cd') AS INT) = 24 THEN 'Normal (8-bit)'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'cd') AS INT) = 30 THEN 'HDR (10-bit)'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'cd') AS INT) = 32 THEN 'Normal (8-bit+alpha)'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'cd') AS INT) = 48 THEN 'Deep Color (16-bit)'
        ELSE 'Unusual Value'
    END AS ColorDepthCategory,
    COUNT(*) AS HitCount,
    COUNT(DISTINCT IPAddress) AS UniqueIPs,
    AVG(TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS FLOAT)) AS AvgBotScore,
    
    -- Percentage that are high risk
    CAST(
        SUM(CASE WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 50 THEN 1 ELSE 0 END) * 100.0 
        / NULLIF(COUNT(*), 0) AS DECIMAL(5,2)
    ) AS HighRiskPct

FROM dbo.PiXL_Test
WHERE ReceivedAt >= DATEADD(DAY, -7, GETUTCDATE())
GROUP BY TRY_CAST(dbo.GetQueryParam(QueryString, 'cd') AS INT);
GO


-- =============================================
-- UPDATED: Evasion Details with ColorDepth
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_EvasionDetails', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_EvasionDetails;
GO

CREATE VIEW dbo.vw_Dashboard_EvasionDetails AS
SELECT 
    Id,
    ReceivedAt,
    DATEADD(
        MINUTE, 
        -ISNULL(TRY_CAST(dbo.GetQueryParam(QueryString, 'tzo') AS INT), 0),
        ReceivedAt
    ) AS LocalReceivedAt,
    IPAddress,
    
    -- Evasion type
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'canvasEvasion') AS INT) = 1 
         AND TRY_CAST(dbo.GetQueryParam(QueryString, 'webglEvasion') AS INT) = 1 THEN 'Both Blocked'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'canvasEvasion') AS INT) = 1 THEN 'Canvas Blocked'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'webglEvasion') AS INT) = 1 THEN 'WebGL Blocked'
        ELSE 'None'
    END AS EvasionType,
    
    -- Tor signature (exact screen size + disabled WebGL)
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT) = 1000 
         AND TRY_CAST(dbo.GetQueryParam(QueryString, 'sh') AS INT) = 900 THEN 1
        ELSE 0
    END AS TorSignatureDetected,
    
    -- ColorDepth (often wrong in Tor/privacy browsers)
    TRY_CAST(dbo.GetQueryParam(QueryString, 'cd') AS INT) AS ColorDepth,
    
    -- Bot correlation
    TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) AS BotScore,
    dbo.GetQueryParam(QueryString, 'botSignals') AS BotSignals,
    
    -- What was detected
    dbo.GetQueryParam(QueryString, 'evasionDetected') AS EvasionToolsDetected,
    
    -- Device
    dbo.GetDeviceType(
        TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT),
        TRY_CAST(dbo.GetQueryParam(QueryString, 'touch') AS INT),
        TRY_CAST(dbo.GetQueryParam(QueryString, 'uaMobile') AS INT)
    ) AS DeviceType,
    
    CONCAT(
        dbo.GetQueryParam(QueryString, 'sw'), 'x',
        dbo.GetQueryParam(QueryString, 'sh')
    ) AS Screen,
    
    dbo.GetQueryParam(QueryString, 'ua') AS UserAgent

FROM dbo.PiXL_Test
WHERE ReceivedAt >= DATEADD(DAY, -7, GETUTCDATE())
  AND (TRY_CAST(dbo.GetQueryParam(QueryString, 'canvasEvasion') AS INT) = 1
    OR TRY_CAST(dbo.GetQueryParam(QueryString, 'webglEvasion') AS INT) = 1);
GO


PRINT '==============================================';
PRINT 'Timezone + ColorDepth Enhancement Applied';
PRINT '==============================================';
PRINT '';
PRINT 'Updated Views:';
PRINT '  vw_Dashboard_LiveFeed      - Added LocalReceivedAt, ColorDepth, ColorDepthAnomaly';
PRINT '  vw_Dashboard_BotDetails    - Added LocalReceivedAt, ColorDepth, ColorDepthStatus';
PRINT '  vw_Dashboard_EvasionDetails- Added LocalReceivedAt, ColorDepth';
PRINT '';
PRINT 'New Views:';
PRINT '  vw_Dashboard_ColorDepthAnomalies - Aggregated ColorDepth analysis for bot detection';
PRINT '';
PRINT 'ColorDepth Bot Detection Logic:';
PRINT '  NULL/Missing = Bot (API not implemented)';
PRINT '  0            = Headless browser';
PRINT '  24/30/32/48  = Normal values';
PRINT '  Other        = Unusual, warrants investigation';
PRINT '';
GO
