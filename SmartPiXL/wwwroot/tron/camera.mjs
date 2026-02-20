// ═══════════════════════════════════════════════════════════════════════════
//  camera.mjs — Cinematic camera presets with smooth transitions
//
//  Five presets inspired by Tron Legacy cinematography:
//  • OVERHEAD  — Default high angle, slow orbital drift
//  • SWEEP     — Low sweeping arc around the arena
//  • FLOOR     — Near-ground, shows reflective floor
//  • DRAMATIC  — Low angle looking up at trail walls (skyscraper effect)
//  • TRACK     — Auto-follows most recent contestant cycle
//
//  Keyboard shortcuts 1-5 for manual preset selection.
//  Auto-cycles between presets every 30 seconds when idle.
// ═══════════════════════════════════════════════════════════════════════════
import * as THREE from 'three';

const PRESETS = {
  OVERHEAD: {
    name: 'OVERHEAD',
    position: new THREE.Vector3(0, 800, 500),
    lookAt: new THREE.Vector3(0, 0, 0),
    drift: true,
    driftRadius: 150,
    driftYAmp: 60,
    driftSpeed: 0.00006
  },
  SWEEP: {
    name: 'SWEEP',
    position: new THREE.Vector3(800, 220, 0),
    lookAt: new THREE.Vector3(0, 40, 0),
    drift: true,
    driftRadius: 850,
    driftYAmp: 40,
    driftSpeed: 0.00012
  },
  FLOOR: {
    name: 'FLOOR',
    position: new THREE.Vector3(500, 20, 200),
    lookAt: new THREE.Vector3(-200, 15, -50),
    drift: true,
    driftRadius: 80,
    driftYAmp: 5,
    driftSpeed: 0.00008
  },
  DRAMATIC: {
    name: 'DRAMATIC',
    position: new THREE.Vector3(0, 45, 350),
    lookAt: new THREE.Vector3(0, 80, -100),
    drift: true,
    driftRadius: 200,
    driftYAmp: 15,
    driftSpeed: 0.0001
  },
  TRACK: {
    name: 'TRACK',
    position: new THREE.Vector3(0, 400, 300),
    lookAt: new THREE.Vector3(0, 0, 0),
    drift: false,
    follow: true
  }
};

const PRESET_LIST = ['OVERHEAD', 'SWEEP', 'FLOOR', 'DRAMATIC', 'TRACK'];
const TRANSITION_DURATION = 2000;  // ms for smooth camera transitions
const AUTO_CYCLE_INTERVAL = 35000; // ms between auto-preset changes

function easeInOutCubic(t) {
  return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
}

/**
 * Create the camera system.
 *
 * @param {THREE.PerspectiveCamera} camera
 * @param {Object} cfg - { HALF_X, HALF_Z, WALL_H }
 * @returns {Object} { update, setPreset, getPresetName, bindKeys, getCycles }
 */
export function createCameraSystem(camera, cfg) {
  let currentPreset = PRESETS.OVERHEAD;
  let targetPreset  = PRESETS.OVERHEAD;

  // Transition state
  let transitioning = false;
  let transitionStart = 0;
  let fromPos = camera.position.clone();
  let fromTarget = new THREE.Vector3(0, 0, 0);
  let toPos = currentPreset.position.clone();
  let toTarget = currentPreset.lookAt.clone();

  // Auto-cycle
  let autoCycleEnabled = true;
  let lastPresetChange = performance.now();
  let currentIndex = 0;

  // Track ref for follow mode (set externally)
  let followTarget = null;

  // Working vectors (reused each frame to avoid allocations)
  const _pos = new THREE.Vector3();
  const _look = new THREE.Vector3();

  function setPreset(name) {
    const preset = PRESETS[name];
    if (!preset || preset === currentPreset) return;

    fromPos.copy(camera.position);
    fromTarget.copy(_look);
    targetPreset = preset;
    toPos.copy(preset.position);
    toTarget.copy(preset.lookAt);
    transitioning = true;
    transitionStart = performance.now();
    lastPresetChange = performance.now();
  }

  function setFollowTarget(cycleRef) {
    followTarget = cycleRef;
  }

  let _reEnableTimer = null;

  function bindKeys() {
    window.addEventListener('keydown', (e) => {
      const num = parseInt(e.key);
      if (num >= 1 && num <= 5) {
        autoCycleEnabled = false;
        setPreset(PRESET_LIST[num - 1]);
        // Re-enable auto-cycle after 60 seconds of no input
        clearTimeout(_reEnableTimer);
        _reEnableTimer = setTimeout(() => { autoCycleEnabled = true; }, 60000);
      }
      // 0 to re-enable auto-cycle
      if (e.key === '0') {
        autoCycleEnabled = true;
        clearTimeout(_reEnableTimer);
      }
    });
  }

  /**
   * Per-frame camera update. Handles transitions, drift, and follow mode.
   * @param {number} now - performance.now()
   * @param {Array} cycles - Current cycle array (for follow mode)
   */
  function update(now, cycles) {
    // Auto-cycle presets
    if (autoCycleEnabled && now - lastPresetChange > AUTO_CYCLE_INTERVAL) {
      currentIndex = (currentIndex + 1) % PRESET_LIST.length;
      setPreset(PRESET_LIST[currentIndex]);
    }

    // Handle transition
    if (transitioning) {
      const elapsed = now - transitionStart;
      const t = Math.min(elapsed / TRANSITION_DURATION, 1);
      const eased = easeInOutCubic(t);

      _pos.lerpVectors(fromPos, toPos, eased);
      _look.lerpVectors(fromTarget, toTarget, eased);
      camera.position.copy(_pos);
      camera.lookAt(_look);

      if (t >= 1) {
        transitioning = false;
        currentPreset = targetPreset;
      }
      return;
    }

    // Active preset behavior
    const p = currentPreset;

    // Follow mode — track the most recent contestant cycle
    if (p.follow && cycles) {
      const contestant = cycles.filter(c => !c.ambient && c.life > 0);
      const target = contestant.length > 0 ? contestant[contestant.length - 1] : null;
      if (target) {
        const followPos = new THREE.Vector3(
          target.x - target.dx * 200,
          300 + Math.sin(now * 0.0003) * 30,
          target.z - target.dz * 200 + 200
        );
        camera.position.lerp(followPos, 0.02);
        _look.set(target.x, 10, target.z);
        camera.lookAt(_look);
      } else {
        // No contestant — fall back to overhead drift
        applyDrift(camera, PRESETS.OVERHEAD, now);
      }
      return;
    }

    // Orbital drift
    if (p.drift) {
      applyDrift(camera, p, now);
    }
  }

  function applyDrift(camera, preset, now) {
    const t = now * preset.driftSpeed;
    const basePos = preset.position;
    camera.position.x = basePos.x + Math.sin(t) * preset.driftRadius;
    camera.position.z = basePos.z + Math.cos(t * 0.7) * preset.driftRadius;
    camera.position.y = basePos.y + Math.sin(t * 0.5) * preset.driftYAmp;
    _look.copy(preset.lookAt);
    camera.lookAt(_look);
  }

  return {
    update,
    setPreset,
    setFollowTarget,
    bindKeys,
    getPresetName: () => currentPreset.name,
    PRESETS,
    PRESET_LIST
  };
}
