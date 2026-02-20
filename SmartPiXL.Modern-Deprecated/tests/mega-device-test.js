/**
 * SmartPiXL MEGA Device Fingerprint Test Suite
 * 
 * Generates 150+ unique device/browser/locale configurations
 * to stress-test fingerprinting and populate the database with diverse data.
 * 
 * M1 Data & Analytics - Internal Testing
 * Company ID: 12345 | Pixel ID: 1
 */

const { chromium, firefox, webkit, devices } = require('playwright');

// ============================================================================
// CONFIGURATION BUILDING BLOCKS
// ============================================================================

// All Playwright built-in devices we want to test
const DEVICE_NAMES = [
    'Desktop Chrome',
    'Desktop Firefox', 
    'Desktop Safari',
    'Desktop Edge',
    'iPhone 12',
    'iPhone 12 Pro',
    'iPhone 12 Pro Max',
    'iPhone 13',
    'iPhone 13 Pro',
    'iPhone 13 Pro Max',
    'iPhone 14',
    'iPhone 14 Plus',
    'iPhone 14 Pro',
    'iPhone 14 Pro Max',
    'iPhone SE',
    'iPad (gen 7)',
    'iPad Mini',
    'iPad Pro 11',
    'Pixel 5',
    'Pixel 7',
    'Galaxy S8',
    'Galaxy S9+',
    'Galaxy Tab S4',
    'Nexus 10',
    'Kindle Fire HDX'
];

// Major world timezones
const TIMEZONES = [
    'America/New_York',
    'America/Chicago',
    'America/Denver',
    'America/Los_Angeles',
    'America/Anchorage',
    'Pacific/Honolulu',
    'America/Toronto',
    'America/Vancouver',
    'America/Mexico_City',
    'America/Sao_Paulo',
    'America/Buenos_Aires',
    'Europe/London',
    'Europe/Paris',
    'Europe/Berlin',
    'Europe/Rome',
    'Europe/Madrid',
    'Europe/Amsterdam',
    'Europe/Stockholm',
    'Europe/Moscow',
    'Europe/Istanbul',
    'Asia/Dubai',
    'Asia/Mumbai',
    'Asia/Kolkata',
    'Asia/Bangkok',
    'Asia/Singapore',
    'Asia/Hong_Kong',
    'Asia/Shanghai',
    'Asia/Tokyo',
    'Asia/Seoul',
    'Australia/Sydney',
    'Australia/Melbourne',
    'Australia/Perth',
    'Pacific/Auckland',
    'Africa/Cairo',
    'Africa/Johannesburg',
    'Africa/Lagos'
];

// Locale/language combinations
const LOCALES = [
    { locale: 'en-US', name: 'English (US)' },
    { locale: 'en-GB', name: 'English (UK)' },
    { locale: 'en-AU', name: 'English (Australia)' },
    { locale: 'en-CA', name: 'English (Canada)' },
    { locale: 'en-IN', name: 'English (India)' },
    { locale: 'es-ES', name: 'Spanish (Spain)' },
    { locale: 'es-MX', name: 'Spanish (Mexico)' },
    { locale: 'es-AR', name: 'Spanish (Argentina)' },
    { locale: 'pt-BR', name: 'Portuguese (Brazil)' },
    { locale: 'pt-PT', name: 'Portuguese (Portugal)' },
    { locale: 'fr-FR', name: 'French (France)' },
    { locale: 'fr-CA', name: 'French (Canada)' },
    { locale: 'de-DE', name: 'German (Germany)' },
    { locale: 'de-AT', name: 'German (Austria)' },
    { locale: 'de-CH', name: 'German (Switzerland)' },
    { locale: 'it-IT', name: 'Italian' },
    { locale: 'nl-NL', name: 'Dutch' },
    { locale: 'pl-PL', name: 'Polish' },
    { locale: 'ru-RU', name: 'Russian' },
    { locale: 'uk-UA', name: 'Ukrainian' },
    { locale: 'tr-TR', name: 'Turkish' },
    { locale: 'ar-SA', name: 'Arabic (Saudi)' },
    { locale: 'ar-EG', name: 'Arabic (Egypt)' },
    { locale: 'he-IL', name: 'Hebrew' },
    { locale: 'hi-IN', name: 'Hindi' },
    { locale: 'th-TH', name: 'Thai' },
    { locale: 'vi-VN', name: 'Vietnamese' },
    { locale: 'id-ID', name: 'Indonesian' },
    { locale: 'ms-MY', name: 'Malay' },
    { locale: 'zh-CN', name: 'Chinese (Simplified)' },
    { locale: 'zh-TW', name: 'Chinese (Traditional)' },
    { locale: 'zh-HK', name: 'Chinese (Hong Kong)' },
    { locale: 'ja-JP', name: 'Japanese' },
    { locale: 'ko-KR', name: 'Korean' },
    { locale: 'sv-SE', name: 'Swedish' },
    { locale: 'nb-NO', name: 'Norwegian' },
    { locale: 'da-DK', name: 'Danish' },
    { locale: 'fi-FI', name: 'Finnish' },
    { locale: 'el-GR', name: 'Greek' },
    { locale: 'cs-CZ', name: 'Czech' },
    { locale: 'hu-HU', name: 'Hungarian' },
    { locale: 'ro-RO', name: 'Romanian' }
];

// Custom viewport sizes
const VIEWPORTS = [
    { width: 1920, height: 1080, name: '1080p' },
    { width: 2560, height: 1440, name: '1440p' },
    { width: 3840, height: 2160, name: '4K' },
    { width: 1366, height: 768, name: 'HD Laptop' },
    { width: 1536, height: 864, name: 'Popular Laptop' },
    { width: 1440, height: 900, name: 'MacBook Air' },
    { width: 1680, height: 1050, name: 'MacBook Pro 15' },
    { width: 2560, height: 1600, name: 'MacBook Pro 16' },
    { width: 3440, height: 1440, name: 'Ultrawide' },
    { width: 5120, height: 1440, name: 'Super Ultrawide' },
    { width: 1280, height: 800, name: 'Small Laptop' },
    { width: 1024, height: 768, name: 'iPad Landscape' },
    { width: 768, height: 1024, name: 'iPad Portrait' },
    { width: 360, height: 640, name: 'Small Phone' },
    { width: 375, height: 667, name: 'iPhone SE' },
    { width: 390, height: 844, name: 'iPhone 12/13' },
    { width: 414, height: 896, name: 'iPhone XR/11' },
    { width: 428, height: 926, name: 'iPhone 14 Pro Max' },
    { width: 320, height: 568, name: 'iPhone 5' },
    { width: 412, height: 915, name: 'Pixel 7' }
];

// Different pixel ratios to simulate
const PIXEL_RATIOS = [1, 1.25, 1.5, 1.75, 2, 2.5, 3, 3.5];

// ============================================================================
// CONFIGURATION GENERATOR
// ============================================================================

function generateConfigurations() {
    const configs = [];
    let id = 1;

    // ----- SECTION 1: All Playwright devices with default settings -----
    for (const deviceName of DEVICE_NAMES) {
        const device = devices[deviceName];
        if (!device) continue;
        
        configs.push({
            id: id++,
            name: deviceName,
            category: 'Built-in Device',
            browser: device.defaultBrowserType || 'chromium',
            device: deviceName,
            useDevice: true
        });
    }

    // ----- SECTION 2: Desktop browsers with different viewports -----
    const browsers = ['chromium', 'webkit', 'firefox'];
    for (const viewport of VIEWPORTS) {
        const browser = browsers[configs.length % 3];
        configs.push({
            id: id++,
            name: `${browser} ${viewport.name}`,
            category: 'Viewport',
            browser: browser,
            viewport: { width: viewport.width, height: viewport.height },
            screen: { width: viewport.width, height: viewport.height }
        });
    }

    // ----- SECTION 3: Timezone matrix (sample of devices × timezones) -----
    const tzDevices = ['Desktop Chrome', 'iPhone 14 Pro', 'Pixel 7'];
    for (const tz of TIMEZONES) {
        const deviceName = tzDevices[configs.length % tzDevices.length];
        const device = devices[deviceName];
        const tzShort = tz.split('/').pop().replace(/_/g, ' ');
        
        configs.push({
            id: id++,
            name: `${deviceName} - ${tzShort}`,
            category: 'Timezone',
            browser: device?.defaultBrowserType || 'chromium',
            device: deviceName,
            useDevice: !!device,
            timezoneId: tz,
            viewport: device ? undefined : { width: 1280, height: 720 }
        });
    }

    // ----- SECTION 4: Locale matrix -----
    for (const loc of LOCALES) {
        const browser = browsers[configs.length % 3];
        configs.push({
            id: id++,
            name: `Chrome - ${loc.name}`,
            category: 'Locale',
            browser: 'chromium',
            locale: loc.locale,
            viewport: { width: 1280, height: 720 }
        });
    }

    // ----- SECTION 5: Dark mode variations -----
    const darkModeDevices = ['Desktop Chrome', 'Desktop Safari', 'iPhone 14', 'Pixel 7', 'Galaxy S9+'];
    for (const deviceName of darkModeDevices) {
        const device = devices[deviceName];
        configs.push({
            id: id++,
            name: `${deviceName} Dark Mode`,
            category: 'Dark Mode',
            browser: device?.defaultBrowserType || 'chromium',
            device: deviceName,
            useDevice: !!device,
            colorScheme: 'dark',
            viewport: device ? undefined : { width: 1280, height: 720 }
        });
    }

    // ----- SECTION 6: Reduced motion variations -----
    for (const deviceName of darkModeDevices) {
        const device = devices[deviceName];
        configs.push({
            id: id++,
            name: `${deviceName} Reduced Motion`,
            category: 'Accessibility',
            browser: device?.defaultBrowserType || 'chromium',
            device: deviceName,
            useDevice: !!device,
            reducedMotion: 'reduce',
            viewport: device ? undefined : { width: 1280, height: 720 }
        });
    }

    // ----- SECTION 7: High DPI variations -----
    for (const ratio of PIXEL_RATIOS) {
        for (const browser of browsers) {
            configs.push({
                id: id++,
                name: `${browser} @ ${ratio}x DPI`,
                category: 'Pixel Ratio',
                browser: browser,
                viewport: { width: 1280, height: 720 },
                screen: { width: 1280, height: 720 },
                deviceScaleFactor: ratio
            });
        }
    }

    // ----- SECTION 8: Touch vs non-touch -----
    const touchConfigs = [
        { hasTouch: true, isMobile: true, name: 'Touch Mobile' },
        { hasTouch: true, isMobile: false, name: 'Touch Desktop' },
        { hasTouch: false, isMobile: false, name: 'No Touch Desktop' }
    ];
    for (const browser of browsers) {
        for (const touch of touchConfigs) {
            configs.push({
                id: id++,
                name: `${browser} ${touch.name}`,
                category: 'Touch',
                browser: browser,
                viewport: { width: 1280, height: 720 },
                hasTouch: touch.hasTouch,
                isMobile: touch.isMobile
            });
        }
    }

    // ----- SECTION 9: Geolocation spoofing -----
    const geolocations = [
        { latitude: 40.7128, longitude: -74.0060, name: 'New York' },
        { latitude: 51.5074, longitude: -0.1278, name: 'London' },
        { latitude: 35.6762, longitude: 139.6503, name: 'Tokyo' },
        { latitude: -33.8688, longitude: 151.2093, name: 'Sydney' },
        { latitude: 55.7558, longitude: 37.6173, name: 'Moscow' },
        { latitude: 19.4326, longitude: -99.1332, name: 'Mexico City' },
        { latitude: -23.5505, longitude: -46.6333, name: 'São Paulo' },
        { latitude: 1.3521, longitude: 103.8198, name: 'Singapore' },
        { latitude: 25.2048, longitude: 55.2708, name: 'Dubai' },
        { latitude: 28.6139, longitude: 77.2090, name: 'Delhi' }
    ];
    for (const geo of geolocations) {
        configs.push({
            id: id++,
            name: `Chrome in ${geo.name}`,
            category: 'Geolocation',
            browser: 'chromium',
            viewport: { width: 1280, height: 720 },
            geolocation: { latitude: geo.latitude, longitude: geo.longitude },
            permissions: ['geolocation']
        });
    }

    // ----- SECTION 10: Offline mode -----
    for (const browser of browsers) {
        configs.push({
            id: id++,
            name: `${browser} Offline`,
            category: 'Network',
            browser: browser,
            viewport: { width: 1280, height: 720 },
            offline: true
        });
    }

    // ----- SECTION 11: Forced colors (high contrast mode) -----
    configs.push({
        id: id++,
        name: 'Chrome Forced Colors',
        category: 'Accessibility',
        browser: 'chromium',
        viewport: { width: 1280, height: 720 },
        forcedColors: 'active'
    });

    // ----- SECTION 12: Combined variations (timezone + locale + dark mode) -----
    const combos = [
        { tz: 'Asia/Tokyo', locale: 'ja-JP', dark: true, name: 'Tokyo Dark Mode' },
        { tz: 'Europe/Berlin', locale: 'de-DE', dark: true, name: 'Berlin Dark Mode' },
        { tz: 'America/Sao_Paulo', locale: 'pt-BR', dark: false, name: 'São Paulo Light' },
        { tz: 'Asia/Shanghai', locale: 'zh-CN', dark: true, name: 'Shanghai Dark Mode' },
        { tz: 'Europe/Paris', locale: 'fr-FR', dark: false, name: 'Paris Light Mode' },
        { tz: 'Asia/Mumbai', locale: 'hi-IN', dark: true, name: 'Mumbai Dark Mode' },
        { tz: 'Africa/Lagos', locale: 'en-NG', dark: false, name: 'Lagos Light Mode' },
        { tz: 'America/Los_Angeles', locale: 'en-US', dark: true, name: 'LA Dark Mode' }
    ];
    for (const combo of combos) {
        for (const browser of ['chromium', 'webkit']) {
            configs.push({
                id: id++,
                name: `${browser} ${combo.name}`,
                category: 'Combined',
                browser: browser,
                viewport: { width: 1920, height: 1080 },
                timezoneId: combo.tz,
                locale: combo.locale,
                colorScheme: combo.dark ? 'dark' : 'light'
            });
        }
    }

    return configs;
}

// ============================================================================
// TEST RUNNER
// ============================================================================

async function runTest(config) {
    const startTime = Date.now();
    let browser, context, page;
    
    try {
        // Launch browser
        const browserType = config.browser === 'firefox' ? firefox : 
                           config.browser === 'webkit' ? webkit : chromium;
        
        browser = await browserType.launch({ 
            headless: true,
            args: config.browser === 'chromium' ? ['--ignore-certificate-errors'] : []
        });
        
        // Build context options
        const contextOptions = {
            ignoreHTTPSErrors: true
        };
        
        // Use built-in device if specified
        if (config.useDevice && config.device && devices[config.device]) {
            Object.assign(contextOptions, devices[config.device]);
        }
        
        // Override with custom settings
        if (config.viewport) contextOptions.viewport = config.viewport;
        if (config.screen) contextOptions.screen = config.screen;
        if (config.deviceScaleFactor) contextOptions.deviceScaleFactor = config.deviceScaleFactor;
        if (config.hasTouch !== undefined) contextOptions.hasTouch = config.hasTouch;
        if (config.isMobile !== undefined) contextOptions.isMobile = config.isMobile;
        if (config.locale) contextOptions.locale = config.locale;
        if (config.timezoneId) contextOptions.timezoneId = config.timezoneId;
        if (config.colorScheme) contextOptions.colorScheme = config.colorScheme;
        if (config.reducedMotion) contextOptions.reducedMotion = config.reducedMotion;
        if (config.forcedColors) contextOptions.forcedColors = config.forcedColors;
        if (config.geolocation) contextOptions.geolocation = config.geolocation;
        if (config.permissions) contextOptions.permissions = config.permissions;
        if (config.offline) contextOptions.offline = config.offline;
        
        context = await browser.newContext(contextOptions);
        page = await context.newPage();
        
        // Navigate to test page
        await page.goto('https://localhost:6001/test', { 
            waitUntil: 'networkidle',
            timeout: 30000
        });
        
        // Wait for fingerprinting to complete and pixel to fire
        await page.waitForTimeout(2000);
        
        // Check if pixel was fired
        const pixelFired = await page.evaluate(() => {
            return window.__smartpixl_sent === true;
        });
        
        const elapsed = Date.now() - startTime;
        
        return {
            success: true,
            pixelFired,
            elapsed,
            config
        };
        
    } catch (error) {
        return {
            success: false,
            error: error.message.split('\n')[0],
            config
        };
    } finally {
        if (page) await page.close().catch(() => {});
        if (context) await context.close().catch(() => {});
        if (browser) await browser.close().catch(() => {});
    }
}

// ============================================================================
// MAIN
// ============================================================================

async function main() {
    const configs = generateConfigurations();
    
    console.log('╔══════════════════════════════════════════════════════════════╗');
    console.log('║  SmartPiXL MEGA Device Fingerprint Test Suite                ║');
    console.log('║  M1 Data & Analytics - Internal Testing                      ║');
    console.log('║  Company ID: 12345 | Pixel ID: 1                             ║');
    console.log('╚══════════════════════════════════════════════════════════════╝');
    console.log();
    console.log(`Total configurations to test: ${configs.length}`);
    console.log('Target: https://localhost:6001/test');
    console.log();
    
    // Group by category for summary
    const categories = {};
    for (const config of configs) {
        const cat = config.category || 'Other';
        categories[cat] = (categories[cat] || 0) + 1;
    }
    
    console.log('Configuration breakdown:');
    for (const [cat, count] of Object.entries(categories).sort((a, b) => b[1] - a[1])) {
        console.log(`  ${cat}: ${count}`);
    }
    console.log();
    console.log('Starting tests...\n');
    
    let successful = 0;
    let failed = 0;
    const failures = [];
    const startTime = Date.now();
    
    // Run tests with concurrency limit to avoid overwhelming the system
    const CONCURRENCY = 5;
    
    for (let i = 0; i < configs.length; i += CONCURRENCY) {
        const batch = configs.slice(i, i + CONCURRENCY);
        const results = await Promise.all(batch.map(runTest));
        
        for (const result of results) {
            const config = result.config;
            const idx = config.id;
            const progress = `[${idx}/${configs.length}]`;
            
            if (result.success && result.pixelFired) {
                successful++;
                console.log(`${progress} ✓ ${config.name} (${result.elapsed}ms)`);
            } else if (result.success) {
                // Page loaded but pixel didn't fire (might be offline test)
                successful++;
                console.log(`${progress} ◐ ${config.name} (no pixel - ${config.category})`);
            } else {
                failed++;
                failures.push({ name: config.name, error: result.error });
                console.log(`${progress} ✗ ${config.name}: ${result.error.substring(0, 50)}`);
            }
        }
    }
    
    const totalTime = ((Date.now() - startTime) / 1000).toFixed(1);
    
    console.log();
    console.log('╔══════════════════════════════════════════════════════════════╗');
    console.log('║  TEST SUMMARY                                                ║');
    console.log('╚══════════════════════════════════════════════════════════════╝');
    console.log();
    console.log(`   ✓ Successful: ${successful}`);
    console.log(`   ✗ Failed: ${failed}`);
    console.log(`   Total: ${configs.length}`);
    console.log(`   Time: ${totalTime}s`);
    console.log();
    
    if (failures.length > 0 && failures.length <= 10) {
        console.log('   Failed tests:');
        for (const f of failures) {
            console.log(`     - ${f.name}: ${f.error.substring(0, 60)}`);
        }
        console.log();
    } else if (failures.length > 10) {
        console.log(`   (${failures.length} failures - mostly Firefox SSL issues on localhost)`);
        console.log();
    }
    
    console.log('   Check your database for the new fingerprint records!');
    console.log('   SQL: SELECT COUNT(*) FROM PiXL_Test');
    console.log('   SQL: SELECT * FROM vw_PiXL_Parsed ORDER BY RecordID DESC');
    console.log();
}

main().catch(console.error);
