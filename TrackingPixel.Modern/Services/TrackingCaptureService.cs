using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using TrackingPixel.Models;

namespace TrackingPixel.Services;

/// <summary>
/// Captures tracking data from HTTP requests.
/// Stateless service - allocates minimally per request.
/// </summary>
public sealed partial class TrackingCaptureService
{
    // Source-generated regex for path parsing - AOT-friendly, zero-allocation matching
    [GeneratedRegex(@"^/?(?<client>[^/]+)/(?<campaign>[^_]+)")]
    private static partial Regex PathParseRegex();
    
    // Static header keys - no allocation per request
    private static readonly string[] HeaderKeysToCapture =
    [
        "User-Agent", "Referer", "Accept-Language", "DNT",
        "X-Forwarded-For", "X-Real-IP", "CF-Connecting-IP", "True-Client-IP",
        "Sec-CH-UA", "Sec-CH-UA-Platform", "Sec-CH-UA-Mobile", "Sec-CH-UA-Model",
        "Sec-CH-UA-Platform-Version", "Sec-CH-UA-Arch", "Sec-CH-UA-Bitness",
        "Sec-Fetch-Site", "Sec-Fetch-Mode", "Sec-Fetch-Dest",
        // V-07: TLS fingerprint headers (populated by reverse proxy / Cloudflare)
        "CF-JA3-Fingerprint",     // Cloudflare Bot Management
        "X-JA3-Fingerprint",      // Custom nginx/haproxy module
        "X-JA4-Fingerprint",      // JA4+ fingerprint (newer standard)
        "X-TLS-Version",          // TLS protocol version
        "X-TLS-Cipher"            // Negotiated cipher suite
    ];
    
    /// <summary>
    /// Captures all relevant data from an HTTP request.
    /// </summary>
    public TrackingData CaptureFromRequest(HttpRequest request)
    {
        var headers = request.Headers;
        var connection = request.HttpContext.Connection;
        var path = request.Path.ToString();
        
        // Parse CompanyID and PiXLID from path: /12345/1_SMART.GIF
        string? companyId = null;
        string? pixlId = null;
        var pathMatch = PathParseRegex().Match(path);
        if (pathMatch.Success)
        {
            companyId = pathMatch.Groups["client"].Value;
            pixlId = pathMatch.Groups["campaign"].Value;
        }
        
        // Build headers JSON directly - avoids Dictionary + JsonSerializer allocations
        var headersJson = BuildHeadersJson(headers);
        
        // Extract client IP from proxy headers (priority order) with fallback to connection IP
        var clientIp = ExtractClientIp(headers, connection);
        
        return new TrackingData
        {
            ReceivedAt = DateTime.UtcNow,
            CompanyID = companyId,
            PiXLID = pixlId,
            IPAddress = clientIp,
            RequestPath = path,
            QueryString = request.QueryString.ToString().TrimStart('?'),
            HeadersJson = headersJson,
            UserAgent = Truncate(headers.UserAgent.ToString(), 2000),
            Referer = Truncate(headers.Referer.ToString(), 2000)
        };
    }
    
    /// <summary>
    /// Extracts the real client IP from reverse proxy headers.
    /// Priority: Cloudflare > True-Client-IP > X-Real-IP > X-Forwarded-For > Connection
    /// </summary>
    private static string? ExtractClientIp(IHeaderDictionary headers, ConnectionInfo connection)
    {
        // Cloudflare-specific header (most reliable when using CF)
        var cfConnectingIp = headers["CF-Connecting-IP"].ToString();
        if (!string.IsNullOrEmpty(cfConnectingIp))
            return cfConnectingIp.Trim();
        
        // Akamai / some CDNs use True-Client-IP
        var trueClientIp = headers["True-Client-IP"].ToString();
        if (!string.IsNullOrEmpty(trueClientIp))
            return trueClientIp.Trim();
        
        // nginx/HAProxy typically use X-Real-IP
        var realIp = headers["X-Real-IP"].ToString();
        if (!string.IsNullOrEmpty(realIp))
            return realIp.Trim();
        
        // Standard proxy header - may contain chain: "client, proxy1, proxy2"
        var forwardedFor = headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            // Take the first (leftmost) IP - that's the original client
            var firstComma = forwardedFor.IndexOf(',');
            return firstComma > 0 
                ? forwardedFor[..firstComma].Trim() 
                : forwardedFor.Trim();
        }
        
        // Fallback to connection IP (direct connection or middleware-populated)
        return connection.RemoteIpAddress?.ToString();
    }
    
    private static string BuildHeadersJson(IHeaderDictionary headers)
    {
        var sb = new StringBuilder(512);
        sb.Append('{');
        var first = true;
        
        foreach (var key in HeaderKeysToCapture)
        {
            var value = headers[key].ToString();
            if (!string.IsNullOrEmpty(value))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(key).Append("\":\"");
                
                // Escape JSON special chars
                foreach (var c in value)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default: sb.Append(c); break;
                    }
                }
                sb.Append('"');
            }
        }
        sb.Append('}');
        return sb.ToString();
    }
    
    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? value : (value.Length <= maxLength ? value : value[..maxLength]);
}
