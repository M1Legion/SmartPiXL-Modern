---
description: 'Deploy Edge to IIS or Forge to Windows Service. Pre-flight checks, publish, verify.'
agent: smartpixl-ops
tools: ['execute', 'read']
---

# Deploy

Which component?

## Edge (IIS)

```powershell
# 1. Stop IIS app pool
Import-Module WebAdministration
Stop-WebAppPool -Name "Smartpixl.info"

# 2. Publish
Push-Location "C:\Users\Administrator\source\repos\SmartPiXL\SmartPiXL"
dotnet publish -c Release -o "C:\inetpub\Smartpixl.info"
Pop-Location

# 3. CRITICAL: Verify web.config wasn't clobbered
type "C:\inetpub\Smartpixl.info\web.config"
# Must have: hostingModel="inprocess", maxQueryString="16384"

# 4. CRITICAL: Verify appsettings.json has PRODUCTION values
type "C:\inetpub\Smartpixl.info\appsettings.json"
# Must have: ports 6000/6001 (NOT 7000/7001), SQL2025 connection string

# 5. Start app pool
Start-WebAppPool -Name "Smartpixl.info"

# 6. Verify
Invoke-WebRequest -Uri "http://192.168.88.176/DEMO/deploy-test_SMART.GIF?verify=1" -UseBasicParsing | Out-Null
Start-Sleep -Seconds 3
Get-Content "C:\inetpub\Smartpixl.info\Log\$(Get-Date -Format 'yyyy_MM_dd').log" -Tail 10
```

## Forge (Windows Service)

```powershell
Stop-Service -Name "SmartPiXL-Forge" -ErrorAction SilentlyContinue
Push-Location "C:\Users\Administrator\source\repos\SmartPiXL\SmartPiXL.Forge"
dotnet publish -c Release -o "C:\Services\SmartPiXL-Forge"
Pop-Location
# First time only: sc.exe create SmartPiXL-Forge binPath= "C:\Services\SmartPiXL-Forge\SmartPiXL.Forge.exe"
Start-Service -Name "SmartPiXL-Forge"
Get-Service SmartPiXL-Forge
```

## Post-Deploy Checks

1. Check app logs for errors
2. Verify PiXL.Raw is receiving new rows
3. Check ETL watermarks are advancing
4. Check Failover/ directory — no new JSONL files accumulating
