using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using HttpClient = System.Net.Http.HttpClient;
using HttpClientHandler = System.Net.Http.HttpClientHandler;

namespace TronDashboard;

/// <summary>
/// ApiClient — Fetches live data from the SmartPiXL dashboard API endpoints.
/// Runs on a 10-second timer matching the Tron HTML dashboard refresh cycle.
/// All responses are deserialized into Dictionary&lt;string, object?&gt; for
/// flexible consumption by HUD renderers.
/// </summary>
public partial class ApiClient : Node
{
    // ══════════════════════════════════════════════════════════════════════
    //  CONFIG
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Default base URL — IIS production on ASGARD.</summary>
    private const string DefaultBaseUrl = "https://smartpixl.info";

    /// <summary>Dev base URL — dotnet run instance.</summary>
    private const string DevBaseUrl = "https://localhost:7001";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── State ────────────────────────────────────────────────────────────
    private HttpClient _http = null!;
    private double _refreshTimer;
    private const double RefreshInterval = 10.0;
    private bool _fetching;

    // ── Latest data (accessed by HUD) ────────────────────────────────────
    public Dictionary<string, object?>? Health { get; private set; }
    public List<Dictionary<string, object?>>? Hourly { get; private set; }
    public List<Dictionary<string, object?>>? Bots { get; private set; }
    public List<Dictionary<string, object?>>? BotSignals { get; private set; }
    public List<Dictionary<string, object?>>? Devices { get; private set; }
    public Dictionary<string, object?>? Evasion { get; private set; }
    public List<Dictionary<string, object?>>? Behavior { get; private set; }
    public List<Dictionary<string, object?>>? Recent { get; private set; }
    public List<Dictionary<string, object?>>? Fingerprints { get; private set; }
    public Dictionary<string, object?>? Infra { get; private set; }

    /// <summary>True after first successful fetch.</summary>
    public bool HasData { get; private set; }

    /// <summary>Last error message (null = no error).</summary>
    public string? LastError { get; private set; }

    // ══════════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ══════════════════════════════════════════════════════════════════════

    public override void _Ready()
    {
        // Use dev URL if running in editor, production if exported
        string baseUrl = OS.HasFeature("editor") ? DevBaseUrl : DefaultBaseUrl;

        // Allow override via command-line: --api-url=https://...
        foreach (var arg in OS.GetCmdlineArgs())
        {
            if (arg.StartsWith("--api-url="))
            {
                baseUrl = arg["--api-url=".Length..];
                break;
            }
        }

        var handler = new HttpClientHandler
        {
            // Accept self-signed certs for dev
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(8),
        };

        GD.Print($"[ApiClient] Base URL: {baseUrl}");

        // Fire initial fetch immediately
        _ = FetchAllAsync();
    }

    public override void _Process(double delta)
    {
        _refreshTimer += delta;
        if (_refreshTimer >= RefreshInterval && !_fetching)
        {
            _refreshTimer = 0;
            _ = FetchAllAsync();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  DATA FETCHING
    // ══════════════════════════════════════════════════════════════════════

    private async Task FetchAllAsync()
    {
        _fetching = true;
        try
        {
            // Fetch in parallel — all endpoints are independent
            var healthTask    = FetchSingleAsync("/api/dash/health");
            var hourlyTask    = FetchListAsync("/api/dash/hourly?hours=48");
            var botsTask      = FetchListAsync("/api/dash/bots");
            var signalsTask   = FetchListAsync("/api/dash/bot-signals");
            var devicesTask   = FetchListAsync("/api/dash/devices");
            var evasionTask   = FetchSingleAsync("/api/dash/evasion");
            var behaviorTask  = FetchListAsync("/api/dash/behavior");
            var recentTask    = FetchListAsync("/api/dash/recent");
            var fpTask        = FetchListAsync("/api/dash/fingerprints?limit=12");
            var infraTask     = FetchSingleAsync("/api/dash/infra");

            await Task.WhenAll(
                healthTask, hourlyTask, botsTask, signalsTask, devicesTask,
                evasionTask, behaviorTask, recentTask, fpTask, infraTask);

            Health       = healthTask.Result;
            Hourly       = hourlyTask.Result;
            Bots         = botsTask.Result;
            BotSignals   = signalsTask.Result;
            Devices      = devicesTask.Result;
            Evasion      = evasionTask.Result;
            Behavior     = behaviorTask.Result;
            Recent       = recentTask.Result;
            Fingerprints = fpTask.Result;
            Infra        = infraTask.Result;

            HasData = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            GD.PrintErr($"[ApiClient] Fetch error: {ex.Message}");
        }
        finally
        {
            _fetching = false;
        }
    }

    private async Task<Dictionary<string, object?>?> FetchSingleAsync(string path)
    {
        try
        {
            var json = await _http.GetStringAsync(path);
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ApiClient] {path}: {ex.Message}");
            return null;
        }
    }

    private async Task<List<Dictionary<string, object?>>?> FetchListAsync(string path)
    {
        try
        {
            var json = await _http.GetStringAsync(path);
            return JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ApiClient] {path}: {ex.Message}");
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  CLEANUP
    // ══════════════════════════════════════════════════════════════════════

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _http?.Dispose();
        base.Dispose(disposing);
    }
}
