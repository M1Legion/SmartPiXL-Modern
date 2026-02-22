using System.Globalization;

namespace SmartPiXL.Forge.Services.Enrichments;

// ============================================================================
// QUERY PARAM READER — Span-based zero-allocation query string parameter
// extraction for the enrichment pipeline.
//
// Tier 2+ enrichments READ values from the query string (e.g., `gpu`,
// `cores`, `mem`, `sw`, `sh`, `plt`, `canvasFP`, `mouseEntropy`, `fontCount`).
// These are raw client-side params set by the PiXL Script.
//
// PERFORMANCE:
//   Core scanning uses ReadOnlySpan<char> slicing — zero heap allocation.
//   Old implementation: ~5 allocations per call (string concat, Substring,
//   Replace, UnescapeDataString). New: 0 allocations for GetInt/GetDouble/
//   GetBool, 0-1 for Get (only when returning a non-null string value).
//
//   Typical usage: 10+ calls per record on a 235-byte avg query string.
//   Old: 50+ allocs/record. New: 0-10 allocs/record (only Get returns).
// ============================================================================

/// <summary>
/// Extracts parameter values from a URL query string using Span-based scanning.
/// Zero-allocation for int/double/bool reads. Minimal allocation for string reads.
/// </summary>
internal static class QueryParamReader
{
    /// <summary>
    /// Gets the value of a query string parameter by name.
    /// Returns null if the parameter is not found.
    /// Only allocates a string on the return path (not during scanning).
    /// Fast-path bypasses <see cref="Uri.UnescapeDataString"/> when no
    /// percent-encoding or form-encoded spaces are present.
    /// </summary>
    /// <param name="queryString">The full query string (without leading '?').</param>
    /// <param name="paramName">The parameter name to search for (case-insensitive).</param>
    /// <returns>The decoded parameter value, or null if not found.</returns>
    public static string? Get(string? queryString, string paramName)
    {
        if (string.IsNullOrEmpty(queryString) || string.IsNullOrEmpty(paramName))
            return null;

        if (!TryGetSpan(queryString.AsSpan(), paramName.AsSpan(), out var valueSpan))
            return null;

        // Empty value (e.g., "gpu=&sw=1920") → return empty string, not null
        if (valueSpan.IsEmpty)
            return string.Empty;

        // Fast path: no URL encoding — materialize span directly (1 allocation)
        if (valueSpan.IndexOfAny('%', '+') < 0)
            return valueSpan.ToString();

        // Slow path: URL-encoded value — intermediate string for UnescapeDataString
        var raw = new string(valueSpan);
        return Uri.UnescapeDataString(raw.Replace('+', ' '));
    }

    /// <summary>
    /// Gets a query string parameter as an integer. Returns 0 if missing or not parseable.
    /// Zero allocation — parses directly from <see cref="ReadOnlySpan{T}"/>.
    /// </summary>
    public static int GetInt(string? queryString, string paramName)
    {
        if (string.IsNullOrEmpty(queryString))
            return 0;

        if (!TryGetSpan(queryString.AsSpan(), paramName.AsSpan(), out var span))
            return 0;
        return int.TryParse(span, out var result) ? result : 0;
    }

    /// <summary>
    /// Gets a query string parameter as a double. Returns 0.0 if missing or not parseable.
    /// Zero allocation — parses directly from <see cref="ReadOnlySpan{T}"/>.
    /// </summary>
    public static double GetDouble(string? queryString, string paramName)
    {
        if (string.IsNullOrEmpty(queryString))
            return 0.0;

        if (!TryGetSpan(queryString.AsSpan(), paramName.AsSpan(), out var span))
            return 0.0;
        return double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0.0;
    }

    /// <summary>
    /// Checks if a query string parameter exists and equals "1" (truthy bit flag).
    /// Zero allocation — single char comparison on span.
    /// </summary>
    public static bool GetBool(string? queryString, string paramName)
    {
        if (string.IsNullOrEmpty(queryString))
            return false;

        if (!TryGetSpan(queryString.AsSpan(), paramName.AsSpan(), out var span))
            return false;
        return span.Length == 1 && span[0] == '1';
    }

    /// <summary>
    /// Core scanning method. Writes the raw value span (not unescaped) to
    /// <paramref name="value"/> and returns <c>true</c> when the param exists
    /// (even with an empty value like <c>gpu=&amp;</c>).
    /// Zero allocation — all operations are span slicing and ordinal comparison.
    /// </summary>
    private static bool TryGetSpan(
        ReadOnlySpan<char> queryString, ReadOnlySpan<char> paramName,
        out ReadOnlySpan<char> value)
    {
        var pos = 0;
        while (pos < queryString.Length)
        {
            var remaining = queryString[pos..];

            // Find paramName in remaining text (case-insensitive)
            var idx = remaining.IndexOf(paramName, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                break;

            var absPos = pos + idx;

            // Must be at the start of the query string or preceded by '&'
            if (absPos > 0 && queryString[absPos - 1] != '&')
            {
                pos = absPos + paramName.Length;
                continue;
            }

            // Must be followed by '='
            var eqPos = absPos + paramName.Length;
            if (eqPos >= queryString.Length || queryString[eqPos] != '=')
            {
                pos = eqPos + 1;
                continue;
            }

            // Extract value: everything from after '=' to next '&' or end
            var valueStart = eqPos + 1;
            var valueSlice = queryString[valueStart..];
            var ampIdx = valueSlice.IndexOf('&');
            value = ampIdx >= 0 ? valueSlice[..ampIdx] : valueSlice;
            return true;
        }

        value = default;
        return false;
    }
}
