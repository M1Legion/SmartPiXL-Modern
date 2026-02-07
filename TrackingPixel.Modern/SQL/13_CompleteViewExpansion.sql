-- =============================================
-- SmartPiXL: vw_PiXL_Complete Full Expansion
-- Migration 13
--
-- Recreates vw_PiXL_Complete with ALL 155 columns matching every
-- data.* property in Tier5Script.cs. Migration 12 introduced wrong
-- param names (e.g. webglVendor vs gpuVendor, cookies vs ck).
-- This migration restores the correct param names from 06 and adds
-- the 13 new evasion/behavioral fields from V-01 through V-10.
--
-- New fields added (not in 06_AnalyticsViews.sql):
--   _proxyBlocked       (safeGet blocked props)
--   canvasConsistency   (V-01: noise injection detection)
--   audioStable         (V-02: audio stability check)
--   audioNoiseDetected  (V-02: audio noise injection)
--   fontMethodMismatch  (V-09: dual-method font spoof)
--   stealthSignals      (V-04: stealth plugin detection)
--   evasionSignalsV2    (V-10: enhanced Tor/evasion)
--   mouseMoves          (V-03: behavioral mouse count)
--   scrolled            (V-03: did user scroll)
--   scrollY             (V-03: scroll depth)
--   mouseEntropy        (V-03: mouse movement entropy)
--   botPermInconsistent (permission inconsistency flag)
--   IsSynthetic         (synthetic test flag)
--
-- Run AFTER 12_SyntheticTestingSupport.sql
-- =============================================

USE SmartPixl;
GO

-- =============================================
-- Recreate vw_PiXL_Complete with ALL fields
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
    -- GROUP 2: SYNTHETIC FLAG + TIER (2 columns)
    -- ======================================================================
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'synthetic') AS INT) = 1 THEN 1
        ELSE 0
    END AS IsSynthetic,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tier') AS INT) AS Tier,
    
    -- ======================================================================
    -- GROUP 3: SCREEN DIMENSIONS (13 columns)
    -- Params: sw, sh, saw, sah, vw, vh, ow, oh, sx, sy, cd, pd, ori
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
    -- Params: tz, tzo, ts, tzLocale, dateFormat, numberFormat, relativeTime
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
    -- Params: lang, langs
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'lang') AS Language,
    dbo.GetQueryParam(p.QueryString, 'langs') AS LanguageList,
    
    -- ======================================================================
    -- GROUP 6: NAVIGATOR PROPERTIES (12 columns)
    -- Params: plt, vnd, ua, cores, mem, touch, product, productSub, vendorSub,
    --         appName, appVersion, appCodeName
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
    -- Params: gpu, gpuVendor, webglParams, webglExt, webgl, webgl2
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'gpu') AS GPURenderer,
    dbo.GetQueryParam(p.QueryString, 'gpuVendor') AS GPUVendor,
    dbo.GetQueryParam(p.QueryString, 'webglParams') AS WebGLParameters,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglExt') AS INT) AS WebGLExtensionCount,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webgl') AS BIT) AS WebGLSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webgl2') AS BIT) AS WebGL2Supported,
    
    -- ======================================================================
    -- GROUP 8: FINGERPRINT HASHES (7 columns)
    -- Params: canvasFP, webglFP, audioFP, audioHash, mathFP, errorFP, cssFontVariant
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'canvasFP') AS CanvasFingerprint,
    dbo.GetQueryParam(p.QueryString, 'webglFP') AS WebGLFingerprint,
    dbo.GetQueryParam(p.QueryString, 'audioFP') AS AudioFingerprintSum,
    dbo.GetQueryParam(p.QueryString, 'audioHash') AS AudioFingerprintHash,
    dbo.GetQueryParam(p.QueryString, 'mathFP') AS MathFingerprint,
    dbo.GetQueryParam(p.QueryString, 'errorFP') AS ErrorFingerprint,
    dbo.GetQueryParam(p.QueryString, 'cssFontVariant') AS CSSFontVariantHash,
    
    -- ======================================================================
    -- GROUP 9: FONT & PLUGIN DETECTION (5 columns)
    -- Params: fonts, plugins, pluginList, mimeTypes, mimeList
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'fonts') AS DetectedFonts,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'plugins') AS INT) AS PluginCount,
    dbo.GetQueryParam(p.QueryString, 'pluginList') AS PluginListDetailed,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mimeTypes') AS INT) AS MimeTypeCount,
    dbo.GetQueryParam(p.QueryString, 'mimeList') AS MimeTypeList,
    
    -- ======================================================================
    -- GROUP 10: SPEECH & GAMEPADS (2 columns)
    -- Params: voices, gamepads
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'voices') AS SpeechVoices,
    dbo.GetQueryParam(p.QueryString, 'gamepads') AS ConnectedGamepads,
    
    -- ======================================================================
    -- GROUP 11: NETWORK (8 columns)
    -- Params: localIp, conn, dl, dlMax, rtt, save, connType, online
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
    -- GROUP 12: STORAGE (6 columns)
    -- Params: storageQuota, storageUsed, ls, ss, idb, caches
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'storageQuota') AS INT) AS StorageQuotaGB,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'storageUsed') AS INT) AS StorageUsedMB,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ls') AS BIT) AS LocalStorageSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ss') AS BIT) AS SessionStorageSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'idb') AS BIT) AS IndexedDBSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'caches') AS BIT) AS CacheAPISupported,
    
    -- ======================================================================
    -- GROUP 13: BATTERY (2 columns)
    -- Params: batteryLevel, batteryCharging
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'batteryLevel') AS INT) AS BatteryLevelPct,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'batteryCharging') AS BIT) AS BatteryCharging,
    
    -- ======================================================================
    -- GROUP 14: MEDIA DEVICES (2 columns)
    -- Params: audioInputs, videoInputs
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'audioInputs') AS INT) AS AudioInputDevices,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'videoInputs') AS INT) AS VideoInputDevices,
    
    -- ======================================================================
    -- GROUP 15: BROWSER CAPABILITIES (9 columns)
    -- Params: ck, dnt, pdf, webdr, java, canvas, wasm, ww, swk
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ck') AS BIT) AS CookiesEnabled,
    dbo.GetQueryParam(p.QueryString, 'dnt') AS DoNotTrack,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pdf') AS BIT) AS PDFViewerEnabled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webdr') AS BIT) AS WebDriverDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'java') AS BIT) AS JavaEnabled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvas') AS BIT) AS CanvasSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'wasm') AS BIT) AS WebAssemblySupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ww') AS BIT) AS WebWorkersSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'swk') AS BIT) AS ServiceWorkerSupported,
    
    -- ======================================================================
    -- GROUP 16: HARDWARE API SUPPORT (2 columns - remaining after exclusions)
    -- Params: mediaDevices, clipboard, speechSynth
    -- Note: bluetooth, usb, serial, hid, midi, xr, share, credentials,
    --       geolocation, notifications, push, payment, speechRecog removed
    --       from Tier5 to avoid permission prompts
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mediaDevices') AS BIT) AS MediaDevicesAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'clipboard') AS BIT) AS ClipboardAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'speechSynth') AS BIT) AS SpeechSynthesisSupported,
    
    -- ======================================================================
    -- GROUP 17: INPUT CAPABILITIES (4 columns)
    -- Params: touchEvent, pointerEvent, hover, pointer
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touchEvent') AS BIT) AS TouchEventsSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pointerEvent') AS BIT) AS PointerEventsSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'hover') AS BIT) AS HoverCapable,
    dbo.GetQueryParam(p.QueryString, 'pointer') AS PointerType,
    
    -- ======================================================================
    -- GROUP 18: DISPLAY PREFERENCES (8 columns)
    -- Params: darkMode, lightMode, reducedMotion, reducedData, contrast,
    --         forcedColors, invertedColors, standalone
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
    -- GROUP 19: DOCUMENT STATE (5 columns)
    -- Params: docCharset, docCompat, docReady, docHidden, docVisibility
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'docCharset') AS DocumentCharset,
    dbo.GetQueryParam(p.QueryString, 'docCompat') AS DocumentCompatMode,
    dbo.GetQueryParam(p.QueryString, 'docReady') AS DocumentReadyState,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'docHidden') AS BIT) AS DocumentHidden,
    dbo.GetQueryParam(p.QueryString, 'docVisibility') AS DocumentVisibility,
    
    -- ======================================================================
    -- GROUP 20: PAGE CONTEXT (8 columns)
    -- Params: url, ref, title, domain, path, hash, protocol, hist
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
    -- GROUP 21: PERFORMANCE TIMING (5 columns)
    -- Params: loadTime, domTime, dnsTime, tcpTime, ttfb
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'loadTime') AS INT) AS PageLoadTimeMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'domTime') AS INT) AS DOMReadyTimeMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'dnsTime') AS INT) AS DNSLookupMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tcpTime') AS INT) AS TCPConnectMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ttfb') AS INT) AS TimeToFirstByteMs,
    
    -- ======================================================================
    -- GROUP 22: BOT DETECTION (6 columns)
    -- Params: botSignals, botScore, scriptExecTime, botPermInconsistent,
    --         canvasEvasion, webglEvasion
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'botSignals') AS BotSignalsList,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) AS BotScore,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scriptExecTime') AS INT) AS ScriptExecutionTimeMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botPermInconsistent') AS BIT) AS BotPermissionInconsistent,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvasEvasion') AS BIT) AS CanvasEvasionDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglEvasion') AS BIT) AS WebGLEvasionDetected,
    
    -- ======================================================================
    -- GROUP 23: EVASION / PRIVACY TOOL DETECTION (2 columns)
    -- Params: evasionDetected, _proxyBlocked
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'evasionDetected') AS EvasionToolsDetected,
    dbo.GetQueryParam(p.QueryString, '_proxyBlocked') AS ProxyBlockedProperties,
    
    -- ======================================================================
    -- GROUP 24: USER AGENT CLIENT HINTS (10 columns)
    -- Params: uaArch, uaBitness, uaModel, uaPlatformVersion, uaFullVersion,
    --         uaWow64, uaMobile, uaPlatform, uaBrands, uaFormFactor
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
    -- GROUP 25: BROWSER-SPECIFIC SIGNALS (4 columns)
    -- Params: oscpu, buildID, chromeObj, chromeRuntime
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'oscpu') AS Firefox_OSCPU,
    dbo.GetQueryParam(p.QueryString, 'buildID') AS Firefox_BuildID,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'chromeObj') AS BIT) AS Chrome_ObjectPresent,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'chromeRuntime') AS BIT) AS Chrome_RuntimePresent,
    
    -- ======================================================================
    -- GROUP 26: CHROME MEMORY (3 columns)
    -- Params: jsHeapLimit, jsHeapTotal, jsHeapUsed
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'jsHeapLimit') AS BIGINT) AS Chrome_JSHeapSizeLimit,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'jsHeapTotal') AS BIGINT) AS Chrome_TotalJSHeapSize,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'jsHeapUsed') AS BIGINT) AS Chrome_UsedJSHeapSize,
    
    -- ======================================================================
    -- GROUP 27: V-01 CANVAS NOISE DETECTION (1 column)
    -- Params: canvasConsistency
    -- Values: 'clean' | 'noise-detected' | 'canvas-blocked' | 'error'
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'canvasConsistency') AS CanvasConsistency,
    
    -- ======================================================================
    -- GROUP 28: V-02 AUDIO STABILITY (2 columns)
    -- Params: audioStable, audioNoiseDetected
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'audioStable') AS BIT) AS AudioIsStable,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'audioNoiseDetected') AS BIT) AS AudioNoiseInjectionDetected,
    
    -- ======================================================================
    -- GROUP 29: V-03 BEHAVIORAL ANALYSIS (4 columns)
    -- Params: mouseMoves, scrolled, scrollY, mouseEntropy
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mouseMoves') AS INT) AS MouseMoveCount,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scrolled') AS BIT) AS UserScrolled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scrollY') AS INT) AS ScrollDepthPx,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mouseEntropy') AS INT) AS MouseEntropy,
    
    -- ======================================================================
    -- GROUP 30: V-04 STEALTH PLUGIN DETECTION (1 column)
    -- Params: stealthSignals
    -- Values: comma-separated: webdriver-slow, platform-slow, toString-spoofed,
    --         nav-proto-modified, proxy-modified
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'stealthSignals') AS StealthPluginSignals,
    
    -- ======================================================================
    -- GROUP 31: V-09 FONT METHOD MISMATCH (1 column)
    -- Params: fontMethodMismatch
    -- offsetWidth vs getBoundingClientRect disagree = spoofing
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'fontMethodMismatch') AS BIT) AS FontMethodMismatch,
    
    -- ======================================================================
    -- GROUP 32: V-10 ENHANCED EVASION SIGNALS (1 column)
    -- Params: evasionSignalsV2
    -- Values: comma-separated: tor-letterbox-viewport, tor-letterbox-screen,
    --         minimal-fonts, canvas-noise, canvas-blocked, audio-noise,
    --         font-spoof, stealth-detected
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'evasionSignalsV2') AS EvasionSignalsV2,
    
    -- ======================================================================
    -- GROUP 33: RAW DATA (2 columns)
    -- ======================================================================
    p.QueryString AS RawQueryString,
    p.HeadersJson AS RawHeadersJson

FROM dbo.PiXL_Test p;
GO

PRINT '';
PRINT '==============================================';
PRINT 'Migration 13: vw_PiXL_Complete expanded to 155 columns';
PRINT '==============================================';
PRINT '';
PRINT 'NEW COLUMNS (Evasion Countermeasures V-01..V-10):';
PRINT '  CanvasConsistency         (V-01: noise injection detection)';
PRINT '  AudioIsStable             (V-02: audio fingerprint stability)';
PRINT '  AudioNoiseInjectionDetected (V-02: audio noise injection)';
PRINT '  MouseMoveCount            (V-03: behavioral mouse tracking)';
PRINT '  UserScrolled              (V-03: user scrolled page)';
PRINT '  ScrollDepthPx             (V-03: scroll depth in pixels)';
PRINT '  MouseEntropy              (V-03: mouse movement randomness)';
PRINT '  StealthPluginSignals      (V-04: stealth plugin detection)';
PRINT '  FontMethodMismatch        (V-09: font spoof detection)';
PRINT '  EvasionSignalsV2          (V-10: enhanced Tor/evasion)';
PRINT '  ProxyBlockedProperties    (safeGet blocked properties)';
PRINT '  BotPermissionInconsistent (permissions API inconsistency)';
PRINT '';
PRINT 'FIXED COLUMNS (wrong param names from migration 12):';
PRINT '  gpu/gpuVendor (was webglVendor/webglRenderer)';
PRINT '  ck (was cookies)';
PRINT '  conn (was connection)';
PRINT '  dl (was downlink)';
PRINT '  batteryLevel (was battLevel)';
PRINT '  swk (was sw2)';
PRINT '  All 150+ data.* params now correctly mapped';
PRINT '';
GO
