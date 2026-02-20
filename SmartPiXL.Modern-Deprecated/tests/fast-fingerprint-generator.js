/**
 * SmartPiXL High-Performance Fingerprint Generator
 * 
 * Uses REAL browsers but optimized for speed:
 * - Reuses browser instances (only creates contexts)
 * - Higher concurrency
 * - Shorter waits
 * - Persistent browser pool
 * 
 * Usage: node fast-fingerprint-generator.js [count]
 * 
 * M1 Data & Analytics - Internal Testing
 */

const { chromium, webkit, devices } = require('playwright');

// ============================================================================
// CONFIG - Conservative to keep server alive
// ============================================================================
const BROWSER_POOL_SIZE = 3;        // Fewer browsers = less load
const CONTEXTS_PER_BROWSER = 5;     // 15 total concurrency (gentler on server)
const PAGE_WAIT_MS = 1200;          // Give server breathing room
const RECYCLE_EVERY = 300;          // Restart browsers every 300 requests

// ============================================================================
// RANDOMIZATION POOLS
// ============================================================================
const DEVICE_NAMES = Object.keys(devices).filter(d => !d.includes('landscape'));
const TIMEZONES = [
    'America/New_York', 'America/Chicago', 'America/Denver', 'America/Los_Angeles',
    'America/Toronto', 'America/Mexico_City', 'America/Sao_Paulo', 'Europe/London',
    'Europe/Paris', 'Europe/Berlin', 'Europe/Rome', 'Europe/Madrid', 'Europe/Moscow',
    'Asia/Dubai', 'Asia/Kolkata', 'Asia/Singapore', 'Asia/Hong_Kong', 'Asia/Tokyo',
    'Asia/Seoul', 'Australia/Sydney', 'Pacific/Auckland', 'Africa/Johannesburg'
];
const LOCALES = [
    'en-US', 'en-GB', 'es-ES', 'es-MX', 'pt-BR', 'fr-FR', 'de-DE', 'it-IT',
    'nl-NL', 'pl-PL', 'ru-RU', 'tr-TR', 'ar-SA', 'hi-IN', 'zh-CN', 'ja-JP', 'ko-KR'
];
const SCREEN_SIZES = [
    { w: 1920, h: 1080 }, { w: 1366, h: 768 }, { w: 1536, h: 864 }, { w: 1440, h: 900 },
    { w: 2560, h: 1440 }, { w: 3840, h: 2160 }, { w: 1280, h: 720 }, { w: 1680, h: 1050 },
    { w: 390, h: 844 }, { w: 393, h: 852 }, { w: 412, h: 915 }, { w: 428, h: 926 },
    { w: 834, h: 1194 }, { w: 768, h: 1024 }, { w: 360, h: 640 }, { w: 375, h: 667 }
];
const PIXEL_RATIOS = [1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 3];

const pick = arr => arr[Math.floor(Math.random() * arr.length)];
const chance = pct => Math.random() * 100 < pct;

// ============================================================================
// BROWSER POOL (with recycling to prevent memory bloat)
// ============================================================================
class BrowserPool {
    constructor() {
        this.browsers = [];
        this.currentIndex = 0;
        this.requestCount = 0;
    }
    
    async initialize() {
        console.log(`   Launching ${BROWSER_POOL_SIZE} browser instances...`);
        
        // Mix of Chromium and WebKit (skip Firefox - too finicky with localhost SSL)
        for (let i = 0; i < BROWSER_POOL_SIZE; i++) {
            await this._launchBrowser(i);
        }
        
        console.log(`   Browser pool ready: ${this.browsers.length} instances`);
    }
    
    async _launchBrowser(index) {
        const type = index % 2 === 0 ? chromium : webkit;
        const browser = await type.launch({
            headless: true,
            args: ['--ignore-certificate-errors', '--disable-web-security']
        });
        this.browsers[index] = { browser, type, typeName: index % 2 === 0 ? 'chromium' : 'webkit', index };
    }
    
    getNext() {
        const entry = this.browsers[this.currentIndex];
        this.currentIndex = (this.currentIndex + 1) % this.browsers.length;
        this.requestCount++;
        return entry;
    }
    
    async recycleIfNeeded() {
        if (this.requestCount >= RECYCLE_EVERY) {
            console.log(`\n   [Recycling browsers to free memory...]`);
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
// FINGERPRINT RUNNER
// ============================================================================
async function runFingerprint(browserEntry) {
    const { browser } = browserEntry;
    let context, page;
    
    try {
        // Random config
        const useDevice = chance(40);
        let contextOptions = { ignoreHTTPSErrors: true };
        
        if (useDevice) {
            const deviceName = pick(DEVICE_NAMES);
            const device = devices[deviceName];
            if (device) Object.assign(contextOptions, device);
        } else {
            const screen = pick(SCREEN_SIZES);
            contextOptions.viewport = { width: screen.w, height: screen.h };
            contextOptions.screen = { width: screen.w, height: screen.h };
            contextOptions.deviceScaleFactor = pick(PIXEL_RATIOS);
            contextOptions.hasTouch = chance(30);
        }
        
        contextOptions.locale = pick(LOCALES);
        contextOptions.timezoneId = pick(TIMEZONES);
        contextOptions.colorScheme = chance(20) ? 'dark' : 'light';
        
        context = await browser.newContext(contextOptions);
        page = await context.newPage();
        
        await page.goto('https://localhost:6001/test', {
            waitUntil: 'domcontentloaded', // Faster than networkidle
            timeout: 10000
        });
        
        await page.waitForTimeout(PAGE_WAIT_MS);
        
        return true;
    } catch (e) {
        return false;
    } finally {
        if (page) await page.close().catch(() => {});
        if (context) await context.close().catch(() => {});
    }
}

// ============================================================================
// MAIN
// ============================================================================
async function main() {
    const targetCount = parseInt(process.argv[2]) || 5000;
    
    console.log('╔══════════════════════════════════════════════════════════════╗');
    console.log('║  SmartPiXL Fast Fingerprint Generator                        ║');
    console.log('║  100% Real Browser Execution - Optimized for Speed           ║');
    console.log('╚══════════════════════════════════════════════════════════════╝');
    console.log();
    console.log(`   Target: ${targetCount.toLocaleString()} fingerprints`);
    console.log(`   Concurrency: ${BROWSER_POOL_SIZE} browsers × ${CONTEXTS_PER_BROWSER} contexts = ${BROWSER_POOL_SIZE * CONTEXTS_PER_BROWSER} parallel`);
    console.log();
    
    const pool = new BrowserPool();
    await pool.initialize();
    
    let completed = 0;
    let successful = 0;
    let failed = 0;
    const startTime = Date.now();
    
    console.log();
    console.log('   Running...');
    
    // Process in waves
    const concurrency = BROWSER_POOL_SIZE * CONTEXTS_PER_BROWSER;
    let consecutiveFailures = 0;
    
    while (completed < targetCount) {
        // Recycle browsers periodically to free memory
        await pool.recycleIfNeeded();
        
        const batchSize = Math.min(concurrency, targetCount - completed);
        const promises = [];
        
        for (let i = 0; i < batchSize; i++) {
            const browserEntry = pool.getNext();
            promises.push(runFingerprint(browserEntry));
        }
        
        const results = await Promise.all(promises);
        
        let batchFails = 0;
        for (const success of results) {
            completed++;
            if (success) {
                successful++;
                consecutiveFailures = 0;
            } else {
                failed++;
                batchFails++;
            }
        }
        
        // If whole batch failed, server might be dead
        if (batchFails === batchSize) {
            consecutiveFailures++;
            if (consecutiveFailures >= 3) {
                console.log('\n\n   ⚠️  Server appears to be down! Stopping...');
                break;
            }
        }
        
        // Progress
        const elapsed = (Date.now() - startTime) / 1000;
        const rate = Math.round(completed / elapsed);
        const eta = Math.round((targetCount - completed) / rate);
        process.stdout.write(`\r   Progress: ${completed.toLocaleString()}/${targetCount.toLocaleString()} | ${rate}/sec | ETA: ${eta}s | ✓${successful} ✗${failed}    `);
    }
    
    const elapsed = ((Date.now() - startTime) / 1000).toFixed(1);
    const finalRate = Math.round(completed / (elapsed / 60));
    
    console.log();
    console.log();
    console.log('╔══════════════════════════════════════════════════════════════╗');
    console.log('║  COMPLETE                                                    ║');
    console.log('╚══════════════════════════════════════════════════════════════╝');
    console.log();
    console.log(`   ✓ Successful: ${successful.toLocaleString()}`);
    console.log(`   ✗ Failed: ${failed.toLocaleString()}`);
    console.log(`   Time: ${elapsed}s`);
    console.log(`   Rate: ${finalRate.toLocaleString()}/min`);
    console.log();
    
    await pool.closeAll();
    console.log('   Browsers closed. Check your database!');
    console.log();
}

main().catch(err => {
    console.error('Fatal:', err);
    process.exit(1);
});
