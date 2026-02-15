---
description: 'Frontend conventions for the Tron dashboard and wwwroot assets (vanilla JS, TRON design system, Three.js 3D background)'
applyTo: 'TrackingPixel.Modern/wwwroot/**'
---

# Tron Dashboard Frontend Conventions

## Architecture

- **Single HTML file**: `tron.html` — all CSS and JS inline. No external files, no build tools.
- **Two views** (JS-driven SPA, no page reload): Operations (`/tron`) and Analytics (`/tron/analytics`)
- **10-second auto-refresh**: `refreshAll()` runs on interval. New sections must hook into this cycle.
- **3D background**: Three.js with bloom post-processing, Tron light-cycle animations. Loaded via CDN — the ONLY external JS library allowed.

## Dependency Rules

- **No** React, Vue, Angular, Svelte
- **No** Tailwind, Bootstrap, or any CSS framework
- **No** Chart.js, D3.js, or charting libraries — use CSS, SVG, or Canvas
- **No** npm, webpack, or build tools
- **Yes** Google Fonts CDN (Orbitron, Share Tech Mono)
- **Yes** Three.js via CDN (3D background only)

## CSS Custom Properties (always use — never hardcode colors)

```css
--bg: #030810;              --panel: rgba(6, 20, 40, 0.85);
--panel-border: rgba(0, 243, 255, 0.2);
--cyan: #00f3ff;            /* Primary accent, healthy */
--orange: #ff6a00;          /* Warnings, medium risk */
--red: #ff2d55;             /* Critical, bots */
--green: #00ff88;           /* Healthy, human */
--yellow: #ffe14d;          /* Caution */
--text: #c0e8ff;            --text-dim: rgba(140, 180, 210, 0.6);
--mono: 'Share Tech Mono', 'Consolas', monospace;   /* Data, tables */
--display: 'Orbitron', sans-serif;                   /* Labels, headings */
```

## Panel Template

Every new section follows:
```html
<div class="panel">
  <div class="panel-title">SECTION NAME</div>
  <!-- content -->
</div>
```

## JavaScript Patterns

```javascript
// API call pattern
const API = { async endpoint(params) { const r = await fetch('/api/dash/endpoint' + (params || '')); return r.ok ? r.json() : null; } };

// Render pattern
function renderSection(data) { const el = document.getElementById('section-id'); if (!data) { el.innerHTML = '...'; return; } /* build HTML */ }

// All renders called from refreshAll()
```

## API Endpoints (all localhost-only, JSON responses)

| Endpoint | Data |
|----------|------|
| `/api/dash/health` | System KPIs |
| `/api/dash/hourly?hours=N` | Time-bucketed traffic |
| `/api/dash/bots` | Risk tier breakdown |
| `/api/dash/bot-signals` | Detection signal frequency |
| `/api/dash/devices` | Browser/OS/device stats |
| `/api/dash/evasion` | Canvas/WebGL evasion |
| `/api/dash/behavior` | Timing/interaction signals |
| `/api/dash/recent` | Latest hits (live feed) |
| `/api/dash/fingerprints?limit=N` | Fingerprint clusters |
| `/api/dash/infra` | Full infrastructure probe |
| `/api/dash/pipeline` | Table counts + watermarks |

## Animation Conventions

- `pulse`: beacon breathing (2s ease-in-out)
- `rowFlash`: new row highlight (cyan ghost → transparent, 1.5s)
- `dataFlow`: pipeline arrows (opacity 0.3↔1.0, 2s)
- Hover transitions: 0.2-0.3s
- Never >3s or distracting effects
