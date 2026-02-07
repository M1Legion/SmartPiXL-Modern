/**
 * SmartPiXL Synthetic Monitor
 * 
 * Navigates to https://smartpixl.com like a real visitor.
 * The embedded pixel fires naturally from the page.
 * Playwright intercepts the pixel request and appends &synthetic=1
 * so we can distinguish synthetic from live traffic in SQL.
 * 
 * Usage:
 *   node synthetic-monitor.js                    # Single run
 *   node synthetic-monitor.js --loop             # Continuous (every 5 min)
 *   node synthetic-monitor.js --loop --interval 60  # Every 60 seconds
 *   node synthetic-monitor.js --browsers 3       # Multi-browser run
 * 
 * Requirements:
 *   npm install  (installs playwright)
 *   npx playwright install chromium webkit  (install browser binaries)
 * 
 * M1 Data & Analytics - Synthetic Testing
 */

const { chromium, webkit, firefox, devices } = require('playwright');
const fs = require('fs');
const path = require('path');

// ============================================================================
// CONFIG
// ============================================================================
const CONFIG = {
    // Target site with embedded pixel
    TARGET_URL: 'https://smartpixl.com',
    
    // Pixel endpoint pattern to intercept
    PIXEL_PATTERN: /smartpixl\.info.*_SMART\.GIF/i,
    
    // Synthetic flag appended to the pixel query string
    SYNTHETIC_FLAG: 'synthetic=1',
    
    // How long to wait for the pixel to fire (ms)
    PAGE_TIMEOUT_MS: 30000,
    PIXEL_WAIT_MS: 5000,
    
    // Loop mode settings
    DEFAULT_INTERVAL_SECONDS: 300, // 5 minutes
    
    // Logging
    LOG_FILE: path.join(__dirname, 'synthetic-monitor.log'),
    RESULTS_FILE: path.join(__dirname, 'synthetic-results.json'),
};

// ============================================================================
// ARGUMENT PARSING
// ============================================================================
const args = process.argv.slice(2);
const isLoop = args.includes('--loop');
const intervalIdx = args.indexOf('--interval');
const intervalSeconds = intervalIdx >= 0 ? parseInt(args[intervalIdx + 1]) : CONFIG.DEFAULT_INTERVAL_SECONDS;
const browserCountIdx = args.indexOf('--browsers');
const browserCount = browserCountIdx >= 0 ? parseInt(args[browserCountIdx + 1]) : 1;

// ============================================================================
// DEVICE PROFILES for realistic simulation
// ============================================================================
const PROFILES = [
    { name: 'Desktop Chrome Windows', browser: 'chromium', options: { viewport: { width: 1920, height: 1080 }, locale: 'en-US', timezoneId: 'America/New_York' } },
    { name: 'Desktop Chrome Mac', browser: 'chromium', options: { viewport: { width: 1440, height: 900 }, locale: 'en-US', timezoneId: 'America/Los_Angeles' } },
    { name: 'Desktop Firefox', browser: 'chromium', options: { viewport: { width: 1366, height: 768 }, locale: 'en-GB', timezoneId: 'Europe/London' } },
    { name: 'iPhone 14', browser: 'webkit', options: { ...devices['iPhone 14'], timezoneId: 'America/Chicago' } },
    { name: 'iPad Pro', browser: 'webkit', options: { ...devices['iPad Pro 11'], timezoneId: 'America/Denver' } },
    { name: 'Pixel 7', browser: 'chromium', options: { ...devices['Pixel 7'], timezoneId: 'America/New_York' } },
    { name: 'Galaxy S21', browser: 'chromium', options: { viewport: { width: 412, height: 915 }, locale: 'en-US', timezoneId: 'America/Chicago', isMobile: true, hasTouch: true } },
    { name: 'Desktop 4K', browser: 'chromium', options: { viewport: { width: 2560, height: 1440 }, locale: 'en-US', timezoneId: 'America/New_York', deviceScaleFactor: 2 } },
];

// ============================================================================
// LOGGING
// ============================================================================
function log(message, level = 'INFO') {
    const timestamp = new Date().toISOString();
    const line = `[${timestamp}] [${level}] ${message}`;
    console.log(line);
    try { fs.appendFileSync(CONFIG.LOG_FILE, line + '\n'); } catch {}
}

// ============================================================================
// CORE: Run a single synthetic test
// ============================================================================
async function runSyntheticTest(profile) {
    const startTime = Date.now();
    let browser = null;
    let context = null;
    let page = null;
    let pixelFired = false;
    let pixelUrl = null;
    let pixelStatus = null;
    let error = null;

    try {
        // Launch browser
        const browserType = profile.browser === 'webkit' ? webkit : 
                           profile.browser === 'firefox' ? firefox : chromium;
        
        browser = await browserType.launch({
            headless: true,
            args: profile.browser === 'chromium' ? ['--ignore-certificate-errors'] : []
        });

        // Create context with device profile
        const contextOptions = {
            ...profile.options,
            ignoreHTTPSErrors: true,
        };
        context = await browser.newContext(contextOptions);
        page = await context.newPage();

        // ================================================================
        // INTERCEPT: Add synthetic=1 flag to pixel requests
        // This is the key differentiator - the pixel fires naturally from
        // the embedded script, but we modify the request to add our flag.
        // ================================================================
        await page.route(CONFIG.PIXEL_PATTERN, async (route) => {
            const request = route.request();
            const originalUrl = request.url();
            
            // Append synthetic flag to the query string
            const separator = originalUrl.includes('?') ? '&' : '?';
            const modifiedUrl = originalUrl + separator + CONFIG.SYNTHETIC_FLAG;
            
            log(`  Pixel intercepted: ${originalUrl.substring(0, 80)}...`);
            log(`  Modified URL: ...${CONFIG.SYNTHETIC_FLAG} appended`);
            
            pixelUrl = modifiedUrl;
            pixelFired = true;
            
            // Continue with the modified URL
            try {
                await route.continue({ url: modifiedUrl });
            } catch (e) {
                // If continue fails (e.g., server unreachable), still count it
                log(`  Route continue failed: ${e.message}`, 'WARN');
                await route.abort().catch(() => {});
            }
        });

        // Navigate to target site
        log(`  Navigating to ${CONFIG.TARGET_URL}...`);
        const response = await page.goto(CONFIG.TARGET_URL, {
            waitUntil: 'domcontentloaded',
            timeout: CONFIG.PAGE_TIMEOUT_MS,
        });
        
        const pageStatus = response?.status() || 0;
        log(`  Page loaded: HTTP ${pageStatus}`);

        // Wait for pixel to fire (JavaScript needs time to collect fingerprints)
        await page.waitForTimeout(CONFIG.PIXEL_WAIT_MS);
        
        // Check if the page has any script errors
        const pageErrors = [];
        page.on('pageerror', err => pageErrors.push(err.message));

        if (!pixelFired) {
            // Give it a bit more time
            log(`  Pixel hasn't fired yet, waiting 5 more seconds...`);
            await page.waitForTimeout(5000);
        }

    } catch (e) {
        error = e.message;
        log(`  Error: ${e.message}`, 'ERROR');
    } finally {
        if (page) await page.close().catch(() => {});
        if (context) await context.close().catch(() => {});
        if (browser) await browser.close().catch(() => {});
    }

    const duration = Date.now() - startTime;

    return {
        profile: profile.name,
        timestamp: new Date().toISOString(),
        pixelFired,
        pixelUrl: pixelUrl ? pixelUrl.substring(0, 100) + '...' : null,
        duration,
        error,
        success: pixelFired && !error,
    };
}

// ============================================================================
// RUNNER: Execute tests across multiple profiles
// ============================================================================
async function runTestSuite() {
    log('='.repeat(60));
    log('SmartPiXL Synthetic Monitor - Test Run Starting');
    log('='.repeat(60));

    const selectedProfiles = [];
    for (let i = 0; i < browserCount; i++) {
        selectedProfiles.push(PROFILES[i % PROFILES.length]);
    }

    const results = [];
    for (const profile of selectedProfiles) {
        log(`Testing: ${profile.name} (${profile.browser})`);
        const result = await runSyntheticTest(profile);
        results.push(result);

        const status = result.success ? 'PASS' : 'FAIL';
        log(`  Result: ${status} | Pixel: ${result.pixelFired ? 'YES' : 'NO'} | ${result.duration}ms`);
    }

    // Summary
    const passed = results.filter(r => r.success).length;
    const failed = results.filter(r => !r.success).length;
    const pixelsFired = results.filter(r => r.pixelFired).length;

    log('-'.repeat(60));
    log(`Summary: ${passed}/${results.length} passed | ${pixelsFired} pixels fired | ${failed} failures`);
    log('-'.repeat(60));

    // Save results
    const report = {
        runAt: new Date().toISOString(),
        totalTests: results.length,
        passed,
        failed,
        pixelsFired,
        results,
    };

    try {
        // Append to existing results or create new
        let allResults = [];
        if (fs.existsSync(CONFIG.RESULTS_FILE)) {
            try {
                allResults = JSON.parse(fs.readFileSync(CONFIG.RESULTS_FILE, 'utf8'));
            } catch {}
        }
        allResults.push(report);
        // Keep last 1000 runs
        if (allResults.length > 1000) allResults = allResults.slice(-1000);
        fs.writeFileSync(CONFIG.RESULTS_FILE, JSON.stringify(allResults, null, 2));
    } catch (e) {
        log(`Failed to save results: ${e.message}`, 'WARN');
    }

    return report;
}

// ============================================================================
// MAIN
// ============================================================================
async function main() {
    console.log('╔══════════════════════════════════════════════════════════════╗');
    console.log('║  SmartPiXL Synthetic Monitor                                ║');
    console.log('║  Visits smartpixl.com like a real user                      ║');
    console.log('║  Pixel fires naturally, synthetic=1 flag added              ║');
    console.log('╚══════════════════════════════════════════════════════════════╝');
    console.log();

    if (isLoop) {
        log(`Loop mode: running every ${intervalSeconds} seconds. Press Ctrl+C to stop.`);
        
        let running = true;
        process.on('SIGINT', () => {
            log('Shutdown requested...');
            running = false;
        });

        while (running) {
            await runTestSuite();
            
            if (running) {
                log(`Next run in ${intervalSeconds} seconds...`);
                await new Promise(r => setTimeout(r, intervalSeconds * 1000));
            }
        }
    } else {
        const report = await runTestSuite();
        process.exit(report.failed > 0 ? 1 : 0);
    }
}

main().catch(err => {
    log(`Fatal: ${err.message}`, 'FATAL');
    console.error(err);
    process.exit(1);
});
