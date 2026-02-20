// ═══════════════════════════════════════════════════════════════════════════
//  pathing.mjs — Pre-computed grid paths for light cycles
//
//  Arena grid pathfinding system. Contestants get safe paths first,
//  then runners get deliberately fatal paths that avoid disrupting
//  contestant trails.
//
//  The grid is integer-based: cells are CELL units wide. All coordinates
//  snap to grid intersections. Movement is cardinal (±X or ±Z).
//
//  Algorithm (inspired by procedural maze generation):
//  1. Build an occupancy grid tracking which cells have active trails
//  2. Compute contestant paths using BFS-like forward search that
//     avoids occupied cells, walls, and other contestant trails
//  3. Reserve those cells in the occupancy grid
//  4. Compute runner paths that deliberately end at an occupied cell
//     or arena wall within a short lifetime
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Create the pathfinding system.
 *
 * @param {Object} cfg - { CELL, ARENA_X, ARENA_Z, HALF_X, HALF_Z }
 * @returns {Object} { computeContestantPath, computeRunnerPath, reservePath, clearReservations }
 */
export function createPathSystem(cfg) {
  const { CELL, HALF_X, HALF_Z } = cfg;

  // Grid dimensions in cells
  const COLS = Math.floor(HALF_X * 2 / CELL);   // 28
  const ROWS = Math.floor(HALF_Z * 2 / CELL);   // 18

  // World coord → grid coord
  const toCol = (x) => Math.round((x + HALF_X) / CELL);
  const toRow = (z) => Math.round((z + HALF_Z) / CELL);

  // Grid coord → world coord
  const toX = (col) => col * CELL - HALF_X;
  const toZ = (row) => row * CELL - HALF_Z;

  // Occupancy grid: true = occupied by a reserved trail
  // Stored as a flat Set of `col,row` strings for fast lookup
  const occupied = new Set();

  function isOccupied(col, row) {
    return occupied.has(`${col},${row}`);
  }

  function reserve(col, row) {
    occupied.add(`${col},${row}`);
  }

  function inBounds(col, row) {
    return col >= 1 && col < COLS && row >= 1 && row < ROWS;
  }

  // Cardinal directions: [dcol, drow]
  const DIRS = [[1, 0], [-1, 0], [0, 1], [0, -1]];

  /**
   * Compute a safe path for a contestant that will survive for `steps` cells.
   * Uses a randomized greedy walk that avoids occupied cells and arena walls.
   * If it gets stuck, it backtracks and tries another direction.
   *
   * @param {number} steps      - How many grid cells the path should cover
   * @param {number} [startCol] - Starting column (random if omitted)
   * @param {number} [startRow] - Starting row (random if omitted)
   * @returns {Array<{col, row, x, z}>} Path as array of waypoints
   */
  function computeContestantPath(steps, startCol, startRow) {
    // Pick a random unoccupied start if not specified
    if (startCol === undefined || startRow === undefined) {
      let attempts = 0;
      do {
        startCol = 2 + Math.floor(Math.random() * (COLS - 4));
        startRow = 2 + Math.floor(Math.random() * (ROWS - 4));
        attempts++;
      } while (isOccupied(startCol, startRow) && attempts < 100);
    }

    const path = [{ col: startCol, row: startRow }];
    const visited = new Set();
    visited.add(`${startCol},${startRow}`);

    let col = startCol, row = startRow;
    // Pick initial direction randomly
    let dir = DIRS[Math.floor(Math.random() * 4)];

    for (let i = 0; i < steps; i++) {
      // Try to continue in the current direction (straight preference)
      // If blocked, try turning. If all blocked, path ends early.
      const candidates = [];

      // Prefer current direction (weighted higher)
      const straightCol = col + dir[0];
      const straightRow = row + dir[1];
      if (inBounds(straightCol, straightRow) &&
          !isOccupied(straightCol, straightRow) &&
          !visited.has(`${straightCol},${straightRow}`)) {
        candidates.push({ d: dir, weight: 3 });
      }

      // Try perpendicular turns
      for (const d of DIRS) {
        if (d === dir) continue;
        if (d[0] === -dir[0] && d[1] === -dir[1]) continue; // No U-turns
        const nc = col + d[0], nr = row + d[1];
        if (inBounds(nc, nr) && !isOccupied(nc, nr) && !visited.has(`${nc},${nr}`)) {
          candidates.push({ d, weight: 1 });
        }
      }

      if (candidates.length === 0) break; // Stuck — path ends early

      // Weighted random selection
      const totalWeight = candidates.reduce((s, c) => s + c.weight, 0);
      let r = Math.random() * totalWeight;
      let chosen = candidates[0];
      for (const c of candidates) {
        r -= c.weight;
        if (r <= 0) { chosen = c; break; }
      }

      dir = chosen.d;
      col += dir[0];
      row += dir[1];
      path.push({ col, row });
      visited.add(`${col},${row}`);
    }

    // Convert to world coordinates
    return path.map(p => ({
      col: p.col, row: p.row,
      x: toX(p.col), z: toZ(p.row)
    }));
  }

  /**
   * Compute a runner path that WILL die (hit occupied trail or wall) within
   * `maxSteps`. Runners are "too dumb to last."
   *
   * Strategy: Start from a position near a contestant trail. Walk a few
   * cells freely, then steer toward an occupied cell or the arena wall.
   *
   * @param {number} maxSteps - Maximum path length before forced death
   * @returns {Array<{col, row, x, z}>} Path ending at a fatal cell
   */
  function computeRunnerPath(maxSteps) {
    // Pick a start position — try to find one near an occupied area
    let startCol, startRow;
    let attempts = 0;

    // Strategy: find a cell that's 2-4 cells away from an occupied cell
    do {
      startCol = 2 + Math.floor(Math.random() * (COLS - 4));
      startRow = 2 + Math.floor(Math.random() * (ROWS - 4));
      attempts++;
    } while (isOccupied(startCol, startRow) && attempts < 50);

    const path = [{ col: startCol, row: startRow }];
    const visited = new Set();
    visited.add(`${startCol},${startRow}`);

    let col = startCol, row = startRow;
    let dir = DIRS[Math.floor(Math.random() * 4)];

    // Walk for a random portion of maxSteps, then steer to death
    const freeSteps = 2 + Math.floor(Math.random() * Math.min(4, maxSteps - 2));
    const deathSteps = maxSteps - freeSteps;

    // Phase 1: Walk freely for a few cells
    for (let i = 0; i < freeSteps; i++) {
      const nc = col + dir[0], nr = row + dir[1];
      if (!inBounds(nc, nr) || visited.has(`${nc},${nr}`)) {
        // Turn randomly
        const turns = DIRS.filter(d => {
          if (d[0] === -dir[0] && d[1] === -dir[1]) return false;
          const tc = col + d[0], tr = row + d[1];
          return inBounds(tc, tr) && !visited.has(`${tc},${tr}`);
        });
        if (turns.length === 0) break;
        dir = turns[Math.floor(Math.random() * turns.length)];
      }
      col += dir[0];
      row += dir[1];
      if (!inBounds(col, row)) break; // Hit wall — dead
      path.push({ col, row });
      visited.add(`${col},${row}`);
    }

    // Phase 2: Steer toward death (occupied cell or wall)
    for (let i = 0; i < deathSteps; i++) {
      // Try to move toward an occupied cell or the arena edge
      let bestDir = dir;
      let bestScore = -Infinity;

      for (const d of DIRS) {
        if (d[0] === -dir[0] && d[1] === -dir[1]) continue; // No U-turns
        const nc = col + d[0], nr = row + d[1];

        let score = 0;
        // Reward moving toward walls
        if (nc <= 0 || nc >= COLS) score += 5;
        if (nr <= 0 || nr >= ROWS) score += 5;
        // Reward moving toward occupied cells
        if (isOccupied(nc, nr)) score += 10;
        // Check 2 cells ahead for occupied
        const nc2 = nc + d[0], nr2 = nr + d[1];
        if (isOccupied(nc2, nr2)) score += 3;
        // Slight random factor
        score += Math.random() * 2;

        if (score > bestScore) {
          bestScore = score;
          bestDir = d;
        }
      }

      dir = bestDir;
      col += dir[0];
      row += dir[1];

      // Hit wall or occupied = dead, path ends
      if (!inBounds(col, row) || isOccupied(col, row)) {
        // Clamp to valid for final position
        col = Math.max(0, Math.min(COLS, col));
        row = Math.max(0, Math.min(ROWS, row));
        path.push({ col, row });
        break;
      }
      path.push({ col, row });
      visited.add(`${col},${row}`);
    }

    return path.map(p => ({
      col: p.col, row: p.row,
      x: toX(p.col), z: toZ(p.row)
    }));
  }

  /**
   * Reserve an entire path in the occupancy grid so other paths avoid it.
   * @param {Array<{col, row}>} path
   */
  function reservePath(path) {
    for (const p of path) {
      reserve(p.col, p.row);
    }
  }

  /**
   * Clear all reservations (called at the start of each planning cycle).
   */
  function clearReservations() {
    occupied.clear();
  }

  /**
   * Get grid info for debugging.
   */
  function getGridInfo() {
    return { COLS, ROWS, occupiedCount: occupied.size };
  }

  /**
   * Register a single cell as occupied (real-time trail tracking).
   * Called each frame as cycles move so that other cycles' pathfinding
   * and freeform movement respects active trail walls.
   * @param {number} col
   * @param {number} row
   */
  function registerCell(col, row) {
    if (inBounds(col, row)) {
      occupied.add(`${col},${row}`);
    }
  }

  return {
    computeContestantPath,
    computeRunnerPath,
    reservePath,
    clearReservations,
    registerCell,
    isOccupied,
    inBounds,
    getGridInfo,
    toCol, toRow, toX, toZ,
    COLS, ROWS
  };
}
