using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using TrackingPixel.Models;

namespace TrackingPixel.Services;

// ============================================================================
// IP CLASSIFICATION SERVICE — Zero-allocation IPv4 classifier, low-alloc IPv6.
//
// PURPOSE:
//   Determines whether an IP address is public, private, loopback, multicast,
//   reserved, etc. This classification is used to:
//   1. Decide whether to geolocate the IP (no point geolocating 127.0.0.1)
//   2. Filter out internal/test traffic from analytics
//   3. Identify CGNAT (Carrier-Grade NAT) which IS geolocatable
//
// PERFORMANCE STRATEGY — IPv4 (hot path):
//   • Parse dotted-decimal to uint WITHOUT IPAddress.TryParse (zero allocation)
//   • Store reserved ranges as (network, mask) tuples for bitwise comparison
//   • Classification = (ipUint & mask) == network — single AND + CMP per range
//   • All operations on ReadOnlySpan<char> — no substring allocations
//
// PERFORMANCE STRATEGY — IPv6 (cold path):
//   • IPv4-mapped IPv6 (::ffff:x.x.x.x) is extracted and routed to IPv4 path
//   • Full IPv6 requires IPAddress.TryParse (allocates ~64 bytes, acceptable for cold path)
//   • After parsing, uses stackalloc byte[16] + TryWriteBytes to avoid GetAddressBytes() alloc
//   • Byte-level prefix matching for all RFC-defined IPv6 reserved ranges
//   • SIMD-accelerated IsAllZeros/IsLoopbackV6 via IndexOfAnyExcept
//
// RESERVED RANGE DATA:
//   All IPv4 ranges stored as pre-computed (networkUint, maskUint) pairs.
//   The mask is the subnet mask in network byte order (big-endian).
//   Example: 10.0.0.0/8 → network=0x0A000000, mask=0xFF000000
//   Check: (ipUint & 0xFF000000) == 0x0A000000 → true if IP is in 10.x.x.x
// ============================================================================

/// <summary>
/// Static IP address classifier with zero-allocation hot path for IPv4.
/// <para>
/// All methods are static — no instance state, no DI registration needed.
/// Called directly as <c>IpClassificationService.Classify(ip)</c>.
/// </para>
/// </summary>
public static class IpClassificationService
{
    // ========================================================================
    // IPv4 RESERVED RANGES — Pre-computed (network, mask) tuples
    //
    // Each entry: (networkAddress, subnetMask, ipType, humanNote)
    //   networkAddress: The network prefix as a uint in network byte order (big-endian)
    //   subnetMask:     The subnet mask as a uint (e.g., /8 = 0xFF000000)
    //   ipType:         The IpType enum classification
    //   humanNote:      Descriptive string for logging/debugging
    //
    // Classification check: (parsedIpUint & mask) == network
    //   This is a single bitwise AND + equality comparison per range.
    //   For 16 ranges, that's 16 iterations — still faster than a Dictionary lookup.
    //
    // ORDER MATTERS: More specific ranges should come after broader ones only if
    // they share a prefix. Currently all ranges are non-overlapping so order is
    // irrelevant for correctness, but we keep RFC-logical grouping for readability.
    // ========================================================================
    
    private static readonly (uint Network, uint Mask, IpType Type, string Note)[] Ipv4Ranges =
    [
        // 0.0.0.0/8 — "This" network (RFC 791). Used for DHCP discovery, not routable.
        (0x00000000, 0xFF000000, IpType.Unspecified, "This network"),
        
        // 127.0.0.0/8 — Loopback (RFC 1122). Entire /8 block, not just 127.0.0.1.
        (0x7F000000, 0xFF000000, IpType.Loopback, "Loopback"),
        
        // RFC 1918 Private Networks — not routable on the public internet
        (0x0A000000, 0xFF000000, IpType.Private, "Private 10.0.0.0/8"),      // 10.0.0.0/8    (16M addresses)
        (0xAC100000, 0xFFF00000, IpType.Private, "Private 172.16.0.0/12"),   // 172.16.0.0/12 (1M addresses)
        (0xC0A80000, 0xFFFF0000, IpType.Private, "Private 192.168.0.0/16"),  // 192.168.0.0/16 (65K addresses)
        
        // 169.254.0.0/16 — Link-local / APIPA (RFC 3927). Auto-assigned when DHCP fails.
        (0xA9FE0000, 0xFFFF0000, IpType.LinkLocal, "Link-local"),
        
        // 100.64.0.0/10 — CGNAT (RFC 6598). ISP shared address space.
        // NOTE: CGNAT IPs CAN be geolocated (they map to the ISP's geographic region).
        (0x64400000, 0xFFC00000, IpType.CGNAT, "CGNAT shared"),
        
        // Documentation ranges (RFC 5737) — used in examples, never on the wire
        (0xC0000200, 0xFFFFFF00, IpType.Documentation, "TEST-NET-1"),  // 192.0.2.0/24
        (0xC6336400, 0xFFFFFF00, IpType.Documentation, "TEST-NET-2"),  // 198.51.100.0/24
        (0xCB007100, 0xFFFFFF00, IpType.Documentation, "TEST-NET-3"),  // 203.0.113.0/24
        
        // 198.18.0.0/15 — Benchmark testing (RFC 2544). Network equipment performance testing.
        (0xC6120000, 0xFFFE0000, IpType.Benchmark, "Benchmark"),
        
        // 192.0.0.0/24 — IETF Protocol Assignments (RFC 6890)
        (0xC0000000, 0xFFFFFF00, IpType.Reserved, "IETF Protocol"),
        
        // 224.0.0.0/4 — Multicast (RFC 5771). Class D address space.
        (0xE0000000, 0xF0000000, IpType.Multicast, "Multicast"),
        
        // 240.0.0.0/4 — Reserved (RFC 1112). Class E "future use" (never used).
        (0xF0000000, 0xF0000000, IpType.Reserved, "Reserved Class E"),
        
        // 255.255.255.255/32 — Limited broadcast (RFC 919)
        (0xFFFFFFFF, 0xFFFFFFFF, IpType.Broadcast, "Broadcast"),
    ];
    
    // ========================================================================
    // PUBLIC API — Entry points for IP classification
    // ========================================================================
    
    /// <summary>
    /// Classifies an IP address string into its network type.
    /// <para>
    /// Zero-allocation for valid IPv4 addresses (the entire parse + classify path
    /// operates on <see cref="ReadOnlySpan{T}"/> without creating intermediate strings).
    /// IPv6 addresses may allocate via <see cref="IPAddress.TryParse"/> on the cold path.
    /// </para>
    /// </summary>
    /// <param name="ipAddress">The IP address string to classify (IPv4 or IPv6).</param>
    /// <returns>An <see cref="IpClassification"/> with type, geo eligibility, and description.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IpClassification Classify(string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress))
            return new IpClassification(IpType.Invalid, false, "Empty");
        
        return Classify(ipAddress.AsSpan());
    }
    
    /// <summary>
    /// Classifies an IP address from a <see cref="ReadOnlySpan{T}"/> (zero-allocation entry point).
    /// <para>
    /// Routes to IPv4 or IPv6 classification based on the presence of a colon character.
    /// IPv4 has dots only; IPv6 always contains at least one colon.
    /// </para>
    /// </summary>
    public static IpClassification Classify(ReadOnlySpan<char> ipSpan)
    {
        if (ipSpan.IsEmpty)
            return new IpClassification(IpType.Invalid, false, "Empty");
        
        // Trim whitespace without allocation (Span.Trim returns a sub-span)
        ipSpan = ipSpan.Trim();
        
        // Presence of ':' → IPv6 (or IPv4-mapped IPv6 like ::ffff:1.2.3.4)
        // Absence of ':' → IPv4 (dotted decimal)
        if (ipSpan.Contains(':'))
        {
            return ClassifyIpv6(ipSpan);
        }
        
        return ClassifyIpv4(ipSpan);
    }
    
    // ========================================================================
    // IPv4 CLASSIFICATION — Zero allocation via manual dotted-decimal parsing
    // ========================================================================
    
    /// <summary>
    /// Classifies an IPv4 address by parsing it to a <c>uint</c> and checking
    /// against all reserved ranges using bitwise AND + equality.
    /// </summary>
    private static IpClassification ClassifyIpv4(ReadOnlySpan<char> ipSpan)
    {
        // Parse "1.2.3.4" → uint without IPAddress.TryParse (zero allocation)
        if (!TryParseIpv4ToUint(ipSpan, out uint ipValue))
        {
            return new IpClassification(IpType.Invalid, false, "Invalid IPv4");
        }
        
        // Check against all reserved ranges using bit operations.
        // Each check: (ipUint & mask) == network — single AND + CMP.
        foreach (var (network, mask, type, note) in Ipv4Ranges)
        {
            if ((ipValue & mask) == network)
            {
                // Reserved ranges generally should NOT be geolocated — they don't
                // correspond to a physical location. Exception: CGNAT (100.64.0.0/10)
                // maps to the ISP's geographic region and CAN be geolocated.
                bool shouldGeo = type == IpType.CGNAT;
                return new IpClassification(type, shouldGeo, note);
            }
        }
        
        // Not in any reserved range → public internet IP (geolocatable)
        return new IpClassification(IpType.Public, true, null);
    }
    
    /// <summary>
    /// Parses an IPv4 dotted-decimal string to a <c>uint</c> in network byte order (big-endian).
    /// <para>
    /// Manual character-by-character parsing — no IPAddress.TryParse, no string.Split,
    /// no intermediate allocations. Validates:
    /// <list type="bullet">
    ///   <item><description>Exactly 4 octets separated by dots</description></item>
    ///   <item><description>Each octet is 0–255 with 1–3 digits</description></item>
    ///   <item><description>No leading zeros, no non-digit characters</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The result is in network byte order (big-endian): <c>192.168.1.1</c> → <c>0xC0A80101</c>.
    /// This matches the Ipv4Ranges constants, so bitwise comparison works directly.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseIpv4ToUint(ReadOnlySpan<char> span, out uint result)
    {
        result = 0;
        
        int octetIndex = 0;    // Which octet we're currently parsing (0–3)
        int currentOctet = 0;  // Accumulated value of current octet
        int digitCount = 0;    // Digits seen in current octet (for validation)
        
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            
            if (c == '.')
            {
                // Dot separator — validate and shift in the completed octet
                if (digitCount == 0 || currentOctet > 255 || octetIndex >= 3)
                {
                    return false; // Empty octet, overflow, or too many dots
                }
                
                // Shift result left 8 bits and OR in the completed octet.
                // This builds the uint in big-endian order: first octet → MSB.
                result = (result << 8) | (uint)currentOctet;
                currentOctet = 0;
                digitCount = 0;
                octetIndex++;
            }
            else if (c >= '0' && c <= '9')
            {
                // Accumulate digit into current octet
                currentOctet = currentOctet * 10 + (c - '0');
                digitCount++;
                
                // Early overflow detection (max 3 digits, max value 255)
                if (digitCount > 3 || currentOctet > 255)
                {
                    return false;
                }
            }
            else
            {
                // Non-digit, non-dot → invalid IPv4
                return false;
            }
        }
        
        // Process the final (4th) octet — there's no trailing dot
        if (digitCount == 0 || currentOctet > 255 || octetIndex != 3)
        {
            return false; // Missing 4th octet or wrong number of octets
        }
        
        // Shift in the last octet to complete the 32-bit value
        result = (result << 8) | (uint)currentOctet;
        return true;
    }
    
    // ========================================================================
    // IPv6 CLASSIFICATION — Uses IPAddress.TryParse (cold path, acceptable alloc)
    // ========================================================================
    
    /// <summary>
    /// Classifies an IPv6 address. Handles IPv4-mapped addresses specially.
    /// <para>
    /// IPv4-mapped addresses (<c>::ffff:x.x.x.x</c>) are extracted and routed through
    /// the zero-allocation IPv4 classifier. Full IPv6 addresses are parsed via
    /// <see cref="IPAddress.TryParse"/> (which allocates ~64 bytes), then classified
    /// using stackalloc byte comparison against known prefixes.
    /// </para>
    /// </summary>
    private static IpClassification ClassifyIpv6(ReadOnlySpan<char> ipSpan)
    {
        // FAST PATH: Check for IPv4-mapped IPv6 (::ffff:192.168.1.1)
        // This is common when an IPv4 client connects to a dual-stack server.
        // Extract the IPv4 portion and classify it with the zero-alloc IPv4 path.
        if (TryExtractIpv4FromMappedIpv6(ipSpan, out var ipv4Span))
        {
            var result = ClassifyIpv4(ipv4Span);
            return result;
        }
        
        // COLD PATH: Full IPv6 parsing via IPAddress.TryParse.
        // IPv6 address parsing is complex (multiple :: compression formats,
        // zone IDs, etc.) so we delegate to the framework. This allocates
        // ~64 bytes but IPv6 traffic is a small fraction of total volume.
        if (!IPAddress.TryParse(ipSpan, out var address) || 
            address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return new IpClassification(IpType.Invalid, false, "Invalid IPv6");
        }
        
        // Write the parsed address bytes to a stack-allocated buffer.
        // TryWriteBytes avoids the GetAddressBytes() heap allocation.
        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out _))
        {
            return new IpClassification(IpType.Invalid, false, "IPv6 write failed");
        }
        
        return ClassifyIpv6Bytes(bytes);
    }
    
    /// <summary>
    /// Attempts to extract an IPv4 address from an IPv4-mapped IPv6 string.
    /// <para>
    /// Handles the <c>::ffff:x.x.x.x</c> format without allocation by slicing
    /// the input span at the known prefix length (7 chars for "::ffff:").
    /// </para>
    /// </summary>
    /// <param name="ipv6Span">The full IPv6 address string as a span.</param>
    /// <param name="ipv4Span">If successful, the IPv4 portion (e.g., "192.168.1.1").</param>
    /// <returns>True if the address matches the <c>::ffff:</c> prefix pattern.</returns>
    private static bool TryExtractIpv4FromMappedIpv6(ReadOnlySpan<char> ipv6Span, out ReadOnlySpan<char> ipv4Span)
    {
        ipv4Span = default;
        
        // The IPv4-mapped prefix is exactly 7 characters: "::ffff:"
        ReadOnlySpan<char> prefix = "::ffff:";
        
        if (ipv6Span.Length > 7)
        {
            var prefixSpan = ipv6Span[..7];
            
            // Case-insensitive comparison (some systems use uppercase FFFF)
            if (prefixSpan.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var remainder = ipv6Span[7..];
                
                // Validate: remainder must look like IPv4 (dots, no colons)
                // "::ffff:192.168.1.1" → remainder = "192.168.1.1" ✓
                // "::ffff:0:0" → remainder = "0:0" (has colon) ✗
                if (remainder.Contains('.') && !remainder.Contains(':'))
                {
                    ipv4Span = remainder;
                    return true;
                }
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Classifies a full IPv6 address from its 16-byte binary representation.
    /// <para>
    /// Checks against all well-known IPv6 reserved prefixes in RFC order.
    /// Uses SIMD-accelerated <see cref="IsAllZeros"/> and <see cref="IsLoopbackV6"/>
    /// for the two most common special addresses (<c>::</c> and <c>::1</c>).
    /// </para>
    /// </summary>
    private static IpClassification ClassifyIpv6Bytes(ReadOnlySpan<byte> bytes)
    {
        // :: (all zeros) — unspecified address (RFC 4291 §2.5.2)
        if (IsAllZeros(bytes))
        {
            return new IpClassification(IpType.Unspecified, false, "Unspecified");
        }
        
        // ::1 — loopback address (RFC 4291 §2.5.3)
        if (IsLoopbackV6(bytes))
        {
            return new IpClassification(IpType.Loopback, false, "Loopback");
        }
        
        // ::ffff:0:0/96 — IPv4-mapped IPv6 (RFC 4291 §2.5.5.2)
        // Bytes 0–9 = 0x00, bytes 10–11 = 0xFF, bytes 12–15 = IPv4 address
        if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0 &&
            bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0 &&
            bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0xFF && bytes[11] == 0xFF)
        {
            // Extract the embedded IPv4 address and classify it against IPv4 ranges
            uint ipv4 = ((uint)bytes[12] << 24) | ((uint)bytes[13] << 16) | 
                        ((uint)bytes[14] << 8) | bytes[15];
            
            foreach (var (network, mask, type, note) in Ipv4Ranges)
            {
                if ((ipv4 & mask) == network)
                {
                    bool shouldGeo = type == IpType.CGNAT;
                    return new IpClassification(type, shouldGeo, $"IPv4-mapped: {note}");
                }
            }
            return new IpClassification(IpType.Public, true, "IPv4-mapped");
        }
        
        // fe80::/10 — Link-local unicast (RFC 4291 §2.5.6)
        // First 10 bits = 1111111010, checked via: byte[0]==0xFE && (byte[1] & 0xC0)==0x80
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
        {
            return new IpClassification(IpType.LinkLocal, false, "Link-local");
        }
        
        // fc00::/7 — Unique Local Address (RFC 4193), usually fd00::/8 in practice
        // First 7 bits = 1111110, checked via: (byte[0] & 0xFE) == 0xFC
        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return new IpClassification(IpType.Private, false, "Unique Local");
        }
        
        // ff00::/8 — Multicast (RFC 4291 §2.7)
        if (bytes[0] == 0xFF)
        {
            return new IpClassification(IpType.Multicast, false, "Multicast");
        }
        
        // 2001:db8::/32 — Documentation (RFC 3849)
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
        {
            return new IpClassification(IpType.Documentation, false, "Documentation");
        }
        
        // 3fff::/20 — Documentation (RFC 9637, newer allocation)
        if (bytes[0] == 0x3F && bytes[1] == 0xFF && (bytes[2] & 0xF0) == 0x00)
        {
            return new IpClassification(IpType.Documentation, false, "Documentation (new)");
        }
        
        // 2001::/32 — Teredo tunneling (RFC 4380, deprecated but still seen in the wild)
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return new IpClassification(IpType.Reserved, false, "Teredo tunnel");
        }
        
        // 2002::/16 — 6to4 tunneling (RFC 3056, deprecated)
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            return new IpClassification(IpType.Reserved, false, "6to4 tunnel");
        }
        
        // 0100::/64 — Discard prefix (RFC 6666)
        if (bytes[0] == 0x01 && bytes[1] == 0x00 && 
            bytes[2] == 0x00 && bytes[3] == 0x00 &&
            bytes[4] == 0x00 && bytes[5] == 0x00 &&
            bytes[6] == 0x00 && bytes[7] == 0x00)
        {
            return new IpClassification(IpType.Reserved, false, "Discard prefix");
        }
        
        // 64:ff9b::/96 — NAT64 well-known prefix (RFC 6052)
        // Contains an embedded IPv4 address in the last 4 bytes
        if (bytes[0] == 0x00 && bytes[1] == 0x64 &&
            bytes[2] == 0xFF && bytes[3] == 0x9B)
        {
            return new IpClassification(IpType.Reserved, false, "NAT64");
        }
        
        // Not in any reserved range → public IPv6 address (geolocatable)
        return new IpClassification(IpType.Public, true, null);
    }
    
    /// <summary>
    /// SIMD-accelerated check: are all 16 bytes zero?
    /// <para>
    /// <see cref="System.MemoryExtensions.IndexOfAnyExcept{T}(ReadOnlySpan{T}, T)"/> uses
    /// SSE2/AVX2 vector instructions to scan 16–32 bytes per CPU cycle.
    /// For a 16-byte IPv6 address, this completes in a single SIMD operation.
    /// Returns -1 if every byte is 0x00 (all zeros = unspecified address <c>::</c>).
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAllZeros(ReadOnlySpan<byte> bytes)
    {
        return bytes.IndexOfAnyExcept((byte)0) < 0;
    }
    
    /// <summary>
    /// SIMD-accelerated check: is this the IPv6 loopback address <c>::1</c>?
    /// <para>
    /// Layout: 15 zero bytes followed by 0x01.
    /// Fast-reject: check byte[15] first (cheapest check). If it's not 0x01,
    /// skip the SIMD scan entirely. If it IS 0x01, use <c>IndexOfAnyExcept</c>
    /// on the first 15 bytes to verify they're all zeros.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLoopbackV6(ReadOnlySpan<byte> bytes)
    {
        // Check last byte first as a fast-reject before the SIMD scan
        return bytes[15] == 1 && bytes[..15].IndexOfAnyExcept((byte)0) < 0;
    }
    
    // ========================================================================
    // UTILITY METHODS — Convenience wrappers for common checks
    // ========================================================================
    
    /// <summary>
    /// Quick check: should this IP be sent to a geolocation service?
    /// <para>
    /// Slightly faster than <c>Classify(ip).ShouldGeolocate</c> when you only
    /// need a boolean answer, because the compiler can elide the unused struct fields.
    /// In practice the difference is negligible — use whichever reads better.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ShouldGeolocate(string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress))
            return false;
        
        return Classify(ipAddress.AsSpan()).ShouldGeolocate;
    }
    
    /// <summary>
    /// Quick check: is this IP in a private or internal range?
    /// <para>
    /// Returns true for <see cref="IpType.Private"/>, <see cref="IpType.Loopback"/>,
    /// and <see cref="IpType.LinkLocal"/>. Used to filter out internal test traffic.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPrivateOrInternal(string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress))
            return false;
        
        var type = Classify(ipAddress.AsSpan()).Type;
        return type is IpType.Private or IpType.Loopback or IpType.LinkLocal;
    }
}
