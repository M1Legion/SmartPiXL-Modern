---
subsystem: pixl-script
title: PiXL Script — Overview
version: 2.0
last_updated: 2026-02-21
status: current
related:
  - architecture/data-flow
  - subsystems/fingerprinting
  - subsystems/bot-detection
deep_dive:
  - subsystems/pixl-script/data-fields
  - subsystems/pixl-script/fingerprinting-techniques
  - subsystems/pixl-script/bot-detection-engine
  - subsystems/pixl-script/evasion-detection
  - subsystems/pixl-script/cross-signal-analysis
  - subsystems/pixl-script/behavioral-analysis
  - subsystems/pixl-script/delivery-mechanism
---

# PiXL Script — Overview

> This is the hub page. Each major capability has its own deep-dive document
> linked below.

## Atlas Public

The PiXL Script is SmartPiXL's browser-side intelligence collector. When a visitor loads any page on your website, this lightweight JavaScript runs silently in the background — invisible, fast, and comprehensive.

```
  ┌──────────────────────────────────────────────────────────┐
  │                   VISITOR'S BROWSER                      │
  │                                                          │
  │  Page loads ──► PiXL Script runs silently (< 500ms)      │
  │                     │                                    │
  │         ┌───────────┼───────────────┐                    │
  │         ▼           ▼               ▼                    │
  │    Device ID    Bot Check    Behavior Watch              │
  │   (who is it?)  (is it real?)  (what does it do?)        │
  │         │           │               │                    │
  │         └───────────┼───────────────┘                    │
  │                     ▼                                    │
  │           1x1 invisible image request                    │
  │           (data encoded in the URL)                      │
  │                     │                                    │
  └─────────────────────│────────────────────────────────────┘
                        ▼
               SmartPiXL Server
```

**By the numbers:**

| Metric | Value |
|--------|-------|
| Data fields collected | 159 |
| Data points extracted | 230+ (composite fields contain multiple signals) |
| Time to complete | Under 500ms (usually ~80ms) |
| External dependencies | Zero |
| Cookies required | None |
| Visible impact on page | None |

**What it captures — in plain terms:**

| Category | Analogy | What It Captures |
|----------|---------|-----------------|
| Device Identity | Like a car's VIN | Screen, GPU, fonts, audio — a unique hardware "signature" |
| Bot Detection | Like a lie detector | 30+ checks for fake browsers, automation tools, spoofed data |
| Privacy Evasion | Like spotting a disguise | Detects Tor, Brave, VPNs, anti-detect browsers |
| Behavior | Like body language | Mouse movement patterns, scroll depth, timing |
| Browser Profile | Like a passport | Language, timezone, installed features, plugins |

**Deep-Dive Pages:**

| Page | What It Explains |
|------|-----------------|
| [Data Fields](pixl-script/data-fields.md) | Every field collected, organized by category |
| [Fingerprinting](pixl-script/fingerprinting-techniques.md) | How canvas, GPU, audio, and font fingerprints work |
| [Bot Detection](pixl-script/bot-detection-engine.md) | The 30+ signal scoring system |
| [Evasion Detection](pixl-script/evasion-detection.md) | How we catch privacy tools and spoofing |
| [Cross-Signal Analysis](pixl-script/cross-signal-analysis.md) | How we correlate signals to catch sophisticated fakes |
| [Behavioral Analysis](pixl-script/behavioral-analysis.md) | Mouse movement, scroll tracking, entropy math |
| [Delivery Mechanism](pixl-script/delivery-mechanism.md) | How the data gets from browser to server |

---

## Atlas Internal

### How the Script Works — Step by Step

```
  STEP 1: Customer's webpage loads
    └──► <script src="/{company}/{pixel}_SMART.js"></script>

  STEP 2: Our server generates the JavaScript dynamically
    └──► C# template (PiXLScript.cs) injects the pixel URL

  STEP 3: Script runs in the visitor's browser
    └──► Collects 159 fields from browser APIs
    └──► Runs bot detection (30+ checks)
    └──► Watches mouse movement and scroll behavior

  STEP 4: All data fires as a 1x1 GIF request
    └──► _SMART.GIF?canvasFP=a3f9c&gpu=NVIDIA+RTX+4090&...
    └──► Visitor never sees, feels, or knows about it

  STEP 5: Server receives, enriches, and stores the data
    └──► Edge server: fast enrichments (IP classification, geo cache)
    └──► Forge service: deep enrichments (DNS, WHOIS, cross-customer intel)
```

### The 500ms Window

The script doesn't wait 500ms — that's a **safety ceiling**, not a delay. Most data is instant (~10ms). The window exists because a few operations are asynchronous:

| Async Operation | Why It's Async | Typical Time |
|-----------------|---------------|--------------|
| Audio fingerprint | Renders an audio buffer | ~50ms |
| Battery level | OS-level API call | ~10ms |
| Media device count | Enumerates hardware | ~20ms |
| Client hints | Requests detailed platform info | ~15ms |
| Storage quota | Disk estimation | ~10ms |

**Guaranteed delivery:** If any async probe hangs forever, the 500ms timeout fires the data anyway with whatever was collected. The pixel **always** fires.

During this window, the script passively records mouse movements and scroll events — used for distinguishing human visitors from bots.

### What We DON'T Collect

| Concern | Answer |
|---------|--------|
| Camera/microphone access? | **No.** We count how many exist. We never activate them. |
| Screen recording? | **No.** |
| Keystrokes or form data? | **No.** The script never reads any text input. |
| Cookies? | **No.** Identification is entirely cookie-independent. |
| Permission dialogs? | **Never.** Nothing triggers a browser popup. |
| Console output? | **None.** Zero visible trace in developer tools. |

### Field Count Summary: 159

| Category | Count | Deep Dive |
|----------|-------|-----------|
| Screen & Display | 14 | [Data Fields](pixl-script/data-fields.md) |
| Navigator Properties | 18 | [Data Fields](pixl-script/data-fields.md) |
| Fingerprints (Canvas/WebGL/Audio/Font/Math/CSS/Error) | 21 | [Fingerprinting](pixl-script/fingerprinting-techniques.md) |
| Client Hints (UA-CH) | 10 | [Data Fields](pixl-script/data-fields.md) |
| Network & WebRTC | 7 | [Data Fields](pixl-script/data-fields.md) |
| Storage & APIs | 23 | [Data Fields](pixl-script/data-fields.md) |
| Hardware (Battery, Media, Gamepads) | 6 | [Data Fields](pixl-script/data-fields.md) |
| CSS & Accessibility Preferences | 10 | [Data Fields](pixl-script/data-fields.md) |
| Timezone & Locale | 7 | [Data Fields](pixl-script/data-fields.md) |
| Performance Timing | 5 | [Data Fields](pixl-script/data-fields.md) |
| Page Context & Document State | 13 | [Data Fields](pixl-script/data-fields.md) |
| Plugins & MIME Types | 4 | [Data Fields](pixl-script/data-fields.md) |
| Bot Detection | 3 | [Bot Detection](pixl-script/bot-detection-engine.md) |
| Evasion Detection | 3 | [Evasion](pixl-script/evasion-detection.md) |
| Cross-Signal Anomaly | 3 | [Cross-Signal](pixl-script/cross-signal-analysis.md) |
| Behavioral (Mouse/Scroll) | 10 | [Behavioral](pixl-script/behavioral-analysis.md) |
| Metadata & Debug | 2 | [Data Fields](pixl-script/data-fields.md) |

---

## Atlas Technical

### Template Architecture

The PiXL Script is not a static `.js` file. It's generated by `PiXLScript.cs` — ~1,155 lines of JavaScript embedded in a C# string constant. At serve time, a single placeholder (`{{PIXL_URL}}`) is replaced with the company/pixel-specific GIF URL.

```
PiXLScript.cs (C# string template)
    │
    ▼  GetScript(pixlUrl)
ConcurrentDictionary cache (max 10,000 entries)
    │
    ▼  Cache miss? Template.Replace("{{PIXL_URL}}", url)
Generated JS string (cached per companyId/pixlId)
    │
    ▼  Served on GET /{company}/{pixl}_SMART.js
Browser receives and executes
```

**Endpoint:** `TrackingEndpoints.ServeScript()` — `Content-Type: application/javascript`, `Cache-Control: public, max-age=3600`.

**Why C# template instead of static JS?**
- Server-side URL injection at generation time (no config file in the browser)
- Versioning via C# file (deploys with the app, not a CDN invalidation)
- Cache per company/pixel combo — zero allocation after first hit

### Script Execution Order

The script executes in this order, with async operations running in parallel during the collection window:

```
1. Setup & Utilities           ──► safeGet(), hashStr()
2. Canvas Fingerprint          ──► canvasFP, canvasEvasion, canvasConsistency
3. WebGL Fingerprint           ──► webglFP, gpu, gpuVendor, webglParams, webglExt
4. Audio Fingerprint (async)   ──► audioFP, audioHash, audioStable
5. Font Detection              ──► fonts, fontMethodMismatch
6. Speech Synthesis Voices     ──► voices
7. WebRTC Local IP (async)     ──► localIp
8. Storage Quota (async)       ──► storageQuota, storageUsed
9. Gamepads                    ──► gamepads
10. Battery (async)            ──► batteryLevel, batteryCharging
11. Media Devices (async)      ──► audioInputs, videoInputs
12. Screen & Display           ──► sw, sh, saw, sah, cd, pd, ori, vw, vh, ow, oh, ...
13. Timezone & Locale          ──► tz, tzo, ts, lang, langs, tzLocale, dateFormat, ...
14. Navigator Properties       ──► plt, vnd, ua, cores, mem, touch, ...
15. JS Heap Memory             ──► jsHeapLimit, jsHeapTotal, jsHeapUsed
16. Client Hints (async)       ──► uaArch, uaBitness, uaModel, uaPlatformVersion, ...
17. Plugins & MIME Types       ──► pluginList, mimeList
18. Boolean Feature Flags      ──► ck, dnt, pdf, webdr, online, java, ...
19. Bot Detection Engine       ──► botSignals, botScore
20. Evasion Detection          ──► evasionDetected
21. Cross-Signal Anomaly       ──► crossSignals, anomalyScore, combinedThreatScore
22. Network & Connection       ──► conn, dl, dlMax, rtt, save, connType
23. Page Context               ──► url, ref, hist, title, domain, path, hash, protocol
24. Performance Timing         ──► loadTime, domTime, dnsTime, tcpTime, ttfb
25. API Support Flags          ──► ls, ss, idb, caches, ww, swk, wasm, webgl, ...
26. CSS Media Preferences      ──► darkMode, reducedMotion, contrast, hover, pointer, ...
27. Document State             ──► docCharset, docCompat, docReady, docHidden, ...
28. Math Fingerprint           ──► mathFP
29. CSS Font Variant FP        ──► cssFontVariant
30. Error Fingerprint          ──► errorFP
31. Stealth Signals            ──► stealthSignals
32. Evasion Signals V2         ──► evasionSignalsV2
  ─── Mouse/Scroll listeners run from step 12 until fire ───
33. Behavioral Analysis        ──► mouseMoves, mouseEntropy, mousePath, scrolled, ...
34. GIF Fire                   ──► new Image().src = '{{PIXL_URL}}?' + queryString
```

See each deep-dive page for implementation details of each step.

---

## Atlas Private

### Complete Architecture: PiXLScript.cs

The entire script lives in a single file: `SmartPiXL/Scripts/PiXLScript.cs`. It's a `public static class` with:

- `Template` — a `const string` containing ~1,155 lines of JavaScript. Allocated once at app startup, lives for the process lifetime. `const` means it's baked into the assembly metadata.
- `_cache` — a `ConcurrentDictionary<string, string>` mapping pixel URLs to generated scripts. Lock-free, thread-safe.
- `GetScript(pixlUrl)` — calls `_cache.GetOrAdd()` with `Template.Replace("{{PIXL_URL}}", url)` as the factory. The Replace only runs on cache miss. Nuclear eviction (`_cache.Clear()`) at 10,000 entries to bound memory — acceptable because warm-up is a single string.Replace per URL.

**Why `const string` instead of embedded resource?** The JIT interns const strings. The 40KB template is interned in the string pool — no additional heap allocation beyond the one the CLR creates at assembly load. An embedded resource would require `GetManifestResourceStream()` → `StreamReader.ReadToEnd()` → a new heap string every time (unless manually cached, which is what we're doing anyway).

### Key Implementation Patterns

**`safeGet(obj, prop, fallback)`** — Lines 29-40. Wraps every browser API access. If the property is a function, it calls it (handles `navigator.javaEnabled()` transparently). If a Proxy traps the access and throws, the caught property name is appended to `data._proxyBlocked` — this is diagnostic data for understanding which privacy tools are active.

**`hashStr(str)`** — Lines 43-52. DJB2 variant. Closure-captured `h` variable (no allocation per call). `h = ((h << 5) - h) + charCodeAt(i)` is multiply-by-31 plus character code. `h = h & h` forces 32-bit integer (avoids floating-point drift). Returns `Math.abs(h).toString(16)`.

**Fire-and-forget pattern** — Lines 1100-1131. The critical guarantee: the GIF **must** fire. Three paths:
1. `Promise.allSettled` available + async work: `Promise.race([allSettled(asyncPromises), 500ms timeout]).then(sendPiXL)` — whichever wins triggers the send.
2. `Promise.allSettled` not available (IE11 edge case): falls through to the 500ms timeout.
3. Catastrophic error: outer try/catch fires `new Image().src = '{{PIXL_URL}}?error=1&msg=...'` — even a script crash produces a data point.

**No `Promise.allSettled` fallback** — old browsers that lack it just wait 500ms. This is fine because the async data (audio, battery, etc.) usually completes well within 500ms anyway. Worst case: those fields are empty, but the hit is recorded.

### Script Size

| Metric | Value |
|--------|-------|
| Source lines (JS in C# template) | ~1,155 |
| Uncompressed JS | ~38KB |
| Gzip over wire | ~9KB |
| Cache-Control | `public, max-age=3600` |
| External dependencies | Zero |

### Known Browser Limitations

| Browser | Missing APIs | Impact |
|---------|-------------|--------|
| Firefox | `deviceMemory`, `performance.memory`, Client Hints (`userAgentData`) | Fingerprint has fewer dimensions, but canvas/WebGL/fonts/audio still work |
| Safari | Battery API, Client Hints, `connection` API | Relies more on canvas, WebGL, fonts, screen |
| Brave | Randomizes canvas and WebGL output | Detected by repeatability test → flagged as evasion → noisy fields excluded from fingerprint hash |
| Tor Browser | Letterboxed viewport, minimal fonts, blocked WebRTC | Detected via viewport math + font count. Very low uniqueness by design. |
| Edge | Identical to Chrome (same engine) | Full API coverage |

### Deep-Dive Documents

| Document | Lines of PiXLScript.cs | Content |
|----------|----------------------|---------|
| [data-fields.md](pixl-script/data-fields.md) | All | Complete 159-field inventory |
| [fingerprinting-techniques.md](pixl-script/fingerprinting-techniques.md) | 56-965 | Canvas, WebGL, Audio, Font, Math, CSS, Error FP |
| [bot-detection-engine.md](pixl-script/bot-detection-engine.md) | 483-713 | Scored signal system |
| [evasion-detection.md](pixl-script/evasion-detection.md) | 715-750, 967-1010 | Privacy/spoofing/stealth detection |
| [cross-signal-analysis.md](pixl-script/cross-signal-analysis.md) | 752-850 | Cross-correlation anomaly scoring |
| [behavioral-analysis.md](pixl-script/behavioral-analysis.md) | 1012-1098 | Mouse/scroll tracking + entropy |
| [delivery-mechanism.md](pixl-script/delivery-mechanism.md) | 1100-1168 | GIF fire, 500ms window, C# host |
