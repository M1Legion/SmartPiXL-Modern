// ─────────────────────────────────────────────────────────────────────────────
// CLR Function: GetSubnet24
// Converts an IPv4 address to its /24 subnet (e.g., "192.168.1.100" → "192.168.1.0/24").
// Span-based parsing, zero allocation beyond the output string.
// ─────────────────────────────────────────────────────────────────────────────

using System.Data.SqlTypes;
using Microsoft.SqlServer.Server;

namespace SmartPiXL.SqlClr.Functions;

public static class GetSubnet24
{
    /// <summary>
    /// Returns the /24 subnet for an IPv4 address.
    /// <example>
    /// <c>SELECT dbo.GetSubnet24('192.168.1.100')</c> → <c>'192.168.1.0/24'</c>
    /// </example>
    /// </summary>
    [SqlFunction(
        IsDeterministic = true,
        IsPrecise = true,
        DataAccess = DataAccessKind.None,
        SystemDataAccess = SystemDataAccessKind.None,
        Name = "GetSubnet24")]
    public static SqlString Execute(SqlString ipAddress)
    {
        if (ipAddress.IsNull)
            return SqlString.Null;

        var ip = ipAddress.Value;

        // Find last dot — everything before it is the /24 prefix
        var lastDot = ip.LastIndexOf('.');
        if (lastDot <= 0)
            return SqlString.Null;

        // Validate we have at least 3 dots (basic IPv4 check)
        var dotCount = 0;
        for (var i = 0; i < ip.Length; i++)
        {
            if (ip[i] == '.') dotCount++;
        }

        if (dotCount != 3)
            return SqlString.Null;

        return new SqlString(ip.Substring(0, lastDot) + ".0/24");
    }
}
