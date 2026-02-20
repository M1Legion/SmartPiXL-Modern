// ═══════════════════════════════════════════════════════════════════════════
//  scene.mjs — Entry point: renderer, scene, camera, bloom, animate loop
//
//  This is the orchestrator. It imports all subsystem modules, initializes
//  the Three.js scene, sets up post-processing, and runs the animate loop.
//
//  Called from tron.html:
//    import { initScene } from './tron/scene.mjs';
//    initScene(document.getElementById('grid-canvas'));
// ═══════════════════════════════════════════════════════════════════════════
import * as THREE from 'three';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass }     from 'three/addons/postprocessing/RenderPass.js';
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js';
import { OutputPass }     from 'three/addons/postprocessing/OutputPass.js';

import { buildArena, updateArena }       from './arena.mjs';
import { createCycleSystem }             from './cycles.mjs';
import { createParticleSystem }          from './particles.mjs';
import { createCameraSystem }            from './camera.mjs';
import { exposeGridAPI }                 from './api.mjs';

/**
 * Initialize the complete 3D Tron arena scene.
 *
 * @param {HTMLCanvasElement} canvas - The canvas element to render into
 * @returns {Object} The window.Grid API for external use
 */
export function initScene(canvas) {
  'use strict';

  // ══════════════════════════════════════════════════════════════════════
  //  CONFIG
  // ══════════════════════════════════════════════════════════════════════
  const CELL       = 50;
  const ARENA_X    = 1400;
  const ARENA_Z    = 900;
  const HALF_X     = ARENA_X / 2;
  const HALF_Z     = ARENA_Z / 2;
  const WALL_H     = 24;
  const MAX_TRAIL  = 300;
  const AMBIENT_MAX = 6;

  const cfg = { CELL, ARENA_X, ARENA_Z, HALF_X, HALF_Z, WALL_H, MAX_TRAIL, AMBIENT_MAX };

  // ══════════════════════════════════════════════════════════════════════
  //  RENDERER — WebGL 2, ACES tone mapping, full pixel ratio (RTX 4090)
  // ══════════════════════════════════════════════════════════════════════
  let W = window.innerWidth, H = window.innerHeight;

  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
  renderer.setPixelRatio(window.devicePixelRatio);
  renderer.setSize(W, H);
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.0;
  renderer.setClearColor(0x000308, 1);

  // ══════════════════════════════════════════════════════════════════════
  //  SCENE + FOG
  // ══════════════════════════════════════════════════════════════════════
  const scene = new THREE.Scene();
  scene.fog = new THREE.FogExp2(0x000308, 0.00022);

  // ══════════════════════════════════════════════════════════════════════
  //  CAMERA
  //  Starts at OVERHEAD preset position. Camera module handles transitions.
  // ══════════════════════════════════════════════════════════════════════
  const camera = new THREE.PerspectiveCamera(55, W / H, 1, 6000);
  camera.position.set(0, 800, 500);
  camera.lookAt(0, 0, 0);

  // ══════════════════════════════════════════════════════════════════════
  //  LIGHTING — Minimal. The Tron world is built from emissive light,
  //  not illuminated by external sources. This ambient fill is very subtle,
  //  just enough to give the reflective surfaces a hint of definition.
  // ══════════════════════════════════════════════════════════════════════
  scene.add(new THREE.AmbientLight(0x0a1020, 0.2));
  const dirLight = new THREE.DirectionalLight(0x223344, 0.25);
  dirLight.position.set(200, 500, 300);
  scene.add(dirLight);

  // ══════════════════════════════════════════════════════════════════════
  //  BLOOM POST-PROCESSING
  //  The bloom pass IS the Tron look. Everything that should glow has
  //  emissive values exceeding the threshold. Everything else stays dark.
  // ══════════════════════════════════════════════════════════════════════
  const composer = new EffectComposer(renderer);
  composer.addPass(new RenderPass(scene, camera));

  const bloom = new UnrealBloomPass(
    new THREE.Vector2(W, H),
    0.65,   // strength — controlled glow, no wash-out at oblique angles
    0.4,    // radius — tight focused halos
    0.45    // threshold — only hot emissives bloom, keeps dark surfaces crisp
  );
  composer.addPass(bloom);
  composer.addPass(new OutputPass());

  // ══════════════════════════════════════════════════════════════════════
  //  SUBSYSTEMS — Each module builds its piece of the world
  // ══════════════════════════════════════════════════════════════════════
  const arena     = buildArena(scene, cfg);
  const particles = createParticleSystem(scene, cfg);
  const cycleSystem = createCycleSystem(scene, cfg, particles);
  const cameraCtl = createCameraSystem(camera, cfg);

  // Bind keyboard shortcuts (1-5 for camera presets)
  cameraCtl.bindKeys();

  // Expose the public API (window.Grid)
  exposeGridAPI(cycleSystem, particles);

  // ══════════════════════════════════════════════════════════════════════
  //  MAIN UPDATE + RENDER LOOP
  // ══════════════════════════════════════════════════════════════════════
  function update(now) {
    cycleSystem.update(now);
    particles.updateParticles();
    particles.updateMotes(now);
    particles.updatePulseRings();
    updateArena(arena, now);
    cameraCtl.update(now, cycleSystem.cycles);
  }

  function animate() {
    update(performance.now());
    composer.render();
    requestAnimationFrame(animate);
  }

  // ══════════════════════════════════════════════════════════════════════
  //  RESIZE HANDLER
  // ══════════════════════════════════════════════════════════════════════
  function onResize() {
    W = window.innerWidth;
    H = window.innerHeight;
    camera.aspect = W / H;
    camera.updateProjectionMatrix();
    renderer.setSize(W, H);
    composer.setSize(W, H);
    bloom.resolution.set(W, H);
  }
  window.addEventListener('resize', onResize);

  // ══════════════════════════════════════════════════════════════════════
  //  START
  // ══════════════════════════════════════════════════════════════════════
  requestAnimationFrame(animate);

  return window.Grid;
}
