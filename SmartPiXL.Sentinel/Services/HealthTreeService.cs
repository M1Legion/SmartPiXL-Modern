using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Sentinel.Services;

// ============================================================================
// HEALTH TREE SERVICE — Aggregates the full platform health tree.
// ============================================================================
// Loads the tree structure from Health.Node (SQL), polls Edge and Forge health
// endpoints, runs local Sentinel probes, and decorates every node with live
// health state. The decorated tree is cached and refreshed every 10 seconds.
//
// TREE STRUCTURE (from SQL):
//   Platform → System → Subsystem → Component → Probe
//   Probes are leaves with binary health (1/0).
//   Parent nodes aggregate as ratios: healthy/total.
//
// DATA SOURCES:
//   Edge:     GET http://127.0.0.1:6000/internal/health → EdgeHealthReport
//   Forge:    GET http://127.0.0.1:7100/health          → ForgeHealthReport
//   Sentinel: Local probes (SQL, Windows services, IIS, self)
//
// CONSUMERS:
//   GET /api/health-tree → full decorated tree JSON
// ============================================================================

[SupportedOSPlatform("windows")]
public sealed class HealthTreeService : IDisposable
{
    private readonly TrackingSettings _settings;
    private readonly HttpClient _edgeHttp;
    private readonly HttpClient _forgeHttp;
    private readonly ITrackingLogger _logger;

    // Tree structure (loaded once from SQL, refreshed on demand)
    private List<HealthTreeNode>? _treeNodes;
    private DateTime _treeLoadedAt = DateTime.MinValue;
    private static readonly TimeSpan TreeCacheDuration = TimeSpan.FromMinutes(5);

    // Decorated tree cache (refreshed every 10s)
    private HealthTreeResult? _cached;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan ProbeCacheDuration = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public HealthTreeService(
        IOptions<TrackingSettings> settings,
        ITrackingLogger logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _edgeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _forgeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }

    /// <summary>
    /// Returns the full decorated health tree. Cached for 10 seconds.
    /// </summary>
    public async Task<HealthTreeResult> GetTreeAsync()
    {
        if (_cached is not null && DateTime.UtcNow < _cacheExpiry)
            return _cached;

        await _lock.WaitAsync();
        try
        {
            if (_cached is not null && DateTime.UtcNow < _cacheExpiry)
                return _cached;

            var result = await BuildTreeAsync();
            _cached = result;
            _cacheExpiry = DateTime.UtcNow.Add(ProbeCacheDuration);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forces a reload of the tree structure from SQL on next call.</summary>
    public void InvalidateTreeStructure()
    {
        _treeLoadedAt = DateTime.MinValue;
        _cacheExpiry = DateTime.MinValue;
        _cached = null;
    }

    private async Task<HealthTreeResult> BuildTreeAsync()
    {
        var sw = Stopwatch.StartNew();

        // 1. Load tree structure from SQL (cached 5 min)
        var nodes = await GetTreeNodesAsync();

        // 2. Fetch health data from Edge + Forge in parallel, plus local probes
        var edgeTask = FetchEdgeHealthAsync();
        var forgeTask = FetchForgeHealthAsync();
        var sentinelTask = Task.Run(ProbeSentinelLocal);
        await Task.WhenAll(edgeTask, forgeTask, sentinelTask);

        var edgeReport = edgeTask.Result;
        var forgeReport = forgeTask.Result;
        var sentinelProbes = sentinelTask.Result;

        // 3. Build probe lookup dictionaries
        var edgeProbes = new Dictionary<string, ProbeState>();
        if (edgeReport is not null)
        {
            foreach (var p in edgeReport.Probes)
                edgeProbes[p.Name] = new ProbeState { Health = p.Health, Metrics = p.Metrics };
        }

        var forgeProbes = new Dictionary<string, Dictionary<string, ProbeState>>();
        if (forgeReport is not null)
        {
            foreach (var sub in forgeReport.Subsystems)
            {
                var probeMap = new Dictionary<string, ProbeState>();
                foreach (var p in sub.Probes)
                    probeMap[p.Name] = new ProbeState { Health = p.Health, Metrics = p.Metrics };
                forgeProbes[sub.Name] = probeMap;
            }
        }

        // 4. Decorate probes with live health data
        foreach (var node in nodes)
        {
            if (node.NodeType != "probe") continue;

            var meta = node.ParsedMetadata;
            if (meta is null) continue;

            var source = meta.Source;
            if (source is null) continue;

            switch (source)
            {
                case "edge":
                    if (edgeReport is null)
                        node.SetUnreachable();
                    else if (meta.SourceProbeName is not null && edgeProbes.TryGetValue(meta.SourceProbeName, out var ep))
                        node.SetLive(ep.Health, ep.Metrics);
                    else
                        node.SetUnknown();
                    break;

                case "forge":
                    if (forgeReport is null)
                    {
                        node.SetUnreachable();
                    }
                    else
                    {
                        var subsystemName = meta.SourceSubsystem;
                        if (subsystemName is not null && meta.SourceProbeName is not null &&
                            forgeProbes.TryGetValue(subsystemName, out var fpMap) &&
                            fpMap.TryGetValue(meta.SourceProbeName, out var fp))
                        {
                            node.SetLive(fp.Health, fp.Metrics);
                        }
                        else
                        {
                            node.SetUnknown();
                        }
                    }
                    break;

                case "sentinel":
                    if (sentinelProbes.TryGetValue(node.Slug, out var sp))
                        node.SetLive(sp.Health, sp.Metrics);
                    else
                        node.SetUnknown();
                    break;
            }
        }

        // 5. Roll up health ratios from leaves to root
        var byId = nodes.ToDictionary(n => n.NodeId);
        var children = nodes.Where(n => n.ParentId.HasValue)
                            .GroupBy(n => n.ParentId!.Value)
                            .ToDictionary(g => g.Key, g => g.ToList());

        // Post-order traversal to aggregate
        var root = nodes.FirstOrDefault(n => n.ParentId is null);
        if (root is not null)
            AggregateHealth(root, byId, children);

        sw.Stop();

        return new HealthTreeResult
        {
            CheckedAt = DateTime.UtcNow,
            ProbeTimeMs = (int)sw.ElapsedMilliseconds,
            EdgeReachable = edgeReport is not null,
            ForgeReachable = forgeReport is not null,
            Root = root is not null ? BuildOutputNode(root, children) : null
        };
    }

    private static void AggregateHealth(
        HealthTreeNode node,
        Dictionary<int, HealthTreeNode> byId,
        Dictionary<int, List<HealthTreeNode>> children)
    {
        if (!children.TryGetValue(node.NodeId, out var kids) || kids.Count == 0)
        {
            // Leaf probe — already has Health set
            node.Healthy = node.Health ?? 0;
            node.Total = 1;
            return;
        }

        int healthy = 0, total = 0;
        foreach (var child in kids)
        {
            AggregateHealth(child, byId, children);
            healthy += child.Healthy;
            total += child.Total;
        }
        node.Healthy = healthy;
        node.Total = total;
        node.Ratio = total > 0 ? (double)healthy / total : 0;
    }

    private static HealthTreeOutputNode BuildOutputNode(
        HealthTreeNode node,
        Dictionary<int, List<HealthTreeNode>> children)
    {
        var output = new HealthTreeOutputNode
        {
            NodeId = node.NodeId,
            Slug = node.Slug,
            Name = node.Name,
            NodeType = node.NodeType,
            Description = node.Description,
            Health = node.Health,
            Healthy = node.Healthy,
            Total = node.Total,
            Ratio = node.Total > 0 ? (double)node.Healthy / node.Total : null,
            Metrics = node.Metrics,
            Status = node.Status
        };

        if (children.TryGetValue(node.NodeId, out var kids) && kids.Count > 0)
        {
            output.Children = kids
                .OrderBy(k => k.SortOrder)
                .Select(k => BuildOutputNode(k, children))
                .ToList();
        }

        return output;
    }

    // ========================================================================
    // DATA SOURCES
    // ========================================================================

    private async Task<EdgeHealthReportDto?> FetchEdgeHealthAsync()
    {
        try
        {
            var baseUrl = _settings.EdgeBaseUrl ?? "http://127.0.0.1:6000";
            var response = await _edgeHttp.GetAsync($"{baseUrl.TrimEnd('/')}/internal/health");
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<EdgeHealthReportDto>(s_jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task<ForgeHealthReportDto?> FetchForgeHealthAsync()
    {
        try
        {
            var response = await _forgeHttp.GetAsync("http://127.0.0.1:7100/health");
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<ForgeHealthReportDto>(s_jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<string, ProbeState> ProbeSentinelLocal()
    {
        var results = new Dictionary<string, ProbeState>();

        // SQL Connectivity
        try
        {
            using var conn = new SqlConnection(_settings.ConnectionString);
            var sw = Stopwatch.StartNew();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = 5;
            cmd.ExecuteScalar();
            sw.Stop();
            results["sentinel.sql-connectivity"] = new ProbeState
            {
                Health = 1,
                Metrics = new { ResponseMs = sw.ElapsedMilliseconds }
            };
        }
        catch (Exception ex)
        {
            results["sentinel.sql-connectivity"] = new ProbeState
            {
                Health = 0,
                Metrics = new { Error = ex.Message }
            };
        }

        // Windows Services
        var criticalServices = new[]
        {
            "MSSQL$SQL2025", "W3SVC", "SmartPiXL-Forge"
        };
        int running = 0, total = criticalServices.Length;
        foreach (var svc in criticalServices)
        {
            try
            {
                using var sc = new ServiceController(svc);
                if (sc.Status == ServiceControllerStatus.Running) running++;
            }
            catch { /* not found = not running */ }
        }
        results["sentinel.windows-services"] = new ProbeState
        {
            Health = running == total ? 1 : 0,
            Metrics = new { Running = running, Total = total }
        };

        // IIS Reachability
        try
        {
            var baseUrl = _settings.EdgeBaseUrl ?? "http://127.0.0.1:6000";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = http.Send(new HttpRequestMessage(HttpMethod.Head, $"{baseUrl.TrimEnd('/')}/health"));
            results["sentinel.iis-reachability"] = new ProbeState
            {
                Health = (int)response.StatusCode < 400 ? 1 : 0,
                Metrics = new { StatusCode = (int)response.StatusCode }
            };
        }
        catch (Exception ex)
        {
            results["sentinel.iis-reachability"] = new ProbeState
            {
                Health = 0,
                Metrics = new { Error = ex.Message }
            };
        }

        // Self (always healthy if we're running)
        results["sentinel.self"] = new ProbeState
        {
            Health = 1,
            Metrics = new
            {
                UptimeSeconds = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
                WorkingSetMB = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0)
            }
        };

        return results;
    }

    // ========================================================================
    // TREE STRUCTURE LOADER (from SQL)
    // ========================================================================

    private async Task<List<HealthTreeNode>> GetTreeNodesAsync()
    {
        if (_treeNodes is not null && DateTime.UtcNow - _treeLoadedAt < TreeCacheDuration)
            return _treeNodes;

        var nodes = new List<HealthTreeNode>();
        await using var conn = new SqlConnection(_settings.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder, IsActive
            FROM Health.Node
            WHERE IsActive = 1
            ORDER BY ParentId, SortOrder";
        cmd.CommandTimeout = 10;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var metadataJson = reader.IsDBNull(6) ? null : reader.GetString(6);
            nodes.Add(new HealthTreeNode
            {
                NodeId = reader.GetInt32(0),
                ParentId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                Slug = reader.GetString(2),
                Name = reader.GetString(3),
                NodeType = reader.GetString(4),
                Description = reader.IsDBNull(5) ? null : reader.GetString(5),
                MetadataJson = metadataJson,
                SortOrder = reader.GetInt32(7),
                ParsedMetadata = metadataJson is not null
                    ? JsonSerializer.Deserialize<NodeMetadata>(metadataJson, s_jsonOptions)
                    : null
            });
        }

        _treeNodes = nodes;
        _treeLoadedAt = DateTime.UtcNow;
        return nodes;
    }

    public void Dispose()
    {
        _edgeHttp.Dispose();
        _forgeHttp.Dispose();
        _lock.Dispose();
    }
}

// ============================================================================
// INTERNAL DTOs — These are deserialization targets for Edge/Forge health JSON.
// Separate from the Shared models because we only need a subset of fields
// and want case-insensitive deserialization.
// ============================================================================

internal sealed class EdgeHealthReportDto
{
    public ProbeReportDto[] Probes { get; set; } = [];
}

internal sealed class ForgeHealthReportDto
{
    public SubsystemReportDto[] Subsystems { get; set; } = [];
}

internal sealed class SubsystemReportDto
{
    public string Name { get; set; } = "";
    public ProbeReportDto[] Probes { get; set; } = [];
}

internal sealed class ProbeReportDto
{
    public string Name { get; set; } = "";
    public int Health { get; set; }
    public object? Metrics { get; set; }
}

// ============================================================================
// HEALTH TREE DATA TYPES
// ============================================================================

/// <summary>Internal working node loaded from SQL, decorated with live health.</summary>
internal sealed class HealthTreeNode
{
    public int NodeId { get; init; }
    public int? ParentId { get; init; }
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public string NodeType { get; init; } = "";
    public string? Description { get; init; }
    public string? MetadataJson { get; init; }
    public int SortOrder { get; init; }
    public NodeMetadata? ParsedMetadata { get; init; }

    // Live state (set during decoration)
    public int? Health { get; private set; }
    public object? Metrics { get; private set; }
    public string Status { get; private set; } = "pending"; // pending, live, unreachable, unknown

    // Aggregated (set during rollup)
    public int Healthy { get; set; }
    public int Total { get; set; }
    public double? Ratio { get; set; }

    public void SetLive(int health, object? metrics) { Health = health; Metrics = metrics; Status = "live"; }
    public void SetUnreachable() { Health = 0; Metrics = null; Status = "unreachable"; }
    public void SetUnknown() { Health = null; Metrics = null; Status = "unknown"; }
}

/// <summary>Parsed Metadata JSON from Health.Node.</summary>
internal sealed class NodeMetadata
{
    public string? Source { get; set; }
    public string? SourceProbeName { get; set; }
    public string? SourceSubsystem { get; set; }
    public string? HealthFunction { get; set; }
    public string? Icon { get; set; }
}

internal sealed class ProbeState
{
    public int Health { get; set; }
    public object? Metrics { get; set; }
}

// ============================================================================
// OUTPUT DTOs — Serialized to JSON for the /api/health-tree endpoint.
// ============================================================================

/// <summary>Top-level result returned by <c>GET /api/health-tree</c>.</summary>
public sealed class HealthTreeResult
{
    public DateTime CheckedAt { get; set; }
    public int ProbeTimeMs { get; set; }
    public bool EdgeReachable { get; set; }
    public bool ForgeReachable { get; set; }
    public HealthTreeOutputNode? Root { get; set; }
}

/// <summary>A single node in the output tree (recursive children).</summary>
public sealed class HealthTreeOutputNode
{
    public int NodeId { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string NodeType { get; set; } = "";
    public string? Description { get; set; }
    public int? Health { get; set; }
    public int Healthy { get; set; }
    public int Total { get; set; }
    public double? Ratio { get; set; }
    public object? Metrics { get; set; }
    public string Status { get; set; } = "pending";
    public List<HealthTreeOutputNode>? Children { get; set; }
}
