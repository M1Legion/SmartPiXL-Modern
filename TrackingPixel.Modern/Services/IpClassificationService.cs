using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using TrackingPixel.Models;

namespace TrackingPixel.Services;

/// <summary>
/// High-performance IP address classifier.
/// Zero-allocation hot path using Span and bit operations.
/// </summary>
public static class IpClassificationService
{
    // ========================================================================
    // IPv4 RESERVED RANGES - Stored as (network, mask) for bit comparison
    // ========================================================================
    // Format: (networkAddress, subnetMask, type, note)
    // Network and mask are pre-computed for fast bitwise comparison
    
    private static readonly (uint Network, uint Mask, IpType Type, string Note)[] Ipv4Ranges =
    [
        // Unspecified / "This" network - 0.0.0.0/8
        (0x00000000, 0xFF000000, IpType.Unspecified, "This network"),
        
        // Loopback - 127.0.0.0/8
        (0x7F000000, 0xFF000000, IpType.Loopback, "Loopback"),
        
        // Private networks (RFC 1918)
        (0x0A000000, 0xFF000000, IpType.Private, "Private 10.0.0.0/8"),      // 10.0.0.0/8
        (0xAC100000, 0xFFF00000, IpType.Private, "Private 172.16.0.0/12"),   // 172.16.0.0/12
        (0xC0A80000, 0xFFFF0000, IpType.Private, "Private 192.168.0.0/16"),  // 192.168.0.0/16
        
        // Link-local (APIPA) - 169.254.0.0/16
        (0xA9FE0000, 0xFFFF0000, IpType.LinkLocal, "Link-local"),
        
        // CGNAT (Carrier-grade NAT) - 100.64.0.0/10
        (0x64400000, 0xFFC00000, IpType.CGNAT, "CGNAT shared"),
        
        // Documentation (TEST-NET)
        (0xC0000200, 0xFFFFFF00, IpType.Documentation, "TEST-NET-1"),  // 192.0.2.0/24
        (0xC6336400, 0xFFFFFF00, IpType.Documentation, "TEST-NET-2"),  // 198.51.100.0/24
        (0xCB007100, 0xFFFFFF00, IpType.Documentation, "TEST-NET-3"),  // 203.0.113.0/24
        
        // Benchmark testing - 198.18.0.0/15
        (0xC6120000, 0xFFFE0000, IpType.Benchmark, "Benchmark"),
        
        // IETF Protocol Assignments - 192.0.0.0/24
        (0xC0000000, 0xFFFFFF00, IpType.Reserved, "IETF Protocol"),
        
        // Multicast - 224.0.0.0/4
        (0xE0000000, 0xF0000000, IpType.Multicast, "Multicast"),
        
        // Reserved (Class E) - 240.0.0.0/4
        (0xF0000000, 0xF0000000, IpType.Reserved, "Reserved Class E"),
        
        // Broadcast
        (0xFFFFFFFF, 0xFFFFFFFF, IpType.Broadcast, "Broadcast"),
    ];
    
    // ========================================================================
    // PUBLIC API
    // ========================================================================
    
    /// <summary>
    /// Classifies an IP address string.
    /// Zero-allocation for valid IPv4 addresses.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IpClassification Classify(string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress))
            return new IpClassification(IpType.Invalid, false, "Empty");
        
        return Classify(ipAddress.AsSpan());
    }
    
    /// <summary>
    /// Classifies an IP address from a ReadOnlySpan (zero-allocation).
    /// </summary>
    public static IpClassification Classify(ReadOnlySpan<char> ipSpan)
    {
        if (ipSpan.IsEmpty)
            return new IpClassification(IpType.Invalid, false, "Empty");
        
        // Trim whitespace without allocation
        ipSpan = ipSpan.Trim();
        
        // Check for IPv6 (contains colon)
        if (ipSpan.Contains(':'))
        {
            return ClassifyIpv6(ipSpan);
        }
        
        // Assume IPv4
        return ClassifyIpv4(ipSpan);
    }
    
    // ========================================================================
    // IPv4 CLASSIFICATION - Zero allocation
    // ========================================================================
    
    private static IpClassification ClassifyIpv4(ReadOnlySpan<char> ipSpan)
    {
        // Parse IPv4 to uint without allocation
        if (!TryParseIpv4ToUint(ipSpan, out uint ipValue))
        {
            return new IpClassification(IpType.Invalid, false, "Invalid IPv4");
        }
        
        // Check against all reserved ranges using bit operations
        foreach (var (network, mask, type, note) in Ipv4Ranges)
        {
            if ((ipValue & mask) == network)
            {
                // Reserved ranges generally should not be geolocated
                // Exception: CGNAT can be geolocated (shared ISP space)
                bool shouldGeo = type == IpType.CGNAT;
                return new IpClassification(type, shouldGeo, note);
            }
        }
        
        // Not in any reserved range = public
        return new IpClassification(IpType.Public, true, null);
    }
    
    /// <summary>
    /// Parses IPv4 dotted-decimal to uint. Zero allocation.
    /// Returns IP in network byte order (big-endian).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseIpv4ToUint(ReadOnlySpan<char> span, out uint result)
    {
        result = 0;
        
        int octetIndex = 0;
        int currentOctet = 0;
        int digitCount = 0;
        
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            
            if (c == '.')
            {
                if (digitCount == 0 || currentOctet > 255 || octetIndex >= 3)
                {
                    return false;
                }
                
                // Shift result left 8 bits and add octet
                result = (result << 8) | (uint)currentOctet;
                currentOctet = 0;
                digitCount = 0;
                octetIndex++;
            }
            else if (c >= '0' && c <= '9')
            {
                currentOctet = currentOctet * 10 + (c - '0');
                digitCount++;
                
                if (digitCount > 3 || currentOctet > 255)
                {
                    return false;
                }
            }
            else
            {
                // Invalid character
                return false;
            }
        }
        
        // Process last octet
        if (digitCount == 0 || currentOctet > 255 || octetIndex != 3)
        {
            return false;
        }
        
        result = (result << 8) | (uint)currentOctet;
        return true;
    }
    
    // ========================================================================
    // IPv6 CLASSIFICATION
    // ========================================================================
    
    private static IpClassification ClassifyIpv6(ReadOnlySpan<char> ipSpan)
    {
        // Check for IPv4-mapped IPv6 (::ffff:192.168.1.1)
        if (TryExtractIpv4FromMappedIpv6(ipSpan, out var ipv4Span))
        {
            var result = ClassifyIpv4(ipv4Span);
            // Keep the classification but note it was IPv4-mapped
            return result;
        }
        
        // For full IPv6, we need to parse and check ranges
        // Using IPAddress.TryParse here as IPv6 parsing is complex
        // This does allocate, but IPv6 is less common
        if (!IPAddress.TryParse(ipSpan, out var address) || 
            address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return new IpClassification(IpType.Invalid, false, "Invalid IPv6");
        }
        
        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out _))
        {
            return new IpClassification(IpType.Invalid, false, "IPv6 write failed");
        }
        
        return ClassifyIpv6Bytes(bytes);
    }
    
    /// <summary>
    /// Try to extract IPv4 address from IPv4-mapped IPv6 format.
    /// Handles ::ffff:x.x.x.x format without allocation.
    /// </summary>
    private static bool TryExtractIpv4FromMappedIpv6(ReadOnlySpan<char> ipv6Span, out ReadOnlySpan<char> ipv4Span)
    {
        ipv4Span = default;
        
        // Look for ::ffff: prefix (case-insensitive)
        ReadOnlySpan<char> prefix = "::ffff:";
        
        if (ipv6Span.Length > 7)
        {
            var prefixSpan = ipv6Span[..7];
            
            if (prefixSpan.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var remainder = ipv6Span[7..];
                
                // Verify remainder looks like IPv4 (contains dots, no colons)
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
    /// Classifies IPv6 from 16-byte representation.
    /// </summary>
    private static IpClassification ClassifyIpv6Bytes(ReadOnlySpan<byte> bytes)
    {
        // Unspecified address ::
        if (IsAllZeros(bytes))
        {
            return new IpClassification(IpType.Unspecified, false, "Unspecified");
        }
        
        // Loopback ::1
        if (IsLoopbackV6(bytes))
        {
            return new IpClassification(IpType.Loopback, false, "Loopback");
        }
        
        // IPv4-mapped ::ffff:0:0/96
        if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0 &&
            bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0 &&
            bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0xFF && bytes[11] == 0xFF)
        {
            // Extract IPv4 portion and classify
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
        
        // Link-local fe80::/10
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
        {
            return new IpClassification(IpType.LinkLocal, false, "Link-local");
        }
        
        // Unique Local Address (ULA) fc00::/7 (usually fd00::/8 in practice)
        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return new IpClassification(IpType.Private, false, "Unique Local");
        }
        
        // Multicast ff00::/8
        if (bytes[0] == 0xFF)
        {
            return new IpClassification(IpType.Multicast, false, "Multicast");
        }
        
        // Documentation 2001:db8::/32
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
        {
            return new IpClassification(IpType.Documentation, false, "Documentation");
        }
        
        // Documentation 3fff::/20 (newer)
        if (bytes[0] == 0x3F && bytes[1] == 0xFF && (bytes[2] & 0xF0) == 0x00)
        {
            return new IpClassification(IpType.Documentation, false, "Documentation (new)");
        }
        
        // Teredo 2001::/32 (tunneling, deprecated but still seen)
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return new IpClassification(IpType.Reserved, false, "Teredo tunnel");
        }
        
        // 6to4 2002::/16 (deprecated)
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            return new IpClassification(IpType.Reserved, false, "6to4 tunnel");
        }
        
        // Discard 100::/64
        if (bytes[0] == 0x01 && bytes[1] == 0x00 && 
            bytes[2] == 0x00 && bytes[3] == 0x00 &&
            bytes[4] == 0x00 && bytes[5] == 0x00 &&
            bytes[6] == 0x00 && bytes[7] == 0x00)
        {
            return new IpClassification(IpType.Reserved, false, "Discard prefix");
        }
        
        // NAT64 64:ff9b::/96
        if (bytes[0] == 0x00 && bytes[1] == 0x64 &&
            bytes[2] == 0xFF && bytes[3] == 0x9B)
        {
            // This contains an embedded IPv4 - could extract and classify
            return new IpClassification(IpType.Reserved, false, "NAT64");
        }
        
        // Not in any reserved range = public
        return new IpClassification(IpType.Public, true, null);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAllZeros(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0) return false;
        }
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLoopbackV6(ReadOnlySpan<byte> bytes)
    {
        // ::1 = 15 zeros followed by 0x01
        for (int i = 0; i < 15; i++)
        {
            if (bytes[i] != 0) return false;
        }
        return bytes[15] == 1;
    }
    
    // ========================================================================
    // UTILITY METHODS
    // ========================================================================
    
    /// <summary>
    /// Quick check if an IP should be geolocated.
    /// Slightly faster than full Classify() when you only need yes/no.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ShouldGeolocate(string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress))
            return false;
        
        return Classify(ipAddress.AsSpan()).ShouldGeolocate;
    }
    
    /// <summary>
    /// Quick check if IP is in a private/internal range.
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
