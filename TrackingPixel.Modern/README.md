# SmartPiXL Tracking Server - Modern Edition

A high-performance, modern ASP.NET Core tracking pixel server that captures 100+ data points from website visitors using a single `<script>` tag. No cookies required.

## 🎯 Overview

SmartPiXL is a complete rewrite of the legacy ASP.NET WebForms tracking pixel system, rebuilt for speed and maximum data collection using .NET 8 Minimal APIs.

### Key Features

- **One Line Integration** - Clients add a single `<script>` tag to their site
- **100+ Data Points** - Captures screen, device, browser, fingerprints, preferences, and more
- **No Cookies Required** - Works in a post-cookie world
- **High Performance** - Fire-and-forget SQL inserts, compiled regex, zero-allocation patterns
- **Full Fingerprinting** - 100+ data points from a single script tag

## 🚀 Quick Start

### Prerequisites

- .NET 8 SDK
- SQL Server (localhost with Windows Auth or configure connection string)
- HTTPS required for full Client Hints support

### Run the Server

```bash
cd TrackingPixel.Modern
dotnet run
```

Server starts on:
- HTTP: `http://localhost:6000`
- HTTPS: `https://localhost:6001` (required for Client Hints)

### Test It

Visit: `https://localhost:6001/test`

See all 100+ data points captured in real-time from YOUR browser.

## 📦 Client Integration

### Integration

Add this single line to any webpage:

```html
<script src="https://your-domain.com/js/CompanyID/PiXLID.js"></script>
```

That's it. The script handles everything automatically.

### What Gets Captured

| Category | Data Points |
|----------|-------------|
| **Screen** | Resolution, available space, viewport, window position, color depth, pixel ratio, orientation |
| **Device** | CPU cores, RAM, GPU, GPU vendor, touch points, platform, vendor |
| **Fingerprints** | Canvas hash, WebGL hash, Audio hash, Math fingerprint, Error fingerprint, Font list |
| **Network** | WebRTC local IP, connection type/speed, RTT, data saver mode |
| **Storage** | Storage quota/usage, battery level/charging, media devices count |
| **Browser** | Cookies, DNT, PDF viewer, WebDriver (bot detection), plugins, MIME types |
| **Features** | WebGL, WebAssembly, Service Workers, Canvas, WebRTC, and more (hardware/permission APIs intentionally excluded) |
| **Preferences** | Dark mode, reduced motion, high contrast, forced colors, hover/pointer type |
| **Performance** | Page load time, DOM ready, DNS lookup, TCP connect, TTFB |
| **Session** | URL, referrer, title, domain, path, history depth |

## 🔐 Fingerprinting Techniques

SmartPiXL uses multiple browser fingerprinting techniques:

### Canvas Fingerprint
Renders specific shapes and text, then hashes the pixel data. Different GPUs/drivers produce different results.

### WebGL Fingerprint
Collects 23+ WebGL parameters including max texture sizes, shader precision, and supported extensions.

### Audio Fingerprint
Uses OfflineAudioContext to create a fingerprint from the audio processing stack.

### Font Detection
Tests 42 common fonts by measuring text rendering differences.

### Math Fingerprint
JavaScript math operations have subtle floating-point differences across systems.

### WebRTC Local IP
Uses RTCPeerConnection to discover the user's local network IP address.

## 🏗️ Architecture

```
TrackingPixel.Modern/
├── Program.cs           # Minimal API entry point (~130 lines)
├── Configuration/       # App settings and configuration
├── Endpoints/           # API endpoint handlers
├── Models/              # Data models (TrackingData, etc.)
├── Services/            # Business logic services
├── wwwroot/
│   └── test.html        # Live data collection demo
├── SQL/
│   └── 00_FreshInstall.sql     # Complete schema (table + view + materialization)
└── FINGERPRINTING_EXPLAINED.md # Technical documentation
```

**Data Flow Architecture:**
1. Client JS collects 100+ data points → sends via query string
2. C# writes raw data to `PiXL_Test` table (8 columns)
3. SQL view `vw_PiXL_Parsed` extracts all fields at query time
4. Optional: `sp_MaterializePiXLData` copies to indexed table for fast queries

### Performance Optimizations

- **Fire-and-Forget SQL** - BlockingCollection with batched bulk inserts
- **Compiled Regex** - Pre-compiled patterns for URL parsing
- **DataTable Template Cloning** - Avoids column recreation per batch
- **Const JS Template** - Pre-built JavaScript with `string.Format` placeholder
- **stackalloc** - Stack allocation for small arrays
- **CORS Preflight Handling** - Immediate OPTIONS responses

## 🗄️ Database

### Quick Setup

```sql
-- Create database with filegroup on D: drive
CREATE DATABASE SmartPixl;
GO

-- Run the fresh install script
-- SQL/00_FreshInstall.sql
```

### Query Captured Data

```sql
-- View all parsed data (no manual parsing needed!)
SELECT * FROM vw_PiXL_Parsed 
WHERE ReceivedAt > DATEADD(hour, -1, GETDATE())
ORDER BY ReceivedAt DESC;

-- Fingerprint uniqueness
SELECT CanvasFingerprint, WebGLFingerprint, COUNT(*) as Hits
FROM vw_PiXL_Parsed
GROUP BY CanvasFingerprint, WebGLFingerprint
ORDER BY Hits DESC;

-- Bot detection
SELECT * FROM vw_PiXL_Parsed
WHERE WebDriverDetected = 1;
```

## 📊 Data Collection

SmartPiXL captures 100+ data points via a single JavaScript tag, including screen info,
device details, browser fingerprints (Canvas, WebGL, Audio), timing data, and evasion
countermeasures. All data is stored in `PiXL_Test` and parsed into `PiXL_Parsed` (~175 columns).

## 🔧 Configuration

### Connection String

Edit in `Program.cs`:

```csharp
const string connectionString = 
    "Server=localhost;Database=SmartPixl;Integrated Security=True;TrustServerCertificate=True";
```

### Ports

```csharp
builder.WebHost.UseUrls("http://*:5000", "https://*:5001");
```

## 📝 Endpoints

| Endpoint | Purpose |
|----------|---------|
| `/{CompanyID}/{PiXLID}_SMART.GIF` | Tracking pixel (receives data) |
| `/js/{CompanyID}/{PiXLID}.js` | Pixel JavaScript |
| `/test` | Live demo page |
| `/debug/headers` | Debug incoming headers |

## 🛡️ Privacy & Compliance

This tool collects extensive data. Ensure you:

- Have proper consent mechanisms
- Include tracking in privacy policies
- Comply with GDPR, CCPA, and other regulations
- Consider data minimization principles

## 📜 License

Proprietary - M1 Data & Analytics

## 🤝 Support

Internal use only. Contact the development team for support.

---

Built with ❤️ by M1 Data & Analytics
