using TrackingPixel.Configuration;

namespace TrackingPixel.Services;

// ============================================================================
// TRACKING LOGGER ABSTRACTION
//
// Lightweight logging interface used by all services in the tracking server.
// The single implementation is FileTrackingLogger (Channel<T>-backed async
// file writer). This abstraction exists so:
//   1. Services don't depend on the concrete logger (testable with mocks)
//   2. The hot path can short-circuit via IsEnabled() before string formatting
//   3. We avoid pulling in the full Microsoft.Extensions.Logging dependency
//      into every service (our logger is simpler and allocation-free on the
//      "not enabled" path)
//
// CONVENTION: ALL services in this project use ITrackingLogger. If you see
// ILogger<T> from Microsoft.Extensions.Logging, that's AI drift — convert it.
// ============================================================================

/// <summary>
/// Lightweight logging abstraction for tracking server operations.
/// <para>
/// Call <see cref="IsEnabled"/> before formatting expensive messages to
/// avoid string allocations when the log level is below the configured minimum.
/// Each method is a direct severity shortcut; <see cref="Error"/> supports
/// an optional <see cref="Exception"/> parameter for structured error details.
/// </para>
/// </summary>
public interface ITrackingLogger
{
    /// <summary>Ultra-verbose per-request data dumps.</summary>
    void Trace(string message);
    
    /// <summary>Developer diagnostics: cache states, batch sizes, lifecycle events.</summary>
    void Debug(string message);
    
    /// <summary>Normal operation: startup, ETL counts, queue status.</summary>
    void Info(string message);
    
    /// <summary>Recoverable issues: queue full, missing config, degraded operation.</summary>
    void Warning(string message);
    
    /// <summary>Failures: SQL errors, unhandled exceptions, data loss.</summary>
    void Error(string message, Exception? ex = null);
    
    /// <summary>
    /// Returns true if a message at <paramref name="level"/> would be written.
    /// Use to guard expensive string-formatting operations.
    /// </summary>
    bool IsEnabled(TrackingLogLevel level);
}
