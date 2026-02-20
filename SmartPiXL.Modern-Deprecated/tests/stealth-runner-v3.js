/**
 * ╔══════════════════════════════════════════════════════════════════╗
 * ║  SmartPiXL RED TEAM — Stealth Synthetic Runner v3 (Pass 3)     ║
 * ║  Target: Bypass ALL SmartPiXL bot detection + cross-signal checks  ║
 * ╚══════════════════════════════════════════════════════════════════╝
 *
 * PASS 2 POST-MORTEM:
 *   A stray "})" after the mimeTypes property definition caused a
 *   SyntaxError that killed the ENTIRE stealth injection. No patches
 *   ran. Every hit leaked raw headless Chrome: BotScore=20
 *   (headless-no-chrome-obj + no-plugins + nav-webdriver).
 *
 * PASS 3 IMPROVEMENTS:
 *   1. CRITICAL: Rewrote plugin injection from scratch (no stray brackets)
 *   2. WeakMap-based toString spoofing (faster than Proxy, passes V-04)
 *   3. Multi-phase mouse simulation with high timing/speed CV (passes CS-09/10)
 *   4. Realistic scroll with injected tall content (passes CS-04)
 *   5. Connection API with RTT always set (passes CS-07)
 *   6. Non-round performance.memory values (passes CS-05)
 *   7. Win32-only profiles (avoids CS-01 font cross-platform detection)
 *   8. WebGL renderer override (avoids SwiftShader detection)
 *   9. JIT warmup for property getters (prevents V-04 timing artifacts)
 *  10. Proper Notification.permission / chrome.runtime / plugins consistency
 *
 * ATTACK SURFACE COVERED (PiXLScript.cs):
 *   Checks  1-25: All bot detection signals
 *   V-01/02/04/09/10: All evasion countermeasures
 *   CS-01 to CS-11:   All cross-signal consistency checks
 *
 * Usage:
 *   node stealth-runner-v3.js [count] [concurrency]
 *   node stealth-runner-v3.js 500 10
 */

const { chromium } = require('playwright');

// ============================================================================
// CONFIG
// ============================================================================
const TARGET_URL = 'https://smartpixl.info';
const BROWSER_POOL_SIZE = 3;
const CONTEXTS_PER_BROWSER = 5;
const RECYCLE_EVERY = 200;
const MAX_CONSECUTIVE_FAILURES = 15;
const STATS_INTERVAL_MS = 10000;

// ============================================================================
// PROFILE LIBRARY — Win32 only (avoids CS-01 font cross-platform detection)
// On this Windows server, Chrome reports Windows fonts. Claiming Mac/Linux
// platform would trigger "win-fonts-on-mac" (+15) or "win-fonts-on-linux" (+15).
// ============================================================================
const UA_PROFILES = [
    // Chrome 132 (newest)
    { platform: 'Win32', ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36', brands: [{brand:'Google Chrome',version:'132'},{brand:'Chromium',version:'132'},{brand:'Not_A Brand',version:'24'}], uaPlatform: 'Windows', uaPlatformVer: '15.0.0', arch: 'x86', bitness: '64', vendor: 'Google Inc.' },
    // Chrome 131
    { platform: 'Win32', ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36', brands: [{brand:'Google Chrome',version:'131'},{brand:'Chromium',version:'131'},{brand:'Not_A Brand',version:'24'}], uaPlatform: 'Windows', uaPlatformVer: '15.0.0', arch: 'x86', bitness: '64', vendor: 'Google Inc.' },
    // Chrome 130
    { platform: 'Win32', ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36', brands: [{brand:'Google Chrome',version:'130'},{brand:'Chromium',version:'130'},{brand:'Not_A Brand',version:'24'}], uaPlatform: 'Windows', uaPlatformVer: '10.0.0', arch: 'x86', bitness: '64', vendor: 'Google Inc.' },
    // Edge 131
    { platform: 'Win32', ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0', brands: [{brand:'Microsoft Edge',version:'131'},{brand:'Chromium',version:'131'},{brand:'Not_A Brand',version:'24'}], uaPlatform: 'Windows', uaPlatformVer: '15.0.0', arch: 'x86', bitness: '64', vendor: 'Google Inc.' },
    // Edge 132
    { platform: 'Win32', ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36 Edg/132.0.0.0', brands: [{brand:'Microsoft Edge',version:'132'},{brand:'Chromium',version:'132'},{brand:'Not_A Brand',version:'24'}], uaPlatform: 'Windows', uaPlatformVer: '15.0.0', arch: 'x86', bitness: '64', vendor: 'Google Inc.' },
    // Chrome 129 on Win10
    { platform: 'Win32', ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36', brands: [{brand:'Google Chrome',version:'129'},{brand:'Chromium',version:'129'},{brand:'Not_A Brand',version:'24'}], uaPlatform: 'Windows', uaPlatformVer: '10.0.0', arch: 'x86', bitness: '64', vendor: 'Google Inc.' },
    // Chrome 131 on Win11 with ARM (Surface Pro)
    { platform: 'Win32', ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36', brands: [{brand:'Google Chrome',version:'131'},{brand:'Chromium',version:'131'},{brand:'Not_A Brand',version:'24'}], uaPlatform: 'Windows', uaPlatformVer: '15.0.0', arch: 'arm', bitness: '64', vendor: 'Google Inc.' },
];

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
    { w: 1360, h:  768, taskbar: 40 },
    { w: 1504, h:  846, taskbar: 40 },
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
];

const LOCALES = [
    'en-US','en-US','en-US','en-US','en-US', // Weighted toward en-US
    'en-GB','en-AU','en-CA','es-ES','es-MX','pt-BR','fr-FR','de-DE',
    'it-IT','nl-NL','pl-PL','ja-JP','ko-KR','zh-CN','sv-SE','da-DK',
];

// Realistic GPU renderer strings for Windows machines
const GPU_RENDERERS = [
    { vendor: 'Google Inc. (NVIDIA)', renderer: 'ANGLE (NVIDIA, NVIDIA GeForce GTX 1060 6GB Direct3D11 vs_5_0 ps_5_0, D3D11)' },
    { vendor: 'Google Inc. (NVIDIA)', renderer: 'ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 Direct3D11 vs_5_0 ps_5_0, D3D11)' },
    { vendor: 'Google Inc. (NVIDIA)', renderer: 'ANGLE (NVIDIA, NVIDIA GeForce GTX 1650 Direct3D11 vs_5_0 ps_5_0, D3D11)' },
    { vendor: 'Google Inc. (Intel)', renderer: 'ANGLE (Intel, Intel(R) UHD Graphics 630 Direct3D11 vs_5_0 ps_5_0, D3D11)' },
    { vendor: 'Google Inc. (Intel)', renderer: 'ANGLE (Intel, Intel(R) UHD Graphics 770 Direct3D11 vs_5_0 ps_5_0, D3D11)' },
    { vendor: 'Google Inc. (Intel)', renderer: 'ANGLE (Intel, Intel(R) Iris(R) Xe Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)' },
    { vendor: 'Google Inc. (AMD)', renderer: 'ANGLE (AMD, AMD Radeon RX 580 Direct3D11 vs_5_0 ps_5_0, D3D11)' },
    { vendor: 'Google Inc. (AMD)', renderer: 'ANGLE (AMD, AMD Radeon(TM) Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)' },
];

const PIXEL_RATIOS = [1, 1, 1, 1.25, 1.5, 2, 2];
const CORES = [4, 4, 6, 8, 8, 8, 12, 16];
const MEMORY = [4, 8, 8, 8, 16, 16, 32];

// Realistic non-round heap sizes for performance.memory (avoids CS-05)
const HEAP_PROFILES = [
    { limit: 2172649472, total: 35291648,  used: 22478592  },
    { limit: 2172649472, total: 41943040,  used: 28311552  },
    { limit: 4294705152, total: 52428800,  used: 34603008  },
    { limit: 2172649472, total: 29360128,  used: 18874368  },
    { limit: 4294705152, total: 67108864,  used: 41517056  },
    { limit: 2172649472, total: 47185920,  used: 31457280  },
    { limit: 2172649472, total: 38797312,  used: 25690112  },
];

const pick = arr => arr[Math.floor(Math.random() * arr.length)];
const randInt = (min, max) => Math.floor(Math.random() * (max - min + 1)) + min;

// ============================================================================
// STEALTH INJECTION SCRIPT — The core red team payload
// Runs BEFORE any page JavaScript via addInitScript().
// ============================================================================
function buildStealthScript(config) {
    // JSON-encode complex values safely (avoids template literal escaping issues)
    const brandsJson = JSON.stringify(config.brands);
    const gpuRenderer = config.gpuRenderer.replace(/\\/g, '\\\\').replace(/'/g, "\\'");
    const gpuVendor = config.gpuVendor.replace(/\\/g, '\\\\').replace(/'/g, "\\'");
    const ua = config.ua.replace(/\\/g, '\\\\').replace(/'/g, "\\'");

    return `(function() {
    'use strict';

    // ================================================================
    // PHASE 1: toString FOUNDATION (WeakMap approach)
    // V-04 checks: Function.prototype.toString self-reference must 
    // contain "[native code]". Using WeakMap is faster than Proxy and
    // avoids "proxy-modified" detection.
    // ================================================================
    var _origTS = Function.prototype.toString;
    var _nativeMap = new WeakMap();

    var _fakeTS = function toString() {
        if (_nativeMap.has(this)) return _nativeMap.get(this);
        return _origTS.call(this);
    };
    _nativeMap.set(_fakeTS, 'function toString() { [native code] }');
    Function.prototype.toString = _fakeTS;

    // Helper: create a getter that looks native via toString
    function nativeGet(name, value) {
        var fn = function() { return value; };
        _nativeMap.set(fn, 'function get ' + name + '() { [native code] }');
        return fn;
    }

    // ================================================================
    // PHASE 2: NAVIGATOR PROPERTY OVERRIDES
    // Checks: #1 webdriver, #14 enumeration, #25 getter toString,
    //         #18 HeadlessChrome in UA, platform match
    // ================================================================

    // webdriver = false — non-enumerable avoids Check #14 navigator scan
    Object.defineProperty(Navigator.prototype, 'webdriver', {
        get: nativeGet('webdriver', false),
        configurable: true,
        enumerable: false
    });

    // Platform must match UA (CRITICAL: fixes ua-platform-mismatch evasion flag)
    Object.defineProperty(Navigator.prototype, 'platform', {
        get: nativeGet('platform', '${config.platform}'),
        configurable: true,
        enumerable: true
    });

    // User agent (removes HeadlessChrome, sets real-looking UA)
    Object.defineProperty(Navigator.prototype, 'userAgent', {
        get: nativeGet('userAgent', '${ua}'),
        configurable: true,
        enumerable: true
    });

    // Vendor must be Google Inc. for Chrome/Edge (CS-02 checks this)
    Object.defineProperty(Navigator.prototype, 'vendor', {
        get: nativeGet('vendor', '${config.vendor}'),
        configurable: true,
        enumerable: true
    });

    // Hardware specs
    Object.defineProperty(Navigator.prototype, 'hardwareConcurrency', {
        get: nativeGet('hardwareConcurrency', ${config.cores}),
        configurable: true,
        enumerable: true
    });
    Object.defineProperty(Navigator.prototype, 'deviceMemory', {
        get: nativeGet('deviceMemory', ${config.memory}),
        configurable: true,
        enumerable: true
    });

    // PDF viewer — all real Chrome has this
    Object.defineProperty(Navigator.prototype, 'pdfViewerEnabled', {
        get: nativeGet('pdfViewerEnabled', true),
        configurable: true,
        enumerable: true
    });

    // ================================================================
    // PHASE 3: window.chrome OBJECT (Checks #2, #20)
    // Headless Chrome lacks window.chrome → "headless-no-chrome-obj" (+8)
    // and !chrome.runtime → "chrome-no-runtime" (+3)
    // ================================================================
    if (!window.chrome) {
        window.chrome = {
            app: {
                isInstalled: false,
                getDetails: function() {},
                getIsInstalled: function() { return false; },
                installState: function(cb) { if (cb) cb('disabled'); },
                runningState: function() { return 'cannot_run'; }
            },
            runtime: {
                OnInstalledReason: { CHROME_UPDATE:'chrome_update', INSTALL:'install', SHARED_MODULE_UPDATE:'shared_module_update', UPDATE:'update' },
                OnRestartRequiredReason: { APP_UPDATE:'app_update', OS_UPDATE:'os_update', PERIODIC:'periodic' },
                PlatformArch: { ARM:'arm', ARM64:'arm64', MIPS:'mips', MIPS64:'mips64', X86_32:'x86-32', X86_64:'x86-64' },
                PlatformNaclArch: { ARM:'arm', MIPS:'mips', MIPS64:'mips64', X86_32:'x86-32', X86_64:'x86-64' },
                PlatformOs: { ANDROID:'android', CROS:'cros', LINUX:'linux', MAC:'mac', OPENBSD:'openbsd', WIN:'win' },
                RequestUpdateCheckStatus: { NO_UPDATE:'no_update', THROTTLED:'throttled', UPDATE_AVAILABLE:'update_available' },
                connect: function() {},
                sendMessage: function() {},
                id: undefined
            },
            csi: function() {},
            loadTimes: function() {}
        };
    } else if (window.chrome && !window.chrome.runtime) {
        // Chrome exists but no runtime — fix "chrome-no-runtime" (+3)
        window.chrome.runtime = {
            OnInstalledReason: { CHROME_UPDATE:'chrome_update', INSTALL:'install' },
            connect: function() {},
            sendMessage: function() {},
            id: undefined
        };
    }

    // ================================================================
    // PHASE 4: PLUGIN & MIME TYPE INJECTION (Checks #9, #11)
    // CAREFULLY rewritten from scratch — Pass 2 had a fatal stray "})"
    // that caused SyntaxError and killed the entire injection.
    // ================================================================
    var fakePluginDefs = [
        { name: 'PDF Viewer', filename: 'internal-pdf-viewer', desc: 'Portable Document Format', mimes: [{type:'application/pdf',suffixes:'pdf',desc:'Portable Document Format'}] },
        { name: 'Chrome PDF Viewer', filename: 'internal-pdf-viewer', desc: 'Portable Document Format', mimes: [{type:'application/x-google-chrome-pdf',suffixes:'pdf',desc:'Portable Document Format'}] },
        { name: 'Chromium PDF Viewer', filename: 'internal-pdf-viewer', desc: 'Portable Document Format', mimes: [{type:'application/pdf',suffixes:'pdf',desc:''}] },
        { name: 'Microsoft Edge PDF Viewer', filename: 'internal-pdf-viewer', desc: 'Portable Document Format', mimes: [{type:'application/pdf',suffixes:'pdf',desc:''}] },
        { name: 'WebKit built-in PDF', filename: 'internal-pdf-viewer', desc: 'Portable Document Format', mimes: [{type:'application/pdf',suffixes:'pdf',desc:''}] }
    ];

    var plugArr = Object.create(PluginArray.prototype);
    var mimeArr = Object.create(MimeTypeArray.prototype);
    var allMimes = [];

    for (var pi = 0; pi < fakePluginDefs.length; pi++) {
        var pd = fakePluginDefs[pi];
        var plug = Object.create(Plugin.prototype);
        Object.defineProperties(plug, {
            name:        { get: (function(n){ return function(){ return n; }; })(pd.name),        enumerable: true },
            filename:    { get: (function(n){ return function(){ return n; }; })(pd.filename),    enumerable: true },
            description: { get: (function(n){ return function(){ return n; }; })(pd.desc),        enumerable: true },
            length:      { get: (function(n){ return function(){ return n; }; })(pd.mimes.length), enumerable: true }
        });
        for (var mi = 0; mi < pd.mimes.length; mi++) {
            var md = pd.mimes[mi];
            var mime = Object.create(MimeType.prototype);
            Object.defineProperties(mime, {
                type:          { get: (function(v){ return function(){ return v; }; })(md.type),     enumerable: true },
                suffixes:      { get: (function(v){ return function(){ return v; }; })(md.suffixes), enumerable: true },
                description:   { get: (function(v){ return function(){ return v; }; })(md.desc),    enumerable: true },
                enabledPlugin: { get: (function(v){ return function(){ return v; }; })(plug),       enumerable: true }
            });
            Object.defineProperty(plug, mi, { get: (function(v){ return function(){ return v; }; })(mime), enumerable: false });
            allMimes.push(mime);
        }
        Object.defineProperty(plugArr, pi, { get: (function(v){ return function(){ return v; }; })(plug), enumerable: false });
    }

    Object.defineProperty(plugArr, 'length', { get: function(){ return fakePluginDefs.length; }, enumerable: true });
    Object.defineProperty(plugArr, 'refresh', { value: function(){} });
    Object.defineProperty(plugArr, 'item', { value: function(i){ return plugArr[i] || null; } });
    Object.defineProperty(plugArr, 'namedItem', { value: function(name){ for(var k=0;k<fakePluginDefs.length;k++) if(fakePluginDefs[k].name===name) return plugArr[k]; return null; } });

    for (var ai = 0; ai < allMimes.length; ai++) {
        Object.defineProperty(mimeArr, ai, { get: (function(v){ return function(){ return v; }; })(allMimes[ai]), enumerable: false });
    }
    Object.defineProperty(mimeArr, 'length', { get: function(){ return allMimes.length; }, enumerable: true });
    Object.defineProperty(mimeArr, 'item', { value: function(i){ return allMimes[i] || null; } });
    Object.defineProperty(mimeArr, 'namedItem', { value: function(type){ for(var k=0;k<allMimes.length;k++) if(allMimes[k].type===type) return allMimes[k]; return null; } });

    Object.defineProperty(Navigator.prototype, 'plugins', {
        get: function() { return plugArr; },
        configurable: true,
        enumerable: true
    });
    Object.defineProperty(Navigator.prototype, 'mimeTypes', {
        get: function() { return mimeArr; },
        configurable: true,
        enumerable: true
    });

    // ================================================================
    // PHASE 5: SCREEN & VIEWPORT (Checks #10, #13, #21)
    //   #10: zero screen → +8
    //   #13: outerWidth=0 && innerWidth>0 → +5
    //   #21: fullscreen-match (screen=outer && availH=H) → +2
    // ================================================================
    var _scW = ${config.screenW}, _scH = ${config.screenH};
    var _tb = ${config.taskbar};
    var _inW = ${config.viewportW}, _inH = ${config.viewportH};
    var _ouW = _inW + ${randInt(2, 18)};
    var _ouH = _inH + ${randInt(65, 115)};

    Object.defineProperty(screen, 'width',       { get: function(){ return _scW; } });
    Object.defineProperty(screen, 'height',      { get: function(){ return _scH; } });
    Object.defineProperty(screen, 'availWidth',  { get: function(){ return _scW; } });
    Object.defineProperty(screen, 'availHeight', { get: function(){ return _scH - _tb; } });
    Object.defineProperty(window, 'outerWidth',  { get: function(){ return _ouW; } });
    Object.defineProperty(window, 'outerHeight', { get: function(){ return _ouH; } });

    // ================================================================
    // PHASE 6: CLIENT HINTS / userAgentData (CS: clienthints-platform-mismatch)
    // ================================================================
    Object.defineProperty(Navigator.prototype, 'userAgentData', {
        get: function() {
            return {
                brands: ${brandsJson},
                mobile: false,
                platform: '${config.uaPlatform}',
                getHighEntropyValues: function(hints) {
                    return Promise.resolve({
                        architecture: '${config.arch}',
                        bitness: '${config.bitness}',
                        brands: ${brandsJson},
                        fullVersionList: ${brandsJson},
                        mobile: false,
                        model: '',
                        platform: '${config.uaPlatform}',
                        platformVersion: '${config.uaPlatformVer}',
                        wow64: false,
                        formFactor: []
                    });
                }
            };
        },
        configurable: true,
        enumerable: true
    });

    // ================================================================
    // PHASE 7: CONNECTION API (Check #22, CS-07)
    //   #22: No connection in Chrome → +3
    //   CS-07: 4g but no RTT → "connection-missing-rtt" (+5)
    // ================================================================
    var _connRTT = [50, 75, 100, 100, 150, 200, 250][Math.floor(Math.random() * 7)];
    var _connDL = [2.5, 5, 7.5, 10, 10, 15, 20][Math.floor(Math.random() * 7)];
    Object.defineProperty(Navigator.prototype, 'connection', {
        get: function() {
            return {
                effectiveType: '4g',
                downlink: _connDL,
                rtt: _connRTT,
                saveData: false,
                type: 'wifi',
                onchange: null
            };
        },
        configurable: true,
        enumerable: true
    });

    // ================================================================
    // PHASE 8: NOTIFICATION.permission (Checks #8, #19)
    // Bots often have denied/inconsistent permission states
    // ================================================================
    if (window.Notification) {
        Object.defineProperty(Notification, 'permission', {
            get: function() { return 'default'; },
            configurable: true
        });
    }

    // ================================================================
    // PHASE 9: PERFORMANCE.MEMORY (CS-05: round-heap-size)
    // Default Playwright has totalJSHeapSize=10000000 (exactly 10MB = round!)
    // We use realistic non-round values from real Chrome profiling.
    // ================================================================
    Object.defineProperty(performance, 'memory', {
        get: function() {
            return {
                jsHeapSizeLimit: ${config.heap.limit},
                totalJSHeapSize: ${config.heap.total},
                usedJSHeapSize:  ${config.heap.used}
            };
        },
        configurable: true
    });

    // ================================================================
    // PHASE 10: WEBGL RENDERER OVERRIDE (WebGL evasion detection)
    // Headless Chrome uses SwiftShader → webglEvasion=1 + 
    // "swiftshader-gpu" (+5 anomaly). Override getParameter for
    // UNMASKED_RENDERER/VENDOR to return realistic GPU strings.
    // ================================================================
    var _origGetContext = HTMLCanvasElement.prototype.getContext;
    HTMLCanvasElement.prototype.getContext = function(type) {
        var ctx = _origGetContext.apply(this, arguments);
        if (ctx && (type === 'webgl' || type === 'experimental-webgl' || type === 'webgl2')) {
            var _origGetParam = ctx.getParameter.bind(ctx);
            ctx.getParameter = function(pname) {
                if (pname === 0x9246) return '${gpuRenderer}';
                if (pname === 0x9245) return '${gpuVendor}';
                return _origGetParam(pname);
            };
        }
        return ctx;
    };

    // ================================================================
    // PHASE 11: JIT WARMUP
    // V-04 times navigator property access (1000 iterations).
    // Warm up V8's JIT so our getters are optimized before timing runs.
    // ================================================================
    for (var _w = 0; _w < 2000; _w++) {
        void navigator.userAgent;
        void navigator.webdriver;
        void navigator.platform;
        void navigator.hardwareConcurrency;
    }

})();`;
}

// ============================================================================
// MOUSE SIMULATION — Multi-phase with high timing/speed CV
// CS-09: timing CV must be > 0.3 (we target CV ~ 0.6-0.8)
// CS-10: speed CV must be > 0.2 (bezier curves + jitter give CV ~ 0.4-0.7)
// CS-11: 15-25 moves → "high" bucket
// ============================================================================
function buildMouseSimulation() {
    return `
    (async function() {
        var sleep = function(ms) { return new Promise(function(r) { setTimeout(r, ms); }); };

        // Make page scrollable by injecting tall content (fixes CS-04)
        var spacer = document.createElement('div');
        spacer.style.cssText = 'height:3000px;width:1px;position:absolute;top:0;left:-9999px;opacity:0;pointer-events:none;';
        document.body.appendChild(spacer);

        // --- PHASE 1: Enter from edge (3-4 fast moves) ---
        var startX = Math.random() < 0.5 ? 0 : window.innerWidth;
        var startY = Math.floor(Math.random() * window.innerHeight * 0.5) + window.innerHeight * 0.25;
        var midX = Math.floor(window.innerWidth * (0.3 + Math.random() * 0.4));
        var midY = Math.floor(window.innerHeight * (0.2 + Math.random() * 0.4));
        var entrySteps = 3 + Math.floor(Math.random() * 2);
        for (var e = 0; e <= entrySteps; e++) {
            var t = e / entrySteps;
            var ex = startX + (midX - startX) * t;
            var ey = startY + (midY - startY) * t + (Math.random() - 0.5) * 15;
            document.dispatchEvent(new MouseEvent('mousemove', { clientX: Math.round(ex), clientY: Math.round(ey), bubbles: true }));
            await sleep(30 + Math.floor(Math.random() * 50));
        }

        // --- PHASE 2: Explore with bezier curves (8-14 moves, variable timing) ---
        var cx = midX, cy = midY;
        var exploreSteps = 8 + Math.floor(Math.random() * 7);
        for (var s = 0; s < exploreSteps; s++) {
            // Random target with bezier influence
            var tx = Math.floor(Math.random() * window.innerWidth * 0.7) + window.innerWidth * 0.15;
            var ty = Math.floor(Math.random() * window.innerHeight * 0.6) + window.innerHeight * 0.15;
            // Intermediate point for curve
            var bx = cx + (tx - cx) * 0.5 + (Math.random() - 0.5) * 80;
            var by = cy + (ty - cy) * 0.5 + (Math.random() - 0.5) * 60;
            // Interpolate along quadratic bezier
            var t2 = 0.3 + Math.random() * 0.4;
            var nx = (1-t2)*(1-t2)*cx + 2*(1-t2)*t2*bx + t2*t2*tx;
            var ny = (1-t2)*(1-t2)*cy + 2*(1-t2)*t2*by + t2*t2*ty;
            document.dispatchEvent(new MouseEvent('mousemove', { clientX: Math.round(nx), clientY: Math.round(ny), bubbles: true }));
            cx = nx; cy = ny;
            // CRITICAL: Highly variable timing (40-200ms) for high CV
            await sleep(40 + Math.floor(Math.random() * 160));
        }

        // --- PHASE 3: Settle / fine movement (2-4 small moves) ---
        var settleSteps = 2 + Math.floor(Math.random() * 3);
        for (var f = 0; f < settleSteps; f++) {
            cx += (Math.random() - 0.5) * 30;
            cy += (Math.random() - 0.5) * 20;
            document.dispatchEvent(new MouseEvent('mousemove', { clientX: Math.round(cx), clientY: Math.round(cy), bubbles: true }));
            await sleep(60 + Math.floor(Math.random() * 90));
        }

        // --- SCROLL (realistic: scrollBy actually moves scrollY) ---
        var scrollSteps = 2 + Math.floor(Math.random() * 3);
        for (var sc = 0; sc < scrollSteps; sc++) {
            window.scrollBy(0, 40 + Math.floor(Math.random() * 100));
            await sleep(50 + Math.floor(Math.random() * 80));
        }
    })();`;
}

// ============================================================================
// CONFIG GENERATOR — Produces fully consistent profiles
// ============================================================================
function generateStealthConfig() {
    const profile = pick(UA_PROFILES);
    const screen = pick(DESKTOP_SCREENS);
    const dpr = pick(PIXEL_RATIOS);
    const gpu = pick(GPU_RENDERERS);
    const heap = pick(HEAP_PROFILES);

    // Viewport smaller than screen (browser chrome)
    let vpW = screen.w - randInt(0, 30);
    let vpH = screen.h - screen.taskbar - randInt(70, 130);

    // Avoid the exact default viewport sizes the detector checks (#17)
    if (vpW === 1280 || vpW === 800) vpW += randInt(3, 47);
    if (vpH === 720 || vpH === 600) vpH += randInt(3, 47);

    return {
        ...profile,
        screenW: screen.w,
        screenH: screen.h,
        taskbar: screen.taskbar,
        viewportW: vpW,
        viewportH: vpH,
        dpr,
        cores: pick(CORES),
        memory: pick(MEMORY),
        timezone: pick(TIMEZONES),
        locale: pick(LOCALES),
        colorScheme: Math.random() < 0.25 ? 'dark' : 'light',
        gpuRenderer: gpu.renderer,
        gpuVendor: gpu.vendor,
        heap,
    };
}

// ============================================================================
// BROWSER POOL
// ============================================================================
class BrowserPool {
    constructor() { this.browsers = []; this.idx = 0; this.reqCount = 0; }

    async initialize() {
        console.log(`   Launching ${BROWSER_POOL_SIZE} Chromium instances...`);
        for (let i = 0; i < BROWSER_POOL_SIZE; i++) {
            const browser = await chromium.launch({
                headless: true,
                args: [
                    '--disable-blink-features=AutomationControlled',
                    '--ignore-certificate-errors',
                    '--no-sandbox',
                    '--disable-web-security',
                    '--disable-infobars',
                    '--disable-dev-shm-usage', // Stability in containers
                    '--disable-gpu-sandbox',
                    // NOTE: Removed --use-gl=angle --use-angle=d3d11 (crashes headless).
                    // WebGL renderer is overridden in Phase 10 via getParameter hook instead.
                ]
            });
            this.browsers.push(browser);
        }
        console.log(`   Pool ready (${this.browsers.length} browsers)`);
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
            this.reqCount = 0;
            await this.initialize();
        }
    }

    async closeAll() {
        await Promise.all(this.browsers.map(b => b.close().catch(() => {})));
    }
}

// ============================================================================
// STEALTH HIT — The core attack function
// ============================================================================
async function runStealthHit(browser) {
    let context, page;
    try {
        const config = generateStealthConfig();

        context = await browser.newContext({
            ignoreHTTPSErrors: true,
            viewport: { width: config.viewportW, height: config.viewportH },
            screen: { width: config.screenW, height: config.screenH },
            deviceScaleFactor: config.dpr,
            locale: config.locale,
            timezoneId: config.timezone,
            colorScheme: config.colorScheme,
            userAgent: config.ua,
        });

        page = await context.newPage();

        // Inject stealth patches BEFORE any page script
        await page.addInitScript(buildStealthScript(config));

        // Route intercept: tag as synthetic (fair play!)
        let pixelFired = false;
        await page.route('**/*SMART.GIF*', async (route) => {
            const url = route.request().url();
            const sep = url.includes('?') ? '&' : '?';
            pixelFired = true;
            await route.continue({ url: url + sep + 'synthetic=1&stealthTest=1&pass=3' });
        });

        // Navigate to target
        await page.goto(TARGET_URL, { waitUntil: 'domcontentloaded', timeout: 15000 });

        // Simulate a realistic page dwell: human sees page, moves mouse before JS loads
        // This also sets performance.timing-derived load time to non-zero (fixes instant-page-load)
        await page.waitForTimeout(randInt(300, 700));

        // Start mouse simulation FIRST — it runs in the page context as async IIFE
        // (dispatches events over ~2-3 seconds, doesn't block)
        const mousePromise = page.evaluate(buildMouseSimulation());

        // Inject tracking script WHILE mouse events are already dispatching
        // The tracking script's 500ms behavioral window will capture mouse events
        await page.evaluate((scriptUrl) => {
            return new Promise(function(resolve) {
                var script = document.createElement('script');
                script.src = scriptUrl;
                script.onload = function() { setTimeout(resolve, 80); };
                script.onerror = function() { resolve(); };
                document.head.appendChild(script);
            });
        }, TARGET_URL + '/js/12345/1.js');

        // Wait for mouse simulation to finish + pixel fire time
        await mousePromise;
        await page.waitForTimeout(1200);

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
        if (s < 60) return s + 's';
        if (s < 3600) return Math.floor(s/60) + 'm';
        return Math.floor(s/3600) + 'h ' + Math.floor((s%3600)/60) + 'm';
    }
    progress() {
        const pct = ((this.completed / this.target) * 100).toFixed(1);
        return `   ${this.completed.toLocaleString()}/${this.target.toLocaleString()} (${pct}%) | ${this.rpm}/min | ETA: ${this.eta} | OK:${this.successful} PX:${this.pixelsFired} ERR:${this.failed}`;
    }
    detailed() {
        const pr = this.completed > 0 ? ((this.pixelsFired/this.completed)*100).toFixed(1) : '0.0';
        return `\n   +-- Pass 3 Stealth Stats -------------------------+\n` +
            `   | Elapsed: ${this.elapsed.toFixed(0)}s | Rate: ${this.rpm}/min\n` +
            `   | Success: ${this.successful} | Failed: ${this.failed}\n` +
            `   | Pixel fire rate: ${pr}%\n` +
            `   | Remaining: ${(this.target - this.completed).toLocaleString()} | ETA: ${this.eta}\n` +
            `   +---------------------------------------------------+`;
    }
}

// ============================================================================
// MAIN
// ============================================================================
async function main() {
    const target = parseInt(process.argv[2]) || 500;
    const concurrency = parseInt(process.argv[3]) || BROWSER_POOL_SIZE * CONTEXTS_PER_BROWSER;

    console.log();
    console.log('================================================================');
    console.log('  RED TEAM — Stealth Synthetic Runner v3 (Pass 3)');
    console.log('  Goal: BotScore=0 across ALL detection layers');
    console.log('================================================================');
    console.log();
    console.log('  Pass 2 post-mortem: SyntaxError in injection killed all patches');
    console.log('  Pass 3 fixes: Rewritten injection + 11 new evasion techniques');
    console.log();
    console.log(`  Target:      ${target.toLocaleString()} stealth hits`);
    console.log(`  Concurrency: ${concurrency}`);
    console.log(`  Endpoint:    ${TARGET_URL}`);
    console.log(`  Profiles:    Win32 only (avoids font cross-platform detection)`);
    console.log(`  Flags:       synthetic=1&stealthTest=1&pass=3`);
    console.log();
    console.log('  Stealth patches applied per hit:');
    console.log('    [1]  WeakMap toString spoofing (passes V-04)');
    console.log('    [2]  webdriver=false (non-enum, native toString)');
    console.log('    [3]  window.chrome + chrome.runtime injection');
    console.log('    [4]  5-plugin + 5-mime injection (FIXED syntax)');
    console.log('    [5]  Screen/viewport with realistic offsets');
    console.log('    [6]  Client Hints (userAgentData) consistency');
    console.log('    [7]  Connection API with RTT (passes CS-07)');
    console.log('    [8]  Notification.permission = "default"');
    console.log('    [9]  Performance.memory non-round values (passes CS-05)');
    console.log('    [10] WebGL GPU renderer override (no SwiftShader)');
    console.log('    [11] JIT warmup for getter timing (passes V-04)');
    console.log('    [12] Multi-phase mouse sim (CV>0.5, passes CS-09/10)');
    console.log('    [13] Scroll + injected content (passes CS-04)');
    console.log();

    const pool = new BrowserPool();
    await pool.initialize();
    const stats = new StatsTracker(target);
    console.log('\n   Running stealth attack...\n');

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
            if (!r.success && stats.completed <= 3) console.log('   ERR: ' + r.error);
        }
        process.stdout.write('\r' + stats.progress() + '    ');
        if (Date.now() - stats.lastReport >= STATS_INTERVAL_MS) {
            stats.lastReport = Date.now();
            console.log(stats.detailed());
        }
    }

    console.log('\n\n');
    console.log('================================================================');
    console.log('  PASS 3 STEALTH RUN COMPLETE');
    console.log('================================================================');
    console.log(`  Total: ${stats.completed.toLocaleString()} | OK: ${stats.successful.toLocaleString()} | PX: ${stats.pixelsFired.toLocaleString()}`);
    console.log(`  Rate: ${stats.rpm}/min | Duration: ${(stats.elapsed/60).toFixed(1)}min`);
    console.log();
    console.log('  Verify results:');
    console.log("    sqlcmd -S localhost -d SmartPixl -E -Q \"SELECT BotScore, ISNULL(BotSignalsList,'(clean)') AS Signals, COUNT(*) AS Cnt FROM vw_PiXL_Complete WHERE IsSynthetic = 1 AND QueryString LIKE '%pass=3%' GROUP BY BotScore, BotSignalsList ORDER BY Cnt DESC\" -W");
    console.log();
    console.log('  Cross-signal check:');
    console.log("    sqlcmd -S localhost -d SmartPixl -E -Q \"SELECT TOP 10 BotScore, AnomalyScore, ISNULL(CrossSignalFlags,'(none)') AS XSignal, ISNULL(EvasionSignalsV2,'(none)') AS Evasion, ISNULL(StealthPluginSignals,'(none)') AS Stealth, MouseEntropy, MoveTimingCV, MoveSpeedCV FROM vw_PiXL_Complete WHERE IsSynthetic = 1 AND QueryString LIKE '%pass=3%' ORDER BY ReceivedAt DESC\" -W -s\"|\"");
    console.log();

    await pool.closeAll();
}

main().catch(err => { console.error('Fatal:', err); process.exit(1); });
