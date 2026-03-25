---
title: "F3: SQL Writer"
node: forge.f3-sql-writer
nodeType: subsystem
status: current
related:
  - systems/forge
  - subsystems/f2-enrichment
  - subsystems/f5-etl
---

# F3: SQL Writer

## Atlas Public

After every visitor record is enriched with intelligence data, it needs to be stored reliably. The SQL Writer handles this — writing enriched records to the database in high-performance batches so they're immediately available for analysis and reporting.

## Atlas Internal

### What F3 Does

The SQL Writer reads fully-enriched records from the enrichment channel and writes them to `PiXL.Parsed` using `SqlBulkCopy`. Each record has 231 columns — the original browser data plus all server-side enrichments.

Batch writing is critical because:
- Individual INSERT statements for 231 columns would be extremely slow
- `SqlBulkCopy` provides 100x faster throughput than parameterized inserts
- Batching reduces SQL Server lock contention

### Health Probes

| Probe | What It Checks |
|-------|---------------|
| **BulkCopy** | SqlBulkCopy batches writing to PiXL.Parsed |

## Atlas Technical

### DatabaseWriterService

Reads from `ForgeChannels.SqlWriter` channel. Accumulates records into batches and writes via `SqlBulkCopy` to `PiXL.Parsed`.

```
ForgeChannels.SqlWriter (Channel<TrackingData>)
  → DatabaseWriterService
    → DataTable (batch accumulation)
    → SqlBulkCopy → PiXL.Parsed (231 columns)
```

### Target Table

`PiXL.Parsed` — 231 columns, one row per enriched visitor hit. This is the primary fact table that all downstream ETL reads from.

### Error Handling

If SQL Server is temporarily unavailable, the writer retries with backoff. If the circuit breaker trips, records fall through to Forge-side failover (F4) for later replay.

## Atlas Private

### Key Files

| File | Purpose |
|------|---------|
| `SmartPiXL.Forge/Services/DatabaseWriterService.cs` | SqlBulkCopy batch writer |
| `SmartPiXL.Forge/Services/ForgeChannels.cs` | SqlWriter channel definition |
