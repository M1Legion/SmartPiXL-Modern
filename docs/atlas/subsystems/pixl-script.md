---
subsystem: pixl-script
title: PiXL Script
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - architecture/data-flow
  - subsystems/fingerprinting
  - subsystems/bot-detection
---

# PiXL Script

## Atlas Public

The PiXL Script is SmartPiXL's browser-side intelligence collector. When a visitor loads any page on your website, this lightweight JavaScript runs silently in the background, gathering a comprehensive profile of the visitor's device, browser, and behavior — all without cookies, popups, or any visible impact.

**What makes it exceptional:**

- **159 data fields** captured per visit — the most comprehensive visitor profile in the industry
- **230+ data points** extracted from those fields (composite fields contain multiple signals)
- **Under 500ms** — faster than your page finishes loading
- **Completely invisible** — no popups, no consent dialogs, no performance impact
- **Works without cookies** — identifies devices using hardware and behavior characteristics
- **Resilient** — if any browser API is blocked (privacy tools, ad blockers), the script gracefully degrades and flags the evasion attempt

**Data categories collected:**

| Category | What It Captures |
|----------|-----------------|
| Display | Screen size, color depth, multi-monitor, viewport, pixel ratio |
| Hardware | CPU cores, device memory, GPU model, battery level |
| Browser | Full feature detection (17 APIs), plugins, MIME types |
| Identity | Canvas fingerprint, WebGL fingerprint, audio fingerprint |
| Location | Timezone, locale, date/number formatting, language preferences |
| Behavior | Mouse movement patterns, scroll depth, page navigation |
| Evasion | Privacy tool detection, browser spoofing, automation signals |

## Atlas Internal

### How the Script Works

1. A customer's webpage includes a reference to `_SMART.js`
2. Our server dynamically generates the JavaScript (it's a C# template, not a static file)
3. The script runs in the visitor's browser for up to 500ms
4. It collects data from browser APIs — screen properties, hardware specs, fingerprints, behavioral observations
5. It fires a 1x1 pixel image request (`_SMART.GIF`) with all the data encoded in the URL
6. The visitor never sees or feels any of this

### The 500ms Window

The script doesn't wait 500ms — that's a ceiling, not a delay. Most data is collected instantly (under 10ms). The 500ms window exists because a few operations are asynchronous:
- Audio fingerprinting (needs to render an audio buffer)
- Battery level check
- Camera/mic device count (no popup — just counts, never accesses)
- High-entropy client hints (detailed platform info)
- Storage quota estimates

If all async operations finish early (they usually do, in ~80ms), data fires immediately. The 500ms is a safety timeout — if any async probe hangs, the data still sends.

During this window, the script also captures behavioral signals:
- Mouse movement tracking (up to 50 coordinate + timestamp events)
- Scroll depth recording
- All used for distinguishing human visitors from bots

### What We DON'T Collect

- No camera or microphone access (we count devices, we never activate them)
- No screen recording or screenshotting
- No form data or text input
- No cookies (our identification is cookie-independent)
- Nothing that requires a permission dialog
- Nothing that would trigger a browser notification

### Field Count: 159

| Category | Field Count | Examples |
|----------|-------------|---------|
| Screen & Display | 14 | Screen size, multi-monitor, pixel ratio |
| Navigator Properties | 18 | User agent, CPU cores, memory, touch points |
| Canvas Fingerprint | 3 | Canvas hash, evasion detection, noise detection |
| WebGL Fingerprint | 6 | GPU model, vendor, parameters, extensions |
| Audio Fingerprint | 4 | Audio hash, consistency, noise detection |
| Font Detection | 2 | 30 fonts tested, method spoofing detection |
| Client Hints | 10 | Platform, architecture, bitness, model |
| Network/WebRTC | 7 | Connection type, speed, local IP |
| Storage | 6 | Local/Session/IndexedDB availability, disk quota |
| Battery | 2 | Level, charging state |
| Media Devices | 2 | Camera count, mic count |
| Feature Detection | 17 | Cookie, WebGL, WASM, WebWorker, etc. |
| CSS/Accessibility | 10 | Dark mode, reduced motion, forced colors |
| Timezone & Locale | 7 | Timezone, locale, date/number format |
| Performance | 5 | Page load, DNS, TCP, TTFB timing |
| JS Heap | 3 | Heap limit, total, used (Chromium) |
| Page Context | 8 | URL, referrer, title, domain |
| Document State | 5 | Charset, compat mode, visibility |
| Exotic Fingerprints | 3 | Math precision, CSS font variant, error message |
| Plugins & MIME | 4 | Plugin list, MIME types |
| Bot Detection | 3 | 30+ signals packed into botSignals field |
| Evasion Detection | 3 | 22+ signals across three fields |
| Cross-Signal | 3 | 20+ contradiction signals |
| Behavioral | 10 | Mouse entropy, timing, scroll depth, mouse path |
| Metadata | 1 | Script execution time |
| Debug | 1 | Proxy-blocked properties |

## Atlas Technical

### Template Architecture

The PiXL Script is not a static `.js` file — it's generated by `PiXLScript.cs` (~1,155 lines of C# producing JavaScript). This approach enables:
- Server-side injection of company-specific configuration
- Template-time optimization (dead code removal for features not needed)
- Versioning via C# file, not CDN cache invalidation

The script is served on `GET /{company}/{pixl}_SMART.js` via `TrackingEndpoints.ServeScript()`. Response headers: `Content-Type: application/javascript`, `Cache-Control: public, max-age=3600`.

### Data Fire Mechanism

```javascript
// After all data is collected:
var qs = Object.keys(data).map(k => k + '=' + encodeURIComponent(data[k])).join('&');
new Image().src = '_SMART.GIF?' + qs;
```

The data is sent as URL query parameters on an image request. This approach:
- Works cross-origin without CORS configuration
- Bypasses most ad blockers (they target `XMLHttpRequest`, not image loads)
- Is truly fire-and-forget — the image request doesn't block the page

### Fingerprinting Techniques

**Canvas fingerprinting**: Draw text + shapes + arc on a `<canvas>` element → `toDataURL()` → hash. Different GPU/OS/font rendering produces different outputs. Two-canvas repeatability test detects noise injection (Brave, Firefox privacy mode).

**WebGL fingerprinting**: Query 23 WebGL parameters + extension list + `UNMASKED_RENDERER_WEBGL` (GPU model). Hash the combined output.

**Audio fingerprinting**: `OfflineAudioContext` → oscillator + compressor → sum samples [4500..5000]. Run twice via `Promise.all` to check consistency. Different audio stacks produce different outputs.

### Bot Detection (80+ Signals)

Three composite fields pack 80+ named signals:

**`botSignals`** (~30 signals): `webdriver` (+10), `selenium` (+10), `cdp` (+10), `headless-no-chrome-obj` (+8), `fake-ua` (+20), `phantomjs` (+10), `empty-languages` (+5), `plugin-mime-mismatch` (+3), `zero-screen` (+8), `eval-tampered` (+5), `cross-realm-toString` (+12), `getter-name-mismatch` (+6 each), etc.

**`evasionDetected`** + **`stealthSignals`** + **`evasionSignalsV2`** (~22 signals): `tor-screen`, `tor-likely`, `brave`, `ua-platform-mismatch`, `mobile-ua-desktop-screen`, `webdriver-slow`, `toString-spoofed`, `canvas-noise`, `audio-noise`, etc.

**`crossSignals`** (~20 signals): `win-fonts-on-mac` (+15), `safari-google-vendor` (+20), `swiftshader-gpu` (+5), `gpu-platform-mismatch` (+15), `scroll-no-depth` (+8), `uniform-timing` (+5), etc.

### Mouse & Behavioral Analysis

During the 500ms window, mouse events are captured (up to 50 `{x, y, timestamp}` events):

| Metric | Bot Signal |
|--------|-----------|
| `mouseMoves` = 0 | Headless/automated |
| `mouseEntropy` low | Straight-line movement (bot) |
| `moveTimingCV` < 0.3 | Metronomic timing (bot) |
| `moveSpeedCV` < 0.2 | Constant speed (bot) |
| `mousePath` | Raw trajectory for replay detection in the Forge |

### Anti-Evasion: Repeatability Testing

The script doesn't snapshot everything twice. It runs specific operations twice and checks consistency:

- **Canvas**: Draw identical content on two separate canvas elements, hash both. Same input → different output = noise injection
- **Audio**: Run `OfflineAudioContext` fingerprint twice in parallel via `Promise.all`. Different results = noise injection

When evasion is detected, the noisy field is excluded from the composite fingerprint (preserving accuracy) and the record is flagged. This feeds into bot scoring.

## Atlas Private

### Implementation Details in PiXLScript.cs

The script is a single string template in `PiXLScript.cs`. Key implementation notes:

**`safeGet()` wrapper**: Every browser API access is wrapped in a try-catch that returns `''` on failure. This prevents any single API from crashing the entire script. If a Proxy is being used to trap property access (anti-fingerprinting tools), the caught error's property name is appended to `_proxyBlocked`.

**Font detection**: Tests 30 fonts against a monospace baseline by measuring `offsetWidth`. Also cross-checks `offsetWidth` against `getBoundingClientRect().width` — a mismatch indicates the browser is spoofing font metrics.

**WebGL parameter query**: Reads 23 parameters via `gl.getParameter()` including `MAX_VERTEX_ATTRIBS`, `MAX_TEXTURE_SIZE`, `MAX_RENDERBUFFER_SIZE`, etc. Extension count is captured separately. The `WEBGL_debug_renderer_info` extension provides the unmasked GPU renderer and vendor — this is the most valuable single data point for device identification.

**Cross-realm toString check**: Creates a hidden `<iframe>`, accesses its `Function.prototype.toString`, and compares it to the main frame's `Function.prototype.toString`. If they differ, a Proxy or prototype manipulation is in play. This catches sophisticated stealth plugins that modify native function behavior.

**Getter property validation**: For 6 `Navigator` properties (`userAgent`, `platform`, `language`, `hardwareConcurrency`, `deviceMemory`, `maxTouchPoints`), the script checks:
1. Does `Object.getOwnPropertyDescriptor(Navigator.prototype, prop).get` have the expected name? (Mismatch = overridden getter)
2. Does the getter have a `prototype` property? (Native getters don't have prototypes; spoofed functions often do)

Each property that fails these checks contributes +6 or +8 to `botScore`.

**Heap spoofing detection**: Chrome's `performance.memory` values (jsHeapSizeLimit, totalJSHeapSize, usedJSHeapSize) should be irregular numbers. If `heapLimit % 1000000 === 0` (round million), it's likely spoofed. If `totalJSHeapSize === usedJSHeapSize`, something is suppressing real values.

**Mouse path encoding**: After the 500ms window, the `moves[]` array (up to 50 `{x, y, timestamp}` events) is serialized as `x,y,t|x,y,t|...` with a hard cap at 2000 characters. The first event's timestamp becomes the base (t=0), subsequent timestamps are relative. This encoding is consumed by the Forge's `BehavioralReplayService` for replay detection.

**`screenExtended`**: `screen.isExtended` returns `true` for multi-monitor setups. Outputs `1` or `0`. On single-monitor setups and older browsers, the ternary resolves to `0` (undefined is falsy).

### Script Size and Delivery

The generated JavaScript is approximately 35-40KB uncompressed. With gzip (default for IIS), it's ~8-10KB over the wire. The script is served with `max-age=3600` (1 hour cache), so repeat visits within an hour don't re-download.

The script has ZERO external dependencies — no jQuery, no analytics libraries, no CDN requests. It's entirely self-contained. This was a deliberate choice: external dependencies can be blocked by ad blockers, and CDN failures would break data collection.

### Known Limitations

- **Firefox**: `navigator.deviceMemory` returns `undefined`. `performance.memory` is unavailable. Client Hints (`navigator.userAgentData`) are unavailable. These fields are simply empty for Firefox visitors — they don't break the fingerprint, they reduce its dimensionality.
- **Safari**: Battery API, Client Hints, and `connection` API are all unavailable. Safari is the most privacy-restrictive mainstream browser — our fingerprint for Safari visitors relies more heavily on canvas, WebGL, fonts, and screen properties.
- **Brave**: Randomizes canvas and WebGL outputs. The repeatability test catches this and flags it as evasion. The noisy fields are excluded from the fingerprint hash.
- **Tor Browser**: Letterboxes the viewport to fixed sizes. The script detects this via viewport math and flags it. Tor users have very low fingerprint uniqueness (by design).
