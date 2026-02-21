using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Services;

namespace SmartPiXL.Sentinel.Endpoints;

// ============================================================================
// TRAFFIC ALERT API — Visitor scoring + customer quality endpoints.
//
// ROUTE MAP:
//   /api/traffic-alert/visitors        →  vw_TrafficAlert_VisitorDetail (paginated)
//   /api/traffic-alert/visitors/{id}   →  Single visitor by VisitorScoreId
//   /api/traffic-alert/customers       →  vw_TrafficAlert_CustomerOverview
//   /api/traffic-alert/customers/{id}  →  Single customer by CompanyID
//   /api/traffic-alert/trend           →  vw_TrafficAlert_Trend (time-series)
//   /api/traffic-alert/summary         →  Aggregate KPI snapshot across all customers
//
// DESIGN:
//   - Localhost-restricted (same as Dashboard — operational data)
//   - All reads from materialized TrafficAlert views (no writes)
//   - Pagination on visitors (default 100, max 500)
//   - Customer filter via ?companyId=N query parameter
// ============================================================================

/// <summary>
/// TrafficAlert API endpoints — visitor scoring, customer quality grades,
/// and traffic quality trend data for the ops dashboard.
/// </summary>
public static class TrafficAlertEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static string _connectionString = null!;
    private static string[]? _allowedIps;
    private static ITrackingLogger _logger = null!;

    public static void MapTrafficAlertEndpoints(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<TrackingSettings>>().Value;
        _connectionString = settings.ConnectionString;
        _allowedIps = settings.DashboardAllowedIPs;
        _logger = app.Services.GetRequiredService<ITrackingLogger>();

        // ====================================================================
        // VISITOR DETAIL — Full scoring breakdown (paginated)
        // ====================================================================
        app.MapGet("/api/traffic-alert/visitors", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;

            int top = ParseInt(ctx, "top", 100, 1, 500);
            int offset = ParseInt(ctx, "offset", 0, 0, int.MaxValue);
            int? companyId = ParseOptionalInt(ctx, "companyId");
            string? bucket = ctx.Request.Query["bucket"].FirstOrDefault();

            var where = new List<string>();
            var parms = new List<SqlParameter>();

            if (companyId.HasValue)
            {
                where.Add("CompanyID = @CompanyID");
                parms.Add(new SqlParameter("@CompanyID", companyId.Value));
            }
            if (!string.IsNullOrEmpty(bucket))
            {
                where.Add("QualityBucket = @Bucket");
                parms.Add(new SqlParameter("@Bucket", bucket));
            }

            var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

            var rows = await QueryAsync($@"
                SELECT *
                FROM dbo.vw_TrafficAlert_VisitorDetail
                {whereClause}
                ORDER BY ReceivedAt DESC
                OFFSET @Offset ROWS FETCH NEXT @Top ROWS ONLY",
                [.. parms, new SqlParameter("@Offset", offset), new SqlParameter("@Top", top)]);

            await WriteJsonAsync(ctx, rows);
        });

        // ====================================================================
        // SINGLE VISITOR — By VisitorScoreId
        // ====================================================================
        app.MapGet("/api/traffic-alert/visitors/{id:long}", async (HttpContext ctx, long id) =>
        {
            if (!RequireLoopback(ctx)) return;

            var rows = await QueryAsync(@"
                SELECT *
                FROM dbo.vw_TrafficAlert_VisitorDetail
                WHERE VisitorScoreId = @Id",
                [new SqlParameter("@Id", id)]);

            if (rows.Count == 0)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("Visitor not found.");
                return;
            }
            await WriteJsonAsync(ctx, rows[0]);
        });

        // ====================================================================
        // CUSTOMER OVERVIEW — Per-customer summary with quality grades
        // ====================================================================
        app.MapGet("/api/traffic-alert/customers", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;

            string periodType = ctx.Request.Query["period"].FirstOrDefault() ?? "D";
            if (periodType is not ("D" or "W" or "M"))
                periodType = "D";

            var rows = await QueryAsync(@"
                SELECT *
                FROM dbo.vw_TrafficAlert_CustomerOverview
                WHERE PeriodType = @PeriodType
                ORDER BY TotalHits DESC",
                [new SqlParameter("@PeriodType", periodType)]);

            await WriteJsonAsync(ctx, rows);
        });

        // ====================================================================
        // SINGLE CUSTOMER — By CompanyID
        // ====================================================================
        app.MapGet("/api/traffic-alert/customers/{id:int}", async (HttpContext ctx, int id) =>
        {
            if (!RequireLoopback(ctx)) return;

            var rows = await QueryAsync(@"
                SELECT *
                FROM dbo.vw_TrafficAlert_CustomerOverview
                WHERE CompanyID = @Id
                ORDER BY PeriodStart DESC",
                [new SqlParameter("@Id", id)]);

            if (rows.Count == 0)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("Customer not found.");
                return;
            }
            await WriteJsonAsync(ctx, rows);
        });

        // ====================================================================
        // TREND — Time-series for charting (per-customer or all)
        // ====================================================================
        app.MapGet("/api/traffic-alert/trend", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;

            int? companyId = ParseOptionalInt(ctx, "companyId");
            string periodType = ctx.Request.Query["period"].FirstOrDefault() ?? "D";
            int days = ParseInt(ctx, "days", 30, 1, 365);

            var parms = new List<SqlParameter>
            {
                new("@PeriodType", periodType),
                new("@CutoffDate", DateTime.UtcNow.AddDays(-days))
            };

            string companyFilter = "";
            if (companyId.HasValue)
            {
                companyFilter = "AND CompanyID = @CompanyID";
                parms.Add(new SqlParameter("@CompanyID", companyId.Value));
            }

            var rows = await QueryAsync($@"
                SELECT *
                FROM dbo.vw_TrafficAlert_Trend
                WHERE PeriodType = @PeriodType
                  AND PeriodStart >= @CutoffDate
                  {companyFilter}
                ORDER BY CompanyID, PeriodStart",
                [.. parms]);

            await WriteJsonAsync(ctx, rows);
        });

        // ====================================================================
        // SUMMARY — Aggregate KPI snapshot across all customers
        // ====================================================================
        app.MapGet("/api/traffic-alert/summary", async (HttpContext ctx) =>
        {
            if (!RequireLoopback(ctx)) return;

            var rows = await QueryAsync(@"
                SELECT
                    COUNT(DISTINCT CompanyID)              AS CustomerCount,
                    SUM(TotalHits)                         AS TotalHits,
                    SUM(BotHits)                           AS TotalBotHits,
                    SUM(HumanHits)                         AS TotalHumanHits,
                    AVG(BotPercent)                         AS AvgBotPercent,
                    AVG(AvgCompositeQuality)                AS AvgCompositeQuality,
                    AVG(AvgLeadQuality)                     AS AvgLeadQuality,
                    AVG(AvgMouseAuthenticity)               AS AvgMouseAuthenticity,
                    SUM(UniqueDevices)                      AS TotalDevices,
                    SUM(UniqueIPs)                          AS TotalIPs,
                    AVG(DeadInternetIndex)                  AS AvgDeadInternetIndex,
                    SUM(CASE WHEN QualityGrade = 'A' THEN 1 ELSE 0 END) AS GradeA,
                    SUM(CASE WHEN QualityGrade = 'B' THEN 1 ELSE 0 END) AS GradeB,
                    SUM(CASE WHEN QualityGrade = 'C' THEN 1 ELSE 0 END) AS GradeC,
                    SUM(CASE WHEN QualityGrade = 'D' THEN 1 ELSE 0 END) AS GradeD,
                    SUM(CASE WHEN QualityGrade = 'F' THEN 1 ELSE 0 END) AS GradeF
                FROM dbo.vw_TrafficAlert_CustomerOverview
                WHERE PeriodType = 'D'
                  AND PeriodStart = (SELECT MAX(PeriodStart) FROM dbo.vw_TrafficAlert_CustomerOverview WHERE PeriodType = 'D')");

            await WriteJsonAsync(ctx, rows.Count > 0 ? rows[0] : new Dictionary<string, object?>());
        });

        _logger.Info("[TrafficAlert] API endpoints mapped: /api/traffic-alert/*");
    }

    // ========================================================================
    // ACCESS CONTROL — Same loopback + allowed-IP check as Dashboard.
    // ========================================================================
    private static bool RequireLoopback(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null)
        {
            ctx.Response.StatusCode = 404;
            return false;
        }

        if (IPAddress.IsLoopback(remote))
            return true;

        var ip = remote.MapToIPv4().ToString();
        if (_allowedIps is not null && Array.Exists(_allowedIps, a => a == ip))
            return true;

        ctx.Response.StatusCode = 404;
        return false;
    }

    // ========================================================================
    // SQL + JSON HELPERS
    // ========================================================================
    private static async Task<List<Dictionary<string, object?>>> QueryAsync(
        string sql, SqlParameter[]? parms = null)
    {
        var results = new List<Dictionary<string, object?>>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = 30;

        if (parms is not null)
            cmd.Parameters.AddRange(parms);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                row[reader.GetName(i)] = value == DBNull.Value ? null : value;
            }
            results.Add(row);
        }

        return results;
    }

    private static async Task WriteJsonAsync(HttpContext ctx, object data)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers.CacheControl = "no-cache";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, data, JsonOptions);
    }

    private static int ParseInt(HttpContext ctx, string name, int defaultVal, int min, int max)
    {
        if (ctx.Request.Query.TryGetValue(name, out var val) && int.TryParse(val, out var n))
            return Math.Clamp(n, min, max);
        return defaultVal;
    }

    private static int? ParseOptionalInt(HttpContext ctx, string name)
    {
        if (ctx.Request.Query.TryGetValue(name, out var val) && int.TryParse(val, out var n))
            return n;
        return null;
    }
}
