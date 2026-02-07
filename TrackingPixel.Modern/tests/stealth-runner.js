/**
 * SmartPiXL RED TEAM Stealth Synthetic Runner
 * 
 * Goal: Generate synthetic hits that BYPASS the Tier5 bot detection.
 * Every signal in the bot detector has a specific countermeasure here.
 * 
 * ATTACK SURFACE (Tier5Script.cs bot detection checks):
 *   1.  navigator.webdriver === true                    → CDP override to false
 *   2.  !window.chrome && UA says Chrome                → Inject full chrome object
 *   2b. UA length < 30 or known fake                    → Use real-length UAs
 *   3.  window._phantom                                 → N/A (not Phantom)
 *   4.  window.__nightmare                              → N/A (not Nightmare)
 *   5.  document.__selenium_*                            → N/A (not Selenium)
 *   6.  navigator.languages.length === 0                → Playwright sets locale
 *   7.  window.cdc_* (CDP leak)                         → Not present in Playwright
 *   8.  Permission inconsistencies                      → Patch Notification.permission
 *   9.  plugins=0 but mimeTypes>0                       → Inject matching plugins+mimes
 *  10.  screen 0x0                                      → Real screen values set
 *  11.  plugins=0 && !Firefox                           → Inject fake plugins
 *  12.  domAutomation / domAutomationController         → N/A (not IE)
 *  13.  outerWidth===0 && innerWidth>0                  → Patch outerWidth
 *  14.  navigator key enumeration matching /webdriver/  → Delete webdriver from enum
 *  15.  permissions.query fn not native                 → Don't patch it
 *  16.  __playwright / __pw_manual                      → Not present in prod mode
 *  17.  Default viewport 1280x720 / 800x600            → Randomize viewport
 *  18.  HeadlessChrome in UA                            → Use headed-style UA
 *  19.  Notification permission inconsistency           → Patch consistently
 *  20.  chrome && !chrome.runtime                       → Inject chrome.runtime
 *  21.  screen === outerWidth && availHeight === height  → Set realistic offsets
 *  22.  No connection API in Chrome                     → Inject navigator.connection
 *  23.  Script execution time (behavioral)              → Artificial delay
 *  24.  eval.toString not native                        → Don't patch eval
 *  25.  webdriver getter is non-native                  → Use CDP, not defineProperty
 * 
 * EVASION DETECTOR COUNTERMEASURES:
 *   - tor-likely: Inject window.chrome to break the Tor heuristic
 *   - ua-platform-mismatch: Match platform to UA (CRITICAL fix from old runner)
 *   - webrtc-blocked: Inject RTCPeerConnection stub
 *   - touch-mismatch: Only set touch on mobile UAs
 *   - mobile-ua-desktop-screen: Only use mobile UA with mobile screens
 * 
 * BEHAVIORAL:
 *   - Simulate realistic mouse movements with curved paths
 *   - Simulate scroll events
 *   - Wait realistic time before pixel fires
 * 
 * Usage:
 *   node stealth-runner.js [count] [concurrency]
 *   node stealth-runner.js 1000 10
 */

const { chromium, webkit, devices } = require('playwright');

// ============================================================================
// CONFIG
// ============================================================================
const TARGET_URL = 'https://smartpixl.info';
const PIXEL_PATTERN = /SMART\.GIF/i;
const BROWSER_POOL_SIZE = 3;
const CONTEXTS_PER_BROWSER = 5;
const RECYCLE_EVERY = 300;
const MAX_CONSECUTIVE_FAILURES = 10;
const STATS_INTERVAL_MS = 15000;

// ============================================================================
// REALISTIC USER AGENT STRINGS (no "HeadlessChrome", proper versions)
// These must match the platform we claim in navigator.platform
// ============================================================================
const UA_PROFILES = {
    // Windows + Chrome (most common real combo)
    win_chrome: [
        { platform: 'Win32', ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36', brands: [{brand:'Google Chrome',version:'131'},{brand:'Chromium',version:'131'},{brand:'Not_A Brand',version:'24'}], uaPlatform: 'Windows', uaPlatformVer: '15.0.0', arch: 'x86', bitness: '64' },
        { platform: 'Win32', ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36', brands: [{brand:'Google Chrome',version:'130'},{brand:'Chromium',version:'130'},{brand:'Not_A Brand',version:'24'}], uaPlatform: 'Windows', uaPlatformVer: '15.0.0', arch: 'x86', bitness: '64' },
        { platform: 'Win32', ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36', brands: [{brand:'Google Chrome',version:'129'},{brand:'Chromium',version:'129'},{brand:'Not_A Brand',version:'24'}], uaPlatform: 'Windows', uaPlatformVer: '10.0.0', arch: 'x86', bitness: '64' },
    ],
    // Windows + Edge
    win_edge: [
        { platform: 'Win32', ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0', brands: [{brand:'Microsoft Edge',version:'131'},{brand:'Chromium',version:'131'},{brand:'Not_A Brand',version:'24'}], uaPlatform: 'Windows', uaPlatformVer: '15.0.0', arch: 'x86', bitness: '64' },
    ],
    // macOS + Chrome  
    mac_chrome: [
        { platform: 'MacIntel', ua: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36', brands: [{brand:'Google Chrome',version:'131'},{brand:'Chromium',version:'131'},{brand:'Not_A Brand',version:'24'}], uaPlatform: 'macOS', uaPlatformVer: '13.0.0', arch: 'arm', bitness: '64' },
        { platform: 'MacIntel', ua: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36', brands: [{brand:'Google Chrome',version:'130'},{brand:'Chromium',version:'130'},{brand:'Not_A Brand',version:'24'}], uaPlatform: 'macOS', uaPlatformVer: '14.0.0', arch: 'arm', bitness: '64' },
    ],
    // macOS + Safari (WebKit) — no Client Hints, no chrome object
    mac_safari: [
        { platform: 'MacIntel', ua: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Safari/605.1.15', brands: null, uaPlatform: null, isSafari: true },
        { platform: 'MacIntel', ua: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15', brands: null, uaPlatform: null, isSafari: true },
    ],
    // Linux + Chrome
    linux_chrome: [
        { platform: 'Linux x86_64', ua: 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36', brands: [{brand:'Google Chrome',version:'131'},{brand:'Chromium',version:'131'},{brand:'Not_A Brand',version:'24'}], uaPlatform: 'Linux', uaPlatformVer: '6.5.0', arch: 'x86', bitness: '64' },
    ],
};

const DESKTOP_SCREENS = [
    { w: 1920, h: 1080, taskbar: 40 },
    { w: 2560, h: 1440, taskbar: 40 },
    { w: 1366, h:  768, taskbar: 40 },
    { w: 1536, h:  864, taskbar: 40 },
    { w: 1440, h:  900, taskbar: 35 },
    { w: 1680, h: 1050, taskbar: 40 },
    { w: 3840, h: 2160, taskbar: 48 },
    { w: 1280, h: 1024, taskbar: 40 },
    { w: 1920, h: 1200, taskbar: 40 },
    { w: 1600, h:  900, taskbar: 40 },
];

const TIMEZONES = [
    'America/New_York','America/Chicago','America/Denver','America/Los_Angeles',
    'America/Toronto','America/Vancouver','America/Mexico_City',
    'Europe/London','Europe/Paris','Europe/Berlin','Europe/Rome','Europe/Madrid',
    'Europe/Amsterdam','Europe/Stockholm','Europe/Moscow','Europe/Istanbul',
    'Europe/Warsaw','Europe/Prague','Europe/Bucharest','Europe/Athens',
    'Asia/Dubai','Asia/Kolkata','Asia/Bangkok','Asia/Singapore',
    'Asia/Shanghai','Asia/Tokyo','Asia/Seoul','Asia/Taipei',
    'Australia/Sydney','Australia/Melbourne','Pacific/Auckland',
    'Africa/Cairo','Africa/Johannesburg',
];

const LOCALES = [
    'en-US','en-US','en-US','en-US', // Weighted toward en-US like real traffic
    'en-GB','en-AU','en-CA',
    'es-ES','es-MX','pt-BR','fr-FR','de-DE',
    'it-IT','nl-NL','pl-PL','ru-RU','ja-JP','ko-KR','zh-CN',
    'sv-SE','da-DK','fi-FI','nb-NO','tr-TR',
];

const PIXEL_RATIOS = [1, 1, 1.25, 1.5, 2, 2, 2]; // weighted: 1x and 2x most common
const CORES = [4, 4, 8, 8, 8, 12, 16]; // weighted realistic
const MEMORY = [4, 8, 8, 8, 16, 16, 32]; // weighted realistic

const pick = arr => arr[Math.floor(Math.random() * arr.length)];
const randInt = (min, max) => Math.floor(Math.random() * (max - min + 1)) + min;

// ============================================================================
// STEALTH INJECTION SCRIPT
// This runs via page.addInitScript() BEFORE any page JavaScript executes.
// It patches the browser environment to look like a real user.
// ============================================================================
function buildStealthScript(profile) {
    return `
    // =====================================================
    // RED TEAM STEALTH PATCHES
    // Runs before any page script. Patches must survive
    // toString() checks and property descriptor inspection.
    // =====================================================
    
    // --- 1. Kill navigator.webdriver (Check #1, #14, #25) ---
    // Use Object.defineProperty on the prototype so the getter
    // returns false AND looks native via toString()
    const origDesc = Object.getOwnPropertyDescriptor(Navigator.prototype, 'webdriver');
    if (origDesc) {
        Object.defineProperty(Navigator.prototype, 'webdriver', {
            get: new Proxy(origDesc.get || function() { return false; }, {
                apply: () => false,
            }),
            configurable: true,
            enumerable: true,
        });
        // Make the getter's toString look native
        const nativeToString = Function.prototype.toString;
        const fakeNative = new Map();
        const origGet = Object.getOwnPropertyDescriptor(Navigator.prototype, 'webdriver').get;
        fakeNative.set(origGet, 'function get webdriver() { [native code] }');
        
        const originalToString = Function.prototype.toString;
        Function.prototype.toString = new Proxy(originalToString, {
            apply(target, thisArg, args) {
                if (fakeNative.has(thisArg)) return fakeNative.get(thisArg);
                return Reflect.apply(target, thisArg, args);
            }
        });
        // Also make our toString proxy look native
        fakeNative.set(Function.prototype.toString, 'function toString() { [native code] }');
    }
    
    // --- 2. Remove 'webdriver' from navigator enumeration (Check #14) ---
    // The bot detector enumerates navigator looking for 'webdriver' key
    // We can't delete it, but we can make it non-enumerable
    try {
        Object.defineProperty(Navigator.prototype, 'webdriver', {
            ...Object.getOwnPropertyDescriptor(Navigator.prototype, 'webdriver'),
            enumerable: false,
        });
    } catch(e) {}
    
    ${!profile.isSafari ? `
    // --- 3. Inject window.chrome object (Check #2, #20, tor-likely fix) ---
    if (!window.chrome) {
        window.chrome = {
            app: { isInstalled: false, getDetails: function(){}, getIsInstalled: function(){ return false; }, installState: function(cb){ if(cb) cb('disabled'); } },
            runtime: {
                OnInstalledReason: { CHROME_UPDATE: 'chrome_update', INSTALL: 'install', SHARED_MODULE_UPDATE: 'shared_module_update', UPDATE: 'update' },
                OnRestartRequiredReason: { APP_UPDATE: 'app_update', OS_UPDATE: 'os_update', PERIODIC: 'periodic' },
                PlatformArch: { ARM: 'arm', ARM64: 'arm64', MIPS: 'mips', MIPS64: 'mips64', X86_32: 'x86-32', X86_64: 'x86-64' },
                PlatformNaclArch: { ARM: 'arm', MIPS: 'mips', MIPS64: 'mips64', X86_32: 'x86-32', X86_64: 'x86-64' },
                PlatformOs: { ANDROID: 'android', CROS: 'cros', LINUX: 'linux', MAC: 'mac', OPENBSD: 'openbsd', WIN: 'win' },
                RequestUpdateCheckStatus: { NO_UPDATE: 'no_update', THROTTLED: 'throttled', UPDATE_AVAILABLE: 'update_available' },
                connect: function() {},
                sendMessage: function() {},
                id: undefined,
            },
            csi: function(){},
            loadTimes: function(){},
        };
    }
    ` : '// Safari: no chrome object expected'}
    
    // --- 4. Inject realistic plugins (Check #9, #11) ---
    ${!profile.isSafari ? `
    const fakePlugins = [
        { name: 'PDF Viewer', filename: 'internal-pdf-viewer', description: 'Portable Document Format', mimeTypes: [{type:'application/pdf',suffixes:'pdf',description:'Portable Document Format'}] },
        { name: 'Chrome PDF Viewer', filename: 'internal-pdf-viewer', description: 'Portable Document Format', mimeTypes: [{type:'application/x-google-chrome-pdf',suffixes:'pdf',description:'Portable Document Format'}] },
        { name: 'Chromium PDF Viewer', filename: 'internal-pdf-viewer', description: 'Portable Document Format', mimeTypes: [{type:'application/pdf',suffixes:'pdf',description:''}] },
        { name: 'Microsoft Edge PDF Viewer', filename: 'internal-pdf-viewer', description: 'Portable Document Format', mimeTypes: [{type:'application/pdf',suffixes:'pdf',description:''}] },
        { name: 'WebKit built-in PDF', filename: 'internal-pdf-viewer', description: 'Portable Document Format', mimeTypes: [{type:'application/pdf',suffixes:'pdf',description:''}] },
    ];` : `
    // Safari: WebKit built-in PDF is the only real plugin
    const fakePlugins = [
        { name: 'WebKit built-in PDF', filename: 'internal-pdf-viewer', description: 'Portable Document Format', mimeTypes: [{type:'application/pdf',suffixes:'pdf',description:'Portable Document Format'}] },
    ];`}
    // Inject plugins for ALL profiles (even Safari has WebKit built-in PDF)
    const pluginArray = Object.create(PluginArray.prototype);
    const mimeTypeArray = Object.create(MimeTypeArray.prototype);
    const allMimes = [];
    
    fakePlugins.forEach((p, i) => {
        const plugin = Object.create(Plugin.prototype);
        Object.defineProperties(plugin, {
            name: { get: () => p.name, enumerable: true },
            filename: { get: () => p.filename, enumerable: true },
            description: { get: () => p.description, enumerable: true },
            length: { get: () => p.mimeTypes.length, enumerable: true },
        });
        p.mimeTypes.forEach((m, j) => {
            const mime = Object.create(MimeType.prototype);
            Object.defineProperties(mime, {
                type: { get: () => m.type, enumerable: true },
                suffixes: { get: () => m.suffixes, enumerable: true },
                description: { get: () => m.description, enumerable: true },
                enabledPlugin: { get: () => plugin, enumerable: true },
            });
            Object.defineProperty(plugin, j, { get: () => mime, enumerable: false });
            allMimes.push(mime);
        });
        Object.defineProperty(pluginArray, i, { get: () => plugin, enumerable: false });
    });
    Object.defineProperty(pluginArray, 'length', { get: () => fakePlugins.length, enumerable: true });
    Object.defineProperty(pluginArray, 'refresh', { value: () => {} });
    Object.defineProperty(pluginArray, 'item', { value: (i) => pluginArray[i] || null });
    Object.defineProperty(pluginArray, 'namedItem', { value: (name) => { for(let i=0;i<fakePlugins.length;i++) if(fakePlugins[i].name===name) return pluginArray[i]; return null; } });
    allMimes.forEach((m, i) => {
        Object.defineProperty(mimeTypeArray, i, { get: () => m, enumerable: false });
    });
    Object.defineProperty(mimeTypeArray, 'length', { get: () => allMimes.length, enumerable: true });
    Object.defineProperty(mimeTypeArray, 'item', { value: (i) => allMimes[i] || null });
    Object.defineProperty(mimeTypeArray, 'namedItem', { value: (name) => allMimes.find(m => m.type === name) || null });
    
    Object.defineProperty(Navigator.prototype, 'plugins', {
        get: () => pluginArray,
        configurable: true, enumerable: true,
    });
    Object.defineProperty(Navigator.prototype, 'mimeTypes', {
        get: () => mimeTypeArray,
        configurable: true, enumerable: true,
    });
    });
    
    // --- 5. Patch platform to match UA (CRITICAL - fixes ua-platform-mismatch) ---
    Object.defineProperty(Navigator.prototype, 'platform', {
        get: () => '${profile.platform}',
        configurable: true, enumerable: true,
    });
    
    // --- 6. Override userAgent (Check #18 - removes HeadlessChrome) ---
    Object.defineProperty(Navigator.prototype, 'userAgent', {
        get: () => '${profile.ua}',
        configurable: true, enumerable: true,
    });
    
    // --- 7. Set realistic hardware values ---
    Object.defineProperty(Navigator.prototype, 'hardwareConcurrency', {
        get: () => ${profile.cores},
        configurable: true, enumerable: true,
    });
    Object.defineProperty(Navigator.prototype, 'deviceMemory', {
        get: () => ${profile.memory},
        configurable: true, enumerable: true,
    });
    
    // --- 8. Patch outerWidth/Height (Check #13, #21 - fullscreen-match) ---
    // Real browsers: outerWidth > innerWidth (browser chrome takes space)
    // availHeight < screen.height (taskbar takes space)
    const screenW = ${profile.screenW};
    const screenH = ${profile.screenH};
    const taskbar = ${profile.taskbar};
    const innerW = ${profile.viewportW};
    const innerH = ${profile.viewportH};
    
    Object.defineProperty(window, 'outerWidth', { get: () => innerW + ${randInt(0, 20)} });
    Object.defineProperty(window, 'outerHeight', { get: () => innerH + ${randInt(60, 120)} });
    Object.defineProperty(screen, 'availWidth', { get: () => screenW });
    Object.defineProperty(screen, 'availHeight', { get: () => screenH - taskbar });
    Object.defineProperty(screen, 'width', { get: () => screenW });
    Object.defineProperty(screen, 'height', { get: () => screenH });
    
    // --- 9. Client Hints (Check: clienthints-platform-mismatch) ---
    ${profile.brands ? `
    Object.defineProperty(Navigator.prototype, 'userAgentData', {
        get: () => ({
            brands: ${JSON.stringify(profile.brands)},
            mobile: false,
            platform: '${profile.uaPlatform}',
            getHighEntropyValues: (hints) => Promise.resolve({
                architecture: '${profile.arch || 'x86'}',
                bitness: '${profile.bitness || '64'}',
                brands: ${JSON.stringify(profile.brands)},
                fullVersionList: ${JSON.stringify(profile.brands)},
                mobile: false,
                model: '',
                platform: '${profile.uaPlatform}',
                platformVersion: '${profile.uaPlatformVer || '15.0.0'}',
                wow64: false,
                formFactor: [],
            }),
        }),
        configurable: true, enumerable: true,
    });
    ` : `
    // Safari/Firefox: no userAgentData
    Object.defineProperty(Navigator.prototype, 'userAgentData', {
        get: () => undefined,
        configurable: true, enumerable: true,
    });
    `}
    
    ${!profile.isSafari ? `
    // --- 10. Inject navigator.connection (Check #22) ---
    if (!navigator.connection) {
        const connTypes = ['4g', '4g', '4g', '3g'];
        const rttValues = [50, 100, 150, 200, 250];
        const dlValues = [1.5, 2.5, 5, 10, 15, 20];
        Object.defineProperty(Navigator.prototype, 'connection', {
            get: () => ({
                effectiveType: connTypes[Math.floor(Math.random() * connTypes.length)],
                downlink: dlValues[Math.floor(Math.random() * dlValues.length)],
                rtt: rttValues[Math.floor(Math.random() * rttValues.length)],
                saveData: false,
                type: 'wifi',
            }),
            configurable: true, enumerable: true,
        });
    }
    ` : '// Safari: no connection API expected'}
    
    // --- 11. Patch Notification.permission consistently (Check #8, #19) ---
    if (window.Notification) {
        Object.defineProperty(Notification, 'permission', {
            get: () => 'default',
            configurable: true,
        });
    }
    
    // --- 12. Ensure pdfViewerEnabled is true (real Chrome always has this) ---
    Object.defineProperty(Navigator.prototype, 'pdfViewerEnabled', {
        get: () => true,
        configurable: true, enumerable: true,
    });
    `;
}

// ============================================================================
// MOUSE MOVEMENT SIMULATOR - Generates realistic curved paths
// ============================================================================
function buildMouseSimulation() {
    // Returns JS code that creates believable mouse movements + scroll
    return `
    async function simulateHumanBehavior() {
        const sleep = ms => new Promise(r => setTimeout(r, ms));
        
        // Simulate mouse entering the page from a random edge
        const startX = Math.random() < 0.5 ? 0 : window.innerWidth;
        const startY = Math.floor(Math.random() * window.innerHeight);
        const endX = Math.floor(Math.random() * window.innerWidth * 0.8) + window.innerWidth * 0.1;
        const endY = Math.floor(Math.random() * window.innerHeight * 0.6) + window.innerHeight * 0.2;
        
        // Bezier curve mouse movement (not straight lines!)
        // FAST: 8-12 steps at 5-15ms gaps fits within the 500ms behavioral window
        const steps = 8 + Math.floor(Math.random() * 5);
        const cx1 = startX + (endX - startX) * (0.2 + Math.random() * 0.3);
        const cy1 = startY + (endY - startY) * (Math.random() * 0.5 - 0.25);
        const cx2 = startX + (endX - startX) * (0.5 + Math.random() * 0.3);
        const cy2 = endY + (startY - endY) * (Math.random() * 0.3 - 0.15);
        
        for (let i = 0; i <= steps; i++) {
            const t = i / steps;
            const u = 1 - t;
            const x = u*u*u*startX + 3*u*u*t*cx1 + 3*u*t*t*cx2 + t*t*t*endX;
            const y = u*u*u*startY + 3*u*u*t*cy1 + 3*u*t*t*cy2 + t*t*t*endY;
            
            document.dispatchEvent(new MouseEvent('mousemove', {
                clientX: Math.round(x),
                clientY: Math.round(y),
                bubbles: true,
            }));
            
            await sleep(5 + Math.floor(Math.random() * 10));
        }
        
        // Quick scroll burst (2-3 events)
        const scrollCount = 2 + Math.floor(Math.random() * 2);
        for (let s = 0; s < scrollCount; s++) {
            window.scrollBy(0, 30 + Math.floor(Math.random() * 80));
            document.dispatchEvent(new Event('scroll', { bubbles: true }));
            await sleep(20 + Math.floor(Math.random() * 30));
        }
    }
    await simulateHumanBehavior();
    `;
}

// ============================================================================
// CONFIG GENERATOR - Produces consistent, non-contradictory profiles
// ============================================================================
function generateStealthConfig() {
    // Pick a platform category (weighted toward Windows like real traffic)
    const platformWeights = [
        { key: 'win_chrome', weight: 40 },
        { key: 'win_edge', weight: 15 },
        { key: 'mac_chrome', weight: 20 },
        { key: 'mac_safari', weight: 15 },
        { key: 'linux_chrome', weight: 10 },
    ];
    const totalWeight = platformWeights.reduce((s, p) => s + p.weight, 0);
    let r = Math.random() * totalWeight;
    let platformKey = 'win_chrome';
    for (const p of platformWeights) {
        r -= p.weight;
        if (r <= 0) { platformKey = p.key; break; }
    }
    
    const uaProfile = pick(UA_PROFILES[platformKey]);
    const screen = pick(DESKTOP_SCREENS);
    const dpr = pick(PIXEL_RATIOS);
    
    // Viewport is always smaller than screen (browser chrome eats space)
    const vpW = screen.w - randInt(0, 40);
    const vpH = screen.h - screen.taskbar - randInt(60, 140);
    
    // CRITICAL: Avoid the two "default viewport" sizes the detector checks
    const finalVpW = (vpW === 1280 || vpW === 800) ? vpW + randInt(1, 50) : vpW;
    const finalVpH = (vpH === 720 || vpH === 600) ? vpH + randInt(1, 50) : vpH;
    
    return {
        ...uaProfile,
        screenW: screen.w,
        screenH: screen.h,
        taskbar: screen.taskbar,
        viewportW: finalVpW,
        viewportH: finalVpH,
        dpr,
        cores: pick(CORES),
        memory: pick(MEMORY),
        timezone: pick(TIMEZONES),
        locale: pick(LOCALES),
        colorScheme: Math.random() < 0.25 ? 'dark' : 'light',
        // Engine choice: Safari profiles use WebKit, everything else Chromium
        engine: uaProfile.isSafari ? 'webkit' : 'chromium',
    };
}

// ============================================================================
// BROWSER POOL
// ============================================================================
class BrowserPool {
    constructor() { this.browsers = []; this.idx = 0; this.reqCount = 0; }

    async initialize() {
        console.log(`   Launching ${BROWSER_POOL_SIZE} browser instances...`);
        for (let i = 0; i < BROWSER_POOL_SIZE; i++) await this._launch(i);
        console.log(`   Pool ready: ${this.browsers.map(b => b.name).join(', ')}`);
    }

    async _launch(index) {
        // Use chromium for most (even webkit profiles get chromium engine since
        // WebKit on Windows doesn't support all the patches we need)
        const browser = await chromium.launch({
            headless: true,
            args: [
                '--disable-blink-features=AutomationControlled',
                '--ignore-certificate-errors',
                '--no-sandbox',
                '--disable-web-security',
                '--disable-infobars',
            ]
        });
        this.browsers[index] = { browser, name: 'chromium', index };
    }

    getNext() {
        const b = this.browsers[this.idx];
        this.idx = (this.idx + 1) % this.browsers.length;
        this.reqCount++;
        return b;
    }

    async recycleIfNeeded() {
        if (this.reqCount >= RECYCLE_EVERY) {
            process.stdout.write('\n   [Recycling browsers...]\n');
            await this.closeAll();
            this.browsers = [];
            await this.initialize();
            this.reqCount = 0;
        }
    }

    async closeAll() {
        await Promise.all(this.browsers.map(b => b.browser.close().catch(() => {})));
    }
}

// ============================================================================
// STEALTH HIT - The core red team function
// ============================================================================
async function runStealthHit(browserEntry) {
    const { browser } = browserEntry;
    let context, page;

    try {
        const config = generateStealthConfig();

        // Create context with matching viewport/screen
        context = await browser.newContext({
            ignoreHTTPSErrors: true,
            viewport: { width: config.viewportW, height: config.viewportH },
            screen: { width: config.screenW, height: config.screenH },
            deviceScaleFactor: config.dpr,
            locale: config.locale,
            timezoneId: config.timezone,
            colorScheme: config.colorScheme,
            userAgent: config.ua, // Set UA at context level too
        });

        page = await context.newPage();

        // CRITICAL: Inject stealth patches BEFORE any page script runs
        await page.addInitScript(buildStealthScript(config));

        // Route intercept for synthetic tagging
        let pixelFired = false;
        await page.route('**/*SMART.GIF*', async (route) => {
            const url = route.request().url();
            const sep = url.includes('?') ? '&' : '?';
            pixelFired = true;
            await route.continue({ url: url + sep + 'synthetic=1&stealthTest=1' });
        });

        // Navigate
        await page.goto(TARGET_URL, { waitUntil: 'domcontentloaded', timeout: 15000 });

        // NOTE: We simulate AFTER script inject, because the Tier5 script 
        // registers its mousemove/scroll listeners at load time.

        // Small random delay (like a user seeing the page before script loads)
        await page.waitForTimeout(randInt(200, 600));

        // Inject the Tier 5 tracking script
        // The Tier5 script registers mousemove/scroll listeners immediately,
        // then waits ~500ms before firing the pixel. We need to:
        //   1. Load the script
        //   2. Immediately start mouse simulation (within the 500ms window)
        //   3. Let the pixel fire with our behavioral data captured
        await page.evaluate((scriptUrl) => {
            return new Promise((resolve) => {
                const script = document.createElement('script');
                script.src = scriptUrl;
                script.onload = () => setTimeout(resolve, 100); // Resolve fast — listeners are now active
                script.onerror = () => resolve();
                document.head.appendChild(script);
            });
        }, TARGET_URL + '/js/12345/1.js');

        // Simulate mouse/scroll DURING the 500ms behavioral window
        await page.evaluate(`(async () => { ${buildMouseSimulation()} })()`);

        // Wait for pixel fire (the Tier5 500ms timer + async collectors)
        await page.waitForTimeout(2000);

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
        this.completed = 0; this.successful = 0; this.failed = 0;
        this.pixelsFired = 0; this.startTime = Date.now();
        this.lastReport = Date.now(); this.consecutiveFailures = 0;
    }
    record(result) {
        this.completed++;
        if (result.success) {
            this.successful++; this.consecutiveFailures = 0;
            if (result.pixelFired) this.pixelsFired++;
        } else { this.failed++; this.consecutiveFailures++; }
    }
    get elapsed() { return (Date.now() - this.startTime) / 1000; }
    get rpm() { return this.elapsed > 0 ? Math.round(this.completed / this.elapsed * 60) : 0; }
    get eta() {
        const r = this.elapsed > 0 ? this.completed / this.elapsed : 0;
        const s = r > 0 ? Math.round((this.target - this.completed) / r) : 0;
        if (s < 60) return `${s}s`;
        if (s < 3600) return `${Math.floor(s/60)}m`;
        return `${Math.floor(s/3600)}h ${Math.floor((s%3600)/60)}m`;
    }
    progress() {
        const pct = ((this.completed / this.target) * 100).toFixed(1);
        return `   ${this.completed.toLocaleString()}/${this.target.toLocaleString()} (${pct}%) | ${this.rpm}/min | ETA: ${this.eta} | OK:${this.successful} PX:${this.pixelsFired} ERR:${this.failed}`;
    }
    detailed() {
        const pr = this.completed > 0 ? ((this.pixelsFired/this.completed)*100).toFixed(1) : '0.0';
        return `\n   ┌── Stealth Stats ─────────────────────────────┐\n` +
            `   │ Elapsed: ${this.elapsed.toFixed(0)}s | Rate: ${this.rpm}/min\n` +
            `   │ Success: ${this.successful} | Failed: ${this.failed}\n` +
            `   │ Pixel fire rate: ${pr}%\n` +
            `   │ Remaining: ${(this.target - this.completed).toLocaleString()} | ETA: ${this.eta}\n` +
            `   └───────────────────────────────────────────────┘`;
    }
}

// ============================================================================
// MAIN
// ============================================================================
async function main() {
    const target = parseInt(process.argv[2]) || 100000;
    const concurrency = parseInt(process.argv[3]) || BROWSER_POOL_SIZE * CONTEXTS_PER_BROWSER;

    console.log('╔══════════════════════════════════════════════════════════════╗');
    console.log('║  RED TEAM Stealth Synthetic Runner                           ║');
    console.log('║  Goal: Bypass Tier5 bot detection with clean synthetic hits  ║');
    console.log('╚══════════════════════════════════════════════════════════════╝');
    console.log();
    console.log(`   Target:      ${target.toLocaleString()} stealth hits`);
    console.log(`   Concurrency: ${concurrency}`);
    console.log(`   Endpoint:    ${TARGET_URL}`);
    console.log(`   Patches:     webdriver, chrome obj, plugins, UA, platform,`);
    console.log(`                connection, notifications, client hints,`);
    console.log(`                screen/viewport offsets, mouse sim, scroll sim`);
    console.log();

    const pool = new BrowserPool();
    await pool.initialize();
    const stats = new StatsTracker(target);
    console.log('\n   Running stealth hits...');

    while (stats.completed < target) {
        await pool.recycleIfNeeded();
        if (stats.consecutiveFailures >= MAX_CONSECUTIVE_FAILURES) {
            console.log(`\n   ABORT: ${MAX_CONSECUTIVE_FAILURES} consecutive failures`);
            break;
        }
        const batch = Math.min(concurrency, target - stats.completed);
        const promises = [];
        for (let i = 0; i < batch; i++) promises.push(runStealthHit(pool.getNext()));
        const results = await Promise.all(promises);
        for (const r of results) {
            stats.record(r);
            if (!r.success && stats.completed <= 5) console.log('\n   ERR: ' + r.error);
        }
        process.stdout.write('\r' + stats.progress() + '    ');
        if (Date.now() - stats.lastReport >= STATS_INTERVAL_MS) {
            stats.lastReport = Date.now();
            console.log(stats.detailed());
        }
    }

    console.log('\n');
    console.log('╔══════════════════════════════════════════════════════════════╗');
    console.log('║  STEALTH RUN COMPLETE                                        ║');
    console.log('╚══════════════════════════════════════════════════════════════╝');
    console.log(`   Total: ${stats.completed.toLocaleString()} | OK: ${stats.successful.toLocaleString()} | PX: ${stats.pixelsFired.toLocaleString()}`);
    console.log(`   Rate: ${stats.rpm}/min | Duration: ${(stats.elapsed/60).toFixed(1)}min`);
    console.log('\n   Verify stealth results:');
    console.log("     SELECT BotScore, BotSignalsList, COUNT(*) FROM vw_PiXL_Complete");
    console.log("     WHERE IsSynthetic = 1 AND QueryString LIKE '%stealthTest=1%'");
    console.log("     GROUP BY BotScore, BotSignalsList ORDER BY COUNT(*) DESC;");
    console.log();

    await pool.closeAll();
}

main().catch(err => { console.error('Fatal:', err); process.exit(1); });
