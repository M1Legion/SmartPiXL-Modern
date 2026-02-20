// ═══════════════════════════════════════════════════════════════════════════
//  cycles.mjs — Light cycle construction, spawning, movement, and derezz
//
//  Visual upgrades from the monolith:
//  • Wireframe hull built from ExtrudeGeometry → EdgesGeometry
//    (sleek wedge profile with visible hard-light edges, not solid boxes)
//  • Torus wheels at front/rear axles
//  • Cockpit glow point (white-hot, blooms hard)
//  • Identity disc ring (contestants only — visible from overhead)
//  • Headlight cone with volumetric forward beam
//  • Enhanced ground glow pool
//  • Proper .dispose() cleanup on removeCycle
// ═══════════════════════════════════════════════════════════════════════════
import * as THREE from 'three';
import { buildTrail, updateTrailClock } from './trails.mjs';
import { createPathSystem } from './pathing.mjs';

// ── Contestant Palette ───────────────────────────────────────────────────
const COLORS = {
  TRON:    0x00f3ff,   // Cyan — the hero
  CLU:     0xffaa00,   // Amber/Gold — the tyrant
  RINZLER: 0xff4400,   // Orange-Red — the enforcer
  QUORRA:  0xcc44ff,   // Purple — the ISO
  GEM:     0x88ddff,   // Ice Blue — the siren
  SARK:    0xff0044,   // Red — the commander
  CASTOR:  0xff00ff,   // Magenta — the wildcard
  YORI:    0x00ff88,   // Green — the architect
  RAM:     0xff66aa,   // Pink — the believer
  FLYNN:   0x4488ff,   // Blue — the creator
};
const AMBIENT_HEX = 0x006677;

// ── Glow texture cache ───────────────────────────────────────────────────
const glowCache = {};
function getGlowTexture(hex) {
  if (glowCache[hex]) return glowCache[hex];
  const sz = 64, c = document.createElement('canvas');
  c.width = sz; c.height = sz;
  const ctx = c.getContext('2d');
  const col = new THREE.Color(hex);
  const r = Math.round(col.r * 255), g = Math.round(col.g * 255), b = Math.round(col.b * 255);
  const grad = ctx.createRadialGradient(sz / 2, sz / 2, 0, sz / 2, sz / 2, sz / 2);
  grad.addColorStop(0, `rgba(${r},${g},${b},1)`);
  grad.addColorStop(0.25, `rgba(${r},${g},${b},0.4)`);
  grad.addColorStop(0.6, `rgba(${r},${g},${b},0.08)`);
  grad.addColorStop(1, `rgba(${r},${g},${b},0)`);
  ctx.fillStyle = grad;
  ctx.fillRect(0, 0, sz, sz);
  const tex = new THREE.CanvasTexture(c);
  glowCache[hex] = tex;
  return tex;
}

// ── Label texture ────────────────────────────────────────────────────────
function makeLabelTexture(stepName, contestantName, hex) {
  const c = document.createElement('canvas');
  c.width = 256; c.height = 48;
  const ctx = c.getContext('2d');
  const col = new THREE.Color(hex);
  const r = Math.round(col.r * 255), g = Math.round(col.g * 255), b = Math.round(col.b * 255);
  ctx.font = 'bold 18px "Share Tech Mono", monospace';
  ctx.fillStyle = `rgb(${r},${g},${b})`;
  ctx.fillText(`${contestantName} // ${stepName}`, 4, 20);
  ctx.font = '12px "Share Tech Mono", monospace';
  ctx.fillStyle = `rgba(${r},${g},${b},0.5)`;
  ctx.fillText('\u25A0 ONLINE', 4, 38);
  return new THREE.CanvasTexture(c);
}

// ══════════════════════════════════════════════════════════════════════════
//  WIREFRAME CYCLE BUILDER
//  Uses Shape → ExtrudeGeometry → EdgesGeometry for clean hard-light edges.
//  Side profile: sleek wedge with cockpit bulge. Extruded for width.
//  From overhead: elongated diamond/hexoid shape with bright wireframe.
// ══════════════════════════════════════════════════════════════════════════
function buildCycleMesh(hex, isAmbient) {
  const grp = new THREE.Group();
  const s = 2.6;

  // ── Define side profile (x = forward, y = up) ─────────────────────────
  const shape = new THREE.Shape();
  shape.moveTo(-15 * s, 0);           // bottom rear
  shape.lineTo(-15 * s, 2 * s);       // rear face up
  shape.lineTo(-10 * s, 7 * s);       // rising to cockpit
  shape.lineTo(-2 * s, 9 * s);        // cockpit rear peak
  shape.lineTo(5 * s, 9 * s);         // cockpit front peak
  shape.lineTo(14 * s, 4 * s);        // nose slope
  shape.lineTo(18 * s, 1.5 * s);      // nose tip
  shape.lineTo(16 * s, 0);            // nose bottom
  shape.lineTo(-15 * s, 0);           // close

  const width = 5 * s;
  const bodyGeo = new THREE.ExtrudeGeometry(shape, {
    depth: width * 2,
    bevelEnabled: false
  });
  // Center the extrusion along z
  bodyGeo.translate(0, 0, -width);

  // ── Wireframe edges (the hard-light skeleton) ─────────────────────────
  const edgesGeo = new THREE.EdgesGeometry(bodyGeo, 15);
  const edgeMat = new THREE.LineBasicMaterial({
    color: hex, transparent: true, opacity: 0.85
  });
  const wireframe = new THREE.LineSegments(edgesGeo, edgeMat);
  grp.add(wireframe);

  // ── Ghost hull fill (barely visible translucent body) ─────────────────
  const hullMat = new THREE.MeshBasicMaterial({
    color: hex, transparent: true, opacity: 0.03,
    blending: THREE.AdditiveBlending, side: THREE.DoubleSide, depthWrite: false
  });
  const hull = new THREE.Mesh(bodyGeo, hullMat);
  grp.add(hull);

  // ── Front wheel (wireframe cylinder — Tron hard-light disc) ──────────
  //  CylinderGeometry axis = Y. After rotation.x = PI/2, axis → Z (correct
  //  axle for a forward-facing cycle). Wireframe triangulation is visible
  //  from every camera angle and rotation.y produces visible rolling spin.
  //  Width = 8*s to nearly span the full bike body width (10*s total).
  const fWheelR = 4.5 * s;
  const fWheelW = 8 * s;   // Almost as wide as the bike body
  const wheelMat = new THREE.MeshBasicMaterial({
    color: hex, wireframe: true, transparent: true, opacity: 0.65
  });
  const fWheel = new THREE.Mesh(
    new THREE.CylinderGeometry(fWheelR, fWheelR, fWheelW, 16, 1, false),
    wheelMat
  );
  fWheel.position.set(14 * s, fWheelR, 0);
  fWheel.rotation.x = Math.PI / 2;
  fWheel.name = 'frontWheel';
  grp.add(fWheel);

  // ── Rear wheel (slightly larger radius, same width) ──────────────────
  const rWheelR = 5 * s;
  const rWheel = new THREE.Mesh(
    new THREE.CylinderGeometry(rWheelR, rWheelR, fWheelW, 16, 1, false),
    wheelMat.clone()
  );
  rWheel.position.set(-13 * s, rWheelR, 0);
  rWheel.rotation.x = Math.PI / 2;
  rWheel.name = 'rearWheel';
  grp.add(rWheel);

  // ── Cockpit glow (soft emissive point that feeds bloom subtly) ───────
  const cockpit = new THREE.Mesh(
    new THREE.SphereGeometry(1.0 * s, 8, 8),
    new THREE.MeshBasicMaterial({
      color: hex, transparent: true, opacity: 0.6
    })
  );
  cockpit.position.set(2 * s, 9.5 * s, 0);
  grp.add(cockpit);

  // ── Ground glow pool (flat radial gradient on the floor) ──────────────
  const glowSize = 120;
  const glowPlane = new THREE.Mesh(
    new THREE.PlaneGeometry(glowSize, glowSize),
    new THREE.MeshBasicMaterial({
      map: getGlowTexture(hex), transparent: true,
      blending: THREE.AdditiveBlending, depthWrite: false,
      side: THREE.DoubleSide
    })
  );
  glowPlane.rotation.x = -Math.PI / 2;
  glowPlane.position.y = 0.5;
  grp.add(glowPlane);

  return grp;
}

/**
 * Create the complete cycle management system.
 *
 * @param {THREE.Scene} scene
 * @param {Object} cfg - { CELL, ARENA_X, ARENA_Z, HALF_X, HALF_Z, WALL_H, MAX_TRAIL, AMBIENT_MAX }
 * @param {Object} particles - particle system from particles.mjs
 * @returns {Object} { spawnCycle, derezzAll, update, cycles, COLORS, AMBIENT_HEX }
 */
export function createCycleSystem(scene, cfg, particles) {
  const { CELL, ARENA_X, ARENA_Z, HALF_X, HALF_Z, WALL_H, MAX_TRAIL, AMBIENT_MAX } = cfg;
  const snap = v => Math.round(v / CELL) * CELL;

  // ── Path system — pre-computed routes for cycles ────────────────────
  const pathing = createPathSystem(cfg);

  const cycles = [];

  function spawnCycle(stepName, contestant, isAmbient) {
    const hex = isAmbient ? AMBIENT_HEX : (COLORS[contestant] || 0x00f3ff);
    const speed = 2.4 + Math.random() * 1.2;

    // ── Compute path: contestants get reserved safe paths ────────────────
    const pathSteps = Math.ceil(10 * 60 * speed / CELL) + 5;
    const path = pathing.computeContestantPath(Math.max(pathSteps, 20));
    pathing.reservePath(path);
    let startX = path[0].x;
    let startZ = path[0].z;
    let dx, dz;
    if (path.length >= 2) {
      const ddx = path[1].x - path[0].x;
      const ddz = path[1].z - path[0].z;
      const len = Math.sqrt(ddx * ddx + ddz * ddz) || 1;
      dx = (ddx / len) * speed;
      dz = (ddz / len) * speed;
    } else {
      const dirs = [[speed, 0], [-speed, 0], [0, speed], [0, -speed]];
      [dx, dz] = dirs[Math.floor(Math.random() * 4)];
    }

    const mesh = buildCycleMesh(hex, false);
    mesh.position.set(startX, 0, startZ);
    mesh.rotation.y = Math.atan2(-dz, dx);
    scene.add(mesh);

    // Label sprite
    let label = null;
    if (stepName) {
      const tex = makeLabelTexture(stepName, contestant, hex);
      label = new THREE.Sprite(
        new THREE.SpriteMaterial({ map: tex, transparent: true, depthWrite: false })
      );
      label.scale.set(100, 18, 1);
      label.position.set(0, 50, 0);
      mesh.add(label);
    }

    const trail = buildTrail(hex, MAX_TRAIL, WALL_H);
    scene.add(trail.wallMesh);
    // Top/base edge lines removed — too bright at converging head point,
    // the shader's top corona and bottom glow handle this better.

    // Headlight — subtle forward glow plane
    let headlight = null;
    if (true) {
      const hlGeo = new THREE.PlaneGeometry(60, 8);
      const hlMat = new THREE.MeshBasicMaterial({
        color: hex, transparent: true, opacity: 0.06,
        blending: THREE.AdditiveBlending, side: THREE.DoubleSide, depthWrite: false
      });
      headlight = new THREE.Mesh(hlGeo, hlMat);
      headlight.position.set(startX, 4, startZ);
      headlight.rotation.y = Math.atan2(-dz, dx);
      scene.add(headlight);
    }

    cycles.push({
      stepName, contestant, hex, mesh, trail, label, headlight,
      x: startX, z: startZ, dx, dz, speed,
      life: 1, maxLife: 20000,
      born: performance.now(),
      pts: [], turnCD: 0, ambient: false, derezz: false,
      // ── Path following state ──────────────────────────────────────────
      path,
      pathIdx: 1,
      pathDone: false
    });
    particles.emitPulse(startX, startZ, hex);
  }

  function derezzAll() {
    // Clear path reservations — next spawn cycle starts fresh
    pathing.clearReservations();

    for (const c of cycles) {
      if (!c.ambient && c.life > 0 && !c.derezz) {
        c.derezz = true;
        c.derezzPhase = 0;  // multi-phase animation
        c.derezzStart = performance.now();

        // Phase 1: Initial flash — intense radial spark burst
        for (let i = 0; i < 80; i++) {
          const a = (i / 80) * Math.PI * 2 + Math.random() * 0.2;
          const sp = 1.5 + Math.random() * 8;
          const vy = Math.random() * 4;  // some sparks go up
          particles.emit(c.x, c.z, Math.cos(a) * sp, Math.sin(a) * sp, c.hex);
        }
        // Phase 1: White flash sparks from center
        for (let i = 0; i < 20; i++) {
          const a = Math.random() * Math.PI * 2;
          const sp = 3 + Math.random() * 5;
          particles.emit(c.x, c.z, Math.cos(a) * sp, Math.sin(a) * sp, 0xffffff);
        }
        // Secondary colored debris burst (scattered, not a ring)
        for (let i = 0; i < 60; i++) {
          const a = Math.random() * Math.PI * 2;
          const sp = 2 + Math.random() * 7;
          const ox = (Math.random() - 0.5) * 12;
          const oz = (Math.random() - 0.5) * 12;
          particles.emit(c.x + ox, c.z + oz, Math.cos(a) * sp, Math.sin(a) * sp, c.hex);
        }
      }
    }
  }

  function removeCycle(i) {
    const c = cycles[i];
    scene.remove(c.mesh);
    scene.remove(c.trail.wallMesh);
    if (c.headlight) scene.remove(c.headlight);
    // Dispose all geometries + materials + textures in the cycle group
    c.mesh.traverse(o => {
      if (o.geometry) o.geometry.dispose();
      if (o.material) {
        if (o.material.map) o.material.map.dispose();
        o.material.dispose();
      }
    });
    c.trail.wallGeo.dispose(); c.trail.wallMat.dispose();
    if (c.headlight) {
      c.headlight.geometry?.dispose();
      c.headlight.material?.dispose();
    }
    cycles.splice(i, 1);
  }

  /**
   * Per-frame cycle update: movement, wall bouncing, turning, trail, particles.
   * @param {number} now - performance.now()
   */
  function update(now) {
    for (let i = cycles.length - 1; i >= 0; i--) {
      const c = cycles[i];

      // ── Derezz animation — multi-phase dissolution ───────────────────
      if (c.derezz) {
        const elapsed = now - c.derezzStart;
        const phase = elapsed / 800;  // 0→1 over 800ms

        if (phase < 0.3) {
          // Phase 1: Flash bright, jitter position (glitch)
          c.mesh.position.x = c.x + (Math.random() - 0.5) * 6;
          c.mesh.position.z = c.z + (Math.random() - 0.5) * 6;
          c.mesh.position.y = (Math.random() - 0.5) * 3;
          c.life = 1.0;
        } else if (phase < 0.7) {
          // Phase 2: Break apart — scale distorts, sparks continue
          const t = (phase - 0.3) / 0.4;  // 0→1 within this phase
          c.mesh.position.set(c.x, t * 3, c.z);
          c.mesh.scale.set(1.0 + t * 0.3, 1.0 - t * 0.6, 1.0 + t * 0.3);
          c.life = 1.0 - t * 0.5;
          // Continuous debris sparks
          if (Math.random() < 0.6) {
            const a = Math.random() * Math.PI * 2;
            const sp = 1 + Math.random() * 3;
            particles.emit(c.x, c.z, Math.cos(a) * sp, Math.sin(a) * sp, c.hex);
          }
        } else {
          // Phase 3: Collapse to nothing
          const t = (phase - 0.7) / 0.3;  // 0→1
          c.life = 0.5 * (1.0 - t);
          c.mesh.scale.setScalar(Math.max(0.01, 0.5 * (1.0 - t)));
          c.mesh.position.y = (1.0 - t) * 3;
        }

        // Trail dissolves throughout
        c.trail.wallMat.uniforms.uOpacity.value = 0.65 * c.life;

        if (phase >= 1.0) { removeCycle(i); continue; }
        continue;
      }

      // ── Age out ───────────────────────────────────────────────────────
      if (now - c.born > c.maxLife) {
        c.life -= 0.008;
        if (c.life <= 0) { removeCycle(i); continue; }
      }

      // ── Path-Following Movement ──────────────────────────────────────
      // Follow pre-computed waypoints. When waypoint reached, snap-turn
      // to face next waypoint. When path exhausted:
      //   Runners: derezz (they hit a wall or trail)
      //   Contestants: continue with random freeform movement
      if (!c.pathDone && c.pathIdx < c.path.length) {
        const wp = c.path[c.pathIdx];
        const distX = wp.x - c.x;
        const distZ = wp.z - c.z;
        const dist = Math.sqrt(distX * distX + distZ * distZ);

        if (dist < c.speed * 1.5) {
          // Reached waypoint — snap to it
          c.x = wp.x;
          c.z = wp.z;
          c.pathIdx++;

          if (c.pathIdx < c.path.length) {
            // Steer toward next waypoint
            const next = c.path[c.pathIdx];
            const nx = next.x - c.x;
            const nz = next.z - c.z;
            const nLen = Math.sqrt(nx * nx + nz * nz) || 1;
            c.dx = (nx / nLen) * c.speed;
            c.dz = (nz / nLen) * c.speed;

            // Snap-turn spark burst (grid intersection turn)
            if (Math.random() < 0.4) {
              for (let j = 0; j < 6; j++) {
                const a = Math.random() * Math.PI * 2;
                const sp = 0.5 + Math.random() * 2;
                particles.emit(c.x, c.z, Math.cos(a) * sp, Math.sin(a) * sp, c.hex);
              }
            }
          } else {
            // Path exhausted
            c.pathDone = true;
            // Switch to freeform random movement
            const dirs = [[c.speed, 0], [-c.speed, 0], [0, c.speed], [0, -c.speed]];
            [c.dx, c.dz] = dirs[Math.floor(Math.random() * 4)];
          }
        } else {
          // Travel toward waypoint
          c.x += c.dx;
          c.z += c.dz;
        }
      } else {
        // ── Freeform movement (post-path or fallback) ────────────────
        // Check ahead: if next cell is occupied by another trail, turn
        const nextX = c.x + c.dx;
        const nextZ = c.z + c.dz;
        const nextCol = pathing.toCol(nextX);
        const nextRow = pathing.toRow(nextZ);
        const curCol  = pathing.toCol(c.x);
        const curRow  = pathing.toRow(c.z);

        // Only check collision when entering a new grid cell
        const enteringNewCell = (nextCol !== curCol || nextRow !== curRow);
        const cellBlocked = enteringNewCell &&
          pathing.inBounds(nextCol, nextRow) &&
          pathing.isOccupied(nextCol, nextRow);

        if (cellBlocked) {
          // Trail wall ahead — force a 90° turn at this grid intersection
          c.x = snap(c.x); c.z = snap(c.z);
          // Try perpendicular directions, pick one that's unoccupied
          const perpDirs = Math.abs(c.dx) > 0.01
            ? [[0, c.speed], [0, -c.speed]]
            : [[c.speed, 0], [-c.speed, 0]];
          // Shuffle so we don't always pick the same direction
          if (Math.random() > 0.5) perpDirs.reverse();

          let turned = false;
          for (const [tdx, tdz] of perpDirs) {
            const tc = pathing.toCol(c.x + tdx * 2);
            const tr = pathing.toRow(c.z + tdz * 2);
            if (pathing.inBounds(tc, tr) && !pathing.isOccupied(tc, tr)) {
              c.dx = tdx;
              c.dz = tdz;
              turned = true;
              break;
            }
          }
          if (!turned) {
            // Boxed in — reverse (last resort)
            c.dx = -c.dx;
            c.dz = -c.dz;
          }
          c.turnCD = Math.ceil(CELL / c.speed) + 5;
          // Spark burst on forced turn
          for (let j = 0; j < 12; j++) {
            const a = Math.random() * Math.PI * 2;
            const sp = 1 + Math.random() * 3;
            particles.emit(c.x, c.z, Math.cos(a) * sp, Math.sin(a) * sp, c.hex);
          }
        } else {
          c.x += c.dx;
          c.z += c.dz;
        }

        // Arena Wall Bounce
        let bounced = false;
        if (c.x <= -HALF_X) { c.x = -HALF_X; bounced = true; }
        if (c.x >= HALF_X)  { c.x = HALF_X;  bounced = true; }
        if (c.z <= -HALF_Z) { c.z = -HALF_Z; bounced = true; }
        if (c.z >= HALF_Z)  { c.z = HALF_Z;  bounced = true; }

        if (bounced) {
          if (Math.abs(c.dx) > 0.01) {
            c.dz = (Math.random() > 0.5 ? 1 : -1) * c.speed;
            c.dx = 0;
          } else {
            c.dx = (Math.random() > 0.5 ? 1 : -1) * c.speed;
            c.dz = 0;
          }
          c.turnCD = Math.ceil(CELL / c.speed) + 10;
          for (let j = 0; j < 24; j++) {
            const a = Math.random() * Math.PI * 2;
            const sp = 2 + Math.random() * 4;
            particles.emit(c.x, c.z, Math.cos(a) * sp, Math.sin(a) * sp, c.hex);
          }
        }

        // Grid Intersection Turns (freeform mode only)
        c.turnCD--;
        const sp = Math.max(Math.abs(c.dx), Math.abs(c.dz)) + 0.5;
        const onX = Math.abs(c.x % CELL) < sp || Math.abs(c.x % CELL - CELL) < sp;
        const onZ = Math.abs(c.z % CELL) < sp || Math.abs(c.z % CELL - CELL) < sp;
        if (onX && onZ && c.turnCD <= 0 && Math.random() < (c.ambient ? 0.06 : 0.15)) {
          c.x = snap(c.x); c.z = snap(c.z);
          // Check occupancy before turning — pick an unblocked direction
          const candDirs = [];
          if (Math.abs(c.dx) > 0.01) {
            // Currently moving on X axis, try Z turns
            for (const zDir of [1, -1]) {
              const tc = pathing.toCol(c.x);
              const tr = pathing.toRow(c.z + zDir * c.speed * 2);
              if (!pathing.isOccupied(tc, tr)) candDirs.push([0, zDir * c.speed]);
            }
          } else {
            // Currently moving on Z axis, try X turns
            for (const xDir of [1, -1]) {
              const tc = pathing.toCol(c.x + xDir * c.speed * 2);
              const tr = pathing.toRow(c.z);
              if (!pathing.isOccupied(tc, tr)) candDirs.push([xDir * c.speed, 0]);
            }
          }
          if (candDirs.length > 0) {
            [c.dx, c.dz] = candDirs[Math.floor(Math.random() * candDirs.length)];
            c.turnCD = Math.ceil(CELL / c.speed) + 5;
          }
        }
      }

      // ── Register current cell as occupied (real-time trail tracking) ──
      const curCellCol = pathing.toCol(c.x);
      const curCellRow = pathing.toRow(c.z);
      pathing.registerCell(curCellCol, curCellRow);

      // ── Update mesh position + heading ────────────────────────────────
      c.mesh.position.set(c.x, 0, c.z);
      if (Math.abs(c.dx) > 0.01 || Math.abs(c.dz) > 0.01) {
        const facing = Math.atan2(-c.dz, c.dx);
        c.mesh.rotation.y = facing;
        if (c.headlight) {
          c.headlight.position.set(c.x, WALL_H * 0.3, c.z);
          c.headlight.rotation.y = facing;
        }
      }

      // ── Spin wheels ───────────────────────────────────────────────────
      const fW = c.mesh.getObjectByName('frontWheel');
      const rW = c.mesh.getObjectByName('rearWheel');
      const wheelSpin = c.speed * 0.08;
      if (fW) fW.rotation.y += wheelSpin;  // Spin around pre-tilt Y = visible roll
      if (rW) rW.rotation.y += wheelSpin;

      // ── Trail Wall Update ─────────────────────────────────────────────
      c.pts.push([c.x, c.z]);
      if (c.pts.length > MAX_TRAIL) c.pts.shift();

      const t = c.trail;
      const n = c.pts.length;
      const nMinus1 = Math.max(n - 1, 1);
      for (let j = 0; j < n; j++) {
        const px = c.pts[j][0], pz = c.pts[j][1];
        const u = j / nMinus1;

        const bi = (j * 2) * 3, ti = (j * 2 + 1) * 3;
        t.wallPos[bi] = px;   t.wallPos[bi + 1] = 0;       t.wallPos[bi + 2] = pz;
        t.wallPos[ti] = px;   t.wallPos[ti + 1] = WALL_H;  t.wallPos[ti + 2] = pz;

        const bUV = (j * 2) * 2, tUV = (j * 2 + 1) * 2;
        t.wallUV[bUV] = u;   t.wallUV[bUV + 1] = 0;
        t.wallUV[tUV] = u;   t.wallUV[tUV + 1] = 1;

        t.topPos[j * 3] = px;  t.topPos[j * 3 + 1] = WALL_H; t.topPos[j * 3 + 2] = pz;
        t.basePos[j * 3] = px; t.basePos[j * 3 + 1] = 0.2;   t.basePos[j * 3 + 2] = pz;
      }
      t.wallGeo.setDrawRange(0, Math.max(0, (n - 1) * 6));
      t.wallGeo.attributes.position.needsUpdate = true;
      t.wallGeo.attributes.uv.needsUpdate = true;

      // Fade with life
      t.wallMat.uniforms.uOpacity.value = 0.65 * c.life;

      // ── Continuous head-spark emission ────────────────────────────────
      if (!c.derezz && Math.random() < 0.5) {
        const sparkSpread = 2;
        const svx = -c.dx * 0.5 + (Math.random() - 0.5) * sparkSpread;
        const svz = -c.dz * 0.5 + (Math.random() - 0.5) * sparkSpread;
        particles.emit(c.x, c.z, svx, svz, c.hex);
      }
    }

    // ── Update shared trail clock ───────────────────────────────────────
    updateTrailClock(now);
  }

  return {
    spawnCycle,
    derezzAll,
    removeCycle,
    update,
    cycles,
    pathing,
    COLORS,
    AMBIENT_HEX
  };
}
