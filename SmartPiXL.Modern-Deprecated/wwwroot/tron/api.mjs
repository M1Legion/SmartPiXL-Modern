// ═══════════════════════════════════════════════════════════════════════════
//  api.mjs — Public window.Grid API (contract with the DevOps UI shell)
//
//  This is the boundary between the 3D scene (owned by @web-design-operator)
//  and the 2D dashboard panels (owned by @devops-ui).
//
//  THE API SURFACE:
//    Grid.spawnOrb(stepName, contestant)  — spawn a named contestant cycle
//    Grid.derezzAll()                     — derezz all non-ambient cycles
//    Grid.emitPulse(x, z, hex)           — emit a floor pulse ring
//
//  ⚠ Changing this API is a breaking change. Coordinate with @devops-ui.
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Expose the public Grid API on the window object.
 *
 * @param {Object} cycleSystem    - From createCycleSystem()
 * @param {Object} particleSystem - From createParticleSystem()
 */
export function exposeGridAPI(cycleSystem, particleSystem) {
  window.Grid = {
    /** Spawn a named contestant cycle on the grid */
    spawnOrb(stepName, contestant) {
      cycleSystem.spawnCycle(stepName, contestant, false);
    },
    /** Derezz (destroy) all non-ambient contestant cycles */
    derezzAll() {
      cycleSystem.derezzAll();
    },
    /** Emit a floor pulse ring at the given position */
    emitPulse(x, z, hex) {
      particleSystem.emitPulse(x, z, hex);
    }
  };

  console.log(
    '%c\u26A1 [GRID] Modular Tron Arena — wireframe cycles, reflective floor, cinema bloom, 5 camera presets',
    'color:#00f3ff;font-weight:bold'
  );
}
