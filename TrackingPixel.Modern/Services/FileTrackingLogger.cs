using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using TrackingPixel.Configuration;

namespace TrackingPixel.Services;

// ============================================================================
// FILE TRACKING LOGGER — High-performance async file writer.
//
// Architecture:
//   Callers  →  Channel<LogEntry>  →  WriteLoopAsync()  →  File.AppendAllTextAsync
//                (lock-free CAS)        (single reader)     (batched I/O)
//
// WHY NOT Microsoft.Extensions.Logging?
//   1. Our hot path (pixel capture) needs zero-alloc when logging is disabled
//   2. We need a single shared file per day with a specific naming convention
//   3. We want guaranteed flush-on-shutdown for diagnostic visibility
//   4. MEL's provider model adds indirection we don't need for one log sink
//
// PERFORMANCE CHARACTERISTICS:
//   • Log() is non-blocking: TryWrite to the channel is a single CAS (Compare-And-Swap)
//   • IsEnabled() is an inline integer comparison — zero cost when disabled
//   • LogEntry is a readonly record struct (~24 bytes on the stack, not heap)
//   • Channel has DropOldest overflow policy: callers NEVER block, even under
//     extreme log volume. Old entries are silently discarded.
//   • Write loop batches up to 100 entries per disk flush, reusing StringBuilder
//   • Log file path is cached and only regenerated when the UTC day changes
//
// SHUTDOWN BEHAVIOR:
//   1. DisposeAsync() signals the channel as complete and cancels the writer
//   2. Writer loop exits WaitToReadAsync, drains remaining entries
//   3. Final drain written to disk (best-effort, swallows exceptions)
//   4. CancellationTokenSource disposed
// ============================================================================

/// <summary>
/// Async file logger backed by <see cref="Channel{T}"/> for lock-free, non-blocking writes.
/// <para>
/// Single writer thread reads batches from the channel and flushes to a daily log file.
/// Implements <see cref="IAsyncDisposable"/> for graceful shutdown with final drain.
/// </para>
/// </summary>
public sealed class FileTrackingLogger : ITrackingLogger, IAsyncDisposable
{
    private readonly TrackingLogSettings _settings;
    private readonly string _logDirectory;
    private readonly Channel<LogEntry> _channel;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _cts;
    
    // Pre-cached uppercase padded level strings — indexed by enum ordinal (0–5).
    // Fixed 7-char width so log lines align vertically in text editors.
    private static readonly string[] LogLevelStrings =
    [
        "TRACE  ", // TrackingLogLevel.Trace   = 0
        "DEBUG  ", // TrackingLogLevel.Debug   = 1
        "INFO   ", // TrackingLogLevel.Info    = 2
        "WARNING", // TrackingLogLevel.Warning = 3
        "ERROR  ", // TrackingLogLevel.Error   = 4
        "NONE   "  // TrackingLogLevel.None    = 5 (should never appear in output)
    ];
    
    /// <summary>
    /// Immutable log entry queued through the channel. Stack-allocated (~24 bytes):
    ///   long TimestampTicks  = 8 bytes (UTC ticks, avoids DateTime struct overhead)
    ///   TrackingLogLevel     = 4 bytes (enum backed by int in memory)
    ///   string Message ref   = 8 bytes (managed pointer)
    ///   + 4 bytes padding    = 24 bytes total
    /// <c>readonly</c> prevents any mutation after construction.
    /// </summary>
    private readonly record struct LogEntry(long TimestampTicks, TrackingLogLevel Level, string Message);
    
    public FileTrackingLogger(TrackingLogSettings settings)
    {
        _settings = settings;
        
        // Resolve log directory: relative paths start from the publish output folder
        _logDirectory = Path.IsPathRooted(settings.LogDirectory) 
            ? settings.LogDirectory 
            : Path.Combine(AppContext.BaseDirectory, settings.LogDirectory);
        
        // Ensure the log directory exists (no-op if it already does)
        Directory.CreateDirectory(_logDirectory);
        
        // Pre-populate the cached log file path so it's never null on first write
        _cachedLogPath = Path.Combine(_logDirectory, $"{DateTime.UtcNow:yyyy_MM_dd}.log");
        
        // Bounded channel with DropOldest: if the writer can't keep up, old entries
        // are silently discarded. This guarantees the hot path (pixel capture) is
        // NEVER blocked by logging backpressure.
        _channel = Channel.CreateBounded<LogEntry>(
            new BoundedChannelOptions(10000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true // Enables lock-free optimized read path
            });
        _cts = new CancellationTokenSource();
        _writerTask = Task.Run(WriteLoopAsync);
    }
    
    /// <summary>
    /// Inline integer comparison — zero cost when the level is below minimum.
    /// Callers should check this before formatting expensive log messages.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEnabled(TrackingLogLevel level) => level >= _settings.MinimumLevel;
    
    // Convenience methods — each delegates to the shared Log() path.
    public void Trace(string message) => Log(TrackingLogLevel.Trace, message);
    public void Debug(string message) => Log(TrackingLogLevel.Debug, message);
    public void Info(string message) => Log(TrackingLogLevel.Info, message);
    public void Warning(string message) => Log(TrackingLogLevel.Warning, message);
    
    /// <summary>
    /// Logs an error with an optional exception. Concatenates the exception's
    /// Message property (not the full stack trace) to keep log lines scannable.
    /// Full stack traces can be recovered from stdout logs if needed.
    /// </summary>
    public void Error(string message, Exception? ex = null)
    {
        var fullMessage = ex is not null ? $"{message}: {ex.Message}" : message;
        Log(TrackingLogLevel.Error, fullMessage);
    }
    
    /// <summary>
    /// Core log method. Returns immediately after a lock-free CAS write to the channel.
    /// If the channel is full, the oldest entry is dropped (DropOldest policy).
    /// Also echoes to Console.WriteLine if WriteToConsole is enabled (dev only).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Log(TrackingLogLevel level, string message)
    {
        if (!IsEnabled(level)) return;
        
        // Fire and forget — Channel.TryWrite is a single CAS operation
        _channel.Writer.TryWrite(new LogEntry(DateTime.UtcNow.Ticks, level, message));
        
        // Console echo for local development visibility (disable in production)
        if (_settings.WriteToConsole)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [{level}] {message}");
        }
    }
    
    /// <summary>
    /// Background writer loop — runs on a dedicated thread via Task.Run.
    /// Batches up to 100 entries per disk flush for I/O efficiency.
    /// Uses a shared StringBuilder (cleared each cycle) to build the output
    /// string, then writes the entire batch in a single File.AppendAllTextAsync call.
    /// </summary>
    private async Task WriteLoopAsync()
    {
        var buffer = new List<LogEntry>(100);
        var sb = new StringBuilder(4096);
        var reader = _channel.Reader;
        
        while (!_cts.Token.IsCancellationRequested)
        {
            buffer.Clear();
            
            try
            {
                // WaitToReadAsync: async wait with no thread burn. Wakes when
                // at least one item is available or cancellation is requested.
                if (await reader.WaitToReadAsync(_cts.Token))
                {
                    // Synchronous drain: items are already buffered in memory,
                    // so TryRead is essentially a pointer copy (no await needed).
                    while (buffer.Count < 100 && reader.TryRead(out var item))
                    {
                        buffer.Add(item);
                    }
                    
                    // Format all entries into a single string
                    sb.Clear();
                    foreach (var entry in buffer)
                    {
                        // Reconstruct DateTime from ticks for formatting
                        var ts = new DateTime(entry.TimestampTicks, DateTimeKind.Utc);
                        sb.Append('[').Append(ts.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
                        sb.Append('[').Append(LogLevelStrings[(int)entry.Level]).Append("] ");
                        sb.AppendLine(entry.Message);
                    }
                    
                    // Single I/O call for the entire batch
                    var logFile = GetLogFilePath();
                    await File.AppendAllTextAsync(logFile, sb.ToString(), _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break; // Shutdown requested — fall through to final drain
            }
            catch (Exception ex)
            {
                // Last resort: write to console so the error isn't completely lost.
                // We can't log to our own file logger (infinite recursion).
                Console.WriteLine($"[LOGGER ERROR] {ex.Message}");
            }
        }
        
        // Final drain on shutdown: read all remaining entries and write them.
        // This happens after the main loop exits (cancellation was requested).
        var drainSb = new StringBuilder(512);
        while (reader.TryRead(out var remaining))
        {
            var ts = new DateTime(remaining.TimestampTicks, DateTimeKind.Utc);
            drainSb.Append('[').Append(ts.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
            drainSb.Append('[').Append(LogLevelStrings[(int)remaining.Level]).Append("] ");
            drainSb.AppendLine(remaining.Message);
        }
        
        if (drainSb.Length > 0)
        {
            // Best-effort write — swallow exceptions during shutdown
            try { await File.AppendAllTextAsync(GetLogFilePath(), drainSb.ToString()); } catch { }
        }
    }
    
    // ========================================================================
    // LOG FILE PATH CACHING
    // The file path only changes once per UTC day (yyyy_MM_dd.log format).
    // We cache the computed path and only regenerate when the day changes.
    // This eliminates Path.Combine + string interpolation on every write cycle.
    // ========================================================================
    private string _cachedLogPath;
    private int _cachedDayOfYear = -1;
    
    /// <summary>
    /// Returns the current day's log file path, regenerating only on UTC day change.
    /// The day key is <c>DayOfYear + Year * 366</c> to uniquely identify each day
    /// across year boundaries (366 = max days in a leap year).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetLogFilePath()
    {
        var now = DateTime.UtcNow;
        var dayOfYear = now.DayOfYear + now.Year * 366;
        if (dayOfYear != _cachedDayOfYear)
        {
            _cachedDayOfYear = dayOfYear;
            _cachedLogPath = Path.Combine(_logDirectory, $"{now:yyyy_MM_dd}.log");
        }
        return _cachedLogPath;
    }
    
    /// <summary>
    /// Graceful shutdown: signals channel completion, cancels the writer loop,
    /// waits up to 5 seconds for the final drain, then disposes the CTS.
    /// Called from Program.cs via the ApplicationStopping lifetime hook.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _cts.CancelAsync();
        
        try
        {
            await _writerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Writer didn't finish in time — accept possible data loss
        }
        
        _cts.Dispose();
    }
}
