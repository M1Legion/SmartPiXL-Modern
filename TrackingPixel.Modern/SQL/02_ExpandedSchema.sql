-- =============================================
-- SmartPiXL Database Schema - EXPANDED Version
-- Adds all new fingerprinting and data columns
-- Run this on SmartPixl database
-- =============================================

USE SmartPixl;
GO

-- =============================================
-- Drop and recreate the view first
-- =============================================
IF OBJECT_ID('dbo.vw_PiXL_Parsed', 'V') IS NOT NULL
    DROP VIEW dbo.vw_PiXL_Parsed;
GO

-- =============================================
-- Add new columns to PiXL_Test table
-- =============================================

-- Fingerprints
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'MathFingerprint')
    ALTER TABLE PiXL_Test ADD MathFingerprint NVARCHAR(200) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'ErrorFingerprint')
    ALTER TABLE PiXL_Test ADD ErrorFingerprint NVARCHAR(100) NULL;

-- WebRTC / Network
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'LocalIP')
    ALTER TABLE PiXL_Test ADD LocalIP NVARCHAR(50) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'ConnectionType')
    ALTER TABLE PiXL_Test ADD ConnectionType NVARCHAR(50) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'DownlinkMax')
    ALTER TABLE PiXL_Test ADD DownlinkMax NVARCHAR(20) NULL;

-- Storage & Battery
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'StorageQuota')
    ALTER TABLE PiXL_Test ADD StorageQuota INT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'StorageUsed')
    ALTER TABLE PiXL_Test ADD StorageUsed INT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'BatteryLevel')
    ALTER TABLE PiXL_Test ADD BatteryLevel INT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'BatteryCharging')
    ALTER TABLE PiXL_Test ADD BatteryCharging BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'CacheAPISupport')
    ALTER TABLE PiXL_Test ADD CacheAPISupport BIT NULL;

-- Media Devices
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'AudioInputs')
    ALTER TABLE PiXL_Test ADD AudioInputs INT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'VideoInputs')
    ALTER TABLE PiXL_Test ADD VideoInputs INT NULL;

-- Speech & Gamepads
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'SpeechVoices')
    ALTER TABLE PiXL_Test ADD SpeechVoices NVARCHAR(500) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'Gamepads')
    ALTER TABLE PiXL_Test ADD Gamepads NVARCHAR(500) NULL;

-- Navigator extended
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'Product')
    ALTER TABLE PiXL_Test ADD Product NVARCHAR(50) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'ProductSub')
    ALTER TABLE PiXL_Test ADD ProductSub NVARCHAR(50) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'VendorSub')
    ALTER TABLE PiXL_Test ADD VendorSub NVARCHAR(50) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'AppName')
    ALTER TABLE PiXL_Test ADD AppName NVARCHAR(50) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'AppVersion')
    ALTER TABLE PiXL_Test ADD AppVersion NVARCHAR(200) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'AppCodeName')
    ALTER TABLE PiXL_Test ADD AppCodeName NVARCHAR(50) NULL;

-- WebGL extended
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'WebGLParams')
    ALTER TABLE PiXL_Test ADD WebGLParams NVARCHAR(500) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'WebGLExtensions')
    ALTER TABLE PiXL_Test ADD WebGLExtensions INT NULL;

-- Performance extended
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'DNSTime')
    ALTER TABLE PiXL_Test ADD DNSTime INT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'TCPTime')
    ALTER TABLE PiXL_Test ADD TCPTime INT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'TTFB')
    ALTER TABLE PiXL_Test ADD TTFB INT NULL;

-- Session extended
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'Domain')
    ALTER TABLE PiXL_Test ADD Domain NVARCHAR(200) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'Path')
    ALTER TABLE PiXL_Test ADD [Path] NVARCHAR(500) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'Hash')
    ALTER TABLE PiXL_Test ADD [Hash] NVARCHAR(200) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'Protocol')
    ALTER TABLE PiXL_Test ADD Protocol NVARCHAR(20) NULL;

-- Advanced feature flags
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'CanvasSupport')
    ALTER TABLE PiXL_Test ADD CanvasSupport BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'BluetoothSupport')
    ALTER TABLE PiXL_Test ADD BluetoothSupport BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'USBSupport')
    ALTER TABLE PiXL_Test ADD USBSupport BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'SerialSupport')
    ALTER TABLE PiXL_Test ADD SerialSupport BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'HIDSupport')
    ALTER TABLE PiXL_Test ADD HIDSupport BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'MIDISupport')
    ALTER TABLE PiXL_Test ADD MIDISupport BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'XRSupport')
    ALTER TABLE PiXL_Test ADD XRSupport BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'ShareSupport')
    ALTER TABLE PiXL_Test ADD ShareSupport BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'ClipboardSupport')
    ALTER TABLE PiXL_Test ADD ClipboardSupport BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'CredentialsSupport')
    ALTER TABLE PiXL_Test ADD CredentialsSupport BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'GeolocationSupport')
    ALTER TABLE PiXL_Test ADD GeolocationSupport BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'NotificationsSupport')
    ALTER TABLE PiXL_Test ADD NotificationsSupport BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'PushSupport')
    ALTER TABLE PiXL_Test ADD PushSupport BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'PaymentSupport')
    ALTER TABLE PiXL_Test ADD PaymentSupport BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'SpeechRecogSupport')
    ALTER TABLE PiXL_Test ADD SpeechRecogSupport BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'SpeechSynthSupport')
    ALTER TABLE PiXL_Test ADD SpeechSynthSupport BIT NULL;

-- Preferences extended
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'LightModePreferred')
    ALTER TABLE PiXL_Test ADD LightModePreferred BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'ReducedDataPreferred')
    ALTER TABLE PiXL_Test ADD ReducedDataPreferred BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'HighContrastPreferred')
    ALTER TABLE PiXL_Test ADD HighContrastPreferred BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'ForcedColorsActive')
    ALTER TABLE PiXL_Test ADD ForcedColorsActive BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'InvertedColorsActive')
    ALTER TABLE PiXL_Test ADD InvertedColorsActive BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'HoverCapable')
    ALTER TABLE PiXL_Test ADD HoverCapable BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'PointerType')
    ALTER TABLE PiXL_Test ADD PointerType NVARCHAR(20) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'StandaloneMode')
    ALTER TABLE PiXL_Test ADD StandaloneMode BIT NULL;

-- Document info
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'DocCharset')
    ALTER TABLE PiXL_Test ADD DocCharset NVARCHAR(50) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'DocCompatMode')
    ALTER TABLE PiXL_Test ADD DocCompatMode NVARCHAR(50) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'DocReadyState')
    ALTER TABLE PiXL_Test ADD DocReadyState NVARCHAR(50) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'DocHidden')
    ALTER TABLE PiXL_Test ADD DocHidden BIT NULL;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PiXL_Test') AND name = 'DocVisibility')
    ALTER TABLE PiXL_Test ADD DocVisibility NVARCHAR(50) NULL;

GO

-- =============================================
-- Recreate the parsed view with all columns
-- =============================================
CREATE VIEW dbo.vw_PiXL_Parsed AS
SELECT 
    p.Id,
    p.CompanyID,
    p.PiXLID,
    p.TrackingCode,
    p.IPAddress,
    p.ReceivedAt,
    
    -- Tier
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tier') AS INT) AS Tier,
    
    -- Screen
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
    
    -- Time & Locale
    dbo.GetQueryParam(p.QueryString, 'tz') AS Timezone,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tzo') AS INT) AS TimezoneOffset,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ts') AS BIGINT) AS ClientTimestamp,
    dbo.GetQueryParam(p.QueryString, 'lang') AS Language,
    dbo.GetQueryParam(p.QueryString, 'langs') AS Languages,
    
    -- Device
    dbo.GetQueryParam(p.QueryString, 'plt') AS Platform,
    dbo.GetQueryParam(p.QueryString, 'vnd') AS Vendor,
    dbo.GetQueryParam(p.QueryString, 'ua') AS UserAgent,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'cores') AS INT) AS CPUCores,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mem') AS DECIMAL(5,2)) AS DeviceMemory,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touch') AS INT) AS MaxTouchPoints,
    dbo.GetQueryParam(p.QueryString, 'product') AS Product,
    dbo.GetQueryParam(p.QueryString, 'productSub') AS ProductSub,
    dbo.GetQueryParam(p.QueryString, 'vendorSub') AS VendorSub,
    dbo.GetQueryParam(p.QueryString, 'appName') AS AppName,
    dbo.GetQueryParam(p.QueryString, 'appVersion') AS AppVersion,
    dbo.GetQueryParam(p.QueryString, 'appCodeName') AS AppCodeName,
    
    -- GPU
    dbo.GetQueryParam(p.QueryString, 'gpu') AS GPU,
    dbo.GetQueryParam(p.QueryString, 'gpuVendor') AS GPUVendor,
    dbo.GetQueryParam(p.QueryString, 'webglParams') AS WebGLParams,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webglExt') AS INT) AS WebGLExtensions,
    
    -- Fingerprints
    dbo.GetQueryParam(p.QueryString, 'canvasFP') AS CanvasFingerprint,
    dbo.GetQueryParam(p.QueryString, 'webglFP') AS WebGLFingerprint,
    dbo.GetQueryParam(p.QueryString, 'audioFP') AS AudioFingerprint,
    dbo.GetQueryParam(p.QueryString, 'mathFP') AS MathFingerprint,
    dbo.GetQueryParam(p.QueryString, 'errorFP') AS ErrorFingerprint,
    dbo.GetQueryParam(p.QueryString, 'fonts') AS DetectedFonts,
    dbo.GetQueryParam(p.QueryString, 'voices') AS SpeechVoices,
    dbo.GetQueryParam(p.QueryString, 'gamepads') AS Gamepads,
    
    -- Network
    dbo.GetQueryParam(p.QueryString, 'localIp') AS LocalIP,
    dbo.GetQueryParam(p.QueryString, 'conn') AS ConnectionType,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'dl') AS DECIMAL(10,2)) AS Downlink,
    dbo.GetQueryParam(p.QueryString, 'dlMax') AS DownlinkMax,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'rtt') AS INT) AS RTT,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'save') AS BIT) AS DataSaverEnabled,
    dbo.GetQueryParam(p.QueryString, 'connType') AS ConnectionNetworkType,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'online') AS BIT) AS IsOnline,
    
    -- Storage & Battery
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'storageQuota') AS INT) AS StorageQuotaGB,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'storageUsed') AS INT) AS StorageUsedMB,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'batteryLevel') AS INT) AS BatteryLevel,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'batteryCharging') AS BIT) AS BatteryCharging,
    
    -- Media Devices
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'audioInputs') AS INT) AS AudioInputDevices,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'videoInputs') AS INT) AS VideoInputDevices,
    
    -- Browser Capabilities
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ck') AS BIT) AS CookiesEnabled,
    dbo.GetQueryParam(p.QueryString, 'dnt') AS DoNotTrack,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pdf') AS BIT) AS PDFViewerEnabled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webdr') AS BIT) AS WebDriverDetected,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'java') AS BIT) AS JavaEnabled,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'plugins') AS INT) AS PluginCount,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mimeTypes') AS INT) AS MimeTypeCount,
    
    -- Session
    dbo.GetQueryParam(p.QueryString, 'url') AS PageURL,
    dbo.GetQueryParam(p.QueryString, 'ref') AS Referrer,
    dbo.GetQueryParam(p.QueryString, 'title') AS PageTitle,
    dbo.GetQueryParam(p.QueryString, 'domain') AS Domain,
    dbo.GetQueryParam(p.QueryString, 'path') AS [Path],
    dbo.GetQueryParam(p.QueryString, 'hash') AS [Hash],
    dbo.GetQueryParam(p.QueryString, 'protocol') AS Protocol,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'hist') AS INT) AS HistoryLength,
    
    -- Performance
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'loadTime') AS INT) AS LoadTimeMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'domTime') AS INT) AS DOMReadyMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'dnsTime') AS INT) AS DNSLookupMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'tcpTime') AS INT) AS TCPConnectMs,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ttfb') AS INT) AS TTFBMs,
    
    -- Storage Support
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ls') AS BIT) AS LocalStorageSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ss') AS BIT) AS SessionStorageSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'idb') AS BIT) AS IndexedDBSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'caches') AS BIT) AS CacheAPISupport,
    
    -- Feature Support
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'ww') AS BIT) AS WebWorkersSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'swk') AS BIT) AS ServiceWorkerSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'wasm') AS BIT) AS WebAssemblySupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webgl') AS BIT) AS WebGLSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'webgl2') AS BIT) AS WebGL2Support,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'canvas') AS BIT) AS CanvasSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'touchEvent') AS BIT) AS TouchEventsSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'pointerEvent') AS BIT) AS PointerEventsSupport,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'mediaDevices') AS BIT) AS MediaDevicesSupport,
    
    -- Advanced APIs
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
    
    -- Preferences
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
    
    -- Document
    dbo.GetQueryParam(p.QueryString, 'docCharset') AS DocCharset,
    dbo.GetQueryParam(p.QueryString, 'docCompat') AS DocCompatMode,
    dbo.GetQueryParam(p.QueryString, 'docReady') AS DocReadyState,
    TRY_CAST(dbo.GetQueryParam(p.QueryString, 'docHidden') AS BIT) AS DocHidden,
    dbo.GetQueryParam(p.QueryString, 'docVisibility') AS DocVisibility,
    
    -- Raw data
    p.QueryString,
    p.RequestPath

FROM dbo.PiXL_Test p;
GO

PRINT 'Schema update complete!';
PRINT 'New columns added to PiXL_Test table.';
PRINT 'View vw_PiXL_Parsed recreated with all new fields.';
GO
