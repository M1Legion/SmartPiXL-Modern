// ═══════════════════════════════════════════════════════════════════════════
//  arena.mjs — Grid world, reflective floor, borders, pillars, atmosphere
//
//  Visual upgrades from the monolith:
//  • Reflector floor — dark mirror reflecting cycles, trails, and glow
//  • Stronger border edges — multi-pass hard-light energy barriers
//  • Taller, brighter corner pillars with cross-billboard geometry
//  • Subtle gradient horizon (distant arena glow)
//  • Refined grid opacity — barely visible until something moves over it
//  • Hexagonal center reticle with animated rotation
// ═══════════════════════════════════════════════════════════════════════════
import * as THREE from 'three';
import { Reflector } from 'three/addons/objects/Reflector.js';

/**
 * Build the complete arena and add it to the scene.
 *
 * @param {THREE.Scene} scene
 * @param {Object} cfg - { CELL, ARENA_X, ARENA_Z, HALF_X, HALF_Z, WALL_H }
 * @returns {Object} { reflector, centerReticle } for later updates
 */
export function buildArena(scene, cfg) {
  const { CELL, ARENA_X, ARENA_Z, HALF_X, HALF_Z, WALL_H } = cfg;

  // ══════════════════════════════════════════════════════════════════════
  //  REFLECTIVE FLOOR — The dark mirror (THE defining Tron visual)
  //  Uses Three.js Reflector with heavy dark tint. Cycles, trails, and
  //  border glow all reflect in the floor's surface.
  // ══════════════════════════════════════════════════════════════════════
  const reflectorGeo = new THREE.PlaneGeometry(ARENA_X * 1.5, ARENA_Z * 1.5);
  const reflector = new Reflector(reflectorGeo, {
    color: new THREE.Color(0x181818),  // Neutral gray — preserves reflected hue
    textureWidth: 2048,
    textureHeight: 2048,
    clipBias: 0.003,
  });
  reflector.rotation.x = -Math.PI / 2;
  reflector.position.y = -0.5;
  scene.add(reflector);

  // ══════════════════════════════════════════════════════════════════════
  //  EXTENDED GRID — Infinite-feeling grid fading into fog
  // ══════════════════════════════════════════════════════════════════════
  const extGrid = new THREE.GridHelper(4000, 80, 0x002030, 0x001018);
  extGrid.material.transparent = true;
  extGrid.material.opacity = 0.4;
  extGrid.position.y = 0.05;
  scene.add(extGrid);

  // ══════════════════════════════════════════════════════════════════════
  //  ARENA INTERIOR GRID — Subtle lines, visible but not distracting
  //  Movie grid: barely visible until light passes over it.
  // ══════════════════════════════════════════════════════════════════════
  const gridPts = [];
  for (let x = -HALF_X; x <= HALF_X; x += CELL) {
    gridPts.push(new THREE.Vector3(x, 0.15, -HALF_Z));
    gridPts.push(new THREE.Vector3(x, 0.15, HALF_Z));
  }
  for (let z = -HALF_Z; z <= HALF_Z; z += CELL) {
    gridPts.push(new THREE.Vector3(-HALF_X, 0.15, z));
    gridPts.push(new THREE.Vector3(HALF_X, 0.15, z));
  }
  const arenaGrid = new THREE.LineSegments(
    new THREE.BufferGeometry().setFromPoints(gridPts),
    new THREE.LineBasicMaterial({ color: 0x003848, transparent: true, opacity: 0.5 })
  );
  scene.add(arenaGrid);

  // ══════════════════════════════════════════════════════════════════════
  //  ARENA BORDER — Smooth mesh strips instead of aliased Line objects.
  //  Lines in WebGL alias badly at oblique angles. Thin mesh quads render
  //  smoothly at any camera angle with proper antialiasing.
  // ══════════════════════════════════════════════════════════════════════
  const corners = [
    [-HALF_X, -HALF_Z], [HALF_X, -HALF_Z],
    [HALF_X, HALF_Z], [-HALF_X, HALF_Z]
  ];

  /**
   * Build a thin vertical mesh strip between two points.
   * Looks like a glowing line but renders AA-clean at all angles.
   */
  function makeBorderStrip(x1, z1, x2, z2, y, stripH, color, opacity, blend) {
    const dx = x2 - x1, dz = z2 - z1;
    const len = Math.sqrt(dx * dx + dz * dz);
    const geo = new THREE.PlaneGeometry(len, stripH);
    const mat = new THREE.MeshBasicMaterial({
      color, transparent: true, opacity,
      blending: blend || THREE.NormalBlending,
      side: THREE.DoubleSide, depthWrite: false
    });
    const mesh = new THREE.Mesh(geo, mat);
    mesh.position.set((x1 + x2) / 2, y + stripH / 2, (z1 + z2) / 2);
    mesh.rotation.y = Math.atan2(dz, dx);
    return mesh;
  }

  // Base border — bright cyan, thin strip at floor level (feeds bloom)
  for (let i = 0; i < 4; i++) {
    const [x1, z1] = corners[i];
    const [x2, z2] = corners[(i + 1) % 4];
    scene.add(makeBorderStrip(x1, z1, x2, z2, 0.1, 1.5, 0x00f3ff, 0.7));
    // Wider soft glow strip behind it (additive, low opacity)
    scene.add(makeBorderStrip(x1, z1, x2, z2, -0.5, 4.0, 0x00f3ff, 0.08, THREE.AdditiveBlending));
  }

  // Top edge strips (at wall height) — subtle
  for (let i = 0; i < 4; i++) {
    const [x1, z1] = corners[i];
    const [x2, z2] = corners[(i + 1) % 4];
    scene.add(makeBorderStrip(x1, z1, x2, z2, WALL_H - 0.5, 1.0, 0x00f3ff, 0.15));
  }

  // ── Vertical corner posts (mesh tubes instead of lines) ────────────────
  for (const [x, z] of corners) {
    const postH = WALL_H * 1.5;
    const postGeo = new THREE.CylinderGeometry(0.4, 0.4, postH, 6);
    const postMat = new THREE.MeshBasicMaterial({
      color: 0x00f3ff, transparent: true, opacity: 0.5
    });
    const post = new THREE.Mesh(postGeo, postMat);
    post.position.set(x, postH / 2, z);
    scene.add(post);
  }

  // ══════════════════════════════════════════════════════════════════════
  //  CORNER LIGHT PILLARS — Dramatic vertical energy columns
  //  Cross-shaped billboards for 3D visibility from all angles.
  // ══════════════════════════════════════════════════════════════════════
  const beamH = WALL_H * 8;
  const beamGeo = new THREE.PlaneGeometry(5, beamH);
  const beamMat = new THREE.MeshBasicMaterial({
    color: 0x00f3ff, transparent: true, opacity: 0.10,
    blending: THREE.AdditiveBlending, side: THREE.DoubleSide, depthWrite: false
  });
  const beamMatBright = new THREE.MeshBasicMaterial({
    color: 0x00f3ff, transparent: true, opacity: 0.18,
    blending: THREE.AdditiveBlending, side: THREE.DoubleSide, depthWrite: false
  });

  for (const [x, z] of corners) {
    // Cross-shaped pillar: two perpendicular planes
    const b1 = new THREE.Mesh(beamGeo, beamMatBright);
    b1.position.set(x, beamH / 2, z);
    scene.add(b1);
    const b2 = new THREE.Mesh(beamGeo.clone(), beamMat);
    b2.position.set(x, beamH / 2, z);
    b2.rotation.y = Math.PI / 2;
    scene.add(b2);
    // Ground glow splash at pillar base
    const splashGeo = new THREE.CircleGeometry(30, 32);
    const splashMat = new THREE.MeshBasicMaterial({
      color: 0x00f3ff, transparent: true, opacity: 0.06,
      blending: THREE.AdditiveBlending, side: THREE.DoubleSide, depthWrite: false
    });
    const splash = new THREE.Mesh(splashGeo, splashMat);
    splash.rotation.x = -Math.PI / 2;
    splash.position.set(x, 0.3, z);
    scene.add(splash);
  }

  // ══════════════════════════════════════════════════════════════════════
  //  BORDER ENERGY BARRIER PLANES — Translucent walls on arena edges
  //  Very faint vertical planes that give the border substance and glow.
  // ══════════════════════════════════════════════════════════════════════
  const barrierMat = new THREE.MeshBasicMaterial({
    color: 0x00f3ff, transparent: true, opacity: 0.015,
    blending: THREE.AdditiveBlending, side: THREE.DoubleSide, depthWrite: false
  });
  // North + South walls
  for (const zSign of [-1, 1]) {
    const wallGeo = new THREE.PlaneGeometry(ARENA_X, WALL_H * 1.5);
    const wall = new THREE.Mesh(wallGeo, barrierMat);
    wall.position.set(0, WALL_H * 0.75, HALF_Z * zSign);
    if (zSign === -1) wall.rotation.y = Math.PI;
    scene.add(wall);
  }
  // East + West walls
  for (const xSign of [-1, 1]) {
    const wallGeo = new THREE.PlaneGeometry(ARENA_Z, WALL_H * 1.5);
    const wall = new THREE.Mesh(wallGeo, barrierMat);
    wall.position.set(HALF_X * xSign, WALL_H * 0.75, 0);
    wall.rotation.y = Math.PI / 2 * (xSign === 1 ? 1 : -1);
    scene.add(wall);
  }

  // ══════════════════════════════════════════════════════════════════════
  //  CENTER RETICLE — Hexagonal ring with inner dot (animated rotation)
  // ══════════════════════════════════════════════════════════════════════
  const reticleMat = new THREE.MeshBasicMaterial({
    color: 0x00f3ff, transparent: true, opacity: 0.15,
    side: THREE.DoubleSide, blending: THREE.AdditiveBlending, depthWrite: false
  });
  const ring = new THREE.Mesh(new THREE.RingGeometry(18, 22, 6), reticleMat);
  ring.rotation.x = -Math.PI / 2;
  ring.position.y = 0.4;
  scene.add(ring);

  const outerRing = new THREE.Mesh(
    new THREE.RingGeometry(30, 32, 6),
    reticleMat.clone()
  );
  outerRing.material.opacity = 0.06;
  outerRing.rotation.x = -Math.PI / 2;
  outerRing.position.y = 0.35;
  scene.add(outerRing);

  const dot = new THREE.Mesh(
    new THREE.CircleGeometry(6, 16),
    reticleMat.clone()
  );
  dot.material.opacity = 0.2;
  dot.rotation.x = -Math.PI / 2;
  dot.position.y = 0.4;
  scene.add(dot);

  const centerReticle = { ring, outerRing, dot };

  // ══════════════════════════════════════════════════════════════════════
  //  HORIZON GLOW — Subtle distant light at the horizon line
  //  In the movie, distant arenas/cities glow faintly on the horizon.
  // ══════════════════════════════════════════════════════════════════════
  const horizonGeo = new THREE.PlaneGeometry(6000, 200);
  const horizonMat = new THREE.MeshBasicMaterial({
    color: 0x001828, transparent: true, opacity: 0.12,
    blending: THREE.AdditiveBlending, side: THREE.DoubleSide, depthWrite: false
  });
  for (let angle = 0; angle < Math.PI * 2; angle += Math.PI / 2) {
    const h = new THREE.Mesh(horizonGeo, horizonMat);
    h.position.set(
      Math.sin(angle) * 2500,
      50,
      Math.cos(angle) * 2500
    );
    h.rotation.y = angle;
    scene.add(h);
  }

  return { reflector, centerReticle };
}

/**
 * Per-frame arena updates (reticle rotation, etc.)
 * @param {Object} arena - Return value of buildArena
 * @param {number} now   - performance.now()
 */
export function updateArena(arena, now) {
  if (!arena || !arena.centerReticle) return;
  const t = now * 0.0003;
  arena.centerReticle.ring.rotation.z = t;
  arena.centerReticle.outerRing.rotation.z = -t * 0.7;
}
