namespace SmartPiXL.Services;

// ============================================================================
// HEALTH REPORT MODELS — Shared types for the hierarchical health tree.
//
// Three levels of aggregation, each with the same (Healthy, Total, Ratio) shape:
//
//   ForgeHealthReport (system)
//     └── SubsystemReport[] (F1-F7 subtrees)
//           └── ProbeReport[] (leaf probes, 1/0 health)
//
// ProbeReport is defined in IEdgeHealthClient.cs (shared by Edge + Forge).
// SubsystemReport and ForgeHealthReport are Forge-specific but live in Shared
// so Sentinel can deserialize them when querying Forge health.
// ============================================================================

/// <summary>
/// Health report for a subtree of probes (e.g. F1: Ingest, F2: Enrichment Engine).
/// Aggregates child <see cref="ProbeReport"/> health into (Healthy, Total, Ratio).
/// </summary>
public sealed class SubsystemReport
{
    public required string Name { get; init; }
    public int Healthy { get; init; }
    public int Total { get; init; }
    public double Ratio { get; init; }
    public ProbeReport[] Probes { get; init; } = [];

    /// <summary>Creates a SubsystemReport from an array of probes, computing aggregates.</summary>
    public static SubsystemReport From(string name, ProbeReport[] probes)
    {
        var healthy = 0;
        foreach (var p in probes) healthy += p.Health;
        return new SubsystemReport
        {
            Name = name,
            Healthy = healthy,
            Total = probes.Length,
            Ratio = probes.Length > 0 ? (double)healthy / probes.Length : 0,
            Probes = probes
        };
    }
}

/// <summary>
/// Full Forge health report: system-level ratio + per-subsystem detail.
/// Built by <c>ForgeMetrics.GetHealthReport()</c>.
/// </summary>
public sealed class ForgeHealthReport
{
    public string System { get; init; } = "Forge";
    public int Healthy { get; init; }
    public int Total { get; init; }
    public double Ratio { get; init; }
    public double UptimeSeconds { get; init; }
    public SubsystemReport[] Subsystems { get; init; } = [];

    /// <summary>Creates a ForgeHealthReport from subsystem reports, computing system-level aggregates.</summary>
    public static ForgeHealthReport From(double uptimeSeconds, SubsystemReport[] subsystems)
    {
        int healthy = 0, total = 0;
        foreach (var s in subsystems)
        {
            healthy += s.Healthy;
            total += s.Total;
        }
        return new ForgeHealthReport
        {
            Healthy = healthy,
            Total = total,
            Ratio = total > 0 ? (double)healthy / total : 0,
            UptimeSeconds = uptimeSeconds,
            Subsystems = subsystems
        };
    }
}
