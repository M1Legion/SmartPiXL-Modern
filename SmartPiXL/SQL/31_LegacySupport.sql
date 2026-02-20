-- ============================================================================
-- 31_LegacySupport.sql
-- Legacy pixel hit support: adds HitType column to PiXL.Parsed and PiXL.Visit,
-- updates ETL.usp_ParseNewHits to parse _srv_hitType from QueryString.
--
-- Legacy hits arrive as bare <img src=".../_SMART.GIF"> requests with no
-- JavaScript query string. They have: IP, User-Agent, Referer, Accept-Language,
-- and all HTTP headers. They do NOT have: canvas/WebGL/audio fingerprints,
-- screen resolution, device hardware, behavioral biometrics, or bot detection.
--
-- The C# endpoint appends _srv_hitType=legacy|modern to the query string.
-- This migration adds the column to materialize that in PiXL.Parsed/Visit.
--
-- Safe to re-run (IF NOT EXISTS guards on column adds, CREATE OR ALTER on proc).
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
PRINT '=== 31_LegacySupport.sql ===';
PRINT '';

-- ============================================================================
-- STEP 1: Add HitType column to PiXL.Parsed
-- ============================================================================
IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'PiXL' AND TABLE_NAME = 'Parsed' AND COLUMN_NAME = 'HitType'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD HitType VARCHAR(10) NULL;
    PRINT 'Added HitType column to PiXL.Parsed';
END
ELSE
    PRINT 'HitType column already exists on PiXL.Parsed';
GO

-- ============================================================================
-- STEP 2: Add HitType column to PiXL.Visit
-- ============================================================================
IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'PiXL' AND TABLE_NAME = 'Visit' AND COLUMN_NAME = 'HitType'
)
BEGIN
    ALTER TABLE PiXL.Visit ADD HitType VARCHAR(10) NULL;
    PRINT 'Added HitType column to PiXL.Visit';
END
ELSE
    PRINT 'HitType column already exists on PiXL.Visit';
GO

-- ============================================================================
-- STEP 3: Backfill existing rows as 'modern' (all existing data is from PiXLScript)
-- ============================================================================
UPDATE PiXL.Parsed SET HitType = 'modern' WHERE HitType IS NULL;
PRINT CONCAT('Backfilled ', @@ROWCOUNT, ' existing PiXL.Parsed rows as modern');

UPDATE PiXL.Visit SET HitType = 'modern' WHERE HitType IS NULL;
PRINT CONCAT('Backfilled ', @@ROWCOUNT, ' existing PiXL.Visit rows as modern');
GO

-- ============================================================================
-- STEP 4: Update ETL.usp_ParseNewHits to parse _srv_hitType
-- This is the full proc definition with HitType support added.
-- Uses CREATE OR ALTER for idempotency.
-- ============================================================================
PRINT '--- Updating ETL.usp_ParseNewHits with HitType support ---';
GO

CREATE OR ALTER PROCEDURE [ETL].[usp_ParseNewHits]
    @BatchSize INT = 10000
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @LastId BIGINT, @MaxId BIGINT, @Inserted INT;
    SELECT @LastId = LastProcessedId FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits';
    -- Self-healing: if Parsed has rows beyond the watermark (partial commit recovery)
    DECLARE @MaxParsedId BIGINT = (SELECT ISNULL(MAX(SourceId), 0) FROM PiXL.Parsed);
    IF @MaxParsedId > @LastId SET @LastId = @MaxParsedId;
    SELECT @MaxId = MAX(Id) FROM PiXL.Raw;
    IF @MaxId IS NULL OR @MaxId <= @LastId
    BEGIN
        SELECT 0 AS RowsParsed, @LastId AS FromId, @LastId AS ToId,
               0 AS DevicesUpserted, 0 AS IPsUpserted, 0 AS VisitsInserted;
        RETURN;
    END;
    IF @MaxId > @LastId + @BatchSize SET @MaxId = @LastId + @BatchSize;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- =====================================================================
        -- PHASE 1: INSERT — Server + Screen + Locale (~30 UDF calls)
        -- Now includes HitType parsed from _srv_hitType query param.
        -- =====================================================================
        INSERT INTO PiXL.Parsed (
            SourceId, CompanyID, PiXLID, IPAddress, ReceivedAt, RequestPath,
            ServerUserAgent, ServerReferer, IsSynthetic, HitType, Tier,
            ScreenWidth, ScreenHeight, ScreenAvailWidth, ScreenAvailHeight,
            ViewportWidth, ViewportHeight, OuterWidth, OuterHeight,
            ScreenX, ScreenY, ColorDepth, PixelRatio, ScreenOrientation,
            Timezone, TimezoneOffsetMins, ClientTimestampMs, TimezoneLocale,
            DateFormatSample, NumberFormatSample, RelativeTimeSample,
            Language, LanguageList)
        SELECT p.Id, p.CompanyID, p.PiXLID, p.IPAddress, p.ReceivedAt, p.RequestPath,
            p.UserAgent, p.Referer,
            CASE WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'synthetic') AS INT) = 1 THEN 1 ELSE 0 END,
            -- HitType: 'legacy' or 'modern' from the _srv_hitType server-side param
            COALESCE(dbo.GetQueryParam(p.QueryString, '_srv_hitType'), 'modern'),
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
        FROM PiXL.Raw p WHERE p.Id > @LastId AND p.Id <= @MaxId;
        SET @Inserted = @@ROWCOUNT;

        -- =====================================================================
        -- PHASE 2: UPDATE — Browser + GPU + Fingerprints (~26 UDF calls)
        -- =====================================================================
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
        FROM PiXL.Parsed pp JOIN PiXL.Raw src ON pp.SourceId = src.Id
        WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;

        -- =====================================================================
        -- PHASE 3: UPDATE — Plugins + Network + Storage + Capabilities1 (~29 UDF calls)
        -- =====================================================================
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
        FROM PiXL.Parsed pp JOIN PiXL.Raw src ON pp.SourceId = src.Id
        WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;

        -- =====================================================================
        -- PHASE 4: UPDATE — Capabilities2 + Prefs + Document (~24 UDF calls)
        -- =====================================================================
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
        FROM PiXL.Parsed pp JOIN PiXL.Raw src ON pp.SourceId = src.Id
        WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;

        -- =====================================================================
        -- PHASE 5: UPDATE — Page + Performance + Bot (~18 UDF calls)
        -- =====================================================================
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
        FROM PiXL.Parsed pp JOIN PiXL.Raw src ON pp.SourceId = src.Id
        WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;

        -- =====================================================================
        -- PHASE 6: UPDATE — Evasion + Client Hints + Browser-specific (~24 UDF calls)
        -- =====================================================================
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
        FROM PiXL.Parsed pp JOIN PiXL.Raw src ON pp.SourceId = src.Id
        WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;

        -- =====================================================================
        -- PHASE 7: UPDATE — Behavioral + Cross-signal (~14 UDF calls)
        -- =====================================================================
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
        FROM PiXL.Parsed pp JOIN PiXL.Raw src ON pp.SourceId = src.Id
        WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;

        -- =====================================================================
        -- PHASE 8: UPDATE — Server-side IP behavior signals (~7 UDF calls)
        -- =====================================================================
        UPDATE pp SET
            Srv_SubnetIps   = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_subnetIps') AS INT),
            Srv_SubnetHits  = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_subnetHits') AS INT),
            Srv_HitsIn15s   = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_hitsIn15s') AS INT),
            Srv_LastGapMs   = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_lastGapMs') AS BIGINT),
            Srv_SubSecDupe  = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_subSecDupe') AS BIT),
            Srv_SubnetAlert = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_subnetAlert') AS BIT),
            Srv_RapidFire   = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_rapidFire') AS BIT)
        FROM PiXL.Parsed pp JOIN PiXL.Raw src ON pp.SourceId = src.Id
        WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;

        -- =============================================================================
        -- PHASE 9: Build #BatchRows with DeviceHash
        -- =============================================================================
        -- Materialize the batch of newly-parsed rows into a temp table.
        -- DeviceHash = SHA2_256 of 5 high-entropy fingerprint fields:
        --   CanvasFingerprint, DetectedFonts, GPURenderer, WebGLFingerprint, AudioFingerprintHash
        -- CONCAT_WS('|', ...) prevents false collisions (delimiter between components).
        -- If ALL 5 are NULL (legacy hit or synthetic/bot hit), DeviceHash = NULL -> no Device row created.
        -- ALL rows get included (including test data) because Device/IP are platform-wide.
        -- The Visit INSERT (Phase 13) filters to valid numeric CompanyID/PiXLID only.

        CREATE TABLE #BatchRows (
            SourceId        BIGINT          NOT NULL  PRIMARY KEY,
            CompanyID       INT             NULL,      -- TRY_CAST from varchar; NULL = test data
            PiXLID          INT             NULL,      -- TRY_CAST from varchar; NULL = test data
            IPAddress       VARCHAR(50)     NULL,
            ReceivedAt      DATETIME2(3)    NOT NULL,
            HitType         VARCHAR(10)     NULL,
            DeviceHash      VARBINARY(32)   NULL,
            DeviceId        BIGINT          NULL,
            IpId            BIGINT          NULL,
            ClientParamsJson JSON           NULL,
            MatchEmail      NVARCHAR(200)   NULL
        );

        INSERT INTO #BatchRows (SourceId, CompanyID, PiXLID, IPAddress, ReceivedAt, HitType, DeviceHash)
        SELECT
            p.SourceId,
            TRY_CAST(p.CompanyID AS INT),
            TRY_CAST(p.PiXLID AS INT),
            p.IPAddress,
            p.ReceivedAt,
            p.HitType,
            -- DeviceHash: Only compute if at least one fingerprint component is present.
            -- Legacy hits will have NULL DeviceHash (no JS fingerprints available).
            CASE
                WHEN COALESCE(p.CanvasFingerprint, p.DetectedFonts, p.GPURenderer,
                              p.WebGLFingerprint, p.AudioFingerprintHash) IS NOT NULL
                THEN HASHBYTES('SHA2_256',
                    CONCAT_WS('|',
                        p.CanvasFingerprint, p.DetectedFonts, p.GPURenderer,
                        p.WebGLFingerprint, p.AudioFingerprintHash))
                ELSE NULL
            END
        FROM PiXL.Parsed p
        WHERE p.SourceId > @LastId AND p.SourceId <= @MaxId;

        -- Track counts for the result set
        DECLARE @DevicesUpserted INT = 0, @IPsUpserted INT = 0, @VisitsInserted INT = 0;

        -- =============================================================================
        -- PHASE 10: MERGE PiXL.Device
        -- =============================================================================
        -- Upsert device records using the computed DeviceHash.
        -- DISTINCT DeviceHash in the source prevents "MERGE attempted to update
        -- the same row more than once" when multiple batch rows share a device.
        -- Legacy hits have NULL DeviceHash and are excluded by the WHERE clause.

        MERGE PiXL.Device AS target
        USING (
            SELECT DeviceHash,
                   MIN(ReceivedAt) AS BatchFirstSeen,
                   MAX(ReceivedAt) AS BatchLastSeen,
                   COUNT(*)        AS BatchHitCount
            FROM #BatchRows
            WHERE DeviceHash IS NOT NULL
            GROUP BY DeviceHash
        ) AS source ON target.DeviceHash = source.DeviceHash

        WHEN MATCHED THEN UPDATE SET
            LastSeen = CASE WHEN source.BatchLastSeen > target.LastSeen
                            THEN source.BatchLastSeen ELSE target.LastSeen END,
            HitCount = target.HitCount + source.BatchHitCount

        WHEN NOT MATCHED THEN INSERT (DeviceHash, FirstSeen, LastSeen, HitCount)
            VALUES (source.DeviceHash, source.BatchFirstSeen, source.BatchLastSeen, source.BatchHitCount);

        SET @DevicesUpserted = @@ROWCOUNT;

        -- Post-join: resolve DeviceId back into #BatchRows
        UPDATE b SET b.DeviceId = d.DeviceId
        FROM #BatchRows b
        JOIN PiXL.Device d ON b.DeviceHash = d.DeviceHash
        WHERE b.DeviceHash IS NOT NULL;


        -- =============================================================================
        -- PHASE 11: MERGE PiXL.IP
        -- =============================================================================
        -- Upsert IP records. Same DISTINCT pattern as Device MERGE.
        -- Works identically for legacy and modern hits — both provide an IP address.

        MERGE PiXL.IP AS target
        USING (
            SELECT IPAddress,
                   MIN(ReceivedAt) AS BatchFirstSeen,
                   MAX(ReceivedAt) AS BatchLastSeen,
                   COUNT(*)        AS BatchHitCount
            FROM #BatchRows
            WHERE IPAddress IS NOT NULL
            GROUP BY IPAddress
        ) AS source ON target.IPAddress = source.IPAddress

        WHEN MATCHED THEN UPDATE SET
            LastSeen = CASE WHEN source.BatchLastSeen > target.LastSeen
                            THEN source.BatchLastSeen ELSE target.LastSeen END,
            HitCount = target.HitCount + source.BatchHitCount

        WHEN NOT MATCHED THEN INSERT (IPAddress, FirstSeen, LastSeen, HitCount)
            VALUES (source.IPAddress, source.BatchFirstSeen, source.BatchLastSeen, source.BatchHitCount);

        SET @IPsUpserted = @@ROWCOUNT;

        -- Post-join: resolve IpId back into #BatchRows
        UPDATE b SET b.IpId = ip.IpId
        FROM #BatchRows b
        JOIN PiXL.IP ip ON b.IPAddress = ip.IPAddress
        WHERE b.IPAddress IS NOT NULL;


        -- =============================================================================
        -- PHASE 12: Extract _cp_* Client Parameters
        -- =============================================================================
        -- Scans PiXL.Raw.QueryString for params prefixed with '_cp_'.
        -- Strips the prefix, URL-decodes values (via dbo.GetQueryParam), and
        -- aggregates into a JSON object using SQL Server 2025's JSON_OBJECTAGG.
        -- Result is native json type — no CAST from NVARCHAR needed.
        --
        -- Also extracts MatchEmail = JSON_VALUE(ClientParamsJson, '$.email')
        -- for identity resolution (used by ETL.usp_MatchVisits).

        UPDATE b SET
            b.ClientParamsJson = cp.JsonObj,
            b.MatchEmail = JSON_VALUE(cp.JsonObj, '$.email')
        FROM #BatchRows b
        OUTER APPLY (
            SELECT JSON_OBJECTAGG(
                SUBSTRING(s.value, 5, CHARINDEX('=', s.value + '=') - 5)
                :
                dbo.GetQueryParam(t.QueryString, SUBSTRING(s.value, 1, CHARINDEX('=', s.value + '=') - 1))
            ) AS JsonObj
            FROM PiXL.Raw t
            CROSS APPLY STRING_SPLIT(t.QueryString, '&') s
            WHERE t.Id = b.SourceId
              AND s.value LIKE '[_]cp[_]%=_%'
        ) cp
        WHERE cp.JsonObj IS NOT NULL;


        -- =============================================================================
        -- PHASE 13: INSERT PiXL.Visit (now includes HitType)
        -- =============================================================================
        -- Simple INSERT (not MERGE) — PiXL.Visit is 1:1 with PiXL.Parsed.
        -- The watermark guarantees each SourceId is processed exactly once.
        --
        -- Filter: Only rows with valid numeric CompanyID and PiXLID.
        -- Test data (CompanyID='CLIENT_ID', 'DEMO') remains in PiXL.Parsed
        -- but does NOT get a Visit row (no Company/Pixel FK target exists).
        --
        -- Legacy hits get a Visit row with DeviceId=NULL (no JS fingerprint).
        -- They still have IpId and all server-side enrichment data.
        --
        -- NOT EXISTS guard prevents FK violations if this batch includes a
        -- SourceId that already has a Visit row (safety for re-entry scenarios).

        INSERT INTO PiXL.Visit (VisitID, CompanyID, PiXLID, DeviceId, IpId,
                                 ReceivedAt, HitType, ClientParamsJson, MatchEmail)
        SELECT b.SourceId, b.CompanyID, b.PiXLID, b.DeviceId, b.IpId,
               b.ReceivedAt, b.HitType, b.ClientParamsJson, b.MatchEmail
        FROM #BatchRows b
        WHERE b.CompanyID IS NOT NULL
          AND b.PiXLID IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM PiXL.Visit v WHERE v.VisitID = b.SourceId);

        SET @VisitsInserted = @@ROWCOUNT;

        DROP TABLE #BatchRows;


        -- Update watermark inside transaction (same as before)
        UPDATE ETL.Watermark SET LastProcessedId = @MaxId,
            LastRunAt = SYSUTCDATETIME(), RowsProcessed = RowsProcessed + @Inserted
        WHERE ProcessName = 'ParseNewHits';

        COMMIT TRANSACTION;
        SELECT @Inserted AS RowsParsed, @LastId + 1 AS FromId, @MaxId AS ToId,
               @DevicesUpserted AS DevicesUpserted, @IPsUpserted AS IPsUpserted,
               @VisitsInserted AS VisitsInserted;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        -- Clean up temp table if it exists (rollback doesn't drop temp tables)
        IF OBJECT_ID('tempdb..#BatchRows') IS NOT NULL DROP TABLE #BatchRows;
        THROW;
    END CATCH;
END;
GO

PRINT '';
PRINT '=== 31_LegacySupport.sql complete ===';
