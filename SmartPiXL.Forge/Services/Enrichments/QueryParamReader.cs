namespace SmartPiXL.Forge.Services.Enrichments;

// ============================================================================
// QUERY PARAM READER — Fast query string parameter extraction for the
// enrichment pipeline.
//
// Tier 2+ enrichments need to READ values from the query string (e.g., `gpu`,
// `cores`, `mem`, `sw`, `sh`, `plt`, `canvasFP`, `audioHash`, `mouseEntropy`,
// `fontCount`, `fpUniq`). These are raw client-side params set by the PiXL
// Script, plus _srv_* params appended by Tier 1 enrichments.
//
// This is NOT on the hot path (it's in the Forge, not the Edge), so
// string.Split is acceptable here for clarity. The query string is typically
// 2-8 KB and each enrichment reads only 2-5 params.
// ============================================================================

/// <summary>
/// Extracts parameter values from a URL query string.
/// Not hot-path — used by Forge enrichment services.
/// </summary>
internal static class QueryParamReader
{
    /// <summary>
    /// Gets the value of a query string parameter by name.
    /// Returns null if the parameter is not found.
    /// </summary>
    /// <param name="queryString">The full query string (without leading '?').</param>
    /// <param name="paramName">The parameter name to search for (case-insensitive).</param>
    /// <returns>The decoded parameter value, or null if not found.</returns>
    public static string? Get(string? queryString, string paramName)
    {
        if (string.IsNullOrEmpty(queryString) || string.IsNullOrEmpty(paramName))
            return null;

        // Search for &paramName= or starting paramName= (at position 0)
        var searchKey = paramName + "=";
        var startIndex = 0;

        while (startIndex < queryString.Length)
        {
            int keyPos;

            if (startIndex == 0)
            {
                // Check if QS starts with the param
                if (queryString.StartsWith(searchKey, StringComparison.OrdinalIgnoreCase))
                    keyPos = 0;
                else
                {
                    // Look for &paramName=
                    keyPos = queryString.IndexOf("&" + searchKey, startIndex, StringComparison.OrdinalIgnoreCase);
                    if (keyPos >= 0) keyPos++; // skip the &
                }
            }
            else
            {
                keyPos = queryString.IndexOf("&" + searchKey, startIndex, StringComparison.OrdinalIgnoreCase);
                if (keyPos >= 0) keyPos++; // skip the &
            }

            if (keyPos < 0)
                return null;

            var valueStart = keyPos + searchKey.Length;

            // Find end of value (next & or end of string)
            var valueEnd = queryString.IndexOf('&', valueStart);
            if (valueEnd < 0) valueEnd = queryString.Length;

            var value = queryString.Substring(valueStart, valueEnd - valueStart);
            // Decode both %xx sequences and + (form URL encoding for spaces)
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return null;
    }

    /// <summary>
    /// Gets a query string parameter as an integer. Returns 0 if missing or not parseable.
    /// </summary>
    public static int GetInt(string? queryString, string paramName)
    {
        var value = Get(queryString, paramName);
        if (value is null) return 0;
        return int.TryParse(value, out var result) ? result : 0;
    }

    /// <summary>
    /// Gets a query string parameter as a double. Returns 0.0 if missing or not parseable.
    /// </summary>
    public static double GetDouble(string? queryString, string paramName)
    {
        var value = Get(queryString, paramName);
        if (value is null) return 0.0;
        return double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0.0;
    }

    /// <summary>
    /// Checks if a query string parameter exists and equals "1" (truthy bit flag).
    /// </summary>
    public static bool GetBool(string? queryString, string paramName)
    {
        var value = Get(queryString, paramName);
        return value == "1";
    }
}
