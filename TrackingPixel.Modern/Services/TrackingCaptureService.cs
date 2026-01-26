using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using TrackingPixel.Models;

namespace TrackingPixel.Services;

/// <summary>
/// Captures tracking data from HTTP requests.
/// Stateless service - allocates minimally per request.
/// </summary>
public sealed class TrackingCaptureService
{
    // Pre-compiled regex for path parsing
    private static readonly Regex PathParseRegex = new(
        @"^/?(?<client>[^/]+)/(?<campaign>[^_]+)",
        RegexOptions.Compiled);
    
    // Static header keys - no allocation per request
    private static readonly string[] HeaderKeysToCapture =
    [
        "User-Agent", "Referer", "Accept-Language", "DNT",
        "X-Forwarded-For", "X-Real-IP", "CF-Connecting-IP", "True-Client-IP",
        "Sec-CH-UA", "Sec-CH-UA-Platform", "Sec-CH-UA-Mobile", "Sec-CH-UA-Model",
        "Sec-CH-UA-Platform-Version", "Sec-CH-UA-Arch", "Sec-CH-UA-Bitness",
        "Sec-Fetch-Site", "Sec-Fetch-Mode", "Sec-Fetch-Dest"
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
        var pathMatch = PathParseRegex.Match(path);
        if (pathMatch.Success)
        {
            companyId = pathMatch.Groups["client"].Value;
            pixlId = pathMatch.Groups["campaign"].Value;
        }
        
        // Build headers JSON directly - avoids Dictionary + JsonSerializer allocations
        var headersJson = BuildHeadersJson(headers);
        
        return new TrackingData
        {
            ReceivedAt = DateTime.UtcNow,
            CompanyID = companyId,
            PiXLID = pixlId,
            IPAddress = connection.RemoteIpAddress?.ToString(),
            RequestPath = path,
            QueryString = request.QueryString.ToString().TrimStart('?'),
            HeadersJson = headersJson,
            UserAgent = Truncate(headers.UserAgent.ToString(), 2000),
            Referer = Truncate(headers.Referer.ToString(), 2000)
        };
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
