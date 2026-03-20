using System.Collections;
using System.Data.Common;
using System.Globalization;
using SmartPiXL.Forge.Services.Enrichments;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// PARSED RECORD PARSER — .NET replacement for ETL Phases 1–8D.
//
// The ETL proc (usp_ParseNewHits) calls dbo.GetQueryParam() ~300 times per row
// as a scalar UDF — SQL Server evaluates these row-by-row with zero vectorization.
// For a 50K batch: 15M+ function invocations → 337 seconds.
//
// This class replaces all that with a single-pass .NET dictionary parse + typed
// extraction using the existing span-based QueryParamReader. Cost: ~1μs per row.
// For a 50K batch: ~50ms vs 337 seconds = 6,700x faster.
//
// FIELD MAPPING:
//   Every assignment below maps directly to an ETL proc phase. The QS param
//   names, column ordinals, and type conversions are 1:1 with the SQL proc.
//   See SmartPiXL/SQL/61_FixDeviceIpUpsert.sql for the source-of-truth mapping.
//
// USAGE:
//   - ParsedBulkInsertService: backfill from PiXL.Raw
//   - SqlBulkCopyWriterService: dual-write for new Forge traffic (future)
// ============================================================================

/// <summary>
/// Parses a PiXL.Raw record's QueryString into all 230 PiXL.Parsed columns.
/// Returns an <c>object?[]</c> array for direct consumption by <see cref="ParsedDataReader"/>.
/// </summary>
internal static class ParsedRecordParser
{
    /// <summary>Number of columns written to PiXL.Parsed (excludes SourceId which is SEQUENCE-generated).</summary>
    internal const int ColumnCount = 230;

    /// <summary>
    /// Column names for <see cref="Microsoft.Data.SqlClient.SqlBulkCopy"/> mappings.
    /// SourceId is omitted — SQL generates it via SEQUENCE DEFAULT (PiXL.HitSequence).
    /// QueryString and HeadersJson are the raw fields merged from the former PiXL.Raw table.
    /// </summary>
    internal static readonly string[] ColumnNames =
    [
        // ── Raw field: QueryString (merged from PiXL.Raw) ──────────────
        "QueryString",          // 0  — nvarchar(max) ← TrackingData.QueryString
        "CompanyID",            // 1  — int      ← Raw.CompanyID
        "PiXLID",               // 2  — int      ← Raw.PiXLID
        "IPAddress",            // 3  — varchar  ← Raw.IPAddress
        "ReceivedAt",           // 4  — datetime2 ← Raw.ReceivedAt
        "RequestPath",          // 5  — varchar  ← Raw.RequestPath
        "ServerUserAgent",      // 6  — varchar  ← Raw.UserAgent
        "ServerReferer",        // 7  — varchar  ← Raw.Referer

        // ── Phase 1: Screen + Locale (cols 9–31) ───────────────────────
        "IsSynthetic",          // 8   — bit     ← 'synthetic'
        "ScreenWidth",          // 9   — int     ← 'sw'
        "ScreenHeight",         // 10  — int     ← 'sh'
        "ScreenAvailWidth",     // 11  — int     ← 'saw'
        "ScreenAvailHeight",    // 12  — int     ← 'sah'
        "ViewportWidth",        // 13  — int     ← 'vw'
        "ViewportHeight",       // 14  — int     ← 'vh'
        "OuterWidth",           // 15  — int     ← 'ow'
        "OuterHeight",          // 16  — int     ← 'oh'
        "ScreenX",              // 17  — int     ← 'sx'
        "ScreenY",              // 18  — int     ← 'sy'
        "ColorDepth",           // 19  — int     ← 'cd'
        "PixelRatio",           // 20  — dec(5,2) ← 'pd'
        "ScreenOrientation",    // 21  — varchar ← 'ori'
        "Timezone",             // 22  — varchar ← 'tz'
        "TimezoneOffsetMins",   // 23  — int     ← 'tzo'
        "ClientTimestampMs",    // 24  — bigint  ← 'ts'
        "TimezoneLocale",       // 25  — varchar ← 'tzLocale'
        "DateFormatSample",     // 26  — varchar ← 'dateFormat'
        "NumberFormatSample",   // 27  — varchar ← 'numberFormat'
        "RelativeTimeSample",   // 28  — varchar ← 'relativeTime'
        "Language",             // 29  — varchar ← 'lang'
        "LanguageList",         // 30  — varchar ← 'langs'

        // ── Phase 2: Browser + GPU + Fingerprints (cols 33–58) ─────────
        "Platform",             // 31  — varchar ← 'plt'
        "Vendor",               // 32  — varchar ← 'vnd'
        "ClientUserAgent",      // 33  — varchar ← 'ua'
        "HardwareConcurrency",  // 34  — int     ← 'cores'
        "DeviceMemoryGB",       // 35  — dec(5,2) ← 'mem'
        "MaxTouchPoints",       // 36  — int     ← 'touch'
        "NavigatorProduct",     // 37  — varchar ← 'product'
        "NavigatorProductSub",  // 38  — varchar ← 'productSub'
        "NavigatorVendorSub",   // 39  — varchar ← 'vendorSub'
        "AppName",              // 40  — varchar ← 'appName'
        "AppVersion",           // 41  — varchar ← 'appVersion'
        "AppCodeName",          // 42  — varchar ← 'appCodeName'
        "GPURenderer",          // 43  — varchar ← 'gpu'
        "GPUVendor",            // 44  — varchar ← 'gpuVendor'
        "WebGLParameters",      // 45  — varchar ← 'webglParams'
        "WebGLExtensionCount",  // 46  — int     ← 'webglExt'
        "WebGLSupported",       // 47  — bit     ← 'webgl'
        "WebGL2Supported",      // 48  — bit     ← 'webgl2'
        "CanvasFingerprint",    // 49  — varchar ← 'canvasFP'
        "WebGLFingerprint",     // 50  — varchar ← 'webglFP'
        "AudioFingerprintSum",  // 51  — varchar ← 'audioFP'
        "AudioFingerprintHash", // 52  — varchar ← 'audioHash'
        "MathFingerprint",      // 53  — varchar ← 'mathFP'
        "ErrorFingerprint",     // 54  — varchar ← 'errorFP'
        "CSSFontVariantHash",   // 55  — varchar ← 'cssFontVariant'
        "DetectedFonts",        // 56  — varchar ← 'fonts'

        // ── Phase 3: Plugins + Network + Storage (cols 59–87) ──────────
        "PluginCount",          // 57  — int     ← 'plugins'
        "PluginListDetailed",   // 58  — varchar ← 'pluginList'
        "MimeTypeCount",        // 59  — int     ← 'mimeTypes'
        "MimeTypeList",         // 60  — varchar ← 'mimeList'
        "SpeechVoices",         // 61  — varchar ← 'voices'
        "ConnectedGamepads",    // 62  — varchar ← 'gamepads'
        "WebRTCLocalIP",        // 63  — varchar ← 'localIp'
        "ConnectionType",       // 64  — varchar ← 'conn'
        "DownlinkMbps",         // 65  — dec(10,2) ← 'dl'
        "DownlinkMax",          // 66  — varchar ← 'dlMax'
        "RTTMs",                // 67  — int     ← 'rtt'
        "DataSaverEnabled",     // 68  — bit     ← 'save'
        "NetworkType",          // 69  — varchar ← 'connType'
        "IsOnline",             // 70  — bit     ← 'online'
        "StorageQuotaGB",       // 71  — int     ← 'storageQuota'
        "StorageUsedMB",        // 72  — int     ← 'storageUsed'
        "LocalStorageSupported",    // 73  — bit  ← 'ls'
        "SessionStorageSupported",  // 74  — bit  ← 'ss'
        "IndexedDBSupported",       // 75  — bit  ← 'idb'
        "CacheAPISupported",        // 76  — bit  ← 'caches'
        "BatteryLevelPct",      // 77  — int     ← 'batteryLevel'
        "BatteryCharging",      // 78  — bit     ← 'batteryCharging'
        "AudioInputDevices",    // 79  — int     ← 'audioInputs'
        "VideoInputDevices",    // 80  — int     ← 'videoInputs'
        "CookiesEnabled",       // 81  — bit     ← 'ck'
        "DoNotTrack",           // 82  — varchar ← 'dnt'
        "PDFViewerEnabled",     // 83  — bit     ← 'pdf'
        "WebDriverDetected",    // 84  — bit     ← 'webdr'
        "JavaEnabled",          // 85  — bit     ← 'java'

        // ── Phase 4: Capabilities + Preferences + Document (cols 88–111) ─
        "CanvasSupported",      // 86  — bit     ← 'canvas'
        "WebAssemblySupported", // 87  — bit     ← 'wasm'
        "WebWorkersSupported",  // 88  — bit     ← 'ww'
        "ServiceWorkerSupported",   // 89  — bit ← 'swk'
        "MediaDevicesAPISupported", // 90  — bit ← 'mediaDevices'
        "ClipboardAPISupported",    // 91  — bit ← 'clipboard'
        "SpeechSynthesisSupported", // 92  — bit ← 'speechSynth'
        "TouchEventsSupported",     // 93  — bit ← 'touchEvent'
        "PointerEventsSupported",   // 94  — bit ← 'pointerEvent'
        "HoverCapable",         // 95  — bit     ← 'hover'
        "PointerType",          // 96  — varchar ← 'pointer'
        "PrefersColorSchemeDark",   // 97  — bit ← 'darkMode'
        "PrefersColorSchemeLight",  // 98  — bit ← 'lightMode'
        "PrefersReducedMotion",     // 99  — bit ← 'reducedMotion'
        "PrefersReducedData",       // 100 — bit ← 'reducedData'
        "PrefersHighContrast",      // 101 — bit ← 'contrast'
        "ForcedColorsActive",       // 102 — bit ← 'forcedColors'
        "InvertedColorsActive",     // 103 — bit ← 'invertedColors'
        "StandaloneDisplayMode",    // 104 — bit ← 'standalone'
        "DocumentCharset",      // 105 — varchar ← 'docCharset'
        "DocumentCompatMode",   // 106 — varchar ← 'docCompat'
        "DocumentReadyState",   // 107 — varchar ← 'docReady'
        "DocumentHidden",       // 108 — bit     ← 'docHidden'
        "DocumentVisibility",   // 109 — varchar ← 'docVisibility'

        // ── Phase 5: Page + Performance + Bot (cols 112–129) ───────────
        "PageURL",              // 110 — varchar ← 'url'
        "PageReferrer",         // 111 — varchar ← 'ref'
        "PageTitle",            // 112 — varchar ← 'title'
        "PageDomain",           // 113 — varchar ← 'domain'
        "PagePath",             // 114 — varchar ← 'path'
        "PageHash",             // 115 — varchar ← 'hash'
        "PageProtocol",         // 116 — varchar ← 'protocol'
        "HistoryLength",        // 117 — int     ← 'hist'
        "PageLoadTimeMs",       // 118 — int     ← 'loadTime'
        "DOMReadyTimeMs",       // 119 — int     ← 'domTime'
        "DNSLookupMs",          // 120 — int     ← 'dnsTime'
        "TCPConnectMs",         // 121 — int     ← 'tcpTime'
        "TimeToFirstByteMs",    // 122 — int     ← 'ttfb'
        "BotSignalsList",       // 123 — varchar ← 'botSignals'
        "BotScore",             // 124 — int     ← 'botScore'
        "CombinedThreatScore",  // 125 — int     ← 'combinedThreatScore'
        "ScriptExecutionTimeMs",// 126 — int     ← 'scriptExecTime'
        "BotPermissionInconsistent", // 127 — bit ← 'botPermInconsistent'

        // ── Phase 6: Evasion + Client Hints + Browser-specific (cols 130–153) ─
        "CanvasEvasionDetected",    // 128 — bit ← 'canvasEvasion'
        "WebGLEvasionDetected",     // 129 — bit ← 'webglEvasion'
        "EvasionToolsDetected",     // 130 — varchar ← 'evasionDetected'
        "ProxyBlockedProperties",   // 131 — varchar ← '_proxyBlocked'
        "UA_Architecture",      // 132 — varchar ← 'uaArch'
        "UA_Bitness",           // 133 — varchar ← 'uaBitness'
        "UA_Model",             // 134 — varchar ← 'uaModel'
        "UA_PlatformVersion",   // 135 — varchar ← 'uaPlatformVersion'
        "UA_FullVersionList",   // 136 — varchar ← 'uaFullVersion'
        "UA_IsWow64",           // 137 — bit     ← 'uaWow64'
        "UA_IsMobile",          // 138 — bit     ← 'uaMobile'
        "UA_Platform",          // 139 — varchar ← 'uaPlatform'
        "UA_Brands",            // 140 — varchar ← 'uaBrands'
        "UA_FormFactor",        // 141 — varchar ← 'uaFormFactor'
        "Firefox_OSCPU",        // 142 — varchar ← 'oscpu'
        "Firefox_BuildID",      // 143 — varchar ← 'buildID'
        "Chrome_ObjectPresent", // 144 — bit     ← 'chromeObj'
        "Chrome_RuntimePresent",// 145 — bit     ← 'chromeRuntime'
        "Chrome_JSHeapSizeLimit",   // 146 — bigint ← 'jsHeapLimit'
        "Chrome_TotalJSHeapSize",   // 147 — bigint ← 'jsHeapTotal'
        "Chrome_UsedJSHeapSize",    // 148 — bigint ← 'jsHeapUsed'
        "CanvasConsistency",    // 149 — varchar ← 'canvasConsistency'
        "AudioIsStable",        // 150 — bit     ← 'audioStable'
        "AudioNoiseInjectionDetected", // 151 — bit ← 'audioNoiseDetected'

        // ── Phase 7: Behavioral + Cross-signal (cols 154–167) ──────────
        "MouseMoveCount",       // 152 — int     ← 'mouseMoves'
        "UserScrolled",         // 153 — bit     ← 'scrolled'
        "ScrollDepthPx",        // 154 — int     ← 'scrollY'
        "MouseEntropy",         // 155 — int     ← 'mouseEntropy'
        "ScrollContradiction",  // 156 — bit     ← 'scrollContradiction'
        "MoveTimingCV",         // 157 — int     ← 'moveTimingCV'
        "MoveSpeedCV",          // 158 — int     ← 'moveSpeedCV'
        "MoveCountBucket",      // 159 — varchar ← 'moveCountBucket'
        "BehavioralFlags",      // 160 — varchar ← 'behavioralFlags'
        "StealthPluginSignals", // 161 — varchar ← 'stealthSignals'
        "FontMethodMismatch",   // 162 — bit     ← 'fontMethodMismatch'
        "EvasionSignalsV2",     // 163 — varchar ← 'evasionSignalsV2'
        "CrossSignalFlags",     // 164 — varchar ← 'crossSignals'
        "AnomalyScore",         // 165 — int     ← 'anomalyScore'

        // ── ParsedAt (col 168) ─────────────────────────────────────────
        "ParsedAt",             // 166 — datetime2 ← SYSUTCDATETIME()

        // ── Phase 8: Server-side IP behavior (cols 169–175) ────────────
        "Srv_SubnetIps",        // 167 — int     ← '_srv_subnetIps'
        "Srv_SubnetHits",       // 168 — int     ← '_srv_subnetHits'
        "Srv_HitsIn15s",        // 169 — int     ← '_srv_hitsIn15s'
        "Srv_LastGapMs",        // 170 — bigint  ← '_srv_lastGapMs'
        "Srv_SubSecDupe",       // 171 — bit     ← '_srv_subSecDupe'
        "Srv_SubnetAlert",      // 172 — bit     ← '_srv_subnetAlert'
        "Srv_RapidFire",        // 173 — bit     ← '_srv_rapidFire'

        // ── Geo fields (cols 176–185) — filled by usp_EnrichParsedGeo ──
        "GeoCountry",           // 174 — varchar (NULL — filled later)
        "GeoCountryCode",       // 175 — varchar (NULL — filled later)
        "GeoRegion",            // 176 — varchar (NULL — filled later)
        "GeoCity",              // 177 — varchar (NULL — filled later)
        "GeoZip",               // 178 — varchar (NULL — filled later)
        "GeoLat",               // 179 — decimal (NULL — filled later)
        "GeoLon",               // 180 — decimal (NULL — filled later)
        "GeoTimezone",          // 181 — varchar (NULL — filled later)
        "GeoISP",               // 182 — varchar (NULL — filled later)
        "GeoTzMismatch",        // 183 — bit     (NULL — filled later)

        // ── Phase 1 extras (cols 186–188) ──────────────────────────────
        "HitType",              // 184 — varchar ← '_srv_hitType' (default 'modern')
        "ScreenExtended",       // 185 — bit     ← 'screenExtended'
        "MousePath",            // 186 — varchar ← 'mousePath'

        // ── Phase 8B: Forge Tier 1 enrichment (cols 189–208) ───────────
        "KnownBot",             // 187 — bit     ← '_srv_knownBot'
        "BotName",              // 188 — varchar ← '_srv_botName'
        "ParsedBrowser",        // 189 — varchar ← '_srv_browser'
        "ParsedBrowserVersion", // 190 — varchar ← '_srv_browserVer'
        "ParsedOS",             // 191 — varchar ← '_srv_os'
        "ParsedOSVersion",      // 192 — varchar ← '_srv_osVer'
        "ParsedDeviceType",     // 193 — varchar ← '_srv_deviceType'
        "ParsedDeviceModel",    // 194 — varchar ← '_srv_deviceModel'
        "ParsedDeviceBrand",    // 195 — varchar ← '_srv_deviceBrand'
        "ReverseDNS",           // 196 — varchar ← '_srv_rdns'
        "ReverseDNSCloud",      // 197 — bit     ← '_srv_rdnsCloud'
        "MaxMindCountry",       // 198 — char(2) ← '_srv_mmCC'
        "MaxMindRegion",        // 199 — varchar ← '_srv_mmReg'
        "MaxMindCity",          // 200 — varchar ← '_srv_mmCity'
        "MaxMindLat",           // 201 — dec(9,6) ← '_srv_mmLat'
        "MaxMindLon",           // 202 — dec(9,6) ← '_srv_mmLon'
        "MaxMindASN",           // 203 — int     ← '_srv_mmASN'
        "MaxMindASNOrg",        // 204 — varchar ← '_srv_mmASNOrg'
        "WhoisASN",             // 205 — varchar ← '_srv_whoisASN'
        "WhoisOrg",             // 206 — varchar ← '_srv_whoisOrg'

        // ── Phase 8C: Forge Tier 2 enrichment (cols 209–217) ───────────
        "CrossCustomerHits",    // 207 — int     ← '_srv_crossCustHits'
        "CrossCustomerAlert",   // 208 — bit     ← '_srv_crossCustAlert'
        "LeadQualityScore",     // 209 — int     ← '_srv_leadScore'
        "SessionId",            // 210 — varchar ← '_srv_sessionId'
        "SessionHitNumber",     // 211 — int     ← '_srv_sessionHitNum'
        "SessionDurationSec",   // 212 — int     ← '_srv_sessionDurationSec'
        "SessionPageCount",     // 213 — int     ← '_srv_sessionPages'
        "AffluenceSignal",      // 214 — varchar ← '_srv_affluence'
        "GpuTier",              // 215 — varchar ← '_srv_gpuTier'

        // ── Phase 8D: Forge Tier 3 enrichment (cols 218–226) ───────────
        "ContradictionCount",       // 216 — int     ← '_srv_contradictions'
        "ContradictionList",        // 217 — varchar ← '_srv_contradictionList'
        "CulturalConsistencyScore", // 218 — int     ← '_srv_culturalScore'
        "CulturalFlags",            // 219 — varchar ← '_srv_culturalFlags'
        "DeviceAgeYears",           // 220 — int     ← '_srv_deviceAge'
        "DeviceAgeAnomaly",         // 221 — bit     ← '_srv_deviceAgeAnomaly'
        "ReplayDetected",           // 222 — bit     ← '_srv_replayDetected'
        "ReplayMatchFingerprint",   // 223 — varchar ← '_srv_replayMatchFP'
        "DeadInternetIndex",        // 224 — int     ← '_srv_deadInternetIdx'

        // ── Bitmap fields (cols 227–230) — computed elsewhere ──────────
        "FeatureBitmapValue",       // 225 — int (NULL — computed)
        "AccessibilityBitmapValue", // 226 — int (NULL — computed)
        "BotBitmapValue",           // 227 — int (NULL — computed)
        "EvasionBitmapValue",       // 228 — int (NULL — computed)

        // ── Raw field: HeadersJson (merged from PiXL.Raw) ──────────────
        "HeadersJson",              // 229 — nvarchar(max) ← TrackingData.HeadersJson
    ];

    /// <summary>
    /// Parses a single TrackingData record into a 230-element value array for PiXL.Parsed.
    /// SourceId is omitted — SQL generates it via SEQUENCE DEFAULT.
    /// QueryString and HeadersJson are preserved as raw fields for future re-parse capability.
    /// </summary>
    internal static object?[] Parse(
        int? companyId, int? pixlId, string? ipAddress,
        DateTime receivedAt, string? requestPath,
        string? queryString, string? headersJson,
        string? userAgent, string? referer)
    {
        var v = new object?[ColumnCount];
        var qs = queryString;

        // ════════════════════════════════════════════════════════════════
        // RAW FIELD — QueryString preserved for re-parse capability
        // ════════════════════════════════════════════════════════════════
        v[0]  = (object?)queryString ?? DBNull.Value;           // QueryString

        // ════════════════════════════════════════════════════════════════
        // IDENTIFIERS — Direct from TrackingData (no parsing)
        // ════════════════════════════════════════════════════════════════
        v[1]  = (object?)companyId ?? DBNull.Value;             // CompanyID
        v[2]  = (object?)pixlId ?? DBNull.Value;                // PiXLID
        v[3]  = (object?)ipAddress ?? DBNull.Value;             // IPAddress
        v[4]  = receivedAt;                                     // ReceivedAt
        v[5]  = (object?)requestPath ?? DBNull.Value;           // RequestPath
        v[6]  = (object?)userAgent ?? DBNull.Value;             // ServerUserAgent
        v[7]  = (object?)referer ?? DBNull.Value;               // ServerReferer

        // ════════════════════════════════════════════════════════════════
        // PHASE 1 — Screen + Locale (ETL proc Phase 1 INSERT)
        // ════════════════════════════════════════════════════════════════
        // IsSynthetic — special: CASE WHEN TRY_CAST(val AS INT) = 1 THEN 1 ELSE 0
        v[8]  = int.TryParse(Qs(qs, "synthetic"), out var syn) && syn == 1;
        v[9] = QsInt(qs, "sw");                                // ScreenWidth
        v[10] = QsInt(qs, "sh");                                // ScreenHeight
        v[11] = QsInt(qs, "saw");                               // ScreenAvailWidth
        v[12] = QsInt(qs, "sah");                               // ScreenAvailHeight
        v[13] = QsInt(qs, "vw");                                // ViewportWidth
        v[14] = QsInt(qs, "vh");                                // ViewportHeight
        v[15] = QsInt(qs, "ow");                                // OuterWidth
        v[16] = QsInt(qs, "oh");                                // OuterHeight
        v[17] = QsInt(qs, "sx");                                // ScreenX
        v[18] = QsInt(qs, "sy");                                // ScreenY
        v[19] = QsInt(qs, "cd");                                // ColorDepth
        v[20] = QsDec(qs, "pd");                                // PixelRatio
        v[21] = QsStr(qs, "ori");                               // ScreenOrientation
        v[22] = QsStr(qs, "tz");                                // Timezone
        v[23] = QsInt(qs, "tzo");                               // TimezoneOffsetMins
        v[24] = QsLong(qs, "ts");                               // ClientTimestampMs
        v[25] = QsStr(qs, "tzLocale");                          // TimezoneLocale
        v[26] = QsStr(qs, "dateFormat");                        // DateFormatSample
        v[27] = QsStr(qs, "numberFormat");                      // NumberFormatSample
        v[28] = QsStr(qs, "relativeTime");                      // RelativeTimeSample
        v[29] = QsStr(qs, "lang");                              // Language
        v[30] = QsStr(qs, "langs");                             // LanguageList

        // ════════════════════════════════════════════════════════════════
        // PHASE 2 — Browser + GPU + Fingerprints (ETL proc Phase 2 UPDATE)
        // ════════════════════════════════════════════════════════════════
        v[31] = QsStr(qs, "plt");                               // Platform
        v[32] = QsStr(qs, "vnd");                               // Vendor
        v[33] = QsStr(qs, "ua");                                // ClientUserAgent
        v[34] = QsInt(qs, "cores");                             // HardwareConcurrency
        v[35] = QsDec(qs, "mem");                               // DeviceMemoryGB
        v[36] = QsInt(qs, "touch");                             // MaxTouchPoints
        v[37] = QsStr(qs, "product");                           // NavigatorProduct
        v[38] = QsStr(qs, "productSub");                        // NavigatorProductSub
        v[39] = QsStr(qs, "vendorSub");                         // NavigatorVendorSub
        v[40] = QsStr(qs, "appName");                           // AppName
        v[41] = QsStr(qs, "appVersion");                        // AppVersion
        v[42] = QsStr(qs, "appCodeName");                       // AppCodeName
        v[43] = QsStr(qs, "gpu");                               // GPURenderer
        v[44] = QsStr(qs, "gpuVendor");                         // GPUVendor
        v[45] = QsStr(qs, "webglParams");                       // WebGLParameters
        v[46] = QsInt(qs, "webglExt");                          // WebGLExtensionCount
        v[47] = QsBit(qs, "webgl");                             // WebGLSupported
        v[48] = QsBit(qs, "webgl2");                            // WebGL2Supported
        v[49] = QsStr(qs, "canvasFP");                          // CanvasFingerprint
        v[50] = QsStr(qs, "webglFP");                           // WebGLFingerprint
        v[51] = QsStr(qs, "audioFP");                           // AudioFingerprintSum
        v[52] = QsStr(qs, "audioHash");                         // AudioFingerprintHash
        v[53] = QsStr(qs, "mathFP");                            // MathFingerprint
        v[54] = QsStr(qs, "errorFP");                           // ErrorFingerprint
        v[55] = QsStr(qs, "cssFontVariant");                    // CSSFontVariantHash
        v[56] = QsStr(qs, "fonts");                             // DetectedFonts

        // ════════════════════════════════════════════════════════════════
        // PHASE 3 — Plugins + Network + Storage + Capabilities (ETL Phase 3)
        // ════════════════════════════════════════════════════════════════
        v[57] = QsInt(qs, "plugins");                           // PluginCount
        v[58] = QsStr(qs, "pluginList");                        // PluginListDetailed
        v[59] = QsInt(qs, "mimeTypes");                         // MimeTypeCount
        v[60] = QsStr(qs, "mimeList");                          // MimeTypeList
        v[61] = QsStr(qs, "voices");                            // SpeechVoices
        v[62] = QsStr(qs, "gamepads");                          // ConnectedGamepads
        v[63] = QsStr(qs, "localIp");                           // WebRTCLocalIP
        v[64] = QsStr(qs, "conn");                              // ConnectionType
        v[65] = QsDec(qs, "dl");                                // DownlinkMbps
        v[66] = QsStr(qs, "dlMax");                             // DownlinkMax
        v[67] = QsInt(qs, "rtt");                               // RTTMs
        v[68] = QsBit(qs, "save");                              // DataSaverEnabled
        v[69] = QsStr(qs, "connType");                          // NetworkType
        v[70] = QsBit(qs, "online");                            // IsOnline
        v[71] = QsInt(qs, "storageQuota");                      // StorageQuotaGB
        v[72] = QsInt(qs, "storageUsed");                       // StorageUsedMB
        v[73] = QsBit(qs, "ls");                                // LocalStorageSupported
        v[74] = QsBit(qs, "ss");                                // SessionStorageSupported
        v[75] = QsBit(qs, "idb");                               // IndexedDBSupported
        v[76] = QsBit(qs, "caches");                            // CacheAPISupported
        v[77] = QsInt(qs, "batteryLevel");                      // BatteryLevelPct
        v[78] = QsBit(qs, "batteryCharging");                   // BatteryCharging
        v[79] = QsInt(qs, "audioInputs");                       // AudioInputDevices
        v[80] = QsInt(qs, "videoInputs");                       // VideoInputDevices
        v[81] = QsBit(qs, "ck");                                // CookiesEnabled
        v[82] = QsStr(qs, "dnt");                               // DoNotTrack
        v[83] = QsBit(qs, "pdf");                               // PDFViewerEnabled
        v[84] = QsBit(qs, "webdr");                             // WebDriverDetected
        v[85] = QsBit(qs, "java");                              // JavaEnabled

        // ════════════════════════════════════════════════════════════════
        // PHASE 4 — Capabilities2 + Preferences + Document (ETL Phase 4)
        // ════════════════════════════════════════════════════════════════
        v[86]  = QsBit(qs, "canvas");                           // CanvasSupported
        v[87]  = QsBit(qs, "wasm");                             // WebAssemblySupported
        v[88]  = QsBit(qs, "ww");                               // WebWorkersSupported
        v[89]  = QsBit(qs, "swk");                              // ServiceWorkerSupported
        v[90]  = QsBit(qs, "mediaDevices");                     // MediaDevicesAPISupported
        v[91]  = QsBit(qs, "clipboard");                        // ClipboardAPISupported
        v[92]  = QsBit(qs, "speechSynth");                      // SpeechSynthesisSupported
        v[93]  = QsBit(qs, "touchEvent");                       // TouchEventsSupported
        v[94]  = QsBit(qs, "pointerEvent");                     // PointerEventsSupported
        v[95]  = QsBit(qs, "hover");                            // HoverCapable
        v[96]  = QsStr(qs, "pointer");                          // PointerType
        v[97]  = QsBit(qs, "darkMode");                         // PrefersColorSchemeDark
        v[98]  = QsBit(qs, "lightMode");                        // PrefersColorSchemeLight
        v[99] = QsBit(qs, "reducedMotion");                    // PrefersReducedMotion
        v[100] = QsBit(qs, "reducedData");                      // PrefersReducedData
        v[101] = QsBit(qs, "contrast");                         // PrefersHighContrast
        v[102] = QsBit(qs, "forcedColors");                     // ForcedColorsActive
        v[103] = QsBit(qs, "invertedColors");                   // InvertedColorsActive
        v[104] = QsBit(qs, "standalone");                       // StandaloneDisplayMode
        v[105] = QsStr(qs, "docCharset");                       // DocumentCharset
        v[106] = QsStr(qs, "docCompat");                        // DocumentCompatMode
        v[107] = QsStr(qs, "docReady");                         // DocumentReadyState
        v[108] = QsBit(qs, "docHidden");                        // DocumentHidden
        v[109] = QsStr(qs, "docVisibility");                    // DocumentVisibility

        // ════════════════════════════════════════════════════════════════
        // PHASE 5 — Page + Performance + Bot (ETL Phase 5)
        // ════════════════════════════════════════════════════════════════
        v[110] = QsStr(qs, "url");                              // PageURL
        v[111] = QsStr(qs, "ref");                              // PageReferrer
        v[112] = QsStr(qs, "title");                            // PageTitle
        v[113] = QsStr(qs, "domain");                           // PageDomain
        v[114] = QsStr(qs, "path");                             // PagePath
        v[115] = QsStr(qs, "hash");                             // PageHash
        v[116] = QsStr(qs, "protocol");                         // PageProtocol
        v[117] = QsInt(qs, "hist");                             // HistoryLength
        v[118] = QsInt(qs, "loadTime");                         // PageLoadTimeMs
        v[119] = QsInt(qs, "domTime");                          // DOMReadyTimeMs
        v[120] = QsInt(qs, "dnsTime");                          // DNSLookupMs
        v[121] = QsInt(qs, "tcpTime");                          // TCPConnectMs
        v[122] = QsInt(qs, "ttfb");                             // TimeToFirstByteMs
        v[123] = QsStr(qs, "botSignals");                       // BotSignalsList
        v[124] = QsInt(qs, "botScore");                         // BotScore
        v[125] = QsInt(qs, "combinedThreatScore");              // CombinedThreatScore
        v[126] = QsInt(qs, "scriptExecTime");                   // ScriptExecutionTimeMs
        v[127] = QsBit(qs, "botPermInconsistent");              // BotPermissionInconsistent

        // ════════════════════════════════════════════════════════════════
        // PHASE 6 — Evasion + Client Hints + Browser-specific (ETL Phase 6)
        // ════════════════════════════════════════════════════════════════
        v[128] = QsBit(qs, "canvasEvasion");                    // CanvasEvasionDetected
        v[129] = QsBit(qs, "webglEvasion");                     // WebGLEvasionDetected
        v[130] = QsStr(qs, "evasionDetected");                  // EvasionToolsDetected
        v[131] = QsStr(qs, "_proxyBlocked");                    // ProxyBlockedProperties
        v[132] = QsStr(qs, "uaArch");                           // UA_Architecture
        v[133] = QsStr(qs, "uaBitness");                        // UA_Bitness
        v[134] = QsStr(qs, "uaModel");                          // UA_Model
        v[135] = QsStr(qs, "uaPlatformVersion");                // UA_PlatformVersion
        v[136] = QsStr(qs, "uaFullVersion");                    // UA_FullVersionList
        v[137] = QsBit(qs, "uaWow64");                          // UA_IsWow64
        v[138] = QsBit(qs, "uaMobile");                         // UA_IsMobile
        v[139] = QsStr(qs, "uaPlatform");                       // UA_Platform
        v[140] = QsStr(qs, "uaBrands");                         // UA_Brands
        v[141] = QsStr(qs, "uaFormFactor");                     // UA_FormFactor
        v[142] = QsStr(qs, "oscpu");                            // Firefox_OSCPU
        v[143] = QsStr(qs, "buildID");                          // Firefox_BuildID
        v[144] = QsBit(qs, "chromeObj");                        // Chrome_ObjectPresent
        v[145] = QsBit(qs, "chromeRuntime");                    // Chrome_RuntimePresent
        v[146] = QsLong(qs, "jsHeapLimit");                     // Chrome_JSHeapSizeLimit
        v[147] = QsLong(qs, "jsHeapTotal");                     // Chrome_TotalJSHeapSize
        v[148] = QsLong(qs, "jsHeapUsed");                      // Chrome_UsedJSHeapSize
        v[149] = QsStr(qs, "canvasConsistency");                // CanvasConsistency
        v[150] = QsBit(qs, "audioStable");                      // AudioIsStable
        v[151] = QsBit(qs, "audioNoiseDetected");               // AudioNoiseInjectionDetected

        // ════════════════════════════════════════════════════════════════
        // PHASE 7 — Behavioral + Cross-signal (ETL Phase 7)
        // ════════════════════════════════════════════════════════════════
        v[152] = QsInt(qs, "mouseMoves");                       // MouseMoveCount
        v[153] = QsBit(qs, "scrolled");                         // UserScrolled
        v[154] = QsInt(qs, "scrollY");                          // ScrollDepthPx
        v[155] = QsInt(qs, "mouseEntropy");                     // MouseEntropy
        v[156] = QsBit(qs, "scrollContradiction");              // ScrollContradiction
        v[157] = QsInt(qs, "moveTimingCV");                     // MoveTimingCV
        v[158] = QsInt(qs, "moveSpeedCV");                      // MoveSpeedCV
        v[159] = QsStr(qs, "moveCountBucket");                  // MoveCountBucket
        v[160] = QsStr(qs, "behavioralFlags");                  // BehavioralFlags
        v[161] = QsStr(qs, "stealthSignals");                   // StealthPluginSignals
        v[162] = QsBit(qs, "fontMethodMismatch");               // FontMethodMismatch
        v[163] = QsStr(qs, "evasionSignalsV2");                 // EvasionSignalsV2
        v[164] = QsStr(qs, "crossSignals");                     // CrossSignalFlags
        v[165] = QsInt(qs, "anomalyScore");                     // AnomalyScore

        // ════════════════════════════════════════════════════════════════
        // PARSED TIMESTAMP
        // ════════════════════════════════════════════════════════════════
        v[166] = DateTime.UtcNow;                               // ParsedAt

        // ════════════════════════════════════════════════════════════════
        // PHASE 8 — Server-side IP behavior signals
        // ════════════════════════════════════════════════════════════════
        v[167] = QsInt(qs,  "_srv_subnetIps");                  // Srv_SubnetIps
        v[168] = QsInt(qs,  "_srv_subnetHits");                 // Srv_SubnetHits
        v[169] = QsInt(qs,  "_srv_hitsIn15s");                  // Srv_HitsIn15s
        v[170] = QsLong(qs, "_srv_lastGapMs");                  // Srv_LastGapMs
        v[171] = QsBit(qs,  "_srv_subSecDupe");                 // Srv_SubSecDupe
        v[172] = QsBit(qs,  "_srv_subnetAlert");                // Srv_SubnetAlert
        v[173] = QsBit(qs,  "_srv_rapidFire");                  // Srv_RapidFire

        // ════════════════════════════════════════════════════════════════
        // GEO FIELDS — Left NULL, filled by usp_EnrichParsedGeo later
        // ════════════════════════════════════════════════════════════════
        v[174] = DBNull.Value;  // GeoCountry
        v[175] = DBNull.Value;  // GeoCountryCode
        v[176] = DBNull.Value;  // GeoRegion
        v[177] = DBNull.Value;  // GeoCity
        v[178] = DBNull.Value;  // GeoZip
        v[179] = DBNull.Value;  // GeoLat
        v[180] = DBNull.Value;  // GeoLon
        v[181] = DBNull.Value;  // GeoTimezone
        v[182] = DBNull.Value;  // GeoISP
        v[183] = DBNull.Value;  // GeoTzMismatch

        // ════════════════════════════════════════════════════════════════
        // PHASE 1 EXTRAS — Placed after Geo in schema but parsed in Phase 1
        // ════════════════════════════════════════════════════════════════
        // HitType — COALESCE(GetQueryParam(qs, '_srv_hitType'), 'modern')
        v[184] = (object?)Qs(qs, "_srv_hitType") ?? "modern";   // HitType
        v[185] = QsBit(qs, "screenExtended");                   // ScreenExtended
        v[186] = QsStr(qs, "mousePath");                        // MousePath

        // ════════════════════════════════════════════════════════════════
        // PHASE 8B — Forge Tier 1 enrichment (_srv_* params)
        // ════════════════════════════════════════════════════════════════
        v[187] = QsBit(qs, "_srv_knownBot");                    // KnownBot
        v[188] = QsStr(qs, "_srv_botName");                     // BotName
        v[189] = QsStr(qs, "_srv_browser");                     // ParsedBrowser
        v[190] = QsStr(qs, "_srv_browserVer");                  // ParsedBrowserVersion
        v[191] = QsStr(qs, "_srv_os");                          // ParsedOS
        v[192] = QsStr(qs, "_srv_osVer");                       // ParsedOSVersion
        v[193] = QsStr(qs, "_srv_deviceType");                  // ParsedDeviceType
        v[194] = QsStr(qs, "_srv_deviceModel");                 // ParsedDeviceModel
        v[195] = QsStr(qs, "_srv_deviceBrand");                 // ParsedDeviceBrand
        v[196] = QsStr(qs, "_srv_rdns");                        // ReverseDNS
        v[197] = QsBit(qs, "_srv_rdnsCloud");                   // ReverseDNSCloud
        v[198] = QsStr(qs, "_srv_mmCC");                        // MaxMindCountry
        v[199] = QsStr(qs, "_srv_mmReg");                       // MaxMindRegion
        v[200] = QsStr(qs, "_srv_mmCity");                      // MaxMindCity
        v[201] = QsDec(qs, "_srv_mmLat");                       // MaxMindLat
        v[202] = QsDec(qs, "_srv_mmLon");                       // MaxMindLon
        v[203] = QsInt(qs, "_srv_mmASN");                       // MaxMindASN
        v[204] = QsStr(qs, "_srv_mmASNOrg");                    // MaxMindASNOrg
        v[205] = QsStr(qs, "_srv_whoisASN");                    // WhoisASN
        v[206] = QsStr(qs, "_srv_whoisOrg");                    // WhoisOrg

        // ════════════════════════════════════════════════════════════════
        // PHASE 8C — Forge Tier 2 enrichment
        // ════════════════════════════════════════════════════════════════
        v[207] = QsInt(qs, "_srv_crossCustHits");               // CrossCustomerHits
        v[208] = QsBit(qs, "_srv_crossCustAlert");              // CrossCustomerAlert
        v[209] = QsInt(qs, "_srv_leadScore");                   // LeadQualityScore
        v[210] = QsStr(qs, "_srv_sessionId");                   // SessionId
        v[211] = QsInt(qs, "_srv_sessionHitNum");               // SessionHitNumber
        v[212] = QsInt(qs, "_srv_sessionDurationSec");          // SessionDurationSec
        v[213] = QsInt(qs, "_srv_sessionPages");                // SessionPageCount
        v[214] = QsStr(qs, "_srv_affluence");                   // AffluenceSignal
        v[215] = QsStr(qs, "_srv_gpuTier");                     // GpuTier

        // ════════════════════════════════════════════════════════════════
        // PHASE 8D — Forge Tier 3 enrichment (Asymmetric Detection)
        // ════════════════════════════════════════════════════════════════
        v[216] = QsInt(qs, "_srv_contradictions");              // ContradictionCount
        v[217] = QsStr(qs, "_srv_contradictionList");           // ContradictionList
        v[218] = QsInt(qs, "_srv_culturalScore");               // CulturalConsistencyScore
        v[219] = QsStr(qs, "_srv_culturalFlags");               // CulturalFlags
        v[220] = QsInt(qs, "_srv_deviceAge");                   // DeviceAgeYears
        v[221] = QsBit(qs, "_srv_deviceAgeAnomaly");            // DeviceAgeAnomaly
        v[222] = QsBit(qs, "_srv_replayDetected");              // ReplayDetected
        v[223] = QsStr(qs, "_srv_replayMatchFP");               // ReplayMatchFingerprint
        v[224] = QsInt(qs, "_srv_deadInternetIdx");             // DeadInternetIndex

        // ════════════════════════════════════════════════════════════════
        // BITMAP FIELDS — Computed elsewhere, NULL for now
        // ════════════════════════════════════════════════════════════════
        v[225] = DBNull.Value;  // FeatureBitmapValue
        v[226] = DBNull.Value;  // AccessibilityBitmapValue
        v[227] = DBNull.Value;  // BotBitmapValue
        v[228] = DBNull.Value;  // EvasionBitmapValue

        // ════════════════════════════════════════════════════════════════
        // RAW FIELD — HeadersJson preserved for re-parse capability
        // ════════════════════════════════════════════════════════════════
        v[229] = (object?)headersJson ?? DBNull.Value;          // HeadersJson

        return v;
    }

    // ════════════════════════════════════════════════════════════════════
    // TYPE CONVERSION HELPERS — Match SQL TRY_CAST semantics exactly
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Raw string extraction — delegates to span-based QueryParamReader.</summary>
    private static string? Qs(string? qs, string param) => QueryParamReader.Get(qs, param);

    /// <summary>String column: returns value or DBNull. Matches GetQueryParam → varchar.</summary>
    private static object QsStr(string? qs, string param)
    {
        var val = QueryParamReader.Get(qs, param);
        return val is not null ? val : DBNull.Value;
    }

    /// <summary>Int column: TRY_CAST(val AS INT). Returns int or DBNull.</summary>
    private static object QsInt(string? qs, string param)
    {
        var val = QueryParamReader.Get(qs, param);
        if (val is null) return DBNull.Value;
        return int.TryParse(val, out var n) ? n : DBNull.Value;
    }

    /// <summary>Bigint column: TRY_CAST(val AS BIGINT). Returns long or DBNull.</summary>
    private static object QsLong(string? qs, string param)
    {
        var val = QueryParamReader.Get(qs, param);
        if (val is null) return DBNull.Value;
        return long.TryParse(val, out var n) ? n : DBNull.Value;
    }

    /// <summary>
    /// Bit column: TRY_CAST(val AS BIT). Returns bool or DBNull.
    /// SQL semantics: '0' → false, any non-zero int → true, non-numeric → NULL.
    /// </summary>
    private static object QsBit(string? qs, string param)
    {
        var val = QueryParamReader.Get(qs, param);
        if (val is null) return DBNull.Value;
        return int.TryParse(val, out var n) ? n != 0 : DBNull.Value;
    }

    /// <summary>Decimal column: TRY_CAST(val AS DECIMAL). Returns decimal or DBNull.</summary>
    private static object QsDec(string? qs, string param)
    {
        var val = QueryParamReader.Get(qs, param);
        if (val is null) return DBNull.Value;
        return decimal.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : DBNull.Value;
    }
}

// ============================================================================
// PARSED DATA READER — Zero-allocation DbDataReader for SqlBulkCopy to PiXL.Parsed.
// Same pattern as TrackingDataReader (for PiXL.Raw) in SqlBulkCopyWriterService.
// ============================================================================

/// <summary>
/// Lightweight <see cref="DbDataReader"/> that wraps a pre-parsed <c>List&lt;object?[]&gt;</c>
/// for direct consumption by <see cref="Microsoft.Data.SqlClient.SqlBulkCopy"/>.
/// Each <c>object?[]</c> is a 229-element row produced by <see cref="ParsedRecordParser.Parse"/>.
/// </summary>
internal sealed class ParsedDataReader(List<object?[]> batch) : DbDataReader
{
    private int _index = -1;

    public override int FieldCount => ParsedRecordParser.ColumnCount;
    public override int RecordsAffected => -1;
    public override bool HasRows => batch.Count > 0;
    public override bool IsClosed => _index >= batch.Count;
    public override int Depth => 0;
    public override bool Read() => ++_index < batch.Count;
    public override bool NextResult() => false;

    public override object GetValue(int ordinal) => batch[_index][ordinal] ?? DBNull.Value;

    public override bool IsDBNull(int ordinal)
    {
        var val = batch[_index][ordinal];
        return val is null or DBNull;
    }

    public override string GetName(int ordinal) => ParsedRecordParser.ColumnNames[ordinal];
    public override int GetOrdinal(string name) => Array.IndexOf(ParsedRecordParser.ColumnNames, name);
    public override string GetDataTypeName(int ordinal) => "nvarchar"; // SqlBulkCopy handles conversion
    public override Type GetFieldType(int ordinal) => typeof(object);
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        Array.Copy(batch[_index], values, count);
        return count;
    }

    // ── Required abstract stubs — never called by SqlBulkCopy ──────────
    public override string GetString(int ordinal) => GetValue(ordinal)?.ToString() ?? throw new InvalidCastException();
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
    public override byte GetByte(int ordinal) => throw new NotSupportedException();
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
    public override char GetChar(int ordinal) => throw new NotSupportedException();
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
    public override float GetFloat(int ordinal) => throw new NotSupportedException();
    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
    public override short GetInt16(int ordinal) => throw new NotSupportedException();
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}
