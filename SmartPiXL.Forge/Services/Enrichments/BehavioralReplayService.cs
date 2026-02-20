// ─────────────────────────────────────────────────────────────────────────────
// SmartPiXL Forge — Behavioral Replay Detection Service
// Hashes mouse movement paths and detects when the SAME path is replayed
// across DIFFERENT browser fingerprints. Identical paths from different
// sessions are a hallmark of automation replay attacks.
// Phase 6 — Tier 3 Enrichments (Asymmetric Detection)
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Concurrent;

namespace SmartPiXL.Forge.Services.Enrichments;

/// <summary>
/// Detects mouse path replay attacks by hashing normalized path strings
/// and tracking which fingerprints produced each unique path hash.
/// Same path + same fingerprint = normal revisit.
/// Same path + different fingerprint = REPLAY DETECTED.
/// </summary>
/// <remarks>
/// Uses FNV-1a (32-bit) for fast non-cryptographic hashing. The hash is
/// used as a lookup key, not for security. Collision probability at 100K
/// entries is ~0.1% (acceptable for a heuristic signal).
/// Entries are evicted after 1 hour to prevent unbounded memory growth.
/// </remarks>
public sealed class BehavioralReplayService : IDisposable
{
    // ════════════════════════════════════════════════════════════════════════
    // RESULT
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Result of a replay check.
    /// </summary>
    /// <param name="Detected">True if the same path was seen from a different fingerprint.</param>
    /// <param name="MatchFingerprint">The fingerprint that originally produced this path.</param>
    /// <param name="ReplayCount">Number of different fingerprints that have replayed this path.</param>
    public readonly record struct ReplayResult(bool Detected, string? MatchFingerprint, int ReplayCount);

    // ════════════════════════════════════════════════════════════════════════
    // CACHE ENTRY
    // ════════════════════════════════════════════════════════════════════════

    private sealed class ReplayEntry
    {
        public string FirstFingerprint = string.Empty;
        public DateTime FirstSeen;
        public DateTime LastSeen;
        public int ReplayCount;
    }

    // ════════════════════════════════════════════════════════════════════════
    // STATE
    // ════════════════════════════════════════════════════════════════════════

    private readonly ConcurrentDictionary<uint, ReplayEntry> _pathCache = new();
    private readonly Timer _evictionTimer;

    private const int EvictionIntervalMs = 300_000; // 5 min
    private static readonly TimeSpan s_entryTtl = TimeSpan.FromHours(1);
    private const int MinPathLength = 10; // Ignore trivially short paths

    // ════════════════════════════════════════════════════════════════════════
    // FNV-1a CONSTANTS (32-bit)
    // ════════════════════════════════════════════════════════════════════════

    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    // ════════════════════════════════════════════════════════════════════════
    // CONSTRUCTOR
    // ════════════════════════════════════════════════════════════════════════

    public BehavioralReplayService()
    {
        _evictionTimer = new Timer(EvictStaleEntries, null, EvictionIntervalMs, EvictionIntervalMs);
    }

    // ════════════════════════════════════════════════════════════════════════
    // PUBLIC API
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks whether a mouse path has been replayed by a different fingerprint.
    /// Normalizes the path by quantizing coordinates to 10px grid and timestamps
    /// to 100ms buckets before hashing, to catch replays that add small jitter.
    /// </summary>
    /// <param name="mousePath">Raw mouse path string (format: "x,y,t|x,y,t|...").</param>
    /// <param name="fingerprint">Browser fingerprint hash (deviceHash or canvasFP).</param>
    public ReplayResult Check(string? mousePath, string? fingerprint)
    {
        if (string.IsNullOrEmpty(mousePath) || mousePath.Length < MinPathLength ||
            string.IsNullOrEmpty(fingerprint))
        {
            return new ReplayResult(false, null, 0);
        }

        // Normalize path: quantize coordinates to 10px grid, timestamps to 100ms
        var normalizedHash = HashNormalizedPath(mousePath);

        var now = DateTime.UtcNow;

        if (_pathCache.TryGetValue(normalizedHash, out var entry))
        {
            entry.LastSeen = now;

            // Same fingerprint → same visitor revisiting (not a replay)
            if (entry.FirstFingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
                return new ReplayResult(false, null, entry.ReplayCount);

            // Different fingerprint → REPLAY DETECTED
            Interlocked.Increment(ref entry.ReplayCount);
            return new ReplayResult(true, entry.FirstFingerprint, entry.ReplayCount);
        }

        // New path → store it
        var newEntry = new ReplayEntry
        {
            FirstFingerprint = fingerprint,
            FirstSeen = now,
            LastSeen = now,
            ReplayCount = 0
        };

        _pathCache.TryAdd(normalizedHash, newEntry);
        return new ReplayResult(false, null, 0);
    }

    // ════════════════════════════════════════════════════════════════════════
    // NORMALIZATION + HASHING
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Normalizes the mouse path by quantizing coordinates to 10px grid and
    /// timestamps to 100ms buckets, then hashes the normalized representation.
    /// This catches replays that add small random noise to evade exact matching.
    /// </summary>
    /// <remarks>
    /// Path format: "x,y,t|x,y,t|..." where x/y are pixel coordinates and
    /// t is a timestamp (ms since page load). Quantization:
    ///   x → x / 10 * 10 (nearest 10px)
    ///   y → y / 10 * 10
    ///   t → t / 100 * 100 (nearest 100ms)
    /// The quantized values are hashed via FNV-1a as a stream of integers.
    /// </remarks>
    private static uint HashNormalizedPath(string path)
    {
        var hash = FnvOffsetBasis;
        var span = path.AsSpan();

        foreach (var pointRange in span.Split('|'))
        {
            var point = span[pointRange];
            if (point.IsEmpty) continue;

            // Parse x,y,t from this point segment
            var commaIdx1 = point.IndexOf(',');
            if (commaIdx1 < 0) continue;

            var commaIdx2 = point[(commaIdx1 + 1)..].IndexOf(',');
            if (commaIdx2 < 0)
            {
                // Only x,y — no timestamp. Hash quantized x and y.
                if (int.TryParse(point[..commaIdx1], out var x2) &&
                    int.TryParse(point[(commaIdx1 + 1)..], out var y2))
                {
                    hash = HashInt(hash, x2 / 10);
                    hash = HashInt(hash, y2 / 10);
                }
                continue;
            }

            commaIdx2 += commaIdx1 + 1; // adjust for absolute position in point

            if (int.TryParse(point[..commaIdx1], out var x) &&
                int.TryParse(point[(commaIdx1 + 1)..commaIdx2], out var y) &&
                int.TryParse(point[(commaIdx2 + 1)..], out var t))
            {
                // Quantize and hash
                hash = HashInt(hash, x / 10);
                hash = HashInt(hash, y / 10);
                hash = HashInt(hash, t / 100);
            }
        }

        return hash;
    }

    /// <summary>
    /// FNV-1a hash step for a 32-bit integer (4 bytes).
    /// </summary>
    private static uint HashInt(uint hash, int value)
    {
        hash ^= (uint)(value & 0xFF);
        hash *= FnvPrime;
        hash ^= (uint)((value >> 8) & 0xFF);
        hash *= FnvPrime;
        hash ^= (uint)((value >> 16) & 0xFF);
        hash *= FnvPrime;
        hash ^= (uint)((value >> 24) & 0xFF);
        hash *= FnvPrime;
        return hash;
    }

    // ════════════════════════════════════════════════════════════════════════
    // EVICTION
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Removes entries older than 1 hour to prevent unbounded memory growth.
    /// </summary>
    private void EvictStaleEntries(object? state)
    {
        var cutoff = DateTime.UtcNow - s_entryTtl;

        foreach (var kvp in _pathCache)
        {
            if (kvp.Value.LastSeen < cutoff)
                _pathCache.TryRemove(kvp.Key, out _);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // DISPOSE
    // ════════════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        _evictionTimer.Dispose();
    }
}
