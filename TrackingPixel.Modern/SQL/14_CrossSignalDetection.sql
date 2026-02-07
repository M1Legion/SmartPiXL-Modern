-- =============================================
-- SmartPiXL: Cross-Signal Consistency Detection
-- Migration 14
--
-- Adds 7 new columns to vw_PiXL_Complete from the blue team
-- analysis of stealth synthetic traffic. These detect sophisticated
-- bots that pass individual bot checks but have contradictory
-- signals across subsystems (fonts vs platform, Safari on Chromium,
-- SwiftShader on Mac, scroll contradictions, behavioral uniformity).
--
-- New JS params (from cross-signal analysis in Tier5Script.cs):
--   crossSignals        - CS-01..CS-08 flag list
--   anomalyScore        - Composite cross-signal anomaly score
--   scrollContradiction - CS-04: scrolled=1 but scrollY=0
--   moveTimingCV        - CS-09: mouse timing coefficient of variation
--   moveSpeedCV         - CS-10: mouse speed coefficient of variation
--   moveCountBucket     - CS-11: low/mid/high/very-high move count
--   behavioralFlags     - CS-09/10: uniform-timing, uniform-speed
--
-- Run AFTER 13_CompleteViewExpansion.sql
-- =============================================

USE SmartPixl;
GO

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
    -- GROUP 6: NAVIGATOR PROPERTIES (12 columns)
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
    -- GROUP 8: FINGERPRINT HASHES (7 columns)
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
    -- GROUP 12: STORAGE (6 columns)
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
    -- GROUP 15: BROWSER CAPABILITIES (9 columns)
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
    -- GROUP 16: HARDWARE API SUPPORT (3 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mediaDevices') AS BIT) AS MediaDevicesAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'clipboard') AS BIT) AS ClipboardAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'speechSynth') AS BIT) AS SpeechSynthesisSupported,
    
    -- ======================================================================
    -- GROUP 17: INPUT CAPABILITIES (4 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touchEvent') AS BIT) AS TouchEventsSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pointerEvent') AS BIT) AS PointerEventsSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'hover') AS BIT) AS HoverCapable,
    dbo.GetQueryParam(p.QueryString, 'pointer') AS PointerType,
    
    -- ======================================================================
    -- GROUP 18: DISPLAY PREFERENCES (8 columns)
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
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'docCharset') AS DocumentCharset,
    dbo.GetQueryParam(p.QueryString, 'docCompat') AS DocumentCompatMode,
    dbo.GetQueryParam(p.QueryString, 'docReady') AS DocumentReadyState,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'docHidden') AS BIT) AS DocumentHidden,
    dbo.GetQueryParam(p.QueryString, 'docVisibility') AS DocumentVisibility,
    
    -- ======================================================================
    -- GROUP 20: PAGE CONTEXT (8 columns)
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
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'loadTime') AS INT) AS PageLoadTimeMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'domTime') AS INT) AS DOMReadyTimeMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'dnsTime') AS INT) AS DNSLookupMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tcpTime') AS INT) AS TCPConnectMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ttfb') AS INT) AS TimeToFirstByteMs,
    
    -- ======================================================================
    -- GROUP 22: BOT DETECTION (6 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'botSignals') AS BotSignalsList,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) AS BotScore,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scriptExecTime') AS INT) AS ScriptExecutionTimeMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botPermInconsistent') AS BIT) AS BotPermissionInconsistent,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvasEvasion') AS BIT) AS CanvasEvasionDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglEvasion') AS BIT) AS WebGLEvasionDetected,
    
    -- ======================================================================
    -- GROUP 23: EVASION / PRIVACY TOOL DETECTION (2 columns)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'evasionDetected') AS EvasionToolsDetected,
    dbo.GetQueryParam(p.QueryString, '_proxyBlocked') AS ProxyBlockedProperties,
    
    -- ======================================================================
    -- GROUP 24: USER AGENT CLIENT HINTS (10 columns)
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
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'oscpu') AS Firefox_OSCPU,
    dbo.GetQueryParam(p.QueryString, 'buildID') AS Firefox_BuildID,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'chromeObj') AS BIT) AS Chrome_ObjectPresent,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'chromeRuntime') AS BIT) AS Chrome_RuntimePresent,
    
    -- ======================================================================
    -- GROUP 26: CHROME MEMORY (3 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'jsHeapLimit') AS BIGINT) AS Chrome_JSHeapSizeLimit,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'jsHeapTotal') AS BIGINT) AS Chrome_TotalJSHeapSize,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'jsHeapUsed') AS BIGINT) AS Chrome_UsedJSHeapSize,
    
    -- ======================================================================
    -- GROUP 27: V-01 CANVAS NOISE DETECTION (1 column)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'canvasConsistency') AS CanvasConsistency,
    
    -- ======================================================================
    -- GROUP 28: V-02 AUDIO STABILITY (2 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'audioStable') AS BIT) AS AudioIsStable,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'audioNoiseDetected') AS BIT) AS AudioNoiseInjectionDetected,
    
    -- ======================================================================
    -- GROUP 29: V-03 BEHAVIORAL ANALYSIS (4 columns)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mouseMoves') AS INT) AS MouseMoveCount,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scrolled') AS BIT) AS UserScrolled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scrollY') AS INT) AS ScrollDepthPx,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mouseEntropy') AS INT) AS MouseEntropy,
    
    -- ======================================================================
    -- GROUP 30: V-04 STEALTH PLUGIN DETECTION (1 column)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'stealthSignals') AS StealthPluginSignals,
    
    -- ======================================================================
    -- GROUP 31: V-09 FONT METHOD MISMATCH (1 column)
    -- ======================================================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'fontMethodMismatch') AS BIT) AS FontMethodMismatch,
    
    -- ======================================================================
    -- GROUP 32: V-10 ENHANCED EVASION SIGNALS (1 column)
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'evasionSignalsV2') AS EvasionSignalsV2,
    
    -- ======================================================================
    -- GROUP 33: CROSS-SIGNAL CONSISTENCY DETECTION (7 columns) [NEW - Migration 14]
    -- Detects sophisticated bots that pass individual checks but have
    -- contradictory signals across multiple subsystems.
    --
    -- crossSignals:        CS-01..CS-08 comma-separated flag list
    --                      win-fonts-on-mac, win-fonts-on-linux, safari-google-vendor,
    --                      safari-has-chrome-obj, safari-has-client-hints, safari-chromium-gpu,
    --                      swiftshader-gpu, swiftshader-on-mac, swiftshader-on-linux,
    --                      llvmpipe-on-mac, scroll-no-depth, round-heap-size,
    --                      heap-total-equals-used, instant-page-load, zero-latency-connection,
    --                      connection-missing-rtt, webgl2-on-old-safari
    -- anomalyScore:        Composite score from all cross-signal checks (0 = clean)
    -- scrollContradiction: CS-04: UserScrolled=1 but ScrollDepthPx=0
    -- moveTimingCV:        CS-09: Coefficient of variation of mouse move timing (x1000)
    --                      < 300 = suspiciously uniform (bot), > 500 = human-like
    -- moveSpeedCV:         CS-10: Coefficient of variation of mouse speed (x1000)
    --                      < 200 = suspiciously uniform (bot)
    -- moveCountBucket:     CS-11: low (<5) / mid (5-19) / high (20-49) / very-high (50+)
    -- behavioralFlags:     CS-09/10: uniform-timing, uniform-speed
    -- ======================================================================
    dbo.GetQueryParam(p.QueryString, 'crossSignals') AS CrossSignalFlags,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'anomalyScore') AS INT) AS AnomalyScore,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scrollContradiction') AS BIT) AS ScrollContradiction,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'moveTimingCV') AS INT) AS MoveTimingCV,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'moveSpeedCV') AS INT) AS MoveSpeedCV,
    dbo.GetQueryParam(p.QueryString, 'moveCountBucket') AS MoveCountBucket,
    dbo.GetQueryParam(p.QueryString, 'behavioralFlags') AS BehavioralFlags,
    
    -- ======================================================================
    -- GROUP 34: RAW DATA (2 columns)
    -- ======================================================================
    p.QueryString AS RawQueryString,
    p.HeadersJson AS RawHeadersJson

FROM dbo.PiXL_Test p;
GO

PRINT '';
PRINT '==============================================';
PRINT 'Migration 14: Cross-Signal Consistency Detection';
PRINT '==============================================';
PRINT '';
PRINT 'NEW COLUMNS (Cross-Signal Analysis):';
PRINT '  CrossSignalFlags      - Comma-separated CS-01..CS-08 flags';
PRINT '  AnomalyScore          - Composite cross-signal anomaly score';
PRINT '  ScrollContradiction   - CS-04: scrolled=1 but depth=0';
PRINT '  MoveTimingCV          - CS-09: mouse timing uniformity (x1000)';
PRINT '  MoveSpeedCV           - CS-10: mouse speed uniformity (x1000)';
PRINT '  MoveCountBucket       - CS-11: low/mid/high/very-high';
PRINT '  BehavioralFlags       - CS-09/10: uniform-timing, uniform-speed';
PRINT '';
PRINT 'DETECTION RULES ADDED TO SCRIPT:';
PRINT '  CS-01: Cross-platform font inconsistency (Win fonts on Mac/Linux)';
PRINT '  CS-02: Safari-on-Chromium (Safari UA + Google vendor/ANGLE GPU)';
PRINT '  CS-03: GPU/Platform cross-reference (SwiftShader on Mac/Linux)';
PRINT '  CS-04: Scroll depth contradiction (scroll event but no movement)';
PRINT '  CS-05: Heap size fingerprint (round 10MB = Playwright default)';
PRINT '  CS-06: Navigation timing anomaly (instant page load)';
PRINT '  CS-07: Connection API realism (4g + no RTT = injected)';
PRINT '  CS-08: WebGL2 on old Safari (Chromium leak)';
PRINT '  CS-09: Mouse timing uniformity (CV < 0.3 = algorithmic)';
PRINT '  CS-10: Mouse speed uniformity (CV < 0.2 = linear interpolation)';
PRINT '  CS-11: Move count bucketing (for server-side clustering)';
PRINT '';
PRINT 'Total view columns: 168 (was 161)';
GO
