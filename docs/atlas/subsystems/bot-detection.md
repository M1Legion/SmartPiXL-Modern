---
subsystem: bot-detection
title: Bot Detection
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - subsystems/pixl-script
  - subsystems/enrichment-pipeline
  - subsystems/traffic-alerts
---

# Bot Detection

## Atlas Public

Bot traffic is a growing problem for every business with a website. Industry estimates suggest 30-50% of all web traffic is automated — scrapers, credential stuffers, ad fraud bots, and competitors' monitoring tools. SmartPiXL doesn't just detect bots — it quantifies your bot problem with precision.

**SmartPiXL detects bots through 80+ distinct signals across three layers:**

- **Browser signals** — automation markers (Selenium, Puppeteer, Playwright), headless browser indicators, spoofed properties, impossible hardware combinations
- **Network signals** — datacenter IPs (AWS, Google Cloud), rapid-fire request patterns, subnet velocity anomalies, VPN/proxy detection
- **Behavioral signals** — mouse movement analysis (bots move in straight lines at constant speed), replayed behavioral recordings, scroll event inconsistencies

**What sets SmartPiXL apart:**
- Each visitor receives a **bot score** (0-100+) based on weighted signal accumulation
- Cross-customer visibility detects bots that hit one page per site — each customer sees one clean visit, but SmartPiXL sees the pattern across all customers
- Advanced behavioral replay detection catches state-of-the-art bots that record and replay real human mouse movements

## Atlas Internal

### Three Layers of Detection

**Layer 1: Browser-Side (PiXL Script)**

The script runs 80+ checks in the visitor's browser before data even reaches our servers:

| Check | What It Catches | Example |
|-------|----------------|---------|
| WebDriver flag | Selenium, Playwright, Puppeteer | `navigator.webdriver = true` |
| Chrome DevTools Protocol | Headless Chrome with CDP enabled | Global CDP functions present |
| DOM automation markers | Selenium/PhantomJS inject objects | `__selenium_unwrapped`, `_phantom` |
| Navigator property tampering | Anti-detect browsers overriding properties | Getter function has wrong name or prototype |
| Cross-realm function check | Stealth plugins modifying Function.prototype | iframe's toString differs from main frame |
| Heap spoofing | Fake memory values in headless environment | `jsHeapSizeLimit` is a round million |
| Empty languages | Headless browsers don't set language preferences | `navigator.languages.length = 0` |

Each detected signal adds to the `botScore`. A visitor with `webdriver=true` + `selenium DOM markers` + `empty languages` would score 25+ immediately.

**Layer 2: Server-Side Fast Checks (Edge)**

In-memory checks on every request:

| Check | What It Detects | Threshold |
|-------|----------------|-----------|
| Datacenter IP | Bots running in AWS/GCP | 8,500+ CIDR ranges checked |
| Rapid-fire timing | Same IP, multiple hits in seconds | 2+ hits in 15s = automation |
| Subnet velocity | Bot farm from same /24 subnet | 3+ IPs from same subnet in 5 min |
| Fingerprint rotation | Anti-detect browser changing identity | 3+ unique fingerprints from same IP in 24h |

**Layer 3: Deep Analysis (Forge)**

Heavyweight analysis with no time constraint:

| Check | What It Detects | Method |
|-------|----------------|--------|
| Known bot database | Googlebot, Bingbot, 10,000+ crawlers | NetCrawlerDetect library |
| Reverse DNS | Cloud-hosted bots vs residential visitors | PTR record → hostname pattern |
| Cross-customer intel | Coordinated scraping across sites | Same IP+FP hitting 3+ customers in 5 min |
| Contradiction matrix | Impossible hardware combinations | Mobile UA + 4K screen + mouse = impossible |
| Behavioral replay | Recorded mouse movements replayed | Mouse path hashing across visitors |
| Cultural arbitrage | VPN users with mismatched cultural fingerprint | French fonts + US IP + Vietnamese language |

### Bot Score Interpretation

| Score Range | Classification | Typical Cause |
|-------------|---------------|--------------|
| 0 | Clean | No bot signals detected |
| 1-15 | Low suspicion | Minor anomalies (missing API, unusual config) |
| 15-30 | Moderate | Multiple indicators (datacenter IP + low mouse) |
| 30-50 | High | Strong bot indicators (webdriver + selenium markers) |
| 50+ | Definite bot | Overwhelming evidence |

### Cross-Customer Detection (SmartPiXL Exclusive)

Most tracking solutions see traffic from one website at a time. SmartPiXL sees ALL traffic across ALL customers simultaneously. This means:

A sophisticated bot visits 26 different SmartPiXL customers in 2 minutes — each customer only sees one clean hit. Any single-site analytics tool would mark it as human. SmartPiXL sees the same IP + fingerprint appearing across 26 sites and flags it immediately.

This is one of SmartPiXL's strongest competitive advantages.

## Atlas Technical

### Bot Signal Architecture

The PiXL Script outputs three composite fields:

```
botSignals=webdriver,selenium,cdp,headless-no-chrome-obj
botScore=38
botPermInconsistent=1
```

Each signal name maps to a weight in the script. `botScore` is the sum of all detected signal weights. The `botSignals` field is a comma-separated list of triggered signal names — useful for debugging and for the Forge's downstream analysis.

### Edge In-Memory Services

**`DatacenterIpService`** — Binary prefix trie (`CidrTrie`) of ~8,500 AWS + GCP CIDR ranges. O(32) lookup per IP. Ranges downloaded weekly from:
- AWS: `https://ip-ranges.amazonaws.com/ip-ranges.json`
- GCP: `https://www.gstatic.com/ipranges/cloud.json`

New trie built in background → atomically swapped via `Volatile.Read` / `Interlocked.CompareExchange`.

**`IpBehaviorService`** — `ConcurrentDictionary<string, TimingEntry>` tracking per-IP and per-/24-subnet hit counts:
- Same IP, 2+ hits in 15 seconds → `_srv_rapidFire=1`
- Same IP, gap < 1 second → `_srv_subSecDupe=1`
- Same /24 subnet, 3+ distinct IPs in 5 minutes → `_srv_subnetAlert=1`

**`FingerprintStabilityService`** — `IMemoryCache` per-IP, 24-hour sliding window:
- Tracks set of unique fingerprint hashes per IP
- 3+ unique FPs → `_srv_fpAlert=1` (anti-detect browser)
- 50+ observations → volume alert
- 20+ in 5 minutes → rate alert

### Forge Enrichments

**`BotUaDetectionService`** — Wraps `NetCrawlerDetect.CrawlerDetect`. Checks User-Agent against 10,000+ known bot patterns. Returns `(bool IsCrawler, string? BotName)`. Appends `_srv_knownBot=1`, `_srv_botName={name}`.

**`ContradictionMatrixService`** — 13 cross-signal rules with severity tiers:

| Rule | Severity | Condition |
|------|----------|-----------|
| Mobile UA + large screen + mouse | IMPOSSIBLE | Mobile can't have mouse and 4K screen |
| macOS + DirectX GPU | IMPOSSIBLE | macOS doesn't use DirectX rendering |
| Battery API on macOS Safari | IMPOSSIBLE | Safari doesn't expose Battery API |
| Touch points > 0 but no touch support | IMPOSSIBLE | Contradictory touch capabilities |
| Desktop UA + screen < 600px | SUSPICIOUS | Desktop on phone-sized screen |
| Linux + Apple fonts | IMPOSSIBLE | Apple fonts don't exist on Linux |

Uses `stackalloc` for zero-allocation rule evaluation.

**`BehavioralReplayService`** — The `mousePath` field (`x,y,t|x,y,t|...`) is quantized to a 10px grid with 100ms time buckets, then hashed with FNV-1a (32-bit). A `ConcurrentDictionary<uint, (string FP, DateTime)>` stores recent hashes. Same hash from a different fingerprint = replayed recording.

### Subnet Reputation (SQL)

`PiXL.SubnetReputation` table aggregates per-/24 subnet:
- Unique IPs, unique devices, total hits
- Average bot score, bot percentage
- Updated daily by `ETL.usp_UpdateSubnetReputation` using `dbo.GetSubnet24()` CLR function

The Forge can check subnet reputation during enrichment: "This IP is from a subnet with 87% bot rate across 6 months of history."

## Atlas Private

### Bot Detection Arms Race

SmartPiXL's bot detection is designed with an adversarial mindset — bot operators actively try to evade detection. The current state of the art in evasion:

1. **Anti-detect browsers** (Multilogin, GoLogin, AdsPower) — Create unique browser profiles with spoofed fingerprints. SmartPiXL counters with: fingerprint stability tracking (same IP rotating FPs), getter property validation (proxy-wrapped Navigator properties have wrong names), cross-realm toString checks (stealth plugins can't perfectly mimic native functions across iframe boundaries).

2. **Behavioral replay** — Record a real human's mouse movements, replay them with a bot. SmartPiXL counters with: mouse path hashing (same trajectory from different fingerprint = replay), path quantization (10px grid + 100ms buckets) to handle minor replay variance.

3. **Residential proxy networks** — Route bot traffic through real residential IPs to avoid datacenter detection. SmartPiXL counters with: cross-customer intelligence (same FP hitting many customers in minutes is bot behavior regardless of IP type), cultural arbitrage (residential US IP but French fonts + Vietnamese language = VPN).

### Signal Stability Across Browsers

Not all browsers expose the same APIs. Signal coverage:

| Signal Category | Chrome | Firefox | Safari | Brave |
|----------------|--------|---------|--------|-------|
| WebDriver detection | Full | Full | Full | Full |
| DOM automation markers | Full | Full | Full | Full |
| Client Hints | Full | None | None | Partial |
| Memory heap | Full | None | None | None |
| Battery API | Full | Full | None | Full |
| Canvas consistency | Clean | Clean | Clean | Noise (detected) |
| Audio consistency | Clean | Clean | Clean | Noise (detected) |

The bot scoring is normalized per browser — a Safari visitor with a lower-dimensional fingerprint isn't penalized for missing Chrome-only signals.

### Known Weaknesses

1. **Slow-rotation anti-detect**: If a bot rotates fingerprints once per 2+ hours, the Edge's `FingerprintStabilityService` (1-hour memory) won't catch it. SQL-level device lifecycle analysis catches it retroactively, but there's a real-time detection gap.

2. **Mobile bots**: Mobile device fingerprints have lower uniqueness (many iPhones are identical). Mobile bots are harder to distinguish from real mobile users based on fingerprint alone — behavioral signals (mouse/touch patterns) become critical.

3. **Legitimate multi-device users**: A pentester or developer using 5 different browser profiles from the same IP looks identical to an anti-detect browser. The `fpAlert` flag fires, but it's a false positive. No plans to address this — the false positive rate is low and the signal value is high.

4. **`botScore` granularity**: The scoring weights are manually assigned based on domain knowledge, not machine-learned. Some weights may be suboptimal. Phase 6+ plans ML-based scoring using `ML.NET`, but this is 6+ months out.
