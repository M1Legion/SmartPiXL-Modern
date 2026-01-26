namespace TrackingPixel.Models;

/// <summary>
/// Captured tracking data from a pixel request.
/// Immutable record for efficient passing between services.
/// </summary>
public sealed record TrackingData
{
    public DateTime ReceivedAt { get; init; }
    public string? CompanyID { get; init; }
    public string? PiXLID { get; init; }
    public string? IPAddress { get; init; }
    public string? RequestPath { get; init; }
    public string? QueryString { get; init; }    // Full query string - SQL view parses all 90+ params
    public string? HeadersJson { get; init; }    // JSON blob of important headers
    public string? UserAgent { get; init; }      // Kept separate for quick access
    public string? Referer { get; init; }        // Kept separate for quick access
}
