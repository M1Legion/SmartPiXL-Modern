using System.Text.Json;
using SmartPiXL.Sentinel;
using SmartPiXL.Sentinel.Services;

namespace SmartPiXL.Sentinel.Endpoints;

// ============================================================================
// HEALTH TREE ENDPOINTS — API surface for the hierarchical health tree.
// ============================================================================
// GET /api/health-tree → full decorated tree (10s cached)
// POST /api/health-tree/invalidate → forces tree structure reload from SQL
// ============================================================================

public static class HealthTreeEndpoints
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static void MapHealthTreeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health-tree", async (HttpContext ctx, HealthTreeService healthTree) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx))
                return;

            var result = await healthTree.GetTreeAsync();
            ctx.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(ctx.Response.Body, result, s_jsonOptions);
        });

        app.MapPost("/api/health-tree/invalidate", (HttpContext ctx, HealthTreeService healthTree) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx))
                return;

            healthTree.InvalidateTreeStructure();
            ctx.Response.StatusCode = 204;
        });
    }
}
