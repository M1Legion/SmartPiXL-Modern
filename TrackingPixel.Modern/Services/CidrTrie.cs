using System.Net;
using System.Runtime.CompilerServices;

namespace TrackingPixel.Services;

// ============================================================================
// CIDR TRIE — Binary prefix tree for O(32) IPv4 / O(128) IPv6 CIDR lookups.
//
// ARCHITECTURE:
//   Each bit of an IP address corresponds to a level in the trie.
//   A CIDR like 10.0.0.0/8 sets a provider string at depth 8 of the path
//   that starts with 00001010 (binary for 10). Any IP that starts with
//   those same 8 bits matches.
//
// PERFORMANCE:
//   Linear scan of 8,500 CIDRs: O(N) = 8,500 iterations per lookup
//   Trie lookup: O(prefix length) = max 32 iterations for IPv4, 128 for IPv6
//   That's a 265× worst-case improvement for IPv4.
//
// MEMORY:
//   Each TrieNode is 2 references (Children[0], Children[1]) + 1 string ref.
//   Typical AWS+GCP dataset creates ~25,000 nodes = ~600 KB. Negligible.
//
// THREAD SAFETY:
//   The trie is immutable after construction. The reference is swapped
//   atomically via volatile write in DatacenterIpService, same as before.
//   Readers get a consistent snapshot — no locks needed.
//
// CONSTRUCTION:
//   Built by CidrTrie.Build() which parses each CIDR string once, walks
//   the trie bit-by-bit, and marks the terminal node with the provider.
//   If a shorter prefix already covers a longer one (e.g., 10.0.0.0/8
//   covers 10.1.0.0/16), the shorter prefix wins (matched first on lookup).
// ============================================================================

/// <summary>
/// Immutable binary prefix trie for fast CIDR range lookups.
/// <para>
/// Replaces the previous linear scan of ~8,500 CIDR strings with O(32)/O(128)
/// bit-level traversal. Built once during <see cref="DatacenterIpService"/>
/// refresh, then atomically swapped via volatile reference.
/// </para>
/// </summary>
public sealed class CidrTrie
{
    /// <summary>
    /// Root node of the trie. IPv4 addresses start with 4-byte paths (32 levels deep),
    /// IPv6 with 16-byte paths (128 levels deep). Both share the same root —
    /// the first bits naturally separate the address families.
    /// </summary>
    /// <remarks>
    /// We use separate roots for IPv4 and IPv6 to avoid wasting depth on
    /// IPv4-mapped IPv6 addresses and to keep the common case (IPv4) shallow.
    /// </remarks>
    private readonly TrieNode _rootV4 = new();
    private readonly TrieNode _rootV6 = new();

    /// <summary>
    /// Internal trie node. Each node has two children (bit 0 and bit 1) and an
    /// optional provider string. When <see cref="Provider"/> is non-null, this
    /// node marks the end of a CIDR prefix — any IP that reaches this node matches.
    /// </summary>
    private sealed class TrieNode
    {
        /// <summary>Children indexed by bit value: [0] = left (bit 0), [1] = right (bit 1).</summary>
        public TrieNode?[] Children { get; } = new TrieNode?[2];

        /// <summary>
        /// Non-null when this node marks the end of a CIDR prefix.
        /// Contains the provider name ("AWS" or "GCP").
        /// Null for intermediate nodes that are just path segments.
        /// </summary>
        public string? Provider { get; set; }
    }

    /// <summary>
    /// Builds a new <see cref="CidrTrie"/> from a list of (CIDR, Provider) entries.
    /// <para>
    /// Parses each CIDR string once during construction. Invalid CIDRs are silently
    /// skipped (logged by the caller). Construction is O(N * prefix_length) where
    /// N is the number of CIDRs — fast enough for ~8,500 entries.
    /// </para>
    /// </summary>
    /// <param name="ranges">Pre-parsed CIDR strings with their provider names.</param>
    /// <returns>A fully populated, immutable trie ready for lookups.</returns>
    public static CidrTrie Build(ReadOnlySpan<(string Cidr, string Provider)> ranges)
    {
        var trie = new CidrTrie();

        // Hoisted outside the loop to avoid stackalloc-per-iteration
        Span<byte> netBytes = stackalloc byte[16];

        foreach (var (cidr, provider) in ranges)
        {
            var span = cidr.AsSpan();
            var slashIdx = span.IndexOf('/');
            if (slashIdx < 0) continue;

            if (!IPAddress.TryParse(span[..slashIdx], out var network)) continue;
            if (!int.TryParse(span[(slashIdx + 1)..], out var prefixLen)) continue;

            // Get the raw bytes of the network address
            if (!network.TryWriteBytes(netBytes, out var netLen)) continue;

            // Select the correct root based on address family
            var root = netLen == 4 ? trie._rootV4 : trie._rootV6;

            // Walk the trie bit-by-bit, creating nodes as needed
            var node = root;
            for (var bit = 0; bit < prefixLen && bit < netLen * 8; bit++)
            {
                // Extract bit at position 'bit' from the address bytes
                // byteIdx = bit / 8, bitIdx = 7 - (bit % 8) for MSB-first order
                var byteIdx = bit >> 3;
                var bitIdx = 7 - (bit & 7);
                var bitVal = (netBytes[byteIdx] >> bitIdx) & 1;

                node.Children[bitVal] ??= new TrieNode();
                node = node.Children[bitVal]!;
            }

            // Mark this node as a terminal — any IP reaching here is in this CIDR.
            // First writer wins: if a shorter prefix already covers this range,
            // the shorter prefix's terminal node was already set at a higher level.
            // But we DO want the most specific match, so overwrite is fine.
            // (In practice, AWS/GCP ranges don't overlap within a single provider.)
            node.Provider ??= provider;
        }

        return trie;
    }

    /// <summary>
    /// Looks up an <see cref="IPAddress"/> in the trie.
    /// <para>
    /// Walks the trie bit-by-bit. The first terminal node (non-null Provider)
    /// encountered is the most general matching CIDR — returned immediately.
    /// </para>
    /// <para>
    /// ALLOCATION-FREE: Uses <c>stackalloc byte[16]</c> for the IP bytes.
    /// The only allocation is the <see cref="DatacenterCheckResult"/> return value,
    /// which is a <c>readonly record struct</c> (stack-allocated).
    /// </para>
    /// </summary>
    /// <param name="ip">The parsed <see cref="IPAddress"/> to look up.</param>
    /// <returns>
    /// <c>default</c> (IsDatacenter=false) if not in any range,
    /// or a result with the provider name.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DatacenterCheckResult Lookup(IPAddress ip)
    {
        Span<byte> ipBytes = stackalloc byte[16];
        if (!ip.TryWriteBytes(ipBytes, out var ipLen))
            return default;

        var node = ipLen == 4 ? _rootV4 : _rootV6;
        var totalBits = ipLen << 3; // ipLen * 8

        for (var bit = 0; bit < totalBits; bit++)
        {
            // Check if we've hit a terminal node (CIDR match)
            if (node.Provider is not null)
                return new DatacenterCheckResult(true, node.Provider);

            // Extract bit at position 'bit'
            var byteIdx = bit >> 3;
            var bitIdx = 7 - (bit & 7);
            var bitVal = (ipBytes[byteIdx] >> bitIdx) & 1;

            var next = node.Children[bitVal];
            if (next is null)
                return default; // No matching prefix in this subtree

            node = next;
        }

        // Check the final node (for /32 or /128 exact matches)
        if (node.Provider is not null)
            return new DatacenterCheckResult(true, node.Provider);

        return default;
    }

    /// <summary>
    /// Empty trie singleton for use before the first refresh completes.
    /// All lookups return <c>default</c> (not a datacenter IP).
    /// </summary>
    public static CidrTrie Empty { get; } = new();
}
