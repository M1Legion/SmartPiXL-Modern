---
subsystem: pixl-script
title: "PiXL Script: Evasion Detection"
version: 1.0
last_updated: 2026-02-21
status: current
parent: subsystems/pixl-script
related:
  - subsystems/pixl-script/bot-detection-engine
  - subsystems/pixl-script/cross-signal-analysis
  - subsystems/pixl-script/fingerprinting-techniques
---

# PiXL Script: Evasion Detection

## Atlas Public

### Catching Visitors Who Hide Their Identity

Some visitors use privacy tools to prevent being identified — VPNs, Tor Browser, anti-detect browsers, or browser extensions that scramble fingerprints. SmartPiXL doesn't just detect these tools — it **identifies which specific evasion technique is being used**.

```
  ┌──────────────── EVASION DETECTION ────────────────┐
  │                                                    │
  │  Is the visitor hiding behind a privacy tool?      │
  │                                                    │
  │  ┌─────────────┐  ┌─────────────┐  ┌───────────┐  │
  │  │   Privacy    │  │  Identity   │  │  Stealth  │  │
  │  │   Browser    │  │  Spoofing   │  │  Plugins  │  │
  │  └──────┬──────┘  └──────┬──────┘  └─────┬─────┘  │
  │         │                │               │         │
  │  Tor, Brave,       Fake browser    Hidden scripts  │
  │  VPN detected      identity        that mask       │
  │                    detected        automation      │
  │                                                    │
  │  Result: Flagged as "evasion detected"             │
  │  Specific tool identified when possible            │
  │                                                    │
  └────────────────────────────────────────────────────┘
```

**Why this matters:** A visitor using an anti-detect browser to disguise their device identity may be:
- A competitor researching your pricing
- A scraper evading your rate limits
- A fraudster covering their tracks
- Or simply a privacy-conscious individual

SmartPiXL gives you the data to decide which it is.

---

## Atlas Internal

### Three Layers of Evasion Detection

SmartPiXL detects evasion through three separate detection systems that run during the same 500ms window:

#### Layer 1: Known Privacy Browser Detection (`evasionDetected`)

| Signal | What It Detects | How We Detect It |
|--------|----------------|------------------|
| `tor-screen` | Tor Browser | Screen is exactly 1000×1000 (Tor's default letterbox) |
| `tor-likely` | Probable Tor | Win32 platform + 24-bit color + no `window.chrome` object |
| `brave` | Brave Browser | `navigator.brave.isBrave()` function exists |
| `webrtc-blocked` | WebRTC disabled | `RTCPeerConnection` is undefined (Tor, some extensions) |
| `partial-js-block` | Selective JS blocking | Web Workers disabled but Fetch API works — unusual combination |

#### Layer 2: Identity Spoofing Detection (`evasionDetected` continued)

These detect visitors who claim to be something they're not:

| Signal | What's Wrong | Example |
|--------|-------------|---------|
| `ua-platform-mismatch` | User-agent says one OS, platform says another | UA says "Mac" but `navigator.platform` says "Win32" |
| `mobile-ua-desktop-screen` | Claims to be mobile but has a desktop screen | UA contains "iPhone" but screen width > 1024px |
| `touch-mismatch` | Has touch capability but no mobile indicators | Touch points > 0, no mobile UA, wide screen |
| `clienthints-platform-mismatch` | Two platform APIs disagree | `navigator.platform` says Linux, Client Hints say Windows |

#### Layer 3: Stealth Plugin Detection (`stealthSignals`)

These are the most advanced checks — they detect tools specifically designed to hide automation:

| Signal | What It Catches | How |
|--------|----------------|-----|
| `webdriver-slow` | Property access intercepted by a Proxy | Accessing `navigator.webdriver` takes 5x+ longer than `navigator.userAgent` |
| `platform-slow` | Another property intercepted | `navigator.platform` access is suspiciously slow |
| `toString-spoofed` | `Function.prototype.toString` replaced | The function that describes functions has been tampered with |
| `nav-proto-modified` | Navigator prototype chain altered | `Object.getPrototypeOf(navigator)` doesn't match `Navigator.prototype` |
| `proxy-modified` | Even the Proxy constructor itself is tampered | `Proxy.toString()` doesn't contain `[native code]` |

#### Aggregation: Evasion Signals V2 (`evasionSignalsV2`)

A second-pass aggregation that combines results from fingerprinting and stealth detection:

| Signal | Source | What It Means |
|--------|--------|---------------|
| `tor-letterbox-viewport` | Viewport math | Viewport dimensions are multiples of 200×100 (Tor's letterboxing) |
| `tor-letterbox-screen` | Screen math | Screen dimensions match Tor letterbox pattern (not common resolutions) |
| `minimal-fonts` | Font detection | Fewer than 5 fonts detected — Tor or stripped-down browser |
| `canvas-noise` | Canvas consistency | Canvas fingerprint changes between reads — noise injection |
| `canvas-blocked` | Canvas consistency | All canvas reads return identical data regardless of content |
| `audio-noise` | Audio fingerprint | Audio fingerprint changes between runs — noise injection |
| `font-spoof` | Font detection | Two font measurement methods disagree — metrics being spoofed |
| `stealth-detected` | Stealth signals | Any stealth plugin signal was triggered |

### How to Explain This to Customers

**Simple version:** "SmartPiXL can tell when a visitor is trying to hide their identity. We detect specific privacy tools like Tor Browser and Brave, and we catch visitors who are pretending to be on a different device than they actually are."

**When asked about false positives:** "A small percentage of legitimate visitors use privacy tools. SmartPiXL doesn't block anyone — it flags them so you can decide how to handle them. A visitor using Brave Browser is flagged as using privacy tools, but their behavior score might still be 'human.' You get both data points."

---

## Atlas Technical

### Evasion Detection Implementation

**Privacy browser detection** (PiXLScript.cs lines 715-750):

```javascript
var evasionResult = (function() {
    var detected = [];
    
    // Tor Browser: letterboxed to 1000×1000
    if (s.width === 1000 && s.height === 1000) {
        detected.push('tor-screen');
    }
    // Tor likely: Win32 + 24-bit + no chrome object
    if (data.plt === 'Win32' && s.colorDepth === 24 && !w.chrome) {
        detected.push('tor-likely');
    }
    // Brave: navigator.brave.isBrave() exists
    var braveNav = safeGet(n, 'brave', null);
    if (braveNav && typeof braveNav.isBrave === 'function') {
        detected.push('brave');
    }
    // ... more checks ...
    return detected.join(',');
})();
```

### Identity Spoofing Detection

**UA/Platform cross-reference:**

```javascript
var platform = (data.plt || '').toLowerCase();
var ua = (data.ua || '').toLowerCase();

// Platform says Windows, UA says Mac (or vice versa)
if ((platform.indexOf('win') > -1 && ua.indexOf('mac') > -1) ||
    (platform.indexOf('mac') > -1 && ua.indexOf('windows') > -1) ||
    (platform.indexOf('linux') > -1 && ua.indexOf('windows') > -1 
     && ua.indexOf('android') === -1)) {
    detected.push('ua-platform-mismatch');
}
```

The Android exclusion is important — Android reports `platform=linux` legitimately.

**Client Hints cross-reference:**

```javascript
if (uaData && uaData.platform) {
    var chPlatform = uaData.platform.toLowerCase();
    var navPlatform = (data.plt || '').toLowerCase();
    if ((navPlatform.indexOf('linux') > -1 && chPlatform === 'windows') ||
        (navPlatform.indexOf('win') > -1 && chPlatform === 'linux') ||
        (navPlatform.indexOf('mac') > -1 && chPlatform !== 'macos' 
         && chPlatform !== 'mac')) {
        detected.push('clienthints-platform-mismatch');
    }
}
```

This is more reliable than the UA string check because Client Hints and `navigator.platform` are separate APIs — spoofing both consistently is harder.

### Stealth Detection Implementation

**Property access timing** (PiXLScript.cs lines 967-983):

```javascript
var timeProp = function(obj, prop, iters) {
    var start = performance.now();
    for (var i = 0; i < iters; i++) { var x = obj[prop]; }
    return (performance.now() - start) / iters;
};
var baseTime = timeProp(navigator, 'userAgent', 1000);
if (baseTime > 0) {
    var wdRatio = timeProp(navigator, 'webdriver', 1000) / baseTime;
    var plRatio = timeProp(navigator, 'platform', 1000) / baseTime;
    if (wdRatio > 5) signals.push('webdriver-slow');
    if (plRatio > 5) signals.push('platform-slow');
}
```

**How this works:** Native property access takes the same time for all properties (nanoseconds). If `navigator.webdriver` takes 5x longer than `navigator.userAgent`, it's being intercepted by a JavaScript Proxy or getter that runs custom logic. The 1000-iteration average smooths out noise.

**`Function.prototype.toString` integrity:**
```javascript
var nts = Function.prototype.toString;
if (nts.call(nts).indexOf('[native code]') === -1) 
    signals.push('toString-spoofed');
```

This calls `toString` on *itself*. If `toString` has been replaced with a JavaScript function (to make other spoofed functions look native), calling it on itself reveals the deception — the replacement function's source code will be returned instead of `[native code]`.

**Prototype chain check:**
```javascript
if (Object.getPrototypeOf(navigator) !== Navigator.prototype)
    signals.push('nav-proto-modified');
```

Some stealth tools replace the entire navigator prototype to intercept all property access. This one-line check catches that.

### Evasion Signals V2 — Second-Pass Aggregation

```javascript
data.evasionSignalsV2 = (function() {
    var det = [];
    // Tor letterboxing: viewport divisible by 200×100
    if (iW % 200 === 0 && iH % 100 === 0) det.push('tor-letterbox-viewport');
    // Tor letterboxing: screen divisible by 200×100 (excluding common resolutions)
    if (sW % 200 === 0 && sH % 100 === 0 && 
        !(sW === 1920 && sH === 1080) && !(sW === 1600 && sH === 900))
        det.push('tor-letterbox-screen');
    // ... aggregates earlier fingerprint evasion results ...
    if (data.canvasConsistency === 'noise-detected') det.push('canvas-noise');
    if (data.canvasConsistency === 'canvas-blocked') det.push('canvas-blocked');
    if (data.audioNoiseDetected) det.push('audio-noise');
    if (data.fontMethodMismatch) det.push('font-spoof');
    if (data.stealthSignals && data.stealthSignals.length > 0) det.push('stealth-detected');
    return det.join(',');
})();
```

The 1920×1080 and 1600×900 exclusions are critical — these common resolutions happen to be divisible by 200×100, so they'd incorrectly flag real desktop monitors.

---

## Atlas Private

### Tor Detection Accuracy

The `tor-screen` check (1000×1000) catches **Tor Browser with default settings**. However:
- Tor Browser allows resizing, so a resized Tor window won't trip this check.
- The `tor-likely` check (Win32 + 24-bit + no chrome) catches more cases but has higher false positive risk — e.g., Firefox on Windows also matches this pattern.
- The letterbox checks in V2 cast a wider net by looking for the 200×100 quantization pattern.

In practice, Tor users who care about anonymity keep the default window size (Tor explicitly warns about resizing). The combined detection rate is estimated at 85%+ for default-config Tor.

### Brave Detection

Brave is the only major browser that exposes `navigator.brave`. This is a deliberate design choice by the Brave team — they want sites to detect Brave for feature compatibility, even though it's a privacy browser. This makes Brave the easiest privacy browser to detect.

However: Brave's fingerprint randomization is the **primary concern**, not detection. We detect Brave via its self-identification, but we also detect its effects (canvas noise, WebGL parameter randomization) independently. A user who installs a Brave-like extension in Chrome gets the noise detection without the Brave identification.

### Stealth Timing Attack Limitations

The property access timing check (`webdriver-slow`, `platform-slow`) is a **probabilistic** detection. Factors:

- **JavaScript timer resolution**: `performance.now()` has been reduced to 5μs in some browsers (spectre mitigations). With 1000 iterations, we're measuring total times in the 0.1-10ms range, which is well above timer resolution.
- **JIT warmup**: The 1000-iteration loop ensures JIT compilation kicks in, normalizing overhead.
- **False positive risk**: Very low. A native property getter takes ~0.001μs. A Proxy getter with even trivial logic takes ~0.01-0.1μs. The 5x threshold is conservative.
- **False negative risk**: A well-optimized stealth plugin that caches results and returns them from a direct property (not a Proxy) would pass this check. This is why we have multiple layers — the cross-realm toString, getter name, and prototype checks catch what timing doesn't.

### Why Three Separate Fields?

The three evasion-related fields evolved over time:

1. **`evasionDetected`** — original field, detects known privacy tools and identity spoofing.
2. **`stealthSignals`** — added when anti-detection stealth plugins became common. Specifically targets JavaScript-level tampering used by Puppeteer stealth, undetectable-chromedriver, etc.
3. **`evasionSignalsV2`** — second-pass aggregation that combines fingerprint evasion results (canvas noise, audio noise) with stealth detection. Created because the original fields didn't cross-reference each other.

A future refactor could merge these into a single composite field, but the three-field structure works for now and preserves backward compatibility with existing ETL column mappings.
