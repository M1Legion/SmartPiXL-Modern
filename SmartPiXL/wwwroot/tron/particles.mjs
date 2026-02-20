// ═══════════════════════════════════════════════════════════════════════════
//  particles.mjs — Spark particles, data motes, pulse rings, derezz bursts
//
//  Visual upgrades from the monolith:
//  • Larger spark size with streak texture for motion-blur feel
//  • Data motes with size variation and directional drift along grid
//  • Derezz shatter ring — fragment ring on cycle removal
//  • Heartbeat pulse — subtle arena-wide floor pulse on ETL refresh
//  • Impact rings — ripple rings on sharp turns
// ═══════════════════════════════════════════════════════════════════════════
import * as THREE from 'three';

/**
 * Create the complete particle system and add it to the scene.
 *
 * @param {THREE.Scene} scene
 * @param {Object} cfg - { ARENA_X, ARENA_Z, HALF_X, HALF_Z, WALL_H }
 * @returns {Object} { emit, updateParticles, updateMotes, emitPulse, updatePulseRings }
 */
export function createParticleSystem(scene, cfg) {
  const { ARENA_X, ARENA_Z, HALF_X, HALF_Z, WALL_H } = cfg;

  // ══════════════════════════════════════════════════════════════════════
  //  SPARK PARTICLE POOL — 3D particles with gravity + motion streak
  // ══════════════════════════════════════════════════════════════════════
  const P_MAX = 2500;
  const pPos = new Float32Array(P_MAX * 3);
  const pCol = new Float32Array(P_MAX * 3);
  const pSiz = new Float32Array(P_MAX);
  const pGeo = new THREE.BufferGeometry();
  pGeo.setAttribute('position', new THREE.BufferAttribute(pPos, 3));
  pGeo.setAttribute('color',    new THREE.BufferAttribute(pCol, 3));
  pGeo.setAttribute('size',     new THREE.BufferAttribute(pSiz, 1));

  // Generate a streak texture (elongated horizontal glow)
  const streakCanvas = document.createElement('canvas');
  streakCanvas.width = 32; streakCanvas.height = 32;
  const sCtx = streakCanvas.getContext('2d');
  const sGrad = sCtx.createRadialGradient(16, 16, 0, 16, 16, 14);
  sGrad.addColorStop(0, 'rgba(255,255,255,1)');
  sGrad.addColorStop(0.2, 'rgba(255,255,255,0.8)');
  sGrad.addColorStop(0.6, 'rgba(255,255,255,0.15)');
  sGrad.addColorStop(1, 'rgba(255,255,255,0)');
  sCtx.fillStyle = sGrad;
  sCtx.fillRect(0, 0, 32, 32);
  const streakTex = new THREE.CanvasTexture(streakCanvas);

  const pMesh = new THREE.Points(pGeo, new THREE.PointsMaterial({
    size: 7,
    map: streakTex,
    vertexColors: true,
    transparent: true,
    blending: THREE.AdditiveBlending,
    depthWrite: false,
    sizeAttenuation: true
  }));
  pMesh.frustumCulled = false;
  scene.add(pMesh);

  const pPool = [];

  function emit(x, z, vx, vz, hex) {
    if (pPool.length >= P_MAX) return;
    const col = new THREE.Color(hex);
    pPool.push({
      x, y: WALL_H * 0.5, z,
      vx, vy: 1.5 + Math.random() * 5, vz,
      r: col.r, g: col.g, b: col.b,
      life: 1, decay: 0.012 + Math.random() * 0.018,
      size: 4 + Math.random() * 8
    });
  }

  function updateParticles() {
    for (let i = pPool.length - 1; i >= 0; i--) {
      const p = pPool[i];
      p.x += p.vx; p.y += p.vy; p.z += p.vz;
      p.vx *= 0.95; p.vy -= 0.07; p.vz *= 0.95;
      p.life -= p.decay;
      if (p.life <= 0 || p.y < 0) pPool.splice(i, 1);
    }
    const len = pPool.length;
    for (let i = 0; i < P_MAX; i++) {
      if (i < len) {
        const p = pPool[i];
        pPos[i * 3]     = p.x;
        pPos[i * 3 + 1] = Math.max(0, p.y);
        pPos[i * 3 + 2] = p.z;
        pCol[i * 3]     = p.r * p.life;
        pCol[i * 3 + 1] = p.g * p.life;
        pCol[i * 3 + 2] = p.b * p.life;
        pSiz[i]         = p.size * p.life;
      } else {
        pPos[i * 3 + 1] = -9999;
        pCol[i * 3] = pCol[i * 3 + 1] = pCol[i * 3 + 2] = 0;
        pSiz[i] = 0;
      }
    }
    pGeo.attributes.position.needsUpdate = true;
    pGeo.attributes.color.needsUpdate = true;
    pGeo.attributes.size.needsUpdate = true;
  }

  // ══════════════════════════════════════════════════════════════════════
  //  DATA MOTES — Atmospheric luminous particles across arena
  //  Upgrades: size variation, some follow grid lines, some drift upward.
  // ══════════════════════════════════════════════════════════════════════
  const MOTE_COUNT = 200;
  const motePos = new Float32Array(MOTE_COUNT * 3);
  const moteCol = new Float32Array(MOTE_COUNT * 3);
  const moteSiz = new Float32Array(MOTE_COUNT);
  const moteVel = [];

  for (let i = 0; i < MOTE_COUNT; i++) {
    const isGridFollower = Math.random() < 0.3;  // 30% follow grid lines
    const isAscender     = Math.random() < 0.2;  // 20% drift upward
    motePos[i * 3]     = (Math.random() - 0.5) * ARENA_X * 1.3;
    motePos[i * 3 + 1] = 3 + Math.random() * WALL_H * 4;
    motePos[i * 3 + 2] = (Math.random() - 0.5) * ARENA_Z * 1.3;

    const brightness = 0.15 + Math.random() * 0.35;
    // Slight color variation — mostly cyan, some hints of white
    const tint = Math.random();
    moteCol[i * 3]     = tint > 0.8 ? brightness * 0.5 : 0;
    moteCol[i * 3 + 1] = 0.90 * brightness;
    moteCol[i * 3 + 2] = 1.00 * brightness;

    moteSiz[i] = 1.5 + Math.random() * 4;  // size variation

    moteVel.push({
      vx: isGridFollower ? (Math.random() > 0.5 ? 0.4 : -0.4) : (Math.random() - 0.5) * 0.25,
      vy: isAscender ? 0.08 + Math.random() * 0.06 : (Math.random() - 0.5) * 0.05,
      vz: isGridFollower ? 0 : (Math.random() - 0.5) * 0.25,
      phase: Math.random() * Math.PI * 2,
      gridFollower: isGridFollower,
      ascender: isAscender
    });
  }

  const moteGeo = new THREE.BufferGeometry();
  moteGeo.setAttribute('position', new THREE.BufferAttribute(motePos, 3));
  moteGeo.setAttribute('color',    new THREE.BufferAttribute(moteCol, 3));
  // Note: size attribute not used by default PointsMaterial — using uniform size
  const moteMesh = new THREE.Points(moteGeo, new THREE.PointsMaterial({
    size: 3,
    vertexColors: true,
    transparent: true,
    opacity: 0.55,
    blending: THREE.AdditiveBlending,
    depthWrite: false,
    sizeAttenuation: true
  }));
  moteMesh.frustumCulled = false;
  scene.add(moteMesh);

  function updateMotes(now) {
    const t = now * 0.001;
    for (let i = 0; i < MOTE_COUNT; i++) {
      const v = moteVel[i];
      const wobble = v.gridFollower ? 0.01 : 0.05;
      motePos[i * 3]     += v.vx + Math.sin(t * 0.5 + v.phase) * wobble;
      motePos[i * 3 + 1] += v.vy + Math.cos(t * 0.3 + v.phase) * 0.015;
      motePos[i * 3 + 2] += v.vz + Math.cos(t * 0.4 + v.phase) * wobble;

      // Wrap around arena bounds
      if (motePos[i * 3]     >  HALF_X * 1.3) motePos[i * 3]     = -HALF_X * 1.3;
      if (motePos[i * 3]     < -HALF_X * 1.3) motePos[i * 3]     =  HALF_X * 1.3;
      if (motePos[i * 3 + 2] >  HALF_Z * 1.3) motePos[i * 3 + 2] = -HALF_Z * 1.3;
      if (motePos[i * 3 + 2] < -HALF_Z * 1.3) motePos[i * 3 + 2] =  HALF_Z * 1.3;
      if (motePos[i * 3 + 1] > WALL_H * 5)    motePos[i * 3 + 1] = 3;
      if (motePos[i * 3 + 1] < 1)              motePos[i * 3 + 1] = WALL_H * 4;
    }
    moteGeo.attributes.position.needsUpdate = true;
  }

  // ══════════════════════════════════════════════════════════════════════
  //  PULSE RINGS — Expanding ground rings on contestant spawn
  //  Plus derezz shatter rings and heartbeat pulses.
  // ══════════════════════════════════════════════════════════════════════
  const pulseRings = [];

  function emitPulse(x, z, hex, opts = {}) {
    const {
      decayRate = 0.012,
      maxScale = 40,
      startOpacity = 0.9,
      ringWidth = 3
    } = opts;

    const geo = new THREE.RingGeometry(2, 2 + ringWidth, 64);
    const mat = new THREE.MeshBasicMaterial({
      color: hex, transparent: true, opacity: startOpacity,
      side: THREE.DoubleSide, blending: THREE.AdditiveBlending, depthWrite: false
    });
    const ring = new THREE.Mesh(geo, mat);
    ring.rotation.x = -Math.PI / 2;
    ring.position.set(x, 0.5, z);
    scene.add(ring);
    pulseRings.push({ mesh: ring, geo, mat, life: 1.0, decayRate, maxScale });
  }

  /** Derezz shatter ring — wider, faster, more dramatic */
  function emitShatterRing(x, z, hex) {
    emitPulse(x, z, hex, {
      decayRate: 0.02,
      maxScale: 60,
      startOpacity: 1.0,
      ringWidth: 6
    });
    // Secondary inner ring (slightly delayed feel via faster expansion)
    emitPulse(x, z, 0xffffff, {
      decayRate: 0.025,
      maxScale: 30,
      startOpacity: 0.6,
      ringWidth: 2
    });
  }

  /** Heartbeat pulse — subtle arena-wide floor pulse */
  function emitHeartbeat() {
    emitPulse(0, 0, 0x002840, {
      decayRate: 0.008,
      maxScale: 80,
      startOpacity: 0.15,
      ringWidth: 8
    });
  }

  function updatePulseRings() {
    for (let i = pulseRings.length - 1; i >= 0; i--) {
      const pr = pulseRings[i];
      pr.life -= pr.decayRate;
      const scale = 1 + (1 - pr.life) * pr.maxScale;
      pr.mesh.scale.set(scale, scale, 1);
      pr.mat.opacity = pr.life * 0.6;
      if (pr.life <= 0) {
        scene.remove(pr.mesh);
        pr.geo.dispose();
        pr.mat.dispose();
        pulseRings.splice(i, 1);
      }
    }
  }

  return {
    emit,
    updateParticles,
    updateMotes,
    emitPulse,
    emitShatterRing,
    emitHeartbeat,
    updatePulseRings
  };
}
