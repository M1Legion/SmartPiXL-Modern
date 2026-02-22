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

    // Channel depths (sampled, not accumulated)
    private int _enrichmentChannelDepth;
    private int _sqlWriterChannelDepth;

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

    /// <summary>Updates the sampled channel depths (called periodically).</summary>
    public void SampleChannelDepths(int enrichmentDepth, int sqlWriterDepth)
    {
        Volatile.Write(ref _enrichmentChannelDepth, enrichmentDepth);
        Volatile.Write(ref _sqlWriterChannelDepth, sqlWriterDepth);
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

            // Channel depths (sampled, not reset)
            EnrichmentChannelDepth = Volatile.Read(ref _enrichmentChannelDepth),
            SqlWriterChannelDepth = Volatile.Read(ref _sqlWriterChannelDepth),
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

    // ── Channels ──
    public int EnrichmentChannelDepth { get; init; }
    public int SqlWriterChannelDepth { get; init; }

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
            $"  SQL→DB     {SqlCount,8:N0} rec  {sqlRps,8:N1}/s  avg {SqlAvgMs,8:N1}ms  min {SqlMinMs,8:N1}ms  max {SqlMaxMs,8:N1}ms  batches {SqlBatchCount}  avg/rec {SqlAvgPerRecordUs,8:N1}μs  ~{SqlAvgBatchSize:N1}rec/batch  fails {SqlFailures}\n" +
            $"  CHANNELS   enrich={EnrichmentChannelDepth:N0}  sqlWriter={SqlWriterChannelDepth:N0}";
    }
}
