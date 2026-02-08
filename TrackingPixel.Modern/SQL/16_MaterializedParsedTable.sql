-- ============================================================================
-- Migration 16: Materialized Parsed Table + Dashboard Infrastructure
-- ============================================================================
-- Replaces on-the-fly GetQueryParam() parsing with a pre-parsed table.
-- Parse once at insert time, query instantly forever after.
--
-- Architecture:
--   PiXL_Test (raw) → usp_ParseNewHits (ETL) → PiXL_Parsed (materialized)
--                                                     ↓
--                                              Dashboard Views
--
-- vw_PiXL_Complete remains as the "definition of record" but is no longer
-- queried by dashboards or the application. Everything reads PiXL_Parsed.
-- ============================================================================

USE SmartPixl;
GO

-- ============================================================================
-- 1. WATERMARK TABLE — tracks incremental ETL progress
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ETL_Watermark')
BEGIN
    CREATE TABLE dbo.ETL_Watermark (
        ProcessName     NVARCHAR(100) NOT NULL PRIMARY KEY,
        LastProcessedId INT           NOT NULL DEFAULT 0,
        LastRunAt       DATETIME2(7)  NULL,
        RowsProcessed   BIGINT        NOT NULL DEFAULT 0
    );
    INSERT INTO dbo.ETL_Watermark (ProcessName) VALUES ('ParseNewHits');
END;
GO

-- ============================================================================
-- 2. MATERIALIZED TABLE — PiXL_Parsed
-- ============================================================================
-- PK: SourceId (identity from PiXL_Test — guaranteed unique)
-- Clustered index: (ReceivedAt, SourceId) — time-series physical sort + uniqueness
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PiXL_Parsed')
BEGIN
    CREATE TABLE dbo.PiXL_Parsed (

        -- ====================================================================
        -- Identity & Server Context
        -- ====================================================================
        SourceId                    INT             NOT NULL,   -- PiXL_Test.Id
        CompanyID                   NVARCHAR(100)   NULL,
        PiXLID                      NVARCHAR(100)   NULL,
        IPAddress                   NVARCHAR(50)    NULL,
        ReceivedAt                  DATETIME2(7)    NOT NULL,
        RequestPath                 NVARCHAR(500)   NULL,
        ServerUserAgent             NVARCHAR(2000)  NULL,
        ServerReferer               NVARCHAR(2000)  NULL,
        IsSynthetic                 BIT             NOT NULL DEFAULT 0,
        Tier                        INT             NULL,

        -- ====================================================================
        -- Screen & Display
        -- ====================================================================
        ScreenWidth                 INT             NULL,
        ScreenHeight                INT             NULL,
        ScreenAvailWidth            INT             NULL,
        ScreenAvailHeight           INT             NULL,
        ViewportWidth               INT             NULL,
        ViewportHeight              INT             NULL,
        OuterWidth                  INT             NULL,
        OuterHeight                 INT             NULL,
        ScreenX                     INT             NULL,
        ScreenY                     INT             NULL,
        ColorDepth                  INT             NULL,
        PixelRatio                  DECIMAL(5,2)    NULL,
        ScreenOrientation           NVARCHAR(50)    NULL,

        -- ====================================================================
        -- Locale & Internationalization
        -- ====================================================================
        Timezone                    NVARCHAR(100)   NULL,
        TimezoneOffsetMins          INT             NULL,
        ClientTimestampMs           BIGINT          NULL,
        TimezoneLocale              NVARCHAR(200)   NULL,
        DateFormatSample            NVARCHAR(200)   NULL,
        NumberFormatSample          NVARCHAR(200)   NULL,
        RelativeTimeSample          NVARCHAR(200)   NULL,
        Language                    NVARCHAR(50)    NULL,
        LanguageList                NVARCHAR(500)   NULL,

        -- ====================================================================
        -- Browser & Navigator
        -- ====================================================================
        Platform                    NVARCHAR(100)   NULL,
        Vendor                      NVARCHAR(200)   NULL,
        ClientUserAgent             NVARCHAR(2000)  NULL,
        HardwareConcurrency         INT             NULL,
        DeviceMemoryGB              DECIMAL(5,2)    NULL,
        MaxTouchPoints              INT             NULL,
        NavigatorProduct            NVARCHAR(50)    NULL,
        NavigatorProductSub         NVARCHAR(50)    NULL,
        NavigatorVendorSub          NVARCHAR(200)   NULL,
        AppName                     NVARCHAR(100)   NULL,
        AppVersion                  NVARCHAR(500)   NULL,
        AppCodeName                 NVARCHAR(100)   NULL,

        -- ====================================================================
        -- GPU & WebGL
        -- ====================================================================
        GPURenderer                 NVARCHAR(500)   NULL,
        GPUVendor                   NVARCHAR(200)   NULL,
        WebGLParameters             NVARCHAR(2000)  NULL,
        WebGLExtensionCount         INT             NULL,
        WebGLSupported              BIT             NULL,
        WebGL2Supported             BIT             NULL,

        -- ====================================================================
        -- Fingerprint Signals
        -- ====================================================================
        CanvasFingerprint           NVARCHAR(200)   NULL,
        WebGLFingerprint            NVARCHAR(200)   NULL,
        AudioFingerprintSum         NVARCHAR(200)   NULL,
        AudioFingerprintHash        NVARCHAR(200)   NULL,
        MathFingerprint             NVARCHAR(200)   NULL,
        ErrorFingerprint            NVARCHAR(200)   NULL,
        CSSFontVariantHash          NVARCHAR(200)   NULL,
        DetectedFonts               NVARCHAR(4000)  NULL,

        -- ====================================================================
        -- Plugins & MIME
        -- ====================================================================
        PluginCount                 INT             NULL,
        PluginListDetailed          NVARCHAR(4000)  NULL,
        MimeTypeCount               INT             NULL,
        MimeTypeList                NVARCHAR(4000)  NULL,

        -- ====================================================================
        -- Speech & Input Devices
        -- ====================================================================
        SpeechVoices                NVARCHAR(4000)  NULL,
        ConnectedGamepads           NVARCHAR(1000)  NULL,

        -- ====================================================================
        -- Network & Connection
        -- ====================================================================
        WebRTCLocalIP               NVARCHAR(50)    NULL,
        ConnectionType              NVARCHAR(50)    NULL,
        DownlinkMbps                DECIMAL(10,2)   NULL,
        DownlinkMax                 NVARCHAR(50)    NULL,
        RTTMs                       INT             NULL,
        DataSaverEnabled            BIT             NULL,
        NetworkType                 NVARCHAR(50)    NULL,
        IsOnline                    BIT             NULL,

        -- ====================================================================
        -- Storage
        -- ====================================================================
        StorageQuotaGB              INT             NULL,
        StorageUsedMB               INT             NULL,

        -- ====================================================================
        -- API Capabilities
        -- ====================================================================
        LocalStorageSupported       BIT             NULL,
        SessionStorageSupported     BIT             NULL,
        IndexedDBSupported          BIT             NULL,
        CacheAPISupported           BIT             NULL,
        BatteryLevelPct             INT             NULL,
        BatteryCharging             BIT             NULL,
        AudioInputDevices           INT             NULL,
        VideoInputDevices           INT             NULL,
        CookiesEnabled              BIT             NULL,
        DoNotTrack                  NVARCHAR(50)    NULL,
        PDFViewerEnabled            BIT             NULL,
        WebDriverDetected           BIT             NULL,
        JavaEnabled                 BIT             NULL,
        CanvasSupported             BIT             NULL,
        WebAssemblySupported        BIT             NULL,
        WebWorkersSupported         BIT             NULL,
        ServiceWorkerSupported      BIT             NULL,
        MediaDevicesAPISupported    BIT             NULL,
        ClipboardAPISupported       BIT             NULL,
        SpeechSynthesisSupported    BIT             NULL,
        TouchEventsSupported        BIT             NULL,
        PointerEventsSupported      BIT             NULL,

        -- ====================================================================
        -- Accessibility & Preferences
        -- ====================================================================
        HoverCapable                BIT             NULL,
        PointerType                 NVARCHAR(20)    NULL,
        PrefersColorSchemeDark      BIT             NULL,
        PrefersColorSchemeLight     BIT             NULL,
        PrefersReducedMotion        BIT             NULL,
        PrefersReducedData          BIT             NULL,
        PrefersHighContrast         BIT             NULL,
        ForcedColorsActive          BIT             NULL,
        InvertedColorsActive        BIT             NULL,
        StandaloneDisplayMode       BIT             NULL,

        -- ====================================================================
        -- Document State
        -- ====================================================================
        DocumentCharset             NVARCHAR(50)    NULL,
        DocumentCompatMode          NVARCHAR(50)    NULL,
        DocumentReadyState          NVARCHAR(50)    NULL,
        DocumentHidden              BIT             NULL,
        DocumentVisibility          NVARCHAR(50)    NULL,

        -- ====================================================================
        -- Page Context
        -- ====================================================================
        PageURL                     NVARCHAR(2000)  NULL,
        PageReferrer                NVARCHAR(2000)  NULL,
        PageTitle                   NVARCHAR(1000)  NULL,
        PageDomain                  NVARCHAR(500)   NULL,
        PagePath                    NVARCHAR(1000)  NULL,
        PageHash                    NVARCHAR(500)   NULL,
        PageProtocol                NVARCHAR(20)    NULL,
        HistoryLength               INT             NULL,

        -- ====================================================================
        -- Performance Timing
        -- ====================================================================
        PageLoadTimeMs              INT             NULL,
        DOMReadyTimeMs              INT             NULL,
        DNSLookupMs                 INT             NULL,
        TCPConnectMs                INT             NULL,
        TimeToFirstByteMs           INT             NULL,

        -- ====================================================================
        -- Bot Detection
        -- ====================================================================
        BotSignalsList              NVARCHAR(4000)  NULL,
        BotScore                    INT             NULL,
        CombinedThreatScore         INT             NULL,
        ScriptExecutionTimeMs       INT             NULL,
        BotPermissionInconsistent   BIT             NULL,

        -- ====================================================================
        -- Evasion Detection
        -- ====================================================================
        CanvasEvasionDetected       BIT             NULL,
        WebGLEvasionDetected        BIT             NULL,
        EvasionToolsDetected        NVARCHAR(1000)  NULL,
        ProxyBlockedProperties      NVARCHAR(1000)  NULL,

        -- ====================================================================
        -- Client Hints
        -- ====================================================================
        UA_Architecture             NVARCHAR(50)    NULL,
        UA_Bitness                  NVARCHAR(10)    NULL,
        UA_Model                    NVARCHAR(200)   NULL,
        UA_PlatformVersion          NVARCHAR(100)   NULL,
        UA_FullVersionList          NVARCHAR(500)   NULL,
        UA_IsWow64                  BIT             NULL,
        UA_IsMobile                 BIT             NULL,
        UA_Platform                 NVARCHAR(50)    NULL,
        UA_Brands                   NVARCHAR(500)   NULL,
        UA_FormFactor               NVARCHAR(100)   NULL,

        -- ====================================================================
        -- Browser-Specific
        -- ====================================================================
        Firefox_OSCPU               NVARCHAR(200)   NULL,
        Firefox_BuildID             NVARCHAR(100)   NULL,
        Chrome_ObjectPresent        BIT             NULL,
        Chrome_RuntimePresent       BIT             NULL,
        Chrome_JSHeapSizeLimit      BIGINT          NULL,
        Chrome_TotalJSHeapSize      BIGINT          NULL,
        Chrome_UsedJSHeapSize       BIGINT          NULL,

        -- ====================================================================
        -- Advanced Fingerprint Stability
        -- ====================================================================
        CanvasConsistency           NVARCHAR(50)    NULL,
        AudioIsStable               BIT             NULL,
        AudioNoiseInjectionDetected BIT             NULL,

        -- ====================================================================
        -- Behavioral Biometrics
        -- ====================================================================
        MouseMoveCount              INT             NULL,
        UserScrolled                BIT             NULL,
        ScrollDepthPx               INT             NULL,
        MouseEntropy                INT             NULL,
        ScrollContradiction         BIT             NULL,
        MoveTimingCV                INT             NULL,
        MoveSpeedCV                 INT             NULL,
        MoveCountBucket             NVARCHAR(20)    NULL,
        BehavioralFlags             NVARCHAR(200)   NULL,

        -- ====================================================================
        -- Cross-Signal & Stealth
        -- ====================================================================
        StealthPluginSignals        NVARCHAR(500)   NULL,
        FontMethodMismatch          BIT             NULL,
        EvasionSignalsV2            NVARCHAR(500)   NULL,
        CrossSignalFlags            NVARCHAR(500)   NULL,
        AnomalyScore                INT             NULL,

        -- ====================================================================
        -- Parsed timestamp (when ETL ran)
        -- ====================================================================
        ParsedAt                    DATETIME2(7)    NOT NULL DEFAULT SYSUTCDATETIME(),

        -- ====================================================================
        -- Constraints
        -- ====================================================================
        CONSTRAINT PK_PiXL_Parsed PRIMARY KEY NONCLUSTERED (SourceId),
        CONSTRAINT FK_PiXL_Parsed_Source FOREIGN KEY (SourceId) 
            REFERENCES dbo.PiXL_Test(Id)
    );

    -- Clustered index: time-series physical sort with uniqueness tiebreaker
    CREATE UNIQUE CLUSTERED INDEX CIX_PiXL_Parsed_ReceivedAt 
        ON dbo.PiXL_Parsed (ReceivedAt, SourceId);

    PRINT 'Created PiXL_Parsed table with clustered index on (ReceivedAt, SourceId)';
END;
GO

-- ============================================================================
-- 3. INDEXES — optimized for dashboard query patterns
-- ============================================================================

-- Bot score queries: "show me all bots", "bot score distribution"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Parsed_BotScore' AND object_id = OBJECT_ID('PiXL_Parsed'))
    CREATE NONCLUSTERED INDEX IX_Parsed_BotScore
        ON dbo.PiXL_Parsed (BotScore)
        INCLUDE (BotSignalsList, Platform, CompanyID, ReceivedAt, CombinedThreatScore);

-- Client filtering: "show me Company X's data"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Parsed_Company' AND object_id = OBJECT_ID('PiXL_Parsed'))
    CREATE NONCLUSTERED INDEX IX_Parsed_Company
        ON dbo.PiXL_Parsed (CompanyID, PiXLID, ReceivedAt DESC)
        INCLUDE (BotScore, IPAddress, CanvasFingerprint);

-- Synthetic filter: "exclude test traffic" (most dashboard queries add this)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Parsed_Synthetic' AND object_id = OBJECT_ID('PiXL_Parsed'))
    CREATE NONCLUSTERED INDEX IX_Parsed_Synthetic
        ON dbo.PiXL_Parsed (IsSynthetic, ReceivedAt DESC)
        INCLUDE (BotScore, CanvasFingerprint, Platform, CompanyID);

-- Fingerprint lookups: "unique visitors", "fingerprint history"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Parsed_CanvasFP' AND object_id = OBJECT_ID('PiXL_Parsed'))
    CREATE NONCLUSTERED INDEX IX_Parsed_CanvasFP
        ON dbo.PiXL_Parsed (CanvasFingerprint)
        INCLUDE (ReceivedAt, IPAddress, Platform, BotScore)
        WHERE CanvasFingerprint IS NOT NULL;

-- IP lookups: "all hits from this IP"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Parsed_IP' AND object_id = OBJECT_ID('PiXL_Parsed'))
    CREATE NONCLUSTERED INDEX IX_Parsed_IP
        ON dbo.PiXL_Parsed (IPAddress, ReceivedAt DESC)
        INCLUDE (CanvasFingerprint, BotScore, CompanyID);

PRINT 'Created indexes on PiXL_Parsed';
GO

-- ============================================================================
-- 4. STORED PROCEDURE — Incremental ETL (7-Phase)
-- ============================================================================
-- Parses new PiXL_Test rows that haven't been processed yet.
-- Split into 7 phases to avoid SQL Server's expression services limit
-- (~30 GetQueryParam UDF calls per statement is the safe threshold).
-- All phases wrapped in a single transaction for atomicity.
-- Safe to run repeatedly — idempotent via watermark tracking.
-- Designed to be called every 30-60 seconds from SQL Agent or .NET.
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

PRINT 'Created usp_ParseNewHits stored procedure';
GO

-- ============================================================================
-- 5. BACKFILL — Parse all existing rows
-- ============================================================================
DECLARE @Remaining INT = 1;
WHILE @Remaining > 0
BEGIN
    EXEC dbo.usp_ParseNewHits @BatchSize = 50000;
    SELECT @Remaining = COUNT(*) 
    FROM dbo.PiXL_Test t
    LEFT JOIN dbo.PiXL_Parsed pp ON t.Id = pp.SourceId
    WHERE pp.SourceId IS NULL;
END;

PRINT 'Backfill complete';
GO

-- ============================================================================
-- 6. DASHBOARD VIEWS
-- ============================================================================

-- ============================================================================
-- 6a. System Health — Single-row overview for top-level dashboard cards
-- ============================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_SystemHealth AS
WITH TimeWindows AS (
    SELECT
        COUNT(*)                                                    AS TotalHits,
        SUM(CASE WHEN ReceivedAt >= DATEADD(HOUR, -1, SYSUTCDATETIME()) THEN 1 ELSE 0 END)  AS Hits_1h,
        SUM(CASE WHEN ReceivedAt >= DATEADD(HOUR, -24, SYSUTCDATETIME()) THEN 1 ELSE 0 END) AS Hits_24h,
        SUM(CASE WHEN ReceivedAt >= DATEADD(DAY, -7, SYSUTCDATETIME()) THEN 1 ELSE 0 END)   AS Hits_7d,
        MAX(ReceivedAt)                                             AS LastHitAt,
        -- Bot counts (BotScore >= 50 = suspicious+)
        SUM(CASE WHEN BotScore >= 50 AND ReceivedAt >= DATEADD(HOUR, -24, SYSUTCDATETIME()) THEN 1 ELSE 0 END) AS Bots_24h,
        SUM(CASE WHEN BotScore >= 80 AND ReceivedAt >= DATEADD(HOUR, -24, SYSUTCDATETIME()) THEN 1 ELSE 0 END) AS HighRiskBots_24h,
        SUM(CASE WHEN BotScore >= 50 THEN 1 ELSE 0 END)            AS Bots_AllTime,
        -- Averages
        AVG(CAST(BotScore AS FLOAT))                                AS AvgBotScore,
        AVG(CASE WHEN ReceivedAt >= DATEADD(HOUR, -24, SYSUTCDATETIME()) THEN CAST(BotScore AS FLOAT) END) AS AvgBotScore_24h,
        -- Unique fingerprints
        COUNT(DISTINCT CanvasFingerprint)                           AS UniqueFP_AllTime,
        COUNT(DISTINCT CASE WHEN ReceivedAt >= DATEADD(HOUR, -24, SYSUTCDATETIME()) THEN CanvasFingerprint END) AS UniqueFP_24h,
        -- Unique IPs
        COUNT(DISTINCT IPAddress)                                   AS UniqueIPs_AllTime,
        COUNT(DISTINCT CASE WHEN ReceivedAt >= DATEADD(HOUR, -24, SYSUTCDATETIME()) THEN IPAddress END) AS UniqueIPs_24h,
        -- Evasion counts
        SUM(CASE WHEN CanvasEvasionDetected = 1 OR WebGLEvasionDetected = 1 
            OR EvasionToolsDetected IS NOT NULL THEN 1 ELSE 0 END) AS EvasionDetected_AllTime,
        -- WebDriver (obvious automation)
        SUM(CASE WHEN WebDriverDetected = 1 THEN 1 ELSE 0 END)     AS WebDriverHits,
        -- Synthetic test traffic
        SUM(CASE WHEN IsSynthetic = 1 THEN 1 ELSE 0 END)           AS SyntheticHits
    FROM dbo.PiXL_Parsed
)
SELECT
    TotalHits,
    Hits_1h,
    Hits_24h,
    Hits_7d,
    LastHitAt,
    DATEDIFF(SECOND, LastHitAt, SYSUTCDATETIME())                  AS SecondsSinceLastHit,
    Bots_24h,
    HighRiskBots_24h,
    Bots_AllTime,
    CASE WHEN Hits_24h > 0 
        THEN CAST(ROUND(100.0 * Bots_24h / Hits_24h, 1) AS DECIMAL(5,1))
        ELSE 0 
    END                                                             AS BotPct_24h,
    CAST(ROUND(ISNULL(AvgBotScore, 0), 1) AS DECIMAL(5,1))        AS AvgBotScore,
    CAST(ROUND(ISNULL(AvgBotScore_24h, 0), 1) AS DECIMAL(5,1))    AS AvgBotScore_24h,
    UniqueFP_AllTime,
    UniqueFP_24h,
    UniqueIPs_AllTime,
    UniqueIPs_24h,
    EvasionDetected_AllTime,
    WebDriverHits,
    SyntheticHits,
    -- ETL health
    w.LastRunAt                                                     AS ETL_LastRunAt,
    w.RowsProcessed                                                 AS ETL_TotalProcessed,
    w.LastProcessedId                                               AS ETL_Watermark
FROM TimeWindows
CROSS JOIN dbo.ETL_Watermark w
WHERE w.ProcessName = 'ParseNewHits';
GO

PRINT 'Created vw_Dash_SystemHealth';
GO

-- ============================================================================
-- 6b. Hourly Rollup — Time-series data for charts
-- ============================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_HourlyRollup AS
SELECT
    DATEADD(HOUR, DATEDIFF(HOUR, 0, ReceivedAt), 0)        AS HourBucket,
    COUNT(*)                                                 AS TotalHits,
    SUM(CASE WHEN BotScore >= 50 THEN 1 ELSE 0 END)        AS BotHits,
    SUM(CASE WHEN BotScore >= 80 THEN 1 ELSE 0 END)        AS HighRiskHits,
    SUM(CASE WHEN BotScore < 20 OR BotScore IS NULL THEN 1 ELSE 0 END) AS LikelyHumanHits,
    COUNT(DISTINCT CanvasFingerprint)                        AS UniqueFingerprints,
    COUNT(DISTINCT IPAddress)                                AS UniqueIPs,
    AVG(CAST(BotScore AS FLOAT))                            AS AvgBotScore,
    AVG(CAST(CombinedThreatScore AS FLOAT))                 AS AvgThreatScore,
    MAX(BotScore)                                            AS MaxBotScore,
    SUM(CASE WHEN WebDriverDetected = 1 THEN 1 ELSE 0 END) AS WebDriverHits,
    SUM(CASE WHEN CanvasEvasionDetected = 1 THEN 1 ELSE 0 END) AS CanvasEvasionHits,
    SUM(CASE WHEN EvasionToolsDetected IS NOT NULL THEN 1 ELSE 0 END) AS EvasionToolHits,
    SUM(CASE WHEN IsSynthetic = 1 THEN 1 ELSE 0 END)       AS SyntheticHits
FROM dbo.PiXL_Parsed
GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, ReceivedAt), 0);
GO

PRINT 'Created vw_Dash_HourlyRollup';
GO

-- ============================================================================
-- 6c. Bot Breakdown — Click "20 Bots" to drill into this
-- ============================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_BotBreakdown AS
-- NOTE: SQL Server 2019 does not support STRING_AGG(DISTINCT ...)
-- so we use CTEs to get distinct platforms per bucket
WITH Bucketed AS (
    SELECT
        CASE
            WHEN BotScore >= 80 THEN 'High Risk'
            WHEN BotScore >= 50 THEN 'Medium Risk'
            WHEN BotScore >= 20 THEN 'Low Risk'
            ELSE 'Likely Human'
        END AS RiskBucket,
        CASE
            WHEN BotScore >= 80 THEN 1
            WHEN BotScore >= 50 THEN 2
            WHEN BotScore >= 20 THEN 3
            ELSE 4
        END AS SortOrder,
        BotScore, CombinedThreatScore, AnomalyScore,
        CanvasFingerprint, IPAddress, ReceivedAt, Platform
    FROM dbo.PiXL_Parsed
    WHERE IsSynthetic = 0
),
BucketAgg AS (
    SELECT
        RiskBucket, SortOrder,
        COUNT(*) AS HitCount,
        COUNT(DISTINCT CanvasFingerprint) AS UniqueDevices,
        COUNT(DISTINCT IPAddress) AS UniqueIPs,
        AVG(CAST(BotScore AS FLOAT)) AS AvgBotScore,
        AVG(CAST(CombinedThreatScore AS FLOAT)) AS AvgThreatScore,
        AVG(CAST(AnomalyScore AS FLOAT)) AS AvgAnomalyScore,
        MIN(ReceivedAt) AS FirstSeen,
        MAX(ReceivedAt) AS LastSeen
    FROM Bucketed
    GROUP BY RiskBucket, SortOrder
),
BucketPlatforms AS (
    SELECT DISTINCT RiskBucket, Platform
    FROM Bucketed
    WHERE Platform IS NOT NULL
)
SELECT
    a.RiskBucket, a.SortOrder, a.HitCount, a.UniqueDevices, a.UniqueIPs,
    a.AvgBotScore, a.AvgThreatScore, a.AvgAnomalyScore,
    a.FirstSeen, a.LastSeen,
    (SELECT STRING_AGG(bp.Platform, ', ') FROM BucketPlatforms bp WHERE bp.RiskBucket = a.RiskBucket) AS Platforms
FROM BucketAgg a;
GO

PRINT 'Created vw_Dash_BotBreakdown';
GO

-- ============================================================================
-- 6d. Top Bot Signals — What's actually triggering bot detections?
-- ============================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_TopBotSignals AS
SELECT
    s.value                                     AS Signal,
    COUNT(*)                                    AS TimesTriggered,
    COUNT(DISTINCT pp.CanvasFingerprint)        AS UniqueDevices,
    AVG(CAST(pp.BotScore AS FLOAT))            AS AvgBotScoreWhenPresent,
    MIN(pp.ReceivedAt)                          AS FirstSeen,
    MAX(pp.ReceivedAt)                          AS LastSeen
FROM dbo.PiXL_Parsed pp
CROSS APPLY STRING_SPLIT(pp.BotSignalsList, ',') s
WHERE pp.BotSignalsList IS NOT NULL
  AND pp.IsSynthetic = 0
  AND s.value != ''
GROUP BY s.value;
GO

PRINT 'Created vw_Dash_TopBotSignals';
GO

-- ============================================================================
-- 6e. Device Breakdown — Platform/browser demographics
-- ============================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_DeviceBreakdown AS
SELECT
    ISNULL(Platform, 'Unknown')                              AS Platform,
    -- Derive browser from UA
    CASE
        WHEN ClientUserAgent LIKE '%Edg/%'    THEN 'Edge'
        WHEN ClientUserAgent LIKE '%Chrome/%' THEN 'Chrome'
        WHEN ClientUserAgent LIKE '%Firefox/%' THEN 'Firefox'
        WHEN ClientUserAgent LIKE '%Safari/%' 
         AND ClientUserAgent NOT LIKE '%Chrome/%' THEN 'Safari'
        WHEN ClientUserAgent LIKE '%Opera%' 
          OR ClientUserAgent LIKE '%OPR/%'    THEN 'Opera'
        ELSE 'Other'
    END                                                      AS Browser,
    -- Screen resolution bucket
    CASE
        WHEN ScreenWidth >= 3840 THEN '4K+'
        WHEN ScreenWidth >= 2560 THEN '1440p'
        WHEN ScreenWidth >= 1920 THEN '1080p'
        WHEN ScreenWidth >= 1366 THEN 'Laptop'
        WHEN ScreenWidth >= 768  THEN 'Tablet'
        WHEN ScreenWidth > 0     THEN 'Mobile'
        ELSE 'Unknown'
    END                                                      AS ScreenBucket,
    CASE WHEN MaxTouchPoints > 0 THEN 'Touch' ELSE 'No Touch' END AS TouchCapability,
    COUNT(*)                                                 AS HitCount,
    COUNT(DISTINCT CanvasFingerprint)                        AS UniqueDevices,
    COUNT(DISTINCT IPAddress)                                AS UniqueIPs,
    AVG(CAST(BotScore AS FLOAT))                            AS AvgBotScore
FROM dbo.PiXL_Parsed
WHERE IsSynthetic = 0
GROUP BY
    ISNULL(Platform, 'Unknown'),
    CASE
        WHEN ClientUserAgent LIKE '%Edg/%'    THEN 'Edge'
        WHEN ClientUserAgent LIKE '%Chrome/%' THEN 'Chrome'
        WHEN ClientUserAgent LIKE '%Firefox/%' THEN 'Firefox'
        WHEN ClientUserAgent LIKE '%Safari/%' 
         AND ClientUserAgent NOT LIKE '%Chrome/%' THEN 'Safari'
        WHEN ClientUserAgent LIKE '%Opera%' 
          OR ClientUserAgent LIKE '%OPR/%'    THEN 'Opera'
        ELSE 'Other'
    END,
    CASE
        WHEN ScreenWidth >= 3840 THEN '4K+'
        WHEN ScreenWidth >= 2560 THEN '1440p'
        WHEN ScreenWidth >= 1920 THEN '1080p'
        WHEN ScreenWidth >= 1366 THEN 'Laptop'
        WHEN ScreenWidth >= 768  THEN 'Tablet'
        WHEN ScreenWidth > 0     THEN 'Mobile'
        ELSE 'Unknown'
    END,
    CASE WHEN MaxTouchPoints > 0 THEN 'Touch' ELSE 'No Touch' END;
GO

PRINT 'Created vw_Dash_DeviceBreakdown';
GO

-- ============================================================================
-- 6f. Evasion Summary — Privacy tools and countermeasure tracking
-- ============================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_EvasionSummary AS
SELECT
    COUNT(*)                                                                AS TotalHits,
    SUM(CASE WHEN CanvasEvasionDetected = 1 THEN 1 ELSE 0 END)           AS CanvasEvasion,
    SUM(CASE WHEN WebGLEvasionDetected = 1 THEN 1 ELSE 0 END)            AS WebGLEvasion,
    SUM(CASE WHEN AudioNoiseInjectionDetected = 1 THEN 1 ELSE 0 END)     AS AudioNoise,
    SUM(CASE WHEN FontMethodMismatch = 1 THEN 1 ELSE 0 END)              AS FontSpoof,
    SUM(CASE WHEN ProxyBlockedProperties IS NOT NULL 
         AND ProxyBlockedProperties != '' THEN 1 ELSE 0 END)             AS ProxyBlocked,
    SUM(CASE WHEN StealthPluginSignals IS NOT NULL 
         AND StealthPluginSignals != '' THEN 1 ELSE 0 END)               AS StealthDetected,
    SUM(CASE WHEN EvasionToolsDetected IS NOT NULL 
         AND EvasionToolsDetected != '' THEN 1 ELSE 0 END)               AS EvasionToolsFound,
    SUM(CASE WHEN EvasionSignalsV2 IS NOT NULL 
         AND EvasionSignalsV2 != '' THEN 1 ELSE 0 END)                   AS EvasionV2Signals,
    SUM(CASE WHEN DoNotTrack = '1' THEN 1 ELSE 0 END)                    AS DNT_Enabled,
    -- Any evasion at all
    SUM(CASE WHEN CanvasEvasionDetected = 1 
              OR WebGLEvasionDetected = 1 
              OR AudioNoiseInjectionDetected = 1 
              OR FontMethodMismatch = 1 
              OR ProxyBlockedProperties IS NOT NULL 
              OR StealthPluginSignals IS NOT NULL 
              OR EvasionToolsDetected IS NOT NULL
         THEN 1 ELSE 0 END)                                              AS AnyEvasionDetected,
    -- Percentages 
    CASE WHEN COUNT(*) > 0 THEN
        CAST(ROUND(100.0 * SUM(CASE WHEN CanvasEvasionDetected = 1 
             OR WebGLEvasionDetected = 1 OR EvasionToolsDetected IS NOT NULL 
             THEN 1 ELSE 0 END) / COUNT(*), 1) AS DECIMAL(5,1))
    ELSE 0 END                                                            AS EvasionPct
FROM dbo.PiXL_Parsed
WHERE IsSynthetic = 0;
GO

PRINT 'Created vw_Dash_EvasionSummary';
GO

-- ============================================================================
-- 6g. Behavioral Analysis — Mouse/scroll patterns for bot vs human
-- ============================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_BehavioralAnalysis AS
SELECT
    CASE
        WHEN BotScore >= 50 THEN 'Bot (50+)'
        ELSE 'Human (<50)'
    END                                                      AS Classification,
    COUNT(*)                                                 AS HitCount,
    -- Mouse behavior
    AVG(CAST(MouseMoveCount AS FLOAT))                      AS AvgMouseMoves,
    AVG(CAST(MouseEntropy AS FLOAT))                        AS AvgMouseEntropy,
    AVG(CAST(MoveTimingCV AS FLOAT))                        AS AvgTimingCV,
    AVG(CAST(MoveSpeedCV AS FLOAT))                         AS AvgSpeedCV,
    SUM(CASE WHEN MouseMoveCount = 0 THEN 1 ELSE 0 END)    AS NoMouseHits,
    SUM(CASE WHEN MouseMoveCount > 0 AND MouseEntropy = 0 THEN 1 ELSE 0 END) AS ZeroEntropyHits,
    -- Scroll behavior
    SUM(CASE WHEN UserScrolled = 1 THEN 1 ELSE 0 END)      AS ScrolledHits,
    SUM(CASE WHEN ScrollContradiction = 1 THEN 1 ELSE 0 END) AS ScrollContradictions,
    AVG(CAST(ScrollDepthPx AS FLOAT))                       AS AvgScrollDepth,
    -- Behavioral flags
    SUM(CASE WHEN BehavioralFlags IS NOT NULL 
         AND BehavioralFlags != '' THEN 1 ELSE 0 END)       AS FlaggedHits
FROM dbo.PiXL_Parsed
WHERE IsSynthetic = 0
GROUP BY
    CASE
        WHEN BotScore >= 50 THEN 'Bot (50+)'
        ELSE 'Human (<50)'
    END;
GO

PRINT 'Created vw_Dash_BehavioralAnalysis';
GO

-- ============================================================================
-- 6h. Recent Hits — Latest N hits for live-feed display
-- ============================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_RecentHits AS
SELECT TOP 100
    SourceId,
    ReceivedAt,
    CompanyID,
    IPAddress,
    CASE
        WHEN BotScore >= 80 THEN 'HIGH'
        WHEN BotScore >= 50 THEN 'MED'
        WHEN BotScore >= 20 THEN 'LOW'
        ELSE 'OK'
    END                                                      AS ThreatLevel,
    BotScore,
    CombinedThreatScore,
    AnomalyScore,
    Platform,
    CASE
        WHEN ClientUserAgent LIKE '%Edg/%'    THEN 'Edge'
        WHEN ClientUserAgent LIKE '%Chrome/%' THEN 'Chrome'
        WHEN ClientUserAgent LIKE '%Firefox/%' THEN 'Firefox'
        WHEN ClientUserAgent LIKE '%Safari/%' 
         AND ClientUserAgent NOT LIKE '%Chrome/%' THEN 'Safari'
        ELSE 'Other'
    END                                                      AS Browser,
    CAST(ScreenWidth AS VARCHAR(10)) + 'x' 
        + CAST(ScreenHeight AS VARCHAR(10))                  AS Resolution,
    LEFT(CanvasFingerprint, 8)                               AS FP_Short,
    BotSignalsList,
    EvasionToolsDetected,
    IsSynthetic,
    MouseMoveCount,
    MouseEntropy
FROM dbo.PiXL_Parsed
ORDER BY ReceivedAt DESC;
GO

PRINT 'Created vw_Dash_RecentHits';
GO

-- ============================================================================
-- 6i. Fingerprint Clusters — Group hits by device fingerprint  
-- ============================================================================
CREATE OR ALTER VIEW dbo.vw_Dash_FingerprintClusters AS
SELECT
    CanvasFingerprint,
    COUNT(*)                                    AS HitCount,
    COUNT(DISTINCT IPAddress)                   AS UniqueIPs,
    COUNT(DISTINCT PageDomain)                  AS UniqueDomains,
    MIN(ReceivedAt)                             AS FirstSeen,
    MAX(ReceivedAt)                             AS LastSeen,
    DATEDIFF(MINUTE, MIN(ReceivedAt), MAX(ReceivedAt)) AS ActiveMinutes,
    AVG(CAST(BotScore AS FLOAT))               AS AvgBotScore,
    MAX(BotScore)                               AS MaxBotScore,
    -- Platform consistency (should be 1 for real devices)
    COUNT(DISTINCT Platform)                    AS PlatformCount,
    MIN(Platform)                               AS PrimaryPlatform,
    -- Screen consistency  
    COUNT(DISTINCT CAST(ScreenWidth AS VARCHAR(10)) + 'x' + CAST(ScreenHeight AS VARCHAR(10))) AS ScreenResolutions
FROM dbo.PiXL_Parsed
WHERE CanvasFingerprint IS NOT NULL
  AND IsSynthetic = 0
GROUP BY CanvasFingerprint;
GO

PRINT 'Created vw_Dash_FingerprintClusters';
GO

-- ============================================================================
-- VERIFICATION
-- ============================================================================
SELECT 'PiXL_Parsed' AS [Object], COUNT(*) AS RowCount FROM dbo.PiXL_Parsed
UNION ALL
SELECT 'PiXL_Test', COUNT(*) FROM dbo.PiXL_Test
UNION ALL
SELECT 'Watermark', LastProcessedId FROM dbo.ETL_Watermark WHERE ProcessName = 'ParseNewHits';
GO

SELECT name AS DashboardView
FROM sys.views 
WHERE name LIKE 'vw_Dash_%' 
ORDER BY name;
GO

PRINT '=== Migration 16 complete ===';
GO
