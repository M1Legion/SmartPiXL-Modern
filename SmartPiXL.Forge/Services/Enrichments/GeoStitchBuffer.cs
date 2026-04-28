using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using SmartPiXL.Models;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services.Enrichments;

// ════════════════════════════════════════════════════════════════════════════
// GEO STITCH BUFFER — Phase 0 of the go-live plan.
//
// Problem: the PiXL JS tag fires two beacons per page view. The first carries
// the fingerprint and is sent immediately (to satisfy Edge's firehose mandate).
// The second carries the user-granted geolocation (lat/lon/accuracy/...) and
// is sent after navigator.geolocation resolves (async, ~100 ms on desktop,
// seconds on mobile, minutes if the user hesitates on the prompt).
//
// Without stitching, those two beacons land as two rows in PiXL.Parsed — one
// with fingerprint-but-no-geo, one with geo-but-no-fingerprint — linked only
// by fragile secondary signals (UA + IP + screen dims). Downstream reports
// then have to "merge" by guessing.
//
// Fix: Forge holds each arriving record in an in-memory buffer keyed on the
// client-generated HitId (UUIDv4 from crypto.randomUUID) for up to 20 s. When
// its pair arrives, we merge the QueryStrings and emit a single record to the
// SQL writer channel. If the pair never arrives (user denied, ignored, or
// browser doesn't support geolocation), we emit the main record unmerged once
// the window expires.
//
// Why in Forge and not the database:
//   • Keeps landing-table semantics simple (one row per pixel hit, always).
//   • Avoids lock contention on PiXL.Parsed during merge.
//   • Scales with Forge workers, not with SQL.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// In-memory merge buffer that stitches a main beacon with its geolocation
/// followup beacon before they reach the SQL writer channel. Registered as
/// a singleton and hooked as an <see cref="IHostedService"/> so its sweep
/// timer starts with the Forge and drains on shutdown.
/// </summary>
public sealed class GeoStitchBuffer : IHostedService, IDisposable
{
    // ── Configuration ─────────────────────────────────────────────────────
    /// <summary>
    /// How long to hold an unmatched record before giving up and emitting
    /// it unmerged. Desktop geolocation usually resolves inside 1 s. Mobile
    /// can take longer if GPS is cold. 20 s is comfortably past the p99
    /// user-response time observed during pre-go-live testing.
    /// </summary>
    private static readonly TimeSpan StitchWindow = TimeSpan.FromSeconds(20);

    /// <summary>Sweep cadence. 1 Hz is more than fine for a 20 s window.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    // ── Dependencies ──────────────────────────────────────────────────────
    private readonly ChannelWriter<TrackingData> _sqlWriter;
    private readonly ForgeFailoverWriter _failoverWriter;
    private readonly ForgeMetrics _forgeMetrics;
    private readonly ITrackingLogger _logger;
    private readonly GeoStitchMetrics _metrics;

    // ── State ─────────────────────────────────────────────────────────────
    // Two buckets so that whichever beacon arrives first can park, and the
    // second can immediately find its pair regardless of order. Capped size
    // prevents an OOM crash in pathological conditions (e.g. a bot firing
    // tons of mains with no followups).
    private const int MaxBucketSize = 50_000;
    private readonly ConcurrentDictionary<Guid, PendingEntry> _pendingMain = new();
    private readonly ConcurrentDictionary<Guid, PendingEntry> _pendingGeo = new();
    private Timer? _sweepTimer;
    private int _disposed;

    public GeoStitchBuffer(
        ForgeChannels channels,
        ForgeFailoverWriter failoverWriter,
        ForgeMetrics forgeMetrics,
        ITrackingLogger logger,
        GeoStitchMetrics metrics)
    {
        _sqlWriter = channels.SqlWriter.Writer;
        _failoverWriter = failoverWriter;
        _forgeMetrics = forgeMetrics;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>
    /// Emits a record to the SQL writer channel, falling back to the failover
    /// writer if the channel is full. Mirrors the legacy path in
    /// <see cref="EnrichmentPipelineService"/> so behaviour is identical.
    /// </summary>
    private void Emit(TrackingData record)
    {
        if (!_sqlWriter.TryWrite(record))
        {
            _failoverWriter.Append(record);
            _forgeMetrics.RecordFailover();
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // PUBLIC — called by EnrichmentPipelineService after enrichment
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Routes an enriched record through the stitch buffer. Either emits
    /// immediately (legacy record without HitId, or matched pair) or parks
    /// the record for later merge / timeout.
    /// </summary>
    public void Handle(TrackingData record)
    {
        var qs = record.QueryString ?? string.Empty;

        // Legacy path: no HitId → nothing to stitch, pass through.
        // This covers synthetic traffic, cached scripts from before Phase 0,
        // and any third party that doesn't run our JS.
        if (!TryParseHitId(qs, out var hitId))
        {
            _metrics.RecordLegacyPassthrough();
            Emit(record);
            return;
        }

        var isFollowup = HasQsFlag(qs, "_geo_followup");

        if (isFollowup)
        {
            HandleFollowup(hitId, record);
        }
        else
        {
            HandleMain(hitId, record);
        }
    }

    private void HandleMain(Guid hitId, TrackingData record)
    {
        // If a matching followup is already parked (rare — arrives only if the
        // geo prompt resolved before the main hit reached Forge, which can
        // happen with ultra-fast desktop + cached permission), merge now.
        if (_pendingGeo.TryRemove(hitId, out var waiting))
        {
            EmitMerged(record, waiting.Record, inline: true);
            return;
        }

        // Over capacity: emit unmerged and count it so the operator notices.
        if (_pendingMain.Count >= MaxBucketSize)
        {
            _metrics.RecordBufferOverflow();
            Emit(record);
            return;
        }

        _pendingMain[hitId] = new PendingEntry(record, Stopwatch.GetTimestamp());
        _metrics.SamplePendingMainDepth(_pendingMain.Count);
    }

    private void HandleFollowup(Guid hitId, TrackingData followup)
    {
        // Happy path: main is already parked, we are the geo for it.
        if (_pendingMain.TryRemove(hitId, out var waiting))
        {
            EmitMerged(waiting.Record, followup, inline: true);
            return;
        }

        // Cross-order tail: geo beat the main beacon. Park it for the main
        // to pick up. If the main never shows, the sweep will drop it as
        // an orphan (see OnSweep).
        if (_pendingGeo.Count >= MaxBucketSize)
        {
            _metrics.RecordBufferOverflow();
            return; // No main to merge with; drop silently.
        }

        _pendingGeo[hitId] = new PendingEntry(followup, Stopwatch.GetTimestamp());
        _metrics.SamplePendingGeoDepth(_pendingGeo.Count);
    }

    // ════════════════════════════════════════════════════════════════════
    // MERGE
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a merged <see cref="TrackingData"/> from the main record plus
    /// the followup's _usr_* geolocation params. The followup carries a few
    /// redundant identifier fields (ua, lang, tz, sw, sh, pd, deviceHash)
    /// that we strip because the main already has stronger versions. All
    /// other _usr_* fields are appended to the main's QueryString.
    /// </summary>
    private void EmitMerged(TrackingData main, TrackingData followup, bool inline)
    {
        var mainQs = main.QueryString ?? string.Empty;
        var followupQs = followup.QueryString ?? string.Empty;

        var merged = MergeQueryStrings(mainQs, followupQs);

        var stitched = new TrackingData
        {
            CompanyID = main.CompanyID,
            PiXLID = main.PiXLID,
            ReceivedAt = main.ReceivedAt,
            IPAddress = main.IPAddress,
            RequestPath = main.RequestPath,
            QueryString = merged,
            HeadersJson = main.HeadersJson,
            UserAgent = main.UserAgent,
            Referer = main.Referer,
            HitId = main.HitId,
        };

        var latencyTicks = Stopwatch.GetTimestamp() - main.ReceivedAt.Ticks;
        _metrics.RecordMerge(inline, latencyTicks);
        Emit(stitched);
    }

    /// <summary>
    /// Appends the followup's _usr_* params to the main's query string.
    /// Drops the redundant identifier fields (already on main) and the
    /// followup marker (_geo_followup / _hit_id already on main).
    /// </summary>
    internal static string MergeQueryStrings(string mainQs, string followupQs)
    {
        if (string.IsNullOrEmpty(followupQs)) return mainQs;

        var sb = new StringBuilder(mainQs.Length + followupQs.Length + 4);
        sb.Append(mainQs);
        var needAmp = mainQs.Length > 0 && mainQs[^1] != '&';

        foreach (var pair in followupQs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair.AsSpan(0, eq).ToString();

            // Keep only _usr_*, _geo_captureMs, and _pv (protocol version).
            // Drop followup's redundant identifiers and its marker flags.
            var keep = key.StartsWith("_usr_", StringComparison.Ordinal)
                    || key == "_geo_captureMs"
                    || key == "_pv";
            if (!keep) continue;

            if (needAmp) { sb.Append('&'); } else { needAmp = true; }
            sb.Append(pair);
        }

        return sb.ToString();
    }

    // ════════════════════════════════════════════════════════════════════
    // SWEEP — runs at 1 Hz, flushes stale entries.
    // ════════════════════════════════════════════════════════════════════

    private void OnSweep(object? _)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            var cutoff = Stopwatch.GetTimestamp() - (long)(StitchWindow.TotalSeconds * Stopwatch.Frequency);

            // Expired mains: emit unmerged. This is the "user denied / didn't
            // answer / browser doesn't support geolocation" path. Downstream
            // sees a row with UserGeoStatus populated but no lat/lon, which
            // is correct and actionable.
            foreach (var kv in _pendingMain)
            {
                if (kv.Value.TimestampTicks > cutoff) continue;
                if (_pendingMain.TryRemove(kv.Key, out var entry))
                {
                    _metrics.RecordFlushedUnmerged();
                    Emit(entry.Record);
                }
            }

            // Expired geos: orphaned followups with no main in sight. Most
            // likely a malformed script load or a user hitting the page twice
            // so fast the first main was already flushed. Drop them — the
            // main has already landed on its own and carries UserGeoStatus
            // from the initial Edge parse where available.
            foreach (var kv in _pendingGeo)
            {
                if (kv.Value.TimestampTicks > cutoff) continue;
                if (_pendingGeo.TryRemove(kv.Key, out PendingEntry _))
                {
                    _metrics.RecordOrphanGeo();
                }
            }

            _metrics.SamplePendingMainDepth(_pendingMain.Count);
            _metrics.SamplePendingGeoDepth(_pendingGeo.Count);
        }
        catch (Exception ex)
        {
            _logger.Error($"GeoStitchBuffer sweep error: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // IHostedService
    // ════════════════════════════════════════════════════════════════════

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sweepTimer = new Timer(OnSweep, null, SweepInterval, SweepInterval);
        _logger.Info($"GeoStitchBuffer started (window={StitchWindow.TotalSeconds}s, sweep={SweepInterval.TotalSeconds}s)");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop the timer first so we don't race with the drain below.
        _sweepTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _sweepTimer?.Dispose();
        _sweepTimer = null;

        // Drain: emit every parked main unmerged, drop parked geos. Better
        // to land incomplete rows in SQL than to lose them entirely.
        var flushed = 0;
        foreach (var kv in _pendingMain)
        {
            if (_pendingMain.TryRemove(kv.Key, out var entry))
            {
                _metrics.RecordFlushedUnmerged();
                Emit(entry.Record);
                flushed++;
            }
        }

        var orphaned = 0;
        foreach (var kv in _pendingGeo)
        {
            if (_pendingGeo.TryRemove(kv.Key, out PendingEntry _))
            {
                _metrics.RecordOrphanGeo();
                orphaned++;
            }
        }

        _logger.Info($"GeoStitchBuffer stopped. Drained {flushed} unmerged mains, dropped {orphaned} orphan geos.");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sweepTimer?.Dispose();
    }

    // ════════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════════

    private static bool TryParseHitId(string qs, out Guid hitId)
    {
        hitId = default;
        if (string.IsNullOrEmpty(qs)) return false;

        // Direct string scan — avoids an allocation over QueryParamReader.Get
        // for records that don't have _hit_id (the common legacy case).
        const string Key = "_hit_id=";
        var idx = qs.IndexOf(Key, StringComparison.Ordinal);
        while (idx > 0 && qs[idx - 1] != '&' && qs[idx - 1] != '?')
        {
            idx = qs.IndexOf(Key, idx + 1, StringComparison.Ordinal);
        }
        if (idx < 0 && !qs.StartsWith(Key, StringComparison.Ordinal)) return false;
        if (idx < 0) idx = 0;

        var valStart = idx + Key.Length;
        var valEnd = qs.IndexOf('&', valStart);
        var val = valEnd < 0
            ? qs.AsSpan(valStart)
            : qs.AsSpan(valStart, valEnd - valStart);

        return Guid.TryParse(val, out hitId);
    }

    private static bool HasQsFlag(string qs, string key)
    {
        // Matches "&key=1", "?key=1", or start-of-string "key=1". Cheap
        // substring check before the full validity test.
        var needle = key + "=1";
        return qs.Contains("&" + needle, StringComparison.Ordinal)
            || qs.Contains("?" + needle, StringComparison.Ordinal)
            || qs.StartsWith(needle, StringComparison.Ordinal);
    }

    private readonly record struct PendingEntry(TrackingData Record, long TimestampTicks);
}

// ════════════════════════════════════════════════════════════════════════════
// METRICS — kept separate from ForgeMetrics to avoid touching the existing
// MetricsSnapshot record. Exposed as a singleton so Sentinel can later pull
// counters for dashboards.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Lock-free counters for <see cref="GeoStitchBuffer"/>. Exposed as a
/// singleton so Sentinel and health endpoints can read current values.
/// </summary>
public sealed class GeoStitchMetrics
{
    private long _mergedInline;
    private long _flushedUnmerged;
    private long _orphanGeo;
    private long _legacyPassthrough;
    private long _bufferOverflow;
    private long _mergeLatencyTicksSum;
    private long _mergeLatencyCount;
    private int _pendingMainDepth;
    private int _pendingGeoDepth;

    public void RecordMerge(bool inline, long latencyTicks)
    {
        if (inline) Interlocked.Increment(ref _mergedInline);
        Interlocked.Add(ref _mergeLatencyTicksSum, latencyTicks);
        Interlocked.Increment(ref _mergeLatencyCount);
    }

    public void RecordFlushedUnmerged() => Interlocked.Increment(ref _flushedUnmerged);
    public void RecordOrphanGeo() => Interlocked.Increment(ref _orphanGeo);
    public void RecordLegacyPassthrough() => Interlocked.Increment(ref _legacyPassthrough);
    public void RecordBufferOverflow() => Interlocked.Increment(ref _bufferOverflow);
    public void SamplePendingMainDepth(int depth) => Volatile.Write(ref _pendingMainDepth, depth);
    public void SamplePendingGeoDepth(int depth) => Volatile.Write(ref _pendingGeoDepth, depth);

    public GeoStitchSnapshot Snapshot() => new(
        MergedInline: Interlocked.Read(ref _mergedInline),
        FlushedUnmerged: Interlocked.Read(ref _flushedUnmerged),
        OrphanGeo: Interlocked.Read(ref _orphanGeo),
        LegacyPassthrough: Interlocked.Read(ref _legacyPassthrough),
        BufferOverflow: Interlocked.Read(ref _bufferOverflow),
        MergeLatencyTicksSum: Interlocked.Read(ref _mergeLatencyTicksSum),
        MergeLatencyCount: Interlocked.Read(ref _mergeLatencyCount),
        PendingMainDepth: Volatile.Read(ref _pendingMainDepth),
        PendingGeoDepth: Volatile.Read(ref _pendingGeoDepth));
}

public readonly record struct GeoStitchSnapshot(
    long MergedInline,
    long FlushedUnmerged,
    long OrphanGeo,
    long LegacyPassthrough,
    long BufferOverflow,
    long MergeLatencyTicksSum,
    long MergeLatencyCount,
    int PendingMainDepth,
    int PendingGeoDepth);
