-- =============================================
-- SmartPiXL Dashboard Views
-- 
-- DevOps & Management Dashboard Support
-- Aligned with FIELD_REFERENCE.md drill-down hierarchy
--
-- VIEWS FOR DRILL-DOWN UI:
--   vw_Dashboard_KPIs           - Main KPI cards (summary level)
--   vw_Dashboard_BotDetails     - Bot risk drill-down
--   vw_Dashboard_EvasionDetails - Evasion drill-down
--   vw_Dashboard_FingerprintDetails - Fingerprint signal drill-down
--   vw_Dashboard_TimingAnalysis - Script execution timing (bot detection)
--   vw_Dashboard_Trends         - Day-over-day comparisons
--   vw_Dashboard_LiveFeed       - Recent activity feed
--   vw_Dashboard_RiskBuckets    - Bot risk bucket breakdown
--
-- Run AFTER 06_AnalyticsViews.sql
-- =============================================

USE SmartPixl;
GO

-- =============================================
-- VIEW: Dashboard KPIs (Summary Cards)
-- Powers the main summary cards management sees first
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_KPIs', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_KPIs;
GO

CREATE VIEW dbo.vw_Dashboard_KPIs AS
SELECT 
    -- Time buckets
    CAST(GETUTCDATE() AS DATE) AS ReportDate,
    
    -- ======== TRAFFIC VOLUME ========
    (SELECT COUNT(*) FROM dbo.PiXL_Test 
     WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)) AS TotalHitsToday,
    
    (SELECT COUNT(*) FROM dbo.PiXL_Test 
     WHERE ReceivedAt >= DATEADD(DAY, -1, CAST(GETUTCDATE() AS DATE))
       AND ReceivedAt < CAST(GETUTCDATE() AS DATE)) AS TotalHitsYesterday,
    
    (SELECT COUNT(*) FROM dbo.PiXL_Test 
     WHERE ReceivedAt >= DATEADD(HOUR, -1, GETUTCDATE())) AS HitsLastHour,
    
    -- ======== UNIQUE DEVICES (Composite Fingerprint) ========
    (SELECT COUNT(DISTINCT CONCAT(
        ISNULL(dbo.GetQueryParam(QueryString, 'canvasFP'), ''),
        ISNULL(dbo.GetQueryParam(QueryString, 'webglFP'), ''),
        ISNULL(dbo.GetQueryParam(QueryString, 'audioHash'), '')
     )) FROM dbo.PiXL_Test 
     WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)) AS UniqueDevicesToday,
    
    (SELECT COUNT(DISTINCT IPAddress) FROM dbo.PiXL_Test 
     WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)) AS UniqueIPsToday,
    
    -- ======== BOT METRICS ========
    -- High risk (score >= 50)
    (SELECT COUNT(*) FROM dbo.PiXL_Test 
     WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)
       AND TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 50) AS HighRiskBotsToday,
    
    -- Bot rate as percentage
    (SELECT 
        CASE WHEN COUNT(*) = 0 THEN 0 
        ELSE CAST(
            SUM(CASE WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 50 THEN 1 ELSE 0 END) * 100.0 
            / COUNT(*) AS DECIMAL(5,2))
        END
     FROM dbo.PiXL_Test 
     WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)) AS BotRatePctToday,
    
    -- ======== EVASION METRICS ========
    (SELECT COUNT(*) FROM dbo.PiXL_Test 
     WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)
       AND (TRY_CAST(dbo.GetQueryParam(QueryString, 'canvasEvasion') AS INT) = 1
         OR TRY_CAST(dbo.GetQueryParam(QueryString, 'webglEvasion') AS INT) = 1)) AS EvasionDetectedToday,
    
    (SELECT 
        CASE WHEN COUNT(*) = 0 THEN 0 
        ELSE CAST(
            SUM(CASE WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'canvasEvasion') AS INT) = 1
                       OR TRY_CAST(dbo.GetQueryParam(QueryString, 'webglEvasion') AS INT) = 1 THEN 1 ELSE 0 END) * 100.0 
            / COUNT(*) AS DECIMAL(5,2))
        END
     FROM dbo.PiXL_Test 
     WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)) AS EvasionRatePctToday,
    
    -- ======== CROSS-NETWORK DEVICES ========
    -- Devices seen from multiple IPs today
    (SELECT COUNT(*) FROM (
        SELECT dbo.GetQueryParam(QueryString, 'canvasFP') AS FP
        FROM dbo.PiXL_Test 
        WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)
          AND dbo.GetQueryParam(QueryString, 'canvasFP') IS NOT NULL
        GROUP BY dbo.GetQueryParam(QueryString, 'canvasFP')
        HAVING COUNT(DISTINCT IPAddress) > 1
     ) x) AS CrossNetworkDevicesToday,
    
    -- ======== FINGERPRINT QUALITY ========
    (SELECT AVG(
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'canvasFP'), '')) > 0 THEN 15.0 ELSE 0 END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'webglFP'), '')) > 0 THEN 15.0 ELSE 0 END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'audioHash'), '')) > 0 THEN 15.0 ELSE 0 END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'fonts'), '')) > 50 THEN 20.0 ELSE 10 END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'uaFullVersion'), '')) > 0 THEN 15.0 ELSE 0 END
     ) FROM dbo.PiXL_Test 
     WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)) AS AvgFingerprintStrengthToday,
    
    -- ======== TIMING ========
    (SELECT AVG(TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS FLOAT))
     FROM dbo.PiXL_Test 
     WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)
       AND TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) IS NOT NULL) AS AvgScriptExecMsToday,
    
    -- Fast executions (< 10ms = likely bot)
    (SELECT COUNT(*) FROM dbo.PiXL_Test 
     WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)
       AND TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 10) AS FastExecsToday,
    
    -- ======== DEVICE BREAKDOWN ========
    (SELECT COUNT(*) FROM dbo.PiXL_Test 
     WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)
       AND dbo.GetDeviceType(
           TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT),
           TRY_CAST(dbo.GetQueryParam(QueryString, 'touch') AS INT),
           TRY_CAST(dbo.GetQueryParam(QueryString, 'uaMobile') AS INT)
       ) = 'Desktop') AS DesktopHitsToday,
    
    (SELECT COUNT(*) FROM dbo.PiXL_Test 
     WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)
       AND dbo.GetDeviceType(
           TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT),
           TRY_CAST(dbo.GetQueryParam(QueryString, 'touch') AS INT),
           TRY_CAST(dbo.GetQueryParam(QueryString, 'uaMobile') AS INT)
       ) = 'Mobile') AS MobileHitsToday,
    
    (SELECT COUNT(*) FROM dbo.PiXL_Test 
     WHERE ReceivedAt >= CAST(GETUTCDATE() AS DATE)
       AND dbo.GetDeviceType(
           TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT),
           TRY_CAST(dbo.GetQueryParam(QueryString, 'touch') AS INT),
           TRY_CAST(dbo.GetQueryParam(QueryString, 'uaMobile') AS INT)
       ) = 'Tablet') AS TabletHitsToday;
GO


-- =============================================
-- VIEW: Bot Risk Buckets
-- For the "click to drill down" from Bot Rate card
-- Aligned with FIELD_REFERENCE.md risk buckets
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_RiskBuckets', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_RiskBuckets;
GO

CREATE VIEW dbo.vw_Dashboard_RiskBuckets AS
SELECT 
    CAST(ReceivedAt AS DATE) AS DateBucket,
    
    -- Risk bucket per FIELD_REFERENCE.md
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 80 THEN 'High Risk'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 50 THEN 'Medium Risk'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 20 THEN 'Low Risk'
        ELSE 'Likely Human'
    END AS RiskBucket,
    
    -- Color coding for UI
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 80 THEN '#ef4444'  -- red
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 50 THEN '#f97316'  -- orange
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 20 THEN '#eab308'  -- yellow
        ELSE '#22c55e'  -- green
    END AS BucketColor,
    
    -- Score range for display
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 80 THEN '80-100'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 50 THEN '50-79'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 20 THEN '20-49'
        ELSE '0-19'
    END AS ScoreRange,
    
    COUNT(*) AS HitCount,
    COUNT(DISTINCT IPAddress) AS UniqueIPs,
    COUNT(DISTINCT dbo.GetQueryParam(QueryString, 'canvasFP')) AS UniqueFingerprints
    
FROM dbo.PiXL_Test
GROUP BY 
    CAST(ReceivedAt AS DATE),
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 80 THEN 'High Risk'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 50 THEN 'Medium Risk'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 20 THEN 'Low Risk'
        ELSE 'Likely Human'
    END,
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 80 THEN '#ef4444'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 50 THEN '#f97316'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 20 THEN '#eab308'
        ELSE '#22c55e'
    END,
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 80 THEN '80-100'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 50 THEN '50-79'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 20 THEN '20-49'
        ELSE '0-19'
    END;
GO


-- =============================================
-- VIEW: Bot Details (Drill-Down Level 2)
-- Shows individual bot signals for selected risk bucket
-- Per FIELD_REFERENCE.md: Click bucket → Show signals
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_BotDetails', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_BotDetails;
GO

CREATE VIEW dbo.vw_Dashboard_BotDetails AS
SELECT 
    p.Id,
    p.ReceivedAt,
    p.IPAddress,
    
    -- Composite fingerprint
    CONCAT(
        ISNULL(dbo.GetQueryParam(p.QueryString, 'canvasFP'), ''),
        '-',
        ISNULL(dbo.GetQueryParam(p.QueryString, 'webglFP'), ''),
        '-',
        ISNULL(dbo.GetQueryParam(p.QueryString, 'audioHash'), '')
    ) AS CompositeFingerprint,
    
    -- Bot metrics
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) AS BotScore,
    
    -- Risk bucket
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) >= 80 THEN 'High Risk'
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) >= 50 THEN 'Medium Risk'
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) >= 20 THEN 'Low Risk'
        ELSE 'Likely Human'
    END AS RiskBucket,
    
    -- Individual signals for drill-down
    dbo.GetQueryParam(p.QueryString, 'botSignals') AS BotSignalsList,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webdr') AS BIT) AS WebDriverDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'chromeObj') AS BIT) AS ChromeObjectPresent,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'chromeRuntime') AS BIT) AS ChromeRuntimePresent,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scriptExecTime') AS INT) AS ScriptExecutionTimeMs,
    dbo.GetQueryParam(p.QueryString, 'evasionDetected') AS EvasionToolsDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvasEvasion') AS BIT) AS CanvasEvasionDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglEvasion') AS BIT) AS WebGLEvasionDetected,
    
    -- Device context
    dbo.GetDeviceType(
        TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sw') AS INT),
        TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touch') AS INT),
        TRY_CAST(dbo.GetQueryParam(p.QueryString, 'uaMobile') AS INT)
    ) AS DeviceType,
    
    -- UA for investigation
    dbo.GetQueryParam(p.QueryString, 'ua') AS UserAgent,
    
    -- Page context
    dbo.GetQueryParam(p.QueryString, 'domain') AS Domain,
    dbo.GetQueryParam(p.QueryString, 'path') AS PagePath

FROM dbo.PiXL_Test p
WHERE TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) >= 20;  -- Only risky traffic
GO


-- =============================================
-- VIEW: Evasion Details (Drill-Down Level 2)
-- Shows evasion type breakdown
-- Per FIELD_REFERENCE.md: Canvas/WebGL/Both/None breakdown
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_EvasionDetails', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_EvasionDetails;
GO

CREATE VIEW dbo.vw_Dashboard_EvasionDetails AS
SELECT 
    p.Id,
    p.ReceivedAt,
    p.IPAddress,
    
    -- Composite fingerprint
    CONCAT(
        ISNULL(dbo.GetQueryParam(p.QueryString, 'canvasFP'), ''),
        '-',
        ISNULL(dbo.GetQueryParam(p.QueryString, 'webglFP'), ''),
        '-',
        ISNULL(dbo.GetQueryParam(p.QueryString, 'audioHash'), '')
    ) AS CompositeFingerprint,
    
    -- Evasion classification
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvasEvasion') AS INT) = 1 
         AND TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglEvasion') AS INT) = 1 THEN 'Both'
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvasEvasion') AS INT) = 1 THEN 'Canvas Only'
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglEvasion') AS INT) = 1 THEN 'WebGL Only'
        ELSE 'None'
    END AS EvasionType,
    
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvasEvasion') AS BIT) AS CanvasEvasionDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglEvasion') AS BIT) AS WebGLEvasionDetected,
    dbo.GetQueryParam(p.QueryString, 'evasionDetected') AS EvasionToolsDetected,
    
    -- Tor Browser signature check (1000x900)
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sw') AS INT) = 1000 
         AND TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sh') AS INT) = 900 THEN 1
        ELSE 0
    END AS TorSignatureDetected,
    
    -- Screen for analysis
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sw') AS INT) AS ScreenWidth,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sh') AS INT) AS ScreenHeight,
    
    -- Associated bot score
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) AS BotScore,
    
    -- Device context
    dbo.GetDeviceType(
        TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sw') AS INT),
        TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touch') AS INT),
        TRY_CAST(dbo.GetQueryParam(p.QueryString, 'uaMobile') AS INT)
    ) AS DeviceType,
    
    dbo.GetQueryParam(p.QueryString, 'ua') AS UserAgent

FROM dbo.PiXL_Test p
WHERE TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvasEvasion') AS INT) = 1 
   OR TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglEvasion') AS INT) = 1;
GO


-- =============================================
-- VIEW: Evasion Summary by Type
-- Aggregated for pie chart display
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_EvasionSummary', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_EvasionSummary;
GO

CREATE VIEW dbo.vw_Dashboard_EvasionSummary AS
SELECT 
    CAST(ReceivedAt AS DATE) AS DateBucket,
    
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'canvasEvasion') AS INT) = 1 
         AND TRY_CAST(dbo.GetQueryParam(QueryString, 'webglEvasion') AS INT) = 1 THEN 'Both Blocked'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'canvasEvasion') AS INT) = 1 THEN 'Canvas Blocked'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'webglEvasion') AS INT) = 1 THEN 'WebGL Blocked'
        ELSE 'No Evasion'
    END AS EvasionType,
    
    COUNT(*) AS HitCount,
    COUNT(DISTINCT IPAddress) AS UniqueIPs,
    
    -- Tor signature count
    SUM(CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT) = 1000 
         AND TRY_CAST(dbo.GetQueryParam(QueryString, 'sh') AS INT) = 900 THEN 1
        ELSE 0
    END) AS TorSignatureCount
    
FROM dbo.PiXL_Test
GROUP BY 
    CAST(ReceivedAt AS DATE),
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'canvasEvasion') AS INT) = 1 
         AND TRY_CAST(dbo.GetQueryParam(QueryString, 'webglEvasion') AS INT) = 1 THEN 'Both Blocked'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'canvasEvasion') AS INT) = 1 THEN 'Canvas Blocked'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'webglEvasion') AS INT) = 1 THEN 'WebGL Blocked'
        ELSE 'No Evasion'
    END;
GO


-- =============================================
-- VIEW: Fingerprint Details (Drill-Down Level 2)
-- Shows fingerprint signal contribution
-- Per FIELD_REFERENCE.md: Canvas → WebGL → Audio → Fonts breakdown
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_FingerprintDetails', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_FingerprintDetails;
GO

CREATE VIEW dbo.vw_Dashboard_FingerprintDetails AS
SELECT 
    -- Composite fingerprint
    CONCAT(
        ISNULL(dbo.GetQueryParam(QueryString, 'canvasFP'), ''),
        '-',
        ISNULL(dbo.GetQueryParam(QueryString, 'webglFP'), ''),
        '-',
        ISNULL(dbo.GetQueryParam(QueryString, 'audioHash'), '')
    ) AS CompositeFingerprint,
    
    -- Individual fingerprints
    dbo.GetQueryParam(QueryString, 'canvasFP') AS CanvasFingerprint,
    dbo.GetQueryParam(QueryString, 'webglFP') AS WebGLFingerprint,
    dbo.GetQueryParam(QueryString, 'audioHash') AS AudioFingerprintHash,
    dbo.GetQueryParam(QueryString, 'mathFP') AS MathFingerprint,
    dbo.GetQueryParam(QueryString, 'errorFP') AS ErrorFingerprint,
    
    -- Fonts (high entropy)
    dbo.GetQueryParam(QueryString, 'fonts') AS DetectedFonts,
    dbo.CountListItems(dbo.GetQueryParam(QueryString, 'fonts')) AS FontCount,
    
    -- GPU (high entropy)
    dbo.GetQueryParam(QueryString, 'gpu') AS GPURenderer,
    dbo.GetQueryParam(QueryString, 'gpuVendor') AS GPUVendor,
    
    -- Hardware
    TRY_CAST(dbo.GetQueryParam(QueryString, 'cores') AS INT) AS HardwareConcurrency,
    TRY_CAST(dbo.GetQueryParam(QueryString, 'mem') AS DECIMAL(5,2)) AS DeviceMemoryGB,
    
    -- Screen
    TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT) AS ScreenWidth,
    TRY_CAST(dbo.GetQueryParam(QueryString, 'sh') AS INT) AS ScreenHeight,
    TRY_CAST(dbo.GetQueryParam(QueryString, 'pd') AS DECIMAL(5,2)) AS PixelRatio,
    
    -- Fingerprint strength score
    (
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'canvasFP'), '')) > 0 THEN 15 ELSE 0 END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'webglFP'), '')) > 0 THEN 15 ELSE 0 END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'audioHash'), '')) > 0 THEN 15 ELSE 0 END +
        CASE 
            WHEN dbo.CountListItems(dbo.GetQueryParam(QueryString, 'fonts')) >= 20 THEN 20
            WHEN dbo.CountListItems(dbo.GetQueryParam(QueryString, 'fonts')) >= 10 THEN 15
            ELSE 10
        END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'uaFullVersion'), '')) > 0 THEN 15 ELSE 0 END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'tz'), '')) > 0 THEN 10 ELSE 0 END
    ) AS FingerprintStrength,
    
    -- Statistics
    COUNT(*) AS Hits,
    COUNT(DISTINCT IPAddress) AS UniqueIPs,
    MIN(ReceivedAt) AS FirstSeen,
    MAX(ReceivedAt) AS LastSeen
    
FROM dbo.PiXL_Test
WHERE dbo.GetQueryParam(QueryString, 'canvasFP') IS NOT NULL
GROUP BY 
    CONCAT(
        ISNULL(dbo.GetQueryParam(QueryString, 'canvasFP'), ''),
        '-',
        ISNULL(dbo.GetQueryParam(QueryString, 'webglFP'), ''),
        '-',
        ISNULL(dbo.GetQueryParam(QueryString, 'audioHash'), '')
    ),
    dbo.GetQueryParam(QueryString, 'canvasFP'),
    dbo.GetQueryParam(QueryString, 'webglFP'),
    dbo.GetQueryParam(QueryString, 'audioHash'),
    dbo.GetQueryParam(QueryString, 'mathFP'),
    dbo.GetQueryParam(QueryString, 'errorFP'),
    dbo.GetQueryParam(QueryString, 'fonts'),
    dbo.GetQueryParam(QueryString, 'gpu'),
    dbo.GetQueryParam(QueryString, 'gpuVendor'),
    TRY_CAST(dbo.GetQueryParam(QueryString, 'cores') AS INT),
    TRY_CAST(dbo.GetQueryParam(QueryString, 'mem') AS DECIMAL(5,2)),
    TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT),
    TRY_CAST(dbo.GetQueryParam(QueryString, 'sh') AS INT),
    TRY_CAST(dbo.GetQueryParam(QueryString, 'pd') AS DECIMAL(5,2)),
    dbo.CountListItems(dbo.GetQueryParam(QueryString, 'fonts')),
    CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'canvasFP'), '')) > 0 THEN 15 ELSE 0 END +
    CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'webglFP'), '')) > 0 THEN 15 ELSE 0 END +
    CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'audioHash'), '')) > 0 THEN 15 ELSE 0 END +
    CASE 
        WHEN dbo.CountListItems(dbo.GetQueryParam(QueryString, 'fonts')) >= 20 THEN 20
        WHEN dbo.CountListItems(dbo.GetQueryParam(QueryString, 'fonts')) >= 10 THEN 15
        ELSE 10
    END +
    CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'uaFullVersion'), '')) > 0 THEN 15 ELSE 0 END +
    CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'tz'), '')) > 0 THEN 10 ELSE 0 END;
GO


-- =============================================
-- VIEW: Script Timing Analysis
-- For drilling into timing-based bot detection
-- Per FIELD_REFERENCE.md: < 10ms = bot, 10-50ms = suspicious
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_TimingAnalysis', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_TimingAnalysis;
GO

CREATE VIEW dbo.vw_Dashboard_TimingAnalysis AS
SELECT 
    CAST(ReceivedAt AS DATE) AS DateBucket,
    
    -- Timing bucket per FIELD_REFERENCE.md
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 10 THEN 'Very Fast (<10ms) - Bot'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 50 THEN 'Fast (10-50ms) - Suspicious'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 500 THEN 'Normal (50-500ms) - Human'
        ELSE 'Slow (>500ms) - Human'
    END AS TimingBucket,
    
    -- Color for UI
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 10 THEN '#ef4444'   -- red
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 50 THEN '#f97316'  -- orange
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 500 THEN '#22c55e' -- green
        ELSE '#22c55e'  -- green
    END AS BucketColor,
    
    COUNT(*) AS HitCount,
    COUNT(DISTINCT IPAddress) AS UniqueIPs,
    AVG(TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS FLOAT)) AS AvgBotScore
    
FROM dbo.PiXL_Test
WHERE TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) IS NOT NULL
GROUP BY 
    CAST(ReceivedAt AS DATE),
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 10 THEN 'Very Fast (<10ms) - Bot'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 50 THEN 'Fast (10-50ms) - Suspicious'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 500 THEN 'Normal (50-500ms) - Human'
        ELSE 'Slow (>500ms) - Human'
    END,
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 10 THEN '#ef4444'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 50 THEN '#f97316'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 500 THEN '#22c55e'
        ELSE '#22c55e'
    END;
GO


-- =============================================
-- VIEW: Live Feed
-- Recent activity for real-time monitoring
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_LiveFeed', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_LiveFeed;
GO

CREATE VIEW dbo.vw_Dashboard_LiveFeed AS
SELECT TOP 100
    p.Id,
    p.ReceivedAt,
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
    
    -- Location
    dbo.GetQueryParam(p.QueryString, 'tz') AS Timezone,
    dbo.GetQueryParam(p.QueryString, 'domain') AS Domain

FROM dbo.PiXL_Test p
ORDER BY p.ReceivedAt DESC;
GO


-- =============================================
-- VIEW: Day-over-Day Trends
-- For trend arrows and comparisons
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_Trends', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_Trends;
GO

CREATE VIEW dbo.vw_Dashboard_Trends AS
SELECT 
    CAST(ReceivedAt AS DATE) AS DateBucket,
    
    -- Volume
    COUNT(*) AS TotalHits,
    COUNT(DISTINCT IPAddress) AS UniqueIPs,
    COUNT(DISTINCT CONCAT(
        ISNULL(dbo.GetQueryParam(QueryString, 'canvasFP'), ''),
        ISNULL(dbo.GetQueryParam(QueryString, 'webglFP'), ''),
        ISNULL(dbo.GetQueryParam(QueryString, 'audioHash'), '')
    )) AS UniqueDevices,
    
    -- Bot metrics
    SUM(CASE WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 50 THEN 1 ELSE 0 END) AS HighRiskCount,
    AVG(TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS FLOAT)) AS AvgBotScore,
    
    -- Evasion
    SUM(CASE WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'canvasEvasion') AS INT) = 1 
              OR TRY_CAST(dbo.GetQueryParam(QueryString, 'webglEvasion') AS INT) = 1 THEN 1 ELSE 0 END) AS EvasionCount,
    
    -- Device breakdown
    SUM(CASE WHEN dbo.GetDeviceType(
            TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT),
            TRY_CAST(dbo.GetQueryParam(QueryString, 'touch') AS INT),
            TRY_CAST(dbo.GetQueryParam(QueryString, 'uaMobile') AS INT)
        ) = 'Desktop' THEN 1 ELSE 0 END) AS DesktopCount,
    SUM(CASE WHEN dbo.GetDeviceType(
            TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT),
            TRY_CAST(dbo.GetQueryParam(QueryString, 'touch') AS INT),
            TRY_CAST(dbo.GetQueryParam(QueryString, 'uaMobile') AS INT)
        ) = 'Mobile' THEN 1 ELSE 0 END) AS MobileCount,
    SUM(CASE WHEN dbo.GetDeviceType(
            TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT),
            TRY_CAST(dbo.GetQueryParam(QueryString, 'touch') AS INT),
            TRY_CAST(dbo.GetQueryParam(QueryString, 'uaMobile') AS INT)
        ) = 'Tablet' THEN 1 ELSE 0 END) AS TabletCount,
    
    -- Fingerprint quality
    AVG(
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'canvasFP'), '')) > 0 THEN 15.0 ELSE 0 END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'webglFP'), '')) > 0 THEN 15.0 ELSE 0 END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(QueryString, 'audioHash'), '')) > 0 THEN 15.0 ELSE 0 END
    ) AS AvgFingerprintStrength,
    
    -- Timing
    AVG(TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS FLOAT)) AS AvgScriptExecMs,
    SUM(CASE WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT) < 10 THEN 1 ELSE 0 END) AS FastExecCount

FROM dbo.PiXL_Test
GROUP BY CAST(ReceivedAt AS DATE);
GO


-- =============================================
-- VIEW: GPU Distribution (for Fingerprint drill-down)
-- Per FIELD_REFERENCE.md: GPURenderer breakdown
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_GPUDistribution', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_GPUDistribution;
GO

CREATE VIEW dbo.vw_Dashboard_GPUDistribution AS
SELECT 
    dbo.GetQueryParam(QueryString, 'gpu') AS GPURenderer,
    dbo.GetQueryParam(QueryString, 'gpuVendor') AS GPUVendor,
    
    COUNT(*) AS HitCount,
    COUNT(DISTINCT IPAddress) AS UniqueIPs,
    COUNT(DISTINCT dbo.GetQueryParam(QueryString, 'canvasFP')) AS UniqueFingerprints
    
FROM dbo.PiXL_Test
WHERE dbo.GetQueryParam(QueryString, 'gpu') IS NOT NULL
  AND LEN(dbo.GetQueryParam(QueryString, 'gpu')) > 5
GROUP BY 
    dbo.GetQueryParam(QueryString, 'gpu'),
    dbo.GetQueryParam(QueryString, 'gpuVendor');
GO


-- =============================================
-- VIEW: Screen Resolution Distribution
-- Per FIELD_REFERENCE.md: Resolution breakdown with Tor detection
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_ScreenDistribution', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_ScreenDistribution;
GO

CREATE VIEW dbo.vw_Dashboard_ScreenDistribution AS
SELECT 
    TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT) AS ScreenWidth,
    TRY_CAST(dbo.GetQueryParam(QueryString, 'sh') AS INT) AS ScreenHeight,
    CONCAT(
        dbo.GetQueryParam(QueryString, 'sw'), 'x',
        dbo.GetQueryParam(QueryString, 'sh')
    ) AS Resolution,
    
    -- Tor signature
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT) = 1000 
         AND TRY_CAST(dbo.GetQueryParam(QueryString, 'sh') AS INT) = 900 THEN 'Tor Browser Signature'
        ELSE 'Normal'
    END AS ResolutionCategory,
    
    COUNT(*) AS HitCount,
    COUNT(DISTINCT IPAddress) AS UniqueIPs,
    
    -- Device type for this resolution
    MAX(dbo.GetDeviceType(
        TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT),
        TRY_CAST(dbo.GetQueryParam(QueryString, 'touch') AS INT),
        TRY_CAST(dbo.GetQueryParam(QueryString, 'uaMobile') AS INT)
    )) AS TypicalDeviceType
    
FROM dbo.PiXL_Test
WHERE dbo.GetQueryParam(QueryString, 'sw') IS NOT NULL
GROUP BY 
    TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT),
    TRY_CAST(dbo.GetQueryParam(QueryString, 'sh') AS INT),
    CONCAT(
        dbo.GetQueryParam(QueryString, 'sw'), 'x',
        dbo.GetQueryParam(QueryString, 'sh')
    ),
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'sw') AS INT) = 1000 
         AND TRY_CAST(dbo.GetQueryParam(QueryString, 'sh') AS INT) = 900 THEN 'Tor Browser Signature'
        ELSE 'Normal'
    END;
GO


PRINT '';
PRINT '============================================';
PRINT 'Dashboard Views Created Successfully!';
PRINT '============================================';
PRINT '';
PRINT 'SUMMARY LEVEL (KPIs):';
PRINT '  vw_Dashboard_KPIs          - Main summary cards with today/yesterday';
PRINT '';
PRINT 'DRILL-DOWN LEVEL 1 (Distributions):';
PRINT '  vw_Dashboard_RiskBuckets   - Bot risk bucket breakdown';
PRINT '  vw_Dashboard_EvasionSummary- Evasion type pie chart';
PRINT '  vw_Dashboard_TimingAnalysis- Script timing buckets';
PRINT '  vw_Dashboard_Trends        - Day-over-day comparison';
PRINT '';
PRINT 'DRILL-DOWN LEVEL 2 (Details):';
PRINT '  vw_Dashboard_BotDetails      - Individual bot records';
PRINT '  vw_Dashboard_EvasionDetails  - Individual evasion records';
PRINT '  vw_Dashboard_FingerprintDetails - Fingerprint signal breakdown';
PRINT '';
PRINT 'DRILL-DOWN LEVEL 3 (Granular):';
PRINT '  vw_Dashboard_GPUDistribution   - GPU breakdown';
PRINT '  vw_Dashboard_ScreenDistribution- Screen resolution breakdown';
PRINT '  vw_Dashboard_LiveFeed          - Real-time feed';
PRINT '';
GO
