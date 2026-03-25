---
title: "F4: Failover & Replay"
node: forge.f4-failover
nodeType: subsystem
status: current
related:
  - systems/forge
  - systems/edge
  - subsystems/f1-ingest
---

# F4: Failover & Replay

## Atlas Public

SmartPiXL is engineered for zero data loss. Every visitor interaction is captured and preserved, even during system maintenance, service restarts, or infrastructure issues. The Failover & Replay subsystem ensures data durability across the entire pipeline.

**Durability guarantees:**
- **Visitor data is written to disk immediately** if downstream services are unavailable
- **Automatic recovery** — when services come back, queued data is processed automatically
- **No manual intervention** — the system self-heals without operator action
- **Multiple fallback layers** — primary, secondary, and tertiary paths activate seamlessly

## Atlas Internal

### Zero Data Loss Architecture

SmartPiXL uses layered failover. If the primary path is unavailable, records fall through to the next layer:

| Layer | Primary Path | Failover Path |
|-------|-------------|---------------|
| **Edge → Forge** | Named pipe (`SmartPiXL-Enrichment`) | JSONL file to `Failover/` directory |
| **Forge → SQL** | SqlBulkCopy to PiXL.Parsed | Channel backpressure + Forge failover JSONL |
| **ETL Processing** | Watermark-based — picks up where it left off | Self-healing watermark recovery |

### How JSONL Failover Works

When the Forge is unavailable (service stopped, restarting, crashed):

1. **Edge detects pipe failure** — named pipe connection attempt fails or times out
2. **Edge writes to disk** — visitor record serialized as a JSON line, appended to a JSONL file in `Failover/`
3. **File rotation** — new JSONL file created each hour or at size threshold
4. **Forge restarts** — `FailoverCatchupService` scans the Failover directory
5. **Catch-up processing** — each JSONL file read line by line, records fed into enrichment pipeline
6. **Cleanup** — successfully processed files renamed with `.done` suffix

### Unified Replay Service

The Replay Service handles three types of replay:
1. **Edge failover** — JSONL files written by Edge when pipe was down
2. **Forge failover** — JSONL files written by Forge when SQL was down
3. **Dead-letter** — Records that failed enrichment, retried after a cooling period

### Health Probes

| Probe | What It Checks |
|-------|---------------|
| **Failover Writer** | Enriched JSONL failover writer for SQL circuit breaker events |
| **Replay Service** | Unified replay for Edge failover + Forge failover + dead-letter |

## Atlas Technical

### Edge-Side Failover

`FailoverWriterService` in the Edge project writes JSONL files to `C:\inetpub\Smartpixl.info\Failover\`. Files are named with timestamp suffixes for chronological ordering.

### Forge-Side Failover

When `DatabaseWriterService` (F3) can't reach SQL Server, it writes enriched records to Forge-side JSONL files in the Forge's failover directory. These are replayed when SQL connectivity returns.

### Catch-Up Timing

- Failover files accumulate at ~1 file per hour during normal traffic
- Catch-up on restart typically completes in seconds for a few hours of failover data
- Extended outages (days): catch-up takes minutes at standard enrichment rate

## Atlas Private

### Key Files

| File | Purpose |
|------|---------|
| `SmartPiXL/Services/FailoverWriterService.cs` | Edge-side JSONL writer |
| `SmartPiXL.Forge/Services/FailoverWriterService.cs` | Forge-side JSONL writer |
| `SmartPiXL.Forge/Services/FailoverCatchupService.cs` | Replay on Forge restart |
| `SmartPiXL.Forge/Services/ReplayService.cs` | Unified replay (Edge + Forge + dead-letter) |

### Failover Directory

Edge failover: `C:\inetpub\Smartpixl.info\Failover\`
Forge failover: `C:\Services\SmartPiXL-Forge\Failover\`
