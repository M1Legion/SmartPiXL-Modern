namespace SmartPiXL.SyntheticTraffic;

/// <summary>
/// Configuration for the synthetic traffic generator.
/// Bound from <c>appsettings.json</c> section <c>SyntheticTraffic</c>.
/// All rate/count settings are overridable via CLI args.
/// </summary>
public sealed class TrafficSettings
{
    /// <summary>Edge URL to send hits to (e.g., http://192.168.88.176).</summary>
    public string TargetUrl { get; set; } = "http://192.168.88.176";

    /// <summary>Starting hit rate (hits/sec). Adaptive controller ramps from here.</summary>
    public int InitialRatePerSecond { get; set; } = 100;

    /// <summary>Hard ceiling for adaptive rate (hits/sec). Controller never exceeds this.</summary>
    public int MaxRatePerSecond { get; set; } = 300;

    /// <summary>Total hits to generate. 0 = run indefinitely until Ctrl+C.</summary>
    public int TargetCount { get; set; } = 5_000_000;

    /// <summary>Number of concurrent HTTP sender tasks. More = higher throughput.</summary>
    public int Concurrency { get; set; } = 8;

    /// <summary>Additive increase per probe interval (hits/sec) during congestion avoidance.</summary>
    public int AdditiveIncrease { get; set; } = 50;

    /// <summary>How often (seconds) the adaptive controller probes the health endpoint.</summary>
    public int ProbeIntervalSeconds { get; set; } = 3;

    /// <summary>
    /// Pipe queue depth threshold for back-off. When the Edge health endpoint
    /// reports a queue depth above this, the controller halves the rate.
    /// </summary>
    public int QueueDepthCeiling { get; set; } = 2000;

    /// <summary>
    /// Pipe queue depth threshold for ramp-up. When queue depth is below this,
    /// the controller increases the rate.
    /// </summary>
    public int QueueDepthFloor { get; set; } = 100;

    /// <summary>Synthetic company IDs (99901-99905 reserved for synthetic data).</summary>
    public int[] CompanyIds { get; set; } = [99901, 99902, 99903, 99904, 99905];

    /// <summary>Pixel IDs to rotate through.</summary>
    public int[] PixlIds { get; set; } = [1, 2, 3, 4, 5];

    /// <summary>SQL connection string for baseline/final metric queries.</summary>
    public string ConnectionString { get; set; } =
        "Server=localhost\\SQL2025;Database=SmartPiXL;Integrated Security=True;TrustServerCertificate=True";

    /// <summary>Path to Research/data/ directory containing RIR delegation files.</summary>
    public string RirDataDirectory { get; set; } = "..\\Research\\data";
}
