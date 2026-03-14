using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using SmartPiXL.SyntheticTraffic;
using SmartPiXL.SyntheticTraffic.Engine;
using SmartPiXL.SyntheticTraffic.Generation;
using SmartPiXL.SyntheticTraffic.Network;

// ============================================================================
// SmartPiXL Synthetic Traffic Generator
//
// Producer-consumer architecture for high-throughput synthetic traffic:
//
//   Producer (this loop) → Channel<SyntheticHit> → N worker tasks → HTTP
//
// The producer generates sessions on the main thread and writes hits into a
// bounded channel. N workers (default 64) pull hits from the channel and send
// them via HTTP. A token-bucket rate limiter (SemaphoreSlim dripped by the
// AdaptiveRateController's current rate) governs aggregate throughput.
//
// Usage:
//   dotnet run
//   dotnet run -- --TargetCount 100000 --InitialRatePerSecond 200
//   dotnet run -- --TargetUrl http://localhost:7000 --Concurrency 128
//
// All settings in appsettings.json can be overridden via CLI args.
// ============================================================================

Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  SmartPiXL Synthetic Traffic Generator");
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine();

// ── Configuration ──────────────────────────────────────────────────────
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddCommandLine(args)
    .Build();

var settings = new TrafficSettings();
config.GetSection("SyntheticTraffic").Bind(settings);

// CLI overrides (flat keys for convenience)
if (config["TargetCount"] is { } tc) settings.TargetCount = int.Parse(tc);
if (config["InitialRatePerSecond"] is { } ir) settings.InitialRatePerSecond = int.Parse(ir);
if (config["InitialRate"] is { } ir2) settings.InitialRatePerSecond = int.Parse(ir2);
if (config["MaxRatePerSecond"] is { } mr) settings.MaxRatePerSecond = int.Parse(mr);
if (config["MaxRate"] is { } mr2) settings.MaxRatePerSecond = int.Parse(mr2);
if (config["TargetUrl"] is { } tu) settings.TargetUrl = tu;
if (config["Concurrency"] is { } cc) settings.Concurrency = int.Parse(cc);
if (config["AdditiveIncrease"] is { } ai) settings.AdditiveIncrease = int.Parse(ai);

Console.WriteLine($"  Target:       {settings.TargetUrl}");
Console.WriteLine($"  Count:        {(settings.TargetCount == 0 ? "unlimited (Ctrl+C to stop)" : settings.TargetCount.ToString("N0"))}");
Console.WriteLine($"  Initial rate: {settings.InitialRatePerSecond}/s");
Console.WriteLine($"  Max rate:     {settings.MaxRatePerSecond}/s");
Console.WriteLine($"  Concurrency:  {settings.Concurrency} workers");
Console.WriteLine($"  Additive Δ:   +{settings.AdditiveIncrease}/s");
Console.WriteLine($"  Companies:    {string.Join(", ", settings.CompanyIds.Distinct())}");
Console.WriteLine();

// ── Load IP ranges from RIR data ───────────────────────────────────────
Console.Write("  Loading IP ranges from RIR delegation files...");
var rirPath = ResolveRirPath(settings.RirDataDirectory);

static string ResolveRirPath(string configured)
{
    // If already absolute and exists, use it
    if (Path.IsPathRooted(configured) && Directory.Exists(configured))
        return configured;

    // Try relative to bin output (e.g. bin/Debug/net10.0/../Research/data)
    var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
    if (Directory.Exists(candidate)) return candidate;

    // Try relative to CWD (works when cd'd into project folder)
    candidate = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configured));
    if (Directory.Exists(candidate)) return candidate;

    // Walk up from CWD looking for Research/data (handles workspace root or any subfolder)
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        candidate = Path.Combine(dir.FullName, "Research", "data");
        if (Directory.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }

    // Last resort: return CWD-relative (will fail with clear error)
    return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configured));
}
var ipGen = IpGenerator.Load(rirPath);
Console.WriteLine($" {ipGen.RangeCount:N0} ranges, {ipGen.TotalHosts:N0} hosts");

// ── Capture baseline metrics ───────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("  ── Baseline Metrics ──");
using var rateController = new AdaptiveRateController(settings);
var baseline = await rateController.CaptureBaseline();
if (baseline is not null)
{
    Console.WriteLine($"  Edge status:     {baseline.Status}");
    Console.WriteLine($"  Pipe connected:  {baseline.PipeConnected}");
    Console.WriteLine($"  Queue depth:     {baseline.QueueDepth}");
    Console.WriteLine($"  Queue status:    {baseline.QueueStatus}");
}
else
{
    Console.WriteLine("  [WARN] Could not reach Edge health endpoint.");
    Console.WriteLine("         Traffic will be sent anyway (fire-and-forget).");
}

// SQL baseline: count existing rows
long baselineRaw = 0, baselineParsed = 0;
try
{
    await using var conn = new SqlConnection(settings.ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT
            (SELECT COUNT(*) FROM PiXL.Raw WHERE CompanyID IN ('99901','99902','99903','99904','99905')) AS RawCount,
            (SELECT COUNT(*) FROM PiXL.Parsed WHERE IsSynthetic = 1) AS ParsedCount
        """;
    await using var reader = await cmd.ExecuteReaderAsync();
    if (await reader.ReadAsync())
    {
        baselineRaw = reader.GetInt32(0);
        baselineParsed = reader.GetInt32(1);
        Console.WriteLine($"  PiXL.Raw (synthetic):    {baselineRaw:N0}");
        Console.WriteLine($"  PiXL.Parsed (synthetic): {baselineParsed:N0}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  [WARN] SQL baseline failed: {ex.Message}");
}

// ── Start generation ───────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("  ── Starting Traffic Generation ──");
Console.WriteLine("  Press Ctrl+C to stop gracefully.");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\n  [INFO] Shutting down gracefully...");
};

var rng = new Random(); // Non-deterministic for production runs
var sessionSim = new SessionSimulator(settings, ipGen, rng);
using var writer = new HttpTrafficWriter(settings, rateController);

// Wire failure-aware feedback: controller reads actual throughput from writer
rateController.AttachThroughputProbe(() => (writer.TotalSuccess, writer.TotalFailed));

// Start adaptive rate probing and HTTP worker pool
rateController.StartProbing();
writer.Start();

var totalTarget = settings.TargetCount;
var statusInterval = TimeSpan.FromSeconds(5);
var lastStatus = Stopwatch.GetTimestamp();
long totalQueued = 0;

try
{
    // ── PRODUCER LOOP ──────────────────────────────────────────────────
    // Generate sessions and push hits into the channel.
    // Workers pull from the other side and send HTTP requests in parallel.
    while (!cts.Token.IsCancellationRequested)
    {
        // Check if we've queued enough
        if (totalTarget > 0 && totalQueued >= totalTarget)
            break;

        // Generate a session
        var session = sessionSim.GenerateSession();

        // Trim if remaining is less than session size
        if (totalTarget > 0)
        {
            var remaining = (int)(totalTarget - totalQueued);
            if (remaining <= 0) break;
            if (session.Length > remaining)
                session = session[..remaining];
        }

        // Push the session into the channel (backpressures if channel full)
        await writer.EnqueueSession(session, cts.Token);
        totalQueued += session.Length;

        // Periodic status report
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(lastStatus, now) >= statusInterval)
        {
            lastStatus = now;
            PrintStatus(writer, rateController, totalTarget);
        }
    }
}
catch (OperationCanceledException)
{
    // Expected on Ctrl+C
}

// ── Signal producer complete and drain remaining hits ──────────────────
Console.WriteLine("  [INFO] Producer complete. Draining remaining queue...");
if (cts.IsCancellationRequested)
{
    await writer.ForceStop();
}
else
{
    writer.CompleteProducer();
    await writer.DrainAndStop();
}

await rateController.StopProbing();

// ── Final Report ───────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════");
Console.WriteLine("  ── Final Report ──");
Console.WriteLine($"  Duration:      {writer.Elapsed.TotalSeconds:F1}s");
Console.WriteLine($"  Queued:        {writer.TotalQueued:N0}");
Console.WriteLine($"  Sent:          {writer.TotalSent:N0}");
Console.WriteLine($"  Successful:    {writer.TotalSuccess:N0}");
Console.WriteLine($"  Failed:        {writer.TotalFailed:N0}");
Console.WriteLine($"  Avg rate:      {writer.HitsPerSecond:F1}/s");
Console.WriteLine($"  Peak rate:     {rateController.PeakRate}/s");
Console.WriteLine($"  Back-offs:     {rateController.BackOffCount}");
Console.WriteLine($"  Final queue:   {rateController.LastQueueDepth}");
Console.WriteLine($"  Concurrency:   {settings.Concurrency} workers");
Console.WriteLine("═══════════════════════════════════════════════════════");

// Final SQL counts with delta
try
{
    // Brief pause to let last batch flush
    await Task.Delay(2000);

    await using var conn = new SqlConnection(settings.ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT
            (SELECT COUNT(*) FROM PiXL.Raw WHERE CompanyID IN ('99901','99902','99903','99904','99905')) AS RawCount,
            (SELECT COUNT(*) FROM PiXL.Parsed WHERE IsSynthetic = 1) AS ParsedCount
        """;
    await using var reader = await cmd.ExecuteReaderAsync();
    if (await reader.ReadAsync())
    {
        var finalRaw = reader.GetInt32(0);
        var finalParsed = reader.GetInt32(1);
        Console.WriteLine($"  PiXL.Raw (final):    {finalRaw:N0}  (+{finalRaw - baselineRaw:N0})");
        Console.WriteLine($"  PiXL.Parsed (final): {finalParsed:N0}  (+{finalParsed - baselineParsed:N0})");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  [WARN] Final SQL count failed: {ex.Message}");
}

Console.WriteLine();

// ════════════════════════════════════════════════════════════════════════
// HELPERS
// ════════════════════════════════════════════════════════════════════════

static void PrintStatus(HttpTrafficWriter w, AdaptiveRateController rc, int target)
{
    var pct = target > 0 ? (w.TotalSuccess * 100.0 / target).ToString("F1") + "%" : "∞";
    var eta = target > 0 && w.HitsPerSecond > 0
        ? TimeSpan.FromSeconds((target - w.TotalSuccess) / w.HitsPerSecond)
        : TimeSpan.Zero;
    var etaStr = eta > TimeSpan.Zero ? $" ETA={eta:hh\\:mm\\:ss}" : "";
    var elapsedFmt = w.Elapsed.TotalHours >= 1
        ? $"{(int)w.Elapsed.TotalHours}:{w.Elapsed:mm\\:ss}"
        : $"{w.Elapsed:mm\\:ss}";
    Console.WriteLine(
        $"  [{elapsedFmt}] Sent={w.TotalSuccess:N0}/{target:N0} ({pct}) " +
        $"Rate={rc.CurrentRate}/s Actual={w.HitsPerSecond:F0}/s " +
        $"Queue={rc.LastQueueDepth} Failed={w.TotalFailed}{etaStr}");
}
