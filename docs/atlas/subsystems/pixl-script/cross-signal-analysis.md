---
subsystem: pixl-script
title: "PiXL Script: Cross-Signal Analysis"
version: 1.0
last_updated: 2026-02-21
status: current
parent: subsystems/pixl-script
related:
  - subsystems/pixl-script/bot-detection-engine
  - subsystems/pixl-script/evasion-detection
  - subsystems/pixl-script/fingerprinting-techniques
---

# PiXL Script: Cross-Signal Analysis

## Atlas Public

### Catching Sophisticated Fakes

Some bots are so advanced that they pass every individual check — they set the right browser properties, mimic human behavior, and even spoof their GPU name. But they can't be perfect at **everything simultaneously**.

SmartPiXL's cross-signal analysis works like a detective comparing stories. If a suspect says they were in New York but their phone GPS says Chicago, something's wrong. Similarly, if a visitor claims to be using Safari on a Mac but has Windows fonts installed and a Chrome-only GPU — that's a contradiction.

```
  ┌────────────── CROSS-SIGNAL ANALYSIS ──────────────┐
  │                                                    │
  │  "I'm a Mac user"                                 │
  │     + Windows fonts installed    → CONTRADICTION   │
  │     + Chrome's GPU renderer     → CONTRADICTION    │
  │     + Google as vendor          → CONTRADICTION    │
  │                                                    │
  │  Any ONE of these could be noise.                  │
  │  All THREE together? That's a spoofed identity.    │
  │                                                    │
  │  Anomaly Score: 50                                 │
  │  (Each contradiction adds points)                  │
  │                                                    │
  └────────────────────────────────────────────────────┘
```

Think of it as a **lie detector for device identity**. You can fake one thing convincingly, but faking everything consistently is almost impossible.

---

## Atlas Internal

### How Cross-Signal Analysis Works

The script already has 100+ data fields collected. Cross-signal analysis **correlates** those fields — looking for combinations that shouldn't exist on a real device.

#### Category 1: Font/Platform Contradictions

Certain fonts only exist on certain operating systems:

| Font | Expected OS | If Found On Wrong OS... |
|------|-------------|------------------------|
| Segoe UI, Calibri, Consolas | Windows | `win-fonts-on-mac` (+15), `win-fonts-on-linux` (+15) |
| Monaco, Lucida Grande, Apple Color Emoji | macOS | `mac-fonts-on-win` (+10) |
| MS Gothic, Microsoft YaHei | Windows (Asian) | Same as above |

**What this catches:** Anti-detect browsers that spoof the platform to "Mac" but forget that their Windows host has Windows fonts installed.

#### Category 2: Safari Impersonation

Safari is the hardest browser to impersonate because it's the most different from Chrome under the hood. When someone claims to be Safari but isn't:

| Signal | Points | What's Wrong |
|--------|--------|-------------|
| `safari-google-vendor` | +20 | Safari's vendor is "Apple Computer, Inc.", not "Google Inc." |
| `safari-has-chrome-obj` | +15 | Safari doesn't have `window.chrome` — Chrome/Edge does |
| `safari-has-client-hints` | +10 | Safari doesn't support Client Hints API at all |
| `safari-chromium-gpu` | +15 | Safari uses Metal GPU names, not ANGLE/SwiftShader |

A bot pretending to be Safari but actually running Chrome typically scores **40-60 anomaly points** from these checks alone.

#### Category 3: GPU/Platform Impossibilities

Some GPU + operating system combinations are physically impossible:

| Signal | Points | Why It's Impossible |
|--------|--------|-------------------|
| `swiftshader-on-mac` | +20 | macOS uses Metal. SwiftShader is a Google software renderer that doesn't run on Mac. |
| `llvmpipe-on-mac` | +20 | llvmpipe is a Linux/Mesa software renderer. It doesn't exist on macOS. |
| `gpu-platform-mismatch` | +15 | Mac GPU names (Intel Iris, Apple M1) on a non-Mac platform |
| `software-gpu-on-mac` | +10 | Any software renderer on macOS — Mac always uses hardware GPU |

#### Category 4: Performance Anomalies

| Signal | Points | What It Means |
|--------|--------|---------------|
| `round-heap-limit` | +5 | JS heap limit is a suspiciously round number |
| `instant-page-load` | +5 | Page loaded in < 50ms with no DNS lookup — locally served or cached |
| `zero-latency-connection` | +3 | Zero TCP latency — traffic isn't going over a real network |
| `connection-missing-rtt` | +5 | Claims 4G with 5+ Mbps but no RTT — spoofed connection data |
| `webgl2-on-old-safari` | +10 | WebGL 2.0 available but Safari version < 15 (Safari 15 was the first with WebGL 2) |

### Scores Are Additive

A single minor anomaly (round heap limit, +5) is just noise. But contradictions compound:

> **Example — A bot pretending to be Safari on Mac:**
> - `safari-google-vendor` (+20)
> - `safari-has-chrome-obj` (+15)
> - `win-fonts-on-mac` (+15)
> - `swiftshader-on-mac` (+20)
>
> **Anomaly score: 70**
>
> Combined with `botScore` → `combinedThreatScore = botScore + min(70, 25) = botScore + 25`

The anomaly score is **capped at 25** when added to the combined threat score. This prevents cross-signal analysis from overwhelming bot detection — a perfectly clean bot that just has weird hardware shouldn't score higher than a confirmed Selenium bot.

---

## Atlas Technical

### Implementation Architecture

Cross-signal analysis (PiXLScript.cs lines 752-850) runs after all data collection is complete. It reads from `data.*` fields already populated by earlier sections:

```javascript
var flags = '', anomalyScore = 0;
var platform = (data.plt || '').toLowerCase();
var ua = (data.ua || '').toLowerCase();
var gpu = (data.gpu || '').toLowerCase();
var vendor = (data.vnd || '');
var fonts = (data.fonts || '');

// Font/platform checks use bitwise OR for boolean accumulation
var hasWinFont = fonts.indexOf('Segoe UI') > -1 | fonts.indexOf('Calibri') > -1 |
          fonts.indexOf('Consolas') > -1 | fonts.indexOf('MS Gothic') > -1 |
          fonts.indexOf('Microsoft YaHei') > -1;
var hasMacFont = fonts.indexOf('Monaco') > -1 | fonts.indexOf('Lucida Grande') > -1 |
          fonts.indexOf('Apple Color Emoji') > -1;
```

**Note:** Uses bitwise OR (`|`) instead of logical OR (`||`) for the font checks. This is intentional — bitwise OR evaluates all operands and produces a 0/1 integer, avoiding short-circuit evaluation. All `indexOf()` calls execute regardless, and the result is a clean truthy/falsy integer.

### Safari Impersonation Detection

```javascript
var isSafariUA = ua.indexOf('safari') > -1 && ua.indexOf('chrome') < 0 
                 && ua.indexOf('chromium') < 0;
if (isSafariUA) {
    if (vendor === 'Google Inc.') {
        flags += (flags ? ',' : '') + 'safari-google-vendor'; anomalyScore += 20;
    }
    if (data.chromeObj) {
        flags += (flags ? ',' : '') + 'safari-has-chrome-obj'; anomalyScore += 15;
    }
    if (userAgentData && userAgentData.brands) {
        flags += (flags ? ',' : '') + 'safari-has-client-hints'; anomalyScore += 10;
    }
    if (gpu.indexOf('angle') > -1 || gpu.indexOf('swiftshader') > -1) {
        flags += (flags ? ',' : '') + 'safari-chromium-gpu'; anomalyScore += 15;
    }
}
```

The `isSafariUA` check explicitly excludes "chrome" and "chromium" from the UA string. All Chromium browsers include "Safari" in their UA (legacy compatibility), so the exclusion is necessary to detect only actual Safari impersonation.

### Performance Timing Anomalies

```javascript
if (perf.timing) {
    var t = perf.timing;
    var pageLoad = (t.loadEventEnd - t.navigationStart) | 0;
    var dns = (t.domainLookupEnd - t.domainLookupStart) | 0;
    var tcp = (t.connectEnd - t.connectStart) | 0;
    if (pageLoad > 0 && pageLoad < 50 && dns <= 1) {
        flags += ... + 'instant-page-load'; anomalyScore += 5;
    }
    if (tcp <= 1 && pageLoad > 0 && pageLoad < 100) {
        flags += ... + 'zero-latency-connection'; anomalyScore += 3;
    }
}
```

The `| 0` truncation forces the timing differences to integers (floors potential NaN to 0). The `pageLoad > 0` guard prevents false positives when `loadEventEnd` hasn't been set yet (script executing before page fully loaded).

### Scroll Contradiction (Behavioral Cross-Signal)

Added to `crossSignals` during the behavioral analysis phase (lines 1030-1038):

```javascript
if (mouseData.scrolled && mouseData.scrollY === 0) {
    data.scrollContradiction = 1;
    var cs = data.crossSignals || '';
    data.crossSignals = cs ? cs + ',scroll-no-depth' : 'scroll-no-depth';
    data.anomalyScore = (data.anomalyScore | 0) + 8;
}
```

A scroll event fired but `scrollY` is still 0 → the scroll was programmatically dispatched (bot) but no actual scrolling occurred.

### Output Fields

```javascript
data.crossSignals = flags;           // comma-separated anomaly names
data.anomalyScore = anomalyScore;    // raw anomaly score (uncapped)
data.combinedThreatScore = (data.botScore || 0) + Math.min(anomalyScore, 25);
```

The anomaly cap (`Math.min(anomalyScore, 25)`) ensures that cross-signal analysis is an **amplifier**, not a **dominator**. The primary signal should always be the bot detection engine.

---

## Atlas Private

### Design Decisions

**Why cap anomaly at 25?** Without the cap, a spoofed Safari bot could score 70+ on anomaly alone, exceeding even a confirmed Selenium bot's score. This made the combined score misleading — the highest scores would be from badly-spoofed identities rather than confirmed automation. The cap ensures `combinedThreatScore` correlates with bot confidence, not identity confusion.

**Why bitwise OR for font checks?** Two reasons:
1. **No short-circuit**: All `indexOf()` calls execute, ensuring we check all fonts. With `||`, JavaScript would stop at the first truthy result.
2. **Integer result**: Bitwise OR produces 0 or 1, which is more predictable than the truthy/falsy dance of `||` where the result is actually the last operand value (which could be a number like `3` from `indexOf` returning a positive position).

In practice, `indexOf() > -1` returns `true`/`false` (boolean), so `||` would also work. But the bitwise pattern is more explicit and avoids any edge case where a future refactor changes the expression shape.

**Why check `window.chrome` for Safari impersonation?** Safari on macOS does not have `window.chrome`. Chrome, Edge, Opera, and Brave all do. A visitor claiming Safari UA but having `window.chrome` is running a Chromium browser with a spoofed User-Agent string. The +15 score is high because this is a very reliable signal — no Safari version has ever exposed `window.chrome`.

### Flag String Accumulation Pattern

The `flags += (flags ? ',' : '') + 'signal-name'` pattern is used throughout instead of array `.push().join(',')`. This is a deliberate choice — it avoids creating an array allocation on the hot path. The ternary prevents a leading comma on the first flag. This pattern is used consistently across the cross-signal section (lines 752-850).

### GPU Impossibility Matrix

| GPU String Contains | Mac Platform? | Linux Platform? | Windows Platform? | Normal? |
|--------------------|----|----|----|---------|
| `intel iris` | Yes | No | Yes | Mac/Windows |
| `apple m` | Yes | No | No | Mac only |
| `apple gpu` | Yes | No | No | Mac only |
| `swiftshader` | **IMPOSSIBLE** | Rare (VMs) | Rare (headless) | Not normal |
| `llvmpipe` | **IMPOSSIBLE** | Yes (VMs) | No | Linux VMs |
| `mesa` | **IMPOSSIBLE** | Yes | No | Linux |
| `angle` | No | No | Yes | Chrome on Windows |

The "impossible" combinations are scored highest (+20) because they represent genuine hardware impossibilities — not just unusual configurations but configurations that **cannot exist on real hardware**.

### WebGL2 Safari Version Check

```javascript
if (data.webgl2 && isSafariUA) {
    var safariMatch = ua.match(/version\/(\d+)/);
    if (safariMatch && (safariMatch[1] | 0) < 15) {
        flags += (flags ? ',' : '') + 'webgl2-on-old-safari';
        anomalyScore += 10;
    }
}
```

Safari gained WebGL 2.0 in version 15 (September 2021). A visitor claiming Safari < 15 with WebGL 2.0 available is lying about their Safari version. The `| 0` converts the regex capture to an integer.

### Connection API Cross-Reference

```javascript
if ((c.effectiveType || '') === '4g' && (c.downlink || 0) > 5 && !(c.rtt > 0)) {
    flags += ... + 'connection-missing-rtt'; anomalyScore += 5;
}
```

A real 4G connection with 5+ Mbps downlink always has measurable RTT. Missing RTT means the connection API is partially spoofed or the browser is on localhost (which would also show a different `effectiveType`).
