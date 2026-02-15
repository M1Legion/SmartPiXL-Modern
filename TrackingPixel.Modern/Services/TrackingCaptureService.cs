using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using TrackingPixel.Models;

namespace TrackingPixel.Services;

// ============================================================================
// TRACKING CAPTURE SERVICE — Stateless HTTP request parser.
//
// HOT PATH: called once per pixel hit, so every allocation matters.
//
// DATA FLOW:
//   HttpRequest → CaptureFromRequest() → TrackingData record
//   (Request)      (parse + extract)       (queued for DB write)
//
// ALLOCATION STRATEGY:
//   • Source-generated regex: compiled IL at build time, no Regex ctor at runtime
//   • ThreadStatic StringBuilder: reused across requests on the same thread
//   • SearchValues<char> (SIMD): vectorized scan for JSON escape characters
//   • Span-based X-Forwarded-For parsing: avoids string.Split allocation
//   • Static header key array: shared immutable reference, never reallocated
//
// WHY NOT System.Text.Json FOR HEADERS?
//   JsonSerializer.Serialize(dict) would:
//   1. Allocate a Dictionary<string,string> to hold the key-value pairs
//   2. Allocate the JsonSerializer internal buffers
//   3. Produce escaped output we don't control
//   Our manual StringBuilder approach avoids #1 and #2 entirely, and the SIMD
//   SearchValues scan for escape chars handles #3 faster than the serializer.
//
// THREAD SAFETY:
//   The service is registered as Singleton but is completely stateless —
//   all mutable state is in [ThreadStatic] fields (one StringBuilder per thread).
//   No locking required.
// ============================================================================

/// <summary>
/// Stateless HTTP request parser that extracts tracking data with minimal allocations.
/// <para>
/// Registered as a Singleton in DI. All instance methods are safe to call concurrently
/// because the only mutable state is thread-local (<c>[ThreadStatic]</c> StringBuilder).
/// </para>
/// </summary>
public sealed partial class TrackingCaptureService
{
    /// <summary>
    /// Source-generated regex for extracting CompanyID and PiXLID from the URL path.
    /// <para>
    /// Pattern: <c>^/?{companyId}/{pixlId}</c> where companyId = everything before the first slash,
    /// pixlId = everything after the slash up to the first underscore.
    /// Example: <c>/ACME/summer2025_SMART.GIF</c> → companyId="ACME", pixlId="summer2025"
    /// </para>
    /// <para>
    /// Source-generated at compile time → no Regex constructor overhead at runtime,
    /// AOT-compatible, and the JIT can inline the matching IL.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"^/?(?<companyId>[^/]+)/(?<pixlId>[^_]+)")]
    private static partial Regex PathParseRegex();
    
    /// <summary>
    /// Static array of HTTP header names to capture from each request.
    /// <para>
    /// Organized into functional groups:
    /// <list type="bullet">
    ///   <item><description>Standard browser headers (User-Agent, Referer, Accept-Language, DNT)</description></item>
    ///   <item><description>Reverse proxy IP headers (X-Forwarded-For, X-Real-IP, CF-Connecting-IP, True-Client-IP)</description></item>
    ///   <item><description>Client Hints (Sec-CH-UA-*) — modern browsers send these for fingerprinting</description></item>
    ///   <item><description>Fetch metadata (Sec-Fetch-*) — reveals navigation context</description></item>
    ///   <item><description>TLS fingerprint headers (V-07) — JA3/JA4 hashes from reverse proxy or Cloudflare</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// This array is allocated once and shared across all threads. The header names are
    /// string literals interned by the runtime, so no per-request allocation occurs.
    /// </para>
    /// </summary>
    private static readonly string[] HeaderKeysToCapture =
    [
        // Standard browser headers — present in virtually all requests
        "User-Agent", "Referer", "Accept-Language", "DNT",
        
        // Reverse proxy IP identification — IIS-set headers only.
        // CF-Connecting-IP, True-Client-IP, X-Real-IP NOT captured: no CDN in front,
        // so any client can inject those headers to spoof their identity.
        "X-Forwarded-For",
        
        // Client Hints (User-Agent) — Chrome 89+, Edge 89+, Opera 75+
        // These replace the User-Agent string with structured, lower-entropy data
        "Sec-CH-UA",                   // Brand list: "Chromium";v="120", "Google Chrome";v="120"
        "Sec-CH-UA-Platform",          // OS name: "Windows", "macOS", "Android"
        "Sec-CH-UA-Mobile",            // Boolean: ?0 (desktop) or ?1 (mobile)
        "Sec-CH-UA-Model",             // Device model (mobile only, often empty)
        "Sec-CH-UA-Platform-Version",  // OS version: "15.0.0" (requires Permissions-Policy)
        "Sec-CH-UA-Arch",              // CPU arch: "x86", "arm" (requires Permissions-Policy)
        "Sec-CH-UA-Bitness",           // "64" or "32" (requires Permissions-Policy)
        
        // Fetch Metadata — reveals how the request was initiated
        "Sec-Fetch-Site",              // "cross-site", "same-origin", "none"
        "Sec-Fetch-Mode",              // "navigate", "no-cors", "cors"
        "Sec-Fetch-Dest",              // "image", "document", "empty"
        
        // V-07: TLS fingerprint headers (populated by reverse proxy / Cloudflare)
        // These identify the client's TLS implementation, which is very hard to spoof
        "CF-JA3-Fingerprint",          // Cloudflare Bot Management JA3 hash
        "X-JA3-Fingerprint",           // Custom nginx/haproxy module JA3 hash
        "X-JA4-Fingerprint",           // JA4+ fingerprint (newer, more granular standard)
        "X-TLS-Version",               // TLS protocol version (1.2, 1.3)
        "X-TLS-Cipher"                 // Negotiated cipher suite name
    ];
    
    /// <summary>
    /// Parses an HTTP request into a <see cref="TrackingData"/> record for database storage.
    /// <para>
    /// Extracts: CompanyID and PiXLID from the URL path, client IP from proxy headers,
    /// all tracked headers as a JSON string, and the full query string (which contains
    /// ~90 JavaScript-collected parameters from the pixel script).
    /// </para>
    /// </summary>
    /// <param name="request">The incoming HTTP request from the pixel hit or JS beacon.</param>
    /// <returns>A fully populated <see cref="TrackingData"/> record ready for queue insertion.</returns>
    public TrackingData CaptureFromRequest(HttpRequest request)
    {
        var headers = request.Headers;
        var connection = request.HttpContext.Connection;
        var path = request.Path.ToString();
        
        // Parse CompanyID and PiXLID from the URL path.
        // URL pattern: /{CompanyID}/{PiXLID}_SMART.GIF?...
        // Example: /ACME/summer2025_SMART.GIF → CompanyID="ACME", PiXLID="summer2025"
        string? companyId = null;
        string? pixlId = null;
        var pathMatch = PathParseRegex().Match(path);
        if (pathMatch.Success)
        {
            companyId = pathMatch.Groups["companyId"].Value;
            pixlId = pathMatch.Groups["pixlId"].Value;
        }
        
        // Build headers JSON manually to avoid Dictionary<string,string> + JsonSerializer allocations.
        // This is the single largest allocation in the hot path after the query string itself.
        var headersJson = BuildHeadersJson(headers);
        
        // Extract the real client IP, walking through reverse proxy headers in priority order.
        // The ForwardedHeaders middleware may have already set RemoteIpAddress, but we also
        // check CDN-specific headers that the middleware doesn't know about (CF-Connecting-IP).
        var clientIp = ExtractClientIp(headers, connection);
        
        return new TrackingData
        {
            ReceivedAt = DateTime.UtcNow,
            CompanyID = companyId,
            PiXLID = pixlId,
            IPAddress = clientIp,
            RequestPath = path,
            // TrimStart('?') removes the leading question mark so the stored value
            // starts directly with the first key=value pair (e.g., "canvasFP=abc&...")
            QueryString = request.QueryString.ToString().TrimStart('?'),
            HeadersJson = headersJson,
            // Truncate User-Agent and Referer to 2000 chars to match the SQL column size
            // (nvarchar(2000) in PiXL.Raw). Prevents SqlBulkCopy truncation errors.
            UserAgent = Truncate(headers.UserAgent.ToString(), 2000),
            Referer = Truncate(headers.Referer.ToString(), 2000)
        };
    }
    
    /// <summary>
    /// Extracts the real client IP from reverse proxy headers in priority order.
    /// <para>
    /// Priority chain (first non-empty wins):
    /// <list type="number">
    ///   <item><description><c>X-Forwarded-For</c> — Standard proxy header (first IP in chain, set by IIS)</description></item>
    ///   <item><description><c>RemoteIpAddress</c> — Direct TCP connection IP (fallback)</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// CDN-specific headers (<c>CF-Connecting-IP</c>, <c>True-Client-IP</c>) are NOT trusted
    /// because there is no CDN in front of IIS — any client could inject those headers
    /// directly, spoofing their IP address. Only <c>X-Forwarded-For</c> (set by IIS) and
    /// the TCP <c>RemoteIpAddress</c> are reliable in this deployment.
    /// </para>
    /// <para>
    /// For <c>X-Forwarded-For</c>, which may contain a chain like <c>"client, proxy1, proxy2"</c>,
    /// we extract only the first IP (the original client) using Span-based comma search
    /// to avoid a <c>string.Split</c> allocation.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? ExtractClientIp(IHeaderDictionary headers, ConnectionInfo connection)
    {
        // NOTE: CF-Connecting-IP and True-Client-IP are intentionally NOT checked here.
        // There is no Cloudflare/Akamai CDN in front of IIS, so those headers could be
        // injected by any client to spoof their IP. Only IIS-set headers are trusted.
        
        // Standard proxy header — may contain a chain: "client, proxy1, proxy2"
        // Use Span-based first-IP extraction: no substring allocation when no comma present
        var forwardedFor = headers["X-Forwarded-For"].ToString();
        if (forwardedFor.Length > 0)
        {
            var span = forwardedFor.AsSpan();
            var firstComma = span.IndexOf(',');
            // If there's a comma, take everything before it (the original client IP).
            // If no comma, the entire value IS the client IP.
            return firstComma > 0 
                ? span[..firstComma].Trim().ToString()
                : forwardedFor.Trim();
        }
        
        // Fallback: direct TCP connection IP (or IP set by ForwardedHeaders middleware)
        return connection.RemoteIpAddress?.ToString();
    }
    
    /// <summary>
    /// SIMD-vectorized character set for JSON escape detection.
    /// <para>
    /// <see cref="SearchValues{T}"/> compiles the character set into a hardware-accelerated
    /// lookup (AVX2 on x64, AdvSIMD on ARM) that scans 16–32 chars per CPU cycle.
    /// This is dramatically faster than a per-character <c>switch</c> statement for typical
    /// header values, which contain zero special characters — the entire string passes
    /// through in a single SIMD batch with no branching.
    /// </para>
    /// </summary>
    private static readonly SearchValues<char> JsonEscapeChars =
        SearchValues.Create("\"\\\n\r\t");

    /// <summary>
    /// Builds a JSON object string from tracked HTTP headers using a thread-local StringBuilder.
    /// <para>
    /// Output format: <c>{"User-Agent":"Mozilla/5.0...","Referer":"https://...","DNT":"1"}</c>
    /// </para>
    /// <para>
    /// Only includes headers that are present (non-empty). Skips Dictionary allocation
    /// and JsonSerializer overhead by writing directly to StringBuilder.
    /// </para>
    /// </summary>
    private static string BuildHeadersJson(IHeaderDictionary headers)
    {
        // Thread-local StringBuilder: zero allocation after the first request on each thread.
        // Initial capacity 512 is sized for typical header payloads (~300–400 chars).
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
                // Header keys are controlled string literals — no escaping needed.
                // Only values need JSON escaping (User-Agent can contain quotes, etc.)
                sb.Append('"').Append(key).Append("\":\"");
                AppendJsonEscaped(sb, value.AsSpan());
                sb.Append('"');
            }
        }
        sb.Append('}');
        return sb.ToString();
    }
    
    /// <summary>
    /// Thread-local StringBuilder reused across requests on the same thread.
    /// Eliminates StringBuilder construction + internal char[] allocation per request.
    /// </summary>
    [ThreadStatic]
    private static StringBuilder? t_sb;

    /// <summary>
    /// Appends a string value to a StringBuilder with JSON-safe escaping.
    /// <para>
    /// Uses <see cref="SearchValues{T}"/> + <see cref="ReadOnlySpan{T}"/> for SIMD-accelerated
    /// scanning. The algorithm works in chunks:
    /// <list type="number">
    ///   <item><description>SIMD scan finds the next special character position (or end of string)</description></item>
    ///   <item><description>Everything before that position is appended as a single span (bulk copy)</description></item>
    ///   <item><description>The special character is replaced with its JSON escape sequence</description></item>
    ///   <item><description>Advance past the special character and repeat</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// For typical header values with zero special characters, the entire string is
    /// appended in one <c>sb.Append(value)</c> call with no per-character branching.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendJsonEscaped(StringBuilder sb, ReadOnlySpan<char> value)
    {
        while (value.Length > 0)
        {
            // SIMD scan: finds first occurrence of any character in JsonEscapeChars
            var idx = value.IndexOfAny(JsonEscapeChars);
            if (idx < 0)
            {
                // No special chars remaining — append entire span in one shot
                sb.Append(value);
                break;
            }
            // Append everything before the special char as a bulk span copy
            if (idx > 0) sb.Append(value[..idx]);
            // Replace the special char with its JSON escape sequence
            sb.Append(value[idx] switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                _ => "\\t"  // Only remaining char in JsonEscapeChars is \t
            });
            // Advance past the escaped character
            value = value[(idx + 1)..];
        }
    }
    
    /// <summary>
    /// Truncates a string to the specified maximum length without allocation when
    /// the string is already within bounds. Returns <c>null</c> or empty strings as-is.
    /// Used to enforce SQL column width limits (nvarchar(2000) for UserAgent/Referer).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length == 0 ? value
        : value.Length <= maxLength ? value
        : value[..maxLength];
}
