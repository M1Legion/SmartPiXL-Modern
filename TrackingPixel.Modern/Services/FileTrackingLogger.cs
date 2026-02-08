using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using TrackingPixel.Configuration;

namespace TrackingPixel.Services;

/// <summary>
/// High-performance file logger with Channel&lt;T&gt; async buffering.
/// Channel is lock-free and natively async — no thread burn while waiting for entries.
/// </summary>
public sealed class FileTrackingLogger : ITrackingLogger, IAsyncDisposable
{
    private readonly TrackingLogSettings _settings;
    private readonly string _logDirectory;
    private readonly Channel<LogEntry> _channel;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _cts;
    
    // Pre-cached uppercase padded level strings — indexed by enum ordinal
    private static readonly string[] LogLevelStrings =
    [
        "TRACE  ", // 0
        "DEBUG  ", // 1
        "INFO   ", // 2
        "WARNING", // 3
        "ERROR  ", // 4
        "NONE   "  // 5
    ];
    
    // 16 bytes on stack, no GC pressure — readonly prevents accidental mutation
    private readonly record struct LogEntry(long TimestampTicks, TrackingLogLevel Level, string Message);
    
    public FileTrackingLogger(TrackingLogSettings settings)
    {
        _settings = settings;
        _logDirectory = Path.IsPathRooted(settings.LogDirectory) 
            ? settings.LogDirectory 
            : Path.Combine(AppContext.BaseDirectory, settings.LogDirectory);
        
        Directory.CreateDirectory(_logDirectory);
        
        _channel = Channel.CreateBounded<LogEntry>(
            new BoundedChannelOptions(10000)
            {
                FullMode = BoundedChannelFullMode.DropOldest, // Never block the hot path
                SingleReader = true
            });
        _cts = new CancellationTokenSource();
        _writerTask = Task.Run(WriteLoopAsync);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEnabled(TrackingLogLevel level) => level >= _settings.MinimumLevel;
    
    public void Trace(string message) => Log(TrackingLogLevel.Trace, message);
    public void Debug(string message) => Log(TrackingLogLevel.Debug, message);
    public void Info(string message) => Log(TrackingLogLevel.Info, message);
    public void Warning(string message) => Log(TrackingLogLevel.Warning, message);
    
    public void Error(string message, Exception? ex = null)
    {
        var fullMessage = ex is not null ? $"{message}: {ex.Message}" : message;
        Log(TrackingLogLevel.Error, fullMessage);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Log(TrackingLogLevel level, string message)
    {
        if (!IsEnabled(level)) return;
        
        // Fire and forget — Channel.TryWrite is lock-free CAS
        _channel.Writer.TryWrite(new LogEntry(DateTime.UtcNow.Ticks, level, message));
        
        if (_settings.WriteToConsole)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [{level}] {message}");
        }
    }
    
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
                if (await reader.WaitToReadAsync(_cts.Token))
                {
                    while (buffer.Count < 100 && reader.TryRead(out var item))
                    {
                        buffer.Add(item);
                    }
                    
                    sb.Clear();
                    foreach (var entry in buffer)
                    {
                        var ts = new DateTime(entry.TimestampTicks, DateTimeKind.Utc);
                        sb.Append('[').Append(ts.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
                        sb.Append('[').Append(LogLevelStrings[(int)entry.Level]).Append("] ");
                        sb.AppendLine(entry.Message);
                    }
                    
                    var logFile = GetLogFilePath();
                    await File.AppendAllTextAsync(logFile, sb.ToString(), _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LOGGER ERROR] {ex.Message}");
            }
        }
        
        // Final drain on shutdown
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
            try { await File.AppendAllTextAsync(GetLogFilePath(), drainSb.ToString()); } catch { }
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetLogFilePath() =>
        Path.Combine(_logDirectory, $"{DateTime.UtcNow:yyyy_MM_dd}.log");
    
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
            // Force stop if it takes too long
        }
        
        _cts.Dispose();
    }
}
