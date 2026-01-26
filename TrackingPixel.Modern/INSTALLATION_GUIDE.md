# SmartPiXL Installation Guide v1.0

> **Audience**: M1 internal deployment. This guide covers deploying SmartPiXL to Windows Server with IIS and SQL Server.

---

## 📋 Prerequisites

### Server Requirements

| Component | Minimum | Recommended |
|-----------|---------|-------------|
| OS | Windows Server 2019 | Windows Server 2022 |
| .NET | .NET 8.0 Runtime | .NET 8.0 Hosting Bundle |
| IIS | 10.0 | 10.0 with URL Rewrite |
| SQL Server | 2017 | 2019+ |
| RAM | 2 GB | 4+ GB |
| Disk | 10 GB | 50 GB (for logs) |

### Required Downloads

1. **.NET 8.0 Hosting Bundle** (includes runtime + IIS module)
   - https://dotnet.microsoft.com/download/dotnet/8.0
   - Download: "Hosting Bundle" under ASP.NET Core Runtime

2. **URL Rewrite Module** (optional, for clean URLs)
   - https://www.iis.net/downloads/microsoft/url-rewrite

---

## 🗄️ Step 1: Database Setup

### 1.1 Create the Database

Connect to SQL Server and run:

```sql
CREATE DATABASE SmartPixl;
GO
```

### 1.2 Create the Application Login

```sql
USE master;
GO

CREATE LOGIN PiXL WITH PASSWORD = 'YOUR_STRONG_PASSWORD_HERE';
GO

USE SmartPixl;
GO

CREATE USER PiXL FOR LOGIN PiXL;
GO

-- Grant necessary permissions
ALTER ROLE db_datareader ADD MEMBER PiXL;
ALTER ROLE db_datawriter ADD MEMBER PiXL;
GRANT EXECUTE TO PiXL;
GO
```

### 1.3 Run Schema Scripts

Execute in order:

1. **Base Schema** - `SQL/01_InitialSchema.sql` (if exists)
2. **Expanded Schema** - `SQL/02_ExpandedSchema.sql`

```sql
-- Connect to SmartPixl database first
USE SmartPixl;
GO

-- Then run the schema script content
```

### 1.4 Verify Schema

After running scripts, confirm the table and view exist:

```sql
-- Check table columns (should be 100+)
SELECT COUNT(*) AS ColumnCount 
FROM sys.columns 
WHERE object_id = OBJECT_ID('PiXL_Test');

-- Check view exists
SELECT * FROM sys.views WHERE name = 'vw_PiXL_Parsed';

-- Quick test
SELECT TOP 1 * FROM vw_PiXL_Parsed;
```

---

## 🌐 Step 2: IIS Configuration

### 2.1 Install IIS Features

Open PowerShell as Administrator:

```powershell
# Install IIS with required features
Install-WindowsFeature -Name Web-Server -IncludeManagementTools
Install-WindowsFeature -Name Web-Asp-Net45
Install-WindowsFeature -Name Web-WebSockets
```

### 2.2 Install .NET 8 Hosting Bundle

1. Download from https://dotnet.microsoft.com/download/dotnet/8.0
2. Run `dotnet-hosting-8.x.x-win.exe`
3. **Restart IIS** after installation:

```powershell
net stop was /y
net start w3svc
```

### 2.3 Create Application Folder

```powershell
# Create the deployment folder
New-Item -Path "C:\inetpub\SmartPiXL" -ItemType Directory -Force

# Set permissions
icacls "C:\inetpub\SmartPiXL" /grant "IIS_IUSRS:(OI)(CI)RX"
icacls "C:\inetpub\SmartPiXL" /grant "IUSR:(OI)(CI)RX"
```

### 2.4 Create IIS Application Pool

```powershell
Import-Module WebAdministration

# Create app pool with No Managed Code (required for .NET Core)
New-WebAppPool -Name "SmartPiXL"
Set-ItemProperty "IIS:\AppPools\SmartPiXL" -Name "managedRuntimeVersion" -Value ""
Set-ItemProperty "IIS:\AppPools\SmartPiXL" -Name "startMode" -Value "AlwaysRunning"
```

Or via IIS Manager:
1. Open IIS Manager → Application Pools → Add Application Pool
2. Name: `SmartPiXL`
3. .NET CLR Version: **No Managed Code**
4. Start mode: **AlwaysRunning** (keeps app warm)

### 2.5 Create IIS Website

```powershell
# Create the site
New-Website -Name "SmartPiXL" `
    -PhysicalPath "C:\inetpub\SmartPiXL" `
    -ApplicationPool "SmartPiXL" `
    -Port 80 `
    -HostHeader "tracking.yourdomain.com"
```

Or via IIS Manager:
1. Sites → Add Website
2. Site name: `SmartPiXL`
3. Physical path: `C:\inetpub\SmartPiXL`
4. Application pool: `SmartPiXL`
5. Host name: `tracking.yourdomain.com`

---

## 📦 Step 3: Deploy Application

### 3.1 Build for Production

On your development machine:

```powershell
cd TrackingPixel.Modern

# Publish for Windows Server
dotnet publish -c Release -r win-x64 --self-contained false -o ./publish
```

**Options explained:**
- `-c Release` - Optimized build
- `-r win-x64` - Target Windows 64-bit
- `--self-contained false` - Use server's .NET runtime (smaller deploy)
- `-o ./publish` - Output folder

### 3.2 Copy to Server

Copy the contents of `./publish` to `C:\inetpub\SmartPiXL\` on the server.

Required files:
```
C:\inetpub\SmartPiXL\
├── TrackingPixel.dll
├── TrackingPixel.exe
├── appsettings.json
├── web.config
└── wwwroot/
    └── test.html
```

### 3.3 Configure Connection String

Edit `C:\inetpub\SmartPiXL\appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ConnectionStrings": {
    "SmartPiXL": "Server=YOUR_SQL_SERVER;Database=SmartPixl;User Id=PiXL;Password=YOUR_PASSWORD;TrustServerCertificate=True"
  }
}
```

**Connection string examples:**

| Scenario | Connection String |
|----------|-------------------|
| Local SQL, SQL Auth | `Server=127.0.0.1;Database=SmartPixl;User Id=PiXL;Password=xxx;TrustServerCertificate=True` |
| Local SQL, Windows Auth | `Server=.;Database=SmartPixl;Integrated Security=True;TrustServerCertificate=True` |
| Remote SQL | `Server=sql.yourdomain.com;Database=SmartPixl;User Id=PiXL;Password=xxx;TrustServerCertificate=True;Encrypt=True` |

---

## 🔒 Step 4: SSL Certificate (Required)

> **⚠️ HTTPS is required** for Client Hints and many fingerprinting features. Without SSL, you lose ~20% of data points.

### 4.1 Option A: Let's Encrypt (Free)

Use win-acme: https://www.win-acme.com/

```powershell
# Download and run win-acme
# It will auto-configure IIS bindings
wacs.exe
```

### 4.2 Option B: Commercial Certificate

1. Generate CSR in IIS Manager → Server Certificates → Create Certificate Request
2. Submit to your CA (DigiCert, Comodo, etc.)
3. Complete request and bind to site

### 4.3 Bind Certificate to Site

```powershell
# Get certificate thumbprint
$cert = Get-ChildItem -Path Cert:\LocalMachine\My | Where-Object { $_.Subject -like "*tracking.yourdomain.com*" }

# Add HTTPS binding
New-WebBinding -Name "SmartPiXL" -Protocol "https" -Port 443 -HostHeader "tracking.yourdomain.com" -SslFlags 1
$binding = Get-WebBinding -Name "SmartPiXL" -Protocol "https"
$binding.AddSslCertificate($cert.Thumbprint, "My")
```

---

## ✅ Step 5: Verify Installation

### 5.1 Test the Endpoint

Open browser to: `https://tracking.yourdomain.com/test`

You should see the test page with live data collection.

### 5.2 Test Data Flow

```powershell
# Quick curl test
Invoke-WebRequest -Uri "https://tracking.yourdomain.com/t?sw=1920&sh=1080&tier=1" -Method GET
```

### 5.3 Verify Database Insert

```sql
-- Check for recent records
SELECT TOP 10 * 
FROM PiXL_Test 
ORDER BY ReceivedAt DESC;

-- Check parsed view
SELECT TOP 10 * 
FROM vw_PiXL_Parsed 
ORDER BY ReceivedAt DESC;
```

---

## 🏷️ Step 6: Client Script Integration

### 6.1 Generate Client Script URL

The script URL format is:
```
https://tracking.yourdomain.com/js/{CLIENT_ID}/{CAMPAIGN_ID}.js
```

Example:
```
https://tracking.yourdomain.com/js/M1DATA/HOMEPAGE.js
```

### 6.2 Add to Website

Add this single line before `</body>`:

```html
<script src="https://tracking.yourdomain.com/js/M1DATA/HOMEPAGE.js"></script>
```

That's it. The script handles everything automatically.

### 6.3 Test on Your Site

1. Open browser DevTools (F12) → Network tab
2. Load your page
3. Look for request to `/t?...` with 200 response
4. Check database for new record

---

## 🔧 Troubleshooting

### Application Won't Start

```powershell
# Check Event Viewer
Get-EventLog -LogName Application -Source "IIS*" -Newest 10

# Check stdout log (if enabled)
Get-Content "C:\inetpub\SmartPiXL\logs\stdout*.log"
```

Enable stdout logging in `web.config`:
```xml
<aspNetCore stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout" ... />
```

### 502.5 Error

1. Verify .NET 8 Hosting Bundle is installed
2. Restart IIS: `iisreset`
3. Check app pool is running and set to "No Managed Code"

### Database Connection Failed

1. Verify SQL Server allows TCP/IP connections
2. Check firewall allows port 1433
3. Test connection string with SSMS
4. Verify user has permissions

### No Data Appearing

1. Check browser console for JavaScript errors
2. Verify script URL is accessible (no CORS errors)
3. Check browser Network tab for `/t?` request
4. Verify SQL permissions allow INSERT

### Partial Data (Missing Fingerprints)

1. **HTTPS required** - Most fingerprinting APIs require secure context
2. Check browser compatibility - Some features are Chrome/Edge only
3. Check browser console for feature detection failures

---

## 📊 Monitoring

### Check Active Connections

```sql
-- Records per hour
SELECT 
    DATEPART(HOUR, ReceivedAt) AS Hour,
    COUNT(*) AS Records
FROM PiXL_Test
WHERE ReceivedAt > DATEADD(DAY, -1, GETDATE())
GROUP BY DATEPART(HOUR, ReceivedAt)
ORDER BY Hour;
```

### Check Tier Distribution

```sql
-- What tier are most clients hitting?
SELECT 
    Tier,
    COUNT(*) AS Count,
    CAST(COUNT(*) * 100.0 / SUM(COUNT(*)) OVER() AS DECIMAL(5,2)) AS Percentage
FROM vw_PiXL_Parsed
WHERE ReceivedAt > DATEADD(DAY, -7, GETDATE())
GROUP BY Tier
ORDER BY Tier;
```

### IIS Performance

```powershell
# Current requests
Get-Counter '\Web Service(_Total)\Current Connections'

# Requests per second
Get-Counter '\Web Service(_Total)\Get Requests/sec'
```

---

## 🚀 Performance Tuning

### IIS Settings

```powershell
# Increase queue length for high traffic
Set-ItemProperty "IIS:\AppPools\SmartPiXL" -Name queueLength -Value 5000

# Disable idle timeout (keep app warm)
Set-ItemProperty "IIS:\AppPools\SmartPiXL" -Name processModel.idleTimeout -Value "00:00:00"
```

### SQL Server

```sql
-- Add index for common queries
CREATE INDEX IX_PiXL_Test_ReceivedAt ON PiXL_Test(ReceivedAt DESC);
CREATE INDEX IX_PiXL_Test_ClientCampaign ON PiXL_Test(ClientID, CampaignID, ReceivedAt DESC);
```

---

## 📝 Quick Reference

| Task | Command/URL |
|------|-------------|
| Test endpoint | `https://tracking.yourdomain.com/test` |
| Health check | `https://tracking.yourdomain.com/health` |
| Raw pixel (Tier 1) | `https://tracking.yourdomain.com/t?tier=1&sw=1920&sh=1080` |
| Client script | `https://tracking.yourdomain.com/js/{CLIENT}/{CAMPAIGN}.js` |
| Restart app pool | `Restart-WebAppPool -Name "SmartPiXL"` |
| View logs | `Get-Content C:\inetpub\SmartPiXL\logs\stdout*.log -Tail 100` |

---

## 📅 Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | 2026-01-23 | Initial release - M1 internal deployment |

---

**Next Steps After Installation:**
1. Verify data flow end-to-end
2. Add to M1 website for external testing
3. Monitor for 48-72 hours
4. Review data quality in SQL views
