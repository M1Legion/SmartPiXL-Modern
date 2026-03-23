using System.Diagnostics;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// FORGE METRICS — Lock-free performance counters for every pipeline handoff.
//
// PIPELINE STAGES (each tracked independently):
//   1. PipeDeserialize  — Pipe read + JSON deserialize → Enrichment channel
//   2. Enrichment       — Enrichment channel read → enrich → SQL channel
//   3. SqlBulkCopy      — SQL channel drain → batch → SqlBulkCopy
//
// DESIGN:
//   All counters use Interlocked operations — zero locking, safe for
//   concurrent writers (multiple pipe instances, enrichment workers).
//   Snapshot() returns a frozen copy and atomically resets all counters
//   for the next reporting window.
//
//   Ticks are Stopwatch ticks (high-resolution, ~100ns on Windows).
//   Convert to microseconds: ticks * 1_000_000 / Stopwatch.Frequency
// ============================================================================

/// <summary>
/// Lock-free performance metrics for the Forge pipeline.
/// Registered as a singleton. Each pipeline stage records timing via
/// <see cref="Record"/> after processing each record or batch.
/// </summary>
public sealed class ForgeMetrics
{
    // ── Stage counters ────────────────────────────────────────────────
    // Each stage has: record count, total ticks, min ticks, max ticks

    // Stage 1: Pipe → Enrichment channel (per record)
    private long _pipeCount;
    private long _pipeTotalTicks;
    private long _pipeMinTicks = long.MaxValue;
    private long _pipeMaxTicks;
    private long _pipeDrops;

    // Stage 2: Enrichment channel → SQL channel (per record)
    private long _enrichCount;
    private long _enrichTotalTicks;
    private long _enrichMinTicks = long.MaxValue;
    private long _enrichMaxTicks;
    private long _enrichDrops;

    // Stage 3: SQL channel → DB (per batch)
    private long _sqlCount;        // records written (sum of batch sizes)
    private long _sqlBatchCount;   // number of batches
    private long _sqlTotalTicks;   // total time across all batches
    private long _sqlMinTicks = long.MaxValue;
    private long _sqlMaxTicks;
    private long _sqlFailures;

    // Batch fill: track how full batches are relative to max batch size
    private long _batchFillSum;       // sum of (batch.Count / maxBatchSize * 100)
    private long _batchFillCount;     // number of batches sampled

    // Failover: records diverted to JSONL on disk (channel full or circuit open)
    private long _failoverCount;

    // Replay: files and records recovered from failover / dead-letter
    private long _replayFiles;
    private long _replayRecords;

    // Pipe connections: visibility into Edge↔Forge pipe health
    private long _pipeConnects;
    private long _pipeDisconnects;

    // Channel depths (sampled, not accumulated)
    private int _enrichmentChannelDepth;
    private int _sqlWriterChannelDepth;

    // Lane 3: Background IP enrichment
    private long _bgIpEnqueued;
    private long _bgIpProcessed;
    private long _bgIpDupSkipped;
    private long _bgIpDnsLookups;
    private long _bgIpWhoisLookups;
    private int _bgIpChannelDepth;
    private int _bgIpDedupCacheSize;

    /// <summary>Starts a high-resolution timer. Call <see cref="Record"/> with the result.</summary>
    public static long StartTimer() => Stopwatch.GetTimestamp();

    /// <summary>Records a completed operation for a pipeline stage.</summary>
    public void Record(Stage stage, long startTimestamp, int count = 1)
    {
        var elapsed = Stopwatch.GetTimestamp() - startTimestamp;

        switch (stage)
        {
            case Stage.PipeDeserialize:
                Interlocked.Add(ref _pipeCount, count);
                Interlocked.Add(ref _pipeTotalTicks, elapsed);
                InterlockedMin(ref _pipeMinTicks, elapsed);
                InterlockedMax(ref _pipeMaxTicks, elapsed);
                break;

            case Stage.Enrichment:
                Interlocked.Add(ref _enrichCount, count);
                Interlocked.Add(ref _enrichTotalTicks, elapsed);
                InterlockedMin(ref _enrichMinTicks, elapsed);
                InterlockedMax(ref _enrichMaxTicks, elapsed);
                break;

            case Stage.SqlBulkCopy:
                Interlocked.Add(ref _sqlCount, count);
                Interlocked.Increment(ref _sqlBatchCount);
                Interlocked.Add(ref _sqlTotalTicks, elapsed);
                InterlockedMin(ref _sqlMinTicks, elapsed);
                InterlockedMax(ref _sqlMaxTicks, elapsed);
                break;
        }
    }

    /// <summary>Records a dropped record at a stage.</summary>
    public void RecordDrop(Stage stage)
    {
        switch (stage)
        {
            case Stage.PipeDeserialize: Interlocked.Increment(ref _pipeDrops); break;
            case Stage.Enrichment: Interlocked.Increment(ref _enrichDrops); break;
            case Stage.SqlBulkCopy: Interlocked.Increment(ref _sqlFailures); break;
        }
    }

    /// <summary>Records a record diverted to failover JSONL on disk.</summary>
    public void RecordFailover() => Interlocked.Increment(ref _failoverCount);

    /// <summary>Records the batch fill percentage for a completed batch.</summary>
    public void RecordBatchFill(int batchCount, int maxBatchSize)
    {
        var pct = maxBatchSize > 0 ? (long)(batchCount * 100 / maxBatchSize) : 0;
        Interlocked.Add(ref _batchFillSum, pct);
        Interlocked.Increment(ref _batchFillCount);
    }

    /// <summary>Records a pipe client connection (Edge connected to this Forge instance).</summary>
    public void RecordPipeConnect() => Interlocked.Increment(ref _pipeConnects);

    /// <summary>Records a pipe client disconnection.</summary>
    public void RecordPipeDisconnect() => Interlocked.Increment(ref _pipeDisconnects);

    /// <summary>Records a replayed failover file and the number of records recovered.</summary>
    public void RecordReplay(int recordCount)
    {
        Interlocked.Increment(ref _replayFiles);
        Interlocked.Add(ref _replayRecords, recordCount);
    }

    /// <summary>Updates the sampled channel depths (called periodically).</summary>
    public void SampleChannelDepths(int enrichmentDepth, int sqlWriterDepth)
    {
        Volatile.Write(ref _enrichmentChannelDepth, enrichmentDepth);
        Volatile.Write(ref _sqlWriterChannelDepth, sqlWriterDepth);
    }

    // ── Lane 3: Background IP enrichment ──────────────────────────────

    /// <summary>Records an IP enqueued to the background enrichment channel.</summary>
    public void RecordBgIpEnqueue() => Interlocked.Increment(ref _bgIpEnqueued);

    /// <summary>Records an IP successfully processed by a background worker.</summary>
    public void RecordBgIpProcessed() => Interlocked.Increment(ref _bgIpProcessed);

    /// <summary>Records an IP skipped due to dedup cache hit.</summary>
    public void RecordBgIpDupSkip() => Interlocked.Increment(ref _bgIpDupSkipped);

    /// <summary>Records a DNS lookup performed by a background worker.</summary>
    public void RecordBgIpDnsLookup() => Interlocked.Increment(ref _bgIpDnsLookups);

    /// <summary>Records a WHOIS lookup performed by a background worker.</summary>
    public void RecordBgIpWhoisLookup() => Interlocked.Increment(ref _bgIpWhoisLookups);

    /// <summary>Updates the sampled Lane 3 depths (called periodically).</summary>
    public void SampleBgIpDepths(int channelDepth, int dedupCacheSize)
    {
        Volatile.Write(ref _bgIpChannelDepth, channelDepth);
        Volatile.Write(ref _bgIpDedupCacheSize, dedupCacheSize);
    }

    /// <summary>
    /// Takes a frozen snapshot of all counters and atomically resets them.
    /// Returns the metrics for the elapsed window.
    /// </summary>
    public MetricsSnapshot Snapshot()
    {
        var snap = new MetricsSnapshot
        {
            // Pipe
            PipeCount = Interlocked.Exchange(ref _pipeCount, 0),
            PipeTotalTicks = Interlocked.Exchange(ref _pipeTotalTicks, 0),
            PipeMinTicks = Interlocked.Exchange(ref _pipeMinTicks, long.MaxValue),
            PipeMaxTicks = Interlocked.Exchange(ref _pipeMaxTicks, 0),
            PipeDrops = Interlocked.Exchange(ref _pipeDrops, 0),

            // Enrichment
            EnrichCount = Interlocked.Exchange(ref _enrichCount, 0),
            EnrichTotalTicks = Interlocked.Exchange(ref _enrichTotalTicks, 0),
            EnrichMinTicks = Interlocked.Exchange(ref _enrichMinTicks, long.MaxValue),
            EnrichMaxTicks = Interlocked.Exchange(ref _enrichMaxTicks, 0),
            EnrichDrops = Interlocked.Exchange(ref _enrichDrops, 0),

            // SQL
            SqlCount = Interlocked.Exchange(ref _sqlCount, 0),
            SqlBatchCount = Interlocked.Exchange(ref _sqlBatchCount, 0),
            SqlTotalTicks = Interlocked.Exchange(ref _sqlTotalTicks, 0),
            SqlMinTicks = Interlocked.Exchange(ref _sqlMinTicks, long.MaxValue),
            SqlMaxTicks = Interlocked.Exchange(ref _sqlMaxTicks, 0),
            SqlFailures = Interlocked.Exchange(ref _sqlFailures, 0),

            // Failover
            FailoverCount = Interlocked.Exchange(ref _failoverCount, 0),

            // Batch fill
            BatchFillSum = Interlocked.Exchange(ref _batchFillSum, 0),
            BatchFillCount = Interlocked.Exchange(ref _batchFillCount, 0),

            // Replay
            ReplayFiles = Interlocked.Exchange(ref _replayFiles, 0),
            ReplayRecords = Interlocked.Exchange(ref _replayRecords, 0),

            // Pipe connections
            PipeConnects = Interlocked.Exchange(ref _pipeConnects, 0),
            PipeDisconnects = Interlocked.Exchange(ref _pipeDisconnects, 0),

            // Channel depths (sampled, not reset)
            EnrichmentChannelDepth = Volatile.Read(ref _enrichmentChannelDepth),
            SqlWriterChannelDepth = Volatile.Read(ref _sqlWriterChannelDepth),

            // Lane 3: Background IP
            BgIpEnqueued = Interlocked.Exchange(ref _bgIpEnqueued, 0),
            BgIpProcessed = Interlocked.Exchange(ref _bgIpProcessed, 0),
            BgIpDupSkipped = Interlocked.Exchange(ref _bgIpDupSkipped, 0),
            BgIpDnsLookups = Interlocked.Exchange(ref _bgIpDnsLookups, 0),
            BgIpWhoisLookups = Interlocked.Exchange(ref _bgIpWhoisLookups, 0),
            BgIpChannelDepth = Volatile.Read(ref _bgIpChannelDepth),
            BgIpDedupCacheSize = Volatile.Read(ref _bgIpDedupCacheSize),
        };

        return snap;
    }

    // ── Interlocked min/max helpers ───────────────────────────────────

    private static void InterlockedMin(ref long target, long value)
    {
        long current;
        do
        {
            current = Volatile.Read(ref target);
            if (value >= current) return;
        } while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static void InterlockedMax(ref long target, long value)
    {
        long current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current) return;
        } while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}

/// <summary>Pipeline stages for metrics recording.</summary>
public enum Stage
{
    /// <summary>Pipe read + JSON deserialize → enrichment channel write.</summary>
    PipeDeserialize,

    /// <summary>Enrichment channel read → enrich record → SQL channel write.</summary>
    Enrichment,

    /// <summary>SQL channel drain → batch → SqlBulkCopy → DB.</summary>
    SqlBulkCopy
}

/// <summary>
/// Frozen snapshot of all pipeline metrics for a reporting window.
/// </summary>
public readonly record struct MetricsSnapshot
{
    private static readonly double s_ticksPerUs = Stopwatch.Frequency / 1_000_000.0;
    private static readonly double s_ticksPerMs = Stopwatch.Frequency / 1_000.0;

    // ── Pipe ──
    public long PipeCount { get; init; }
    public long PipeTotalTicks { get; init; }
    public long PipeMinTicks { get; init; }
    public long PipeMaxTicks { get; init; }
    public long PipeDrops { get; init; }

    // ── Enrichment ──
    public long EnrichCount { get; init; }
    public long EnrichTotalTicks { get; init; }
    public long EnrichMinTicks { get; init; }
    public long EnrichMaxTicks { get; init; }
    public long EnrichDrops { get; init; }

    // ── SQL ──
    public long SqlCount { get; init; }
    public long SqlBatchCount { get; init; }
    public long SqlTotalTicks { get; init; }
    public long SqlMinTicks { get; init; }
    public long SqlMaxTicks { get; init; }
    public long SqlFailures { get; init; }

    // ── Failover ──
    public long FailoverCount { get; init; }

    // ── Replay ──
    public long ReplayFiles { get; init; }
    public long ReplayRecords { get; init; }

    // ── Batch fill ──
    public long BatchFillSum { get; init; }
    public long BatchFillCount { get; init; }

    // ── Pipe connections ──
    public long PipeConnects { get; init; }
    public long PipeDisconnects { get; init; }

    // ── Channels ──
    public int EnrichmentChannelDepth { get; init; }
    public int SqlWriterChannelDepth { get; init; }

    // ── Lane 3: Background IP ──
    public long BgIpEnqueued { get; init; }
    public long BgIpProcessed { get; init; }
    public long BgIpDupSkipped { get; init; }
    public long BgIpDnsLookups { get; init; }
    public long BgIpWhoisLookups { get; init; }
    public int BgIpChannelDepth { get; init; }
    public int BgIpDedupCacheSize { get; init; }

    // ── Derived values (microseconds) ──
    public double PipeAvgUs => PipeCount > 0 ? PipeTotalTicks / (PipeCount * s_ticksPerUs) : 0;
    public double PipeMinUs => PipeMinTicks == long.MaxValue ? 0 : PipeMinTicks / s_ticksPerUs;
    public double PipeMaxUs => PipeMaxTicks / s_ticksPerUs;

    public double EnrichAvgUs => EnrichCount > 0 ? EnrichTotalTicks / (EnrichCount * s_ticksPerUs) : 0;
    public double EnrichMinUs => EnrichMinTicks == long.MaxValue ? 0 : EnrichMinTicks / s_ticksPerUs;
    public double EnrichMaxUs => EnrichMaxTicks / s_ticksPerUs;

    public double SqlAvgMs => SqlBatchCount > 0 ? SqlTotalTicks / (SqlBatchCount * s_ticksPerMs) : 0;
    public double SqlMinMs => SqlMinTicks == long.MaxValue ? 0 : SqlMinTicks / s_ticksPerMs;
    public double SqlMaxMs => SqlMaxTicks / s_ticksPerMs;
    public double SqlAvgPerRecordUs => SqlCount > 0 ? SqlTotalTicks / (SqlCount * s_ticksPerUs) : 0;
    public double SqlAvgBatchSize => SqlBatchCount > 0 ? (double)SqlCount / SqlBatchCount : 0;
    public double BatchFillPct => BatchFillCount > 0 ? (double)BatchFillSum / BatchFillCount : 0;

    /// <summary>
    /// Formats the snapshot as a compact multi-line log entry.
    /// </summary>
    public string Format(double windowSeconds)
    {
        var pipeRps = windowSeconds > 0 ? PipeCount / windowSeconds : 0;
        var enrichRps = windowSeconds > 0 ? EnrichCount / windowSeconds : 0;
        var sqlRps = windowSeconds > 0 ? SqlCount / windowSeconds : 0;

        return
            $"═══ FORGE METRICS ({windowSeconds:F0}s window) ═══\n" +
            $"  PIPE→CH    {PipeCount,8:N0} rec  {pipeRps,8:N1}/s  avg {PipeAvgUs,8:N1}μs  min {PipeMinUs,8:N1}μs  max {PipeMaxUs,8:N1}μs  drops {PipeDrops}\n" +
            $"  ENRICH→CH  {EnrichCount,8:N0} rec  {enrichRps,8:N1}/s  avg {EnrichAvgUs,8:N1}μs  min {EnrichMinUs,8:N1}μs  max {EnrichMaxUs,8:N1}μs  drops {EnrichDrops}\n" +
            $"  SQL→DB     {SqlCount,8:N0} rec  {sqlRps,8:N1}/s  avg {SqlAvgMs,8:N1}ms  min {SqlMinMs,8:N1}ms  max {SqlMaxMs,8:N1}ms  batches {SqlBatchCount}  avg/rec {SqlAvgPerRecordUs,8:N1}μs  ~{SqlAvgBatchSize:N1}rec/batch  fill {BatchFillPct:N0}%  fails {SqlFailures}\n" +
            $"  CHANNELS   enrich={EnrichmentChannelDepth:N0}  sqlWriter={SqlWriterChannelDepth:N0}" +
            (BgIpEnqueued > 0 || BgIpProcessed > 0
                ? $"\n  LANE3-IP   enq={BgIpEnqueued:N0}  proc={BgIpProcessed:N0}  dedup={BgIpDupSkipped:N0}  dns={BgIpDnsLookups:N0}  whois={BgIpWhoisLookups:N0}  ch={BgIpChannelDepth:N0}  seen={BgIpDedupCacheSize:N0}"
                : "") +
            (PipeConnects > 0 || PipeDisconnects > 0 ? $"  PIPE conn={PipeConnects} disconn={PipeDisconnects}" : "") +
            (FailoverCount > 0 ? $"  FAILOVER {FailoverCount:N0} rec" : "") +
            (ReplayFiles > 0 ? $"  REPLAY {ReplayFiles} file(s) {ReplayRecords:N0} rec" : "");
    }
}
