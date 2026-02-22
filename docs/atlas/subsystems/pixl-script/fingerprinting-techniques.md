---
subsystem: pixl-script
title: "PiXL Script: Fingerprinting Techniques"
version: 1.0
last_updated: 2026-02-21
status: current
parent: subsystems/pixl-script
related:
  - subsystems/fingerprinting
  - subsystems/pixl-script/evasion-detection
  - subsystems/identity-resolution
---

# PiXL Script: Fingerprinting Techniques

## Atlas Public

### How SmartPiXL Recognizes Returning Visitors — Without Cookies

Cookies are fragile. Users clear them, browsers expire them, privacy tools block them. SmartPiXL uses a fundamentally different approach: **device fingerprinting** — recognizing the unique combination of hardware and software characteristics that make every device slightly different.

```
  ┌──────────────── YOUR DEVICE ────────────────┐
  │                                              │
  │  Screen: 2560×1440, 2 monitors               │
  │  GPU: NVIDIA RTX 4090                        │
  │  Fonts: Arial, Calibri, Segoe UI, ...        │
  │  Audio stack: unique processing signature     │
  │  Browser: Chrome 120 on Windows 11           │
  │                                              │
  │  Combined = Device Fingerprint: a3f9c7e      │
  │  (like a car's VIN — unique to this device)  │
  │                                              │
  └──────────────────────────────────────────────┘
```

**The analogy:** Imagine you walk into a hotel without ID. The front desk can still recognize you by your combination of height, hair color, glasses, watch, shoes, and accent. No single trait is unique, but the combination is. That's device fingerfingerprinting.

SmartPiXL generates **seven distinct fingerprints** from different device characteristics and combines them:

| Fingerprint | Analogy | What Makes It Unique |
|-------------|---------|---------------------|
| Canvas | Signature on a greeting card | Same pen, same hand, but every person's is slightly different |
| WebGL (GPU) | Engine serial number | Every GPU model has different capabilities and limits |
| Audio | Voice print | Same song, different speakers produce subtly different sound |
| Fonts | Bookshelf contents | The specific set of fonts installed varies by OS and user |
| Math | Calculator quirks | Different calculators display slightly different decimal places |
| CSS Rendering | Handwriting style | How the browser renders font details varies by platform |
| Error Messages | Accent when confused | Different browsers describe errors in different words |

**Together, these fingerprints identify your device across visits — even in incognito mode, even after clearing cookies.**

---

## Atlas Internal

### The Seven Fingerprints Explained

#### 1. Canvas Fingerprint — "Draw Something, We'll Recognize Your Style"

The script asks the browser to draw specific text and shapes on a hidden canvas (an invisible drawing surface). Different combinations of GPU, operating system, font rendering engine, and anti-aliasing produce subtly different results — even when drawing the exact same content.

```
  What we draw:                    What we compare:
  ┌───────────────────┐
  │ ██████████████    │            Browser A: hash = "a3f9c7e"
  │ SmartPiXL <canvas>│            Browser B: hash = "b8d2e1a"
  │                   │            Same content, different rendering
  │ Fingerprint!      │            = different device recognized
  │        ○          │
  └───────────────────┘
```

We hash the result into a compact code. The visitor never sees the canvas — it exists only in memory.

**Tamper detection:** We draw the same content on two separate canvases. If the results differ, a privacy tool is injecting random noise (like Brave browser does). We detect this and flag it.

#### 2. WebGL Fingerprint — "What Can Your GPU Do?"

WebGL is the browser's 3D graphics API. Every GPU has different limits — maximum texture sizes, buffer capacities, color bit depths. We query 23 of these parameters and combine them with the GPU's name.

| Parameter | What It Reveals | Why It Varies |
|-----------|----------------|---------------|
| GPU renderer | `NVIDIA GeForce RTX 4090` | Different hardware |
| Max texture size | `16384` vs `8192` | GPU generation/model |
| Max viewport dimensions | `32767,32767` | Driver implementation |
| Extension count | `38` vs `25` | Browser + GPU combo |

The unmasked GPU name is the single most valuable identification signal — there are thousands of distinct GPU models.

#### 3. Audio Fingerprint — "How Does Your Device Process Sound?"

The script creates a tiny audio processing pipeline (a triangle wave through a compressor) and measures the output. Different audio stacks (Windows DirectSound, macOS CoreAudio, Linux ALSA) produce measurably different results.

**Tamper detection:** We run the audio fingerprint **twice**. If the results differ, a privacy tool is injecting audio noise. Real audio processing always produces the same output for the same input.

#### 4. Font Detection — "What Fonts Are Installed?"

The script tests 30 common fonts by measuring the width of text rendered in each one versus a baseline font. If the width changes, the font is installed.

**30 fonts tested:** Arial, Arial Black, Verdana, Times New Roman, Courier New, Georgia, Comic Sans MS, Impact, Trebuchet MS, Tahoma, Segoe UI, Calibri, Consolas, Helvetica, Monaco, Roboto, Open Sans, Lato, Montserrat, Source Sans Pro, Century Gothic, Futura, Gill Sans, Lucida Grande, Garamond, MS Gothic, SimSun, Microsoft YaHei, Apple Color Emoji, Segoe UI Emoji

Windows machines typically have Segoe UI, Calibri, Consolas. Macs have Monaco, Lucida Grande, Apple Color Emoji. This alone helps identify the operating system — and the specific combination narrows to device families.

#### 5. Math Fingerprint — "How Does Your Calculator Round?"

JavaScript engines (V8 in Chrome, SpiderMonkey in Firefox, JavaScriptCore in Safari) implement math functions with slightly different floating-point precision. We evaluate 8 math functions and capture the first 10 digits of each result.

This fingerprint identifies the **browser engine family**, not individual devices — but it's very hard to spoof because it comes from compiled native code.

#### 6. CSS Font Variant Fingerprint — "How Does Your Browser Render Text Details?"

We read 10 CSS font rendering properties (ligatures, kerning, optical sizing, etc.) and measure the rendered width of a test string. Different operating systems have different font rendering engines, producing different default values.

#### 7. Error Fingerprint — "How Does Your Browser Describe Errors?"

We intentionally trigger an error (`null[0]()`) and measure the length of the error message and stack trace. Chrome says one thing, Firefox says another, Safari says something else entirely. Even the stack trace format differs.

### What Customers Should Know

- **No personal data** is used in fingerprinting — it's purely hardware/software characteristics
- **Device fingerprints are one-way hashes** — you can identify a returning device but cannot reverse-engineer what GPU or fonts it has from the hash
- **Cross-session persistence** — the fingerprint survives cookie clearing, incognito mode, and browser restarts
- **Cross-browser limitation** — the same device using Chrome and Firefox will produce different fingerprints (different rendering engines)

---

## Atlas Technical

### Canvas Fingerprinting Implementation

**Primary fingerprint** (PiXLScript.cs lines 56-95):

1. Create an off-screen 280×60 canvas
2. Draw a deterministic scene:
   - Orange filled rectangle (10,10,100,40)
   - Blue text "SmartPiXL \<canvas\> 1.0" in 15px Arial
   - Semi-transparent green text "Fingerprint!" in 18px Times New Roman
   - Stroked arc (center 80,30, radius 20, full circle)
3. Call `canvas.toDataURL()` → full base64 PNG
4. Hash the data URL via DJB2 → `canvasFP`

**Evasion detection** (pixel variance analysis):
- Sample 100 pixels at row 25 (center of the drawing)
- Calculate color variance: $\sigma^2 = \frac{1}{n}\sum_{i=1}^{n}(x_i - \bar{x})^2$
- If variance < 1 → uniform output → canvas reads are being spoofed → `canvasEvasion = 1`
- If `dataUrl.length < 1000` → suspiciously small → likely blocked → `canvasEvasion = 1`

**Consistency test** (PiXLScript.cs lines 97-115):
- Draw identical content on two 100×50 canvases
- Hash both → compare. Three outcomes:
  - Hashes differ → `noise-detected` (privacy tool adding random pixels per read)
  - Add new content to canvas 2, re-hash → if still matches canvas 1 → `canvas-blocked` (all reads return same spoofed data)
  - Otherwise → `clean`

### WebGL Fingerprinting Implementation

**Parameter extraction** (PiXLScript.cs lines 117-164):

23 parameters queried via `gl.getParameter()`:

| # | Parameter | Type |
|---|-----------|------|
| 1 | `VERSION` | string |
| 2 | `SHADING_LANGUAGE_VERSION` | string |
| 3 | `VENDOR` | string |
| 4 | `RENDERER` | string |
| 5 | `MAX_VERTEX_ATTRIBS` | int |
| 6 | `MAX_VERTEX_UNIFORM_VECTORS` | int |
| 7 | `MAX_VARYING_VECTORS` | int |
| 8 | `MAX_COMBINED_TEXTURE_IMAGE_UNITS` | int |
| 9 | `MAX_VERTEX_TEXTURE_IMAGE_UNITS` | int |
| 10 | `MAX_TEXTURE_IMAGE_UNITS` | int |
| 11 | `MAX_FRAGMENT_UNIFORM_VECTORS` | int |
| 12 | `MAX_CUBE_MAP_TEXTURE_SIZE` | int |
| 13 | `MAX_RENDERBUFFER_SIZE` | int |
| 14 | `MAX_VIEWPORT_DIMS` | int[2] |
| 15 | `MAX_TEXTURE_SIZE` | int |
| 16 | `ALIASED_LINE_WIDTH_RANGE` | float[2] |
| 17 | `ALIASED_POINT_SIZE_RANGE` | float[2] |
| 18-23 | `RED/GREEN/BLUE/ALPHA/DEPTH/STENCIL_BITS` | int |

All parameters joined with `|`, extensions joined with `,`, entire string hashed → `webglFP`.

**Unmasked GPU** via `WEBGL_debug_renderer_info` extension:
- `UNMASKED_RENDERER_WEBGL` → `gpu` (e.g., `ANGLE (NVIDIA, NVIDIA GeForce RTX 4090, ...)`)
- `UNMASKED_VENDOR_WEBGL` → `gpuVendor`

**Software renderer detection**: If GPU string contains `SwiftShader`, `llvmpipe`, `Mesa`, or `Disabled` → `webglEvasion = 1`. These indicate headless/VM environments.

### Audio Fingerprinting Implementation

**Pipeline** (PiXLScript.cs lines 166-200):

```
OfflineAudioContext(1 channel, 44100 samples, 44100 Hz)
    │
    ▼
OscillatorNode (triangle wave, 10000 Hz)
    │
    ▼
DynamicsCompressorNode
  threshold: -50, knee: 40, ratio: 12
  attack: 0, release: 0.25
    │
    ▼
ctx.destination → startRendering()
    │
    ▼
Sum of |channelData[4500..4999]| → audioFP (6 decimal places)
Hash of every 100th sample → audioHash
```

**Stability test**: Runs twice via `Promise.all([runAudioFP(), runAudioFP()])`:
- Same result → `audioStable = 1`
- Different result → `audioStable = 0`, `audioNoiseDetected = 1`

### Font Detection Implementation

**Method** (PiXLScript.cs lines 202-244):

1. Create test string `"mmmmmmmmmmlli"` (wide chars + narrow chars for maximum width variation)
2. Render at 72px in `monospace` baseline → measure width via two methods:
   - `span.offsetWidth` (integer pixel width)
   - `span.getBoundingClientRect().width` (sub-pixel floating-point width)
3. For each of 30 fonts: set `fontFamily` to `"FontName, monospace"` → if width differs from baseline, font is installed
4. If the two methods disagree on any font → `fontMethodMismatch = 1` (spoofing detected)

**Why two methods?** Some anti-fingerprinting tools intercept `offsetWidth` to return fake values, but forget to also intercept `getBoundingClientRect()`. The dual-measurement catches this.

### Math Fingerprint Implementation

Eight math functions evaluated, first 10 digits captured:

```javascript
[
    Math.tan(-1e300),    // ~5.9270...  (varies by engine)
    Math.sin(1),         // 0.841470... 
    Math.acos(0.5),      // 1.047197...
    Math.atan(2),        // 1.107148...
    Math.exp(1),         // 2.718281...
    Math.log(2),         // 0.693147...
    Math.sqrt(2),        // 1.414213...
    Math.pow(2, 53)      // 9007199254740992
]
```

Joined with `,` → `mathFP`. The key differentiator is `Math.tan(-1e300)` which produces meaningfully different results across V8 (Chrome), SpiderMonkey (Firefox), and JavaScriptCore (Safari).

### CSS Font Variant Fingerprint

10 computed CSS properties read from a test element:

1. `fontVariantLigatures`
2. `fontVariantCaps`
3. `fontVariantNumeric`
4. `fontVariantEastAsian`
5. `fontFeatureSettings`
6. `fontKerning`
7. `fontStretch`
8. `fontSizeAdjust`
9. `fontOpticalSizing`
10. `fontSynthesis`

Each value truncated to 4 chars, joined with `|`, plus `offsetWidth` appended. Output: `norm|norm|norm|norm|norm|auto|norm|_|auto|weig|347`

### Error Fingerprint

```javascript
try { null[0](); } catch(e) {
    return e.message.length + (e.stack ? e.stack.length : 0);
}
```

- Chrome: `"Cannot read properties of null (reading '0')"` → message.length ≈ 46
- Firefox: `"null has no properties"` → message.length ≈ 22
- Safari: `"null is not an object (evaluating 'null[0]')"` → message.length ≈ 46

Stack trace lengths vary even more dramatically between engines.

---

## Atlas Private

### Fingerprint Stability Analysis

**Most stable** (rarely changes for the same device):
- `webglFP` — GPU parameters change only with driver updates or GPU swap
- `gpu` / `gpuVendor` — hardware identity, stable until hardware change
- `mathFP` — changes only when browser engine updates its math implementation (rare)
- `errorFP` — changes only with major browser engine version changes

**Moderately stable** (changes with browser updates):
- `canvasFP` — can shift with browser update, OS update, or font installation
- `audioFP` — shifts with audio driver updates or major browser changes
- `fonts` — changes when user installs/removes fonts

**Least stable** (session-dependent):
- `voices` — can change based on installed language packs, loaded lazily by OS
- `cssFontVariant` — can change with CSS default changes in browser updates

### Hash Collisions

`hashStr()` produces 32-bit hashes (8 hex chars). Collision space: 2^32 ≈ 4.3 billion. For canvas fingerprinting alone, this is adequate — the input space (base64 PNG data URLs) is vast, but the distinct outputs across real devices number in the thousands, not billions. The probability of two genuinely different canvas renders producing the same hash is negligible in practice.

The real uniqueness comes from **combining** all seven fingerprints. Even if two devices share the same canvas hash, they almost certainly differ on GPU, audio, or fonts.

### DJB2 Hash Implementation Detail

```javascript
var h = 0;
for (var i = 0, len = str.length; i < len; i++) {
    h = ((h << 5) - h) + str.charCodeAt(i);  // h * 31 + char
    h = h & h;  // Force 32-bit integer
}
return Math.abs(h).toString(16);
```

The `h = h & h` line is a JavaScript idiom for truncating to a 32-bit signed integer. Without it, `h` could grow beyond Number.MAX_SAFE_INTEGER after enough iterations, losing precision. The `Math.abs()` handles the case where the final `h` is negative (sign bit set after the bitwise AND).

The closure-captured `h` variable (initialized inside the IIFE, reset to 0 at the top of the returned function) avoids creating a new variable per call. This is a minor optimization but consistent with the zero-allocation philosophy.

### Canvas Variance Math

The pixel variance calculation (lines 79-90) is computing:

$$\sigma^2 = \frac{1}{n}\sum_{i=1}^{n}\left(\frac{R_i + G_i + B_i}{3} - \bar{x}\right)^2$$

where $\bar{x} = \frac{1}{3n}\sum_{i=1}^{n}(R_i + G_i + B_i)$

Sampled at row 25 (vertical center of the 60px canvas), using 100 pixel samples (400 bytes of RGBA data, skipping the alpha channel). A variance below 1.0 means essentially uniform color output — the canvas is returning fake data.

### WebGL Software Renderer Detection

The GPU string check catches:
- **SwiftShader** — Google's software rasterizer, used in headless Chrome. Real Macs/Windows machines never use SwiftShader.
- **llvmpipe** — Mesa's LLVM-based software rasterizer, common in Linux VMs/containers.
- **Mesa** — generic software rendering fallback.
- **Disabled** — WebGL is off (set via browser flags or policy).

These are checked in the cross-signal analysis module too — `swiftshader-on-mac` scores +20 anomaly because macOS never uses SwiftShader (it uses Metal). This is a dead giveaway of a headless Chrome instance pretending to be macOS.
