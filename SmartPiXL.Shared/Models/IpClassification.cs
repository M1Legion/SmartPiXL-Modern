namespace SmartPiXL.Models;

// ============================================================================
// IP CLASSIFICATION TYPES — Used by IpClassificationService to categorize
// IP addresses into reserved vs. public ranges. The classification determines
// whether an IP should be geolocated and how it's treated in the dashboard.
//
// IpClassification is a readonly record struct — stack-allocated, 0 GC pressure.
// IpType is a byte-backed enum — fits in a single register, enables compact
// storage in arrays and pattern-match jump tables.
// ============================================================================

/// <summary>
/// Stack-allocated classification result for an IP address.
/// <para>
/// <c>readonly record struct</c> gives us value semantics (no heap allocation),
/// structural equality, and <c>with</c> expression support. The <c>readonly</c>
/// modifier ensures all fields are immutable once constructed.
/// </para>
/// </summary>
/// <param name="Type">The broad IP category (Public, Private, Loopback, etc.).</param>
/// <param name="ShouldGeolocate">
/// Whether this IP is worth sending to a geolocation service. False for all
/// non-routable ranges. Also false for Documentation/Benchmark test addresses.
/// True for Public and CGNAT (real users behind carrier-grade NAT).
/// </param>
/// <param name="RangeNote">
/// Human-readable note about which RFC/range matched, e.g., <c>"Private 10.0.0.0/8"</c>.
/// Null when the IP is public (no specific range matched).
/// </param>
public readonly record struct IpClassification(
    IpType Type,
    bool ShouldGeolocate,
    string? RangeNote = null
);

/// <summary>
/// IP address type classification. Byte-backed for compact storage.
/// <para>
/// Values 0–10 are sequential for common types. 255 (<c>Invalid</c>) is
/// intentionally far from the others so it stands out in debug output
/// and can't be confused with a valid classification by off-by-one errors.
/// </para>
/// </summary>
public enum IpType : byte
{
    /// <summary>Normal routable IP — geolocate. This is the common case for real traffic.</summary>
    Public = 0,
    
    /// <summary>RFC 1918 private networks: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16.
    /// Also covers IPv6 Unique Local Addresses (fc00::/7).</summary>
    Private = 1,
    
    /// <summary>Loopback: 127.0.0.0/8 (IPv4) or ::1 (IPv6). Only seen from localhost requests.</summary>
    Loopback = 2,
    
    /// <summary>Link-local / APIPA: 169.254.0.0/16 or fe80::/10. Auto-configured when DHCP fails.</summary>
    LinkLocal = 3,
    
    /// <summary>Carrier-Grade NAT (RFC 6598): 100.64.0.0/10. Real users behind ISP-level NAT.
    /// Geolocatable — these are actual subscriber connections.</summary>
    CGNAT = 4,
    
    /// <summary>Documentation / TEST-NET: 192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24,
    /// 2001:db8::/32. Used in examples and documentation, never appear on the real internet.</summary>
    Documentation = 5,
    
    /// <summary>Multicast: 224.0.0.0/4 (IPv4) or ff00::/8 (IPv6). Group communication addresses.</summary>
    Multicast = 6,
    
    /// <summary>Reserved / Class E: 240.0.0.0/4 (IPv4). Also covers protocol-specific ranges
    /// like Teredo (2001::/32), 6to4 (2002::/16), NAT64 (64:ff9b::/96), Discard (100::/64).</summary>
    Reserved = 7,
    
    /// <summary>IPv4 broadcast: 255.255.255.255. May match Reserved first depending on range order.</summary>
    Broadcast = 8,
    
    /// <summary>Unspecified: 0.0.0.0/8 (IPv4) or :: (IPv6). "This host on this network."</summary>
    Unspecified = 9,
    
    /// <summary>Benchmark testing: 198.18.0.0/15. Used for inter-network device benchmark testing.</summary>
    Benchmark = 10,
    
    /// <summary>Could not be parsed, was null, or was empty string. Sentinel value at 255.</summary>
    Invalid = 255
}
