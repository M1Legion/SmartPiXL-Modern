-- =============================================
-- SmartPiXL Analytics Views
-- 
-- 1. vw_PiXL_Summary      - At-a-glance summary with composite scores
-- 2. vw_PiXL_Complete     - All 130+ columns fully expanded
-- 3. vw_PiXL_ColumnMap    - Mapping showing which columns feed into summaries
--
-- Run AFTER 03_StreamlinedSchema.sql
-- =============================================

USE SmartPixl;
GO

-- =============================================
-- HELPER FUNCTION: Count detected items in comma-separated list
-- =============================================
IF OBJECT_ID('dbo.CountListItems', 'FN') IS NOT NULL
    DROP FUNCTION dbo.CountListItems;
GO

CREATE FUNCTION dbo.CountListItems(@List NVARCHAR(MAX))
RETURNS INT
AS
BEGIN
    IF @List IS NULL OR LEN(@List) = 0
        RETURN 0;
    RETURN LEN(@List) - LEN(REPLACE(@List, ',', '')) + 1;
END
GO

-- =============================================
-- VIEW 1: vw_PiXL_Summary
-- At-a-glance summary with composite fingerprint and risk scores
-- ~15 columns that tell the story
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
    
    -- ====== COMPOSITE FINGERPRINT ======
    -- Combines canvas + webgl + audio hashes into single identifier
    CONCAT(
        ISNULL(dbo.GetQueryParam(p.QueryString, 'canvasFP'), ''),
        '-',
        ISNULL(dbo.GetQueryParam(p.QueryString, 'webglFP'), ''),
        '-',
        ISNULL(dbo.GetQueryParam(p.QueryString, 'audioHash'), '')
    ) AS CompositeFingerprint,
    
    -- ====== DEVICE SUMMARY ======
    -- Single string: "Desktop/Win/Chrome" or "Mobile/iOS/Safari"
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'uaMobile') AS INT) = 1 THEN 'Mobile'
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touch') AS INT) > 0 THEN 'Tablet'
        ELSE 'Desktop'
    END + '/' +
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
    -- "1920x1080 @2x" or "375x812 @3x"
    CONCAT(
        ISNULL(dbo.GetQueryParam(p.QueryString, 'sw'), '?'), 'x',
        ISNULL(dbo.GetQueryParam(p.QueryString, 'sh'), '?'),
        ' @', ISNULL(dbo.GetQueryParam(p.QueryString, 'pd'), '1'), 'x'
    ) AS ScreenProfile,
    
    -- ====== HARDWARE SCORE ======
    -- 0-100 scale: cores + memory + GPU presence
    CASE
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cores') AS INT) IS NULL THEN 0
        ELSE
            -- Cores: up to 40 points (24+ cores = 40pts, 1 core = ~2pts)
            CASE 
                WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cores') AS INT) >= 24 THEN 40
                ELSE TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cores') AS INT) * 2
            END +
            -- Memory: up to 40 points (8GB+ = 40pts)
            CASE 
                WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mem') AS INT) >= 8 THEN 40
                WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mem') AS INT) >= 4 THEN 30
                WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mem') AS INT) >= 2 THEN 20
                ELSE 10
            END +
            -- GPU: 20 points if present
            CASE WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'gpu'), '')) > 10 THEN 20 ELSE 0 END
    END AS HardwareScore,
    
    -- ====== FINGERPRINT STRENGTH ======
    -- 0-100: How unique is this device based on entropy signals?
    (
        -- Canvas: 15pts
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'canvasFP'), '')) > 0 THEN 15 ELSE 0 END +
        -- WebGL: 15pts
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'webglFP'), '')) > 0 THEN 15 ELSE 0 END +
        -- Audio: 15pts
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'audioHash'), '')) > 0 THEN 15 ELSE 0 END +
        -- Fonts: up to 20pts (more fonts = more entropy)
        CASE 
            WHEN dbo.CountListItems(dbo.GetQueryParam(p.QueryString, 'fonts')) >= 20 THEN 20
            WHEN dbo.CountListItems(dbo.GetQueryParam(p.QueryString, 'fonts')) >= 10 THEN 15
            WHEN dbo.CountListItems(dbo.GetQueryParam(p.QueryString, 'fonts')) >= 5 THEN 10
            ELSE 5
        END +
        -- High-entropy client hints: 15pts
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'uaFullVersion'), '')) > 0 THEN 15 ELSE 0 END +
        -- Timezone + locale: 10pts
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'tzLocale'), '')) > 0 THEN 10 ELSE 0 END +
        -- Plugins: 10pts
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'pluginList'), '')) > 0 THEN 10 ELSE 0 END
    ) AS FingerprintStrength,
    
    -- ====== BOT RISK ======
    -- 0-100: Higher = more likely automated
    CASE
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) IS NULL THEN 0
        ELSE
            -- Base bot score (weighted)
            CASE 
                WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) >= 50 THEN 60
                WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) >= 25 THEN 40
                WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) >= 10 THEN 20
                ELSE TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT)
            END +
            -- Script exec time penalty
            CASE 
                WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scriptExecTime') AS INT) < 10 THEN 30
                WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scriptExecTime') AS INT) < 50 THEN 15
                ELSE 0
            END +
            -- Evasion detection
            CASE WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvasEvasion') AS INT) = 1 THEN 5 ELSE 0 END +
            CASE WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglEvasion') AS INT) = 1 THEN 5 ELSE 0 END
    END AS BotRisk,
    
    -- ====== PRIVACY LEVEL ======
    -- Detected privacy protections
    CONCAT_WS(', ',
        CASE WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvasEvasion') AS INT) = 1 THEN 'CanvasBlock' ELSE NULL END,
        CASE WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglEvasion') AS INT) = 1 THEN 'WebGLBlock' ELSE NULL END,
        CASE WHEN LEN(ISNULL(dbo.GetQueryParam(p.QueryString, 'dnt'), '')) > 0 AND dbo.GetQueryParam(p.QueryString, 'dnt') != 'null' THEN 'DNT' ELSE NULL END,
        CASE WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ck') AS INT) = 0 THEN 'NoCookies' ELSE NULL END
    ) AS PrivacyFlags,
    
    -- ====== CAPABILITY SCORE ======
    -- How many modern APIs are supported (0-100)
    (
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ww') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'swk') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'wasm') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webgl') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webgl2') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'bluetooth') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'usb') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'serial') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'hid') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'midi') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'xr') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'share') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'geolocation') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'notifications') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'payment') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'speechRecog') AS INT), 0) AS INT) +
        CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(p.QueryString, 'speechSynth') AS INT), 0) AS INT)
    ) * 6 AS CapabilityScore,  -- 17 flags * 6 ≈ 100 max
    
    -- ====== LOCATION SUMMARY ======
    CONCAT(
        ISNULL(dbo.GetQueryParam(p.QueryString, 'tz'), 'Unknown'), ' (',
        ISNULL(dbo.GetQueryParam(p.QueryString, 'lang'), '?'), ')'
    ) AS LocationProfile,
    
    -- ====== PAGE CONTEXT ======
    dbo.GetQueryParam(p.QueryString, 'domain') AS Domain,
    dbo.GetQueryParam(p.QueryString, 'path') AS PagePath,
    dbo.GetQueryParam(p.QueryString, 'title') AS PageTitle,
    
    -- ====== PERFORMANCE SUMMARY ======
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ttfb') AS INT) < 100 THEN 'Fast'
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ttfb') AS INT) < 500 THEN 'Normal'
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ttfb') AS INT) < 1000 THEN 'Slow'
        ELSE 'Very Slow'
    END AS PerformanceCategory,
    
    -- ====== NETWORK SUMMARY ======
    CONCAT(
        ISNULL(dbo.GetQueryParam(p.QueryString, 'conn'), '?'), 
        CASE WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'rtt') AS INT) IS NOT NULL 
             THEN CONCAT(' (', dbo.GetQueryParam(p.QueryString, 'rtt'), 'ms)')
             ELSE ''
        END
    ) AS NetworkProfile

FROM dbo.PiXL_Test p;
GO

-- =============================================
-- VIEW 2: vw_PiXL_Complete
-- All 130+ columns - every data point expanded
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
    -- GROUP 2: TIER INFO (1 column)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tier') AS INT) AS Tier,
    
    -- ======================================================================
    -- GROUP 3: SCREEN DIMENSIONS (13 columns)
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
    -- GROUP 4: TIME & TIMEZONE (7 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'tz') AS Timezone,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tzo') AS INT) AS TimezoneOffsetMins,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ts') AS BIGINT) AS ClientTimestampMs,
    dbo.GetQueryParam(p.QueryString, 'tzLocale') AS TimezoneLocale,
    dbo.GetQueryParam(p.QueryString, 'dateFormat') AS DateFormatSample,
    dbo.GetQueryParam(p.QueryString, 'numberFormat') AS NumberFormatSample,
    dbo.GetQueryParam(p.QueryString, 'relativeTime') AS RelativeTimeSample,
    
    -- ======================================================================
    -- GROUP 5: LANGUAGE & LOCALE (2 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'lang') AS Language,
    dbo.GetQueryParam(p.QueryString, 'langs') AS LanguageList,
    
    -- ======================================================================
    -- GROUP 6: NAVIGATOR PROPERTIES (11 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'plt') AS Platform,
    dbo.GetQueryParam(p.QueryString, 'vnd') AS Vendor,
    dbo.GetQueryParam(p.QueryString, 'ua') AS ClientUserAgent,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cores') AS INT) AS HardwareConcurrency,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mem') AS DECIMAL(5,2)) AS DeviceMemoryGB,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touch') AS INT) AS MaxTouchPoints,
    dbo.GetQueryParam(p.QueryString, 'product') AS NavigatorProduct,
    dbo.GetQueryParam(p.QueryString, 'productSub') AS NavigatorProductSub,
    dbo.GetQueryParam(p.QueryString, 'vendorSub') AS NavigatorVendorSub,
    dbo.GetQueryParam(p.QueryString, 'appName') AS AppName,
    dbo.GetQueryParam(p.QueryString, 'appVersion') AS AppVersion,
    dbo.GetQueryParam(p.QueryString, 'appCodeName') AS AppCodeName,
    
    -- ======================================================================
    -- GROUP 7: GPU & WEBGL (6 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'gpu') AS GPURenderer,
    dbo.GetQueryParam(p.QueryString, 'gpuVendor') AS GPUVendor,
    dbo.GetQueryParam(p.QueryString, 'webglParams') AS WebGLParameters,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglExt') AS INT) AS WebGLExtensionCount,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webgl') AS BIT) AS WebGLSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webgl2') AS BIT) AS WebGL2Supported,
    
    -- ======================================================================
    -- GROUP 8: FINGERPRINT HASHES (8 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'canvasFP') AS CanvasFingerprint,
    dbo.GetQueryParam(p.QueryString, 'webglFP') AS WebGLFingerprint,
    dbo.GetQueryParam(p.QueryString, 'audioFP') AS AudioFingerprintSum,
    dbo.GetQueryParam(p.QueryString, 'audioHash') AS AudioFingerprintHash,
    dbo.GetQueryParam(p.QueryString, 'mathFP') AS MathFingerprint,
    dbo.GetQueryParam(p.QueryString, 'errorFP') AS ErrorFingerprint,
    dbo.GetQueryParam(p.QueryString, 'cssFontVariant') AS CSSFontVariantHash,
    
    -- ======================================================================
    -- GROUP 9: FONT & PLUGIN DETECTION (4 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'fonts') AS DetectedFonts,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'plugins') AS INT) AS PluginCount,
    dbo.GetQueryParam(p.QueryString, 'pluginList') AS PluginListDetailed,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mimeTypes') AS INT) AS MimeTypeCount,
    dbo.GetQueryParam(p.QueryString, 'mimeList') AS MimeTypeList,
    
    -- ======================================================================
    -- GROUP 10: SPEECH & GAMEPADS (2 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'voices') AS SpeechVoices,
    dbo.GetQueryParam(p.QueryString, 'gamepads') AS ConnectedGamepads,
    
    -- ======================================================================
    -- GROUP 11: NETWORK (8 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'localIp') AS WebRTCLocalIP,
    dbo.GetQueryParam(p.QueryString, 'conn') AS ConnectionType,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'dl') AS DECIMAL(10,2)) AS DownlinkMbps,
    dbo.GetQueryParam(p.QueryString, 'dlMax') AS DownlinkMax,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'rtt') AS INT) AS RTTMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'save') AS BIT) AS DataSaverEnabled,
    dbo.GetQueryParam(p.QueryString, 'connType') AS NetworkType,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'online') AS BIT) AS IsOnline,
    
    -- ======================================================================
    -- GROUP 12: STORAGE (4 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'storageQuota') AS INT) AS StorageQuotaGB,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'storageUsed') AS INT) AS StorageUsedMB,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ls') AS BIT) AS LocalStorageSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ss') AS BIT) AS SessionStorageSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'idb') AS BIT) AS IndexedDBSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'caches') AS BIT) AS CacheAPISupported,
    
    -- ======================================================================
    -- GROUP 13: BATTERY (2 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'batteryLevel') AS INT) AS BatteryLevelPct,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'batteryCharging') AS BIT) AS BatteryCharging,
    
    -- ======================================================================
    -- GROUP 14: MEDIA DEVICES (2 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'audioInputs') AS INT) AS AudioInputDevices,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'videoInputs') AS INT) AS VideoInputDevices,
    
    -- ======================================================================
    -- GROUP 15: BROWSER CAPABILITIES (7 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ck') AS BIT) AS CookiesEnabled,
    dbo.GetQueryParam(p.QueryString, 'dnt') AS DoNotTrack,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pdf') AS BIT) AS PDFViewerEnabled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webdr') AS BIT) AS WebDriverDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'java') AS BIT) AS JavaEnabled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvas') AS BIT) AS CanvasSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'wasm') AS BIT) AS WebAssemblySupported,
    
    -- ======================================================================
    -- GROUP 16: WORKER & SERVICE SUPPORT (3 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ww') AS BIT) AS WebWorkersSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'swk') AS BIT) AS ServiceWorkerSupported,
    
    -- ======================================================================
    -- GROUP 17: HARDWARE API SUPPORT (7 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'bluetooth') AS BIT) AS BluetoothAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'usb') AS BIT) AS USBAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'serial') AS BIT) AS SerialAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'hid') AS BIT) AS HIDAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'midi') AS BIT) AS MIDIAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'xr') AS BIT) AS WebXRSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mediaDevices') AS BIT) AS MediaDevicesAPISupported,
    
    -- ======================================================================
    -- GROUP 18: PERMISSION-GATED APIS (7 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'geolocation') AS BIT) AS GeolocationAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'notifications') AS BIT) AS NotificationsAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'push') AS BIT) AS PushAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'share') AS BIT) AS ShareAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'clipboard') AS BIT) AS ClipboardAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'credentials') AS BIT) AS CredentialsAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'payment') AS BIT) AS PaymentRequestSupported,
    
    -- ======================================================================
    -- GROUP 19: SPEECH APIS (2 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'speechRecog') AS BIT) AS SpeechRecognitionSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'speechSynth') AS BIT) AS SpeechSynthesisSupported,
    
    -- ======================================================================
    -- GROUP 20: INPUT CAPABILITIES (4 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touchEvent') AS BIT) AS TouchEventsSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pointerEvent') AS BIT) AS PointerEventsSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'hover') AS BIT) AS HoverCapable,
    dbo.GetQueryParam(p.QueryString, 'pointer') AS PointerType,
    
    -- ======================================================================
    -- GROUP 21: ACCESSIBILITY & DISPLAY PREFERENCES (8 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'darkMode') AS BIT) AS PrefersColorSchemeDark,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'lightMode') AS BIT) AS PrefersColorSchemeLight,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'reducedMotion') AS BIT) AS PrefersReducedMotion,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'reducedData') AS BIT) AS PrefersReducedData,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'contrast') AS BIT) AS PrefersHighContrast,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'forcedColors') AS BIT) AS ForcedColorsActive,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'invertedColors') AS BIT) AS InvertedColorsActive,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'standalone') AS BIT) AS StandaloneDisplayMode,
    
    -- ======================================================================
    -- GROUP 22: DOCUMENT STATE (5 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'docCharset') AS DocumentCharset,
    dbo.GetQueryParam(p.QueryString, 'docCompat') AS DocumentCompatMode,
    dbo.GetQueryParam(p.QueryString, 'docReady') AS DocumentReadyState,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'docHidden') AS BIT) AS DocumentHidden,
    dbo.GetQueryParam(p.QueryString, 'docVisibility') AS DocumentVisibility,
    
    -- ======================================================================
    -- GROUP 23: PAGE CONTEXT (8 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'url') AS PageURL,
    dbo.GetQueryParam(p.QueryString, 'ref') AS PageReferrer,
    dbo.GetQueryParam(p.QueryString, 'title') AS PageTitle,
    dbo.GetQueryParam(p.QueryString, 'domain') AS PageDomain,
    dbo.GetQueryParam(p.QueryString, 'path') AS PagePath,
    dbo.GetQueryParam(p.QueryString, 'hash') AS PageHash,
    dbo.GetQueryParam(p.QueryString, 'protocol') AS PageProtocol,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'hist') AS INT) AS HistoryLength,
    
    -- ======================================================================
    -- GROUP 24: PERFORMANCE TIMING (5 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'loadTime') AS INT) AS PageLoadTimeMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'domTime') AS INT) AS DOMReadyTimeMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'dnsTime') AS INT) AS DNSLookupMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tcpTime') AS INT) AS TCPConnectMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ttfb') AS INT) AS TimeToFirstByteMs,
    
    -- ======================================================================
    -- GROUP 25: BOT DETECTION (6 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'botSignals') AS BotSignalsList,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) AS BotScore,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scriptExecTime') AS INT) AS ScriptExecutionTimeMs,
    dbo.GetQueryParam(p.QueryString, 'evasionDetected') AS EvasionToolsDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvasEvasion') AS BIT) AS CanvasEvasionDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglEvasion') AS BIT) AS WebGLEvasionDetected,
    
    -- ======================================================================
    -- GROUP 26: USER AGENT CLIENT HINTS (10 columns)
    -- ======================================================================
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
    
    -- ======================================================================
    -- GROUP 27: BROWSER-SPECIFIC SIGNALS (5 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'oscpu') AS Firefox_OSCPU,
    dbo.GetQueryParam(p.QueryString, 'buildID') AS Firefox_BuildID,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'chromeObj') AS BIT) AS Chrome_ObjectPresent,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'chromeRuntime') AS BIT) AS Chrome_RuntimePresent,
    
    -- ======================================================================
    -- GROUP 28: CHROME MEMORY (3 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'jsHeapLimit') AS BIGINT) AS Chrome_JSHeapSizeLimit,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'jsHeapTotal') AS BIGINT) AS Chrome_TotalJSHeapSize,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'jsHeapUsed') AS BIGINT) AS Chrome_UsedJSHeapSize,
    
    -- ======================================================================
    -- GROUP 29: RAW DATA (2 columns)
    -- ======================================================================
    p.QueryString AS RawQueryString,
    p.HeadersJson AS RawHeadersJson

FROM dbo.PiXL_Test p;
GO

-- =============================================
-- VIEW 3: vw_PiXL_ColumnMap
-- Documents which detailed columns feed into which summary columns
-- Useful for analysts to understand the data model
-- =============================================
IF OBJECT_ID('dbo.vw_PiXL_ColumnMap', 'V') IS NOT NULL
    DROP VIEW dbo.vw_PiXL_ColumnMap;
GO

CREATE VIEW dbo.vw_PiXL_ColumnMap AS
SELECT * FROM (VALUES
    -- Summary Column, Source View, Source Columns, Formula/Logic
    
    -- COMPOSITE FINGERPRINT
    ('CompositeFingerprint', 'vw_PiXL_Complete', 'CanvasFingerprint', 'CONCAT with dash separator'),
    ('CompositeFingerprint', 'vw_PiXL_Complete', 'WebGLFingerprint', 'CONCAT with dash separator'),
    ('CompositeFingerprint', 'vw_PiXL_Complete', 'AudioFingerprintHash', 'CONCAT with dash separator'),
    
    -- DEVICE PROFILE
    ('DeviceProfile', 'vw_PiXL_Complete', 'UA_IsMobile', 'If 1 = Mobile'),
    ('DeviceProfile', 'vw_PiXL_Complete', 'MaxTouchPoints', 'If >0 and not mobile = Tablet'),
    ('DeviceProfile', 'vw_PiXL_Complete', 'ClientUserAgent', 'Pattern match for OS: Windows/Mac/Linux/Android/iOS'),
    ('DeviceProfile', 'vw_PiXL_Complete', 'ClientUserAgent', 'Pattern match for Browser: Chrome/Firefox/Safari/Edge/Opera'),
    
    -- SCREEN PROFILE
    ('ScreenProfile', 'vw_PiXL_Complete', 'ScreenWidth', 'Concatenated as WxH'),
    ('ScreenProfile', 'vw_PiXL_Complete', 'ScreenHeight', 'Concatenated as WxH'),
    ('ScreenProfile', 'vw_PiXL_Complete', 'PixelRatio', 'Appended as @Nx'),
    
    -- HARDWARE SCORE
    ('HardwareScore', 'vw_PiXL_Complete', 'HardwareConcurrency', 'Up to 40 points: 24+ cores = 40pts'),
    ('HardwareScore', 'vw_PiXL_Complete', 'DeviceMemoryGB', 'Up to 40 points: 8GB+ = 40pts'),
    ('HardwareScore', 'vw_PiXL_Complete', 'GPURenderer', '20 points if present and >10 chars'),
    
    -- FINGERPRINT STRENGTH
    ('FingerprintStrength', 'vw_PiXL_Complete', 'CanvasFingerprint', '15 points if present'),
    ('FingerprintStrength', 'vw_PiXL_Complete', 'WebGLFingerprint', '15 points if present'),
    ('FingerprintStrength', 'vw_PiXL_Complete', 'AudioFingerprintHash', '15 points if present'),
    ('FingerprintStrength', 'vw_PiXL_Complete', 'DetectedFonts', 'Up to 20 points: 20+ fonts = 20pts'),
    ('FingerprintStrength', 'vw_PiXL_Complete', 'UA_FullVersionList', '15 points if present'),
    ('FingerprintStrength', 'vw_PiXL_Complete', 'TimezoneLocale', '10 points if present'),
    ('FingerprintStrength', 'vw_PiXL_Complete', 'PluginListDetailed', '10 points if present'),
    
    -- BOT RISK
    ('BotRisk', 'vw_PiXL_Complete', 'BotScore', 'Base score: 50+ = 60pts, 25+ = 40pts, 10+ = 20pts'),
    ('BotRisk', 'vw_PiXL_Complete', 'ScriptExecutionTimeMs', 'Penalty: <10ms = 30pts, <50ms = 15pts'),
    ('BotRisk', 'vw_PiXL_Complete', 'CanvasEvasionDetected', '5 points if detected'),
    ('BotRisk', 'vw_PiXL_Complete', 'WebGLEvasionDetected', '5 points if detected'),
    
    -- PRIVACY FLAGS
    ('PrivacyFlags', 'vw_PiXL_Complete', 'CanvasEvasionDetected', 'CanvasBlock flag'),
    ('PrivacyFlags', 'vw_PiXL_Complete', 'WebGLEvasionDetected', 'WebGLBlock flag'),
    ('PrivacyFlags', 'vw_PiXL_Complete', 'DoNotTrack', 'DNT flag if set'),
    ('PrivacyFlags', 'vw_PiXL_Complete', 'CookiesEnabled', 'NoCookies flag if 0'),
    
    -- CAPABILITY SCORE
    ('CapabilityScore', 'vw_PiXL_Complete', 'WebWorkersSupported', '6 points each (17 APIs x 6 = 102 max)'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'ServiceWorkerSupported', '6 points each'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'WebAssemblySupported', '6 points each'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'WebGLSupported', '6 points each'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'WebGL2Supported', '6 points each'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'BluetoothAPISupported', '6 points each'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'USBAPISupported', '6 points each'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'SerialAPISupported', '6 points each'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'HIDAPISupported', '6 points each'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'MIDIAPISupported', '6 points each'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'WebXRSupported', '6 points each'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'ShareAPISupported', '6 points each'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'GeolocationAPISupported', '6 points each'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'NotificationsAPISupported', '6 points each'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'PaymentRequestSupported', '6 points each'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'SpeechRecognitionSupported', '6 points each'),
    ('CapabilityScore', 'vw_PiXL_Complete', 'SpeechSynthesisSupported', '6 points each'),
    
    -- LOCATION PROFILE
    ('LocationProfile', 'vw_PiXL_Complete', 'Timezone', 'Formatted as "Timezone (lang)"'),
    ('LocationProfile', 'vw_PiXL_Complete', 'Language', 'Appended in parens'),
    
    -- PERFORMANCE CATEGORY
    ('PerformanceCategory', 'vw_PiXL_Complete', 'TimeToFirstByteMs', 'Bucket: <100=Fast, <500=Normal, <1000=Slow, else Very Slow'),
    
    -- NETWORK PROFILE
    ('NetworkProfile', 'vw_PiXL_Complete', 'ConnectionType', 'Primary value'),
    ('NetworkProfile', 'vw_PiXL_Complete', 'RTTMs', 'Appended as (Xms)')
    
) AS ColumnMap (SummaryColumn, SourceView, SourceColumn, Logic);
GO

-- =============================================
-- BONUS: Useful aggregate views
-- =============================================

-- Hourly hit counts by company/pixel
IF OBJECT_ID('dbo.vw_PiXL_HourlyStats', 'V') IS NOT NULL
    DROP VIEW dbo.vw_PiXL_HourlyStats;
GO

CREATE VIEW dbo.vw_PiXL_HourlyStats AS
SELECT 
    CompanyID,
    PiXLID,
    DATEADD(HOUR, DATEDIFF(HOUR, 0, ReceivedAt), 0) AS HourBucket,
    COUNT(*) AS HitCount,
    COUNT(DISTINCT IPAddress) AS UniqueIPs,
    COUNT(DISTINCT dbo.GetQueryParam(QueryString, 'canvasFP')) AS UniqueCanvasFPs,
    AVG(TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS FLOAT)) AS AvgBotScore
FROM dbo.PiXL_Test
GROUP BY 
    CompanyID,
    PiXLID,
    DATEADD(HOUR, DATEDIFF(HOUR, 0, ReceivedAt), 0);
GO

-- Device type breakdown
IF OBJECT_ID('dbo.vw_PiXL_DeviceBreakdown', 'V') IS NOT NULL
    DROP VIEW dbo.vw_PiXL_DeviceBreakdown;
GO

CREATE VIEW dbo.vw_PiXL_DeviceBreakdown AS
SELECT 
    CompanyID,
    CAST(ReceivedAt AS DATE) AS DateBucket,
    
    -- Device Type
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'uaMobile') AS INT) = 1 THEN 'Mobile'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'touch') AS INT) > 0 THEN 'Tablet'
        ELSE 'Desktop'
    END AS DeviceType,
    
    -- OS
    CASE 
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Windows%' THEN 'Windows'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Mac%' THEN 'macOS'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Android%' THEN 'Android'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%iPhone%' OR dbo.GetQueryParam(QueryString, 'ua') LIKE '%iPad%' THEN 'iOS'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Linux%' THEN 'Linux'
        ELSE 'Other'
    END AS OperatingSystem,
    
    -- Browser
    CASE 
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%OPR%' OR dbo.GetQueryParam(QueryString, 'ua') LIKE '%Opera%' THEN 'Opera'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Edg%' THEN 'Edge'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Firefox%' THEN 'Firefox'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Safari%' AND dbo.GetQueryParam(QueryString, 'ua') NOT LIKE '%Chrome%' THEN 'Safari'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Chrome%' THEN 'Chrome'
        ELSE 'Other'
    END AS Browser,
    
    COUNT(*) AS Hits,
    COUNT(DISTINCT IPAddress) AS UniqueIPs
FROM dbo.PiXL_Test
GROUP BY 
    CompanyID,
    CAST(ReceivedAt AS DATE),
    -- Device Type
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'uaMobile') AS INT) = 1 THEN 'Mobile'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'touch') AS INT) > 0 THEN 'Tablet'
        ELSE 'Desktop'
    END,
    -- OS
    CASE 
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Windows%' THEN 'Windows'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Mac%' THEN 'macOS'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Android%' THEN 'Android'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%iPhone%' OR dbo.GetQueryParam(QueryString, 'ua') LIKE '%iPad%' THEN 'iOS'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Linux%' THEN 'Linux'
        ELSE 'Other'
    END,
    -- Browser
    CASE 
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%OPR%' OR dbo.GetQueryParam(QueryString, 'ua') LIKE '%Opera%' THEN 'Opera'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Edg%' THEN 'Edge'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Firefox%' THEN 'Firefox'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Safari%' AND dbo.GetQueryParam(QueryString, 'ua') NOT LIKE '%Chrome%' THEN 'Safari'
        WHEN dbo.GetQueryParam(QueryString, 'ua') LIKE '%Chrome%' THEN 'Chrome'
        ELSE 'Other'
    END;
GO

-- Bot detection summary
IF OBJECT_ID('dbo.vw_PiXL_BotAnalysis', 'V') IS NOT NULL
    DROP VIEW dbo.vw_PiXL_BotAnalysis;
GO

CREATE VIEW dbo.vw_PiXL_BotAnalysis AS
SELECT 
    CompanyID,
    CAST(ReceivedAt AS DATE) AS DateBucket,
    
    -- Risk Buckets
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 50 THEN 'High Risk (50+)'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 25 THEN 'Medium Risk (25-49)'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 10 THEN 'Low Risk (10-24)'
        ELSE 'Likely Human (<10)'
    END AS RiskCategory,
    
    COUNT(*) AS Hits,
    
    -- Evasion breakdown
    SUM(CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(QueryString, 'canvasEvasion') AS INT), 0) AS INT)) AS CanvasEvasionCount,
    SUM(CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(QueryString, 'webglEvasion') AS INT), 0) AS INT)) AS WebGLEvasionCount,
    SUM(CAST(ISNULL(TRY_CAST(dbo.GetQueryParam(QueryString, 'webdr') AS INT), 0) AS INT)) AS WebDriverCount,
    
    -- Script timing analysis
    AVG(TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS FLOAT)) AS AvgScriptExecMs,
    MIN(TRY_CAST(dbo.GetQueryParam(QueryString, 'scriptExecTime') AS INT)) AS MinScriptExecMs
    
FROM dbo.PiXL_Test
GROUP BY 
    CompanyID,
    CAST(ReceivedAt AS DATE),
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 50 THEN 'High Risk (50+)'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 25 THEN 'Medium Risk (25-49)'
        WHEN TRY_CAST(dbo.GetQueryParam(QueryString, 'botScore') AS INT) >= 10 THEN 'Low Risk (10-24)'
        ELSE 'Likely Human (<10)'
    END;
GO

-- Fingerprint uniqueness analysis
IF OBJECT_ID('dbo.vw_PiXL_FingerprintUniqueness', 'V') IS NOT NULL
    DROP VIEW dbo.vw_PiXL_FingerprintUniqueness;
GO

CREATE VIEW dbo.vw_PiXL_FingerprintUniqueness AS
SELECT 
    dbo.GetQueryParam(QueryString, 'canvasFP') AS CanvasFingerprint,
    dbo.GetQueryParam(QueryString, 'webglFP') AS WebGLFingerprint,
    dbo.GetQueryParam(QueryString, 'audioHash') AS AudioHash,
    
    COUNT(*) AS Occurrences,
    COUNT(DISTINCT IPAddress) AS UniqueIPs,
    MIN(ReceivedAt) AS FirstSeen,
    MAX(ReceivedAt) AS LastSeen,
    DATEDIFF(DAY, MIN(ReceivedAt), MAX(ReceivedAt)) AS DaysActive
    
FROM dbo.PiXL_Test
WHERE dbo.GetQueryParam(QueryString, 'canvasFP') IS NOT NULL
GROUP BY 
    dbo.GetQueryParam(QueryString, 'canvasFP'),
    dbo.GetQueryParam(QueryString, 'webglFP'),
    dbo.GetQueryParam(QueryString, 'audioHash');
GO

PRINT '';
PRINT '======================================';
PRINT 'Analytics Views Created Successfully!';
PRINT '======================================';
PRINT '';
PRINT 'VIEWS:';
PRINT '  vw_PiXL_Summary        - 15 columns with composite scores';
PRINT '  vw_PiXL_Complete       - 130+ columns, every data point';
PRINT '  vw_PiXL_ColumnMap      - Maps summary columns to source columns';
PRINT '';
PRINT 'BONUS AGGREGATE VIEWS:';
PRINT '  vw_PiXL_HourlyStats    - Hourly hit counts with unique counts';
PRINT '  vw_PiXL_DeviceBreakdown- Device/OS/Browser breakdown by day';
PRINT '  vw_PiXL_BotAnalysis    - Bot risk bucketing with evasion counts';
PRINT '  vw_PiXL_FingerprintUniqueness - How unique is each fingerprint?';
PRINT '';
GO
