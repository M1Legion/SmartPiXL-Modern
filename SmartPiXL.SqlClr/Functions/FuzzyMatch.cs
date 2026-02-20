// ─────────────────────────────────────────────────────────────────────────────
// CLR Functions: JaroWinkler + Levenshtein distance
// Fuzzy string matching for near-duplicate UA detection and identity matching.
//
// Design doc note: "test vectors first" — if VECTOR_DISTANCE on UA vectors
// works well enough, these become secondary. But the infrastructure is here.
//
// Zero NuGet dependencies — pure algorithmic implementation. The Forge uses
// FuzzySharp for real-time matching, but CLR assemblies must be self-contained.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Data.SqlTypes;
using Microsoft.SqlServer.Server;

namespace SmartPiXL.SqlClr.Functions;

public static class FuzzyMatch
{
    // ════════════════════════════════════════════════════════════════════════
    // Jaro-Winkler Distance (0.0 = completely different, 1.0 = identical)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes the Jaro-Winkler similarity between two strings.
    /// Returns a value between 0.0 (no similarity) and 1.0 (identical).
    /// <example>
    /// <c>SELECT dbo.JaroWinkler('AppleWebKit/537.36', 'AppleWebKit/537.37')</c> → ~0.98
    /// </example>
    /// </summary>
    [SqlFunction(
        IsDeterministic = true,
        IsPrecise = false,
        DataAccess = DataAccessKind.None,
        SystemDataAccess = SystemDataAccessKind.None,
        Name = "JaroWinkler")]
    public static SqlDouble JaroWinkler(SqlString s1, SqlString s2)
    {
        if (s1.IsNull || s2.IsNull)
            return SqlDouble.Null;

        var a = s1.Value;
        var b = s2.Value;

        if (a.Length == 0 && b.Length == 0)
            return new SqlDouble(1.0);
        if (a.Length == 0 || b.Length == 0)
            return new SqlDouble(0.0);

        var jaro = ComputeJaro(a, b);

        // Winkler prefix bonus: up to 4 matching prefix characters
        var prefixLen = 0;
        var maxPrefix = Math.Min(4, Math.Min(a.Length, b.Length));
        for (var i = 0; i < maxPrefix; i++)
        {
            if (a[i] == b[i])
                prefixLen++;
            else
                break;
        }

        // Standard Winkler scaling factor: p = 0.1
        const double p = 0.1;
        return new SqlDouble(jaro + prefixLen * p * (1.0 - jaro));
    }

    private static double ComputeJaro(string a, string b)
    {
        var maxDist = Math.Max(a.Length, b.Length) / 2 - 1;
        if (maxDist < 0) maxDist = 0;

        var aMatched = new bool[a.Length];
        var bMatched = new bool[b.Length];

        var matches = 0;
        var transpositions = 0;

        // Count matches within the matching window
        for (var i = 0; i < a.Length; i++)
        {
            var start = Math.Max(0, i - maxDist);
            var end = Math.Min(i + maxDist + 1, b.Length);

            for (var j = start; j < end; j++)
            {
                if (bMatched[j] || a[i] != b[j])
                    continue;

                aMatched[i] = true;
                bMatched[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0)
            return 0.0;

        // Count transpositions (matched chars in different order)
        var k = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (!aMatched[i])
                continue;

            while (!bMatched[k]) k++;

            if (a[i] != b[k])
                transpositions++;

            k++;
        }

        return ((double)matches / a.Length
              + (double)matches / b.Length
              + (double)(matches - transpositions / 2.0) / matches)
              / 3.0;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Levenshtein Distance (edit distance — lower = more similar)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes the Levenshtein edit distance between two strings.
    /// Returns the number of single-character edits (inserts, deletions,
    /// substitutions) required to transform one string into the other.
    /// <example>
    /// <c>SELECT dbo.LevenshteinDistance('kitten', 'sitting')</c> → <c>3</c>
    /// </example>
    /// </summary>
    [SqlFunction(
        IsDeterministic = true,
        IsPrecise = true,
        DataAccess = DataAccessKind.None,
        SystemDataAccess = SystemDataAccessKind.None,
        Name = "LevenshteinDistance")]
    public static SqlInt32 LevenshteinDistance(SqlString s1, SqlString s2)
    {
        if (s1.IsNull || s2.IsNull)
            return SqlInt32.Null;

        var a = s1.Value;
        var b = s2.Value;

        if (a.Length == 0) return new SqlInt32(b.Length);
        if (b.Length == 0) return new SqlInt32(a.Length);

        // Space-optimized: only keep two rows instead of full matrix
        var prevRow = new int[b.Length + 1];
        var currRow = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
            prevRow[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            currRow[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                currRow[j] = Math.Min(
                    Math.Min(currRow[j - 1] + 1, prevRow[j] + 1),
                    prevRow[j - 1] + cost);
            }

            // Swap rows
            var temp = prevRow;
            prevRow = currRow;
            currRow = temp;
        }

        return new SqlInt32(prevRow[b.Length]);
    }
}
