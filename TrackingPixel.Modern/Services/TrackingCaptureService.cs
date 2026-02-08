using System.Buffers;
using System.Runtime.CompilerServices;
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
    /// Uses Span-based comma search for X-Forwarded-For to avoid substring allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? ExtractClientIp(IHeaderDictionary headers, ConnectionInfo connection)
    {
        // Cloudflare-specific header (most reliable when using CF)
        var cfConnectingIp = headers["CF-Connecting-IP"].ToString();
        if (cfConnectingIp.Length > 0)
            return cfConnectingIp.Trim();
        
        // Akamai / some CDNs use True-Client-IP
        var trueClientIp = headers["True-Client-IP"].ToString();
        if (trueClientIp.Length > 0)
            return trueClientIp.Trim();
        
        // nginx/HAProxy typically use X-Real-IP
        var realIp = headers["X-Real-IP"].ToString();
        if (realIp.Length > 0)
            return realIp.Trim();
        
        // Standard proxy header - may contain chain: "client, proxy1, proxy2"
        // Span-based first-IP extraction: no substring alloc when no comma
        var forwardedFor = headers["X-Forwarded-For"].ToString();
        if (forwardedFor.Length > 0)
        {
            var span = forwardedFor.AsSpan();
            var firstComma = span.IndexOf(',');
            return firstComma > 0 
                ? span[..firstComma].Trim().ToString()
                : forwardedFor.Trim();
        }
        
        // Fallback to connection IP (direct connection or middleware-populated)
        return connection.RemoteIpAddress?.ToString();
    }
    
    // SIMD-vectorized search for JSON escape chars (AVX2/SSE on .NET 8+)
    // Scans 16-32 chars per cycle vs char-by-char switch
    private static readonly SearchValues<char> JsonEscapeChars =
        SearchValues.Create("\"\\\n\r\t");

    private static string BuildHeadersJson(IHeaderDictionary headers)
    {
        // Thread-local StringBuilder: zero alloc after first request per thread
        var sb = t_sb ??= new StringBuilder(512);
        sb.Clear();
        sb.Append('{');
        var first = true;

        foreach (var key in HeaderKeysToCapture)
        {
            var value = headers[key].ToString();
            if (value.Length > 0)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(key).Append("\":\"");
                AppendJsonEscaped(sb, value.AsSpan());
                sb.Append('"');
            }
        }
        sb.Append('}');
        return sb.ToString();
    }
    
    [ThreadStatic]
    private static StringBuilder? t_sb;

    /// <summary>
    /// Appends a string to StringBuilder with JSON-safe escaping.
    /// Uses SearchValues + ReadOnlySpan for SIMD-accelerated scanning.
    /// Typical header values contain zero special chars —
    /// entire string appended in one batch with no per-char branching.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendJsonEscaped(StringBuilder sb, ReadOnlySpan<char> value)
    {
        while (value.Length > 0)
        {
            var idx = value.IndexOfAny(JsonEscapeChars);
            if (idx < 0)
            {
                sb.Append(value);
                break;
            }
            if (idx > 0) sb.Append(value[..idx]);
            sb.Append(value[idx] switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                _ => "\\t"
            });
            value = value[(idx + 1)..];
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length == 0 ? value
        : value.Length <= maxLength ? value
        : value[..maxLength];
}
