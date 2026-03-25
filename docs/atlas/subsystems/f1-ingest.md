---
title: "F1: Ingest"
node: forge.f1-ingest
nodeType: subsystem
status: current
related:
  - systems/forge
  - subsystems/f2-enrichment
  - subsystems/f4-failover
---

# F1: Ingest

## Atlas Public

The Ingest subsystem is the entry point for all visitor data reaching the Forge. It receives records from the Edge server at wire speed through a direct internal connection and feeds them into the enrichment pipeline.

Every visitor interaction flows through F1 — it's the gateway that ensures no data is lost and all records enter the enrichment pipeline in order.

## Atlas Internal

### How Ingest Works

F1 has two components:

1. **Pipe Listener** — A named pipe server (`SmartPiXL-Enrichment`) that accepts connections from the Edge. Each connection delivers JSON-serialized `TrackingData` records, one per line. The pipe listener deserializes each record and pushes it into the enrichment channel.

2. **Enrichment Channel** — A bounded `Channel<TrackingData>` that buffers records between the pipe listener and the enrichment workers. The channel provides backpressure — if enrichment workers can't keep up, the channel applies bounded waiting rather than dropping records.

### Data Flow

```
Edge → NamedPipeClientStream → Forge PipeListenerService
  → JSON deserialize → Channel<TrackingData> → EnrichmentPipelineService
```

### Health Probes

| Probe | What It Checks |
|-------|---------------|
| **Pipe Listener** | Named pipe server accepting connections from Edge |
| **Enrichment Channel** | Channel between pipe listener and enrichment workers |

## Atlas Technical

### PipeListenerService

The pipe listener creates a `NamedPipeServerStream` and continuously accepts connections. Each connection is handled in a loop reading JSON lines. When the Forge service stops, the pipe server shuts down gracefully, allowing in-flight records to complete.

### Channel Bounds

The enrichment channel uses `BoundedChannelOptions` with a configurable capacity. When the channel is full, the pipe listener waits (backpressure) rather than dropping records. This prevents memory exhaustion under burst traffic while guaranteeing no data loss.

## Atlas Private

### Key Files

| File | Purpose |
|------|---------|
| `SmartPiXL.Forge/Services/PipeListenerService.cs` | Named pipe server |
| `SmartPiXL.Forge/Services/ForgeChannels.cs` | Channel definitions (Enrichment, SqlWriter) |
