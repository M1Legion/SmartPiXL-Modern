/*
    Atlas Content Population, All 4 Tiers
    ─────────────────────────────────────────
    Populates ManagementHtml, TechnicalHtml, WalkthroughHtml, and MermaidDiagram
    for all 9 Atlas sections. PitchHtml was seeded in 29_AtlasDocsSchema.sql.
    
    Server: localhost\SQL2025
    Database: SmartPiXL
    Date: 2026-02-16
    Agent: atlas
*/

SET NOCOUNT ON;

-- ═══════════════════════════════════════════════════════════════════
-- SECTION 1: SmartPiXL Overview
-- ═══════════════════════════════════════════════════════════════════
UPDATE Docs.Section SET
    ManagementHtml = N'
<h4>System Architecture</h4>
<p>SmartPiXL is a .NET 10 Minimal API application hosted in IIS via the ASP.NET Core Module (InProcess). It processes the full visitor identification lifecycle:</p>
<ul>
    <li><strong>Collection:</strong> A JavaScript tag collects 100+ browser signals per visit</li>
    <li><strong>Ingestion:</strong> Zero-allocation hot path captures, enriches, and queues hits in &lt;10ms</li>
    <li><strong>Warehousing:</strong> ETL pipeline parses raw data into a 175-column star schema every 60 seconds</li>
    <li><strong>Identity Resolution:</strong> Multi-strategy matching against a 275M+ consumer database</li>
    <li><strong>Analytics:</strong> Real-time operator dashboard with 11 API endpoints and 10 SQL views</li>
</ul>
<table>
    <tr><th>Component</th><th>Technology</th><th>Status</th></tr>
    <tr><td>Application Server</td><td>.NET 10, C# 14, Minimal APIs</td><td>Live</td></tr>
    <tr><td>Database</td><td>SQL Server 2025 Developer</td><td>Live</td></tr>
    <tr><td>Hosting</td><td>IIS InProcess on Windows Server</td><td>Live</td></tr>
    <tr><td>ETL Pipeline</td><td>Background services + stored procedures</td><td>Live</td></tr>
    <tr><td>Dashboard</td><td>Three.js WebGL (Tron)</td><td>Live</td></tr>
    <tr><td>Documentation</td><td>Atlas 4-tier portal</td><td>Live</td></tr>
</table>',
    TechnicalHtml = N'
<h4>Runtime Stack</h4>
<ul>
    <li><strong>Runtime:</strong> .NET 10.0, C# 14, nullable reference types, source-generated regex</li>
    <li><strong>API Style:</strong> Minimal APIs (no controllers), endpoints as lambdas in <code>*Endpoints.cs</code></li>
    <li><strong>Database:</strong> SQL Server 2025 on <code>localhost\SQL2025</code>, database <code>SmartPiXL</code></li>
    <li><strong>Schemas:</strong> <code>PiXL</code> (domain), <code>ETL</code> (pipeline), <code>IPAPI</code> (geolocation), <code>Docs</code> (atlas)</li>
    <li><strong>Hosting:</strong> IIS InProcess via AspNetCoreModuleV2, Kestrel behind IIS (ports 6000/6001 prod, 7000/7001 dev)</li>
</ul>
<h4>Key Files</h4>
<table>
    <tr><th>File</th><th>Purpose</th></tr>
    <tr><td><code>Program.cs</code></td><td>Composition root, DI registration, middleware pipeline, endpoint mapping</td></tr>
    <tr><td><code>TrackingEndpoints.cs</code></td><td>Pixel serving, JS generation, enrichment pipeline</td></tr>
    <tr><td><code>DashboardEndpoints.cs</code></td><td>11 JSON endpoints for Tron dashboard</td></tr>
    <tr><td><code>AtlasEndpoints.cs</code></td><td>4 JSON endpoints for Atlas documentation portal</td></tr>
    <tr><td><code>TrackingSettings.cs</code></td><td>Strongly-typed configuration (connection strings, queue sizes)</td></tr>
    <tr><td><code>appsettings.json</code></td><td>Runtime configuration (two copies: dev + IIS production)</td></tr>
</table>
<h4>Performance Philosophy</h4>
<p>The lead developer is a former C++ game dev. Hot-path code follows these rules:</p>
<ul>
    <li>Zero heap allocation on the pixel endpoint (<code>Span&lt;T&gt;</code>, <code>stackalloc</code>, <code>ThreadStatic</code> StringBuilder)</li>
    <li>Source-generated regex (<code>[GeneratedRegex]</code>), never runtime <code>new Regex()</code></li>
    <li><code>SearchValues&lt;char&gt;</code> (SIMD) for character scanning</li>
    <li>Lock-free patterns: <code>Volatile.Read</code>, <code>Interlocked.CompareExchange</code></li>
    <li><code>Channel&lt;T&gt;</code> bounded queues instead of <code>Task.Run()</code></li>
</ul>',
    WalkthroughHtml = N'
<h4>The Big Picture, What SmartPiXL Actually Does</h4>
<p>Imagine you run an e-commerce business. You have a website. People visit your website. Most of them leave without buying anything, and you have no idea who they were. No login, no form fill, no cookie consent, they are ghosts.</p>
<p>SmartPiXL changes that. You add one line of code to your website, a <code>&lt;script&gt;</code> tag. That script silently collects 100+ signals from every visitor''s browser (their screen size, graphics card, installed fonts, timezone, mouse behavior, and about 90 other things). It fires all of that data to our server as a tiny invisible image request.</p>
<p>Our server catches that request, enriches it with IP intelligence and geolocation, writes it to a database, and, every 60 seconds, runs an identity resolution pipeline that matches the visitor against a 275-million consumer database. If we find a match, we know who that visitor is: name, email, physical address.</p>
<p>The entire system is built in .NET 10 with a philosophy borrowed from game engine programming: zero heap allocation on the hot path, sub-10ms response times, lock-free queues, and SIMD-accelerated string processing. The database is SQL Server 2025 with a star-schema warehouse design that processes raw hits into 175 enriched columns every minute.</p>
<p>Everything runs on a single Windows Server machine with IIS. No Kubernetes, no microservices, no message brokers, just a very fast monolith that does one thing extremely well: turning anonymous web traffic into identified leads.</p>',
    MermaidDiagram = N'graph LR
    Browser["Browser<br/>100+ signals"] -->|"HTTP GET<br/>_SMART.GIF"| Server["ASP.NET Core<br/>.NET 10"]
    Server -->|"Channel&lt;T&gt;"| Queue["Bounded Queue"]
    Queue -->|"SqlBulkCopy"| Raw["PiXL.Test<br/>9 columns"]
    Raw -->|"ETL 60s"| Parsed["PiXL.Parsed<br/>175 columns"]
    Parsed -->|"Match"| Match["PiXL.Match<br/>Identity Resolution"]
    Match -->|"275M+ consumers"| Consumer["AutoConsumer<br/>Consumer DB"]

    style Browser fill:#1a2035,stroke:#3b82f6,color:#f0f4f8
    style Server fill:#1a2035,stroke:#3b82f6,color:#f0f4f8
    style Queue fill:#1a2035,stroke:#8b5cf6,color:#f0f4f8
    style Raw fill:#1a2035,stroke:#f59e0b,color:#f0f4f8
    style Parsed fill:#1a2035,stroke:#10b981,color:#f0f4f8
    style Match fill:#1a2035,stroke:#10b981,color:#f0f4f8
    style Consumer fill:#1a2035,stroke:#ef4444,color:#f0f4f8',
    LastUpdated = SYSUTCDATETIME(),
    UpdatedBy = 'agent:atlas'
WHERE Slug = 'overview';
GO

-- ═══════════════════════════════════════════════════════════════════
-- SECTION 2: Data Collection
-- ═══════════════════════════════════════════════════════════════════
UPDATE Docs.Section SET
    ManagementHtml = N'
<h4>How Browser Fingerprinting Works</h4>
<p>When a client page loads, a <code>&lt;script&gt;</code> tag requests the fingerprinting JavaScript from our server. This script, an IIFE (Immediately Invoked Function Expression), silently collects signals from the visitor''s browser without storing anything on their device. No cookies, no localStorage, no permission prompts.</p>
<p>The signals fall into several categories:</p>
<table>
    <tr><th>Category</th><th>Signals</th><th>Uniqueness</th></tr>
    <tr><td>Canvas Rendering</td><td>GPU-specific pixel-level rendering differences</td><td>~90% unique alone</td></tr>
    <tr><td>WebGL Parameters</td><td>GPU model, max texture size, extensions, shader version</td><td>High</td></tr>
    <tr><td>Audio Fingerprint</td><td>AudioContext oscillator output frequency analysis</td><td>Medium-High</td></tr>
    <tr><td>Font Detection</td><td>Installed system font enumeration via width probing</td><td>High</td></tr>
    <tr><td>Hardware Signals</td><td>CPU cores, device memory, screen geometry, touch support</td><td>Medium</td></tr>
    <tr><td>Browser Identity</td><td>User agent, platform, plugins, language, timezone</td><td>Low individually</td></tr>
    <tr><td>Behavioral</td><td>Mouse movement entropy, touch vs. click, scroll patterns</td><td>Anti-bot signal</td></tr>
</table>
<p><strong>Key constraint:</strong> No APIs that trigger permission prompts (Bluetooth, USB, MIDI, Geolocation, etc.) are used. The collection must be completely invisible to the end user.</p>',
    TechnicalHtml = N'
<h4>PiXLScript Architecture</h4>
<p>The fingerprinting JavaScript is generated server-side by <code>Scripts/PiXLScript.cs</code> and served via <code>TrackingEndpoints.cs</code> at the route <code>/js/{companyId}/{pixlId}.js</code>. The script is an IIFE that:</p>
<ol>
    <li>Collects all synchronous signals immediately (navigator properties, screen geometry, timezone)</li>
    <li>Launches async signal collection (canvas, WebGL, audio, fonts) in parallel</li>
    <li>Waits 200ms for async APIs to complete</li>
    <li>Fires a pixel by creating an <code>Image()</code> element pointing to <code>/{companyId}/{pixlId}_SMART.GIF?{all_signals}</code></li>
</ol>
<h4>Signal Categories (100+ data points)</h4>
<table>
    <tr><th>Signal</th><th>Query Param</th><th>Collection Method</th></tr>
    <tr><td>Screen Width/Height</td><td><code>sw</code>, <code>sh</code></td><td><code>screen.width</code>, <code>screen.height</code></td></tr>
    <tr><td>Canvas Hash</td><td><code>cv</code></td><td>Draw text+shapes → <code>canvas.toDataURL()</code> → hash</td></tr>
    <tr><td>WebGL Hash</td><td><code>gl</code></td><td>18+ GL parameters → hash</td></tr>
    <tr><td>WebGL Vendor</td><td><code>glv</code></td><td><code>WEBGL_debug_renderer_info</code> extension</td></tr>
    <tr><td>Audio Fingerprint</td><td><code>af</code></td><td>OscillatorNode → AnalyserNode → frequency hash</td></tr>
    <tr><td>Font List</td><td><code>fl</code></td><td>Width-probing against 60+ known fonts</td></tr>
    <tr><td>Timezone</td><td><code>tz</code></td><td><code>Intl.DateTimeFormat().resolvedOptions().timeZone</code></td></tr>
    <tr><td>CPU Cores</td><td><code>cpu</code></td><td><code>navigator.hardwareConcurrency</code></td></tr>
    <tr><td>Device Memory</td><td><code>dm</code></td><td><code>navigator.deviceMemory</code></td></tr>
    <tr><td>Touch Support</td><td><code>ts</code></td><td><code>navigator.maxTouchPoints</code></td></tr>
</table>
<p>See <code>docs/FIELD_REFERENCE.md</code> for the complete list of all 175 parsed columns including server-side enrichment fields.</p>
<h4>Evasion Countermeasures</h4>
<p>10 countermeasures are implemented to detect fingerprint spoofing:</p>
<ul>
    <li><strong>Canvas noise detection:</strong> Multi-canvas cross-validation (same drawing should produce same hash)</li>
    <li><strong>Audio noise detection:</strong> Oscillator output consistency check</li>
    <li><strong>Behavioral analysis:</strong> Mouse entropy, timing patterns, scroll depth</li>
    <li><strong>Stealth plugin detection:</strong> CDP (Chrome DevTools Protocol) leak detection</li>
    <li><strong>Anti-detect browser detection:</strong> Fingerprint stability scoring per IP</li>
</ul>',
    WalkthroughHtml = N'
<h4>Step-by-Step: How Data Collection Actually Works</h4>
<div class="walkthrough-step" data-step="1">
<strong>The script tag loads.</strong> A client website has <code>&lt;script src="https://smartpixl.info/js/12800/100_SMART.js"&gt;&lt;/script&gt;</code> in their page. When a visitor loads that page, the browser requests this URL from our server.
</div>
<div class="walkthrough-step" data-step="2">
<strong>TrackingEndpoints serves the script.</strong> The route <code>/js/{companyId}/{pixlId}.js</code> matches. The handler calls <code>PiXLScript.GetScript(pixelUrl)</code> which returns a self-contained JavaScript IIFE. The script is <em>generated</em> server-side with the pixel URL baked in, no external dependencies.
</div>
<div class="walkthrough-step" data-step="3">
<strong>The IIFE executes immediately.</strong> The moment the script loads, it starts collecting signals. Synchronous signals (screen size, timezone, navigator properties) are grabbed instantly. Asynchronous signals (canvas rendering, WebGL probing, audio fingerprinting, font enumeration) are launched in parallel.
</div>
<div class="walkthrough-step" data-step="4">
<strong>Canvas fingerprinting runs.</strong> The script creates an invisible canvas, draws colored rectangles, text with emoji (🎨), and applies specific fill styles. It converts the result to a data URL via <code>toDataURL()</code> and hashes it. Because different GPUs render these primitives with subtly different anti-aliasing and sub-pixel rendering, the hash is nearly unique per hardware configuration. Two canvases are drawn and compared to detect noise injection (evasion countermeasure V-01).
</div>
<div class="walkthrough-step" data-step="5">
<strong>WebGL probing runs.</strong> The script creates a WebGL context and queries 18+ parameters: max texture size, vertex attributes, supported extensions, shader language version. The <code>WEBGL_debug_renderer_info</code> extension reveals the exact GPU model (e.g., "NVIDIA GeForce RTX 4090").
</div>
<div class="walkthrough-step" data-step="6">
<strong>Audio fingerprinting runs.</strong> An <code>OscillatorNode</code> generates a tone, routed through a <code>DynamicsCompressorNode</code> to an <code>AnalyserNode</code>. The frequency data from the compressor output is hardware-dependent. The result is hashed.
</div>
<div class="walkthrough-step" data-step="7">
<strong>After 200ms, the pixel fires.</strong> The script creates an <code>Image()</code> element with <code>src</code> set to <code>/{companyId}/{pixlId}_SMART.GIF?sw=1920&amp;sh=1080&amp;cv=1826540c&amp;gl=43f43aae&amp;af=...</code>. This triggers an HTTP GET to our server, the browser sees a 1x1 transparent GIF, but the query string carries all 100+ signals.
</div>',
    MermaidDiagram = N'graph TB
    subgraph "Client Browser"
        SCRIPT["PiXLScript.js<br/>IIFE"] --> SYNC["Sync Signals<br/>screen, navigator, timezone"]
        SCRIPT --> CANVAS["Canvas Fingerprint<br/>multi-canvas cross-validation"]
        SCRIPT --> WEBGL["WebGL Probe<br/>18+ GPU parameters"]
        SCRIPT --> AUDIO["Audio Fingerprint<br/>oscillator frequency hash"]
        SCRIPT --> FONTS["Font Enumeration<br/>60+ font width-probe"]
    end

    SYNC --> PIXEL["Image() → _SMART.GIF<br/>100+ params in query string"]
    CANVAS --> PIXEL
    WEBGL --> PIXEL
    AUDIO --> PIXEL
    FONTS --> PIXEL

    PIXEL -->|"HTTP GET"| SERVER["TrackingEndpoints"]

    style SCRIPT fill:#1a2035,stroke:#3b82f6,color:#f0f4f8
    style PIXEL fill:#1a2035,stroke:#f59e0b,color:#f0f4f8
    style SERVER fill:#1a2035,stroke:#10b981,color:#f0f4f8',
    LastUpdated = SYSUTCDATETIME(),
    UpdatedBy = 'agent:atlas'
WHERE Slug = 'data-collection';
GO

-- ═══════════════════════════════════════════════════════════════════
-- SECTION 3: Ingestion Pipeline
-- ═══════════════════════════════════════════════════════════════════
UPDATE Docs.Section SET
    ManagementHtml = N'
<h4>Request Processing Pipeline</h4>
<p>When a pixel hit arrives, the server executes a multi-stage enrichment pipeline before writing to the database. The entire process completes in under 10 milliseconds, the browser receives its 1x1 GIF immediately while background processing continues.</p>
<table>
    <tr><th>Stage</th><th>Service</th><th>What It Does</th></tr>
    <tr><td>1. Capture</td><td>TrackingCaptureService</td><td>Parses HTTP request into TrackingData record</td></tr>
    <tr><td>2. Fingerprint Check</td><td>FingerprintStabilityService</td><td>Scores canvas/WebGL/audio variation per IP</td></tr>
    <tr><td>3. IP Behavior</td><td>IpBehaviorService</td><td>Detects subnet velocity and rapid-fire timing</td></tr>
    <tr><td>4. Datacenter Check</td><td>DatacenterIpService</td><td>Classifies IP against AWS/GCP ranges</td></tr>
    <tr><td>5. IP Classification</td><td>IpClassificationService</td><td>Private, CGNAT, loopback, residential</td></tr>
    <tr><td>6. Geolocation</td><td>GeoCacheService</td><td>Two-tier cached geo lookup (342M-row DB)</td></tr>
    <tr><td>7. Enqueue</td><td>DatabaseWriterService</td><td>Fire-and-forget into Channel&lt;T&gt; bounded queue</td></tr>
</table>
<p>The queue is drained by <code>DatabaseWriterService</code> which accumulates batches and writes via <code>SqlBulkCopy</code>. Zero intermediate DataTable objects, a custom <code>DbDataReader</code> streams records directly to SQL Server.</p>',
    TechnicalHtml = N'
<h4>Hot Path Architecture</h4>
<p>The pixel endpoint (<code>/{**path}</code> matching <code>*_SMART.GIF</code>) is the highest-throughput code path. It must return in &lt;10ms with zero heap allocation.</p>
<h4>TrackingCaptureService</h4>
<p>Stateless HTTP request parser. Uses a <code>[ThreadStatic]</code> StringBuilder to avoid allocation. Extracts:</p>
<ul>
    <li><strong>CompanyID / PiXLID:</strong> From URL path segments (e.g., <code>/12800/100_SMART.GIF</code>)</li>
    <li><strong>Client IP:</strong> From <code>X-Forwarded-For</code> header chain (ForwardLimit=1, loopback trusted)</li>
    <li><strong>Headers JSON:</strong> Escaped via <code>SearchValues&lt;char&gt;</code> (SIMD), no JsonSerializer on hot path</li>
    <li><strong>Query String:</strong> Raw, unparsed, ETL parses individual signals later</li>
</ul>
<h4>DatabaseWriterService</h4>
<p>Background service with a <code>Channel&lt;TrackingData&gt;</code> bounded queue (capacity: 10,000, DropOldest). Single reader drains batches of 100 via a custom <code>DbDataReader</code> subclass that maps <code>TrackingData</code> fields to SQL column ordinals. <code>SqlBulkCopy</code> writes to <code>PiXL.Test</code> (9 columns).</p>
<h4>PiXL.Test Schema (Raw Ingest)</h4>
<pre><code>CREATE TABLE PiXL.Test (
    Id           bigint IDENTITY(1,1),
    CompanyID    varchar(50),
    PiXLID       varchar(50),
    IPAddress    varchar(45),
    RequestPath  varchar(500),
    QueryString  varchar(max),
    HeadersJson  nvarchar(max),
    UserAgent    varchar(1000),
    Referer      varchar(2000),
    ReceivedAt   datetime2 DEFAULT SYSUTCDATETIME()
)</code></pre>
<h4>Server-Side Enrichment Params</h4>
<p>Enrichment services append <code>_srv_*</code> query parameters before enqueue:</p>
<table>
    <tr><th>Param</th><th>Source Service</th><th>Values</th></tr>
    <tr><td><code>_srv_ipClass</code></td><td>IpClassificationService</td><td>Residential, Datacenter, Private, CGNAT, Loopback...</td></tr>
    <tr><td><code>_srv_fpStability</code></td><td>FingerprintStabilityService</td><td>Variation score (0=stable, higher=suspicious)</td></tr>
    <tr><td><code>_srv_ipVelocity</code></td><td>IpBehaviorService</td><td>Requests per /24 subnet in window</td></tr>
    <tr><td><code>_srv_rapidFire</code></td><td>IpBehaviorService</td><td>true if timing pattern matches automation</td></tr>
    <tr><td><code>_srv_dcName</code></td><td>DatacenterIpService</td><td>AWS/GCP prefix name if matched</td></tr>
    <tr><td><code>_srv_geoCity</code></td><td>GeoCacheService</td><td>City from IP geolocation</td></tr>
    <tr><td><code>_srv_geoRegion</code></td><td>GeoCacheService</td><td>State/region from IP geolocation</td></tr>
    <tr><td><code>_srv_tzMismatch</code></td><td>GeoCacheService</td><td>true if browser TZ != IP geo TZ</td></tr>
</table>',
    WalkthroughHtml = N'
<h4>Step-by-Step: What Happens When a Pixel Hit Arrives</h4>
<div class="walkthrough-step" data-step="1">
<strong>The HTTP request arrives.</strong> The browser sends <code>GET /12800/100_SMART.GIF?sw=1920&amp;sh=1080&amp;cv=1826540c&amp;...</code>. IIS passes it to ASP.NET Core InProcess. The catch-all route <code>/{**path}</code> matches because the path ends with <code>_SMART.GIF</code>.
</div>
<div class="walkthrough-step" data-step="2">
<strong>The GIF is returned immediately.</strong> <code>TrackingEndpoints.CaptureAndEnqueue()</code> writes a pre-generated 43-byte transparent GIF to the response and calls <code>return</code>. The browser gets its image, from its perspective, the request is done.
</div>
<div class="walkthrough-step" data-step="3">
<strong>TrackingCaptureService parses the request.</strong> Before the GIF is sent, the service extracts CompanyID and PiXLID from the URL path, the client''s real IP from the X-Forwarded-For chain, the raw query string, headers JSON (escaped via SIMD SearchValues), User-Agent, and Referer. This produces a <code>TrackingData</code> sealed record.
</div>
<div class="walkthrough-step" data-step="4">
<strong>Enrichment services fire in sequence.</strong> FingerprintStabilityService checks if this IP has sent wildly different fingerprints recently (anti-detect browser signal). IpBehaviorService checks subnet /24 velocity (many IPs from the same subnet = coordinated bot). DatacenterIpService checks AWS/GCP CIDR ranges. GeoCacheService looks up the IP for city/region/timezone. Each appends <code>_srv_*</code> params to the query string.
</div>
<div class="walkthrough-step" data-step="5">
<strong>The record enters the Channel queue.</strong> <code>DatabaseWriterService.TryQueue()</code> writes the enriched <code>TrackingData</code> into a bounded <code>Channel&lt;T&gt;</code> (capacity 10,000). This is non-blocking, if the queue is full, the oldest item is dropped (DropOldest policy).
</div>
<div class="walkthrough-step" data-step="6">
<strong>DatabaseWriterService drains the queue.</strong> A background loop reads from the channel, accumulates batches of 100, and writes them to <code>PiXL.Test</code> via <code>SqlBulkCopy</code> using a custom <code>DbDataReader</code> (no intermediate DataTable). This is the only SQL write on the ingest path.
</div>',
    MermaidDiagram = N'graph TD
    REQ["HTTP GET _SMART.GIF"] --> GIF["Return 43-byte GIF<br/>(immediate)"]
    REQ --> CAP["TrackingCaptureService<br/>Parse request → TrackingData"]
    CAP --> FP["FingerprintStabilityService<br/>_srv_fpStability"]
    CAP --> IP["IpBehaviorService<br/>_srv_ipVelocity, _srv_rapidFire"]
    CAP --> DC["DatacenterIpService<br/>_srv_dcName"]
    CAP --> GEO["GeoCacheService<br/>_srv_geoCity, _srv_tzMismatch"]
    FP --> ENQ["Channel&lt;T&gt; Queue<br/>(bounded 10,000)"]
    IP --> ENQ
    DC --> ENQ
    GEO --> ENQ
    ENQ --> BULK["SqlBulkCopy<br/>→ PiXL.Test"]

    style REQ fill:#1a2035,stroke:#3b82f6,color:#f0f4f8
    style GIF fill:#1a2035,stroke:#10b981,color:#f0f4f8
    style BULK fill:#1a2035,stroke:#f59e0b,color:#f0f4f8',
    LastUpdated = SYSUTCDATETIME(),
    UpdatedBy = 'agent:atlas'
WHERE Slug = 'ingestion-pipeline';
GO

-- ═══════════════════════════════════════════════════════════════════
-- SECTION 4: ETL & Data Warehousing
-- ═══════════════════════════════════════════════════════════════════
UPDATE Docs.Section SET
    ManagementHtml = N'
<h4>ETL Pipeline Architecture</h4>
<p>Every 60 seconds, <code>EtlBackgroundService</code> wakes up and runs two stored procedures in sequence:</p>
<ol>
    <li><strong>ETL.usp_ParseNewHits:</strong> Reads new rows from <code>PiXL.Test</code> (raw, 9 columns) using a watermark, parses the query string into ~175 typed columns, and writes to <code>PiXL.Parsed</code> (materialized warehouse). Also MERGEs into dimension tables (<code>PiXL.Device</code>, <code>PiXL.IP</code>) and inserts into the fact table (<code>PiXL.Visit</code>).</li>
    <li><strong>ETL.usp_MatchVisits:</strong> Picks up unmatched visits and runs multi-strategy identity resolution against the <code>AutoConsumer</code> table (~470M rows / 275M+ unique consumers, 67M with email). Match results go to <code>PiXL.Match</code>.</li>
</ol>
<h4>Star Schema Design</h4>
<table>
    <tr><th>Table</th><th>Type</th><th>Key</th><th>Rows</th></tr>
    <tr><td>PiXL.Parsed</td><td>Warehouse</td><td>Id (1:1 with Test)</td><td>~175 columns</td></tr>
    <tr><td>PiXL.Device</td><td>Dimension</td><td>DeviceHash (5-field composite)</td><td>MERGE upsert</td></tr>
    <tr><td>PiXL.IP</td><td>Dimension</td><td>IPAddress</td><td>Geo-enriched</td></tr>
    <tr><td>PiXL.Visit</td><td>Fact</td><td>1:1 with Parsed</td><td>Links Device + IP + JSON</td></tr>
    <tr><td>PiXL.Match</td><td>Result</td><td>MERGE on device+email</td><td>Identity resolution output</td></tr>
</table>',
    TechnicalHtml = N'
<h4>ETL.usp_ParseNewHits</h4>
<p>Watermark-based incremental processor. Reads <code>ETL.Watermark</code> where <code>ProcessName = ''ParseNewHits''</code> to get <code>LastProcessedId</code>. Selects all <code>PiXL.Test</code> rows with <code>Id > @LastProcessedId</code>, parses each row''s <code>QueryString</code> using <code>dbo.GetQueryParam()</code> scalar function, and inserts into <code>PiXL.Parsed</code>.</p>
<h4>Key Tables</h4>
<pre><code>-- PiXL.Parsed (~175 columns, materialized from raw QueryString)
-- Includes: sw, sh, cv (canvas), gl (WebGL), af (audio), fl (fonts),
-- tz, cpu, dm, ts, plus _srv_* enrichment columns, plus computed columns

-- PiXL.Device (dimension, keyed on DeviceHash)
-- DeviceHash = hash of: canvas + WebGL + audio + platform + screenRes

-- PiXL.Visit (fact, 1:1 with Parsed)
-- Links DeviceId, IpId, ClientParamsJson (native JSON type in SQL 2025)

-- ETL.Watermark
-- ProcessName, LastProcessedId, RowsProcessed, LastRun</code></pre>
<h4>ETL.usp_MatchVisits</h4>
<p>Identity resolution pipeline. Reads unmatched visits from <code>PiXL.Visit</code> via <code>ETL.MatchWatermark</code>. Match strategies (in priority order):</p>
<ol>
    <li><strong>UID Match:</strong> Direct unique identifier if present</li>
    <li><strong>IP + Geo Proximity:</strong> Same IP within 692m radius of consumer address</li>
    <li><strong>Cookie Correlation:</strong> Shared cookie/UID linking visits</li>
    <li><strong>Direct IP:</strong> IP address match to consumer record</li>
</ol>
<p>Matched records are MERGEd into <code>PiXL.Match</code> with <code>IndividualKey</code> and <code>AddressKey</code> from AutoConsumer.</p>',
    WalkthroughHtml = N'
<h4>Step-by-Step: What the ETL Does Every 60 Seconds</h4>
<div class="walkthrough-step" data-step="1">
<strong>EtlBackgroundService wakes up.</strong> This is a .NET <code>BackgroundService</code> that sleeps for 60 seconds, then fires. It reads the connection string from <code>TrackingSettings</code> and calls the stored procedures sequentially.
</div>
<div class="walkthrough-step" data-step="2">
<strong>ETL.usp_ParseNewHits runs.</strong> First, it reads the watermark: <code>SELECT LastProcessedId FROM ETL.Watermark WHERE ProcessName = ''ParseNewHits''</code>. Say it returns 50,000. It then selects all <code>PiXL.Test</code> rows with <code>Id > 50000</code>, these are the new raw hits since last run.
</div>
<div class="walkthrough-step" data-step="3">
<strong>Query strings get parsed into 175 columns.</strong> For each raw row, the proc calls <code>dbo.GetQueryParam(QueryString, ''sw'')</code>, <code>dbo.GetQueryParam(QueryString, ''sh'')</code>, etc., extracting each signal from the URL-encoded query string into a typed column. Screen width becomes an int. Canvas hash becomes a varchar. Timezone becomes a varchar. The result is inserted into <code>PiXL.Parsed</code>.
</div>
<div class="walkthrough-step" data-step="4">
<strong>Dimension tables get MERGEd.</strong> The proc computes a <code>DeviceHash</code> from 5 fingerprint fields (canvas + WebGL + audio + platform + screen resolution). It MERGEs this into <code>PiXL.Device</code>, if the hash already exists, the row is updated; if new, it is inserted. Similarly, the IP address is MERGEd into <code>PiXL.IP</code> with geolocation data.
</div>
<div class="walkthrough-step" data-step="5">
<strong>Visit fact rows are inserted.</strong> <code>PiXL.Visit</code> gets one row per parsed hit, linking the <code>DeviceId</code>, <code>IpId</code>, and a <code>ClientParamsJson</code> column (native SQL 2025 JSON type) containing the full signal payload.
</div>
<div class="walkthrough-step" data-step="6">
<strong>ETL.usp_MatchVisits runs.</strong> Now the identity resolution kicks in. It reads the match watermark and picks up unmatched visits. For each, it queries the <code>AutoConsumer</code> table (~470M rows / 275M+ unique consumers, indexed on email via <code>IX_AutoConsumer_EMail</code>) using multiple match strategies: direct IP, geo-proximity within 692m, cookie correlation, and UID matching.
</div>
<div class="walkthrough-step" data-step="7">
<strong>Matches are written to PiXL.Match.</strong> Each successful match produces a row with the visitor''s <code>IndividualKey</code> and <code>AddressKey</code> from AutoConsumer, plus the match strategy used and a confidence indicator. The watermark is advanced.
</div>',
    MermaidDiagram = N'graph TD
    ETL["EtlBackgroundService<br/>Every 60 seconds"] --> PARSE["ETL.usp_ParseNewHits"]
    ETL --> MATCH_PROC["ETL.usp_MatchVisits"]

    PARSE --> |"Read watermark"| WM["ETL.Watermark"]
    PARSE --> |"SELECT WHERE Id > watermark"| RAW["PiXL.Test<br/>9 columns"]
    RAW --> PARSED["PiXL.Parsed<br/>~175 columns"]
    RAW --> DEVICE["PiXL.Device<br/>MERGE on DeviceHash"]
    RAW --> IPDB["PiXL.IP<br/>MERGE on IPAddress"]
    RAW --> VISIT["PiXL.Visit<br/>INSERT fact rows"]

    MATCH_PROC --> |"Unmatched visits"| VISIT
    MATCH_PROC --> |"Lookup"| AC["AutoConsumer<br/>275M+ consumers"]
    MATCH_PROC --> MATCH_TBL["PiXL.Match<br/>Identity matches"]

    style ETL fill:#1a2035,stroke:#8b5cf6,color:#f0f4f8
    style PARSED fill:#1a2035,stroke:#10b981,color:#f0f4f8
    style MATCH_TBL fill:#1a2035,stroke:#10b981,color:#f0f4f8
    style AC fill:#1a2035,stroke:#ef4444,color:#f0f4f8',
    LastUpdated = SYSUTCDATETIME(),
    UpdatedBy = 'agent:atlas'
WHERE Slug = 'etl-processing';
GO

-- ═══════════════════════════════════════════════════════════════════
-- SECTION 5: Identity Resolution
-- ═══════════════════════════════════════════════════════════════════
UPDATE Docs.Section SET
    ManagementHtml = N'
<h4>Multi-Strategy Identity Resolution</h4>
<p>SmartPiXL resolves anonymous web visitors to real consumer identities using multiple matching strategies against a 275-million consumer database (<code>AutoConsumer</code>). Of these, 67 million records have verified email addresses.</p>
<h4>Match Strategies</h4>
<table>
    <tr><th>Strategy</th><th>Description</th><th>Accuracy</th></tr>
    <tr><td>UID Match</td><td>Direct unique identifier from returning visitor cookies</td><td>Highest</td></tr>
    <tr><td>IP + Geo Proximity</td><td>IP address within 692m of consumer''s known address</td><td>High</td></tr>
    <tr><td>Cookie Correlation</td><td>Shared cookie/session linking multiple visits</td><td>High</td></tr>
    <tr><td>Direct IP</td><td>Residential IP address matching consumer ISP record</td><td>Medium</td></tr>
</table>
<p>The match process runs every 60 seconds as part of the ETL cycle. Results include <code>IndividualKey</code> (person) and <code>AddressKey</code> (physical address) from the consumer database.</p>
<p><strong>Current focus:</strong> Modern pixel hits (with 100+ JS signals) use all strategies. Legacy pixel hits (server-side data only) will use a reduced strategy set focused on IP + User-Agent + timestamp correlation.</p>',
    TechnicalHtml = N'
<h4>ETL.usp_MatchVisits Implementation</h4>
<p>The stored procedure is called by <code>EtlBackgroundService</code> after <code>usp_ParseNewHits</code> completes. It reads unmatched visits from <code>PiXL.Visit</code> via <code>ETL.MatchWatermark</code>.</p>
<h4>AutoConsumer Table</h4>
<ul>
    <li><strong>Total rows:</strong> 275,000,000+</li>
    <li><strong>Rows with email:</strong> 67,000,000+</li>
    <li><strong>Key index:</strong> <code>IX_AutoConsumer_EMail</code> (SQL/22)</li>
    <li><strong>Location:</strong> Xavier server (<code>192.168.88.35</code>), accessed via <code>XavierSmartPiXLConnectionString</code></li>
</ul>
<h4>Match Output: PiXL.Match</h4>
<pre><code>PiXL.Match
├── MatchId (PK)
├── VisitId (FK → PiXL.Visit)
├── DeviceHash
├── IndividualKey (from AutoConsumer)
├── AddressKey (from AutoConsumer)
├── MatchType (strategy used)
├── Email
├── MatchedAt (datetime2)</code></pre>
<h4>Match Configuration</h4>
<p>Match strategies are configurable via <code>PiXL.Config</code> table (SQL/27). Geo-proximity radius (default 692m) and other thresholds can be adjusted without code changes.</p>',
    WalkthroughHtml = N'
<h4>Step-by-Step: How Identity Resolution Works</h4>
<div class="walkthrough-step" data-step="1">
<strong>The match proc picks up unmatched visits.</strong> After <code>usp_ParseNewHits</code> completes, <code>usp_MatchVisits</code> reads the match watermark from <code>ETL.MatchWatermark</code> and selects all <code>PiXL.Visit</code> rows that haven''t been processed yet.
</div>
<div class="walkthrough-step" data-step="2">
<strong>For each visit, match strategies fire in priority order.</strong> First: does this visitor have a UID (returning visitor cookie)? If so, look up the UID directly, highest confidence match. If not, proceed to IP-based strategies.
</div>
<div class="walkthrough-step" data-step="3">
<strong>IP + Geo Proximity matching.</strong> The visit''s IP address is geolocated (we already have this from the ingestion pipeline''s <code>GeoCacheService</code> enrichment). The proc looks for AutoConsumer records where the consumer''s known address is within 692 meters of the IP''s geolocation. This is the most common match path for first-time visitors on residential IPs.
</div>
<div class="walkthrough-step" data-step="4">
<strong>Match results are MERGEd into PiXL.Match.</strong> For each successful match, a row is created (or updated if the same device+email pair was already matched) with the consumer''s <code>IndividualKey</code>, <code>AddressKey</code>, email, and the match strategy used. This means you now know: "The person who visited your website at 2:47 PM from Chrome on Windows with a GeForce RTX 3070 is John Smith at 123 Main Street."
</div>
<div class="walkthrough-step" data-step="5">
<strong>The watermark advances.</strong> The <code>ETL.MatchWatermark</code> is updated so the next 60-second cycle only processes new visits. Failed matches (no consumer record found) are skipped, they''ll be retried if new consumer data arrives.
</div>',
    MermaidDiagram = N'sequenceDiagram
    participant ETL as EtlBackgroundService
    participant WM as ETL.MatchWatermark
    participant Visit as PiXL.Visit
    participant AC as AutoConsumer (275M+)
    participant Match as PiXL.Match

    ETL->>WM: Read last processed ID
    WM-->>ETL: LastProcessedId = N
    ETL->>Visit: SELECT WHERE VisitId > N
    Visit-->>ETL: Unmatched visits
    loop Each visit
        ETL->>AC: Strategy 1: UID lookup
        ETL->>AC: Strategy 2: IP + Geo proximity (692m)
        ETL->>AC: Strategy 3: Cookie correlation
        ETL->>AC: Strategy 4: Direct IP
        AC-->>ETL: Match result (or no match)
        ETL->>Match: MERGE match record
    end
    ETL->>WM: Update watermark',
    LastUpdated = SYSUTCDATETIME(),
    UpdatedBy = 'agent:atlas'
WHERE Slug = 'identity-resolution';
GO

-- ═══════════════════════════════════════════════════════════════════
-- SECTION 6: Bot Detection & Evasion
-- ═══════════════════════════════════════════════════════════════════
UPDATE Docs.Section SET
    ManagementHtml = N'
<h4>Multi-Layer Bot Detection</h4>
<p>SmartPiXL detects bots and fingerprint evasion attempts through multiple independent detection layers, each targeting a different evasion technique. This defense-in-depth approach means adversaries must defeat <em>all</em> layers simultaneously, not just one.</p>
<table>
    <tr><th>Layer</th><th>Detection Method</th><th>What It Catches</th><th>Status</th></tr>
    <tr><td>Canvas Noise</td><td>Multi-canvas cross-validation</td><td>Canvas Blocker, JShelter, Trace</td><td>Live</td></tr>
    <tr><td>Audio Noise</td><td>Oscillator output consistency</td><td>AudioContext spoofing</td><td>Live</td></tr>
    <tr><td>Behavioral</td><td>Mouse entropy, timing patterns</td><td>Headless browsers, automation</td><td>Live</td></tr>
    <tr><td>Stealth Plugins</td><td>CDP leak detection</td><td>Puppeteer Extra, Playwright</td><td>Live</td></tr>
    <tr><td>Anti-Detect</td><td>Fingerprint stability per IP</td><td>Multilogin, GoLogin, Dolphin</td><td>Live</td></tr>
    <tr><td>Datacenter IP</td><td>AWS/GCP CIDR range matching</td><td>Cloud-hosted bots</td><td>Live</td></tr>
    <tr><td>IP Velocity</td><td>Subnet /24 request frequency</td><td>Coordinated bot farms</td><td>Live</td></tr>
    <tr><td>Rapid-Fire</td><td>Inter-request timing analysis</td><td>Automated scripts</td><td>Live</td></tr>
    <tr><td>Font Spoofing</td><td>Internal font consistency</td><td>Font list manipulation</td><td>Live</td></tr>
    <tr><td>TZ Mismatch</td><td>Browser TZ vs IP geo TZ</td><td>VPN/proxy users</td><td>Live</td></tr>
</table>
<p>9 of 10 countermeasures from the red team assessment are implemented. V-07 (TLS/JA3 fingerprinting) is deferred pending reverse proxy infrastructure.</p>',
    TechnicalHtml = N'
<h4>Detection Services</h4>
<table>
    <tr><th>Service</th><th>File</th><th>Enrichment Params</th></tr>
    <tr><td>FingerprintStabilityService</td><td><code>Services/FingerprintStabilityService.cs</code></td><td><code>_srv_fpStability</code></td></tr>
    <tr><td>IpBehaviorService</td><td><code>Services/IpBehaviorService.cs</code></td><td><code>_srv_ipVelocity</code>, <code>_srv_rapidFire</code></td></tr>
    <tr><td>DatacenterIpService</td><td><code>Services/DatacenterIpService.cs</code></td><td><code>_srv_dcName</code>, <code>_srv_ipClass=Datacenter</code></td></tr>
    <tr><td>IpClassificationService</td><td><code>Services/IpClassificationService.cs</code></td><td><code>_srv_ipClass</code></td></tr>
</table>
<h4>Client-Side Detection (PiXLScript.cs)</h4>
<ul>
    <li><strong>Canvas noise:</strong> Draws the same scene on two canvases. If hashes differ → noise injection detected. Param: <code>cn=1</code></li>
    <li><strong>Audio noise:</strong> Runs OscillatorNode twice with identical params. Frequency variance → spoofing. Param: <code>an=1</code></li>
    <li><strong>WebDriver:</strong> Checks <code>navigator.webdriver</code>, <code>window.chrome.cdc</code> (CDP leak). Param: <code>wd=1</code></li>
    <li><strong>Mouse entropy:</strong> Samples mouse movement coordinates. Zero entropy → no real human. Param: <code>me=&lt;score&gt;</code></li>
</ul>
<h4>PiXL.Parsed Bot Columns</h4>
<p>ETL parses these signals into dedicated <code>PiXL.Parsed</code> columns for dashboard analytics and match quality filtering.</p>
<p>See <code>docs/EVASION_COUNTERMEASURES.md</code> for the complete red team assessment and implementation details.</p>',
    WalkthroughHtml = N'
<h4>Step-by-Step: How Bot Detection Works</h4>
<div class="walkthrough-step" data-step="1">
<strong>Client-side signals fire first.</strong> When PiXLScript.js runs in the browser, it performs real-time detection: drawing two identical canvases and comparing hashes (noise injection check), running the audio oscillator twice (audio spoofing check), probing for <code>navigator.webdriver</code> and CDP leaks (automation check), and sampling mouse movement entropy (human presence check). These results are included as query parameters in the pixel URL.
</div>
<div class="walkthrough-step" data-step="2">
<strong>Server-side enrichment adds more signals.</strong> When the pixel hit arrives at the server, <code>FingerprintStabilityService</code> checks if this IP has sent multiple different fingerprints in the last 24 hours (anti-detect browser signal). <code>IpBehaviorService</code> checks if the /24 subnet has unusually high traffic (bot farm signal) and if the inter-request timing is suspiciously regular (automation signal).
</div>
<div class="walkthrough-step" data-step="3">
<strong>DatacenterIpService checks cloud ranges.</strong> AWS publishes their IP ranges as a JSON file. GCP does the same. <code>DatacenterIpService</code> downloads these on startup and refreshes weekly. If the visitor''s IP falls within any cloud provider''s CIDR range, it''s flagged as <code>_srv_ipClass=Datacenter</code> with the specific prefix name.
</div>
<div class="walkthrough-step" data-step="4">
<strong>GeoCacheService checks timezone mismatch.</strong> The browser reports its timezone (e.g., "America/New_York"). The IP geolocation says the visitor is in California. If these don''t match, it''s flagged as <code>_srv_tzMismatch=true</code>, a strong VPN/proxy signal.
</div>
<div class="walkthrough-step" data-step="5">
<strong>All signals are stored and analyzed.</strong> The ETL pipeline parses these signals into dedicated <code>PiXL.Parsed</code> columns. The Tron dashboard displays bot detection rates, evasion attempt breakdowns, and behavioral analysis in real time. The match process uses bot signals to filter out non-human traffic before identity resolution.
</div>',
    MermaidDiagram = N'graph LR
    subgraph "Client-Side Detection"
        CV["Canvas Noise<br/>Multi-canvas cross-validation"]
        AU["Audio Noise<br/>Oscillator consistency"]
        WD["WebDriver<br/>CDP leak detection"]
        ME["Mouse Entropy<br/>Human presence check"]
    end
    
    subgraph "Server-Side Detection"
        FP["FingerprintStability<br/>Per-IP variation scoring"]
        IPB["IpBehavior<br/>Subnet velocity + timing"]
        DC["DatacenterIp<br/>AWS/GCP CIDR matching"]
        GEO["GeoCacheService<br/>TZ mismatch detection"]
    end
    
    CV --> SCORE["Combined Bot<br/>Signal Profile"]
    AU --> SCORE
    WD --> SCORE
    ME --> SCORE
    FP --> SCORE
    IPB --> SCORE
    DC --> SCORE
    GEO --> SCORE
    
    SCORE --> ETL["PiXL.Parsed<br/>Bot columns"]

    style SCORE fill:#1a2035,stroke:#ef4444,color:#f0f4f8
    style ETL fill:#1a2035,stroke:#10b981,color:#f0f4f8',
    LastUpdated = SYSUTCDATETIME(),
    UpdatedBy = 'agent:atlas'
WHERE Slug = 'bot-detection';
GO

-- ═══════════════════════════════════════════════════════════════════
-- SECTION 7: IP Geolocation
-- ═══════════════════════════════════════════════════════════════════
UPDATE Docs.Section SET
    ManagementHtml = N'
<h4>Real-Time IP Geolocation System</h4>
<p>SmartPiXL resolves visitor IP addresses to physical locations in real time using a 342-million-row geolocation database. A two-tier caching architecture ensures sub-millisecond lookups for repeat IPs while staying current through daily incremental syncs.</p>
<table>
    <tr><th>Component</th><th>Description</th></tr>
    <tr><td>IPAPI.IP Table</td><td>342M rows of IP-to-location mappings, synced from Xavier</td></tr>
    <tr><td>Hot Cache (Tier 1)</td><td>ConcurrentDictionary, most recent IPs, instant lookup</td></tr>
    <tr><td>Warm Cache (Tier 2)</td><td>MemoryCache with sliding expiration</td></tr>
    <tr><td>Daily Sync</td><td>Watermark-based incremental sync from Xavier IPGEO database</td></tr>
    <tr><td>Pre-warming</td><td>Top 2,000 IPs loaded into cache at startup</td></tr>
</table>
<p>Geolocation data powers several critical features: timezone mismatch bot detection, geo-proximity identity matching (692m radius), and geographic analytics in the dashboard.</p>',
    TechnicalHtml = N'
<h4>GeoCacheService</h4>
<p>File: <code>Services/GeoCacheService.cs</code>. Non-blocking, two-tier IP geolocation lookup.</p>
<ul>
    <li><strong>Tier 1 (Hot):</strong> <code>ConcurrentDictionary&lt;string, GeoResult&gt;</code>, O(1) lookup, no contention</li>
    <li><strong>Tier 2 (Warm):</strong> <code>MemoryCache</code> with 1-hour sliding expiration</li>
    <li><strong>Miss path:</strong> Writes IP to a bounded <code>Channel&lt;string&gt;</code>. Background reader performs SQL lookup against <code>IPAPI.IP</code> and populates both cache tiers.</li>
    <li><strong>Pre-warm:</strong> At startup, queries top 2,000 IPs by hit frequency from <code>PiXL.IP</code> and bulk-loads into hot cache.</li>
</ul>
<h4>IpApiSyncService</h4>
<p>File: <code>Services/IpApiSyncService.cs</code>. Daily incremental sync from Xavier (<code>192.168.88.35</code>).</p>
<ul>
    <li>Source: <code>IPGEO.dbo.IP_Location_New</code> on Xavier</li>
    <li>Target: <code>IPAPI.IP</code> on local <code>SmartPiXL</code> database</li>
    <li>Method: Watermark by <code>Last_Seen</code>. Staging table → MERGE → clear geo hot cache.</li>
    <li>Runs at 2 AM UTC (configurable via <code>TrackingSettings.IpApiSyncHourUtc</code>)</li>
</ul>
<h4>IPAPI Schema</h4>
<pre><code>IPAPI.IP           -- 342M rows: IPFrom, IPTo, Country, Region, City, Lat, Lon, TZ, ISP
IPAPI.SyncLog      -- Audit trail: SyncId, StartedAt, RowsSynced, Status, ErrorMessage</code></pre>
<h4>Geo Enrichment</h4>
<p><code>ETL.usp_EnrichParsedGeo</code> (SQL/26) backfills geo columns on <code>PiXL.Parsed</code> for rows that were ingested before their IP was in the geo cache.</p>',
    WalkthroughHtml = N'
<h4>Step-by-Step: How IP Geolocation Works</h4>
<div class="walkthrough-step" data-step="1">
<strong>A pixel hit arrives with IP 73.162.45.123.</strong> During the enrichment pipeline (before database write), <code>GeoCacheService.TryGetGeo()</code> is called with this IP.
</div>
<div class="walkthrough-step" data-step="2">
<strong>Tier 1 cache check.</strong> The service looks in the <code>ConcurrentDictionary</code>, a lock-free O(1) lookup. If the IP was seen recently, we get an instant <code>GeoResult</code> with city, region, country, latitude, longitude, and timezone. Cache hit → done, append <code>_srv_geoCity=Portland&amp;_srv_geoRegion=OR</code> to the query string.
</div>
<div class="walkthrough-step" data-step="3">
<strong>Tier 2 cache check.</strong> If not in Tier 1, check <code>MemoryCache</code>. This has a larger capacity with sliding expiration. Cache hit → promote to Tier 1 and return.
</div>
<div class="walkthrough-step" data-step="4">
<strong>Cache miss → background lookup.</strong> The IP is written to a bounded <code>Channel&lt;string&gt;</code>. The current request proceeds without geo data (the GIF is already being sent). A background reader task picks up the IP, does a range query against <code>IPAPI.IP</code> (WHERE @IP BETWEEN IPFrom AND IPTo), and populates both cache tiers. The <em>next</em> hit from this IP will get instant geo data.
</div>
<div class="walkthrough-step" data-step="5">
<strong>Daily sync keeps the data fresh.</strong> At 2 AM UTC, <code>IpApiSyncService</code> connects to Xavier''s IPGEO database, reads rows where <code>Last_Seen</code> is newer than our watermark, stages them via a temp table, and MERGEs into <code>IPAPI.IP</code>. After sync, the hot cache is cleared so lookups pick up the new data.
</div>',
    MermaidDiagram = N'graph TD
    HIT["Pixel Hit<br/>IP: 73.162.45.123"] --> T1{"Tier 1: ConcurrentDict<br/>O(1) lookup"}
    T1 -->|Hit| RESULT["GeoResult<br/>City, Region, TZ"]
    T1 -->|Miss| T2{"Tier 2: MemoryCache<br/>Sliding expiration"}
    T2 -->|Hit| RESULT
    T2 -->|Miss| CHAN["Channel&lt;string&gt;<br/>Background queue"]
    CHAN --> SQL["SQL: IPAPI.IP<br/>342M rows"]
    SQL --> T1

    SYNC["IpApiSyncService<br/>Daily 2AM UTC"] --> XAVIER["Xavier IPGEO<br/>192.168.88.35"]
    XAVIER --> SQL

    style HIT fill:#1a2035,stroke:#3b82f6,color:#f0f4f8
    style RESULT fill:#1a2035,stroke:#10b981,color:#f0f4f8
    style SQL fill:#1a2035,stroke:#f59e0b,color:#f0f4f8',
    LastUpdated = SYSUTCDATETIME(),
    UpdatedBy = 'agent:atlas'
WHERE Slug = 'ip-geolocation';
GO

-- ═══════════════════════════════════════════════════════════════════
-- SECTION 8: Real-Time Dashboard
-- ═══════════════════════════════════════════════════════════════════
UPDATE Docs.Section SET
    ManagementHtml = N'
<h4>Tron Operations Dashboard</h4>
<p>The Tron dashboard is a real-time operations console built with Three.js WebGL rendering. It provides instant visibility into traffic patterns, bot detection, pipeline health, and system performance. Access is restricted to localhost and explicitly whitelisted IPs.</p>
<table>
    <tr><th>Panel</th><th>Data Source</th><th>What It Shows</th></tr>
    <tr><td>System Health</td><td>vw_Dash_SystemHealth</td><td>Total hits, bot %, evasion %, uptime</td></tr>
    <tr><td>Hourly Traffic</td><td>vw_Dash_HourlyRollup</td><td>Time-bucketed hit counts (up to 30 days)</td></tr>
    <tr><td>Bot Breakdown</td><td>vw_Dash_BotBreakdown</td><td>Risk tier distribution (High/Medium/Low/Clean)</td></tr>
    <tr><td>Detection Signals</td><td>vw_Dash_TopBotSignals</td><td>Top 20 triggered bot detection signals</td></tr>
    <tr><td>Devices</td><td>vw_Dash_DeviceBreakdown</td><td>Top 30 browser/OS/device combinations</td></tr>
    <tr><td>Evasion</td><td>vw_Dash_EvasionSummary</td><td>Canvas/WebGL evasion detection rates</td></tr>
    <tr><td>Behavior</td><td>vw_Dash_BehavioralAnalysis</td><td>Interaction timing and mouse entropy</td></tr>
    <tr><td>Recent Hits</td><td>vw_Dash_RecentHits</td><td>Live feed of latest raw pixel hits</td></tr>
    <tr><td>Fingerprints</td><td>vw_Dash_FingerprintClusters</td><td>Grouped fingerprint patterns</td></tr>
    <tr><td>Infrastructure</td><td>InfraHealthService</td><td>Windows services, SQL health, IIS, data flow</td></tr>
    <tr><td>Pipeline</td><td>vw_Dash_PipelineHealth</td><td>ETL watermarks, lag, row counts, freshness</td></tr>
</table>',
    TechnicalHtml = N'
<h4>Dashboard Architecture</h4>
<p>File: <code>Endpoints/DashboardEndpoints.cs</code> (366 lines). All endpoints at <code>/api/dash/*</code> are protected by <code>RequireLoopback()</code>, returns HTTP 404 (not 403) to external callers to avoid revealing the API exists.</p>
<h4>API Endpoints</h4>
<pre><code>GET /api/dash/health        → vw_Dash_SystemHealth (single row aggregate)
GET /api/dash/hourly?hours=N → vw_Dash_HourlyRollup (default 72h, max 720h)
GET /api/dash/bots          → vw_Dash_BotBreakdown
GET /api/dash/bot-signals   → vw_Dash_TopBotSignals (top 20)
GET /api/dash/devices       → vw_Dash_DeviceBreakdown (top 30)
GET /api/dash/evasion       → vw_Dash_EvasionSummary
GET /api/dash/behavior      → vw_Dash_BehavioralAnalysis
GET /api/dash/recent        → vw_Dash_RecentHits
GET /api/dash/fingerprints  → vw_Dash_FingerprintClusters (top 50, max 200)
GET /api/dash/infra         → InfraHealthService (live probes)
GET /api/dash/pipeline      → vw_Dash_PipelineHealth</code></pre>
<h4>Frontend</h4>
<p><code>wwwroot/tron.html</code>, Single HTML file, no build tools. Three.js for 3D WebGL scene, custom data panels overlaid. Cyberpunk/Tron Legacy aesthetic. Polls <code>/api/dash/*</code> every 10 seconds.</p>
<h4>Access Control</h4>
<p>Configured via <code>Tracking:DashboardAllowedIPs</code> in <code>appsettings.json</code>. Loopback addresses (127.0.0.1, ::1) and same-machine connections are always allowed. IPv4-mapped IPv6 is handled transparently.</p>',
    WalkthroughHtml = N'
<h4>Step-by-Step: How the Dashboard Works</h4>
<div class="walkthrough-step" data-step="1">
<strong>You navigate to /tron.</strong> The route serves <code>wwwroot/tron.html</code>. This is a single HTML file with inline CSS and JavaScript, no build tools, no npm, no frameworks. Three.js handles the 3D WebGL scene.
</div>
<div class="walkthrough-step" data-step="2">
<strong>The dashboard starts polling.</strong> On load, JavaScript fetches all 11 API endpoints simultaneously (<code>/api/dash/health</code>, <code>/api/dash/hourly</code>, etc.). Each endpoint runs a SQL query against a <code>vw_Dash_*</code> view that reads from <code>PiXL.Parsed</code> (the materialized warehouse, fast indexed reads, not raw query string parsing).
</div>
<div class="walkthrough-step" data-step="3">
<strong>Data panels render.</strong> Health stats, hourly traffic charts, bot breakdown pies, recent hit feeds, all render into the 3D scene as floating holographic panels. The aesthetic is cinema-fidelity Tron Legacy.
</div>
<div class="walkthrough-step" data-step="4">
<strong>Every 10 seconds, it refreshes.</strong> The polling loop calls all endpoints again. SQL views return fresh data (they read from PiXL.Parsed which the ETL updates every 60 seconds). Panels animate transitions smoothly.
</div>
<div class="walkthrough-step" data-step="5">
<strong>Infrastructure probes run live.</strong> The <code>/api/dash/infra</code> endpoint is different, it doesn''t query a SQL view. Instead, <code>InfraHealthService</code> probes the actual Windows services (SQL Server, IIS), checks SQL connectivity, tests IIS website status, and reports in-process app metrics. Results are cached 15 seconds.
</div>',
    LastUpdated = SYSUTCDATETIME(),
    UpdatedBy = 'agent:atlas'
WHERE Slug = 'dashboard';
GO

PRINT 'All 8 sections populated with 4-tier content.';
GO
