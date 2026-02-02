-- =============================================
-- SmartPiXL Database Schema - STREAMLINED Version
-- 
-- This schema stores raw data for maximum insert performance.
-- The view vw_PiXL_Parsed extracts all 90+ data points at query time.
-- A materialization job can copy to a permanent typed table for indexing.
--
-- Run this AFTER 02_ExpandedSchema.sql (or on a fresh database)
-- =============================================

USE SmartPixl;
GO

-- =============================================
-- Ensure base table has the columns we need
-- The C# app writes: CompanyID, PiXLID, IPAddress, RequestPath, QueryString, HeadersJson, UserAgent, Referer, ReceivedAt
-- =============================================

-- Add HeadersJson if it doesn't exist (new column for JSON headers)
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'HeadersJson')
    ALTER TABLE PiXL_Test ADD HeadersJson NVARCHAR(MAX) NULL;
GO

-- Ensure QueryString can hold long query strings (all 90+ params)
-- Check current size and expand if needed
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'QueryString' AND max_length < 8000)
BEGIN
    ALTER TABLE PiXL_Test ALTER COLUMN QueryString NVARCHAR(MAX) NULL;
END
GO

-- Ensure UserAgent and Referer exist with adequate size
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'UserAgent')
    ALTER TABLE PiXL_Test ADD UserAgent NVARCHAR(2000) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'Referer')
    ALTER TABLE PiXL_Test ADD Referer NVARCHAR(2000) NULL;
GO

-- =============================================
-- Create the GetQueryParam function if it doesn't exist
-- This is used by the view to parse query string parameters
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('dbo.GetQueryParam') AND type = 'FN')
BEGIN
    EXEC('
    CREATE FUNCTION dbo.GetQueryParam(@QueryString NVARCHAR(MAX), @ParamName NVARCHAR(100))
    RETURNS NVARCHAR(4000)
    AS
    BEGIN
        DECLARE @Start INT, @End INT, @Value NVARCHAR(4000);
        
        -- Look for param=value or &param=value
        SET @Start = CHARINDEX(@ParamName + ''='', @QueryString);
        
        -- If not found at start, look for &param=
        IF @Start = 0 OR (@Start > 1 AND SUBSTRING(@QueryString, @Start - 1, 1) NOT IN (''&'', ''?''))
            SET @Start = CHARINDEX(''&'' + @ParamName + ''='', @QueryString);
        
        IF @Start = 0
            RETURN NULL;
        
        -- Move past the param= part
        SET @Start = @Start + LEN(@ParamName) + 1;
        IF SUBSTRING(@QueryString, @Start - LEN(@ParamName) - 1, 1) = ''&''
            SET @Start = @Start + 1;
        
        -- Find end (next & or end of string)
        SET @End = CHARINDEX(''&'', @QueryString, @Start);
        IF @End = 0
            SET @End = LEN(@QueryString) + 1;
        
        SET @Value = SUBSTRING(@QueryString, @Start, @End - @Start);
        
        -- URL decode common patterns
        SET @Value = REPLACE(@Value, ''%20'', '' '');
        SET @Value = REPLACE(@Value, ''%2F'', ''/'');
        SET @Value = REPLACE(@Value, ''%3A'', '':'');
        SET @Value = REPLACE(@Value, ''%2C'', '','');
        SET @Value = REPLACE(@Value, ''%3D'', ''='');
        SET @Value = REPLACE(@Value, ''%26'', ''&'');
        SET @Value = REPLACE(@Value, ''%3F'', ''?'');
        SET @Value = REPLACE(@Value, ''%23'', ''#'');
        SET @Value = REPLACE(@Value, ''%25'', ''%'');
        SET @Value = REPLACE(@Value, ''+'', '' '');
        
        RETURN @Value;
    END
    ');
END
GO

-- =============================================
-- Drop and recreate the parsed view
-- This extracts all 90+ parameters from QueryString at query time
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
    -- BOT DETECTION (added 2026-02-02)
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
    -- HIGH-ENTROPY CLIENT HINTS (added 2026-02-02)
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'uaArch') AS UA_Architecture,          -- x86, arm, etc.
    dbo.GetQueryParam(p.QueryString, 'uaBitness') AS UA_Bitness,            -- 32 or 64
    dbo.GetQueryParam(p.QueryString, 'uaModel') AS UA_Model,                -- Device model (mobile)
    dbo.GetQueryParam(p.QueryString, 'uaPlatformVersion') AS UA_PlatformVersion, -- Full OS version
    dbo.GetQueryParam(p.QueryString, 'uaFullVersion') AS UA_FullVersionList,-- Browser version details
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'uaWow64') AS BIT) AS UA_Wow64,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'uaMobile') AS BIT) AS UA_Mobile,
    dbo.GetQueryParam(p.QueryString, 'uaPlatform') AS UA_Platform,
    dbo.GetQueryParam(p.QueryString, 'uaBrands') AS UA_Brands,
    
    -- ============================================
    -- FIREFOX-SPECIFIC (added 2026-02-02)
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'oscpu') AS Firefox_OSCPU,
    dbo.GetQueryParam(p.QueryString, 'buildID') AS Firefox_BuildID,
    
    -- ============================================
    -- CHROME-SPECIFIC (added 2026-02-02)
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'chromeObj') AS BIT) AS Chrome_ObjectPresent,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'chromeRuntime') AS BIT) AS Chrome_RuntimePresent,
    dbo.GetQueryParam(p.QueryString, 'jsHeapLimit') AS Chrome_JSHeapLimit,
    dbo.GetQueryParam(p.QueryString, 'jsHeapTotal') AS Chrome_JSHeapTotal,
    dbo.GetQueryParam(p.QueryString, 'jsHeapUsed') AS Chrome_JSHeapUsed,
    
    -- ============================================
    -- ADDITIONAL FINGERPRINTS (added 2026-02-02)
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'audioHash') AS AudioHash,             -- Full audio buffer hash
    dbo.GetQueryParam(p.QueryString, 'pluginList') AS PluginListDetail,     -- Full plugin names/descriptions
    dbo.GetQueryParam(p.QueryString, 'mimeList') AS MimeTypeList,           -- Full MIME type list
    dbo.GetQueryParam(p.QueryString, 'tzLocale') AS TimezoneLocale,         -- Locale formatting details
    dbo.GetQueryParam(p.QueryString, 'dateFormat') AS DateFormatSample,     -- How dates render
    dbo.GetQueryParam(p.QueryString, 'numberFormat') AS NumberFormatSample, -- How numbers render
    dbo.GetQueryParam(p.QueryString, 'cssFontVariant') AS CSSFontVariant,   -- CSS font feature support
    
    -- ============================================
    -- RAW DATA (for debugging/reprocessing)
    -- ============================================
    p.QueryString AS RawQueryString

FROM dbo.PiXL_Test p;
GO

-- =============================================
-- Create permanent typed table for materialized data
-- This is where you'll copy data for indexed queries
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PiXL_Permanent')
BEGIN
    CREATE TABLE dbo.PiXL_Permanent (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        SourceId INT NOT NULL,  -- Links back to PiXL_Test.Id
        CompanyID NVARCHAR(100),
        PiXLID NVARCHAR(100),
        IPAddress NVARCHAR(50),
        ReceivedAt DATETIME2,
        
        -- Screen
        ScreenWidth INT,
        ScreenHeight INT,
        ViewportWidth INT,
        ViewportHeight INT,
        ColorDepth INT,
        PixelRatio DECIMAL(5,2),
        
        -- Device
        Platform NVARCHAR(50),
        CPUCores INT,
        DeviceMemory DECIMAL(5,2),
        GPU NVARCHAR(500),
        
        -- Fingerprints
        CanvasFingerprint NVARCHAR(100),
        WebGLFingerprint NVARCHAR(100),
        AudioFingerprint NVARCHAR(100),
        MathFingerprint NVARCHAR(200),
        
        -- Location/Time
        Timezone NVARCHAR(100),
        Language NVARCHAR(50),
        
        -- Page
        PageURL NVARCHAR(2000),
        PageReferrer NVARCHAR(2000),
        Domain NVARCHAR(200),
        
        -- Flags
        DarkModePreferred BIT,
        CookiesEnabled BIT,
        WebDriverDetected BIT,
        
        -- Processing
        MaterializedAt DATETIME2 DEFAULT GETUTCDATE()
    );
    
    -- Indexes for common queries
    CREATE INDEX IX_PiXL_Permanent_ReceivedAt ON dbo.PiXL_Permanent(ReceivedAt DESC);
    CREATE INDEX IX_PiXL_Permanent_CompanyPixl ON dbo.PiXL_Permanent(CompanyID, PiXLID);
    CREATE INDEX IX_PiXL_Permanent_CanvasFP ON dbo.PiXL_Permanent(CanvasFingerprint);
    CREATE INDEX IX_PiXL_Permanent_Domain ON dbo.PiXL_Permanent(Domain);
    
    -- UNIQUE constraint to prevent duplicate materialization from concurrent runs
    CREATE UNIQUE INDEX UX_PiXL_Permanent_SourceId ON dbo.PiXL_Permanent(SourceId);
END
GO

-- =============================================
-- Stored procedure to materialize data
-- Run this on a schedule (e.g., every 5 minutes)
-- =============================================
IF OBJECT_ID('dbo.sp_MaterializePiXLData', 'P') IS NOT NULL
    DROP PROCEDURE dbo.sp_MaterializePiXLData;
GO

CREATE PROCEDURE dbo.sp_MaterializePiXLData
AS
BEGIN
    SET NOCOUNT ON;
    
    -- Prevent concurrent execution with application lock
    DECLARE @LockResult INT;
    EXEC @LockResult = sp_getapplock 
        @Resource = 'MaterializePiXLData', 
        @LockMode = 'Exclusive', 
        @LockTimeout = 0;  -- Don't wait, just exit if locked
    
    IF @LockResult < 0
    BEGIN
        PRINT 'Another materialization is in progress. Exiting.';
        SELECT 0 AS RecordsMaterialized, 'Skipped - concurrent execution' AS Status;
        RETURN;
    END
    
    DECLARE @LastProcessedId INT;
    
    BEGIN TRY
        -- Get the last ID we processed
        SELECT @LastProcessedId = ISNULL(MAX(SourceId), 0) FROM dbo.PiXL_Permanent;
        
        -- Insert new records from the view
        INSERT INTO dbo.PiXL_Permanent (
            SourceId, CompanyID, PiXLID, IPAddress, ReceivedAt,
            ScreenWidth, ScreenHeight, ViewportWidth, ViewportHeight, ColorDepth, PixelRatio,
            Platform, CPUCores, DeviceMemory, GPU,
            CanvasFingerprint, WebGLFingerprint, AudioFingerprint, MathFingerprint,
            Timezone, Language,
            PageURL, PageReferrer, Domain,
            DarkModePreferred, CookiesEnabled, WebDriverDetected
        )
        SELECT 
            Id, CompanyID, PiXLID, IPAddress, ReceivedAt,
            ScreenWidth, ScreenHeight, ViewportWidth, ViewportHeight, ColorDepth, PixelRatio,
            Platform, CPUCores, DeviceMemory, GPU,
            CanvasFingerprint, WebGLFingerprint, AudioFingerprint, MathFingerprint,
            Timezone, Language,
            PageURL, PageReferrer, Domain,
            DarkModePreferred, CookiesEnabled, WebDriverDetected
        FROM dbo.vw_PiXL_Parsed
        WHERE Id > @LastProcessedId
        ORDER BY Id;
        
        SELECT @@ROWCOUNT AS RecordsMaterialized, 'Success' AS Status;
    END TRY
    BEGIN CATCH
        SELECT 0 AS RecordsMaterialized, ERROR_MESSAGE() AS Status;
        THROW;
    END CATCH
    
    -- Release the lock
    EXEC sp_releaseapplock @Resource = 'MaterializePiXLData';
END
GO

PRINT 'Streamlined schema complete!';
PRINT '- Base table updated with HeadersJson column';
PRINT '- View vw_PiXL_Parsed parses all 90+ parameters from QueryString';
PRINT '- Table PiXL_Permanent ready for materialized indexed data';
PRINT '- Stored proc sp_MaterializePiXLData ready to run on schedule (with concurrency protection)';
GO
