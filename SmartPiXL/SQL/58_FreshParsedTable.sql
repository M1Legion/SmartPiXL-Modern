-- ============================================================
-- 58_FreshParsedTable.sql
-- Archive old PiXL.Parsed → PiXL.Parsed_Archive (manual steps above)
-- Create fresh empty PiXL.Parsed with same 231-column schema
-- Reset HitSequence to start at 1
-- Reset all watermarks
-- ============================================================

-- Reset sequence to start fresh
ALTER SEQUENCE PiXL.HitSequence RESTART WITH 1;

CREATE TABLE PiXL.Parsed (
    SourceId              bigint          NOT NULL CONSTRAINT DF_Parsed_SourceId DEFAULT (NEXT VALUE FOR PiXL.HitSequence),
    CompanyID             int             NULL,
    PiXLID                int             NULL,
    IPAddress             varchar(50)     NULL,
    ReceivedAt            datetime2(7)    NOT NULL,
    RequestPath           varchar(500)    NULL,
    ServerUserAgent       varchar(2000)   NULL,
    ServerReferer         varchar(2000)   NULL,
    IsSynthetic           bit             NOT NULL DEFAULT 0,

    -- Screen & viewport
    ScreenWidth           int NULL, ScreenHeight int NULL,
    ScreenAvailWidth      int NULL, ScreenAvailHeight int NULL,
    ViewportWidth         int NULL, ViewportHeight int NULL,
    OuterWidth            int NULL, OuterHeight int NULL,
    ScreenX               int NULL, ScreenY int NULL,
    ColorDepth            int NULL,
    PixelRatio            decimal(5,2) NULL,
    ScreenOrientation     varchar(50) NULL,

    -- Time & locale
    Timezone              varchar(100) NULL,
    TimezoneOffsetMins    int NULL,
    ClientTimestampMs     bigint NULL,
    TimezoneLocale        varchar(200) NULL,
    DateFormatSample      varchar(200) NULL,
    NumberFormatSample    varchar(200) NULL,
    RelativeTimeSample    varchar(200) NULL,

    -- Browser identity
    [Language]            varchar(50) NULL,
    LanguageList          varchar(500) NULL,
    Platform              varchar(100) NULL,
    Vendor                varchar(200) NULL,
    ClientUserAgent       varchar(2000) NULL,
    HardwareConcurrency   int NULL,
    DeviceMemoryGB        decimal(5,2) NULL,
    MaxTouchPoints        int NULL,
    NavigatorProduct      varchar(50) NULL,
    NavigatorProductSub   varchar(50) NULL,
    NavigatorVendorSub    varchar(200) NULL,
    AppName               varchar(100) NULL,
    AppVersion            varchar(500) NULL,
    AppCodeName           varchar(100) NULL,

    -- GPU & WebGL
    GPURenderer           varchar(500) NULL,
    GPUVendor             varchar(200) NULL,
    WebGLParameters       varchar(2000) NULL,
    WebGLExtensionCount   int NULL,
    WebGLSupported        bit NULL,
    WebGL2Supported       bit NULL,

    -- Fingerprints
    CanvasFingerprint     varchar(200) NULL,
    WebGLFingerprint      varchar(200) NULL,
    AudioFingerprintSum   varchar(200) NULL,
    AudioFingerprintHash  varchar(200) NULL,
    MathFingerprint       varchar(200) NULL,
    ErrorFingerprint      varchar(200) NULL,
    CSSFontVariantHash    varchar(200) NULL,

    -- Fonts & plugins
    DetectedFonts         varchar(4000) NULL,
    PluginCount           int NULL,
    PluginListDetailed    varchar(4000) NULL,
    MimeTypeCount         int NULL,
    MimeTypeList          varchar(4000) NULL,
    SpeechVoices          varchar(4000) NULL,
    ConnectedGamepads     varchar(1000) NULL,

    -- Network
    WebRTCLocalIP         varchar(50) NULL,
    ConnectionType        varchar(50) NULL,
    DownlinkMbps          decimal(10,2) NULL,
    DownlinkMax           varchar(50) NULL,
    RTTMs                 int NULL,
    DataSaverEnabled      bit NULL,
    NetworkType           varchar(50) NULL,
    IsOnline              bit NULL,

    -- Storage
    StorageQuotaGB        int NULL,
    StorageUsedMB         int NULL,
    LocalStorageSupported bit NULL,
    SessionStorageSupported bit NULL,
    IndexedDBSupported    bit NULL,
    CacheAPISupported     bit NULL,

    -- Hardware
    BatteryLevelPct       int NULL,
    BatteryCharging       bit NULL,
    AudioInputDevices     int NULL,
    VideoInputDevices     int NULL,

    -- Browser features
    CookiesEnabled        bit NULL,
    DoNotTrack            varchar(50) NULL,
    PDFViewerEnabled      bit NULL,
    WebDriverDetected     bit NULL,
    JavaEnabled           bit NULL,
    CanvasSupported       bit NULL,
    WebAssemblySupported  bit NULL,
    WebWorkersSupported   bit NULL,
    ServiceWorkerSupported bit NULL,
    MediaDevicesAPISupported bit NULL,
    ClipboardAPISupported bit NULL,
    SpeechSynthesisSupported bit NULL,
    TouchEventsSupported  bit NULL,
    PointerEventsSupported bit NULL,
    HoverCapable          bit NULL,
    PointerType           varchar(20) NULL,

    -- Preferences
    PrefersColorSchemeDark  bit NULL,
    PrefersColorSchemeLight bit NULL,
    PrefersReducedMotion    bit NULL,
    PrefersReducedData      bit NULL,
    PrefersHighContrast     bit NULL,
    ForcedColorsActive      bit NULL,
    InvertedColorsActive    bit NULL,
    StandaloneDisplayMode   bit NULL,

    -- Document
    DocumentCharset       varchar(50) NULL,
    DocumentCompatMode    varchar(50) NULL,
    DocumentReadyState    varchar(50) NULL,
    DocumentHidden        bit NULL,
    DocumentVisibility    varchar(50) NULL,

    -- Page
    PageURL               varchar(2000) NULL,
    PageReferrer          varchar(2000) NULL,
    PageTitle             varchar(1000) NULL,
    PageDomain            varchar(500) NULL,
    PagePath              varchar(1000) NULL,
    PageHash              varchar(500) NULL,
    PageProtocol          varchar(20) NULL,
    HistoryLength         int NULL,

    -- Performance
    PageLoadTimeMs        int NULL,
    DOMReadyTimeMs        int NULL,
    DNSLookupMs           int NULL,
    TCPConnectMs          int NULL,
    TimeToFirstByteMs     int NULL,

    -- Bot detection
    BotSignalsList        varchar(4000) NULL,
    BotScore              int NULL,
    CombinedThreatScore   int NULL,
    ScriptExecutionTimeMs int NULL,
    BotPermissionInconsistent bit NULL,

    -- Evasion detection
    CanvasEvasionDetected bit NULL,
    WebGLEvasionDetected  bit NULL,
    EvasionToolsDetected  varchar(1000) NULL,
    ProxyBlockedProperties varchar(1000) NULL,

    -- UA Client Hints
    UA_Architecture       varchar(50) NULL,
    UA_Bitness            varchar(10) NULL,
    UA_Model              varchar(200) NULL,
    UA_PlatformVersion    varchar(100) NULL,
    UA_FullVersionList    varchar(500) NULL,
    UA_IsWow64            bit NULL,
    UA_IsMobile           bit NULL,
    UA_Platform           varchar(50) NULL,
    UA_Brands             varchar(500) NULL,
    UA_FormFactor         varchar(100) NULL,

    -- Browser-specific
    Firefox_OSCPU         varchar(200) NULL,
    Firefox_BuildID       varchar(100) NULL,
    Chrome_ObjectPresent  bit NULL,
    Chrome_RuntimePresent bit NULL,
    Chrome_JSHeapSizeLimit bigint NULL,
    Chrome_TotalJSHeapSize bigint NULL,
    Chrome_UsedJSHeapSize bigint NULL,

    -- Canvas & audio stability
    CanvasConsistency     varchar(50) NULL,
    AudioIsStable         bit NULL,
    AudioNoiseInjectionDetected bit NULL,

    -- Behavioral
    MouseMoveCount        int NULL,
    UserScrolled          bit NULL,
    ScrollDepthPx         int NULL,
    MouseEntropy          int NULL,
    ScrollContradiction   bit NULL,
    MoveTimingCV          int NULL,
    MoveSpeedCV           int NULL,
    MoveCountBucket       varchar(20) NULL,
    BehavioralFlags       varchar(200) NULL,

    -- Evasion v2
    StealthPluginSignals  varchar(500) NULL,
    FontMethodMismatch    bit NULL,
    EvasionSignalsV2      varchar(500) NULL,
    CrossSignalFlags      varchar(500) NULL,
    AnomalyScore          int NULL,

    -- ETL timestamp
    ParsedAt              datetime2(7) NOT NULL DEFAULT (SYSUTCDATETIME()),

    -- Server-side enrichments
    Srv_SubnetIps         int NULL,
    Srv_SubnetHits        int NULL,
    Srv_HitsIn15s         int NULL,
    Srv_LastGapMs         bigint NULL,
    Srv_SubSecDupe        bit NULL,
    Srv_SubnetAlert       bit NULL,
    Srv_RapidFire         bit NULL,

    -- Geo (from enrichment)
    GeoCountry            varchar(99) NULL,
    GeoCountryCode        varchar(10) NULL,
    GeoRegion             varchar(99) NULL,
    GeoCity               varchar(99) NULL,
    GeoZip                varchar(20) NULL,
    GeoLat                decimal(9,4) NULL,
    GeoLon                decimal(9,4) NULL,
    GeoTimezone           varchar(50) NULL,
    GeoISP                varchar(200) NULL,
    GeoTzMismatch         bit NULL,

    -- Hit metadata
    HitType               varchar(10) NULL,
    ScreenExtended        bit NULL,
    MousePath             varchar(2000) NULL,

    -- Bot classification
    KnownBot              bit NULL,
    BotName               varchar(200) NULL,

    -- UA parsing
    ParsedBrowser         varchar(100) NULL,
    ParsedBrowserVersion  varchar(50) NULL,
    ParsedOS              varchar(100) NULL,
    ParsedOSVersion       varchar(50) NULL,
    ParsedDeviceType      varchar(50) NULL,
    ParsedDeviceModel     varchar(100) NULL,
    ParsedDeviceBrand     varchar(100) NULL,

    -- DNS / MaxMind / WHOIS enrichments
    ReverseDNS            varchar(500) NULL,
    ReverseDNSCloud       bit NULL,
    MaxMindCountry        char(2) NULL,
    MaxMindRegion         varchar(100) NULL,
    MaxMindCity           varchar(200) NULL,
    MaxMindLat            decimal(9,6) NULL,
    MaxMindLon            decimal(9,6) NULL,
    MaxMindASN            int NULL,
    MaxMindASNOrg         varchar(200) NULL,
    WhoisASN              varchar(50) NULL,
    WhoisOrg              varchar(200) NULL,

    -- Cross-customer intelligence
    CrossCustomerHits     int NULL,
    CrossCustomerAlert    bit NULL,

    -- Lead scoring
    LeadQualityScore      int NULL,

    -- Session stitching
    SessionId             varchar(36) NULL,
    SessionHitNumber      int NULL,
    SessionDurationSec    int NULL,
    SessionPageCount      int NULL,

    -- Device intelligence
    AffluenceSignal       varchar(4) NULL,
    GpuTier               varchar(4) NULL,
    ContradictionCount    int NULL,
    ContradictionList     varchar(500) NULL,
    CulturalConsistencyScore int NULL,
    CulturalFlags         varchar(500) NULL,
    DeviceAgeYears        int NULL,
    DeviceAgeAnomaly      bit NULL,

    -- Replay detection
    ReplayDetected        bit NULL,
    ReplayMatchFingerprint varchar(200) NULL,

    -- Composite scores
    DeadInternetIndex     int NULL,

    -- Bitmaps
    FeatureBitmapValue    int NULL,
    AccessibilityBitmapValue int NULL,
    BotBitmapValue        int NULL,
    EvasionBitmapValue    int NULL,

    -- Raw payload for re-parse
    QueryString           nvarchar(max) NULL,
    HeadersJson           nvarchar(max) NULL,

    CONSTRAINT PK_PiXL_Parsed PRIMARY KEY NONCLUSTERED (SourceId)
);

-- Clustered index on ReceivedAt for time-range queries
CREATE UNIQUE CLUSTERED INDEX CIX_PiXL_Parsed_ReceivedAt
    ON PiXL.Parsed (ReceivedAt, SourceId);

-- Company dashboard queries
CREATE NONCLUSTERED INDEX IX_Parsed_Company_ReceivedAt
    ON PiXL.Parsed (PiXLID, IPAddress, BotScore, AnomalyScore, MouseMoveCount, UserScrolled, CompanyID, ReceivedAt);

CREATE NONCLUSTERED INDEX IX_Parsed_Company
    ON PiXL.Parsed (BotScore, IPAddress, CanvasFingerprint, CompanyID, PiXLID, ReceivedAt);

-- Reset watermarks for fresh start
UPDATE ETL.Watermark SET LastProcessedId = 0 WHERE ProcessName = 'ProcessDimensions';
UPDATE ETL.Watermark SET LastProcessedId = 0 WHERE ProcessName = 'ParseNewHits';
UPDATE ETL.Watermark SET LastProcessedId = 0 WHERE ProcessName = 'MatchVisits';
UPDATE ETL.Watermark SET LastProcessedId = 0 WHERE ProcessName = 'MatchLegacyVisits';

PRINT 'Fresh PiXL.Parsed created. Sequence reset to 1. All watermarks reset.';
