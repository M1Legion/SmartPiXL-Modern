using System.Net;
using System.Runtime.CompilerServices;

namespace SmartPiXL.SyntheticTraffic.Network;

// ============================================================================
// IP GENERATOR — Produces diverse IPv4 addresses from real RIR allocations.
//
// Parses ARIN/RIPE/APNIC/LACNIC/AFRINIC delegation files to extract allocated
// IPv4 CIDR ranges, then generates random IPs within those ranges. This ensures
// every synthetic IP falls within a real allocated block, producing realistic
// geolocation, ASN, and ISP enrichment when the Forge processes the traffic.
//
// Distribution by country weighting (approximate real traffic):
//   US ~55%, GB ~8%, CA ~7%, DE ~5%, FR ~4%, AU ~3%, Other ~18%
// ============================================================================

internal sealed class IpGenerator
{
    // Pre-parsed IP ranges: (baseIpAsUInt32, hostCount)
    private readonly (uint BaseIp, int HostCount)[] _ranges;
    private readonly int[] _cumulativeHosts;
    private readonly long _totalHosts;

    /// <summary>Country weights for traffic distribution. Sums to 100.</summary>
    private static readonly (string CountryCode, int Weight)[] CountryWeights =
    [
        ("US", 55), ("GB", 8), ("CA", 7), ("DE", 5), ("FR", 4),
        ("AU", 3), ("JP", 3), ("NL", 2), ("BR", 2), ("IN", 2),
        ("IT", 2), ("ES", 2), ("SE", 1), ("KR", 1), ("MX", 1),
        ("ZA", 1), ("SG", 1),
    ];

    private IpGenerator((uint BaseIp, int HostCount)[] ranges)
    {
        _ranges = ranges;

        // Build cumulative host count array for weighted random selection.
        // Larger blocks are proportionally more likely to be selected, which
        // mirrors real allocation density.
        _cumulativeHosts = new int[ranges.Length];
        long sum = 0;
        for (var i = 0; i < ranges.Length; i++)
        {
            sum += ranges[i].HostCount;
            // Clamp to int — fine since we binary search within int range
            _cumulativeHosts[i] = (int)Math.Min(sum, int.MaxValue);
        }
        _totalHosts = sum;
    }

    /// <summary>
    /// Load IP ranges from all RIR delegation files in the specified directory.
    /// Filters to allocated/assigned IPv4 ranges from weighted countries.
    /// </summary>
    public static IpGenerator Load(string rirDataDirectory)
    {
        var targetCountries = new HashSet<string>(
            CountryWeights.Select(w => w.CountryCode),
            StringComparer.OrdinalIgnoreCase);

        var allRanges = new List<(uint BaseIp, int HostCount)>(32_000);

        // Delegation files: delegated-arin.txt, delegated-ripencc.txt, etc.
        var files = Directory.GetFiles(rirDataDirectory, "delegated-*");
        foreach (var file in files)
        {
            using var reader = new StreamReader(file);
            while (reader.ReadLine() is { } line)
            {
                // Skip comments, headers, summary lines
                if (line.Length == 0 || line[0] == '#') continue;

                // Format: registry|cc|type|start|value|date|status[|hash]
                // We want: ipv4 lines with status allocated or assigned
                var span = line.AsSpan();

                // Field 1: skip registry (arin, ripencc, etc.)
                var pipe1 = span.IndexOf('|');
                if (pipe1 < 0) continue;
                span = span[(pipe1 + 1)..];

                // Field 2: country code
                var pipe2 = span.IndexOf('|');
                if (pipe2 < 0 || pipe2 > 3) continue; // cc is 2 chars or * for summary
                var cc = span[..pipe2];
                span = span[(pipe2 + 1)..];

                // Fast reject: skip non-target countries
                if (cc.Length != 2) continue;
                if (!targetCountries.Contains(new string(cc))) continue;

                // Field 3: type (must be "ipv4")
                var pipe3 = span.IndexOf('|');
                if (pipe3 < 0) continue;
                if (!span[..pipe3].SequenceEqual("ipv4")) continue;
                span = span[(pipe3 + 1)..];

                // Field 4: start IP
                var pipe4 = span.IndexOf('|');
                if (pipe4 < 0) continue;
                var ipStr = span[..pipe4];
                span = span[(pipe4 + 1)..];

                // Field 5: host count
                var pipe5 = span.IndexOf('|');
                if (pipe5 < 0) continue;
                var countStr = span[..pipe5];
                span = span[(pipe5 + 1)..];

                // Field 6: date (skip)
                var pipe6 = span.IndexOf('|');
                if (pipe6 < 0) continue;
                span = span[(pipe6 + 1)..];

                // Field 7: status (must be allocated or assigned)
                var pipe7 = span.IndexOf('|');
                var status = pipe7 >= 0 ? span[..pipe7] : span;
                if (!status.SequenceEqual("allocated") && !status.SequenceEqual("assigned"))
                    continue;

                // Parse IP and host count
                if (!IPAddress.TryParse(ipStr, out var ip)) continue;
                if (!int.TryParse(countStr, out var hostCount) || hostCount <= 0) continue;

                var bytes = ip.GetAddressBytes();
                var baseIp = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);

                // Skip private/reserved ranges
                if (IsPrivateOrReserved(baseIp)) continue;

                allRanges.Add((baseIp, hostCount));
            }
        }

        if (allRanges.Count == 0)
            throw new InvalidOperationException(
                $"No IP ranges loaded from RIR files in '{rirDataDirectory}'. " +
                "Ensure delegated-*.txt files exist.");

        // Sort by base IP for deterministic ordering
        allRanges.Sort((a, b) => a.BaseIp.CompareTo(b.BaseIp));

        return new IpGenerator([.. allRanges]);
    }

    /// <summary>Generate a random IP from the loaded allocation pool.</summary>
    public string Next(Random rng)
    {
        // Pick a random range weighted by host count
        var roll = rng.Next(0, _cumulativeHosts[^1]);
        var idx = Array.BinarySearch(_cumulativeHosts, roll);
        if (idx < 0) idx = ~idx;

        var (baseIp, hostCount) = _ranges[idx];

        // Pick a random offset within the range, avoiding .0 (network) and .255 (broadcast)
        var offset = (uint)rng.Next(1, Math.Max(2, hostCount - 1));
        var ip = baseIp + offset;

        return $"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";
    }

    /// <summary>Number of IP ranges loaded.</summary>
    public int RangeCount => _ranges.Length;

    /// <summary>Total host addresses available.</summary>
    public long TotalHosts => _totalHosts;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPrivateOrReserved(uint ip) =>
        (ip >> 24) == 10 ||                          // 10.0.0.0/8
        (ip >> 20) == 0xAC1 ||                       // 172.16.0.0/12
        (ip >> 16) == 0xC0A8 ||                      // 192.168.0.0/16
        (ip >> 24) == 127 ||                          // 127.0.0.0/8
        (ip >> 24) == 0 ||                            // 0.0.0.0/8
        (ip >> 24) >= 224;                            // 224.0.0.0/4+ (multicast/reserved)
}
