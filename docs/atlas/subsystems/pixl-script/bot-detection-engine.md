---
subsystem: pixl-script
title: "PiXL Script: Bot Detection Engine"
version: 1.0
last_updated: 2026-02-21
status: current
parent: subsystems/pixl-script
related:
  - subsystems/bot-detection
  - subsystems/pixl-script/evasion-detection
  - subsystems/pixl-script/cross-signal-analysis
  - subsystems/pixl-script/behavioral-analysis
---

# PiXL Script: Bot Detection Engine

## Atlas Public

### Separating Real Visitors from Bots

Industry research estimates that **30-50% of all web traffic is automated** — bots, scrapers, credential stuffers, and competitor monitoring tools. If you're making business decisions based on website analytics, you need to know which visitors are real.

SmartPiXL runs **30+ individual bot checks** the instant a page loads. Each check looks for a specific telltale sign of automation. Think of it like airport security — no single check catches everyone, but the combination of ID check, metal detector, baggage scan, and behavioral observation catches virtually all threats.

```
  ┌──────────────── BOT DETECTION ────────────────┐
  │                                                │
  │  CHECK 1: Is this a known automation tool?     │
  │     └─ Selenium? Puppeteer? PhantomJS?         │
  │                                                │
  │  CHECK 2: Is the browser lying about itself?   │
  │     └─ Claims to be Chrome but missing parts   │
  │                                                │
  │  CHECK 3: Has someone tampered with the code?  │
  │     └─ Native functions overridden?            │
  │                                                │
  │  CHECK 4: Does the "device" make sense?        │
  │     └─ Zero screen size? Fake memory values?   │
  │                                                │
  │           ┌──────────────┐                     │
  │           │  BOT SCORE   │                     │
  │           │    0 - 100+  │                     │
  │           └──────────────┘                     │
  │                                                │
  │  0 = Clean    15 = Suspicious    50+ = Bot     │
  │                                                │
  └────────────────────────────────────────────────┘
```

Each check contributes a **weighted score**. Minor anomalies (like a slightly unusual browser configuration) add 1-3 points. Major red flags (like automation tool markers present) add 10-20 points. The total is your visitor's **bot score**.

---

## Atlas Internal

### How Bot Scoring Works

Every bot signal has a **weight** — the number of points it contributes to the bot score. Higher weight = stronger evidence of automation. The weights are calibrated based on false positive risk and detection confidence.

**Score interpretation for support/account managers:**

| Score | Classification | What to Tell the Customer |
|-------|---------------|--------------------------|
| **0** | Clean | No bot indicators detected. This is a genuine visitor. |
| **1-10** | Low noise | Minor anomalies. Usually legitimate — some browsers just have quirks. |
| **10-20** | Moderate | Worth investigating. Could be a bot with basic disguise, or a misconfigured browser. |
| **20-40** | High suspicion | Multiple bot indicators. Very likely automated. |
| **40+** | Confirmed bot | Overwhelming evidence. Selenium, Puppeteer, or similar tool. |
| **80+** | Blatant | Not even trying to hide. Multiple automation frameworks detected simultaneously. |

### The 30+ Signals Explained in Plain Language

#### Automation Tool Detection (The "Are You a Robot?" Checks)

| Signal Name | Points | What It Means in Plain English |
|-------------|--------|-------------------------------|
| `webdriver` | +10 | The browser itself admits it's being controlled by automation software |
| `selenium` | +10 | Selenium testing framework left its fingerprints in the page |
| `phantomjs` | +10 | PhantomJS (old headless browser) is running |
| `nightmare` | +10 | NightmareJS automation tool detected |
| `playwright-global` | +10 | Microsoft Playwright testing tool detected |
| `cdp` | +10 | Chrome DevTools Protocol control variables found |
| `dom-automation` | +10 | Chrome's DOM automation controller is active |
| `headless-ua` | +10 | The browser identifies itself as "HeadlessChrome" |

#### "Your Browser Is Lying" Checks

| Signal Name | Points | What It Means in Plain English |
|-------------|--------|-------------------------------|
| `headless-no-chrome-obj` | +8 | Claims to be Chrome but missing `window.chrome` — a hallmark of headless Chrome |
| `fake-ua` | +20 | User-agent string is literally "bot", "desktop", or "crawler" |
| `minimal-ua` | +15 | User-agent string is suspiciously short (< 30 characters) |
| `chrome-no-runtime` | +1 | Has `window.chrome` but not `window.chrome.runtime` — could be headless |
| `no-plugins` | +2 | No browser plugins in a non-Firefox browser (Firefox legitimately has none) |

#### Function Tampering Checks

| Signal Name | Points | What It Means in Plain English |
|-------------|--------|-------------------------------|
| `fn-tampered` | +5 | A browser function was replaced with a fake version |
| `eval-tampered` | +5 | The `eval()` function has been overridden |
| `cross-realm-toString` | +12 | A stealth plugin has modified how functions describe themselves |
| `getter-name-mismatch` | +6 each | Browser properties have been overridden with incorrectly named fakes |
| `getter-has-prototype` | +8 each | Browser properties have been replaced with fake functions that have telltale extra features |
| `webdriver-getter-override` | +8 | Someone tried to hide `navigator.webdriver` by replacing the property |

#### Environment Anomaly Checks

| Signal Name | Points | What It Means in Plain English |
|-------------|--------|-------------------------------|
| `empty-languages` | +5 | Browser has no language preferences set — real browsers always have at least one |
| `zero-screen` | +8 | Screen width or height is zero — only happens in headless environments |
| `outer-zero` | +5 | Window outer dimensions are zero but inner dimensions exist — headless browser |
| `plugin-mime-mismatch` | +3 | No plugins but MIME types exist — inconsistent browser state |
| `default-viewport` | +2 | Browser window is exactly 1280×720 or 800×600 — common headless defaults |
| `fullscreen-match` | +2 | Screen size exactly matches window size and no taskbar — unusual for real desktops |
| `no-connection-api` | +3 | Claims to be Chrome but missing the connection API — headless doesn't have it |

#### Heap Memory Spoofing

| Signal Name | Points | What It Means in Plain English |
|-------------|--------|-------------------------------|
| `heap-size-spoofed` | +8 | Memory values are suspiciously round numbers (e.g., exactly 10,000,000) |
| `heap-total-equals-used` | +5 | Total memory equals used memory — only happens in constrained/fake environments |

#### Behavioral/Contextual

| Signal Name | Points | What It Means in Plain English |
|-------------|--------|-------------------------------|
| `perm-inconsistent` | flagged | Notification permission says "denied" at OS level but "default" in browser — impossible naturally |
| `nav-*` (dynamic) | +10 each | Found automation keywords (webdriver, selenium, puppeteer, playwright) as properties on the navigator object |

### Composite Field: `botSignals`

All detected signals are packed into a single comma-separated string:

```
botSignals=webdriver,selenium,empty-languages,heap-size-spoofed
botScore=33
```

This allows:
- **Dashboard filtering**: Show only visits with botScore > X
- **Customer reports**: "X% of your traffic showed bot indicators"
- **Forensics**: Drill into exactly which signals triggered

---

## Atlas Technical

### Signal Detection Implementation

The bot detection engine (PiXLScript.cs lines 483-713) is a single IIFE that accumulates signals and scores:

```javascript
var botSignals = (function() {
    var signals = [];
    var score = 0;
    
    // Each check pushes to signals[] and adds to score
    if (data.webdr) { signals.push('webdriver'); score += 10; }
    // ... 30+ more checks ...
    
    return { signals: signals.join(','), score: score };
})();

data.botSignals = botSignals.signals;
data.botScore = botSignals.score;
```

### Automation Framework Detection

**WebDriver flag**: `navigator.webdriver` is set to `true` by Selenium, Puppeteer, Playwright, and ChromeDriver. This is the simplest and most reliable check.

**DOM markers** — automation frameworks inject global objects:

| Global | Framework |
|--------|-----------|
| `window._phantom`, `window.phantom`, `window.callPhantom` | PhantomJS |
| `window.__nightmare` | NightmareJS |
| `window.__playwright`, `window.__pw_manual` | Playwright |
| `window.domAutomation`, `window.domAutomationController` | Chrome DevTools |
| `document.__selenium_unwrapped`, `document.__webdriver_evaluate`, `document.__driver_evaluate`, `document.__webdriver_unwrapped`, `document.__fxdriver_evaluate`, `document.__driver_unwrapped` | Selenium (various WebDriver bindings) |

**CDP (Chrome DevTools Protocol)**:
```javascript
window.cdc_adoQpoasnfa76pfcZLmcfl_Array
window.cdc_adoQpoasnfa76pfcZLmcfl_Promise
window.cdc_adoQpoasnfa76pfcZLmcfl_Symbol
```
These are ChromeDriver-injected globals with a characteristic `cdc_` prefix.

**Navigator property enumeration**:
```javascript
for (var key in navigator) {
    if (/webdriver|selenium|puppeteer|playwright/i.test(key)) {
        signals.push('nav-' + key);
        score += 10;
    }
}
```
Some frameworks add custom navigator properties. This catch-all scan finds them.

### Function Integrity Checks

**Native function verification** pattern:
```javascript
var fnStr = Function.prototype.toString.call(target);
if (fnStr.indexOf('[native code]') === -1) {
    // Function has been replaced with a JavaScript implementation
}
```

Applied to:
- `permissions.query` → `fn-tampered` (+5)
- `eval` → `eval-tampered` (+5)

**Cross-realm toString** (most sophisticated check):
```javascript
var iframe = document.createElement('iframe');
iframe.style.display = 'none';
document.body.appendChild(iframe);
var pristineToString = iframe.contentWindow.Function.prototype.toString;
var currentToString = Function.prototype.toString;
if (pristineToString.call(currentToString).indexOf('[native code]') === -1) {
    signals.push('cross-realm-toString');  // +12
}
```

This creates a fresh JavaScript realm (via iframe) and uses its untouched `toString` to inspect the main frame's `toString`. If the main frame's `toString` has been replaced (to make spoofed functions look native), this catches it. This defeats popular stealth plugins like `puppeteer-extra-plugin-stealth`.

**Getter property validation** (two checks per property):

1. **Name check**: `Object.getOwnPropertyDescriptor(Navigator.prototype, prop).get.name` should be `'get ' + prop`. If it's `''` or something else, the getter was replaced with a plain function.

2. **Prototype check**: Native getters don't have `.prototype` (they're not constructors). `getter.hasOwnProperty('prototype')` being `true` means the getter is a regular function masquerading as a native getter.

Properties checked: `webdriver`, `hardwareConcurrency`, `platform`, `languages`, `deviceMemory`, `vendor` (name check) and `webdriver`, `platform`, `vendor`, `hardwareConcurrency`, `deviceMemory`, `pdfViewerEnabled` (prototype check).

### Permission Inconsistency Check

```javascript
navigator.permissions.query({name: 'notifications'}).then(function(p) {
    if (p.state === 'denied' && Notification.permission === 'default') {
        signals.push('perm-inconsistent');
    }
});
```

In a real browser, `Notification.permission` and `permissions.query({name: 'notifications'})` agree. In headless Chrome with default settings, the permissions API returns `denied` but the Notification constructor returns `default`. This async check sets `botPermInconsistent = 1` if triggered.

### Score Calibration Philosophy

Weights follow these principles:

| Weight | Criteria | Examples |
|--------|----------|---------|
| **1-3** | Legitimate explanation exists | Default viewport, no plugins, fullscreen match |
| **5** | Unusual but not conclusive | Function tampering, empty languages |
| **8-10** | Strong indicator, minimal false positives | WebDriver flag, automation globals |
| **12-20** | Near-certain automation evidence | Cross-realm toString, fake UA, getter prototype |

The `combinedThreatScore` (bot + anomaly) is capped: `botScore + min(anomalyScore, 25)`. The anomaly cap prevents cross-signal checks from overwhelming the bot score — a perfectly clean bot with spoofed hardware could otherwise score 80+ on anomaly alone without any actual bot signals.

---

## Atlas Private

### Implementation Notes

**Signal detection order matters.** The script checks `navigator.webdriver` first because it's the cheapest check (single property access). Expensive checks (cross-realm toString, getter enumeration) come later. If a simple check already scores 40+, the expensive checks still run — we want the full signal picture for forensics, even if it's obviously a bot.

**The `for...in` navigator scan** (lines 560-568) is wrapped in a try-catch because Proxy-wrapped navigators can throw on enumeration. If it throws, we silently skip it — the other 29 checks provide coverage. The comment in code explains: `Proxy enumeration blocked - non-critical, other checks cover this`.

**Heap spoofing detection** (lines 700-713):

```javascript
if (_ht === 10000000 || _hu === 10000000 ||
    (_ht > 0 && _ht % 1000000 === 0) ||
    (_hu > 0 && _hu % 1000000 === 0)) {
    signals.push('heap-size-spoofed');
    score += 8;
}
```

Real `performance.memory` values look like `4294705152`, `14234567`, `8765432` — irregular numbers. Anti-detect browsers often set them to round numbers like `10000000` or `2000000000`. The modulo check catches this.

**`totalJSHeapSize === usedJSHeapSize`** is checked because in a minimal headless environment, Chrome may report total = used (no free heap). In a real browser with extensions, tabs, and DevTools, there's always a gap.

### Scoring Boundary Decisions

Some signals have deliberately low weights despite being useful:

- **`chrome-no-runtime` (+1)**: In Chrome extensions, `window.chrome.runtime` exists. In regular tabs, it may not. Too many false positives for a higher weight.
- **`default-viewport` (+2)**: Many legitimate corporate setups use 1280×720 monitors. Can't penalize too hard.
- **`fullscreen-match` (+2)**: Some users genuinely run maximized with auto-hidden taskbars.
- **`no-plugins` (+2)**: Modern Chrome shows 5 plugins. Firefox shows 0 (legitimate). Only penalized in non-Firefox.

These low-weight signals are still valuable in aggregate — a bot that triggers 5 of them scores 10, which is enough to flag for review.

### Known Evasion: Puppeteer Stealth

`puppeteer-extra-plugin-stealth` patches:
- `navigator.webdriver` → returns `false` via getter override. **Caught by:** `webdriver-getter-override` (+8), getter name/prototype checks (+6-8 per property).
- `Function.prototype.toString` → returns `'function () { [native code] }'` for spoofed functions. **Caught by:** `cross-realm-toString` (+12) using the iframe's pristine toString.
- `chrome.runtime` → injected fake object. **Caught by:** inconsistency with other Chrome-specific checks if not done carefully.

A visitor running Puppeteer stealth with default settings typically scores **25-45** depending on how many getter properties they override. The cross-realm toString check alone is worth +12 and is the hardest to defeat (requires patching the iframe's prototype chain too, which most stealth plugins don't do).

### Future Signal Considerations

Signals NOT currently implemented but on the radar:
- **`navigator.scheduling`** — Chrome-only API, absent in headless. Low priority (Chrome team may remove it).
- **`CSS.supports()` fingerprinting** — different browsers support different CSS features. Would add 2-3 dimensions but not worth the complexity yet.
- **`performance.measureUserAgentSpecificMemory()`** — cross-origin isolated contexts only. Not viable for our use case.
