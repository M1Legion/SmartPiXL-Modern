using Godot;
using System;
using System.Collections.Generic;

namespace TronDashboard;

/// <summary>
/// LightCycle — A single cycle on the arena grid. Follows TRON rules:
///   - Moves in cardinal directions only
///   - Leaves a permanent solid trail wall behind
///   - Dies on contact with ANY trail wall (own or others) or arena boundary
///   - On derezz, the trail wall dissolves/collapses into the floor
///
/// Trail walls are rendered as a single continuous ArrayMesh ribbon that
/// extends in real-time from the last committed turn point to the cycle's
/// current position — no visible rectangle segments.
/// </summary>
public sealed class LightCycle
{
    private readonly TronArena _arena;
    private readonly string _name;
    private readonly Color _color;
    private readonly bool _isAmbient;
    private readonly float _speed;

    // ── Movement ─────────────────────────────────────────────────────────
    private Vector3 _pos;
    private Vector2 _dir;        // (dx, dz) cardinal only — ±1 or 0
    private float _moveSpeed;    // world units per second
    private double _turnTimer;
    private double _nextTurn;
    private double _aiCooldown;  // minimum time between AI-driven turns

    // ── Trail geometry ───────────────────────────────────────────────────
    // Trail points are the corners of the path. The cycle always travels in
    // a straight line between turns. The LAST segment extends from
    // _trailPoints[^1] to _pos in real-time — no visible "drawing in".
    private readonly List<Vector3> _trailPoints = [];

    // ── Visual scene nodes ───────────────────────────────────────────────
    private readonly Node3D _bodyRoot;           // Parent for all cycle parts
    private readonly List<MeshInstance3D> _bodyParts = [];
    private readonly OmniLight3D _headLight;
    private readonly OmniLight3D _trailGlow;
    private readonly MeshInstance3D _trailRibbon;
    private static Shader? _trailShader;
    private readonly ShaderMaterial _trailMat;
    private readonly StandardMaterial3D _bodyMat;
    private readonly StandardMaterial3D _wheelMat;
    private readonly StandardMaterial3D _chassisMat;

    // ── Derezz state ─────────────────────────────────────────────────────
    private bool _derezzing;
    private double _derezzTimer;
    private const double DerezzDuration = 1.5;

    public bool IsAmbient => _isAmbient;
    public string Name => _name;
    public bool IsDerezzing => _derezzing;

    /// <summary>Trail corner points for collision testing.</summary>
    public IReadOnlyList<Vector3> TrailPoints => _trailPoints;
    public Vector3 CurrentPos => _pos;

    public LightCycle(
        TronArena arena, string name, Color color, bool isAmbient,
        Vector3 startPos, Vector2 dir, float speed)
    {
        _arena = arena;
        _name = name;
        _color = color;
        _isAmbient = isAmbient;
        _speed = speed;
        _moveSpeed = speed * 10.0f;
        _pos = startPos;
        _dir = new Vector2(Mathf.Sign(dir.X), Mathf.Sign(dir.Y));
        _nextTurn = 2.0 + GD.Randf() * 4.0;

        _pos.Y = 0;  // bodyRoot at ground level — children handle their own Y

        // ── Body materials ───────────────────────────────────────────
        _bodyMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(color, 0.95f),
            EmissionEnabled = true,
            Emission = color,
            EmissionEnergyMultiplier = isAmbient ? 1.5f : 3.5f,
            Metallic = 0.9f,
            Roughness = 0.08f,
        };

        // Wheels — slightly brighter emission to read as distinct circles
        _wheelMat = new StandardMaterial3D
        {
            AlbedoColor = new Color("080c14"),
            EmissionEnabled = true,
            Emission = color,
            EmissionEnergyMultiplier = isAmbient ? 2.0f : 4.5f,
            Metallic = 1.0f,
            Roughness = 0.05f,
        };

        // Chassis spine — dark metallic with subtle emission
        _chassisMat = new StandardMaterial3D
        {
            AlbedoColor = new Color("040810"),
            EmissionEnabled = true,
            Emission = color,
            EmissionEnergyMultiplier = isAmbient ? 0.8f : 1.8f,
            Metallic = 0.95f,
            Roughness = 0.06f,
        };

        // Scale factor — ambient cycles are 60% size
        // Scale: named cycles are 2.4m front-to-back, ambient slightly smaller.
        // At 75m camera distance with 55° FOV, 1px ≈ 0.05m, so a 2.4m cycle
        // is ~48px long — large enough for the silhouette to read.
        float s = isAmbient ? 0.85f : 1.5f;

        _bodyRoot = new Node3D { Position = _pos };
        arena.AddChild(_bodyRoot);

        // ────────────────────────────────────────────────────────────
        //  LIGHT CYCLE GEOMETRY — side profile (looking from +X):
        //
        //         ╲ rider (leaning forward)
        //     ○────╲────○
        //    rear       front
        //    wheel       wheel
        //
        //  All positions relative to _bodyRoot at ground level (Y=0).
        //  Forward is -Z. Wheels are torus rims only (open center).
        //  At camera distance (~75m), the cycle is ~30px wide — so
        //  the silhouette matters more than fine detail.
        // ────────────────────────────────────────────────────────────

        float wheelR = 0.35f * s;       // wheel outer radius
        float rimTube = 0.07f * s;      // torus tube radius — thick enough to read at distance
        float rearR = 0.40f * s;        // rear wheel slightly larger

        // ── Front wheel rim — glowing torus ring ────────────────────
        var frontRimMesh = new TorusMesh
        {
            InnerRadius = wheelR - rimTube,
            OuterRadius = wheelR + rimTube,
            Rings = 24,
            RingSegments = 12,
        };
        var frontRim = new MeshInstance3D
        {
            Mesh = frontRimMesh,
            MaterialOverride = _wheelMat,
        };
        frontRim.Position = new Vector3(0, wheelR, -0.95f * s);
        frontRim.RotateX(Mathf.DegToRad(90)); // stand upright (torus default is flat)
        _bodyRoot.AddChild(frontRim);
        _bodyParts.Add(frontRim);

        // ── Rear wheel rim ──────────────────────────────────────────
        var rearRimMesh = new TorusMesh
        {
            InnerRadius = rearR - rimTube,
            OuterRadius = rearR + rimTube,
            Rings = 24,
            RingSegments = 12,
        };
        var rearRim = new MeshInstance3D
        {
            Mesh = rearRimMesh,
            MaterialOverride = _wheelMat,
        };
        rearRim.Position = new Vector3(0, rearR, 0.65f * s);
        rearRim.RotateX(Mathf.DegToRad(90));
        _bodyRoot.AddChild(rearRim);
        _bodyParts.Add(rearRim);

        // ── Front axle hub — small sphere at wheel center ───────────
        float hubR = 0.06f * s;
        var frontHubMesh = new SphereMesh { Radius = hubR, Height = hubR * 2, RadialSegments = 8, Rings = 4 };
        var frontHub = new MeshInstance3D { Mesh = frontHubMesh, MaterialOverride = _bodyMat };
        frontHub.Position = new Vector3(0, wheelR, -0.95f * s);
        _bodyRoot.AddChild(frontHub);
        _bodyParts.Add(frontHub);

        var rearHubMesh = new SphereMesh { Radius = hubR, Height = hubR * 2, RadialSegments = 8, Rings = 4 };
        var rearHub = new MeshInstance3D { Mesh = rearHubMesh, MaterialOverride = _bodyMat };
        rearHub.Position = new Vector3(0, rearR, 0.65f * s);
        _bodyRoot.AddChild(rearHub);
        _bodyParts.Add(rearHub);

        // ── Chassis spine — long thin bar connecting wheel centers ───
        float spineLen = 1.55f * s;
        float spineH   = 0.08f * s;
        float spineW   = 0.12f * s;
        float spineY   = Mathf.Lerp(wheelR, rearR, 0.5f) * 0.55f; // slightly below axle line
        var spineMesh = new BoxMesh { Size = new Vector3(spineW, spineH, spineLen) };
        var spine = new MeshInstance3D { Mesh = spineMesh, MaterialOverride = _chassisMat };
        spine.Position = new Vector3(0, spineY, -0.15f * s);
        _bodyRoot.AddChild(spine);
        _bodyParts.Add(spine);

        // ── Upper chassis / fairing — thin wedge from headstock back to seat ──
        float fairingLen = 1.0f * s;
        float fairingH  = 0.06f * s;
        float fairingW  = spineW * 0.9f;
        float fairingY  = wheelR * 0.85f;
        var fairingMesh = new BoxMesh { Size = new Vector3(fairingW, fairingH, fairingLen) };
        var fairing = new MeshInstance3D { Mesh = fairingMesh, MaterialOverride = _chassisMat };
        fairing.Position = new Vector3(0, fairingY, -0.25f * s);
        _bodyRoot.AddChild(fairing);
        _bodyParts.Add(fairing);

        // ── Nose cone — small taper in front of fairing ─────────────
        float noseLen = 0.25f * s;
        var noseMesh = new BoxMesh { Size = new Vector3(fairingW * 0.5f, fairingH * 0.7f, noseLen) };
        var nose = new MeshInstance3D { Mesh = noseMesh, MaterialOverride = _bodyMat };
        nose.Position = new Vector3(0, fairingY + 0.02f * s, -0.8f * s);
        _bodyRoot.AddChild(nose);
        _bodyParts.Add(nose);

        // ── Rider — torso leaning forward aggressively ──────────────
        float riderH = 0.40f * s;
        float riderW = 0.08f * s;
        var riderMesh = new BoxMesh { Size = new Vector3(riderW, riderH, riderW * 1.5f) };
        var rider = new MeshInstance3D { Mesh = riderMesh, MaterialOverride = _chassisMat };
        rider.Position = new Vector3(0, wheelR + riderH * 0.25f, -0.05f * s);
        rider.RotateX(Mathf.DegToRad(-25)); // lean forward
        _bodyRoot.AddChild(rider);
        _bodyParts.Add(rider);

        // ── Rider head — small sphere ───────────────────────────────
        float headR = 0.07f * s;
        var headMesh = new SphereMesh { Radius = headR, Height = headR * 2, RadialSegments = 8, Rings = 6 };
        var head = new MeshInstance3D { Mesh = headMesh, MaterialOverride = _bodyMat };
        // Head position accounts for the forward lean
        head.Position = new Vector3(0, wheelR + riderH * 0.65f, -0.25f * s);
        _bodyRoot.AddChild(head);
        _bodyParts.Add(head);

        // ── Handlebar — thin wide bar at arm reach ──────────────────
        float barW = 0.28f * s;
        float barH = 0.03f * s;
        float barLen = 0.08f * s;
        var barMesh = new BoxMesh { Size = new Vector3(barW, barH, barLen) };
        var handlebar = new MeshInstance3D { Mesh = barMesh, MaterialOverride = _bodyMat };
        handlebar.Position = new Vector3(0, wheelR + riderH * 0.15f, -0.45f * s);
        _bodyRoot.AddChild(handlebar);
        _bodyParts.Add(handlebar);

        // ── Rear cowl — tapers down behind rider to rear wheel ──────
        float cowlLen = 0.50f * s;
        float cowlH   = 0.10f * s;
        var cowlMesh = new BoxMesh { Size = new Vector3(spineW * 1.1f, cowlH, cowlLen) };
        var cowl = new MeshInstance3D { Mesh = cowlMesh, MaterialOverride = _chassisMat };
        cowl.Position = new Vector3(0, rearR * 0.55f, 0.45f * s);
        cowl.RotateX(Mathf.DegToRad(-12)); // downward taper toward rear
        _bodyRoot.AddChild(cowl);
        _bodyParts.Add(cowl);

        OrientBody();

        // ── Headlight — hovers at cycle body height ─────────────────
        _headLight = new OmniLight3D
        {
            LightColor = color,
            LightEnergy = isAmbient ? 0.6f : 1.8f,
            OmniRange = isAmbient ? 10.0f : 22.0f,
            OmniAttenuation = 1.6f,
            ShadowEnabled = false,
            Position = _pos + new Vector3(0, 0.8f, 0),
        };
        arena.AddChild(_headLight);

        // ── Trail glow light ─────────────────────────────────────────
        _trailGlow = new OmniLight3D
        {
            LightColor = color,
            LightEnergy = isAmbient ? 0.2f : 0.6f,
            OmniRange = isAmbient ? 6.0f : 14.0f,
            OmniAttenuation = 2.0f,
            ShadowEnabled = false,
            Position = _pos + new Vector3(0, 1.0f, 0),
        };
        arena.AddChild(_trailGlow);

        // ── Trail ribbon ─────────────────────────────────────────────
        _trailShader ??= CreateTrailShader();
        _trailMat = new ShaderMaterial { Shader = _trailShader };
        _trailMat.SetShaderParameter("trail_color", new Vector3(color.R, color.G, color.B));
        _trailMat.SetShaderParameter("energy", isAmbient ? 1.5f : 3.0f);
        _trailMat.SetShaderParameter("collapse", 0.0f);

        _trailRibbon = new MeshInstance3D { MaterialOverride = _trailMat };
        arena.AddChild(_trailRibbon);

        // First trail point = spawn position on the floor
        _trailPoints.Add(new Vector3(_pos.X, 0, _pos.Z));
    }

    // ═════════════════════════════════════════════════════════════════════
    //  UPDATE
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Returns false when the cycle should be removed.</summary>
    public bool Update(double delta, double globalTime)
    {
        if (_derezzing)
            return UpdateDerezz(delta);

        // ── AI: look ahead for danger, turn if about to die ─────────
        _aiCooldown -= delta;
        if (_aiCooldown <= 0)
        {
            if (TryAvoidDeath())
                _aiCooldown = 0.3;
        }

        // ── Movement ─────────────────────────────────────────────────
        float dt = (float)delta;
        Vector3 newPos = _pos;
        newPos.X += _dir.X * _moveSpeed * dt;
        newPos.Z += _dir.Y * _moveSpeed * dt;

        // ── Arena boundary = death ───────────────────────────────────
        float halfX = _arena.GetArenaX() / 2 - 1.0f;
        float halfZ = _arena.GetArenaZ() / 2 - 1.0f;
        if (Mathf.Abs(newPos.X) > halfX || Mathf.Abs(newPos.Z) > halfZ)
        {
            newPos.X = Mathf.Clamp(newPos.X, -halfX, halfX);
            newPos.Z = Mathf.Clamp(newPos.Z, -halfZ, halfZ);
            _pos = newPos;
            StartDerezz();
            return true;
        }

        _pos = newPos;

        // ── Trail collision = death ──────────────────────────────────
        if (_arena.CheckTrailCollision(this, new Vector2(_pos.X, _pos.Z)))
        {
            StartDerezz();
            return true;
        }

        // ── Update visuals ───────────────────────────────────────────
        _bodyRoot.Position = _pos;
        _headLight.Position = _pos + new Vector3(0, 0.8f, 0);
        OrientBody();

        if (_trailPoints.Count >= 2)
            _trailGlow.Position = new Vector3(_trailPoints[^1].X, 1.0f, _trailPoints[^1].Z);
        else
            _trailGlow.Position = _pos + new Vector3(0, 1.0f, 0);

        // ── Turning ──────────────────────────────────────────────────
        _turnTimer += delta;
        if (_turnTimer >= _nextTurn)
        {
            _turnTimer = 0;
            _nextTurn = 2.0 + GD.Randf() * 4.0;
            _trailPoints.Add(new Vector3(_pos.X, 0, _pos.Z));
            Turn90();
        }

        // Rebuild ribbon every frame — live segment extends smoothly
        RebuildTrailRibbon();
        return true;
    }

    private bool UpdateDerezz(double delta)
    {
        _derezzTimer += delta;
        float t = (float)(_derezzTimer / DerezzDuration);
        if (t >= 1.0f) return false;

        _trailMat.SetShaderParameter("collapse", t);

        float fade = 1.0f - t;
        _bodyMat.AlbedoColor = new Color(_color, 0.95f * fade);
        _bodyMat.EmissionEnergyMultiplier = (_isAmbient ? 1.5f : 3.5f) * fade;
        _wheelMat.EmissionEnergyMultiplier = (_isAmbient ? 2.0f : 4.5f) * fade;
        _chassisMat.EmissionEnergyMultiplier = (_isAmbient ? 0.8f : 1.8f) * fade;
        _headLight.LightEnergy = (_isAmbient ? 0.6f : 1.8f) * fade;
        _trailGlow.LightEnergy = (_isAmbient ? 0.2f : 0.6f) * fade;
        _bodyRoot.Position = new Vector3(_pos.X, -0.3f * t, _pos.Z);  // sink into floor on derezz

        RebuildTrailRibbon();
        return true;
    }

    private void StartDerezz()
    {
        _derezzing = true;
        _derezzTimer = 0;
        _trailPoints.Add(new Vector3(_pos.X, 0, _pos.Z));
    }

    private void Turn90()
    {
        if (Mathf.Abs(_dir.X) > 0.5f)
            _dir = new Vector2(0, GD.Randf() > 0.5f ? 1 : -1);
        else
            _dir = new Vector2(GD.Randf() > 0.5f ? 1 : -1, 0);

        _moveSpeed = _speed * 10.0f * (0.9f + GD.Randf() * 0.2f);
    }

    private void OrientBody()
    {
        float angle = Mathf.Atan2(_dir.X, _dir.Y);
        _bodyRoot.Rotation = new Vector3(0, angle, 0);
    }

    // ═════════════════════════════════════════════════════════════════════
    //  TRAIL RIBBON — Single continuous wall, real-time live extension
    // ═════════════════════════════════════════════════════════════════════

    private void RebuildTrailRibbon()
    {
        int committed = _trailPoints.Count;
        bool addLive = !_derezzing && committed >= 1;
        int total = committed + (addLive ? 1 : 0);
        if (total < 2) return;

        var vertices = new List<Vector3>();
        var normals  = new List<Vector3>();
        var uvs      = new List<Vector2>();
        var indices  = new List<int>();

        float wallH = _arena.GetWallH();
        float halfThick = 0.08f;
        float cumDist = 0;

        for (int i = 0; i < total; i++)
        {
            // Get point — committed trail point or live cycle position
            Vector3 pt = (i < committed)
                ? _trailPoints[i]
                : new Vector3(_pos.X, 0, _pos.Z);

            // Cumulative distance for stable UVs
            if (i > 0)
            {
                Vector3 prev = (i - 1 < committed)
                    ? _trailPoints[i - 1]
                    : _trailPoints[committed - 1];
                if (i == committed && committed > 0)
                    prev = _trailPoints[committed - 1];
                cumDist += new Vector2(pt.X - prev.X, pt.Z - prev.Z).Length();
            }

            // Forward direction for wall orientation
            Vector3 forward;
            if (i < total - 1)
            {
                Vector3 next = (i + 1 < committed)
                    ? _trailPoints[i + 1]
                    : new Vector3(_pos.X, 0, _pos.Z);
                forward = next - pt;
                if (forward.LengthSquared() < 0.001f)
                    forward = new Vector3(_dir.X, 0, _dir.Y);
                forward = forward.Normalized();
            }
            else if (i > 0)
            {
                Vector3 prev = (i - 1 < committed)
                    ? _trailPoints[i - 1]
                    : _trailPoints[committed - 1];
                forward = pt - prev;
                if (forward.LengthSquared() < 0.001f)
                    forward = new Vector3(_dir.X, 0, _dir.Y);
                forward = forward.Normalized();
            }
            else
            {
                forward = new Vector3(_dir.X, 0, _dir.Y).Normalized();
            }

            // Wall perpendicular to movement
            var right = forward.Cross(Vector3.Up).Normalized() * halfThick;
            if (right.LengthSquared() < 0.0001f)
                right = Vector3.Right * halfThick;

            float u = cumDist;
            var n = right.Normalized();

            // 4 vertices per cross-section: BL, BR, TL, TR
            vertices.Add(pt - right);
            vertices.Add(pt + right);
            vertices.Add(pt - right + Vector3.Up * wallH);
            vertices.Add(pt + right + Vector3.Up * wallH);

            normals.Add(-n); normals.Add(n); normals.Add(-n); normals.Add(n);

            uvs.Add(new Vector2(u, 0)); uvs.Add(new Vector2(u, 0));
            uvs.Add(new Vector2(u, 1)); uvs.Add(new Vector2(u, 1));

            // Triangles — connect to next cross-section
            if (i < total - 1)
            {
                int b = i * 4;
                // Left face
                indices.Add(b); indices.Add(b + 4); indices.Add(b + 2);
                indices.Add(b + 2); indices.Add(b + 4); indices.Add(b + 6);
                // Right face
                indices.Add(b + 1); indices.Add(b + 3); indices.Add(b + 5);
                indices.Add(b + 3); indices.Add(b + 7); indices.Add(b + 5);
                // Top face
                indices.Add(b + 2); indices.Add(b + 6); indices.Add(b + 3);
                indices.Add(b + 3); indices.Add(b + 6); indices.Add(b + 7);
                // Bottom face
                indices.Add(b); indices.Add(b + 1); indices.Add(b + 4);
                indices.Add(b + 1); indices.Add(b + 5); indices.Add(b + 4);
            }
        }

        if (vertices.Count >= 8)
        {
            var arrayMesh = new ArrayMesh();
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
            arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            _trailRibbon.Mesh = arrayMesh;
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  TRAIL SHADER — Solid wall + collapse dissolve on derezz
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Hard-light wall — semi-opaque PBR surface with strong emission.
    /// In TRON Legacy, trail walls are solid-looking barriers with a bright
    /// white-hot core edge, colored body, and intense base glow where they
    /// meet the floor. They have physical presence — not transparent haze.
    /// On derezz: vertex collapse + noise discard + flare.
    /// </summary>
    private static Shader CreateTrailShader()
    {
        var shader = new Shader();
        shader.Code = @"
shader_type spatial;
render_mode cull_disabled;

uniform vec3 trail_color : source_color = vec3(0.0, 0.95, 1.0);
uniform float energy : hint_range(0.0, 8.0) = 3.0;
uniform float collapse : hint_range(0.0, 1.0) = 0.0;

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

void vertex() {
    if (collapse > 0.0) {
        float keep = max(1.0 - collapse * (1.0 + UV.y * 2.0), 0.0);
        VERTEX.y *= keep;
    }
}

void fragment() {
    float v = UV.y;  // 0 = base (floor), 1 = top edge

    // Derezz dissolve
    if (collapse > 0.0) {
        float n = hash(floor(UV * vec2(20.0, 10.0)));
        if (n < collapse * 1.5) discard;
    }

    // ── Hard-light wall structure ────────────────────────────────
    // Bright white-hot base strip where wall meets floor
    float base_hot = pow(max(1.0 - v * 5.0, 0.0), 2.0);

    // Bright top edge (the 'hard light' rim)
    float top_edge = pow(max(v - 0.7, 0.0) / 0.3, 2.0);

    // Core body — mostly the trail color, slightly translucent at top
    float alpha = mix(0.95, 0.7, smoothstep(0.0, 1.0, v));

    // Color layers
    vec3 hot_white = vec3(1.0, 0.97, 0.92);
    vec3 body = trail_color * 0.4;  // dark-ish colored body (PBR albedo)
    body = mix(body, hot_white * 0.8, base_hot);  // white-hot at base
    body = mix(body, trail_color * 0.6, top_edge); // bright rim at top

    // Emission — this is what makes it glow
    vec3 emit = trail_color * energy * 0.35;
    emit = mix(emit, hot_white * energy * 0.5, base_hot);  // base flare
    emit += trail_color * top_edge * energy * 0.25;         // top rim glow

    // Derezz: brief flare then die
    if (collapse > 0.0) {
        float flare = mix(1.0, 2.5, smoothstep(0.0, 0.15, collapse))
                    * (1.0 - smoothstep(0.2, 1.0, collapse));
        emit *= flare;
        alpha *= (1.0 - collapse);
    }

    ALBEDO = body;
    EMISSION = emit;
    ALPHA = alpha;
    METALLIC = 0.6;
    ROUGHNESS = 0.15;
}
";
        return shader;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  COLLISION
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Test if a point is within threshold distance of any segment in this
    /// cycle's trail. Skips the last few segments when testing against self
    /// to prevent self-collision on spawn/turn.
    /// </summary>
    public bool HitsTrail(Vector2 testPoint, float threshold, LightCycle? queryCycle)
    {
        int n = _trailPoints.Count;

        // Check committed segments (need at least 2 points for a segment)
        if (n >= 2)
        {
            for (int i = 0; i < n - 1; i++)
            {
                // Don't let a cycle collide with its own most recent segments
                if (queryCycle == this && i >= n - 3) continue;

                var a = new Vector2(_trailPoints[i].X, _trailPoints[i].Z);
                var b = new Vector2(_trailPoints[i + 1].X, _trailPoints[i + 1].Z);

                if (PointToSegmentDist(testPoint, a, b) < threshold)
                    return true;
            }
        }

        // Also check the live segment (from last committed point to current pos).
        // This runs even with only 1 trail point — fixes the startup grace period.
        if (queryCycle != this && n >= 1 && !_derezzing)
        {
            var a = new Vector2(_trailPoints[n - 1].X, _trailPoints[n - 1].Z);
            var b = new Vector2(_pos.X, _pos.Z);
            if ((b - a).LengthSquared() > 0.01f &&
                PointToSegmentDist(testPoint, a, b) < threshold)
                return true;
        }

        return false;
    }

    private static float PointToSegmentDist(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 0.001f) return (p - a).Length();
        float t = Mathf.Clamp((p - a).Dot(ab) / lenSq, 0, 1);
        return (p - (a + ab * t)).Length();
    }

    // ═════════════════════════════════════════════════════════════════════
    //  AI — Nominal survival instinct. Look ahead, turn if about to die.
    //  Not Dijkstra, not A* — just "don't drive into a wall you can see."
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Probe ahead in the current direction. If an obstacle is within the
    /// danger window, try both perpendicular turns and pick the one with
    /// more room. Returns true if a turn was executed.
    /// </summary>
    private bool TryAvoidDeath()
    {
        float lookDist = Mathf.Max(_moveSpeed * 0.5f, 8.0f);
        Vector2 pos2d = new(_pos.X, _pos.Z);

        float forwardClear = ProbeDirection(pos2d, _dir, lookDist);
        if (forwardClear >= lookDist)
            return false; // all clear, carry on

        // Danger ahead — evaluate both perpendicular options
        Vector2 leftDir, rightDir;
        if (Mathf.Abs(_dir.X) > 0.5f)
        {
            leftDir  = new Vector2(0, -1);
            rightDir = new Vector2(0,  1);
        }
        else
        {
            leftDir  = new Vector2(-1, 0);
            rightDir = new Vector2( 1, 0);
        }

        float leftClear  = ProbeDirection(pos2d, leftDir,  lookDist);
        float rightClear = ProbeDirection(pos2d, rightDir, lookDist);

        // If both directions are also deadly, just pick the least-bad one
        // (the cycle is probably doomed, which is fine — it's TRON)
        Vector2 bestDir = leftClear >= rightClear ? leftDir : rightDir;

        // Commit trail point at current position and execute the turn
        _trailPoints.Add(new Vector3(_pos.X, 0, _pos.Z));
        _dir = bestDir;
        _turnTimer = 0;
        _nextTurn = 1.5 + GD.Randf() * 3.0;
        _moveSpeed = _speed * 10.0f * (0.9f + GD.Randf() * 0.2f);
        OrientBody();

        return true;
    }

    /// <summary>
    /// Sample points along a cardinal direction and return distance to the
    /// nearest obstacle (trail wall or arena boundary). Cheap enough to
    /// call 3× per frame per cycle.
    /// </summary>
    private float ProbeDirection(Vector2 from, Vector2 direction, float maxDist)
    {
        float halfX = _arena.GetArenaX() / 2 - 1.0f;
        float halfZ = _arena.GetArenaZ() / 2 - 1.0f;

        // Arena boundary gives a hard ceiling on how far we can go
        float wallDist = maxDist;
        if (direction.X >  0.5f) wallDist = Mathf.Min(wallDist, halfX - from.X);
        if (direction.X < -0.5f) wallDist = Mathf.Min(wallDist, from.X + halfX);
        if (direction.Y >  0.5f) wallDist = Mathf.Min(wallDist, halfZ - from.Y);
        if (direction.Y < -0.5f) wallDist = Mathf.Min(wallDist, from.Y + halfZ);

        if (wallDist <= 0) return 0;

        // Sample along the probe line for trail collisions
        float step = 2.0f;
        float limit = Mathf.Min(wallDist, maxDist);
        for (float d = step; d <= limit; d += step)
        {
            Vector2 test = from + direction * d;
            if (_arena.CheckTrailCollision(this, test))
                return d;
        }

        return Mathf.Min(wallDist, maxDist);
    }

    // ═════════════════════════════════════════════════════════════════════
    //  CLEANUP
    // ═════════════════════════════════════════════════════════════════════

    public void Cleanup()
    {
        _bodyRoot.QueueFree();  // frees all child MeshInstance3D parts too
        _headLight.QueueFree();
        _trailGlow.QueueFree();
        _trailRibbon.QueueFree();
        _trailPoints.Clear();
    }
}
