-- ============================================================================
-- Migration 17: IP Behavior Signals + Bot Scoring Improvements
-- ============================================================================
-- Date: 2026-02-08
-- Purpose:
--   1. Add server-side IP behavior columns to PiXL_Parsed (subnet velocity,
--      rapid-fire timing)
--   2. Update usp_ParseNewHits with Phase 8 for new _srv_* params
--   3. Update vw_PiXL_Complete to expose the new columns
--   4. Update dashboard views to use new bot scoring signals
--
-- JS Changes (Tier5Script.cs — no SQL impact, flows through querystring):
--   - chrome-no-runtime weight reduced from 3 → 1
--   - heap-size-spoofed promoted from cross-signal to primary bot signal (+8)
--   - heap-total-equals-used promoted from cross-signal to primary bot signal (+5)
--   - gpu-platform-mismatch added as cross-signal flag (+15 anomaly)
--   - round-heap-limit added as cross-signal flag (+5 anomaly)
--   - CSSFontVariantHash rewritten to produce actual computed values
--
-- Server Changes (IpBehaviorService.cs):
--   - Subnet /24 velocity: 3+ unique IPs in same /24 in 5min = alert
--   - Rapid-fire timing: 3+ hits from same IP in 15sec = alert
--   - Sub-second duplicate detection
-- ============================================================================

USE SmartPixl;
GO

-- ============================================================================
-- 1. ADD NEW COLUMNS TO PiXL_Parsed
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PiXL_Parsed') AND name = 'Srv_SubnetIps')
BEGIN
    ALTER TABLE dbo.PiXL_Parsed ADD
        -- Server-side IP behavior signals
        Srv_SubnetIps       INT     NULL,    -- Unique IPs in same /24 in 5min window
        Srv_SubnetHits      INT     NULL,    -- Total hits from same /24 in 5min window
        Srv_HitsIn15s       INT     NULL,    -- Hits from same IP in 15 second window
        Srv_LastGapMs       BIGINT  NULL,    -- Milliseconds since last hit from same IP
        Srv_SubSecDupe      BIT     NULL,    -- Sub-second duplicate from same IP
        Srv_SubnetAlert     BIT     NULL,    -- Subnet /24 velocity alert fired
        Srv_RapidFire       BIT     NULL;    -- Rapid-fire timing alert fired
    PRINT 'Added Srv_* columns to PiXL_Parsed';
END;
GO

-- ============================================================================
-- 2. UPDATE STORED PROCEDURE — Add Phase 8 for _srv_* params
-- ============================================================================
CREATE OR ALTER PROCEDURE dbo.usp_ParseNewHits
    @BatchSize INT = 10000
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @LastId INT, @MaxId INT, @Inserted INT;
    SELECT @LastId = LastProcessedId FROM dbo.ETL_Watermark WHERE ProcessName = 'ParseNewHits';
    SELECT @MaxId = MAX(Id) FROM dbo.PiXL_Test;
    IF @MaxId IS NULL OR @MaxId <= @LastId
    BEGIN
        SELECT 0 AS RowsParsed, @LastId AS FromId, @LastId AS ToId;
        RETURN;
    END;
    IF @MaxId > @LastId + @BatchSize SET @MaxId = @LastId + @BatchSize;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- PHASE 1: INSERT — Server + Screen + Locale (~30 UDF calls)
        INSERT INTO dbo.PiXL_Parsed (
            SourceId, CompanyID, PiXLID, IPAddress, ReceivedAt, RequestPath,
            ServerUserAgent, ServerReferer, IsSynthetic, Tier,
            ScreenWidth, ScreenHeight, ScreenAvailWidth, ScreenAvailHeight,
            ViewportWidth, ViewportHeight, OuterWidth, OuterHeight,
            ScreenX, ScreenY, ColorDepth, PixelRatio, ScreenOrientation,
            Timezone, TimezoneOffsetMins, ClientTimestampMs, TimezoneLocale,
            DateFormatSample, NumberFormatSample, RelativeTimeSample,
            Language, LanguageList)
        SELECT p.Id, p.CompanyID, p.PiXLID, p.IPAddress, p.ReceivedAt, p.RequestPath,
            p.UserAgent, p.Referer,
            CASE WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'synthetic') AS INT) = 1 THEN 1 ELSE 0 END,
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tier') AS INT),
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sw') AS INT),
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sh') AS INT),
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'saw') AS INT),
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sah') AS INT),
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'vw') AS INT),
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'vh') AS INT),
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ow') AS INT),
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'oh') AS INT),
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sx') AS INT),
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'sy') AS INT),
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cd') AS INT),
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pd') AS DECIMAL(5,2)),
            dbo.GetQueryParam(p.QueryString, 'ori'),
            dbo.GetQueryParam(p.QueryString, 'tz'),
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tzo') AS INT),
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ts') AS BIGINT),
            dbo.GetQueryParam(p.QueryString, 'tzLocale'),
            dbo.GetQueryParam(p.QueryString, 'dateFormat'),
            dbo.GetQueryParam(p.QueryString, 'numberFormat'),
            dbo.GetQueryParam(p.QueryString, 'relativeTime'),
            dbo.GetQueryParam(p.QueryString, 'lang'),
            dbo.GetQueryParam(p.QueryString, 'langs')
        FROM dbo.PiXL_Test p WHERE p.Id > @LastId AND p.Id <= @MaxId;
        SET @Inserted = @@ROWCOUNT;

        -- PHASE 2: UPDATE — Browser + GPU + Fingerprints (~26 UDF calls)
        UPDATE pp SET
            Platform = dbo.GetQueryParam(src.QueryString, 'plt'),
            Vendor = dbo.GetQueryParam(src.QueryString, 'vnd'),
            ClientUserAgent = dbo.GetQueryParam(src.QueryString, 'ua'),
            HardwareConcurrency = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'cores') AS INT),
            DeviceMemoryGB = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'mem') AS DECIMAL(5,2)),
            MaxTouchPoints = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'touch') AS INT),
            NavigatorProduct = dbo.GetQueryParam(src.QueryString, 'product'),
            NavigatorProductSub = dbo.GetQueryParam(src.QueryString, 'productSub'),
            NavigatorVendorSub = dbo.GetQueryParam(src.QueryString, 'vendorSub'),
            AppName = dbo.GetQueryParam(src.QueryString, 'appName'),
            AppVersion = dbo.GetQueryParam(src.QueryString, 'appVersion'),
            AppCodeName = dbo.GetQueryParam(src.QueryString, 'appCodeName'),
            GPURenderer = dbo.GetQueryParam(src.QueryString, 'gpu'),
            GPUVendor = dbo.GetQueryParam(src.QueryString, 'gpuVendor'),
            WebGLParameters = dbo.GetQueryParam(src.QueryString, 'webglParams'),
            WebGLExtensionCount = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'webglExt') AS INT),
            WebGLSupported = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'webgl') AS BIT),
            WebGL2Supported = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'webgl2') AS BIT),
            CanvasFingerprint = dbo.GetQueryParam(src.QueryString, 'canvasFP'),
            WebGLFingerprint = dbo.GetQueryParam(src.QueryString, 'webglFP'),
            AudioFingerprintSum = dbo.GetQueryParam(src.QueryString, 'audioFP'),
            AudioFingerprintHash = dbo.GetQueryParam(src.QueryString, 'audioHash'),
            MathFingerprint = dbo.GetQueryParam(src.QueryString, 'mathFP'),
            ErrorFingerprint = dbo.GetQueryParam(src.QueryString, 'errorFP'),
            CSSFontVariantHash = dbo.GetQueryParam(src.QueryString, 'cssFontVariant'),
            DetectedFonts = dbo.GetQueryParam(src.QueryString, 'fonts')
        FROM dbo.PiXL_Parsed pp JOIN dbo.PiXL_Test src ON pp.SourceId = src.Id
        WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;

        -- PHASE 3: UPDATE — Plugins + Network + Storage + Capabilities1 (~29 UDF calls)
        UPDATE pp SET
            PluginCount = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'plugins') AS INT),
            PluginListDetailed = dbo.GetQueryParam(src.QueryString, 'pluginList'),
            MimeTypeCount = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'mimeTypes') AS INT),
            MimeTypeList = dbo.GetQueryParam(src.QueryString, 'mimeList'),
            SpeechVoices = dbo.GetQueryParam(src.QueryString, 'voices'),
            ConnectedGamepads = dbo.GetQueryParam(src.QueryString, 'gamepads'),
            WebRTCLocalIP = dbo.GetQueryParam(src.QueryString, 'localIp'),
            ConnectionType = dbo.GetQueryParam(src.QueryString, 'conn'),
            DownlinkMbps = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'dl') AS DECIMAL(10,2)),
            DownlinkMax = dbo.GetQueryParam(src.QueryString, 'dlMax'),
            RTTMs = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'rtt') AS INT),
            DataSaverEnabled = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'save') AS BIT),
            NetworkType = dbo.GetQueryParam(src.QueryString, 'connType'),
            IsOnline = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'online') AS BIT),
            StorageQuotaGB = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'storageQuota') AS INT),
            StorageUsedMB = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'storageUsed') AS INT),
            LocalStorageSupported = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'ls') AS BIT),
            SessionStorageSupported = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'ss') AS BIT),
            IndexedDBSupported = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'idb') AS BIT),
            CacheAPISupported = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'caches') AS BIT),
            BatteryLevelPct = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'batteryLevel') AS INT),
            BatteryCharging = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'batteryCharging') AS BIT),
            AudioInputDevices = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'audioInputs') AS INT),
            VideoInputDevices = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'videoInputs') AS INT),
            CookiesEnabled = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'ck') AS BIT),
            DoNotTrack = dbo.GetQueryParam(src.QueryString, 'dnt'),
            PDFViewerEnabled = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'pdf') AS BIT),
            WebDriverDetected = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'webdr') AS BIT),
            JavaEnabled = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'java') AS BIT)
        FROM dbo.PiXL_Parsed pp JOIN dbo.PiXL_Test src ON pp.SourceId = src.Id
        WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;

        -- PHASE 4: UPDATE — Capabilities2 + Prefs + Document (~24 UDF calls)
        UPDATE pp SET
            CanvasSupported = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'canvas') AS BIT),
            WebAssemblySupported = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'wasm') AS BIT),
            WebWorkersSupported = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'ww') AS BIT),
            ServiceWorkerSupported = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'swk') AS BIT),
            MediaDevicesAPISupported = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'mediaDevices') AS BIT),
            ClipboardAPISupported = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'clipboard') AS BIT),
            SpeechSynthesisSupported = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'speechSynth') AS BIT),
            TouchEventsSupported = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'touchEvent') AS BIT),
            PointerEventsSupported = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'pointerEvent') AS BIT),
            HoverCapable = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'hover') AS BIT),
            PointerType = dbo.GetQueryParam(src.QueryString, 'pointer'),
            PrefersColorSchemeDark = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'darkMode') AS BIT),
            PrefersColorSchemeLight = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'lightMode') AS BIT),
            PrefersReducedMotion = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'reducedMotion') AS BIT),
            PrefersReducedData = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'reducedData') AS BIT),
            PrefersHighContrast = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'contrast') AS BIT),
            ForcedColorsActive = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'forcedColors') AS BIT),
            InvertedColorsActive = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'invertedColors') AS BIT),
            StandaloneDisplayMode = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'standalone') AS BIT),
            DocumentCharset = dbo.GetQueryParam(src.QueryString, 'docCharset'),
            DocumentCompatMode = dbo.GetQueryParam(src.QueryString, 'docCompat'),
            DocumentReadyState = dbo.GetQueryParam(src.QueryString, 'docReady'),
            DocumentHidden = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'docHidden') AS BIT),
            DocumentVisibility = dbo.GetQueryParam(src.QueryString, 'docVisibility')
        FROM dbo.PiXL_Parsed pp JOIN dbo.PiXL_Test src ON pp.SourceId = src.Id
        WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;

        -- PHASE 5: UPDATE — Page + Performance + Bot (~18 UDF calls)
        UPDATE pp SET
            PageURL = dbo.GetQueryParam(src.QueryString, 'url'),
            PageReferrer = dbo.GetQueryParam(src.QueryString, 'ref'),
            PageTitle = dbo.GetQueryParam(src.QueryString, 'title'),
            PageDomain = dbo.GetQueryParam(src.QueryString, 'domain'),
            PagePath = dbo.GetQueryParam(src.QueryString, 'path'),
            PageHash = dbo.GetQueryParam(src.QueryString, 'hash'),
            PageProtocol = dbo.GetQueryParam(src.QueryString, 'protocol'),
            HistoryLength = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'hist') AS INT),
            PageLoadTimeMs = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'loadTime') AS INT),
            DOMReadyTimeMs = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'domTime') AS INT),
            DNSLookupMs = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'dnsTime') AS INT),
            TCPConnectMs = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'tcpTime') AS INT),
            TimeToFirstByteMs = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'ttfb') AS INT),
            BotSignalsList = dbo.GetQueryParam(src.QueryString, 'botSignals'),
            BotScore = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'botScore') AS INT),
            CombinedThreatScore = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'combinedThreatScore') AS INT),
            ScriptExecutionTimeMs = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'scriptExecTime') AS INT),
            BotPermissionInconsistent = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'botPermInconsistent') AS BIT)
        FROM dbo.PiXL_Parsed pp JOIN dbo.PiXL_Test src ON pp.SourceId = src.Id
        WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;

        -- PHASE 6: UPDATE — Evasion + Client Hints + Browser-specific (~24 UDF calls)
        UPDATE pp SET
            CanvasEvasionDetected = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'canvasEvasion') AS BIT),
            WebGLEvasionDetected = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'webglEvasion') AS BIT),
            EvasionToolsDetected = dbo.GetQueryParam(src.QueryString, 'evasionDetected'),
            ProxyBlockedProperties = dbo.GetQueryParam(src.QueryString, '_proxyBlocked'),
            UA_Architecture = dbo.GetQueryParam(src.QueryString, 'uaArch'),
            UA_Bitness = dbo.GetQueryParam(src.QueryString, 'uaBitness'),
            UA_Model = dbo.GetQueryParam(src.QueryString, 'uaModel'),
            UA_PlatformVersion = dbo.GetQueryParam(src.QueryString, 'uaPlatformVersion'),
            UA_FullVersionList = dbo.GetQueryParam(src.QueryString, 'uaFullVersion'),
            UA_IsWow64 = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'uaWow64') AS BIT),
            UA_IsMobile = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'uaMobile') AS BIT),
            UA_Platform = dbo.GetQueryParam(src.QueryString, 'uaPlatform'),
            UA_Brands = dbo.GetQueryParam(src.QueryString, 'uaBrands'),
            UA_FormFactor = dbo.GetQueryParam(src.QueryString, 'uaFormFactor'),
            Firefox_OSCPU = dbo.GetQueryParam(src.QueryString, 'oscpu'),
            Firefox_BuildID = dbo.GetQueryParam(src.QueryString, 'buildID'),
            Chrome_ObjectPresent = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'chromeObj') AS BIT),
            Chrome_RuntimePresent = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'chromeRuntime') AS BIT),
            Chrome_JSHeapSizeLimit = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'jsHeapLimit') AS BIGINT),
            Chrome_TotalJSHeapSize = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'jsHeapTotal') AS BIGINT),
            Chrome_UsedJSHeapSize = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'jsHeapUsed') AS BIGINT),
            CanvasConsistency = dbo.GetQueryParam(src.QueryString, 'canvasConsistency'),
            AudioIsStable = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'audioStable') AS BIT),
            AudioNoiseInjectionDetected = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'audioNoiseDetected') AS BIT)
        FROM dbo.PiXL_Parsed pp JOIN dbo.PiXL_Test src ON pp.SourceId = src.Id
        WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;

        -- PHASE 7: UPDATE — Behavioral + Cross-signal (~14 UDF calls)
        UPDATE pp SET
            MouseMoveCount = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'mouseMoves') AS INT),
            UserScrolled = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'scrolled') AS BIT),
            ScrollDepthPx = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'scrollY') AS INT),
            MouseEntropy = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'mouseEntropy') AS INT),
            ScrollContradiction = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'scrollContradiction') AS BIT),
            MoveTimingCV = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'moveTimingCV') AS INT),
            MoveSpeedCV = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'moveSpeedCV') AS INT),
            MoveCountBucket = dbo.GetQueryParam(src.QueryString, 'moveCountBucket'),
            BehavioralFlags = dbo.GetQueryParam(src.QueryString, 'behavioralFlags'),
            StealthPluginSignals = dbo.GetQueryParam(src.QueryString, 'stealthSignals'),
            FontMethodMismatch = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'fontMethodMismatch') AS BIT),
            EvasionSignalsV2 = dbo.GetQueryParam(src.QueryString, 'evasionSignalsV2'),
            CrossSignalFlags = dbo.GetQueryParam(src.QueryString, 'crossSignals'),
            AnomalyScore = TRY_CAST(dbo.GetQueryParam(src.QueryString, 'anomalyScore') AS INT)
        FROM dbo.PiXL_Parsed pp JOIN dbo.PiXL_Test src ON pp.SourceId = src.Id
        WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;

        -- PHASE 8: UPDATE — Server-side IP behavior signals (~7 UDF calls)
        -- These _srv_* params are appended server-side by IpBehaviorService
        -- when subnet velocity or rapid-fire alerts fire.
        UPDATE pp SET
            Srv_SubnetIps   = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_subnetIps') AS INT),
            Srv_SubnetHits  = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_subnetHits') AS INT),
            Srv_HitsIn15s   = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_hitsIn15s') AS INT),
            Srv_LastGapMs   = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_lastGapMs') AS BIGINT),
            Srv_SubSecDupe  = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_subSecDupe') AS BIT),
            Srv_SubnetAlert = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_subnetAlert') AS BIT),
            Srv_RapidFire   = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_rapidFire') AS BIT)
        FROM dbo.PiXL_Parsed pp JOIN dbo.PiXL_Test src ON pp.SourceId = src.Id
        WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;

        -- Update watermark inside transaction
        UPDATE dbo.ETL_Watermark SET LastProcessedId = @MaxId,
            LastRunAt = SYSUTCDATETIME(), RowsProcessed = RowsProcessed + @Inserted
        WHERE ProcessName = 'ParseNewHits';

        COMMIT TRANSACTION;
        SELECT @Inserted AS RowsParsed, @LastId + 1 AS FromId, @MaxId AS ToId;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO

PRINT 'Updated usp_ParseNewHits with Phase 8 (IP behavior signals)';
GO

-- ============================================================================
-- 3. UPDATE vw_PiXL_Complete — add new columns
-- ============================================================================
ALTER VIEW dbo.vw_PiXL_Complete AS
SELECT 
    p.Id,
    p.CompanyID,
    p.PiXLID,
    p.IPAddress,
    p.ReceivedAt,
    p.RequestPath,
    p.UserAgent AS ServerUserAgent,
    p.Referer AS ServerReferer,
    CASE 
        WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'synthetic') AS INT) = 1 THEN 1
        ELSE 0
    END AS IsSynthetic,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tier') AS INT) AS Tier,
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
    dbo.GetQueryParam(p.QueryString, 'tz') AS Timezone,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tzo') AS INT) AS TimezoneOffsetMins,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ts') AS BIGINT) AS ClientTimestampMs,
    dbo.GetQueryParam(p.QueryString, 'tzLocale') AS TimezoneLocale,
    dbo.GetQueryParam(p.QueryString, 'dateFormat') AS DateFormatSample,
    dbo.GetQueryParam(p.QueryString, 'numberFormat') AS NumberFormatSample,
    dbo.GetQueryParam(p.QueryString, 'relativeTime') AS RelativeTimeSample,
    dbo.GetQueryParam(p.QueryString, 'lang') AS Language,
    dbo.GetQueryParam(p.QueryString, 'langs') AS LanguageList,
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
    dbo.GetQueryParam(p.QueryString, 'gpu') AS GPURenderer,
    dbo.GetQueryParam(p.QueryString, 'gpuVendor') AS GPUVendor,
    dbo.GetQueryParam(p.QueryString, 'webglParams') AS WebGLParameters,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglExt') AS INT) AS WebGLExtensionCount,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webgl') AS BIT) AS WebGLSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webgl2') AS BIT) AS WebGL2Supported,
    dbo.GetQueryParam(p.QueryString, 'canvasFP') AS CanvasFingerprint,
    dbo.GetQueryParam(p.QueryString, 'webglFP') AS WebGLFingerprint,
    dbo.GetQueryParam(p.QueryString, 'audioFP') AS AudioFingerprintSum,
    dbo.GetQueryParam(p.QueryString, 'audioHash') AS AudioFingerprintHash,
    dbo.GetQueryParam(p.QueryString, 'mathFP') AS MathFingerprint,
    dbo.GetQueryParam(p.QueryString, 'errorFP') AS ErrorFingerprint,
    dbo.GetQueryParam(p.QueryString, 'cssFontVariant') AS CSSFontVariantHash,
    dbo.GetQueryParam(p.QueryString, 'fonts') AS DetectedFonts,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'plugins') AS INT) AS PluginCount,
    dbo.GetQueryParam(p.QueryString, 'pluginList') AS PluginListDetailed,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mimeTypes') AS INT) AS MimeTypeCount,
    dbo.GetQueryParam(p.QueryString, 'mimeList') AS MimeTypeList,
    dbo.GetQueryParam(p.QueryString, 'voices') AS SpeechVoices,
    dbo.GetQueryParam(p.QueryString, 'gamepads') AS ConnectedGamepads,
    dbo.GetQueryParam(p.QueryString, 'localIp') AS WebRTCLocalIP,
    dbo.GetQueryParam(p.QueryString, 'conn') AS ConnectionType,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'dl') AS DECIMAL(10,2)) AS DownlinkMbps,
    dbo.GetQueryParam(p.QueryString, 'dlMax') AS DownlinkMax,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'rtt') AS INT) AS RTTMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'save') AS BIT) AS DataSaverEnabled,
    dbo.GetQueryParam(p.QueryString, 'connType') AS NetworkType,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'online') AS BIT) AS IsOnline,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'storageQuota') AS INT) AS StorageQuotaGB,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'storageUsed') AS INT) AS StorageUsedMB,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ls') AS BIT) AS LocalStorageSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ss') AS BIT) AS SessionStorageSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'idb') AS BIT) AS IndexedDBSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'caches') AS BIT) AS CacheAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'batteryLevel') AS INT) AS BatteryLevelPct,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'batteryCharging') AS BIT) AS BatteryCharging,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'audioInputs') AS INT) AS AudioInputDevices,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'videoInputs') AS INT) AS VideoInputDevices,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ck') AS BIT) AS CookiesEnabled,
    dbo.GetQueryParam(p.QueryString, 'dnt') AS DoNotTrack,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pdf') AS BIT) AS PDFViewerEnabled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webdr') AS BIT) AS WebDriverDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'java') AS BIT) AS JavaEnabled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvas') AS BIT) AS CanvasSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'wasm') AS BIT) AS WebAssemblySupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ww') AS BIT) AS WebWorkersSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'swk') AS BIT) AS ServiceWorkerSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mediaDevices') AS BIT) AS MediaDevicesAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'clipboard') AS BIT) AS ClipboardAPISupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'speechSynth') AS BIT) AS SpeechSynthesisSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touchEvent') AS BIT) AS TouchEventsSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pointerEvent') AS BIT) AS PointerEventsSupported,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'hover') AS BIT) AS HoverCapable,
    dbo.GetQueryParam(p.QueryString, 'pointer') AS PointerType,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'darkMode') AS BIT) AS PrefersColorSchemeDark,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'lightMode') AS BIT) AS PrefersColorSchemeLight,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'reducedMotion') AS BIT) AS PrefersReducedMotion,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'reducedData') AS BIT) AS PrefersReducedData,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'contrast') AS BIT) AS PrefersHighContrast,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'forcedColors') AS BIT) AS ForcedColorsActive,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'invertedColors') AS BIT) AS InvertedColorsActive,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'standalone') AS BIT) AS StandaloneDisplayMode,
    dbo.GetQueryParam(p.QueryString, 'docCharset') AS DocumentCharset,
    dbo.GetQueryParam(p.QueryString, 'docCompat') AS DocumentCompatMode,
    dbo.GetQueryParam(p.QueryString, 'docReady') AS DocumentReadyState,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'docHidden') AS BIT) AS DocumentHidden,
    dbo.GetQueryParam(p.QueryString, 'docVisibility') AS DocumentVisibility,
    dbo.GetQueryParam(p.QueryString, 'url') AS PageURL,
    dbo.GetQueryParam(p.QueryString, 'ref') AS PageReferrer,
    dbo.GetQueryParam(p.QueryString, 'title') AS PageTitle,
    dbo.GetQueryParam(p.QueryString, 'domain') AS PageDomain,
    dbo.GetQueryParam(p.QueryString, 'path') AS PagePath,
    dbo.GetQueryParam(p.QueryString, 'hash') AS PageHash,
    dbo.GetQueryParam(p.QueryString, 'protocol') AS PageProtocol,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'hist') AS INT) AS HistoryLength,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'loadTime') AS INT) AS PageLoadTimeMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'domTime') AS INT) AS DOMReadyTimeMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'dnsTime') AS INT) AS DNSLookupMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tcpTime') AS INT) AS TCPConnectMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ttfb') AS INT) AS TimeToFirstByteMs,
    dbo.GetQueryParam(p.QueryString, 'botSignals') AS BotSignalsList,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botScore') AS INT) AS BotScore,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'combinedThreatScore') AS INT) AS CombinedThreatScore,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scriptExecTime') AS INT) AS ScriptExecutionTimeMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'botPermInconsistent') AS BIT) AS BotPermissionInconsistent,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvasEvasion') AS BIT) AS CanvasEvasionDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglEvasion') AS BIT) AS WebGLEvasionDetected,
    dbo.GetQueryParam(p.QueryString, 'evasionDetected') AS EvasionToolsDetected,
    dbo.GetQueryParam(p.QueryString, '_proxyBlocked') AS ProxyBlockedProperties,
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
    dbo.GetQueryParam(p.QueryString, 'oscpu') AS Firefox_OSCPU,
    dbo.GetQueryParam(p.QueryString, 'buildID') AS Firefox_BuildID,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'chromeObj') AS BIT) AS Chrome_ObjectPresent,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'chromeRuntime') AS BIT) AS Chrome_RuntimePresent,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'jsHeapLimit') AS BIGINT) AS Chrome_JSHeapSizeLimit,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'jsHeapTotal') AS BIGINT) AS Chrome_TotalJSHeapSize,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'jsHeapUsed') AS BIGINT) AS Chrome_UsedJSHeapSize,
    dbo.GetQueryParam(p.QueryString, 'canvasConsistency') AS CanvasConsistency,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'audioStable') AS BIT) AS AudioIsStable,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'audioNoiseDetected') AS BIT) AS AudioNoiseInjectionDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mouseMoves') AS INT) AS MouseMoveCount,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scrolled') AS BIT) AS UserScrolled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scrollY') AS INT) AS ScrollDepthPx,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mouseEntropy') AS INT) AS MouseEntropy,
    dbo.GetQueryParam(p.QueryString, 'stealthSignals') AS StealthPluginSignals,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'fontMethodMismatch') AS BIT) AS FontMethodMismatch,
    dbo.GetQueryParam(p.QueryString, 'evasionSignalsV2') AS EvasionSignalsV2,
    dbo.GetQueryParam(p.QueryString, 'crossSignals') AS CrossSignalFlags,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'anomalyScore') AS INT) AS AnomalyScore,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'scrollContradiction') AS BIT) AS ScrollContradiction,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'moveTimingCV') AS INT) AS MoveTimingCV,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'moveSpeedCV') AS INT) AS MoveSpeedCV,
    dbo.GetQueryParam(p.QueryString, 'moveCountBucket') AS MoveCountBucket,
    dbo.GetQueryParam(p.QueryString, 'behavioralFlags') AS BehavioralFlags,
    -- Server-side IP behavior signals (Migration 17)
    TRY_CAST(dbo.GetQueryParam(p.QueryString, '_srv_subnetIps') AS INT) AS Srv_SubnetIps,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, '_srv_subnetHits') AS INT) AS Srv_SubnetHits,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, '_srv_hitsIn15s') AS INT) AS Srv_HitsIn15s,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, '_srv_lastGapMs') AS BIGINT) AS Srv_LastGapMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, '_srv_subSecDupe') AS BIT) AS Srv_SubSecDupe,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, '_srv_subnetAlert') AS BIT) AS Srv_SubnetAlert,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, '_srv_rapidFire') AS BIT) AS Srv_RapidFire,
    p.QueryString AS RawQueryString,
    p.HeadersJson AS RawHeadersJson
FROM dbo.PiXL_Test p;
GO

PRINT 'Updated vw_PiXL_Complete with IP behavior columns (Migration 17)';
GO

-- ============================================================================
-- 4. UPDATE vw_Dash_RecentHits — add IP behavior alerts to live feed
-- ============================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_RecentHits AS
SELECT TOP 100
    pp.SourceId,
    pp.ReceivedAt,
    pp.IPAddress,
    pp.CompanyID,
    pp.PiXLID,
    CASE
        WHEN pp.BotScore >= 80 THEN 'HIGH'
        WHEN pp.BotScore >= 50 THEN 'MED'
        WHEN pp.BotScore >= 20 THEN 'LOW'
        ELSE 'OK'
    END AS ThreatLevel,
    pp.BotScore,
    pp.CombinedThreatScore,
    pp.AnomalyScore,
    pp.BotSignalsList,
    pp.CrossSignalFlags,
    CASE
        WHEN pp.ClientUserAgent LIKE '%Edg/%' THEN 'Edge'
        WHEN pp.ClientUserAgent LIKE '%Chrome/%' THEN 'Chrome'
        WHEN pp.ClientUserAgent LIKE '%Firefox/%' THEN 'Firefox'
        WHEN pp.ClientUserAgent LIKE '%Safari/%' THEN 'Safari'
        WHEN pp.ClientUserAgent LIKE '%Opera%' OR pp.ClientUserAgent LIKE '%OPR/%' THEN 'Opera'
        ELSE 'Other'
    END AS Browser,
    pp.Platform,
    CAST(pp.ScreenWidth AS VARCHAR(8)) + 'x' + CAST(pp.ScreenHeight AS VARCHAR(8)) AS Resolution,
    LEFT(pp.CanvasFingerprint, 8) AS FP_Short,
    pp.GPURenderer,
    pp.Timezone,
    pp.IsSynthetic,
    -- New: IP behavior alerts
    pp.Srv_SubnetAlert,
    pp.Srv_RapidFire,
    pp.Srv_SubSecDupe,
    pp.Srv_HitsIn15s,
    pp.Srv_LastGapMs
FROM dbo.PiXL_Parsed pp
ORDER BY pp.ReceivedAt DESC;
GO

PRINT 'Updated vw_Dash_RecentHits with IP behavior columns';
GO

-- ============================================================================
-- 5. NEW VIEW: vw_Dash_SubnetClusters — subnet velocity analysis
-- ============================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_SubnetClusters AS
WITH SubnetData AS (
    SELECT
        LEFT(pp.IPAddress, LEN(pp.IPAddress) - CHARINDEX('.', REVERSE(pp.IPAddress))) AS Subnet24,
        pp.IPAddress,
        pp.CanvasFingerprint,
        pp.BotScore,
        pp.GPURenderer,
        pp.Platform,
        pp.ScreenWidth,
        pp.ScreenHeight,
        pp.ReceivedAt,
        pp.Srv_SubnetAlert
    FROM dbo.PiXL_Parsed pp
    WHERE pp.IPAddress IS NOT NULL
      AND pp.IPAddress LIKE '%.%.%.%'     -- IPv4 only
      AND pp.IPAddress NOT LIKE '%:%'     -- Exclude IPv6
)
SELECT
    Subnet24,
    COUNT(*) AS TotalHits,
    COUNT(DISTINCT IPAddress) AS UniqueIPs,
    COUNT(DISTINCT CanvasFingerprint) AS UniqueFingerprints,
    AVG(BotScore) AS AvgBotScore,
    MIN(ReceivedAt) AS FirstSeen,
    MAX(ReceivedAt) AS LastSeen,
    DATEDIFF(SECOND, MIN(ReceivedAt), MAX(ReceivedAt)) AS SpanSeconds,
    MAX(CASE WHEN Srv_SubnetAlert = 1 THEN 1 ELSE 0 END) AS HasSubnetAlert
FROM SubnetData
GROUP BY Subnet24
HAVING COUNT(DISTINCT IPAddress) >= 2;   -- At least 2 IPs in subnet
GO

PRINT 'Created vw_Dash_SubnetClusters view';
GO

-- ============================================================================
-- 6. INDEX for subnet alert queries
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Parsed_SubnetAlert' AND object_id = OBJECT_ID('PiXL_Parsed'))
    CREATE NONCLUSTERED INDEX IX_Parsed_SubnetAlert
        ON dbo.PiXL_Parsed (Srv_SubnetAlert)
        INCLUDE (IPAddress, ReceivedAt, BotScore, CanvasFingerprint, Srv_RapidFire)
        WHERE Srv_SubnetAlert = 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Parsed_RapidFire' AND object_id = OBJECT_ID('PiXL_Parsed'))
    CREATE NONCLUSTERED INDEX IX_Parsed_RapidFire
        ON dbo.PiXL_Parsed (Srv_RapidFire)
        INCLUDE (IPAddress, ReceivedAt, BotScore, Srv_HitsIn15s)
        WHERE Srv_RapidFire = 1;
GO

PRINT 'Migration 17 complete: IP Behavior Signals + Bot Scoring Improvements';
GO
