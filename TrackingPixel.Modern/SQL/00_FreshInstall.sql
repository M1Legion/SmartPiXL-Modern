-- =============================================
-- SmartPiXL Database - FRESH INSTALL SCRIPT
-- 
-- PREREQUISITES: 
--   Database 'SmartPixl' must exist with filegroup 'SmartPixl' on D:
--
-- This script creates:
-- 1. PiXL_Test table (raw data storage) on SmartPixl filegroup
-- 2. GetQueryParam function (parses query strings)
-- 3. vw_PiXL_Parsed view (extracts 100+ fingerprinting fields)
-- 4. PiXL_Materialized table (indexed data) on SmartPixl filegroup
-- 5. sp_MaterializePiXLData (scheduled data processing)
-- 6. Indexes on SmartPixl filegroup
--
-- Last Updated: 2026-01-26
-- =============================================

-- =============================================
-- STEP 1: USE EXISTING DATABASE
-- =============================================
USE SmartPixl;
GO

PRINT 'Using database SmartPixl with filegroup SmartPixl on D:';
GO

-- =============================================
-- STEP 2: CREATE MAIN RAW DATA TABLE
-- This table receives bulk inserts from the C# app.
-- Columns are: CompanyID, PiXLID, IPAddress, RequestPath, 
-- QueryString, HeadersJson, UserAgent, Referer, ReceivedAt
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PiXL_Test')
BEGIN
    CREATE TABLE dbo.PiXL_Test (
        Id              INT IDENTITY(1,1) PRIMARY KEY,
        CompanyID       NVARCHAR(100)   NULL,
        PiXLID          NVARCHAR(100)   NULL,
        IPAddress       NVARCHAR(50)    NULL,
        RequestPath     NVARCHAR(500)   NULL,
        QueryString     NVARCHAR(MAX)   NULL,  -- Holds all 100+ fingerprinting params
        HeadersJson     NVARCHAR(MAX)   NULL,  -- Raw HTTP headers as JSON
        UserAgent       NVARCHAR(2000)  NULL,
        Referer         NVARCHAR(2000)  NULL,
        ReceivedAt      DATETIME2       NOT NULL DEFAULT GETUTCDATE()
    ) ON [SmartPixl];
    
    PRINT 'Table PiXL_Test created on SmartPixl filegroup.';
END
ELSE
BEGIN
    PRINT 'Table PiXL_Test already exists.';
    
    -- Ensure all columns exist (for upgrades)
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'HeadersJson')
        ALTER TABLE PiXL_Test ADD HeadersJson NVARCHAR(MAX) NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'UserAgent')
        ALTER TABLE PiXL_Test ADD UserAgent NVARCHAR(2000) NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'Referer')
        ALTER TABLE PiXL_Test ADD Referer NVARCHAR(2000) NULL;
END
GO

-- =============================================
-- STEP 3: CREATE QUERY PARAMETER PARSER FUNCTION
-- Used by the view to extract values from QueryString
-- =============================================
IF OBJECT_ID('dbo.GetQueryParam', 'FN') IS NOT NULL
    DROP FUNCTION dbo.GetQueryParam;
GO

CREATE FUNCTION dbo.GetQueryParam(@QueryString NVARCHAR(MAX), @ParamName NVARCHAR(100))
RETURNS NVARCHAR(4000)
AS
BEGIN
    DECLARE @Start INT, @End INT, @Value NVARCHAR(4000);
    
    -- Handle NULL or empty
    IF @QueryString IS NULL OR LEN(@QueryString) = 0
        RETURN NULL;
    
    -- Look for param=value at start of string
    SET @Start = CHARINDEX(@ParamName + '=', @QueryString);
    
    -- If found but not at start and not after &, look for &param=
    IF @Start > 1 AND SUBSTRING(@QueryString, @Start - 1, 1) NOT IN ('&', '?')
        SET @Start = 0;
    
    IF @Start = 0
        SET @Start = CHARINDEX('&' + @ParamName + '=', @QueryString);
    
    IF @Start = 0
        RETURN NULL;
    
    -- Move past the param= or &param= part
    IF SUBSTRING(@QueryString, @Start, 1) = '&'
        SET @Start = @Start + 1;
    SET @Start = @Start + LEN(@ParamName) + 1;
    
    -- Find end (next & or end of string)
    SET @End = CHARINDEX('&', @QueryString, @Start);
    IF @End = 0
        SET @End = LEN(@QueryString) + 1;
    
    SET @Value = SUBSTRING(@QueryString, @Start, @End - @Start);
    
    -- URL decode common patterns
    SET @Value = REPLACE(@Value, '%20', ' ');
    SET @Value = REPLACE(@Value, '%2F', '/');
    SET @Value = REPLACE(@Value, '%3A', ':');
    SET @Value = REPLACE(@Value, '%2C', ',');
    SET @Value = REPLACE(@Value, '%3D', '=');
    SET @Value = REPLACE(@Value, '%26', '&');
    SET @Value = REPLACE(@Value, '%3F', '?');
    SET @Value = REPLACE(@Value, '%23', '#');
    SET @Value = REPLACE(@Value, '%25', '%');
    SET @Value = REPLACE(@Value, '+', ' ');
    
    RETURN NULLIF(@Value, '');
END
GO

PRINT 'Function GetQueryParam created.';
GO

-- =============================================
-- STEP 4: CREATE PARSED VIEW
-- Extracts all 90+ fingerprinting fields from QueryString
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
    
    -- Tier indicator
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tier') AS INT) AS Tier,
    
    -- ============================================
    -- SCREEN & WINDOW (13 fields)
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
    -- TIME & LOCALE (5 fields)
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'tz') AS Timezone,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tzo') AS INT) AS TimezoneOffset,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ts') AS BIGINT) AS ClientTimestamp,
    dbo.GetQueryParam(p.QueryString, 'lang') AS [Language],
    dbo.GetQueryParam(p.QueryString, 'langs') AS Languages,
    
    -- ============================================
    -- DEVICE & BROWSER (12 fields)
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'plt') AS [Platform],
    dbo.GetQueryParam(p.QueryString, 'vnd') AS Vendor,
    dbo.GetQueryParam(p.QueryString, 'ua') AS ClientUserAgent,
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
    -- GPU & WEBGL (4 fields)
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'gpu') AS GPU,
    dbo.GetQueryParam(p.QueryString, 'gpuVendor') AS GPUVendor,
    dbo.GetQueryParam(p.QueryString, 'webglParams') AS WebGLParams,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglExt') AS INT) AS WebGLExtensions,
    
    -- ============================================
    -- FINGERPRINTS (8 fields) - The good stuff!
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
    -- NETWORK & WEBRTC (9 fields)
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
    -- STORAGE & BATTERY (4 fields)
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'storageQuota') AS INT) AS StorageQuotaGB,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'storageUsed') AS INT) AS StorageUsedMB,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'batteryLevel') AS INT) AS BatteryLevel,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'batteryCharging') AS BIT) AS BatteryCharging,
    
    -- ============================================
    -- MEDIA DEVICES (2 fields)
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'audioInputs') AS INT) AS AudioInputDevices,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'videoInputs') AS INT) AS VideoInputDevices,
    
    -- ============================================
    -- BROWSER CAPABILITIES (7 fields)
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ck') AS BIT) AS CookiesEnabled,
    dbo.GetQueryParam(p.QueryString, 'dnt') AS DoNotTrack,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pdf') AS BIT) AS PDFViewerEnabled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webdr') AS BIT) AS WebDriverDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'java') AS BIT) AS JavaEnabled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'plugins') AS INT) AS PluginCount,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mimeTypes') AS INT) AS MimeTypeCount,
    
    -- ============================================
    -- SESSION & PAGE (8 fields)
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
    -- PERFORMANCE TIMING (5 fields)
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'loadTime') AS INT) AS LoadTimeMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'domTime') AS INT) AS DOMReadyMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'dnsTime') AS INT) AS DNSLookupMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tcpTime') AS INT) AS TCPConnectMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ttfb') AS INT) AS TTFBMs,
    
    -- ============================================
    -- STORAGE SUPPORT FLAGS (4 fields)
    -- ============================================
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ls') AS BIT) AS LocalStorageSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ss') AS BIT) AS SessionStorageSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'idb') AS BIT) AS IndexedDBSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'caches') AS BIT) AS CacheAPISupport,
    
    -- ============================================
    -- FEATURE DETECTION FLAGS (9 fields)
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
    -- ADVANCED API SUPPORT FLAGS (15 fields)
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
    -- CSS/MEDIA PREFERENCES (10 fields)
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
    -- DOCUMENT INFO (5 fields)
    -- ============================================
    dbo.GetQueryParam(p.QueryString, 'docCharset') AS DocCharset,
    dbo.GetQueryParam(p.QueryString, 'docCompat') AS DocCompatMode,
    dbo.GetQueryParam(p.QueryString, 'docReady') AS DocReadyState,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'docHidden') AS BIT) AS DocHidden,
    dbo.GetQueryParam(p.QueryString, 'docVisibility') AS DocVisibility,
    
    -- ============================================
    -- RAW DATA (for debugging/reprocessing)
    -- ============================================
    p.QueryString AS RawQueryString

FROM dbo.PiXL_Test p;
GO

PRINT 'View vw_PiXL_Parsed created with 100+ fields.';
GO

-- =============================================
-- STEP 5: CREATE MATERIALIZED TABLE
-- For indexed queries after data processing
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PiXL_Materialized')
BEGIN
    CREATE TABLE dbo.PiXL_Materialized (
        Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
        SourceId        INT NOT NULL,  -- Links back to PiXL_Test.Id
        CompanyID       NVARCHAR(100),
        PiXLID          NVARCHAR(100),
        IPAddress       NVARCHAR(50),
        ReceivedAt      DATETIME2,
        
        -- Screen
        ScreenWidth     INT,
        ScreenHeight    INT,
        ViewportWidth   INT,
        ViewportHeight  INT,
        ColorDepth      INT,
        PixelRatio      DECIMAL(5,2),
        
        -- Device
        [Platform]      NVARCHAR(50),
        CPUCores        INT,
        DeviceMemory    DECIMAL(5,2),
        GPU             NVARCHAR(500),
        
        -- Fingerprints
        CanvasFingerprint   NVARCHAR(100),
        WebGLFingerprint    NVARCHAR(100),
        AudioFingerprint    NVARCHAR(100),
        MathFingerprint     NVARCHAR(200),
        
        -- Location/Time
        Timezone        NVARCHAR(100),
        [Language]      NVARCHAR(50),
        
        -- Page
        PageURL         NVARCHAR(2000),
        PageReferrer    NVARCHAR(2000),
        Domain          NVARCHAR(200),
        
        -- Flags
        DarkModePreferred   BIT,
        CookiesEnabled      BIT,
        WebDriverDetected   BIT,
        
        -- IP Classification (future)
        IpType          NVARCHAR(20)    NULL,  -- Public, Private, CGNAT, etc.
        ShouldGeolocate BIT             NULL,
        
        -- Geo Data (future - from 342M cache)
        Country         NVARCHAR(100)   NULL,
        Region          NVARCHAR(100)   NULL,
        City            NVARCHAR(100)   NULL,
        PostalCode      NVARCHAR(20)    NULL,
        Latitude        DECIMAL(9,6)    NULL,
        Longitude       DECIMAL(9,6)    NULL,
        
        -- Processing metadata
        MaterializedAt  DATETIME2 DEFAULT GETUTCDATE()
    ) ON [SmartPixl];
    
    PRINT 'Table PiXL_Materialized created on SmartPixl filegroup.';
END
GO

-- =============================================
-- STEP 6: CREATE INDEXES (on SmartPixl filegroup)
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Test_ReceivedAt')
    CREATE INDEX IX_PiXL_Test_ReceivedAt ON dbo.PiXL_Test(ReceivedAt DESC) ON [SmartPixl];

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Test_CompanyPixl')
    CREATE INDEX IX_PiXL_Test_CompanyPixl ON dbo.PiXL_Test(CompanyID, PiXLID) ON [SmartPixl];

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Materialized_ReceivedAt')
    CREATE INDEX IX_PiXL_Materialized_ReceivedAt ON dbo.PiXL_Materialized(ReceivedAt DESC) ON [SmartPixl];

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Materialized_CompanyPixl')
    CREATE INDEX IX_PiXL_Materialized_CompanyPixl ON dbo.PiXL_Materialized(CompanyID, PiXLID) ON [SmartPixl];

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Materialized_CanvasFP')
    CREATE INDEX IX_PiXL_Materialized_CanvasFP ON dbo.PiXL_Materialized(CanvasFingerprint) ON [SmartPixl];

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Materialized_Domain')
    CREATE INDEX IX_PiXL_Materialized_Domain ON dbo.PiXL_Materialized(Domain) ON [SmartPixl];

-- UNIQUE constraint to prevent duplicate materialization from concurrent runs
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PiXL_Materialized_SourceId')
    CREATE UNIQUE INDEX UX_PiXL_Materialized_SourceId ON dbo.PiXL_Materialized(SourceId) ON [SmartPixl];

PRINT 'Indexes created on SmartPixl filegroup.';
GO

-- =============================================
-- STEP 7: CREATE MATERIALIZATION STORED PROCEDURE
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
    DECLARE @RowsInserted INT;
    
    BEGIN TRY
        -- Get the last ID we processed
        SELECT @LastProcessedId = ISNULL(MAX(SourceId), 0) FROM dbo.PiXL_Materialized;
        
        -- Insert new records from the view
        INSERT INTO dbo.PiXL_Materialized (
            SourceId, CompanyID, PiXLID, IPAddress, ReceivedAt,
            ScreenWidth, ScreenHeight, ViewportWidth, ViewportHeight, ColorDepth, PixelRatio,
            [Platform], CPUCores, DeviceMemory, GPU,
            CanvasFingerprint, WebGLFingerprint, AudioFingerprint, MathFingerprint,
            Timezone, [Language],
            PageURL, PageReferrer, Domain,
            DarkModePreferred, CookiesEnabled, WebDriverDetected
        )
        SELECT 
            Id, CompanyID, PiXLID, IPAddress, ReceivedAt,
            ScreenWidth, ScreenHeight, ViewportWidth, ViewportHeight, ColorDepth, PixelRatio,
            [Platform], CPUCores, DeviceMemory, GPU,
            CanvasFingerprint, WebGLFingerprint, AudioFingerprint, MathFingerprint,
            Timezone, [Language],
            PageURL, PageReferrer, Domain,
            DarkModePreferred, CookiesEnabled, WebDriverDetected
        FROM dbo.vw_PiXL_Parsed
        WHERE Id > @LastProcessedId
        ORDER BY Id;
        
        SET @RowsInserted = @@ROWCOUNT;
        
        PRINT 'Materialized ' + CAST(@RowsInserted AS VARCHAR(20)) + ' records.';
        SELECT @RowsInserted AS RecordsMaterialized, 'Success' AS Status;
    END TRY
    BEGIN CATCH
        SELECT 0 AS RecordsMaterialized, ERROR_MESSAGE() AS Status;
        THROW;
    END CATCH
    
    -- Release the lock
    EXEC sp_releaseapplock @Resource = 'MaterializePiXLData';
END
GO

PRINT 'Stored procedure sp_MaterializePiXLData created.';
GO

-- =============================================
-- DONE!
-- =============================================
PRINT '';
PRINT '============================================';
PRINT 'SmartPiXL Database Installation Complete!';
PRINT '============================================';
PRINT '';
PRINT 'Objects created:';
PRINT '  - Database: SmartPixl';
PRINT '  - Table: dbo.PiXL_Test (raw data from C# app)';
PRINT '  - Function: dbo.GetQueryParam (query string parser)';
PRINT '  - View: dbo.vw_PiXL_Parsed (100+ fingerprinting fields)';
PRINT '  - Table: dbo.PiXL_Materialized (indexed data)';
PRINT '  - Procedure: dbo.sp_MaterializePiXLData (batch processing)';
PRINT '';
PRINT 'Next steps:';
PRINT '  1. Configure C# app connection string to this server';
PRINT '  2. Test with: SELECT * FROM vw_PiXL_Parsed';
PRINT '  3. Schedule sp_MaterializePiXLData to run every 5 min';
PRINT '';
GO
