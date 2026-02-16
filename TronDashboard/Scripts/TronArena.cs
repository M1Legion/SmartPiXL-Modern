using Godot;
using System;
using System.Collections.Generic;

namespace TronDashboard;

/// <summary>
/// TronArena — Root scene controller. Builds the entire 3D arena procedurally:
/// grid floor, border walls, corner pillars, environment, camera, lighting,
/// and manages light-cycle spawning/lifecycle.
///
/// Visual target: TRON Legacy arena — obsidian-black mirror floors, thin cyan
/// grid lines as emission only, varied emissive accent panels along borders,
/// volumetric corner beams, stadium-style edge lighting. The floor reflects
/// everything via SSR on a high-metallic / near-zero-roughness PBR surface.
/// </summary>
public partial class TronArena : Node3D
{
    // ══════════════════════════════════════════════════════════════════════
    //  CONFIG
    // ══════════════════════════════════════════════════════════════════════
    private const float CellSize  = 5.0f;    // Grid cell size (meters)
    private const float ArenaX    = 160.0f;  // Arena width
    private const float ArenaZ    = 100.0f;  // Arena depth
    private const float WallH     = 3.0f;    // Trail wall height
    private const int   MaxTrail  = 400;     // Trail points per cycle
    private const int   AmbientMax = 8;
    private const int   ContestantMin = 4;
    private const int   ContestantMax = 8;

    // ── TRON Palette ─────────────────────────────────────────────────────
    private static readonly Dictionary<string, Color> Palette = new()
    {
        ["TRON"]    = new Color("00f3ff"),
        ["CLU"]     = new Color("ffaa00"),
        ["RINZLER"] = new Color("ff4400"),
        ["QUORRA"]  = new Color("cc44ff"),
        ["GEM"]     = new Color("88ddff"),
        ["SARK"]    = new Color("ff0044"),
        ["CASTOR"]  = new Color("ff00ff"),
        ["YORI"]    = new Color("00ff88"),
        ["RAM"]     = new Color("ff66aa"),
        ["FLYNN"]   = new Color("4488ff"),
    };
    private static readonly string[] ContestantNames = [.. Palette.Keys];
    private static readonly Color AmbientColor = new("006677");
    private static readonly Color CyanColor    = new("00f3ff");

    // True black — the void of the Grid
    private static readonly Color BlackVoid     = new("000000");
    // Very faint blue-black for floor albedo — just enough for SSR to find a surface
    private static readonly Color FloorBlack    = new("020408");

    // ── State ────────────────────────────────────────────────────────────
    private Camera3D _camera = null!;
    private WorldEnvironment _worldEnv = null!;
    private readonly List<LightCycle> _cycles = [];
    private int _contestantIdx;
    private double _ambientTimer;
    private double _contestantTimer;
    private double _elapsed;
    private Label _fpsLabel = null!;
    private Label _cycleLabel = null!;

    // ── Screenshot system ────────────────────────────────────────────────
    private const string ScreenshotDir = "Screenshots";
    private int _screenshotIdx;
    private bool _autoScreenshotDone;
    private const double AutoScreenshotDelay = 3.0; // seconds after launch

    // ══════════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ══════════════════════════════════════════════════════════════════════
    public override void _Ready()
    {
        BuildEnvironment();
        BuildCamera();
        BuildLighting();
        BuildGridFloor();
        BuildArenaBorder();
        BuildCornerPillars();
        BuildEdgeLighting();
        BuildHUD();

        // Initial fleet
        for (int i = 0; i < 3; i++) SpawnCycle("", true);
        SpawnCycle("TRON", false);
        SpawnCycle("CLU", false);
        SpawnCycle("QUORRA", false);
        SpawnCycle("RINZLER", false);
        SpawnCycle("FLYNN", false);
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;

        UpdateCamera(delta);
        UpdateGridShader();
        UpdateCycles(delta);
        SpawnLoop(delta);
        UpdateHUD();
        HandleScreenshots();
    }

    public override void _Input(InputEvent @event)
    {
        // F12 = take screenshot
        if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.F12)
        {
            CaptureScreenshot("manual");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  SCREENSHOT SYSTEM — Auto-capture on launch + F12 manual capture.
    //  Saves PNGs to TronDashboard/Screenshots/ for review.
    // ══════════════════════════════════════════════════════════════════════
    private void HandleScreenshots()
    {
        if (!_autoScreenshotDone && _elapsed >= AutoScreenshotDelay)
        {
            _autoScreenshotDone = true;
            CaptureScreenshot("auto");
        }
    }

    private void CaptureScreenshot(string tag)
    {
        var viewport = GetViewport();
        var img = viewport.GetTexture().GetImage();
        if (img == null)
        {
            GD.PrintErr("[Screenshot] Failed to get viewport image");
            return;
        }

        // Ensure directory exists
        string dirPath = $"res://{ScreenshotDir}";
        if (!DirAccess.DirExistsAbsolute(ProjectSettings.GlobalizePath(dirPath)))
            DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(dirPath));

        string filename = $"{tag}_{_screenshotIdx++:D4}_{DateTime.Now:HHmmss}.png";
        string fullPath = ProjectSettings.GlobalizePath($"{dirPath}/{filename}");

        var err = img.SavePng(fullPath);
        if (err == Error.Ok)
            GD.Print($"[Screenshot] Saved: {fullPath}");
        else
            GD.PrintErr($"[Screenshot] Save failed: {err}");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ENVIRONMENT — Pure black void, SSR mirror floor, emissive-only bloom
    // ══════════════════════════════════════════════════════════════════════
    private void BuildEnvironment()
    {
        var env = new Godot.Environment
        {
            // ── Background: the void ────────────────────────────────
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = BlackVoid,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("021020"),
            AmbientLightEnergy = 0.15f,  // Subtle fill — keeps dark areas readable

            // ── Tonemap: ACES filmic for deep blacks + punchy highlights ─
            TonemapMode = Godot.Environment.ToneMapper.Aces,
            TonemapExposure = 0.9f,
            TonemapWhite = 8.0f,

            // ── Glow (bloom): high threshold so only emissives bloom ────
            GlowEnabled = true,
            GlowIntensity = 1.0f,
            GlowStrength = 1.0f,
            GlowBloom = 0.15f,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive,
            GlowHdrThreshold = 1.0f,   // Only surfaces > 1.0 energy bloom
            GlowHdrScale = 2.0f,
            GlowHdrLuminanceCap = 16.0f,

            // ── Fog: barely-there atmospheric haze ──────────────────
            FogEnabled = true,
            FogLightColor = new Color("000508"),
            FogDensity = 0.0008f,          // Very subtle
            FogAerialPerspective = 0.15f,

            // ── Volumetric fog: atmospheric haze for light interaction ─
            VolumetricFogEnabled = true,
            VolumetricFogDensity = 0.008f,
            VolumetricFogAlbedo = new Color("021828"),
            VolumetricFogEmission = new Color("002030"),
            VolumetricFogEmissionEnergy = 0.5f,
            VolumetricFogLength = 250.0f,
            VolumetricFogAnisotropy = 0.6f,  // Forward scattering for beams

            // ── SSR: mirror floor — cranked to max ──────────────────
            SsrEnabled = true,
            SsrMaxSteps = 256,     // Max quality for RTX 4090
            SsrFadeIn = 0.05f,    // Reflections appear quickly
            SsrFadeOut = 4.0f,    // Reflections persist far
            SsrDepthTolerance = 0.1f,

            // ── SSAO: subtle contact shadows (not crushing darks) ────
            SsaoEnabled = true,
            SsaoRadius = 1.2f,
            SsaoIntensity = 1.0f,  // Gentle — doesn't crush the existing dark tones
            SsaoPower = 1.2f,

            // ── SSIL: emissive bounce light — key for TRON look ─────
            SsilEnabled = true,
            SsilRadius = 12.0f,
            SsilIntensity = 1.5f,  // Emissive bounce is how TRON scenes get their glow
        };

        _worldEnv = new WorldEnvironment { Environment = env };
        AddChild(_worldEnv);

        // Glow levels via setter (0-indexed, float intensity)
        env.SetGlowLevel(0, 1.0f);   // Fine bloom
        env.SetGlowLevel(1, 1.0f);   // Medium bloom
        env.SetGlowLevel(2, 1.0f);   // Wide bloom
        env.SetGlowLevel(3, 1.0f);   // Very wide — TRON glow spread
        env.SetGlowLevel(4, 0.5f);   // Ultra-wide — faint haze
        env.SetGlowLevel(5, 0.0f);
        env.SetGlowLevel(6, 0.0f);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  CAMERA — Static overhead view of the full arena
    //
    //  Math verification:
    //    Position:  (0, 55, 40)   — 55m up, 40m south of center
    //    LookAt:    (0, 0, -8)    — center-ish of the arena
    //    Direction: (0, -55, -48) — 49° below horizontal (steep view)
    //    Distance to floor center: sqrt(55² + 48²) = 73m
    //    FOV 70° vertical half-angle = 35°
    //    Frame bottom (84° below horiz): hits floor ~7m from camera base = Z≈33 (south wall at Z=50 ✓)
    //    Frame top (14° below horiz): hits floor ~220m ahead = far north wall ✓
    //    Horizontal half-angle ≈ 55° at 16:9: at 73m distance covers ±104m (arena is ±80m ✓)
    //    View distance to arena center: 73m — well within shader bright zone (<80m)
    //    View distance to far corners (±80, 0, -50): ~130m — still within 80-300m fade
    // ══════════════════════════════════════════════════════════════════════
    private void BuildCamera()
    {
        _camera = new Camera3D
        {
            Fov = 55.0f,
            Near = 0.1f,
            Far = 800.0f,
        };
        // Orbit camera position at t≈3s — confirmed visible in auto_0000_202228.png (953KB).
        // The LookAt-before-AddChild bug is what broke every previous static attempt.
        _camera.Position = new Vector3(3, 76, 72);
        AddChild(_camera);  // Must be in tree BEFORE LookAt
        _camera.LookAt(new Vector3(0, 0, -5), Vector3.Up);
        _camera.MakeCurrent();
    }

    private void UpdateCamera(double delta)
    {
        // Frozen. No orbit, no transform.
    }

    // ══════════════════════════════════════════════════════════════════════
    //  LIGHTING — Minimal fill, emissives provide 90% of illumination.
    //  Direct lights exist only for shadow casting and subtle fill.
    // ══════════════════════════════════════════════════════════════════════
    private void BuildLighting()
    {
        // Very faint overhead directional — just enough for shadows
        var dirLight = new DirectionalLight3D
        {
            LightColor = new Color("182838"),
            LightEnergy = 0.15f,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
        };
        dirLight.Position = new Vector3(0, 80, 0);
        dirLight.Rotation = new Vector3(Mathf.DegToRad(-90), 0, 0); // Point straight down
        AddChild(dirLight);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  EDGE LIGHTING — Small accent lights along the arena perimeter,
    //  like stadium rigging in TRON Legacy. These create the warm/cool
    //  contrast and the only real direct illumination on the floor.
    // ══════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Edge lighting removed — TRON arenas are emissive-lit.
    /// The wall seam shader + SSIL bounce provides all wall illumination.
    /// OmniLights along walls created a uniform wash that killed the
    /// darkness. Emissive seam lines + SSR reflections are the correct look.
    /// </summary>
    private void BuildEdgeLighting()
    {
        // Intentionally empty — emissive-only lighting from wall seams + grid.
        // SSIL bounces emissive light onto nearby geometry for natural fill.
    }

    // ══════════════════════════════════════════════════════════════════════
    //  GRID FLOOR — Obsidian-black mirror surface with thin emissive grid.
    //  Uses a proper PBR spatial shader so SSR can reflect off it.
    //  Metallic=1, Roughness≈0.02 inside arena for mirror finish.
    //  Grid lines are EMISSION only — the albedo stays near-black.
    // ══════════════════════════════════════════════════════════════════════
    private ShaderMaterial _gridMat = null!;
    private ShaderMaterial _wallMat = null!;

    private void BuildGridFloor()
    {
        // Oversized plane to extend into darkness beyond the arena edge
        var planeMesh = new PlaneMesh
        {
            Size = new Vector2(ArenaX * 3, ArenaZ * 3),
            SubdivideWidth = 2,
            SubdivideDepth = 2,
        };

        _gridMat = new ShaderMaterial
        {
            Shader = CreateGridShader(),
        };
        _gridMat.SetShaderParameter("arena_size", new Vector2(ArenaX, ArenaZ));
        _gridMat.SetShaderParameter("cell_size", CellSize);
        _gridMat.SetShaderParameter("time", 0.0f);

        var mi = new MeshInstance3D
        {
            Mesh = planeMesh,
            MaterialOverride = _gridMat,
        };
        mi.Position = Vector3.Zero;
        AddChild(mi);
    }

    /// <summary>
    /// Tick the grid shader's time uniform for animated effects.
    /// Called from _Process.
    /// </summary>
    private void UpdateGridShader()
    {
        _gridMat?.SetShaderParameter("time", (float)_elapsed);
    }

    private static Shader CreateGridShader()
    {
        var shader = new Shader();
        shader.Code = @"
shader_type spatial;
render_mode blend_mix, cull_back, diffuse_burley, specular_schlick_ggx;

// ── Uniforms ─────────────────────────────────────────────────────────
uniform vec2 arena_size;
uniform float cell_size;
uniform float time;

// ══════════════════════════════════════════════════════════════════════
//  CANONICAL TRON LEGACY GRID
//
//  The Grid from the movie is deceptively simple:
//    - Pure black mirror floor (high metallic, near-zero roughness)
//    - Single-weight thin cyan lines — constant brightness, no sub-grid
//    - Bright white-cyan intersection nodes
//    - Clean border edge glow
//    - Lines fade with distance into the void (perspective depth)
//    - No pulse rings, no scan lines, no sub-grid — just pristine geometry
//    - Floor reflects everything via SSR
// ══════════════════════════════════════════════════════════════════════

const vec3 FLOOR_BLACK = vec3(0.003, 0.006, 0.012);
const vec3 VOID_BLACK  = vec3(0.001, 0.002, 0.003);
const vec3 GRID_CYAN   = vec3(0.0, 0.9, 1.0);
const vec3 NODE_WHITE  = vec3(0.75, 1.0, 1.0);
const vec3 EDGE_CYAN   = vec3(0.0, 0.95, 1.0);

// ── Anti-aliased grid line (fwidth for pixel-perfect at any distance) ─
float aa_line(float coord, float width) {
    float d = abs(fract(coord - 0.5) - 0.5);
    float fw = fwidth(coord);
    return 1.0 - smoothstep(width - fw * 1.5, width + fw * 1.5, d);
}

// ── Anti-aliased dot at grid intersections ──
float aa_dot(vec2 cell_pos, float radius) {
    vec2 nearest = round(cell_pos);
    float d = length(cell_pos - nearest);
    float fw = length(fwidth(cell_pos));
    return 1.0 - smoothstep(radius - fw * 1.5, radius + fw * 1.5, d);
}

// ── Arena boundary SDF ──
float arena_sdf(vec2 p, vec2 half_size) {
    vec2 d = abs(p) - half_size;
    return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0);
}

// Pass world-space position from vertex shader (VERTEX in fragment is view-space!)
varying vec3 world_pos;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    // world_pos is correct world-space XZ from the vertex shader.
    // VERTEX in fragment is VIEW-space — using it with MODEL_MATRIX was wrong
    // and caused grid lines to misalign with the actual world coordinates
    // where light cycles drive.
    vec2 wp = world_pos.xz;
    float half_x = arena_size.x * 0.5;
    float half_z = arena_size.y * 0.5;

    // ── Zones ───────────────────────────────────────────────────
    float sdf = arena_sdf(wp, vec2(half_x, half_z));
    float in_arena = 1.0 - smoothstep(-0.3, 0.3, sdf);
    float near_edge = 1.0 - smoothstep(0.0, 15.0, min(half_x - abs(wp.x), half_z - abs(wp.y)));

    // ── Distance fade — grid lines dissolve into the void ───────
    float view_dist = length(VERTEX);
    float dist_fade = 1.0 - smoothstep(60.0, 250.0, view_dist);

    // ── Grid ────────────────────────────────────────────────────
    vec2 cell_uv = wp / cell_size;
    float line_w = 0.04;   // thin — canonical TRON lines are subtle
    float gx = aa_line(cell_uv.x, line_w);
    float gz = aa_line(cell_uv.y, line_w);
    float grid = max(gx, gz);

    // Intersection nodes — slightly brighter where lines cross
    float node = aa_dot(cell_uv, 0.08);

    // ── Emission compositing ────────────────────────────────────
    vec3 emission = vec3(0.0);

    // Grid lines — subtle, not overpowering
    emission += GRID_CYAN * grid * 0.7 * dist_fade;

    // Intersection nodes — slightly brighter
    emission += NODE_WHITE * node * 1.2 * dist_fade;

    // Border seam — concentrated bright line right at arena perimeter
    // Two layers: sharp inner seam + gentle outer glow
    float border_sharp = smoothstep(0.4, 0.0, abs(sdf));   // tight 0.4m seam
    float border_soft  = smoothstep(2.0, 0.0, abs(sdf));   // gentle 2m glow
    float border = border_sharp * 0.7 + border_soft * 0.3;
    emission += EDGE_CYAN * border * 5.0;

    // Arena mask — nothing beyond the border, pure void
    emission *= in_arena;

    // Fresnel — floor glows faintly at grazing angles (TRON horizon glow)
    float fresnel = pow(1.0 - abs(dot(NORMAL, VIEW)), 3.5);
    emission += GRID_CYAN * fresnel * 0.08 * in_arena;

    // ── PBR — Mirror-black reflective surface ───────────────────
    ALBEDO = mix(VOID_BLACK, FLOOR_BLACK, in_arena);
    METALLIC = mix(0.2, 1.0, in_arena);

    // Mirror-smooth center, slightly rougher at edges and distance
    float base_rough = mix(0.06, 0.01, 1.0 - near_edge);
    float dist_rough = mix(base_rough, 0.12, smoothstep(80.0, 200.0, view_dist));
    ROUGHNESS = mix(0.5, dist_rough, in_arena);
    SPECULAR = mix(0.2, 0.95, in_arena);

    // Fresnel boost — even more reflective at grazing angles
    ROUGHNESS = mix(ROUGHNESS, 0.003, fresnel * in_arena * 0.6);

    // Emission output — multiplied for bloom engagement
    EMISSION = emission * 2.0;
}
";
        return shader;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ARENA BORDER — TRON Legacy style: dark metallic wall structure with
    //  thin emissive seam lines between panels. Light leaks through the
    //  cracks — not from flat glowing surfaces.
    //
    //  The wall shader draws procedural hairline horizontal/vertical seams
    //  on an otherwise near-black reflective surface. The base seam (where
    //  floor meets wall) is brightest, matching the floor grid border glow.
    // ══════════════════════════════════════════════════════════════════════
    private void BuildArenaBorder()
    {
        float halfX = ArenaX / 2;
        float halfZ = ArenaZ / 2;
        float wallHeight = WallH * 2.5f;
        float wallThick = 0.5f;

        // Shared wall shader material — procedural seam lines
        var wallShader = CreateWallShader();
        _wallMat = new ShaderMaterial { Shader = wallShader };
        _wallMat.SetShaderParameter("wall_height", wallHeight);
        _wallMat.SetShaderParameter("panel_spacing", 8.0f);

        // Walls placed flush with the arena boundary (inner face at ±half)
        // North wall
        AddWallMesh(new Vector3(0, wallHeight / 2, -halfZ - wallThick / 2),
                    new Vector3(ArenaX + wallThick, wallHeight, wallThick));
        // South wall
        AddWallMesh(new Vector3(0, wallHeight / 2, halfZ + wallThick / 2),
                    new Vector3(ArenaX + wallThick, wallHeight, wallThick));
        // West wall
        AddWallMesh(new Vector3(-halfX - wallThick / 2, wallHeight / 2, 0),
                    new Vector3(wallThick, wallHeight, ArenaZ + wallThick));
        // East wall
        AddWallMesh(new Vector3(halfX + wallThick / 2, wallHeight / 2, 0),
                    new Vector3(wallThick, wallHeight, ArenaZ + wallThick));
    }

    private void AddWallMesh(Vector3 pos, Vector3 size)
    {
        var mesh = new BoxMesh { Size = size };
        AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = _wallMat, Position = pos });
    }

    /// <summary>
    /// Procedural wall shader — dark metallic panels with thin emissive seam
    /// lines between them. The seams are anti-aliased hairlines that look like
    /// light leaking through cracks in the wall structure.
    /// </summary>
    private static Shader CreateWallShader()
    {
        var shader = new Shader();
        shader.Code = @"
shader_type spatial;
render_mode blend_mix, cull_back, diffuse_burley, specular_schlick_ggx;

// ── Uniforms ─────────────────────────────────────────────────────────
uniform float wall_height;
uniform float panel_spacing;

const vec3 WALL_BLACK  = vec3(0.006, 0.010, 0.016);
const vec3 SEAM_CYAN   = vec3(0.0, 0.85, 1.0);
const vec3 SEAM_WHITE  = vec3(0.6, 0.95, 1.0);

varying vec3 wall_world_pos;

void vertex() {
    wall_world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

// ── Hash for per-panel brightness variation ──
float hash11(float p) {
    p = fract(p * 0.1031);
    p *= p + 33.33;
    p *= p + p;
    return fract(p);
}

// ── Anti-aliased edge at a specific position ──
float aa_edge(float dist, float half_width) {
    float fw = fwidth(dist);
    return 1.0 - smoothstep(half_width - fw, half_width + fw, abs(dist));
}

// ── Anti-aliased repeating line ──
float aa_line(float coord, float half_width) {
    float d = abs(fract(coord - 0.5) - 0.5);
    float fw = fwidth(coord);
    return 1.0 - smoothstep(half_width - fw, half_width + fw, d);
}

void fragment() {
    float y = wall_world_pos.y;
    // Along-wall coordinate: for N/S walls Z is constant so this = X+const,
    // for E/W walls X is constant so this = const+Z — works for both.
    float along = wall_world_pos.x + wall_world_pos.z;

    // Normalized height [0, 1]
    float ny = clamp(y / wall_height, 0.0, 1.0);

    // ── Vertical brightness fade — wall dissolves into darkness above ──
    float vert_fade = 1.0 - ny * ny * 0.6;

    // ═══════════════════════════════════════════════════════════════
    //  HORIZONTAL SEAM LINES — specific heights, varied widths
    //  These simulate the joints between horizontal wall panels.
    //  The base seam is brightest (floor junction), others are subtle.
    // ═══════════════════════════════════════════════════════════════
    float base_line  = aa_edge(y - 0.015, 0.010) * 3.5;      // Floor junction — brightest
    float seam_lo    = aa_edge(ny - 0.18, 0.004) * 1.0;       // Lower panel joint
    float seam_mid   = aa_edge(ny - 0.42, 0.005) * 1.3;       // Mid joint
    float seam_hi    = aa_edge(ny - 0.68, 0.003) * 0.7;       // Upper joint — dimmer
    float cap_line   = aa_edge(ny - 0.95, 0.004) * 0.9;       // Cap edge

    float h_seam = max(max(base_line, seam_lo), max(seam_mid, max(seam_hi, cap_line)));

    // ═══════════════════════════════════════════════════════════════
    //  VERTICAL SEAM LINES — panel divisions along the wall length
    //  Thinner than horizontal, with per-panel brightness variation.
    // ═══════════════════════════════════════════════════════════════
    float panel_cell = along / panel_spacing;
    float v_seam = aa_line(panel_cell, 0.012) * 0.7;

    // Per-panel brightness variation — broken symmetry
    float panel_id = floor(panel_cell);
    float panel_var = 0.4 + 0.6 * hash11(panel_id * 7.3);
    v_seam *= panel_var;

    // ═══════════════════════════════════════════════════════════════
    //  COMBINED SEAM PATTERN
    // ═══════════════════════════════════════════════════════════════
    float seam = max(h_seam, v_seam) * vert_fade;

    // Slight per-panel albedo variation (dark tiles, slightly different shades)
    float tile_var = 0.6 + 0.4 * hash11(panel_id * 13.7 + floor(ny * 4.0) * 3.1);

    // ── PBR: dark metallic reflective surface ──
    ALBEDO = WALL_BLACK * tile_var;
    METALLIC = 0.85;
    ROUGHNESS = 0.10;
    SPECULAR = 0.7;

    // ── Emission: seam lines only — light through cracks ──
    vec3 emission = SEAM_CYAN * seam * 2.0;

    // Base seam gets a warmer white-hot boost (floor-wall junction)
    emission += SEAM_WHITE * base_line * 0.4;

    EMISSION = emission;
}
";
        return shader;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  CORNER PILLARS — Tall volumetric beams at each corner, each a
    //  different intensity to break the symmetry. Inner glow mesh +
    //  spot light pointing up for volumetric fog god-ray interaction.
    //  Also adds mid-wall marker beams for additional emissive detail.
    // ══════════════════════════════════════════════════════════════════════
    private void BuildCornerPillars()
    {
        float halfX = ArenaX / 2;
        float halfZ = ArenaZ / 2;
        float pillarH = WallH * 16;  // Taller — reads as stadium towers

        // Corner positions + colors (broken symmetry)
        (Vector3 pos, Color col, float energy)[] corners =
        [
            (new(-halfX, 0, -halfZ), new Color("00f3ff"), 4.0f),   // NW — brightest, hero corner
            (new(halfX, 0, -halfZ),  new Color("0088cc"), 2.5f),   // NE — medium teal
            (new(halfX, 0, halfZ),   new Color("004466"), 1.5f),   // SE — dim (far from camera)
            (new(-halfX, 0, halfZ),  new Color("006688"), 2.0f),   // SW — medium
        ];

        foreach (var (cpos, ccol, cenergy) in corners)
        {
            // ── Inner beam: tall thin emissive pillar ────────────────
            var beamMesh = new BoxMesh { Size = new Vector3(0.3f, pillarH, 0.3f) };
            var beamMat = new StandardMaterial3D
            {
                EmissionEnabled = true,
                Emission = ccol,
                EmissionEnergyMultiplier = cenergy,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(ccol, 0.04f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };
            AddChild(new MeshInstance3D
            {
                Mesh = beamMesh,
                MaterialOverride = beamMat,
                Position = cpos + new Vector3(0, pillarH / 2, 0),
            });

            // ── Outer haze: wider, dimmer glow volume ───────────────
            var hazeMesh = new BoxMesh { Size = new Vector3(1.5f, pillarH * 0.8f, 1.5f) };
            var hazeMat = new StandardMaterial3D
            {
                EmissionEnabled = true,
                Emission = ccol,
                EmissionEnergyMultiplier = cenergy * 0.15f,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(ccol, 0.015f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            AddChild(new MeshInstance3D
            {
                Mesh = hazeMesh,
                MaterialOverride = hazeMat,
                Position = cpos + new Vector3(0, pillarH * 0.4f, 0),
            });

            // ── Base pedestal: small solid block at floor level ──────
            var baseMesh = new BoxMesh { Size = new Vector3(1.5f, 0.6f, 1.5f) };
            var baseMat = new StandardMaterial3D
            {
                AlbedoColor = new Color("050a10"),
                EmissionEnabled = true,
                Emission = ccol,
                EmissionEnergyMultiplier = cenergy * 0.5f,
                Metallic = 0.9f,
                Roughness = 0.08f,
            };
            AddChild(new MeshInstance3D
            {
                Mesh = baseMesh,
                MaterialOverride = baseMat,
                Position = cpos + new Vector3(0, 0.3f, 0),
            });

            // ── SpotLight3D pointing up for volumetric fog ──────────
            var spot = new SpotLight3D
            {
                LightColor = ccol,
                LightEnergy = cenergy * 1.2f,
                SpotRange = pillarH * 0.7f,
                SpotAngle = 6.0f,
                ShadowEnabled = false,
            };
            spot.Position = cpos + new Vector3(0, 0.5f, 0);
            spot.RotateX(Mathf.DegToRad(-90)); // Point up
            AddChild(spot);
        }

        // Mid-wall markers removed — wall shader's vertical seam lines
        // now handle this. The old marker beams were redundant geometry.
    }

    // ══════════════════════════════════════════════════════════════════════
    //  HUD — FPS, cycle count overlay
    // ══════════════════════════════════════════════════════════════════════
    private void BuildHUD()
    {
        var canvas = new CanvasLayer();
        AddChild(canvas);

        // Title
        var title = new Label
        {
            Text = "TRON DASHBOARD",
            Position = new Vector2(20, 10),
        };
        title.AddThemeColorOverride("font_color", CyanColor);
        title.AddThemeFontSizeOverride("font_size", 22);
        canvas.AddChild(title);

        var subtitle = new Label
        {
            Text = "SMARTPIXL // RTX 4090 // GODOT 4.6",
            Position = new Vector2(20, 40),
        };
        subtitle.AddThemeColorOverride("font_color", new Color("8cb4d2"));
        subtitle.AddThemeFontSizeOverride("font_size", 12);
        canvas.AddChild(subtitle);

        // Stats
        _fpsLabel = new Label
        {
            Text = "FPS: --",
            HorizontalAlignment = HorizontalAlignment.Right,
            Position = new Vector2(-200, 10),
        };
        _fpsLabel.AddThemeColorOverride("font_color", CyanColor);
        _fpsLabel.AddThemeFontSizeOverride("font_size", 14);
        _fpsLabel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        canvas.AddChild(_fpsLabel);

        _cycleLabel = new Label
        {
            Text = "CYCLES: --",
            HorizontalAlignment = HorizontalAlignment.Right,
            Position = new Vector2(-200, 30),
        };
        _cycleLabel.AddThemeColorOverride("font_color", CyanColor);
        _cycleLabel.AddThemeFontSizeOverride("font_size", 14);
        _cycleLabel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        canvas.AddChild(_cycleLabel);
    }

    private void UpdateHUD()
    {
        _fpsLabel.Text = $"FPS: {Engine.GetFramesPerSecond()}";
        _cycleLabel.Text = $"CYCLES: {_cycles.Count}";
    }

    // ══════════════════════════════════════════════════════════════════════
    //  CYCLE MANAGEMENT
    // ══════════════════════════════════════════════════════════════════════
    private void SpawnCycle(string contestantName, bool isAmbient)
    {
        Color color = isAmbient ? AmbientColor :
            (Palette.TryGetValue(contestantName, out var c) ? c : CyanColor);
        float speed = isAmbient ? (1.5f + GD.Randf()) : (1.2f + GD.Randf() * 0.6f);

        float halfX = ArenaX / 2 - CellSize;
        float halfZ = ArenaZ / 2 - CellSize;
        float x = Snap(GD.Randf() * ArenaX - halfX);
        float z = Snap(GD.Randf() * ArenaZ - halfZ);
        x = Mathf.Clamp(x, -halfX, halfX);
        z = Mathf.Clamp(z, -halfZ, halfZ);

        // Random cardinal direction
        int dir = GD.RandRange(0, 3);
        float dx = dir < 2 ? (dir == 0 ? speed : -speed) : 0;
        float dz = dir >= 2 ? (dir == 2 ? speed : -speed) : 0;

        var cycle = new LightCycle(this, contestantName, color, isAmbient,
            new Vector3(x, 0, z), new Vector2(dx, dz), speed);
        _cycles.Add(cycle);
    }

    private void UpdateCycles(double delta)
    {
        for (int i = _cycles.Count - 1; i >= 0; i--)
        {
            if (!_cycles[i].Update(delta, _elapsed))
            {
                _cycles[i].Cleanup();
                _cycles.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Check if a cycle's head position collides with any trail wall
    /// (including its own, excluding the most recent segments).
    /// Uses segment-vs-point distance testing on the XZ plane.
    /// </summary>
    public bool CheckTrailCollision(LightCycle queryCycle, Vector2 testPoint)
    {
        const float threshold = 1.2f; // collision radius in world units

        foreach (var cycle in _cycles)
        {
            if (cycle.IsDerezzing) continue;
            if (cycle.HitsTrail(testPoint, threshold, queryCycle))
                return true;
        }
        return false;
    }

    private void SpawnLoop(double delta)
    {
        _ambientTimer += delta;
        _contestantTimer += delta;

        if (_ambientTimer > 3.0)
        {
            _ambientTimer = 0;
            int ambientCount = 0;
            foreach (var c in _cycles) if (c.IsAmbient) ambientCount++;
            if (ambientCount < AmbientMax)
                SpawnCycle("", true);
        }

        if (_contestantTimer > 4.0)
        {
            _contestantTimer = 0;
            int namedCount = 0;
            foreach (var c in _cycles) if (!c.IsAmbient) namedCount++;
            if (namedCount < ContestantMin)
            {
                int toSpawn = Math.Min(ContestantMax - namedCount, 1 + GD.RandRange(0, 1));
                for (int i = 0; i < toSpawn; i++)
                {
                    string name = ContestantNames[_contestantIdx % ContestantNames.Length];
                    _contestantIdx++;
                    SpawnCycle(name, false);
                }
            }
        }
    }

    private static float Snap(float v) => Mathf.Round(v / CellSize) * CellSize;

    public float GetArenaX() => ArenaX;
    public float GetArenaZ() => ArenaZ;
    public float GetWallH() => WallH;
    public float GetCellSize() => CellSize;
    public int GetMaxTrail() => MaxTrail;
}
