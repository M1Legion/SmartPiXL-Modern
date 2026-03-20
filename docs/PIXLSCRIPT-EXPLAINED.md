# PiXL Script — How It Works

**Audience:** Management, non-technical stakeholders
**Last Updated:** 2026-03-17
**File:** `SmartPiXL/Scripts/PiXLScript.cs`

---

## What Is The PiXL Script?

The PiXL Script is a small JavaScript program that runs inside a website visitor's browser. When a customer installs SmartPiXL on their website, they add a single line of code that loads our script. The script silently collects ~120 data points about the visitor's device, browser, and behavior — then sends that data back to our server for analysis.

**The visitor never sees or interacts with the script.** It runs invisibly, typically in under 500 milliseconds.

---

## How Does Data Get From The Browser To Our Server?

This is the core concept:

1. A visitor loads a customer's website (e.g., a car dealer)
2. The page includes a `<script>` tag pointing to our server
3. The visitor's browser downloads and runs our JavaScript
4. The script collects device/browser data points
5. The script sends that data back to our server via an HTTP request
6. Our server (Edge) receives it and forwards it to the Forge for processing

The data exists only in the visitor's browser memory until step 5. Without the HTTP transmission, we get nothing. We use `navigator.sendBeacon()` (a modern browser API designed for analytics) as the primary delivery mechanism, with a traditional tracking pixel (invisible 1×1 image) as a fallback for older browsers.

---

## What Data Does It Collect?

### 1. Canvas Fingerprint
The script creates an invisible drawing surface and draws specific shapes and text on it. Every device renders text and graphics slightly differently due to GPU, driver, and OS differences. The script reads the pixel data and creates a unique identifier from it.

**Anti-evasion:** Draws the same thing twice and compares results. If they differ, someone is injecting random noise to defeat fingerprinting — which is itself a red flag.

**Fields:** `canvasFP`, `canvasEvasion`, `canvasConsistency`

### 2. WebGL Fingerprint
Reads 23 parameters from the device's 3D graphics system (GPU model, maximum texture sizes, supported features). Additionally renders a 3D scene and reads back the pixel data — different GPUs produce different results.

Detects software renderers (SwiftShader, llvmpipe) which indicate headless browsers or virtual machines — common in bot farms.

**Fields:** `webglFP`, `gpu`, `gpuVendor`, `webglParams`, `webglExt`, `webglRenderFP`

### 3. Audio Fingerprint
Creates an offline audio processing pipeline (inaudible to the user) and analyzes the output. Different audio stacks produce different floating-point results, creating a device-specific signature.

**Anti-evasion:** Runs the test twice. If results differ, audio noise injection is detected.

**Fields:** `audioFP`, `audioHash`, `audioStable`, `audioNoiseDetected`

### 4. Font Detection
Tests for the presence of 30 common fonts by measuring text width. Different operating systems ship different fonts — Windows has Segoe UI, Mac has Monaco, etc.

**Anti-evasion:** Uses two independent measurement methods. If they disagree, font metrics are being spoofed.

**Fields:** `fonts`, `fontMethodMismatch`

### 5. Screen & Display
Resolution, available area, color depth, pixel density, viewport size, multi-monitor detection, color gamut, HDR support.

**Fields:** `sw`, `sh`, `cd`, `pd`, `vw`, `vh`, `colorGamut`, `hdr`, etc.

### 6. Locale & Time
Timezone, language, date/number formatting preferences. These reflect real system configuration and are difficult to spoof consistently.

**Why it matters:** A device claiming to be in New York but with a Tokyo timezone is suspicious.

**Fields:** `tz`, `tzo`, `lang`, `langs`, `dateFormat`, `numberFormat`

### 7. Navigator Properties
CPU cores, device memory, touch capability, platform identifier, User-Agent string. Modern browsers also expose "Client Hints" — a more reliable, structured alternative to User-Agent strings that provides architecture, OS version, and device model.

**Fields:** `cores`, `mem`, `touch`, `plt`, `ua`, `uaArch`, `uaBitness`, `uaPlatformVersion`, etc.

### 8. Bot Detection (~30 checks)
Detects automated browsers, headless environments, and browser automation tools:

| Signal | What It Catches |
|--------|----------------|
| `navigator.webdriver` | Selenium, Puppeteer, Playwright |
| Missing Chrome object | Headless Chrome |
| Short/fake User-Agent | Simple bots |
| PhantomJS/Nightmare/Selenium markers | Legacy automation tools |
| Chrome DevTools Protocol markers | CDP-based automation |
| Permission state contradictions | Incomplete browser spoofing |
| Zero screen dimensions | No-display environments |
| Navigator property tampering | Proxy-based spoofing tools |
| Getter name/prototype analysis | Advanced property override detection |
| Memory heap analysis | Spoofed runtime environments |
| Cross-realm integrity checks | Sophisticated evasion tools |

Produces a `botScore` (0 = clean, higher = more suspicious) and a `botSignals` list of specific triggers.

### 9. Evasion Detection
Identifies specific privacy and anti-fingerprint tools:
- **Tor Browser:** Screen size 1000×1000, letterbox viewports, specific font signatures
- **Brave Browser:** Has `navigator.brave.isBrave` API
- **Anti-detect browsers:** Platform/UA mismatches, missing APIs, font/GPU contradictions
- **Client Hints conflicts:** Modern Chromium API disagrees with legacy navigator properties

**Fields:** `evasionDetected`, `evasionSignalsV2`

### 10. Cross-Signal Correlation
Compares independent data sources to find contradictions that indicate spoofing:
- Windows fonts on a Mac → anti-detect browser with wrong font configuration
- Safari User-Agent but Chrome-specific properties → fake Safari
- Software GPU renderer on Mac → virtual machine or anti-detect environment
- GPU brand doesn't match platform → spoofed hardware identity

**Fields:** `crossSignals`, `anomalyScore`, `combinedThreatScore`

### 11. Math & CSS Fingerprints
- **Math fingerprint:** Different JavaScript engines return slightly different floating-point results for trig functions. Stable, hard to spoof.
- **CSS font variant fingerprint:** Measures how the browser renders font properties — varies by engine and OS.
- **Error fingerprint:** Different browsers produce different error messages. The message format identifies the engine.

**Fields:** `mathFP`, `cssFontVariant`, `errorFP`

### 12. Stealth Detection (Advanced)
Targets sophisticated evasion tools that override browser APIs:
- **Timing attacks:** Spoofed properties are slower to access than real ones
- **Prototype chain integrity:** Real browsers have a specific internal structure
- **Function toString integrity:** Overridden functions can't perfectly fake the native format

**Fields:** `stealthSignals`

### 13. Behavioral Analysis
Records mouse movement and scroll behavior during the data collection window:
- **Mouse entropy:** Real humans move in varied directions and speeds; bots move in straight lines with uniform timing
- **Scroll contradiction:** Reports scrolling but scroll position is 0 (impossible for real user)

**Fields:** `mouseMoves`, `mouseEntropy`, `behavioralFlags`, `scrolled`, `scrollY`

### 14. Page Context
Current URL, referrer, page title, navigation history. This data is reported directly to customers so they can see which pages visitors viewed and where they came from.

**Fields:** `url`, `ref`, `title`, `path`, `domain`

### 15. Browser Capabilities
Feature detection for Web Workers, Service Workers, WebAssembly, WebGL, IndexedDB, etc. The pattern of supported/unsupported features creates a browser profile.

**Fields:** `ww`, `swk`, `wasm`, `webgl`, `webgl2`, `canvas`, etc.

### 16. Media Preferences
Dark mode, reduced motion, high contrast, pointer type (mouse vs touch). These reflect user-configured accessibility settings — stable and hard to fake.

**Fields:** `darkMode`, `reducedMotion`, `contrast`, `pointer`, `hover`

### 17. Permissions State
Queries the browser's permission state for camera, microphone, geolocation, notifications, and push. The pattern of allowed/denied/prompt permissions is unique per user and extremely stable — users rarely change these settings.

**Fields:** `permCamera`, `permMicrophone`, `permGeolocation`, `permNotifications`, `permPush`

### 18. Speech Synthesis Voices
Lists installed text-to-speech voices. Different operating systems and language packs produce different voice lists.

**Fields:** `voices`

---

## How Is The Data Sent?

After collecting all data points (typically within 500ms–1.5s):

1. **Primary method:** `navigator.sendBeacon()` — A POST request that the browser guarantees to deliver even if the user navigates away or closes the tab
2. **Fallback:** Tracking pixel — An invisible 1×1 GIF image request with data encoded in the URL

The callback always goes to the same domain the script was loaded from. If the customer's site loads our script from `smartpixl.com`, the data goes back to `smartpixl.com`.

---

## What Happens After Collection?

```
Browser → PiXL Script → sendBeacon POST → Edge Server
                                              ↓
                                         Named Pipe
                                              ↓
                                    Forge (enrichment)
                                              ↓
                                     SQL Server (storage)
                                              ↓
                                   Sentinel (dashboards)
```

The Edge server receives the raw data and forwards it to the Forge via named pipe. The Forge runs 15+ enrichment services (bot detection, geolocation, device identification, session stitching) before writing to SQL Server. The Sentinel dashboard serves analytics to our team and customers.

---

## Key Design Principles

1. **Invisible:** The script must never visibly affect the customer's website. No UI, no delays, no console output.
2. **Resilient:** If any individual test fails, the script continues collecting other data. If the entire script crashes, it reports the error.
3. **Fast:** Target is under 500ms total execution time. Heavy tests (audio) run asynchronously.
4. **Anti-evasion aware:** Every fingerprint test includes a consistency/noise check. If someone is trying to defeat fingerprinting, we detect the attempt itself.
5. **No external dependencies:** The script is self-contained. No third-party libraries.
