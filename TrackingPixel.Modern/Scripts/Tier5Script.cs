namespace TrackingPixel.Scripts;

/// <summary>
/// Contains the Tier 5 JavaScript template for maximum data collection.
/// Stored as a constant string - allocated once at startup.
/// </summary>
public static class Tier5Script
{
    /// <summary>
    /// JavaScript template with {0} placeholder for pixel URL.
    /// Use string.Format(Template, pixelUrl) to generate per-request.
    /// </summary>
    public const string Template = @"
// SmartPiXL Tracking Script - Tier 5 (Maximum Data Collection)
(function() {
    try {
        var s = screen;
        var n = navigator;
        var w = window;
        var d = new Date();
        var c = n.connection || {};
        var perf = w.performance || {};
        var data = {};
        
        // ============================================
        // CANVAS FINGERPRINT
        // ============================================
        data.canvasFP = (function() {
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
                var hash = 0, str = canvas.toDataURL();
                for (var i = 0; i < str.length; i++) { hash = ((hash << 5) - hash) + str.charCodeAt(i); hash = hash & hash; }
                return Math.abs(hash).toString(16);
            } catch(e) { return ''; }
        })();
        
        // ============================================
        // WEBGL FINGERPRINT
        // ============================================
        var webglData = (function() {
            try {
                var canvas = document.createElement('canvas');
                var gl = canvas.getContext('webgl') || canvas.getContext('experimental-webgl');
                if (!gl) return { fp: '', gpu: '', gpuVendor: '', params: '' };
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
                var hash = 0, str = params.join('|') + extensions.join(',');
                for (var i = 0; i < str.length; i++) { hash = ((hash << 5) - hash) + str.charCodeAt(i); hash = hash & hash; }
                return {
                    fp: Math.abs(hash).toString(16),
                    gpu: ext ? gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) : '',
                    gpuVendor: ext ? gl.getParameter(ext.UNMASKED_VENDOR_WEBGL) : '',
                    params: params.slice(0, 5).join('|'),
                    extensions: extensions.length
                };
            } catch(e) { return { fp: '', gpu: '', gpuVendor: '', params: '', extensions: 0 }; }
        })();
        data.webglFP = webglData.fp;
        data.gpu = webglData.gpu;
        data.gpuVendor = webglData.gpuVendor;
        data.webglParams = webglData.params;
        data.webglExt = webglData.extensions;
        
        // ============================================
        // AUDIO FINGERPRINT (OfflineAudioContext)
        // ============================================
        data.audioFP = (function() {
            try {
                var ctx = new (w.OfflineAudioContext || w.webkitOfflineAudioContext)(1, 44100, 44100);
                var osc = ctx.createOscillator();
                var comp = ctx.createDynamicsCompressor();
                osc.type = 'triangle';
                osc.frequency.value = 10000;
                comp.threshold.value = -50;
                comp.knee.value = 40;
                comp.ratio.value = 12;
                comp.attack.value = 0;
                comp.release.value = 0.25;
                osc.connect(comp);
                comp.connect(ctx.destination);
                osc.start(0);
                ctx.startRendering();
                var hash = ctx.length.toString();
                return hash;
            } catch(e) { return ''; }
        })();
        
        // ============================================
        // FONT DETECTION
        // ============================================
        data.fonts = (function() {
            var testFonts = [
                'Arial','Arial Black','Arial Narrow','Verdana','Times New Roman','Courier New',
                'Georgia','Comic Sans MS','Impact','Trebuchet MS','Lucida Console','Tahoma',
                'Palatino Linotype','Segoe UI','Calibri','Cambria','Consolas','Helvetica',
                'Monaco','Menlo','Ubuntu','Roboto','Open Sans','Lato','Montserrat',
                'Source Sans Pro','Droid Sans','Century Gothic','Futura','Gill Sans',
                'Lucida Grande','Optima','Book Antiqua','Garamond','Bookman Old Style',
                'MS Gothic','MS PGothic','MS Mincho','SimSun','SimHei','Microsoft YaHei',
                'Malgun Gothic','Apple Color Emoji','Segoe UI Emoji','Noto Color Emoji'
            ];
            var detected = [];
            var baseline = 'monospace';
            var span = document.createElement('span');
            span.style.cssText = 'position:absolute;left:-9999px;font-size:72px;';
            span.innerHTML = 'mmmmmmmmmmlli';
            document.body.appendChild(span);
            span.style.fontFamily = baseline;
            var baseWidth = span.offsetWidth;
            var baseHeight = span.offsetHeight;
            for (var i = 0; i < testFonts.length; i++) {
                span.style.fontFamily = testFonts[i] + ',' + baseline;
                if (span.offsetWidth !== baseWidth || span.offsetHeight !== baseHeight) detected.push(testFonts[i]);
            }
            document.body.removeChild(span);
            return detected.join(',');
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
        // STORAGE ESTIMATION
        // ============================================
        (function() {
            try {
                if (n.storage && n.storage.estimate) {
                    n.storage.estimate().then(function(est) {
                        data.storageQuota = Math.round((est.quota || 0) / 1073741824);
                        data.storageUsed = Math.round((est.usage || 0) / 1048576);
                    });
                }
            } catch(e) {}
        })();
        
        // ============================================
        // GAMEPAD DETECTION
        // ============================================
        data.gamepads = (function() {
            try {
                var gp = n.getGamepads ? n.getGamepads() : [];
                var found = [];
                for (var i = 0; i < gp.length; i++) {
                    if (gp[i]) found.push(gp[i].id);
                }
                return found.join('|');
            } catch(e) { return ''; }
        })();
        
        // ============================================
        // BATTERY STATUS
        // ============================================
        (function() {
            try {
                if (n.getBattery) {
                    n.getBattery().then(function(b) {
                        data.batteryLevel = Math.round(b.level * 100);
                        data.batteryCharging = b.charging ? 1 : 0;
                    });
                }
            } catch(e) {}
        })();
        
        // ============================================
        // MEDIA DEVICES COUNT
        // ============================================
        (function() {
            try {
                if (n.mediaDevices && n.mediaDevices.enumerateDevices) {
                    n.mediaDevices.enumerateDevices().then(function(devices) {
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
        // TIME & LOCALE
        // ============================================
        data.tz = Intl.DateTimeFormat().resolvedOptions().timeZone;
        data.tzo = d.getTimezoneOffset();
        data.ts = d.getTime();
        data.lang = n.language;
        data.langs = (n.languages || []).join(',');
        
        // ============================================
        // DEVICE & BROWSER
        // ============================================
        data.plt = n.platform || '';
        data.vnd = n.vendor || '';
        data.ua = n.userAgent || '';
        data.cores = n.hardwareConcurrency || '';
        data.mem = n.deviceMemory || '';
        data.touch = n.maxTouchPoints || 0;
        data.product = n.product || '';
        data.productSub = n.productSub || '';
        data.vendorSub = n.vendorSub || '';
        data.appName = n.appName || '';
        data.appVersion = n.appVersion || '';
        data.appCodeName = n.appCodeName || '';
        
        // ============================================
        // BROWSER CAPABILITIES
        // ============================================
        data.ck = n.cookieEnabled ? 1 : 0;
        data.dnt = n.doNotTrack || '';
        data.pdf = n.pdfViewerEnabled ? 1 : 0;
        data.webdr = n.webdriver ? 1 : 0;
        data.online = n.onLine ? 1 : 0;
        data.java = n.javaEnabled ? (n.javaEnabled() ? 1 : 0) : 0;
        data.plugins = n.plugins ? n.plugins.length : 0;
        data.mimeTypes = n.mimeTypes ? n.mimeTypes.length : 0;
        
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
        // FEATURE DETECTION
        // ============================================
        data.ww = !!w.Worker ? 1 : 0;
        data.swk = !!n.serviceWorker ? 1 : 0;
        data.wasm = typeof WebAssembly === 'object' ? 1 : 0;
        data.webgl = (function() { try { return !!document.createElement('canvas').getContext('webgl'); } catch(e) { return 0; } })() ? 1 : 0;
        data.webgl2 = (function() { try { return !!document.createElement('canvas').getContext('webgl2'); } catch(e) { return 0; } })() ? 1 : 0;
        data.canvas = !!document.createElement('canvas').getContext ? 1 : 0;
        data.touchEvent = 'ontouchstart' in w ? 1 : 0;
        data.pointerEvent = !!w.PointerEvent ? 1 : 0;
        data.mediaDevices = !!n.mediaDevices ? 1 : 0;
        data.bluetooth = !!n.bluetooth ? 1 : 0;
        data.usb = !!n.usb ? 1 : 0;
        data.serial = !!n.serial ? 1 : 0;
        data.hid = !!n.hid ? 1 : 0;
        data.midi = !!n.requestMIDIAccess ? 1 : 0;
        data.xr = !!n.xr ? 1 : 0;
        data.share = !!n.share ? 1 : 0;
        data.clipboard = !!(n.clipboard && n.clipboard.writeText) ? 1 : 0;
        data.credentials = !!n.credentials ? 1 : 0;
        data.geolocation = !!n.geolocation ? 1 : 0;
        data.notifications = !!w.Notification ? 1 : 0;
        data.push = !!(w.PushManager) ? 1 : 0;
        data.payment = !!w.PaymentRequest ? 1 : 0;
        data.speechRecog = !!(w.SpeechRecognition || w.webkitSpeechRecognition) ? 1 : 0;
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
        // ERROR HANDLING FINGERPRINT
        // ============================================
        data.errorFP = (function() {
            try { null[0](); } catch(e) { return e.message.length + (e.stack ? e.stack.length : 0); }
            return '';
        })();
        
        // TIER ID
        data.tier = 5;
        
        // ============================================
        // FIRE PIXEL (with delay for async data)
        // ============================================
        setTimeout(function() {
            var params = [];
            for (var key in data) {
                if (data[key] !== '' && data[key] !== null && data[key] !== undefined) {
                    params.push(key + '=' + encodeURIComponent(data[key]));
                }
            }
            new Image().src = '{0}?' + params.join('&');
        }, 100);
        
    } catch (e) {
        new Image().src = '{0}?error=1&msg=' + encodeURIComponent(e.message);
    }
})();
";
}
