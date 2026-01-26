using TrackingPixel.Configuration;

namespace TrackingPixel.Services;

/// <summary>
/// Lightweight logging abstraction for tracking operations.
/// Avoids allocations when log level is disabled.
/// </summary>
public interface ITrackingLogger
{
    void Trace(string message);
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? ex = null);
    
    bool IsEnabled(TrackingLogLevel level);
}
