# Brilliant PiXL — Platform Design Document

> **Living document.** Updated as design decisions are made.
> Last updated: 2026-02-18 by agent:copilot + platform owner.

---

## 1. What This Is

This is a planned upgrade to M1's SmartPiXL platform. Not a PiXL 2.0, not a new product.
Internal naming: **Legacy PiXL** (the old ASPX script) → **Brilliant PiXL** (the upgraded platform).

This project is two weeks old. The foundation is built. Live test data has not arrived yet (pending management). Every design decision below will be validated against real data when it comes in. The methodology is: collect every data point possible now, refine which are useful later.

---

## 1.5 Terminology — Project Lexicon

These terms have specific meanings in this project. Use them consistently.

| Term | Meaning | Example |
|------|---------|--------|
| **Data point** | One atomic piece of information about a visitor. Indivisible. A data point cannot be composite — if it contains sub-values, it's a field. | `sw=1920` is a data point: screen width is 1920 pixels. |
| **Field** | A named key in the query string, or a column in SQL. A field may carry a single data point, or it may be a composite containing multiple data points packed together. | `botSignals=webdriver,selenium,cdp` is one field containing 3 data points. |
| **Signal** | A named indicator extracted from raw data that informs a detection decision. Signals live inside composite fields. Each signal has a score weight. | `webdriver` (+10) is a signal inside the `botSignals` field. |
| **Column** | A SQL column in a table. In conversation, "field" and "column" are used interchangeably when discussing SQL. | `PiXL.Parsed.ScreenWidth` is a column. |
| **Enrichment** | A data point or signal that is added server-side (not from the browser). Prefixed with `_srv_` in the query string. | `_srv_geoCC=US` is an enrichment. |
| **Score** | A weighted integer that accumulates from individual signals. Not a data point — it's derived. | `botScore=35` is the sum of signal weights. |

**Counts in this document:**
- **159 fields** — unique `data.*` keys sent in the query string from the PiXL script
- **230+ data points** — the total information extracted, including data points packed inside composite fields
- **80+ signals** — named bot/evasion/cross-signal indicators packed inside composite fields like `botSignals`, `crossSignals`, `evasionDetected`, `stealthSignals`, `evasionSignalsV2`

---

## 2. Architecture Overview

### 2.1 Legacy PiXL (What We're Replacing)

One `aspx.cs` script. Blocking. Fired HTTP headers into SQL on every page hit. Single-threaded, synchronous, no enrichment. IP geolocation done by a 2 AM batch job that blasted 6M rows at IPAPI daily whether they needed it or not.

### 2.2 Brilliant PiXL (What We're Building)

Three components working in concert:

| Component | Where It Runs | What It Does | Time Budget |
|-----------|---------------|-------------|-------------|
| **PiXL Script** | Visitor's browser | Collects 230+ data points across 159 fields via browser APIs. Fire-and-forget. Visitor never knows. | Up to 500ms |
| **IIS Fast Pass** | IIS in-process (w3wp.exe) | Parses HTTP request + 6 in-memory enrichment checks. Returns 43-byte transparent GIF. | ~5 microseconds |
| **SmartPiXL Forge** | Windows Service (.NET) | Receives enriched record from IIS. Forges raw data into enriched intelligence (API calls, DNS, scoring). Writes fully enriched row to SQL. Durable — never loses data. | Unlimited (async) |

### 2.3 Data Flow

```
Visitor's Browser
    │
    ├── PiXL script injected via ASP.NET (JS served on _SMART.js request)
    ├── Script runs in browser for up to 500ms
    ├── Collects 230+ data points across 159 fields from browser APIs
    ├── Fires: new Image().src = "_SMART.GIF?canvasFP=...&sw=1920&cores=8&..."
    │   (fire-and-forget, visitor never sees it)
    │
    ▼
IIS (In-Process, w3wp.exe)
    │
    ├── TrackingCaptureService: Parse HTTP → TrackingData (9 fields)
    ├── FingerprintStabilityService: Per-IP fingerprint history (in-memory, 24h window)
    ├── IpBehaviorService: Subnet /24 velocity + rapid-fire timing (in-memory)
    ├── DatacenterIpService: AWS/GCP CIDR trie (8,500 ranges, weekly refresh)
    ├── IpClassificationService: Bitwise IPv4 classification (16 reserved ranges)
    ├── GeoCacheService: Two-tier geo cache (hot + TTL, backed by IPAPI.IP)
    ├── Timezone mismatch: Client TZ vs IP-derived TZ
    ├── Append all results as _srv_* query string params
    ├── Return 43-byte transparent GIF immediately
    │
    ├── Send enriched record via named pipe to the Forge
    │   └── If pipe fails → write JSONL to Failover/ directory (durable)
    │
    ▼
SmartPiXL Forge (Windows Service, always-on)
    │
    ├── Receive record from named pipe (or catch-up loop reads JSONL failover files)
    ├── IPAPI Pro lookup (new/stale IPs only, real-time, replaces legacy 2AM batch)
    ├── Bot UA database check (NetCrawlerDetect + known bot UA list)
    ├── DNS reverse lookup (hostname reveals residential vs cloud)
    ├── UA parsing (UAParser / DeviceDetector.NET — structured browser/OS/device)
    ├── Cross-customer intelligence (same IP+FP hitting multiple customers)
    ├── Lead quality scoring (reverse of bot scoring — how human is this visitor?)
    ├── Session stitching (visitor journey graph by fingerprint, real-time)
    ├── Geographic arbitrage (cultural fingerprint vs claimed geography)
    ├── Device age / affluence estimation (GPU + cores + RAM + screen)
    ├── Impossible combination detection (contradiction matrix)
    ├── Behavioral replay detection (mouse path hashing)
    ├── Write fully enriched row to PiXL.Raw
    │
    └── Catch-up loop (every 60s):
        ├── Read JSONL failover files
        ├── Process with awareness that data is catch-up (adjust scoring)
        ├── Archive processed files
        └── Never lose data, ever
```

### 2.4 Why Three Components?

| | Browser Can See | IIS Fast Pass Can See | Forge Can See |
|--|----------------|----------------------|---------------------|
| Own fingerprint | Yes | No (receives it as data) | Yes (from IIS) |
| Other visitors' fingerprints | **No** | **Yes** (in-memory correlation) | **Yes** (full cross-customer view) |
| Real IP address | **No** | **Yes** (socket-level) | **Yes** (from IIS) |
| Subnet correlation | **No** | **Yes** (/24 velocity in-memory) | **Yes** |
| Timing between hits | **No** | **Yes** (rapid-fire detection) | **Yes** |
| DNS reverse hostname | No | **No** (too slow, ~50ms) | **Yes** (unlimited time) |
| IPAPI enrichment | No | **No** (would block thread) | **Yes** (async API call) |
| Cross-customer patterns | No | **No** (serves all customers but doesn't correlate) | **Yes** (sees all traffic) |
| Historical device behavior | No | No | No — **SQL does this** |

SQL handles historical analysis across time. The Forge handles real-time analysis across customers and sessions.

---

## 3. PiXL Script — Browser-Side Data Collection

### 3.1 How It Works

The PiXL script is a JavaScript file injected into the visitor's browser through ASP.NET. When a customer's page requests `_SMART.js`, IIS serves the script. The script runs in browser memory, hits browser APIs, and fires the collected data back as a `_SMART.GIF` image request. The visitor never sees or feels any of this.

### 3.2 The 500ms Window

The script uses `Promise.race` between async probes and a 500ms safety timeout:
- If all async work finishes in 80ms → data fires at 80ms (no padding delay)
- If any async probe hangs → 500ms timeout guarantees delivery
- The 500ms is a ceiling, not a wait

**What happens DURING the window** (in parallel with async probes):
- Mouse movement tracking: up to 50 `{x, y, timestamp}` events captured
- Scroll event tracking: scroll depth recorded
- `navigator.storage.estimate()`: disk quota/usage (async, already implemented)
- `navigator.getBattery()`: battery level + charging state (async, already implemented)
- `navigator.mediaDevices.enumerateDevices()`: camera/mic count (async, already implemented, no popup)
- `navigator.userAgentData.getHighEntropyValues()`: detailed platform/CPU/version data (async, already implemented)
- Audio fingerprint: OfflineAudioContext rendered twice for consistency check

**What we're adding to the window** (new data points):
- `window.screen.isExtended`: multi-monitor detection (sync, trivial to add)
- Raw mouse path encoding: compact `x,y,t|x,y,t|...` payload for replay detection in the Forge

### 3.3 Anti-Noise / Evasion Detection

The script does NOT snapshot all fields twice after 500ms. The actual technique is **repeatability testing** — running the same operation twice immediately and checking if the browser gives the same answer both times.

**Canvas consistency** (synchronous, microseconds):
- Draw identical content on two separate canvas elements
- Hash both outputs
- Same input, different output = browser injecting noise (Brave, Firefox privacy mode)
- Also checks: canvas completely blocked (draw something, hash stays the same)

**Audio consistency** (async, during 500ms window):
- Run `OfflineAudioContext` fingerprint twice in parallel via `Promise.all`
- Compare results
- Different results from same operation = noise injection

When noise is detected:
1. The noisy field is excluded from the composite fingerprint (doesn't degrade accuracy)
2. The record is flagged as evasive (`canvasConsistency=noise-detected`, `audioNoiseDetected=1`)
3. Feeds into `evasionSignalsV2` and `anomalyScore` for bot/traffic quality scoring

### 3.4 Mouse & Behavioral Analysis

Already implemented in the script (lines 1010-1095 of PiXLScript.cs):

| Metric | What It Measures | Bot Signal |
|--------|-----------------|-----------|
| `mouseMoves` | Count of mouse events in the window (0-50) | 0 = headless/automated |
| `mouseEntropy` | Variance of angles between consecutive movements | Low = straight lines (bot). High = natural curves (human) |
| `moveTimingCV` | Coefficient of variation of time gaps between events | < 0.3 = metronomic timing (bot). Human timing is jittery |
| `moveSpeedCV` | Coefficient of variation of movement speed | < 0.2 = constant speed (bot). Humans accelerate/decelerate |
| `scrolled` | Did the visitor scroll? | Part of engagement signal |
| `scrollY` | Actual scroll depth | scrolled=1 but scrollY=0 = synthetic scroll event |
| `moveCountBucket` | low/mid/high/very-high classification | Behavioral segmentation |

**Planned addition — mouse path replay detection:**
- Encode the raw 50-point `{x,y,t}` array as a compact string
- Send as `mousePath` parameter
- The Forge hashes the path and maintains a set of recent hashes
- Identical mouse path from different fingerprints = replayed recorded behavior

### 3.5 Complete Data Point Inventory (Audited)

> **Source of truth.** Line-by-line audit of `PiXLScript.cs` (1155 lines).
> Last audited: 2025-07-14. Every `data.*` assignment cataloged.
> Total: **159 named fields** sent as query string key=value pairs.
> Composite fields (botSignals, crossSignals, etc.) pack **80+ additional named signals** inside them.

#### Sync — Screen & Display (14 fields)

| Field | Source | Value |
|-------|--------|-------|
| `sw` | `screen.width` | pixels |
| `sh` | `screen.height` | pixels |
| `saw` | `screen.availWidth` | pixels |
| `sah` | `screen.availHeight` | pixels |
| `cd` | `screen.colorDepth` | bits |
| `pd` | `window.devicePixelRatio` | ratio (1, 2, 3) |
| `ori` | `screen.orientation.type` | string (portrait-primary, etc.) |
| `vw` | `window.innerWidth` | viewport pixels |
| `vh` | `window.innerHeight` | viewport pixels |
| `ow` | `window.outerWidth` | window pixels |
| `oh` | `window.outerHeight` | window pixels |
| `sx` | `window.screenX` / `screenLeft` | monitor position px |
| `sy` | `window.screenY` / `screenTop` | monitor position px |
| `screenExtended` | `screen.isExtended` | 1 (multi-monitor) / 0 (single) |

#### Sync — Navigator Properties (18 fields)

| Field | Source | Value |
|-------|--------|-------|
| `plt` | `navigator.platform` | Win32, MacIntel, Linux x86_64, etc. |
| `vnd` | `navigator.vendor` | Google Inc., Apple Computer, Inc., '' |
| `ua` | `navigator.userAgent` | full UA string |
| `cores` | `navigator.hardwareConcurrency` | CPU thread count |
| `mem` | `navigator.deviceMemory` | GB (Chromium only) |
| `touch` | `navigator.maxTouchPoints` | 0-10+ |
| `product` | `navigator.product` | always 'Gecko' |
| `productSub` | `navigator.productSub` | '20030107' or '20100101' |
| `vendorSub` | `navigator.vendorSub` | usually '' |
| `oscpu` | `navigator.oscpu` | Firefox only — OS + CPU |
| `buildID` | `navigator.buildID` | Firefox only — version fingerprint |
| `lang` | `navigator.language` | primary language tag |
| `langs` | `navigator.languages` | comma-joined list |
| `appName` | `navigator.appName` | 'Netscape' (all browsers) |
| `appVersion` | `navigator.appVersion` | version string |
| `appCodeName` | `navigator.appCodeName` | 'Mozilla' (all browsers) |
| `online` | `navigator.onLine` | 1/0 |
| `java` | `navigator.javaEnabled()` | 1/0 |

#### Sync — Canvas Fingerprinting (3 fields)

| Field | Source | Value |
|-------|--------|-------|
| `canvasFP` | Canvas 2D: text + shapes + arc → `toDataURL()` → hash | hex hash |
| `canvasEvasion` | Pixel variance analysis: variance < 1 or dataUrl < 1000 bytes | 1/0 |
| `canvasConsistency` | Two-canvas repeatability test: draw same content, hash both | 'clean', 'noise-detected', 'canvas-blocked', 'error' |

#### Sync — WebGL Fingerprinting (6 fields)

| Field | Source | Value |
|-------|--------|-------|
| `webglFP` | Hash of 23 WebGL parameters + extension list | hex hash |
| `gpu` | `WEBGL_debug_renderer_info.UNMASKED_RENDERER_WEBGL` | GPU model string |
| `gpuVendor` | `WEBGL_debug_renderer_info.UNMASKED_VENDOR_WEBGL` | GPU vendor string |
| `webglParams` | First 5 WebGL params: VERSION, SHADING_LANGUAGE, VENDOR, RENDERER, MAX_VERTEX_ATTRIBS | pipe-separated |
| `webglExt` | `gl.getSupportedExtensions().length` | extension count |
| `webglEvasion` | SwiftShader/llvmpipe/Mesa/Disabled detected in GPU renderer | 1/0 |

#### Async — Audio Fingerprinting (4 fields)

| Field | Source | Value |
|-------|--------|-------|
| `audioFP` | `OfflineAudioContext`: oscillator → compressor → sum of samples[4500..5000] | float string or 'blocked'/'error' |
| `audioHash` | Hash of audio buffer sampled every 100th element | hex hash |
| `audioStable` | `Promise.all([runAudioFP(), runAudioFP()])` — same result twice? | 1/0 |
| `audioNoiseDetected` | Two runs differ AND first isn't 'blocked' | 1 (only set when noise found) |

#### Sync — Font Detection (2 fields)

| Field | Source | Value |
|-------|--------|-------|
| `fonts` | Test 30 fonts against monospace baseline via width measurement | comma-separated detected fonts |
| `fontMethodMismatch` | `offsetWidth` agrees with `getBoundingClientRect().width`? | 1/0 (mismatch = spoof) |

Fonts tested (30): Arial, Arial Black, Verdana, Times New Roman, Courier New, Georgia, Comic Sans MS, Impact, Trebuchet MS, Tahoma, Segoe UI, Calibri, Consolas, Helvetica, Monaco, Roboto, Open Sans, Lato, Montserrat, Source Sans Pro, Century Gothic, Futura, Gill Sans, Lucida Grande, Garamond, MS Gothic, SimSun, Microsoft YaHei, Apple Color Emoji, Segoe UI Emoji.

#### Sync — Speech Voices (1 field)

| Field | Source | Value |
|-------|--------|-------|
| `voices` | `speechSynthesis.getVoices()` | pipe-separated name/lang, max 20 |

#### Async + Sync — Network / WebRTC (7 fields)

| Field | Source | Sync/Async | Value |
|-------|--------|------------|-------|
| `localIp` | `RTCPeerConnection` ICE candidate | Async | local LAN IP (192.168.x.x, etc.) |
| `conn` | `navigator.connection.effectiveType` | Sync | '4g', '3g', '2g', 'slow-2g' |
| `dl` | `navigator.connection.downlink` | Sync | Mbps |
| `dlMax` | `navigator.connection.downlinkMax` | Sync | Mbps |
| `rtt` | `navigator.connection.rtt` | Sync | milliseconds |
| `save` | `navigator.connection.saveData` | Sync | 1/0 |
| `connType` | `navigator.connection.type` | Sync | 'wifi', 'cellular', etc. |

#### Async + Sync — Storage (6 fields)

| Field | Source | Sync/Async | Value |
|-------|--------|------------|-------|
| `storageQuota` | `navigator.storage.estimate().quota` | Async | GB (rounded) |
| `storageUsed` | `navigator.storage.estimate().usage` | Async | MB (rounded) |
| `ls` | `window.localStorage` available | Sync | 1/0 |
| `ss` | `window.sessionStorage` available | Sync | 1/0 |
| `idb` | `window.indexedDB` available | Sync | 1/0 |
| `caches` | `window.caches` available | Sync | 1/0 |

#### Async — User Agent Data / Client Hints (10 fields)

| Field | Source | Sync/Async | Value |
|-------|--------|------------|-------|
| `uaMobile` | `navigator.userAgentData.mobile` | Sync | 1/0 |
| `uaPlatform` | `navigator.userAgentData.platform` | Sync | 'Windows', 'macOS', 'Linux', etc. |
| `uaBrands` | `navigator.userAgentData.brands` | Sync | pipe-separated brand/version |
| `uaArch` | `.getHighEntropyValues(['architecture'])` | Async | 'x86', 'arm', etc. |
| `uaBitness` | `.getHighEntropyValues(['bitness'])` | Async | '64', '32' |
| `uaModel` | `.getHighEntropyValues(['model'])` | Async | device model (mobile only) |
| `uaPlatformVersion` | `.getHighEntropyValues(['platformVersion'])` | Async | OS version string |
| `uaWow64` | `.getHighEntropyValues(['wow64'])` | Async | 1/0 (32-bit on 64-bit) |
| `uaFormFactor` | `.getHighEntropyValues(['formFactor'])` | Async | comma-joined form factors |
| `uaFullVersion` | `.getHighEntropyValues(['fullVersionList'])` | Async | pipe-separated brand/version |

#### Async — Battery (2 fields)

| Field | Source | Value |
|-------|--------|-------|
| `batteryLevel` | `navigator.getBattery().level` | 0-100 (percentage) |
| `batteryCharging` | `navigator.getBattery().charging` | 1/0 |

#### Async — Media Devices (2 fields)

| Field | Source | Value |
|-------|--------|-------|
| `audioInputs` | `navigator.mediaDevices.enumerateDevices()` → audioinput count | integer (NO popup, no labels) |
| `videoInputs` | `navigator.mediaDevices.enumerateDevices()` → videoinput count | integer (NO popup, no labels) |

#### Sync — Plugins & MIME Types (4 fields)

| Field | Source | Value |
|-------|--------|-------|
| `pluginList` | `navigator.plugins` | pipe-separated name::filename::description, max 20 |
| `mimeList` | `navigator.mimeTypes` | comma-separated types, max 30 |
| `plugins` | `navigator.plugins.length` | count |
| `mimeTypes` | `navigator.mimeTypes.length` | count |

#### Sync — Feature Detection (17 fields)

| Field | Source | Value |
|-------|--------|-------|
| `ck` | `navigator.cookieEnabled` | 1/0 |
| `dnt` | `navigator.doNotTrack` | '1', '0', or '' |
| `pdf` | `navigator.pdfViewerEnabled` | 1/0 |
| `webdr` | `navigator.webdriver` | 1/0 |
| `ww` | `window.Worker` available | 1/0 |
| `swk` | `navigator.serviceWorker` available | 1/0 |
| `wasm` | `WebAssembly` available | 1/0 |
| `webgl` | WebGL context available | 1/0 |
| `webgl2` | WebGL2 context available | 1/0 |
| `canvas` | Canvas 2D context available | 1/0 |
| `touchEvent` | `'ontouchstart' in window` | 1/0 |
| `pointerEvent` | `window.PointerEvent` available | 1/0 |
| `mediaDevices` | `navigator.mediaDevices` available | 1/0 |
| `clipboard` | `navigator.clipboard.writeText` available | 1/0 |
| `speechSynth` | `window.speechSynthesis` available | 1/0 |
| `chromeObj` | `window.chrome` exists | 1/0 |
| `chromeRuntime` | `window.chrome.runtime` exists | 1/0 |

#### Sync — CSS Media Queries / Accessibility (10 fields)

| Field | Source | Value |
|-------|--------|-------|
| `darkMode` | `matchMedia('(prefers-color-scheme: dark)')` | 1/0 |
| `lightMode` | `matchMedia('(prefers-color-scheme: light)')` | 1/0 |
| `reducedMotion` | `matchMedia('(prefers-reduced-motion: reduce)')` | 1/0 |
| `reducedData` | `matchMedia('(prefers-reduced-data: reduce)')` | 1/0 |
| `contrast` | `matchMedia('(prefers-contrast: high)')` | 1/0 |
| `forcedColors` | `matchMedia('(forced-colors: active)')` | 1/0 |
| `invertedColors` | `matchMedia('(inverted-colors: inverted)')` | 1/0 |
| `hover` | `matchMedia('(hover: hover)')` | 1/0 |
| `pointer` | `matchMedia('(pointer: fine/coarse)')` | 'fine', 'coarse', or '' |
| `standalone` | `matchMedia('(display-mode: standalone)')` | 1/0 |

#### Sync — Timezone & Locale (7 fields)

| Field | Source | Value |
|-------|--------|-------|
| `tz` | `Intl.DateTimeFormat().resolvedOptions().timeZone` | IANA timezone |
| `tzo` | `new Date().getTimezoneOffset()` | minutes offset from UTC |
| `ts` | `new Date().getTime()` | epoch milliseconds |
| `tzLocale` | `Intl.DateTimeFormat().resolvedOptions()` | pipe-separated: locale, calendar, numberingSystem, hourCycle |
| `dateFormat` | `new Intl.DateTimeFormat().format(Jan 15 2024)` | locale-formatted date string |
| `numberFormat` | `new Intl.NumberFormat().format(1234567.89)` | locale-formatted number string |
| `relativeTime` | `new Intl.RelativeTimeFormat().format(-1, 'day')` | locale-formatted relative string |

#### Sync — Performance Timing (5 fields)

| Field | Source | Value |
|-------|--------|-------|
| `loadTime` | `timing.loadEventEnd - timing.navigationStart` | ms |
| `domTime` | `timing.domContentLoadedEventEnd - timing.navigationStart` | ms |
| `dnsTime` | `timing.domainLookupEnd - timing.domainLookupStart` | ms |
| `tcpTime` | `timing.connectEnd - timing.connectStart` | ms |
| `ttfb` | `timing.responseStart - timing.requestStart` | ms |

#### Sync — JS Heap Memory (3 fields, Chromium only)

| Field | Source | Value |
|-------|--------|-------|
| `jsHeapLimit` | `performance.memory.jsHeapSizeLimit` | bytes |
| `jsHeapTotal` | `performance.memory.totalJSHeapSize` | bytes |
| `jsHeapUsed` | `performance.memory.usedJSHeapSize` | bytes |

#### Sync — Page Context (8 fields)

| Field | Source | Value |
|-------|--------|-------|
| `url` | `location.href` | full URL |
| `ref` | `document.referrer` | referrer URL |
| `hist` | `history.length` | navigation count |
| `title` | `document.title` | page title |
| `domain` | `location.hostname` | hostname |
| `path` | `location.pathname` | path |
| `hash` | `location.hash` | URL fragment |
| `protocol` | `location.protocol` | 'http:' or 'https:' |

#### Sync — Document State (5 fields)

| Field | Source | Value |
|-------|--------|-------|
| `docCharset` | `document.characterSet` | 'UTF-8', etc. |
| `docCompat` | `document.compatMode` | 'CSS1Compat' or 'BackCompat' |
| `docReady` | `document.readyState` | 'loading', 'interactive', 'complete' |
| `docHidden` | `document.hidden` | 1/0 |
| `docVisibility` | `document.visibilityState` | 'visible', 'hidden', 'prerender' |

#### Sync — Exotic Fingerprints (3 fields)

| Field | Source | Value |
|-------|--------|-------|
| `mathFP` | 8 Math operations (tan, sin, acos, atan, exp, log, sqrt, pow) → truncated results | comma-separated |
| `cssFontVariant` | 10 CSS font-variant properties + element width via `getComputedStyle` | pipe-separated |
| `errorFP` | `null[0]()` → error message length + stack length | integer |

#### Sync — Gamepads (1 field)

| Field | Source | Value |
|-------|--------|-------|
| `gamepads` | `navigator.getGamepads()` | pipe-separated gamepad IDs |

#### Sync — Bot Detection (3 fields, ~30+ packed signals)

| Field | Source | Value |
|-------|--------|-------|
| `botSignals` | Composite bot detection engine (see below) | comma-separated signal names |
| `botScore` | Weighted sum of bot signals | integer 0-100+ |
| `botPermInconsistent` | `Notification.permission` contradicts `permissions.query` | 1 (only set when detected) |

**Signals packed inside `botSignals`** (each adds to `botScore`):
`webdriver` (+10), `headless-no-chrome-obj` (+8), `minimal-ua` (+15, UA < 30 chars), `fake-ua` (+20), `phantomjs` (+10), `nightmare` (+10), `selenium` (+10, 6 DOM markers), `empty-languages` (+5), `cdp` (+10, Chrome DevTools Protocol globals), `perm-inconsistent` (async, notifications vs permissions.query), `plugin-mime-mismatch` (+3), `zero-screen` (+8), `no-plugins` (+2, non-Firefox), `dom-automation` (+10), `outer-zero` (+5, outerWidth=0 innerWidth>0), `nav-*` (+10 each, webdriver/selenium/puppeteer/playwright in navigator), `fn-tampered` (+5, permissions.query not native), `playwright-global` (+10), `default-viewport` (+2, 1280x720 or 800x600), `headless-ua` (+10), `chrome-no-runtime` (+1), `fullscreen-match` (+2), `no-connection-api` (+3), `eval-tampered` (+5), `webdriver-getter-override` (+8), `cross-realm-toString` (+12, iframe cross-realm check), `getter-name-mismatch:*` (+6 per property, checks 6 Navigator properties), `getter-has-prototype:*` (+8 per property, checks 6 Navigator properties), `heap-size-spoofed` (+8, round heap values), `heap-total-equals-used` (+5).

#### Sync — Evasion Detection (3 fields, ~22 packed signals)

| Field | Source | Value |
|-------|--------|-------|
| `evasionDetected` | Privacy tool / spoof detection | comma-separated signal names |
| `stealthSignals` | Timing-based stealth detection | comma-separated signal names |
| `evasionSignalsV2` | Consolidated evasion summary | comma-separated signal names |

**Signals packed inside `evasionDetected`**: `tor-screen`, `tor-likely`, `brave`, `webrtc-blocked`, `ua-platform-mismatch`, `mobile-ua-desktop-screen`, `touch-mismatch`, `partial-js-block`, `clienthints-platform-mismatch`.

**Signals packed inside `stealthSignals`**: `webdriver-slow`, `platform-slow`, `timing-error`, `toString-spoofed`, `toString-blocked`, `nav-proto-modified`, `proxy-modified`.

**Signals packed inside `evasionSignalsV2`**: `tor-letterbox-viewport`, `tor-letterbox-screen`, `minimal-fonts`, `canvas-noise`, `canvas-blocked`, `audio-noise`, `font-spoof`, `stealth-detected`.

#### Sync — Cross-Signal Analysis (3 fields, ~20+ packed signals)

| Field | Source | Value |
|-------|--------|-------|
| `crossSignals` | Cross-field contradiction detection | comma-separated flag names |
| `anomalyScore` | Weighted sum of cross-signal contradictions | integer 0-100+ |
| `combinedThreatScore` | `botScore + min(anomalyScore, 25)` | integer |

**Signals packed inside `crossSignals`**: `win-fonts-on-mac` (+15), `win-fonts-on-linux` (+15), `mac-fonts-on-win` (+10), `safari-google-vendor` (+20), `safari-has-chrome-obj` (+15), `safari-has-client-hints` (+10), `safari-chromium-gpu` (+15), `swiftshader-gpu` (+5), `swiftshader-on-mac` (+20), `swiftshader-on-linux` (+10), `llvmpipe-on-mac` (+20), `round-heap-limit` (+5), `instant-page-load` (+5), `zero-latency-connection` (+3), `connection-missing-rtt` (+5), `webgl2-on-old-safari` (+10), `gpu-platform-mismatch` (+15), `software-gpu-on-mac` (+10), `scroll-no-depth` (+8, from behavioral), `uniform-timing` (+5, from behavioral), `uniform-speed` (+5, from behavioral).

#### Behavioral — Mouse & Scroll (10 fields, collected during 500ms window)

| Field | Source | Value |
|-------|--------|-------|
| `mouseMoves` | `mousemove` event count (capped at 50) | integer |
| `mouseEntropy` | Variance of angles between consecutive moves × 1000 | integer (0 = no movement or straight lines) |
| `moveTimingCV` | Coefficient of variation of time gaps × 1000 | integer (< 300 = metronomic = bot) |
| `moveSpeedCV` | Coefficient of variation of movement speed × 1000 | integer (< 200 = constant speed = bot) |
| `moveCountBucket` | Classification of move count | 'low' (<5), 'mid' (<20), 'high' (<50), 'very-high' (50) |
| `mousePath` | Raw mouse trajectory as `x,y,t\|x,y,t\|...` | string (max 2000 chars) |
| `behavioralFlags` | Autodetected behavioral anomalies | 'uniform-timing', 'uniform-speed', or both |
| `scrolled` | `scroll` event fired | 1/0 |
| `scrollY` | `window.scrollY` at time of scroll | pixels |
| `scrollContradiction` | scrolled=1 but scrollY=0 (synthetic scroll event) | 1/0 |

#### Script Metadata (1 field)

| Field | Source | Value |
|-------|--------|-------|
| `scriptExecTime` | `Date.now() - startTime` (measured mid-execution, inside botSignals) | milliseconds |

#### Internal / Debug (1 field)

| Field | Source | Value |
|-------|--------|-------|
| `_proxyBlocked` | `safeGet()` catches Proxy trap errors, appends property name | comma-separated blocked property names |

---

#### Summary: 159 Named Fields by Execution Type

| Type | Count | Fields |
|------|-------|--------|
| **Sync** (immediate) | 130 | Screen (14 incl. screenExtended), navigator, canvas, WebGL, fonts, voices, gamepads, plugins, features, CSS media, timezone, timing, heap, page, document, math/error/CSS FP, bot signals, evasion, cross-signals |
| **Async** (during 500ms window) | 19 | audio (4), localIp, storageQuota/Used, battery (2), mediaDevices (2), UA high-entropy (7), botPermInconsistent |
| **Behavioral** (at send time) | 10 | mouse (7 incl. mousePath), scroll (3) |

**Total unique `data.*` keys**: 159
**Total distinct signals** (including packed composites): 230+
**Underlying browser API calls**: 280+ (WebGL alone queries 23 parameters)

#### Data Points to ADD (not yet in script)

_None — all planned fields implemented as of Phase 1._

#### Data Points NOT to Add (popup/gesture risk — VETOED)

- `navigator.keyboard.getLayoutMap()` — requires user gesture
- `navigator.mediaDevices.getUserMedia()` — camera/mic permission popup
- Any API that triggers a permission prompt or visible UI

---

## 4. IIS Fast Pass — In-Memory Enrichment (~5 microseconds)

This runs synchronously on the IIS thread inside `CaptureAndEnqueue` (TrackingEndpoints.cs lines 295-448). Everything here is in-memory, zero-allocation where possible, and completes before the GIF is returned to the browser.

### 4.1 Steps (in order)

| Step | Service | What It Does | State |
|------|---------|-------------|-------|
| 1 | `TrackingCaptureService` | Parse HTTP request → `TrackingData` record (IP, URL, QueryString, Headers, Referer, etc.) | Stateless |
| 2 | Inline | Hit-type detection: QueryString contains `sw` or `canvasFP`? → `modern` vs `legacy` | Stateless |
| 3 | Inline | Legacy referer fallback: if no Referer header but `?ref=` exists, copy it in | Stateless |
| 4 | `FingerprintStabilityService` | Per-IP fingerprint history in `IMemoryCache` (24h sliding window). Layer 1: 3+ unique FPs from same IP = anti-detect browser. Layer 2: 50+ observations = high volume. 20+ in 5 min = high rate. | Per-IP, 24h |
| 5 | `IpBehaviorService` | Subnet /24 velocity: 3+ IPs from same /24 in 5 min = bot farm. Rapid-fire: 2+ hits from same IP in 15s = automation. Sub-second gap = definite bot. | Per-IP/subnet, 2-10 min |
| 6 | `DatacenterIpService` | Binary prefix trie of ~8,500 AWS + GCP CIDR ranges. O(32) lookup. Downloaded at startup, refreshed weekly. | Global, immutable between refreshes |
| 7 | `IpClassificationService` | Zero-allocation bitwise IPv4 classifier against 16 reserved ranges → Public/Private/Loopback/CGNAT/LinkLocal/Multicast/etc. Determines if IP is worth geolocating. | Stateless |
| 8 | `GeoCacheService` | Two-tier cache: ConcurrentDictionary (hot, zero-alloc read) → MemoryCache (1h TTL) → async SQL queue (Channel, single reader). Returns immediately, never blocks. First hit = no geo (queued for SQL). Next hit = cached. | Per-IP, 1h TTL |
| 9 | Inline | Timezone mismatch: client-reported TZ (from JS `Intl.DateTimeFormat`) vs IP-derived TZ (from geo). Mismatch = VPN/proxy signal. | Stateless |
| 10 | Inline | Build enriched QueryString via ThreadStatic StringBuilder. Appends `_srv_*` params for all enrichment results. | Stateless |
| 11 | `DatabaseWriterService` | Lock-free CAS enqueue to `Channel<T>` (bounded 10,000). Background thread drains → `SqlBulkCopy` → `PiXL.Raw`. | In-memory buffer |
| 12 | Inline | Return pre-allocated 43-byte transparent GIF with `Cache-Control: no-store`. | Stateless |

### 4.2 Server-Side _srv_* Parameters Appended

Always: `_srv_hitType=modern|legacy`

Conditional:
- `_srv_botTrap=1` — URL didn't match valid PiXL pattern
- `_srv_fpAlert=1`, `_srv_fpObs`, `_srv_fpUniq`, `_srv_fpRate5m` — fingerprint stability alerts
- `_srv_subnetIps`, `_srv_subnetHits`, `_srv_hitsIn15s`, `_srv_lastGapMs` — IP behavior
- `_srv_subSecDupe=1`, `_srv_subnetAlert=1`, `_srv_rapidFire=1` — specific IP behavior flags
- `_srv_dc=AWS|GCP` — datacenter IP
- `_srv_ipType=N` — IP classification enum
- `_srv_geoCC`, `_srv_geoReg`, `_srv_geoCity`, `_srv_geoTz`, `_srv_geoISP` — geolocation
- `_srv_geoProxy=1`, `_srv_geoMobile=1` — IPAPI flags
- `_srv_geoTzMismatch=1` — timezone contradiction

### 4.3 Why This Stays on IIS

All of this runs in ~5μs. Moving it to the Forge would add ~50-100μs of named pipe serialization overhead. The Forge is for things that take **milliseconds**, not microseconds. The fast pass stays fast.

---

## 5. SmartPiXL Forge — Slow Enrichment (Unlimited Time)

### 5.1 Architecture

The Forge is a Windows Service that receives records from IIS via named pipe. It processes them asynchronously with no time constraint. If IIS can't reach the pipe, it writes JSONL failover files to disk. The Forge's catch-up loop replays those files on a 60-second interval.

**IIS becomes a dumb relay**: parse + fast enrichment + pipe + GIF. All slow enrichment and all SQL writes live in the Forge. IIS can crash, recycle, or redeploy freely — data is either in the pipe, in the Forge's buffer, or on disk.

### 5.2 Durability Model

```
IIS → named pipe → Forge buffer → SqlBulkCopy → PiXL.Raw

If pipe fails:
IIS → JSONL to Failover/ directory (durable on disk)

If Forge crashes:
JSONL files survive on disk → Forge restarts → catch-up loop reads & processes them

If SQL fails:
Forge's dead-letter writes JSONL → retries on recovery

Result: ZERO data loss under any failure mode.
```

Current architecture gap: if IIS recycles/crashes, the Channel<T> buffer evaporates silently. The Forge eliminates this.

### 5.3 Enrichments — Tier 1 (Day 1)

These are straightforward API/library integrations:

| Enrichment | Library / API | What It Does | What It Replaces |
|------------|--------------|-------------|-----------------|
| **IPAPI Pro lookup** | `ip-api.com/json/{ip}?key=...` (existing paid access) | Geo, ISP, proxy, mobile, reverse DNS, ASN for new/stale IPs. In-memory HashSet of all known IPs — only call API for genuinely new IPs or stale > 90 days. | Legacy 2 AM batch job that blasts 6M rows daily. Saves massive API cost. Data available before SQL insert instead of next-day. |
| **Bot UA detection** | `NetCrawlerDetect` NuGet package | Check User-Agent against known bot/crawler database. Flag known bots before SQL insert. | Nothing — new capability. Client-recommended library. |
| **UA parsing** | `UAParser` or `DeviceDetector.NET` NuGet | Parse raw User-Agent into structured Browser/Version/OS/Device. Identifies specific device models, IoT, smart TVs, consoles. | Raw UA string stored unstructured. ETL has to regex it. |
| **DNS reverse lookup** | `DnsClient` NuGet | `nslookup IP` → `ec2-1-2-3-4.amazonaws.com` vs `pool-1-2-3-4.verizon.net`. Cloud hostname = bot signal. ISP hostname = likely human. | Nothing — new capability. |
| **MaxMind GeoIP2** | `MaxMind.GeoIP2` NuGet + GeoLite2 `.mmdb` | Offline geo lookup, ~1μs, ~95% accuracy. Replaces most IPAPI calls. IPAPI becomes fallback for edge cases and fields MaxMind doesn't cover (proxy detection, mobile carrier). | Reduces IPAPI call volume by ~95%. |
| **WHOIS / ASN** | `Whois.NET` NuGet | What autonomous system owns this IP block? Catches small cloud providers the CIDR trie misses. | Nothing — new capability beyond the existing AWS/GCP trie. |

**IPAPI integration** (from legacy code, ready to activate):
```csharp
// Legacy IPAPI call pattern (from user's existing code):
// GET https://pro.ip-api.com/json/{ip}?key=oJC4NplwJaCnbWw
// Returns: Country, CountryCode, Region, RegionName, City, Zip,
//          Lat, Lon, Timezone, ISP, Org, As, Reverse, Mobile, Proxy, Status, Message
// Forge wraps this with:
// - In-memory HashSet check: skip if IP is known AND fresh (< 90 days)
// - Batch parallel calls for multiple new IPs
// - Cache result in memory and write to IPAPI.IP table
```

### 5.4 Enrichments — Tier 2 (Cross-Request Intelligence)

These use the Forge's unique position: stateful, sees all customers, real-time.

| Enrichment | What It Does | Why Only The Forge Can Do It |
|------------|-------------|-------------------------------------|
| **Cross-customer intelligence** | Track IP+fingerprint→customer hit count with sliding window. Same IP+FP hitting 26 different customers in 2 minutes = bot (each customer only sees 1 clean hit). | Multi-tenant visibility. No single customer can see this. No single-site analytics tool can see this. Competitive moat. |
| **Lead quality scoring** | Reverse of bot scoring. Residential IP + consistent FP + real mouse entropy + 3+ fonts + clean canvas + matching TZ = high-quality human. Score 0-100. | Requires combining all enrichment data (IIS fast pass + Forge enrichments) in one place. |
| **Session stitching** | In-memory session graph by fingerprint. Track: FP `abc123` → `site.com/pricing` at 10:01 → `/features` at 10:02 → `/signup` at 10:03. Record arrives with `_srv_sessionId`, `_srv_sessionHitNum`, `_srv_sessionDurationSec`. | Real-time cross-page journey without cookies. ETL doesn't have to reconstruct sessions after the fact. |
| **Device affluence signal** | GPU model + cores + deviceMemory + screen resolution + platform → affluence score. RTX 4090 + 16 cores + 4K + macOS = high-value visitor. Intel HD 4000 + 4 cores + 1366x768 = budget. | Car dealership use case: expensive hardware = money to burn = easier to convert. Demographic proxy without cookies or login. |

### 5.5 Enrichments — Tier 3 (Asymmetric Detection)

Weird shit no one else does:

| Enrichment | What It Does | Why It's Powerful |
|------------|-------------|-------------------|
| **Geographic arbitrage** | Cross-reference cultural fingerprint (language, fonts, keyboard, calendar, date/number format) against IP-derived geography. French fonts + Vietnamese language + Persian calendar on a "US" IP = VPN user. Cultural consistency score. | Goes WAY beyond TZ mismatch. Catches sophisticated VPN users that simple checks miss. Uses data already being collected (fonts, locale, dateFormat, numberFormat). |
| **Device age estimation** | Map GPU model → release year, cross-reference with OS version, WebGL params, screen resolution. Modern browser on 2012 hardware from a datacenter IP with zero mouse movement = headless bot in a Docker container. | Device age is implausible for the behavioral pattern. Hard for bot operators to fake because they'd need to understand GPU/OS/screen generation relationships. |
| **Contradiction matrix** | Rule set: "if field A = X, then field B should be in range [Y₁..Y₂]". Examples: Mobile=true + 2560x1440 + mouse moves = impossible. macOS + DirectX GPU = impossible. Battery API present + macOS Safari = impossible. Each contradiction increments `_srv_contradictions`. | Extremely hard for bot operators to get right. Requires understanding relationships between hundreds of fields across every OS/browser/device combination. |
| **Behavioral replay detection** | Hash the raw mouse movement path (50 points). Maintain recent hash set. Identical mouse path from different fingerprints = someone recording real sessions and replaying with rotated FPs. | Catches the most sophisticated bots: anti-detect browsers + behavioral replay is the current state of the art in evasion. Almost nobody detects this. |
| **Dead Internet measurement** | Per customer, per hour: total hits, hits with bot signals, zero-mouse hits, datacenter hits, contradiction hits, replay hits, unique FPs / total ratio, cross-customer pollination rate. Track trends over time. | SmartPiXL is positioned to quantify bot traffic across a diverse set of websites because it sees raw traffic before ad networks filter it. If the dead internet theory has teeth, these ratios trend upward. |

### 5.6 Enrichments — Future (6+ months)

| Enrichment | Dependency | Notes |
|------------|-----------|-------|
| **ML bot probability** | ML.NET or Accord.NET | Train on PiXL.Parsed data. All 190+ fields → single bot probability score. Runs in-process, no Python, no external service. |
| **LLM-powered analysis** | Ollama + MSSQL 2025 vector store | Owner has RAG experience with this stack. Hardware constraint: only GPU is an RTX 4090 at office (8.27.24.2), not at colo. Requires clever data routing across the internet. |

---

## 6. .NET Libraries — Approved for the Forge

### 6.1 Install on Day 1

| Library | NuGet Package | Purpose |
|---------|--------------|---------|
| **NetCrawlerDetect** | `NetCrawlerDetect` | Bot/crawler UA detection. Client-recommended. |
| **UAParser** | `UAParser` | Structured UA parsing (Browser/OS/Device). Uses Google's regex database. |
| **DeviceDetector.NET** | `DeviceDetector.NET` | Deep device identification. 10,000+ patterns. IoT, TVs, consoles, car browsers. |
| **DnsClient** | `DnsClient` | Pure .NET async DNS resolver. Reverse DNS lookups. Pooled connections + caching. |
| **MaxMind.GeoIP2** | `MaxMind.GeoIP2` | Offline geo database (~1μs lookup, free GeoLite2 tier). Replaces ~95% of IPAPI calls. |
| **Whois.NET** | `Whois.NET` | WHOIS / ASN lookups. Local, no external service dependency. |
| **MathNet.Numerics** | `MathNet.Numerics` | Statistical analysis for contradiction scoring, affluence estimation, cultural consistency. |
| **FuzzySharp** | `FuzzySharp` | Fuzzy string matching. Near-duplicate UA detection. Potential SQL CLR candidate. |

### 6.2 Install Later

| Library | NuGet Package | Purpose | When |
|---------|--------------|---------|------|
| **ML.NET** | `Microsoft.ML` | AutoML model training on PiXL.Parsed. Bot probability scorer. ONNX support. | 6 months |
| **Accord.NET** | `Accord.MachineLearning` | Decision trees, random forests, SVM, clustering. All in-process. | 6 months |

### 6.3 Library Evaluation: NetCrawlerDetect vs MyCSharpBot

NetCrawlerDetect is the approved choice (client-recommended). MyCSharpBot is similar in scope but less actively maintained. Both can coexist — NetCrawlerDetect for the primary bot UA check, MyCSharpBot as a secondary cross-reference if desired.

### 6.4 MaxMind Notes

- **GeoLite2 ACQUIRED** (2026-02-19). All three databases downloaded from GitHub mirror (`P3TERX/GeoLite.mmdb`) which auto-updates from MaxMind's free releases. No MaxMind account needed. Files stored at `C:\GeoLite2\`:
  - `GeoLite2-City.mmdb` — 54.2 MB (city-level geolocation)
  - `GeoLite2-ASN.mmdb` — 11 MB (autonomous system numbers)
  - `GeoLite2-Country.mmdb` — 9.2 MB (country-level)
- **Loaded into SQL Server** (Geo schema on `localhost\SQL2025`, database `SmartPiXL`):
  - `Geo.CityBlock` — 1,349,448 rows (IP network CIDR → lat/lon/postal/geoname_id, with integer bucket computed columns)
  - `Geo.CityLocation` — 59,936 rows (geoname_id → country/region/city/timezone/continent/EU flag)
  - `Geo.ASN` — 507,792 rows (IP network CIDR → AS number + organization name)
  - `Geo.ImportLog` — tracks import history for refresh auditing
- **Converter tool** at `C:\GeoLite2\converter\GeoLiteImporter\` — reads mmdb via `MaxMind.GeoIP2` NuGet, walks all routable IPv4 /24 blocks, and `SqlBulkCopy`s into SQL. Re-run to refresh after downloading updated mmdb files.
- **Update cadence**: MaxMind releases GeoLite2 updates weekly (Tuesdays). The GitHub mirror auto-publishes same day. GeoLite2 EULA requires refreshing within 30 days of each release. A scheduled task to re-download + re-import monthly is sufficient.
- **MaxMind.MinFraud** is a paid API service, NOT a local database. Not authorized for additional costs. Skip unless pricing changes.
- Strategy: MaxMind for fast local geo (offline, ~1μs via mmdb OR SQL JOIN via Geo.CityBlock). IPAPI Pro for fields MaxMind doesn't cover (proxy detection, mobile carrier detail). IPAPI also serves as fallback if MaxMind lookup fails.

---

## 7. TrafficAlert Subsystem

### 7.1 Vision

Originally planned as its own product. Now integrated into SmartPiXL as a subsystem that informs customers how much of their traffic is bots and how valuable their human traffic is.

### 7.2 Metrics Per Visitor

| Metric | Range | Source |
|--------|-------|--------|
| `botScore` | 0-100+ | PiXL script (client-side signals) |
| `anomalyScore` | 0-100+ | PiXL script (cross-signal analysis) |
| `combinedThreatScore` | 0-125+ | PiXL script (botScore + capped anomalyScore) |
| `leadQualityScore` | 0-100 | Forge (reverse bot scoring) |
| `affluenceSignal` | LOW / MID / HIGH | Forge (GPU + cores + RAM + screen) |
| `culturalConsistency` | 0-100 | Forge (geographic arbitrage) |
| `contradictionCount` | 0-N | Forge (impossible combination matrix) |
| `sessionQuality` | derived | Forge (session stitching — pages visited, duration, navigation pattern) |
| `mouseAuthenticity` | derived | Forge (entropy + timing CV + speed CV + replay check) |

### 7.3 Aggregate Metrics Per Customer

- Total hits, bot hits, human hits, unknown hits
- Bot percentage over time (daily/weekly/monthly trend)
- Lead quality distribution
- Cross-customer pollination rate
- Session depth and engagement metrics
- Device/browser/OS/platform breakdown
- Geographic distribution vs cultural consistency

---

## 8. Database Design Principles

### 8.1 Decoupled Dimensions

The legacy system kept IP, Device, and Visit as one flat object — rampant duplication. The modern schema deliberately normalizes by decoupling Device-level info and IP-level info from the Visit:

- `PiXL.Device` — global device dimension, identified by `DeviceHash` (hash of 5 fingerprint fields). Tracked across all PiXLs, all customers, all time. This is what makes SmartPiXL *Smart* — the same device showing up at a car dealership on Monday and a mortgage broker on Wednesday is the same device, and only SmartPiXL knows that.
- `PiXL.IP` — global IP dimension. Geo, ISP, classification, datacenter flag. Decoupled from Visit because thousands of visits share the same IP.
- `PiXL.Visit` — fact table, 1:1 with PiXL.Parsed. Foreign keys to Device and IP. Carries a `ClientParamsJson` column (SQL 2025 native `json` type) for extensible client parameters.
- `PiXL.Match` — identity resolution output (visitor ↔ known consumer from AutoConsumer).

This normalization is what enables cross-PiXL, cross-domain, cross-customer intelligence. The legacy system couldn't do any of that because everything was one flat row.

#### PiXL.Parsed — The Research Table

`PiXL.Parsed` is a fully materialized, denormalized table with **every single data point we're collecting as its own column**. Currently ~175 typed columns. It will grow past 300+ columns as we add Forge enrichments. This is deliberate.

**PiXL.Parsed is a research/debug table, not a production query surface.** It exists so we can:
- Inspect any individual hit with all its data points as typed columns
- Run ad-hoc analytics during development before the dimensional model is finalized
- Validate ETL correctness (compare raw QueryString against parsed columns)
- Discover which data points have signal value vs noise

Production dashboards and APIs read from the dimensional model (Visit + Device + IP + Match). PiXL.Parsed is the development workbench. We'll trim it back when we know what matters. Until then, every data point gets a column.

### 8.2 ETL Pipeline

The ETL pipeline (Worker service, every 60s) remains responsible for:
1. `ETL.usp_ParseNewHits` — parse PiXL.Raw querystrings into ~175 typed columns in PiXL.Parsed + populate Device/IP/Visit dimensions
2. `ETL.usp_MatchVisits` — identity resolution against AutoConsumer
3. `ETL.usp_EnrichParsedGeo` — backfill geo data from IPAPI.IP

With the Forge, many fields arrive pre-enriched. The ETL still runs for historical consistency and any enrichments that require SQL-side JOINs.

### 8.3 SQL-Side Analysis & CLR Functions (Designed)

SQL handles things that require historical data the Forge doesn't have. The Forge sees traffic in real-time but forgets. SQL has 7.2M raw rows, 2M parsed rows, 343M geolocation rows, 616K IPs, and 414K matches. That's longitudinal memory.

**SQL Server 2025 features confirmed available:**
- Native `vector` type with `VECTOR_DISTANCE()` (cosine, dot product, euclidean)
- Native `json` type with `CREATE JSON INDEX`, `JSON_OBJECTAGG`, `JSON_ARRAYAGG`
- Graph tables (NODE/EDGE with MATCH, SHORTEST_PATH)
- Full-Text Indexing (for fuzzy UA/string matching as an alternative to CLR)
- CLR currently disabled (`clr enabled = 0`) — needs `sp_configure` + assembly signing

#### Pure T-SQL Analysis (no CLR needed)

**1. Impossible Travel Detection**
Same DeviceHash appearing from two different GeoCountries within X hours. Window functions on PiXL.Visit joined to PiXL.IP. Device in New York at 10 AM and London at 10:30 AM = VPN, shared credentials, or credential stuffing. High-value intelligence for identity resolution confidence scoring.

**2. Subnet Reputation Scoring (Aggregate)**
IpBehaviorService tracks /24 velocity in real-time (5-minute window). SQL tracks it across **all time**. Per /24 subnet: unique IPs, unique devices, total hits, avg bot score, bot %. Materialized into a `PiXL.SubnetReputation` table, updated daily. The Forge checks it in real-time: "this IP is from a subnet with 87% bot rate across 6 months."

**3. Device Lifecycle (This IS SmartPiXL)**
This is the core value proposition of the normalized schema. The legacy system couldn't track a device across domains because everything was one flat row with rampant duplication. Now, with Device and IP decoupled from Visit:
- **Return frequency**: median days between visits per device
- **Customer hop pattern**: which companies does this device visit? (cross-customer intelligence at the SQL level)
- **Fingerprint drift**: same DeviceHash but UA version changes over time = real user updating their browser (not a bot rotating UAs)
- **Dormancy detection**: device gone for 60+ days then returns = same person, same machine. Valuable for lead re-engagement.

This is `PiXL.Visit` and its FK'd tables. This is what the entire platform is built for.

**4. Customer Traffic Quality Trending**
Per-company, per-month: total hits, avg bot score, bot %, unique visitors. When the platform is live, this powers customer-facing reports: "Your bot traffic dropped 12% this month" or "Your lead quality score improved."

**5. Session Reconstruction via Window Functions**
Stitch page views into sessions (same device, gap < 30 min). Pages/session, duration, bounce rate. We want **both** — real-time session stitching in the Forge (data arrives enriched with `_srv_sessionId`) AND historical session reconstruction in SQL (complete sessions visible after the fact, with full enrichment data). The Forge sees sessions as they happen. SQL sees what happened after the dust settles.

**6. Cross-Customer Intelligence (Historical)**
Devices that hit 5+ different companies historically = scraper, researcher, or competitor spy. Part of the plan — this is one of SmartPiXL's competitive moats.

**7. Dead Internet Index**
Per customer, per week: definite-bot %, zero-mouse-bot %, likely-human %. Track the trend over time. This doesn't have direct M1 value per se, but we want to see what we can see. SmartPiXL is uniquely positioned to measure this because it sees raw traffic across a diverse set of websites before ad networks filter it.

#### SQL Server 2025 Specific

**8. Vector Fingerprint Similarity (Identity Resolution Without Cookies)**

SQL 2025 has a native `vector` type with `VECTOR_DISTANCE()` for cosine similarity. Instead of exact DeviceHash matching (binary — either matches or doesn't), encode visitor characteristics as a **vector** and find similar visitors:

```sql
ALTER TABLE PiXL.Device ADD FingerprintVector VECTOR(64) NULL;

-- Find similar devices (visitors who changed one thing — dark mode, browser update, etc.)
SELECT TOP 10
    d1.DeviceId, d2.DeviceId AS SimilarDevice,
    VECTOR_DISTANCE('cosine', d1.FingerprintVector, d2.FingerprintVector) AS Distance
FROM PiXL.Device d1
CROSS APPLY (
    SELECT TOP 10 DeviceId, FingerprintVector
    FROM PiXL.Device
    WHERE DeviceId <> d1.DeviceId
    ORDER BY VECTOR_DISTANCE('cosine', d1.FingerprintVector, FingerprintVector)
) d2
WHERE d1.DeviceId = @targetDevice
```

Visitor clears browser, changes one setting, gets a new DeviceHash. Exact match fails. Vector similarity of 0.95+ catches it. **Nobody in the pixel tracking space does this.**

This also applies to **User-Agent drift detection**. Encode UA characteristics as a vector. Bot operators rotate UAs with minor changes (one version digit, one patch string). Cosine similarity catches UA clusters that exact-match misses. Same vector infrastructure, different input encoding.

**9. Native JSON Aggregation for API Responses**

Build complete dashboard JSON payloads in a single query:
```sql
SELECT JSON_OBJECTAGG(
    CompanyID VALUE JSON_OBJECT(
        'total': TotalHits,
        'botPct': BotPct,
        'uniqueVisitors': UniqueVisitors
    )
) AS DashboardPayload
FROM vw_Dash_CustomerSummary
```

We can also leverage the native `json` type for structured column storage (already doing this with `PiXL.Visit.ClientParamsJson`), and the native XML type if we need hierarchical report generation.

**10. Graph Tables for Identity Resolution Chains**

##### What is a graph table?

A graph table is a SQL Server feature that models relationships as first-class objects. Instead of JOINing through foreign keys manually, you define **nodes** (entities: Device, Person, IP) and **edges** (relationships: "uses", "resolves to", "visited from") as separate tables. SQL Server then provides the `MATCH` clause and `SHORTEST_PATH` function to traverse these relationships in a single query — no recursive CTEs, no self-joins, no temp tables.

Think of it like a game engine's scene graph. Each node is an entity in the world, each edge is a relationship. You can ask: "starting from this Person, walk every edge, find every connected Device, then find every IP those Devices used, then find every OTHER Person who used those IPs." That's multi-hop traversal, and graph tables do it in one statement.

```sql
-- Define the graph
CREATE TABLE Graph.Device AS NODE (DeviceId BIGINT, DeviceHash VARBINARY(32));
CREATE TABLE Graph.Person AS NODE (Email VARCHAR(256), IndividualKey VARCHAR(35));
CREATE TABLE Graph.IpAddress AS NODE (IP VARCHAR(50));

CREATE TABLE Graph.UsesIP AS EDGE;       -- Device --uses--> IP
CREATE TABLE Graph.ResolvesTo AS EDGE;   -- Device --resolves_to--> Person

-- Multi-hop identity resolution: Person → Device → IP → (other Devices) → (other People)
SELECT Person.Email, Device.DeviceHash,
       LAST_VALUE(IpAddress.IP) WITHIN GROUP (GRAPH PATH)
FROM Graph.Person, Graph.ResolvesTo, Graph.Device,
     Graph.UsesIP FOR PATH, Graph.IpAddress FOR PATH
WHERE MATCH(Person<-(ResolvesTo)-Device-(UsesIP)->IpAddress+)
  AND Person.Email = 'target@example.com'
```

"Show me every device this person has ever used, every IP those devices touched, and every OTHER person who shared those devices or IPs." That's identity resolution that would take 50+ lines of recursive CTEs, done in one clean query.

#### CLR Functions (Things T-SQL Is Terrible At)

CLR must be enabled first: `EXEC sp_configure 'clr enabled', 1; RECONFIGURE;`
With SQL 2025 strict security, assemblies need to be signed (certificate-based) or database set to TRUSTWORTHY.

> **VALIDATED (2026-02-20):** SQL Server 2025 RTM-GDR (17.0.1050.2) CLR host is `.NET Framework v4.0.30319`, NOT modern .NET. Assemblies MUST target `net48`. .NET 10 assemblies are rejected with "references assembly 'system.runtime, version=10.0.0.0' which is not present." We use `<LangVersion>latest</LangVersion>` with `net48` to get modern C# syntax (pattern matching, nullable, switch expressions) compiled by Roslyn into Framework-compatible IL. Runtime features (Span<T>, SIMD, tiered JIT) are not available under the Framework CLR host but are irrelevant for our pure scalar functions. Assembly requires `PERMISSION_SET = UNSAFE` for `Regex` with `RegexOptions.Compiled` and `ConcurrentDictionary`; security enforced via certificate-based signing in master.

**11. CLR: Subnet Math (`dbo.GetSubnet24`)**

T-SQL parsing an IP address into a /24 subnet requires ugly `CHARINDEX`/`REVERSE`/`SUBSTRING` chains. CLR does it in nanoseconds:
```csharp
[SqlFunction(IsDeterministic = true, IsPrecise = true)]
public static SqlString GetSubnet24(SqlString ipAddress)
{
    ReadOnlySpan<char> ip = ipAddress.Value.AsSpan();
    int lastDot = ip.LastIndexOf('.');
    return lastDot > 0 ? new SqlString(ip[..lastDot].ToString() + ".0/24") : SqlString.Null;
}
```
Now every query can `GROUP BY dbo.GetSubnet24(IPAddress)`. The subnet reputation query becomes clean and fast.

**12. CLR: Fuzzy String Matching (`dbo.JaroWinkler`) — With Caveats**

Near-duplicate User-Agent detection. Bot operators rotate UAs slightly: `AppleWebKit/537.36` vs `AppleWebKit/537.37`.

**Owner note**: Levenshtein/Jaro-Winkler has been tried before in CLR and was not fast enough for large-scale scans. Three options to evaluate:

| Approach | Pros | Cons |
|----------|------|------|
| **CLR + FuzzySharp** | Fine-grained control, ~1μs per pair | Still O(n²) for pairwise comparison. Slow at scale. |
| **Full-Text Indexing** | Native SQL Server feature, handles fuzzy/proximity search, no CLR needed | Designed for document search, not short-string similarity. May not capture 1-char UA diffs. |
| **Vector Similarity** | Encode UAs as vectors, use `VECTOR_DISTANCE`. Same infrastructure as fingerprint vectors. O(n) with vector index. | Need a good UA→vector encoding. Worth testing. |

Recommendation: test vector similarity first (it's already needed for fingerprint similarity — item #8). If UA vectors work, skip CLR fuzzy entirely. If not, enable CLR + FuzzySharp as fallback.

**13. CLR: Regex (`dbo.RegexExtract`, `dbo.RegexMatch`)**

SQL Server has `LIKE` and `PATINDEX` but **no regex**. Period. This unlocks:
- Extract domain from referrer URL: `dbo.RegexExtract(PageReferrer, '://([^/]+)', 1)`
- Validate email format in MatchEmail
- Parse structured bot signal names from BotSignalsList
- Pattern-match on URL paths for funnel analysis
- Will have use across the platform beyond PiXL analysis

```csharp
[SqlFunction(IsDeterministic = true)]
public static SqlString RegexExtract(SqlString input, SqlString pattern, SqlInt32 group)
{
    var match = Regex.Match(input.Value, pattern.Value);
    return match.Success ? new SqlString(match.Groups[group.Value].Value) : SqlString.Null;
}
```

**14. Geo Calculations — Integer Bucket Approach (Not Raw Haversine)**

For geographical work (impossible travel, radius-based zipcode matching), we are NOT doing raw Haversine or `geography` type calculations. The platform owner built the LocationIQ platform for M1 Data and knows from experience that float-based geo math in SQL is treacherous at scale — slow, rounding-error-prone, and optimizer-hostile.

Instead: **integer bucket geo**. Cast coordinates to integer centroids, do coarse matching first, then refine. Why use a float when an int will do. Your CPU will thank you.

The proven pattern from LocationIQ:
```sql
-- Phase 1: Coarse bucket (lat*100, lon*100) — eliminates 99% of non-matches
;WITH CoarseBuckets AS (
    SELECT
        s.ad_id,
        CAST(s.latitude * 100 AS INT) AS lat_100,
        CAST(s.longitude * 100 AS INT) AS lon_100,
        COUNT(*) AS coarse_count
    FROM dbo.CurrentStaging s
    GROUP BY s.ad_id, CAST(s.latitude * 100 AS INT), CAST(s.longitude * 100 AS INT)
),
CoarseWinner AS (
    -- Pack count + lat + lon into a single BIGINT (game dev bit-packing trick)
    SELECT
        ad_id,
        MAX(CAST(coarse_count AS BIGINT) * 10000000000
          + CAST((lat_100 + 9000) AS BIGINT) * 100000
          + CAST((lon_100 + 18000) AS BIGINT)) AS packed
    FROM CoarseBuckets
    GROUP BY ad_id
)
-- Phase 2: Fine bucket (lat*10000, lon*10000) — within winning coarse bucket only
SELECT
    s.ad_id,
    CAST(s.latitude * 10000 AS INT) AS lat_bucket,
    CAST(s.longitude * 10000 AS INT) AS lon_bucket,
    COUNT(*) AS bucket_count,
    AVG(s.latitude) AS centroid_lat,
    AVG(s.longitude) AS centroid_lon
INTO #FineBuckets
FROM dbo.CurrentStaging s
INNER JOIN CoarseWinner w
    ON s.ad_id = w.ad_id
    AND CAST(s.latitude * 100 AS INT) = CAST(w.packed / 100000 % 100000 AS INT) - 9000
    AND CAST(s.longitude * 100 AS INT) = CAST(w.packed % 100000 AS INT) - 18000
GROUP BY
    s.ad_id,
    CAST(s.latitude * 10000 AS INT),
    CAST(s.longitude * 10000 AS INT);
```

Key insight: `CAST(latitude * 100 AS INT)` turns a float into an integer grid cell. Coarse pass filters by ~1.1 km grid squares (at equator). Fine pass refines to ~11 meter grid squares. Integer comparison is orders of magnitude faster than `geography::STDistance()`. And the bit-packing into a BIGINT (`count * 10B + lat_offset * 100K + lon_offset`) is pure game dev — pack multiple values into one sortable integer so `MAX()` gives you the winning bucket in a single pass.

For impossible travel: coarse-bucket the two IPs' lat/lon pairs. If they're in the same bucket, travel is trivially possible. If they're in different buckets, check the bucket distance (integer subtraction) against the time delta. Only call `geography::STDistance()` for the rare edge cases that fall near bucket boundaries.

**15. CLR: Feature Bitmap Encoder (`dbo.FeatureBitmap`) — With Explainer**

##### What is a feature bitmap?

A bitmap is a single integer where each **bit** represents a true/false flag. The 17 feature detection fields in PiXL (localStorage, sessionStorage, indexedDB, caches, WebWorkers, ServiceWorker, WASM, WebGL, WebGL2, canvas, touchEvent, pointerEvent, mediaDevices, clipboard, speechSynthesis, chromeObject, chromeRuntime) are each 1/0 values. Instead of storing and comparing 17 separate columns, pack them into one 32-bit integer:

```
Bit 0 (1)    = localStorage
Bit 1 (2)    = sessionStorage
Bit 2 (4)    = indexedDB
Bit 3 (8)    = caches
Bit 4 (16)   = webWorkers
Bit 5 (32)   = serviceWorker
Bit 6 (64)   = webAssembly
Bit 7 (128)  = webGL
Bit 8 (256)  = webGL2
Bit 9 (512)  = canvas
Bit 10 (1024) = touchEvent
Bit 11 (2048) = pointerEvent
Bit 12 (4096) = mediaDevices
Bit 13 (8192) = clipboard
Bit 14 (16384) = speechSynthesis
Bit 15 (32768) = chromeObject
Bit 16 (65536) = chromeRuntime
```

A device with all features except touch and pointer events: `0b11001111111111111` = integer `114687`. Now:
- `GROUP BY dbo.FeatureBitmap(...)` clusters devices by capability profile — one integer comparison instead of 17 column ANDs
- An index on the bitmap column is tiny (4 bytes per row vs 17 bytes)
- Bitwise AND/OR queries: "all devices with WebGL AND WASM but NOT touch" = `WHERE Bitmap & 192 = 192 AND Bitmap & 1024 = 0`

```csharp
[SqlFunction(IsDeterministic = true, IsPrecise = true)]
public static SqlInt32 FeatureBitmap(
    SqlBoolean ls, SqlBoolean ss, SqlBoolean idb, SqlBoolean caches,
    SqlBoolean ww, SqlBoolean swk, SqlBoolean wasm, SqlBoolean webgl,
    SqlBoolean webgl2, SqlBoolean canvas, SqlBoolean touchEvent,
    SqlBoolean pointerEvent, SqlBoolean mediaDevices, SqlBoolean clipboard,
    SqlBoolean speechSynth, SqlBoolean chromeObj, SqlBoolean chromeRuntime)
{
    int bits = 0;
    if (ls.IsTrue) bits |= 1;
    if (ss.IsTrue) bits |= 2;
    if (idb.IsTrue) bits |= 4;
    if (caches.IsTrue) bits |= 8;
    if (ww.IsTrue) bits |= 16;
    if (swk.IsTrue) bits |= 32;
    if (wasm.IsTrue) bits |= 64;
    if (webgl.IsTrue) bits |= 128;
    if (webgl2.IsTrue) bits |= 256;
    if (canvas.IsTrue) bits |= 512;
    if (touchEvent.IsTrue) bits |= 1024;
    if (pointerEvent.IsTrue) bits |= 2048;
    if (mediaDevices.IsTrue) bits |= 4096;
    if (clipboard.IsTrue) bits |= 8192;
    if (speechSynth.IsTrue) bits |= 16384;
    if (chromeObj.IsTrue) bits |= 32768;
    if (chromeRuntime.IsTrue) bits |= 65536;
    return new SqlInt32(bits);
}
```

**Other bitmap candidates** (things with many boolean/small-int fields):
- **Accessibility bitmap**: darkMode, lightMode, reducedMotion, reducedData, contrast, forcedColors, invertedColors, hover, standalone (9 bits)
- **Bot signal bitmap**: encode the top 20 most common bot signals as bits for fast `WHERE BotBitmap & X > 0` scans
- **Evasion bitmap**: canvasEvasion, webglEvasion, audioNoiseDetected, fontMethodMismatch, scrollContradiction, tor-likely signals (8+ bits)
- **Storage bitmap**: ls, ss, idb, caches, ck (5 bits — subset of feature bitmap, but useful standalone)

Rule of thumb: **if you have 4+ boolean columns that are frequently queried together, bitmap them.**

**16. CLR: MurmurHash3 for Consistent DeviceHash**

SQL's `HASHBYTES` does SHA2/MD5 — crypto-grade hashes designed for tamper resistance, not for hash table distribution. For non-crypto use cases (DeviceHash, fingerprint bucketing, consistent partitioning), crypto hashes are:
- **Slow**: ~2μs per call vs ~200ns for MurmurHash3
- **Over-distributed**: SHA2-256 produces 32 bytes when we only need 4-16 bytes for hash table operations
- **Collision-prone at truncation**: if you truncate SHA2 to 4 bytes for an INT column, collision rates are worse than purpose-built non-crypto hashes

MurmurHash3 is designed for hash tables: excellent distribution, minimal collisions at small output sizes, and 10x faster:

```csharp
[SqlFunction(IsDeterministic = true, IsPrecise = true)]
public static SqlBinary MurmurHash3(SqlString input)
{
    return new SqlBinary(MurmurHash3Impl.ComputeHash(Encoding.UTF8.GetBytes(input.Value)));
}
```

### 8.4 Zipcode Polygon Table

The legacy system was built around two modes:
1. **Nationwide IP matching** — match any IP in the US
2. **Radius-limited matching** — match IPs within a radius around zipcode centroids

The centroid approach is crude. A zipcode centroid is a single lat/lon point, and radius matching draws a circle around it. But zipcodes aren't circles — they're irregular polygons, especially in rural areas where one zipcode can span hundreds of square miles.

**The upgrade**: download ZCTA (Zip Code Tabulation Area) shapefiles from the US Census Bureau and store the actual polygon boundaries. This enables:
- **Point-in-polygon containment**: is this IP's lat/lon *inside* the zipcode boundary? (Not "within 5 miles of the centroid")
- **Adjacent zipcode detection**: which zipcodes border this one? (For expanded radius matching without circles)
- **Area-based density**: hits per square mile per zipcode (not per radius circle)

```sql
CREATE TABLE Geo.Zipcode (
    ZipcodeId       INT             NOT NULL IDENTITY(1,1),
    Zipcode         CHAR(5)         NOT NULL,
    State           CHAR(2)         NOT NULL,
    City            VARCHAR(100)    NULL,
    CentroidLat     DECIMAL(9,6)    NOT NULL,
    CentroidLon     DECIMAL(9,6)    NOT NULL,
    -- Integer buckets for fast coarse matching (LocationIQ pattern)
    LatBucket100    AS CAST(CentroidLat * 100 AS INT) PERSISTED,
    LonBucket100    AS CAST(CentroidLon * 100 AS INT) PERSISTED,
    -- Native geography type for the actual polygon boundary
    Boundary        GEOGRAPHY       NULL,       -- From Census ZCTA shapefile
    AreaSqMi        DECIMAL(10,2)   NULL,       -- Computed from Boundary
    Population      INT             NULL,       -- From Census data
    
    CONSTRAINT PK_Geo_Zipcode PRIMARY KEY CLUSTERED (ZipcodeId),
    CONSTRAINT UQ_Geo_Zipcode UNIQUE (Zipcode)
);

-- Integer bucket index for fast coarse-pass geo matching
CREATE NONCLUSTERED INDEX IX_Geo_Zipcode_Buckets
    ON Geo.Zipcode (LatBucket100, LonBucket100)
    INCLUDE (Zipcode, CentroidLat, CentroidLon);

-- Spatial index on the polygon boundary for STContains/STIntersects
CREATE SPATIAL INDEX SIX_Geo_Zipcode_Boundary
    ON Geo.Zipcode (Boundary)
    WITH (GRIDS = (HIGH, HIGH, HIGH, HIGH));
```

**Query pattern**: coarse integer bucket filter first (fast), then `geography::STContains()` only on the handful of candidate zipcodes:
```sql
-- Is this IP (lat 40.7128, lon -74.0060) inside a zipcode?
DECLARE @lat DECIMAL(9,6) = 40.7128, @lon DECIMAL(9,6) = -74.0060;
DECLARE @point GEOGRAPHY = GEOGRAPHY::Point(@lat, @lon, 4326);
DECLARE @latBucket INT = CAST(@lat * 100 AS INT);
DECLARE @lonBucket INT = CAST(@lon * 100 AS INT);

SELECT Zipcode, City, State
FROM Geo.Zipcode
WHERE LatBucket100 BETWEEN @latBucket - 1 AND @latBucket + 1   -- coarse filter
  AND LonBucket100 BETWEEN @lonBucket - 1 AND @lonBucket + 1   -- coarse filter
  AND Boundary.STContains(@point) = 1;                          -- precise containment
```

This combines the integer bucket trick (eliminate 99% of candidates with integer comparison) with the precision of actual polygon containment. Best of both worlds.

#### Priority Order

| Priority | What | Type | Why First |
|----------|------|------|-----------|
| 1 | Subnet reputation table + daily aggregation | T-SQL | Immediate value, powers Forge lookups |
| 2 | Vector fingerprint similarity + UA drift | SQL 2025 | Differentiator — identity resolution without cookies |
| 3 | CLR: GetSubnet24 | CLR | Clean subnet queries everywhere |
| 4 | CLR: RegexExtract | CLR | Unlocks ad-hoc URL/UA analysis without ETL |
| 5 | Session reconstruction views | T-SQL | Dashboard session metrics (complements real-time Forge sessions) |
| 6 | CLR: FeatureBitmap (+ accessibility, bot, evasion bitmaps) | CLR | Index compression, device clustering, fast bitwise queries |
| 7 | CLR: MurmurHash3 | CLR | Replace SHA2 for all non-crypto hashing |
| 8 | Graph tables for identity chains | SQL 2025 | Multi-hop identity resolution |
| 9 | Zipcode polygon table (Geo.Zipcode) | T-SQL + Spatial | Replaces legacy centroid radius matching |
| 10 | Geo calculations (integer bucket pattern) | T-SQL | Impossible travel + zipcode matching |
| 11 | Fuzzy string matching (vector similarity preferred, CLR fallback) | SQL 2025 / CLR | UA cluster detection — test vectors first |
| 12 | Dead Internet Index | T-SQL | See what we can see |
| 13 | Customer traffic quality trending | T-SQL | Customer-facing value (post-launch) |

---

## 9. Deployment Architecture

| Component | Technology | Location | Port |
|-----------|-----------|----------|------|
| **PiXL Edge** (IIS) | ASP.NET Core InProcess | `C:\inetpub\Smartpixl.info\` | 80/443 (IIS), 6000/6001 (Kestrel internal) |
| **SmartPiXL Forge** | .NET Windows Service | `C:\Services\SmartPiXL-Forge` | Named pipe (no network port) |
| **SmartPiXL Worker** | .NET Windows Service | `C:\Services\SmartPiXL-Worker` | **DEPRECATED — OFF** |
| **SmartPiXL Sentinel** | .NET Windows Service | TBD | 7500 (Phase 10) |
| **SQL Server** | MSSQL 2025 Developer | `localhost\SQL2025` | 1433 |
| **Xavier** (IPGEO source) | MSSQL 2017 on `192.168.88.35` | Remote | 1433 |

Dev ports: Edge 7000/7001, Sentinel 7500. Production ports: Edge 6000/6001 (behind IIS on 80/443).

### 9.1 Xavier Sync Lifecycle — Temporary Bridge

All three Xavier syncs are a **transitional bridge**, not permanent architecture.
They exist only because Xavier's legacy front-end is currently the client-facing
product and needs to stay in sync with SmartPiXL's data:

| Sync Service | Source DB | Direction | Purpose |
|---|---|---|---|
| `IpApiSyncService` | Xavier `IPGEO` | Xavier → SmartPiXL | IP geolocation deltas from `IP_Location_New` |
| `CompanyPiXLSyncService` (Company) | Xavier `SmartPiXL` | Xavier → SmartPiXL | Company master data |
| `CompanyPiXLSyncService` (Pixel) | Xavier `SmartPiXL` | Xavier → SmartPiXL | Pixel configuration per company |

**Lifecycle:**
1. **Current state**: Xavier is authoritative for Company/Pixel/IPGEO data. SmartPiXL syncs from Xavier periodically.
2. **Target state**: A new front-end (not yet scoped) replaces Xavier. SmartPiXL becomes the authoritative data source. Xavier syncs are decommissioned.
3. **Timeline**: The new front-end is not scoped — these syncs will run for an extended period. They're temporary but not imminent to remove.

**Connection Security:**
- Xavier (192.168.88.35, hostname D43DQBM2) has a self-signed certificate:
  - Subject/CN: `192.168.88.35`
  - SAN: `DNS:192.168.88.35, DNS:Xavier, DNS:localhost`
  - Signature: sha1RSA (legacy — should be regenerated as sha256RSA)
  - Thumbprint: `02AC76BBF2531C5B7DE4B93D2B301BEE5C2BB269`
  - Expires: 2031-02-16
  - Imported into `Cert:\LocalMachine\Root` on both `LocalMachine` and `CurrentUser` stores on the SmartPiXL server
- **Problem**: SQL Server 2017 on Xavier is NOT configured to present this cert (it still uses its auto-generated cert). SChannel rejects the auto-generated cert since it's not in our trust store.
- **Current workaround**: `Encrypt=True;TrustServerCertificate=True` — encrypted transit but no cert validation
- **Remediation**: Configure Xavier's SQL Server Configuration Manager → SQL Server Network Configuration → Protocols → Certificate to use the custom cert. Then remove `TrustServerCertificate=True` from all Xavier connection strings.

---

## 10. Design Methodology

This project is two weeks old. No live test data yet. The methodology is:

1. **Collect everything**: grab every data point the browser will give us. Refine later.
2. **Build the pipeline first**: get data flowing end-to-end before optimizing.
3. **Validate with real data**: every assumption in this document will be tested against actual traffic.
4. **Iterate fast**: the Forge architecture lets us add new enrichments without touching IIS or SQL schema.
5. **Don't over-constrain**: our ideas should not be overfit to what we already have. New data points may reveal use cases we haven't imagined yet.

---

## 11. Open Questions

- [ ] What does live data actually look like? (Pending management enabling test traffic)
- [ ] Which Forge enrichments have the highest signal-to-noise ratio? (Requires real data)
- [x] Can we acquire MaxMind GeoLite2 `.mmdb` file? (**YES** — acquired 2026-02-19 via GitHub mirror. All 3 databases downloaded + imported into SQL `Geo` schema: 1.35M city blocks, 60K locations, 508K ASN networks. See Section 6.4)
- [ ] What GPU database can we use for device age / affluence mapping? (Need a GPU model → release year lookup)
- [x] What SQL CLR functions would be valuable? (Designed — see Section 8.3: 6 CLR functions with owner notes on each)
- [x] What SQL-side historical analysis should we build? (Designed — see Section 8.3: 7 T-SQL analyses + 3 SQL 2025 features)
- [x] Zipcode polygon vs centroid radius? (Designed — see Section 8.4: ZCTA shapefiles + integer bucket matching)
- [x] Geo calculation approach? (Integer bucket pattern from LocationIQ — see Section 8.3, item #14)
- [ ] ML/LLM integration: how to route data between colo servers and the RTX 4090 workstation at the office? (6-month problem)
- [ ] Enable CLR on SQL 2025 instance and create signed assembly for CLR functions
- [ ] Download Census ZCTA shapefiles and import polygon boundaries into Geo.Zipcode

---

## 12. What's Not Changing

- PiXL.Raw schema (9 columns) — all enrichment rides in the QueryString as `_srv_*` params
- ETL pipeline (usp_ParseNewHits / usp_MatchVisits / usp_EnrichParsedGeo) — still the backbone
- IIS in-process hosting model — we learned the hard way that Kestrel standalone + reverse proxy destroys raw connection data
- Fire-and-forget GIF response — the visitor never waits for enrichment
- IPAPI Pro subscription — stays as a data source, the Forge just uses it smarter
