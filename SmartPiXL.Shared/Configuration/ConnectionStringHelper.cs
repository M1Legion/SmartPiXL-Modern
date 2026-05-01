using Microsoft.Data.SqlClient;

namespace SmartPiXL.Configuration;

// ============================================================================
// CONNECTION STRING HELPER — Cross-process utility for connection-string
// transformations applied at startup.
//
// PURPOSE:
//   IIS app pool / Windows Service identities can't delegate Windows Auth
//   across the network (falls back to NT AUTHORITY\ANONYMOUS LOGON), so SQL
//   Auth is required for reliable cross-server connectivity (e.g., Xavier
//   at 192.168.88.35). When SQL_USERNAME / SQL_PASSWORD machine env vars
//   are present, both Edge and Forge call RewriteToSqlAuth at startup to
//   swap Integrated Security for SQL Auth.
// ============================================================================

/// <summary>
/// Helpers for applying environment-driven transformations to connection
/// strings at process startup.
/// </summary>
public static class ConnectionStringHelper
{
    /// <summary>
    /// Rewrites a connection string to use SQL Authentication with the
    /// supplied user/password, replacing any existing
    /// <c>Integrated Security</c> setting.
    /// <para>
    /// Returns <paramref name="connStr"/> unchanged when it is null or empty.
    /// </para>
    /// </summary>
    public static string? RewriteToSqlAuth(string? connStr, string user, string password)
    {
        if (string.IsNullOrEmpty(connStr)) return connStr;
        var csb = new SqlConnectionStringBuilder(connStr)
        {
            IntegratedSecurity = false,
            UserID = user,
            Password = password
        };
        return csb.ConnectionString;
    }
}
