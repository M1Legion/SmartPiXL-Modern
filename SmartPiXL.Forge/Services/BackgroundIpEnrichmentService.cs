using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Forge.Services.Enrichments;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// BACKGROUND IP ENRICHMENT SERVICE — Lane 3 of the Three-Lane Architecture.
//
// Runs DNS and WHOIS lookups OFF the enrichment hot path. Pipeline
// workers call Enqueue(ip) fire-and-forget; background workers perform the
// actual I/O-bound lookups asynchronously. Results populate the per-service
// ConcurrentDictionary caches so that SUBSEQUENT records for the same IP
// hit the cache inline (zero latency via TryGetCached).
//
// DESIGN:
//   Pipeline worker → Enqueue(ip)        [non-blocking, dedup'd]
//       → Channel<string>                [bounded 50K, DropOldest]
//       → N background workers           [default 4, I/O-overlapped]
//           → DnsLookupService.LookupAsync     → populates DNS cache
//           → WhoisAsnService.LookupAsync      → populates WHOIS cache
//                                                (only when MaxMind has CC but no ASN)
//
// CACHE-AHEAD PATTERN:
//   1st hit for IP: cache miss in pipeline → no _srv_* params → background starts lookup
//   2nd+ hit for IP: cache hit → _srv_* params appended inline at zero latency
//   IP cardinality ~38% unique per 100K → 62% inline cache hit rate from the start.
//   As the background service warms up, hit rate approaches 100%.
//
// DEDUPLICATION:
//   ConcurrentDictionary<string, byte> tracks all IPs already enqueued in this
//   process lifetime. Only genuinely new IPs trigger background lookups.
//   Evicted every 30 minutes when count exceeds 500K to prevent unbounded growth.
//
// METRICS:
//   Logs progress every 1,000 IPs per worker. Total enriched count tracked.
// ============================================================================

/// <summary>
/// Background service that performs DNS and WHOIS lookups off the
/// enrichment hot path. Pipeline workers call <see cref="Enqueue"/> to
/// submit IPs for asynchronous enrichment. Results populate service caches
/// for zero-latency inline reads on subsequent records.
/// </summary>
public sealed class BackgroundIpEnrichmentService : BackgroundService
{
    private readonly Channel<string> _ipChannel;
    private readonly BoundedCache<string, byte> _seen; // dedup cache (value unused, timestamps tracked by BoundedCache)
    private readonly DnsLookupService? _dns;
    private readonly WhoisAsnService? _whois;
    private readonly MaxMindGeoService? _maxMind;
    private readonly ITrackingLogger _logger;
    private readonly int _workerCount;

    // Metrics
    private long _enqueued;
    private long _processed;
    private long _duplicatesSkipped;

    public BackgroundIpEnrichmentService(
        ITrackingLogger logger,
        IOptions<ForgeSettings> forgeSettings,
        DnsLookupService? dns = null,
        WhoisAsnService? whois = null,
        MaxMindGeoService? maxMind = null)
    {
        _logger = logger;
        _dns = dns;
        _whois = whois;
        _maxMind = maxMind;
        _workerCount = forgeSettings.Value.BackgroundIpWorkerCount;

        _seen = new BoundedCache<string, byte>(
            maxEntries: 500_000, evictTarget: 250_000,
            maxAge: TimeSpan.FromMinutes(30), StringComparer.Ordinal);
        _ipChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // NEVER block the pipeline
            SingleWriter = false,
            SingleReader = false
        });
    }

    /// <summary>
    /// Fire-and-forget from pipeline workers. Non-blocking, dedup'd.
    /// Skips private/reserved IPs. Only enqueues genuinely new IPs.
    /// </summary>
    public void Enqueue(string? ip)
    {
        if (string.IsNullOrEmpty(ip))
            return;

        // Skip private/reserved IPs — no external enrichment possible
        if (ip.StartsWith("10.", StringComparison.Ordinal) ||
            ip.StartsWith("192.168.", StringComparison.Ordinal) ||
            ip.StartsWith("127.", StringComparison.Ordinal) ||
            ip.StartsWith("172.", StringComparison.Ordinal))
            return;

        // Dedup: only enqueue if this IP hasn't been seen in this process lifetime
        if (!_seen.TryAdd(ip, 0))
        {
            Interlocked.Increment(ref _duplicatesSkipped);
            return;
        }

        Interlocked.Increment(ref _enqueued);

        // Non-blocking write — drops oldest if channel is full
        _ipChannel.Writer.TryWrite(ip);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var hasAnyService = _dns is not null || _whois is not null;
        if (!hasAnyService)
        {
            _logger.Info("BackgroundIpEnrichment: No I/O services registered — service disabled.");
            return;
        }

        _logger.Info($"BackgroundIpEnrichment started. Workers: {_workerCount}, " +
                     $"DNS: {(_dns is not null ? "ON" : "OFF")}, " +
                     $"WHOIS: {(_whois is not null ? "ON" : "OFF")}");

        // Periodic cache eviction — delegates to BoundedCache hybrid strategy.
        _ = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

                    if (_seen.Count <= _seen.MaxEntries) continue;

                    var evicted = _seen.Evict();
                    if (evicted > 0)
                        _logger.Info($"BackgroundIpEnrichment: Evicted {evicted:N0} IPs (remaining: {_seen.Count:N0})");
                }
                catch (OperationCanceledException) { break; }
            }
        }, stoppingToken);

        // Launch N concurrent I/O workers
        var tasks = new Task[_workerCount];
        for (var i = 0; i < _workerCount; i++)
        {
            var workerId = i;
            tasks[i] = Task.Run(() => RunWorkerAsync(workerId, stoppingToken), stoppingToken);
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        _logger.Info($"BackgroundIpEnrichment stopped. " +
                     $"Enqueued: {Volatile.Read(ref _enqueued):N0}, " +
                     $"Processed: {Volatile.Read(ref _processed):N0}, " +
                     $"Dedup skips: {Volatile.Read(ref _duplicatesSkipped):N0}");
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken ct)
    {
        var count = 0L;

        try
        {
            await foreach (var ip in _ipChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    // DNS lookup
                    if (_dns is not null)
                        await _dns.LookupAsync(ip, ct);

                    // WHOIS — only when MaxMind has country but no ASN (supplementary)
                    if (_whois is not null && _maxMind is not null)
                    {
                        var mmResult = _maxMind.Lookup(ip);
                        if (!mmResult.Asn.HasValue && mmResult.CountryCode is not null)
                        {
                            await _whois.LookupAsync(ip, ct);
                        }
                    }

                    count++;
                    Interlocked.Increment(ref _processed);

                    if (count % 1_000 == 0)
                    {
                        _logger.Info($"BackgroundIpEnrichment worker {workerId}: " +
                                     $"{count:N0} IPs processed, " +
                                     $"queue depth: {_ipChannel.Reader.Count:N0}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Debug($"BackgroundIpEnrichment: error for {ip} — {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        _logger.Info($"BackgroundIpEnrichment worker {workerId} stopped. Processed: {count:N0}");
    }
}
