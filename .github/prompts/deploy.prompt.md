---
description: 'Full IIS deployment checklist — publish, verify web.config + appsettings.json, restart, validate'
agent: 'smartpixl-ops'
tools: ['execute', 'read']
---

# Deploy to IIS

Run the full deployment checklist for the SmartPiXL IIS production instance.

## Steps

1. Stop the IIS app pool `Smartpixl.info`
2. Publish from source: `dotnet publish -c Release -o "C:\inetpub\Smartpixl.info"`
3. **CRITICAL**: Verify `web.config` was not clobbered by publish — it MUST contain `AspNetCoreModuleV2`, `hostingModel="inprocess"`, and `requestLimits maxQueryString="16384"`
4. **CRITICAL**: Verify `appsettings.json` has production values — Kestrel ports MUST be 6000/6001 (NOT 7000/7001), connection string MUST point to `localhost\SQL2025` database `SmartPiXL`
5. Start the app pool
6. Send a test pixel hit to `http://192.168.88.176/DEMO/deploy-test_SMART.GIF?verify=1`
7. Wait 3 seconds, then check the application log for success
8. Report the last 10 log lines

See [full deployment reference](.github/copilot-instructions.md) for expected file contents.

**If web.config or appsettings.json are wrong, fix them BEFORE starting the app pool.**
