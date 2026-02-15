namespace TrackingPixel.Models;

// ============================================================================
// TRACKING DATA — The canonical representation of a single pixel hit.
//
// Created by TrackingCaptureService from an HTTP request, enriched by
// FingerprintStabilityService and IpBehaviorService (server-side alert
// params appended to QueryString), then enqueued to DatabaseWriterService
// which bulk-inserts into PiXL.Test.
//
// IMPORTANT: The 9 properties here map 1:1 to the 9 columns of PiXL.Test.
// Column mapping is ordinal-based in DatabaseWriterService.ColumnNames[].
// If you add/remove/reorder properties, update ColumnNames AND the SQL table.
//
// QueryString carries ALL client-side fingerprint data (~90 parameters from
// the pixel JavaScript). These are parsed server-side by the SQL view
// vw_Dash_* and the ETL proc ETL.usp_ParseNewHits into PiXL.Parsed (~175 columns).
// ============================================================================

/// <summary>
/// Immutable record representing a single tracking pixel hit.
/// <para>
/// Record semantics give us value-based equality (useful in tests), a built-in
/// <c>with</c> expression for non-destructive mutation (used when appending
/// server-side alert params to <see cref="QueryString"/>), and compiler-generated
/// <c>ToString</c>/<c>GetHashCode</c> without boilerplate.
/// </para>
/// <para>
/// <c>sealed</c> prevents inheritance — allows the JIT to devirtualize method
/// calls and enables more aggressive inlining.
/// </para>
/// </summary>
public sealed record TrackingData
{
    /// <summary>UTC timestamp when the HTTP request hit TrackingCaptureService.</summary>
    public DateTime ReceivedAt { get; init; }
    
    /// <summary>
    /// Client identifier from the URL path, e.g., "12345" from <c>/12345/1_SMART.GIF</c>.
    /// Parsed by regex from the first path segment. Null if path doesn't match.
    /// </summary>
    public string? CompanyID { get; init; }
    
    /// <summary>
    /// Campaign/pixel identifier from the URL path, e.g., "1" from <c>/12345/1_SMART.GIF</c>.
    /// Parsed from the second path segment (before the <c>_SMART.GIF</c> suffix).
    /// </summary>
    public string? PiXLID { get; init; }
    
    /// <summary>
    /// Real client IP address, extracted from the proxy header priority chain:
    /// CF-Connecting-IP → True-Client-IP → X-Real-IP → X-Forwarded-For (first) → Connection.
    /// </summary>
    public string? IPAddress { get; init; }
    
    /// <summary>Full request path including leading slash, e.g., <c>/12345/1_SMART.GIF</c>.</summary>
    public string? RequestPath { get; init; }
    
    /// <summary>
    /// Full query string with leading <c>?</c> trimmed. Contains all pixel JavaScript
    /// parameters (<c>sw=1920&amp;sh=1080&amp;canvasFP=abc...</c>) plus any server-side
    /// enrichment params (<c>&amp;_srv_fpAlert=1</c>). Can exceed 8 KB for heavy fingerprints.
    /// </summary>
    public string? QueryString { get; init; }
    
    /// <summary>
    /// JSON object of captured HTTP headers. Built manually (not via JsonSerializer)
    /// using a thread-static StringBuilder with SIMD-accelerated JSON escaping.
    /// Contains: User-Agent, Referer, Client Hints, Sec-Fetch-*, proxy headers, TLS fingerprints.
    /// </summary>
    public string? HeadersJson { get; init; }
    
    /// <summary>
    /// User-Agent header value, kept as a separate column for fast SQL filtering
    /// without needing to parse HeadersJson. Truncated to 2000 characters.
    /// </summary>
    public string? UserAgent { get; init; }
    
    /// <summary>
    /// HTTP Referer header — the page that embedded the tracking pixel.
    /// Truncated to 2000 characters. Kept separate from HeadersJson for the same
    /// reason as UserAgent: fast direct-column queries in SQL.
    /// </summary>
    public string? Referer { get; init; }
}
