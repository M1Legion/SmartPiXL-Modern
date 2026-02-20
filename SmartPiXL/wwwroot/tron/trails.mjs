// ═══════════════════════════════════════════════════════════════════════════
//  trails.mjs — Trail wall geometry + GLSL shaders (the iconic Tron light walls)
//
//  Enhanced from the monolith:
//  • Dual-frequency vertical energy flow for visual depth
//  • Asymmetric pulse acceleration toward the cycle head
//  • Perspective-dependent scanlines (tighter at oblique angles)
//  • Multi-scale micro-circuit layering
//  • Head-creation white-hot flash (200ms settle)
//  • Trail settling animation (bright → cruise intensity)
//  • Harder top-edge corona (blooms through UnrealBloomPass)
// ═══════════════════════════════════════════════════════════════════════════
import * as THREE from 'three';

// ── Enhanced vertex shader ────────────────────────────────────────────────
const TRAIL_VERT = /* glsl */ `
  varying vec2 vUv;
  varying vec3 vWorldPos;
  void main() {
    vUv = uv;
    vWorldPos = (modelMatrix * vec4(position, 1.0)).xyz;
    gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
  }
`;

// ── Enhanced fragment shader — Tron Legacy solid light wall ────────────────
//  Designed as SOLID LIGHT: a smooth, uniform glowing wall with subtle
//  vertical energy flow, a bright top corona, and ground contact glow.
//  No blocky grid patterns. No fast-moving horizontal pulses.
//  Think Green Lantern constructs in multiple colors: solid, luminous, real.
const TRAIL_FRAG = /* glsl */ `
  uniform float uTime;
  uniform vec3  uColor;
  uniform float uOpacity;
  uniform float uBirth;

  varying vec2 vUv;
  varying vec3 vWorldPos;

  void main() {
    float u = vUv.x;   // 0 = oldest trail point → 1 = cycle head
    float v = vUv.y;   // 0 = ground → 1 = top edge

    // ── Trail age fade: old end dissolves smoothly ──────────────────────
    float trailFade = smoothstep(0.0, 0.08, u);

    // ── Vertical energy flow — very slow, subtle luminance variation ────
    //    Two soft sine waves create a gentle shimmer, not visible blocks
    float flow1 = sin(v * 12.566 - uTime * 1.2) * 0.5 + 0.5;
    float flow2 = sin(v * 8.0 - uTime * 0.8 + 1.5) * 0.5 + 0.5;
    float flow = flow1 * 0.06 + flow2 * 0.04;  // Very subtle — just shimmer

    // ── Top-edge corona — bright clean strip (the defining hard-light edge)
    float topGlow = smoothstep(0.70, 0.95, v) * 0.9;
    float topEdge = smoothstep(0.92, 1.0, v) * 0.4;  // Bright but controlled

    // ── Ground-contact glow — soft warm base ────────────────────────────
    float bottomGlow = smoothstep(0.12, 0.0, v) * 0.3;

    // ── Fresnel edge glow — view-angle dependent brightness ─────────────
    vec3 dFdxPos = dFdx(vWorldPos);
    vec3 dFdyPos = dFdy(vWorldPos);
    vec3 faceN   = normalize(cross(dFdxPos, dFdyPos));
    vec3 viewDir = normalize(cameraPosition - vWorldPos);
    float fresnel = 1.0 - abs(dot(faceN, viewDir));
    fresnel = pow(fresnel, 3.0) * 0.35;

    // ── Core brightness — the wall face has a uniform solid luminance ───
    //    Vertical gradient: brightest at top, dimmer toward ground
    float coreLight = 0.45 + v * 0.25;

    // ── Head proximity glow (softer than before) ────────────────────────
    float headGlow = pow(u, 6.0) * 0.25;

    // ── Creation settle — new trails ease from bright to cruise ─────────
    float age = uTime - uBirth;
    float settleBoost = max(0.0, 1.0 - age * 3.0) * 0.15 * u;

    // ── Composite energy ────────────────────────────────────────────────
    float energy = coreLight
                 + flow
                 + topGlow
                 + topEdge
                 + bottomGlow
                 + fresnel
                 + headGlow
                 + settleBoost;

    float alpha = (0.30 + energy * 0.45) * trailFade * uOpacity;

    // ── Color — restrained HDR for controlled bloom ─────────────────────
    vec3 col = uColor * (1.0 + energy * 1.0);

    // ── Hot-white core along top edge ───────────────────────────────────
    float edgeWhite = smoothstep(0.88, 0.98, v) * 0.25;
    col = mix(col, vec3(1.2), edgeWhite);

    // ── Soft head glow in cycle color (not white flash) ─────────────────
    col = mix(col, uColor * 1.6, pow(u, 10.0) * 0.15);

    gl_FragColor = vec4(col, clamp(alpha, 0.0, 1.0));
  }
`;

// ── Shared uniform clock (updated once per frame, all trail materials read it) ──
const trailClock = { value: 0 };

/**
 * Build a complete trail system for one cycle.
 * Returns all geometry, materials, and mesh references needed to update the trail each frame.
 *
 * @param {number} hex       - Cycle color hex
 * @param {number} maxTrail  - Maximum trail points
 * @param {number} wallH     - Trail wall height
 * @returns {Object} Trail components: wallMesh, topLine, baseLine, plus raw buffers
 */
export function buildTrail(hex, maxTrail, wallH) {
  const MAX = maxTrail;
  const col = new THREE.Color(hex);

  // ── Wall mesh: indexed BufferGeometry, 2 verts per point (bottom + top) ──
  const wallPos = new Float32Array(MAX * 2 * 3);
  const wallUV  = new Float32Array(MAX * 2 * 2);
  const wallGeo = new THREE.BufferGeometry();
  wallGeo.setAttribute('position', new THREE.BufferAttribute(wallPos, 3));
  wallGeo.setAttribute('uv',       new THREE.BufferAttribute(wallUV, 2));

  const indices = [];
  for (let i = 0; i < MAX - 1; i++) {
    const b0 = i * 2, t0 = i * 2 + 1;
    const b1 = (i + 1) * 2, t1 = (i + 1) * 2 + 1;
    indices.push(b0, b1, t1, b0, t1, t0);
  }
  wallGeo.setIndex(indices);
  wallGeo.setDrawRange(0, 0);

  const wallMat = new THREE.ShaderMaterial({
    vertexShader: TRAIL_VERT,
    fragmentShader: TRAIL_FRAG,
    uniforms: {
      uTime:    trailClock,
      uColor:   { value: new THREE.Vector3(col.r, col.g, col.b) },
      uOpacity: { value: 0.65 },
      uBirth:   { value: performance.now() * 0.001 }
    },
    transparent: true,
    side: THREE.DoubleSide,
    depthWrite: false,
    blending: THREE.AdditiveBlending
  });

  const wallMesh = new THREE.Mesh(wallGeo, wallMat);
  wallMesh.frustumCulled = false;

  // ── Top edge line (bright, feeds bloom — the glowing ribbon crest) ────
  const topPos = new Float32Array(MAX * 3);
  const topGeo = new THREE.BufferGeometry();
  topGeo.setAttribute('position', new THREE.BufferAttribute(topPos, 3));
  topGeo.setDrawRange(0, 0);
  const topLine = new THREE.Line(topGeo,
    new THREE.LineBasicMaterial({ color: hex, transparent: true, opacity: 0.95 })
  );
  topLine.frustumCulled = false;

  // ── Base glow line (on the floor, additive bloom feeder) ──────────────
  const basePos = new Float32Array(MAX * 3);
  const baseGeo = new THREE.BufferGeometry();
  baseGeo.setAttribute('position', new THREE.BufferAttribute(basePos, 3));
  baseGeo.setDrawRange(0, 0);
  const baseLine = new THREE.Line(baseGeo,
    new THREE.LineBasicMaterial({
      color: hex, transparent: true, opacity: 0.5,
      blending: THREE.AdditiveBlending
    })
  );
  baseLine.frustumCulled = false;

  return {
    wallPos, wallUV, wallGeo, wallMesh, wallMat,
    topPos, topGeo, topLine,
    basePos, baseGeo, baseLine
  };
}

/**
 * Update the shared trail clock uniform. Call once per frame.
 * @param {number} now - performance.now() timestamp
 */
export function updateTrailClock(now) {
  trailClock.value = now * 0.001;
}

export { TRAIL_VERT, TRAIL_FRAG, trailClock };
