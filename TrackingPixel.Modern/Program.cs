using Microsoft.Data.SqlClient;
using System.Collections.Concurrent;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;

// ============================================================================
// PRE-COMPUTED TEMPLATES (allocated once at startup, not per-request)
// ============================================================================

// Regex for cleaning referer strings (compiled once)
var refererCleanupRegex = new Regex(
    @"%26amp|%3Bamp|%23x3b|%3Bx3b|%23x23|%3Bx23|%3Bx2f|%3Bx3a|%26|%3B",
    RegexOptions.Compiled);

// DataTable schema template (clone this per batch instead of rebuilding)
var dataTableTemplate = CreateDataTableTemplate();

// JavaScript template for Tier 5 (allocated once, string.Format per request)
// Using {0} placeholder for pixelUrl - much cheaper than $"" interpolation
const string tier5JsTemplate = @"
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
        // AUDIO FINGERPRINT (OfflineAudioContext - better)
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
        // FONT DETECTION (expanded list)
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
        // MATH FINGERPRINT (float precision varies)
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

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel to listen on HTTP and HTTPS
// HTTPS is required for Client Hints (Sec-CH-UA-*) from non-localhost
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5000); // HTTP
    options.ListenAnyIP(5001, listenOptions => listenOptions.UseHttps()); // HTTPS with dev cert
});

// Add CORS - allows any website to request the pixel
builder.Services.AddCors();

var app = builder.Build();

// Enable CORS for all origins (required for cross-site pixel requests)
app.UseCors(policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod());

// Request Client Hints from browsers on ALL responses
// Without this, browsers only send Sec-CH-UA-* headers to localhost
app.Use(async (context, next) =>
{
    context.Response.Headers["Accept-CH"] = "Sec-CH-UA, Sec-CH-UA-Mobile, Sec-CH-UA-Platform, Sec-CH-UA-Platform-Version, Sec-CH-UA-Full-Version-List, Sec-CH-UA-Arch, Sec-CH-UA-Model, Sec-CH-UA-Bitness";
    await next();
});

// Serve static files from wwwroot folder
app.UseStaticFiles();

// ============================================================================
// CONFIGURATION
// ============================================================================
// Using Windows Authentication (Integrated Security) for local dev
string connectionString = "Server=localhost;Database=SmartPixl;Integrated Security=True;TrustServerCertificate=True";

// Pre-generate the 1x1 transparent GIF (43 bytes) - optional, you could also just return 204
byte[] transparentGif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

// Background queue for fire-and-forget DB writes
var writeQueue = new BlockingCollection<TrackingData>(boundedCapacity: 10000);

// Start background worker for database writes
var dbWorker = Task.Run(() => DatabaseWriterLoop(writeQueue, connectionString));

// ============================================================================
// DEBUG ENDPOINT - Shows all captured data (register BEFORE catch-all route)
// ============================================================================
app.MapGet("/debug/headers", (HttpContext ctx) =>
{
    var data = CaptureTrackingData(ctx.Request);
    return Results.Json(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
});

// TEST PAGE - Serve the test HTML directly
// ============================================================================
app.MapGet("/test", async (HttpContext ctx) => 
{
    var filePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "test.html");
    if (!File.Exists(filePath))
        filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "test.html");
    
    if (File.Exists(filePath))
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.SendFileAsync(filePath);
    }
    else
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("Test page not found. Looking for: " + filePath);
    }
});

// ============================================================================
// TIER 5: JAVASCRIPT FILE ENDPOINT
// ============================================================================
// This serves a JavaScript file that collects client-side data and fires the pixel
// Client installs: <script src="https://smartpixl.com/js/12346/00009.js" async></script>
// 
// FLOW:
// 1. Browser requests /js/12346/00009.js
// 2. We return JavaScript code (not a .gif!)
// 3. Browser EXECUTES the JavaScript
// 4. JavaScript collects screen size, CPU cores, timezone, etc.
// 5. JavaScript creates: new Image().src = "/12346/00009.gif?sw=1920&cores=8..."
// 6. Browser requests the .gif (hits our pixel endpoint below)
// 7. We capture headers + query params, return 1x1 gif
// ============================================================================
app.MapGet("/js/{clientId}/{campaignId}.js", (HttpContext ctx, string clientId, string campaignId) =>
{
    // Build the pixel URL (only dynamic part per-request)
    var pixelUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/{clientId}/{campaignId}_SMART.GIF";
    
    // Single string.Format instead of giant $"" interpolation
    var javascript = string.Format(tier5JsTemplate, pixelUrl);

    // Return as JavaScript with appropriate headers
    ctx.Response.ContentType = "application/javascript; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
    
    return Results.Text(javascript, "application/javascript");
});

// ============================================================================
// THE TRACKING ENDPOINT - This is the entire "pixel" handler
// ============================================================================

// Route pattern matches your existing URL structure: /12346/00009_VandergriffHonda.com_SMART.GIF
// The {**path} captures the whole path so IIS rewrite rules aren't needed
app.MapGet("/{**path}", (HttpContext ctx) =>
{
    // Capture all the data we need from the request
    var trackingData = CaptureTrackingData(ctx.Request);
    
    // Queue it for background write - doesn't block the response
    writeQueue.TryAdd(trackingData);
    
    // Return 1x1 transparent GIF immediately
    ctx.Response.ContentType = "image/gif";
    ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    ctx.Response.Headers.Pragma = "no-cache";
    ctx.Response.Headers["Expires"] = "0";
    
    // Request high-entropy Client Hints for future requests
    ctx.Response.Headers["Accept-CH"] = "Sec-CH-UA-Full-Version-List, Sec-CH-UA-Platform-Version, Sec-CH-UA-Arch, Sec-CH-UA-Model, Sec-CH-UA-Bitness";
    
    return Results.Bytes(transparentGif, "image/gif");
});

// Alternative endpoint that returns HTTP 204 No Content (even faster, no body at all)
// Your old code essentially did this - returned nothing
app.MapGet("/pixel204/{**path}", (HttpContext ctx) =>
{
    var trackingData = CaptureTrackingData(ctx.Request);
    writeQueue.TryAdd(trackingData);
    
    ctx.Response.Headers["Accept-CH"] = "Sec-CH-UA-Full-Version-List, Sec-CH-UA-Platform-Version, Sec-CH-UA-Arch, Sec-CH-UA-Model";
    return Results.NoContent(); // HTTP 204
});

Console.WriteLine("SmartPixl Tracking Server running on http://localhost:5000");
Console.WriteLine("Test it: http://localhost:5000/12346/00009_Test_SMART.GIF");
Console.WriteLine("Debug:   http://localhost:5000/debug/headers");
Console.WriteLine("Press Ctrl+C to stop");

app.Run();

// ============================================================================
// DATA CAPTURE - Equivalent to your Page_Load logic
// ============================================================================

TrackingData CaptureTrackingData(HttpRequest request)
{
    var headers = request.Headers;
    var connection = request.HttpContext.Connection;
    
    // Parse referer (same logic as your old code)
    string? referer = headers.Referer.ToString();
    string? refererRoot = null;
    string? refererQuery = null;
    
    if (!string.IsNullOrEmpty(referer))
    {
        // Clean up encoded characters (single regex pass, no intermediate strings)
        if (referer.Contains('%'))
        {
            referer = refererCleanupRegex.Replace(referer, "");
        }
        
        var parts = referer.Split('?', 2);
        refererRoot = parts[0];
        refererQuery = parts.Length > 1 ? parts[1] : null;
    }
    
    return new TrackingData
    {
        Timestamp = DateTime.Now,
        
        // === ORIGINAL HEADERS (from your current code) ===
        RemoteAddr = connection.RemoteIpAddress?.ToString(),
        UserAgent = headers.UserAgent.ToString(),
        Referer = referer,
        RefererRoot = refererRoot,
        RefererQuery = refererQuery,
        OriginalUrl = request.Path + request.QueryString, // Replaces HTTP_X_ORIGINAL_URL
        Dnt = headers["DNT"].ToString(),
        Cookie = headers.Cookie.ToString(),
        ClientIp = headers["Client-IP"].ToString(),
        Forwarded = headers["Forwarded"].ToString(),
        From = headers["From"].ToString(),
        ProxyConnection = headers["Proxy-Connection"].ToString(),
        Via = headers["Via"].ToString(),
        XMcProxyFilter = headers["X-MCProxyFilter"].ToString(),
        XTargetProxy = headers["X-Target-Proxy"].ToString(),
        XRequestedWith = headers["X-Requested-With"].ToString(),
        AcceptLanguage = headers.AcceptLanguage.ToString(),
        
        // === NEW HEADERS - Proxy/CDN (get real IP behind load balancers) ===
        XForwardedFor = headers["X-Forwarded-For"].ToString(),
        XRealIp = headers["X-Real-IP"].ToString(),
        CfConnectingIp = headers["CF-Connecting-IP"].ToString(),      // Cloudflare
        TrueClientIp = headers["True-Client-IP"].ToString(),          // Akamai
        
        // === NEW HEADERS - Client Hints (modern browser detection) ===
        SecChUa = headers["Sec-CH-UA"].ToString(),                    // Browser brands
        SecChUaPlatform = headers["Sec-CH-UA-Platform"].ToString(),   // OS name
        SecChUaMobile = headers["Sec-CH-UA-Mobile"].ToString(),       // Is mobile?
        SecChUaModel = headers["Sec-CH-UA-Model"].ToString(),         // Device model
        SecChUaPlatformVersion = headers["Sec-CH-UA-Platform-Version"].ToString(),
        SecChUaArch = headers["Sec-CH-UA-Arch"].ToString(),           // CPU architecture
        SecChUaBitness = headers["Sec-CH-UA-Bitness"].ToString(),     // 32 or 64 bit
        
        // === NEW HEADERS - Sec-Fetch (request context) ===
        SecFetchSite = headers["Sec-Fetch-Site"].ToString(),          // same-origin, cross-site, etc.
        SecFetchMode = headers["Sec-Fetch-Mode"].ToString(),          // navigate, cors, no-cors
        SecFetchDest = headers["Sec-Fetch-Dest"].ToString(),          // document, image, script
        SecFetchUser = headers["Sec-Fetch-User"].ToString(),          // User initiated?
        
        // === NEW HEADERS - Additional useful ones ===
        Accept = headers.Accept.ToString(),
        AcceptEncoding = headers.AcceptEncoding.ToString(),
        Connection = headers.Connection.ToString(),
        CacheControl = headers.CacheControl.ToString(),
        UpgradeInsecureRequests = headers["Upgrade-Insecure-Requests"].ToString(),
        
        // === QUERY STRING PARAMETERS (for enhanced JS script) ===
        // These come from: new Image().src = "...?sw=1920&sh=1080&tz=America/Chicago..."
        ScreenWidth = request.Query["sw"].ToString(),
        ScreenHeight = request.Query["sh"].ToString(),
        ColorDepth = request.Query["cd"].ToString(),
        PixelRatio = request.Query["pd"].ToString(),
        Timezone = request.Query["tz"].ToString(),
        TimezoneOffset = request.Query["tzo"].ToString(),
        Language = request.Query["lang"].ToString(),
        Platform = request.Query["plt"].ToString(),
        CpuCores = request.Query["cores"].ToString(),
        DeviceMemory = request.Query["mem"].ToString(),
        TouchSupport = request.Query["touch"].ToString(),
        ClientTimestamp = request.Query["ts"].ToString(),
        PageUrl = request.Query["url"].ToString(),
        PageReferrer = request.Query["ref"].ToString(),
        ConnectionType = request.Query["conn"].ToString(),
        ExplicitTier = request.Query["tier"].ToString(),
    };
}

// ============================================================================
// BACKGROUND DATABASE WRITER - Simplified for testing
// ============================================================================

void DatabaseWriterLoop(BlockingCollection<TrackingData> queue, string connString)
{
    // Batch writes for efficiency - collect up to 100 records or wait 100ms
    var batch = new List<TrackingData>(100);
    
    while (!queue.IsCompleted)
    {
        batch.Clear();
        
        // Get first item (blocks until available)
        if (queue.TryTake(out var first, TimeSpan.FromMilliseconds(100)))
        {
            batch.Add(first);
            
            // Grab more items if available (non-blocking)
            while (batch.Count < 100 && queue.TryTake(out var item))
            {
                batch.Add(item);
            }
            
            // Write to test table
            WriteToTestTable(batch, connString);
        }
    }
}

// Determine tier based on what query params are present
int DetectTier(TrackingData data)
{
    // Tier 5: Explicitly marked by our JS file
    if (data.ExplicitTier == "5")
        return 5;
    
    // Tier 4: Has CPU cores, device memory, touch support
    if (!string.IsNullOrEmpty(data.CpuCores) || !string.IsNullOrEmpty(data.DeviceMemory) || !string.IsNullOrEmpty(data.TouchSupport))
        return 4;
    
    // Tier 3: Has screen size and timezone
    if (!string.IsNullOrEmpty(data.ScreenWidth) && !string.IsNullOrEmpty(data.Timezone))
        return 3;
    
    // Tier 2: Has explicit page URL param
    if (!string.IsNullOrEmpty(data.PageUrl))
        return 2;
    
    // Tier 1: Headers only
    return 1;
}

void WriteToTestTable(List<TrackingData> batch, string connString)
{
    if (batch.Count == 0) return;
    
    try
    {
        // Clone the pre-built schema (no column allocations!)
        using var table = dataTableTemplate.Clone();
        
        // Track tiers for logging (avoid calling DetectTier twice per record)
        Span<int> tiers = batch.Count <= 128 ? stackalloc int[batch.Count] : new int[batch.Count];
        var tierIndex = 0;
        
        foreach (var data in batch)
        {
            var row = table.NewRow();
            
            // Compute tier once, store for logging
            var tier = DetectTier(data);
            tiers[tierIndex++] = tier;
            row["TierLevel"] = tier;
            
            // Original headers
            row["REMOTE_ADDR"] = Truncate(data.RemoteAddr, 100);
            row["HTTP_USER_AGENT"] = Truncate(data.UserAgent, 4000);
            row["HTTP_REFERER"] = Truncate(data.Referer, 2000);
            row["HTTP_REFERER_ROOT"] = Truncate(data.RefererRoot, 500);
            row["HTTP_REFERER_QUERY"] = Truncate(data.RefererQuery, 2000);
            row["HTTP_ORIGINAL_URL"] = Truncate(data.OriginalUrl, 2000);
            row["HTTP_DNT"] = Truncate(data.Dnt, 10);
            row["HTTP_COOKIE"] = Truncate(data.Cookie, 4000);
            row["HTTP_CLIENT_IP"] = Truncate(data.ClientIp, 100);
            row["HTTP_FORWARDED"] = Truncate(data.Forwarded, 500);
            row["HTTP_FROM"] = Truncate(data.From, 200);
            row["HTTP_PROXY_CONN"] = Truncate(data.ProxyConnection, 100);
            row["HTTP_VIA"] = Truncate(data.Via, 500);
            row["HTTP_X_MCPROXYFILTER"] = Truncate(data.XMcProxyFilter, 100);
            row["HTTP_X_TARGET_PROXY"] = Truncate(data.XTargetProxy, 100);
            row["HTTP_X_REQUESTED_WITH"] = Truncate(data.XRequestedWith, 100);
            row["HTTP_ACCEPT_LANGUAGE"] = Truncate(data.AcceptLanguage, 500);
            
            // Proxy/CDN headers
            row["HTTP_X_FORWARDED_FOR"] = Truncate(data.XForwardedFor, 200);
            row["HTTP_X_REAL_IP"] = Truncate(data.XRealIp, 100);
            row["CF_CONNECTING_IP"] = Truncate(data.CfConnectingIp, 100);
            row["TRUE_CLIENT_IP"] = Truncate(data.TrueClientIp, 100);
            
            // Client Hints
            row["SEC_CH_UA"] = Truncate(data.SecChUa, 500);
            row["SEC_CH_UA_PLATFORM"] = Truncate(data.SecChUaPlatform, 50);
            row["SEC_CH_UA_MOBILE"] = Truncate(data.SecChUaMobile, 10);
            row["SEC_CH_UA_MODEL"] = Truncate(data.SecChUaModel, 100);
            row["SEC_CH_UA_PLATFORM_VERSION"] = Truncate(data.SecChUaPlatformVersion, 50);
            row["SEC_CH_UA_ARCH"] = Truncate(data.SecChUaArch, 50);
            row["SEC_CH_UA_BITNESS"] = Truncate(data.SecChUaBitness, 10);
            
            // Sec-Fetch
            row["SEC_FETCH_SITE"] = Truncate(data.SecFetchSite, 50);
            row["SEC_FETCH_MODE"] = Truncate(data.SecFetchMode, 50);
            row["SEC_FETCH_DEST"] = Truncate(data.SecFetchDest, 50);
            row["SEC_FETCH_USER"] = Truncate(data.SecFetchUser, 10);
            
            // Additional headers
            row["HTTP_ACCEPT"] = Truncate(data.Accept, 500);
            row["HTTP_ACCEPT_ENCODING"] = Truncate(data.AcceptEncoding, 200);
            row["HTTP_CONNECTION"] = Truncate(data.Connection, 50);
            row["HTTP_CACHE_CONTROL"] = Truncate(data.CacheControl, 100);
            row["UPGRADE_INSECURE_REQ"] = Truncate(data.UpgradeInsecureRequests, 10);
            
            // Client-side data
            row["PAGE_URL"] = Truncate(data.PageUrl, 2000);
            row["PAGE_REFERRER"] = Truncate(data.PageReferrer, 2000);
            row["SCREEN_WIDTH"] = Truncate(data.ScreenWidth, 20);
            row["SCREEN_HEIGHT"] = Truncate(data.ScreenHeight, 20);
            row["COLOR_DEPTH"] = Truncate(data.ColorDepth, 10);
            row["PIXEL_RATIO"] = Truncate(data.PixelRatio, 20);
            row["TIMEZONE"] = Truncate(data.Timezone, 100);
            row["TIMEZONE_OFFSET"] = Truncate(data.TimezoneOffset, 20);
            row["LANGUAGE"] = Truncate(data.Language, 50);
            row["PLATFORM"] = Truncate(data.Platform, 50);
            row["CPU_CORES"] = Truncate(data.CpuCores, 10);
            row["DEVICE_MEMORY"] = Truncate(data.DeviceMemory, 10);
            row["TOUCH_SUPPORT"] = Truncate(data.TouchSupport, 10);
            row["CLIENT_TIMESTAMP"] = Truncate(data.ClientTimestamp, 50);
            row["CONNECTION_TYPE"] = Truncate(data.ConnectionType, 20);
            row["EXPLICIT_TIER"] = Truncate(data.ExplicitTier, 10);
            
            table.Rows.Add(row);
        }
        
        using var bulkCopy = new SqlBulkCopy(connString);
        bulkCopy.DestinationTableName = "dbo.PiXL_Test";
        bulkCopy.BatchSize = batch.Count;
        bulkCopy.BulkCopyTimeout = 60;
        
        foreach (DataColumn col in table.Columns)
        {
            bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        }
        
        bulkCopy.WriteToServer(table);
        
        // Use pre-computed tiers (no LINQ, no re-computation)
        var tierLog = new StringBuilder(batch.Count * 2);
        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0) tierLog.Append(',');
            tierLog.Append(tiers[i]);
        }
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Wrote {batch.Count} records (Tiers: {tierLog})");
    }
    catch (Exception ex)
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "Log");
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, $"{DateTime.Now:yyyy_MM_dd}.log");
        File.AppendAllText(logFile, $"[{DateTime.Now}] ERROR: {ex}\n");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR: {ex.Message}");
    }
}

// Truncate strings to fit column sizes
string? Truncate(string? value, int maxLength) => 
    string.IsNullOrEmpty(value) ? value : (value.Length <= maxLength ? value : value[..maxLength]);

// ============================================================================
// DATATABLE TEMPLATE - Schema defined once, cloned per batch
// ============================================================================
DataTable CreateDataTableTemplate()
{
    var table = new DataTable();
    
    // Meta
    table.Columns.Add("TierLevel", typeof(int));
    
    // Original headers
    table.Columns.Add("REMOTE_ADDR");
    table.Columns.Add("HTTP_USER_AGENT");
    table.Columns.Add("HTTP_REFERER");
    table.Columns.Add("HTTP_REFERER_ROOT");
    table.Columns.Add("HTTP_REFERER_QUERY");
    table.Columns.Add("HTTP_ORIGINAL_URL");
    table.Columns.Add("HTTP_DNT");
    table.Columns.Add("HTTP_COOKIE");
    table.Columns.Add("HTTP_CLIENT_IP");
    table.Columns.Add("HTTP_FORWARDED");
    table.Columns.Add("HTTP_FROM");
    table.Columns.Add("HTTP_PROXY_CONN");
    table.Columns.Add("HTTP_VIA");
    table.Columns.Add("HTTP_X_MCPROXYFILTER");
    table.Columns.Add("HTTP_X_TARGET_PROXY");
    table.Columns.Add("HTTP_X_REQUESTED_WITH");
    table.Columns.Add("HTTP_ACCEPT_LANGUAGE");
    
    // Proxy/CDN headers
    table.Columns.Add("HTTP_X_FORWARDED_FOR");
    table.Columns.Add("HTTP_X_REAL_IP");
    table.Columns.Add("CF_CONNECTING_IP");
    table.Columns.Add("TRUE_CLIENT_IP");
    
    // Client Hints
    table.Columns.Add("SEC_CH_UA");
    table.Columns.Add("SEC_CH_UA_PLATFORM");
    table.Columns.Add("SEC_CH_UA_MOBILE");
    table.Columns.Add("SEC_CH_UA_MODEL");
    table.Columns.Add("SEC_CH_UA_PLATFORM_VERSION");
    table.Columns.Add("SEC_CH_UA_ARCH");
    table.Columns.Add("SEC_CH_UA_BITNESS");
    
    // Sec-Fetch
    table.Columns.Add("SEC_FETCH_SITE");
    table.Columns.Add("SEC_FETCH_MODE");
    table.Columns.Add("SEC_FETCH_DEST");
    table.Columns.Add("SEC_FETCH_USER");
    
    // Additional headers
    table.Columns.Add("HTTP_ACCEPT");
    table.Columns.Add("HTTP_ACCEPT_ENCODING");
    table.Columns.Add("HTTP_CONNECTION");
    table.Columns.Add("HTTP_CACHE_CONTROL");
    table.Columns.Add("UPGRADE_INSECURE_REQ");
    
    // Client-side data
    table.Columns.Add("PAGE_URL");
    table.Columns.Add("PAGE_REFERRER");
    table.Columns.Add("SCREEN_WIDTH");
    table.Columns.Add("SCREEN_HEIGHT");
    table.Columns.Add("COLOR_DEPTH");
    table.Columns.Add("PIXEL_RATIO");
    table.Columns.Add("TIMEZONE");
    table.Columns.Add("TIMEZONE_OFFSET");
    table.Columns.Add("LANGUAGE");
    table.Columns.Add("PLATFORM");
    table.Columns.Add("CPU_CORES");
    table.Columns.Add("DEVICE_MEMORY");
    table.Columns.Add("TOUCH_SUPPORT");
    table.Columns.Add("CLIENT_TIMESTAMP");
    table.Columns.Add("CONNECTION_TYPE");
    table.Columns.Add("EXPLICIT_TIER");
    
    return table;
}

// ============================================================================
// DATA MODEL - All the fields we capture
// ============================================================================

record TrackingData
{
    public DateTime Timestamp { get; init; }
    
    // Original fields
    public string? RemoteAddr { get; init; }
    public string? UserAgent { get; init; }
    public string? Referer { get; init; }
    public string? RefererRoot { get; init; }
    public string? RefererQuery { get; init; }
    public string? OriginalUrl { get; init; }
    public string? Dnt { get; init; }
    public string? Cookie { get; init; }
    public string? ClientIp { get; init; }
    public string? Forwarded { get; init; }
    public string? From { get; init; }
    public string? ProxyConnection { get; init; }
    public string? Via { get; init; }
    public string? XMcProxyFilter { get; init; }
    public string? XTargetProxy { get; init; }
    public string? XRequestedWith { get; init; }
    public string? AcceptLanguage { get; init; }
    
    // New: Proxy/CDN headers
    public string? XForwardedFor { get; init; }
    public string? XRealIp { get; init; }
    public string? CfConnectingIp { get; init; }
    public string? TrueClientIp { get; init; }
    
    // New: Client Hints
    public string? SecChUa { get; init; }
    public string? SecChUaPlatform { get; init; }
    public string? SecChUaMobile { get; init; }
    public string? SecChUaModel { get; init; }
    public string? SecChUaPlatformVersion { get; init; }
    public string? SecChUaArch { get; init; }
    public string? SecChUaBitness { get; init; }
    
    // New: Sec-Fetch
    public string? SecFetchSite { get; init; }
    public string? SecFetchMode { get; init; }
    public string? SecFetchDest { get; init; }
    public string? SecFetchUser { get; init; }
    
    // New: Additional headers
    public string? Accept { get; init; }
    public string? AcceptEncoding { get; init; }
    public string? Connection { get; init; }
    public string? CacheControl { get; init; }
    public string? UpgradeInsecureRequests { get; init; }
    
    // New: Client-side data (from enhanced JS script)
    public string? ScreenWidth { get; init; }
    public string? ScreenHeight { get; init; }
    public string? ColorDepth { get; init; }
    public string? PixelRatio { get; init; }
    public string? Timezone { get; init; }
    public string? TimezoneOffset { get; init; }
    public string? Language { get; init; }
    public string? Platform { get; init; }
    public string? CpuCores { get; init; }
    public string? DeviceMemory { get; init; }
    public string? TouchSupport { get; init; }
    public string? ClientTimestamp { get; init; }
    public string? PageUrl { get; init; }
    public string? PageReferrer { get; init; }
    public string? ConnectionType { get; init; }  // 4g, 3g, wifi, etc.
    public string? ExplicitTier { get; init; }    // Tier marker from our JS
}
