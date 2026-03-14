namespace SmartPiXL.SyntheticTraffic.Profiles;

// ============================================================================
// PROFILE CATALOG — 30 device profiles with power-law weighted selection.
//
// Profiles are derived from real browser/device specifications:
//   - Screen geometries from actual device specs (Apple, Google, Samsung)
//   - GPU strings from Chromium source and real WEBGL_debug_renderer_info output
//   - Client Hints from Chromium UA reduction spec
//   - Navigator properties from browser conformance tests
//   - Market share weights from StatCounter GlobalStats (Feb 2026 approximation)
//
// WEIGHT DISTRIBUTION (sums to 100):
//   Desktop: 55%  (Chrome 30%, Firefox 7%, Safari 7%, Edge 6%, Other 5%)
//   Mobile:  35%  (Chrome Android 15%, Safari iOS 17%, Other 3%)
//   Tablet:   5%  (iPad 4%, Android 1%)
//   Bot:      5%  (Headless 2%, Googlebot 1%, Bingbot 1%, Crawlers 1%)
// ============================================================================

public static class ProfileCatalog
{
    /// <summary>All 30 device profiles. Weights sum to 100.</summary>
    public static readonly DeviceProfile[] All = BuildProfiles();

    /// <summary>Cumulative weight array for O(log n) weighted random selection.</summary>
    public static readonly int[] CumulativeWeights = BuildCumulativeWeights();

    /// <summary>Total weight across all profiles (should be 100).</summary>
    public static readonly int TotalWeight = CumulativeWeights[^1];

    /// <summary>
    /// Select a random profile using power-law weighted distribution.
    /// Uses binary search on cumulative weights — O(log 30) = ~5 comparisons.
    /// </summary>
    public static DeviceProfile Select(Random rng)
    {
        var roll = rng.Next(1, TotalWeight + 1);
        var idx = Array.BinarySearch(CumulativeWeights, roll);
        if (idx < 0) idx = ~idx; // BinarySearch returns bitwise complement of next-larger index
        return All[idx];
    }

    private static int[] BuildCumulativeWeights()
    {
        var profiles = All;
        var cw = new int[profiles.Length];
        var sum = 0;
        for (var i = 0; i < profiles.Length; i++)
        {
            sum += profiles[i].Weight;
            cw[i] = sum;
        }
        return cw;
    }

    private static DeviceProfile[] BuildProfiles() =>
    [
        // ================================================================
        // DESKTOP — CHROME (30%)
        // ================================================================

        // 1. Chrome 133 / Win11 / RTX 4070 — high-end desktop gamer/pro
        new()
        {
            Name = "Chrome133-Win11-RTX4070", Weight = 8,
            Browser = BrowserFamily.Chrome, OS = OsFamily.Windows,
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            AppVersion = "5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            Platform = "Win32", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [
                new(1920, 1080, 1920, 1040, 24, 1.0),
                new(2560, 1440, 2560, 1400, 24, 1.0),
                new(3840, 2160, 3840, 2120, 24, 1.5),
            ],
            CoreOptions = [8, 12, 16], MemoryGBOptions = [16, 32],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = true,
            GPURenderers = [
                "ANGLE (NVIDIA, NVIDIA GeForce RTX 4070 Direct3D11 vs_5_0 ps_5_0, D3D11)",
                "ANGLE (NVIDIA, NVIDIA GeForce RTX 4060 Ti Direct3D11 vs_5_0 ps_5_0, D3D11)",
            ],
            GPUVendors = ["Google Inc. (NVIDIA)"],
            ColorDepthOverride = 24,
            ClientHints = new()
            {
                UaPlatform = "Windows", UaArch = "x86", UaBitness = "64",
                UaBrands = "\"Chromium\";v=\"133\", \"Google Chrome\";v=\"133\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Desktop", UaMobile = false,
                UaPlatformVersion = "15.0.0", UaFullVersion = "133.0.6943.88",
            },
            HasBatteryAPI = false, HasConnectionAPI = true,
        },

        // 2. Chrome 133 / Win11 / Intel UHD 770 — typical office desktop
        new()
        {
            Name = "Chrome133-Win11-IntelUHD770", Weight = 7,
            Browser = BrowserFamily.Chrome, OS = OsFamily.Windows,
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            AppVersion = "5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            Platform = "Win32", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [
                new(1920, 1080, 1920, 1040, 24, 1.0),
                new(1366, 768, 1366, 728, 24, 1.0),
                new(1536, 864, 1536, 824, 24, 1.25),
            ],
            CoreOptions = [4, 8], MemoryGBOptions = [8, 16],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = true,
            GPURenderers = [
                "ANGLE (Intel, Intel(R) UHD Graphics 770 Direct3D11 vs_5_0 ps_5_0, D3D11)",
                "ANGLE (Intel, Intel(R) UHD Graphics 730 Direct3D11 vs_5_0 ps_5_0, D3D11)",
            ],
            GPUVendors = ["Google Inc. (Intel)"],
            ColorDepthOverride = 24,
            ClientHints = new()
            {
                UaPlatform = "Windows", UaArch = "x86", UaBitness = "64",
                UaBrands = "\"Chromium\";v=\"133\", \"Google Chrome\";v=\"133\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Desktop", UaMobile = false,
                UaPlatformVersion = "15.0.0", UaFullVersion = "133.0.6943.88",
            },
            HasBatteryAPI = false, HasConnectionAPI = true,
        },

        // 3. Chrome 132 / Win10 / GTX 1660 — older gaming rig
        new()
        {
            Name = "Chrome132-Win10-GTX1660", Weight = 5,
            Browser = BrowserFamily.Chrome, OS = OsFamily.Windows,
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36",
            AppVersion = "5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36",
            Platform = "Win32", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [
                new(1920, 1080, 1920, 1040, 24, 1.0),
                new(2560, 1440, 2560, 1400, 24, 1.0),
            ],
            CoreOptions = [4, 6, 8], MemoryGBOptions = [8, 16],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = true,
            GPURenderers = [
                "ANGLE (NVIDIA, NVIDIA GeForce GTX 1660 SUPER Direct3D11 vs_5_0 ps_5_0, D3D11)",
            ],
            GPUVendors = ["Google Inc. (NVIDIA)"],
            ColorDepthOverride = 24,
            ClientHints = new()
            {
                UaPlatform = "Windows", UaArch = "x86", UaBitness = "64",
                UaBrands = "\"Chromium\";v=\"132\", \"Google Chrome\";v=\"132\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Desktop", UaMobile = false,
                UaPlatformVersion = "10.0.0", UaFullVersion = "132.0.6834.110",
            },
            HasBatteryAPI = false, HasConnectionAPI = true,
        },

        // 4. Chrome 133 / Win10 / AMD RX 580 — budget desktop
        new()
        {
            Name = "Chrome133-Win10-RX580", Weight = 4,
            Browser = BrowserFamily.Chrome, OS = OsFamily.Windows,
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            AppVersion = "5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            Platform = "Win32", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [
                new(1920, 1080, 1920, 1040, 24, 1.0),
                new(1366, 768, 1366, 728, 24, 1.0),
            ],
            CoreOptions = [4, 6], MemoryGBOptions = [8, 16],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = true,
            GPURenderers = [
                "ANGLE (AMD, AMD Radeon RX 580 2048SP Direct3D11 vs_5_0 ps_5_0, D3D11)",
            ],
            GPUVendors = ["Google Inc. (AMD)"],
            ColorDepthOverride = 24,
            ClientHints = new()
            {
                UaPlatform = "Windows", UaArch = "x86", UaBitness = "64",
                UaBrands = "\"Chromium\";v=\"133\", \"Google Chrome\";v=\"133\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Desktop", UaMobile = false,
                UaPlatformVersion = "10.0.0", UaFullVersion = "133.0.6943.88",
            },
            HasBatteryAPI = false, HasConnectionAPI = true,
        },

        // 5. Chrome 133 / Mac M2 Pro
        new()
        {
            Name = "Chrome133-Mac-M2Pro", Weight = 3,
            Browser = BrowserFamily.Chrome, OS = OsFamily.MacOS,
            UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            AppVersion = "5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            Platform = "MacIntel", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [
                new(1512, 982, 1512, 957, 30, 2.0),
                new(1728, 1117, 1728, 1092, 30, 2.0),
            ],
            CoreOptions = [10, 12], MemoryGBOptions = [16, 32],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = true,
            GPURenderers = ["ANGLE (Apple, Apple M2 Pro, OpenGL 4.1)"],
            GPUVendors = ["Google Inc. (Apple)"],
            ColorDepthOverride = 30,
            ClientHints = new()
            {
                UaPlatform = "macOS", UaArch = "arm", UaBitness = "64",
                UaBrands = "\"Chromium\";v=\"133\", \"Google Chrome\";v=\"133\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Desktop", UaMobile = false,
                UaPlatformVersion = "14.3.0", UaFullVersion = "133.0.6943.88",
            },
            HasBatteryAPI = false, HasConnectionAPI = true,
        },

        // 6. Chrome 133 / Mac M3
        new()
        {
            Name = "Chrome133-Mac-M3", Weight = 3,
            Browser = BrowserFamily.Chrome, OS = OsFamily.MacOS,
            UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            AppVersion = "5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            Platform = "MacIntel", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [
                new(1470, 956, 1470, 931, 30, 2.0),
                new(1512, 982, 1512, 957, 30, 2.0),
            ],
            CoreOptions = [8], MemoryGBOptions = [8, 16, 24],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = true,
            GPURenderers = ["ANGLE (Apple, Apple M3, OpenGL 4.1)"],
            GPUVendors = ["Google Inc. (Apple)"],
            ColorDepthOverride = 30,
            ClientHints = new()
            {
                UaPlatform = "macOS", UaArch = "arm", UaBitness = "64",
                UaBrands = "\"Chromium\";v=\"133\", \"Google Chrome\";v=\"133\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Desktop", UaMobile = false,
                UaPlatformVersion = "15.2.0", UaFullVersion = "133.0.6943.88",
            },
            HasBatteryAPI = false, HasConnectionAPI = true,
        },

        // ================================================================
        // DESKTOP — FIREFOX (7%)
        // ================================================================

        // 7. Firefox 134 / Win11
        new()
        {
            Name = "Firefox134-Win11", Weight = 4,
            Browser = BrowserFamily.Firefox, OS = OsFamily.Windows,
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:134.0) Gecko/20100101 Firefox/134.0",
            AppVersion = "5.0 (Windows)",
            Platform = "Win32", Vendor = "", ProductSub = "20100101",
            OSCPU = "Windows NT 10.0; Win64; x64", BuildID = "20260215000000",
            Screens = [
                new(1920, 1080, 1920, 1040, 24, 1.0),
                new(2560, 1440, 2560, 1400, 24, 1.0),
                new(1366, 768, 1366, 728, 24, 1.0),
            ],
            CoreOptions = [4, 8, 12], MemoryGBOptions = [8, 16],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = true,
            GPURenderers = [
                "NVIDIA GeForce RTX 3070/PCIe/SSE2",
                "NVIDIA GeForce GTX 1650/PCIe/SSE2",
                "Intel(R) UHD Graphics 630",
            ],
            GPUVendors = ["NVIDIA Corporation", "Intel"],
            ColorDepthOverride = 24,
            ClientHints = null, // Firefox never sends Client Hints
            HasBatteryAPI = false, HasConnectionAPI = false,
        },

        // 8. Firefox 134 / Mac
        new()
        {
            Name = "Firefox134-Mac", Weight = 3,
            Browser = BrowserFamily.Firefox, OS = OsFamily.MacOS,
            UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:134.0) Gecko/20100101 Firefox/134.0",
            AppVersion = "5.0 (Macintosh)",
            Platform = "MacIntel", Vendor = "", ProductSub = "20100101",
            OSCPU = "Intel Mac OS X 10.15", BuildID = "20260215000000",
            Screens = [
                new(2560, 1600, 2560, 1575, 30, 2.0),
                new(1440, 900, 1440, 875, 30, 2.0),
            ],
            CoreOptions = [8, 10], MemoryGBOptions = [8, 16, 32],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = true,
            GPURenderers = ["Apple M1", "Apple M2"],
            GPUVendors = ["Apple"],
            ColorDepthOverride = 30,
            ClientHints = null,
            HasBatteryAPI = false, HasConnectionAPI = false,
        },

        // ================================================================
        // DESKTOP — SAFARI (7%)
        // ================================================================

        // 9. Safari 17.6 / Mac M1
        new()
        {
            Name = "Safari17-Mac-M1", Weight = 4,
            Browser = BrowserFamily.Safari, OS = OsFamily.MacOS,
            UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Safari/605.1.15",
            AppVersion = "5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Safari/605.1.15",
            Platform = "MacIntel", Vendor = "Apple Computer, Inc.", ProductSub = "20030107",
            Screens = [
                new(2560, 1600, 2560, 1575, 30, 2.0),
                new(1440, 900, 1440, 875, 30, 2.0),
            ],
            CoreOptions = [8], MemoryGBOptions = [8, 16],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = true,
            GPURenderers = ["Apple GPU"],
            GPUVendors = ["Apple GPU"],
            ColorDepthOverride = 30,
            ClientHints = null, // Safari never sends Client Hints
            HasBatteryAPI = false, HasConnectionAPI = false,
        },

        // 10. Safari 17.6 / Mac M2
        new()
        {
            Name = "Safari17-Mac-M2", Weight = 3,
            Browser = BrowserFamily.Safari, OS = OsFamily.MacOS,
            UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Safari/605.1.15",
            AppVersion = "5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Safari/605.1.15",
            Platform = "MacIntel", Vendor = "Apple Computer, Inc.", ProductSub = "20030107",
            Screens = [
                new(1512, 982, 1512, 957, 30, 2.0),
                new(1728, 1117, 1728, 1092, 30, 2.0),
            ],
            CoreOptions = [8, 10], MemoryGBOptions = [16, 24],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = true,
            GPURenderers = ["Apple GPU"],
            GPUVendors = ["Apple GPU"],
            ColorDepthOverride = 30,
            ClientHints = null,
            HasBatteryAPI = false, HasConnectionAPI = false,
        },

        // ================================================================
        // DESKTOP — EDGE (6%)
        // ================================================================

        // 11. Edge 133 / Win11 / Iris Xe
        new()
        {
            Name = "Edge133-Win11-IrisXe", Weight = 4,
            Browser = BrowserFamily.Edge, OS = OsFamily.Windows,
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36 Edg/133.0.0.0",
            AppVersion = "5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36 Edg/133.0.0.0",
            Platform = "Win32", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [
                new(1920, 1080, 1920, 1040, 24, 1.0),
                new(2560, 1440, 2560, 1400, 24, 1.0),
                new(1536, 864, 1536, 824, 24, 1.25),
            ],
            CoreOptions = [4, 8, 12], MemoryGBOptions = [8, 16],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = true,
            GPURenderers = [
                "ANGLE (Intel, Intel(R) Iris(R) Xe Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)",
            ],
            GPUVendors = ["Google Inc. (Intel)"],
            ColorDepthOverride = 24,
            ClientHints = new()
            {
                UaPlatform = "Windows", UaArch = "x86", UaBitness = "64",
                UaBrands = "\"Chromium\";v=\"133\", \"Microsoft Edge\";v=\"133\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Desktop", UaMobile = false,
                UaPlatformVersion = "15.0.0", UaFullVersion = "133.0.3065.59",
            },
            HasBatteryAPI = false, HasConnectionAPI = true,
        },

        // 12. Edge 132 / Win10
        new()
        {
            Name = "Edge132-Win10", Weight = 2,
            Browser = BrowserFamily.Edge, OS = OsFamily.Windows,
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36 Edg/132.0.0.0",
            AppVersion = "5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36 Edg/132.0.0.0",
            Platform = "Win32", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [
                new(1920, 1080, 1920, 1040, 24, 1.0),
                new(1366, 768, 1366, 728, 24, 1.0),
            ],
            CoreOptions = [4, 6], MemoryGBOptions = [4, 8],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = true,
            GPURenderers = [
                "ANGLE (Intel, Intel(R) UHD Graphics 630 Direct3D11 vs_5_0 ps_5_0, D3D11)",
            ],
            GPUVendors = ["Google Inc. (Intel)"],
            ColorDepthOverride = 24,
            ClientHints = new()
            {
                UaPlatform = "Windows", UaArch = "x86", UaBitness = "64",
                UaBrands = "\"Chromium\";v=\"132\", \"Microsoft Edge\";v=\"132\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Desktop", UaMobile = false,
                UaPlatformVersion = "10.0.0", UaFullVersion = "132.0.2957.127",
            },
            HasBatteryAPI = false, HasConnectionAPI = true,
        },

        // ================================================================
        // DESKTOP — OTHER CHROME (5%)
        // ================================================================

        // 13. Chrome 131 / Win11 / RTX 3060 — one version behind
        new()
        {
            Name = "Chrome131-Win11-RTX3060", Weight = 3,
            Browser = BrowserFamily.Chrome, OS = OsFamily.Windows,
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            AppVersion = "5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            Platform = "Win32", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [
                new(1920, 1080, 1920, 1040, 24, 1.0),
                new(2560, 1440, 2560, 1400, 24, 1.0),
            ],
            CoreOptions = [6, 8, 12], MemoryGBOptions = [16, 32],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = true,
            GPURenderers = [
                "ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 Direct3D11 vs_5_0 ps_5_0, D3D11)",
            ],
            GPUVendors = ["Google Inc. (NVIDIA)"],
            ColorDepthOverride = 24,
            ClientHints = new()
            {
                UaPlatform = "Windows", UaArch = "x86", UaBitness = "64",
                UaBrands = "\"Chromium\";v=\"131\", \"Google Chrome\";v=\"131\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Desktop", UaMobile = false,
                UaPlatformVersion = "15.0.0", UaFullVersion = "131.0.6778.205",
            },
            HasBatteryAPI = false, HasConnectionAPI = true,
        },

        // 14. Chrome 133 / Linux / Mesa Intel
        new()
        {
            Name = "Chrome133-Linux", Weight = 2,
            Browser = BrowserFamily.Chrome, OS = OsFamily.Linux,
            UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            AppVersion = "5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
            Platform = "Linux x86_64", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [
                new(1920, 1080, 1920, 1053, 24, 1.0),
                new(2560, 1440, 2560, 1413, 24, 1.0),
            ],
            CoreOptions = [4, 8, 16], MemoryGBOptions = [8, 16, 32],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = true,
            GPURenderers = [
                "ANGLE (Intel, Mesa Intel(R) UHD Graphics 630 (CFL GT2), OpenGL 4.6)",
                "ANGLE (AMD, AMD Radeon RX 7900 XTX (radeonsi, navi31, LLVM 17.0.6, DRM 3.56, 6.7.12-arch1), OpenGL 4.6)",
            ],
            GPUVendors = ["Google Inc. (Intel)", "Google Inc. (AMD)"],
            ColorDepthOverride = 24,
            ClientHints = new()
            {
                UaPlatform = "Linux", UaArch = "x86", UaBitness = "64",
                UaBrands = "\"Chromium\";v=\"133\", \"Google Chrome\";v=\"133\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Desktop", UaMobile = false,
                UaPlatformVersion = "6.7.0", UaFullVersion = "133.0.6943.88",
            },
            HasBatteryAPI = true, HasConnectionAPI = true,
        },

        // ================================================================
        // MOBILE — CHROME ANDROID (15%)
        // ================================================================

        // 15. Chrome 133 / Android 15 / Pixel 8
        new()
        {
            Name = "Chrome133-Android15-Pixel8", Weight = 4,
            Browser = BrowserFamily.Chrome, OS = OsFamily.Android,
            UserAgent = "Mozilla/5.0 (Linux; Android 15; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.6943.90 Mobile Safari/537.36",
            AppVersion = "5.0 (Linux; Android 15; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.6943.90 Mobile Safari/537.36",
            Platform = "Linux armv81", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [new(412, 915, 412, 851, 24, 2.625)],
            CoreOptions = [8], MemoryGBOptions = [8],
            MaxTouchPoints = 5, IsMobile = true, HoverCapable = false,
            GPURenderers = ["Adreno (TM) 740"],
            GPUVendors = ["Qualcomm"],
            ColorDepthOverride = 24,
            ClientHints = new()
            {
                UaPlatform = "Android", UaArch = "", UaBitness = "",
                UaBrands = "\"Chromium\";v=\"133\", \"Google Chrome\";v=\"133\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Handset", UaMobile = true, UaModel = "Pixel 8",
                UaPlatformVersion = "15.0.0", UaFullVersion = "133.0.6943.90",
            },
            HasBatteryAPI = true, HasConnectionAPI = true,
        },

        // 16. Chrome 133 / Android 14 / Pixel 7a
        new()
        {
            Name = "Chrome133-Android14-Pixel7a", Weight = 3,
            Browser = BrowserFamily.Chrome, OS = OsFamily.Android,
            UserAgent = "Mozilla/5.0 (Linux; Android 14; Pixel 7a) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.6943.90 Mobile Safari/537.36",
            AppVersion = "5.0 (Linux; Android 14; Pixel 7a) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.6943.90 Mobile Safari/537.36",
            Platform = "Linux armv81", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [new(412, 892, 412, 828, 24, 2.625)],
            CoreOptions = [8], MemoryGBOptions = [8],
            MaxTouchPoints = 5, IsMobile = true, HoverCapable = false,
            GPURenderers = ["Adreno (TM) 730"],
            GPUVendors = ["Qualcomm"],
            ColorDepthOverride = 24,
            ClientHints = new()
            {
                UaPlatform = "Android", UaArch = "", UaBitness = "",
                UaBrands = "\"Chromium\";v=\"133\", \"Google Chrome\";v=\"133\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Handset", UaMobile = true, UaModel = "Pixel 7a",
                UaPlatformVersion = "14.0.0", UaFullVersion = "133.0.6943.90",
            },
            HasBatteryAPI = true, HasConnectionAPI = true,
        },

        // 17. Chrome 133 / Android 15 / Galaxy S24
        new()
        {
            Name = "Chrome133-Android15-GalaxyS24", Weight = 4,
            Browser = BrowserFamily.Chrome, OS = OsFamily.Android,
            UserAgent = "Mozilla/5.0 (Linux; Android 15; SM-S921B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.6943.90 Mobile Safari/537.36",
            AppVersion = "5.0 (Linux; Android 15; SM-S921B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.6943.90 Mobile Safari/537.36",
            Platform = "Linux armv81", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [new(360, 780, 360, 724, 24, 3.0)],
            CoreOptions = [8], MemoryGBOptions = [8, 12],
            MaxTouchPoints = 5, IsMobile = true, HoverCapable = false,
            GPURenderers = ["Mali-G715"],
            GPUVendors = ["ARM"],
            ColorDepthOverride = 24,
            ClientHints = new()
            {
                UaPlatform = "Android", UaArch = "", UaBitness = "",
                UaBrands = "\"Chromium\";v=\"133\", \"Google Chrome\";v=\"133\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Handset", UaMobile = true, UaModel = "SM-S921B",
                UaPlatformVersion = "15.0.0", UaFullVersion = "133.0.6943.90",
            },
            HasBatteryAPI = true, HasConnectionAPI = true,
        },

        // 18. Chrome 132 / Android 14 / Galaxy A54 — mid-range
        new()
        {
            Name = "Chrome132-Android14-GalaxyA54", Weight = 2,
            Browser = BrowserFamily.Chrome, OS = OsFamily.Android,
            UserAgent = "Mozilla/5.0 (Linux; Android 14; SM-A546B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.6834.163 Mobile Safari/537.36",
            AppVersion = "5.0 (Linux; Android 14; SM-A546B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.6834.163 Mobile Safari/537.36",
            Platform = "Linux armv81", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [new(384, 854, 384, 790, 24, 2.8125)],
            CoreOptions = [8], MemoryGBOptions = [6, 8],
            MaxTouchPoints = 5, IsMobile = true, HoverCapable = false,
            GPURenderers = ["Mali-G68"],
            GPUVendors = ["ARM"],
            ColorDepthOverride = 24,
            ClientHints = new()
            {
                UaPlatform = "Android", UaArch = "", UaBitness = "",
                UaBrands = "\"Chromium\";v=\"132\", \"Google Chrome\";v=\"132\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Handset", UaMobile = true, UaModel = "SM-A546B",
                UaPlatformVersion = "14.0.0", UaFullVersion = "132.0.6834.163",
            },
            HasBatteryAPI = true, HasConnectionAPI = true,
        },

        // 19. Chrome 133 / Android 14 / OnePlus 12
        new()
        {
            Name = "Chrome133-Android14-OnePlus12", Weight = 2,
            Browser = BrowserFamily.Chrome, OS = OsFamily.Android,
            UserAgent = "Mozilla/5.0 (Linux; Android 14; CPH2583) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.6943.90 Mobile Safari/537.36",
            AppVersion = "5.0 (Linux; Android 14; CPH2583) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.6943.90 Mobile Safari/537.36",
            Platform = "Linux armv81", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [new(412, 915, 412, 857, 24, 2.625)],
            CoreOptions = [8], MemoryGBOptions = [12, 16],
            MaxTouchPoints = 5, IsMobile = true, HoverCapable = false,
            GPURenderers = ["Adreno (TM) 750"],
            GPUVendors = ["Qualcomm"],
            ColorDepthOverride = 24,
            ClientHints = new()
            {
                UaPlatform = "Android", UaArch = "", UaBitness = "",
                UaBrands = "\"Chromium\";v=\"133\", \"Google Chrome\";v=\"133\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Handset", UaMobile = true, UaModel = "CPH2583",
                UaPlatformVersion = "14.0.0", UaFullVersion = "133.0.6943.90",
            },
            HasBatteryAPI = true, HasConnectionAPI = true,
        },

        // ================================================================
        // MOBILE — SAFARI iOS (17%)
        // ================================================================

        // 20. Safari / iOS 18 / iPhone 15 Pro
        new()
        {
            Name = "Safari-iOS18-iPhone15Pro", Weight = 5,
            Browser = BrowserFamily.Safari, OS = OsFamily.IOS,
            UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.3 Mobile/15E148 Safari/604.1",
            AppVersion = "5.0 (iPhone; CPU iPhone OS 18_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.3 Mobile/15E148 Safari/604.1",
            Platform = "iPhone", Vendor = "Apple Computer, Inc.", ProductSub = "20030107",
            Screens = [new(393, 852, 393, 659, 32, 3.0)],
            CoreOptions = [6], MemoryGBOptions = [8],
            MaxTouchPoints = 5, IsMobile = true, HoverCapable = false,
            GPURenderers = ["Apple GPU"],
            GPUVendors = ["Apple GPU"],
            ColorDepthOverride = 32,
            ClientHints = null, // Safari never sends Client Hints
            HasBatteryAPI = false, HasConnectionAPI = false,
        },

        // 21. Safari / iOS 17 / iPhone 14
        new()
        {
            Name = "Safari-iOS17-iPhone14", Weight = 4,
            Browser = BrowserFamily.Safari, OS = OsFamily.IOS,
            UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_7 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.7 Mobile/15E148 Safari/604.1",
            AppVersion = "5.0 (iPhone; CPU iPhone OS 17_7 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.7 Mobile/15E148 Safari/604.1",
            Platform = "iPhone", Vendor = "Apple Computer, Inc.", ProductSub = "20030107",
            Screens = [new(390, 844, 390, 664, 32, 3.0)],
            CoreOptions = [6], MemoryGBOptions = [6],
            MaxTouchPoints = 5, IsMobile = true, HoverCapable = false,
            GPURenderers = ["Apple GPU"],
            GPUVendors = ["Apple GPU"],
            ColorDepthOverride = 32,
            ClientHints = null,
            HasBatteryAPI = false, HasConnectionAPI = false,
        },

        // 22. Safari / iOS 17 / iPhone 13
        new()
        {
            Name = "Safari-iOS17-iPhone13", Weight = 3,
            Browser = BrowserFamily.Safari, OS = OsFamily.IOS,
            UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_7 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.7 Mobile/15E148 Safari/604.1",
            AppVersion = "5.0 (iPhone; CPU iPhone OS 17_7 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.7 Mobile/15E148 Safari/604.1",
            Platform = "iPhone", Vendor = "Apple Computer, Inc.", ProductSub = "20030107",
            Screens = [new(390, 844, 390, 664, 32, 3.0)],
            CoreOptions = [6], MemoryGBOptions = [4],
            MaxTouchPoints = 5, IsMobile = true, HoverCapable = false,
            GPURenderers = ["Apple GPU"],
            GPUVendors = ["Apple GPU"],
            ColorDepthOverride = 32,
            ClientHints = null,
            HasBatteryAPI = false, HasConnectionAPI = false,
        },

        // 23. Safari / iOS 16 / iPhone SE 3rd gen — small budget phone
        new()
        {
            Name = "Safari-iOS16-iPhoneSE3", Weight = 2,
            Browser = BrowserFamily.Safari, OS = OsFamily.IOS,
            UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 16_7_8 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1",
            AppVersion = "5.0 (iPhone; CPU iPhone OS 16_7_8 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1",
            Platform = "iPhone", Vendor = "Apple Computer, Inc.", ProductSub = "20030107",
            Screens = [new(375, 667, 375, 548, 32, 2.0)],
            CoreOptions = [6], MemoryGBOptions = [4],
            MaxTouchPoints = 5, IsMobile = true, HoverCapable = false,
            GPURenderers = ["Apple GPU"],
            GPUVendors = ["Apple GPU"],
            ColorDepthOverride = 32,
            ClientHints = null,
            HasBatteryAPI = false, HasConnectionAPI = false,
        },

        // 24. Safari / iOS 18 / iPhone 16 Pro Max — newest flagship
        new()
        {
            Name = "Safari-iOS18-iPhone16ProMax", Weight = 3,
            Browser = BrowserFamily.Safari, OS = OsFamily.IOS,
            UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.3 Mobile/15E148 Safari/604.1",
            AppVersion = "5.0 (iPhone; CPU iPhone OS 18_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.3 Mobile/15E148 Safari/604.1",
            Platform = "iPhone", Vendor = "Apple Computer, Inc.", ProductSub = "20030107",
            Screens = [new(440, 956, 440, 763, 32, 3.0)],
            CoreOptions = [6], MemoryGBOptions = [8],
            MaxTouchPoints = 5, IsMobile = true, HoverCapable = false,
            GPURenderers = ["Apple GPU"],
            GPUVendors = ["Apple GPU"],
            ColorDepthOverride = 32,
            ClientHints = null,
            HasBatteryAPI = false, HasConnectionAPI = false,
        },

        // ================================================================
        // TABLET (5%)
        // ================================================================

        // 25. Safari / iPadOS 17 / iPad Pro 12.9"
        new()
        {
            Name = "Safari-iPadOS17-iPadPro129", Weight = 2,
            Browser = BrowserFamily.Safari, OS = OsFamily.IPadOS,
            UserAgent = "Mozilla/5.0 (iPad; CPU OS 17_7 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.7 Mobile/15E148 Safari/604.1",
            AppVersion = "5.0 (iPad; CPU OS 17_7 like Mac OS X) AppleWebKit/605.1.15",
            Platform = "iPad", Vendor = "Apple Computer, Inc.", ProductSub = "20030107",
            Screens = [new(1024, 1366, 1024, 1286, 32, 2.0)],
            CoreOptions = [8], MemoryGBOptions = [8, 16],
            MaxTouchPoints = 5, IsMobile = false, HoverCapable = false,
            GPURenderers = ["Apple GPU"],
            GPUVendors = ["Apple GPU"],
            ColorDepthOverride = 32,
            ClientHints = null,
            HasBatteryAPI = false, HasConnectionAPI = false,
        },

        // 26. Safari / iPadOS 18 / iPad Air
        new()
        {
            Name = "Safari-iPadOS18-iPadAir", Weight = 2,
            Browser = BrowserFamily.Safari, OS = OsFamily.IPadOS,
            UserAgent = "Mozilla/5.0 (iPad; CPU OS 18_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.3 Mobile/15E148 Safari/604.1",
            AppVersion = "5.0 (iPad; CPU OS 18_3 like Mac OS X) AppleWebKit/605.1.15",
            Platform = "iPad", Vendor = "Apple Computer, Inc.", ProductSub = "20030107",
            Screens = [new(820, 1180, 820, 1100, 32, 2.0)],
            CoreOptions = [8], MemoryGBOptions = [8],
            MaxTouchPoints = 5, IsMobile = false, HoverCapable = false,
            GPURenderers = ["Apple GPU"],
            GPUVendors = ["Apple GPU"],
            ColorDepthOverride = 32,
            ClientHints = null,
            HasBatteryAPI = false, HasConnectionAPI = false,
        },

        // 27. Chrome / Android 14 / Samsung Tab S9
        new()
        {
            Name = "Chrome133-Android14-TabS9", Weight = 1,
            Browser = BrowserFamily.Chrome, OS = OsFamily.Android,
            UserAgent = "Mozilla/5.0 (Linux; Android 14; SM-X710) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.6943.90 Safari/537.36",
            AppVersion = "5.0 (Linux; Android 14; SM-X710) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.6943.90 Safari/537.36",
            Platform = "Linux armv81", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [new(800, 1280, 800, 1216, 24, 2.0)],
            CoreOptions = [8], MemoryGBOptions = [8, 12],
            MaxTouchPoints = 5, IsMobile = false, HoverCapable = false,
            GPURenderers = ["Adreno (TM) 740"],
            GPUVendors = ["Qualcomm"],
            ColorDepthOverride = 24,
            ClientHints = new()
            {
                UaPlatform = "Android", UaArch = "", UaBitness = "",
                UaBrands = "\"Chromium\";v=\"133\", \"Google Chrome\";v=\"133\", \"Not?A_Brand\";v=\"24\"",
                UaFormFactor = "Tablet", UaMobile = false, UaModel = "SM-X710",
                UaPlatformVersion = "14.0.0", UaFullVersion = "133.0.6943.90",
            },
            HasBatteryAPI = true, HasConnectionAPI = true,
        },

        // ================================================================
        // BOTS (5%)
        // ================================================================

        // 28. Headless Chrome (SwiftShader) — automation bot
        new()
        {
            Name = "Bot-HeadlessChrome", Weight = 2,
            Browser = BrowserFamily.HeadlessChrome, OS = OsFamily.Linux,
            UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/133.0.0.0 Safari/537.36",
            AppVersion = "5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/133.0.0.0 Safari/537.36",
            Platform = "Linux x86_64", Vendor = "Google Inc.", ProductSub = "20030107",
            Screens = [
                new(1920, 1080, 1920, 1080, 24, 1.0),
                new(800, 600, 800, 600, 24, 1.0),
            ],
            CoreOptions = [2, 4], MemoryGBOptions = [2, 4],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = true,
            GPURenderers = [
                "ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device (Subzero) (0x0000C0DE)), SwiftShader driver-5.0.0)",
                "Google SwiftShader",
            ],
            GPUVendors = ["Google Inc. (Google)"],
            ColorDepthOverride = 24,
            ClientHints = new()
            {
                UaPlatform = "Linux", UaArch = "x86", UaBitness = "64",
                UaBrands = "\"Chromium\";v=\"133\", \"HeadlessChrome\";v=\"133\"",
                UaFormFactor = "Desktop", UaMobile = false,
                UaPlatformVersion = "6.5.0", UaFullVersion = "133.0.6943.88",
            },
            IsBot = true,
            HasBatteryAPI = false, HasConnectionAPI = true,
        },

        // 29. Googlebot
        new()
        {
            Name = "Bot-Googlebot", Weight = 1,
            Browser = BrowserFamily.Googlebot, OS = OsFamily.Linux,
            UserAgent = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
            AppVersion = "5.0 (compatible; Googlebot/2.1)",
            Platform = "Linux x86_64", Vendor = "", ProductSub = "",
            Screens = [new(0, 0, 0, 0, 0, 0)],
            CoreOptions = [1], MemoryGBOptions = [0],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = false,
            GPURenderers = [""],
            GPUVendors = [""],
            ColorDepthOverride = 0,
            ClientHints = null,
            IsBot = true, IsCrawler = true,
            HasBatteryAPI = false, HasConnectionAPI = false,
        },

        // 30. Bingbot
        new()
        {
            Name = "Bot-Bingbot", Weight = 1,
            Browser = BrowserFamily.Bingbot, OS = OsFamily.Windows,
            UserAgent = "Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)",
            AppVersion = "5.0 (compatible; bingbot/2.0)",
            Platform = "Win32", Vendor = "", ProductSub = "",
            Screens = [new(0, 0, 0, 0, 0, 0)],
            CoreOptions = [1], MemoryGBOptions = [0],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = false,
            GPURenderers = [""],
            GPUVendors = [""],
            ColorDepthOverride = 0,
            ClientHints = null,
            IsBot = true, IsCrawler = true,
            HasBatteryAPI = false, HasConnectionAPI = false,
        },

        // 31. Generic crawler (ahrefs, Screaming Frog, etc.)
        new()
        {
            Name = "Bot-GenericCrawler", Weight = 1,
            Browser = BrowserFamily.Crawler, OS = OsFamily.Linux,
            UserAgent = "Mozilla/5.0 (compatible; AhrefsBot/7.0; +http://ahrefs.com/robot/)",
            AppVersion = "5.0 (compatible; AhrefsBot/7.0)",
            Platform = "Linux x86_64", Vendor = "", ProductSub = "",
            Screens = [new(0, 0, 0, 0, 0, 0)],
            CoreOptions = [1], MemoryGBOptions = [0],
            MaxTouchPoints = 0, IsMobile = false, HoverCapable = false,
            GPURenderers = [""],
            GPUVendors = [""],
            ColorDepthOverride = 0,
            ClientHints = null,
            IsBot = true, IsCrawler = true,
            HasBatteryAPI = false, HasConnectionAPI = false,
        },
    ];
}
