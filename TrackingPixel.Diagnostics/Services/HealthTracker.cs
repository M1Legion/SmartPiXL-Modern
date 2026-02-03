using System.Collections.Concurrent;
using TrackingPixel.Diagnostics.Models;

namespace TrackingPixel.Diagnostics.Services;

public class HealthTracker
{
    private readonly DateTime _startTime = DateTime.UtcNow;
    private long _totalRequests;
    private DateTime _lastRequest = DateTime.UtcNow;
    private readonly ConcurrentQueue<DateTime> _recentRequests = new();

    public void RecordRequest()
    {
        Interlocked.Increment(ref _totalRequests);
        _lastRequest = DateTime.UtcNow;
        _recentRequests.Enqueue(DateTime.UtcNow);
        
        // Keep only last minute of requests
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        while (_recentRequests.TryPeek(out var oldest) && oldest < cutoff)
        {
            _recentRequests.TryDequeue(out _);
        }
    }

    public HealthStatus GetStatus()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        var recentCount = _recentRequests.Count(r => r >= cutoff);
        
        return new HealthStatus
        {
            StartTime = _startTime,
            Uptime = DateTime.UtcNow - _startTime,
            TotalRequests = _totalRequests,
            RequestsPerMinute = recentCount,
            LastRequest = _lastRequest
        };
    }
}
