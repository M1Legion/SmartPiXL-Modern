---
subsystem: pixl-script
title: "PiXL Script: Behavioral Analysis"
version: 1.0
last_updated: 2026-02-21
status: current
parent: subsystems/pixl-script
related:
  - subsystems/pixl-script/bot-detection-engine
  - subsystems/pixl-script/cross-signal-analysis
  - subsystems/enrichment-pipeline
---

# PiXL Script: Behavioral Analysis

## Atlas Public

### Watching How Visitors Move — Not Just Where They Click

SmartPiXL doesn't just identify devices — it observes **how** visitors interact with your website. Real humans and bots move differently, and these differences are measurable.

```
  ┌──────────── HUMAN VISITOR ──────────────┐
  │                                          │
  │     Cursor path:                         │
  │        ·  ·                              │
  │       ·    · ·                           │
  │      ·       · ·                         │
  │     ·     ·      ·  ← Curves, pauses,   │
  │      ·  ·    ·      varied speed         │
  │       ··   ·                             │
  │                                          │
  │  Entropy: HIGH (natural randomness)      │
  │  Timing: VARIED (human reaction time)    │
  │  Speed: VARIABLE (accelerate/decelerate) │
  └──────────────────────────────────────────┘

  ┌──────────── BOT VISITOR ────────────────┐
  │                                          │
  │     Cursor path:                         │
  │     ·                                    │
  │      ·                                   │
  │       ·                                  │
  │        ·         ← Straight lines,       │
  │         ·           constant speed,      │
  │          ·          perfect timing        │
  │                                          │
  │  Entropy: LOW (mechanical precision)     │
  │  Timing: UNIFORM (millisecond-perfect)   │
  │  Speed: CONSTANT (no acceleration)       │
  └──────────────────────────────────────────┘
```

**What we measure:**
- **Mouse movement patterns** — up to 50 cursor positions captured in the first 500ms
- **Movement randomness** — humans zigzag, bots move in straight lines
- **Speed variation** — humans accelerate and brake, bots maintain constant speed
- **Scroll behavior** — did the visitor actually scroll, and did the page move?

This behavioral data feeds into SmartPiXL's bot detection and is also forwarded to the server for advanced replay analysis — detecting when multiple visitors produce the exact same mouse path (recorded and replayed movements).

---

## Atlas Internal

### What We Capture

During the 500ms data collection window, the script listens for two types of browser events:

| Event | What We Record | Limit |
|-------|---------------|-------|
| `mousemove` | X position, Y position, timestamp | 50 events max |
| `scroll` | Whether scrolling occurred + scroll depth | Continuous |

**Important:** We do NOT capture clicks, keystrokes, or any form interaction. Only mouse movement coordinates and scroll position.

### The 10 Behavioral Fields

| Field | What It Means | Human Value | Bot Red Flag |
|-------|--------------|-------------|-------------|
| `mouseMoves` | Number of mouse events captured | 10-50 | 0 (no mouse at all) |
| `mouseEntropy` | How varied the movement angles are | 200-1000+ | < 50 (straight lines) |
| `moveTimingCV` | How varied the timing between moves is | 500-2000 | < 300 (metronomic) |
| `moveSpeedCV` | How varied the cursor speed is | 300-2000 | < 200 (constant speed) |
| `mousePath` | Raw coordinate data (x,y,time pipe-separated) | Irregular path | Straight line or replay |
| `moveCountBucket` | Volume category | `mid` or `high` | `low` or `very-high` |
| `scrolled` | Did scrolling occur? | `1` | `0` or `1` |
| `scrollY` | How far down the page (pixels) | 200-2000+ | `0` if scrolled but no depth |
| `scrollContradiction` | Scroll event but zero depth | `0` | `1` (spoofed scroll) |
| `behavioralFlags` | Anomaly summary | empty | `uniform-timing,uniform-speed` |

### How to Explain Entropy to Customers

**Simple version:** "Entropy measures randomness. High entropy = unpredictable = human. Low entropy = repetitive = bot."

**Analogy:** "If you watch someone sign their name, the pen moves in natural curves with varying pressure and speed. If a robot draws a signature, it moves in perfectly even strokes. MouseEntropy is how we tell the difference — it measures whether the cursor moved like a human hand or a machine."

### Scroll Contradiction

This is a particularly clever check. Some bots dispatch `scroll` events programmatically (to simulate human behavior) but forget to actually scroll the page. The result: a scroll event fires, but `window.scrollY` is still 0.

**When a customer asks about false positives:** This has essentially zero false positive risk. In a real browser, a scroll event **always** changes `scrollY` (unless you're at the absolute top, but the event wouldn't fire if nothing moved). A contradiction here is conclusive evidence of programmatic event dispatch.

---

## Atlas Technical

### Event Listener Setup

Mouse and scroll tracking begins after all synchronous data collection (PiXLScript.cs lines 1012-1022):

```javascript
var mouseData = { moves: [], startTime: Date.now(), scrolled: 0, scrollY: 0 };
var mouseHandler = function(e) {
    if (mouseData.moves.length < 50)
        mouseData.moves.push({ x: e.clientX, y: e.clientY, t: Date.now() - mouseData.startTime });
};
var scrollHandler = function() {
    mouseData.scrolled = 1;
    mouseData.scrollY = w.scrollY || w.pageYOffset || 0;
};
document.addEventListener('mousemove', mouseHandler);
document.addEventListener('scroll', scrollHandler);
```

- Timestamps are relative to `startTime` (when tracking began), not absolute. This produces smaller numbers (0-500) instead of 13-digit epoch timestamps.
- The 50-event cap prevents unbounded array growth from rapid mouse movement.
- Both listeners are removed after the data fires (line 1097).

### Entropy Calculation

The `calculateMouseEntropy()` function (lines 1024-1098) computes three statistical measures:

#### Mouse Entropy (Angle Variance)

For each consecutive pair of mouse positions, compute the direction angle:

$$\theta_i = \text{atan2}(y_i - y_{i-1},\ x_i - x_{i-1})$$

Then compute variance of angles:

$$\text{entropy} = \left(\frac{1}{n}\sum\theta_i^2 - \left(\frac{1}{n}\sum\theta_i\right)^2\right) \times 1000$$

High variance = diverse angles = natural curve movement. Low variance = consistent direction = straight line.

The `* 1000 + 0.5 | 0` at the end scales to integer milliradians and rounds.

#### Timing CV (Coefficient of Variation)

For each consecutive pair, compute the time interval $\Delta t_i = t_i - t_{i-1}$:

$$CV_{timing} = \frac{\sigma_{\Delta t}}{\mu_{\Delta t}} = \frac{\sqrt{\frac{1}{n}\sum\Delta t_i^2 - \left(\frac{1}{n}\sum\Delta t_i\right)^2}}{\frac{1}{n}\sum\Delta t_i}$$

CV < 0.3 signals `uniform-timing` (+5 anomaly). Human timing CV is typically 0.5-2.0 due to variable reaction times.

#### Speed CV (Coefficient of Variation)

For each consecutive pair, compute speed:

$$v_i = \frac{\sqrt{(x_i - x_{i-1})^2 + (y_i - y_{i-1})^2}}{\Delta t_i}$$

$$CV_{speed} = \frac{\sigma_v}{\mu_v}$$

CV < 0.2 signals `uniform-speed` (+5 anomaly). Human speed CV is typically 0.3-2.0 (we accelerate and decelerate constantly).

### Implementation Detail: Running Variance

The code uses the **computational formula** for variance rather than the two-pass algorithm:

```javascript
var aSum = 0, aSq = 0, n = 0;
for (var i = 1; i < mLen; i++) {
    var a = Math.atan2(dy, dx);
    aSum += a; aSq += a * a;
    n++;
}
var aM = aSum / n;
data.mouseEntropy = (aSq / n - aM * aM) * 1000 + 0.5 | 0;
```

$$\sigma^2 = E[X^2] - (E[X])^2 = \frac{\sum x_i^2}{n} - \left(\frac{\sum x_i}{n}\right)^2$$

This is a single-pass algorithm — no need to compute the mean first, then iterate again. It accumulates sum and sum-of-squares simultaneously.

### Mouse Path Serialization

```javascript
if (mLen > 0) {
    var mp = '';
    for (var j = 0; j < mLen; j++) {
        var pt = moves[j].x + ',' + moves[j].y + ',' + moves[j].t;
        if (mp.length + pt.length + 1 > 2000) break;
        mp += pt;
        if (j > 0) mp = /* prepend pipe */;
    }
    data.mousePath = mp;
}
```

Format: `x1,y1,t1|x2,y2,t2|x3,y3,t3|...`

Example: `512,340,0|520,345,16|535,342,32|560,350,48|...`

The 2000-character cap ensures the query string doesn't explode. With typical coordinate/time values, this fits ~80-100 data points (though only 50 are captured).

### Scroll Contradiction Check

```javascript
if (mouseData.scrolled && mouseData.scrollY === 0) {
    data.scrollContradiction = 1;
    data.crossSignals = (data.crossSignals || '') + 
        (data.crossSignals ? ',scroll-no-depth' : 'scroll-no-depth');
    data.anomalyScore = (data.anomalyScore | 0) + 8;
}
```

This modifies `crossSignals` and `anomalyScore` from the behavioral section — it's a cross-signal contribution that happens during behavioral analysis because the scroll data isn't available until the listeners finish.

### Move Count Bucketing

```javascript
data.moveCountBucket = mLen < 5 ? 'low' : mLen < 20 ? 'mid' : mLen < 50 ? 'high' : 'very-high';
```

| Bucket | Range | Interpretation |
|--------|-------|---------------|
| `low` | 0-4 | Minimal interaction (suspicious if combined with other signals) |
| `mid` | 5-19 | Normal brief visit |
| `high` | 20-49 | Active browsing |
| `very-high` | 50 (cap) | Very active or rapid automation |

### Data Flow: Browser → Forge

The raw `mousePath` is transmitted to Edge, stored in `PiXL.Raw`, and forwarded to the Forge's `BehavioralReplayService`. The Forge:
1. Parses the `x,y,t` triples
2. Hashes the normalized path (removing absolute position)
3. Compares against a rolling window of recent paths across all visitors
4. If two visitors produce the same or highly similar path → replay detection (bot reusing recorded human movements)

---

## Atlas Private

### Statistical Thresholds

The threshold values (0.3 for timing CV, 0.2 for speed CV) were chosen based on empirical testing:

- **Real humans**: Timing CV ranges from 0.4 to 3.0+. Speed CV ranges from 0.3 to 3.0+. The distributions have long right tails (some movements are very erratic).
- **Basic bots**: Timing CV is often 0.0-0.15 (perfectly even event dispatch). Speed CV is 0.0-0.1 (constant speed interpolation).
- **Sophisticated bots**: Use jitter to simulate human variation. Timing CV of 0.2-0.5, speed CV of 0.15-0.4. These fall in a gray zone where behavioral analysis alone isn't conclusive, which is why bot detection + cross-signal + behavioral all contribute to the combined score.

The thresholds are set conservatively to minimize false positives. A real human would need to move their mouse in a perfectly straight line at constant speed for 500ms to trigger — essentially impossible without robotic assistance.

### Minimum Data Requirements

```javascript
if (mLen < 5) { data.mouseEntropy = 0; data.behavioralFlags = ''; return; }
```

Fewer than 5 mouse events produces statistically meaningless results. The entropy is set to 0 and no flags are generated. This prevents false positives on visitors who loaded the page but didn't touch their mouse (mobile visitors, keyboard navigators, etc.).

The CV calculations require `n > 3` (where `n` = number of valid intervals with `dt > 0`):

```javascript
if (n > 3) {
    // CV calculations...
}
```

This is separate from the 5-event minimum because some events may have `dt = 0` (two events at the same millisecond) and are excluded from the interval analysis.

### Anomaly Score Integration

Behavioral flags contribute to `crossSignals` and `anomalyScore`:

```javascript
data.behavioralFlags = _bf;
if (_bf) {
    var cs2 = data.crossSignals || '';
    data.crossSignals = cs2 ? cs2 + ',' + _bf : _bf;
}
```

This means behavioral flags appear in **two** places:
1. `behavioralFlags` — isolated behavioral anomaly flags
2. `crossSignals` — merged into the cross-signal string alongside font/GPU/Safari anomalies

This is by design — the Forge and ETL can filter on either field depending on the analysis purpose.

### Event Listener Cleanup

```javascript
document.removeEventListener('mousemove', mouseHandler);
document.removeEventListener('scroll', scrollHandler);
```

Listeners are removed in `calculateMouseEntropy()`, which runs immediately before `sendPiXL()`. After the GIF fires, the script has no residual event listeners on the page — zero ongoing impact.

### Mobile Visitor Handling

On mobile devices, `mousemove` events don't fire (there's no mouse). `touchmove` events exist but we don't listen for them. Mobile visitors will have:
- `mouseMoves = 0`
- `mouseEntropy = 0`
- `mousePath` = empty

This is **not** flagged as suspicious because mobile detection happens via other fields (`touch > 0`, mobile UA, coarse pointer, etc.). The behavioral analysis is desktop-focused by design.

### 50-Event Cap Reasoning

The cap exists because:
1. **Query string size**: 50 events × ~12 chars per point = ~600 chars. Uncapped rapid movement could produce 200+ events in 500ms.
2. **Statistical sufficiency**: 50 points provide more than enough data for entropy, CV, and replay hash calculations.
3. **Performance**: Array growth beyond 50 provides no analytical benefit but costs memory and serialization time.

The cap is checked per-event (`if (mouseData.moves.length < 50)`) before pushing, so the array never exceeds 50 elements.
