# SmartPiXL Production Deployment Guide

## Quick Start (Windows Service)

The simplest production deployment: self-contained Windows Service with HTTPS.

### Prerequisites
- Windows Server 2016+ with admin access
- SQL Server accessible from the server
- A valid SSL certificate (PFX format)

---

## Step 1: Build Self-Contained Package

On your dev machine:

```powershell
cd C:\Users\Brian\source\repos\SmartPixl\TrackingPixel.Modern

# Build self-contained for Windows x64
dotnet publish -c Release -r win-x64 --self-contained -o .\publish
```

This creates `.\publish\` with everything needed - no .NET install required on server.

---

## Step 2: Configure for Production

Edit `publish\appsettings.json`:

```json
{
  "TrackingSettings": {
    "ConnectionString": "Server=YOUR_SQL_SERVER;Database=SmartPixl;Integrated Security=True;TrustServerCertificate=True",
    "QueueCapacity": 10000,
    "BatchSize": 100,
    "BatchTimeoutMs": 500,
    "BulkCopyTimeoutSeconds": 30,
    "ShutdownTimeoutSeconds": 10
  },
  "TrackingLogSettings": {
    "LogDirectory": "C:\\SmartPiXL\\Logs",
    "MinimumLevel": "Info",
    "WriteToConsole": false
  }
}
```

---

## Step 3: Install SSL Certificate

### Option A: Use Existing PFX
Copy your `.pfx` file to the server and note the path + password.

### Option B: Create Self-Signed (Testing Only)
```powershell
$cert = New-SelfSignedCertificate -DnsName "pixl.yourcompany.com" -CertStoreLocation "Cert:\LocalMachine\My" -NotAfter (Get-Date).AddYears(5)
$pwd = ConvertTo-SecureString -String "YourPassword123" -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath "C:\SmartPiXL\pixl.pfx" -Password $pwd
```

### Option C: Let's Encrypt (Free, Trusted)
Use [win-acme](https://www.win-acme.com/) for automated Let's Encrypt certs.

---

## Step 4: Configure HTTPS in appsettings.json

Add Kestrel section to `appsettings.json`:

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://*:80"
      },
      "Https": {
        "Url": "https://*:443",
        "Certificate": {
          "Path": "C:\\SmartPiXL\\pixl.pfx",
          "Password": "YourCertPassword"
        }
      }
    }
  },
  "TrackingSettings": { ... }
}
```

**Note:** Update `Program.cs` to remove hardcoded ports and use config instead (see below).

---

## Step 5: Install as Windows Service

On the production server (as Administrator):

```powershell
# Create installation directory
New-Item -ItemType Directory -Path "C:\SmartPiXL" -Force

# Copy published files
Copy-Item -Path ".\publish\*" -Destination "C:\SmartPiXL" -Recurse

# Create the Windows Service
sc.exe create SmartPiXL binPath="C:\SmartPiXL\TrackingPixel.exe" start=auto displayname="SmartPiXL Tracking Server"

# Set description
sc.exe description SmartPiXL "SmartPiXL Fingerprinting Tracking Pixel Server"

# Configure recovery (restart on failure)
sc.exe failure SmartPiXL reset=86400 actions=restart/60000/restart/60000/restart/60000

# Start the service
sc.exe start SmartPiXL
```

---

## Step 6: Firewall Rules

```powershell
# Allow HTTP (optional, for redirect to HTTPS)
New-NetFirewallRule -DisplayName "SmartPiXL HTTP" -Direction Inbound -Protocol TCP -LocalPort 80 -Action Allow

# Allow HTTPS
New-NetFirewallRule -DisplayName "SmartPiXL HTTPS" -Direction Inbound -Protocol TCP -LocalPort 443 -Action Allow
```

---

## Step 7: Verify Installation

```powershell
# Check service status
Get-Service SmartPiXL

# Test endpoints
Invoke-WebRequest -Uri "https://pixl.yourcompany.com/health" -UseBasicParsing
```

---

## Management Commands

```powershell
# Stop service
sc.exe stop SmartPiXL

# Start service
sc.exe start SmartPiXL

# Restart service
sc.exe stop SmartPiXL; Start-Sleep 2; sc.exe start SmartPiXL

# View logs (Event Viewer)
Get-EventLog -LogName Application -Source SmartPiXL -Newest 20

# View custom logs
Get-Content "C:\SmartPiXL\Logs\*.log" -Tail 50

# Remove service (for reinstall)
sc.exe stop SmartPiXL
sc.exe delete SmartPiXL
```

---

## Sample Pixel Script for Client

Give this to your client to add to their website:

```html
<!-- SmartPiXL Tracking - Company ID: 12345, PiXL ID: 1 -->
<script src="https://pixl.yourcompany.com/js/12345/1.js" async></script>
```

Or the direct image tag (no JavaScript fingerprinting):

```html
<img src="https://pixl.yourcompany.com/12345/1_SMART.GIF" width="1" height="1" style="display:none" alt="">
```

---

## Troubleshooting

### Service Won't Start
```powershell
# Check Windows Event Log
Get-EventLog -LogName Application -Newest 20 | Where-Object { $_.Source -eq ".NET Runtime" -or $_.Message -like "*SmartPiXL*" }
```

### Connection Issues
- Verify SQL Server is accessible: `Test-NetConnection YOUR_SQL_SERVER -Port 1433`
- Check firewall allows inbound 80/443
- Verify certificate is valid: `certutil -verify C:\SmartPiXL\pixl.pfx`

### Performance Tuning
For high-volume production, edit `appsettings.json`:
```json
{
  "TrackingSettings": {
    "QueueCapacity": 50000,
    "BatchSize": 500
  }
}
```

---

## Alternative: IIS Hosting

If you prefer IIS (existing infrastructure):

1. Install ASP.NET Core Hosting Bundle for .NET 10
2. Create IIS Site pointing to publish folder
3. Use web.config for process model settings

But honestly, the Windows Service approach is simpler and has less moving parts.

---

## Updating the Application

```powershell
# 1. Build new version
dotnet publish -c Release -r win-x64 --self-contained -o .\publish

# 2. Stop service
sc.exe stop SmartPiXL

# 3. Backup current
Copy-Item "C:\SmartPiXL" "C:\SmartPiXL.backup" -Recurse

# 4. Copy new files (preserve appsettings.json)
Copy-Item ".\publish\*" "C:\SmartPiXL" -Recurse -Exclude "appsettings.json"

# 5. Start service
sc.exe start SmartPiXL
```
