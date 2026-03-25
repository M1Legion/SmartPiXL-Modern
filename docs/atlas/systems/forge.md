---
title: SmartPiXL Forge
node: forge
nodeType: system
status: current
related:
  - platform/smartpixl
  - systems/edge
  - systems/sentinel
---

# SmartPiXL Forge

## Atlas Public

The SmartPiXL Forge is the enrichment engine — the component that transforms raw visitor data into actionable intelligence. While the Edge captures data in milliseconds, the Forge performs deep analysis with no time constraints.

**What the Forge does for you:**

- **Geographic verification** — confirms where your visitors actually are, not just where they say they are
- **Advanced bot detection** — checks visitors against databases of 10,000+ known bot patterns
- **Device intelligence** — identifies the actual device, operating system, and browser in structured detail
- **Cross-customer intelligence** — detects when the same visitor is hitting multiple websites simultaneously
- **Behavioral analysis** — detects replayed mouse movements and cultural fingerprint inconsistencies
- **Lead quality scoring** — rates each visitor's likelihood of being a genuine prospect

The Forge runs 24/7 as a background service, continuously processing and enriching every visitor record.

## Atlas Internal

### What the Forge Does

Think of the Edge as the receptionist and the Forge as the back office. The Edge takes the call quickly, the Forge does the research.

The Forge is a Windows Service organized into seven subsystems (F1–F7):

| Subsystem | Name | Purpose |
|-----------|------|---------|
| **F1** | Ingest | Named pipe server + enrichment channel intake |
| **F2** | Enrichment Engine | 16 enrichment services in an adaptive worker pool |
| **F3** | SQL Writer | SqlBulkCopy from enrichment channel to PiXL.Parsed |
| **F4** | Failover & Replay | Edge failover + Forge failover + dead-letter replay |
| **F5** | ETL Pipeline | Identity resolution every 60 seconds |
| **F6** | Background IP | Off-hot-path DNS and WHOIS enrichment workers |
| **F7** | Data Sync | Company/pixel sync and IP data acquisition |

### Enrichment Tiers

**Tier 1 — Library Lookups (milliseconds)**
- Known bot database (10,000+ patterns)
- User-agent parsing (browser, OS, device identification)
- Reverse DNS lookup (ISP vs cloud hosting)
- MaxMind GeoIP2 (precise offline geographic data)
- WHOIS ASN lookup (network ownership)

**Tier 2 — Cross-Request Intelligence (real-time)**
- Session stitching across page views
- Cross-customer intelligence (same visitor, multiple sites)
- Device affluence estimation (hardware signals → value tier)
- Dead Internet Index (per-customer bot traffic trend)

**Tier 3 — Asymmetric Detection**
- Cultural fingerprint analysis (French fonts + Vietnamese language + US IP = VPN)
- Device age estimation (old GPU + new browser + datacenter IP = bot)
- Impossible combination detection (mobile UA + 4K screen + mouse = fake)
- Behavioral replay detection (identical mouse paths = recorded behavior)
- Contradiction matrix (conflicting device/network signals)

**Final Scoring**
- Lead quality composite scoring (0–100)

### Health Probes

Forge subsystems expose 27 health probes monitored by Sentinel. See each subsystem page (F1–F7) for probe details.

## Atlas Technical

### Project Structure

```
SmartPiXL.Forge/
├── Program.cs                           # Composition root, service registration
├── appsettings.json                     # Port 7100 (loopback health only)
├── Services/
│   ├── PipeListenerService.cs           # F1: Named pipe server
│   ├── EnrichmentPipelineService.cs     # F2: 16-service enrichment pipeline
│   ├── DatabaseWriterService.cs         # F3: SqlBulkCopy writer
│   ├── FailoverWriterService.cs         # F4: Forge-side failover
│   ├── FailoverCatchupService.cs        # F4: Replay on restart
│   ├── EtlOrchestratorService.cs        # F5: ETL scheduling
│   ├── BackgroundDnsService.cs          # F6: DNS enrichment workers
│   ├── BackgroundWhoisService.cs        # F6: WHOIS enrichment workers
│   └── DataSyncService.cs              # F7: Company/pixel sync
└── bin/                                 # Build output
```

### Production Location

| Setting | Value |
|---------|-------|
| Service Name | `SmartPiXL-Forge` |
| Path | `C:\Services\SmartPiXL-Forge\` |
| Port | 7100 (loopback health endpoint only) |
| Pipe Name | `SmartPiXL-Enrichment` |

### Channel Architecture

```
PipeListenerService → ForgeChannels.Enrichment → EnrichmentPipelineService
                                                          ↓
FailoverCatchupService ───────────────────────→ ForgeChannels.Enrichment
                                                          ↓
                                                 ForgeChannels.SqlWriter
                                                          ↓
                                              DatabaseWriterService (SqlBulkCopy)
```

All inter-service communication uses `Channel<TrackingData>` for bounded, backpressure-aware async flow.

## Atlas Private

### Key Design Decisions

- **Named pipe over HTTP** — Lower latency, no TCP overhead, same-machine optimization.
- **Channel<T> over queues** — Built-in .NET bounded channels with backpressure. No external message broker.
- **SqlBulkCopy over ORM** — Writes 231 columns per record. Batch insert is 100x faster than individual inserts.
- **Worker service over console** — Runs as a Windows Service with proper lifecycle management.
- **Append-only enrichment** — Forge never modifies original browser data. Server enrichments are added as `_srv_*` fields.

### Monitoring

Forge exposes health data at `GET http://127.0.0.1:7100/health` which Sentinel polls. The response includes all subsystem and probe statuses that feed the Tron health tree.
