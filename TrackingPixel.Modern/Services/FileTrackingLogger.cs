using System.Collections.Concurrent;
using System.Text;
using TrackingPixel.Configuration;

namespace TrackingPixel.Services;

/// <summary>
/// High-performance file logger with async buffering.
/// Uses a background writer to avoid blocking the hot path.
/// </summary>
public sealed class FileTrackingLogger : ITrackingLogger, IAsyncDisposable
{
    private readonly TrackingLogSettings _settings;
    private readonly string _logDirectory;
    private readonly BlockingCollection<LogEntry> _logQueue;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _cts;
    
    // Pre-cached uppercase padded level strings - avoids allocations in hot path
    private static readonly string[] LogLevelStrings =
    [
        "TRACE  ", // 0
        "DEBUG  ", // 1
        "INFO   ", // 2
        "WARNING", // 3
        "ERROR  ", // 4
        "NONE   "  // 5
    ];
    
    private readonly record struct LogEntry(DateTime Timestamp, TrackingLogLevel Level, string Message);
    
    public FileTrackingLogger(TrackingLogSettings settings)
    {
        _settings = settings;
        _logDirectory = Path.IsPathRooted(settings.LogDirectory) 
            ? settings.LogDirectory 
            : Path.Combine(AppContext.BaseDirectory, settings.LogDirectory);
        
        Directory.CreateDirectory(_logDirectory);
        
        _logQueue = new BlockingCollection<LogEntry>(boundedCapacity: 10000);
        _cts = new CancellationTokenSource();
        _writerTask = Task.Run(WriteLoopAsync);
    }
    
    public bool IsEnabled(TrackingLogLevel level) => level >= _settings.MinimumLevel;
    
    public void Trace(string message) => Log(TrackingLogLevel.Trace, message);
    public void Debug(string message) => Log(TrackingLogLevel.Debug, message);
    public void Info(string message) => Log(TrackingLogLevel.Info, message);
    public void Warning(string message) => Log(TrackingLogLevel.Warning, message);
    
    public void Error(string message, Exception? ex = null)
    {
        var fullMessage = ex != null ? $"{message}: {ex.Message}" : message;
        Log(TrackingLogLevel.Error, fullMessage);
    }
    
    private void Log(TrackingLogLevel level, string message)
    {
        if (!IsEnabled(level)) return;
        
        // Fire and forget - don't block caller
        _logQueue.TryAdd(new LogEntry(DateTime.UtcNow, level, message));
        
        if (_settings.WriteToConsole)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [{level}] {message}");
        }
    }
    
    private async Task WriteLoopAsync()
    {
        var buffer = new List<LogEntry>(100);
        var sb = new StringBuilder(4096);
        
        while (!_cts.Token.IsCancellationRequested || _logQueue.Count > 0)
        {
            buffer.Clear();
            
            try
            {
                // Block until we get an entry or timeout
                if (_logQueue.TryTake(out var first, 500, _cts.Token))
                {
                    buffer.Add(first);
                    
                    // Grab more if available (non-blocking)
                    while (buffer.Count < 100 && _logQueue.TryTake(out var item))
                    {
                        buffer.Add(item);
                    }
                    
                    // Build log content
                    sb.Clear();
                    foreach (var entry in buffer)
                    {
                        sb.Append('[').Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
                        sb.Append('[').Append(LogLevelStrings[(int)entry.Level]).Append("] ");
                        sb.AppendLine(entry.Message);
                    }
                    
                    // Write to file
                    var logFile = GetLogFilePath();
                    await File.AppendAllTextAsync(logFile, sb.ToString(), _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down - drain remaining
                break;
            }
            catch (Exception ex)
            {
                // Last resort - write to console if file fails
                Console.WriteLine($"[LOGGER ERROR] {ex.Message}");
            }
        }
        
        // Final drain on shutdown
        var drainSb = new StringBuilder(512);
        while (_logQueue.TryTake(out var remaining))
        {
            drainSb.Append('[').Append(remaining.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
            drainSb.Append('[').Append(LogLevelStrings[(int)remaining.Level]).Append("] ");
            drainSb.AppendLine(remaining.Message);
        }
        
        if (drainSb.Length > 0)
        {
            var logFile = GetLogFilePath();
            try { await File.AppendAllTextAsync(logFile, drainSb.ToString()); } catch { }
        }
    }
    
    private string GetLogFilePath()
    {
        return Path.Combine(_logDirectory, $"{DateTime.UtcNow:yyyy_MM_dd}.log");
    }
    
    public async ValueTask DisposeAsync()
    {
        _logQueue.CompleteAdding();
        _cts.Cancel();
        
        try
        {
            await _writerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Force stop if it takes too long
        }
        
        _cts.Dispose();
        _logQueue.Dispose();
    }
}
