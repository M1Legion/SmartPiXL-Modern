---
title: PiXL Edge
node: edge
nodeType: system
status: current
related:
  - platform/smartpixl
  - systems/forge
  - subsystems/f4-failover
---

# PiXL Edge

## Atlas Public

The PiXL Edge is the front door of SmartPiXL's intelligence platform. It's the component that receives visitor data from your website and processes it in real time.

**What makes it special:**

- **Blazing fast** — responds in under 10 milliseconds, so your website performance is never affected
- **Always analyzing** — performs 12 instant intelligence checks on every single visit before even storing the data
- **Rock-solid reliable** — if any downstream system is temporarily unavailable, the Edge never loses a single data point

When a visitor hits your page, the Edge captures the data, performs instant analysis (datacenter IP detection, fingerprint recognition, geographic lookup), and passes the enriched record downstream for deep analysis — all before the visitor's browser has finished rendering the page.

## Atlas Internal

### What the Edge Does

The Edge is an ASP.NET Core application running inside IIS. It handles two types of requests:

1. **`_SMART.js`** — serves the PiXL tracking script to the visitor's browser
2. **`_SMART.GIF`** — receives the collected data and does something useful with it

When the GIF request arrives, the Edge runs 12 fast enrichment checks before responding. These checks are all in-memory — no external API calls, no database queries. They answer questions like:

- Is this a datacenter IP? (AWS, Google Cloud — 8,500+ CIDR ranges)
- Have we seen this fingerprint from this IP before? Is it varying suspiciously?
- Is this IP or subnet hitting us unusually fast?
- What country/city is this IP from? (cached lookup)
- Does the visitor's timezone match their IP's geography?

After enrichment, the Edge fires the data to the Forge for deep analysis and returns a 43-byte transparent GIF. Total time: under 10 milliseconds.

### What It Doesn't Do

The Edge does NOT write to the database. That's the Forge's job. The Edge's only two output paths are:
1. Named pipe → Forge (primary)
2. JSONL file on disk (failover if Forge is unavailable)

This separation means the Edge is extremely fast and resilient — it never blocks on database contention or network timeouts.

### Fast Enrichment Summary

| Check | What It Detects | Speed |
|-------|----------------|-------|
| Fingerprint stability | Anti-detect browsers (3+ unique fingerprints from one IP in 24h) | ~1μs |
| IP behavior | Bot farms (3+ IPs from same /24 subnet in 5 min) | ~1μs |
| Rapid-fire detection | Automation (2+ hits from same IP in 15 seconds) | ~1μs |
| Datacenter IP | Cloud-hosted bots (AWS, GCP — 8,500 CIDR ranges) | ~1μs |
| IP classification | Reserved/private/CGNAT/multicast/loopback ranges | ~0.5μs |
| Geo cache | Country, region, city, timezone from IP | ~0.5μs |
| Timezone mismatch | VPN/proxy signals (browser says EST, IP geolocates to Tokyo) | ~0.1μs |

### Health Probes

The Edge system exposes 4 health probes monitored by Sentinel:

| Probe | What It Checks |
|-------|---------------|
| **HTTP Listener** | Kestrel responding — always healthy if the Edge process is running |
| **Capture Pipeline** | Requests to TrackingData conversion with error rate monitoring |
| **Pipe Client** | Named pipe connection to Forge |
| **JSONL Failover** | Disk-based failover when pipe is unavailable |

## Atlas Technical

### Hosting Model

The Edge runs **InProcess** inside IIS via `AspNetCoreModuleV2`. The .NET application lives inside the `w3wp.exe` process — no reverse proxy, no inter-process Kestrel communication.

- InProcess model gives access to the real client IP via `HttpContext.Connection.RemoteIpAddress`
- Out-of-process only sees `127.0.0.1` unless `X-Forwarded-For` is configured
- `web.config` must contain `hostingModel="inprocess"` and extended query string limits

```xml
<requestLimits maxQueryString="16384" maxUrl="8192" />
```

### Production Location

| Setting | Value |
|---------|-------|
| IIS Site | `Smartpixl.info` |
| App Pool | `Smartpixl.info` |
| Path | `C:\inetpub\Smartpixl.info\` |
| Ports | 80/443 via IIS |
| Log | `C:\inetpub\Smartpixl.info\Log\` |
| Failover | `C:\inetpub\Smartpixl.info\Failover\` |

### Request Pipeline

```
HTTP GET _SMART.GIF?{qs}
  → TrackingEndpoints.CaptureAndEnqueue()
    → QueryParamReader.Parse() — 231 fields from query string
    → TrackingCaptureService.EnrichFast() — 12 in-memory checks
    → PipeClientService.TrySendAsync() — named pipe to Forge
    → (fallback) FailoverWriterService.WriteAsync() — JSONL to disk
  → 43-byte transparent GIF response
```

## Atlas Private

### Key Files

| File | Purpose |
|------|---------|
| `SmartPiXL/Endpoints/TrackingEndpoints.cs` | HTTP capture endpoint (`_SMART.GIF`) |
| `SmartPiXL/Services/TrackingCaptureService.cs` | Fast enrichment orchestration |
| `SmartPiXL/Services/PipeClientService.cs` | Named pipe client to Forge |
| `SmartPiXL/Services/FailoverWriterService.cs` | JSONL failover writer |
| `SmartPiXL/Scripts/` | PiXL Script JavaScript source |
| `SmartPiXL/appsettings.json` | Dev config (ports 7000/7001) |
| `C:\inetpub\Smartpixl.info\appsettings.json` | Production config (ports 6000/6001) |

### Important: web.config

`dotnet publish` overwrites `web.config`. After publishing Edge, always verify:
- `hostingModel="inprocess"` is present
- `maxQueryString="16384"` is set
- Production appsettings.json has correct ports (6000/6001)
