using System.Net;
using SmartPiXL.Services;

namespace SmartPiXL.Sentinel;

// ============================================================================
// SENTINEL ACCESS CONTROL — Unified IP-based access gate for all restricted
// Sentinel endpoints (Dashboard, TrafficAlert, Remediation).
//
// DESIGN:
//   - Loopback (127.0.0.1, ::1): always allowed
//   - Local IP (server's own LAN IP via RDP): always allowed
//   - Configured IPs (Tracking:DashboardAllowedIPs): always allowed
//   - Everything else: 404 (not 403 — don't reveal the API exists)
//   - Null remote IP: fail-closed (deny)
//   - Pure IPv6: handled safely (no MapToIPv4 on non-mapped addresses)
//
// HISTORY:
//   Prior to this unification, Dashboard and TrafficAlert each had their own
//   RequireLoopback with 3 behavioral differences (BUG-S2/S3/S4/S5 in the
//   2026-02-25 Embedded QA report). This class replaces both.
// ============================================================================

/// <summary>
/// Centralized IP-based access control for Sentinel endpoints.
/// <para>
/// Allows loopback, the server's own LAN IP (for RDP sessions), and
/// explicitly configured IPs from <c>Tracking:DashboardAllowedIPs</c>.
/// Denies everything else with a silent 404.
/// </para>
/// </summary>
public static class SentinelAccessControl
{
    private static HashSet<IPAddress> _allowedIps = new();
    private static ITrackingLogger _logger = null!;

    /// <summary>
    /// Initializes access control with the allowed IP list from configuration.
    /// Called once during endpoint mapping in <c>Program.cs</c>.
    /// </summary>
    public static void Initialize(string[] allowedIpStrings, ITrackingLogger logger)
    {
        _logger = logger;
        _allowedIps = new HashSet<IPAddress>();

        foreach (var ipStr in allowedIpStrings)
        {
            if (IPAddress.TryParse(ipStr.Trim(), out var parsed))
            {
                _allowedIps.Add(parsed);
                _logger.Info($"[AccessControl] Allowed remote IP: {parsed}");
            }
            else
            {
                _logger.Warning($"[AccessControl] Could not parse allowed IP: '{ipStr}'");
            }
        }

        _logger.Info($"[AccessControl] Initialized — {_allowedIps.Count} allowed IP(s), loopback + local IP always permitted");
    }

    /// <summary>
    /// Checks whether the current request is from an allowed source.
    /// If denied, sets <c>ctx.Response.StatusCode = 404</c> and returns <c>false</c>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>Null remote IP → deny (fail-closed)</item>
    ///   <item>Loopback (127.*, ::1) → allow</item>
    ///   <item>Server's own LAN IP (remote == local) → allow (RDP sessions)</item>
    ///   <item>Configured allowed IPs → allow</item>
    ///   <item>IPv6-mapped-IPv4 → safely converted before comparison</item>
    ///   <item>Pure IPv6 → compared directly (no unsafe MapToIPv4 call)</item>
    /// </list>
    /// </remarks>
    public static bool IsAllowed(HttpContext ctx)
    {
        var remoteIp = ctx.Connection.RemoteIpAddress;

        // Fail-closed: if we can't determine the caller, deny.
        if (remoteIp is null)
        {
            ctx.Response.StatusCode = 404;
            return false;
        }

        // Loopback — always allowed (127.0.0.1, ::1).
        if (IPAddress.IsLoopback(remoteIp))
            return true;

        // Local IP equality — allows access when RDP'd into the server and
        // the request originates from the server's own LAN IP.
        var localIp = ctx.Connection.LocalIpAddress;
        if (localIp is not null && remoteIp.Equals(localIp))
            return true;

        // Normalize IPv6-mapped-IPv4 (e.g. ::ffff:192.168.88.176 → 192.168.88.176)
        // for comparison against the allowed IP set. Pure IPv6 addresses are
        // compared as-is — never call MapToIPv4() without this guard.
        var checkIp = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;
        if (_allowedIps.Contains(checkIp))
            return true;

        ctx.Response.StatusCode = 404;
        return false;
    }
}
