using System.Collections.Concurrent;

namespace SmartPiXL.Services;

// ============================================================================
// BOUNDED CACHE — Thread-safe cache with hybrid eviction (time + count).
//
// Replaces the "nuclear Clear()" pattern used across multiple enrichment
// services. Instead of wiping the entire cache at a threshold (causing
// cache miss storms across all workers), this evicts:
//   Phase 1: Entries older than MaxAge (stale data, no longer relevant)
//   Phase 2: If still over cap, keep only the most recent EvictTarget
//            entries by timestamp (preserves hot entries)
//
// USAGE:
//   var cache = new BoundedCache<string, MyResult>(
//       maxEntries: 50_000,
//       evictTarget: 25_000,
//       maxAge: TimeSpan.FromMinutes(30));
//
//   if (cache.TryGet(key, out var result)) return result;
//   result = ExpensiveCompute(key);
//   cache.Set(key, result);
//
//   // Call periodically from a Timer or inline check:
//   if (cache.Count > cache.MaxEntries) cache.Evict();
//
// THREAD-SAFETY:
//   All operations are lock-free (ConcurrentDictionary). Evict() is safe to
//   call from any thread — concurrent reads/writes continue during eviction.
//   Multiple concurrent Evict() calls are harmless (idempotent).
// ============================================================================

/// <summary>
/// Thread-safe bounded cache with hybrid time + count eviction.
/// Wraps <see cref="ConcurrentDictionary{TKey, TValue}"/> with per-entry
/// timestamps for intelligent eviction instead of nuclear <c>Clear()</c>.
/// </summary>
public sealed class BoundedCache<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, (TValue Value, long Timestamp)> _dict;
    private readonly int _evictTarget;
    private readonly long _maxAgeMs;

    /// <summary>Maximum entries before eviction should be triggered.</summary>
    public int MaxEntries { get; }

    /// <summary>Current number of cached entries.</summary>
    public int Count => _dict.Count;

    /// <param name="maxEntries">Eviction trigger threshold.</param>
    /// <param name="evictTarget">Target count after cap-based eviction (keep newest).</param>
    /// <param name="maxAge">Entries older than this are evicted regardless of count.</param>
    public BoundedCache(int maxEntries, int evictTarget, TimeSpan maxAge)
    {
        MaxEntries = maxEntries;
        _evictTarget = evictTarget;
        _maxAgeMs = (long)maxAge.TotalMilliseconds;
        _dict = new ConcurrentDictionary<TKey, (TValue, long)>();
    }

    /// <param name="maxEntries">Eviction trigger threshold.</param>
    /// <param name="evictTarget">Target count after cap-based eviction (keep newest).</param>
    /// <param name="maxAge">Entries older than this are evicted regardless of count.</param>
    /// <param name="comparer">Key equality comparer.</param>
    public BoundedCache(int maxEntries, int evictTarget, TimeSpan maxAge, IEqualityComparer<TKey> comparer)
    {
        MaxEntries = maxEntries;
        _evictTarget = evictTarget;
        _maxAgeMs = (long)maxAge.TotalMilliseconds;
        _dict = new ConcurrentDictionary<TKey, (TValue, long)>(comparer);
    }

    /// <summary>
    /// Attempts to retrieve a cached value. Returns false on miss.
    /// Lock-free hash table lookup (~100ns).
    /// </summary>
    public bool TryGet(TKey key, out TValue value)
    {
        if (_dict.TryGetValue(key, out var entry))
        {
            value = entry.Value;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>
    /// Adds or updates a cache entry with the current timestamp.
    /// Lock-free via ConcurrentDictionary indexer.
    /// </summary>
    public void Set(TKey key, TValue value)
    {
        _dict[key] = (value, Environment.TickCount64);
    }

    /// <summary>
    /// Adds a cache entry only if the key doesn't already exist.
    /// Returns true if added, false if key was already present.
    /// </summary>
    public bool TryAdd(TKey key, TValue value)
    {
        return _dict.TryAdd(key, (value, Environment.TickCount64));
    }

    /// <summary>
    /// Checks whether a key exists in the cache.
    /// </summary>
    public bool ContainsKey(TKey key) => _dict.ContainsKey(key);

    /// <summary>
    /// Hybrid eviction: remove stale entries by age, then cap by count.
    /// Safe to call from any thread. Concurrent reads/writes continue
    /// during eviction. Call when <see cref="Count"/> exceeds <see cref="MaxEntries"/>.
    /// </summary>
    /// <returns>Number of entries evicted.</returns>
    public int Evict()
    {
        var now = Environment.TickCount64;
        var evicted = 0;

        // Phase 1: Remove entries older than MaxAge
        foreach (var kvp in _dict)
        {
            if (now - kvp.Value.Timestamp > _maxAgeMs)
            {
                if (_dict.TryRemove(kvp.Key, out _))
                    evicted++;
            }
        }

        // Phase 2: If still over cap, keep only the most recent EvictTarget
        if (_dict.Count > MaxEntries)
        {
            var toRemove = _dict
                .OrderBy(kvp => kvp.Value.Timestamp)
                .Take(_dict.Count - _evictTarget)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                if (_dict.TryRemove(key, out _))
                    evicted++;
            }
        }

        return evicted;
    }
}
