# SmartPiXL Field Reference

Complete reference for all fingerprinting fields collected by SmartPiXL.
Used by SQL views, diagnostics dashboard, and client reporting.

---

## Quick Navigation

- [Identity & Context](#identity--context)
- [Fingerprint Signals](#fingerprint-signals)
- [Bot Detection](#bot-detection)
- [Evasion Detection](#evasion-detection)
- [Device & Hardware](#device--hardware)
- [Screen & Display](#screen--display)
- [Browser & Navigator](#browser--navigator)
- [Client Hints](#client-hints)
- [Network & Connection](#network--connection)
- [Performance Timing](#performance-timing)
- [API Capabilities](#api-capabilities)
- [Accessibility & Preferences](#accessibility--preferences)
- [Locale & Internationalization](#locale--internationalization)
- [Page Context](#page-context)
- [Raw Data](#raw-data)

---

## Identity & Context

| Field | Type | Description | Indicates |
|-------|------|-------------|-----------|
| `Id` | int | Auto-increment primary key | Unique record identifier |
| `CompanyID` | string | Client company identifier | Which client's pixel triggered this |
| `PiXLID` | string | Specific pixel identifier | Which campaign/page is being tracked |
| `IPAddress` | string | Visitor's external IP | Geographic location, ISP, potential household grouping |
| `ReceivedAt` | datetime | Server timestamp when request was received | Session timing, time-of-day patterns |
| `Tier` | int | Script complexity tier (always 5) | Data collection level |

---

## Fingerprint Signals

### Canvas Fingerprint
| Field | Type | Description | Indicates | Entropy |
|-------|------|-------------|-----------|---------|
| `CanvasFingerprint` | string | Hash of canvas rendering output | GPU + driver + OS + fonts unique combination | ~10-15 bits |
| `CanvasSupported` | bool | Whether canvas is available | Browser capability | 1 bit |
| `CanvasEvasionDetected` | bool | Whether canvas appears blocked/spoofed | Privacy tools in use (Brave, Canvas Blocker, Tor) | High risk signal |

**UI Drill-Down:** When clicking CanvasFingerprint, show:
- Distribution of unique hashes
- Most common hashes (potential bot signatures)
- Evasion rate (% with CanvasEvasionDetected = true)

### WebGL Fingerprint
| Field | Type | Description | Indicates | Entropy |
|-------|------|-------------|-----------|---------|
| `WebGLFingerprint` | string | Hash of WebGL parameters | GPU capabilities unique signature | ~10-15 bits |
| `WebGLSupported` | bool | Whether WebGL is available | Browser capability | 1 bit |
| `WebGL2Supported` | bool | Whether WebGL 2.0 is available | Modern browser indicator | 1 bit |
| `WebGLEvasionDetected` | bool | Whether WebGL appears blocked/spoofed | Privacy tools or headless browser | High risk signal |
| `WebGLExtensionCount` | int | Number of supported WebGL extensions | GPU capability level | ~3-5 bits |
| `WebGLParameters` | string | Raw WebGL parameter values | Detailed GPU capability debugging | N/A |
| `GPURenderer` | string | GPU model string (e.g., "NVIDIA GeForce RTX 4090") | Exact hardware, desktop vs laptop | ~8-10 bits |
| `GPUVendor` | string | GPU vendor (NVIDIA, AMD, Intel, Apple) | Hardware brand, potential segmentation | ~3 bits |

**UI Drill-Down:** When clicking WebGLFingerprint, show:
- Top GPU models (GPURenderer breakdown)
- Vendor distribution (GPUVendor pie chart)
- Extension count histogram
- Blocked/spoofed rate

### Audio Fingerprint
| Field | Type | Description | Indicates | Entropy |
|-------|------|-------------|-----------|---------|
| `AudioFingerprintHash` | string | Hash of audio processing output | Audio stack uniqueness | ~5-8 bits |
| `AudioFingerprintSum` | float | Raw sum of audio frequency bins | Audio processing precision | Debugging |
| `AudioInputDevices` | int | Count of audio input devices (microphones) | Desktop vs laptop vs headset user | ~2-3 bits |

**UI Drill-Down:** When clicking AudioFingerprint, show:
- Unique hash distribution
- Input device count histogram
- Cross-reference with VideoInputDevices

### Font Fingerprint
| Field | Type | Description | Indicates | Entropy |
|-------|------|-------------|-----------|---------|
| `DetectedFonts` | string | Comma-separated list of detected fonts | OS, installed software (Office, Adobe, etc.) | ~10-15 bits |
| `CSSFontVariantHash` | string | Hash of CSS font rendering properties | Font rendering engine differences | ~3-5 bits |

**UI Drill-Down:** When clicking DetectedFonts, show:
- Most common font sets (Windows vs Mac vs Linux)
- "Office installed" indicator (Calibri, Cambria present)
- Font count distribution

### Math Fingerprint
| Field | Type | Description | Indicates | Entropy |
|-------|------|-------------|-----------|---------|
| `MathFingerprint` | string | Hash of Math function precision | JavaScript engine + CPU architecture | ~3-5 bits |

**What it captures:** Results of `Math.tan(-1e300)`, `Math.sinh(1)`, etc. Different CPUs and JS engines produce slightly different floating-point results.

### Error Fingerprint
| Field | Type | Description | Indicates | Entropy |
|-------|------|-------------|-----------|---------|
| `ErrorFingerprint` | string | Hash of error message formats | Browser engine (V8, SpiderMonkey, JavaScriptCore) | ~3-5 bits |

**What it captures:** How different browsers format error stack traces and messages. Chrome says "TypeError: x is not a function" while Firefox says "x is not a function".

### Speech Synthesis Voices
| Field | Type | Description | Indicates | Entropy |
|-------|------|-------------|-----------|---------|
| `InstalledVoices` | string | List of speech synthesis voices | OS, language packs installed | ~5-8 bits |

**What it captures:** `speechSynthesis.getVoices()` returns the installed text-to-speech voices. Format: `name/lang|name/lang` (up to 20 voices). 

**Why it matters:**
- Windows has different default voices than macOS
- Additional language packs reveal user's language interests
- Some VMs have no voices installed (bot indicator)

---

## Bot Detection

| Field | Type | Description | Indicates | Risk Weight |
|-------|------|-------------|-----------|-------------|
| `BotScore` | int | Composite bot likelihood score (0-100) | Overall automation probability | PRIMARY |
| `BotSignalsList` | string | Comma-separated list of triggered signals | Which specific bot indicators fired | DIAGNOSTIC |
| `ScriptExecTimeMs` | int | Milliseconds from page load to script execution | Bot timing signature | KEY SIGNAL |
| `WebDriverDetected` | bool | `navigator.webdriver` is true | Selenium, Puppeteer, Playwright | +10 risk |
| `Chrome_ObjectPresent` | bool | `window.chrome` object exists | Real Chrome vs headless | +8 if false in Chrome UA |
| `Chrome_RuntimePresent` | bool | `window.chrome.runtime` exists | Extension context available | +3 if false in Chrome UA |
| `BotPermInconsistent` | bool | Permission API returns inconsistent state | Headless browser quirk | +5 risk |

### Script Execution Time (KEY BOT INDICATOR)

| ScriptExecTime | Likelihood | Explanation |
|----------------|------------|-------------|
| < 10ms | 🔴 Almost certainly bot | Instant DOM, no network stack, pre-rendered |
| 10-50ms | 🟡 Suspicious | Could be fast cache + SSD, but rare |
| 50-200ms | 🟢 Normal human | Real browser with network, parsing, execution |
| > 200ms | 🟢 Definitely human | Slow connection or device |

### Bot Signal Reference

All possible values in `BotSignalsList`:

| Signal | Score | Description |
|--------|-------|-------------|
| `webdriver` | +10 | `navigator.webdriver` is true |
| `headless-no-chrome-obj` | +8 | Chrome UA but no `window.chrome` object |
| `minimal-ua` | +15 | User-Agent < 30 characters (bots often use short UA) |
| `fake-ua` | +20 | UA matches known fake pattern (e.g., "desktop", "mobile") |
| `phantom` | +8 | PhantomJS artifacts detected |
| `nightmare` | +8 | Nightmare.js artifacts detected |
| `selenium` | +10 | Selenium artifacts detected (`__selenium_*`) |
| `cdp` | +10 | Chrome DevTools Protocol variables detected |
| `playwright-global` | +10 | `__playwright` or `__pw_manual` detected |
| `empty-languages` | +5 | `navigator.languages` is empty array |
| `plugin-mime-mismatch` | +3 | Plugins empty but mimeTypes present |
| `zero-screen` | +8 | Screen dimensions are 0 |
| `no-plugins` | +2 | No browser plugins (rare for real users) |
| `dom-automation` | +10 | `domAutomation` or `domAutomationController` present |
| `outer-zero` | +5 | `outerWidth` is 0 but `innerWidth` > 0 |
| `nav-*` | +10 | Automation property in navigator object |
| `fn-tampered` | +5 | Native functions appear tampered |
| `default-viewport` | +2 | Common headless viewport (1280x720, 800x600) |
| `headless-ua` | +10 | "HeadlessChrome" in User-Agent |
| `perm-inconsistent` | +5 | Permission API returns inconsistent state |
| `chrome-no-runtime` | +3 | Chrome object but no runtime (headless) |
| `fullscreen-match` | +2 | Screen equals window equals available (VM/headless) |
| `no-connection-api` | +3 | Chrome browser but no Connection API |
| `eval-tampered` | +5 | `eval` function has been overridden |

**Bot Score Calculation:**
```
BotScore = sum of all triggered signal scores (capped at 100)
```

**UI Drill-Down:** When clicking BotScore summary:
1. Show risk bucket distribution (High/Medium/Low/Human)
2. Click bucket → Show which signals are most common in that bucket
3. Click signal → Show devices with that signal, time patterns

**Risk Buckets for Display:**
| Bucket | Score Range | Color | Description |
|--------|-------------|-------|-------------|
| High Risk | 80-100 | Red | Almost certainly automated |
| Medium Risk | 50-79 | Orange | Suspicious, needs review |
| Low Risk | 20-49 | Yellow | Minor anomalies |
| Likely Human | 0-19 | Green | Normal behavior |

---

## Evasion Detection

| Field | Type | Description | Indicates |
|-------|------|-------------|-----------|
| `CanvasEvasionDetected` | bool | Canvas returns blank/noise | Canvas Blocker, Brave Shields, Tor Browser |
| `WebGLEvasionDetected` | bool | WebGL returns generic values | WebGL blocking, Tor Browser |
| `EvasionToolsDetected` | string | Detected privacy tools | Specific tool identification |
| `ProxyBlockedProperties` | string | Navigator properties blocked by Proxy | Privacy extension (JShelter, Trace, Privacy Badger) |

**Detection Methods:**
- **Canvas noise:** Variance in pixel data is 0 or data URL is too short
- **WebGL blocking:** Renderer is "Unknown" or generic ANGLE
- **Tor Browser:** Screen is exactly 1000x900, WebGL disabled
- **Proxy blocking:** Privacy extensions wrap `navigator` in JavaScript Proxy that throws on property access

### Privacy Extension Detection (ProxyBlockedProperties)

| Field | Type | Description | Indicates |
|-------|------|-------------|-----------|
| `ProxyBlockedProperties` | string | Comma-separated list of blocked navigator properties | Which APIs the extension blocks |
| `ProxyBlockedCount` | int | Count of blocked properties (computed) | Extension aggressiveness level |

**How it works:**
Privacy extensions like JShelter, Trace, and Privacy Badger wrap `navigator` in a JavaScript Proxy. When the script tries to access properties like `navigator.javaEnabled` or `navigator.platform`, the Proxy throws a TypeError:
```
TypeError: 'get' on proxy: property 'javaEnabled' is a read-only and non-configurable data property...
```

Our `safeGet()` helper catches these errors and records which properties were blocked:
```javascript
var safeGet = function(obj, prop, fallback) {
    try {
        return obj[prop];
    } catch(e) {
        data._proxyBlocked = (data._proxyBlocked || '') + prop + ',';
        return fallback;
    }
};
```

**Example values:**
- `"javaEnabled,"` - Only javaEnabled blocked
- `"javaEnabled,platform,languages,userAgent,"` - Aggressive blocking
- Empty/null - No privacy extension detected

**Paradox:** The presence of `ProxyBlockedProperties` is itself a fingerprint signal! Users with privacy extensions are rare (~1-2% of traffic), making them more identifiable.

### Evasion Signals Reference

All possible values in `EvasionDetected`:

| Signal | Description |
|--------|-------------|
| `tor-screen` | Screen is exactly 1000x1000 (Tor Browser default) |
| `tor-likely` | Win32 platform + 24-bit color + no chrome object |
| `brave` | Brave browser detected via `navigator.brave` |
| `webrtc-blocked` | WebRTC API is undefined (privacy extension) |
| `ua-platform-mismatch` | User-Agent OS doesn't match `navigator.platform` |
| `mobile-ua-desktop-screen` | Mobile UA but screen > 1024px |
| `touch-mismatch` | Touch capability but no mobile UA on large screen |
| `partial-js-block` | Some JS APIs blocked (NoScript pattern) |
| `clienthints-platform-mismatch` | Client Hints platform differs from navigator.platform |

**Client Hints Platform Mismatch (KEY SIGNAL):**
Sophisticated bots may spoof `navigator.platform` but forget to spoof Client Hints, or vice versa:
```javascript
// Bot sets navigator.platform to "Linux"
// But Client Hints returns "Windows" 
// = clienthints-platform-mismatch
```

**UI Drill-Down:** When clicking Privacy Extension summary:
1. Show percentage of traffic with privacy extensions
2. Show which properties are most commonly blocked
3. Correlate with BotScore (privacy users are rarely bots)

**UI Drill-Down:** When clicking Evasion summary:
1. Show evasion type breakdown (Canvas/WebGL/Both/None)
2. Click type → Show fingerprints with that evasion type
3. Show correlation with BotScore

---

## Device & Hardware

| Field | Type | Description | Indicates | Entropy |
|-------|------|-------------|-----------|---------|
| `HardwareConcurrency` | int | CPU thread count | Desktop power, VM detection | ~3-5 bits |
| `DeviceMemoryGB` | int | Approximate RAM in GB | Device class (low/mid/high end) | ~3 bits |
| `MaxTouchPoints` | int | Touch capability | Mobile vs desktop, touchscreen monitor | ~2 bits |
| `ConnectedGamepads` | int | Game controllers connected | Gaming user profile | ~1 bit |
| `BatteryCharging` | bool | Device is plugged in | Laptop vs desktop behavior | ~1 bit |
| `BatteryLevelPct` | int | Battery percentage | Mobile device state | Low entropy |
| `StorageQuotaGB` | int | Available storage quota in GB | Device storage capacity | ~3-4 bits |
| `StorageUsedMB` | int | Used storage in MB | Browser data usage | ~2-3 bits |
| `VideoInputDevices` | int | Count of video input devices (cameras) | Webcam presence | ~1-2 bits |

**Storage Quota Analysis:**
Storage quota from `navigator.storage.estimate()` reveals:
- Device storage class (8GB mobile vs 500GB+ desktop)
- Incognito/private browsing (reduced quota)
- Storage pressure (near-full devices)

**Device Classification Logic:**
```
DeviceType = 
    IF UA_IsMobile = 1 THEN 'Mobile'
    ELSE IF ScreenWidth >= 1200 THEN 'Desktop'  -- Even with touch (touchscreen monitors)
    ELSE IF MaxTouchPoints > 0 AND ScreenWidth < 768 THEN 'Mobile'
    ELSE IF MaxTouchPoints > 0 THEN 'Tablet'
    ELSE 'Desktop'
```

**UI Drill-Down:** When clicking Device summary:
1. Show device type distribution (Desktop/Mobile/Tablet)
2. Click type → Show hardware specs for that type
3. Show CPU cores histogram, memory distribution

---

## Screen & Display

| Field | Type | Description | Indicates | Entropy |
|-------|------|-------------|-----------|---------|
| `ScreenWidth` | int | Screen width in pixels | Device type, monitor setup | ~5-6 bits |
| `ScreenHeight` | int | Screen height in pixels | Device type, monitor setup | ~5-6 bits |
| `ScreenAvailWidth` | int | Available width (minus taskbar) | OS, taskbar position | ~2 bits |
| `ScreenAvailHeight` | int | Available height (minus taskbar) | OS, taskbar position | ~2 bits |
| `ScreenX` | int | Browser window X position | Multi-monitor setup | ~2 bits |
| `ScreenY` | int | Browser window Y position | Multi-monitor setup | ~2 bits |
| `ScreenOrientation` | string | Portrait/landscape | Mobile device orientation | ~1 bit |
| `ViewportWidth` | int | Browser viewport width | Window size, responsive breakpoint | ~4 bits |
| `ViewportHeight` | int | Browser viewport height | Window size | ~4 bits |
| `OuterWidth` | int | Browser window outer width | Includes chrome/toolbar | ~3 bits |
| `OuterHeight` | int | Browser window outer height | Includes chrome/toolbar | ~3 bits |
| `PixelRatio` | float | Device pixel ratio (1x, 2x, 3x) | Retina/HiDPI display | ~2 bits |
| `ColorDepth` | int | Color depth in bits | Display capability | ~2 bits |
| `ColorDepthAnomaly` | bool | ColorDepth is unexpected for platform | Spoofing/VM indicator | BOT SIGNAL |

### ColorDepth Anomaly Detection

ColorDepth can reveal spoofing or headless browsers:

| ColorDepth | Platform | Expected? | Indicates |
|------------|----------|-----------|-----------|
| 24 | Windows | ✅ Yes | Normal |
| 24 | macOS | ⚠️ Suspicious | Real Macs use 30-bit color |
| 30 | macOS | ✅ Yes | Normal for Mac |
| 32 | Linux | ✅ Yes | Normal |
| 8 or 16 | Any | ⚠️ Suspicious | Very old device or VM |

**Why macOS + 24-bit is suspicious:**
Real macOS systems report 30-bit color depth. Bots/VMs spoofing macOS often forget this detail:
```sql
-- Flag records with macOS platform but 24-bit color
WHERE Platform LIKE '%Mac%' AND ColorDepth = 24
```

**Common Screen Patterns:**
| Resolution | Device Type | Notes |
|------------|-------------|-------|
| 1920x1080 | Desktop | Most common desktop |
| 2560x1440 | Desktop | Gaming/professional |
| 3840x2160 | Desktop | 4K monitor |
| 1366x768 | Laptop | Common laptop |
| 390x844 | Mobile | iPhone 12/13/14 |
| 360x800 | Mobile | Android common |
| 1000x900 | Unknown | **Tor Browser signature** |

**UI Drill-Down:** When clicking Screen summary:
1. Show resolution distribution
2. Highlight anomalies (Tor pattern, unusual sizes)
3. Show pixel ratio distribution
4. Flag ColorDepthAnomaly records

---

## Browser & Navigator

| Field | Type | Description | Indicates |
|-------|------|-------------|-----------|
| `ClientUserAgent` | string | Full User-Agent string | Browser, OS, version |
| `ServerUserAgent` | string | User-Agent from HTTP header | Should match client |
| `AppCodeName` | string | Browser code name (usually "Mozilla") | Legacy compatibility |
| `AppName` | string | Browser name (usually "Netscape") | Legacy, always same |
| `AppVersion` | string | Browser version string | Browser age |
| `NavigatorProduct` | string | Product name ("Gecko") | Engine type |
| `NavigatorProductSub` | string | Product sub-version | Engine version |
| `Vendor` | string | Browser vendor (Google, Apple, Mozilla) | Browser identification |
| `NavigatorVendorSub` | string | Vendor sub-version | Usually empty |
| `Platform` | string | Platform string ("Win32", "MacIntel") | OS identification |
| `PluginCount` | int | Number of browser plugins | Extension/plugin usage |
| `PluginListDetailed` | string | Detailed plugin list | Specific plugins installed |
| `MimeTypeCount` | int | Supported MIME types | Browser capability |
| `MimeTypeList` | string | List of MIME types | File handling capability |
| `HistoryLength` | int | Session history depth | Browsing depth |
| `OSCpu` | string | Firefox-only CPU identifier | OS/CPU details (Firefox) |
| `BuildID` | string | Firefox build identifier | Exact Firefox version |
| `ChromeObjPresent` | bool | `window.chrome` object exists | Chromium-based browser |
| `ChromeRuntimePresent` | bool | `chrome.runtime` API exists | Extension context |
| `JSHeapSizeLimit` | int | Max JS heap size | Browser memory config |
| `JSHeapTotalSize` | int | Total allocated heap | Memory usage pattern |
| `JSHeapUsedSize` | int | Currently used heap | Active memory usage |

### Firefox-Only Fields
Firefox exposes additional navigator properties not available in other browsers:
- `oscpu`: Returns the operating system/CPU info (e.g., "Windows NT 10.0; Win64; x64")
- `buildID`: Returns Firefox's build timestamp (can be frozen for privacy)

### Chrome Object Detection
The presence of `window.chrome` helps identify browser family:
| Browser | `chrome` | `chrome.runtime` |
|---------|----------|-------------------|
| Chrome | ✅ | ✅ (extensions only) |
| Edge | ✅ | ✅ (extensions only) |
| Brave | ✅ | ✅ |
| Firefox | ❌ | ❌ |
| Safari | ❌ | ❌ |

### JS Heap Memory (Chrome DevTools API)
Chrome exposes `performance.memory` for debugging:
- `jsHeapSizeLimit`: Maximum available heap
- `totalJSHeapSize`: Allocated heap size
- `usedJSHeapSize`: Currently used heap

**Bot Detection:** Unusual heap patterns can indicate:
- Very small heap = headless browser with limited memory
- Identical heap values = VM snapshots

---

## Client Hints

High-entropy signals from modern browsers (Chromium-based).

| Field | Type | Description | Indicates | Entropy |
|-------|------|-------------|-----------|---------|
| `UA_Platform` | string | OS name (Windows, macOS, Android) | Operating system | ~3 bits |
| `UA_PlatformVersion` | string | OS version (e.g., "10.0.0", "14.0.0") | OS age, update status | ~4-6 bits |
| `UA_Architecture` | string | CPU architecture (x86, arm) | Device type | ~2 bits |
| `UA_Bitness` | string | 32-bit or 64-bit | OS/browser architecture | ~1 bit |
| `UA_IsMobile` | bool | Mobile device flag | Device type | ~1 bit |
| `UA_IsWow64` | bool | 32-bit app on 64-bit OS | Compatibility mode | ~1 bit |
| `UA_Model` | string | Device model (mobile only) | Specific device | ~5-8 bits (mobile) |
| `UA_Brands` | string | Browser brand list | Browser identification | ~3 bits |
| `UA_FullVersionList` | string | Full browser versions | Precise browser version | ~5 bits |
| `UA_FormFactor` | string | Form factor (desktop, mobile, tablet) | Device category | ~2 bits |

**UI Drill-Down:** When clicking Client Hints:
1. Show platform distribution
2. Show platform version age distribution
3. Highlight outdated versions (security concern)

---

## Network & Connection

| Field | Type | Description | Indicates |
|-------|------|-------------|-----------|
| `ConnectionType` | string | Network type (4g, wifi, ethernet) | Mobile vs desktop context |
| `DownlinkMbps` | float | Estimated bandwidth | Network speed class |
| `DownlinkMax` | float | Maximum downlink speed | Hardware capability |
| `RTTMs` | int | Round-trip time estimate | Network latency |
| `NetworkType` | string | Effective connection type | Network quality |
| `DataSaverEnabled` | bool | Data saver mode active | Mobile/bandwidth-constrained |
| `IsOnline` | bool | Online status | Connectivity |
| `WebRTCLocalIP` | string | Local IP from WebRTC | Internal network structure |

**UI Drill-Down:** When clicking Network summary:
1. Show connection type distribution
2. Show bandwidth histogram
3. Show geographic distribution (from IP)

---

## Performance Timing

| Field | Type | Description | Indicates |
|-------|------|-------------|-----------|
| `ScriptExecutionTimeMs` | int | Time from page load to script execution | **Bot detection signal** |
| `PageLoadTimeMs` | int | Total page load time | Page performance |
| `DOMReadyTimeMs` | int | DOMContentLoaded timing | Page complexity |
| `DNSLookupMs` | int | DNS resolution time | Network path |
| `TCPConnectMs` | int | TCP connection time | Network latency |
| `TimeToFirstByteMs` | int | TTFB timing | Server response time |
| `ClientTimestampMs` | long | Client-side timestamp | Time zone verification |

**Bot Detection via Timing:**
| ScriptExecTime | Likelihood |
|----------------|------------|
| < 10ms | 🔴 Very likely bot (too fast) |
| 10-50ms | 🟡 Suspicious |
| 50-500ms | 🟢 Normal human |
| > 500ms | 🟢 Normal (slow network) |

**UI Drill-Down:** When clicking Performance:
1. Show execution time histogram
2. Highlight fast executions (bot indicator)
3. Show load time by device type

---

## API Capabilities

Boolean flags for browser API support. Used for browser fingerprinting and capability detection.

| Field | Type | Description | Indicates |
|-------|------|-------------|-----------|
| `CookiesEnabled` | bool | Cookies allowed | Privacy settings |
| `DoNotTrack` | string | DNT header value (1/0/unset) | Privacy preference |
| `LocalStorageSupported` | bool | localStorage available | Storage capability |
| `SessionStorageSupported` | bool | sessionStorage available | Storage capability |
| `IndexedDBSupported` | bool | IndexedDB available | Storage capability |
| `ServiceWorkerSupported` | bool | Service workers available | PWA capability |
| `WebWorkersSupported` | bool | Web workers available | Threading capability |
| `WebAssemblySupported` | bool | WebAssembly available | Performance capability |
| `JavaEnabled` | bool | Java plugin active | Legacy (usually false) |
| `PDFViewerEnabled` | bool | Built-in PDF viewer | Browser capability |
| `GeolocationAPISupported` | bool | Geolocation available | Location capability |
| `NotificationsAPISupported` | bool | Notifications available | Engagement capability |
| `PushAPISupported` | bool | Push API available | Engagement capability |
| `BluetoothAPISupported` | bool | Bluetooth API available | Hardware access |
| `USBAPISupported` | bool | USB API available | Hardware access |
| `SerialAPISupported` | bool | Serial API available | Hardware access |
| `HIDAPISupported` | bool | HID API available | Hardware access |
| `MIDIAPISupported` | bool | MIDI API available | Audio hardware |
| `MediaDevicesAPISupported` | bool | MediaDevices available | Camera/mic access |
| `SpeechRecognitionSupported` | bool | Speech recognition available | Voice input |
| `SpeechSynthesisSupported` | bool | Speech synthesis available | Voice output |
| `ShareAPISupported` | bool | Web Share API available | Mobile sharing |
| `ClipboardAPISupported` | bool | Clipboard API available | Copy/paste access |
| `CredentialsAPISupported` | bool | Credentials API available | Password manager |
| `PaymentRequestSupported` | bool | Payment Request API | E-commerce capability |
| `CacheAPISupported` | bool | Cache API available | Offline capability |
| `WebXRSupported` | bool | WebXR available | VR/AR capability |
| `PointerEventsSupported` | bool | Pointer events available | Input handling |
| `TouchEventsSupported` | bool | Touch events available | Touch capability |

**Capability Score:** Sum of supported APIs indicates browser modernity and potential fingerprint uniqueness.

---

## Accessibility & Preferences

| Field | Type | Description | Indicates |
|-------|------|-------------|-----------|
| `PrefersColorSchemeDark` | bool | Prefers dark mode | User preference |
| `PrefersColorSchemeLight` | bool | Prefers light mode | User preference |
| `PrefersReducedMotion` | bool | Reduce animations | Accessibility need |
| `PrefersReducedData` | bool | Reduce data usage | Bandwidth constraint |
| `PrefersHighContrast` | bool | High contrast mode | Visual accessibility |
| `ForcedColorsActive` | bool | Forced colors mode | Accessibility override |
| `InvertedColorsActive` | bool | Colors inverted | Accessibility setting |
| `HoverCapable` | bool | Device can hover | Mouse vs touch |
| `PointerType` | string | Primary pointer type | Input method |

---

## Locale & Internationalization

| Field | Type | Description | Indicates |
|-------|------|-------------|-----------|
| `Language` | string | Primary language (e.g., "en-US") | User locale |
| `LanguageList` | string | All accepted languages | User language preferences |
| `Timezone` | string | IANA timezone (e.g., "America/New_York") | Geographic location |
| `TimezoneLocale` | string | Locale from timezone | Regional setting |
| `TimezoneOffsetMins` | int | UTC offset in minutes | Time zone verification |
| `DateFormatSample` | string | Formatted date sample | Locale formatting |
| `NumberFormatSample` | string | Formatted number sample | Locale formatting |
| `RelativeTimeSample` | string | Relative time format | Locale formatting |
| `DocumentCharset` | string | Document character encoding | Internationalization |

**UI Drill-Down:** When clicking Locale:
1. Show timezone distribution map
2. Show language breakdown
3. Cross-reference with IP geolocation

---

## Page Context

| Field | Type | Description | Indicates |
|-------|------|-------------|-----------|
| `PageURL` | string | Full page URL | Source page |
| `PageDomain` | string | Domain only | Site identification |
| `PagePath` | string | Path portion | Page identification |
| `PageProtocol` | string | HTTP/HTTPS | Security status |
| `PageTitle` | string | Document title | Page content hint |
| `PageReferrer` | string | Referrer URL | Traffic source |
| `PageHash` | string | URL hash fragment | In-page navigation |
| `ServerReferer` | string | Referer from HTTP header | Traffic source (server-side) |
| `RequestPath` | string | Server request path | Endpoint hit |
| `DocumentReadyState` | string | Document state at capture | Load timing |
| `DocumentHidden` | bool | Tab is hidden | Active vs background |
| `DocumentVisibility` | string | Visibility state | Tab state |
| `DocumentCompatMode` | string | Quirks/standards mode | Page rendering |
| `StandaloneDisplayMode` | bool | PWA standalone mode | App context |

---

## Raw Data

| Field | Type | Description | Use |
|-------|------|-------------|-----|
| `RawQueryString` | string | Full query string from request | Debugging |
| `RawHeadersJson` | string | All HTTP headers as JSON | Server-side fingerprinting |

---

## Composite Scores

### Fingerprint Strength (0-100)
How unique is this device based on available signals?

```sql
FingerprintStrength = 
    (CanvasFingerprint != '' ? 15 : 0) +
    (WebGLFingerprint != '' ? 15 : 0) +
    (AudioFingerprintHash != '' ? 15 : 0) +
    (FontCount >= 20 ? 20 : FontCount >= 10 ? 15 : 10) +
    (UA_FullVersionList != '' ? 15 : 0) +  -- High-entropy client hints
    (Timezone != '' ? 10 : 0) +
    (PluginCount > 0 ? 10 : 0)
```

### Hardware Score (0-100)
Device capability level.

```sql
HardwareScore = 
    (HardwareConcurrency >= 24 ? 40 : HardwareConcurrency * 2) +
    (DeviceMemoryGB >= 8 ? 40 : DeviceMemoryGB >= 4 ? 30 : 20) +
    (GPURenderer != '' ? 20 : 0)
```

### Privacy Score (0-100)
How privacy-conscious is this user?

```sql
PrivacyScore = 
    (DoNotTrack ? 15 : 0) +
    (CookiesEnabled = 0 ? 20 : 0) +
    (CanvasEvasionDetected ? 25 : 0) +
    (WebGLEvasionDetected ? 25 : 0) +
    (PluginCount = 0 ? 15 : 0)
```

---

## UI Hierarchy for Drill-Down

```
Dashboard (vw_Dashboard_KPIs)
│
├── 📊 Bot Rate Card
│   ├── Click → vw_Dashboard_RiskBuckets (pie chart: High/Med/Low/Human)
│   │   └── Click bucket → vw_Dashboard_BotDetails (individual records)
│   │       └── Columns: BotScore, WebDriverDetected, ScriptExecMs, EvasionTools
│   └── Related → vw_Dashboard_TimingAnalysis (script timing histogram)
│
├── 🛡️ Evasion Rate Card  
│   ├── Click → vw_Dashboard_EvasionSummary (pie: Canvas/WebGL/Both/None)
│   │   └── Click type → vw_Dashboard_EvasionDetails (individual records)
│   │       └── Columns: EvasionType, TorSignatureDetected, BotScore
│   └── Tor detection: ScreenWidth=1000, ScreenHeight=900
│
├── 🔐 Fingerprint Card
│   ├── Click → vw_Dashboard_FingerprintDetails (fingerprint breakdown)
│   │   └── Columns: Canvas/WebGL/Audio hashes, FontCount, FingerprintStrength
│   ├── Click Canvas → vw_Dashboard_GPUDistribution (GPU breakdown)
│   └── Click Screen → vw_Dashboard_ScreenDistribution (resolution chart)
│
├── 📱 Device Card
│   ├── Click → vw_PiXL_DeviceBreakdown (device/OS/browser breakdown)
│   └── Hardware → HardwareConcurrency, DeviceMemoryGB from vw_PiXL_Complete
│
├── 🌐 Network Card
│   ├── Click → vw_PiXL_NetworkHouseholds (devices per IP)
│   │   └── Click IP → vw_PiXL_NetworkDevices (device list for that IP)
│   └── Cross-network → vw_PiXL_DeviceIdentity (same device, different IPs)
│
├── 📈 Trends
│   └── Day-over-day → vw_Dashboard_Trends
│       └── Columns: TotalHits, UniqueDevices, HighRiskCount, EvasionCount
│
└── 🔴 Live Feed
    └── vw_Dashboard_LiveFeed (last 100 hits, real-time)
```

---

## API Endpoint Mapping

| Dashboard Card | Primary View | Drill-Down View |
|----------------|--------------|-----------------|
| Total Hits | `vw_Dashboard_KPIs.TotalHitsToday` | `vw_Dashboard_Trends` |
| Unique Devices | `vw_Dashboard_KPIs.UniqueDevicesToday` | `vw_Dashboard_FingerprintDetails` |
| Bot Rate | `vw_Dashboard_KPIs.BotRatePctToday` | `vw_Dashboard_RiskBuckets` → `vw_Dashboard_BotDetails` |
| Evasion Rate | `vw_Dashboard_KPIs.EvasionRatePctToday` | `vw_Dashboard_EvasionSummary` → `vw_Dashboard_EvasionDetails` |
| Cross-Network | `vw_Dashboard_KPIs.CrossNetworkDevicesToday` | `vw_PiXL_DeviceIdentity` |
| Fast Execs | `vw_Dashboard_KPIs.FastExecsToday` | `vw_Dashboard_TimingAnalysis` |
| Device Split | `Desktop/Mobile/TabletHitsToday` | `vw_PiXL_DeviceBreakdown` |

---

## SQL View Recommendations

### Existing Views (06_AnalyticsViews.sql)

| View | Purpose | Use Case |
|------|---------|----------|
| `vw_PiXL_Summary` | 15 columns with composite scores | Quick overview |
| `vw_PiXL_Complete` | 130+ columns, every data point | Full detail access |
| `vw_PiXL_ColumnMap` | Maps summary → source columns | Documentation |
| `vw_PiXL_HourlyStats` | Hourly aggregates | Time series charts |
| `vw_PiXL_DeviceBreakdown` | Device/OS/Browser breakdown | Device analytics |
| `vw_PiXL_BotAnalysis` | Bot risk bucketing | Bot detection |
| `vw_PiXL_FingerprintUniqueness` | Fingerprint entropy | Uniqueness analysis |
| `vw_PiXL_NetworkHouseholds` | Devices per IP | NAT detection |
| `vw_PiXL_NetworkDevices` | Device list per IP | Household drill-down |
| `vw_PiXL_DeviceIdentity` | Cross-network device tracking | Identity resolution |
| `vw_PiXL_DeviceNetworkHistory` | IP history per device | Network mobility |

### Dashboard Views (07_DashboardViews.sql)

**Summary Level (KPIs):**
| View | Purpose |
|------|---------|
| `vw_Dashboard_KPIs` | Main summary cards with today/yesterday metrics |

**Drill-Down Level 1 (Distributions):**
| View | Purpose | Drill-Down From |
|------|---------|-----------------|
| `vw_Dashboard_RiskBuckets` | Bot risk bucket breakdown | Bot Rate card |
| `vw_Dashboard_EvasionSummary` | Evasion type pie chart | Evasion Rate card |
| `vw_Dashboard_TimingAnalysis` | Script timing buckets | Performance card |
| `vw_Dashboard_Trends` | Day-over-day comparison | Trend arrows |

**Drill-Down Level 2 (Details):**
| View | Purpose | Drill-Down From |
|------|---------|-----------------|
| `vw_Dashboard_BotDetails` | Individual bot records | Risk bucket click |
| `vw_Dashboard_EvasionDetails` | Individual evasion records | Evasion type click |
| `vw_Dashboard_FingerprintDetails` | Fingerprint signal breakdown | Fingerprint card |

**Drill-Down Level 3 (Granular):**
| View | Purpose | Drill-Down From |
|------|---------|-----------------|
| `vw_Dashboard_GPUDistribution` | GPU breakdown | WebGL fingerprint |
| `vw_Dashboard_ScreenDistribution` | Resolution breakdown | Screen card |
| `vw_Dashboard_LiveFeed` | Real-time feed | Any live monitoring |

---

## Frontend Component Mapping

| Summary Card | Level 1 View | Level 2 View | Key Columns |
|--------------|--------------|--------------|-------------|
| Bot Rate | `vw_Dashboard_RiskBuckets` | `vw_Dashboard_BotDetails` | BotScore, RiskBucket, BucketColor |
| Evasion | `vw_Dashboard_EvasionSummary` | `vw_Dashboard_EvasionDetails` | EvasionType, TorSignatureDetected |
| Fingerprints | `vw_Dashboard_FingerprintDetails` | `vw_Dashboard_GPUDistribution` | FingerprintStrength, FontCount |
| Devices | `vw_PiXL_DeviceBreakdown` | `vw_PiXL_NetworkDevices` | DeviceType, Browser, OS |
| Timing | `vw_Dashboard_TimingAnalysis` | `vw_Dashboard_BotDetails` | TimingBucket, AvgBotScore |
| Network | `vw_PiXL_NetworkHouseholds` | `vw_PiXL_DeviceIdentity` | UniqueIPAddresses, UniqueDevices |

### Dashboard Card Design Pattern

Each card should:
1. **Header**: Show metric name + primary value from `vw_Dashboard_KPIs`
2. **Trend Arrow**: Compare `Today` vs `Yesterday` (green up / red down)
3. **Spark Line**: 7-day trend from `vw_Dashboard_Trends`
4. **Click Action**: Navigate to Level 1 drill-down view
5. **Color Coding**: Use `BucketColor` from aggregation views

### Example: Bot Rate Card

```html
<div class="card" onclick="drillDown('risk-buckets')">
  <h3>Bot Rate</h3>
  <span class="value">5.2%</span>
  <span class="trend up">↑ 0.3% vs yesterday</span>
  <div class="sparkline"><!-- 7-day chart --></div>
</div>
```

When clicked, show `vw_Dashboard_RiskBuckets` as pie chart:
- High Risk (red): 80-100
- Medium Risk (orange): 50-79  
- Low Risk (yellow): 20-49
- Likely Human (green): 0-19

Click a pie slice → Filter `vw_Dashboard_BotDetails WHERE RiskBucket = 'High Risk'`
