-- ============================================================================
-- Migration 42: Forge Tier 1 Enrichment Columns (Phase 4)
-- ============================================================================
-- Adds 19 new columns to PiXL.Parsed for Tier 1 enrichment data produced
-- by SmartPiXL Forge enrichment services:
--
--   Bot detection:   KnownBot, BotName
--   UA parsing:      ParsedBrowser, ParsedBrowserVersion, ParsedOS, ParsedOSVersion,
--                    ParsedDeviceType, ParsedDeviceModel, ParsedDeviceBrand
--   Reverse DNS:     ReverseDNS, ReverseDNSCloud
--   MaxMind Geo:     MaxMindCountry, MaxMindRegion, MaxMindCity,
--                    MaxMindLat, MaxMindLon, MaxMindASN, MaxMindASNOrg
--   WHOIS:           WhoisASN, WhoisOrg
--
-- Updates ETL.usp_ParseNewHits:
--   New Phase 8B: Parses all _srv_* Tier 1 params via dbo.GetQueryParam()
-- ============================================================================
PRINT '--- 42: Adding Forge Tier 1 enrichment columns to PiXL.Parsed ---';
GO

-- =====================================================================
-- Step 1: Add columns to PiXL.Parsed — Bot Detection
-- =====================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'KnownBot'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD KnownBot BIT NULL;
    PRINT 'Added PiXL.Parsed.KnownBot (BIT NULL)';
END
ELSE PRINT 'PiXL.Parsed.KnownBot already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'BotName'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD BotName VARCHAR(200) NULL;
    PRINT 'Added PiXL.Parsed.BotName (VARCHAR(200) NULL)';
END
ELSE PRINT 'PiXL.Parsed.BotName already exists — skipped';
GO

-- =====================================================================
-- Step 2: Add columns — UA Parsing
-- =====================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'ParsedBrowser'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD ParsedBrowser VARCHAR(100) NULL;
    PRINT 'Added PiXL.Parsed.ParsedBrowser (VARCHAR(100) NULL)';
END
ELSE PRINT 'PiXL.Parsed.ParsedBrowser already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'ParsedBrowserVersion'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD ParsedBrowserVersion VARCHAR(50) NULL;
    PRINT 'Added PiXL.Parsed.ParsedBrowserVersion (VARCHAR(50) NULL)';
END
ELSE PRINT 'PiXL.Parsed.ParsedBrowserVersion already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'ParsedOS'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD ParsedOS VARCHAR(100) NULL;
    PRINT 'Added PiXL.Parsed.ParsedOS (VARCHAR(100) NULL)';
END
ELSE PRINT 'PiXL.Parsed.ParsedOS already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'ParsedOSVersion'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD ParsedOSVersion VARCHAR(50) NULL;
    PRINT 'Added PiXL.Parsed.ParsedOSVersion (VARCHAR(50) NULL)';
END
ELSE PRINT 'PiXL.Parsed.ParsedOSVersion already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'ParsedDeviceType'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD ParsedDeviceType VARCHAR(50) NULL;
    PRINT 'Added PiXL.Parsed.ParsedDeviceType (VARCHAR(50) NULL)';
END
ELSE PRINT 'PiXL.Parsed.ParsedDeviceType already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'ParsedDeviceModel'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD ParsedDeviceModel VARCHAR(100) NULL;
    PRINT 'Added PiXL.Parsed.ParsedDeviceModel (VARCHAR(100) NULL)';
END
ELSE PRINT 'PiXL.Parsed.ParsedDeviceModel already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'ParsedDeviceBrand'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD ParsedDeviceBrand VARCHAR(100) NULL;
    PRINT 'Added PiXL.Parsed.ParsedDeviceBrand (VARCHAR(100) NULL)';
END
ELSE PRINT 'PiXL.Parsed.ParsedDeviceBrand already exists — skipped';
GO

-- =====================================================================
-- Step 3: Add columns — Reverse DNS
-- =====================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'ReverseDNS'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD ReverseDNS VARCHAR(500) NULL;
    PRINT 'Added PiXL.Parsed.ReverseDNS (VARCHAR(500) NULL)';
END
ELSE PRINT 'PiXL.Parsed.ReverseDNS already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'ReverseDNSCloud'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD ReverseDNSCloud BIT NULL;
    PRINT 'Added PiXL.Parsed.ReverseDNSCloud (BIT NULL)';
END
ELSE PRINT 'PiXL.Parsed.ReverseDNSCloud already exists — skipped';
GO

-- =====================================================================
-- Step 4: Add columns — MaxMind Geo
-- =====================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'MaxMindCountry'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD MaxMindCountry CHAR(2) NULL;
    PRINT 'Added PiXL.Parsed.MaxMindCountry (CHAR(2) NULL)';
END
ELSE PRINT 'PiXL.Parsed.MaxMindCountry already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'MaxMindRegion'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD MaxMindRegion VARCHAR(100) NULL;
    PRINT 'Added PiXL.Parsed.MaxMindRegion (VARCHAR(100) NULL)';
END
ELSE PRINT 'PiXL.Parsed.MaxMindRegion already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'MaxMindCity'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD MaxMindCity VARCHAR(200) NULL;
    PRINT 'Added PiXL.Parsed.MaxMindCity (VARCHAR(200) NULL)';
END
ELSE PRINT 'PiXL.Parsed.MaxMindCity already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'MaxMindLat'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD MaxMindLat DECIMAL(9,6) NULL;
    PRINT 'Added PiXL.Parsed.MaxMindLat (DECIMAL(9,6) NULL)';
END
ELSE PRINT 'PiXL.Parsed.MaxMindLat already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'MaxMindLon'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD MaxMindLon DECIMAL(9,6) NULL;
    PRINT 'Added PiXL.Parsed.MaxMindLon (DECIMAL(9,6) NULL)';
END
ELSE PRINT 'PiXL.Parsed.MaxMindLon already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'MaxMindASN'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD MaxMindASN INT NULL;
    PRINT 'Added PiXL.Parsed.MaxMindASN (INT NULL)';
END
ELSE PRINT 'PiXL.Parsed.MaxMindASN already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'MaxMindASNOrg'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD MaxMindASNOrg VARCHAR(200) NULL;
    PRINT 'Added PiXL.Parsed.MaxMindASNOrg (VARCHAR(200) NULL)';
END
ELSE PRINT 'PiXL.Parsed.MaxMindASNOrg already exists — skipped';
GO

-- =====================================================================
-- Step 5: Add columns — WHOIS
-- =====================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'WhoisASN'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD WhoisASN VARCHAR(50) NULL;
    PRINT 'Added PiXL.Parsed.WhoisASN (VARCHAR(50) NULL)';
END
ELSE PRINT 'PiXL.Parsed.WhoisASN already exists — skipped';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'PiXL.Parsed')
      AND name = N'WhoisOrg'
)
BEGIN
    ALTER TABLE PiXL.Parsed ADD WhoisOrg VARCHAR(200) NULL;
    PRINT 'Added PiXL.Parsed.WhoisOrg (VARCHAR(200) NULL)';
END
ELSE PRINT 'PiXL.Parsed.WhoisOrg already exists — skipped';
GO

-- =====================================================================
-- Step 6: Update ETL.usp_ParseNewHits — Add Phase 8B for Tier 1
--         enrichment params from the Forge's _srv_* query string params
-- =====================================================================
-- The full proc is re-created via CREATE OR ALTER to incorporate the
-- 19 new Tier 1 enrichment fields. This is the authoritative copy
-- after migration 42.
-- =====================================================================

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
        -- PHASE 1: INSERT — Server + Screen + Locale (~31 UDF calls)
        -- =====================================================================
        INSERT INTO PiXL.Parsed (
            SourceId, CompanyID, PiXLID, IPAddress, ReceivedAt, RequestPath,
            ServerUserAgent, ServerReferer, IsSynthetic, HitType, Tier,
            ScreenWidth, ScreenHeight, ScreenAvailWidth, ScreenAvailHeight,
            ViewportWidth, ViewportHeight, OuterWidth, OuterHeight,
            ScreenX, ScreenY, ScreenExtended,
            ColorDepth, PixelRatio, ScreenOrientation,
            Timezone, TimezoneOffsetMins, ClientTimestampMs, TimezoneLocale,
            DateFormatSample, NumberFormatSample, RelativeTimeSample,
            Language, LanguageList)
        SELECT p.Id, p.CompanyID, p.PiXLID, p.IPAddress, p.ReceivedAt, p.RequestPath,
            p.UserAgent, p.Referer,
            CASE WHEN TRY_CAST(dbo.GetQueryParam(p.QueryString, 'synthetic') AS INT) = 1 THEN 1 ELSE 0 END,
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
            TRY_CAST(dbo.GetQueryParam(p.QueryString, 'screenExtended') AS BIT),
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
        -- PHASE 7: UPDATE — Behavioral + Cross-signal (~15 UDF calls)
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
            MousePath = dbo.GetQueryParam(src.QueryString, 'mousePath'),
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

        -- =====================================================================
        -- PHASE 8B: UPDATE — Forge Tier 1 enrichment params (~19 UDF calls)
        -- =====================================================================
        UPDATE pp SET
            -- Bot detection
            KnownBot          = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_knownBot') AS BIT),
            BotName            = dbo.GetQueryParam(src.QueryString, '_srv_botName'),
            -- UA parsing
            ParsedBrowser      = dbo.GetQueryParam(src.QueryString, '_srv_browser'),
            ParsedBrowserVersion = dbo.GetQueryParam(src.QueryString, '_srv_browserVer'),
            ParsedOS           = dbo.GetQueryParam(src.QueryString, '_srv_os'),
            ParsedOSVersion    = dbo.GetQueryParam(src.QueryString, '_srv_osVer'),
            ParsedDeviceType   = dbo.GetQueryParam(src.QueryString, '_srv_deviceType'),
            ParsedDeviceModel  = dbo.GetQueryParam(src.QueryString, '_srv_deviceModel'),
            ParsedDeviceBrand  = dbo.GetQueryParam(src.QueryString, '_srv_deviceBrand'),
            -- Reverse DNS
            ReverseDNS         = dbo.GetQueryParam(src.QueryString, '_srv_rdns'),
            ReverseDNSCloud    = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_rdnsCloud') AS BIT),
            -- MaxMind Geo
            MaxMindCountry     = dbo.GetQueryParam(src.QueryString, '_srv_mmCC'),
            MaxMindRegion      = dbo.GetQueryParam(src.QueryString, '_srv_mmReg'),
            MaxMindCity        = dbo.GetQueryParam(src.QueryString, '_srv_mmCity'),
            MaxMindLat         = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_mmLat') AS DECIMAL(9,6)),
            MaxMindLon         = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_mmLon') AS DECIMAL(9,6)),
            MaxMindASN         = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_mmASN') AS INT),
            MaxMindASNOrg      = dbo.GetQueryParam(src.QueryString, '_srv_mmASNOrg'),
            -- WHOIS
            WhoisASN           = dbo.GetQueryParam(src.QueryString, '_srv_whoisASN'),
            WhoisOrg           = dbo.GetQueryParam(src.QueryString, '_srv_whoisOrg')
        FROM PiXL.Parsed pp JOIN PiXL.Raw src ON pp.SourceId = src.Id
        WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;

        -- =============================================================================
        -- PHASE 9: Build #BatchRows with DeviceHash
        -- =============================================================================
        CREATE TABLE #BatchRows (
            SourceId        BIGINT          NOT NULL  PRIMARY KEY,
            CompanyID       INT             NULL,
            PiXLID          INT             NULL,
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

        DECLARE @DevicesUpserted INT = 0, @IPsUpserted INT = 0, @VisitsInserted INT = 0;

        -- =============================================================================
        -- PHASE 10: MERGE PiXL.Device
        -- =============================================================================
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

        UPDATE b SET b.DeviceId = d.DeviceId
        FROM #BatchRows b
        JOIN PiXL.Device d ON b.DeviceHash = d.DeviceHash
        WHERE b.DeviceHash IS NOT NULL;

        -- =============================================================================
        -- PHASE 11: MERGE PiXL.IP
        -- =============================================================================
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

        UPDATE b SET b.IpId = ip.IpId
        FROM #BatchRows b
        JOIN PiXL.IP ip ON b.IPAddress = ip.IPAddress
        WHERE b.IPAddress IS NOT NULL;

        -- =============================================================================
        -- PHASE 12: Extract _cp_* Client Parameters
        -- =============================================================================
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
        -- PHASE 13: INSERT PiXL.Visit
        -- =============================================================================
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
        IF OBJECT_ID('tempdb..#BatchRows') IS NOT NULL DROP TABLE #BatchRows;
        THROW;
    END CATCH;
END;
GO

PRINT '--- 42: Forge Tier 1 enrichment columns migration complete ---';
