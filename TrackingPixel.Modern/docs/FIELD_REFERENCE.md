# SmartPiXL Field Reference

Complete reference for all fields in `pixl_parsed` (175 columns) and `vw_PiXL_Complete` (176 columns).
Every field here is **verified against the live pixel JavaScript, the `pixl_parsed` materialized table, and the SQL view** as of 2026-02-12.

> **Data flow:** Browser JS (`data.paramName`) → pixel GET request → `PiXL_Test.QueryString` → `vw_PiXL_Complete` SQL view → `pixl_parsed` materialized table (via `DatabaseWriterService`)
> 
> Server-side signals (`_srv_*`) are appended by `IpBehaviorService` before storage.

> **Note:** `pixl_parsed` uses `SourceId` (FK reference to `PiXL_Test.Id`) in place of `Id`, and adds `ParsedAt`. The raw columns `RawQueryString` and `RawHeadersJson` remain only in `vw_PiXL_Complete` / `PiXL_Test`.

---

## At a Glance

| Metric | Count |
|--------|-------|
| `pixl_parsed` columns | 175 |
| `vw_PiXL_Complete` columns | 176 (includes `RawQueryString`/`RawHeadersJson`) |
| JS query string params | 158 (+ 2 error-only) |
| Server-side columns (no JS) | 9 (`SourceId`, `CompanyID`, `PiXLID`, `IPAddress`, `ReceivedAt`, `RequestPath`, `ServerUserAgent`, `ServerReferer`, `ParsedAt`) |
| Server-side computed signals | 7 (`Srv_SubnetIps`, `Srv_SubnetHits`, `Srv_HitsIn15s`, `Srv_LastGapMs`, `Srv_SubSecDupe`, `Srv_SubnetAlert`, `Srv_RapidFire`) |
| Computed columns | 1 (`IsSynthetic`) |
| Fingerprint hash signals | 7 (canvas, webgl, audio hash, audio sum, math, error, css font) |
| Bot detection fields | 7 |
| Behavioral biometric fields | 8 |
| Cross-signal analysis fields | 3 |
| IP behavior fields | 7 |
| Evasion/stealth fields | 7 |
| Client Hints fields | 10 |

---

## Quick Navigation

- [Identity & Server Context](#1-identity--server-context)
- [Screen & Display](#2-screen--display)
- [Locale & Internationalization](#3-locale--internationalization)
- [Browser & Navigator](#4-browser--navigator)
- [Client Hints](#5-client-hints-chromium-only)
- [Browser-Specific Fields](#6-browser-specific-fields)
- [Fingerprint Signals](#7-fingerprint-signals)
- [Device & Hardware](#8-device--hardware)
- [Network & Connection](#9-network--connection)
- [Storage & Media Devices](#10-storage--media-devices)
- [API Capabilities](#11-api-capabilities)
- [Accessibility & Preferences](#12-accessibility--preferences)
- [Page Context](#13-page-context)
- [Document State](#14-document-state)
- [Performance Timing](#15-performance-timing)
- [Bot Detection](#16-bot-detection)
- [Evasion Detection](#17-evasion-detection)
- [Behavioral Biometrics](#18-behavioral-biometrics)
- [Cross-Signal Analysis](#19-cross-signal-analysis)
- [IP Behavior Analysis (Server-Side)](#20-ip-behavior-analysis-server-side)
- [Raw Data](#21-raw-data)
- [Real-World Entropy Analysis](#22-real-world-entropy-analysis-feb-2026)
- [Data Dictionary](#23-data-dictionary-pixl_parsed)

---

## 1. Identity & Server Context

These fields come from the HTTP request itself, not from JavaScript.

| SQL Column | JS Param | SQL Type | Nullable | Description |
|------------|----------|----------|:--------:|-------------|
| `SourceId` | — | int | NO | FK to `PiXL_Test.Id` (auto-increment PK in raw table) |
| `CompanyID` | — | nvarchar(100) | YES | Client company identifier (from URL route) |
| `PiXLID` | — | nvarchar(100) | YES | Campaign/pixel identifier (from URL route) |
| `IPAddress` | — | nvarchar(50) | YES | Visitor IP (from X-Forwarded-For or RemoteIpAddress) |
| `ReceivedAt` | — | datetime2 | NO | Server UTC timestamp |
| `RequestPath` | — | nvarchar(500) | YES | HTTP request path |
| `ServerUserAgent` | — | nvarchar(2000) | YES | User-Agent from HTTP header |
| `ServerReferer` | — | nvarchar(2000) | YES | Referer from HTTP header |
| `IsSynthetic` | `synthetic` | bit | NO | 1 if test/synthetic traffic, 0 if real. Computed from param. |
| `Tier` | `tier` | int | YES | Script complexity tier (always 5 for current script) |
| `ParsedAt` | — | datetime2 | NO | UTC timestamp when record was parsed into `pixl_parsed` |

---

## 2. Screen & Display

| SQL Column | JS Param | SQL Type | Description | Entropy |
|------------|----------|----------|-------------|---------|
| `ScreenWidth` | `sw` | int | `screen.width` in pixels | ~5-6 bits |
| `ScreenHeight` | `sh` | int | `screen.height` in pixels | ~5-6 bits |
| `ScreenAvailWidth` | `saw` | int | Available width (minus taskbar/dock) | ~2 bits |
| `ScreenAvailHeight` | `sah` | int | Available height (minus taskbar/dock) | ~2 bits |
| `ViewportWidth` | `vw` | int | `window.innerWidth` (CSS viewport) | ~4 bits |
| `ViewportHeight` | `vh` | int | `window.innerHeight` (CSS viewport) | ~4 bits |
| `OuterWidth` | `ow` | int | `window.outerWidth` (including chrome) | ~3 bits |
| `OuterHeight` | `oh` | int | `window.outerHeight` (including chrome) | ~3 bits |
| `ScreenX` | `sx` | int | Window X position (`screenX`/`screenLeft`) | ~2 bits |
| `ScreenY` | `sy` | int | Window Y position (`screenY`/`screenTop`) | ~2 bits |
| `ColorDepth` | `cd` | int | `screen.colorDepth` (bits per pixel) | ~2 bits |
| `PixelRatio` | `pd` | decimal(5,2) | `devicePixelRatio` (1.0, 1.5, 2.0, 3.0) | ~2 bits |
| `ScreenOrientation` | `ori` | nvarchar | `screen.orientation.type` (e.g., "landscape-primary") | ~1 bit |

### Key Patterns

| Resolution | Typical Device | Notes |
|------------|---------------|-------|
| 1920x1080 | Desktop (most common) | |
| 2560x1440 | Gaming/Pro desktop | |
| 3840x2160 | 4K monitor | |
| 1366x768 | Laptop | |
| 390x844 | iPhone 12/13/14/15 | |
| 360x800 | Android common | |
| 1000x1000 | **Tor Browser** | Signature — see Evasion Detection |

### ColorDepth Analysis

| Depth | Platform | Normal? | Implication |
|-------|----------|---------|-------------|
| 24 | Windows | Yes | Standard |
| 24 | macOS | **Suspicious** | Real Macs report 30-bit color |
| 30 | macOS | Yes | Normal P3 display |
| 32 | Linux | Yes | Standard with alpha |
| 8 or 16 | Any | **Suspicious** | VM or very old device |

---

## 3. Locale & Internationalization

| SQL Column | JS Param | SQL Type | Description | Entropy |
|------------|----------|----------|-------------|---------|
| `Language` | `lang` | nvarchar | Primary language (`navigator.language`, e.g., "en-US") | ~4-6 bits |
| `LanguageList` | `langs` | nvarchar | All accepted languages, comma-separated | ~5-8 bits |
| `Timezone` | `tz` | nvarchar | IANA timezone (e.g., "America/New_York") | ~5-7 bits |
| `TimezoneOffsetMins` | `tzo` | int | UTC offset in minutes (`getTimezoneOffset()`) | ~4 bits |
| `ClientTimestampMs` | `ts` | bigint | Client epoch ms (`new Date().getTime()`) | Timing |
| `TimezoneLocale` | `tzLocale` | nvarchar | `locale\|calendar\|numberingSystem\|hourCycle` | ~3-5 bits |
| `DateFormatSample` | `dateFormat` | nvarchar | `Intl.DateTimeFormat` output for 2024-01-15 | ~3 bits |
| `NumberFormatSample` | `numberFormat` | nvarchar | `Intl.NumberFormat().format(1234567.89)` | ~3 bits |
| `RelativeTimeSample` | `relativeTime` | nvarchar | `Intl.RelativeTimeFormat().format(-1, 'day')` | ~3 bits |

**Why this matters:** Locale formatting reveals the user's OS language configuration at a deeper level than `navigator.language`. A US English user on Windows vs macOS will produce different `DateFormatSample` strings. Combined with timezone, this cross-validates IP geolocation.

---

## 4. Browser & Navigator

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `ClientUserAgent` | `ua` | nvarchar | Full `navigator.userAgent` string |
| `Platform` | `plt` | nvarchar | `navigator.platform` ("Win32", "MacIntel", "Linux x86_64") |
| `Vendor` | `vnd` | nvarchar | `navigator.vendor` ("Google Inc.", "Apple Computer, Inc.") |
| `AppName` | `appName` | nvarchar | `navigator.appName` (usually "Netscape") |
| `AppVersion` | `appVersion` | nvarchar | `navigator.appVersion` |
| `AppCodeName` | `appCodeName` | nvarchar | `navigator.appCodeName` (usually "Mozilla") |
| `NavigatorProduct` | `product` | nvarchar | `navigator.product` ("Gecko") |
| `NavigatorProductSub` | `productSub` | nvarchar | `navigator.productSub` |
| `NavigatorVendorSub` | `vendorSub` | nvarchar | `navigator.vendorSub` (usually empty) |
| `PluginCount` | `plugins` | int | Count of `navigator.plugins` |
| `PluginListDetailed` | `pluginList` | nvarchar | `name::filename::description` pipe-separated (up to 20) |
| `MimeTypeCount` | `mimeTypes` | int | Count of `navigator.mimeTypes` |
| `MimeTypeList` | `mimeList` | nvarchar | Comma-separated MIME types (up to 30) |
| `HistoryLength` | `hist` | int | `history.length` — browsing session depth |

---

## 5. Client Hints (Chromium Only)

High-entropy UA signals requested via `Accept-CH` response header. Only available in Chromium-based browsers.

| SQL Column | JS Param | SQL Type | Description | Entropy |
|------------|----------|----------|-------------|---------|
| `UA_Platform` | `uaPlatform` | nvarchar | OS name ("Windows", "macOS", "Android", "Linux") | ~3 bits |
| `UA_PlatformVersion` | `uaPlatformVersion` | nvarchar | OS version (e.g., "15.0.0", "10.0.0") | ~4-6 bits |
| `UA_Architecture` | `uaArch` | nvarchar | CPU arch ("x86", "arm") | ~2 bits |
| `UA_Bitness` | `uaBitness` | nvarchar | "32" or "64" | ~1 bit |
| `UA_IsMobile` | `uaMobile` | bit | Mobile device flag | ~1 bit |
| `UA_IsWow64` | `uaWow64` | bit | 32-bit app on 64-bit OS (WoW64) | ~1 bit |
| `UA_Model` | `uaModel` | nvarchar | Device model, mobile only ("Pixel 7", "SM-S918B") | ~5-8 bits |
| `UA_Brands` | `uaBrands` | nvarchar | Low-entropy brand list ("Chromium/120\|Google Chrome/120") | ~3 bits |
| `UA_FullVersionList` | `uaFullVersion` | nvarchar | Full version list with patch numbers | ~5 bits |
| `UA_FormFactor` | `uaFormFactor` | nvarchar | Form factor(s): "Desktop", "Mobile", "Tablet" | ~2 bits |

**Cross-validation:** `UA_Platform` vs `Platform` (navigator.platform) should agree. A mismatch is a strong evasion signal — bots often spoof one but forget the other.

---

## 6. Browser-Specific Fields

### Firefox Only

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `Firefox_OSCPU` | `oscpu` | nvarchar | `navigator.oscpu` — OS/CPU string (e.g., "Windows NT 10.0; Win64; x64") |
| `Firefox_BuildID` | `buildID` | nvarchar | Firefox build timestamp (may be frozen for privacy) |

### Chrome/Chromium Only

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `Chrome_ObjectPresent` | `chromeObj` | bit | `window.chrome` exists |
| `Chrome_RuntimePresent` | `chromeRuntime` | bit | `window.chrome.runtime` exists |
| `Chrome_JSHeapSizeLimit` | `jsHeapLimit` | bigint | `performance.memory.jsHeapSizeLimit` |
| `Chrome_TotalJSHeapSize` | `jsHeapTotal` | bigint | `performance.memory.totalJSHeapSize` |
| `Chrome_UsedJSHeapSize` | `jsHeapUsed` | bigint | `performance.memory.usedJSHeapSize` |

**Chrome Object Matrix:**

| Browser | `chrome` obj | `chrome.runtime` |
|---------|-------------|-------------------|
| Chrome | Yes | Yes (extensions only) |
| Edge | Yes | Yes (extensions only) |
| Brave | Yes | Yes |
| Firefox | No | No |
| Safari | No | No |
| HeadlessChrome | Often missing | No |

---

## 7. Fingerprint Signals

These are the core device fingerprinting hashes. Each captures a different hardware/software dimension.

### Canvas Fingerprint (~10-15 bits)

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `CanvasFingerprint` | `canvasFP` | nvarchar | Hex hash of canvas rendering output |
| `CanvasSupported` | `canvas` | bit | Canvas 2D context available |
| `CanvasEvasionDetected` | `canvasEvasion` | bit | Pixel data variance suspiciously low or dataURL too short |
| `CanvasConsistency` | `canvasConsistency` | nvarchar | Noise injection detection result |

**CanvasConsistency values:**
- `clean` — two renders match exactly (normal)
- `noise-detected` — two renders differ (Canvas Blocker or Brave injecting random noise)
- `canvas-blocked` — canvas API blocked entirely
- `error` — exception during rendering

**How canvas fingerprinting works:** Draws shapes, gradients, text with specific fonts onto a hidden canvas. The resulting pixel data is deterministic per GPU+driver+OS+font-rasterizer combination but varies across devices. Samples at y=25 (center of drawn region) to defeat top-row-only evasion techniques.

### WebGL Fingerprint (~10-15 bits)

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `WebGLFingerprint` | `webglFP` | nvarchar | Hex hash of 23 WebGL parameters |
| `WebGLSupported` | `webgl` | bit | WebGL 1.0 context available |
| `WebGL2Supported` | `webgl2` | bit | WebGL 2.0 context available |
| `WebGLEvasionDetected` | `webglEvasion` | bit | GPU is software renderer (SwiftShader/llvmpipe/Mesa) |
| `WebGLExtensionCount` | `webglExt` | int | Number of supported WebGL extensions |
| `WebGLParameters` | `webglParams` | nvarchar | First 5 WebGL params: VERSION\|SHADING_LANGUAGE\|VENDOR\|RENDERER\|MAX_VERTEX_ATTRIBS |
| `GPURenderer` | `gpu` | nvarchar | Unmasked GPU model (e.g., "NVIDIA GeForce RTX 4090") |
| `GPUVendor` | `gpuVendor` | nvarchar | Unmasked GPU vendor ("NVIDIA Corporation", "Google Inc.") |

**Software renderer = bot signal:** SwiftShader, llvmpipe, and Mesa OffScreen are software GPU emulators used in headless environments (Docker, CI, cloud VMs).

### Audio Fingerprint (~5-8 bits)

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `AudioFingerprintSum` | `audioFP` | nvarchar | Sum of OfflineAudioContext frequency bin samples (6 decimal places) |
| `AudioFingerprintHash` | `audioHash` | nvarchar | Hash of full audio sample data |
| `AudioIsStable` | `audioStable` | bit | Two audio fingerprint runs match (1=stable, 0=varies) |
| `AudioNoiseInjectionDetected` | `audioNoiseDetected` | bit | Two audio runs differ — noise injection extension detected |

**How it works:** Creates an `OfflineAudioContext`, generates a triangle wave through a `DynamicsCompressor`, and reads back the processed frequency data. Different audio stacks (OS + browser + audio driver) produce slightly different results.

### Font Detection (~10-15 bits)

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `DetectedFonts` | `fonts` | nvarchar | Comma-separated list of detected installed fonts |
| `CSSFontVariantHash` | `cssFontVariant` | nvarchar | CSS font variant computed values (V2: computed property values + rendered width) |
| `FontMethodMismatch` | `fontMethodMismatch` | bit | `offsetWidth` and `getBoundingClientRect` disagree — spoofing indicator |

**How font detection works:** Renders test strings in each target font + monospace fallback. If the rendered width differs from the pure monospace baseline, the font is installed. Tests ~42 fonts.

**FontMethodMismatch:** Privacy extensions that fake font widths typically only intercept `offsetWidth` but not `getBoundingClientRect`, or vice versa. Detecting a difference between the two methods reveals the spoofing.

### Other Fingerprint Hashes

| SQL Column | JS Param | SQL Type | Description | Real-World Entropy |
|------------|----------|----------|-------------|:---:|
| `MathFingerprint` | `mathFP` | nvarchar(200) | Hash of Math function precision (tan, sin, acos, atan, exp, log, sqrt, pow) | **0 bits** (uniform) |
| `ErrorFingerprint` | `errorFP` | nvarchar(200) | `e.message.length + e.stack.length` from `null[0]()` | ~3.9 bits (15 values) |
| `SpeechVoices` | `voices` | nvarchar(4000) | Speech synthesis voices: `name/lang` pipe-separated (up to 20) | ~5-8 bits |

**MathFingerprint:** Different CPUs and JS engines produce slightly different floating-point results for transcendental functions. V8 on x86 differs from V8 on ARM.

> **Note (Migration 17, confirmed 2026-02-12):** MathFingerprint has **zero entropy** in production — all 252 visitors produce the identical value (`-1.4214488,0.84147098,1.04719755,1.10714871,2.71828182,0.69314718,1.41421356,9007199254`). The V8/SpiderMonkey/JSC engines have fully converged on IEEE 754 precision for these operations. The field is retained for future cross-platform analysis but is **excluded from FingerprintStrength calculations** and should be considered for replacement with a higher-entropy signal.

**ErrorFingerprint:** Chrome says "TypeError: Cannot read properties of null" while Firefox says "null has no properties". The message length + stack length creates a browser engine signature.

**SpeechVoices:** `speechSynthesis.getVoices()`. Windows, macOS, Linux, and mobile each have different default voice sets. Additional language packs add voices. VMs with no voices are a bot indicator.

---

## 8. Device & Hardware

| SQL Column | JS Param | SQL Type | Description | Entropy |
|------------|----------|----------|-------------|---------|
| `HardwareConcurrency` | `cores` | int | CPU logical core count (`navigator.hardwareConcurrency`) | ~3-5 bits |
| `DeviceMemoryGB` | `mem` | decimal(5,2) | Approximate RAM in GB (`navigator.deviceMemory`) | ~3 bits |
| `MaxTouchPoints` | `touch` | int | Touch capability (`navigator.maxTouchPoints`) | ~2 bits |
| `ConnectedGamepads` | `gamepads` | nvarchar | Pipe-separated gamepad IDs from `navigator.getGamepads()` | ~1 bit |
| `BatteryCharging` | `batteryCharging` | bit | Device is plugged in | ~1 bit |
| `BatteryLevelPct` | `batteryLevel` | int | Battery percentage (0-100) | Low |

**Device Classification:**
```
IF UA_IsMobile = 1 THEN 'Mobile'
ELSE IF MaxTouchPoints > 0 AND ScreenWidth < 1200 THEN 'Tablet'
ELSE 'Desktop'
```

---

## 9. Network & Connection

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `ConnectionType` | `conn` | nvarchar | `connection.effectiveType` ("4g", "3g", "2g", "slow-2g") |
| `NetworkType` | `connType` | nvarchar | `connection.type` ("wifi", "cellular", "ethernet") |
| `DownlinkMbps` | `dl` | decimal(10,2) | Estimated bandwidth in Mbps |
| `DownlinkMax` | `dlMax` | nvarchar | Maximum downlink speed |
| `RTTMs` | `rtt` | int | Round-trip time estimate in ms |
| `DataSaverEnabled` | `save` | bit | `connection.saveData` active |
| `IsOnline` | `online` | bit | `navigator.onLine` |
| `WebRTCLocalIP` | `localIp` | nvarchar | Local network IP from WebRTC ICE candidate (e.g., "192.168.1.5") |

**WebRTCLocalIP:** Reveals internal network topology — corporate VLAN assignment, VPN usage, Carrier-grade NAT. Only available if WebRTC is not blocked by the browser.

---

## 10. Storage & Media Devices

| SQL Column | JS Param | SQL Type | Description | Entropy |
|------------|----------|----------|-------------|---------|
| `StorageQuotaGB` | `storageQuota` | int | `navigator.storage.estimate()` quota in GB | ~3-4 bits |
| `StorageUsedMB` | `storageUsed` | int | Storage used in MB | ~2-3 bits |
| `AudioInputDevices` | `audioInputs` | int | Microphone count from `enumerateDevices()` | ~2 bits |
| `VideoInputDevices` | `videoInputs` | int | Camera count from `enumerateDevices()` | ~1-2 bits |

**Storage Quota Analysis:**
- Desktop: Typically 50-300+ GB quota
- Mobile: Typically 1-10 GB quota
- Incognito/Private: Drastically reduced quota
- No quota or 0: Headless/bot environment

---

## 11. API Capabilities

Boolean flags for browser API support. Each contributes 1 bit of entropy. The combination creates a browser "capability signature."

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `CookiesEnabled` | `ck` | bit | Cookies allowed |
| `DoNotTrack` | `dnt` | nvarchar | DNT header value ("1", "0", null) |
| `PDFViewerEnabled` | `pdf` | bit | Built-in PDF viewer active |
| `WebDriverDetected` | `webdr` | bit | `navigator.webdriver` is true |
| `JavaEnabled` | `java` | bit | `navigator.javaEnabled()` |
| `CanvasSupported` | `canvas` | bit | Canvas 2D API available |
| `WebAssemblySupported` | `wasm` | bit | WebAssembly available |
| `WebWorkersSupported` | `ww` | bit | Web Workers available |
| `ServiceWorkerSupported` | `swk` | bit | Service Workers available |
| `LocalStorageSupported` | `ls` | bit | localStorage available |
| `SessionStorageSupported` | `ss` | bit | sessionStorage available |
| `IndexedDBSupported` | `idb` | bit | IndexedDB available |
| `CacheAPISupported` | `caches` | bit | CacheStorage API available |
| `MediaDevicesAPISupported` | `mediaDevices` | bit | `navigator.mediaDevices` exists |
| `ClipboardAPISupported` | `clipboard` | bit | Clipboard `writeText` available |
| `SpeechSynthesisSupported` | `speechSynth` | bit | `window.speechSynthesis` available |
| `TouchEventsSupported` | `touchEvent` | bit | `ontouchstart` in window |
| `PointerEventsSupported` | `pointerEvent` | bit | PointerEvent API available |

> **Note on removed phantom fields:** The previous version of this document listed 13 additional API capability fields (Geolocation, Notifications, Push, Bluetooth, USB, Serial, HID, MIDI, SpeechRecognition, Share, Credentials, PaymentRequest, WebXR) that were never collected by the pixel script and had no SQL view columns. They have been removed. If these are needed for future fingerprinting, they must be added to PiXLScript.cs first, then to the SQL view.

---

## 12. Accessibility & Preferences

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `PrefersColorSchemeDark` | `darkMode` | bit | `prefers-color-scheme: dark` |
| `PrefersColorSchemeLight` | `lightMode` | bit | `prefers-color-scheme: light` |
| `PrefersReducedMotion` | `reducedMotion` | bit | `prefers-reduced-motion: reduce` |
| `PrefersReducedData` | `reducedData` | bit | `prefers-reduced-data: reduce` |
| `PrefersHighContrast` | `contrast` | bit | `prefers-contrast: more` |
| `ForcedColorsActive` | `forcedColors` | bit | `forced-colors: active` (Windows High Contrast) |
| `InvertedColorsActive` | `invertedColors` | bit | `inverted-colors: inverted` |
| `HoverCapable` | `hover` | bit | `(hover: hover)` media query |
| `PointerType` | `pointer` | nvarchar | Primary pointer: "fine" (mouse), "coarse" (touch), "" (none) |
| `StandaloneDisplayMode` | `standalone` | bit | `(display-mode: standalone)` — PWA mode |

---

## 13. Page Context

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `PageURL` | `url` | nvarchar | `location.href` (full URL) |
| `PageDomain` | `domain` | nvarchar | `location.hostname` |
| `PagePath` | `path` | nvarchar | `location.pathname` |
| `PageProtocol` | `protocol` | nvarchar | "http:" or "https:" |
| `PageHash` | `hash` | nvarchar | URL fragment |
| `PageTitle` | `title` | nvarchar | `document.title` |
| `PageReferrer` | `ref` | nvarchar | `document.referrer` (client-side) |

---

## 14. Document State

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `DocumentCharset` | `docCharset` | nvarchar | `document.characterSet` (e.g., "UTF-8") |
| `DocumentCompatMode` | `docCompat` | nvarchar | "CSS1Compat" (standards) or "BackCompat" (quirks) |
| `DocumentReadyState` | `docReady` | nvarchar | "loading", "interactive", "complete" |
| `DocumentHidden` | `docHidden` | bit | `document.hidden` — tab is backgrounded |
| `DocumentVisibility` | `docVisibility` | nvarchar | "visible", "hidden", "prerender" |

---

## 15. Performance Timing

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `ScriptExecutionTimeMs` | `scriptExecTime` | int | Time from page load to script execution point |
| `PageLoadTimeMs` | `loadTime` | int | `loadEventEnd - navigationStart` |
| `DOMReadyTimeMs` | `domTime` | int | `domContentLoadedEventEnd - navigationStart` |
| `DNSLookupMs` | `dnsTime` | int | `domainLookupEnd - domainLookupStart` |
| `TCPConnectMs` | `tcpTime` | int | `connectEnd - connectStart` |
| `TimeToFirstByteMs` | `ttfb` | int | `responseStart - requestStart` |

### Script Execution Time (Key Bot Indicator)

| Time | Assessment | Explanation |
|------|------------|-------------|
| < 10ms | **Almost certainly bot** | Instant DOM, no network stack, pre-rendered |
| 10-50ms | Suspicious | Could be fast cache + SSD, but rare |
| 50-200ms | Normal human | Real browser with network, parsing, execution |
| > 200ms | Definitely human | Slow connection or device |

---

## 16. Bot Detection

### Scores

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `BotScore` | `botScore` | int | Composite bot likelihood (0-100). Sum of triggered signal weights, capped at 100. |
| `CombinedThreatScore` | `combinedThreatScore` | int | `botScore + min(anomalyScore, 25)`. Bridges automation detection with cross-signal anomaly detection. |
| `BotSignalsList` | `botSignals` | nvarchar | Comma-separated list of triggered signal names |
| `BotPermissionInconsistent` | `botPermInconsistent` | bit | Permission API returns inconsistent state |

**Risk Buckets:**

| Bucket | Score Range | Interpretation |
|--------|-------------|----------------|
| High Risk | 80-100 | Almost certainly automated |
| Medium Risk | 50-79 | Suspicious, needs review |
| Low Risk | 20-49 | Minor anomalies detected |
| Likely Human | 0-19 | Normal behavior |

### Bot Signal Reference

All possible values in `BotSignalsList`:

| Signal | Weight | Trigger |
|--------|--------|---------|
| `webdriver` | +10 | `navigator.webdriver` is true |
| `headless-no-chrome-obj` | +8 | Chrome UA but no `window.chrome` |
| `minimal-ua` | +15 | User-Agent < 30 characters |
| `fake-ua` | +20 | UA matches known fake pattern |
| `phantom` | +8 | PhantomJS artifacts |
| `nightmare` | +8 | Nightmare.js artifacts |
| `selenium` | +10 | `__selenium_*` globals |
| `cdp` | +10 | Chrome DevTools Protocol variables |
| `playwright-global` | +10 | `__playwright` or `__pw_manual` |
| `empty-languages` | +5 | `navigator.languages` is empty |
| `plugin-mime-mismatch` | +3 | Plugins empty but mimeTypes present |
| `zero-screen` | +8 | Screen dimensions are 0 |
| `no-plugins` | +2 | No browser plugins |
| `dom-automation` | +10 | `domAutomation` / `domAutomationController` |
| `outer-zero` | +5 | `outerWidth` is 0 but `innerWidth` > 0 |
| `nav-*` | +10 | Automation property on navigator |
| `fn-tampered` | +5 | Native functions appear tampered |
| `default-viewport` | +2 | Common headless viewport (1280x720, 800x600) |
| `headless-ua` | +10 | "HeadlessChrome" in User-Agent |
| `perm-inconsistent` | +5 | Permission API inconsistency |
| `chrome-no-runtime` | +1 | Chrome object but no runtime (reduced from +3 — fires on all normal Chrome page visits since `chrome.runtime` is extension-only) |
| `fullscreen-match` | +2 | Screen equals window equals available |
| `no-connection-api` | +3 | Chrome but no Connection API |
| `eval-tampered` | +5 | `eval` function overridden |
| `cross-realm-toString` | +12 | `Function.prototype.toString.call()` cross-realm check fails |
| `getter-name-*` | +6 each | Property descriptor getter `.name` validation fails |
| `getter-proto-*` | +8 each | Property descriptor getter `.prototype` validation fails |
| `heap-size-spoofed` | +8 | Heap size is a perfectly round number (e.g., 10000000) — real V8 heaps are always messy |
| `heap-total-equals-used` | +5 | `totalJSHeapSize === usedJSHeapSize` — physically impossible in real V8 (promoted from cross-signal) |

---

## 17. Evasion Detection

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `CanvasEvasionDetected` | `canvasEvasion` | bit | Canvas data variance is 0 or dataURL suspiciously short |
| `WebGLEvasionDetected` | `webglEvasion` | bit | GPU is software renderer (SwiftShader, llvmpipe, Mesa) |
| `EvasionToolsDetected` | `evasionDetected` | nvarchar | Comma-separated evasion tool names |
| `EvasionSignalsV2` | `evasionSignalsV2` | nvarchar | Enhanced evasion/Tor/stealth signals |
| `StealthPluginSignals` | `stealthSignals` | nvarchar | Stealth plugin detection signals |
| `ProxyBlockedProperties` | `_proxyBlocked` | nvarchar | Navigator properties blocked by JS Proxy extension |
| `FontMethodMismatch` | `fontMethodMismatch` | bit | `offsetWidth` vs `getBoundingClientRect` disagree |

### EvasionToolsDetected Values

| Signal | Description |
|--------|-------------|
| `tor-screen` | Screen is 1000x1000 (Tor Browser letterbox) |
| `tor-likely` | Win32 + 24-bit color + no chrome object |
| `brave` | `navigator.brave` API detected |
| `webrtc-blocked` | WebRTC API undefined |
| `ua-platform-mismatch` | User-Agent OS doesn't match `navigator.platform` |
| `mobile-ua-desktop-screen` | Mobile UA but screen > 1024px |
| `touch-mismatch` | Touch capability but no mobile UA on large screen |
| `partial-js-block` | Some JS APIs blocked (NoScript pattern) |
| `clienthints-platform-mismatch` | Client Hints platform differs from `navigator.platform` |

### EvasionSignalsV2 Values

| Signal | Description |
|--------|-------------|
| `tor-letterbox-viewport` | Viewport matches Tor Browser letterboxing patterns |
| `canvas-noise` | Canvas noise injection detected (Brave Shields, Canvas Blocker) |
| `stealth-detected` | Stealth plugin patterns found |

### StealthPluginSignals Values

| Signal | Description |
|--------|-------------|
| `webdriver-slow` | `navigator.webdriver` exists but returns false (stealth override) |
| `toString-spoofed` | Native function `toString()` has been replaced |
| `nav-proto-modified` | Navigator prototype chain has been tampered with |

### ProxyBlockedProperties

Privacy extensions (JShelter, Trace, Privacy Badger) wrap `navigator` in a JS Proxy. When the script accesses blocked properties, it catches the TypeError and records which properties were blocked.

**Example values:**
- `"javaEnabled,"` — minimal blocking
- `"javaEnabled,platform,languages,userAgent,"` — aggressive blocking
- Empty — no privacy extension

**Paradox:** The presence of Proxy blocking is itself a fingerprint signal. Users with privacy extensions are ~1-2% of traffic — rarer makes them more identifiable, not less.

---

## 18. Behavioral Biometrics

Real-time input behavior captured during the ~500ms window before pixel fires. Bots typically have zero or perfectly uniform input patterns.

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `MouseMoveCount` | `mouseMoves` | int | Number of mouse movements captured |
| `MouseEntropy` | `mouseEntropy` | int | Mouse movement angle variance x 1000 (0 if < 5 moves) |
| `MoveTimingCV` | `moveTimingCV` | int | Coefficient of variation of time between moves x 1000 |
| `MoveSpeedCV` | `moveSpeedCV` | int | Coefficient of variation of movement speed x 1000 |
| `MoveCountBucket` | `moveCountBucket` | nvarchar | "low" / "mid" / "high" / "very-high" |
| `UserScrolled` | `scrolled` | bit | User scrolled within capture window |
| `ScrollDepthPx` | `scrollY` | int | Scroll Y position at pixel fire time |
| `ScrollContradiction` | `scrollContradiction` | bit | Scroll event fired but `scrollY` = 0 (bot indicator) |
| `BehavioralFlags` | `behavioralFlags` | nvarchar | Behavioral analysis flags |

### Behavioral Interpretation

| `MouseMoveCount` | `MouseEntropy` | Assessment |
|-------------------|----------------|------------|
| 0 | 0 | No mouse activity — could be mobile or bot |
| > 0 | 0 | Movement but no angle variance — bot-like straight lines |
| > 5 | > 100 | Normal human — varied movement angles |
| > 50 | < 50 | High count but low variance — suspicious automation |

### BehavioralFlags Values

| Flag | Description |
|------|-------------|
| `uniform-timing` | Time between mouse moves has very low variance (robotic) |
| `uniform-speed` | Movement speed has very low variance (robotic) |

---

## 19. Cross-Signal Analysis

Cross-referencing multiple signals to detect inconsistencies that indicate spoofing.

| SQL Column | JS Param | SQL Type | Description |
|------------|----------|----------|-------------|
| `CrossSignalFlags` | `crossSignals` | nvarchar | Comma-separated cross-signal inconsistency flags |
| `AnomalyScore` | `anomalyScore` | int | Cumulative cross-signal anomaly score |

### CrossSignalFlags Values

| Flag | Description |
|------|-------------|
| `win-fonts-on-mac` | Windows-only fonts detected but platform claims macOS |
| `mac-fonts-on-win` | macOS-only fonts detected but platform claims Windows |
| `swiftshader-gpu` | Software GPU renderer (headless/VM indicator) |
| `screen-mismatch` | Screen dimensions don't match claimed platform |
| `heap-mismatch` | JS heap size inconsistent with claimed device memory |
| `ua-brand-mismatch` | User-Agent brands don't match Client Hints |
| `gpu-platform-mismatch` | macOS-only GPU string (e.g., "Intel Iris OpenGL Engine") found on non-Mac platform — spoofed UA |
| `software-gpu-on-mac` | Software GPU (SwiftShader/llvmpipe/Mesa) found on macOS platform — VM or misconfigured |
| `round-heap-limit` | `jsHeapSizeLimit` is a round multiple of 10M — known fake heap limit values |

**CombinedThreatScore formula:**
```
CombinedThreatScore = BotScore + min(AnomalyScore, 25)
```
The anomaly contribution is capped at 25 — anomalies alone shouldn't push a human to "High Risk," but they significantly amplify an already suspicious bot score.

---

## 20. IP Behavior Analysis (Server-Side)

These fields are computed **server-side** by `IpBehaviorService` — they require cross-request correlation that JavaScript running in a single page cannot perform. They are appended as `_srv_*` query parameters before the hit is stored.

| SQL Column | Source Param | SQL Type | Description |
|------------|-------------|----------|-------------|
| `Srv_SubnetIps` | `_srv_subnetIps` | int | Unique IPs from same /24 subnet in 5-minute window |
| `Srv_SubnetHits` | `_srv_subnetHits` | int | Total hits from same /24 subnet in 5-minute window |
| `Srv_HitsIn15s` | `_srv_hitsIn15s` | int | Hits from same IP in 15-second window |
| `Srv_LastGapMs` | `_srv_lastGapMs` | bigint | Milliseconds since last hit from same IP (-1 if first hit) |
| `Srv_SubSecDupe` | `_srv_subSecDupe` | bit | Sub-second duplicate from same IP (< 1000ms gap) |
| `Srv_SubnetAlert` | `_srv_subnetAlert` | bit | Subnet /24 velocity alert: 3+ unique IPs in same /24 in 5min |
| `Srv_RapidFire` | `_srv_rapidFire` | bit | Rapid-fire alert: 3+ hits from same IP in 15 seconds |

**Note:** These fields are only populated when an alert fires. Null values mean no alert — not that the analysis wasn't performed. The server analyzes every hit but only appends params when thresholds are exceeded, to keep querystrings lean.

### Subnet Velocity Detection

Bot farms deploy multiple instances across cloud VMs in the same data center, which typically share a /24 subnet (e.g., `205.169.39.x`). When 3+ unique IPs from the same /24 hit within 5 minutes, this signals coordinated infrastructure.

| `SubnetUniqueIps` | `SubnetTotalHits` | Interpretation |
|--------------------|--------------------|----------------|
| 1 | Any | Normal — single IP |
| 2 | < 5 | Possible NAT / shared office |
| 3+ | Any | **Alert** — likely coordinated infrastructure |
| 3+ | > 10 | **Strong signal** — bot farm cluster |

### Rapid-Fire Timing Detection

Same IP hitting multiple times within 15 seconds indicates automation. Human revisits are hours or days apart.

| `HitsIn15s` | `LastGapMs` | Interpretation |
|-------------|-------------|----------------|
| 1 | -1 | First hit — no history |
| 1 | > 15000 | Normal organic revisit |
| 2 | 1000-15000 | Possible automation |
| 3+ | Any | **Alert** — rapid-fire automation |
| Any | < 1000 | **Sub-second duplicate** — definite automation |

---

## 21. Raw Data

| SQL Column | Source | SQL Type | Description |
|------------|--------|----------|-------------|
| `RawQueryString` | `PiXL_Test.QueryString` | nvarchar(max) | Full query string — all 158 params URL-encoded |
| `RawHeadersJson` | `PiXL_Test.HeadersJson` | nvarchar(max) | All HTTP request headers as JSON |

These are the source of truth. The view parses them via `dbo.GetQueryParam()`. If a new JS param is added, the raw data already contains it — only the view needs updating.

---

## Composite Scores (Computed at Query Time)

These are not stored columns but useful SQL expressions for dashboards and reporting.

### Fingerprint Strength (0-100)
How unique is this device based on available high-entropy signals?

```sql
SELECT *,
    (CASE WHEN CanvasFingerprint IS NOT NULL AND CanvasFingerprint != '' THEN 15 ELSE 0 END) +
    (CASE WHEN WebGLFingerprint IS NOT NULL AND WebGLFingerprint != '' THEN 15 ELSE 0 END) +
    (CASE WHEN AudioFingerprintHash IS NOT NULL AND AudioFingerprintHash != '' THEN 10 ELSE 0 END) +
    (CASE WHEN DetectedFonts IS NOT NULL AND DetectedFonts != '' THEN 15 ELSE 0 END) +
    -- MathFingerprint excluded: zero entropy in practice (all V8/x86_64 produce identical values)
    -- (CASE WHEN MathFingerprint IS NOT NULL AND MathFingerprint != '' THEN 10 ELSE 0 END) +
    (CASE WHEN ErrorFingerprint IS NOT NULL AND ErrorFingerprint != '' THEN 5 ELSE 0 END) +
    (CASE WHEN SpeechVoices IS NOT NULL AND SpeechVoices != '' THEN 10 ELSE 0 END) +
    (CASE WHEN CSSFontVariantHash IS NOT NULL AND CSSFontVariantHash != '' THEN 5 ELSE 0 END) +
    (CASE WHEN GPURenderer IS NOT NULL AND GPURenderer != '' THEN 10 ELSE 0 END) +
    (CASE WHEN PluginCount > 0 THEN 5 ELSE 0 END)
    AS FingerprintStrength
FROM vw_PiXL_Complete
```

### Privacy Score (0-100)
How privacy-conscious is this visitor?

```sql
SELECT *,
    (CASE WHEN DoNotTrack = '1' THEN 15 ELSE 0 END) +
    (CASE WHEN CanvasEvasionDetected = 1 THEN 20 ELSE 0 END) +
    (CASE WHEN WebGLEvasionDetected = 1 THEN 15 ELSE 0 END) +
    (CASE WHEN ProxyBlockedProperties IS NOT NULL AND ProxyBlockedProperties != '' THEN 20 ELSE 0 END) +
    (CASE WHEN WebRTCLocalIP IS NULL OR WebRTCLocalIP = '' THEN 10 ELSE 0 END) +
    (CASE WHEN PluginCount = 0 THEN 10 ELSE 0 END) +
    (CASE WHEN CookiesEnabled = 0 THEN 10 ELSE 0 END)
    AS PrivacyScore
FROM vw_PiXL_Complete
```

---

## JS → SQL Column Quick Lookup

For developers adding new fields. Sorted alphabetically by JS param name.

| JS Param | SQL Column |
|----------|------------|
| `_proxyBlocked` | `ProxyBlockedProperties` |
| `anomalyScore` | `AnomalyScore` |
| `appCodeName` | `AppCodeName` |
| `appName` | `AppName` |
| `appVersion` | `AppVersion` |
| `audioFP` | `AudioFingerprintSum` |
| `audioHash` | `AudioFingerprintHash` |
| `audioInputs` | `AudioInputDevices` |
| `audioNoiseDetected` | `AudioNoiseInjectionDetected` |
| `audioStable` | `AudioIsStable` |
| `batteryCharging` | `BatteryCharging` |
| `batteryLevel` | `BatteryLevelPct` |
| `behavioralFlags` | `BehavioralFlags` |
| `botPermInconsistent` | `BotPermissionInconsistent` |
| `botScore` | `BotScore` |
| `botSignals` | `BotSignalsList` |
| `buildID` | `Firefox_BuildID` |
| `caches` | `CacheAPISupported` |
| `canvas` | `CanvasSupported` |
| `canvasConsistency` | `CanvasConsistency` |
| `canvasEvasion` | `CanvasEvasionDetected` |
| `canvasFP` | `CanvasFingerprint` |
| `cd` | `ColorDepth` |
| `chromeObj` | `Chrome_ObjectPresent` |
| `chromeRuntime` | `Chrome_RuntimePresent` |
| `ck` | `CookiesEnabled` |
| `clipboard` | `ClipboardAPISupported` |
| `combinedThreatScore` | `CombinedThreatScore` |
| `conn` | `ConnectionType` |
| `connType` | `NetworkType` |
| `contrast` | `PrefersHighContrast` |
| `cores` | `HardwareConcurrency` |
| `crossSignals` | `CrossSignalFlags` |
| `cssFontVariant` | `CSSFontVariantHash` |
| `darkMode` | `PrefersColorSchemeDark` |
| `dateFormat` | `DateFormatSample` |
| `dl` | `DownlinkMbps` |
| `dlMax` | `DownlinkMax` |
| `dnsTime` | `DNSLookupMs` |
| `dnt` | `DoNotTrack` |
| `docCharset` | `DocumentCharset` |
| `docCompat` | `DocumentCompatMode` |
| `docHidden` | `DocumentHidden` |
| `docReady` | `DocumentReadyState` |
| `docVisibility` | `DocumentVisibility` |
| `domain` | `PageDomain` |
| `domTime` | `DOMReadyTimeMs` |
| `errorFP` | `ErrorFingerprint` |
| `evasionDetected` | `EvasionToolsDetected` |
| `evasionSignalsV2` | `EvasionSignalsV2` |
| `fontMethodMismatch` | `FontMethodMismatch` |
| `fonts` | `DetectedFonts` |
| `forcedColors` | `ForcedColorsActive` |
| `gamepads` | `ConnectedGamepads` |
| `gpu` | `GPURenderer` |
| `gpuVendor` | `GPUVendor` |
| `hash` | `PageHash` |
| `hist` | `HistoryLength` |
| `hover` | `HoverCapable` |
| `idb` | `IndexedDBSupported` |
| `invertedColors` | `InvertedColorsActive` |
| `java` | `JavaEnabled` |
| `jsHeapLimit` | `Chrome_JSHeapSizeLimit` |
| `jsHeapTotal` | `Chrome_TotalJSHeapSize` |
| `jsHeapUsed` | `Chrome_UsedJSHeapSize` |
| `lang` | `Language` |
| `langs` | `LanguageList` |
| `lightMode` | `PrefersColorSchemeLight` |
| `loadTime` | `PageLoadTimeMs` |
| `localIp` | `WebRTCLocalIP` |
| `ls` | `LocalStorageSupported` |
| `mathFP` | `MathFingerprint` |
| `mediaDevices` | `MediaDevicesAPISupported` |
| `mem` | `DeviceMemoryGB` |
| `mimeList` | `MimeTypeList` |
| `mimeTypes` | `MimeTypeCount` |
| `mouseEntropy` | `MouseEntropy` |
| `mouseMoves` | `MouseMoveCount` |
| `moveCountBucket` | `MoveCountBucket` |
| `moveSpeedCV` | `MoveSpeedCV` |
| `moveTimingCV` | `MoveTimingCV` |
| `numberFormat` | `NumberFormatSample` |
| `oh` | `OuterHeight` |
| `online` | `IsOnline` |
| `ori` | `ScreenOrientation` |
| `oscpu` | `Firefox_OSCPU` |
| `ow` | `OuterWidth` |
| `path` | `PagePath` |
| `pd` | `PixelRatio` |
| `pdf` | `PDFViewerEnabled` |
| `plt` | `Platform` |
| `pluginList` | `PluginListDetailed` |
| `plugins` | `PluginCount` |
| `pointer` | `PointerType` |
| `pointerEvent` | `PointerEventsSupported` |
| `product` | `NavigatorProduct` |
| `productSub` | `NavigatorProductSub` |
| `protocol` | `PageProtocol` |
| `reducedData` | `PrefersReducedData` |
| `reducedMotion` | `PrefersReducedMotion` |
| `ref` | `PageReferrer` |
| `relativeTime` | `RelativeTimeSample` |
| `rtt` | `RTTMs` |
| `sah` | `ScreenAvailHeight` |
| `save` | `DataSaverEnabled` |
| `saw` | `ScreenAvailWidth` |
| `scriptExecTime` | `ScriptExecutionTimeMs` |
| `scrollContradiction` | `ScrollContradiction` |
| `scrolled` | `UserScrolled` |
| `scrollY` | `ScrollDepthPx` |
| `sh` | `ScreenHeight` |
| `speechSynth` | `SpeechSynthesisSupported` |
| `ss` | `SessionStorageSupported` |
| `standalone` | `StandaloneDisplayMode` |
| `stealthSignals` | `StealthPluginSignals` |
| `storageQuota` | `StorageQuotaGB` |
| `storageUsed` | `StorageUsedMB` |
| `sw` | `ScreenWidth` |
| `swk` | `ServiceWorkerSupported` |
| `sx` | `ScreenX` |
| `sy` | `ScreenY` |
| `tcpTime` | `TCPConnectMs` |
| `tier` | `Tier` |
| `title` | `PageTitle` |
| `touch` | `MaxTouchPoints` |
| `touchEvent` | `TouchEventsSupported` |
| `ts` | `ClientTimestampMs` |
| `ttfb` | `TimeToFirstByteMs` |
| `tz` | `Timezone` |
| `tzLocale` | `TimezoneLocale` |
| `tzo` | `TimezoneOffsetMins` |
| `ua` | `ClientUserAgent` |
| `uaArch` | `UA_Architecture` |
| `uaBitness` | `UA_Bitness` |
| `uaBrands` | `UA_Brands` |
| `uaFormFactor` | `UA_FormFactor` |
| `uaFullVersion` | `UA_FullVersionList` |
| `uaMobile` | `UA_IsMobile` |
| `uaModel` | `UA_Model` |
| `uaPlatform` | `UA_Platform` |
| `uaPlatformVersion` | `UA_PlatformVersion` |
| `uaWow64` | `UA_IsWow64` |
| `url` | `PageURL` |
| `vendorSub` | `NavigatorVendorSub` |
| `vh` | `ViewportHeight` |
| `videoInputs` | `VideoInputDevices` |
| `vnd` | `Vendor` |
| `voices` | `SpeechVoices` |
| `vw` | `ViewportWidth` |
| `wasm` | `WebAssemblySupported` |
| `webdr` | `WebDriverDetected` |
| `webgl` | `WebGLSupported` |
| `webgl2` | `WebGL2Supported` |
| `webglEvasion` | `WebGLEvasionDetected` |
| `webglExt` | `WebGLExtensionCount` |
| `webglFP` | `WebGLFingerprint` |
| `webglParams` | `WebGLParameters` |
| `ww` | `WebWorkersSupported` |
| **Server-Side Params** | *(appended by IpBehaviorService)* |
| `_srv_subnetIps` | `Srv_SubnetIps` |
| `_srv_subnetHits` | `Srv_SubnetHits` |
| `_srv_hitsIn15s` | `Srv_HitsIn15s` |
| `_srv_lastGapMs` | `Srv_LastGapMs` |
| `_srv_subSecDupe` | `Srv_SubSecDupe` |
| `_srv_subnetAlert` | `Srv_SubnetAlert` |
| `_srv_rapidFire` | `Srv_RapidFire` |

---

## 22. Real-World Entropy Analysis (Feb 2026)

Based on analysis of **267 real (non-synthetic) production records** collected Feb 2–12, 2026 from `pixl_parsed`. 252 records have full JS payload, 15 are server-only (no JS executed).

### Fingerprint Signal Entropy (Measured)

| Signal | Distinct Values (n=252) | Theoretical Max | Measured Entropy | Assessment |
|--------|:-:|:-:|:-:|------------|
| **CanvasFingerprint** | 68 | ~10-15 bits | ~6.1 bits | **HIGH** — Best single hash signal |
| **DetectedFonts** | 62 | ~10-15 bits | ~6.0 bits | **HIGH** — Very diverse across real devices |
| **GPURenderer** | 53 | ~10-15 bits | ~5.7 bits | **HIGH** — Doubles as bot detection signal |
| **WebGLFingerprint** | 33 | ~10-15 bits | ~5.0 bits | **GOOD** — Solid secondary signal |
| **AudioFingerprintSum** | 29 | ~5-10 bits | ~4.9 bits | **GOOD** — More granular than hash |
| **AudioFingerprintHash** | 22 | ~5-10 bits | ~4.5 bits | **MODERATE** — 75% dominated by one value |
| **HardwareConcurrency** | 21 | ~3-5 bits | ~4.4 bits | **GOOD** — Wide range (1–192 cores) |
| **ErrorFingerprint** | 15 | ~3-5 bits | ~3.9 bits | **MODERATE** — Browser engine signature |
| **PixelRatio** | 11 | ~2 bits | ~3.5 bits | **MODERATE** — Better than expected |
| **Timezone** | 17 | ~5-7 bits | ~4.1 bits | **MODERATE** — Stable geo signal |
| **Platform** | 8 | ~3 bits | ~3.0 bits | **LOW-MODERATE** — Stable but coarse |
| **DeviceMemoryGB** | 3 | ~3 bits | ~1.6 bits | **LOW** — 89% report 8GB |
| **CSSFontVariantHash** | 2 | ~3-5 bits | ~0.03 bits | **NEAR ZERO** — 99.6% identical |
| **MathFingerprint** | 1 | ~2-5 bits | **0 bits** | **ZERO** — All records identical |

### Combined Fingerprint Uniqueness

Canvas + WebGL + AudioHash yields **77 distinct combinations** from 230 eligible records. Adding GPURenderer and DetectedFonts would push this well above 100 distinct groups, approaching near-unique identification even in this small dataset.

| Combo Approach | Distinct Groups | ID Rate |
|---------------|:-:|:-:|
| Canvas alone | 68 | 27% |
| Canvas + WebGL | ~60+ | ~55% |
| Canvas + WebGL + Audio | 77 | ~65% |
| Canvas + WebGL + Audio + GPU + Fonts | ~100+ | ~85%+ |

### Key Observations

**AudioFingerprintHash concentration:** One hash (`49d5a04b`) accounts for 75% of records (190/252). The OfflineAudioContext processing path is far more uniform across real hardware than lab testing suggested. Still valuable but should be weighted lower in composite scoring.

**Canvas evasion rate is high (50%):** 127 of 252 records trigger `CanvasEvasionDetected`. This rate is likely too aggressive — warrants threshold calibration.

**SwiftShader + 800x600 bot cluster:** ~20% of records show software GPU renderers (SwiftShader, llvmpipe, Microsoft Basic Render Driver) combined with 800x600 screen resolution — near-certain headless automation markers.

**Linux x86_64 at 33%** is far above typical web traffic (~1-2% for desktop Linux), confirming significant bot/scanner traffic from Linux servers.

**High core counts (48-192)** in 30 records indicate cloud VM environments, reinforcing the bot-heavy traffic profile.

**Repeat visitor stability:** IPs with 2-4 visits and identical fingerprints across sessions confirm that Canvas, WebGL, and Audio fingerprints are stable for the same device. Multi-fingerprint IPs (e.g., 108.214.110.52 with 55 visits, 4 Canvas FPs) represent NAT/office environments with multiple machines.

### Recommendations from Real-World Data

1. **Replace or deprioritize MathFingerprint** — zero entropy; consider `Intl.ListFormat` patterns or `performance.now()` precision
2. **Rework CSSFontVariantHash** — current implementation produces near-uniform output; test more CSS properties
3. **Recalibrate canvas evasion threshold** — 50% positive rate suggests false positives
4. **Weight composite scoring:** Canvas (15) > Fonts (15) > WebGL (15) > GPU (10) > Audio (5) > Error (5)
5. **Proactively flag SwiftShader + 800x600** as a high-confidence bot cluster

---

## 23. Data Dictionary (`pixl_parsed`)

Complete standardized data dictionary for all 175 columns in the `pixl_parsed` materialized table. Ordered by `ORDINAL_POSITION` as stored in the database.

### Legend

| Abbreviation | Meaning |
|:---:|---------|
| PK/FK | Primary Key / Foreign Key reference |
| Calc | Calculated/computed field |
| Srv | Server-side only (not from JS) |
| JS | Populated from client-side JavaScript |
| CH | Client Hints (Chromium only) |

---

### Identity & Server Context

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 1 | `SourceId` | int | NO | Srv/FK | FK to `PiXL_Test.Id`. Auto-increment PK in the raw table. |
| 2 | `CompanyID` | nvarchar(100) | YES | Srv | Client company identifier extracted from the URL route. |
| 3 | `PiXLID` | nvarchar(100) | YES | Srv | Campaign/pixel identifier extracted from the URL route. |
| 4 | `IPAddress` | nvarchar(50) | YES | Srv | Visitor IP address from `X-Forwarded-For` header or `RemoteIpAddress`. |
| 5 | `ReceivedAt` | datetime2 | NO | Srv | Server UTC timestamp when the pixel hit was received. |
| 6 | `RequestPath` | nvarchar(500) | YES | Srv | HTTP request path (e.g., `/t/companyX/pixelY`). |
| 7 | `ServerUserAgent` | nvarchar(2000) | YES | Srv | User-Agent string from the HTTP request header (server-side). |
| 8 | `ServerReferer` | nvarchar(2000) | YES | Srv | Referer URL from the HTTP request header (server-side). |
| 9 | `IsSynthetic` | bit | NO | Calc | `1` if test/synthetic traffic, `0` if real. Derived from `synthetic` query param. |
| 10 | `Tier` | int | YES | JS | Script identifier. Currently always `5` for full JS hits. NULL for server-only hits. |

### Screen & Display

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 11 | `ScreenWidth` | int | YES | JS | `screen.width` — physical screen width in pixels. |
| 12 | `ScreenHeight` | int | YES | JS | `screen.height` — physical screen height in pixels. |
| 13 | `ScreenAvailWidth` | int | YES | JS | `screen.availWidth` — available width excluding OS chrome (taskbar/dock). |
| 14 | `ScreenAvailHeight` | int | YES | JS | `screen.availHeight` — available height excluding OS chrome (taskbar/dock). |
| 15 | `ViewportWidth` | int | YES | JS | `window.innerWidth` — CSS viewport width in pixels. |
| 16 | `ViewportHeight` | int | YES | JS | `window.innerHeight` — CSS viewport height in pixels. |
| 17 | `OuterWidth` | int | YES | JS | `window.outerWidth` — browser window width including chrome. |
| 18 | `OuterHeight` | int | YES | JS | `window.outerHeight` — browser window height including chrome. |
| 19 | `ScreenX` | int | YES | JS | `window.screenX` / `screenLeft` — window X position on screen. |
| 20 | `ScreenY` | int | YES | JS | `window.screenY` / `screenTop` — window Y position on screen. |
| 21 | `ColorDepth` | int | YES | JS | `screen.colorDepth` — bits per pixel (24, 30, 32 typical). |
| 22 | `PixelRatio` | decimal(5,2) | YES | JS | `devicePixelRatio` — display scaling factor (1.0, 1.25, 1.5, 2.0, 3.0). |
| 23 | `ScreenOrientation` | nvarchar(50) | YES | JS | `screen.orientation.type` — e.g., `landscape-primary`, `portrait-primary`. |

### Locale & Internationalization

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 24 | `Timezone` | nvarchar(100) | YES | JS | IANA timezone identifier (e.g., `America/New_York`, `UTC`). |
| 25 | `TimezoneOffsetMins` | int | YES | JS | UTC offset in minutes from `new Date().getTimezoneOffset()`. Negative = ahead of UTC. |
| 26 | `ClientTimestampMs` | bigint | YES | JS | Client-side epoch timestamp in milliseconds from `new Date().getTime()`. |
| 27 | `TimezoneLocale` | nvarchar(200) | YES | JS | Resolved locale info: `locale\|calendar\|numberingSystem\|hourCycle`. |
| 28 | `DateFormatSample` | nvarchar(200) | YES | JS | `Intl.DateTimeFormat` output for a fixed reference date (2024-01-15). |
| 29 | `NumberFormatSample` | nvarchar(200) | YES | JS | `Intl.NumberFormat().format(1234567.89)` — locale-specific number formatting. |
| 30 | `RelativeTimeSample` | nvarchar(200) | YES | JS | `Intl.RelativeTimeFormat().format(-1, 'day')` — locale-specific relative time. |
| 31 | `Language` | nvarchar(50) | YES | JS | Primary language from `navigator.language` (e.g., `en-US`). |
| 32 | `LanguageList` | nvarchar(500) | YES | JS | All accepted languages from `navigator.languages`, comma-separated. |

### Browser & Navigator

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 33 | `Platform` | nvarchar(100) | YES | JS | `navigator.platform` — OS platform string (`Win32`, `MacIntel`, `Linux x86_64`, `iPhone`). |
| 34 | `Vendor` | nvarchar(200) | YES | JS | `navigator.vendor` — browser vendor (`Google Inc.`, `Apple Computer, Inc.`, empty). |
| 35 | `ClientUserAgent` | nvarchar(2000) | YES | JS | Full `navigator.userAgent` string (client-side, may differ from server-side). |
| 36 | `HardwareConcurrency` | int | YES | JS | `navigator.hardwareConcurrency` — CPU logical core count. Range: 1–192 observed. |
| 37 | `DeviceMemoryGB` | decimal(5,2) | YES | JS | `navigator.deviceMemory` — approximate RAM in GB (0.25, 0.5, 1, 2, 4, 8). Chromium only. |
| 38 | `MaxTouchPoints` | int | YES | JS | `navigator.maxTouchPoints` — touch capability (0=none, 1-10=touch, 256=pen). |
| 39 | `NavigatorProduct` | nvarchar(50) | YES | JS | `navigator.product` — always `Gecko` in modern browsers. |
| 40 | `NavigatorProductSub` | nvarchar(50) | YES | JS | `navigator.productSub` — typically `20030107` (Chrome) or `20100101` (Firefox). |
| 41 | `NavigatorVendorSub` | nvarchar(200) | YES | JS | `navigator.vendorSub` — usually empty string. |
| 42 | `AppName` | nvarchar(100) | YES | JS | `navigator.appName` — usually `Netscape` in all modern browsers. |
| 43 | `AppVersion` | nvarchar(500) | YES | JS | `navigator.appVersion` — version string (legacy, mirrors UA). |
| 44 | `AppCodeName` | nvarchar(100) | YES | JS | `navigator.appCodeName` — always `Mozilla` in modern browsers. |

### GPU & WebGL

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 45 | `GPURenderer` | nvarchar(500) | YES | JS | Unmasked GPU renderer string from `WEBGL_debug_renderer_info` (e.g., `ANGLE (NVIDIA, NVIDIA GeForce RTX 4090 ...)`). |
| 46 | `GPUVendor` | nvarchar(200) | YES | JS | Unmasked GPU vendor from `WEBGL_debug_renderer_info` (e.g., `Google Inc.`, `NVIDIA Corporation`). |
| 47 | `WebGLParameters` | nvarchar(2000) | YES | JS | First 5 WebGL params: `VERSION\|SHADING_LANGUAGE\|VENDOR\|RENDERER\|MAX_VERTEX_ATTRIBS`. |
| 48 | `WebGLExtensionCount` | int | YES | JS | Number of supported WebGL extensions. Varies by GPU capability. |
| 49 | `WebGLSupported` | bit | YES | JS | `1` if WebGL 1.0 context can be created. |
| 50 | `WebGL2Supported` | bit | YES | JS | `1` if WebGL 2.0 context can be created. |

### Fingerprint Hashes

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 51 | `CanvasFingerprint` | nvarchar(200) | YES | JS | Hex hash of canvas 2D rendering output. 68 distinct values in production. |
| 52 | `WebGLFingerprint` | nvarchar(200) | YES | JS | Hex hash of 23 WebGL parameter values. 33 distinct values in production. |
| 53 | `AudioFingerprintSum` | nvarchar(200) | YES | JS | Sum of OfflineAudioContext frequency bin samples (6 decimal places). 29 distinct values. |
| 54 | `AudioFingerprintHash` | nvarchar(200) | YES | JS | Hex hash of full audio sample data. 22 distinct values; 75% dominated by one value. |
| 55 | `MathFingerprint` | nvarchar(200) | YES | JS | Hash of `Math.*` function precision. **Zero entropy** — all records identical. |
| 56 | `ErrorFingerprint` | nvarchar(200) | YES | JS | Error message/stack length signature from `null[0]()`. 15 distinct values. |
| 57 | `CSSFontVariantHash` | nvarchar(200) | YES | JS | Hash of CSS font-variant computed values. Near-zero entropy (2 values, 99.6% identical). |

### Fonts, Plugins & Media

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 58 | `DetectedFonts` | nvarchar(4000) | YES | JS | Comma-separated list of detected installed fonts from width-measurement test (~42 fonts). 62 distinct combos. |
| 59 | `PluginCount` | int | YES | JS | Count of entries in `navigator.plugins`. |
| 60 | `PluginListDetailed` | nvarchar(4000) | YES | JS | `name::filename::description` pipe-separated plugin details (up to 20). |
| 61 | `MimeTypeCount` | int | YES | JS | Count of entries in `navigator.mimeTypes`. |
| 62 | `MimeTypeList` | nvarchar(4000) | YES | JS | Comma-separated MIME type strings (up to 30). |
| 63 | `SpeechVoices` | nvarchar(4000) | YES | JS | `speechSynthesis.getVoices()` — `name/lang` pipe-separated (up to 20). |
| 64 | `ConnectedGamepads` | nvarchar(1000) | YES | JS | Pipe-separated gamepad IDs from `navigator.getGamepads()`. |

### Network & Connection

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 65 | `WebRTCLocalIP` | nvarchar(50) | YES | JS | Local network IP from WebRTC ICE candidate (e.g., `192.168.1.5`). Blocked by some browsers. |
| 66 | `ConnectionType` | nvarchar(50) | YES | JS | `navigator.connection.effectiveType` — `4g`, `3g`, `2g`, `slow-2g`. Chromium only. |
| 67 | `DownlinkMbps` | decimal(10,2) | YES | JS | `navigator.connection.downlink` — estimated bandwidth in Mbps. |
| 68 | `DownlinkMax` | nvarchar(50) | YES | JS | `navigator.connection.downlinkMax` — maximum downlink speed. |
| 69 | `RTTMs` | int | YES | JS | `navigator.connection.rtt` — round-trip time estimate in milliseconds. |
| 70 | `DataSaverEnabled` | bit | YES | JS | `navigator.connection.saveData` — `1` if data saver is active. |
| 71 | `NetworkType` | nvarchar(50) | YES | JS | `navigator.connection.type` — `wifi`, `cellular`, `ethernet`, `none`. |
| 72 | `IsOnline` | bit | YES | JS | `navigator.onLine` — `1` if browser reports network connectivity. |

### Storage & Media Devices

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 73 | `StorageQuotaGB` | int | YES | JS | `navigator.storage.estimate()` quota converted to GB. |
| 74 | `StorageUsedMB` | int | YES | JS | `navigator.storage.estimate()` usage converted to MB. |
| 75 | `LocalStorageSupported` | bit | YES | JS | `1` if `window.localStorage` is accessible. |
| 76 | `SessionStorageSupported` | bit | YES | JS | `1` if `window.sessionStorage` is accessible. |
| 77 | `IndexedDBSupported` | bit | YES | JS | `1` if `window.indexedDB` is accessible. |
| 78 | `CacheAPISupported` | bit | YES | JS | `1` if `window.caches` (CacheStorage API) is accessible. |
| 79 | `BatteryLevelPct` | int | YES | JS | Battery charge percentage (0–100). Only available via Battery API (Chromium). |
| 80 | `BatteryCharging` | bit | YES | JS | `1` if device is plugged in / charging. |
| 81 | `AudioInputDevices` | int | YES | JS | Microphone count from `navigator.mediaDevices.enumerateDevices()`. |
| 82 | `VideoInputDevices` | int | YES | JS | Camera count from `navigator.mediaDevices.enumerateDevices()`. |

### API Capabilities (Boolean Flags)

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 83 | `CookiesEnabled` | bit | YES | JS | `1` if `navigator.cookieEnabled` is true. |
| 84 | `DoNotTrack` | nvarchar(50) | YES | JS | `navigator.doNotTrack` value — `1`, `0`, `unspecified`, or NULL. |
| 85 | `PDFViewerEnabled` | bit | YES | JS | `1` if built-in PDF viewer is active (`navigator.pdfViewerEnabled`). |
| 86 | `WebDriverDetected` | bit | YES | JS | `1` if `navigator.webdriver` is `true` — strong automation indicator. |
| 87 | `JavaEnabled` | bit | YES | JS | `1` if `navigator.javaEnabled()` returns true. |
| 88 | `CanvasSupported` | bit | YES | JS | `1` if Canvas 2D rendering context can be created. |
| 89 | `WebAssemblySupported` | bit | YES | JS | `1` if `WebAssembly` global object exists. |
| 90 | `WebWorkersSupported` | bit | YES | JS | `1` if `Worker` constructor exists. |
| 91 | `ServiceWorkerSupported` | bit | YES | JS | `1` if `navigator.serviceWorker` exists. |
| 92 | `MediaDevicesAPISupported` | bit | YES | JS | `1` if `navigator.mediaDevices` exists. |
| 93 | `ClipboardAPISupported` | bit | YES | JS | `1` if `navigator.clipboard.writeText` exists. |
| 94 | `SpeechSynthesisSupported` | bit | YES | JS | `1` if `window.speechSynthesis` exists. |
| 95 | `TouchEventsSupported` | bit | YES | JS | `1` if `ontouchstart` is in `window`. |
| 96 | `PointerEventsSupported` | bit | YES | JS | `1` if `PointerEvent` constructor exists. |

### Accessibility & Preferences

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 97 | `HoverCapable` | bit | YES | JS | `1` if `(hover: hover)` media query matches — points to mouse/trackpad input. |
| 98 | `PointerType` | nvarchar(20) | YES | JS | Primary pointer type: `fine` (mouse), `coarse` (touch), empty (none). |
| 99 | `PrefersColorSchemeDark` | bit | YES | JS | `1` if `prefers-color-scheme: dark` media query matches. |
| 100 | `PrefersColorSchemeLight` | bit | YES | JS | `1` if `prefers-color-scheme: light` media query matches. |
| 101 | `PrefersReducedMotion` | bit | YES | JS | `1` if `prefers-reduced-motion: reduce` media query matches. |
| 102 | `PrefersReducedData` | bit | YES | JS | `1` if `prefers-reduced-data: reduce` media query matches. |
| 103 | `PrefersHighContrast` | bit | YES | JS | `1` if `prefers-contrast: more` media query matches. |
| 104 | `ForcedColorsActive` | bit | YES | JS | `1` if `forced-colors: active` — Windows High Contrast mode. |
| 105 | `InvertedColorsActive` | bit | YES | JS | `1` if `inverted-colors: inverted` media query matches. |
| 106 | `StandaloneDisplayMode` | bit | YES | JS | `1` if `(display-mode: standalone)` — running as PWA. |

### Document State

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 107 | `DocumentCharset` | nvarchar(50) | YES | JS | `document.characterSet` (e.g., `UTF-8`). |
| 108 | `DocumentCompatMode` | nvarchar(50) | YES | JS | `CSS1Compat` (standards mode) or `BackCompat` (quirks mode). |
| 109 | `DocumentReadyState` | nvarchar(50) | YES | JS | `loading`, `interactive`, or `complete`. |
| 110 | `DocumentHidden` | bit | YES | JS | `1` if `document.hidden` — tab is backgrounded at pixel fire time. |
| 111 | `DocumentVisibility` | nvarchar(50) | YES | JS | `document.visibilityState` — `visible`, `hidden`, or `prerender`. |

### Page Context

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 112 | `PageURL` | nvarchar(2000) | YES | JS | `location.href` — full page URL at pixel fire time. |
| 113 | `PageReferrer` | nvarchar(2000) | YES | JS | `document.referrer` — client-side referrer URL. |
| 114 | `PageTitle` | nvarchar(1000) | YES | JS | `document.title` — page title. |
| 115 | `PageDomain` | nvarchar(500) | YES | JS | `location.hostname` — domain of the hosting page. |
| 116 | `PagePath` | nvarchar(1000) | YES | JS | `location.pathname` — path component of the URL. |
| 117 | `PageHash` | nvarchar(500) | YES | JS | `location.hash` — URL fragment/anchor. |
| 118 | `PageProtocol` | nvarchar(20) | YES | JS | `location.protocol` — `http:` or `https:`. |
| 119 | `HistoryLength` | int | YES | JS | `history.length` — browsing session depth in the current tab. |

### Performance Timing

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 120 | `PageLoadTimeMs` | int | YES | JS | `loadEventEnd - navigationStart` from Performance Timing API. |
| 121 | `DOMReadyTimeMs` | int | YES | JS | `domContentLoadedEventEnd - navigationStart`. |
| 122 | `DNSLookupMs` | int | YES | JS | `domainLookupEnd - domainLookupStart`. |
| 123 | `TCPConnectMs` | int | YES | JS | `connectEnd - connectStart`. |
| 124 | `TimeToFirstByteMs` | int | YES | JS | `responseStart - requestStart` (TTFB). |

### Bot Detection

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 125 | `BotSignalsList` | nvarchar(4000) | YES | JS | Comma-separated list of triggered bot signal names. |
| 126 | `BotScore` | int | YES | JS | Composite bot likelihood score (0–100). Sum of triggered signal weights, capped at 100. |
| 127 | `CombinedThreatScore` | int | YES | JS | `BotScore + min(AnomalyScore, 25)`. Bridges bot + anomaly detection. |
| 128 | `ScriptExecutionTimeMs` | int | YES | JS | Milliseconds from page load to script completion. <10ms is near-certain bot. |
| 129 | `BotPermissionInconsistent` | bit | YES | JS | `1` if Permission API returns inconsistent state. |

### Evasion Detection

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 130 | `CanvasEvasionDetected` | bit | YES | JS | `1` if canvas pixel data variance is 0 or dataURL suspiciously short. |
| 131 | `WebGLEvasionDetected` | bit | YES | JS | `1` if GPU is a software renderer (SwiftShader, llvmpipe, Mesa). |
| 132 | `EvasionToolsDetected` | nvarchar(1000) | YES | JS | Comma-separated evasion tool identifiers (e.g., `tor-likely`, `brave`, `ua-platform-mismatch`). |
| 133 | `ProxyBlockedProperties` | nvarchar(1000) | YES | JS | Navigator properties blocked by JS Proxy privacy extensions (JShelter, Trace). |

### Client Hints (Chromium Only)

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 134 | `UA_Architecture` | nvarchar(50) | YES | CH | CPU architecture — `x86`, `arm`. |
| 135 | `UA_Bitness` | nvarchar(10) | YES | CH | CPU bitness — `32` or `64`. |
| 136 | `UA_Model` | nvarchar(200) | YES | CH | Device model (mobile only) — e.g., `Pixel 7`, `SM-S918B`. |
| 137 | `UA_PlatformVersion` | nvarchar(100) | YES | CH | OS version string (e.g., `15.0.0`, `10.0.0`). |
| 138 | `UA_FullVersionList` | nvarchar(500) | YES | CH | Full browser version list with patch numbers. |
| 139 | `UA_IsWow64` | bit | YES | CH | `1` if 32-bit app running on 64-bit OS (WoW64). |
| 140 | `UA_IsMobile` | bit | YES | CH | `1` if mobile device per Client Hints. |
| 141 | `UA_Platform` | nvarchar(50) | YES | CH | OS name — `Windows`, `macOS`, `Android`, `Linux`. |
| 142 | `UA_Brands` | nvarchar(500) | YES | CH | Low-entropy brand list (`Chromium/120\|Google Chrome/120`). |
| 143 | `UA_FormFactor` | nvarchar(100) | YES | CH | Device form factor — `Desktop`, `Mobile`, `Tablet`. |

### Browser-Specific Fields

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 144 | `Firefox_OSCPU` | nvarchar(200) | YES | JS | `navigator.oscpu` — Firefox-only OS/CPU string. |
| 145 | `Firefox_BuildID` | nvarchar(100) | YES | JS | Firefox build timestamp (may be frozen `20181001000000` for privacy). |
| 146 | `Chrome_ObjectPresent` | bit | YES | JS | `1` if `window.chrome` object exists. |
| 147 | `Chrome_RuntimePresent` | bit | YES | JS | `1` if `window.chrome.runtime` exists (extension context). |
| 148 | `Chrome_JSHeapSizeLimit` | bigint | YES | JS | `performance.memory.jsHeapSizeLimit` — V8 heap limit in bytes. Chrome only. |
| 149 | `Chrome_TotalJSHeapSize` | bigint | YES | JS | `performance.memory.totalJSHeapSize` — total allocated heap. Chrome only. |
| 150 | `Chrome_UsedJSHeapSize` | bigint | YES | JS | `performance.memory.usedJSHeapSize` — actively used heap. Chrome only. |

### Fingerprint Stability & Evasion Signals

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 151 | `CanvasConsistency` | nvarchar(50) | YES | JS | Canvas noise detection: `clean` (normal), `noise-detected` (Canvas Blocker), `canvas-blocked`, `error`. |
| 152 | `AudioIsStable` | bit | YES | JS | `1` if two audio fingerprint runs match. `0` if unstable. |
| 153 | `AudioNoiseInjectionDetected` | bit | YES | JS | `1` if two audio runs differ — noise injection extension detected. |

### Behavioral Biometrics

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 154 | `MouseMoveCount` | int | YES | JS | Number of mouse movement events captured in ~500ms window. |
| 155 | `UserScrolled` | bit | YES | JS | `1` if user scrolled within the capture window. |
| 156 | `ScrollDepthPx` | int | YES | JS | `window.scrollY` at pixel fire time. |
| 157 | `MouseEntropy` | int | YES | JS | Mouse movement angle variance × 1000. `0` if < 5 moves. |
| 158 | `ScrollContradiction` | bit | YES | JS | `1` if scroll event fired but `scrollY` = 0 — bot indicator. |
| 159 | `MoveTimingCV` | int | YES | JS | Coefficient of variation of time between mouse moves × 1000. |
| 160 | `MoveSpeedCV` | int | YES | JS | Coefficient of variation of mouse movement speed × 1000. |
| 161 | `MoveCountBucket` | nvarchar(20) | YES | JS | Categorical bucket: `low`, `mid`, `high`, `very-high`. |
| 162 | `BehavioralFlags` | nvarchar(200) | YES | JS | Behavioral analysis flags (e.g., `uniform-timing`, `uniform-speed`). |

### Advanced Evasion & Cross-Signal

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 163 | `StealthPluginSignals` | nvarchar(500) | YES | JS | Stealth plugin detection: `webdriver-slow`, `toString-spoofed`, `nav-proto-modified`. |
| 164 | `FontMethodMismatch` | bit | YES | JS | `1` if `offsetWidth` vs `getBoundingClientRect` disagree — font spoofing indicator. |
| 165 | `EvasionSignalsV2` | nvarchar(500) | YES | JS | Enhanced signals: `tor-letterbox-viewport`, `canvas-noise`, `stealth-detected`. |
| 166 | `CrossSignalFlags` | nvarchar(500) | YES | JS | Cross-signal inconsistency flags (e.g., `win-fonts-on-mac`, `swiftshader-gpu`, `heap-mismatch`). |
| 167 | `AnomalyScore` | int | YES | JS | Cumulative cross-signal anomaly score. Contribution to `CombinedThreatScore` capped at 25. |

### Parse Metadata

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 168 | `ParsedAt` | datetime2 | NO | Srv | UTC timestamp when this record was parsed from `PiXL_Test` into `pixl_parsed` by `DatabaseWriterService`. |

### IP Behavior Analysis (Server-Side)

| # | Column Name | SQL Type | Nullable | Source | Description |
|:-:|-------------|----------|:--------:|:------:|-------------|
| 169 | `Srv_SubnetIps` | int | YES | Srv | Unique IPs from same /24 subnet in 5-minute window. |
| 170 | `Srv_SubnetHits` | int | YES | Srv | Total hits from same /24 subnet in 5-minute window. |
| 171 | `Srv_HitsIn15s` | int | YES | Srv | Hits from same IP in 15-second window. |
| 172 | `Srv_LastGapMs` | bigint | YES | Srv | Milliseconds since last hit from same IP. `-1` if first hit. |
| 173 | `Srv_SubSecDupe` | bit | YES | Srv | `1` if sub-second duplicate from same IP (< 1000ms gap). |
| 174 | `Srv_SubnetAlert` | bit | YES | Srv | `1` if subnet /24 velocity alert: 3+ unique IPs in same /24 in 5min. |
| 175 | `Srv_RapidFire` | bit | YES | Srv | `1` if rapid-fire alert: 3+ hits from same IP in 15 seconds. |

---

*Last verified: 2026-02-12 against PiXLScript.cs (160+ params), pixl_parsed (175 columns), vw_PiXL_Complete (176 columns), SQL Migration 17, and 267 real production records.*
