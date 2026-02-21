---
description: 'JavaScript conventions for the PiXL Script — browser-side data collection embedded in C# template (PiXLScript.cs)'
applyTo: '**/PiXLScript.cs'
---

# JavaScript Conventions — PiXL Script

## Context

The PiXL Script is ~1155 lines of JavaScript embedded inside `SmartPiXL/Scripts/PiXLScript.cs` as a C# string template. It runs in the visitor's browser, collects 159 data fields via browser APIs, and fires the data as a `_SMART.GIF` image request. The visitor never sees or feels any of this.

## Architecture Constraints

- **Self-contained**: zero external dependencies, no CDN imports, no module system
- **Fire-and-forget**: collect data → fire GIF → done. No response data flows back.
- **500ms ceiling**: `Promise.race` between async probes and safety timeout
- **Invisible**: no UI modifications, no console output, no user-visible effects
- **C# host**: JavaScript lives inside a C# string — escaping rules apply

## Performance Rules

- Minimize DOM queries — cache results of `document.querySelector`, `window.screen`, etc.
- Prefer array `.join()` over string concatenation for building large strings
- Avoid unnecessary `try/catch` around operations that can't throw
- No `setTimeout` or `setInterval` outside the 500ms window mechanism
- No synchronous `XMLHttpRequest` — the GIF `Image()` pattern is intentional
- No `eval()`, `Function()`, or dynamic code execution

## Data Field Rules

- Every field is assigned as `data.{fieldName} = value`
- Field names must match the design doc inventory exactly (§3.5 of BRILLIANT-PIXL-DESIGN.md)
- Composite fields (`botSignals`, `crossSignals`, `evasionSignalsV2`, `stealthSignals`) use comma-separated values internally
- Pipe `|` is used as a delimiter between structured items (voices, mousePath points, WebGL params)
- All values must be URL-safe or properly encoded before inclusion in the GIF query string

## Browser API Usage

### Feature Detection Required
```javascript
// CORRECT — check before use
if (navigator.connection) {
    data.conn = navigator.connection.effectiveType;
}

// WRONG — will throw in Firefox/Safari
data.conn = navigator.connection.effectiveType;
```

### Async Probes
- All async operations (`navigator.storage.estimate()`, `navigator.getBattery()`, etc.) must be wrapped in the `Promise.race` mechanism
- Individual async failures must not prevent the GIF from firing
- Use `Promise.allSettled` or individual `.catch()` handlers, never bare `await`

### Fingerprinting APIs
- Canvas: draw identical content, hash output. The drawing operations are deterministic by design.
- WebGL: read parameters and extensions. `WEBGL_debug_renderer_info` may not be available (report as empty, don't throw).
- Audio: `OfflineAudioContext` with specific parameters. Run twice for consistency check.
- Fonts: width measurement against monospace baseline. The 30-font list is fixed.

## Security Concerns

- **No PII collection** from DOM content — only technical fingerprint data and behavioral signals
- **Email** comes from the page's form submission or custom client params (`_cp_*`), not from scraping
- **Local IP** via WebRTC ICE candidates — privacy-sensitive, may be restricted by browsers in future
- **Query string construction** must properly encode all values to prevent injection

## C# Template Rules

Since the JavaScript is inside a C# string literal:

```csharp
// Inside PiXLScript.cs:
// Double braces {{ }} for literal braces in JavaScript
// {0}, {1} etc. for C# string.Format placeholders
// Backslashes must be doubled: \\ for JS \
// Quotes must be escaped: \" for JS "
```

- **Placeholders**: `{0}` = company ID, `{1}` = pixel ID (injected at serve time)
- **Testing**: `PiXLScriptTests.cs` verifies the generated JS output — always run after changes
- **Output verification**: `dotnet test SmartPiXL.Tests/ --filter PiXLScript`

## Signal Scoring

Bot signals have weights defined in the script. When modifying signal detection:
- Positive weight = more bot-like
- Accumulate into `botScore` field
- Never remove a signal without updating the design doc's signal count
- Cross-reference with `evasionSignalsV2` for evasion detection signals

## What NOT to Change Without Owner Approval

- The 30-font detection list
- The 50-event mouse tracking cap
- The 500ms timeout ceiling
- Canvas/Audio/WebGL fingerprinting algorithms
- The GIF fire-and-forget pattern
- `Promise.race` timing mechanism
- Any of the 159 `data.*` field names or semantics
