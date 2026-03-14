using System.Text;
using SmartPiXL.SyntheticTraffic.Profiles;

namespace SmartPiXL.SyntheticTraffic.Generation;

// ============================================================================
// QUERY STRING BUILDER — Generates 159-field PiXL query strings.
//
// Every QS param name maps 1:1 to ParsedRecordParser.cs field extraction.
// Values are derived from the DeviceProfile to maintain internal consistency.
// A profile for "Safari on iPhone 15 Pro" produces Safari-correct vendor,
// Apple GPU, no Client Hints, touch=5, no battery API, etc.
//
// IMPORTANT: All param names here MUST match exactly the QS keys that
// ParsedRecordParser.Qs() extracts. See that file's comment block for mapping.
// ============================================================================

internal sealed class QueryStringBuilder
{
    // Per-company page URL pools — realistic page paths for each synthetic company
    private static readonly string[][] CompanyPages =
    [
        // 99901 — SaaS product company
        ["/", "/features", "/pricing", "/demo", "/about", "/blog", "/blog/ai-analytics",
         "/blog/data-pipeline", "/docs", "/docs/getting-started", "/contact", "/case-studies",
         "/case-studies/enterprise", "/integrations", "/security", "/careers"],

        // 99902 — E-commerce
        ["/", "/shop", "/shop/electronics", "/shop/clothing", "/product/wireless-headphones",
         "/product/smart-watch", "/product/running-shoes", "/cart", "/checkout",
         "/account", "/orders", "/wishlist", "/deals", "/new-arrivals", "/returns"],

        // 99903 — Real estate
        ["/", "/listings", "/listings/residential", "/listings/commercial",
         "/property/123-elm-street", "/property/456-oak-avenue", "/agents",
         "/agent/john-smith", "/mortgage-calculator", "/sell-your-home",
         "/neighborhoods", "/open-houses", "/blog", "/contact", "/about"],

        // 99904 — Healthcare
        ["/", "/services", "/providers", "/provider/dr-sarah-chen",
         "/appointments", "/patient-portal", "/insurance", "/locations",
         "/location/downtown", "/urgent-care", "/pharmacy", "/lab-results",
         "/health-library", "/about", "/contact"],

        // 99905 — Financial services
        ["/", "/accounts", "/checking", "/savings", "/credit-cards",
         "/loans/personal", "/loans/mortgage", "/investments", "/retirement",
         "/wealth-management", "/calculators", "/rates", "/locations",
         "/about", "/contact", "/support"],
    ];

    private static readonly string[] CompanyDomains =
        ["app.dataforge.io", "shop.trendhaven.com", "homefind.realty",
         "myhealth.careplus.org", "secure.firstbank.com"];

    private static readonly string[] Referrers =
        ["https://www.google.com/", "https://www.google.com/search?q=",
         "https://www.bing.com/", "https://duckduckgo.com/",
         "https://twitter.com/", "https://www.linkedin.com/",
         "https://www.facebook.com/", "https://news.ycombinator.com/",
         "https://www.reddit.com/", ""];

    private static readonly string[] Timezones =
        ["America/New_York", "America/Chicago", "America/Denver",
         "America/Los_Angeles", "America/Phoenix", "Europe/London",
         "Europe/Berlin", "Europe/Paris", "Asia/Tokyo", "Australia/Sydney",
         "America/Toronto", "America/Vancouver"];

    private static readonly int[] TimezoneOffsetsMin =
        [-300, -360, -420, -480, -420, 0, 60, 60, 540, 660, -300, -480];

    private static readonly string[] Orientations = ["landscape-primary", "portrait-primary"];

    private static readonly string[] DocCharsets = ["UTF-8"];
    private static readonly string[] DocCompatModes = ["CSS1Compat"];
    private static readonly string[] DocReadyStates = ["complete"];
    private static readonly string[] Protocols = ["https:"];

    /// <summary>Stable fingerprint hashes per session — 64-char hex.</summary>
    private readonly string _canvasFP;
    private readonly string _webglFP;
    private readonly string _audioFP;
    private readonly string _audioHash;
    private readonly string _mathFP;
    private readonly string _errorFP;
    private readonly string _cssFontVariant;
    private readonly string _combinedFP;

    private readonly DeviceProfile _profile;
    private readonly ScreenProfile _screen;
    private readonly int _cores;
    private readonly int _memoryGB;
    private readonly string _gpu;
    private readonly string _gpuVendor;
    private readonly int _companyIndex;
    private readonly string _domain;
    private readonly string[] _pages;
    private readonly int _tzIndex;

    /// <summary>
    /// Create a QS builder bound to a specific device profile + session identity.
    /// Fingerprints are stable for the lifetime of this builder (= one session).
    /// </summary>
    public QueryStringBuilder(DeviceProfile profile, int companyIndex, Random rng)
    {
        _profile = profile;
        _companyIndex = companyIndex % CompanyDomains.Length;
        _domain = CompanyDomains[_companyIndex];
        _pages = CompanyPages[_companyIndex];

        // Select hardware variants from profile options
        _screen = profile.Screens[rng.Next(profile.Screens.Length)];
        _cores = profile.CoreOptions[rng.Next(profile.CoreOptions.Length)];
        _memoryGB = profile.MemoryGBOptions[rng.Next(profile.MemoryGBOptions.Length)];
        _gpu = profile.GPURenderers[rng.Next(profile.GPURenderers.Length)];
        _gpuVendor = profile.GPUVendors[rng.Next(profile.GPUVendors.Length)];
        _tzIndex = rng.Next(Timezones.Length);

        // Generate stable fingerprint hashes for this session
        _canvasFP = GenerateHex(rng, 64);
        _webglFP = GenerateHex(rng, 64);
        _audioFP = GenerateAudioSum(rng);
        _audioHash = GenerateHex(rng, 64);
        _mathFP = GenerateHex(rng, 32);
        _errorFP = GenerateHex(rng, 32);
        _cssFontVariant = GenerateHex(rng, 32);
        _combinedFP = GenerateHex(rng, 64);
    }

    /// <summary>
    /// Build a complete query string for one page view within this session.
    /// </summary>
    /// <param name="rng">Random source.</param>
    /// <param name="companyId">Company ID (99901-99905).</param>
    /// <param name="hitNumber">Hit number within the session (1-based). Affects page selection and timing.</param>
    /// <param name="sessionStartMs">Session start timestamp in Unix epoch ms.</param>
    public string Build(Random rng, int companyId, int hitNumber, long sessionStartMs)
    {
        var sb = new StringBuilder(8192);
        var p = _profile;
        var s = _screen;
        var isBot = p.IsBot;
        var isCrawler = p.IsCrawler;

        // Timestamp: session start + cumulative dwell time per page
        var pageTimeMs = sessionStartMs + (hitNumber - 1) * rng.Next(5000, 45000);

        // Page selection: sequential through company pages, wrapping
        var pageIdx = (hitNumber - 1) % _pages.Length;
        var pagePath = _pages[pageIdx];
        var pageUrl = $"https://{_domain}{pagePath}";
        var pageTitle = GeneratePageTitle(pagePath);

        // Referrer: first hit gets external referrer, subsequent hits get internal
        var referrer = hitNumber == 1
            ? Referrers[rng.Next(Referrers.Length)]
            : $"https://{_domain}{_pages[Math.Max(0, pageIdx - 1)]}";

        // ── Phase 0: Identity ──────────────────────────────────────────
        Append(sb, "synthetic", "1");

        // ── Phase 1: Screen + Locale ───────────────────────────────────
        Append(sb, "sw", s.Width);
        Append(sb, "sh", s.Height);
        Append(sb, "saw", s.AvailWidth);
        Append(sb, "sah", s.AvailHeight);

        // Viewport: on mobile = screen, on desktop = screen minus browser chrome
        int vw, vh;
        if (p.IsMobile)
        {
            vw = s.Width;
            vh = s.AvailHeight;
        }
        else
        {
            vw = s.Width - rng.Next(0, 20);
            vh = s.Height - rng.Next(80, 200);
        }
        Append(sb, "vw", vw);
        Append(sb, "vh", vh);

        // Outer: on mobile ~= inner, on desktop adds browser chrome
        if (p.IsMobile)
        {
            Append(sb, "ow", vw);
            Append(sb, "oh", s.Height);
        }
        else
        {
            Append(sb, "ow", s.Width);
            Append(sb, "oh", s.Height - rng.Next(0, 40));
        }

        // Monitor position: primary = 0,0; multi-monitor = offset
        var multiMonitor = !p.IsMobile && rng.Next(100) < 25;
        Append(sb, "sx", multiMonitor ? -s.Width : 0);
        Append(sb, "sy", 0);

        Append(sb, "cd", s.ColorDepth);
        Append(sb, "pd", s.PixelRatio.ToString("F2"));
        Append(sb, "ori", p.IsMobile ? Orientations[rng.Next(Orientations.Length)] : "landscape-primary");

        var tz = Timezones[_tzIndex];
        Append(sb, "tz", tz);
        Append(sb, "tzo", TimezoneOffsetsMin[_tzIndex]);
        Append(sb, "ts", pageTimeMs);

        // Locale fields
        Append(sb, "tzLocale", "en-US");
        Append(sb, "dateFormat", "2/26/2026");
        Append(sb, "numberFormat", "1,234.56");
        Append(sb, "relativeTime", "yesterday");
        Append(sb, "lang", "en-US");
        Append(sb, "langs", "en-US,en");

        // ── Phase 2: Browser + GPU + Fingerprints ──────────────────────
        Append(sb, "plt", p.Platform);
        Append(sb, "vnd", p.Vendor);
        Append(sb, "ua", p.UserAgent);
        Append(sb, "cores", isCrawler ? "" : _cores.ToString());
        Append(sb, "mem", isCrawler ? "" : _memoryGB.ToString());
        Append(sb, "touch", p.MaxTouchPoints);
        Append(sb, "product", p.Product);
        Append(sb, "productSub", p.ProductSub);
        Append(sb, "vendorSub", "");
        Append(sb, "appName", p.AppName);
        Append(sb, "appVersion", p.AppVersion);
        Append(sb, "appCodeName", p.AppCodeName);

        // GPU — crawlers have no GPU
        if (isCrawler)
        {
            Append(sb, "gpu", "");
            Append(sb, "gpuVendor", "");
            Append(sb, "webglParams", "");
            Append(sb, "webglExt", "0");
            Append(sb, "webgl", "0");
            Append(sb, "webgl2", "0");
        }
        else
        {
            Append(sb, "gpu", _gpu);
            Append(sb, "gpuVendor", _gpuVendor);
            Append(sb, "webglParams", GenerateWebGLParams(rng, p));
            Append(sb, "webglExt", GenerateWebGLExtCount(rng, p));
            Append(sb, "webgl", "1");
            Append(sb, "webgl2", p.Browser is BrowserFamily.Safari && p.OS is OsFamily.IOS ? "0" : "1");
        }

        // Fingerprints — stable per session
        if (isCrawler)
        {
            Append(sb, "canvasFP", "");
            Append(sb, "webglFP", "");
            Append(sb, "audioFP", "");
            Append(sb, "audioHash", "");
            Append(sb, "mathFP", "");
            Append(sb, "errorFP", "");
            Append(sb, "cssFontVariant", "");
            Append(sb, "fonts", "");
        }
        else
        {
            Append(sb, "canvasFP", _canvasFP);
            Append(sb, "webglFP", _webglFP);
            Append(sb, "audioFP", _audioFP);
            Append(sb, "audioHash", _audioHash);
            Append(sb, "mathFP", _mathFP);
            Append(sb, "errorFP", _errorFP);
            Append(sb, "cssFontVariant", _cssFontVariant);
            Append(sb, "fonts", GenerateFontList(rng, p));
        }

        // ── Phase 3: Plugins + Network + Storage ───────────────────────
        if (isCrawler)
        {
            Append(sb, "plugins", "0");
            Append(sb, "pluginList", "");
            Append(sb, "mimeTypes", "0");
            Append(sb, "mimeList", "");
        }
        else
        {
            var pluginCount = p.Browser == BrowserFamily.Firefox ? rng.Next(0, 3) : rng.Next(3, 6);
            Append(sb, "plugins", pluginCount);
            Append(sb, "pluginList", GeneratePluginList(p, pluginCount));
            var mimeCount = pluginCount * 2;
            Append(sb, "mimeTypes", mimeCount);
            Append(sb, "mimeList", GenerateMimeList(mimeCount));
        }

        Append(sb, "voices", isCrawler ? "" : GenerateVoiceCount(rng, p));
        Append(sb, "gamepads", "");
        Append(sb, "localIp", "");

        // Connection API — Chromium only
        if (!isCrawler && p.HasConnectionAPI)
        {
            var isMobileConn = p.IsMobile;
            Append(sb, "conn", isMobileConn ? "cellular" : "wifi");
            Append(sb, "dl", isMobileConn
                ? (rng.Next(15, 250) / 10.0).ToString("F1")
                : (rng.Next(100, 1000) / 10.0).ToString("F1"));
            Append(sb, "dlMax", "Infinity");
            Append(sb, "rtt", isMobileConn ? rng.Next(30, 200) : rng.Next(5, 50));
            Append(sb, "save", "0");
            Append(sb, "connType", isMobileConn ? "4g" : "wifi");
        }
        else
        {
            Append(sb, "conn", "");
            Append(sb, "dl", "");
            Append(sb, "dlMax", "");
            Append(sb, "rtt", "");
            Append(sb, "save", "");
            Append(sb, "connType", "");
        }

        Append(sb, "online", isCrawler ? "" : "1");

        // Storage
        if (!isCrawler)
        {
            Append(sb, "storageQuota", rng.Next(50, 300).ToString());
            Append(sb, "storageUsed", rng.Next(1, 50).ToString());
            Append(sb, "ls", "1");
            Append(sb, "ss", "1");
            Append(sb, "idb", "1");
            Append(sb, "caches", "1");
        }
        else
        {
            Append(sb, "storageQuota", "");
            Append(sb, "storageUsed", "");
            Append(sb, "ls", "");
            Append(sb, "ss", "");
            Append(sb, "idb", "");
            Append(sb, "caches", "");
        }

        // Battery — Android Chrome + some desktop Chrome only
        if (!isCrawler && p.HasBatteryAPI)
        {
            Append(sb, "batteryLevel", rng.Next(15, 101));
            Append(sb, "batteryCharging", rng.Next(100) < 40 ? "1" : "0");
        }
        else
        {
            Append(sb, "batteryLevel", "");
            Append(sb, "batteryCharging", "");
        }

        // Media devices
        if (!isCrawler)
        {
            Append(sb, "audioInputs", p.IsMobile ? "1" : rng.Next(0, 3).ToString());
            Append(sb, "videoInputs", p.IsMobile ? "1" : rng.Next(0, 3).ToString());
        }
        else
        {
            Append(sb, "audioInputs", "");
            Append(sb, "videoInputs", "");
        }

        Append(sb, "ck", isCrawler ? "" : "1");
        Append(sb, "dnt", isCrawler ? "" : (rng.Next(100) < 15 ? "1" : "unspecified"));
        Append(sb, "pdf", isCrawler ? "" : "1");
        Append(sb, "webdr", isBot && p.Browser == BrowserFamily.HeadlessChrome ? "1" : "0");
        Append(sb, "java", "0");

        // ── Phase 4: Capabilities ──────────────────────────────────────
        if (!isCrawler)
        {
            Append(sb, "canvas", "1");
            Append(sb, "wasm", "1");
            Append(sb, "ww", "1");
            Append(sb, "swk", "1");
            Append(sb, "mediaDevices", "1");
            Append(sb, "clipboard", "1");
            Append(sb, "speechSynth", "1");
            Append(sb, "touchEvent", p.MaxTouchPoints > 0 ? "1" : "0");
            Append(sb, "pointerEvent", "1");
            Append(sb, "hover", p.HoverCapable ? "hover" : "none");
            Append(sb, "pointer", p.IsMobile ? "coarse" : "fine");
            Append(sb, "darkMode", rng.Next(100) < 30 ? "1" : "0");
            Append(sb, "lightMode", rng.Next(100) < 70 ? "1" : "0");
            Append(sb, "reducedMotion", rng.Next(100) < 5 ? "1" : "0");
            Append(sb, "reducedData", "0");
            Append(sb, "contrast", rng.Next(100) < 3 ? "1" : "0");
            Append(sb, "forcedColors", "0");
            Append(sb, "invertedColors", "0");
            Append(sb, "standalone", "0");
        }
        else
        {
            // Crawlers: most capabilities are empty
            Append(sb, "canvas", ""); Append(sb, "wasm", ""); Append(sb, "ww", "");
            Append(sb, "swk", ""); Append(sb, "mediaDevices", ""); Append(sb, "clipboard", "");
            Append(sb, "speechSynth", ""); Append(sb, "touchEvent", ""); Append(sb, "pointerEvent", "");
            Append(sb, "hover", ""); Append(sb, "pointer", ""); Append(sb, "darkMode", "");
            Append(sb, "lightMode", ""); Append(sb, "reducedMotion", ""); Append(sb, "reducedData", "");
            Append(sb, "contrast", ""); Append(sb, "forcedColors", ""); Append(sb, "invertedColors", "");
            Append(sb, "standalone", "");
        }

        // Document state
        Append(sb, "docCharset", "UTF-8");
        Append(sb, "docCompat", "CSS1Compat");
        Append(sb, "docReady", "complete");
        Append(sb, "docHidden", "0");
        Append(sb, "docVisibility", "visible");

        // ── Phase 5: Page + Performance ────────────────────────────────
        Append(sb, "url", pageUrl);
        Append(sb, "ref", referrer);
        Append(sb, "title", pageTitle);
        Append(sb, "domain", _domain);
        Append(sb, "path", pagePath);
        Append(sb, "hash", "");
        Append(sb, "protocol", "https:");
        Append(sb, "hist", hitNumber + rng.Next(0, 10));

        // Performance timing — monotonically ordered: DNS < TCP < TTFB < DOM < Load
        if (!isCrawler)
        {
            var dns = rng.Next(1, 30);
            var tcp = dns + rng.Next(5, 50);
            var ttfb = tcp + rng.Next(50, 300);
            var dom = ttfb + rng.Next(200, 1000);
            var load = dom + rng.Next(200, 1500);

            Append(sb, "loadTime", load);
            Append(sb, "domTime", dom);
            Append(sb, "dnsTime", dns);
            Append(sb, "tcpTime", tcp);
            Append(sb, "ttfb", ttfb);
        }
        else
        {
            Append(sb, "loadTime", "");
            Append(sb, "domTime", "");
            Append(sb, "dnsTime", "");
            Append(sb, "tcpTime", "");
            Append(sb, "ttfb", "");
        }

        // Bot signals (client-side detection) — real humans have low scores
        if (isBot)
        {
            if (isCrawler)
            {
                Append(sb, "botSignals", "navigator.webdriver,headless_ua");
                Append(sb, "botScore", rng.Next(70, 100));
                Append(sb, "combinedThreatScore", rng.Next(60, 95));
            }
            else // HeadlessChrome
            {
                Append(sb, "botSignals", rng.Next(100) < 50 ? "navigator.webdriver" : "");
                Append(sb, "botScore", rng.Next(20, 60));
                Append(sb, "combinedThreatScore", rng.Next(15, 50));
            }
        }
        else
        {
            Append(sb, "botSignals", "");
            Append(sb, "botScore", rng.Next(0, 10));
            Append(sb, "combinedThreatScore", rng.Next(0, 8));
        }

        Append(sb, "scriptExecTime", isCrawler ? "" : rng.Next(50, 500).ToString());
        Append(sb, "botPermInconsistent", isBot ? (rng.Next(100) < 30 ? "1" : "0") : "0");

        // ── Phase 6: Evasion + Client Hints ────────────────────────────
        if (isBot && p.Browser == BrowserFamily.HeadlessChrome)
        {
            Append(sb, "canvasEvasion", rng.Next(100) < 20 ? "1" : "0");
            Append(sb, "webglEvasion", rng.Next(100) < 15 ? "1" : "0");
            Append(sb, "evasionDetected", rng.Next(100) < 25 ? "puppeteer" : "");
        }
        else
        {
            Append(sb, "canvasEvasion", "0");
            Append(sb, "webglEvasion", "0");
            Append(sb, "evasionDetected", "");
        }
        Append(sb, "_proxyBlocked", "");

        // Client Hints — Chromium only (never Firefox/Safari)
        if (p.ClientHints is { } ch)
        {
            Append(sb, "uaArch", ch.UaArch);
            Append(sb, "uaBitness", ch.UaBitness);
            Append(sb, "uaModel", ch.UaModel ?? "");
            Append(sb, "uaPlatformVersion", ch.UaPlatformVersion);
            Append(sb, "uaFullVersion", ch.UaFullVersion);
            Append(sb, "uaWow64", "0");
            Append(sb, "uaMobile", ch.UaMobile ? "1" : "0");
            Append(sb, "uaPlatform", ch.UaPlatform);
            Append(sb, "uaBrands", ch.UaBrands);
            Append(sb, "uaFormFactor", ch.UaFormFactor);
        }
        else
        {
            Append(sb, "uaArch", "");
            Append(sb, "uaBitness", "");
            Append(sb, "uaModel", "");
            Append(sb, "uaPlatformVersion", "");
            Append(sb, "uaFullVersion", "");
            Append(sb, "uaWow64", "");
            Append(sb, "uaMobile", "");
            Append(sb, "uaPlatform", "");
            Append(sb, "uaBrands", "");
            Append(sb, "uaFormFactor", "");
        }

        // Firefox-specific
        Append(sb, "oscpu", p.OSCPU ?? "");
        Append(sb, "buildID", p.BuildID ?? "");

        // Chrome-specific detection
        var isChromium = p.Browser is BrowserFamily.Chrome or BrowserFamily.Edge or BrowserFamily.HeadlessChrome;
        Append(sb, "chromeObj", isChromium ? "1" : "0");
        Append(sb, "chromeRuntime", isChromium && !isBot ? "1" : "0");

        // JS Heap — Chromium only
        if (isChromium && !isCrawler)
        {
            var heapLimit = 4_294_967_296L; // 4GB typical
            var heapTotal = rng.Next(10_000_000, 100_000_000);
            var heapUsed = (int)(heapTotal * (0.3 + rng.NextDouble() * 0.5));
            Append(sb, "jsHeapLimit", heapLimit);
            Append(sb, "jsHeapTotal", heapTotal);
            Append(sb, "jsHeapUsed", heapUsed);
        }
        else
        {
            Append(sb, "jsHeapLimit", "");
            Append(sb, "jsHeapTotal", "");
            Append(sb, "jsHeapUsed", "");
        }

        // Canvas/Audio consistency (legitimate browsers are consistent)
        Append(sb, "canvasConsistency", isCrawler ? "" : "consistent");
        Append(sb, "audioStable", isCrawler ? "" : "1");
        Append(sb, "audioNoiseDetected", isBot && p.Browser == BrowserFamily.HeadlessChrome
            ? (rng.Next(100) < 10 ? "1" : "0") : "0");

        // ── Phase 7: Behavioral ────────────────────────────────────────
        if (!isBot)
        {
            // Real human behavioral signals
            var mouseMoves = rng.Next(15, 200);
            var scrolled = rng.Next(100) < 85;
            var scrollY = scrolled ? rng.Next(100, 3000) : 0;
            var entropy = rng.Next(30, 95);

            Append(sb, "mouseMoves", mouseMoves);
            Append(sb, "scrolled", scrolled ? "1" : "0");
            Append(sb, "scrollY", scrollY);
            Append(sb, "mouseEntropy", entropy);
            Append(sb, "scrollContradiction", "0");
            Append(sb, "moveTimingCV", rng.Next(20, 80));
            Append(sb, "moveSpeedCV", rng.Next(15, 70));
            Append(sb, "moveCountBucket", mouseMoves < 30 ? "low" : mouseMoves < 100 ? "medium" : "high");
            Append(sb, "behavioralFlags", "");
            Append(sb, "mousePath", GenerateMousePath(rng, mouseMoves));
        }
        else if (p.Browser == BrowserFamily.HeadlessChrome)
        {
            // Headless bot: robotic or zero behavioral data
            var fakeMouseMoves = rng.Next(100) < 40 ? rng.Next(3, 20) : 0;
            Append(sb, "mouseMoves", fakeMouseMoves);
            Append(sb, "scrolled", rng.Next(100) < 20 ? "1" : "0");
            Append(sb, "scrollY", rng.Next(0, 500));
            Append(sb, "mouseEntropy", rng.Next(0, 15));
            Append(sb, "scrollContradiction", rng.Next(100) < 10 ? "1" : "0");
            Append(sb, "moveTimingCV", rng.Next(0, 10));
            Append(sb, "moveSpeedCV", rng.Next(0, 10));
            Append(sb, "moveCountBucket", fakeMouseMoves == 0 ? "zero" : "low");
            Append(sb, "behavioralFlags", "robotic_timing");
            Append(sb, "mousePath", "");
        }
        else
        {
            // Crawlers: no behavioral data at all
            Append(sb, "mouseMoves", "0");
            Append(sb, "scrolled", "0");
            Append(sb, "scrollY", "0");
            Append(sb, "mouseEntropy", "0");
            Append(sb, "scrollContradiction", "0");
            Append(sb, "moveTimingCV", "0");
            Append(sb, "moveSpeedCV", "0");
            Append(sb, "moveCountBucket", "zero");
            Append(sb, "behavioralFlags", "");
            Append(sb, "mousePath", "");
        }

        // Cross-signal + evasion v2
        Append(sb, "stealthSignals", "");
        Append(sb, "fontMethodMismatch", "0");
        Append(sb, "evasionSignalsV2", isBot && rng.Next(100) < 15 ? "automation_detected" : "");
        Append(sb, "crossSignals", "");
        Append(sb, "anomalyScore", isBot ? rng.Next(20, 80) : rng.Next(0, 12));

        // Combined fingerprint
        Append(sb, "fp", isCrawler ? "" : _combinedFP);

        // Screen extended (multi-monitor)
        Append(sb, "screenExtended", multiMonitor ? "1" : "0");

        // Remove trailing &
        if (sb.Length > 0 && sb[^1] == '&')
            sb.Length--;

        return sb.ToString();
    }

    /// <summary>The referrer URL for the HTTP request header (first hit external, else internal).</summary>
    public string GetReferrer(Random rng, int hitNumber)
    {
        if (hitNumber == 1)
            return Referrers[rng.Next(Referrers.Length)];
        return $"https://{_domain}/";
    }

    // ════════════════════════════════════════════════════════════════════
    // INTERNAL HELPERS
    // ════════════════════════════════════════════════════════════════════

    private static void Append(StringBuilder sb, string key, string value)
    {
        sb.Append(key);
        sb.Append('=');
        sb.Append(Uri.EscapeDataString(value));
        sb.Append('&');
    }

    private static void Append(StringBuilder sb, string key, int value)
    {
        sb.Append(key);
        sb.Append('=');
        sb.Append(value);
        sb.Append('&');
    }

    private static void Append(StringBuilder sb, string key, long value)
    {
        sb.Append(key);
        sb.Append('=');
        sb.Append(value);
        sb.Append('&');
    }

    private static string GenerateHex(Random rng, int length)
    {
        Span<char> hex = stackalloc char[length];
        const string chars = "0123456789abcdef";
        for (var i = 0; i < length; i++)
            hex[i] = chars[rng.Next(16)];
        return new string(hex);
    }

    /// <summary>Audio fingerprint sum: realistic float like "124.04347527516074"</summary>
    private static string GenerateAudioSum(Random rng)
    {
        var whole = rng.Next(100, 200);
        var frac = rng.Next(10000000, 99999999);
        return $"{whole}.{frac:D8}";
    }

    private static string GenerateWebGLParams(Random rng, DeviceProfile p)
    {
        // MAX_TEXTURE_SIZE varies by GPU tier
        var maxTex = p.OS switch
        {
            OsFamily.IOS or OsFamily.IPadOS => 8192,
            OsFamily.Android => rng.Next(100) < 70 ? 8192 : 16384,
            _ => 16384,
        };
        return $"MAX_TEXTURE_SIZE:{maxTex},MAX_VIEWPORT_DIMS:{maxTex}x{maxTex},MAX_RENDERBUFFER_SIZE:{maxTex}";
    }

    private static string GenerateWebGLExtCount(Random rng, DeviceProfile p)
    {
        return p.Browser switch
        {
            BrowserFamily.Chrome or BrowserFamily.Edge => rng.Next(28, 45).ToString(),
            BrowserFamily.Firefox => rng.Next(20, 35).ToString(),
            BrowserFamily.Safari => rng.Next(12, 22).ToString(),
            BrowserFamily.HeadlessChrome => rng.Next(25, 40).ToString(),
            _ => "0",
        };
    }

    private static string GenerateFontList(Random rng, DeviceProfile p)
    {
        // Return a small subset of detected fonts — varies by OS
        return p.OS switch
        {
            OsFamily.Windows => "Arial,Calibri,Cambria,Consolas,Courier New,Georgia,Impact,Segoe UI,Tahoma,Times New Roman,Trebuchet MS,Verdana",
            OsFamily.MacOS or OsFamily.IOS or OsFamily.IPadOS => "Arial,Avenir,Courier,Futura,Geneva,Helvetica,Helvetica Neue,Menlo,Monaco,San Francisco,Times",
            OsFamily.Android => "Droid Sans,Noto Sans,Roboto",
            OsFamily.Linux => "DejaVu Sans,Liberation Mono,Liberation Sans,Noto Sans,Ubuntu",
            _ => "Arial,Times New Roman",
        };
    }

    private static string GeneratePluginList(DeviceProfile p, int count)
    {
        if (count == 0) return "";
        // Chromium standard plugins since Chrome 92
        string[] chromiumPlugins = ["PDF Viewer", "Chrome PDF Viewer", "Chromium PDF Viewer",
            "Microsoft Edge PDF Viewer", "WebKit built-in PDF"];
        return string.Join(",", chromiumPlugins.Take(count));
    }

    private static string GenerateMimeList(int count)
    {
        if (count == 0) return "";
        string[] mimes = ["application/pdf", "text/pdf", "application/x-pdf",
            "application/x-google-chrome-pdf", "application/x-nacl", "application/x-pnacl"];
        return string.Join(",", mimes.Take(count));
    }

    private static string GenerateVoiceCount(Random rng, DeviceProfile p)
    {
        return p.OS switch
        {
            OsFamily.Windows => rng.Next(3, 8).ToString(),
            OsFamily.MacOS => rng.Next(60, 80).ToString(),
            OsFamily.IOS or OsFamily.IPadOS => rng.Next(40, 60).ToString(),
            OsFamily.Android => rng.Next(5, 15).ToString(),
            _ => rng.Next(3, 10).ToString(),
        };
    }

    private static string GeneratePageTitle(string path)
    {
        if (path == "/") return "Home";
        // Convert /blog/ai-analytics → "Ai Analytics"
        var name = path.TrimStart('/').Replace('-', ' ').Replace("/", " - ");
        if (name.Length > 0)
            name = char.ToUpperInvariant(name[0]) + name[1..];
        return name;
    }

    /// <summary>
    /// Generate a compressed mouse path string: pairs of (dx,dy) deltas.
    /// Real mouse paths have curved trajectories with varying speed.
    /// Format matches PiXL script: "dx1,dy1;dx2,dy2;..." (up to 50 points).
    /// </summary>
    private static string GenerateMousePath(Random rng, int moveCount)
    {
        var points = Math.Min(moveCount, 50);
        if (points <= 0) return "";

        var sb = new StringBuilder(points * 8);
        for (var i = 0; i < points; i++)
        {
            if (i > 0) sb.Append(';');
            // Natural mouse deltas: small jittery movements with occasional jumps
            var dx = rng.Next(-80, 81);
            var dy = rng.Next(-60, 61);
            // Add curve bias — humans tend to arc
            if (i % 5 == 0)
            {
                dx += rng.Next(-200, 201);
                dy += rng.Next(-150, 151);
            }
            sb.Append(dx);
            sb.Append(',');
            sb.Append(dy);
        }
        return sb.ToString();
    }
}
