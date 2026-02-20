/**
 * SmartPiXL Weekend Runner
 * 
 * Runs fingerprint generation continuously with self-healing:
 * - Monitors DB count to detect stalls
 * - Restarts server if no progress
 * - Logs everything to file
 * - Runs indefinitely until manually stopped
 * 
 * Usage: node weekend-runner.js
 * Stop:  Ctrl+C (graceful shutdown)
 * 
 * M1 Data & Analytics - Weekend Testing
 */

const { chromium, webkit, devices } = require('playwright');
const { execSync, spawn } = require('child_process');
const fs = require('fs');
const path = require('path');

// ============================================================================
// CONFIG
// ============================================================================
const CONFIG = {
    // Fingerprint generation
    BROWSER_POOL_SIZE: 3,
    CONTEXTS_PER_BROWSER: 5,
    PAGE_WAIT_MS: 1200,
    BATCH_SIZE: 500,              // Fingerprints per batch before health check
    
    // Health monitoring
    HEALTH_CHECK_INTERVAL: 60000, // Check DB every 60 seconds during batch
    MIN_PROGRESS_PER_BATCH: 100,  // Minimum records expected per batch
    MAX_STALL_COUNT: 3,           // Restart server after this many stalls
    
    // Server
    SERVER_PORT: 6001,
    SERVER_START_WAIT_MS: 8000,
    SERVER_PATH: 'c:\\Users\\Brian\\source\\repos\\SmartPixl\\TrackingPixel.Modern',
    
    // Logging
    LOG_FILE: path.join(__dirname, 'weekend-runner.log'),
    STATS_FILE: path.join(__dirname, 'weekend-stats.json'),
};

// ============================================================================
// LOGGING
// ============================================================================
function log(message, level = 'INFO') {
    const timestamp = new Date().toISOString();
    const line = `[${timestamp}] [${level}] ${message}`;
    console.log(line);
    fs.appendFileSync(CONFIG.LOG_FILE, line + '\n');
}

function logStats(stats) {
    fs.writeFileSync(CONFIG.STATS_FILE, JSON.stringify(stats, null, 2));
}

// ============================================================================
// DATABASE MONITORING
// ============================================================================
function getDbCount() {
    try {
        const result = execSync(
            'sqlcmd -S localhost -d SmartPixl -E -Q "SELECT COUNT(*) FROM PiXL_Test" -h -1',
            { encoding: 'utf8', timeout: 10000 }
        );
        const count = parseInt(result.trim().split('\n')[0].trim());
        return isNaN(count) ? -1 : count;
    } catch (e) {
        log(`DB query failed: ${e.message}`, 'ERROR');
        return -1;
    }
}

// ============================================================================
// SERVER MANAGEMENT (using Start-Process for reliable Windows restart)
// ============================================================================
let serverProcess = null;

function isServerRunning() {
    try {
        // Try to actually hit the server instead of checking port
        execSync(`powershell -Command "Invoke-WebRequest -Uri http://localhost:6000 -UseBasicParsing -TimeoutSec 5 | Out-Null"`, 
            { encoding: 'utf8', timeout: 10000, stdio: 'pipe' });
        return true;
    } catch {
        return false;
    }
}

async function startServer() {
    log('Starting server...');
    
    // Kill any existing dotnet processes
    try {
        execSync('powershell -Command "Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force"', 
            { timeout: 15000, stdio: 'pipe' });
    } catch {}
    
    await sleep(3000);
    
    // Use Start-Process with -WindowStyle Hidden for a truly detached server
    try {
        execSync(`powershell -Command "Start-Process powershell -ArgumentList '-NoProfile', '-Command', 'cd ''${CONFIG.SERVER_PATH}''; dotnet run' -WindowStyle Hidden"`, 
            { timeout: 15000, stdio: 'pipe' });
    } catch (e) {
        log(`Start-Process failed: ${e.message}`, 'ERROR');
    }
    
    // Wait for server to be ready
    log(`Waiting ${CONFIG.SERVER_START_WAIT_MS}ms for server startup...`);
    await sleep(CONFIG.SERVER_START_WAIT_MS);
    
    // Verify server is actually responding
    for (let i = 0; i < 5; i++) {
        if (isServerRunning()) {
            log('Server started successfully');
            return true;
        }
        log(`Server not ready yet, retrying (${i+1}/5)...`);
        await sleep(3000);
    }
    
    log('Server failed to start after retries!', 'ERROR');
    return false;
}

async function restartServer() {
    log('Restarting server...', 'WARN');
    return await startServer();
}

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
const sleep = ms => new Promise(r => setTimeout(r, ms));

// ============================================================================
// BROWSER POOL
// ============================================================================
class BrowserPool {
    constructor() {
        this.browsers = [];
        this.currentIndex = 0;
    }
    
    async initialize() {
        log(`Launching ${CONFIG.BROWSER_POOL_SIZE} browsers...`);
        
        for (let i = 0; i < CONFIG.BROWSER_POOL_SIZE; i++) {
            await this._launchBrowser(i);
        }
        
        log(`Browser pool ready: ${this.browsers.length} instances`);
    }
    
    async _launchBrowser(index) {
        const type = index % 2 === 0 ? chromium : webkit;
        const browser = await type.launch({
            headless: true,
            args: ['--ignore-certificate-errors', '--disable-web-security']
        });
        this.browsers[index] = { browser, index };
    }
    
    getNext() {
        const entry = this.browsers[this.currentIndex];
        this.currentIndex = (this.currentIndex + 1) % this.browsers.length;
        return entry;
    }
    
    async recycle() {
        log('Recycling browser pool...');
        await this.closeAll();
        this.browsers = [];
        this.currentIndex = 0;
        await this.initialize();
    }
    
    async closeAll() {
        await Promise.all(this.browsers.map(b => b?.browser?.close().catch(() => {})));
    }
}

// ============================================================================
// FINGERPRINT RUNNER
// ============================================================================
async function runFingerprint(browserEntry) {
    const { browser } = browserEntry;
    let context, page;
    
    try {
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
        
        await page.goto(`https://localhost:${CONFIG.SERVER_PORT}/test`, {
            waitUntil: 'domcontentloaded',
            timeout: 15000
        });
        
        await page.waitForTimeout(CONFIG.PAGE_WAIT_MS);
        
        return true;
    } catch {
        return false;
    } finally {
        if (page) await page.close().catch(() => {});
        if (context) await context.close().catch(() => {});
    }
}

// ============================================================================
// BATCH RUNNER
// ============================================================================
async function runBatch(pool, batchSize) {
    let completed = 0;
    let successful = 0;
    let failed = 0;
    const concurrency = CONFIG.BROWSER_POOL_SIZE * CONFIG.CONTEXTS_PER_BROWSER;
    
    while (completed < batchSize) {
        const waveSize = Math.min(concurrency, batchSize - completed);
        const promises = [];
        
        for (let i = 0; i < waveSize; i++) {
            promises.push(runFingerprint(pool.getNext()));
        }
        
        const results = await Promise.all(promises);
        
        for (const success of results) {
            completed++;
            if (success) successful++;
            else failed++;
        }
        
        // Progress indicator
        const pct = Math.round(completed / batchSize * 100);
        process.stdout.write(`\r   Batch progress: ${completed}/${batchSize} (${pct}%) | ✓${successful} ✗${failed}    `);
    }
    
    console.log(); // New line after progress
    return { completed, successful, failed };
}

// ============================================================================
// MAIN LOOP
// ============================================================================
async function main() {
    console.log('╔══════════════════════════════════════════════════════════════╗');
    console.log('║  SmartPiXL Weekend Runner                                    ║');
    console.log('║  Continuous fingerprint generation with self-healing         ║');
    console.log('║  Press Ctrl+C to stop gracefully                             ║');
    console.log('╚══════════════════════════════════════════════════════════════╝');
    console.log();
    
    // Initialize stats
    const stats = {
        startTime: new Date().toISOString(),
        totalFingerprints: 0,
        totalSuccessful: 0,
        totalFailed: 0,
        batchesCompleted: 0,
        serverRestarts: 0,
        browserRecycles: 0,
        lastDbCount: 0,
        lastUpdate: new Date().toISOString()
    };
    
    log('Weekend Runner starting...');
    
    // Initial DB count
    stats.lastDbCount = getDbCount();
    log(`Starting DB count: ${stats.lastDbCount.toLocaleString()}`);
    
    // Ensure server is running
    if (!isServerRunning()) {
        const started = await startServer();
        if (!started) {
            log('Cannot start server. Exiting.', 'FATAL');
            process.exit(1);
        }
    } else {
        log('Server already running');
    }
    
    // Initialize browser pool
    const pool = new BrowserPool();
    await pool.initialize();
    
    let stallCount = 0;
    let batchNumber = 0;
    
    // Graceful shutdown handler
    let shutdownRequested = false;
    process.on('SIGINT', async () => {
        if (shutdownRequested) {
            log('Force quit!', 'WARN');
            process.exit(1);
        }
        shutdownRequested = true;
        log('Shutdown requested. Finishing current batch...', 'WARN');
    });
    
    // Main loop
    while (!shutdownRequested) {
        batchNumber++;
        const batchStartTime = Date.now();
        const dbCountBefore = getDbCount();
        
        log(`Starting batch #${batchNumber} | DB: ${dbCountBefore.toLocaleString()}`);
        
        try {
            // Run batch
            const result = await runBatch(pool, CONFIG.BATCH_SIZE);
            
            stats.totalFingerprints += result.completed;
            stats.totalSuccessful += result.successful;
            stats.totalFailed += result.failed;
            stats.batchesCompleted++;
            
            // Check DB progress
            await sleep(2000); // Let writes flush
            const dbCountAfter = getDbCount();
            const dbProgress = dbCountAfter - dbCountBefore;
            
            const batchTime = ((Date.now() - batchStartTime) / 1000).toFixed(1);
            const rate = Math.round(result.completed / (batchTime / 60));
            
            log(`Batch #${batchNumber} complete: ${result.successful}/${result.completed} success | DB +${dbProgress} | ${rate}/min | ${batchTime}s`);
            
            stats.lastDbCount = dbCountAfter;
            stats.lastUpdate = new Date().toISOString();
            logStats(stats);
            
            // Health check
            if (dbProgress < CONFIG.MIN_PROGRESS_PER_BATCH) {
                stallCount++;
                log(`⚠️ Low DB progress: ${dbProgress} records (stall ${stallCount}/${CONFIG.MAX_STALL_COUNT})`, 'WARN');
                
                if (stallCount >= CONFIG.MAX_STALL_COUNT) {
                    log('Too many stalls! Restarting everything...', 'WARN');
                    
                    await pool.closeAll();
                    const restarted = await restartServer();
                    stats.serverRestarts++;
                    
                    if (restarted) {
                        await pool.initialize();
                        stallCount = 0;
                    } else {
                        log('Server restart failed! Waiting 30s...', 'ERROR');
                        await sleep(30000);
                    }
                }
            } else {
                stallCount = 0; // Reset on good progress
            }
            
            // Recycle browsers every 10 batches
            if (batchNumber % 10 === 0) {
                await pool.recycle();
                stats.browserRecycles++;
            }
            
        } catch (err) {
            log(`Batch error: ${err.message}`, 'ERROR');
            await sleep(5000);
        }
        
        // Small pause between batches
        if (!shutdownRequested) {
            await sleep(2000);
        }
    }
    
    // Cleanup
    log('Shutting down...');
    await pool.closeAll();
    
    // Final stats
    const runtime = ((Date.now() - new Date(stats.startTime).getTime()) / 1000 / 60 / 60).toFixed(2);
    log(`╔════════════════════════════════════════╗`);
    log(`║  WEEKEND RUNNER COMPLETE               ║`);
    log(`╚════════════════════════════════════════╝`);
    log(`Runtime: ${runtime} hours`);
    log(`Total fingerprints: ${stats.totalFingerprints.toLocaleString()}`);
    log(`Successful: ${stats.totalSuccessful.toLocaleString()}`);
    log(`Failed: ${stats.totalFailed.toLocaleString()}`);
    log(`Batches: ${stats.batchesCompleted}`);
    log(`Server restarts: ${stats.serverRestarts}`);
    log(`Final DB count: ${getDbCount().toLocaleString()}`);
    
    logStats(stats);
    log('Goodbye!');
}

main().catch(err => {
    log(`Fatal error: ${err.message}`, 'FATAL');
    console.error(err);
    process.exit(1);
});
