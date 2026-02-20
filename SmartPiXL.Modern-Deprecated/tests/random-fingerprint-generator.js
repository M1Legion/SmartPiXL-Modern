/**
 * SmartPiXL Random Fingerprint Generator
 * 
 * Generates RANDOM valid device configurations for stress testing.
 * Each run produces unique fingerprints by randomizing all attributes.
 * 
 * Usage: node random-fingerprint-generator.js [count] [concurrency]
 *   count: number of fingerprints to generate (default: 1000)
 *   concurrency: parallel browser instances (default: 20)
 * 
 * Example: node random-fingerprint-generator.js 100000 30
 * 
 * M1 Data & Analytics - Internal Testing
 */

const { chromium, firefox, webkit, devices } = require('playwright');

// ============================================================================
// RANDOMIZATION POOLS
// ============================================================================

const BROWSERS = ['chromium', 'webkit', 'firefox'];

const DEVICE_NAMES = Object.keys(devices);

const TIMEZONES = [
    'America/New_York', 'America/Chicago', 'America/Denver', 'America/Los_Angeles',
    'America/Anchorage', 'Pacific/Honolulu', 'America/Toronto', 'America/Vancouver',
    'America/Mexico_City', 'America/Sao_Paulo', 'America/Buenos_Aires', 'America/Lima',
    'America/Bogota', 'America/Santiago', 'Europe/London', 'Europe/Paris',
    'Europe/Berlin', 'Europe/Rome', 'Europe/Madrid', 'Europe/Amsterdam',
    'Europe/Stockholm', 'Europe/Oslo', 'Europe/Helsinki', 'Europe/Warsaw',
    'Europe/Prague', 'Europe/Vienna', 'Europe/Zurich', 'Europe/Brussels',
    'Europe/Moscow', 'Europe/Istanbul', 'Europe/Athens', 'Europe/Bucharest',
    'Asia/Dubai', 'Asia/Karachi', 'Asia/Kolkata', 'Asia/Dhaka',
    'Asia/Bangkok', 'Asia/Singapore', 'Asia/Hong_Kong', 'Asia/Shanghai',
    'Asia/Tokyo', 'Asia/Seoul', 'Asia/Taipei', 'Asia/Manila',
    'Asia/Jakarta', 'Asia/Kuala_Lumpur', 'Australia/Sydney', 'Australia/Melbourne',
    'Australia/Perth', 'Australia/Brisbane', 'Pacific/Auckland', 'Pacific/Fiji',
    'Africa/Cairo', 'Africa/Johannesburg', 'Africa/Lagos', 'Africa/Nairobi',
    'Africa/Casablanca', 'Africa/Accra'
];

const LOCALES = [
    'en-US', 'en-GB', 'en-AU', 'en-CA', 'en-NZ', 'en-IN', 'en-ZA', 'en-IE',
    'es-ES', 'es-MX', 'es-AR', 'es-CO', 'es-CL', 'es-PE', 'es-VE',
    'pt-BR', 'pt-PT', 'fr-FR', 'fr-CA', 'fr-BE', 'fr-CH',
    'de-DE', 'de-AT', 'de-CH', 'it-IT', 'nl-NL', 'nl-BE',
    'pl-PL', 'ru-RU', 'uk-UA', 'cs-CZ', 'sk-SK', 'hu-HU', 'ro-RO',
    'tr-TR', 'el-GR', 'bg-BG', 'hr-HR', 'sl-SI', 'sr-RS',
    'ar-SA', 'ar-EG', 'ar-AE', 'ar-MA', 'he-IL', 'fa-IR',
    'hi-IN', 'bn-IN', 'ta-IN', 'te-IN', 'mr-IN', 'gu-IN',
    'th-TH', 'vi-VN', 'id-ID', 'ms-MY', 'tl-PH',
    'zh-CN', 'zh-TW', 'zh-HK', 'ja-JP', 'ko-KR',
    'sv-SE', 'nb-NO', 'da-DK', 'fi-FI', 'is-IS',
    'af-ZA', 'sw-KE', 'am-ET'
];

const SCREEN_SIZES = [
    { w: 1920, h: 1080 }, { w: 1366, h: 768 }, { w: 1536, h: 864 },
    { w: 1440, h: 900 }, { w: 1280, h: 720 }, { w: 1280, h: 800 },
    { w: 1680, h: 1050 }, { w: 1600, h: 900 }, { w: 2560, h: 1440 },
    { w: 3840, h: 2160 }, { w: 2560, h: 1600 }, { w: 3440, h: 1440 },
    { w: 1024, h: 768 }, { w: 768, h: 1024 }, { w: 360, h: 640 },
    { w: 375, h: 667 }, { w: 390, h: 844 }, { w: 393, h: 852 },
    { w: 414, h: 896 }, { w: 428, h: 926 }, { w: 412, h: 915 },
    { w: 360, h: 780 }, { w: 384, h: 854 }, { w: 320, h: 568 },
    { w: 834, h: 1194 }, { w: 820, h: 1180 }, { w: 768, h: 1024 },
    { w: 810, h: 1080 }, { w: 800, h: 1280 }, { w: 601, h: 962 },
    { w: 1280, h: 1024 }, { w: 1400, h: 1050 }, { w: 1920, h: 1200 },
    { w: 2048, h: 1152 }, { w: 2880, h: 1800 }, { w: 5120, h: 2880 }
];

const PIXEL_RATIOS = [1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 2.75, 3, 3.5, 4];

// ============================================================================
// RANDOM HELPERS
// ============================================================================

const pick = arr => arr[Math.floor(Math.random() * arr.length)];
const chance = pct => Math.random() * 100 < pct;

function generateRandomConfig(id) {
    // Decide: use a built-in device or custom config?
    const useDevice = chance(30); // 30% chance to use built-in device
    
    if (useDevice) {
        const deviceName = pick(DEVICE_NAMES);
        const device = devices[deviceName];
        if (!device) return generateRandomConfig(id); // retry if device not found
        
        return {
            id,
            name: `${deviceName} (${pick(TIMEZONES).split('/').pop()})`,
            browser: device.defaultBrowserType || pick(BROWSERS),
            useDevice: true,
            device: deviceName,
            timezoneId: pick(TIMEZONES),
            locale: pick(LOCALES),
            colorScheme: chance(25) ? 'dark' : 'light',
            reducedMotion: chance(5) ? 'reduce' : 'no-preference'
        };
    }
    
    // Custom random config
    const screen = pick(SCREEN_SIZES);
    const isMobile = screen.w < 600 || screen.h < 600;
    const browser = pick(BROWSERS);
    
    return {
        id,
        name: `Random-${id}`,
        browser,
        viewport: { width: screen.w, height: screen.h },
        screen: { width: screen.w, height: screen.h },
        deviceScaleFactor: pick(PIXEL_RATIOS),
        hasTouch: isMobile ? chance(90) : chance(10),
        isMobile: browser !== 'firefox' ? isMobile : undefined, // Firefox doesn't support isMobile
        timezoneId: pick(TIMEZONES),
        locale: pick(LOCALES),
        colorScheme: chance(25) ? 'dark' : 'light',
        reducedMotion: chance(5) ? 'reduce' : 'no-preference',
        forcedColors: chance(2) ? 'active' : 'none'
    };
}

// ============================================================================
// TEST RUNNER
// ============================================================================

async function runTest(config) {
    let browser, context, page;
    
    try {
        const browserType = config.browser === 'firefox' ? firefox : 
                           config.browser === 'webkit' ? webkit : chromium;
        
        browser = await browserType.launch({ 
            headless: true,
            args: config.browser === 'chromium' ? ['--ignore-certificate-errors'] : []
        });
        
        const contextOptions = { ignoreHTTPSErrors: true };
        
        if (config.useDevice && config.device && devices[config.device]) {
            Object.assign(contextOptions, devices[config.device]);
        }
        
        if (config.viewport) contextOptions.viewport = config.viewport;
        if (config.screen) contextOptions.screen = config.screen;
        if (config.deviceScaleFactor) contextOptions.deviceScaleFactor = config.deviceScaleFactor;
        if (config.hasTouch !== undefined) contextOptions.hasTouch = config.hasTouch;
        if (config.isMobile !== undefined) contextOptions.isMobile = config.isMobile;
        if (config.locale) contextOptions.locale = config.locale;
        if (config.timezoneId) contextOptions.timezoneId = config.timezoneId;
        if (config.colorScheme) contextOptions.colorScheme = config.colorScheme;
        if (config.reducedMotion) contextOptions.reducedMotion = config.reducedMotion;
        if (config.forcedColors && config.forcedColors !== 'none') {
            contextOptions.forcedColors = config.forcedColors;
        }
        
        context = await browser.newContext(contextOptions);
        page = await context.newPage();
        
        await page.goto('https://localhost:6001/test', { 
            waitUntil: 'networkidle',
            timeout: 15000
        });
        
        await page.waitForTimeout(1500);
        
        return { success: true };
        
    } catch (error) {
        return { success: false, error: error.message.split('\n')[0].substring(0, 50) };
    } finally {
        if (page) await page.close().catch(() => {});
        if (context) await context.close().catch(() => {});
        if (browser) await browser.close().catch(() => {});
    }
}

// ============================================================================
// BATCH PROCESSOR
// ============================================================================

async function runBatch(configs, concurrency) {
    let successful = 0;
    let failed = 0;
    
    for (let i = 0; i < configs.length; i += concurrency) {
        const batch = configs.slice(i, i + concurrency);
        const results = await Promise.all(batch.map(runTest));
        
        for (const result of results) {
            if (result.success) successful++;
            else failed++;
        }
        
        // Progress update every batch
        const done = Math.min(i + concurrency, configs.length);
        const pct = ((done / configs.length) * 100).toFixed(1);
        process.stdout.write(`\r   Progress: ${done}/${configs.length} (${pct}%) | ✓ ${successful} ✗ ${failed}`);
    }
    
    console.log(); // newline after progress
    return { successful, failed };
}

// ============================================================================
// MAIN
// ============================================================================

async function main() {
    const count = parseInt(process.argv[2]) || 1000;
    const concurrency = parseInt(process.argv[3]) || 20;
    
    console.log('╔══════════════════════════════════════════════════════════════╗');
    console.log('║  SmartPiXL Random Fingerprint Generator                      ║');
    console.log('║  M1 Data & Analytics - Stress Test Mode                      ║');
    console.log('╚══════════════════════════════════════════════════════════════╝');
    console.log();
    console.log(`   Target Count: ${count.toLocaleString()}`);
    console.log(`   Concurrency: ${concurrency} parallel browsers`);
    console.log(`   Endpoint: https://localhost:6001/test`);
    console.log();
    
    // Generate all random configs upfront
    console.log('   Generating random configurations...');
    const configs = [];
    for (let i = 1; i <= count; i++) {
        configs.push(generateRandomConfig(i));
    }
    console.log(`   Generated ${configs.length} unique random configs`);
    console.log();
    
    const startTime = Date.now();
    console.log('   Running tests...');
    
    const { successful, failed } = await runBatch(configs, concurrency);
    
    const elapsed = ((Date.now() - startTime) / 1000).toFixed(1);
    const rate = (count / (elapsed / 60)).toFixed(0);
    
    console.log();
    console.log('╔══════════════════════════════════════════════════════════════╗');
    console.log('║  COMPLETE                                                    ║');
    console.log('╚══════════════════════════════════════════════════════════════╝');
    console.log();
    console.log(`   ✓ Successful: ${successful.toLocaleString()}`);
    console.log(`   ✗ Failed: ${failed.toLocaleString()}`);
    console.log(`   Time: ${elapsed}s`);
    console.log(`   Rate: ~${rate}/min`);
    console.log();
    console.log('   Check database: SELECT COUNT(*) FROM PiXL_Test');
    console.log();
}

main().catch(err => {
    console.error('Fatal error:', err);
    process.exit(1);
});
