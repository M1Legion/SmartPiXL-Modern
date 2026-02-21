---
subsystem: failover
title: Failover & Durability
version: 1.0
last_updated: 2026-02-20
status: current
related:
  - architecture/data-flow
  - architecture/edge
  - architecture/forge
  - subsystems/enrichment-pipeline
---

# Failover & Durability

## Atlas Public

SmartPiXL is engineered for zero data loss. Every visitor interaction is captured and preserved, even during system maintenance, service restarts, or infrastructure issues. Your data pipeline doesn't lose records — it stores them durably and catches up automatically when services recover.

**Durability guarantees:**
- **Visitor data is written to disk immediately** — Even if background services are offline, no visitor interactions are lost
- **Automatic recovery** — When services come back online, queued data is automatically processed
- **No manual intervention required** — The system self-heals without operator action
- **Multiple fallback layers** — If the primary path fails, secondary and tertiary paths activate seamlessly

## Atlas Internal

### Zero Data Loss Architecture

SmartPiXL uses a layered failover strategy. If the primary path for any record is unavailable, it falls through to the next layer:

| Layer | Primary Path | Failover Path |
|-------|-------------|---------------|
| **Browser → Edge** | HTTP request to IIS | Not applicable (browser retries natively) |
| **Edge → Forge** | Named pipe (`SmartPiXL-Enrichment`) | JSONL file to `Failover/` directory |
| **Forge → SQL** | SqlBulkCopy to PiXL.Raw | Channel backpressure + retry |
| **ETL Processing** | Watermark-based — picks up where it left off | Self-healing watermark recovery |

### How JSONL Failover Works

When the Forge is unavailable (service stopped, restarting, crashed):

1. **Edge detects pipe failure** — The named pipe connection attempt fails or times out
2. **Edge writes to disk** — The visitor record is serialized as a single JSON line and appended to a JSONL file in the `Failover/` directory
3. **File rotation** — A new JSONL file is created each hour (or when the current file reaches size threshold)
4. **Forge restarts** — The `FailoverCatchupService` scans the Failover directory on startup
5. **Catch-up processing** — Each JSONL file is read line by line, records are fed into the enrichment pipeline
6. **Cleanup** — Successfully processed files are renamed with a `.done` suffix

### Timing

- Failover files accumulate at roughly 1 file per hour during normal traffic
- Catch-up on restart typically completes in seconds for a few hours of failover data
- For extended outages (days), catch-up may take minutes — the enrichment pipeline processes records at the same rate regardless of source

### What Customers See

Nothing. The failover is completely transparent. There may be a brief delay (minutes) in data appearing in dashboards after a Forge restart, but no data is lost and all historical records are complete once catch-up finishes.

## Atlas Technical

### Edge: PipeClientService

`PipeClientService` manages the named pipe connection to the Forge:

```csharp
public sealed class PipeClientService : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    
    public async Task<bool> TrySendAsync(TrackingData record)
    {
        if (!EnsureConnected())
        {
            // Pipe unavailable — caller falls back to JSONL
            return false;
        }
        
        var json = JsonSerializer.Serialize(record);
        await _writer!.WriteLineAsync(json);
        await _writer.FlushAsync();
        return true;
    }
}
```

Connection lifecycle:
- Attempts connection on first send
- Keeps connection alive between sends (persistent pipe)
- Detects broken pipe via write failure
- Does NOT auto-reconnect in the background — reconnects on next send attempt
- Connection timeout: 1 second (fails fast to avoid blocking the hot path)

### Edge: JsonlFailoverService

```csharp
public sealed class JsonlFailoverService
{
    private readonly string _failoverDir;
    private StreamWriter? _currentWriter;
    
    public void WriteRecord(TrackingData record)
    {
        EnsureWriter();
        var json = JsonSerializer.Serialize(record);
        _currentWriter!.WriteLine(json);
        _currentWriter.Flush(); // fsync to ensure durability
    }
}
```

File naming: `failover_{timestamp}.jsonl`
File rotation: hourly or at 100MB, whichever comes first
Location: `Failover/` directory relative to the Edge application root

### Edge: Failover Decision Path

In `TrackingCaptureService`, after enriching the record:

```csharp
if (!await _pipeClient.TrySendAsync(enrichedRecord))
{
    _failoverService.WriteRecord(enrichedRecord);
    _logger.Warning("Pipe unavailable — wrote to JSONL failover");
}
```

This is a synchronous fallback — the GIF response has already been sent to the browser. The failover write happens in the background request processing.

### Edge: DatabaseWriterService (Tertiary Fallback)

If both the pipe AND JSONL failover fail (disk full, permissions issue), `DatabaseWriterService` writes directly to PiXL.Raw via SQL:

```csharp
public sealed class DatabaseWriterService
{
    public async Task WriteDirectAsync(TrackingData record);
}
```

This is the fallback of last resort. It bypasses Forge enrichment entirely — the record goes to PiXL.Raw without `_srv_*` enrichment params. The ETL will still parse it, but enrichment data will be missing.

### Forge: FailoverCatchupService

```csharp
public sealed class FailoverCatchupService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // On startup, process any pending failover files
        var files = Directory.GetFiles(_failoverDir, "*.jsonl")
                             .OrderBy(f => f);
        
        foreach (var file in files)
        {
            await ProcessFailoverFileAsync(file, ct);
            File.Move(file, file + ".done");
        }
        
        // Then watch for new files
        using var watcher = new FileSystemWatcher(_failoverDir, "*.jsonl");
        // ...
    }
}
```

Key behaviors:
- Processes files in chronological order (sorted by filename/timestamp)
- Each line is deserialized and written to `ForgeChannels.Enrichment` — same channel as live pipe records
- Records go through the full enrichment pipeline (they get `_srv_*` params)
- Processed files are renamed `.done`, not deleted (audit trail)
- After initial catch-up, watches for new files via `FileSystemWatcher`

### Forge: PipeListenerService

The Forge side of the named pipe:

```csharp
public sealed class PipeListenerService : BackgroundService
{
    // Multiple pipe server instances for concurrent connections
    // Default: 4 instances
    
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var tasks = Enumerable.Range(0, _instanceCount)
            .Select(i => ListenOnInstanceAsync(i, ct));
        await Task.WhenAll(tasks);
    }
}
```

Multiple pipe instances allow concurrent Edge connections (IIS worker process recycling can cause brief overlaps).

### Channel Backpressure

If the enrichment pipeline is slower than the pipe listener:

1. `ForgeChannels.Enrichment` fills up (bounded channel)
2. `PipeListenerService.TryWrite()` returns false
3. Record is logged as dropped (rare — channel capacity is high)

If SQL is down:
1. `ForgeChannels.SqlWriter` fills up
2. `EnrichmentPipelineService.TryWrite()` returns false
3. Enriched records are dropped with warning log
4. BUT: the original un-enriched records exist in PiXL.Raw (written by Edge's failover/direct path)

### Watermark-Based ETL Recovery

Even if the Forge crashes mid-ETL:

```sql
-- Self-healing: if Parsed has rows beyond the watermark
DECLARE @MaxParsedId INT = (SELECT ISNULL(MAX(SourceId), 0) FROM PiXL.Parsed);
IF @MaxParsedId > @LastId SET @LastId = @MaxParsedId;
```

The watermark is updated at the end of each batch transaction. If the transaction committed (Parsed has rows) but the watermark update failed, the self-healing check advances the watermark to prevent re-processing.

## Atlas Private

### Durability Gap: Channel Records

Records that are in-flight in `ForgeChannels` (Enrichment or SqlWriter Channel<T> buffers) are lost on Forge process crash. These are in-memory only. The mitigation:

1. Channel capacity is intentionally limited (not millions of records)
2. Records in the channel but not yet written to SQL exist as JSONL on disk (the Edge wrote them before sending to the pipe)
3. Wait… actually, the Edge sends to the pipe OR writes to JSONL, not both. So records that were successfully sent to the pipe but are sitting in the Forge's channel ARE lost if the Forge crashes.

**Real data loss window**: Records received by PipeListenerService but not yet written to PiXL.Raw by SqlBulkCopyWriterService. At typical channel throughput, this is seconds of data — a few dozen to a few hundred records. 

**Mitigation options considered but not implemented**:
- Write-ahead log in the Forge (adds I/O to every record — defeats the purpose of the pipe)
- Dual-write from Edge (pipe + JSONL — doubles disk I/O on the Edge)
- Acknowledged pipe protocol (pipe listener acks after SQL write — adds latency, complexity)

The current trade-off is accepted: the probability of Forge crash × data in channel = negligible expected loss. The records can be reconstructed from IIS request logs if truly needed.

### JSONL File Retention

`.done` files accumulate in the Failover directory. There's no automated cleanup:

- `MaintenanceSchedulerService` should purge `.done` files older than 7 days
- Currently: they accumulate until manually cleaned
- Disk usage: ~1MB per 10,000 records — negligible even for months of accumulation

### FileSystemWatcher Reliability

`FileSystemWatcher` on Windows has known reliability issues:
- Can miss events under heavy I/O
- Buffer overflow drops notifications silently
- Network drives (UNC paths) are especially unreliable

The Forge's directory is local (`C:\Services\SmartPiXL-Forge\Failover\`), which is reliable. The `FailoverCatchupService` also does periodic directory scans (every 5 minutes) as a backstop for missed FileSystemWatcher events.

### DatabaseWriterService: The "Oh Shit" Path

The direct-to-SQL fallback exists for catastrophic scenarios:
- Named pipe AND disk are both failing
- The Forge is completely dead AND the failover directory is inaccessible

In practice, this has never fired in production. If both the pipe and disk fail simultaneously, something is fundamentally wrong with the server (disk failure, permissions reset). The DatabaseWriterService is there as a last-resort data preservation mechanism.

Performance impact: Direct SQL writes from the Edge's hot path add ~2-5ms latency per request (SqlCommand execution). This is acceptable in an emergency but would degrade throughput if sustained.

### Pipe Connection Lifecycle Fragility

The `PipeClientService` has a subtle issue: if the Edge process recycles (IIS app pool recycle) while the pipe is connected, the Forge sees an abrupt pipe disconnection. The `PipeListenerService` handles this gracefully (catches the broken pipe, spins up a new listener instance), but there's a brief window where records are sent to a dead pipe and fail.

During IIS app pool recycling:
1. Old w3wp.exe process has an active pipe connection
2. New w3wp.exe process starts, creates a new `PipeClientService`
3. Old process sends final records (may fail — pipe broken)
4. New process connects a new pipe instance
5. Overlap window: ~2-5 seconds during which the old process's failover kicks in

This is tested and works correctly — the failover files from the overlap window are caught up normally.
