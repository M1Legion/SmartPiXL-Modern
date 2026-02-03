namespace TrackingPixel.Diagnostics.Models;

public record SummaryStats
{
    public int TotalHits { get; init; }
    public int UniqueDevices { get; init; }
    public int UniqueIPs { get; init; }
    public int Last24HourHits { get; init; }
    public int LastHourHits { get; init; }
    public double BotRate { get; init; }
    public double EvasionRate { get; init; }
    public int CrossNetworkDevices { get; init; }
}

public record HourlyStat
{
    public DateTime Hour { get; init; }
    public int Hits { get; init; }
    public int UniqueDevices { get; init; }
}

public record DeviceBreakdown
{
    public string DeviceType { get; init; } = "";
    public string OS { get; init; } = "";
    public string Browser { get; init; } = "";
    public int Count { get; init; }
    public double Percentage { get; init; }
}

public record BotAnalysis
{
    public string RiskBucket { get; init; } = "";
    public int Hits { get; init; }
    public int UniqueDevices { get; init; }
    public double Percentage { get; init; }
}

public record BotIndicator
{
    public string Indicator { get; init; } = "";
    public int Count { get; init; }
}

public record FingerprintMetrics
{
    public int TotalUnique { get; init; }
    public double CollisionRate { get; init; }
    public int CanvasUnique { get; init; }
    public int WebGLUnique { get; init; }
    public int AudioUnique { get; init; }
    public int FontCombinations { get; init; }
    public int ScreenResolutions { get; init; }
}

public record EvasionAttempt
{
    public string DeviceFingerprint { get; init; } = "";
    public int CanvasVariations { get; init; }
    public int WebGLVariations { get; init; }
    public double WebGLBlockedRate { get; init; }
    public string EvasionType { get; init; } = "";
    public DateTime LastSeen { get; init; }
}

public record CrossNetworkDevice
{
    public string DeviceFingerprint { get; init; } = "";
    public string DeviceType { get; init; } = "";
    public int UniqueIPs { get; init; }
    public int TotalHits { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }
    public string IPList { get; init; } = "";
}

public record RecentActivity
{
    public DateTime Timestamp { get; init; }
    public string DeviceProfile { get; init; } = "";
    public string IPAddress { get; init; } = "";
    public string Location { get; init; } = "";
    public int BotRisk { get; init; }
    public string RiskLevel { get; init; } = "";
    public string Fingerprint { get; init; } = "";
}

public record HealthStatus
{
    public DateTime StartTime { get; init; }
    public TimeSpan Uptime { get; init; }
    public long TotalRequests { get; init; }
    public double RequestsPerMinute { get; init; }
    public DateTime LastRequest { get; init; }
}
