---
description: 'Step-by-step guide to add a new section to the Tron dashboard'
agent: 'devops-ui'
tools: ['read', 'edit', 'search', 'execute']
---

# Add New Dashboard Section

Walk through every step needed to add a new panel to the Tron dashboard.

## Checklist

### 1. Data Source
- Does a SQL view already exist for this data? Check `dbo.vw_Dash_*` views.
- If not: create a new view in `TrackingPixel.Modern/SQL/` following the `vw_Dash_{Name}` naming convention. It should read from `PiXL.Parsed` or dimension tables (`PiXL.Device`, `PiXL.IP`, `PiXL.Visit`, `PiXL.Match`).

### 2. API Endpoint
- Add a new endpoint in `TrackingPixel.Modern/Endpoints/DashboardEndpoints.cs`
- Follow the existing pattern: `group.MapGet("/api/dash/{name}", ...)` with localhost restriction
- Return JSON via `Results.Ok(results)`

### 3. HTML Structure
- Add the panel in `TrackingPixel.Modern/wwwroot/tron.html`
- Use the standard panel template: `<div class="panel"><div class="panel-title">NAME</div>...</div>`
- Use ONLY existing CSS custom properties (--cyan, --orange, --red, --green, --text, etc.)
- Place it in the correct view (Operations or Analytics) within the existing section flow

### 4. JavaScript
- Add an API method: `API.newEndpoint = async (params) => { ... }`
- Add a render function: `function renderNewSection(data) { ... }`
- Hook into `refreshAll()` — add the API call + render to the appropriate view's refresh block
- Handle null/empty data gracefully

### 5. Verify
- Load `/tron` or `/tron/analytics` in browser
- Confirm the section renders with data
- Confirm the 10-second auto-refresh updates it
- Confirm it looks correct at different viewport widths
- Confirm the TRON aesthetic is maintained (no external CSS, proper colors/fonts)
