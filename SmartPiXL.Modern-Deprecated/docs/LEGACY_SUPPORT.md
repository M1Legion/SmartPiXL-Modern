# SmartPiXL — Legacy PiXL Support

**Status:** Complete — full pipeline live (§5.1–5.6 ingestion + §5.7 IP matching)
**Last updated:** February 17, 2026

---

## 1. Context

SmartPiXL is an upgrade to the existing Xavier tracking platform. Hundreds of clients have legacy pixel tags deployed on their websites. These must continue to work without client-side changes. SmartPiXL must accept legacy pixel hits, process them through the modern pipeline, and provide an effortless upgrade path.

---

## 2. Legacy Pixel Formats

Clients have one of two embed code formats deployed:

### Format A: `<img>` Tag (Most Common)

```html
<img src="https://smartpixl.info/12506/00106_SMART.GIF" style="display: none !important;" />
```

- Browser sends a bare GET request for `_SMART.GIF`
- **No JavaScript executes** — the browser treats the response as image bytes
- Server receives: IP, User-Agent, Referer, Accept-Language, all HTTP headers
- Query string: **empty**

### Format B: Inline `<script>` Tag

```html
<script>
  (async function () {
    new Image().src = `https://smartpixl.info/12506/00106_SMART.GIF?ref=${encodeURIComponent(window.location.href)}`;
  })();
</script>
```

- JavaScript constructs a `new Image().src` — still an image request
- **Our PiXLScript does NOT load or execute** — this inline script fires its own image request
- Server receives: IP, User-Agent, Referer, Accept-Language, all HTTP headers
- Query string: `?ref=<encoded page URL>` (typically 50–200 chars)

### Modern Format: `<script src>` Tag

```html
<script src="https://smartpixl.info/12506/00106_SMART.js"></script>
```

- Browser loads and executes PiXLScript (90+ fingerprint data points)
- PiXLScript fires `new Image().src = ".../_SMART.GIF?sw=...&sh=...&canvasFP=...&..."` with full query string
- Server receives: full JS fingerprint data + all HTTP headers + server-side enrichment

---

## 3. Why Legacy Pixels Can't Run Our JavaScript

The browser's security model enforces a strict boundary:

1. **`<img>` tags** — The response is processed by the image decoder. Even if the server returns JavaScript with `Content-Type: text/javascript`, the browser will **not** execute it. It will attempt to decode it as image bytes and show a broken image icon.

2. **`new Image().src`** — Same constraint. The `Image` constructor creates an image request context. The response is handled by the image pipeline, never the JavaScript engine.

3. **HTTP 302 redirects** — Redirecting from `_SMART.GIF` to a `.js` URL preserves the original request context (image). The destination is still processed as an image.

**This is a fundamental browser security boundary, not a server limitation.**

---

## 4. What Legacy Hits Capture

Even without JavaScript, legacy hits provide:

| Data Point | Source | Available? |
|------------|--------|------------|
| Client IP | Connection / X-Forwarded-For / CF-Connecting-IP | ✅ |
| User-Agent | HTTP header | ✅ |
| Referer | HTTP header (or `?ref=` param for Format B) | ✅ |
| Accept-Language | HTTP header | ✅ |
| DNT | HTTP header | ✅ |
| Sec-CH-UA-* | Client Hints (modern browsers auto-send) | ✅ |
| Sec-Fetch-* | Fetch metadata headers | ✅ |
| IP Classification | Server-side enrichment (datacenter/residential/reserved) | ✅ |
| IP Geolocation | Server-side enrichment (IPAPI.IP cache lookup) | ✅ |
| IP Behavior | Server-side enrichment (subnet velocity, rapid-fire detection) | ✅ |
| Canvas/WebGL/Audio FP | JavaScript only | ❌ |
| Screen resolution | JavaScript only | ❌ |
| Device hardware | JavaScript only | ❌ |
| Behavioral biometrics | JavaScript only | ❌ |
| Bot detection signals | JavaScript only (29+ signals) | ❌ |

**Server-side enrichment still runs on legacy hits.** The enrichment pipeline (IP geo, IP classification, datacenter detection, IP behavior) is applied regardless of hit type.

---

## 5. Implementation Design

### 5.1 Accept All `_SMART.GIF` Hits

**Change:** Remove the `queryString.Length > 10` gate in `TrackingEndpoints.cs`. If `path.EndsWith("_SMART.GIF")`, always call `CaptureAndEnqueue`.

**Rationale:** The `_SMART.GIF` suffix is sufficient to identify tracking pixel requests. The query-string length check was intended to filter noise, but non-pixel requests don't end in `_SMART.GIF`.

### 5.2 Add `_SMART.js` Route

**Change:** Add a new route matching `/{**path}` where path ends in `_SMART.js`. Extract `companyId`/`pixlId` from the URL path (same regex as `_SMART.GIF`) and serve PiXLScript.

This provides the upgrade-friendly URL pattern:
```
LEGACY:  <img    src="https://smartpixl.info/12506/00106_SMART.GIF">
MODERN:  <script src="https://smartpixl.info/12506/00106_SMART.js"></script>
```

The existing `/js/{companyId}/{pixlId}.js` endpoint is retained for backward compatibility.

### 5.3 Hit-Type Detection

**Change:** In `CaptureAndEnqueue`, inspect the query string for a modern-only parameter (e.g., `tier=` or `sw=`). If absent → `_srv_hitType=legacy`. If present → `_srv_hitType=modern`. Appended to the enriched query string alongside other `_srv_*` params.

### 5.4 Legacy `?ref=` Handling

Legacy Format B scripts send `?ref=<pageURL>`. When the HTTP `Referer` header is absent (some browsers strip it for cross-origin image requests), populate the Referer field from the `ref` query parameter.

### 5.5 ETL: HitType Columns

**SQL Migration** (`SQL/28_LegacySupport.sql`):
- Add `HitType VARCHAR(10)` to `PiXL.Parsed`
- Add `HitType VARCHAR(10)` to `PiXL.Visit`
- Update `ETL.usp_ParseNewHits` to parse `_srv_hitType` from QueryString into `HitType`

Legacy rows in `PiXL.Parsed` will have ~170 NULL columns (all JS-only fields). The ETL proc is already NULL-tolerant — `GetQueryParam` returns NULL when a parameter is absent.

### 5.6 Pipeline Flow

```
Legacy <img> or <script>        Modern <script src>
        │                               │
        │  _SMART.GIF (bare)            │  _SMART.js → PiXLScript → _SMART.GIF?90+params
        │                               │
        └───────────┬───────────────────┘
                    │
            CaptureAndEnqueue
            (server-side enrichment applied to both)
            (_srv_hitType = legacy | modern)
                    │
                PiXL.Raw (same table, same 9 columns)
                    │
            usp_ParseNewHits
                    │
            PiXL.Parsed (HitType = 'legacy' → ~170 NULLs)
            PiXL.Visit  (HitType = 'legacy', DeviceId = NULL)
            PiXL.IP     (works for both — both provide an IP)
                    │
            ┌───────┴───────┐
            │               │
     usp_MatchVisits   usp_MatchLegacyVisits
     (email-based)     (IP-based)
     modern hits       legacy hits
            │               │
            └───────┬───────┘
                    │
              PiXL.Match
              MatchType = 'email' | 'ip'
```

---

## 6. IP-Based Identity Resolution (Legacy Match)

### 5.7 ETL: `usp_MatchLegacyVisits`

Legacy hits have no MatchEmail (JavaScript doesn't execute, so no form-fill capture). Identity resolution uses IP address matching against AutoConsumer instead.

**Match logic:**
1. Select legacy visits (HitType='legacy', MatchEmail IS NULL) from the watermark window
2. Gate by `PiXL.Config.MatchIP` (defaults to enabled when no Config row)
3. Join PiXL.Visit → PiXL.IP → AutoConsumer ON IP address
4. For each IP, pick the most recent AutoConsumer record (highest RecordID)
5. MERGE into PiXL.Match with MatchType='ip', MatchKey=IPAddress

**Key behaviors:**
- Source is deduplicated by (CompanyID, PiXLID, IPAddress) before MERGE to avoid "update same row twice" errors
- Multiple consumers can share one IP (NAT, VPN, household) — we pick the most recent record
- Uses its own watermark row ('MatchLegacyVisits') separate from email matching
- Requires `IX_AutoConsumer_IP` index on the 420M+ row AutoConsumer table

**Columns populated in PiXL.Match:**

| Column | Source |
|--------|--------|
| MatchType | `'ip'` (constant) |
| MatchKey | IPAddress from PiXL.IP |
| IndividualKey | AutoConsumer.IndividualKey (most recent record for that IP) |
| AddressKey | AutoConsumer.AddressKey (most recent record for that IP) |
| DeviceId | NULL (legacy hits have no JS fingerprints) |
| IpId | From PiXL.IP (always populated) |
| ConfidenceScore | NULL (future: inversely proportional to consumers sharing the IP) |

**SQL Migration:** `SQL/32_LegacyIpMatching.sql`

---

## 7. The Upgrade Pitch

This architecture directly enables the sales conversation:

> "Here's Company X. Their legacy pixel captures IP and User-Agent — that's it. We matched **47** visitors this month from their traffic.
>
> Company Y upgraded to the modern pixel last week. Same traffic volume. We matched **312** visitors with full fingerprint data, device profiles, and behavioral signals.
>
> The upgrade is literally changing one HTML tag. Want to see what you're missing?"

The `HitType` column in `PiXL.Visit` enables a direct side-by-side dashboard comparison of legacy vs modern match yield per company.

---

## 8. Xavier Traffic Forwarding

To receive real production traffic from Xavier while it remains the live platform, we need SmartPiXL to see the **original browser request** — not a reconstructed copy of it. There are two approaches, in order of preference.

### Recommended: IIS ARR Reverse Proxy on Xavier

Application Request Routing (ARR) is a native IIS module. It receives the browser's HTTP request with **all original headers intact** (Accept-Language, Client Hints, Sec-Fetch, DNT, cookies, everything), then forwards the entire request to SmartPiXL. The only modification is adding `X-Forwarded-For: {real client IP}`, which SmartPiXL already reads as top priority in its IP extraction chain.

This approach gives SmartPiXL the same request it would see if the browser had connected directly. When we eventually cut DNS to point at SmartPiXL, the only change is `X-Forwarded-For` disappears (because there's no proxy anymore) and `Connection.RemoteIpAddress` becomes the browser's real IP — which is already the last fallback in our extraction chain.

**See `docs/XAVIER_ARR_SETUP.md` for step-by-step setup instructions.**

### Backup: Code-Level HTTP Forward (Not Recommended)

If ARR cannot be installed on Xavier, a code-level forward from `Default.aspx.cs` is a functional but inferior alternative. It only passes IP and User-Agent — all other browser headers (Accept-Language, Client Hints, Sec-Fetch, DNT, cookies) are lost.

```csharp
// After sbc.WriteToServer(InsertTable) in Default.aspx.cs:
try {
    var fwd = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
        "http://192.168.88.176" + vars["HTTP_X_ORIGINAL_URL"]);
    fwd.Headers["X-Forwarded-For"] = vars["REMOTE_ADDR"];
    fwd.Headers["User-Agent"] = vars["HTTP_USER_AGENT"];
    fwd.Timeout = 2000;
    fwd.GetResponse().Close();
} catch { }
```

**Why this is inferior:** SmartPiXL's `TrackingCaptureService` captures 23 specific headers into `HeadersJson`. The code forward provides 2 of them. This doesn't test legacy support realistically — it tests a hollow shell. Use ARR instead.

---

## 9. Company/Pixel Sync from Xavier

Xavier's `dbo.Company` (~467 rows) and `dbo.PiXL` (~5,612 rows) are the system-of-record tables. SmartPiXL mirrors them via `CompanyPixelSyncService`.

### Sync Architecture

| Aspect | Details |
|--------|---------|
| **Source** | `192.168.88.35`, database `SmartPixl`, tables `dbo.Company` and `dbo.PiXL` |
| **Destination** | `localhost\SQL2025`, database `SmartPiXL`, tables `PiXL.Company` and `PiXL.Pixel` |
| **Pattern** | Watermark-based incremental (same as `IpApiSyncService`) |
| **Watermark column** | `ModifiedDate` (both tables have this on Xavier) |
| **Merge key** | `CompanyID` for Company; `(CompanyId, PiXLId)` for Pixel |
| **Frequency** | Every 15 minutes (configurable via `TrackingSettings.CompanyPixelSyncIntervalMinutes`) |
| **Local-only columns** | `ClientParams`, `Notes`, `IsActive` (on Pixel), `SysStartTime`, `SysEndTime` — **NOT overwritten** by MERGE |

### Schema Differences

**PiXL.Company** — Local has 41 columns vs Xavier's 38. Local extras: `Notes` (nvarchar 500), `SysStartTime`, `SysEndTime` (temporal columns).

**PiXL.Pixel** — Local has 52 columns vs Xavier's 46. Local extras: `ClientParams` (nvarchar 500), `Notes` (nvarchar 500), `IsActive` (bit), `ModifiedDate` (datetime2 vs Xavier datetime), `SysStartTime`, `SysEndTime`.

---

## 10. Related Files

| File | Purpose |
|------|---------|
| `Endpoints/TrackingEndpoints.cs` | Gate logic, `_SMART.GIF` + `_SMART.js` routes, `CaptureAndEnqueue` |
| `Services/TrackingCaptureService.cs` | HTTP request → TrackingData (IP extraction chain, header capture) |
| `Services/CompanyPixelSyncService.cs` | Company/Pixel MERGE sync from Xavier |
| `Services/IpApiSyncService.cs` | Template for sync pattern (watermark, MERGE, SyncLog) |
| `Scripts/PiXLScript.cs` | Modern JS fingerprinting script template |
| `SQL/28_LegacySupport.sql` | HitType column migration |
| `SQL/29_CompanyPixelSyncLog.sql` | Sync audit table |
| `Old Website/TrackingPixel/Default.aspx.cs` | Xavier's legacy pixel handler (reference) |
