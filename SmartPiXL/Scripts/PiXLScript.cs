using System.Buffers;
using System.Collections.Concurrent;

namespace SmartPiXL.Scripts;

/// <summary>
/// Contains the SmartPiXL JavaScript template for fingerprint data collection.
/// Stored as a constant string — allocated once at startup.
/// Uses string.Create + Span for zero-allocation script generation after first hit.
/// </summary>
public static class PiXLScript
{
    /// <summary>
    /// JavaScript template with {{PIXL_URL}} placeholder for PiXL URL.
    /// Use GetScript(pixlUrl) for cached, zero-alloc generation per companyId/pixlId.
    /// </summary>
    public const string Template = @"
(function() {
    try {
        var s = screen;
        var n = navigator;
        var w = window;
        var d = new Date();
        var perf = w.performance || {};
        var data = {};
        
        var safeGet = function(obj, prop, fallback) {
            try {
                var val = obj[prop];
                if (typeof val === 'function') {
                    try { return val.call(obj); } catch(e) { return fallback; }
                }
                return val !== undefined ? val : fallback;
            } catch(e) {
                data._proxyBlocked = (data._proxyBlocked || '') + prop + ',';
                return fallback;
            }
        };
        
        var c = safeGet(n, 'connection', {}) || {};
        
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
                
                var imgData = ctx.getImageData(0, 0, 280, 60).data;
                var variance = 0, sum = 0, samples = 100;
                var sampleOffset = 25 * 280 * 4;
                for (var i = sampleOffset; i < sampleOffset + samples * 4; i += 4) {
                    sum += imgData[i] + imgData[i+1] + imgData[i+2];
                }
                var mean = sum / (samples * 3);
                for (var j = sampleOffset; j < sampleOffset + samples * 4; j += 4) {
                    var diff = ((imgData[j] + imgData[j+1] + imgData[j+2]) / 3) - mean;
                    variance += diff * diff;
                }
                variance = variance / samples;
                
                var evasion = (variance < 1 || dataUrl.length < 1000) ? 1 : 0;
                
                return { fp: hash, evasion: evasion };
            } catch(e) { return { fp: '', evasion: 0 }; }
        })();
        data.canvasFP = canvasResult.fp;
        data.canvasEvasion = canvasResult.evasion;
        
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
        
        data.voices = (function() {
            try {
                var v = speechSynthesis.getVoices();
                if (v.length === 0) return '';
                return v.map(function(x) { return x.name + '/' + x.lang; }).slice(0, 20).join('|');
            } catch(e) { return ''; }
        })();
        
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
        data.screenExtended = s.isExtended ? 1 : 0;
        
        data.tz = Intl.DateTimeFormat().resolvedOptions().timeZone;
        data.tzo = d.getTimezoneOffset();
        data.ts = d.getTime();
        data.lang = safeGet(n, 'language', '');
        data.langs = (safeGet(n, 'languages', []) || []).join(',');
        
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
        
        data.plt = safeGet(n, 'platform', '');
        data.vnd = safeGet(n, 'vendor', '');
        data.ua = safeGet(n, 'userAgent', '');
        data.cores = safeGet(n, 'hardwareConcurrency', '');
        data.mem = safeGet(n, 'deviceMemory', '');
        data.touch = safeGet(n, 'maxTouchPoints', 0);
        data.product = safeGet(n, 'product', '');
        data.productSub = safeGet(n, 'productSub', '');
        data.vendorSub = safeGet(n, 'vendorSub', '');
        
        data.oscpu = safeGet(n, 'oscpu', '');  // Firefox only
        data.buildID = safeGet(n, 'buildID', '');  // Firefox only (version fingerprint)
        
        data.chromeObj = w.chrome ? 1 : 0;
        data.chromeRuntime = (w.chrome && w.chrome.runtime) ? 1 : 0;
        
        if (perf.memory) {
            data.jsHeapLimit = perf.memory.jsHeapSizeLimit || '';
            data.jsHeapTotal = perf.memory.totalJSHeapSize || '';
            data.jsHeapUsed = perf.memory.usedJSHeapSize || '';
        }
        
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
        
        if (userAgentData) {
            data.uaMobile = userAgentData.mobile ? 1 : 0;
            data.uaPlatform = userAgentData.platform || '';
            data.uaBrands = (userAgentData.brands || []).map(function(b) {
                return b.brand + '/' + b.version;
            }).join('|');
        }
        
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
        
        data.ck = safeGet(n, 'cookieEnabled', false) ? 1 : 0;
        data.dnt = safeGet(n, 'doNotTrack', '');
        data.pdf = safeGet(n, 'pdfViewerEnabled', false) ? 1 : 0;
        data.webdr = safeGet(n, 'webdriver', false) ? 1 : 0;
        data.online = safeGet(n, 'onLine', true) ? 1 : 0;
        data.java = safeGet(n, 'javaEnabled', 0) ? 1 : 0;  // safeGet handles function call
        data.plugins = pluginsArray ? pluginsArray.length : 0;
        data.mimeTypes = mimeTypesArray ? mimeTypesArray.length : 0;
        
        var botSignals = (function() {
            var signals = [];
            var score = 0;
            
            if (data.webdr) { signals.push('webdriver'); score += 10; }
            
            if (!w.chrome && /Chrome/.test(data.ua)) {
                signals.push('headless-no-chrome-obj');
                score += 8;
            }
            
            var ua = data.ua || '';
            if (ua.length < 30) {
                signals.push('minimal-ua');
                score += 15;
            }
            if (/^(desktop|mobile|bot|crawler|spider|scraper)$/i.test(ua)) {
                signals.push('fake-ua');
                score += 20;
            }
            
            if (w._phantom || w.phantom || w.callPhantom) {
                signals.push('phantomjs');
                score += 10;
            }
            
            if (w.__nightmare) {
                signals.push('nightmare');
                score += 10;
            }
            
            if (w.document.__selenium_unwrapped || w.document.__webdriver_evaluate ||
                w.document.__driver_evaluate || w.document.__webdriver_unwrapped ||
                w.document.__fxdriver_evaluate || w.document.__driver_unwrapped) {
                signals.push('selenium');
                score += 10;
            }
            
            var langs = safeGet(n, 'languages', null);
            if (langs && langs.length === 0) {
                signals.push('empty-languages');
                score += 5;
            }
            
            if (w.cdc_adoQpoasnfa76pfcZLmcfl_Array ||
                w.cdc_adoQpoasnfa76pfcZLmcfl_Promise ||
                w.cdc_adoQpoasnfa76pfcZLmcfl_Symbol) {
                signals.push('cdp');
                score += 10;
            }
            
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
            
            if (pluginsArray && pluginsArray.length === 0 && mimeTypesArray && mimeTypesArray.length > 0) {
                signals.push('plugin-mime-mismatch');
                score += 3;
            }
            
            if (s.width === 0 || s.height === 0 || s.availHeight === 0) {
                signals.push('zero-screen');
                score += 8;
            }
            
            if ((!pluginsArray || pluginsArray.length === 0) && !/Firefox/.test(data.ua)) {
                signals.push('no-plugins');
                score += 2;
            }
            
            if (w.domAutomation || w.domAutomationController) {
                signals.push('dom-automation');
                score += 10;
            }
            
            if (w.outerWidth === 0 && w.innerWidth > 0) {
                signals.push('outer-zero');
                score += 5;
            }
            
            try {
                for (var key in n) {
                    if (/webdriver|selenium|puppeteer|playwright/i.test(key)) {
                        signals.push('nav-' + key);
                        score += 10;
                    }
                }
            } catch(e) { /* Proxy enumeration blocked - non-critical, other checks cover this */ }
            
            try {
                if (permissions) {
                    var fnStr = Function.prototype.toString.call(permissions.query);
                    if (fnStr.indexOf('[native code]') === -1) {
                        signals.push('fn-tampered');
                        score += 5;
                    }
                }
            } catch(e) {}
            
            if (w.__playwright || w.__pw_manual) {
                signals.push('playwright-global');
                score += 10;
            }
            
            if ((w.innerWidth === 1280 && w.innerHeight === 720) ||
                (w.innerWidth === 800 && w.innerHeight === 600)) {
                signals.push('default-viewport');
                score += 2; // Low score - could be legitimate
            }
            
            if (/HeadlessChrome/.test(data.ua)) {
                signals.push('headless-ua');
                score += 10;
            }
            
            try {
                if (w.Notification && Notification.permission === 'denied' && 
                    permissions) {
                }
            } catch(e) {}
            
            if (w.chrome && !w.chrome.runtime) {
                signals.push('chrome-no-runtime');
                score += 1;
            }
            
            if (s.width === w.outerWidth && s.height === w.outerHeight && s.availHeight === s.height) {
                signals.push('fullscreen-match');
                score += 2;
            }
            
            if (!c && /Chrome/.test(data.ua)) {
                signals.push('no-connection-api');
                score += 3;
            }
            
            data.scriptExecTime = Date.now() - d.getTime();
            
            try {
                if (eval.toString().indexOf('[native code]') === -1) {
                    signals.push('eval-tampered');
                    score += 5;
                }
            } catch(e) {}
            
            try {
                var descGetter = Object.getOwnPropertyDescriptor(Navigator.prototype, 'webdriver');
                if (descGetter && descGetter.get && descGetter.get.toString().indexOf('[native code]') === -1) {
                    signals.push('webdriver-getter-override');
                    score += 8;
                }
            } catch(e) {}
            
            try {
                var _ifr = document.createElement('iframe');
                _ifr.style.display = 'none';
                if (document.body) {
                    document.body.appendChild(_ifr);
                    var _pTS = _ifr.contentWindow.Function.prototype.toString;
                    var _curTS = Function.prototype.toString;
                    if (_pTS.call(_curTS).indexOf('[native code]') === -1) {
                        signals.push('cross-realm-toString');
                        score += 12;
                    }
                    document.body.removeChild(_ifr);
                }
            } catch(e) {}
            
            try {
                var _gnChecks = ['webdriver','hardwareConcurrency','platform','languages','deviceMemory','vendor'];
                var _badNames = [];
                for (var gi = 0; gi < _gnChecks.length; gi++) {
                    var gd = Object.getOwnPropertyDescriptor(Navigator.prototype, _gnChecks[gi]);
                    if (gd && gd.get && gd.get.name !== 'get ' + _gnChecks[gi]) {
                        _badNames.push(_gnChecks[gi]);
                    }
                }
                if (_badNames.length > 0) {
                    signals.push('getter-name-mismatch:' + _badNames.join('+'));
                    score += 6 * _badNames.length;
                }
            } catch(e) {}
            
            try {
                var _gpChecks = ['webdriver','platform','vendor','hardwareConcurrency','deviceMemory','pdfViewerEnabled'];
                var _hasProto = [];
                for (var pi2 = 0; pi2 < _gpChecks.length; pi2++) {
                    var pd2 = Object.getOwnPropertyDescriptor(Navigator.prototype, _gpChecks[pi2]);
                    if (pd2 && pd2.get && pd2.get.hasOwnProperty('prototype')) {
                        _hasProto.push(_gpChecks[pi2]);
                    }
                }
                if (_hasProto.length > 0) {
                    signals.push('getter-has-prototype:' + _hasProto.join('+'));
                    score += 8 * _hasProto.length;
                }
            } catch(e) {}
            
            if (perf.memory) {
                var _ht = perf.memory.totalJSHeapSize | 0;
                var _hu = perf.memory.usedJSHeapSize | 0;
                if (_ht === 10000000 || _hu === 10000000 ||
                    (_ht > 0 && _ht % 1000000 === 0) ||
                    (_hu > 0 && _hu % 1000000 === 0)) {
                    signals.push('heap-size-spoofed');
                    score += 8;
                }
                if (_ht > 0 && _ht === _hu) {
                    signals.push('heap-total-equals-used');
                    score += 5;
                }
            }

            return { signals: signals.join(','), score: score };
        })();
        
        data.botSignals = botSignals.signals;
        data.botScore = botSignals.score;
        
        var evasionResult = (function() {
            var detected = [];
            
            if (s.width === 1000 && s.height === 1000) {
                detected.push('tor-screen');
            }
            if (data.plt === 'Win32' && s.colorDepth === 24 && !w.chrome) {
                detected.push('tor-likely');
            }
            
            var braveNav = safeGet(n, 'brave', null);
            if (braveNav && typeof braveNav.isBrave === 'function') {
                detected.push('brave');
            }
            
            if (typeof RTCPeerConnection === 'undefined') {
                detected.push('webrtc-blocked');
            }
            
            var platform = (data.plt || '').toLowerCase();
            var ua = (data.ua || '').toLowerCase();
            if ((platform.indexOf('win') > -1 && ua.indexOf('mac') > -1) ||
                (platform.indexOf('mac') > -1 && ua.indexOf('windows') > -1) ||
                (platform.indexOf('linux') > -1 && ua.indexOf('windows') > -1 && ua.indexOf('android') === -1)) {
                detected.push('ua-platform-mismatch');
            }
            
            if (/Mobile|Android|iPhone/.test(data.ua) && s.width > 1024) {
                detected.push('mobile-ua-desktop-screen');
            }
            
            if (data.touch > 0 && !/Mobile|Android|iPhone|iPad|Touch/.test(data.ua) && s.width > 1024) {
                detected.push('touch-mismatch');
            }
            
            if (typeof w.Worker === 'undefined' && typeof w.fetch !== 'undefined') {
                detected.push('partial-js-block');
            }
            
            var uaData = safeGet(n, 'userAgentData', null);
            if (uaData && uaData.platform) {
                var chPlatform = uaData.platform.toLowerCase();
                var navPlatform = (data.plt || '').toLowerCase();
                if ((navPlatform.indexOf('linux') > -1 && chPlatform === 'windows') ||
                    (navPlatform.indexOf('win') > -1 && chPlatform === 'linux') ||
                    (navPlatform.indexOf('mac') > -1 && chPlatform !== 'macos' && chPlatform !== 'mac')) {
                    detected.push('clienthints-platform-mismatch');
                }
            }
            
            return detected.join(',');
        })();
        
        data.evasionDetected = evasionResult;
        
        var flags = '', anomalyScore = 0;
        var platform = (data.plt || '').toLowerCase();
        var ua = (data.ua || '').toLowerCase();
        var gpu = (data.gpu || '').toLowerCase();
        var vendor = (data.vnd || '');
        var fonts = (data.fonts || '');
        
        var hasWinFont = fonts.indexOf('Segoe UI') > -1 | fonts.indexOf('Calibri') > -1 |
                  fonts.indexOf('Consolas') > -1 | fonts.indexOf('MS Gothic') > -1 |
                  fonts.indexOf('Microsoft YaHei') > -1;
        var hasMacFont = fonts.indexOf('Monaco') > -1 | fonts.indexOf('Lucida Grande') > -1 |
                  fonts.indexOf('Apple Color Emoji') > -1;
        if (platform.indexOf('mac') > -1 && hasWinFont) {
            flags = 'win-fonts-on-mac'; anomalyScore += 15;
        }
        if (platform.indexOf('linux') > -1 && hasWinFont) {
            flags += (flags ? ',' : '') + 'win-fonts-on-linux'; anomalyScore += 15;
        }
        if (platform.indexOf('win') > -1 && hasMacFont && !hasWinFont) {
            flags += (flags ? ',' : '') + 'mac-fonts-on-win'; anomalyScore += 10;
        }
        
        var isSafariUA = ua.indexOf('safari') > -1 && ua.indexOf('chrome') < 0 && ua.indexOf('chromium') < 0;
        if (isSafariUA) {
            if (vendor === 'Google Inc.') {
                flags += (flags ? ',' : '') + 'safari-google-vendor'; anomalyScore += 20;
            }
            if (data.chromeObj) {
                flags += (flags ? ',' : '') + 'safari-has-chrome-obj'; anomalyScore += 15;
            }
            if (userAgentData && userAgentData.brands) {
                flags += (flags ? ',' : '') + 'safari-has-client-hints'; anomalyScore += 10;
            }
            if (gpu.indexOf('angle') > -1 || gpu.indexOf('swiftshader') > -1) {
                flags += (flags ? ',' : '') + 'safari-chromium-gpu'; anomalyScore += 15;
            }
        }
        
        if (gpu.indexOf('swiftshader') > -1) {
            flags += (flags ? ',' : '') + 'swiftshader-gpu'; anomalyScore += 5;
            if (platform.indexOf('mac') > -1) {
                flags += ',swiftshader-on-mac'; anomalyScore += 20;
            }
            if (platform.indexOf('linux') > -1) {
                flags += ',swiftshader-on-linux'; anomalyScore += 10;
            }
        }
        if (gpu.indexOf('llvmpipe') > -1 && platform.indexOf('mac') > -1) {
            flags += (flags ? ',' : '') + 'llvmpipe-on-mac'; anomalyScore += 20;
        }
        
        if (perf.memory) {
            var heapLimit = perf.memory.jsHeapSizeLimit | 0;
            if (heapLimit === 3760000000 || heapLimit === 2330000000 ||
                (heapLimit > 0 && heapLimit % 10000000 === 0)) {
                flags += (flags ? ',' : '') + 'round-heap-limit'; anomalyScore += 5;
            }
        }
        
        if (perf.timing) {
            var t = perf.timing;
            var pageLoad = (t.loadEventEnd - t.navigationStart) | 0;
            var dns = (t.domainLookupEnd - t.domainLookupStart) | 0;
            var tcp = (t.connectEnd - t.connectStart) | 0;
            if (pageLoad > 0 && pageLoad < 50 && dns <= 1) {
                flags += (flags ? ',' : '') + 'instant-page-load'; anomalyScore += 5;
            }
            if (tcp <= 1 && pageLoad > 0 && pageLoad < 100) {
                flags += (flags ? ',' : '') + 'zero-latency-connection'; anomalyScore += 3;
            }
        }
        
        if ((c.effectiveType || '') === '4g' && (c.downlink || 0) > 5 && !(c.rtt > 0)) {
            flags += (flags ? ',' : '') + 'connection-missing-rtt'; anomalyScore += 5;
        }
        
        if (data.webgl2 && isSafariUA) {
            var safariMatch = ua.match(/version\/(\d+)/);
            if (safariMatch && (safariMatch[1] | 0) < 15) {
                flags += (flags ? ',' : '') + 'webgl2-on-old-safari'; anomalyScore += 10;
            }
        }
        
        var isMacGPU = gpu.indexOf('intel iris') > -1 || gpu.indexOf('apple m') > -1 ||
                       gpu.indexOf('apple gpu') > -1;
        var isMacPlatform = platform.indexOf('mac') > -1;
        if (isMacGPU && !isMacPlatform && gpu) {
            flags += (flags ? ',' : '') + 'gpu-platform-mismatch'; anomalyScore += 15;
        }
        if (isMacPlatform && gpu && (gpu.indexOf('swiftshader') > -1 ||
            gpu.indexOf('llvmpipe') > -1 || gpu.indexOf('mesa') > -1)) {
            if (flags.indexOf('swiftshader-on-mac') < 0 && flags.indexOf('llvmpipe-on-mac') < 0) {
                flags += (flags ? ',' : '') + 'software-gpu-on-mac'; anomalyScore += 10;
            }
        }
        
        data.crossSignals = flags;
        data.anomalyScore = anomalyScore;
        
        data.combinedThreatScore = (data.botScore || 0) + Math.min(anomalyScore, 25);
        
        data.conn = c.effectiveType || '';
        data.dl = c.downlink || '';
        data.dlMax = c.downlinkMax || '';
        data.rtt = c.rtt || '';
        data.save = c.saveData ? 1 : 0;
        data.connType = c.type || '';
        
        data.url = location.href;
        data.ref = document.referrer;
        data.hist = history.length;
        data.title = document.title;
        data.domain = location.hostname;
        data.path = location.pathname;
        data.hash = location.hash;
        data.protocol = location.protocol;
        
        if (perf.timing) {
            var t = perf.timing;
            data.loadTime = t.loadEventEnd - t.navigationStart;
            data.domTime = t.domContentLoadedEventEnd - t.navigationStart;
            data.dnsTime = t.domainLookupEnd - t.domainLookupStart;
            data.tcpTime = t.connectEnd - t.connectStart;
            data.ttfb = t.responseStart - t.requestStart;
        }
        
        data.ls = (function() { try { return !!w.localStorage; } catch(e) { return 0; } })() ? 1 : 0;
        data.ss = (function() { try { return !!w.sessionStorage; } catch(e) { return 0; } })() ? 1 : 0;
        data.idb = !!w.indexedDB ? 1 : 0;
        data.caches = !!w.caches ? 1 : 0;
        
        data.ww = !!w.Worker ? 1 : 0;
        data.swk = !!safeGet(n, 'serviceWorker', null) ? 1 : 0;
        data.wasm = typeof WebAssembly === 'object' ? 1 : 0;
        data.webgl = (function() { try { return !!document.createElement('canvas').getContext('webgl'); } catch(e) { return 0; } })() ? 1 : 0;
        data.webgl2 = (function() { try { return !!document.createElement('canvas').getContext('webgl2'); } catch(e) { return 0; } })() ? 1 : 0;
        data.canvas = !!document.createElement('canvas').getContext ? 1 : 0;
        data.touchEvent = 'ontouchstart' in w ? 1 : 0;
        data.pointerEvent = !!w.PointerEvent ? 1 : 0;
        data.mediaDevices = !!safeGet(n, 'mediaDevices', null) ? 1 : 0;
        var clipboardObj = safeGet(n, 'clipboard', null);
        data.clipboard = !!(clipboardObj && clipboardObj.writeText) ? 1 : 0;
        data.speechSynth = !!w.speechSynthesis ? 1 : 0;
        
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
        
        data.docCharset = document.characterSet || '';
        data.docCompat = document.compatMode || '';
        data.docReady = document.readyState || '';
        data.docHidden = document.hidden ? 1 : 0;
        data.docVisibility = document.visibilityState || '';
        
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
        
        data.cssFontVariant = (function() {
            try {
                var el = document.createElement('span');
                el.style.cssText = 'position:absolute;left:-9999px;font-size:16px;';
                el.innerHTML = 'AaBbCcDdEeFfGg0123456789';
                document.body.appendChild(el);
                
                var cs = w.getComputedStyle(el);
                var parts = [];
                var props = [
                    'fontVariantLigatures',
                    'fontVariantCaps',
                    'fontVariantNumeric',
                    'fontVariantEastAsian',
                    'fontFeatureSettings',
                    'fontKerning',
                    'fontStretch',
                    'fontSizeAdjust',
                    'fontOpticalSizing',
                    'fontSynthesis'
                ];
                for (var i = 0; i < props.length; i++) {
                    var val = cs[props[i]];
                    parts.push(val !== undefined && val !== '' ? val.slice(0,4) : '_');
                }
                parts.push(el.offsetWidth);
                
                document.body.removeChild(el);
                return parts.join('|');
            } catch(e) { return ''; }
        })();
        
        data.errorFP = (function() {
            try { null[0](); } catch(e) { return e.message.length + (e.stack ? e.stack.length : 0); }
            return '';
        })();
        
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
        
        data.evasionSignalsV2 = (function() {
            var det = [];
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
            var mLen = moves.length;
            data.mouseMoves = mLen;
            data.scrolled = mouseData.scrolled;
            data.scrollY = mouseData.scrollY;
            
            if (mouseData.scrolled && mouseData.scrollY === 0) {
                data.scrollContradiction = 1;
                var cs = data.crossSignals || '';
                data.crossSignals = cs ? cs + ',scroll-no-depth' : 'scroll-no-depth';
                data.anomalyScore = (data.anomalyScore | 0) + 8;
            } else {
                data.scrollContradiction = 0;
            }
            
            if (mLen < 5) { data.mouseEntropy = 0; data.behavioralFlags = ''; return; }
            
            var aSum = 0, aSq = 0, tSum = 0, tSq = 0, sSum = 0, sSq = 0, n = 0;
            for (var i = 1; i < mLen; i++) {
                var dx = moves[i].x - moves[i-1].x;
                var dy = moves[i].y - moves[i-1].y;
                var dt = moves[i].t - moves[i-1].t;
                if (dt > 0) {
                    var a = Math.atan2(dy, dx);
                    aSum += a; aSq += a * a;
                    var spd = Math.sqrt(dx * dx + dy * dy) / dt;
                    sSum += spd; sSq += spd * spd;
                    tSum += dt; tSq += dt * dt;
                    n++;
                }
            }
            
            var _bf = '';
            
            if (n > 1) {
                var aM = aSum / n;
                data.mouseEntropy = (aSq / n - aM * aM) * 1000 + 0.5 | 0;
                
                if (n > 3) {
                    var tM = tSum / n;
                    var tV = tSq / n - tM * tM;
                    if (tV > 0 && tM > 0) {
                        var tCV = Math.sqrt(tV) / tM;
                        data.moveTimingCV = tCV * 1000 + 0.5 | 0;
                        if (tCV < 0.3 && n > 5) {
                            _bf = 'uniform-timing';
                            data.anomalyScore = (data.anomalyScore | 0) + 5;
                        }
                    }
                    var sM = sSum / n;
                    var sV = sSq / n - sM * sM;
                    if (sV > 0 && sM > 0) {
                        var sCV = Math.sqrt(sV) / sM;
                        data.moveSpeedCV = sCV * 1000 + 0.5 | 0;
                        if (sCV < 0.2 && n > 5) {
                            _bf += (_bf ? ',' : '') + 'uniform-speed';
                            data.anomalyScore = (data.anomalyScore | 0) + 5;
                        }
                    }
                }
                
                data.moveCountBucket = mLen < 5 ? 'low' : mLen < 20 ? 'mid' : mLen < 50 ? 'high' : 'very-high';
                
            } else { data.mouseEntropy = 0; }
            
            data.behavioralFlags = _bf;
            if (_bf) {
                var cs2 = data.crossSignals || '';
                data.crossSignals = cs2 ? cs2 + ',' + _bf : _bf;
            }
            
            // Serialize raw mouse path as compact x,y,t|x,y,t|... string (max 2000 chars)
            if (mLen > 0) {
                var mp = '';
                for (var j = 0; j < mLen; j++) {
                    var pt = moves[j].x + ',' + moves[j].y + ',' + moves[j].t;
                    if (mp.length + pt.length + 1 > 2000) break;
                    if (j > 0) mp += '|';
                    mp += pt;
                }
                data.mousePath = mp;
            }
            
            document.removeEventListener('mousemove', mouseHandler);
            document.removeEventListener('scroll', scrollHandler);
        };
        
        var sendPiXL = function() {
            calculateMouseEntropy();
            var params = [];
            for (var key in data) {
                if (data[key] !== '' && data[key] !== null && data[key] !== undefined) {
                    params.push(key + '=' + encodeURIComponent(data[key]));
                }
            }
            new Image().src = '{{PIXL_URL}}?' + params.join('&');
        };
        
        var asyncPromises = [];
        if (typeof audioPromise !== 'undefined') asyncPromises.push(audioPromise);
        
        // Send PiXL after async work completes, with a 500ms safety cap.
        // - If all promises resolve before 500ms → send immediately (no padding delay)
        // - If any promise hangs → 500ms timeout guarantees delivery
        // - If Promise.allSettled is unavailable (IE11) → 500ms fallback
        // Critical: the PiXL MUST fire. Never block the client site.
        var timeoutPromise = new Promise(function(resolve) { setTimeout(resolve, 500); });
        
        if (asyncPromises.length > 0 && Promise.allSettled) {
            Promise.race([
                Promise.allSettled(asyncPromises),
                timeoutPromise
            ]).then(sendPiXL);
        } else {
            // No async work or no allSettled support — just send after 500ms
            // to let synchronous data collection settle (stealth signals, etc.)
            timeoutPromise.then(sendPiXL);
        }
        
    } catch (e) {
        new Image().src = '{{PIXL_URL}}?error=1&msg=' + encodeURIComponent(e.message);
    }
})();
";

    // Cache per PiXL URL — zero allocation after first hit per companyId/pixlId combo.
    // Capped at 10,000 entries to prevent memory exhaustion from malicious URL generation.
    private static readonly ConcurrentDictionary<string, string> _cache = new();
    private const int MaxCacheEntries = 10_000;

    /// <summary>
    /// Returns script with PiXL URL injected. Cached per URL.
    /// Uses simple Replace for correctness — cached so only runs once per URL.
    /// Evicts the entire cache if it grows beyond MaxCacheEntries to bound memory.
    /// </summary>
    public static string GetScript(string pixlUrl)
    {
        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear(); // Nuclear eviction — acceptable since warm-up is cheap
        }
        return _cache.GetOrAdd(pixlUrl, url => Template.Replace("{{PIXL_URL}}", url));
    }
}
