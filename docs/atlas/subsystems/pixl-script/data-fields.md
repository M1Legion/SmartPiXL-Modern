---
subsystem: pixl-script
title: "PiXL Script: Complete Data Field Inventory"
version: 1.0
last_updated: 2026-02-21
status: current
parent: subsystems/pixl-script
related:
  - subsystems/pixl-script/fingerprinting-techniques
  - subsystems/pixl-script/bot-detection-engine
  - database/schema-map
---

# PiXL Script: Complete Data Field Inventory

## Atlas Public

Every time a visitor loads a page on your website, SmartPiXL collects **159 data fields** about their device, browser, and behavior. These data points are combined to identify returning visitors, detect bots, and build a complete picture of your web traffic.

**Think of it like a hotel check-in form — but for devices:**

```
  ┌─────────────────────────────────────────────┐
  │          DEVICE "CHECK-IN" CARD              │
  │                                              │
  │  Screen: 1920×1080, 2 monitors       ◄─── Display
  │  GPU: NVIDIA RTX 4090                ◄─── Hardware
  │  Browser: Chrome 120 on Windows 11   ◄─── Software
  │  Location: US Eastern timezone       ◄─── Location
  │  Language: English (US)              ◄─── Preferences
  │  Device ID: a3f9c7e...              ◄─── Fingerprint
  │  Human? Yes (score: 0)              ◄─── Bot check
  │  Disguised? No                      ◄─── Evasion check
  │                                              │
  └─────────────────────────────────────────────┘
```

No single field identifies a person. The value is in the **combination** — like how a car's make, model, year, color, and mileage together narrow it to one specific vehicle.

### Data Categories at a Glance

| Category | Fields | What It Tells You |
|----------|--------|-------------------|
| Display & Screen | 14 | Monitor setup, screen size, browser window |
| Hardware | 6 | CPU, memory, GPU, battery |
| Browser Identity | 18 | Browser type, version, operating system |
| Device Fingerprints | 21 | Unique hardware "signature" (like a VIN) |
| Location & Language | 7 | Timezone, locale, formatting preferences |
| Internet Connection | 7 | Connection speed, type, latency |
| Browser Features | 23 | What the browser supports (storage, workers, etc.) |
| Preferences | 10 | Dark mode, accessibility settings, input type |
| Visitor Behavior | 10 | Mouse movement, scroll depth, timing |
| Page Info | 13 | URL, referrer, page title |
| Bot Detection | 9 | Automation signals, threat scores |
| Performance | 5 | Page load speed, DNS time |

---

## Atlas Internal

### The Complete 159-Field Inventory

Every field below is collected by the PiXL Script in the visitor's browser and sent to our server as URL parameters on the `_SMART.GIF` request.

#### Screen & Display (14 fields)

| Field | What It Contains | Example | Why We Collect It |
|-------|-----------------|---------|-------------------|
| `sw` | Screen width (pixels) | `1920` | Screen size fingerprinting |
| `sh` | Screen height (pixels) | `1080` | Screen size fingerprinting |
| `saw` | Available screen width | `1920` | Taskbar deduction reveals OS |
| `sah` | Available screen height | `1040` | Taskbar deduction reveals OS |
| `cd` | Color depth (bits) | `24` | Hardware capability |
| `pd` | Device pixel ratio | `2` | Retina/HiDPI detection |
| `ori` | Screen orientation | `landscape-primary` | Mobile vs desktop behavior |
| `vw` | Viewport width | `1903` | Browser chrome deduction |
| `vh` | Viewport height | `969` | Browser chrome deduction |
| `ow` | Outer window width | `1920` | Full window including chrome |
| `oh` | Outer window height | `1040` | Full window including chrome |
| `sx` | Window X position | `0` | Multi-monitor layout |
| `sy` | Window Y position | `0` | Multi-monitor layout |
| `screenExtended` | Multi-monitor detected | `1` | Multi-monitor setup = desktop user |

#### Navigator Properties (18 fields)

| Field | What It Contains | Example |
|-------|-----------------|---------|
| `plt` | Platform identifier | `Win32` |
| `vnd` | Browser vendor | `Google Inc.` |
| `ua` | Full User Agent string | `Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120...` |
| `cores` | CPU core count | `16` |
| `mem` | Device memory (GB) | `8` |
| `touch` | Max touch points | `0` (desktop) or `5` (mobile) |
| `product` | Product name | `Gecko` |
| `productSub` | Product sub-version | `20030107` |
| `vendorSub` | Vendor sub-version | `` (empty) |
| `oscpu` | OS/CPU (Firefox only) | `Windows NT 10.0; Win64; x64` |
| `buildID` | Build ID (Firefox only) | `20181001000000` |
| `chromeObj` | `window.chrome` exists? | `1` |
| `chromeRuntime` | `window.chrome.runtime` exists? | `1` |
| `appName` | Application name | `Netscape` |
| `appVersion` | Application version | `5.0 (Windows NT 10.0...)` |
| `appCodeName` | Code name | `Mozilla` |
| `jsHeapLimit` | JS heap size limit (Chrome) | `4294705152` |
| `jsHeapTotal` | Total JS heap (Chrome) | `12345678` |
| `jsHeapUsed` | Used JS heap (Chrome) | `8765432` |

#### Fingerprints (21 fields)

| Field | Fingerprint Type | What It Measures |
|-------|-----------------|------------------|
| `canvasFP` | Canvas hash | How the browser renders text + shapes |
| `canvasEvasion` | Canvas evasion flag | Privacy tool blocking canvas reads |
| `canvasConsistency` | Canvas repeatability | `clean`, `noise-detected`, or `canvas-blocked` |
| `webglFP` | WebGL hash | GPU parameters + extensions combined |
| `gpu` | GPU renderer | e.g., `NVIDIA GeForce RTX 4090` |
| `gpuVendor` | GPU vendor | e.g., `Google Inc. (NVIDIA)` |
| `webglParams` | WebGL parameter summary | First 5 GL parameters |
| `webglExt` | WebGL extension count | e.g., `38` |
| `webglEvasion` | WebGL evasion flag | Software renderer detected |
| `audioFP` | Audio fingerprint value | Numeric sum of audio samples |
| `audioHash` | Audio fingerprint hash | Hash of sampled audio data |
| `audioStable` | Audio repeatability | `1` = consistent, `0` = noise injected |
| `audioNoiseDetected` | Audio noise flag | Privacy tool injecting noise |
| `fonts` | Detected fonts | `Arial,Verdana,Segoe UI,Calibri,...` |
| `fontMethodMismatch` | Font method mismatch | Spoofing detected between two measurement methods |
| `voices` | Speech synthesis voices | `Microsoft David/en-US|Microsoft Zira/en-US|...` |
| `mathFP` | Math precision fingerprint | JS engine math function outputs |
| `cssFontVariant` | CSS font rendering | Font variant CSS property values + width |
| `errorFP` | Error message fingerprint | Length of error message + stack trace |
| `docCharset` | Document character set | `UTF-8` |
| `docCompat` | Document compat mode | `CSS1Compat` |

#### Client Hints — UA-CH (10 fields)

| Field | What It Contains | Example |
|-------|-----------------|---------|
| `uaArch` | CPU architecture | `x86` |
| `uaBitness` | Architecture bitness | `64` |
| `uaModel` | Device model | `` (empty on desktop) |
| `uaPlatformVersion` | OS version | `15.0.0` |
| `uaWow64` | WoW64 emulation | `0` |
| `uaFormFactor` | Device form factor | `desktop` |
| `uaFullVersion` | Full browser versions | `Chromium/120.0.6099.130|Chrome/120.0.6099.130` |
| `uaMobile` | Mobile flag | `0` |
| `uaPlatform` | Platform name | `Windows` |
| `uaBrands` | Browser brand list | `Chromium/120|Chrome/120|Not_A Brand/24` |

#### Network & Connection (7 fields)

| Field | What It Contains | Example |
|-------|-----------------|---------|
| `conn` | Effective connection type | `4g` |
| `dl` | Downlink speed (Mbps) | `10` |
| `dlMax` | Max downlink | `` |
| `rtt` | Round-trip time (ms) | `50` |
| `save` | Data saver enabled | `0` |
| `connType` | Connection type | `wifi` |
| `localIp` | Local/private IP (WebRTC) | `192.168.1.100` |

#### Storage & Hardware APIs (6 fields)

| Field | What It Contains | Example |
|-------|-----------------|---------|
| `storageQuota` | Storage quota (GB) | `284` |
| `storageUsed` | Storage used (MB) | `12` |
| `batteryLevel` | Battery level (%) | `85` |
| `batteryCharging` | Charging state | `1` |
| `audioInputs` | Microphone count | `1` |
| `videoInputs` | Camera count | `1` |

#### Timezone & Locale (7 fields)

| Field | What It Contains | Example |
|-------|-----------------|---------|
| `tz` | IANA timezone | `America/New_York` |
| `tzo` | UTC offset (minutes) | `-300` |
| `ts` | Epoch timestamp (ms) | `1708531200000` |
| `lang` | Primary language | `en-US` |
| `langs` | All languages | `en-US,en,fr` |
| `tzLocale` | Locale details | `en-US|gregory|latn|h12` |
| `dateFormat` | Date rendering | `1/15/2024` |
| `numberFormat` | Number rendering | `1,234,567.89` |
| `relativeTime` | Relative time rendering | `1 day ago` |

#### Boolean Feature Flags (9 fields)

| Field | What It Tests | Values |
|-------|--------------|--------|
| `ck` | Cookies enabled | `1`/`0` |
| `dnt` | Do Not Track set | `1`/`0`/empty |
| `pdf` | PDF viewer enabled | `1`/`0` |
| `webdr` | WebDriver flag (automation!) | `1`/`0` |
| `online` | Browser online | `1`/`0` |
| `java` | Java enabled | `1`/`0` |
| `plugins` | Plugin count | `5` |
| `mimeTypes` | MIME type count | `4` |
| `gamepads` | Connected gamepads | `Xbox Controller|...` |

#### API Support Detection (17 fields)

| Field | API Tested | Values |
|-------|-----------|--------|
| `ls` | localStorage | `1`/`0` |
| `ss` | sessionStorage | `1`/`0` |
| `idb` | IndexedDB | `1`/`0` |
| `caches` | Cache API | `1`/`0` |
| `ww` | Web Workers | `1`/`0` |
| `swk` | Service Workers | `1`/`0` |
| `wasm` | WebAssembly | `1`/`0` |
| `webgl` | WebGL 1.0 | `1`/`0` |
| `webgl2` | WebGL 2.0 | `1`/`0` |
| `canvas` | Canvas API | `1`/`0` |
| `touchEvent` | Touch events | `1`/`0` |
| `pointerEvent` | Pointer events | `1`/`0` |
| `mediaDevices` | Media devices API | `1`/`0` |
| `clipboard` | Clipboard API | `1`/`0` |
| `speechSynth` | Speech synthesis | `1`/`0` |
| `pluginList` | Plugin details | `Chrome PDF Plugin::mhjfbn...` |
| `mimeList` | MIME type list | `application/pdf,...` |

#### CSS & Accessibility Preferences (10 fields)

| Field | Media Query | What It Reveals |
|-------|------------|-----------------|
| `darkMode` | `prefers-color-scheme: dark` | OS dark mode enabled |
| `lightMode` | `prefers-color-scheme: light` | OS light mode enabled |
| `reducedMotion` | `prefers-reduced-motion: reduce` | Accessibility: motion sensitivity |
| `reducedData` | `prefers-reduced-data: reduce` | Data saver preference |
| `contrast` | `prefers-contrast: high` | Accessibility: high contrast |
| `forcedColors` | `forced-colors: active` | Windows High Contrast mode |
| `invertedColors` | `inverted-colors: inverted` | Color inversion active |
| `hover` | `hover: hover` | Mouse/trackpad available |
| `pointer` | `pointer: fine/coarse` | Input precision (mouse vs touch) |
| `standalone` | `display-mode: standalone` | Running as installed PWA |

#### Page Context (8 fields)

| Field | What It Contains | Example |
|-------|-----------------|---------|
| `url` | Full page URL | `https://example.com/products/widget` |
| `ref` | Referrer URL | `https://google.com/search?q=...` |
| `hist` | History length | `5` |
| `title` | Page title | `Widget Pro - Example Corp` |
| `domain` | Hostname | `example.com` |
| `path` | URL path | `/products/widget` |
| `hash` | URL hash fragment | `#pricing` |
| `protocol` | Protocol | `https:` |

#### Document State (5 fields)

| Field | What It Contains | Example |
|-------|-----------------|---------|
| `docCharset` | Character encoding | `UTF-8` |
| `docCompat` | Compatibility mode | `CSS1Compat` |
| `docReady` | Ready state | `complete` |
| `docHidden` | Tab hidden? | `0` |
| `docVisibility` | Visibility state | `visible` |

#### Performance Timing (5 fields)

| Field | What It Measures | Example |
|-------|-----------------|---------|
| `loadTime` | Full page load (ms) | `1234` |
| `domTime` | DOM content loaded (ms) | `890` |
| `dnsTime` | DNS lookup (ms) | `12` |
| `tcpTime` | TCP connection (ms) | `23` |
| `ttfb` | Time to first byte (ms) | `156` |

#### Bot Detection (3 composite fields)

| Field | What It Contains | Example |
|-------|-----------------|---------|
| `botSignals` | Comma-separated signal names | `webdriver,selenium,empty-languages` |
| `botScore` | Numeric threat score | `25` |
| `botPermInconsistent` | Permission state mismatch | `1` |

See [Bot Detection Engine](bot-detection-engine.md) for the full signal inventory.

#### Evasion Detection (3 composite fields)

| Field | What It Contains | Example |
|-------|-----------------|---------|
| `evasionDetected` | Privacy/spoofing signals | `brave,ua-platform-mismatch` |
| `stealthSignals` | Anti-detection plugin signals | `webdriver-slow,toString-spoofed` |
| `evasionSignalsV2` | Aggregated evasion flags | `canvas-noise,minimal-fonts` |

See [Evasion Detection](evasion-detection.md) for the full signal inventory.

#### Cross-Signal Analysis (3 fields)

| Field | What It Contains | Example |
|-------|-----------------|---------|
| `crossSignals` | Anomaly flag names | `win-fonts-on-mac,safari-google-vendor` |
| `anomalyScore` | Cross-signal anomaly score | `35` |
| `combinedThreatScore` | botScore + min(anomalyScore, 25) | `60` |

See [Cross-Signal Analysis](cross-signal-analysis.md) for the full signal inventory.

#### Behavioral Analysis (10 fields)

| Field | What It Contains | Example |
|-------|-----------------|---------|
| `mouseMoves` | Mouse event count | `23` |
| `mouseEntropy` | Movement angle variance (×1000) | `487` |
| `moveTimingCV` | Timing uniformity (×1000) | `892` |
| `moveSpeedCV` | Speed uniformity (×1000) | `1245` |
| `mousePath` | Raw coordinates `x,y,t|x,y,t|...` | `512,340,0|520,345,16|...` |
| `moveCountBucket` | Movement volume bucket | `mid` |
| `scrolled` | Scroll detected | `1` |
| `scrollY` | Scroll depth (pixels) | `840` |
| `scrollContradiction` | Scroll event but depth = 0 | `0` |
| `behavioralFlags` | Behavioral anomaly flags | `uniform-timing` |

See [Behavioral Analysis](behavioral-analysis.md) for entropy math and bot detection.

#### Metadata (2 fields)

| Field | What It Contains | Example |
|-------|-----------------|---------|
| `scriptExecTime` | Time spent in bot checks (ms) | `3` |
| `_proxyBlocked` | Properties that threw on access | `webdriver,platform,` |

---

## Atlas Technical

### Query String Construction

All 159 fields are serialized as URL query parameters on the `_SMART.GIF` request:

```
_SMART.GIF?sw=1920&sh=1080&cd=24&pd=2&canvasFP=a3f9c7e&gpu=NVIDIA+GeForce+RTX+4090&...
```

Each field value is `encodeURIComponent()`-encoded. Empty/null/undefined fields are **excluded** (not sent as empty params). This keeps the query string tight — a typical hit is 3-5KB of query parameters.

### Field Naming Conventions

- Short names (2-4 chars) for high-volume fields: `sw`, `sh`, `cd`, `pd`, `vw`, `vh`
- Camel case for composite names: `canvasFP`, `webglFP`, `audioFP`, `botScore`
- Prefixed for UA Client Hints: `ua` prefix — `uaArch`, `uaBitness`, `uaModel`
- Score fields: `botScore`, `anomalyScore`, `combinedThreatScore`
- Binary flags: `1`/`0` — never `true`/`false` (smaller query string)

### ETL Column Mapping

These 159 browser-side fields map to columns in `PiXL.Parsed` during ETL. The mapping is in `ETL.usp_ParseNewHits`, which extracts each field from the raw query string stored in `PiXL.Raw.QueryParams`.

Not all 159 fields become individual columns — composite fields like `botSignals` and `crossSignals` are stored as-is (comma-separated strings) and also decomposed into individual boolean columns for filtering.

---

## Atlas Private

### Field Count Verification

The 159 count is based on a meticulous audit of every `data.{fieldName} = ...` assignment in PiXLScript.cs. The breakdown:

| Category | Count | Verification |
|----------|-------|-------------|
| Screen & Display | 14 | sw, sh, saw, sah, cd, pd, ori, vw, vh, ow, oh, sx, sy, screenExtended |
| Navigator Properties | 18 | plt, vnd, ua, cores, mem, touch, product, productSub, vendorSub, oscpu, buildID, chromeObj, chromeRuntime, appName, appVersion, appCodeName, jsHeapLimit + jsHeapTotal + jsHeapUsed (3 fields under perf.memory check) |
| Fingerprints | 21 | canvasFP, canvasEvasion, canvasConsistency, webglFP, gpu, gpuVendor, webglParams, webglExt, webglEvasion, audioFP, audioHash, audioStable, audioNoiseDetected, fonts, fontMethodMismatch, voices, mathFP, cssFontVariant, errorFP, docCharset, docCompat |
| Client Hints | 10 | uaArch, uaBitness, uaModel, uaPlatformVersion, uaWow64, uaFormFactor, uaFullVersion, uaMobile, uaPlatform, uaBrands |
| Network | 7 | conn, dl, dlMax, rtt, save, connType, localIp |
| Storage/Hardware | 6 | storageQuota, storageUsed, batteryLevel, batteryCharging, audioInputs, videoInputs |
| Timezone/Locale | 7+2 = 9 | tz, tzo, ts, lang, langs, tzLocale, dateFormat, numberFormat, relativeTime |
| Boolean flags | 9 | ck, dnt, pdf, webdr, online, java, plugins, mimeTypes, gamepads |
| API support | 17 | ls, ss, idb, caches, ww, swk, wasm, webgl, webgl2, canvas, touchEvent, pointerEvent, mediaDevices, clipboard, speechSynth, pluginList, mimeList |
| CSS prefs | 10 | darkMode, lightMode, reducedMotion, reducedData, contrast, forcedColors, invertedColors, hover, pointer, standalone |
| Page context | 8 | url, ref, hist, title, domain, path, hash, protocol |
| Document state | 5 | docCharset, docCompat, docReady, docHidden, docVisibility |
| Performance | 5 | loadTime, domTime, dnsTime, tcpTime, ttfb |
| Bot detection | 3 | botSignals, botScore, botPermInconsistent |
| Evasion | 3 | evasionDetected, stealthSignals, evasionSignalsV2 |
| Cross-signal | 3 | crossSignals, anomalyScore, combinedThreatScore |
| Behavioral | 10 | mouseMoves, mouseEntropy, moveTimingCV, moveSpeedCV, mousePath, moveCountBucket, scrolled, scrollY, scrollContradiction, behavioralFlags |
| Metadata | 2 | scriptExecTime, _proxyBlocked |

**Note:** `docCharset` and `docCompat` appear under both fingerprints and document state in conceptual groupings. They're counted once. The exact count may vary by 1-2 depending on how you classify edge cases like `gamepads` (boolean-ish but actually a string).

### Query String Size Constraints

The `_SMART.GIF` request uses GET parameters. IIS and `web.config` set:
- `maxQueryString=16384` (16KB)
- `maxUrl=8192` (8KB for URL path)

A typical hit with all fields populated is 3-5KB. The main space consumers:
- `mousePath`: up to 2000 chars
- `ua`: 150-300 chars
- `fonts`: 200-400 chars
- `pluginList`: 200-500 chars
- `voices`: 200-600 chars

The 16KB limit provides comfortable headroom. If a future field addition pushes close to the limit, the `mousePath` cap (currently 2000 chars) is the first knob to turn.
