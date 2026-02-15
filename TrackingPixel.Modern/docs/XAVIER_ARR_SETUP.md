# Xavier — ARR Reverse Proxy Setup Guide

**Purpose:** Forward a copy of every legacy pixel hit that arrives on Xavier to SmartPiXL, so SmartPiXL receives the exact same HTTP request the browser sent — all headers, full URL, query string, everything.

**Last updated:** February 15, 2026

---

## Table of Contents

1. [What This Does (Plain English)](#1-what-this-does-plain-english)
2. [What You Need Before Starting](#2-what-you-need-before-starting)
3. [Step 1: Install ARR and URL Rewrite on Xavier](#step-1-install-arr-and-url-rewrite-on-xavier)
4. [Step 2: Enable ARR as a Proxy](#step-2-enable-arr-as-a-proxy)
5. [Step 3: Create a Server Farm](#step-3-create-a-server-farm)
6. [Step 4: Create the URL Rewrite Rule](#step-4-create-the-url-rewrite-rule)
7. [Step 5: Configure X-Forwarded-For](#step-5-configure-x-forwarded-for)
8. [Step 6: Test It](#step-6-test-it)
9. [Step 7: Verify on SmartPiXL](#step-7-verify-on-smartpixl)
10. [Troubleshooting](#troubleshooting)
11. [How to Turn It Off](#how-to-turn-it-off)
12. [What Happens When We Cut Over](#what-happens-when-we-cut-over)

---

## 1. What This Does (Plain English)

Right now, when a browser loads a client's website, the legacy pixel tag causes the browser to send an HTTP request to Xavier:

```
Browser  ──GET /12506/00106_SMART.GIF──►  Xavier (192.168.88.35)
                                            │
                                            ▼
                                        Default.aspx.cs runs
                                        Writes to ASP_PiXL_Staging
                                        Returns 1×1 transparent GIF
```

After this setup, Xavier will **also** forward a copy of that request to SmartPiXL. Xavier still handles the request as normal — the browser doesn't know or care that a copy went somewhere else:

```
Browser  ──GET /12506/00106_SMART.GIF──►  Xavier (192.168.88.35)
                                            │
                                            ├──► Default.aspx.cs runs (unchanged)
                                            │    Returns 1×1 GIF to browser (unchanged)
                                            │
                                            └──► ARR forwards copy to SmartPiXL (192.168.88.176)
                                                 SmartPiXL processes the hit
                                                 SmartPiXL's response is discarded
```

**The key:** ARR forwards the **real browser request** — every header the browser sent (Accept-Language, Client Hints, DNT, cookies, User-Agent, Referer, all of them). It's not a reconstruction. It's the actual HTTP request, re-sent to a different server. The only thing ARR adds is an `X-Forwarded-For` header containing the browser's real IP address, because from SmartPiXL's perspective the connection is coming from Xavier's IP, not the browser's.

SmartPiXL already knows to look at `X-Forwarded-For` first when determining the client's IP. So it works out of the box.

---

## 2. What You Need Before Starting

- **Remote Desktop access** to Xavier (`192.168.88.35`)
- **Administrator account** on Xavier (you need admin to install IIS modules)
- **IIS Manager** — already installed (Xavier runs IIS)
- **Internet access from Xavier** — needed to download the ARR installer (one-time)
- **SmartPiXL running** on `192.168.88.176` — either the IIS production instance (port 80/443) or the dev instance (port 7000). For testing, use the dev instance first.

### Network Connectivity Check

Before anything else, verify Xavier can reach SmartPiXL. RDP into Xavier and open PowerShell:

```powershell
# Test basic TCP connectivity to SmartPiXL IIS (port 80)
Test-NetConnection -ComputerName 192.168.88.176 -Port 80

# You should see:
#   TcpTestSucceeded : True

# Test that SmartPiXL responds to a pixel request
Invoke-WebRequest -Uri "http://192.168.88.176/TEST/1_SMART.GIF" -UseBasicParsing | Select-Object StatusCode, ContentType

# You should see:
#   StatusCode  ContentType
#   ----------  -----------
#   200         image/gif
```

If `TcpTestSucceeded` is `False`, there's a firewall blocking traffic between the two machines. Fix that first — no point continuing until packets can flow.

---

## Step 1: Install ARR and URL Rewrite on Xavier

ARR (Application Request Routing) is an official Microsoft IIS extension. URL Rewrite is its companion — ARR handles the proxying, URL Rewrite tells it which requests to proxy.

### Option A: Web Platform Installer (Easiest)

1. RDP into Xavier
2. Open **IIS Manager** (type `inetmgr` in the Start menu search)
3. In the right-hand **Actions** panel, look for **"Get New Web Platform Components"** or **"Microsoft Web Platform Installer"**
   - If you see it: click it, search for **"Application Request Routing 3.0"**, click **Add**, then **Install**
   - It will automatically install URL Rewrite as a dependency
4. If Web Platform Installer isn't there, use Option B

### Option B: Direct Download

1. RDP into Xavier
2. Open a browser (Edge, whatever)
3. Download **URL Rewrite 2.1** (must install this FIRST):
   - Go to: `https://www.iis.net/downloads/microsoft/url-rewrite`
   - Click the **x64** installer link
   - Run the `.msi` — click Next through everything, accept defaults
4. Download **Application Request Routing 3.0**:
   - Go to: `https://www.iis.net/downloads/microsoft/application-request-routing`
   - Click the **x64** installer link
   - Run the `.msi` — click Next through everything, accept defaults
5. **Restart IIS Manager** — close it if it's open and reopen it (`inetmgr`). The new modules won't show up until you restart IIS Manager.

### Option C: PowerShell (If You Prefer Command Line)

```powershell
# Run this in an elevated (Administrator) PowerShell on Xavier

# Download and install URL Rewrite 2.1
$urlRewrite = "$env:TEMP\rewrite_amd64_en-US.msi"
Invoke-WebRequest -Uri "https://download.microsoft.com/download/1/2/8/128E2E22-C1B9-44A4-BE2A-5859ED1D4592/rewrite_amd64_en-US.msi" -OutFile $urlRewrite
Start-Process msiexec.exe -ArgumentList "/i `"$urlRewrite`" /quiet /norestart" -Wait

# Download and install ARR 3.0
$arr = "$env:TEMP\ARRv3_0.exe"
Invoke-WebRequest -Uri "https://download.microsoft.com/download/E/9/8/E9849D6A-020E-47E4-9FD0-A023E99B54EB/requestRouter_amd64.msi" -OutFile $arr
Start-Process msiexec.exe -ArgumentList "/i `"$arr`" /quiet /norestart" -Wait

# Verify installation
Get-WebGlobalModule | Where-Object Name -like "*arr*"
Get-WebGlobalModule | Where-Object Name -like "*rewrite*"

# Both should return at least one result each
```

### Verify Installation

After installing, open IIS Manager (`inetmgr`). Click on the **server name** (top node in the left tree, not a specific site). In the center panel you should now see:

- **Application Request Routing Cache** (in the IIS section)
- **URL Rewrite** (in the IIS section)

If you don't see them, close and reopen IIS Manager. If you still don't see them, the install failed — re-run Option B.

> **⚠️ Installing ARR and URL Rewrite does NOT change any existing behavior.** Nothing is proxied or rewritten until you explicitly configure rules. Xavier continues to work exactly as before after installation.

---

## Step 2: Enable ARR as a Proxy

By default, ARR is installed but the proxy feature is **disabled**. You have to turn it on.

### Via IIS Manager (GUI)

1. In IIS Manager, click the **server name** (top node in left tree)
2. Double-click **"Application Request Routing Cache"** in the center panel
3. In the right-hand **Actions** panel, click **"Server Proxy Settings..."**
4. Check the box: **☑ Enable proxy**
5. The other settings can stay at defaults. Note these defaults for reference:
   - `Time-out (seconds)`: 30 — this is how long ARR waits for SmartPiXL to respond before giving up
   - `HTTP version`: Pass through — ARR will use whatever HTTP version the client used
   - `Reverse rewrite host in response headers`: checked — leave it checked
6. You'll see a field for **"Preserve client IP in the following header"** — set this to: `X-Forwarded-For`
   - This tells ARR to add the real client IP to this header automatically
7. Click **Apply** in the right-hand Actions panel

### Via PowerShell

```powershell
# Run on Xavier as Administrator

# Enable the ARR proxy
Set-WebConfigurationProperty -PSPath "MACHINE/WEBROOT/APPHOST" `
    -Filter "system.webServer/proxy" `
    -Name "enabled" -Value "True"

# Verify
Get-WebConfigurationProperty -PSPath "MACHINE/WEBROOT/APPHOST" `
    -Filter "system.webServer/proxy" `
    -Name "enabled"
# Should return: True
```

> **⚠️ Enabling the proxy alone does NOT change any behavior.** No requests are proxied until you create a URL Rewrite rule that tells ARR where to send them. Xavier still works exactly as before.

---

## Step 3: Create a Server Farm

A "Server Farm" is ARR's term for "a backend server to forward requests to." We need one entry: SmartPiXL.

### Via IIS Manager (GUI)

1. In IIS Manager, click the **server name** (top node)
2. In the left tree, you should now see **"Server Farms"** — right-click it and select **"Create Server Farm..."**
3. Name it: `SmartPiXL`
4. Click **Next**
5. In **"Add Server"**, enter:
   - **Server address:** `192.168.88.176`
   - Leave the port at `80` (this targets the IIS production instance on SmartPiXL)
   - If you want to test against the dev instance first, use port `7000` instead — but you'll need to change it to `80` later
6. Click **Add** to add the server to the farm
7. Click **Finish**
8. **A dialog will appear** asking: *"Do you want to create a URL Rewrite rule for this server farm?"* — Click **NO**. We're going to create a more specific rule in the next step. If you click Yes, it creates a catch-all rule that forwards EVERYTHING to SmartPiXL, which would break Xavier.

### Via PowerShell

```powershell
# Create the server farm configuration in applicationHost.config
# Run on Xavier as Administrator

$ahPath = "$env:SystemRoot\System32\inetsrv\config\applicationHost.config"

# Add the webFarms section if it doesn't exist, then add our farm
# Using appcmd for reliability:
& "$env:SystemRoot\System32\inetsrv\appcmd.exe" set config `
    -section:webFarms `
    /+"[name='SmartPiXL']" `
    /commit:apphost

& "$env:SystemRoot\System32\inetsrv\appcmd.exe" set config `
    -section:webFarms `
    /+"[name='SmartPiXL'].[address='192.168.88.176']" `
    /commit:apphost

# Verify
& "$env:SystemRoot\System32\inetsrv\appcmd.exe" list config `
    -section:webFarms
```

---

## Step 4: Create the URL Rewrite Rule

This is the rule that says: "When a request comes in that matches `*_SMART.GIF`, forward a copy to the SmartPiXL server farm."

### Important Concept: We Want a COPY, Not a Redirect

We do NOT want to redirect the browser to SmartPiXL. We want Xavier to handle the request normally (Default.aspx.cs runs, GIF is returned to the browser) AND ALSO forward a copy to SmartPiXL in the background.

ARR doesn't natively do "forward a copy and also process locally." It does one or the other. So we have two approaches:

#### Approach A: Outbound Rule + Code Forward (Hybrid)

Actually, the cleanest way to achieve "process locally AND forward a copy" is **not** with ARR alone. ARR replaces the local handler — it's designed for "send this request somewhere else instead." 

The better architecture: use **URL Rewrite only** (not ARR's server farm) to create an **outbound rule** that... actually, this gets complicated. Let me give you the approach that actually works cleanly.

#### The Right Approach: IIS URL Rewrite + Double Request Pattern

The cleanest solution that preserves Xavier's normal processing AND sends a full copy to SmartPiXL:

**The trick:** We use a URL Rewrite **Outbound Rule** — no wait, that's for responses. Let me be straight with you: IIS doesn't have a native "tee" (duplicate and send to two places) feature. ARR is a reverse proxy — it forwards requests to a backend INSTEAD of processing them locally.

Here's what actually works:

### The Actual Solution: IIS Rewrite Rule + ARR for a Specific Sub-Path

We create a **second virtual URL** that ARR proxies to SmartPiXL, and we use Xavier's `Default.aspx.cs` to fire a quick local request to that URL after processing. Since it's a local IIS request (localhost to localhost), it has near-zero overhead, and ARR then forwards it to SmartPiXL with the original headers we inject.

**Wait — that's the code-level forward again with extra steps.**

Let me level with you. Here's the honest truth:

---

### The Honest Assessment

IIS ARR is designed to do one thing: route a request to backend server A **instead of** processing it locally. It's a reverse proxy, not a request duplicator. There is no built-in IIS feature to say "process this request normally AND also send a copy somewhere else."

Your options for getting real browser traffic to SmartPiXL are:

#### Option 1: ARR Full Proxy (Recommended for Testing)

Point ARR at SmartPiXL for `*_SMART.GIF` requests. Xavier stops processing those requests — SmartPiXL handles them entirely. Xavier's `Default.aspx.cs` does NOT run for proxied requests.

**Pros:**
- SmartPiXL gets the REAL browser request with ALL headers
- This is exactly how SmartPiXL will work in production after cutover
- True integration test

**Cons:**
- Xavier stops recording those hits to `ASP_PiXL_Staging`
- If SmartPiXL is down, pixel hits are lost (no fallback)

**Mitigation for the cons:** Run this on a subset of traffic first. Create the rule to match only specific CompanyIDs (e.g., your test company 12800) so 99% of traffic still goes through Xavier normally.

#### Option 2: DNS Split / Host Header Routing

Add a second binding to Xavier's IIS site with a different hostname (e.g., `test.smartpixl.info`). Route that hostname via ARR to SmartPiXL. Deploy test pixels using the test hostname. Real production pixels continue hitting Xavier's normal hostname.

**Pros:**
- Zero impact on production traffic
- SmartPiXL gets real browser requests
- Clean separation

**Cons:**
- Need a DNS entry for the test hostname (or use a hosts file on test machines)
- Test pixels are not the same as production pixels (different hostname)

#### Option 3: Code-Level Forward (Backup)

The `Default.aspx.cs` modification from LEGACY_SUPPORT.md. Forwards IP + UA only. Inferior but zero-risk to Xavier.

---

### Recommendation: Option 1 with a CompanyID Filter

This gives you real browser traffic on SmartPiXL with zero header loss. Limit it to test company(s) so production is unaffected.

Here's how to set it up:

### Create the Filtered ARR Rule

#### Via IIS Manager (GUI)

1. In IIS Manager, click on the **website** that handles pixel traffic (not the server node — the specific site)
2. Double-click **"URL Rewrite"** in the center panel
3. In the right-hand **Actions** panel, click **"Add Rule(s)..."**
4. Select **"Blank rule"** under Inbound Rules, click **OK**
5. Fill in:

| Field | Value | Explanation |
|-------|-------|-------------|
| **Name** | `SmartPiXL Forward - Test` | Just a label, call it whatever |
| **Match URL - Requested URL** | `Matches the Pattern` | (default) |
| **Match URL - Using** | `Regular Expressions` | We need regex for the CompanyID filter |
| **Match URL - Pattern** | `^(12800/.+_SMART\.GIF)$` | Only matches CompanyID 12800. Change this number to your test company. To match multiple companies: `^(12800\|12801)/.+_SMART\.GIF$` |
| **Conditions** | (none needed) | |
| **Action - Action type** | `Route to Server Farm` | This is the ARR proxy action |
| **Action - Server farm** | `SmartPiXL` | The farm we created in Step 3 |
| **Action - Path** | `/{R:1}` | Forwards the matched URL path as-is |
| **Stop processing of subsequent rules** | ☑ Checked | |

6. Click **Apply** in the right-hand Actions panel

#### Via PowerShell

```powershell
# Run on Xavier as Administrator
# This adds the rule to the site's web.config

# First, find the site name. List all sites:
Get-Website | Select-Object Name, PhysicalPath, Bindings

# Replace 'YourSiteName' below with the actual site name from the output above.
$siteName = "YourSiteName"

# Add the inbound rewrite rule
Add-WebConfigurationProperty `
    -PSPath "IIS:\Sites\$siteName" `
    -Filter "system.webServer/rewrite/rules" `
    -Name "." `
    -Value @{
        name = "SmartPiXL Forward - Test"
        stopProcessing = "true"
    }

# Set the match pattern (CompanyID 12800 only)
Set-WebConfigurationProperty `
    -PSPath "IIS:\Sites\$siteName" `
    -Filter "system.webServer/rewrite/rules/rule[@name='SmartPiXL Forward - Test']/match" `
    -Name "url" -Value "^(12800/.+_SMART\.GIF)$"

# Set the action to route to SmartPiXL server farm
Set-WebConfigurationProperty `
    -PSPath "IIS:\Sites\$siteName" `
    -Filter "system.webServer/rewrite/rules/rule[@name='SmartPiXL Forward - Test']/action" `
    -Name "type" -Value "Rewrite"

Set-WebConfigurationProperty `
    -PSPath "IIS:\Sites\$siteName" `
    -Filter "system.webServer/rewrite/rules/rule[@name='SmartPiXL Forward - Test']/action" `
    -Name "url" -Value "http://SmartPiXL/{R:1}"
```

#### What the web.config Rule Looks Like

After creating the rule (by either method), Xavier's site `web.config` will contain something like this in the `<system.webServer>` section:

```xml
<rewrite>
    <rules>
        <rule name="SmartPiXL Forward - Test" stopProcessing="true">
            <match url="^(12800/.+_SMART\.GIF)$" />
            <action type="Rewrite" url="http://SmartPiXL/{R:1}" />
        </rule>
    </rules>
</rewrite>
```

**What this means in English:** "If someone requests a URL that starts with `12800/` and ends with `_SMART.GIF`, send that request to the SmartPiXL server farm instead of processing it here on Xavier. Keep the same URL path."

---

## Step 5: Configure X-Forwarded-For

This should already be set from Step 2, but let's verify. When ARR forwards a request, the connection IP that SmartPiXL sees is **Xavier's IP** (192.168.88.35), not the browser's real IP. The `X-Forwarded-For` header tells SmartPiXL the real client IP.

### Verify

In IIS Manager:
1. Click the **server name** (top node)
2. Double-click **"Application Request Routing Cache"**
3. Click **"Server Proxy Settings..."** in the Actions panel
4. Confirm:
   - **☑ Enable proxy** is checked
   - **☑ Include TCP port in X_FORWARDED_FOR header** — leave this UNCHECKED (we don't need the port)
   - The setting **"Preserve client IP in the following header"** should show `X-Forwarded-For`

SmartPiXL's `TrackingCaptureService` reads headers in this priority order:
1. `CF-Connecting-IP` (Cloudflare — not relevant here)
2. `True-Client-IP` (CDN — not relevant here)
3. `X-Real-IP`
4. **`X-Forwarded-For`** ← ARR sets this one
5. `Connection.RemoteIpAddress` (fallback)

So ARR's `X-Forwarded-For` is picked up at priority #4. This is correct.

---

## Step 6: Test It

### From Xavier Itself

Open PowerShell on Xavier and simulate a browser request to Xavier's own IIS:

```powershell
# Hit a test URL that matches our rule (CompanyID 12800)
# Replace 'xavier-hostname' with whatever hostname/IP Xavier's IIS site binds to
$response = Invoke-WebRequest -Uri "http://localhost/12800/test_SMART.GIF" `
    -UseBasicParsing `
    -Headers @{
        "User-Agent" = "ARR-Setup-Test/1.0"
        "Accept-Language" = "en-US,en;q=0.9"
        "Referer" = "https://example.com/test-page"
    }

$response.StatusCode
# Should be 200

$response.Headers["Content-Type"]
# Should be "image/gif" (SmartPiXL returns a 1×1 transparent GIF)
```

**If you get a 502 Bad Gateway:** SmartPiXL is not reachable from Xavier. Go back to the connectivity check in Section 2.

**If you get a 404:** The URL Rewrite rule isn't matching. Double-check the regex pattern.

**If you get a 200 but it's Xavier's response (not SmartPiXL's):** The rule isn't firing. Make sure:
- The rule is on the correct IIS site (not the server level)
- The rule is enabled (not disabled)
- The URL pattern matches (try removing the CompanyID filter temporarily to test with `^(.+_SMART\.GIF)$`)

### From Your Browser (On Your Dev Machine)

Open your browser and navigate to:
```
http://xavier-hostname/12800/test_SMART.GIF
```

You'll see a blank page (or a tiny invisible image). That's correct — it's the 1×1 transparent GIF.

---

## Step 7: Verify on SmartPiXL

After sending the test request, check that SmartPiXL recorded it.

### Check the Log File

```powershell
# Run on SmartPiXL (192.168.88.176)
# Check today's log for the test hit
$logPath = "C:\inetpub\Smartpixl.info\Log\$(Get-Date -Format 'yyyy_MM_dd').log"
Get-Content $logPath -Tail 20

# For the dev instance:
$logPath = "C:\Users\Administrator\source\repos\SmartPiXL\TrackingPixel.Modern\Log\$(Get-Date -Format 'yyyy_MM_dd').log"
Get-Content $logPath -Tail 20
```

You should see a log entry showing:
- The request path (`/12800/test_SMART.GIF`)
- The IP address (should be your real IP from the `X-Forwarded-For` header, NOT Xavier's 192.168.88.35)
- The User-Agent (should match what the browser sent)

### Check the Database

```powershell
# Run on SmartPiXL
sqlcmd -S "localhost\SQL2025" -d SmartPiXL -Q "
    SELECT TOP 5 
        Id, CompanyID, PiXLID, IPAddress, UserAgent, 
        LEFT(HeadersJson, 200) AS HeadersPreview,
        ReceivedAt
    FROM PiXL.Test 
    ORDER BY Id DESC
" -E -W -s ","
```

You should see a row with:
- `CompanyID` = `12800`
- `PiXLID` = `test`
- `IPAddress` = your real client IP (not 192.168.88.35)
- `UserAgent` = the real browser's User-Agent string
- `HeadersJson` should contain `Accept-Language`, `Referer`, and other real browser headers

**If `IPAddress` shows `192.168.88.35`:** The X-Forwarded-For header isn't being set or isn't being read. Check Step 5.

**If `HeadersJson` is empty or minimal:** Something is stripping headers. This shouldn't happen with ARR — it forwards the original request headers by default.

---

## Troubleshooting

### "I installed ARR but don't see it in IIS Manager"

Close IIS Manager completely and reopen it. The modules register on IIS Manager startup.

### "502 Bad Gateway"

This means ARR tried to forward the request but couldn't reach SmartPiXL.

```powershell
# From Xavier, test connectivity:
Test-NetConnection -ComputerName 192.168.88.176 -Port 80
```

If that fails: Windows Firewall on SmartPiXL is blocking port 80 from Xavier. Fix:

```powershell
# Run on SmartPiXL as Administrator
New-NetFirewallRule -DisplayName "Allow Xavier ARR" `
    -Direction Inbound -Protocol TCP -LocalPort 80 `
    -RemoteAddress 192.168.88.35 `
    -Action Allow
```

### "500 Internal Server Error"

Check Xavier's Failed Request Tracing logs, or the Windows Event Log:

```powershell
# On Xavier
Get-EventLog -LogName Application -Newest 20 | Where-Object { $_.Source -like "*IIS*" -or $_.Source -like "*ASP*" } | Format-List TimeGenerated, Message
```

### "The rule doesn't seem to fire — Xavier processes the request normally"

1. Verify the rule exists on the **site level**, not just the server level
2. Open the site's `web.config` and look for the `<rewrite><rules>` section
3. Test the regex pattern: go to `https://regex101.com/`, paste `^(12800/.+_SMART\.GIF)$` as the pattern, and paste `12800/test_SMART.GIF` as the test string. It should match.
4. Check if `stopProcessing="true"` is set — if not, a later rule might override it

### "I want to forward ALL traffic, not just CompanyID 12800"

Change the URL match pattern from `^(12800/.+_SMART\.GIF)$` to `^(.+_SMART\.GIF)$`. This matches any CompanyID.

**⚠️ WARNING:** This means Xavier stops processing ALL pixel hits locally. `Default.aspx.cs` will NOT run for ANY pixel request. Only do this when you're confident SmartPiXL is working correctly and you're ready for cutover.

### "ARR is causing timeouts / slow responses"

The default ARR timeout is 30 seconds. SmartPiXL should respond in under 100ms. If you're seeing timeouts:

```powershell
# From Xavier, time a direct request to SmartPiXL:
Measure-Command { Invoke-WebRequest -Uri "http://192.168.88.176/12800/test_SMART.GIF" -UseBasicParsing }
```

If that's slow, the problem is SmartPiXL performance, not ARR.

---

## How to Turn It Off

### Disable the Rule (Keeps Config, Stops Forwarding)

In IIS Manager:
1. Click the site → double-click **URL Rewrite**
2. Select the rule **"SmartPiXL Forward - Test"**
3. In the right Actions panel, click **"Disable Rule"**
4. Xavier immediately resumes processing those requests locally

### Remove the Rule Entirely

In IIS Manager:
1. Click the site → double-click **URL Rewrite**
2. Select the rule → click **"Delete"** in Actions panel

Or via PowerShell:

```powershell
# Remove the rule from the site's web.config
$siteName = "YourSiteName"
Clear-WebConfiguration `
    -PSPath "IIS:\Sites\$siteName" `
    -Filter "system.webServer/rewrite/rules/rule[@name='SmartPiXL Forward - Test']"
```

### Disable ARR Proxy Entirely

If you want to disable the proxy feature globally (not just the rule):

1. Server node → **Application Request Routing Cache** → **Server Proxy Settings**
2. Uncheck **☑ Enable proxy**
3. Click **Apply**

---

## What Happens When We Cut Over

When SmartPiXL is ready to be the primary pixel handler:

1. **DNS change:** Point the pixel domain (e.g., `smartpixl.info`) directly at SmartPiXL's IP (`192.168.88.176`) instead of Xavier (`192.168.88.35`)
2. **Remove ARR rules on Xavier** — they're no longer needed since traffic goes directly to SmartPiXL
3. **SmartPiXL sees direct browser connections** — `Connection.RemoteIpAddress` is now the real client IP. The `X-Forwarded-For` header is no longer present (no proxy). SmartPiXL's IP extraction chain handles this correctly because `RemoteIpAddress` is the last fallback.
4. **Xavier's `Default.aspx.cs` stops receiving pixel traffic** — it becomes dormant

The beauty of testing with ARR first: SmartPiXL has been processing real browser requests with real headers for weeks/months before cutover. The only thing that changes is the IP comes from `RemoteIpAddress` instead of `X-Forwarded-For`. Everything else is identical.

---

## Quick Reference Card

| What | Where | Value |
|------|-------|-------|
| Xavier IP | Server | `192.168.88.35` |
| SmartPiXL IP | Server | `192.168.88.176` |
| SmartPiXL IIS port | Production | `80` (HTTP), `443` (HTTPS) |
| SmartPiXL dev port | Dev | `7000` (HTTP), `7001` (HTTPS) |
| ARR Server Farm name | IIS config | `SmartPiXL` |
| Rewrite rule name | Site web.config | `SmartPiXL Forward - Test` |
| URL pattern (test) | Regex | `^(12800/.+_SMART\.GIF)$` |
| URL pattern (all traffic) | Regex | `^(.+_SMART\.GIF)$` |
| Client IP header | HTTP | `X-Forwarded-For` |
| SmartPiXL IP extraction priority | Code | CF-Connecting-IP → True-Client-IP → X-Real-IP → X-Forwarded-For → RemoteIpAddress |
