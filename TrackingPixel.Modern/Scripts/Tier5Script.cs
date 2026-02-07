namespace TrackingPixel.Scripts;

/// <summary>
/// Contains the Tier 5 JavaScript template for maximum data collection.
/// Stored as a constant string - allocated once at startup.
/// </summary>
public static class Tier5Script
{
    /// <summary>
    /// JavaScript template with {{PIXEL_URL}} placeholder for pixel URL.
    /// Use Template.Replace("{{PIXEL_URL}}", pixelUrl) to generate per-request.
    /// </summary>
    public const string Template = @"
// SmartPiXL Tracking Script - Tier 5 (Maximum Data Collection)
(function() {
    try {
        var s = screen;
        var n = navigator;
        var w = window;
        var d = new Date();
        var perf = w.performance || {};
        var data = {};
        
        // ============================================
        // SAFE ACCESSOR - Handles privacy extension Proxy traps
        // Privacy extensions (JShelter, Trace, etc.) wrap navigator in Proxy
        // which can throw on property access. This helper catches those.
        // MUST be defined early before any navigator property access!
        // ============================================
        var safeGet = function(obj, prop, fallback) {
            try {
                var val = obj[prop];
                // If it's a function, try to call it
                if (typeof val === 'function') {
                    try { return val.call(obj); } catch(e) { return fallback; }
                }
                return val !== undefined ? val : fallback;
            } catch(e) {
                data._proxyBlocked = (data._proxyBlocked || '') + prop + ',';
                return fallback;
            }
        };
        
        // Connection object - use safeGet since this is navigator property
        var c = safeGet(n, 'connection', {}) || {};
        
        // ============================================
        // SHARED HASH FUNCTION (optimized - single allocation)
        // ============================================
        var hashStr = (function() {
            var h = 0;
            return function(str) {
                h = 0;
                for (var i = 0, len = str.length; i < len; i++) {
                    h = ((h << 5) - h) + str.charCodeAt(i);
                    h = h & h; // Convert to 32-bit integer
                }
                return Math.abs(h).toString(16);
            };
        })();
        
        // ============================================
        // CANVAS FINGERPRINT
        // ============================================
        var canvasResult = (function() {
            try {
                var canvas = document.createElement('canvas');
                canvas.width = 280; canvas.height = 60;
                var ctx = canvas.getContext('2d');
                ctx.textBaseline = 'alphabetic';
                ctx.fillStyle = '#f60';
                ctx.fillRect(10, 10, 100, 40);
                ctx.fillStyle = '#069';
                ctx.font = '15px Arial';
                ctx.fillText('SmartPiXL <canvas> 1.0', 2, 15);
                ctx.fillStyle = 'rgba(102, 204, 0, 0.7)';
                ctx.font = '18px Times New Roman';
                ctx.fillText('Fingerprint!', 4, 45);
                ctx.strokeStyle = 'rgb(120,186,176)';
                ctx.arc(80, 30, 20, 0, Math.PI * 2);
                ctx.stroke();
                var dataUrl = canvas.toDataURL();
                var hash = hashStr(dataUrl);
                
                // Evasion detection: Check if canvas returns suspiciously uniform data
                var imgData = ctx.getImageData(0, 0, 280, 60).data;
                var variance = 0, sum = 0, samples = 100;
                for (var i = 0; i < samples * 4; i += 4) {
                    sum += imgData[i] + imgData[i+1] + imgData[i+2];
                }
                var mean = sum / (samples * 3);
                for (var j = 0; j < samples * 4; j += 4) {
                    var diff = ((imgData[j] + imgData[j+1] + imgData[j+2]) / 3) - mean;
                    variance += diff * diff;
                }
                variance = variance / samples;
                
                // If variance is 0, canvas was likely blocked/spoofed
                var evasion = (variance < 1 || dataUrl.length < 1000) ? 1 : 0;
                
                return { fp: hash, evasion: evasion };
            } catch(e) { return { fp: '', evasion: 0 }; }
        })();
        data.canvasFP = canvasResult.fp;
        data.canvasEvasion = canvasResult.evasion;
        
        // ============================================
        // V-01: CANVAS NOISE INJECTION DETECTION
        // Draws identical content on 2 canvases. Noise injection extensions
        // (Canvas Blocker, JShelter, Trace) add random per-canvas noise,
        // making identical draws produce different hashes.
        // ============================================
        data.canvasConsistency = (function() {
            try {
                var c1 = document.createElement('canvas');
                var c2 = document.createElement('canvas');
                c1.width = c2.width = 100; c1.height = c2.height = 50;
                var x1 = c1.getContext('2d'), x2 = c2.getContext('2d');
                x1.fillStyle = '#ff6600'; x1.fillRect(0, 0, 50, 50);
                x1.fillStyle = '#000'; x1.font = '12px Arial'; x1.fillText('Test1', 5, 25);
                x2.fillStyle = '#ff6600'; x2.fillRect(0, 0, 50, 50);
                x2.fillStyle = '#000'; x2.font = '12px Arial'; x2.fillText('Test1', 5, 25);
                var h1 = hashStr(c1.toDataURL()), h2 = hashStr(c2.toDataURL());
                if (h1 !== h2) return 'noise-detected';
                x2.fillText('X', 60, 25);
                if (h1 === hashStr(c2.toDataURL())) return 'canvas-blocked';
                return 'clean';
            } catch(e) { return 'error'; }
        })();
        
        // ============================================
        // WEBGL FINGERPRINT
        // ============================================
        var webglData = (function() {
            try {
                var canvas = document.createElement('canvas');
                var gl = canvas.getContext('webgl') || canvas.getContext('experimental-webgl');
                if (!gl) return { fp: '', gpu: '', gpuVendor: '', params: '', evasion: 0 };
                var ext = gl.getExtension('WEBGL_debug_renderer_info');
                var params = [
                    gl.getParameter(gl.VERSION),
                    gl.getParameter(gl.SHADING_LANGUAGE_VERSION),
                    gl.getParameter(gl.VENDOR),
                    gl.getParameter(gl.RENDERER),
                    gl.getParameter(gl.MAX_VERTEX_ATTRIBS),
                    gl.getParameter(gl.MAX_VERTEX_UNIFORM_VECTORS),
                    gl.getParameter(gl.MAX_VARYING_VECTORS),
                    gl.getParameter(gl.MAX_COMBINED_TEXTURE_IMAGE_UNITS),
                    gl.getParameter(gl.MAX_VERTEX_TEXTURE_IMAGE_UNITS),
                    gl.getParameter(gl.MAX_TEXTURE_IMAGE_UNITS),
                    gl.getParameter(gl.MAX_FRAGMENT_UNIFORM_VECTORS),
                    gl.getParameter(gl.MAX_CUBE_MAP_TEXTURE_SIZE),
                    gl.getParameter(gl.MAX_RENDERBUFFER_SIZE),
                    gl.getParameter(gl.MAX_VIEWPORT_DIMS) ? gl.getParameter(gl.MAX_VIEWPORT_DIMS).join(',') : '',
                    gl.getParameter(gl.MAX_TEXTURE_SIZE),
                    gl.getParameter(gl.ALIASED_LINE_WIDTH_RANGE) ? gl.getParameter(gl.ALIASED_LINE_WIDTH_RANGE).join(',') : '',
                    gl.getParameter(gl.ALIASED_POINT_SIZE_RANGE) ? gl.getParameter(gl.ALIASED_POINT_SIZE_RANGE).join(',') : '',
                    gl.getParameter(gl.RED_BITS),
                    gl.getParameter(gl.GREEN_BITS),
                    gl.getParameter(gl.BLUE_BITS),
                    gl.getParameter(gl.ALPHA_BITS),
                    gl.getParameter(gl.DEPTH_BITS),
                    gl.getParameter(gl.STENCIL_BITS)
                ];
                var extensions = gl.getSupportedExtensions() || [];
                var str = params.join('|') + extensions.join(',');
                
                // Evasion detection: Check for suspiciously generic values
                var gpu = ext ? gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) : '';
                var gpuVendor = ext ? gl.getParameter(ext.UNMASKED_VENDOR_WEBGL) : '';
                var evasion = 0;
                if (gpu && (gpu.indexOf('SwiftShader') > -1 || gpu.indexOf('llvmpipe') > -1 || gpu === 'Mesa' || gpu === 'Disabled')) {
                    evasion = 1; // Software renderer often = headless/spoofed
                }
                
                return {
                    fp: hashStr(str),
                    gpu: gpu,
                    gpuVendor: gpuVendor,
                    params: params.slice(0, 5).join('|'),
                    extensions: extensions.length,
                    evasion: evasion
                };
            } catch(e) { return { fp: '', gpu: '', gpuVendor: '', params: '', extensions: 0, evasion: 0 }; }
        })();
        data.webglFP = webglData.fp;
        data.gpu = webglData.gpu;
        data.gpuVendor = webglData.gpuVendor;
        data.webglParams = webglData.params;
        data.webglExt = webglData.extensions;
        data.webglEvasion = webglData.evasion;
        
        // ============================================
        // V-02: AUDIO FINGERPRINT WITH STABILITY CHECK
        // Runs the audio fingerprint twice. Real audio hardware produces
        // identical results; noise injection extensions differ each run.
        // ============================================
        var audioPromise = (function() {
            try {
                var AudioCtx = w.OfflineAudioContext || w.webkitOfflineAudioContext;
                if (!AudioCtx) { data.audioFP = ''; return Promise.resolve(); }
                var runAudioFP = function() {
                    return new Promise(function(resolve) {
                        try {
                            var ctx = new AudioCtx(1, 44100, 44100);
                            var osc = ctx.createOscillator();
                            var comp = ctx.createDynamicsCompressor();
                            osc.type = 'triangle'; osc.frequency.value = 10000;
                            comp.threshold.value = -50; comp.knee.value = 40;
                            comp.ratio.value = 12; comp.attack.value = 0; comp.release.value = 0.25;
                            osc.connect(comp); comp.connect(ctx.destination); osc.start(0);
                            ctx.startRendering().then(function(buffer) {
                                var cd = buffer.getChannelData(0);
                                var sum = 0;
                                for (var i = 4500; i < 5000; i++) sum += Math.abs(cd[i]);
                                // Also hash full sample for extra entropy
                                var sampleStr = '';
                                for (var j = 0; j < cd.length; j += 100) sampleStr += cd[j].toFixed(4);
                                resolve({ fp: sum.toFixed(6), hash: hashStr(sampleStr) });
                            }).catch(function() { resolve({ fp: 'blocked', hash: '' }); });
                        } catch(e) { resolve({ fp: 'error', hash: '' }); }
                    });
                };
                return Promise.all([runAudioFP(), runAudioFP()]).then(function(r) {
                    data.audioFP = r[0].fp;
                    data.audioHash = r[0].hash;
                    data.audioStable = (r[0].fp === r[1].fp) ? 1 : 0;
                    if (r[0].fp !== r[1].fp && r[0].fp !== 'blocked') data.audioNoiseDetected = 1;
                });
            } catch(e) { data.audioFP = ''; return Promise.resolve(); }
        })();
        
        // ============================================
        // V-09: FONT DETECTION (Dual-Method Anti-Spoof)
        // Uses both offsetWidth AND getBoundingClientRect. If an extension
        // spoofs one but not the other, we detect the mismatch.
        // ============================================
        data.fonts = (function() {
            try {
                if (!document.body) return '';
                var testFonts = [
                    'Arial','Arial Black','Verdana','Times New Roman','Courier New',
                    'Georgia','Comic Sans MS','Impact','Trebuchet MS','Tahoma',
                    'Segoe UI','Calibri','Consolas','Helvetica','Monaco',
                    'Roboto','Open Sans','Lato','Montserrat','Source Sans Pro',
                    'Century Gothic','Futura','Gill Sans','Lucida Grande',
                    'Garamond','MS Gothic','SimSun','Microsoft YaHei',
                    'Apple Color Emoji','Segoe UI Emoji'
                ];
                var detected = [];
                var baseline = 'monospace';
                var testStr = 'mmmmmmmmmmlli';
                var s1 = document.createElement('span');
                s1.style.cssText = 'position:absolute;left:-9999px;font-size:72px;visibility:hidden;';
                s1.innerHTML = testStr;
                document.body.appendChild(s1);
                s1.style.fontFamily = baseline;
                var bw1 = s1.offsetWidth;
                var s2 = document.createElement('span');
                s2.style.cssText = 'position:absolute;left:-9999px;font-size:72px;visibility:hidden;';
                s2.innerHTML = testStr;
                document.body.appendChild(s2);
                s2.style.fontFamily = baseline;
                var bw2 = s2.getBoundingClientRect().width;
                var mismatch = false;
                for (var i = 0; i < testFonts.length; i++) {
                    s1.style.fontFamily = testFonts[i] + ',' + baseline;
                    s2.style.fontFamily = testFonts[i] + ',' + baseline;
                    var d1 = s1.offsetWidth !== bw1;
                    var d2 = s2.getBoundingClientRect().width !== bw2;
                    if (d1 !== d2) mismatch = true;
                    if (d1 || d2) detected.push(testFonts[i]);
                }
                document.body.removeChild(s1);
                document.body.removeChild(s2);
                data.fontMethodMismatch = mismatch ? 1 : 0;
                return detected.join(',');
            } catch(e) { return ''; }
        })();
        
        // ============================================
        // SPEECH SYNTHESIS VOICES
        // ============================================
        data.voices = (function() {
            try {
                var v = speechSynthesis.getVoices();
                if (v.length === 0) return '';
                return v.map(function(x) { return x.name + '/' + x.lang; }).slice(0, 20).join('|');
            } catch(e) { return ''; }
        })();
        
        // ============================================
        // WEBRTC LOCAL IP
        // ============================================
        (function() {
            try {
                var rtc = new RTCPeerConnection({iceServers: []});
                rtc.createDataChannel('');
                rtc.createOffer().then(function(offer) { return rtc.setLocalDescription(offer); });
                rtc.onicecandidate = function(e) {
                    if (e && e.candidate && e.candidate.candidate) {
                        var match = /([0-9]{1,3}\.){3}[0-9]{1,3}/.exec(e.candidate.candidate);
                        if (match && !data.localIp) {
                            data.localIp = match[0];
                        }
                    }
                };
            } catch(e) {}
        })();
        
        // ============================================
        // STORAGE ESTIMATION (wrapped in try-catch + safeGet)
        // ============================================
        (function() {
            try {
                var storage = safeGet(n, 'storage', null);
                if (storage && storage.estimate) {
                    storage.estimate().then(function(est) {
                        data.storageQuota = Math.round((est.quota || 0) / 1073741824);
                        data.storageUsed = Math.round((est.usage || 0) / 1048576);
                    });
                }
            } catch(e) {}
        })();
        
        // ============================================
        // GAMEPAD DETECTION (wrapped in try-catch + safeGet)
        // ============================================
        data.gamepads = (function() {
            try {
                var getGP = safeGet(n, 'getGamepads', null);
                var gp = getGP ? getGP.call(n) : [];
                var found = [];
                for (var i = 0; i < gp.length; i++) {
                    if (gp[i]) found.push(gp[i].id);
                }
                return found.join('|');
            } catch(e) { return ''; }
        })();
        
        // ============================================
        // BATTERY STATUS (wrapped in try-catch + safeGet)
        // ============================================
        (function() {
            try {
                var getBat = safeGet(n, 'getBattery', null);
                if (getBat) {
                    getBat.call(n).then(function(b) {
                        data.batteryLevel = Math.round(b.level * 100);
                        data.batteryCharging = b.charging ? 1 : 0;
                    });
                }
            } catch(e) {}
        })();
        
        // ============================================
        // MEDIA DEVICES COUNT (wrapped in try-catch + safeGet)
        // ============================================
        (function() {
            try {
                var mediaDevices = safeGet(n, 'mediaDevices', null);
                if (mediaDevices && mediaDevices.enumerateDevices) {
                    mediaDevices.enumerateDevices().then(function(devices) {
                        var audio = 0, video = 0;
                        devices.forEach(function(d) {
                            if (d.kind === 'audioinput') audio++;
                            if (d.kind === 'videoinput') video++;
                        });
                        data.audioInputs = audio;
                        data.videoInputs = video;
                    });
                }
            } catch(e) {}
        })();
        
        // ============================================
        // SCREEN & WINDOW
        // ============================================
        data.sw = s.width;
        data.sh = s.height;
        data.saw = s.availWidth;
        data.sah = s.availHeight;
        data.cd = s.colorDepth;
        data.pd = w.devicePixelRatio || 1;
        data.ori = (s.orientation && s.orientation.type) || '';
        data.vw = w.innerWidth;
        data.vh = w.innerHeight;
        data.ow = w.outerWidth;
        data.oh = w.outerHeight;
        data.sx = w.screenX || w.screenLeft || 0;
        data.sy = w.screenY || w.screenTop || 0;
        
        // ============================================
        // TIME & LOCALE (using safeGet for language properties)
        // ============================================
        data.tz = Intl.DateTimeFormat().resolvedOptions().timeZone;
        data.tzo = d.getTimezoneOffset();
        data.ts = d.getTime();
        data.lang = safeGet(n, 'language', '');
        data.langs = (safeGet(n, 'languages', []) || []).join(',');
        
        // ============================================
        // TIMEZONE LOCALE FORMATTING (High Entropy)
        // ============================================
        data.tzLocale = (function() {
            try {
                var opts = Intl.DateTimeFormat().resolvedOptions();
                return [
                    opts.locale,
                    opts.calendar,
                    opts.numberingSystem,
                    opts.hourCycle
                ].filter(Boolean).join('|');
            } catch(e) { return ''; }
        })();
        data.dateFormat = (function() {
            try {
                return new Intl.DateTimeFormat().format(new Date(2024, 0, 15));
            } catch(e) { return ''; }
        })();
        data.numberFormat = (function() {
            try {
                return new Intl.NumberFormat().format(1234567.89);
            } catch(e) { return ''; }
        })();
        data.relativeTime = (function() {
            try {
                if (Intl.RelativeTimeFormat) {
                    return new Intl.RelativeTimeFormat().format(-1, 'day');
                }
            } catch(e) {}
            return '';
        })();
        
        // ============================================
        // DEVICE & BROWSER (using safeGet for Proxy protection)
        // ============================================
        data.plt = safeGet(n, 'platform', '');
        data.vnd = safeGet(n, 'vendor', '');
        data.ua = safeGet(n, 'userAgent', '');
        data.cores = safeGet(n, 'hardwareConcurrency', '');
        data.mem = safeGet(n, 'deviceMemory', '');
        data.touch = safeGet(n, 'maxTouchPoints', 0);
        data.product = safeGet(n, 'product', '');
        data.productSub = safeGet(n, 'productSub', '');
        data.vendorSub = safeGet(n, 'vendorSub', '');
        
        // ============================================
        // FIREFOX-SPECIFIC SIGNALS (using safeGet)
        // ============================================
        data.oscpu = safeGet(n, 'oscpu', '');  // Firefox only
        data.buildID = safeGet(n, 'buildID', '');  // Firefox only (version fingerprint)
        
        // ============================================
        // CHROME-SPECIFIC SIGNALS
        // ============================================
        data.chromeObj = w.chrome ? 1 : 0;
        data.chromeRuntime = (w.chrome && w.chrome.runtime) ? 1 : 0;
        
        // Performance.memory (Chrome only - heap size fingerprint)
        if (perf.memory) {
            data.jsHeapLimit = perf.memory.jsHeapSizeLimit || '';
            data.jsHeapTotal = perf.memory.totalJSHeapSize || '';
            data.jsHeapUsed = perf.memory.usedJSHeapSize || '';
        }
        
        // ============================================
        // HIGH-ENTROPY CLIENT HINTS (async) - using safeGet for userAgentData
        // ============================================
        var userAgentData = safeGet(n, 'userAgentData', null);
        (function() {
            try {
                if (userAgentData && userAgentData.getHighEntropyValues) {
                    userAgentData.getHighEntropyValues([
                        'architecture', 'bitness', 'model', 'platformVersion',
                        'fullVersionList', 'wow64', 'formFactor'
                    ]).then(function(ua) {
                        data.uaArch = ua.architecture || '';
                        data.uaBitness = ua.bitness || '';
                        data.uaModel = ua.model || '';
                        data.uaPlatformVersion = ua.platformVersion || '';
                        data.uaWow64 = ua.wow64 ? 1 : 0;
                        data.uaFormFactor = (ua.formFactor || []).join(',');
                        if (ua.fullVersionList) {
                            data.uaFullVersion = ua.fullVersionList.map(function(b) {
                                return b.brand + '/' + b.version;
                            }).join('|');
                        }
                    }).catch(function() {});
                }
            } catch(e) {}
        })();
        
        // Low-entropy client hints (sync) - using cached userAgentData
        if (userAgentData) {
            data.uaMobile = userAgentData.mobile ? 1 : 0;
            data.uaPlatform = userAgentData.platform || '';
            data.uaBrands = (userAgentData.brands || []).map(function(b) {
                return b.brand + '/' + b.version;
            }).join('|');
        }
        
        // ============================================
        // DETAILED PLUGIN ENUMERATION (using safeGet for Proxy protection)
        // ============================================
        var pluginsArray = safeGet(n, 'plugins', null);
        var mimeTypesArray = safeGet(n, 'mimeTypes', null);
        
        data.pluginList = (function() {
            try {
                if (!pluginsArray || pluginsArray.length === 0) return '';
                var plugs = [];
                for (var i = 0; i < Math.min(pluginsArray.length, 20); i++) {
                    var p = pluginsArray[i];
                    if (p && p.name) {
                        plugs.push(p.name + '::' + (p.filename || '') + '::' + (p.description || '').substring(0, 50));
                    }
                }
                return plugs.join('|');
            } catch(e) { return ''; }
        })();
        
        data.mimeList = (function() {
            try {
                if (!mimeTypesArray || mimeTypesArray.length === 0) return '';
                var mimes = [];
                for (var i = 0; i < Math.min(mimeTypesArray.length, 30); i++) {
                    var m = mimeTypesArray[i];
                    if (m && m.type) mimes.push(m.type);
                }
                return mimes.join(',');
            } catch(e) { return ''; }
        })();
        data.appName = safeGet(n, 'appName', '');
        data.appVersion = safeGet(n, 'appVersion', '');
        data.appCodeName = safeGet(n, 'appCodeName', '');
        
        // ============================================
        // BROWSER CAPABILITIES (using safeGet for Proxy-protected properties)
        // ============================================
        data.ck = safeGet(n, 'cookieEnabled', false) ? 1 : 0;
        data.dnt = safeGet(n, 'doNotTrack', '');
        data.pdf = safeGet(n, 'pdfViewerEnabled', false) ? 1 : 0;
        data.webdr = safeGet(n, 'webdriver', false) ? 1 : 0;
        data.online = safeGet(n, 'onLine', true) ? 1 : 0;
        data.java = safeGet(n, 'javaEnabled', 0) ? 1 : 0;  // safeGet handles function call
        data.plugins = pluginsArray ? pluginsArray.length : 0;
        data.mimeTypes = mimeTypesArray ? mimeTypesArray.length : 0;
        
        // ============================================
        // BOT DETECTION (Comprehensive)
        // ============================================
        var botSignals = (function() {
            var signals = [];
            var score = 0;
            
            // 1. WebDriver detection (most common) - use data.webdr captured earlier
            if (data.webdr) { signals.push('webdriver'); score += 10; }
            
            // 2. Headless Chrome detection - use data.ua captured earlier
            if (!w.chrome && /Chrome/.test(data.ua)) {
                signals.push('headless-no-chrome-obj');
                score += 8;
            }
            
            // 2b. Minimal/Fake User-Agent detection
            // Real browsers have UA strings 50+ chars, bots often set minimal strings
            var ua = data.ua || '';
            if (ua.length < 30) {
                signals.push('minimal-ua');
                score += 15;
            }
            // Known fake UA patterns
            if (/^(desktop|mobile|bot|crawler|spider|scraper)$/i.test(ua)) {
                signals.push('fake-ua');
                score += 20;
            }
            
            // 3. PhantomJS detection
            if (w._phantom || w.phantom || w.callPhantom) {
                signals.push('phantomjs');
                score += 10;
            }
            
            // 4. Nightmare.js detection
            if (w.__nightmare) {
                signals.push('nightmare');
                score += 10;
            }
            
            // 5. Selenium detection
            if (w.document.__selenium_unwrapped || w.document.__webdriver_evaluate ||
                w.document.__driver_evaluate || w.document.__webdriver_unwrapped ||
                w.document.__fxdriver_evaluate || w.document.__driver_unwrapped) {
                signals.push('selenium');
                score += 10;
            }
            
            // 6. Puppeteer/Playwright detection
            var langs = safeGet(n, 'languages', null);
            if (langs && langs.length === 0) {
                signals.push('empty-languages');
                score += 5;
            }
            
            // 7. Chrome DevTools Protocol (CDP) detection
            if (w.cdc_adoQpoasnfa76pfcZLmcfl_Array ||
                w.cdc_adoQpoasnfa76pfcZLmcfl_Promise ||
                w.cdc_adoQpoasnfa76pfcZLmcfl_Symbol) {
                signals.push('cdp');
                score += 10;
            }
            
            // 8. Permission inconsistencies (bots often have weird permission states)
            // Use safeGet for permissions API
            var permissions = safeGet(n, 'permissions', null);
            try {
                if (permissions) {
                    permissions.query({name: 'notifications'}).then(function(p) {
                        if (p.state === 'denied' && Notification && Notification.permission === 'default') {
                            signals.push('perm-inconsistent');
                            data.botPermInconsistent = 1;
                        }
                    }).catch(function(){});
                }
            } catch(e) {}
            
            // 9. Plugin/mimeType inconsistencies - use already-captured pluginsArray/mimeTypesArray
            if (pluginsArray && pluginsArray.length === 0 && mimeTypesArray && mimeTypesArray.length > 0) {
                signals.push('plugin-mime-mismatch');
                score += 3;
            }
            
            // 10. Suspicious screen dimensions
            if (s.width === 0 || s.height === 0 || s.availHeight === 0) {
                signals.push('zero-screen');
                score += 8;
            }
            
            // 11. No browser plugins (very rare for real users)
            // Use data.ua which was safely captured earlier
            if ((!pluginsArray || pluginsArray.length === 0) && !/Firefox/.test(data.ua)) {
                signals.push('no-plugins');
                score += 2;
            }
            
            // 12. Automation-specific properties
            if (w.domAutomation || w.domAutomationController) {
                signals.push('dom-automation');
                score += 10;
            }
            
            // 13. Inconsistent outerWidth/innerWidth (headless often has issues)
            if (w.outerWidth === 0 && w.innerWidth > 0) {
                signals.push('outer-zero');
                score += 5;
            }
            
            // 14. Check for automation flags in navigator
            // Wrapped in try/catch: privacy extensions (JShelter, Trace) wrap navigator
            // in a Proxy. Enumerating a Proxy can trigger invariant violations on
            // non-configurable properties like javaEnabled, crashing the entire script.
            try {
                for (var key in n) {
                    if (/webdriver|selenium|puppeteer|playwright/i.test(key)) {
                        signals.push('nav-' + key);
                        score += 10;
                    }
                }
            } catch(e) { /* Proxy enumeration blocked - non-critical, other checks cover this */ }
            
            // 15. Function.toString tampering (bots often patch native functions)
            try {
                if (permissions) {
                    var fnStr = Function.prototype.toString.call(permissions.query);
                    if (fnStr.indexOf('[native code]') === -1) {
                        signals.push('fn-tampered');
                        score += 5;
                    }
                }
            } catch(e) {}
            
            // ============================================
            // PLAYWRIGHT-SPECIFIC DETECTION
            // ============================================
            
            // 16. Playwright injects __playwright into window in some modes
            if (w.__playwright || w.__pw_manual) {
                signals.push('playwright-global');
                score += 10;
            }
            
            // 17. Playwright default viewport sizes (common defaults)
            // 1280x720 is Playwright's default, 800x600 is also common for headless
            if ((w.innerWidth === 1280 && w.innerHeight === 720) ||
                (w.innerWidth === 800 && w.innerHeight === 600)) {
                signals.push('default-viewport');
                score += 2; // Low score - could be legitimate
            }
            
            // 18. Headless detection via missing plugins in Chromium
            if (/HeadlessChrome/.test(data.ua)) {
                signals.push('headless-ua');
                score += 10;
            }
            
            // 19. Notification permission in headless is always 'denied'
            // but Notification.permission might report 'default' - inconsistency
            try {
                if (w.Notification && Notification.permission === 'denied' && 
                    permissions) {
                    // Already checked above, but Playwright specifically has this issue
                }
            } catch(e) {}
            
            // 20. Chrome runtime inconsistency (Playwright/Puppeteer headless)
            if (w.chrome && !w.chrome.runtime) {
                signals.push('chrome-no-runtime');
                score += 3;
            }
            
            // 21. Automated browsers often have identical screen and viewport
            if (s.width === w.outerWidth && s.height === w.outerHeight && s.availHeight === s.height) {
                signals.push('fullscreen-match');
                score += 2;
            }
            
            // 22. Missing connection info (common in headless)
            // Use data.ua which was safely captured earlier
            if (!c && /Chrome/.test(data.ua)) {
                signals.push('no-connection-api');
                score += 3;
            }
            
            // 23. Script Execution Time (BOT DETECTION - HIGH VALUE)
            // =========================================================
            // scriptExecTime = milliseconds from page load to this point in script execution
            // 
            // INTERPRETATION:
            //   < 10ms  : Almost certainly a bot (instant DOM ready, no network latency)
            //   10-50ms : Suspicious, could be very fast connection + SSD + cached
            //   50-200ms: Normal range for real users
            //   > 200ms : Slow connection/device, definitely human
            //
            // WHY IT WORKS:
            //   Real browsers: DNS lookup + TCP + TLS + HTML parse + JS load + execute
            //   Bots: DOM is pre-constructed or instant, no real network stack
            //
            // ANALYSIS: Group by scriptExecTime in SQL to find bot clusters
            // =========================================================
            data.scriptExecTime = Date.now() - d.getTime();
            
            // 24. Check if eval is native (some bots override it)
            try {
                if (eval.toString().indexOf('[native code]') === -1) {
                    signals.push('eval-tampered');
                    score += 5;
                }
            } catch(e) {}
            
            // 25. Check for Playwright's browser context fingerprint override
            // Playwright can set a specific fingerprint, but the override pattern is detectable
            try {
                var descGetter = Object.getOwnPropertyDescriptor(Navigator.prototype, 'webdriver');
                if (descGetter && descGetter.get && descGetter.get.toString().indexOf('[native code]') === -1) {
                    signals.push('webdriver-getter-override');
                    score += 8;
                }
            } catch(e) {}
            
            return { signals: signals.join(','), score: score };
        })();
        
        data.botSignals = botSignals.signals;
        data.botScore = botSignals.score;
        
        // ============================================
        // PRIVACY/EVASION TOOL DETECTION
        // ============================================
        var evasionResult = (function() {
            var detected = [];
            
            // 1. Tor Browser (standardized values)
            if (s.width === 1000 && s.height === 1000) {
                detected.push('tor-screen');
            }
            // Use data.plt captured earlier via safeGet
            if (data.plt === 'Win32' && s.colorDepth === 24 && !w.chrome) {
                // Tor on Windows has specific fingerprint
                detected.push('tor-likely');
            }
            
            // 2. Brave Browser (randomizes fingerprints)
            var braveNav = safeGet(n, 'brave', null);
            if (braveNav && typeof braveNav.isBrave === 'function') {
                detected.push('brave');
            }
            
            // 3. Privacy Badger / uBlock patterns
            // These often block WebRTC
            if (typeof RTCPeerConnection === 'undefined') {
                detected.push('webrtc-blocked');
            }
            
            // 4. Canvas Blocker extensions (already in canvasEvasion)
            
            // 5. User Agent spoofing detection - use data.plt and data.ua captured earlier
            var platform = (data.plt || '').toLowerCase();
            var ua = (data.ua || '').toLowerCase();
            if ((platform.indexOf('win') > -1 && ua.indexOf('mac') > -1) ||
                (platform.indexOf('mac') > -1 && ua.indexOf('windows') > -1) ||
                (platform.indexOf('linux') > -1 && ua.indexOf('windows') > -1 && ua.indexOf('android') === -1)) {
                detected.push('ua-platform-mismatch');
            }
            
            // 6. Screen resolution vs. User Agent mismatch (mobile UA on desktop resolution)
            if (/Mobile|Android|iPhone/.test(data.ua) && s.width > 1024) {
                detected.push('mobile-ua-desktop-screen');
            }
            
            // 7. Touch capability mismatch - use data.touch captured earlier
            if (data.touch > 0 && !/Mobile|Android|iPhone|iPad|Touch/.test(data.ua) && s.width > 1024) {
                detected.push('touch-mismatch');
            }
            
            // 8. NoScript detection - check if key APIs are undefined
            if (typeof w.Worker === 'undefined' && typeof w.fetch !== 'undefined') {
                detected.push('partial-js-block');
            }
            
            // 9. Client Hints vs Navigator.platform mismatch
            // Bots often spoof one but not the other - use safeGet for userAgentData
            var uaData = safeGet(n, 'userAgentData', null);
            if (uaData && uaData.platform) {
                var chPlatform = uaData.platform.toLowerCase();
                var navPlatform = (data.plt || '').toLowerCase();
                // Linux platform but Windows client hints (or vice versa)
                if ((navPlatform.indexOf('linux') > -1 && chPlatform === 'windows') ||
                    (navPlatform.indexOf('win') > -1 && chPlatform === 'linux') ||
                    (navPlatform.indexOf('mac') > -1 && chPlatform !== 'macos' && chPlatform !== 'mac')) {
                    detected.push('clienthints-platform-mismatch');
                }
            }
            
            return detected.join(',');
        })();
        
        data.evasionDetected = evasionResult;
        
        // ============================================
        // CONNECTION
        // ============================================
        data.conn = c.effectiveType || '';
        data.dl = c.downlink || '';
        data.dlMax = c.downlinkMax || '';
        data.rtt = c.rtt || '';
        data.save = c.saveData ? 1 : 0;
        data.connType = c.type || '';
        
        // ============================================
        // PAGE & SESSION
        // ============================================
        data.url = location.href;
        data.ref = document.referrer;
        data.hist = history.length;
        data.title = document.title;
        data.domain = location.hostname;
        data.path = location.pathname;
        data.hash = location.hash;
        data.protocol = location.protocol;
        
        // ============================================
        // PERFORMANCE TIMING
        // ============================================
        if (perf.timing) {
            var t = perf.timing;
            data.loadTime = t.loadEventEnd - t.navigationStart;
            data.domTime = t.domContentLoadedEventEnd - t.navigationStart;
            data.dnsTime = t.domainLookupEnd - t.domainLookupStart;
            data.tcpTime = t.connectEnd - t.connectStart;
            data.ttfb = t.responseStart - t.requestStart;
        }
        
        // ============================================
        // STORAGE AVAILABLE
        // ============================================
        data.ls = (function() { try { return !!w.localStorage; } catch(e) { return 0; } })() ? 1 : 0;
        data.ss = (function() { try { return !!w.sessionStorage; } catch(e) { return 0; } })() ? 1 : 0;
        data.idb = !!w.indexedDB ? 1 : 0;
        data.caches = !!w.caches ? 1 : 0;
        
        // ============================================
        // FEATURE DETECTION (using safeGet for navigator properties)
        // ============================================
        data.ww = !!w.Worker ? 1 : 0;
        data.swk = !!safeGet(n, 'serviceWorker', null) ? 1 : 0;
        data.wasm = typeof WebAssembly === 'object' ? 1 : 0;
        data.webgl = (function() { try { return !!document.createElement('canvas').getContext('webgl'); } catch(e) { return 0; } })() ? 1 : 0;
        data.webgl2 = (function() { try { return !!document.createElement('canvas').getContext('webgl2'); } catch(e) { return 0; } })() ? 1 : 0;
        data.canvas = !!document.createElement('canvas').getContext ? 1 : 0;
        data.touchEvent = 'ontouchstart' in w ? 1 : 0;
        data.pointerEvent = !!w.PointerEvent ? 1 : 0;
        data.mediaDevices = !!safeGet(n, 'mediaDevices', null) ? 1 : 0;
        // ============================================
        // INTENTIONALLY EXCLUDED APIS
        // ============================================
        // The following APIs are NOT checked because:
        // 1. They add near-zero fingerprinting entropy (all Chrome has them, Firefox/Safari don't)
        // 2. Some trigger browser permission prompts (MIDI, Bluetooth, USB, Geolocation, Notifications)
        // 3. The MIDI popup (control and reprogram your MIDI devices) looks alarming/black-hat
        // 4. Privacy extensions flag access to these APIs
        //
        // Excluded: bluetooth, usb, serial, hid, midi, xr, share, credentials,
        //           geolocation, notifications, push, payment, speechRecog
        var clipboardObj = safeGet(n, 'clipboard', null);
        data.clipboard = !!(clipboardObj && clipboardObj.writeText) ? 1 : 0;
        data.speechSynth = !!w.speechSynthesis ? 1 : 0;
        
        // ============================================
        // CSS/MEDIA PREFERENCES
        // ============================================
        data.darkMode = w.matchMedia && w.matchMedia('(prefers-color-scheme: dark)').matches ? 1 : 0;
        data.lightMode = w.matchMedia && w.matchMedia('(prefers-color-scheme: light)').matches ? 1 : 0;
        data.reducedMotion = w.matchMedia && w.matchMedia('(prefers-reduced-motion: reduce)').matches ? 1 : 0;
        data.reducedData = w.matchMedia && w.matchMedia('(prefers-reduced-data: reduce)').matches ? 1 : 0;
        data.contrast = w.matchMedia && w.matchMedia('(prefers-contrast: high)').matches ? 1 : 0;
        data.forcedColors = w.matchMedia && w.matchMedia('(forced-colors: active)').matches ? 1 : 0;
        data.invertedColors = w.matchMedia && w.matchMedia('(inverted-colors: inverted)').matches ? 1 : 0;
        data.hover = w.matchMedia && w.matchMedia('(hover: hover)').matches ? 1 : 0;
        data.pointer = w.matchMedia && w.matchMedia('(pointer: fine)').matches ? 'fine' : (w.matchMedia && w.matchMedia('(pointer: coarse)').matches ? 'coarse' : '');
        data.standalone = w.matchMedia && w.matchMedia('(display-mode: standalone)').matches ? 1 : 0;
        
        // ============================================
        // DOCUMENT INFO
        // ============================================
        data.docCharset = document.characterSet || '';
        data.docCompat = document.compatMode || '';
        data.docReady = document.readyState || '';
        data.docHidden = document.hidden ? 1 : 0;
        data.docVisibility = document.visibilityState || '';
        
        // ============================================
        // MATH FINGERPRINT
        // ============================================
        data.mathFP = (function() {
            var m = Math;
            return [
                m.tan(-1e300),
                m.sin(1),
                m.acos(0.5),
                m.atan(2),
                m.exp(1),
                m.log(2),
                m.sqrt(2),
                m.pow(2, 53)
            ].map(function(x) { return x.toString().slice(0, 10); }).join(',');
        })();
        
        // ============================================
        // CSS FONT-VARIANT FINGERPRINT
        // ============================================
        data.cssFontVariant = (function() {
            try {
                var el = document.createElement('span');
                el.style.cssText = 'position:absolute;left:-9999px;';
                el.innerHTML = 'Test';
                document.body.appendChild(el);
                
                var variants = [];
                var testProps = [
                    'font-variant-ligatures',
                    'font-variant-caps', 
                    'font-variant-numeric',
                    'font-variant-east-asian',
                    'font-feature-settings',
                    'font-kerning'
                ];
                
                for (var i = 0; i < testProps.length; i++) {
                    var prop = testProps[i];
                    var camelProp = prop.replace(/-([a-z])/g, function(m, c) { return c.toUpperCase(); });
                    if (el.style[camelProp] !== undefined) {
                        variants.push(prop.charAt(0));
                    }
                }
                
                document.body.removeChild(el);
                return variants.join('');
            } catch(e) { return ''; }
        })();
        
        // ============================================
        // ERROR HANDLING FINGERPRINT
        // ============================================
        data.errorFP = (function() {
            try { null[0](); } catch(e) { return e.message.length + (e.stack ? e.stack.length : 0); }
            return '';
        })();
        
        // TIER ID
        data.tier = 5;
        
        // ============================================
        // V-04: STEALTH PLUGIN DETECTION
        // Puppeteer-extra-plugin-stealth patches navigator properties via
        // Object.defineProperty. Spoofed getters are measurably slower than
        // native ones due to JS execution overhead.
        // ============================================
        data.stealthSignals = (function() {
            var signals = [];
            try {
                var timeProp = function(obj, prop, iters) {
                    var start = performance.now();
                    for (var i = 0; i < iters; i++) { var x = obj[prop]; }
                    return (performance.now() - start) / iters;
                };
                var baseTime = timeProp(navigator, 'userAgent', 1000);
                if (baseTime > 0) {
                    var wdRatio = timeProp(navigator, 'webdriver', 1000) / baseTime;
                    var plRatio = timeProp(navigator, 'platform', 1000) / baseTime;
                    if (wdRatio > 5) signals.push('webdriver-slow');
                    if (plRatio > 5) signals.push('platform-slow');
                }
            } catch(e) { signals.push('timing-error'); }
            try {
                var nts = Function.prototype.toString;
                if (nts.call(nts).indexOf('[native code]') === -1) signals.push('toString-spoofed');
            } catch(e) { signals.push('toString-blocked'); }
            try {
                if (typeof Navigator !== 'undefined' && Object.getPrototypeOf(navigator) !== Navigator.prototype)
                    signals.push('nav-proto-modified');
            } catch(e) {}
            try {
                if (w.Proxy && w.Proxy.toString().indexOf('[native code]') === -1) signals.push('proxy-modified');
            } catch(e) {}
            return signals.join(',');
        })();
        
        // ============================================
        // V-10: ENHANCED EVASION / TOR DETECTION
        // Supplements the existing evasionDetected field with deeper checks.
        // ============================================
        data.evasionSignalsV2 = (function() {
            var det = [];
            // Tor Browser: viewport rounds to 200x100 increments
            var iW = w.innerWidth, iH = w.innerHeight;
            if (iW % 200 === 0 && iH % 100 === 0) det.push('tor-letterbox-viewport');
            var sW = s.width, sH = s.height;
            if (sW % 200 === 0 && sH % 100 === 0 && !(sW === 1920 && sH === 1080) && !(sW === 1600 && sH === 900))
                det.push('tor-letterbox-screen');
            if (data.fonts && data.fonts.split(',').length < 5) det.push('minimal-fonts');
            if (data.canvasConsistency === 'noise-detected') det.push('canvas-noise');
            if (data.canvasConsistency === 'canvas-blocked') det.push('canvas-blocked');
            if (data.audioNoiseDetected) det.push('audio-noise');
            if (data.fontMethodMismatch) det.push('font-spoof');
            if (data.stealthSignals && data.stealthSignals.length > 0) det.push('stealth-detected');
            return det.join(',');
        })();
        
        // ============================================
        // V-03: BEHAVIORAL ANALYSIS (Mouse/Scroll)
        // Real users have curved, variable mouse movements.
        // Bots have zero movement or perfectly linear paths.
        // ============================================
        var mouseData = { moves: [], startTime: Date.now(), scrolled: 0, scrollY: 0 };
        var mouseHandler = function(e) {
            if (mouseData.moves.length < 50)
                mouseData.moves.push({ x: e.clientX, y: e.clientY, t: Date.now() - mouseData.startTime });
        };
        var scrollHandler = function() {
            mouseData.scrolled = 1;
            mouseData.scrollY = w.scrollY || w.pageYOffset || 0;
        };
        document.addEventListener('mousemove', mouseHandler);
        document.addEventListener('scroll', scrollHandler);
        
        var calculateMouseEntropy = function() {
            var moves = mouseData.moves;
            data.mouseMoves = moves.length;
            data.scrolled = mouseData.scrolled;
            data.scrollY = mouseData.scrollY;
            if (moves.length < 5) { data.mouseEntropy = 0; return; }
            var angles = [];
            for (var i = 1; i < moves.length; i++) {
                var dx = moves[i].x - moves[i-1].x, dy = moves[i].y - moves[i-1].y;
                var dt = moves[i].t - moves[i-1].t;
                if (dt > 0) angles.push(Math.atan2(dy, dx));
            }
            if (angles.length > 1) {
                var mean = angles.reduce(function(a,b) { return a+b; }, 0) / angles.length;
                var variance = angles.reduce(function(s, a) { return s + (a - mean) * (a - mean); }, 0) / angles.length;
                data.mouseEntropy = Math.round(variance * 1000);
            } else { data.mouseEntropy = 0; }
            document.removeEventListener('mousemove', mouseHandler);
            document.removeEventListener('scroll', scrollHandler);
        };
        
        // ============================================
        // V-06: FIRE PIXEL (Promise-based async + behavioral data)
        // Waits for async collectors (audio, battery, storage) and
        // 500ms for behavioral mouse/scroll data before firing.
        // ============================================
        var sendPixel = function() {
            calculateMouseEntropy();
            var params = [];
            for (var key in data) {
                if (data[key] !== '' && data[key] !== null && data[key] !== undefined) {
                    params.push(key + '=' + encodeURIComponent(data[key]));
                }
            }
            new Image().src = '{{PIXEL_URL}}?' + params.join('&');
        };
        
        var asyncPromises = [];
        if (typeof audioPromise !== 'undefined') asyncPromises.push(audioPromise);
        
        var timeoutPromise = new Promise(function(resolve) { setTimeout(resolve, 500); });
        Promise.race([
            Promise.allSettled ? Promise.allSettled(asyncPromises).then(function() {
                return new Promise(function(r) { setTimeout(r, 300); });
            }) : timeoutPromise,
            timeoutPromise
        ]).then(sendPixel);
        
    } catch (e) {
        new Image().src = '{{PIXEL_URL}}?error=1&msg=' + encodeURIComponent(e.message);
    }
})();
";
}
