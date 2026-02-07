/**
 * SmartPiXL High-Volume Synthetic Test Runner
 * 
 * Generates 100,000+ synthetic hits against https://smartpixl.com
 * using REAL browser fingerprinting with the synthetic=1 flag.
 * 
 * Architecture:
 *   - Browser pool (3 instances) with context recycling
 *   - Rich randomization: 58 TZ, 68 locales, 36 screens, 3 engines
 *   - page.route() intercepts pixel GIF requests to append &synthetic=1
 *   - Self-healing: auto-restarts on consecutive failures
 *   - Progress tracking with ETA and rate
 * 
 * Usage:
 *   node synthetic-runner.js [count] [concurrency]
 *   node synthetic-runner.js 100000 20
 *   node synthetic-runner.js           # defaults: 100000 count, 20 concurrency
 * 
 * M1 Data & Analytics - Synthetic Testing
 */

const { chromium, firefox, webkit, devices } = require('playwright');

// ============================================================================
// CONFIG
// ============================================================================
const TARGET_URL = 'https://smartpixl.info';
const PIXEL_PATTERN = /SMART\.GIF/i;
const BROWSER_POOL_SIZE = 3;
const CONTEXTS_PER_BROWSER = 7;
const PAGE_WAIT_MS = 1500;          // Wait for async collectors (audio, battery, storage)
const RECYCLE_EVERY = 500;          // Restart browsers every N requests (memory leak prevention)
const MAX_CONSECUTIVE_FAILURES = 10;
const STATS_INTERVAL_MS = 15000;    // Print detailed stats every 15s

// ============================================================================
// RANDOMIZATION POOLS (merged from random-fingerprint-generator + mega-device-test)
// ============================================================================
const TIMEZONES = [
    'America/New_York', 'America/Chicago', 'America/Denver', 'America/Los_Angeles',
    'America/Anchorage', 'Pacific/Honolulu', 'America/Toronto', 'America/Vancouver',
    'America/Mexico_City', 'America/Sao_Paulo', 'America/Buenos_Aires',
    'America/Bogota', 'America/Santiago', 'America/Lima', 'America/Caracas',
    'Europe/London', 'Europe/Paris', 'Europe/Berlin', 'Europe/Rome', 'Europe/Madrid',
    'Europe/Amsterdam', 'Europe/Stockholm', 'Europe/Moscow', 'Europe/Istanbul',
    'Europe/Warsaw', 'Europe/Prague', 'Europe/Bucharest', 'Europe/Athens',
    'Europe/Helsinki', 'Europe/Lisbon', 'Europe/Dublin', 'Europe/Brussels',
    'Europe/Vienna', 'Europe/Zurich', 'Europe/Oslo', 'Europe/Copenhagen',
    'Asia/Dubai', 'Asia/Kolkata', 'Asia/Mumbai', 'Asia/Bangkok', 'Asia/Singapore',
    'Asia/Hong_Kong', 'Asia/Shanghai', 'Asia/Tokyo', 'Asia/Seoul',
    'Asia/Taipei', 'Asia/Jakarta', 'Asia/Manila', 'Asia/Karachi',
    'Asia/Tehran', 'Asia/Riyadh', 'Asia/Kuala_Lumpur',
    'Australia/Sydney', 'Australia/Melbourne', 'Australia/Perth',
    'Pacific/Auckland', 'Africa/Cairo', 'Africa/Johannesburg', 'Africa/Lagos'
];

const LOCALES = [
    'en-US', 'en-GB', 'en-AU', 'en-CA', 'en-IN', 'en-NZ', 'en-ZA',
    'es-ES', 'es-MX', 'es-AR', 'es-CO', 'es-CL',
    'pt-BR', 'pt-PT', 'fr-FR', 'fr-CA', 'fr-BE',
    'de-DE', 'de-AT', 'de-CH', 'it-IT', 'nl-NL', 'nl-BE',
    'pl-PL', 'ru-RU', 'uk-UA', 'tr-TR',
    'ar-SA', 'ar-EG', 'he-IL', 'hi-IN', 'bn-IN',
    'th-TH', 'vi-VN', 'id-ID', 'ms-MY', 'fil-PH',
    'zh-CN', 'zh-TW', 'zh-HK', 'ja-JP', 'ko-KR',
    'sv-SE', 'nb-NO', 'da-DK', 'fi-FI',
    'el-GR', 'cs-CZ', 'hu-HU', 'ro-RO', 'bg-BG',
    'sk-SK', 'hr-HR', 'sl-SI', 'lt-LT', 'lv-LV',
    'et-EE', 'ka-GE', 'sr-RS', 'mk-MK',
    'af-ZA', 'sw-KE', 'am-ET',
    'ur-PK', 'fa-IR', 'ta-IN', 'te-IN', 'ml-IN', 'kn-IN',
    'gu-IN', 'mr-IN', 'pa-IN'
];

const SCREEN_SIZES = [
    // Desktop
    { w: 1920, h: 1080 }, { w: 1366, h: 768 }, { w: 1536, h: 864 },
    { w: 1440, h: 900 }, { w: 2560, h: 1440 }, { w: 3840, h: 2160 },
    { w: 1280, h: 720 }, { w: 1680, h: 1050 }, { w: 2560, h: 1600 },
    { w: 3440, h: 1440 }, { w: 1280, h: 800 }, { w: 1024, h: 768 },
    { w: 1920, h: 1200 }, { w: 1600, h: 900 }, { w: 5120, h: 1440 },
    { w: 1280, h: 1024 },
    // Tablet
    { w: 768, h: 1024 }, { w: 834, h: 1194 }, { w: 820, h: 1180 },
    { w: 810, h: 1080 }, { w: 1024, h: 1366 },
    // Mobile
    { w: 390, h: 844 }, { w: 393, h: 852 }, { w: 412, h: 915 },
    { w: 428, h: 926 }, { w: 375, h: 812 }, { w: 360, h: 800 },
    { w: 375, h: 667 }, { w: 414, h: 896 }, { w: 360, h: 640 },
    { w: 320, h: 568 }, { w: 360, h: 780 }, { w: 384, h: 854 },
    { w: 411, h: 731 }, { w: 360, h: 720 }, { w: 412, h: 892 },
    { w: 432, h: 960 }
];

const PIXEL_RATIOS = [1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 2.75, 3, 3.5];

const DEVICE_NAMES = Object.keys(devices).filter(d => 
    !d.includes('landscape') && !d.includes('HiDPI') && !d.includes('--')
);

// Helpers
const pick = arr => arr[Math.floor(Math.random() * arr.length)];
const chance = pct => Math.random() * 100 < pct;

// ============================================================================
// BROWSER POOL (with interleaved engines and recycling)
// ============================================================================
class BrowserPool {
    constructor() {
        this.browsers = [];
        this.currentIndex = 0;
        this.requestCount = 0;
    }

    async initialize() {
        console.log(`   Launching ${BROWSER_POOL_SIZE} browser instances...`);
        // Alternate between Chromium and WebKit
        // (Skip Firefox - too finicky with external SSL certs on Windows)
        for (let i = 0; i < BROWSER_POOL_SIZE; i++) {
            await this._launchBrowser(i);
        }
        console.log(`   Browser pool ready: ${this.browsers.map(b => b.typeName).join(', ')}`);
    }

    async _launchBrowser(index) {
        const type = index % 2 === 0 ? chromium : webkit;
        const typeName = index % 2 === 0 ? 'chromium' : 'webkit';
        const browser = await type.launch({
            headless: true,
            args: typeName === 'chromium' 
                ? ['--ignore-certificate-errors', '--disable-web-security', '--no-sandbox']
                : []
        });
        this.browsers[index] = { browser, type, typeName, index };
    }

    getNext() {
        const entry = this.browsers[this.currentIndex];
        this.currentIndex = (this.currentIndex + 1) % this.browsers.length;
        this.requestCount++;
        return entry;
    }

    async recycleIfNeeded() {
        if (this.requestCount >= RECYCLE_EVERY) {
            process.stdout.write('\n   [Recycling browsers to free memory...]\n');
            await this.closeAll();
            this.browsers = [];
            await this.initialize();
            this.requestCount = 0;
        }
    }

    async closeAll() {
        await Promise.all(this.browsers.map(b => b.browser.close().catch(() => {})));
    }
}

// ============================================================================
// RANDOM CONFIG GENERATOR
// ============================================================================
function generateRandomConfig() {
    const useDevice = chance(30); // 30% use built-in Playwright device profiles
    const isMobile = chance(35);

    if (useDevice) {
        const deviceName = pick(DEVICE_NAMES);
        const device = devices[deviceName];
        if (device) {
            return {
                useDevice: true,
                device: deviceName,
                timezoneId: pick(TIMEZONES),
                locale: pick(LOCALES),
                colorScheme: chance(25) ? 'dark' : 'light',
                reducedMotion: chance(5) ? 'reduce' : 'no-preference',
                forcedColors: chance(2) ? 'active' : 'none'
            };
        }
    }

    const screen = pick(SCREEN_SIZES);
    return {
        useDevice: false,
        viewport: { width: screen.w, height: screen.h },
        screen: { width: screen.w, height: screen.h },
        deviceScaleFactor: pick(PIXEL_RATIOS),
        hasTouch: isMobile ? chance(90) : chance(10),
        isMobile: isMobile,
        timezoneId: pick(TIMEZONES),
        locale: pick(LOCALES),
        colorScheme: chance(25) ? 'dark' : 'light',
        reducedMotion: chance(5) ? 'reduce' : 'no-preference',
        forcedColors: chance(2) ? 'active' : 'none'
    };
}

// ============================================================================
// SYNTHETIC HIT RUNNER
// Visits smartpixl.com with a real browser, intercepts the pixel request
// to append &synthetic=1, waits for the fingerprint fire.
// ============================================================================
async function runSyntheticHit(browserEntry) {
    const { browser, typeName } = browserEntry;
    let context, page;

    try {
        const config = generateRandomConfig();
        const contextOptions = { ignoreHTTPSErrors: true };

        if (config.useDevice && config.device && devices[config.device]) {
            Object.assign(contextOptions, devices[config.device]);
        } else {
            if (config.viewport) contextOptions.viewport = config.viewport;
            if (config.screen) contextOptions.screen = config.screen;
            if (config.deviceScaleFactor) contextOptions.deviceScaleFactor = config.deviceScaleFactor;
            if (config.hasTouch !== undefined) contextOptions.hasTouch = config.hasTouch;
            // Only Chromium supports isMobile context option
            if (config.isMobile !== undefined && typeName === 'chromium') {
                contextOptions.isMobile = config.isMobile;
            }
        }

        if (config.locale) contextOptions.locale = config.locale;
        if (config.timezoneId) contextOptions.timezoneId = config.timezoneId;
        if (config.colorScheme) contextOptions.colorScheme = config.colorScheme;
        if (config.reducedMotion) contextOptions.reducedMotion = config.reducedMotion;
        if (config.forcedColors && config.forcedColors !== 'none') {
            contextOptions.forcedColors = config.forcedColors;
        }

        context = await browser.newContext(contextOptions);
        page = await context.newPage();

        // ============================================================
        // ROUTE INTERCEPTION: Append &synthetic=1 to pixel requests
        // This is the key mechanism - the real browser fires the real
        // fingerprint JS, we just tag the resulting pixel hit.
        // ============================================================
        let pixelFired = false;
        await page.route('**/*SMART.GIF*', async (route) => {
            const url = route.request().url();
            const separator = url.includes('?') ? '&' : '?';
            const newUrl = url + separator + 'synthetic=1';
            pixelFired = true;
            await route.continue({ url: newUrl });
        });

        // Navigate to the target site first (sets correct origin/referer)
        await page.goto(TARGET_URL, {
            waitUntil: 'domcontentloaded',
            timeout: 15000
        });

        // Inject the Tier 5 tracking script dynamically
        // This mimics a real customer embedding: <script src="/js/12345/1.js"></script>
        await page.evaluate((scriptUrl) => {
            return new Promise((resolve) => {
                const script = document.createElement('script');
                script.src = scriptUrl;
                script.onload = () => setTimeout(resolve, 1200); // Wait for async collectors
                script.onerror = () => resolve(); // Don't hang on failure
                document.head.appendChild(script);
            });
        }, TARGET_URL + '/js/12345/1.js');

        // Wait for pixel fire
        await page.waitForTimeout(PAGE_WAIT_MS);

        return { success: true, pixelFired };

    } catch (e) {
        return { success: false, error: e.message.split('\n')[0].substring(0, 200) };
    } finally {
        if (page) await page.close().catch(() => {});
        if (context) await context.close().catch(() => {});
    }
}

// ============================================================================
// STATS TRACKER
// ============================================================================
class StatsTracker {
    constructor(target) {
        this.target = target;
        this.completed = 0;
        this.successful = 0;
        this.failed = 0;
        this.pixelsFired = 0;
        this.startTime = Date.now();
        this.lastReport = Date.now();
        this.consecutiveFailures = 0;
    }

    record(result) {
        this.completed++;
        if (result.success) {
            this.successful++;
            this.consecutiveFailures = 0;
            if (result.pixelFired) this.pixelsFired++;
        } else {
            this.failed++;
            this.consecutiveFailures++;
        }
    }

    get elapsedSec() {
        return (Date.now() - this.startTime) / 1000;
    }

    get rate() {
        return this.elapsedSec > 0 ? this.completed / this.elapsedSec : 0;
    }

    get ratePerMin() {
        return Math.round(this.rate * 60);
    }

    get etaSec() {
        return this.rate > 0 ? Math.round((this.target - this.completed) / this.rate) : 0;
    }

    get etaFormatted() {
        const s = this.etaSec;
        if (s < 60) return `${s}s`;
        if (s < 3600) return `${Math.floor(s / 60)}m ${s % 60}s`;
        const h = Math.floor(s / 3600);
        const m = Math.floor((s % 3600) / 60);
        return `${h}h ${m}m`;
    }

    progressLine() {
        const pct = ((this.completed / this.target) * 100).toFixed(1);
        return `   ${this.completed.toLocaleString()}/${this.target.toLocaleString()} (${pct}%) | ` +
               `${this.ratePerMin}/min | ETA: ${this.etaFormatted} | ` +
               `OK:${this.successful} PX:${this.pixelsFired} ERR:${this.failed}`;
    }

    shouldPrintDetailed() {
        if (Date.now() - this.lastReport >= STATS_INTERVAL_MS) {
            this.lastReport = Date.now();
            return true;
        }
        return false;
    }

    detailedReport() {
        const elapsed = this.elapsedSec;
        const pixelRate = this.completed > 0 
            ? ((this.pixelsFired / this.completed) * 100).toFixed(1) 
            : '0.0';
        return `\n   ┌── Detailed Stats ──────────────────────────────┐\n` +
               `   │ Elapsed: ${elapsed.toFixed(0)}s | Rate: ${this.ratePerMin}/min\n` +
               `   │ Success: ${this.successful} | Failed: ${this.failed}\n` +
               `   │ Pixel fire rate: ${pixelRate}%\n` +
               `   │ Remaining: ${(this.target - this.completed).toLocaleString()} | ETA: ${this.etaFormatted}\n` +
               `   └────────────────────────────────────────────────┘`;
    }
}

// ============================================================================
// MAIN
// ============================================================================
async function main() {
    const targetCount = parseInt(process.argv[2]) || 100000;
    const concurrency = parseInt(process.argv[3]) || BROWSER_POOL_SIZE * CONTEXTS_PER_BROWSER;

    console.log('╔══════════════════════════════════════════════════════════════╗');
    console.log('║  SmartPiXL Synthetic Test Runner                             ║');
    console.log('║  High-Volume Real-Browser Fingerprinting with synthetic=1    ║');
    console.log('║  M1 Data & Analytics                                         ║');
    console.log('╚══════════════════════════════════════════════════════════════╝');
    console.log();
    console.log(`   Target:      ${targetCount.toLocaleString()} synthetic hits`);
    console.log(`   Concurrency: ${concurrency} parallel contexts`);
    console.log(`   Endpoint:    ${TARGET_URL}`);
    console.log(`   Pool:        ${BROWSER_POOL_SIZE} browsers, recycle every ${RECYCLE_EVERY}`);
    console.log(`   Randomization:`);
    console.log(`     Timezones: ${TIMEZONES.length}`);
    console.log(`     Locales:   ${LOCALES.length}`);
    console.log(`     Screens:   ${SCREEN_SIZES.length}`);
    console.log(`     DPI:       ${PIXEL_RATIOS.length}`);
    console.log(`     Devices:   ${DEVICE_NAMES.length} Playwright built-in`);
    console.log(`     30% use built-in device profiles, 70% custom random`);
    console.log();

    const pool = new BrowserPool();
    await pool.initialize();

    const stats = new StatsTracker(targetCount);
    console.log();
    console.log('   Running...');

    while (stats.completed < targetCount) {
        // Recycle browsers periodically
        await pool.recycleIfNeeded();

        // Bail on persistent failures
        if (stats.consecutiveFailures >= MAX_CONSECUTIVE_FAILURES) {
            console.log(`\n\n   ⚠️  ${MAX_CONSECUTIVE_FAILURES} consecutive failures - server may be down!`);
            console.log(`   Last error: ${stats.lastError || 'unknown'}`);
            console.log('   Stopping...');
            break;
        }

        const batchSize = Math.min(concurrency, targetCount - stats.completed);
        const promises = [];

        for (let i = 0; i < batchSize; i++) {
            const browserEntry = pool.getNext();
            promises.push(runSyntheticHit(browserEntry));
        }

        const results = await Promise.all(promises);
        
        for (const result of results) {
            stats.record(result);
            if (!result.success) {
                stats.lastError = result.error;
                if (stats.completed <= 5) console.log('\n   ERR: ' + result.error);
            }
        }

        // Progress output
        process.stdout.write('\r' + stats.progressLine() + '    ');

        // Periodic detailed stats
        if (stats.shouldPrintDetailed()) {
            console.log(stats.detailedReport());
        }
    }

    // Final report
    const elapsed = stats.elapsedSec;
    console.log();
    console.log();
    console.log('╔══════════════════════════════════════════════════════════════╗');
    console.log('║  SYNTHETIC TEST RUN COMPLETE                                 ║');
    console.log('╚══════════════════════════════════════════════════════════════╝');
    console.log();
    console.log(`   Total:       ${stats.completed.toLocaleString()}`);
    console.log(`   Successful:  ${stats.successful.toLocaleString()}`);
    console.log(`   Pixels fired: ${stats.pixelsFired.toLocaleString()}`);
    console.log(`   Failed:      ${stats.failed.toLocaleString()}`);
    console.log(`   Duration:    ${elapsed.toFixed(0)}s (${(elapsed / 60).toFixed(1)}min)`);
    console.log(`   Rate:        ${stats.ratePerMin.toLocaleString()}/min`);
    console.log();
    console.log('   Verify in SQL:');
    console.log('     SELECT COUNT(*) FROM vw_PiXL_Complete WHERE IsSynthetic = 1;');
    console.log('     SELECT TOP 10 * FROM vw_PiXL_Complete WHERE IsSynthetic = 1 ORDER BY ReceivedAt DESC;');
    console.log();

    await pool.closeAll();
    console.log('   Browsers closed. Done!');
}

main().catch(err => {
    console.error('Fatal:', err);
    process.exit(1);
});
