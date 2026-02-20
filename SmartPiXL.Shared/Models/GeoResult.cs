namespace SmartPiXL.Models;

// ============================================================================
// GEO RESULT — Geolocation data for a single IP address.
//
// Returned by GeoCacheService after looking up an IP in IPAPI.IP.
// Used by TrackingEndpoints to append _srv_geo* parameters and by the
// ETL pipeline to populate geo columns in PiXL.Parsed.
//
// readonly record struct — stack-allocated, zero GC pressure on the hot path.
// Fields are sized to match the IPAPI.IP table columns.
// ============================================================================

/// <summary>
/// Stack-allocated geolocation result for an IP address.
/// <para>
/// <c>readonly record struct</c> — same pattern as <see cref="IpClassification"/>.
/// Value semantics, no heap allocation, structural equality.
/// The <c>Found</c> flag distinguishes a cache hit from a miss without needing nullable.
/// </para>
/// </summary>
public readonly record struct GeoResult
{
    /// <summary>Whether this IP was found in the IPAPI.IP table with status='success'.</summary>
    public bool Found { get; init; }
    
    /// <summary>Full country name, e.g., "United States". Null if not found.</summary>
    public string? Country { get; init; }
    
    /// <summary>ISO 3166-1 alpha-2 country code, e.g., "US". Null if not found.</summary>
    public string? CountryCode { get; init; }
    
    /// <summary>Region/state name, e.g., "California". Null if not found.</summary>
    public string? Region { get; init; }
    
    /// <summary>City name, e.g., "San Francisco". Null if not found.</summary>
    public string? City { get; init; }
    
    /// <summary>Postal/ZIP code, e.g., "94105". Null if not found.</summary>
    public string? Zip { get; init; }
    
    /// <summary>Latitude as decimal degrees. Null if not found or unparseable.</summary>
    public double? Lat { get; init; }
    
    /// <summary>Longitude as decimal degrees. Null if not found or unparseable.</summary>
    public double? Lon { get; init; }
    
    /// <summary>IANA timezone identifier, e.g., "America/New_York". Null if not found.</summary>
    public string? Timezone { get; init; }
    
    /// <summary>Internet Service Provider name. Null if not found.</summary>
    public string? ISP { get; init; }
    
    /// <summary>Organization name (often same as ISP). Null if not found.</summary>
    public string? Org { get; init; }
    
    /// <summary>Whether the IP is a known proxy/VPN. Null if unknown.</summary>
    public bool? IsProxy { get; init; }
    
    /// <summary>Whether the IP belongs to a mobile carrier. Null if unknown.</summary>
    public bool? IsMobile { get; init; }
    
    /// <summary>
    /// Pre-computed "not found" sentinel. Avoids allocating a new struct for every cache miss.
    /// <c>Found</c> is false, all other fields are null/default.
    /// </summary>
    public static readonly GeoResult NotFound = new() { Found = false };
}
