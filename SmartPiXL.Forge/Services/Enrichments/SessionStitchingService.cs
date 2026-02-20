using System.Collections.Concurrent;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services.Enrichments;

// ============================================================================
// SESSION STITCHING SERVICE — In-memory session tracking keyed by composite
// fingerprint hash.
//
// The PiXL Script captures multiple fingerprint signals that together form a
// device identity: canvasFP + audioHash → DeviceHash. The Forge sees all hits
// in sequence and can stitch them into sessions.
//
// SESSION RULES:
//   - New session if first hit from this fingerprint, OR gap > 30 minutes
//   - Each session gets a GUID, tracks: page sequence, hit count, duration,
//     entry page, last page
//   - Sessions are finalized + evicted after 30 minutes of inactivity
//
// MEMORY MANAGEMENT:
//   ConcurrentDictionary<fingerprintHash, SessionState>.
//   Periodic eviction of sessions idle > 30 minutes, runs every 2 minutes.
//   Estimated memory: ~500 bytes per session × 50K concurrent = ~25 MB.
//
// APPENDED PARAMS:
//   _srv_sessionId={guid}
//   _srv_sessionHitNum={N}
//   _srv_sessionDurationSec={seconds}
//   _srv_sessionPages={count}
// ============================================================================

/// <summary>
/// Stitches individual pixel hits into sessions based on fingerprint identity
/// and 30-minute inactivity timeout. Singleton — thread-safe.
/// </summary>
public sealed class SessionStitchingService
{
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITrackingLogger _logger;
    private DateTime _lastEviction = DateTime.UtcNow;

    private static readonly TimeSpan s_sessionTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan s_evictionInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Result of session stitching for a single hit.
    /// </summary>
    public readonly record struct SessionResult(
        string SessionId,
        int HitNumber,
        int DurationSec,
        int PageCount);

    public SessionStitchingService(ITrackingLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Records a hit for the given fingerprint and returns the session context.
    /// Creates a new session if this is the first hit or the previous session
    /// has timed out (30+ minutes of inactivity).
    /// </summary>
    /// <param name="fingerprintHash">Composite fingerprint hash (DeviceHash, canvasFP+audioHash, etc.).</param>
    /// <param name="pagePath">The page URL/path for this hit (for page counting).</param>
    /// <returns>Session information for this hit.</returns>
    public SessionResult RecordHit(string? fingerprintHash, string? pagePath)
    {
        if (string.IsNullOrEmpty(fingerprintHash))
            return new SessionResult(Guid.NewGuid().ToString("D"), 1, 0, 1);

        var now = DateTime.UtcNow;

        var session = _sessions.AddOrUpdate(
            fingerprintHash,
            // Factory: create new session
            _ => CreateNewSession(now, pagePath),
            // Update: extend existing session or start new one if timed out
            (_, existing) =>
            {
                lock (existing)
                {
                    if ((now - existing.LastHitAt) > s_sessionTimeout)
                    {
                        // Session timed out — create a new one
                        return CreateNewSession(now, pagePath);
                    }

                    // Extend existing session
                    existing.HitCount++;
                    existing.LastHitAt = now;

                    // Track distinct pages
                    if (pagePath is not null && !existing.Pages.Contains(pagePath))
                        existing.Pages.Add(pagePath);

                    return existing;
                }
            });

        // Periodic eviction
        if ((now - _lastEviction) > s_evictionInterval)
        {
            _lastEviction = now;
            EvictStaleSessions(now);
        }

        int hitNumber;
        int durationSec;
        int pageCount;

        lock (session)
        {
            hitNumber = session.HitCount;
            durationSec = (int)(session.LastHitAt - session.StartedAt).TotalSeconds;
            pageCount = session.Pages.Count;
        }

        return new SessionResult(session.SessionId, hitNumber, durationSec, pageCount);
    }

    /// <summary>
    /// Creates a new session state with the given start time and entry page.
    /// </summary>
    private static SessionState CreateNewSession(DateTime now, string? entryPage)
    {
        var state = new SessionState
        {
            SessionId = Guid.NewGuid().ToString("D"),
            StartedAt = now,
            LastHitAt = now,
            HitCount = 1
        };

        if (entryPage is not null)
            state.Pages.Add(entryPage);

        return state;
    }

    /// <summary>
    /// Removes sessions that have been idle longer than the session timeout.
    /// Called periodically from <see cref="RecordHit"/>.
    /// </summary>
    private void EvictStaleSessions(DateTime now)
    {
        var evicted = 0;
        foreach (var kvp in _sessions)
        {
            bool isStale;
            lock (kvp.Value)
            {
                isStale = (now - kvp.Value.LastHitAt) > s_sessionTimeout;
            }

            if (isStale)
            {
                _sessions.TryRemove(kvp.Key, out _);
                evicted++;
            }
        }

        if (evicted > 0)
            _logger.Debug($"SessionStitching: evicted {evicted} timed-out sessions. Active: {_sessions.Count}");
    }

    /// <summary>
    /// Returns the current number of active sessions (for diagnostics).
    /// </summary>
    public int ActiveSessionCount => _sessions.Count;

    /// <summary>
    /// Internal state for a single session.
    /// </summary>
    internal sealed class SessionState
    {
        public string SessionId { get; init; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime LastHitAt { get; set; }
        public int HitCount { get; set; }
        public HashSet<string> Pages { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
