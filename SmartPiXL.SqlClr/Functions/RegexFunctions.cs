// ─────────────────────────────────────────────────────────────────────────────
// CLR Functions: RegexExtract + RegexMatch
// SQL Server has LIKE and PATINDEX but NO regex. These unlock:
//   - Domain extraction from referrer URLs
//   - Email validation in MatchEmail
//   - Bot signal name parsing
//   - URL path pattern matching for funnel analysis
//
// Compiled regex with LRU caching for repeated patterns. Thread-safe.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Concurrent;
using System.Data.SqlTypes;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.Server;

namespace SmartPiXL.SqlClr.Functions;

public static class RegexFunctions
{
    // ── Compiled regex cache ─────────────────────────────────────────────
    // ConcurrentDictionary is thread-safe for SQL CLR's multi-threaded host.
    // Bounded to 256 entries to prevent unbounded memory growth.
    private static readonly ConcurrentDictionary<string, Regex> s_cache =
        new ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);

    private const int MaxCacheSize = 256;

    private static Regex GetOrAdd(string pattern)
    {
        if (s_cache.TryGetValue(pattern, out var cached))
            return cached;

        // Evict if over limit (simple clear — rare event)
        if (s_cache.Count >= MaxCacheSize)
            s_cache.Clear();

        var rx = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        s_cache.TryAdd(pattern, rx);
        return rx;
    }

    // ════════════════════════════════════════════════════════════════════════
    // RegexExtract — extract a capture group from a string
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extracts a regex capture group from the input string.
    /// Returns NULL if no match or if the group index is out of range.
    /// <example>
    /// <c>SELECT dbo.RegexExtract('https://example.com/path', '://([^/]+)', 1)</c>
    /// → <c>'example.com'</c>
    /// </example>
    /// </summary>
    [SqlFunction(
        IsDeterministic = true,
        IsPrecise = false,
        DataAccess = DataAccessKind.None,
        SystemDataAccess = SystemDataAccessKind.None,
        Name = "RegexExtract")]
    public static SqlString RegexExtract(SqlString input, SqlString pattern, SqlInt32 groupIndex)
    {
        if (input.IsNull || pattern.IsNull || groupIndex.IsNull)
            return SqlString.Null;

        try
        {
            var rx = GetOrAdd(pattern.Value);
            var match = rx.Match(input.Value);

            if (!match.Success)
                return SqlString.Null;

            var idx = groupIndex.Value;
            if (idx < 0 || idx >= match.Groups.Count)
                return SqlString.Null;

            var group = match.Groups[idx];
            return group.Success ? new SqlString(group.Value) : SqlString.Null;
        }
        catch
        {
            // Invalid regex pattern or timeout — return NULL rather than crash SQL
            return SqlString.Null;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // RegexMatch — test if a string matches a pattern
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns 1 if the input matches the regex pattern, 0 otherwise.
    /// <example>
    /// <c>SELECT dbo.RegexMatch('bot@crawl.com', '^[^@]+@[^@]+\.[^@]+$')</c>
    /// → <c>1</c>
    /// </example>
    /// </summary>
    [SqlFunction(
        IsDeterministic = true,
        IsPrecise = false,
        DataAccess = DataAccessKind.None,
        SystemDataAccess = SystemDataAccessKind.None,
        Name = "RegexMatch")]
    public static SqlBoolean RegexMatch(SqlString input, SqlString pattern)
    {
        if (input.IsNull || pattern.IsNull)
            return SqlBoolean.Null;

        try
        {
            var rx = GetOrAdd(pattern.Value);
            return rx.IsMatch(input.Value) ? SqlBoolean.True : SqlBoolean.False;
        }
        catch
        {
            return SqlBoolean.False;
        }
    }
}
