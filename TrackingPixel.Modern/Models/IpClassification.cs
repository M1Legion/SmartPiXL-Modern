namespace TrackingPixel.Models;

/// <summary>
/// Classification result for an IP address.
/// </summary>
public readonly record struct IpClassification(
    IpType Type,
    bool ShouldGeolocate,
    string? RangeNote = null
);

/// <summary>
/// IP address type classification.
/// </summary>
public enum IpType : byte
{
    /// <summary>Normal routable IP - geolocate</summary>
    Public = 0,
    
    /// <summary>RFC 1918 private networks (10.x, 172.16-31.x, 192.168.x)</summary>
    Private = 1,
    
    /// <summary>127.x.x.x or ::1</summary>
    Loopback = 2,
    
    /// <summary>169.254.x.x or fe80::</summary>
    LinkLocal = 3,
    
    /// <summary>100.64.x.x - Carrier-grade NAT shared space</summary>
    CGNAT = 4,
    
    /// <summary>192.0.2.x, 198.51.100.x, 203.0.113.x, 2001:db8::</summary>
    Documentation = 5,
    
    /// <summary>224.x.x.x+ or ff00::</summary>
    Multicast = 6,
    
    /// <summary>240.x.x.x+ (Class E) or other reserved</summary>
    Reserved = 7,
    
    /// <summary>255.255.255.255</summary>
    Broadcast = 8,
    
    /// <summary>0.x.x.x or ::</summary>
    Unspecified = 9,
    
    /// <summary>198.18.x.x - Benchmark testing</summary>
    Benchmark = 10,
    
    /// <summary>Couldn't parse or empty</summary>
    Invalid = 255
}
