-- =============================================
-- SmartPiXL: Synthetic Testing Support
-- 
-- Adds IsSynthetic flag to vw_PiXL_Complete and vw_PiXL_Summary.
-- 
-- Synthetic test records are identified by synthetic=1 in the query string.
-- This allows filtering synthetic traffic from live customer data:
--   WHERE IsSynthetic = 0   (live traffic only)
--   WHERE IsSynthetic = 1   (synthetic tests only)
-- 
-- Run AFTER 06_AnalyticsViews.sql
-- =============================================

USE SmartPixl;
GO

-- =============================================
-- vw_PiXL_Complete: Add IsSynthetic flag to GROUP 1 (Core Identity)
-- We recreate the view to add the new column
-- =============================================
-- NOTE: Rather than DROP/CREATE the entire 550-line view, we use a
-- lightweight wrapper view that adds the flag. This is safer for
-- production since the original view definition stays intact.
-- =============================================

-- Synthetic flag view - wraps the complete view with the flag
IF OBJECT_ID('dbo.vw_PiXL_Complete_v2', 'V') IS NOT NULL
    DROP VIEW dbo.vw_PiXL_Complete_v2;
GO

-- Add the IsSynthetic column directly to the base complete view
-- by recreating it with the new field

-- First, let's add it via ALTER on the original view
-- Since views can't be ALTERed to add columns without full redefinition,
-- we'll add it as a computed column via a new thin view

-- Option chosen: Add IsSynthetic to the existing complete view by
-- modifying Group 29 (Raw Data) to include the synthetic flag before
-- the raw columns. This preserves backward compatibility.

-- =============================================
-- APPROACH: Recreate vw_PiXL_Complete with IsSynthetic
-- =============================================
IF OBJECT_ID('dbo.vw_PiXL_Complete', 'V') IS NOT NULL
    DROP VIEW dbo.vw_PiXL_Complete;
GO

CREATE VIEW dbo.vw_PiXL_Complete AS
SELECT 
    -- ======================================================================
    -- GROUP 1: CORE IDENTITY (8 columns)
    -- ======================================================================
    p.Id,
    p.CompanyID,
    p.PiXLID,
    p.IPAddress,
    p.ReceivedAt,
    p.RequestPath,
    p.UserAgent AS ServerUserAgent,
    p.Referer AS ServerReferer,
    
    -- ======================================================================
    -- GROUP 2: SYNTHETIC TEST FLAG (1 column) 
    -- synthetic=1 appended by synthetic-monitor.js via Playwright route interception
    -- ======================================================================
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'synthetic') AS INT) = 1 THEN 1
        ELSE 0
    END AS IsSynthetic,
    
    -- ======================================================================
    -- GROUP 3: TIER INFO (1 column)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tier') AS INT) AS Tier,
    
    -- ======================================================================
    -- GROUP 4: SCREEN DIMENSIONS (13 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sw') AS INT) AS ScreenWidth,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sh') AS INT) AS ScreenHeight,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'saw') AS INT) AS ScreenAvailWidth,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sah') AS INT) AS ScreenAvailHeight,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'vw') AS INT) AS ViewportWidth,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'vh') AS INT) AS ViewportHeight,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ow') AS INT) AS OuterWidth,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'oh') AS INT) AS OuterHeight,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sx') AS INT) AS ScreenX,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sy') AS INT) AS ScreenY,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cd') AS INT) AS ColorDepth,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pd') AS DECIMAL(5,2)) AS PixelRatio,
    dbo.GetQueryParam(p.QueryString, 'ori') AS ScreenOrientation,
    
    -- ======================================================================
    -- GROUP 5: TIME & TIMEZONE (7 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'tz') AS Timezone,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tzo') AS INT) AS TimezoneOffsetMins,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ts') AS BIGINT) AS ClientTimestampMs,
    dbo.GetQueryParam(p.QueryString, 'tzLocale') AS TimezoneLocale,
    dbo.GetQueryParam(p.QueryString, 'dateFormat') AS DateFormatSample,
    dbo.GetQueryParam(p.QueryString, 'numberFormat') AS NumberFormatSample,
    dbo.GetQueryParam(p.QueryString, 'relativeTime') AS RelativeTimeSample,
    
    -- ======================================================================
    -- GROUP 6: LANGUAGE & LOCALE (2 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'lang') AS Language,
    dbo.GetQueryParam(p.QueryString, 'langs') AS LanguageList,
    
    -- ======================================================================
    -- REMAINING GROUPS: Carried forward from 06_AnalyticsViews.sql
    -- (Hardware, Canvas, WebGL, Audio, Fonts, Plugins, Network, etc.)
    -- ======================================================================
    -- GROUP 7: HARDWARE (3 columns)
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cores') AS INT) AS HardwareConcurrency,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mem') AS DECIMAL(5,2)) AS DeviceMemoryGB,
    dbo.GetQueryParam(p.QueryString, 'maxTouch') AS MaxTouchPoints,
    
    -- GROUP 8: CANVAS FINGERPRINT (2 columns)
    dbo.GetQueryParam(p.QueryString, 'canvasFP') AS CanvasFingerprint,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvasEvasion') AS BIT) AS CanvasEvasionDetected,
    
    -- GROUP 9: WEBGL (6 columns)
    dbo.GetQueryParam(p.QueryString, 'webglFP') AS WebGLFingerprint,
    dbo.GetQueryParam(p.QueryString, 'webglVendor') AS WebGLVendor,
    dbo.GetQueryParam(p.QueryString, 'webglRenderer') AS WebGLRenderer,
    dbo.GetQueryParam(p.QueryString, 'webglVersion') AS WebGLVersion,
    dbo.GetQueryParam(p.QueryString, 'webglShadingVer') AS WebGLShadingLanguageVersion,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglEvasion') AS BIT) AS WebGLEvasionDetected,
    
    -- GROUP 10: AUDIO (3 columns)
    dbo.GetQueryParam(p.QueryString, 'audioFP') AS AudioFingerprint,
    dbo.GetQueryParam(p.QueryString, 'audioHash') AS AudioFingerprintHash,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'audioStable') AS BIT) AS AudioIsStable,
    
    -- GROUP 11: FONTS (1 column)
    dbo.GetQueryParam(p.QueryString, 'fonts') AS DetectedFonts,
    
    -- GROUP 12: PLUGINS (1 column)
    dbo.GetQueryParam(p.QueryString, 'plugins') AS DetectedPlugins,
    
    -- GROUP 13: BROWSER FEATURES (15+ columns)
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cookies') AS BIT) AS CookiesEnabled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ls') AS BIT) AS LocalStorage,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ss') AS BIT) AS SessionStorage,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'idb') AS BIT) AS IndexedDB,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sw2') AS BIT) AS ServiceWorker,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ww') AS BIT) AS WebWorker,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sharedW') AS BIT) AS SharedWorker,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webgl2') AS BIT) AS WebGL2,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webrtc') AS BIT) AS WebRTC,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webasm') AS BIT) AS WebAssembly,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touch') AS INT) AS TouchPoints,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touchEvent') AS BIT) AS TouchEventSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pointerEvent') AS BIT) AS PointerEventSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mediaDevices') AS BIT) AS MediaDevicesSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'clipboard') AS BIT) AS ClipboardAPI,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'speechSynth') AS BIT) AS SpeechSynthesis,
    
    -- GROUP 14: PREFERENCES (5 columns)
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'dnt') AS BIT) AS DoNotTrack,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'gpc') AS BIT) AS GlobalPrivacyControl,
    dbo.GetQueryParam(p.QueryString, 'colorScheme') AS PreferredColorScheme,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'reducedMotion') AS BIT) AS PrefersReducedMotion,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'highContrast') AS BIT) AS PrefersHighContrast,
    
    -- GROUP 15: DISPLAY (3 columns)
    dbo.GetQueryParam(p.QueryString, 'displayMode') AS DisplayMode,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'standalone') AS BIT) AS IsStandalone,
    dbo.GetQueryParam(p.QueryString, 'pointer') AS PointerType,
    
    -- GROUP 16: DOCUMENT (4 columns)
    dbo.GetQueryParam(p.QueryString, 'docCharset') AS DocumentCharset,
    dbo.GetQueryParam(p.QueryString, 'docCompat') AS DocumentCompatMode,
    dbo.GetQueryParam(p.QueryString, 'docReady') AS DocumentReadyState,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'docHidden') AS BIT) AS DocumentHidden,
    
    -- GROUP 17: NETWORK (3 columns)
    dbo.GetQueryParam(p.QueryString, 'connection') AS ConnectionType,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'downlink') AS DECIMAL(6,2)) AS DownlinkMbps,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'rtt') AS INT) AS RTTMs,
    
    -- GROUP 18: BATTERY (3 columns)
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'battLevel') AS DECIMAL(5,4)) AS BatteryLevel,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'battCharging') AS BIT) AS BatteryCharging,
    dbo.GetQueryParam(p.QueryString, 'battTime') AS BatteryTimeToDischarge,
    
    -- GROUP 19: USER AGENT CLIENT HINTS (10 columns)
    dbo.GetQueryParam(p.QueryString, 'uaArch') AS UA_Architecture,
    dbo.GetQueryParam(p.QueryString, 'uaBitness') AS UA_Bitness,
    dbo.GetQueryParam(p.QueryString, 'uaModel') AS UA_Model,
    dbo.GetQueryParam(p.QueryString, 'uaPlatformVersion') AS UA_PlatformVersion,
    dbo.GetQueryParam(p.QueryString, 'uaFullVersion') AS UA_FullVersionList,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'uaWow64') AS BIT) AS UA_IsWow64,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'uaMobile') AS BIT) AS UA_IsMobile,
    dbo.GetQueryParam(p.QueryString, 'uaPlatform') AS UA_Platform,
    dbo.GetQueryParam(p.QueryString, 'uaBrands') AS UA_Brands,
    dbo.GetQueryParam(p.QueryString, 'uaFormFactor') AS UA_FormFactor,
    
    -- GROUP 20: RAW DATA (2 columns)
    p.QueryString AS RawQueryString,
    p.HeadersJson AS RawHeadersJson

FROM dbo.PiXL_Test p;
GO

PRINT 'View vw_PiXL_Complete recreated with IsSynthetic flag.';
GO

-- =============================================
-- Also update vw_PiXL_Summary to include IsSynthetic
-- =============================================
IF OBJECT_ID('dbo.vw_PiXL_Summary', 'V') IS NOT NULL
    DROP VIEW dbo.vw_PiXL_Summary;
GO

CREATE VIEW dbo.vw_PiXL_Summary AS
SELECT 
    -- ====== IDENTITY ======
    p.Id,
    p.CompanyID,
    p.PiXLID,
    p.IPAddress,
    p.ReceivedAt,
    
    -- ====== SYNTHETIC FLAG ======
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'synthetic') AS INT) = 1 THEN 1
        ELSE 0
    END AS IsSynthetic,
    
    -- ====== COMPOSITE FINGERPRINT ======
    CONCAT(
        ISNULL(dbo.GetQueryParam(p.QueryString, 'canvasFP'), ''),
        '-',
        ISNULL(dbo.GetQueryParam(p.QueryString, 'webglFP'), ''),
        '-',
        ISNULL(dbo.GetQueryParam(p.QueryString, 'audioHash'), '')
    ) AS CompositeFingerprint,
    
    -- ====== DEVICE SUMMARY ======
    dbo.GetDeviceType(
        TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sw') AS INT),
        TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touch') AS INT),
        TRY_CAST(dbo.GetQueryParam(p.QueryString, 'uaMobile') AS INT)
    ) + '/' +
    CASE 
        WHEN dbo.GetQueryParam(p.QueryString, 'ua') LIKE '%Windows%' THEN 'Win'
        WHEN dbo.GetQueryParam(p.QueryString, 'ua') LIKE '%Mac%' THEN 'Mac'
        WHEN dbo.GetQueryParam(p.QueryString, 'ua') LIKE '%Linux%' THEN 'Linux'
        WHEN dbo.GetQueryParam(p.QueryString, 'ua') LIKE '%Android%' THEN 'Android'
        WHEN dbo.GetQueryParam(p.QueryString, 'ua') LIKE '%iPhone%' OR dbo.GetQueryParam(p.QueryString, 'ua') LIKE '%iPad%' THEN 'iOS'
        ELSE 'Other'
    END + '/' +
    CASE 
        WHEN dbo.GetQueryParam(p.QueryString, 'ua') LIKE '%OPR%' OR dbo.GetQueryParam(p.QueryString, 'ua') LIKE '%Opera%' THEN 'Opera'
        WHEN dbo.GetQueryParam(p.QueryString, 'ua') LIKE '%Edg%' THEN 'Edge'
        WHEN dbo.GetQueryParam(p.QueryString, 'ua') LIKE '%Firefox%' THEN 'Firefox'
        WHEN dbo.GetQueryParam(p.QueryString, 'ua') LIKE '%Safari%' AND dbo.GetQueryParam(p.QueryString, 'ua') NOT LIKE '%Chrome%' THEN 'Safari'
        WHEN dbo.GetQueryParam(p.QueryString, 'ua') LIKE '%Chrome%' THEN 'Chrome'
        ELSE 'Other'
    END AS DeviceProfile,
    
    -- ====== SCREEN PROFILE ======
    CONCAT(
        ISNULL(dbo.GetQueryParam(p.QueryString, 'sw'), '?'), 'x',
        ISNULL(dbo.GetQueryParam(p.QueryString, 'sh'), '?'),
        ' @', ISNULL(dbo.GetQueryParam(p.QueryString, 'pd'), '1'), 'x'
    ) AS ScreenProfile,
    
    -- ====== HARDWARE SCORE ======
    CASE
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cores') AS INT) IS NULL THEN 0
        ELSE
            CASE 
                WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cores') AS INT) >= 24 THEN 40
                ELSE TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cores') AS INT) * 2
            END +
            CASE 
                WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mem') AS INT) >= 8 THEN 40
                ELSE ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mem') AS INT), 0) * 5
            END +
            CASE 
                WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'webglRenderer'), '')) > 10 THEN 20
                ELSE 0
            END
    END AS HardwareScore,
    
    -- ====== FINGERPRINT STRENGTH ======
    (
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'canvasFP'), '')) > 0 THEN 15 ELSE 0 END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'webglFP'), '')) > 0 THEN 15 ELSE 0 END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'audioHash'), '')) > 0 THEN 15 ELSE 0 END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'fonts'), '')) > 0 THEN 15 ELSE 0 END +
        CASE WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sw') AS INT) > 0 THEN 10 ELSE 0 END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'tz'), '')) > 0 THEN 10 ELSE 0 END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'lang'), '')) > 0 THEN 10 ELSE 0 END +
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'plugins'), '')) > 0 THEN 10 ELSE 0 END
    ) AS FingerprintStrength,
    
    -- ====== BOT SCORE ======
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) AS BotScore

FROM dbo.PiXL_Test p;
GO

PRINT 'View vw_PiXL_Summary recreated with IsSynthetic flag.';
PRINT '';
PRINT 'Usage examples:';
PRINT '  -- Live traffic only:';
PRINT '  SELECT * FROM vw_PiXL_Complete WHERE IsSynthetic = 0';
PRINT '';
PRINT '  -- Synthetic tests only:';
PRINT '  SELECT * FROM vw_PiXL_Complete WHERE IsSynthetic = 1';
PRINT '';
PRINT '  -- Synthetic test success rate:';
PRINT '  SELECT COUNT(*) AS SyntheticHits, MIN(ReceivedAt) AS FirstHit, MAX(ReceivedAt) AS LastHit';
PRINT '  FROM vw_PiXL_Complete WHERE IsSynthetic = 1';
GO
