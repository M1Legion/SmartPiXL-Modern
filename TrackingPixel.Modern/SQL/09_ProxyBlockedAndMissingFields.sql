-- =============================================
-- SmartPiXL Database Schema Patch 09
-- Adds new fields from safeGet() proxy protection
-- 
-- NEW FIELDS:
--   _proxyBlocked  - Comma-separated list of navigator properties blocked by privacy extensions
--   uaFormFactor   - Client hints form factor (phone, tablet, desktop, etc.)
--   uaWow64        - 32-bit process on 64-bit Windows
--   relativeTime   - Intl.RelativeTimeFormat output for locale detection
--
-- Run this AFTER all previous schema patches
-- =============================================

USE SmartPixl;
GO

-- =============================================
-- Drop and recreate the parsed view with new fields
-- =============================================
IF OBJECT_ID('dbo.vw_PiXL_Parsed', 'V') IS NOT NULL
    DROP VIEW dbo.vw_PiXL_Parsed;
GO

CREATE VIEW dbo.vw_PiXL_Parsed AS
SELECT 
    p.Id,
    p.CompanyID,
    p.PiXLID,
    p.IPAddress,
    p.ReceivedAt,
    p.RequestPath,
    p.UserAgent,
    p.Referer,
    p.HeadersJson,
    
    -- Tier
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tier') AS INT) AS Tier,
    
    -- ============================================
    -- SCREEN & WINDOW
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sw') AS INT) AS ScreenWidth,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sh') AS INT) AS ScreenHeight,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'saw') AS INT) AS AvailWidth,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sah') AS INT) AS AvailHeight,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'vw') AS INT) AS ViewportWidth,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'vh') AS INT) AS ViewportHeight,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ow') AS INT) AS OuterWidth,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'oh') AS INT) AS OuterHeight,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sx') AS INT) AS ScreenX,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sy') AS INT) AS ScreenY,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cd') AS INT) AS ColorDepth,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pd') AS DECIMAL(5,2)) AS PixelRatio,
    dbo.GetQueryParam(p.QueryString, 'ori') AS Orientation,
    
    -- ============================================
    -- TIME & LOCALE
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'tz') AS Timezone,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tzo') AS INT) AS TimezoneOffset,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ts') AS BIGINT) AS ClientTimestamp,
    dbo.GetQueryParam(p.QueryString, 'lang') AS Language,
    dbo.GetQueryParam(p.QueryString, 'langs') AS Languages,
    
    -- ============================================
    -- DEVICE & BROWSER
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'plt') AS Platform,
    dbo.GetQueryParam(p.QueryString, 'vnd') AS Vendor,
    dbo.GetQueryParam(p.QueryString, 'ua') AS ClientUserAgent,  -- From JS (may differ from header)
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cores') AS INT) AS CPUCores,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mem') AS DECIMAL(5,2)) AS DeviceMemory,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touch') AS INT) AS MaxTouchPoints,
    dbo.GetQueryParam(p.QueryString, 'product') AS Product,
    dbo.GetQueryParam(p.QueryString, 'productSub') AS ProductSub,
    dbo.GetQueryParam(p.QueryString, 'vendorSub') AS VendorSub,
    dbo.GetQueryParam(p.QueryString, 'appName') AS AppName,
    dbo.GetQueryParam(p.QueryString, 'appVersion') AS AppVersion,
    dbo.GetQueryParam(p.QueryString, 'appCodeName') AS AppCodeName,
    
    -- ============================================
    -- GPU & WEBGL
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'gpu') AS GPU,
    dbo.GetQueryParam(p.QueryString, 'gpuVendor') AS GPUVendor,
    dbo.GetQueryParam(p.QueryString, 'webglParams') AS WebGLParams,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglExt') AS INT) AS WebGLExtensions,
    
    -- ============================================
    -- FINGERPRINTS (the good stuff)
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'canvasFP') AS CanvasFingerprint,
    dbo.GetQueryParam(p.QueryString, 'webglFP') AS WebGLFingerprint,
    dbo.GetQueryParam(p.QueryString, 'audioFP') AS AudioFingerprint,
    dbo.GetQueryParam(p.QueryString, 'mathFP') AS MathFingerprint,
    dbo.GetQueryParam(p.QueryString, 'errorFP') AS ErrorFingerprint,
    dbo.GetQueryParam(p.QueryString, 'fonts') AS DetectedFonts,
    dbo.GetQueryParam(p.QueryString, 'voices') AS SpeechVoices,
    dbo.GetQueryParam(p.QueryString, 'gamepads') AS Gamepads,
    
    -- ============================================
    -- NETWORK & WEBRTC
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'localIp') AS LocalIP,
    dbo.GetQueryParam(p.QueryString, 'conn') AS ConnectionType,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'dl') AS DECIMAL(10,2)) AS Downlink,
    dbo.GetQueryParam(p.QueryString, 'dlMax') AS DownlinkMax,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'rtt') AS INT) AS RTT,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'save') AS BIT) AS DataSaverEnabled,
    dbo.GetQueryParam(p.QueryString, 'connType') AS ConnectionNetworkType,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'online') AS BIT) AS IsOnline,
    
    -- ============================================
    -- STORAGE & BATTERY
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'storageQuota') AS INT) AS StorageQuotaGB,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'storageUsed') AS INT) AS StorageUsedMB,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'batteryLevel') AS INT) AS BatteryLevel,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'batteryCharging') AS BIT) AS BatteryCharging,
    
    -- ============================================
    -- MEDIA DEVICES
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'audioInputs') AS INT) AS AudioInputDevices,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'videoInputs') AS INT) AS VideoInputDevices,
    
    -- ============================================
    -- BROWSER CAPABILITIES
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ck') AS BIT) AS CookiesEnabled,
    dbo.GetQueryParam(p.QueryString, 'dnt') AS DoNotTrack,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pdf') AS BIT) AS PDFViewerEnabled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webdr') AS BIT) AS WebDriverDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'java') AS BIT) AS JavaEnabled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'plugins') AS INT) AS PluginCount,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mimeTypes') AS INT) AS MimeTypeCount,
    
    -- ============================================
    -- SESSION & PAGE
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'url') AS PageURL,
    dbo.GetQueryParam(p.QueryString, 'ref') AS PageReferrer,
    dbo.GetQueryParam(p.QueryString, 'title') AS PageTitle,
    dbo.GetQueryParam(p.QueryString, 'domain') AS Domain,
    dbo.GetQueryParam(p.QueryString, 'path') AS [Path],
    dbo.GetQueryParam(p.QueryString, 'hash') AS [Hash],
    dbo.GetQueryParam(p.QueryString, 'protocol') AS Protocol,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'hist') AS INT) AS HistoryLength,
    
    -- ============================================
    -- PERFORMANCE TIMING
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'loadTime') AS INT) AS LoadTimeMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'domTime') AS INT) AS DOMReadyMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'dnsTime') AS INT) AS DNSLookupMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tcpTime') AS INT) AS TCPConnectMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ttfb') AS INT) AS TTFBMs,
    
    -- ============================================
    -- STORAGE SUPPORT FLAGS
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ls') AS BIT) AS LocalStorageSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ss') AS BIT) AS SessionStorageSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'idb') AS BIT) AS IndexedDBSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'caches') AS BIT) AS CacheAPISupport,
    
    -- ============================================
    -- FEATURE DETECTION FLAGS
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ww') AS BIT) AS WebWorkersSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'swk') AS BIT) AS ServiceWorkerSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'wasm') AS BIT) AS WebAssemblySupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webgl') AS BIT) AS WebGLSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webgl2') AS BIT) AS WebGL2Support,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvas') AS BIT) AS CanvasSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touchEvent') AS BIT) AS TouchEventsSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pointerEvent') AS BIT) AS PointerEventsSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mediaDevices') AS BIT) AS MediaDevicesSupport,
    
    -- ============================================
    -- ADVANCED API SUPPORT FLAGS
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'bluetooth') AS BIT) AS BluetoothSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'usb') AS BIT) AS USBSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'serial') AS BIT) AS SerialSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'hid') AS BIT) AS HIDSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'midi') AS BIT) AS MIDISupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'xr') AS BIT) AS WebXRSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'share') AS BIT) AS ShareAPISupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'clipboard') AS BIT) AS ClipboardSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'credentials') AS BIT) AS CredentialsSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'geolocation') AS BIT) AS GeolocationSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'notifications') AS BIT) AS NotificationsSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'push') AS BIT) AS PushAPISupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'payment') AS BIT) AS PaymentRequestSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'speechRecog') AS BIT) AS SpeechRecognitionSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'speechSynth') AS BIT) AS SpeechSynthesisSupport,
    
    -- ============================================
    -- CSS/MEDIA PREFERENCES
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'darkMode') AS BIT) AS DarkModePreferred,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'lightMode') AS BIT) AS LightModePreferred,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'reducedMotion') AS BIT) AS ReducedMotionPreferred,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'reducedData') AS BIT) AS ReducedDataPreferred,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'contrast') AS BIT) AS HighContrastPreferred,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'forcedColors') AS BIT) AS ForcedColorsActive,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'invertedColors') AS BIT) AS InvertedColorsActive,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'hover') AS BIT) AS HoverCapable,
    dbo.GetQueryParam(p.QueryString, 'pointer') AS PointerType,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'standalone') AS BIT) AS StandaloneMode,
    
    -- ============================================
    -- DOCUMENT INFO
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'docCharset') AS DocCharset,
    dbo.GetQueryParam(p.QueryString, 'docCompat') AS DocCompatMode,
    dbo.GetQueryParam(p.QueryString, 'docReady') AS DocReadyState,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'docHidden') AS BIT) AS DocHidden,
    dbo.GetQueryParam(p.QueryString, 'docVisibility') AS DocVisibility,
    
    -- ============================================
    -- BOT DETECTION
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'botSignals') AS BotSignals,           -- Comma-separated list of detected bot indicators
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) AS BotScore,  -- Cumulative risk score (higher = more likely bot)
    dbo.GetQueryParam(p.QueryString, 'evasionDetected') AS EvasionDetected, -- Privacy tools/spoofing detected
    
    -- scriptExecTime: Milliseconds from page load to script execution
    -- KEY BOT INDICATOR:
    --   < 10ms  = Almost certainly bot (instant DOM, no network stack)
    --   10-50ms = Suspicious (could be fast cache hit)
    --   50-200ms = Normal human range
    --   > 200ms = Slow connection/device, definitely human
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scriptExecTime') AS INT) AS ScriptExecTimeMs,
    
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvasEvasion') AS BIT) AS CanvasEvasionDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglEvasion') AS BIT) AS WebGLEvasionDetected,
    
    -- ============================================
    -- HIGH-ENTROPY CLIENT HINTS
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'uaArch') AS UA_Architecture,          -- x86, arm, etc.
    dbo.GetQueryParam(p.QueryString, 'uaBitness') AS UA_Bitness,            -- 32 or 64
    dbo.GetQueryParam(p.QueryString, 'uaModel') AS UA_Model,                -- Device model (mobile)
    dbo.GetQueryParam(p.QueryString, 'uaPlatformVersion') AS UA_PlatformVersion, -- Full OS version
    dbo.GetQueryParam(p.QueryString, 'uaFullVersion') AS UA_FullVersionList,-- Browser version details
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'uaWow64') AS BIT) AS UA_Wow64,  -- 32-bit on 64-bit Windows
    dbo.GetQueryParam(p.QueryString, 'uaFormFactor') AS UA_FormFactor,      -- phone, tablet, desktop, etc.
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'uaMobile') AS BIT) AS UA_Mobile,
    dbo.GetQueryParam(p.QueryString, 'uaPlatform') AS UA_Platform,
    dbo.GetQueryParam(p.QueryString, 'uaBrands') AS UA_Brands,
    
    -- ============================================
    -- FIREFOX-SPECIFIC
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'oscpu') AS Firefox_OSCPU,
    dbo.GetQueryParam(p.QueryString, 'buildID') AS Firefox_BuildID,
    
    -- ============================================
    -- CHROME-SPECIFIC
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'chromeObj') AS BIT) AS Chrome_ObjectPresent,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'chromeRuntime') AS BIT) AS Chrome_RuntimePresent,
    dbo.GetQueryParam(p.QueryString, 'jsHeapLimit') AS Chrome_JSHeapLimit,
    dbo.GetQueryParam(p.QueryString, 'jsHeapTotal') AS Chrome_JSHeapTotal,
    dbo.GetQueryParam(p.QueryString, 'jsHeapUsed') AS Chrome_JSHeapUsed,
    
    -- ============================================
    -- ADDITIONAL FINGERPRINTS
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'audioHash') AS AudioHash,             -- Full audio buffer hash
    dbo.GetQueryParam(p.QueryString, 'pluginList') AS PluginListDetail,     -- Full plugin names/descriptions
    dbo.GetQueryParam(p.QueryString, 'mimeList') AS MimeTypeList,           -- Full MIME type list
    dbo.GetQueryParam(p.QueryString, 'tzLocale') AS TimezoneLocale,         -- Locale formatting details
    dbo.GetQueryParam(p.QueryString, 'dateFormat') AS DateFormatSample,     -- How dates render
    dbo.GetQueryParam(p.QueryString, 'numberFormat') AS NumberFormatSample, -- How numbers render
    dbo.GetQueryParam(p.QueryString, 'relativeTime') AS RelativeTimeFormat, -- Intl.RelativeTimeFormat output (added 2026-02-04)
    dbo.GetQueryParam(p.QueryString, 'cssFontVariant') AS CSSFontVariant,   -- CSS font feature support
    
    -- ============================================
    -- PRIVACY EXTENSION DETECTION (added 2026-02-04)
    -- ============================================
    -- _proxyBlocked: Comma-separated list of navigator properties that threw errors
    -- Caused by privacy extensions (JShelter, Trace, Privacy Badger) wrapping navigator in Proxy
    -- Example: "javaEnabled,platform,languages,"
    -- Presence of this field = privacy extension detected (itself a fingerprint signal!)
    dbo.GetQueryParam(p.QueryString, '_proxyBlocked') AS ProxyBlockedProperties,
    
    -- ============================================
    -- RAW DATA (for debugging/reprocessing)
    -- ============================================
    p.QueryString AS RawQueryString

FROM dbo.PiXL_Test p;
GO

-- =============================================
-- Create a view to identify privacy extension users
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_PrivacyExtensionUsers', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_PrivacyExtensionUsers;
GO

CREATE VIEW dbo.vw_Dashboard_PrivacyExtensionUsers AS
SELECT 
    Id,
    IPAddress,
    ReceivedAt,
    ProxyBlockedProperties,
    -- Count how many properties were blocked
    LEN(ProxyBlockedProperties) - LEN(REPLACE(ProxyBlockedProperties, ',', '')) AS BlockedPropertyCount,
    -- Common blocked properties
    CASE 
        WHEN ProxyBlockedProperties LIKE '%javaEnabled%' THEN 1 
        ELSE 0 
    END AS JavaEnabledBlocked,
    CASE 
        WHEN ProxyBlockedProperties LIKE '%platform%' THEN 1 
        ELSE 0 
    END AS PlatformBlocked,
    CASE 
        WHEN ProxyBlockedProperties LIKE '%languages%' THEN 1 
        ELSE 0 
    END AS LanguagesBlocked,
    EvasionDetected,
    BotScore
FROM dbo.vw_PiXL_Parsed
WHERE ProxyBlockedProperties IS NOT NULL 
  AND LEN(ProxyBlockedProperties) > 0;
GO

-- =============================================
-- Update the dashboard DevOps view to include proxy blocked count
-- =============================================
IF OBJECT_ID('dbo.vw_Dashboard_DevOps', 'V') IS NOT NULL
    DROP VIEW dbo.vw_Dashboard_DevOps;
GO

CREATE VIEW dbo.vw_Dashboard_DevOps AS
SELECT TOP 100
    p.Id,
    p.ReceivedAt,
    -- Compute local time from timezone offset
    DATEADD(MINUTE, -ISNULL(p.TimezoneOffset, 0), p.ReceivedAt) AS LocalReceivedAt,
    p.IPAddress,
    p.CompanyID,
    p.PiXLID,
    p.Tier,
    
    -- Fingerprints
    p.CanvasFingerprint,
    p.WebGLFingerprint,
    p.AudioFingerprint,
    
    -- Bot Detection
    p.BotScore,
    p.BotSignals,
    p.ScriptExecTimeMs,
    p.WebDriverDetected,
    
    -- Evasion
    p.EvasionDetected,
    p.CanvasEvasionDetected,
    p.WebGLEvasionDetected,
    
    -- Privacy Extension Detection
    p.ProxyBlockedProperties,
    CASE 
        WHEN p.ProxyBlockedProperties IS NOT NULL AND LEN(p.ProxyBlockedProperties) > 0 
        THEN LEN(p.ProxyBlockedProperties) - LEN(REPLACE(p.ProxyBlockedProperties, ',', ''))
        ELSE 0 
    END AS ProxyBlockedCount,
    
    -- ColorDepth Anomaly (24-bit on macOS = suspicious)
    p.ColorDepth,
    CASE 
        WHEN p.Platform LIKE '%Mac%' AND p.ColorDepth = 24 THEN 1
        WHEN p.ColorDepth NOT IN (24, 30, 32, 48) AND p.ColorDepth IS NOT NULL THEN 1
        ELSE 0 
    END AS ColorDepthAnomaly,
    
    -- Device Summary
    LEFT(p.ClientUserAgent, 100) AS UserAgentSummary,
    p.Platform,
    p.ScreenWidth,
    p.ScreenHeight,
    p.CPUCores,
    p.DeviceMemory,
    
    -- Error indicator
    CASE WHEN p.RawQueryString LIKE '%error=1%' THEN 1 ELSE 0 END AS HasError,
    
    -- Raw for debugging
    p.RawQueryString
FROM dbo.vw_PiXL_Parsed p
ORDER BY p.Id DESC;
GO

PRINT 'Schema patch 09 complete!';
PRINT '- Added ProxyBlockedProperties to vw_PiXL_Parsed';
PRINT '- Added UA_FormFactor and UA_Wow64 fields';
PRINT '- Added RelativeTimeFormat field';
PRINT '- Created vw_Dashboard_PrivacyExtensionUsers view';
PRINT '- Updated vw_Dashboard_DevOps with ProxyBlockedCount';
GO
