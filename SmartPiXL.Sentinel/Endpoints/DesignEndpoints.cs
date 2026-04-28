using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Sentinel.Services;

namespace SmartPiXL.Sentinel.Endpoints;

// ============================================================================
// DESIGN TREE API — CRUD surface powering the /design blueprint editor.
// ----------------------------------------------------------------------------
// GET    /api/design/tree                       → every node + sibling tables
// POST   /api/design/node                       → create new node
// PATCH  /api/design/node/{id}                  → update any field (partial)
// POST   /api/design/node/{id}/position         → cheap drag-autosave (x,y only)
// POST   /api/design/node/{id}/reparent         → change ParentId (cycle-checked)
// DELETE /api/design/node/{id}                  → soft delete (Lifecycle='removed', IsActive=0)
//
// POST   /api/design/node/{id}/code-link        → add a file claim
// DELETE /api/design/code-link/{linkId}
//
// POST   /api/design/decision                   → add an architectural decision
// PATCH  /api/design/decision/{id}              → edit
//
// All writes pass the request through SentinelAccessControl (loopback / allowed IPs).
// ============================================================================

public static class DesignEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static void MapDesignEndpoints(this WebApplication app)
    {
        var cs = app.Services.GetRequiredService<IOptions<TrackingSettings>>().Value.ConnectionString!;

        // ---- SPA HTML ------------------------------------------------------
        app.MapGet("/design", async (HttpContext ctx, IWebHostEnvironment env) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            var path = Path.Combine(env.WebRootPath ?? "wwwroot", "design.html");
            if (!File.Exists(path))
                path = Path.Combine(env.ContentRootPath, "wwwroot", "design.html");
            if (File.Exists(path))
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                await ctx.Response.SendFileAsync(path);
            }
            else { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("Design editor not found."); }
        });

        // ---- READ ----------------------------------------------------------
        app.MapGet("/api/design/tree", async (HttpContext ctx, HealthTreeService tree) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            var payload = await LoadTreeAsync(cs);
            await WriteJson(ctx, payload);
            // Proactively clear HealthTreeService cache so newly-created probes show in the live tree.
            tree.InvalidateTreeStructure();
        });

        // ---- CREATE NODE ---------------------------------------------------
        app.MapPost("/api/design/node", async (HttpContext ctx, HealthTreeService tree) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            var body = await JsonSerializer.DeserializeAsync<NodeWriteDto>(ctx.Request.Body, JsonOptions);
            if (body is null || string.IsNullOrWhiteSpace(body.Slug) || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.NodeType))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("slug, name, nodeType required");
                return;
            }

            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Health.Node
                    (ParentId, Slug, Name, NodeType, Description, SortOrder, IsActive,
                     Lifecycle, [Owner], Icon, ProbeBinding, HealthRules,
                     DescMarketing, DescManagement, DescDeveloper, LayoutX, LayoutY)
                OUTPUT INSERTED.NodeId
                VALUES (@p, @slug, @name, @type, @desc, @sort, 1,
                        COALESCE(@life,'active'), @owner, @icon, @pb, @hr,
                        @dm, @dg, @dv, @x, @y);";
            AddNodeParams(cmd, body);
            var id = (int)(await cmd.ExecuteScalarAsync())!;
            tree.InvalidateTreeStructure();
            await WriteJson(ctx, new { nodeId = id });
        });

        // ---- UPDATE NODE ---------------------------------------------------
        app.MapMethods("/api/design/node/{id:int}", new[] { "PATCH" }, async (HttpContext ctx, int id, HealthTreeService tree) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            var body = await JsonSerializer.DeserializeAsync<NodeWriteDto>(ctx.Request.Body, JsonOptions);
            if (body is null) { ctx.Response.StatusCode = 400; return; }

            // Dynamic SET list — only columns actually present in the payload.
            var sets = new List<string>();
            var parms = new List<(string, object?)>();
            void Add(string col, string p, object? v) { sets.Add($"{col}=@{p}"); parms.Add((p, v ?? DBNull.Value)); }
            if (body.Slug is not null)            Add("Slug",           "slug",  body.Slug);
            if (body.Name is not null)            Add("Name",           "name",  body.Name);
            if (body.NodeType is not null)        Add("NodeType",       "type",  body.NodeType);
            if (body.Description is not null)     Add("Description",    "desc",  body.Description);
            if (body.SortOrder.HasValue)          Add("SortOrder",      "sort",  body.SortOrder.Value);
            if (body.IsActive.HasValue)           Add("IsActive",       "act",   body.IsActive.Value ? 1 : 0);
            if (body.Lifecycle is not null)       Add("Lifecycle",      "life",  body.Lifecycle);
            if (body.Owner is not null)           Add("[Owner]",        "owner", body.Owner);
            if (body.Icon is not null)            Add("Icon",           "icon",  body.Icon);
            if (body.ProbeBinding is not null)    Add("ProbeBinding",   "pb",    body.ProbeBinding);
            if (body.HealthRules is not null)     Add("HealthRules",    "hr",    body.HealthRules);
            if (body.DescMarketing is not null)   Add("DescMarketing",  "dm",    body.DescMarketing);
            if (body.DescManagement is not null)  Add("DescManagement", "dg",    body.DescManagement);
            if (body.DescDeveloper is not null)   Add("DescDeveloper",  "dv",    body.DescDeveloper);
            if (body.LayoutX.HasValue)            Add("LayoutX",        "x",     body.LayoutX.Value);
            if (body.LayoutY.HasValue)            Add("LayoutY",        "y",     body.LayoutY.Value);
            if (sets.Count == 0) { ctx.Response.StatusCode = 204; return; }

            sets.Add("UpdatedAt=SYSUTCDATETIME()");
            var sql = $"UPDATE Health.Node SET {string.Join(",", sets)} WHERE NodeId=@id;";
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@id", id);
            foreach (var (p, v) in parms) cmd.Parameters.AddWithValue("@" + p, v);
            var rows = await cmd.ExecuteNonQueryAsync();
            tree.InvalidateTreeStructure();
            ctx.Response.StatusCode = rows > 0 ? 204 : 404;
        });

        // ---- LIGHTWEIGHT POSITION SAVE (drag autosave) ---------------------
        app.MapPost("/api/design/node/{id:int}/position", async (HttpContext ctx, int id) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            var body = await JsonSerializer.DeserializeAsync<PositionDto>(ctx.Request.Body, JsonOptions);
            if (body is null) { ctx.Response.StatusCode = 400; return; }
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Health.Node SET LayoutX=@x, LayoutY=@y, UpdatedAt=SYSUTCDATETIME() WHERE NodeId=@id;";
            cmd.Parameters.AddWithValue("@x", body.X);
            cmd.Parameters.AddWithValue("@y", body.Y);
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
            ctx.Response.StatusCode = 204;
        });

        // ---- REPARENT ------------------------------------------------------
        app.MapPost("/api/design/node/{id:int}/reparent", async (HttpContext ctx, int id, HealthTreeService tree) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            var body = await JsonSerializer.DeserializeAsync<ReparentDto>(ctx.Request.Body, JsonOptions);
            if (body is null) { ctx.Response.StatusCode = 400; return; }

            if (body.NewParentId.HasValue && await WouldCreateCycleAsync(cs, id, body.NewParentId.Value))
            {
                ctx.Response.StatusCode = 409;
                await ctx.Response.WriteAsync("cycle detected");
                return;
            }

            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Health.Node SET ParentId=@p, UpdatedAt=SYSUTCDATETIME() WHERE NodeId=@id;";
            cmd.Parameters.AddWithValue("@p", (object?)body.NewParentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@id", id);
            var rows = await cmd.ExecuteNonQueryAsync();
            tree.InvalidateTreeStructure();
            ctx.Response.StatusCode = rows > 0 ? 204 : 404;
        });

        // ---- SOFT DELETE ---------------------------------------------------
        app.MapDelete("/api/design/node/{id:int}", async (HttpContext ctx, int id, HealthTreeService tree) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            // Block delete if node has active children.
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM Health.Node WHERE ParentId=@id AND IsActive=1;";
            check.Parameters.AddWithValue("@id", id);
            var kids = (int)(await check.ExecuteScalarAsync())!;
            if (kids > 0)
            {
                ctx.Response.StatusCode = 409;
                await ctx.Response.WriteAsync($"{kids} active children — remove or reparent them first");
                return;
            }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Health.Node SET IsActive=0, Lifecycle='removed', UpdatedAt=SYSUTCDATETIME() WHERE NodeId=@id;";
            cmd.Parameters.AddWithValue("@id", id);
            var rows = await cmd.ExecuteNonQueryAsync();
            tree.InvalidateTreeStructure();
            ctx.Response.StatusCode = rows > 0 ? 204 : 404;
        });

        // ---- CODE LINKS ----------------------------------------------------
        app.MapPost("/api/design/node/{id:int}/code-link", async (HttpContext ctx, int id) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            var body = await JsonSerializer.DeserializeAsync<CodeLinkDto>(ctx.Request.Body, JsonOptions);
            if (body is null || string.IsNullOrWhiteSpace(body.Path) || string.IsNullOrWhiteSpace(body.Kind))
            {
                ctx.Response.StatusCode = 400; return;
            }
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Design.CodeLink (NodeId, Kind, [Path], IsPrimary, Notes)
                OUTPUT INSERTED.CodeLinkId
                VALUES (@id, @kind, @path, @primary, @notes);";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@kind", body.Kind);
            cmd.Parameters.AddWithValue("@path", body.Path);
            cmd.Parameters.AddWithValue("@primary", body.IsPrimary ? 1 : 0);
            cmd.Parameters.AddWithValue("@notes", (object?)body.Notes ?? DBNull.Value);
            var linkId = (int)(await cmd.ExecuteScalarAsync())!;
            await WriteJson(ctx, new { codeLinkId = linkId });
        });

        app.MapDelete("/api/design/code-link/{linkId:int}", async (HttpContext ctx, int linkId) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Design.CodeLink WHERE CodeLinkId=@id;";
            cmd.Parameters.AddWithValue("@id", linkId);
            var rows = await cmd.ExecuteNonQueryAsync();
            ctx.Response.StatusCode = rows > 0 ? 204 : 404;
        });

        // ---- DECISIONS -----------------------------------------------------
        app.MapPost("/api/design/decision", async (HttpContext ctx) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            var body = await JsonSerializer.DeserializeAsync<DecisionDto>(ctx.Request.Body, JsonOptions);
            if (body is null || string.IsNullOrWhiteSpace(body.Slug) || string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Decision))
            {
                ctx.Response.StatusCode = 400; return;
            }
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Design.Decision (NodeId, Slug, Title, Decision, Rationale, Rejected, Status)
                OUTPUT INSERTED.DecisionId
                VALUES (@node, @slug, @title, @dec, @rat, @rej, COALESCE(@st,'locked'));";
            cmd.Parameters.AddWithValue("@node",  (object?)body.NodeId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@slug",  body.Slug);
            cmd.Parameters.AddWithValue("@title", body.Title);
            cmd.Parameters.AddWithValue("@dec",   body.Decision);
            cmd.Parameters.AddWithValue("@rat",   (object?)body.Rationale ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@rej",   (object?)body.Rejected  ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@st",    (object?)body.Status    ?? DBNull.Value);
            var id = (int)(await cmd.ExecuteScalarAsync())!;
            await WriteJson(ctx, new { decisionId = id });
        });

        app.MapMethods("/api/design/decision/{id:int}", new[] { "PATCH" }, async (HttpContext ctx, int id) =>
        {
            if (!SentinelAccessControl.IsAllowed(ctx)) return;
            var body = await JsonSerializer.DeserializeAsync<DecisionDto>(ctx.Request.Body, JsonOptions);
            if (body is null) { ctx.Response.StatusCode = 400; return; }
            var sets = new List<string>();
            var parms = new List<(string, object?)>();
            void Add(string c, string p, object? v) { sets.Add($"{c}=@{p}"); parms.Add((p, v ?? DBNull.Value)); }
            if (body.NodeId.HasValue)       Add("NodeId",    "node",  body.NodeId.Value);
            if (body.Title is not null)     Add("Title",     "title", body.Title);
            if (body.Decision is not null)  Add("Decision",  "dec",   body.Decision);
            if (body.Rationale is not null) Add("Rationale", "rat",   body.Rationale);
            if (body.Rejected is not null)  Add("Rejected",  "rej",   body.Rejected);
            if (body.Status is not null)    Add("Status",    "st",    body.Status);
            if (sets.Count == 0) { ctx.Response.StatusCode = 204; return; }
            sets.Add("UpdatedAt=SYSUTCDATETIME()");
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE Design.Decision SET {string.Join(",", sets)} WHERE DecisionId=@id;";
            cmd.Parameters.AddWithValue("@id", id);
            foreach (var (p, v) in parms) cmd.Parameters.AddWithValue("@" + p, v);
            var rows = await cmd.ExecuteNonQueryAsync();
            ctx.Response.StatusCode = rows > 0 ? 204 : 404;
        });
    }

    // ------------------------------------------------------------------
    // HELPERS
    // ------------------------------------------------------------------

    private static async Task<object> LoadTreeAsync(string cs)
    {
        var nodes = new List<Dictionary<string, object?>>();
        var codeLinks = new List<Dictionary<string, object?>>();
        var decisions = new List<Dictionary<string, object?>>();

        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT NodeId, ParentId, Slug, Name, NodeType, Description, SortOrder, IsActive,
                       Lifecycle, [Owner], Icon, ProbeBinding, HealthRules,
                       DescMarketing, DescManagement, DescDeveloper,
                       LayoutX, LayoutY, CreatedAt, UpdatedAt
                FROM Health.Node
                ORDER BY NodeId;";
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                nodes.Add(new Dictionary<string, object?>
                {
                    ["nodeId"]         = rdr.GetInt32(0),
                    ["parentId"]       = rdr.IsDBNull(1) ? null : rdr.GetInt32(1),
                    ["slug"]           = rdr.GetString(2),
                    ["name"]           = rdr.GetString(3),
                    ["nodeType"]       = rdr.GetString(4),
                    ["description"]    = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    ["sortOrder"]      = rdr.GetInt32(6),
                    ["isActive"]       = rdr.GetBoolean(7),
                    ["lifecycle"]      = rdr.GetString(8),
                    ["owner"]          = rdr.IsDBNull(9)  ? null : rdr.GetString(9),
                    ["icon"]           = rdr.IsDBNull(10) ? null : rdr.GetString(10),
                    ["probeBinding"]   = rdr.IsDBNull(11) ? null : rdr.GetString(11),
                    ["healthRules"]    = rdr.IsDBNull(12) ? null : rdr.GetString(12),
                    ["descMarketing"]  = rdr.IsDBNull(13) ? null : rdr.GetString(13),
                    ["descManagement"] = rdr.IsDBNull(14) ? null : rdr.GetString(14),
                    ["descDeveloper"]  = rdr.IsDBNull(15) ? null : rdr.GetString(15),
                    ["layoutX"]        = rdr.IsDBNull(16) ? null : (object)rdr.GetDouble(16),
                    ["layoutY"]        = rdr.IsDBNull(17) ? null : (object)rdr.GetDouble(17),
                    ["createdAt"]      = rdr.GetDateTime(18),
                    ["updatedAt"]      = rdr.GetDateTime(19)
                });
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT CodeLinkId, NodeId, Kind, [Path], IsPrimary, Notes FROM Design.CodeLink ORDER BY NodeId, CodeLinkId;";
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                codeLinks.Add(new Dictionary<string, object?>
                {
                    ["codeLinkId"] = rdr.GetInt32(0),
                    ["nodeId"]     = rdr.GetInt32(1),
                    ["kind"]       = rdr.GetString(2),
                    ["path"]       = rdr.GetString(3),
                    ["isPrimary"]  = rdr.GetBoolean(4),
                    ["notes"]      = rdr.IsDBNull(5) ? null : rdr.GetString(5)
                });
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT DecisionId, NodeId, Slug, Title, Decision, Rationale, Rejected, DecidedAt, Status, SupersededBy
                                FROM Design.Decision ORDER BY DecisionId;";
            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                decisions.Add(new Dictionary<string, object?>
                {
                    ["decisionId"]   = rdr.GetInt32(0),
                    ["nodeId"]       = rdr.IsDBNull(1) ? null : (object)rdr.GetInt32(1),
                    ["slug"]         = rdr.GetString(2),
                    ["title"]        = rdr.GetString(3),
                    ["decision"]     = rdr.GetString(4),
                    ["rationale"]    = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    ["rejected"]     = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                    ["decidedAt"]    = rdr.GetDateTime(7),
                    ["status"]       = rdr.GetString(8),
                    ["supersededBy"] = rdr.IsDBNull(9) ? null : (object)rdr.GetInt32(9)
                });
            }
        }

        return new { nodes, codeLinks, decisions };
    }

    private static async Task<bool> WouldCreateCycleAsync(string cs, int id, int newParentId)
    {
        if (id == newParentId) return true;
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        // Walk from newParent up to root, fail if we hit id.
        var cursor = (int?)newParentId;
        int safety = 0;
        while (cursor.HasValue && safety++ < 500)
        {
            if (cursor.Value == id) return true;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ParentId FROM Health.Node WHERE NodeId=@id;";
            cmd.Parameters.AddWithValue("@id", cursor.Value);
            var obj = await cmd.ExecuteScalarAsync();
            cursor = obj is null || obj is DBNull ? null : (int)obj;
        }
        return false;
    }

    private static void AddNodeParams(SqlCommand cmd, NodeWriteDto body)
    {
        cmd.Parameters.AddWithValue("@p",     (object?)body.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@slug",  body.Slug!);
        cmd.Parameters.AddWithValue("@name",  body.Name!);
        cmd.Parameters.AddWithValue("@type",  body.NodeType!);
        cmd.Parameters.AddWithValue("@desc",  (object?)body.Description    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sort",  body.SortOrder ?? 0);
        cmd.Parameters.AddWithValue("@life",  (object?)body.Lifecycle      ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@owner", (object?)body.Owner          ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@icon",  (object?)body.Icon           ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pb",    (object?)body.ProbeBinding   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hr",    (object?)body.HealthRules    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dm",    (object?)body.DescMarketing  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dg",    (object?)body.DescManagement ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dv",    (object?)body.DescDeveloper  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@x",     (object?)body.LayoutX        ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@y",     (object?)body.LayoutY        ?? DBNull.Value);
    }

    private static async Task WriteJson(HttpContext ctx, object payload)
    {
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, payload, JsonOptions);
    }

    // ---- DTOs --------------------------------------------------------------
    private sealed class NodeWriteDto
    {
        public int? ParentId { get; set; }
        public string? Slug { get; set; }
        public string? Name { get; set; }
        public string? NodeType { get; set; }
        public string? Description { get; set; }
        public int? SortOrder { get; set; }
        public bool? IsActive { get; set; }
        public string? Lifecycle { get; set; }
        public string? Owner { get; set; }
        public string? Icon { get; set; }
        public string? ProbeBinding { get; set; }
        public string? HealthRules { get; set; }
        public string? DescMarketing { get; set; }
        public string? DescManagement { get; set; }
        public string? DescDeveloper { get; set; }
        public double? LayoutX { get; set; }
        public double? LayoutY { get; set; }
    }

    private sealed class PositionDto { public double X { get; set; } public double Y { get; set; } }
    private sealed class ReparentDto { public int? NewParentId { get; set; } }
    private sealed class CodeLinkDto
    {
        public string? Kind { get; set; }
        public string? Path { get; set; }
        public bool IsPrimary { get; set; }
        public string? Notes { get; set; }
    }
    private sealed class DecisionDto
    {
        public int? NodeId { get; set; }
        public string? Slug { get; set; }
        public string? Title { get; set; }
        public string? Decision { get; set; }
        public string? Rationale { get; set; }
        public string? Rejected { get; set; }
        public string? Status { get; set; }
    }
}
