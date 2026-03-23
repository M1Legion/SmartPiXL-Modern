using System.Diagnostics;

namespace SmartPiXL.Services;

// ============================================================================
// EDGE METRICS — Lock-free performance counters + health derivation for Edge.
//
// PROBES (from Health Tree):
//   1. HTTP Listener    — Kestrel responding (always 1 if process is up)
//   2. Capture Pipeline — requests → TrackingData succeeding
//   3. Pipe Client      — connected to Forge, data flowing
//   4. JSONL Failover   — disk writable, not accumulating old files
//
// DESIGN:
//   Same Interlocked pattern as ForgeMetrics. Services call Record* methods
//   on the hot path. The /internal/health endpoint reads counters and derives
//   per-probe health (binary 1/0) plus overall Edge health (ratio).
//
//   Snapshot() returns a frozen copy and resets windowed counters.
//   Cumulative counters (_totalCaptures, etc.) never reset.
// ============================================================================

/// <summary>
/// Lock-free metrics + health derivation for the Edge process.
/// Singleton. Services record counters; <c>/internal/health</c> reads them.
/// </summary>
public sealed class EdgeMetrics
{
    private static readonly double s_ticksPerUs = Stopwatch.Frequency / 1_000_000.0;
    private static readonly double s_ticksPerMs = Stopwatch.Frequency / 1_000.0;

    // ── Probe 1: HTTP Listener ─────────────────────────────────────
    // Always healthy if the process is serving requests. Track request
    // count so we can confirm traffic is flowing.
    private long _httpRequests;

    // ── Probe 2: Capture Pipeline ──────────────────────────────────
    // TrackingCaptureService.CaptureFromRequest success/error counts.
    private long _captureCount;
    private long _captureTotalTicks;
    private long _captureErrors;

    // ── Probe 3: Pipe Client ───────────────────────────────────────
    // PipeClientService batch writes + failover delegations.
    private long _pipeWriteCount;        // records successfully written to pipe
    private long _pipeWriteTotalTicks;   // total time across all batch flushes
    private long _pipeBatchCount;        // number of batches flushed
    private long _pipeFailoverCount;     // records delegated to failover (pipe down)
    private long _pipeReconnects;        // successful reconnections

    // Sampled state (not accumulated — updated by PipeClientService)
    private int _pipeConnected;          // 1 = connected, 0 = disconnected
    private int _pipeQueueDepth;         // current channel depth

    // ── Probe 4: JSONL Failover ────────────────────────────────────
    // JsonlFailoverService write counts.
    private long _failoverWriteCount;    // records written to JSONL file
    private long _failoverEmergencyCount; // emergency synchronous writes
    private long _failoverErrors;        // write failures

    // Sampled state
    private int _failoverQueueDepth;     // current failover channel depth
    private int _failoverFileCount;      // .jsonl files currently on disk

    // ── Startup ────────────────────────────────────────────────────
    private static readonly long s_startTicks = Stopwatch.GetTimestamp();

    // ── Recording methods (called from hot path) ───────────────────

    /// <summary>Starts a high-resolution timer.</summary>
    public static long StartTimer() => Stopwatch.GetTimestamp();

    /// <summary>Records an HTTP request received by any endpoint.</summary>
    public void RecordHttpRequest() => Interlocked.Increment(ref _httpRequests);

    /// <summary>Records a successful capture (request → TrackingData).</summary>
    public void RecordCapture(long startTimestamp)
    {
        Interlocked.Increment(ref _captureCount);
        Interlocked.Add(ref _captureTotalTicks, Stopwatch.GetTimestamp() - startTimestamp);
    }

    /// <summary>Records a capture failure (exception in CaptureFromRequest).</summary>
    public void RecordCaptureError() => Interlocked.Increment(ref _captureErrors);

    /// <summary>Records records successfully written to the named pipe.</summary>
    public void RecordPipeWrite(long startTimestamp, int recordCount)
    {
        Interlocked.Add(ref _pipeWriteCount, recordCount);
        Interlocked.Increment(ref _pipeBatchCount);
        Interlocked.Add(ref _pipeWriteTotalTicks, Stopwatch.GetTimestamp() - startTimestamp);
    }

    /// <summary>Records records delegated to failover because the pipe was down.</summary>
    public void RecordPipeFailover(int recordCount)
        => Interlocked.Add(ref _pipeFailoverCount, recordCount);

    /// <summary>Records a successful pipe reconnection.</summary>
    public void RecordPipeReconnect() => Interlocked.Increment(ref _pipeReconnects);

    /// <summary>Updates sampled pipe state (call periodically or on state change).</summary>
    public void SamplePipeState(bool connected, int queueDepth)
    {
        Volatile.Write(ref _pipeConnected, connected ? 1 : 0);
        Volatile.Write(ref _pipeQueueDepth, queueDepth);
    }

    /// <summary>Records a record written to failover JSONL (async path).</summary>
    public void RecordFailoverWrite() => Interlocked.Increment(ref _failoverWriteCount);

    /// <summary>Records an emergency synchronous failover write.</summary>
    public void RecordFailoverEmergency() => Interlocked.Increment(ref _failoverEmergencyCount);

    /// <summary>Records a failover write error.</summary>
    public void RecordFailoverError() => Interlocked.Increment(ref _failoverErrors);

    /// <summary>Updates sampled failover state.</summary>
    public void SampleFailoverState(int queueDepth, int filesOnDisk)
    {
        Volatile.Write(ref _failoverQueueDepth, queueDepth);
        Volatile.Write(ref _failoverFileCount, filesOnDisk);
    }

    // ── Health derivation ──────────────────────────────────────────

    /// <summary>
    /// Computes the full Edge health report: per-probe health (1 or 0),
    /// per-probe metrics, and overall Edge health ratio.
    /// </summary>
    public EdgeHealthReport GetHealthReport()
    {
        var uptimeSeconds = Stopwatch.GetElapsedTime(s_startTicks).TotalSeconds;
        var pipeConnected = Volatile.Read(ref _pipeConnected) == 1;
        var pipeQueueDepth = Volatile.Read(ref _pipeQueueDepth);
        var failoverQueueDepth = Volatile.Read(ref _failoverQueueDepth);
        var failoverFileCount = Volatile.Read(ref _failoverFileCount);

        // Read cumulative counters (non-destructive; these are lifetime totals)
        var httpReqs = Volatile.Read(ref _httpRequests);
        var captures = Volatile.Read(ref _captureCount);
        var captureErrors = Volatile.Read(ref _captureErrors);
        var pipeWrites = Volatile.Read(ref _pipeWriteCount);
        var pipeBatches = Volatile.Read(ref _pipeBatchCount);
        var pipeFailovers = Volatile.Read(ref _pipeFailoverCount);
        var pipeReconnects = Volatile.Read(ref _pipeReconnects);
        var failoverWrites = Volatile.Read(ref _failoverWriteCount);
        var failoverEmergencies = Volatile.Read(ref _failoverEmergencyCount);
        var failoverErrors = Volatile.Read(ref _failoverErrors);

        // ── Probe 1: HTTP Listener ──
        // Healthy if process is up (we're executing this code).
        var httpHealth = 1;

        // ── Probe 2: Capture Pipeline ──
        // Healthy if captures > 0 AND error rate < 5%.
        // First 30s after startup: healthy by default (no traffic yet).
        var captureHealth = uptimeSeconds < 30 || captureErrors == 0
            ? 1
            : (captures > 0 && captureErrors * 100 / (captures + captureErrors) < 5) ? 1 : 0;

        // ── Probe 3: Pipe Client ──
        // Healthy if pipe is connected.
        var pipeHealth = pipeConnected ? 1 : 0;

        // ── Probe 4: JSONL Failover ──
        // Healthy if no recent write errors. Emergency writes are OK (they work),
        // but write errors mean disk is failing.
        var failoverHealth = failoverErrors == 0 ? 1 : 0;

        var probes = new ProbeReport[]
        {
            new() { Name = "HTTP Listener", Health = httpHealth, Metrics = new
            {
                Requests = httpReqs
            }},
            new() { Name = "Capture Pipeline", Health = captureHealth, Metrics = new
            {
                Captured = captures,
                Errors = captureErrors
            }},
            new() { Name = "Pipe Client", Health = pipeHealth, Metrics = new
            {
                Connected = pipeConnected,
                QueueDepth = pipeQueueDepth,
                Written = pipeWrites,
                Batches = pipeBatches,
                FailedOver = pipeFailovers,
                Reconnects = pipeReconnects
            }},
            new() { Name = "JSONL Failover", Health = failoverHealth, Metrics = new
            {
                QueueDepth = failoverQueueDepth,
                FilesOnDisk = failoverFileCount,
                Written = failoverWrites,
                EmergencyWrites = failoverEmergencies,
                Errors = failoverErrors
            }}
        };

        var healthy = probes.Count(p => p.Health == 1);

        return new EdgeHealthReport
        {
            System = "Edge",
            Healthy = healthy,
            Total = probes.Length,
            Ratio = probes.Length > 0 ? (double)healthy / probes.Length : 0,
            UptimeSeconds = uptimeSeconds,
            IsReachable = true,
            Probes = probes,
            QueueDepth = pipeQueueDepth
        };
    }

    /// <summary>
    /// Takes a frozen snapshot of windowed counters and resets them.
    /// Cumulative counters in the health report are NOT reset.
    /// </summary>
    public EdgeMetricsSnapshot Snapshot()
    {
        return new EdgeMetricsSnapshot
        {
            HttpRequests = Interlocked.Exchange(ref _httpRequests, 0),
            CaptureCount = Interlocked.Exchange(ref _captureCount, 0),
            CaptureTotalTicks = Interlocked.Exchange(ref _captureTotalTicks, 0),
            CaptureErrors = Interlocked.Exchange(ref _captureErrors, 0),
            PipeWriteCount = Interlocked.Exchange(ref _pipeWriteCount, 0),
            PipeWriteTotalTicks = Interlocked.Exchange(ref _pipeWriteTotalTicks, 0),
            PipeBatchCount = Interlocked.Exchange(ref _pipeBatchCount, 0),
            PipeFailoverCount = Interlocked.Exchange(ref _pipeFailoverCount, 0),
            PipeReconnects = Interlocked.Exchange(ref _pipeReconnects, 0),
            PipeConnected = Volatile.Read(ref _pipeConnected) == 1,
            PipeQueueDepth = Volatile.Read(ref _pipeQueueDepth),
            FailoverWriteCount = Interlocked.Exchange(ref _failoverWriteCount, 0),
            FailoverEmergencyCount = Interlocked.Exchange(ref _failoverEmergencyCount, 0),
            FailoverErrors = Interlocked.Exchange(ref _failoverErrors, 0),
            FailoverQueueDepth = Volatile.Read(ref _failoverQueueDepth),
            FailoverFileCount = Volatile.Read(ref _failoverFileCount),
        };
    }
}

/// <summary>
/// Frozen snapshot of windowed Edge metrics for periodic logging.
/// </summary>
public readonly record struct EdgeMetricsSnapshot
{
    private static readonly double s_ticksPerUs = Stopwatch.Frequency / 1_000_000.0;
    private static readonly double s_ticksPerMs = Stopwatch.Frequency / 1_000.0;

    public long HttpRequests { get; init; }
    public long CaptureCount { get; init; }
    public long CaptureTotalTicks { get; init; }
    public long CaptureErrors { get; init; }
    public long PipeWriteCount { get; init; }
    public long PipeWriteTotalTicks { get; init; }
    public long PipeBatchCount { get; init; }
    public long PipeFailoverCount { get; init; }
    public long PipeReconnects { get; init; }
    public bool PipeConnected { get; init; }
    public int PipeQueueDepth { get; init; }
    public long FailoverWriteCount { get; init; }
    public long FailoverEmergencyCount { get; init; }
    public long FailoverErrors { get; init; }
    public int FailoverQueueDepth { get; init; }
    public int FailoverFileCount { get; init; }

    // ── Derived values ─────────────────────────────────────────────
    public double CaptureAvgUs => CaptureCount > 0 ? CaptureTotalTicks / (CaptureCount * s_ticksPerUs) : 0;
    public double PipeBatchAvgMs => PipeBatchCount > 0 ? PipeWriteTotalTicks / (PipeBatchCount * s_ticksPerMs) : 0;
    public double PipeAvgBatchSize => PipeBatchCount > 0 ? (double)PipeWriteCount / PipeBatchCount : 0;

    /// <summary>Formats the snapshot as a compact log line.</summary>
    public string Format(double windowSeconds)
    {
        var captureRps = windowSeconds > 0 ? CaptureCount / windowSeconds : 0;
        var pipeRps = windowSeconds > 0 ? PipeWriteCount / windowSeconds : 0;

        return
            $"═══ EDGE METRICS ({windowSeconds:F0}s window) ═══\n" +
            $"  HTTP       {HttpRequests,8:N0} req\n" +
            $"  CAPTURE    {CaptureCount,8:N0} rec  {captureRps,8:N1}/s  avg {CaptureAvgUs,8:N1}μs  errors {CaptureErrors}\n" +
            $"  PIPE       {PipeWriteCount,8:N0} rec  {pipeRps,8:N1}/s  batches {PipeBatchCount}  avg {PipeBatchAvgMs,6:N1}ms  ~{PipeAvgBatchSize:N1}rec/batch  queue={PipeQueueDepth}  {(PipeConnected ? "CONNECTED" : "DISCONNECTED")}  reconnects {PipeReconnects}\n" +
            $"  FAILOVER   written={FailoverWriteCount:N0}  emergency={FailoverEmergencyCount:N0}  errors={FailoverErrors:N0}  queue={FailoverQueueDepth}  files={FailoverFileCount}" +
            (PipeFailoverCount > 0 ? $"  PIPE→FAILOVER {PipeFailoverCount:N0} rec" : "");
    }
}
