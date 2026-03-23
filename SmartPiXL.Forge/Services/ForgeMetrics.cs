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
    private static readonly long s_startTicks = Stopwatch.GetTimestamp();

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

    // Lane 4: Data Sync (CompanyPiXLSyncService)
    private long _syncCompanyCycles;
    private long _syncCompanyInserted;
    private long _syncCompanyUpdated;
    private long _syncCompanyDeleted;
    private long _syncPixelCycles;
    private long _syncPixelInserted;
    private long _syncPixelUpdated;
    private long _syncPixelDeleted;
    private long _syncDurationTicks;
    private long _syncFailures;

    // Lane 5: IP Data Acquisition (IpDataAcquisitionService)
    private long _ipAcqCycles;
    private long _ipAcqAsnRows;
    private long _ipAcqGeoRows;
    private long _ipAcqCloudRanges;
    private long _ipAcqSkipped;
    private long _ipAcqDurationTicks;
    private long _ipAcqFailures;

    // ══════════════════════════════════════════════════════════════════
    // CUMULATIVE HEALTH STATE — Non-windowed. NOT reset by Snapshot().
    // Services push state here; GetHealthReport() reads it.
    // ══════════════════════════════════════════════════════════════════

    // F1: Ingest — pipe listener accepting connections?
    private int _pipeListenerActive;         // 1 = accepting, 0 = stopped
    private int _enrichmentChannelAlive;     // 1 = consumers running

    // F2: Enrichment Engine — worker pool health
    private int _enrichmentWorkerCount;      // current active workers
    private int _enrichmentWorkersAlive;     // 1 = at least one worker processing

    // F2: Enrichment cache sizes (sampled by MetricsReporterService)
    private int _cacheUaParsing;
    private int _cacheBotUaDetection;
    private int _cacheDnsLookup;
    private int _cacheWhoisAsn;
    private int _cacheMaxMindGeo;
    private int _cacheDeadInternet;
    private int _cacheBehavioralReplay;
    private int _cacheCrossCustomerIntel;
    private int _cacheSessionStitching;

    // F3: SQL Writer — lifetime failure tracking
    private long _sqlLifetimeFailures;
    private long _sqlLifetimeCount;

    // F4: Failover & Replay
    private int _failoverDiskWritable;       // 1 = last write succeeded
    private int _replayStuckFiles;           // files > 1h old not replaying

    // F5: ETL — last run timestamps (Stopwatch ticks)
    private long _etlMatchVisitsLastRun;
    private long _etlMatchLegacyLastRun;

    // F6: Background IP
    private int _bgIpDnsProcessing;          // 1 = lookups running
    private int _bgIpWhoisProcessing;        // 1 = lookups running

    // F7: Data Sync
    private long _syncLastCompanyRun;        // Stopwatch ticks
    private long _syncLastPixelRun;          // Stopwatch ticks
    private long _syncLifetimeFailures;
    private long _ipAcqLastRun;              // Stopwatch ticks
    private long _ipAcqLifetimeFailures;

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

    // ── Lane 4: Data Sync ─────────────────────────────────────────────

    /// <summary>Records a completed Company sync cycle with row counts.</summary>
    public void RecordCompanySync(long startTimestamp, int inserted, int updated, int deleted)
    {
        Interlocked.Increment(ref _syncCompanyCycles);
        Interlocked.Add(ref _syncCompanyInserted, inserted);
        Interlocked.Add(ref _syncCompanyUpdated, updated);
        Interlocked.Add(ref _syncCompanyDeleted, deleted);
        Interlocked.Add(ref _syncDurationTicks, Stopwatch.GetTimestamp() - startTimestamp);
    }

    /// <summary>Records a completed Pixel sync cycle with row counts.</summary>
    public void RecordPixelSync(long startTimestamp, int inserted, int updated, int deleted)
    {
        Interlocked.Increment(ref _syncPixelCycles);
        Interlocked.Add(ref _syncPixelInserted, inserted);
        Interlocked.Add(ref _syncPixelUpdated, updated);
        Interlocked.Add(ref _syncPixelDeleted, deleted);
        Interlocked.Add(ref _syncDurationTicks, Stopwatch.GetTimestamp() - startTimestamp);
    }

    /// <summary>Records a failed sync attempt.</summary>
    public void RecordSyncFailure() => Interlocked.Increment(ref _syncFailures);

    // ── Lane 5: IP Data Acquisition ───────────────────────────────────

    /// <summary>Records a completed IP data acquisition cycle.</summary>
    public void RecordIpAcqCycle(long startTimestamp, int asnRows, int geoRows, int cloudRanges, int skipped)
    {
        Interlocked.Increment(ref _ipAcqCycles);
        Interlocked.Add(ref _ipAcqAsnRows, asnRows);
        Interlocked.Add(ref _ipAcqGeoRows, geoRows);
        Interlocked.Add(ref _ipAcqCloudRanges, cloudRanges);
        Interlocked.Add(ref _ipAcqSkipped, skipped);
        Interlocked.Add(ref _ipAcqDurationTicks, Stopwatch.GetTimestamp() - startTimestamp);
    }

    /// <summary>Records a failed IP data acquisition attempt.</summary>
    public void RecordIpAcqFailure() => Interlocked.Increment(ref _ipAcqFailures);

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

            // Lane 4: Data Sync
            SyncCompanyCycles = Interlocked.Exchange(ref _syncCompanyCycles, 0),
            SyncCompanyInserted = Interlocked.Exchange(ref _syncCompanyInserted, 0),
            SyncCompanyUpdated = Interlocked.Exchange(ref _syncCompanyUpdated, 0),
            SyncCompanyDeleted = Interlocked.Exchange(ref _syncCompanyDeleted, 0),
            SyncPixelCycles = Interlocked.Exchange(ref _syncPixelCycles, 0),
            SyncPixelInserted = Interlocked.Exchange(ref _syncPixelInserted, 0),
            SyncPixelUpdated = Interlocked.Exchange(ref _syncPixelUpdated, 0),
            SyncPixelDeleted = Interlocked.Exchange(ref _syncPixelDeleted, 0),
            SyncDurationTicks = Interlocked.Exchange(ref _syncDurationTicks, 0),
            SyncFailures = Interlocked.Exchange(ref _syncFailures, 0),

            // Lane 5: IP Data Acquisition
            IpAcqCycles = Interlocked.Exchange(ref _ipAcqCycles, 0),
            IpAcqAsnRows = Interlocked.Exchange(ref _ipAcqAsnRows, 0),
            IpAcqGeoRows = Interlocked.Exchange(ref _ipAcqGeoRows, 0),
            IpAcqCloudRanges = Interlocked.Exchange(ref _ipAcqCloudRanges, 0),
            IpAcqSkipped = Interlocked.Exchange(ref _ipAcqSkipped, 0),
            IpAcqDurationTicks = Interlocked.Exchange(ref _ipAcqDurationTicks, 0),
            IpAcqFailures = Interlocked.Exchange(ref _ipAcqFailures, 0),
        };

        return snap;
    }

    // ══════════════════════════════════════════════════════════════════
    // HEALTH STATE RECORDING
    // ══════════════════════════════════════════════════════════════════

    // F1
    public void SamplePipeListenerState(bool active) =>
        Volatile.Write(ref _pipeListenerActive, active ? 1 : 0);
    public void SampleEnrichmentChannelAlive(bool alive) =>
        Volatile.Write(ref _enrichmentChannelAlive, alive ? 1 : 0);

    // F2
    public void SampleEnrichmentWorkers(int count, bool alive)
    {
        Volatile.Write(ref _enrichmentWorkerCount, count);
        Volatile.Write(ref _enrichmentWorkersAlive, alive ? 1 : 0);
    }

    /// <summary>Samples enrichment cache sizes (called by MetricsReporterService).</summary>
    public void SampleEnrichmentCaches(
        int uaParsing, int botUaDetection, int dnsLookup, int whoisAsn,
        int maxMindGeo, int deadInternet, int behavioralReplay,
        int crossCustomerIntel, int sessionStitching)
    {
        Volatile.Write(ref _cacheUaParsing, uaParsing);
        Volatile.Write(ref _cacheBotUaDetection, botUaDetection);
        Volatile.Write(ref _cacheDnsLookup, dnsLookup);
        Volatile.Write(ref _cacheWhoisAsn, whoisAsn);
        Volatile.Write(ref _cacheMaxMindGeo, maxMindGeo);
        Volatile.Write(ref _cacheDeadInternet, deadInternet);
        Volatile.Write(ref _cacheBehavioralReplay, behavioralReplay);
        Volatile.Write(ref _cacheCrossCustomerIntel, crossCustomerIntel);
        Volatile.Write(ref _cacheSessionStitching, sessionStitching);
    }

    // F3
    public void RecordSqlWriteHealth(long batchCount, long failures)
    {
        Volatile.Write(ref _sqlLifetimeCount, batchCount);
        Volatile.Write(ref _sqlLifetimeFailures, failures);
    }

    // F4
    public void SampleFailoverDiskWritable(bool writable) =>
        Volatile.Write(ref _failoverDiskWritable, writable ? 1 : 0);
    public void SampleReplayStuckFiles(int count) =>
        Volatile.Write(ref _replayStuckFiles, count);

    // F5
    public void RecordEtlMatchVisitsRun() =>
        Volatile.Write(ref _etlMatchVisitsLastRun, Stopwatch.GetTimestamp());
    public void RecordEtlMatchLegacyRun() =>
        Volatile.Write(ref _etlMatchLegacyLastRun, Stopwatch.GetTimestamp());

    // F6
    public void SampleBgIpProcessingState(bool dnsActive, bool whoisActive)
    {
        Volatile.Write(ref _bgIpDnsProcessing, dnsActive ? 1 : 0);
        Volatile.Write(ref _bgIpWhoisProcessing, whoisActive ? 1 : 0);
    }

    // F7
    public void RecordCompanySyncRun() =>
        Volatile.Write(ref _syncLastCompanyRun, Stopwatch.GetTimestamp());
    public void RecordPixelSyncRun() =>
        Volatile.Write(ref _syncLastPixelRun, Stopwatch.GetTimestamp());
    public void RecordSyncLifetimeFailure() =>
        Interlocked.Increment(ref _syncLifetimeFailures);
    public void RecordIpAcqRun() =>
        Volatile.Write(ref _ipAcqLastRun, Stopwatch.GetTimestamp());
    public void RecordIpAcqLifetimeFailure() =>
        Interlocked.Increment(ref _ipAcqLifetimeFailures);

    // ══════════════════════════════════════════════════════════════════
    // HEALTH TREE — Derives all 29 probes from cumulative state.
    // ══════════════════════════════════════════════════════════════════

    public ForgeHealthReport GetHealthReport()
    {
        var uptimeSeconds = Stopwatch.GetElapsedTime(s_startTicks).TotalSeconds;

        // ── F1: Ingest (2 probes) ─────────────────────────────────────
        var f1 = SubsystemReport.From("F1: Ingest", [
            new() { Name = "Pipe Listener", Health = Volatile.Read(ref _pipeListenerActive), Metrics = new
            {
                Connects = Volatile.Read(ref _pipeConnects),
                Disconnects = Volatile.Read(ref _pipeDisconnects)
            }},
            new() { Name = "Enrichment Channel", Health = Volatile.Read(ref _enrichmentChannelAlive), Metrics = new
            {
                Depth = Volatile.Read(ref _enrichmentChannelDepth)
            }}
        ]);

        // ── F2: Enrichment Engine (17 probes) ─────────────────────────
        // 1 Worker Pool + 9 stateful + 7 stateless
        var f2Probes = new ProbeReport[17];
        var idx = 0;

        // Worker Pool
        f2Probes[idx++] = new() { Name = "Worker Pool", Health = Volatile.Read(ref _enrichmentWorkersAlive), Metrics = new
        {
            Workers = Volatile.Read(ref _enrichmentWorkerCount)
        }};

        // Stateful enrichments with caches — healthy if cache bounded
        f2Probes[idx++] = CacheProbe("UaParsing", Volatile.Read(ref _cacheUaParsing), 50_000);
        f2Probes[idx++] = CacheProbe("BotUaDetection", Volatile.Read(ref _cacheBotUaDetection), 50_000);
        f2Probes[idx++] = CacheProbe("DnsLookup", Volatile.Read(ref _cacheDnsLookup), 200_000);
        f2Probes[idx++] = CacheProbe("WhoisAsn", Volatile.Read(ref _cacheWhoisAsn), 200_000);
        f2Probes[idx++] = CacheProbe("MaxMindGeo", Volatile.Read(ref _cacheMaxMindGeo), 200_000);
        f2Probes[idx++] = CacheProbe("DeadInternet", Volatile.Read(ref _cacheDeadInternet), 100_000);
        f2Probes[idx++] = CacheProbe("BehavioralReplay", Volatile.Read(ref _cacheBehavioralReplay), 500_000);
        f2Probes[idx++] = CacheProbe("CrossCustomerIntel", Volatile.Read(ref _cacheCrossCustomerIntel), 500_000);
        f2Probes[idx++] = CacheProbe("SessionStitching", Volatile.Read(ref _cacheSessionStitching), 500_000);

        // Stateless enrichments — pure computation, always 1
        f2Probes[idx++] = new() { Name = "IpClassification", Health = 1 };
        f2Probes[idx++] = new() { Name = "ContradictionMatrix", Health = 1 };
        f2Probes[idx++] = new() { Name = "DeviceAffluence", Health = 1 };
        f2Probes[idx++] = new() { Name = "DeviceAgeEstimation", Health = 1 };
        f2Probes[idx++] = new() { Name = "GeographicArbitrage", Health = 1 };
        f2Probes[idx++] = new() { Name = "GpuTierReference", Health = 1 };
        f2Probes[idx++] = new() { Name = "LeadQualityScoring", Health = 1 };

        var f2 = SubsystemReport.From("F2: Enrichment Engine", f2Probes);

        // ── F3: SQL Writer (1 probe) ──────────────────────────────────
        var sqlCount = Volatile.Read(ref _sqlLifetimeCount);
        var sqlFail = Volatile.Read(ref _sqlLifetimeFailures);
        var sqlHealth = uptimeSeconds < 30 || sqlFail == 0
            ? 1
            : (sqlCount > 0 && sqlFail * 100 / (sqlCount + sqlFail) < 5) ? 1 : 0;
        var f3 = SubsystemReport.From("F3: SQL Writer", [
            new() { Name = "BulkCopy", Health = sqlHealth, Metrics = new
            {
                Written = sqlCount,
                Failures = sqlFail,
                QueueDepth = Volatile.Read(ref _sqlWriterChannelDepth)
            }}
        ]);

        // ── F4: Failover & Replay (2 probes) ──────────────────────────
        var stuckFiles = Volatile.Read(ref _replayStuckFiles);
        var f4 = SubsystemReport.From("F4: Failover & Replay", [
            new() { Name = "Failover Writer", Health = Volatile.Read(ref _failoverDiskWritable), Metrics = new
            {
                RecordsDiverted = Volatile.Read(ref _failoverCount)
            }},
            new() { Name = "Replay Service", Health = stuckFiles == 0 ? 1 : 0, Metrics = new
            {
                StuckFiles = stuckFiles
            }}
        ]);

        // ── F5: ETL Pipeline (2 probes) ───────────────────────────────
        // Healthy if last run < 2 minutes ago (or first 2 min after startup)
        var matchLastTicks = Volatile.Read(ref _etlMatchVisitsLastRun);
        var legacyLastTicks = Volatile.Read(ref _etlMatchLegacyLastRun);
        var matchAge = matchLastTicks > 0 ? Stopwatch.GetElapsedTime(matchLastTicks).TotalSeconds : double.MaxValue;
        var legacyAge = legacyLastTicks > 0 ? Stopwatch.GetElapsedTime(legacyLastTicks).TotalSeconds : double.MaxValue;
        var f5 = SubsystemReport.From("F5: ETL Pipeline", [
            new() { Name = "MatchVisits", Health = uptimeSeconds < 120 || matchAge < 120 ? 1 : 0, Metrics = new
            {
                SecondsSinceLastRun = matchLastTicks > 0 ? matchAge : -1
            }},
            new() { Name = "MatchLegacyVisits", Health = uptimeSeconds < 120 || legacyAge < 120 ? 1 : 0, Metrics = new
            {
                SecondsSinceLastRun = legacyLastTicks > 0 ? legacyAge : -1
            }}
        ]);

        // ── F6: Background IP (2 probes) ──────────────────────────────
        var f6 = SubsystemReport.From("F6: Background IP", [
            new() { Name = "DNS Enrichment", Health = Volatile.Read(ref _bgIpDnsProcessing), Metrics = new
            {
                ChannelDepth = Volatile.Read(ref _bgIpChannelDepth),
                DedupCacheSize = Volatile.Read(ref _bgIpDedupCacheSize)
            }},
            new() { Name = "WHOIS Enrichment", Health = Volatile.Read(ref _bgIpWhoisProcessing), Metrics = new
            {
                ChannelDepth = Volatile.Read(ref _bgIpChannelDepth)
            }}
        ]);

        // ── F7: Data Sync (3 probes) ──────────────────────────────────
        // Company/Pixel sync: healthy if last run < 7 hours ago (or first 7h)
        var companyLastTicks = Volatile.Read(ref _syncLastCompanyRun);
        var pixelLastTicks = Volatile.Read(ref _syncLastPixelRun);
        var companyAge = companyLastTicks > 0 ? Stopwatch.GetElapsedTime(companyLastTicks).TotalSeconds : double.MaxValue;
        var pixelAge = pixelLastTicks > 0 ? Stopwatch.GetElapsedTime(pixelLastTicks).TotalSeconds : double.MaxValue;
        var maxSyncAge = 7 * 3600; // 7 hours
        var syncFails = Volatile.Read(ref _syncLifetimeFailures);
        var syncHealth = (uptimeSeconds < maxSyncAge || (companyAge < maxSyncAge && pixelAge < maxSyncAge)) && syncFails == 0 ? 1 : 0;

        // IP Data Acquisition: healthy if last import < 26 hours ago (or first 26h)
        var ipAcqLastTicks = Volatile.Read(ref _ipAcqLastRun);
        var ipAcqAge = ipAcqLastTicks > 0 ? Stopwatch.GetElapsedTime(ipAcqLastTicks).TotalSeconds : double.MaxValue;
        var maxIpAcqAge = 26 * 3600; // 26 hours

        var f7 = SubsystemReport.From("F7: Data Sync", [
            new() { Name = "Company/Pixel Sync", Health = syncHealth, Metrics = new
            {
                CompanySecsSinceRun = companyLastTicks > 0 ? companyAge : -1,
                PixelSecsSinceRun = pixelLastTicks > 0 ? pixelAge : -1,
                Failures = syncFails
            }},
            new() { Name = "IPtoASN", Health = uptimeSeconds < maxIpAcqAge || ipAcqAge < maxIpAcqAge ? 1 : 0, Metrics = new
            {
                SecondsSinceLastRun = ipAcqLastTicks > 0 ? ipAcqAge : -1,
                Failures = Volatile.Read(ref _ipAcqLifetimeFailures)
            }},
            new() { Name = "DB-IP", Health = uptimeSeconds < maxIpAcqAge || ipAcqAge < maxIpAcqAge ? 1 : 0, Metrics = new
            {
                SecondsSinceLastRun = ipAcqLastTicks > 0 ? ipAcqAge : -1
            }}
        ]);

        return ForgeHealthReport.From(uptimeSeconds, [f1, f2, f3, f4, f5, f6, f7]);
    }

    /// <summary>Probe for a cache-backed enrichment: healthy if under limit.</summary>
    private static ProbeReport CacheProbe(string name, int count, int maxEntries) =>
        new() { Name = name, Health = count <= maxEntries ? 1 : 0, Metrics = new { CacheSize = count, MaxEntries = maxEntries } };

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

    // ── Lane 4: Data Sync ──
    public long SyncCompanyCycles { get; init; }
    public long SyncCompanyInserted { get; init; }
    public long SyncCompanyUpdated { get; init; }
    public long SyncCompanyDeleted { get; init; }
    public long SyncPixelCycles { get; init; }
    public long SyncPixelInserted { get; init; }
    public long SyncPixelUpdated { get; init; }
    public long SyncPixelDeleted { get; init; }
    public long SyncDurationTicks { get; init; }
    public long SyncFailures { get; init; }

    // ── Lane 5: IP Data Acquisition ──
    public long IpAcqCycles { get; init; }
    public long IpAcqAsnRows { get; init; }
    public long IpAcqGeoRows { get; init; }
    public long IpAcqCloudRanges { get; init; }
    public long IpAcqSkipped { get; init; }
    public long IpAcqDurationTicks { get; init; }
    public long IpAcqFailures { get; init; }

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
    public double SyncDurationMs => SyncDurationTicks / s_ticksPerMs;
    public double IpAcqDurationMs => IpAcqDurationTicks / s_ticksPerMs;

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
            (ReplayFiles > 0 ? $"  REPLAY {ReplayFiles} file(s) {ReplayRecords:N0} rec" : "") +
            (SyncCompanyCycles > 0 || SyncPixelCycles > 0
                ? $"\n  DATASYNC  co={SyncCompanyCycles} ins={SyncCompanyInserted} upd={SyncCompanyUpdated} del={SyncCompanyDeleted}  px={SyncPixelCycles} ins={SyncPixelInserted} upd={SyncPixelUpdated} del={SyncPixelDeleted}  {SyncDurationMs:N0}ms  fails={SyncFailures}"
                : "") +
            (IpAcqCycles > 0
                ? $"\n  IPACQ     cycles={IpAcqCycles} asn={IpAcqAsnRows:N0} geo={IpAcqGeoRows:N0} cloud={IpAcqCloudRanges:N0} skip={IpAcqSkipped} {IpAcqDurationMs:N0}ms  fails={IpAcqFailures}"
                : "");
    }
}
