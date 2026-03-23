using System.Runtime.InteropServices;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// NUMA HELPER — Pins the Forge process to a specific NUMA node.
//
// WHY:
//   The SmartPiXL server is a 4-socket Intel Xeon Gold 6254 (72 cores / 144 LPs).
//   Each socket is a NUMA node with 18 cores (36 logical processors) and ~500GB RAM.
//   Cross-NUMA memory access adds 1.5-2x latency. By pinning Forge to one NUMA
//   node, all enrichment workers share the same L3 cache and local RAM — zero
//   cross-socket traffic. SQL Server naturally gravitates to the other nodes.
//
// API:
//   Uses GetSystemCpuSetInformation (Win10 2004+) to enumerate CPU set IDs per
//   NUMA node, then SetProcessDefaultCpuSets to pin the entire process. This
//   handles multi-processor-group systems transparently (groups > 64 LPs).
//
// FALLBACK:
//   If NUMA APIs fail (older Windows, VM, container), logs a warning and
//   returns Environment.ProcessorCount. The pipeline runs normally, just
//   without NUMA isolation.
// ============================================================================

internal static class NumaHelper
{
    /// <summary>
    /// Pins the current process to the specified NUMA node and returns the
    /// number of logical processors available on that node.
    /// </summary>
    /// <param name="nodeIndex">NUMA node index (0-based). -1 = no pinning.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="ramPerNodeGB">Estimated RAM in GB per NUMA node (for log output).</param>
    /// <returns>Logical processor count on the pinned node (or total system count if not pinned).</returns>
    public static int PinToNumaNode(int nodeIndex, ITrackingLogger logger, int ramPerNodeGB = 500)
    {
        if (nodeIndex < 0)
        {
            logger.Info($"NUMA: No pinning requested (NumaNode={nodeIndex}). Using all {Environment.ProcessorCount} processors.");
            return Environment.ProcessorCount;
        }

        try
        {
            // Verify NUMA topology
            if (!GetNumaHighestNodeNumber(out var highestNode))
            {
                var err = Marshal.GetLastPInvokeError();
                logger.Warning($"NUMA: GetNumaHighestNodeNumber failed (error {err}). Running without NUMA pinning.");
                return Environment.ProcessorCount;
            }

            if ((uint)nodeIndex > highestNode)
            {
                logger.Warning($"NUMA: Node {nodeIndex} does not exist (highest={highestNode}). Running without NUMA pinning.");
                return Environment.ProcessorCount;
            }

            // Enumerate CPU set IDs for the target NUMA node
            var cpuSetIds = GetCpuSetIdsForNode((byte)nodeIndex);
            if (cpuSetIds.Length == 0)
            {
                logger.Warning($"NUMA: No CPU sets found for node {nodeIndex}. Running without NUMA pinning.");
                return Environment.ProcessorCount;
            }

            // Pin the entire process to those CPU sets
            var handle = GetCurrentProcess();
            if (!SetProcessDefaultCpuSets(handle, cpuSetIds, (uint)cpuSetIds.Length))
            {
                var err = Marshal.GetLastPInvokeError();
                logger.Warning($"NUMA: SetProcessDefaultCpuSets failed (error {err}). Running without NUMA pinning.");
                return cpuSetIds.Length;
            }

            logger.Info($"NUMA: Forge pinned to node {nodeIndex} \u2014 {cpuSetIds.Length} logical processors, ~{ramPerNodeGB}GB local RAM");
            return cpuSetIds.Length;
        }
        catch (Exception ex)
        {
            logger.Warning($"NUMA: Unexpected error during pinning — {ex.Message}. Running without NUMA pinning.");
            return Environment.ProcessorCount;
        }
    }

    /// <summary>
    /// Enumerates CPU set IDs belonging to the specified NUMA node using
    /// GetSystemCpuSetInformation. Handles variable-length struct arrays.
    /// </summary>
    private static uint[] GetCpuSetIdsForNode(byte nodeIndex)
    {
        // First call: get required buffer size (expected to fail with ERROR_INSUFFICIENT_BUFFER)
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out var requiredLength, GetCurrentProcess(), 0);
        if (requiredLength == 0) return [];

        var buffer = Marshal.AllocHGlobal((int)requiredLength);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, requiredLength, out _, GetCurrentProcess(), 0))
                return [];

            var ids = new List<uint>();
            var offset = 0;

            // Walk variable-length struct array using the Size field for navigation.
            // SYSTEM_CPU_SET_INFORMATION layout (offsets):
            //   0: Size (uint)   4: Type (uint)   8: Id (uint)   17: NumaNodeIndex (byte)
            while (offset + 18 <= (int)requiredLength)
            {
                var structSize = Marshal.ReadInt32(buffer + offset);       // Size at offset 0
                var type = Marshal.ReadInt32(buffer + offset + 4);         // Type at offset 4

                if (type == 0) // CpuSet type
                {
                    var id = (uint)Marshal.ReadInt32(buffer + offset + 8); // Id at offset 8
                    var numa = Marshal.ReadByte(buffer + offset + 17);     // NumaNodeIndex at offset 17

                    if (numa == nodeIndex)
                        ids.Add(id);
                }

                if (structSize <= 0) break; // Safety: prevent infinite loop
                offset += structSize;
            }

            return ids.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ── P/Invoke declarations (Win10 2004+ / Server 2022+) ────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNumaHighestNodeNumber(out uint highestNodeNumber);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(
        IntPtr information,
        uint bufferLength,
        out uint returnedLength,
        IntPtr process,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultCpuSets(
        IntPtr process,
        [In] uint[] cpuSetIds,
        uint cpuSetIdCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
