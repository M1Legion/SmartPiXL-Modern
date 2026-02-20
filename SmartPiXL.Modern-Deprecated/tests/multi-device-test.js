/**
 * SmartPiXL Multi-Device/Browser Fingerprint Test
 * 
 * This script uses Playwright to emulate different browsers and devices,
 * hitting the tracking pixel to generate diverse fingerprint data.
 * 
 * Usage: node multi-device-test.js
 * 
 * Prerequisites:
 *   - SmartPiXL server running on https://localhost:6001
 *   - npm install playwright
 *   - npx playwright install
 */

const { chromium, firefox, webkit, devices } = require('playwright');

// Test configurations - different browsers and device emulations
const testConfigs = [
    // Desktop browsers
    { name: 'Chrome Desktop', browser: 'chromium', device: null },
    { name: 'Firefox Desktop', browser: 'firefox', device: null },
    { name: 'Safari Desktop', browser: 'webkit', device: null },
    
    // Mobile devices (emulated)
    { name: 'iPhone 14 Pro', browser: 'webkit', device: 'iPhone 14 Pro' },
    { name: 'iPhone 14 Pro Max', browser: 'webkit', device: 'iPhone 14 Pro Max' },
    { name: 'iPad Pro 11', browser: 'webkit', device: 'iPad Pro 11' },
    { name: 'Pixel 7', browser: 'chromium', device: 'Pixel 7' },
    { name: 'Galaxy S23', browser: 'chromium', device: 'Galaxy S23' },
    { name: 'Galaxy Tab S4', browser: 'chromium', device: 'Galaxy Tab S4' },
    
    // Desktop with custom viewports
    { name: 'Chrome 4K Monitor', browser: 'chromium', device: null, viewport: { width: 3840, height: 2160 } },
    { name: 'Chrome 1080p', browser: 'chromium', device: null, viewport: { width: 1920, height: 1080 } },
    { name: 'Chrome Ultrawide', browser: 'chromium', device: null, viewport: { width: 3440, height: 1440 } },
    
    // Different locales/timezones
    { name: 'Chrome Tokyo', browser: 'chromium', device: null, locale: 'ja-JP', timezone: 'Asia/Tokyo' },
    { name: 'Chrome London', browser: 'chromium', device: null, locale: 'en-GB', timezone: 'Europe/London' },
    { name: 'Chrome Berlin', browser: 'chromium', device: null, locale: 'de-DE', timezone: 'Europe/Berlin' },
    { name: 'Chrome Sydney', browser: 'chromium', device: null, locale: 'en-AU', timezone: 'Australia/Sydney' },
    
    // Dark mode preference
    { name: 'Chrome Dark Mode', browser: 'chromium', device: null, colorScheme: 'dark' },
    { name: 'Firefox Dark Mode', browser: 'firefox', device: null, colorScheme: 'dark' },
    
    // Reduced motion
    { name: 'Chrome Reduced Motion', browser: 'chromium', device: null, reducedMotion: 'reduce' },
];

// Target URL - M1 Company ID: 12345, Pixel ID: 1
const TEST_URL = 'https://localhost:6001/test';
const PIXEL_URL = 'https://localhost:6001/12345/1_SMART.GIF';

async function getBrowser(browserType) {
    const options = {
        // Ignore SSL errors for localhost
        ignoreHTTPSErrors: true,
    };
    
    switch (browserType) {
        case 'firefox':
            return await firefox.launch(options);
        case 'webkit':
            return await webkit.launch(options);
        default:
            return await chromium.launch(options);
    }
}

async function runTest(config, index) {
    const { name, browser: browserType, device, viewport, locale, timezone, colorScheme, reducedMotion } = config;
    
    console.log(`\n[${index + 1}/${testConfigs.length}] Testing: ${name}`);
    console.log('─'.repeat(50));
    
    let browser;
    try {
        browser = await getBrowser(browserType);
        
        // Build context options
        const contextOptions = {
            ignoreHTTPSErrors: true,
        };
        
        // Apply device emulation
        if (device && devices[device]) {
            Object.assign(contextOptions, devices[device]);
        }
        
        // Apply custom viewport
        if (viewport) {
            contextOptions.viewport = viewport;
        }
        
        // Apply locale/timezone
        if (locale) contextOptions.locale = locale;
        if (timezone) contextOptions.timezoneId = timezone;
        
        // Apply color scheme
        if (colorScheme) contextOptions.colorScheme = colorScheme;
        
        // Apply reduced motion
        if (reducedMotion) contextOptions.reducedMotion = reducedMotion;
        
        const context = await browser.newContext(contextOptions);
        const page = await context.newPage();
        
        // Navigate to test page
        await page.goto(TEST_URL, { waitUntil: 'networkidle', timeout: 30000 });
        
        // Wait a bit for async data collection
        await page.waitForTimeout(500);
        
        // Get some data from the page for verification
        const fingerprints = await page.evaluate(() => {
            return {
                viewport: `${window.innerWidth}x${window.innerHeight}`,
                screen: `${screen.width}x${screen.height}`,
                pixelRatio: window.devicePixelRatio,
                platform: navigator.platform,
                language: navigator.language,
                darkMode: window.matchMedia('(prefers-color-scheme: dark)').matches,
                reducedMotion: window.matchMedia('(prefers-reduced-motion: reduce)').matches,
                touchPoints: navigator.maxTouchPoints,
                timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
            };
        });
        
        console.log(`   Browser: ${browserType}`);
        console.log(`   Device: ${device || 'Desktop'}`);
        console.log(`   Viewport: ${fingerprints.viewport}`);
        console.log(`   Screen: ${fingerprints.screen}`);
        console.log(`   Pixel Ratio: ${fingerprints.pixelRatio}`);
        console.log(`   Platform: ${fingerprints.platform}`);
        console.log(`   Language: ${fingerprints.language}`);
        console.log(`   Timezone: ${fingerprints.timezone}`);
        console.log(`   Touch Points: ${fingerprints.touchPoints}`);
        console.log(`   Dark Mode: ${fingerprints.darkMode}`);
        console.log(`   ✓ Pixel fired successfully`);
        
        await context.close();
        return { success: true, name, fingerprints };
        
    } catch (error) {
        console.error(`   ✗ Error: ${error.message}`);
        return { success: false, name, error: error.message };
        
    } finally {
        if (browser) {
            await browser.close();
        }
    }
}

async function main() {
    console.log('╔════════════════════════════════════════════════════════════╗');
    console.log('║  SmartPiXL Multi-Device Fingerprint Test                   ║');
    console.log('║  Company ID: 12345 (M1 Data & Analytics)                   ║');
    console.log('║  Pixel ID: 1                                               ║');
    console.log('╚════════════════════════════════════════════════════════════╝');
    console.log(`\nTarget: ${TEST_URL}`);
    console.log(`Configurations to test: ${testConfigs.length}`);
    console.log('\nStarting tests...\n');
    
    const results = [];
    
    for (let i = 0; i < testConfigs.length; i++) {
        const result = await runTest(testConfigs[i], i);
        results.push(result);
        
        // Small delay between tests
        await new Promise(r => setTimeout(r, 500));
    }
    
    // Summary
    console.log('\n');
    console.log('╔════════════════════════════════════════════════════════════╗');
    console.log('║  TEST SUMMARY                                              ║');
    console.log('╚════════════════════════════════════════════════════════════╝');
    
    const successful = results.filter(r => r.success);
    const failed = results.filter(r => !r.success);
    
    console.log(`\n   ✓ Successful: ${successful.length}`);
    console.log(`   ✗ Failed: ${failed.length}`);
    console.log(`   Total: ${results.length}`);
    
    if (failed.length > 0) {
        console.log('\n   Failed tests:');
        failed.forEach(f => console.log(`     - ${f.name}: ${f.error}`));
    }
    
    console.log('\n   Check your database for the new fingerprint records!');
    console.log('   SQL: SELECT TOP 20 * FROM vw_PiXL_Parsed ORDER BY [Timestamp] DESC');
    console.log('');
}

main().catch(console.error);
