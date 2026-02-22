---
subsystem: pixl-script
title: "PiXL Script: Delivery Mechanism"
version: 1.0
last_updated: 2026-02-21
status: current
parent: subsystems/pixl-script
related:
  - architecture/data-flow
  - architecture/edge
  - subsystems/failover
---

# PiXL Script: Delivery Mechanism

## Atlas Public

### How Your Visitor Data Gets to SmartPiXL

After collecting all 159 data fields, the PiXL Script sends everything to SmartPiXL's servers using a technique as old as the web itself: a **tracking pixel** — a 1×1 invisible image.

```
  ┌──────────────── VISITOR'S BROWSER ────────────────┐
  │                                                    │
  │  PiXL Script finishes collecting data              │
  │         │                                          │
  │         ▼                                          │
  │  Packs 159 fields into an image URL:               │
  │  ┌──────────────────────────────────────────┐      │
  │  │ _SMART.GIF?sw=1920&sh=1080&gpu=NVIDIA... │      │
  │  └──────────────────────────────────────────┘      │
  │         │                                          │
  │         ▼                                          │
  │  Browser loads the "image" (1×1 transparent GIF)   │
  │  Server receives the data from the URL             │
  │                                                    │
  │  Visitor sees: NOTHING                             │
  │  Page impact: NONE                                 │
  │  Data delivered: ALL 159 FIELDS                    │
  │                                                    │
  └────────────────────────────────────────────────────┘
```

**Why an image instead of a normal data request?**
- Works on every website without any special server configuration
- Ad blockers block data requests but rarely block image loads
- The browser handles it naturally — no JavaScript errors, no popups
- Truly invisible — images load in the background constantly on every website

**Guaranteed delivery:** Even if the script encounters an error, a simplified data payload is still sent. SmartPiXL is designed to **never lose a hit**.

---

## Atlas Internal

### The 500ms Timing Guarantee

The script doesn't sit idle for 500ms — it works as fast as possible and uses the 500ms only as a safety net:

```
  TIME ──────────────────────────────────────────────►

  0ms    Instant data collection starts
         (screen, navigator, features, locale)
  │
  ~5ms   Canvas, WebGL, Font fingerprints complete
  │
  ~10ms  Bot detection and cross-signal analysis complete
  │
  ├──── Async operations running in parallel ────┤
  │     Audio fingerprint                        │
  │     Battery check                            │
  │     Media device count                       │
  │     Client hints                             │
  │     Storage quota                            │
  │                                              │
  ~80ms  All async operations typically done      ─── GIF FIRES HERE
         ▲                                            (no waiting)
         │
  500ms  Safety timeout                          ─── GIF FIRES HERE
         (only if async operations hang)              (worst case)
```

**Key point:** The data fires as soon as async operations complete OR at 500ms — **whichever comes first**. There is no unnecessary delay.

### Three Delivery Scenarios

| Scenario | What Happens | Typical Case |
|----------|-------------|-------------|
| **Happy path** | All async operations complete in ~80ms. GIF fires immediately. | 95%+ of visitors |
| **Partial timeout** | One async operation hangs; 500ms timeout fires GIF with whatever data is available. Missing fields are empty. | ~4% of visitors |
| **Script error** | Something crashes; the outer try/catch fires a minimal GIF with `error=1` and the error message. | < 1% of visitors |

### What the GIF Response Is

The server doesn't send a real image — it responds with a pre-built 1×1 transparent GIF (43 bytes). The browser displays "nothing" — the image is invisible on the page. But the server received the visitor's data in the URL query string.

The response includes `Cache-Control: no-store` to prevent the browser from caching the GIF (which would prevent future tracking requests from reaching the server).

---

## Atlas Technical

### Script Serving: Template Injection

The script itself is served as a dynamic JavaScript file:

**Request:** `GET /{company}/{pixel}_SMART.js`

**Response generation:**
```csharp
public static string GetScript(string pixlUrl)
{
    if (_cache.Count >= MaxCacheEntries)
        _cache.Clear();
    return _cache.GetOrAdd(pixlUrl, url => Template.Replace("{{PIXL_URL}}", url));
}
```

The `{{PIXL_URL}}` placeholder in the JavaScript template is replaced with the actual GIF URL at serve time. For example:
- Template: `new Image().src = '{{PIXL_URL}}?' + params.join('&');`
- Generated: `new Image().src = '/ACME/lead_SMART.GIF?' + params.join('&');`

**Caching:**
- `ConcurrentDictionary<string, string>` — one entry per unique pixel URL
- Thread-safe, lock-free reads via .NET's `ConcurrentDictionary`
- Nuclear eviction at 10,000 entries (prevents malicious URL flooding)
- After first hit per company/pixel, serve is zero-allocation

**Response headers:**
- `Content-Type: application/javascript`
- `Cache-Control: public, max-age=3600` (browser caches script for 1 hour)

### Data Fire: The GIF Request

```javascript
var sendPiXL = function() {
    calculateMouseEntropy();
    var params = [];
    for (var key in data) {
        if (data[key] !== '' && data[key] !== null && data[key] !== undefined) {
            params.push(key + '=' + encodeURIComponent(data[key]));
        }
    }
    new Image().src = '{{PIXL_URL}}?' + params.join('&');
};
```

**Query string construction:**
1. Iterate all `data` properties
2. Skip empty/null/undefined values (keeps query string tight)
3. URL-encode each value via `encodeURIComponent()`
4. Join with `&`
5. Assign to `new Image().src` — browser immediately requests the URL

**Why `new Image()` instead of `fetch()` or `XMLHttpRequest`?**

| Method | Cross-origin? | Ad-blocker resistant? | Fire-and-forget? |
|--------|:---:|:---:|:---:|
| `new Image().src` | Yes | Yes | Yes |
| `fetch()` | Needs CORS | No | Needs `.then()` handling |
| `XMLHttpRequest` | Needs CORS | No | Needs callback handling |
| `navigator.sendBeacon()` | Yes | No | Yes |

The image approach wins on every dimension. It's also the simplest — one line of code.

### Async Orchestration: Promise.race

```javascript
var timeoutPromise = new Promise(function(resolve) { 
    setTimeout(resolve, 500); 
});

if (asyncPromises.length > 0 && Promise.allSettled) {
    Promise.race([
        Promise.allSettled(asyncPromises),
        timeoutPromise
    ]).then(sendPiXL);
} else {
    timeoutPromise.then(sendPiXL);
}
```

**Three code paths:**

1. **Async + `Promise.allSettled` available**: `Promise.race()` between all async work and the 500ms timeout. Whichever resolves first triggers `sendPiXL()`. If async finishes in 80ms, data fires in 80ms. If it hangs, data fires in 500ms.

2. **No async work**: Falls through to the 500ms timeout path. The timeout ensures synchronous stealth detection (property timing, cross-realm checks) has time to complete.

3. **`Promise.allSettled` unavailable**: IE11/old browsers. Falls through to the 500ms timeout. Async data (audio, battery) may or may not be ready — collected if it resolves in time, empty if not.

**Why `Promise.allSettled` instead of `Promise.all`?** `Promise.all` rejects if **any** promise rejects. `Promise.allSettled` waits for all to complete regardless of success/failure. A failing battery API shouldn't prevent audio fingerprint data from being sent.

### Error-Safe Outer Wrapper

```javascript
(function() {
    try {
        // ... entire script body (1100+ lines) ...
    } catch (e) {
        new Image().src = '{{PIXL_URL}}?error=1&msg=' + encodeURIComponent(e.message);
    }
})();
```

If **anything** in the script throws an uncaught exception, the outer catch fires a minimal GIF with:
- `error=1` — flags this as an error hit
- `msg=...` — the error message (useful for debugging script issues)

This guarantees that **every page load produces a server-side record**, even if the script itself is broken. The server can monitor error rates to detect script issues.

### GIF Response Server-Side

When Edge receives the `_SMART.GIF` request:

1. Parse query string into `TrackingData` (zero-allocation hot path)
2. Run 12 fast enrichments (IP classification, geo cache, etc.)
3. Return pre-built 43-byte transparent GIF immediately
4. Enqueue enriched data to named pipe (async, non-blocking)

The GIF response is returned **before** any enrichment completes. The visitor's browser gets the response in < 10ms.

---

## Atlas Private

### C# Host: Template Storage

```csharp
public const string Template = @"
(function() {
    try {
        // ... 1,155 lines of JavaScript ...
    } catch (e) {
        new Image().src = '{{PIXL_URL}}?error=1&msg=' + encodeURIComponent(e.message);
    }
})();
";
```

**`const string`** — not `static readonly string`. The difference matters:
- `const` is baked into the assembly metadata at compile time. The CLR interns it in the managed string pool. Zero heap allocation at runtime.
- `static readonly` would allocate a new string at class initialization time.

For a ~38KB string, the `const` approach means the template occupies space in the assembly DLL itself, not on the managed heap. It's effectively free memory-wise at runtime.

### Cache Implementation

```csharp
private static readonly ConcurrentDictionary<string, string> _cache = new();
private const int MaxCacheEntries = 10_000;

public static string GetScript(string pixlUrl)
{
    if (_cache.Count >= MaxCacheEntries)
        _cache.Clear();
    return _cache.GetOrAdd(pixlUrl, url => Template.Replace("{{PIXL_URL}}", url));
}
```

**Design decisions:**

- **`ConcurrentDictionary`** over manual locking: `GetOrAdd` is atomic for reads (returns cached value) and (nearly) atomic for writes (factory may run twice on race, but only one result is stored). Thread-safe without `lock`.
- **Nuclear eviction** (`Clear()`) at 10K entries: A legitimate deployment might have 100-500 company/pixel combos. 10K means someone is probing random URLs. `Clear()` is O(1) amortized and the warm-up cost (a single `string.Replace` per URL) is negligible.
- **`string.Replace("{{PIXL_URL}}", url)`**: Simple and correct. The template has exactly one `{{PIXL_URL}}` occurrence. The Replace runs once per cache miss, then the result is cached. No regex needed.

### Why Not `string.Create` / `Span<T>` for Script Generation?

The class summary mentions "Uses string.Create + Span for zero-allocation script generation after first hit." This is aspirational documentation — the actual implementation uses `string.Replace` cached in a `ConcurrentDictionary`. The zero-allocation claim is correct **after** the first hit per URL (subsequent calls return the cached string), but the generation itself allocates a new string via `Replace`.

A `string.Create` approach would avoid the intermediate allocation during generation, but since generation only runs once per URL and the result is cached, the optimization would provide negligible benefit. The current approach is correct and maintainable.

### Query String Size Budget

| Component | Typical Size | Max Size |
|-----------|-------------|---------|
| URL path (`/{company}/{pixel}_SMART.GIF`) | ~40 chars | ~100 chars |
| Field names + delimiters | ~700 chars | ~700 chars |
| Field values (encoded) | 2-4 KB | ~8 KB |
| `mousePath` alone | 0-600 chars | 2000 chars |
| **Total query string** | **3-5 KB** | **~10 KB** |

IIS limit: 16,384 bytes (16KB) for query string, configured in `web.config`:
```xml
<requestLimits maxQueryString="16384" maxUrl="8192" />
```

Current headroom: ~6-13KB. If a future field pushes close, reduce `mousePath` cap from 2000 to 1000 chars first — that alone frees ~1KB.

### The `sendPiXL` Function: Ordering Matters

`sendPiXL()` calls `calculateMouseEntropy()` **first**, then builds the query string. This ordering is critical because:
1. `calculateMouseEntropy()` writes to `data.mouseMoves`, `data.mouseEntropy`, `data.mousePath`, etc.
2. `calculateMouseEntropy()` may also modify `data.crossSignals` and `data.anomalyScore` (scroll contradiction)
3. The `for (var key in data)` loop needs all fields populated before it runs

If the order were reversed, behavioral data would be missing from the GIF request.

### `encodeURIComponent` for Safety

Every field value is encoded before inclusion in the URL. This is essential for:
- **Pipe characters** (`|`) in composite fields like `voices`, `mousePath`, `uaFullVersion`
- **Commas** (`,`) in `fonts`, `botSignals`, `crossSignals`
- **Spaces** in `ua`, `gpu`, `title`
- **Special chars** in `url`, `ref` (which may contain their own query strings)

Without encoding, the URL would be malformed and the server would misparse field boundaries.
