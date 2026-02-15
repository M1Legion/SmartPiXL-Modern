using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using TrackingPixel.Configuration;
using TrackingPixel.Models;

namespace TrackingPixel.Services;

// ============================================================================
// GEO CACHE SERVICE — In-memory IP geolocation lookup backed by IPAPI.IP.
//
// ARCHITECTURE:
//   Hot path (CaptureAndEnqueue)  →  TryLookup()  →  ConcurrentDictionary  →  GeoResult
//                                                         ↓ miss
//                                                    MemoryCache (1h TTL)
//                                                         ↓ miss
//                                                    IPAPI.IP (async SQL)
//
// WHY TWO CACHE LAYERS?
//   ConcurrentDictionary: O(1) thread-safe reads for the hottest IPs. No TTL
//     overhead, no MemoryCache entry allocation. Contains only IPs seen in
//     the current process lifetime.
//   MemoryCache: Provides TTL-based eviction (1h sliding) for IPs looked up
//     from SQL. Prevents stale geo data from persisting across IPAPI syncs.
//     Also prevents the ConcurrentDictionary from growing unbounded.
//
// HOT PATH BEHAVIOR:
//   TryLookup() is synchronous — returns immediately from cache or NotFound.
//   It never blocks the HTTP thread. If an IP isn't cached, it queues an
//   async SQL lookup on a background thread. The NEXT hit from that IP
//   will find the result in cache.
//
// This means the first hit from a new IP won't have geo data in the
// _srv_geo* params, but the ETL pipeline will still enrich it via JOIN.
// The hot-path geo is best-effort for real-time bot signals (TZ mismatch).
// ============================================================================

/// <summary>
/// Provides non-blocking in-memory geolocation lookups for IP addresses.
/// <para>
/// Backed by the <c>IPAPI.IP</c> table (342M+ rows of IP-API Pro data).
/// Uses a two-tier cache: <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// for zero-contention hot reads, and <see cref="IMemoryCache"/> with
/// sliding TTL for SQL-backed lookups.
/// </para>
/// </summary>
public sealed class GeoCacheService
{
    private readonly IMemoryCache _memoryCache;
    private readonly TrackingSettings _settings;
    private readonly ITrackingLogger _logger;
    
    // Hot cache — only IPs currently being seen. No TTL, evicted in bulk periodically.
    // ConcurrentDictionary is lock-free for reads (the common case).
    private readonly ConcurrentDictionary<string, GeoResult> _hotCache = new(StringComparer.OrdinalIgnoreCase);
    
    // Set of IPs currently being looked up — prevents duplicate SQL queries
    private readonly ConcurrentDictionary<string, byte> _pendingLookups = new(StringComparer.OrdinalIgnoreCase);
    
    // Metrics
    private long _cacheHits;
    private long _cacheMisses;
    private long _sqlLookups;
    private long _sqlErrors;
    
    /// <summary>Number of IPs currently in the hot cache.</summary>
    public int HotCacheSize => _hotCache.Count;
    
    /// <summary>Total cache hits (hot cache + memory cache).</summary>
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    
    /// <summary>Total cache misses (IP not in any cache tier).</summary>
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);
    
    /// <summary>Total SQL lookups issued.</summary>
    public long SqlLookups => Interlocked.Read(ref _sqlLookups);

    public GeoCacheService(
        IMemoryCache memoryCache,
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _memoryCache = memoryCache;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Non-blocking geolocation lookup. Returns immediately from cache.
    /// If the IP is not cached, queues an async SQL lookup for next time.
    /// <para>
    /// This is the hot-path method called from <c>CaptureAndEnqueue</c>.
    /// It NEVER blocks, NEVER allocates on cache hit, and NEVER does I/O.
    /// </para>
    /// </summary>
    /// <param name="ip">The IP address to look up (IPv4 or IPv6 string).</param>
    /// <returns>
    /// <see cref="GeoResult"/> with <c>Found=true</c> on cache hit,
    /// or <see cref="GeoResult.NotFound"/> on cache miss.
    /// </returns>
    public GeoResult TryLookup(string ip)
    {
        if (string.IsNullOrEmpty(ip))
            return GeoResult.NotFound;
        
        // Tier 1: Hot cache — ConcurrentDictionary, zero-contention read
        if (_hotCache.TryGetValue(ip, out var result))
        {
            Interlocked.Increment(ref _cacheHits);
            return result;
        }
        
        // Tier 2: Memory cache — IMemoryCache with sliding TTL
        if (_memoryCache.TryGetValue($"geo:{ip}", out GeoResult cached))
        {
            // Promote to hot cache for subsequent reads
            _hotCache.TryAdd(ip, cached);
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }
        
        // Cache miss — queue async SQL lookup, return NotFound for now
        Interlocked.Increment(ref _cacheMisses);
        QueueAsyncLookup(ip);
        return GeoResult.NotFound;
    }
    
    /// <summary>
    /// Synchronous cache-only lookup. Returns null if not in any cache tier.
    /// Used by dashboard endpoints where blocking is undesirable but null is fine.
    /// </summary>
    public GeoResult? GetFromCache(string ip)
    {
        if (_hotCache.TryGetValue(ip, out var result))
            return result;
        if (_memoryCache.TryGetValue($"geo:{ip}", out GeoResult cached))
            return cached;
        return null;
    }

    /// <summary>
    /// Fires an async SQL lookup on a ThreadPool thread. Does NOT block the caller.
    /// De-duplicated: only one lookup per IP can be in flight at a time.
    /// </summary>
    private void QueueAsyncLookup(string ip)
    {
        // Prevent duplicate lookups for the same IP
        if (!_pendingLookups.TryAdd(ip, 0))
            return;
        
        // Fire-and-forget on ThreadPool — does NOT block CaptureAndEnqueue
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await LookupFromSqlAsync(ip);
                if (result.Found)
                {
                    // Store in both cache tiers
                    _hotCache[ip] = result;
                    _memoryCache.Set($"geo:{ip}", result, new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = TimeSpan.FromHours(1),
                        Size = 1  // For cache size limiting if configured
                    });
                }
                else
                {
                    // Cache the "not found" too — prevents re-querying IPs not in IPAPI
                    _memoryCache.Set($"geo:{ip}", GeoResult.NotFound, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15),
                        Size = 1
                    });
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _sqlErrors);
                _logger.Warning($"Geo lookup failed for {ip}: {ex.Message}");
            }
            finally
            {
                _pendingLookups.TryRemove(ip, out _);
            }
        });
    }

    /// <summary>
    /// Queries IPAPI.IP for a single IP address.
    /// Returns <see cref="GeoResult.NotFound"/> if the IP doesn't exist or has bad status.
    /// </summary>
    private async Task<GeoResult> LookupFromSqlAsync(string ip)
    {
        Interlocked.Increment(ref _sqlLookups);
        
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();
        
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 Country, CountryCode, RegionName, City, Zip,
                   Lat, Lon, Timezone, ISP, Org, Proxy, Mobile
            FROM IPAPI.IP
            WHERE IP = @IP AND Status = 'success'";
        cmd.Parameters.AddWithValue("@IP", ip);
        cmd.CommandTimeout = 5; // Fast timeout — don't slow down under pressure
        
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return GeoResult.NotFound;
        
        return new GeoResult
        {
            Found = true,
            Country = reader.IsDBNull(0) ? null : reader.GetString(0),
            CountryCode = reader.IsDBNull(1) ? null : reader.GetString(1),
            Region = reader.IsDBNull(2) ? null : reader.GetString(2),
            City = reader.IsDBNull(3) ? null : reader.GetString(3),
            Zip = reader.IsDBNull(4) ? null : reader.GetString(4),
            Lat = reader.IsDBNull(5) ? null : double.TryParse(reader.GetString(5), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ? lat : null,
            Lon = reader.IsDBNull(6) ? null : double.TryParse(reader.GetString(6), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) ? lon : null,
            Timezone = reader.IsDBNull(7) ? null : reader.GetString(7),
            ISP = reader.IsDBNull(8) ? null : reader.GetString(8),
            Org = reader.IsDBNull(9) ? null : reader.GetString(9),
            IsProxy = reader.IsDBNull(10) ? null : string.Equals(reader.GetString(10), "true", StringComparison.OrdinalIgnoreCase),
            IsMobile = reader.IsDBNull(11) ? null : string.Equals(reader.GetString(11), "true", StringComparison.OrdinalIgnoreCase)
        };
    }
    
    /// <summary>
    /// Evicts the hot cache. Called periodically to prevent unbounded growth.
    /// The MemoryCache tier handles its own eviction via TTL.
    /// </summary>
    public void ClearHotCache()
    {
        var count = _hotCache.Count;
        _hotCache.Clear();
        _logger.Info($"GeoCacheService: cleared hot cache ({count} entries)");
    }
    
    /// <summary>
    /// Pre-warms the cache with the most frequently seen IPs.
    /// Called on startup or after a sync to reduce first-hit SQL lookups.
    /// </summary>
    public async Task PrewarmAsync(int topN = 1000, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct);
            
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT TOP (@TopN) pip.IPAddress, 
                       ipa.Country, ipa.CountryCode, ipa.RegionName, ipa.City, ipa.Zip,
                       ipa.Lat, ipa.Lon, ipa.Timezone, ipa.ISP, ipa.Org, ipa.Proxy, ipa.Mobile
                FROM PiXL.IP pip
                INNER JOIN IPAPI.IP ipa ON pip.IPAddress = ipa.IP
                WHERE ipa.Status = 'success'
                ORDER BY pip.HitCount DESC";
            cmd.Parameters.AddWithValue("@TopN", topN);
            cmd.CommandTimeout = 30;
            
            var count = 0;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var ip = reader.GetString(0);
                var result = new GeoResult
                {
                    Found = true,
                    Country = reader.IsDBNull(1) ? null : reader.GetString(1),
                    CountryCode = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Region = reader.IsDBNull(3) ? null : reader.GetString(3),
                    City = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Zip = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Lat = reader.IsDBNull(6) ? null : double.TryParse(reader.GetString(6), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ? lat : null,
                    Lon = reader.IsDBNull(7) ? null : double.TryParse(reader.GetString(7), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) ? lon : null,
                    Timezone = reader.IsDBNull(8) ? null : reader.GetString(8),
                    ISP = reader.IsDBNull(9) ? null : reader.GetString(9),
                    Org = reader.IsDBNull(10) ? null : reader.GetString(10),
                    IsProxy = reader.IsDBNull(11) ? null : string.Equals(reader.GetString(11), "true", StringComparison.OrdinalIgnoreCase),
                    IsMobile = reader.IsDBNull(12) ? null : string.Equals(reader.GetString(12), "true", StringComparison.OrdinalIgnoreCase)
                };
                
                _hotCache[ip] = result;
                _memoryCache.Set($"geo:{ip}", result, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromHours(1),
                    Size = 1
                });
                count++;
            }
            
            _logger.Info($"GeoCacheService: prewarmed {count} IPs into cache");
        }
        catch (Exception ex)
        {
            _logger.Warning($"GeoCacheService: prewarm failed — {ex.Message}");
        }
    }
}
