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
    /// <summary>Number of columns in PiXL.Parsed (column_id 1–230).</summary>
    internal const int ColumnCount = 230;

    /// <summary>
    /// Column names in PiXL.Parsed column_id order (1–230 → index 0–229).
    /// Used for <see cref="Microsoft.Data.SqlClient.SqlBulkCopy"/> column mappings.
    /// </summary>
    internal static readonly string[] ColumnNames =
    [
        // ── Identifiers (cols 1–8) ─────────────────────────────────────
        "SourceId",             // 0  — bigint   ← Raw.Id
        "CompanyID",            // 1  — varchar  ← Raw.CompanyID
        "PiXLID",               // 2  — varchar  ← Raw.PiXLID
        "IPAddress",            // 3  — varchar  ← Raw.IPAddress
        "ReceivedAt",           // 4  — datetime2 ← Raw.ReceivedAt
        "RequestPath",          // 5  — varchar  ← Raw.RequestPath
        "ServerUserAgent",      // 6  — varchar  ← Raw.UserAgent
        "ServerReferer",        // 7  — varchar  ← Raw.Referer

        // ── Phase 1: Screen + Locale (cols 9–32) ───────────────────────
        "IsSynthetic",          // 8   — bit     ← 'synthetic'
        "Tier",                 // 9   — int     ← 'tier'
        "ScreenWidth",          // 10  — int     ← 'sw'
        "ScreenHeight",         // 11  — int     ← 'sh'
        "ScreenAvailWidth",     // 12  — int     ← 'saw'
        "ScreenAvailHeight",    // 13  — int     ← 'sah'
        "ViewportWidth",        // 14  — int     ← 'vw'
        "ViewportHeight",       // 15  — int     ← 'vh'
        "OuterWidth",           // 16  — int     ← 'ow'
        "OuterHeight",          // 17  — int     ← 'oh'
        "ScreenX",              // 18  — int     ← 'sx'
        "ScreenY",              // 19  — int     ← 'sy'
        "ColorDepth",           // 20  — int     ← 'cd'
        "PixelRatio",           // 21  — dec(5,2) ← 'pd'
        "ScreenOrientation",    // 22  — varchar ← 'ori'
        "Timezone",             // 23  — varchar ← 'tz'
        "TimezoneOffsetMins",   // 24  — int     ← 'tzo'
        "ClientTimestampMs",    // 25  — bigint  ← 'ts'
        "TimezoneLocale",       // 26  — varchar ← 'tzLocale'
        "DateFormatSample",     // 27  — varchar ← 'dateFormat'
        "NumberFormatSample",   // 28  — varchar ← 'numberFormat'
        "RelativeTimeSample",   // 29  — varchar ← 'relativeTime'
        "Language",             // 30  — varchar ← 'lang'
        "LanguageList",         // 31  — varchar ← 'langs'

        // ── Phase 2: Browser + GPU + Fingerprints (cols 33–58) ─────────
        "Platform",             // 32  — varchar ← 'plt'
        "Vendor",               // 33  — varchar ← 'vnd'
        "ClientUserAgent",      // 34  — varchar ← 'ua'
        "HardwareConcurrency",  // 35  — int     ← 'cores'
        "DeviceMemoryGB",       // 36  — dec(5,2) ← 'mem'
        "MaxTouchPoints",       // 37  — int     ← 'touch'
        "NavigatorProduct",     // 38  — varchar ← 'product'
        "NavigatorProductSub",  // 39  — varchar ← 'productSub'
        "NavigatorVendorSub",   // 40  — varchar ← 'vendorSub'
        "AppName",              // 41  — varchar ← 'appName'
        "AppVersion",           // 42  — varchar ← 'appVersion'
        "AppCodeName",          // 43  — varchar ← 'appCodeName'
        "GPURenderer",          // 44  — varchar ← 'gpu'
        "GPUVendor",            // 45  — varchar ← 'gpuVendor'
        "WebGLParameters",      // 46  — varchar ← 'webglParams'
        "WebGLExtensionCount",  // 47  — int     ← 'webglExt'
        "WebGLSupported",       // 48  — bit     ← 'webgl'
        "WebGL2Supported",      // 49  — bit     ← 'webgl2'
        "CanvasFingerprint",    // 50  — varchar ← 'canvasFP'
        "WebGLFingerprint",     // 51  — varchar ← 'webglFP'
        "AudioFingerprintSum",  // 52  — varchar ← 'audioFP'
        "AudioFingerprintHash", // 53  — varchar ← 'audioHash'
        "MathFingerprint",      // 54  — varchar ← 'mathFP'
        "ErrorFingerprint",     // 55  — varchar ← 'errorFP'
        "CSSFontVariantHash",   // 56  — varchar ← 'cssFontVariant'
        "DetectedFonts",        // 57  — varchar ← 'fonts'

        // ── Phase 3: Plugins + Network + Storage (cols 59–87) ──────────
        "PluginCount",          // 58  — int     ← 'plugins'
        "PluginListDetailed",   // 59  — varchar ← 'pluginList'
        "MimeTypeCount",        // 60  — int     ← 'mimeTypes'
        "MimeTypeList",         // 61  — varchar ← 'mimeList'
        "SpeechVoices",         // 62  — varchar ← 'voices'
        "ConnectedGamepads",    // 63  — varchar ← 'gamepads'
        "WebRTCLocalIP",        // 64  — varchar ← 'localIp'
        "ConnectionType",       // 65  — varchar ← 'conn'
        "DownlinkMbps",         // 66  — dec(10,2) ← 'dl'
        "DownlinkMax",          // 67  — varchar ← 'dlMax'
        "RTTMs",                // 68  — int     ← 'rtt'
        "DataSaverEnabled",     // 69  — bit     ← 'save'
        "NetworkType",          // 70  — varchar ← 'connType'
        "IsOnline",             // 71  — bit     ← 'online'
        "StorageQuotaGB",       // 72  — int     ← 'storageQuota'
        "StorageUsedMB",        // 73  — int     ← 'storageUsed'
        "LocalStorageSupported",    // 74  — bit  ← 'ls'
        "SessionStorageSupported",  // 75  — bit  ← 'ss'
        "IndexedDBSupported",       // 76  — bit  ← 'idb'
        "CacheAPISupported",        // 77  — bit  ← 'caches'
        "BatteryLevelPct",      // 78  — int     ← 'batteryLevel'
        "BatteryCharging",      // 79  — bit     ← 'batteryCharging'
        "AudioInputDevices",    // 80  — int     ← 'audioInputs'
        "VideoInputDevices",    // 81  — int     ← 'videoInputs'
        "CookiesEnabled",       // 82  — bit     ← 'ck'
        "DoNotTrack",           // 83  — varchar ← 'dnt'
        "PDFViewerEnabled",     // 84  — bit     ← 'pdf'
        "WebDriverDetected",    // 85  — bit     ← 'webdr'
        "JavaEnabled",          // 86  — bit     ← 'java'

        // ── Phase 4: Capabilities + Preferences + Document (cols 88–111) ─
        "CanvasSupported",      // 87  — bit     ← 'canvas'
        "WebAssemblySupported", // 88  — bit     ← 'wasm'
        "WebWorkersSupported",  // 89  — bit     ← 'ww'
        "ServiceWorkerSupported",   // 90  — bit ← 'swk'
        "MediaDevicesAPISupported", // 91  — bit ← 'mediaDevices'
        "ClipboardAPISupported",    // 92  — bit ← 'clipboard'
        "SpeechSynthesisSupported", // 93  — bit ← 'speechSynth'
        "TouchEventsSupported",     // 94  — bit ← 'touchEvent'
        "PointerEventsSupported",   // 95  — bit ← 'pointerEvent'
        "HoverCapable",         // 96  — bit     ← 'hover'
        "PointerType",          // 97  — varchar ← 'pointer'
        "PrefersColorSchemeDark",   // 98  — bit ← 'darkMode'
        "PrefersColorSchemeLight",  // 99  — bit ← 'lightMode'
        "PrefersReducedMotion",     // 100 — bit ← 'reducedMotion'
        "PrefersReducedData",       // 101 — bit ← 'reducedData'
        "PrefersHighContrast",      // 102 — bit ← 'contrast'
        "ForcedColorsActive",       // 103 — bit ← 'forcedColors'
        "InvertedColorsActive",     // 104 — bit ← 'invertedColors'
        "StandaloneDisplayMode",    // 105 — bit ← 'standalone'
        "DocumentCharset",      // 106 — varchar ← 'docCharset'
        "DocumentCompatMode",   // 107 — varchar ← 'docCompat'
        "DocumentReadyState",   // 108 — varchar ← 'docReady'
        "DocumentHidden",       // 109 — bit     ← 'docHidden'
        "DocumentVisibility",   // 110 — varchar ← 'docVisibility'

        // ── Phase 5: Page + Performance + Bot (cols 112–129) ───────────
        "PageURL",              // 111 — varchar ← 'url'
        "PageReferrer",         // 112 — varchar ← 'ref'
        "PageTitle",            // 113 — varchar ← 'title'
        "PageDomain",           // 114 — varchar ← 'domain'
        "PagePath",             // 115 — varchar ← 'path'
        "PageHash",             // 116 — varchar ← 'hash'
        "PageProtocol",         // 117 — varchar ← 'protocol'
        "HistoryLength",        // 118 — int     ← 'hist'
        "PageLoadTimeMs",       // 119 — int     ← 'loadTime'
        "DOMReadyTimeMs",       // 120 — int     ← 'domTime'
        "DNSLookupMs",          // 121 — int     ← 'dnsTime'
        "TCPConnectMs",         // 122 — int     ← 'tcpTime'
        "TimeToFirstByteMs",    // 123 — int     ← 'ttfb'
        "BotSignalsList",       // 124 — varchar ← 'botSignals'
        "BotScore",             // 125 — int     ← 'botScore'
        "CombinedThreatScore",  // 126 — int     ← 'combinedThreatScore'
        "ScriptExecutionTimeMs",// 127 — int     ← 'scriptExecTime'
        "BotPermissionInconsistent", // 128 — bit ← 'botPermInconsistent'

        // ── Phase 6: Evasion + Client Hints + Browser-specific (cols 130–153) ─
        "CanvasEvasionDetected",    // 129 — bit ← 'canvasEvasion'
        "WebGLEvasionDetected",     // 130 — bit ← 'webglEvasion'
        "EvasionToolsDetected",     // 131 — varchar ← 'evasionDetected'
        "ProxyBlockedProperties",   // 132 — varchar ← '_proxyBlocked'
        "UA_Architecture",      // 133 — varchar ← 'uaArch'
        "UA_Bitness",           // 134 — varchar ← 'uaBitness'
        "UA_Model",             // 135 — varchar ← 'uaModel'
        "UA_PlatformVersion",   // 136 — varchar ← 'uaPlatformVersion'
        "UA_FullVersionList",   // 137 — varchar ← 'uaFullVersion'
        "UA_IsWow64",           // 138 — bit     ← 'uaWow64'
        "UA_IsMobile",          // 139 — bit     ← 'uaMobile'
        "UA_Platform",          // 140 — varchar ← 'uaPlatform'
        "UA_Brands",            // 141 — varchar ← 'uaBrands'
        "UA_FormFactor",        // 142 — varchar ← 'uaFormFactor'
        "Firefox_OSCPU",        // 143 — varchar ← 'oscpu'
        "Firefox_BuildID",      // 144 — varchar ← 'buildID'
        "Chrome_ObjectPresent", // 145 — bit     ← 'chromeObj'
        "Chrome_RuntimePresent",// 146 — bit     ← 'chromeRuntime'
        "Chrome_JSHeapSizeLimit",   // 147 — bigint ← 'jsHeapLimit'
        "Chrome_TotalJSHeapSize",   // 148 — bigint ← 'jsHeapTotal'
        "Chrome_UsedJSHeapSize",    // 149 — bigint ← 'jsHeapUsed'
        "CanvasConsistency",    // 150 — varchar ← 'canvasConsistency'
        "AudioIsStable",        // 151 — bit     ← 'audioStable'
        "AudioNoiseInjectionDetected", // 152 — bit ← 'audioNoiseDetected'

        // ── Phase 7: Behavioral + Cross-signal (cols 154–167) ──────────
        "MouseMoveCount",       // 153 — int     ← 'mouseMoves'
        "UserScrolled",         // 154 — bit     ← 'scrolled'
        "ScrollDepthPx",        // 155 — int     ← 'scrollY'
        "MouseEntropy",         // 156 — int     ← 'mouseEntropy'
        "ScrollContradiction",  // 157 — bit     ← 'scrollContradiction'
        "MoveTimingCV",         // 158 — int     ← 'moveTimingCV'
        "MoveSpeedCV",          // 159 — int     ← 'moveSpeedCV'
        "MoveCountBucket",      // 160 — varchar ← 'moveCountBucket'
        "BehavioralFlags",      // 161 — varchar ← 'behavioralFlags'
        "StealthPluginSignals", // 162 — varchar ← 'stealthSignals'
        "FontMethodMismatch",   // 163 — bit     ← 'fontMethodMismatch'
        "EvasionSignalsV2",     // 164 — varchar ← 'evasionSignalsV2'
        "CrossSignalFlags",     // 165 — varchar ← 'crossSignals'
        "AnomalyScore",         // 166 — int     ← 'anomalyScore'

        // ── ParsedAt (col 168) ─────────────────────────────────────────
        "ParsedAt",             // 167 — datetime2 ← SYSUTCDATETIME()

        // ── Phase 8: Server-side IP behavior (cols 169–175) ────────────
        "Srv_SubnetIps",        // 168 — int     ← '_srv_subnetIps'
        "Srv_SubnetHits",       // 169 — int     ← '_srv_subnetHits'
        "Srv_HitsIn15s",        // 170 — int     ← '_srv_hitsIn15s'
        "Srv_LastGapMs",        // 171 — bigint  ← '_srv_lastGapMs'
        "Srv_SubSecDupe",       // 172 — bit     ← '_srv_subSecDupe'
        "Srv_SubnetAlert",      // 173 — bit     ← '_srv_subnetAlert'
        "Srv_RapidFire",        // 174 — bit     ← '_srv_rapidFire'

        // ── Geo fields (cols 176–185) — filled by usp_EnrichParsedGeo ──
        "GeoCountry",           // 175 — varchar (NULL — filled later)
        "GeoCountryCode",       // 176 — varchar (NULL — filled later)
        "GeoRegion",            // 177 — varchar (NULL — filled later)
        "GeoCity",              // 178 — varchar (NULL — filled later)
        "GeoZip",               // 179 — varchar (NULL — filled later)
        "GeoLat",               // 180 — decimal (NULL — filled later)
        "GeoLon",               // 181 — decimal (NULL — filled later)
        "GeoTimezone",          // 182 — varchar (NULL — filled later)
        "GeoISP",               // 183 — varchar (NULL — filled later)
        "GeoTzMismatch",        // 184 — bit     (NULL — filled later)

        // ── Phase 1 extras (cols 186–188) ──────────────────────────────
        "HitType",              // 185 — varchar ← '_srv_hitType' (default 'modern')
        "ScreenExtended",       // 186 — bit     ← 'screenExtended'
        "MousePath",            // 187 — varchar ← 'mousePath'

        // ── Phase 8B: Forge Tier 1 enrichment (cols 189–208) ───────────
        "KnownBot",             // 188 — bit     ← '_srv_knownBot'
        "BotName",              // 189 — varchar ← '_srv_botName'
        "ParsedBrowser",        // 190 — varchar ← '_srv_browser'
        "ParsedBrowserVersion", // 191 — varchar ← '_srv_browserVer'
        "ParsedOS",             // 192 — varchar ← '_srv_os'
        "ParsedOSVersion",      // 193 — varchar ← '_srv_osVer'
        "ParsedDeviceType",     // 194 — varchar ← '_srv_deviceType'
        "ParsedDeviceModel",    // 195 — varchar ← '_srv_deviceModel'
        "ParsedDeviceBrand",    // 196 — varchar ← '_srv_deviceBrand'
        "ReverseDNS",           // 197 — varchar ← '_srv_rdns'
        "ReverseDNSCloud",      // 198 — bit     ← '_srv_rdnsCloud'
        "MaxMindCountry",       // 199 — char(2) ← '_srv_mmCC'
        "MaxMindRegion",        // 200 — varchar ← '_srv_mmReg'
        "MaxMindCity",          // 201 — varchar ← '_srv_mmCity'
        "MaxMindLat",           // 202 — dec(9,6) ← '_srv_mmLat'
        "MaxMindLon",           // 203 — dec(9,6) ← '_srv_mmLon'
        "MaxMindASN",           // 204 — int     ← '_srv_mmASN'
        "MaxMindASNOrg",        // 205 — varchar ← '_srv_mmASNOrg'
        "WhoisASN",             // 206 — varchar ← '_srv_whoisASN'
        "WhoisOrg",             // 207 — varchar ← '_srv_whoisOrg'

        // ── Phase 8C: Forge Tier 2 enrichment (cols 209–217) ───────────
        "CrossCustomerHits",    // 208 — int     ← '_srv_crossCustHits'
        "CrossCustomerAlert",   // 209 — bit     ← '_srv_crossCustAlert'
        "LeadQualityScore",     // 210 — int     ← '_srv_leadScore'
        "SessionId",            // 211 — varchar ← '_srv_sessionId'
        "SessionHitNumber",     // 212 — int     ← '_srv_sessionHitNum'
        "SessionDurationSec",   // 213 — int     ← '_srv_sessionDurationSec'
        "SessionPageCount",     // 214 — int     ← '_srv_sessionPages'
        "AffluenceSignal",      // 215 — varchar ← '_srv_affluence'
        "GpuTier",              // 216 — varchar ← '_srv_gpuTier'

        // ── Phase 8D: Forge Tier 3 enrichment (cols 218–226) ───────────
        "ContradictionCount",       // 217 — int     ← '_srv_contradictions'
        "ContradictionList",        // 218 — varchar ← '_srv_contradictionList'
        "CulturalConsistencyScore", // 219 — int     ← '_srv_culturalScore'
        "CulturalFlags",            // 220 — varchar ← '_srv_culturalFlags'
        "DeviceAgeYears",           // 221 — int     ← '_srv_deviceAge'
        "DeviceAgeAnomaly",         // 222 — bit     ← '_srv_deviceAgeAnomaly'
        "ReplayDetected",           // 223 — bit     ← '_srv_replayDetected'
        "ReplayMatchFingerprint",   // 224 — varchar ← '_srv_replayMatchFP'
        "DeadInternetIndex",        // 225 — int     ← '_srv_deadInternetIdx'

        // ── Bitmap fields (cols 227–230) — computed elsewhere ──────────
        "FeatureBitmapValue",       // 226 — int (NULL — computed)
        "AccessibilityBitmapValue", // 227 — int (NULL — computed)
        "BotBitmapValue",           // 228 — int (NULL — computed)
        "EvasionBitmapValue",       // 229 — int (NULL — computed)
    ];

    /// <summary>
    /// Parses a single PiXL.Raw record into a 230-element value array for PiXL.Parsed.
    /// Every assignment maps directly to an ETL proc phase. QueryString is parsed
    /// using span-based <see cref="QueryParamReader"/> — zero-allocation for numeric reads.
    /// </summary>
    /// <param name="sourceId">PiXL.Raw.Id (known for backfill, query-back for new traffic).</param>
    /// <param name="companyId">PiXL.Raw.CompanyID</param>
    /// <param name="pixlId">PiXL.Raw.PiXLID</param>
    /// <param name="ipAddress">PiXL.Raw.IPAddress</param>
    /// <param name="receivedAt">PiXL.Raw.ReceivedAt</param>
    /// <param name="requestPath">PiXL.Raw.RequestPath</param>
    /// <param name="userAgent">PiXL.Raw.UserAgent</param>
    /// <param name="referer">PiXL.Raw.Referer</param>
    /// <param name="queryString">PiXL.Raw.QueryString — the raw QS with all params.</param>
    internal static object?[] Parse(
        long sourceId, string? companyId, string? pixlId, string? ipAddress,
        DateTime receivedAt, string? requestPath, string? userAgent, string? referer,
        string? queryString)
    {
        var v = new object?[ColumnCount];
        var qs = queryString;

        // ════════════════════════════════════════════════════════════════
        // IDENTIFIERS — Direct from PiXL.Raw (no parsing)
        // ════════════════════════════════════════════════════════════════
        v[0]  = sourceId;                                       // SourceId
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
        v[9]  = QsInt(qs, "tier");                              // Tier
        v[10] = QsInt(qs, "sw");                                // ScreenWidth
        v[11] = QsInt(qs, "sh");                                // ScreenHeight
        v[12] = QsInt(qs, "saw");                               // ScreenAvailWidth
        v[13] = QsInt(qs, "sah");                               // ScreenAvailHeight
        v[14] = QsInt(qs, "vw");                                // ViewportWidth
        v[15] = QsInt(qs, "vh");                                // ViewportHeight
        v[16] = QsInt(qs, "ow");                                // OuterWidth
        v[17] = QsInt(qs, "oh");                                // OuterHeight
        v[18] = QsInt(qs, "sx");                                // ScreenX
        v[19] = QsInt(qs, "sy");                                // ScreenY
        v[20] = QsInt(qs, "cd");                                // ColorDepth
        v[21] = QsDec(qs, "pd");                                // PixelRatio
        v[22] = QsStr(qs, "ori");                               // ScreenOrientation
        v[23] = QsStr(qs, "tz");                                // Timezone
        v[24] = QsInt(qs, "tzo");                               // TimezoneOffsetMins
        v[25] = QsLong(qs, "ts");                               // ClientTimestampMs
        v[26] = QsStr(qs, "tzLocale");                          // TimezoneLocale
        v[27] = QsStr(qs, "dateFormat");                        // DateFormatSample
        v[28] = QsStr(qs, "numberFormat");                      // NumberFormatSample
        v[29] = QsStr(qs, "relativeTime");                      // RelativeTimeSample
        v[30] = QsStr(qs, "lang");                              // Language
        v[31] = QsStr(qs, "langs");                             // LanguageList

        // ════════════════════════════════════════════════════════════════
        // PHASE 2 — Browser + GPU + Fingerprints (ETL proc Phase 2 UPDATE)
        // ════════════════════════════════════════════════════════════════
        v[32] = QsStr(qs, "plt");                               // Platform
        v[33] = QsStr(qs, "vnd");                               // Vendor
        v[34] = QsStr(qs, "ua");                                // ClientUserAgent
        v[35] = QsInt(qs, "cores");                             // HardwareConcurrency
        v[36] = QsDec(qs, "mem");                               // DeviceMemoryGB
        v[37] = QsInt(qs, "touch");                             // MaxTouchPoints
        v[38] = QsStr(qs, "product");                           // NavigatorProduct
        v[39] = QsStr(qs, "productSub");                        // NavigatorProductSub
        v[40] = QsStr(qs, "vendorSub");                         // NavigatorVendorSub
        v[41] = QsStr(qs, "appName");                           // AppName
        v[42] = QsStr(qs, "appVersion");                        // AppVersion
        v[43] = QsStr(qs, "appCodeName");                       // AppCodeName
        v[44] = QsStr(qs, "gpu");                               // GPURenderer
        v[45] = QsStr(qs, "gpuVendor");                         // GPUVendor
        v[46] = QsStr(qs, "webglParams");                       // WebGLParameters
        v[47] = QsInt(qs, "webglExt");                          // WebGLExtensionCount
        v[48] = QsBit(qs, "webgl");                             // WebGLSupported
        v[49] = QsBit(qs, "webgl2");                            // WebGL2Supported
        v[50] = QsStr(qs, "canvasFP");                          // CanvasFingerprint
        v[51] = QsStr(qs, "webglFP");                           // WebGLFingerprint
        v[52] = QsStr(qs, "audioFP");                           // AudioFingerprintSum
        v[53] = QsStr(qs, "audioHash");                         // AudioFingerprintHash
        v[54] = QsStr(qs, "mathFP");                            // MathFingerprint
        v[55] = QsStr(qs, "errorFP");                           // ErrorFingerprint
        v[56] = QsStr(qs, "cssFontVariant");                    // CSSFontVariantHash
        v[57] = QsStr(qs, "fonts");                             // DetectedFonts

        // ════════════════════════════════════════════════════════════════
        // PHASE 3 — Plugins + Network + Storage + Capabilities (ETL Phase 3)
        // ════════════════════════════════════════════════════════════════
        v[58] = QsInt(qs, "plugins");                           // PluginCount
        v[59] = QsStr(qs, "pluginList");                        // PluginListDetailed
        v[60] = QsInt(qs, "mimeTypes");                         // MimeTypeCount
        v[61] = QsStr(qs, "mimeList");                          // MimeTypeList
        v[62] = QsStr(qs, "voices");                            // SpeechVoices
        v[63] = QsStr(qs, "gamepads");                          // ConnectedGamepads
        v[64] = QsStr(qs, "localIp");                           // WebRTCLocalIP
        v[65] = QsStr(qs, "conn");                              // ConnectionType
        v[66] = QsDec(qs, "dl");                                // DownlinkMbps
        v[67] = QsStr(qs, "dlMax");                             // DownlinkMax
        v[68] = QsInt(qs, "rtt");                               // RTTMs
        v[69] = QsBit(qs, "save");                              // DataSaverEnabled
        v[70] = QsStr(qs, "connType");                          // NetworkType
        v[71] = QsBit(qs, "online");                            // IsOnline
        v[72] = QsInt(qs, "storageQuota");                      // StorageQuotaGB
        v[73] = QsInt(qs, "storageUsed");                       // StorageUsedMB
        v[74] = QsBit(qs, "ls");                                // LocalStorageSupported
        v[75] = QsBit(qs, "ss");                                // SessionStorageSupported
        v[76] = QsBit(qs, "idb");                               // IndexedDBSupported
        v[77] = QsBit(qs, "caches");                            // CacheAPISupported
        v[78] = QsInt(qs, "batteryLevel");                      // BatteryLevelPct
        v[79] = QsBit(qs, "batteryCharging");                   // BatteryCharging
        v[80] = QsInt(qs, "audioInputs");                       // AudioInputDevices
        v[81] = QsInt(qs, "videoInputs");                       // VideoInputDevices
        v[82] = QsBit(qs, "ck");                                // CookiesEnabled
        v[83] = QsStr(qs, "dnt");                               // DoNotTrack
        v[84] = QsBit(qs, "pdf");                               // PDFViewerEnabled
        v[85] = QsBit(qs, "webdr");                             // WebDriverDetected
        v[86] = QsBit(qs, "java");                              // JavaEnabled

        // ════════════════════════════════════════════════════════════════
        // PHASE 4 — Capabilities2 + Preferences + Document (ETL Phase 4)
        // ════════════════════════════════════════════════════════════════
        v[87]  = QsBit(qs, "canvas");                           // CanvasSupported
        v[88]  = QsBit(qs, "wasm");                             // WebAssemblySupported
        v[89]  = QsBit(qs, "ww");                               // WebWorkersSupported
        v[90]  = QsBit(qs, "swk");                              // ServiceWorkerSupported
        v[91]  = QsBit(qs, "mediaDevices");                     // MediaDevicesAPISupported
        v[92]  = QsBit(qs, "clipboard");                        // ClipboardAPISupported
        v[93]  = QsBit(qs, "speechSynth");                      // SpeechSynthesisSupported
        v[94]  = QsBit(qs, "touchEvent");                       // TouchEventsSupported
        v[95]  = QsBit(qs, "pointerEvent");                     // PointerEventsSupported
        v[96]  = QsBit(qs, "hover");                            // HoverCapable
        v[97]  = QsStr(qs, "pointer");                          // PointerType
        v[98]  = QsBit(qs, "darkMode");                         // PrefersColorSchemeDark
        v[99]  = QsBit(qs, "lightMode");                        // PrefersColorSchemeLight
        v[100] = QsBit(qs, "reducedMotion");                    // PrefersReducedMotion
        v[101] = QsBit(qs, "reducedData");                      // PrefersReducedData
        v[102] = QsBit(qs, "contrast");                         // PrefersHighContrast
        v[103] = QsBit(qs, "forcedColors");                     // ForcedColorsActive
        v[104] = QsBit(qs, "invertedColors");                   // InvertedColorsActive
        v[105] = QsBit(qs, "standalone");                       // StandaloneDisplayMode
        v[106] = QsStr(qs, "docCharset");                       // DocumentCharset
        v[107] = QsStr(qs, "docCompat");                        // DocumentCompatMode
        v[108] = QsStr(qs, "docReady");                         // DocumentReadyState
        v[109] = QsBit(qs, "docHidden");                        // DocumentHidden
        v[110] = QsStr(qs, "docVisibility");                    // DocumentVisibility

        // ════════════════════════════════════════════════════════════════
        // PHASE 5 — Page + Performance + Bot (ETL Phase 5)
        // ════════════════════════════════════════════════════════════════
        v[111] = QsStr(qs, "url");                              // PageURL
        v[112] = QsStr(qs, "ref");                              // PageReferrer
        v[113] = QsStr(qs, "title");                            // PageTitle
        v[114] = QsStr(qs, "domain");                           // PageDomain
        v[115] = QsStr(qs, "path");                             // PagePath
        v[116] = QsStr(qs, "hash");                             // PageHash
        v[117] = QsStr(qs, "protocol");                         // PageProtocol
        v[118] = QsInt(qs, "hist");                             // HistoryLength
        v[119] = QsInt(qs, "loadTime");                         // PageLoadTimeMs
        v[120] = QsInt(qs, "domTime");                          // DOMReadyTimeMs
        v[121] = QsInt(qs, "dnsTime");                          // DNSLookupMs
        v[122] = QsInt(qs, "tcpTime");                          // TCPConnectMs
        v[123] = QsInt(qs, "ttfb");                             // TimeToFirstByteMs
        v[124] = QsStr(qs, "botSignals");                       // BotSignalsList
        v[125] = QsInt(qs, "botScore");                         // BotScore
        v[126] = QsInt(qs, "combinedThreatScore");              // CombinedThreatScore
        v[127] = QsInt(qs, "scriptExecTime");                   // ScriptExecutionTimeMs
        v[128] = QsBit(qs, "botPermInconsistent");              // BotPermissionInconsistent

        // ════════════════════════════════════════════════════════════════
        // PHASE 6 — Evasion + Client Hints + Browser-specific (ETL Phase 6)
        // ════════════════════════════════════════════════════════════════
        v[129] = QsBit(qs, "canvasEvasion");                    // CanvasEvasionDetected
        v[130] = QsBit(qs, "webglEvasion");                     // WebGLEvasionDetected
        v[131] = QsStr(qs, "evasionDetected");                  // EvasionToolsDetected
        v[132] = QsStr(qs, "_proxyBlocked");                    // ProxyBlockedProperties
        v[133] = QsStr(qs, "uaArch");                           // UA_Architecture
        v[134] = QsStr(qs, "uaBitness");                        // UA_Bitness
        v[135] = QsStr(qs, "uaModel");                          // UA_Model
        v[136] = QsStr(qs, "uaPlatformVersion");                // UA_PlatformVersion
        v[137] = QsStr(qs, "uaFullVersion");                    // UA_FullVersionList
        v[138] = QsBit(qs, "uaWow64");                          // UA_IsWow64
        v[139] = QsBit(qs, "uaMobile");                         // UA_IsMobile
        v[140] = QsStr(qs, "uaPlatform");                       // UA_Platform
        v[141] = QsStr(qs, "uaBrands");                         // UA_Brands
        v[142] = QsStr(qs, "uaFormFactor");                     // UA_FormFactor
        v[143] = QsStr(qs, "oscpu");                            // Firefox_OSCPU
        v[144] = QsStr(qs, "buildID");                          // Firefox_BuildID
        v[145] = QsBit(qs, "chromeObj");                        // Chrome_ObjectPresent
        v[146] = QsBit(qs, "chromeRuntime");                    // Chrome_RuntimePresent
        v[147] = QsLong(qs, "jsHeapLimit");                     // Chrome_JSHeapSizeLimit
        v[148] = QsLong(qs, "jsHeapTotal");                     // Chrome_TotalJSHeapSize
        v[149] = QsLong(qs, "jsHeapUsed");                      // Chrome_UsedJSHeapSize
        v[150] = QsStr(qs, "canvasConsistency");                // CanvasConsistency
        v[151] = QsBit(qs, "audioStable");                      // AudioIsStable
        v[152] = QsBit(qs, "audioNoiseDetected");               // AudioNoiseInjectionDetected

        // ════════════════════════════════════════════════════════════════
        // PHASE 7 — Behavioral + Cross-signal (ETL Phase 7)
        // ════════════════════════════════════════════════════════════════
        v[153] = QsInt(qs, "mouseMoves");                       // MouseMoveCount
        v[154] = QsBit(qs, "scrolled");                         // UserScrolled
        v[155] = QsInt(qs, "scrollY");                          // ScrollDepthPx
        v[156] = QsInt(qs, "mouseEntropy");                     // MouseEntropy
        v[157] = QsBit(qs, "scrollContradiction");              // ScrollContradiction
        v[158] = QsInt(qs, "moveTimingCV");                     // MoveTimingCV
        v[159] = QsInt(qs, "moveSpeedCV");                      // MoveSpeedCV
        v[160] = QsStr(qs, "moveCountBucket");                  // MoveCountBucket
        v[161] = QsStr(qs, "behavioralFlags");                  // BehavioralFlags
        v[162] = QsStr(qs, "stealthSignals");                   // StealthPluginSignals
        v[163] = QsBit(qs, "fontMethodMismatch");               // FontMethodMismatch
        v[164] = QsStr(qs, "evasionSignalsV2");                 // EvasionSignalsV2
        v[165] = QsStr(qs, "crossSignals");                     // CrossSignalFlags
        v[166] = QsInt(qs, "anomalyScore");                     // AnomalyScore

        // ════════════════════════════════════════════════════════════════
        // PARSED TIMESTAMP
        // ════════════════════════════════════════════════════════════════
        v[167] = DateTime.UtcNow;                               // ParsedAt

        // ════════════════════════════════════════════════════════════════
        // PHASE 8 — Server-side IP behavior signals
        // ════════════════════════════════════════════════════════════════
        v[168] = QsInt(qs,  "_srv_subnetIps");                  // Srv_SubnetIps
        v[169] = QsInt(qs,  "_srv_subnetHits");                 // Srv_SubnetHits
        v[170] = QsInt(qs,  "_srv_hitsIn15s");                  // Srv_HitsIn15s
        v[171] = QsLong(qs, "_srv_lastGapMs");                  // Srv_LastGapMs
        v[172] = QsBit(qs,  "_srv_subSecDupe");                 // Srv_SubSecDupe
        v[173] = QsBit(qs,  "_srv_subnetAlert");                // Srv_SubnetAlert
        v[174] = QsBit(qs,  "_srv_rapidFire");                  // Srv_RapidFire

        // ════════════════════════════════════════════════════════════════
        // GEO FIELDS — Left NULL, filled by usp_EnrichParsedGeo later
        // ════════════════════════════════════════════════════════════════
        v[175] = DBNull.Value;  // GeoCountry
        v[176] = DBNull.Value;  // GeoCountryCode
        v[177] = DBNull.Value;  // GeoRegion
        v[178] = DBNull.Value;  // GeoCity
        v[179] = DBNull.Value;  // GeoZip
        v[180] = DBNull.Value;  // GeoLat
        v[181] = DBNull.Value;  // GeoLon
        v[182] = DBNull.Value;  // GeoTimezone
        v[183] = DBNull.Value;  // GeoISP
        v[184] = DBNull.Value;  // GeoTzMismatch

        // ════════════════════════════════════════════════════════════════
        // PHASE 1 EXTRAS — Placed after Geo in schema but parsed in Phase 1
        // ════════════════════════════════════════════════════════════════
        // HitType — COALESCE(GetQueryParam(qs, '_srv_hitType'), 'modern')
        v[185] = (object?)Qs(qs, "_srv_hitType") ?? "modern";   // HitType
        v[186] = QsBit(qs, "screenExtended");                   // ScreenExtended
        v[187] = QsStr(qs, "mousePath");                        // MousePath

        // ════════════════════════════════════════════════════════════════
        // PHASE 8B — Forge Tier 1 enrichment (_srv_* params)
        // ════════════════════════════════════════════════════════════════
        v[188] = QsBit(qs, "_srv_knownBot");                    // KnownBot
        v[189] = QsStr(qs, "_srv_botName");                     // BotName
        v[190] = QsStr(qs, "_srv_browser");                     // ParsedBrowser
        v[191] = QsStr(qs, "_srv_browserVer");                  // ParsedBrowserVersion
        v[192] = QsStr(qs, "_srv_os");                          // ParsedOS
        v[193] = QsStr(qs, "_srv_osVer");                       // ParsedOSVersion
        v[194] = QsStr(qs, "_srv_deviceType");                  // ParsedDeviceType
        v[195] = QsStr(qs, "_srv_deviceModel");                 // ParsedDeviceModel
        v[196] = QsStr(qs, "_srv_deviceBrand");                 // ParsedDeviceBrand
        v[197] = QsStr(qs, "_srv_rdns");                        // ReverseDNS
        v[198] = QsBit(qs, "_srv_rdnsCloud");                   // ReverseDNSCloud
        v[199] = QsStr(qs, "_srv_mmCC");                        // MaxMindCountry
        v[200] = QsStr(qs, "_srv_mmReg");                       // MaxMindRegion
        v[201] = QsStr(qs, "_srv_mmCity");                      // MaxMindCity
        v[202] = QsDec(qs, "_srv_mmLat");                       // MaxMindLat
        v[203] = QsDec(qs, "_srv_mmLon");                       // MaxMindLon
        v[204] = QsInt(qs, "_srv_mmASN");                       // MaxMindASN
        v[205] = QsStr(qs, "_srv_mmASNOrg");                    // MaxMindASNOrg
        v[206] = QsStr(qs, "_srv_whoisASN");                    // WhoisASN
        v[207] = QsStr(qs, "_srv_whoisOrg");                    // WhoisOrg

        // ════════════════════════════════════════════════════════════════
        // PHASE 8C — Forge Tier 2 enrichment
        // ════════════════════════════════════════════════════════════════
        v[208] = QsInt(qs, "_srv_crossCustHits");               // CrossCustomerHits
        v[209] = QsBit(qs, "_srv_crossCustAlert");              // CrossCustomerAlert
        v[210] = QsInt(qs, "_srv_leadScore");                   // LeadQualityScore
        v[211] = QsStr(qs, "_srv_sessionId");                   // SessionId
        v[212] = QsInt(qs, "_srv_sessionHitNum");               // SessionHitNumber
        v[213] = QsInt(qs, "_srv_sessionDurationSec");          // SessionDurationSec
        v[214] = QsInt(qs, "_srv_sessionPages");                // SessionPageCount
        v[215] = QsStr(qs, "_srv_affluence");                   // AffluenceSignal
        v[216] = QsStr(qs, "_srv_gpuTier");                     // GpuTier

        // ════════════════════════════════════════════════════════════════
        // PHASE 8D — Forge Tier 3 enrichment (Asymmetric Detection)
        // ════════════════════════════════════════════════════════════════
        v[217] = QsInt(qs, "_srv_contradictions");              // ContradictionCount
        v[218] = QsStr(qs, "_srv_contradictionList");           // ContradictionList
        v[219] = QsInt(qs, "_srv_culturalScore");               // CulturalConsistencyScore
        v[220] = QsStr(qs, "_srv_culturalFlags");               // CulturalFlags
        v[221] = QsInt(qs, "_srv_deviceAge");                   // DeviceAgeYears
        v[222] = QsBit(qs, "_srv_deviceAgeAnomaly");            // DeviceAgeAnomaly
        v[223] = QsBit(qs, "_srv_replayDetected");              // ReplayDetected
        v[224] = QsStr(qs, "_srv_replayMatchFP");               // ReplayMatchFingerprint
        v[225] = QsInt(qs, "_srv_deadInternetIdx");             // DeadInternetIndex

        // ════════════════════════════════════════════════════════════════
        // BITMAP FIELDS — Computed elsewhere, NULL for now
        // ════════════════════════════════════════════════════════════════
        v[226] = DBNull.Value;  // FeatureBitmapValue
        v[227] = DBNull.Value;  // AccessibilityBitmapValue
        v[228] = DBNull.Value;  // BotBitmapValue
        v[229] = DBNull.Value;  // EvasionBitmapValue

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
/// Each <c>object?[]</c> is a 230-element row produced by <see cref="ParsedRecordParser.Parse"/>.
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
