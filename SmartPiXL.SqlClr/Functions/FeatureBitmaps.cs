// ─────────────────────────────────────────────────────────────────────────────
// CLR Functions: FeatureBitmap, AccessibilityBitmap, BotBitmap, EvasionBitmap
// Packs multiple boolean signals into a single INT for compact storage,
// fast comparison, and bitwise querying.
//
// Game-dev bit-packing: 17 booleans → 1 integer instead of 17 columns.
// Query with: WHERE (FeatureBitmap & 0x3) = 0x3  (bits 0 AND 1 set)
// ─────────────────────────────────────────────────────────────────────────────

using System.Data.SqlTypes;
using Microsoft.SqlServer.Server;

namespace SmartPiXL.SqlClr.Functions;

public static class FeatureBitmaps
{
    // ════════════════════════════════════════════════════════════════════════
    // FeatureBitmap — 17 browser feature detection booleans → 1 INT
    // ════════════════════════════════════════════════════════════════════════
    // Bit layout:
    //   0: localStorage       1: sessionStorage    2: indexedDB
    //   3: openDatabase        4: serviceWorker     5: webGL
    //   6: canvas              7: audioContext       8: webRTC
    //   9: bluetooth          10: midi              11: gamepads
    //  12: hardwareConcurrency 13: deviceMemory     14: touchSupport
    //  15: screenExtended     16: batteryAPI

    /// <summary>
    /// Packs 17 browser feature booleans into a single 32-bit integer.
    /// Each bit position corresponds to a specific browser API support flag.
    /// </summary>
    [SqlFunction(
        IsDeterministic = true,
        IsPrecise = true,
        DataAccess = DataAccessKind.None,
        SystemDataAccess = SystemDataAccessKind.None,
        Name = "FeatureBitmap")]
    public static SqlInt32 FeatureBitmap(
        SqlBoolean localStorage, SqlBoolean sessionStorage, SqlBoolean indexedDB,
        SqlBoolean openDatabase, SqlBoolean serviceWorker, SqlBoolean webGL,
        SqlBoolean canvas, SqlBoolean audioContext, SqlBoolean webRTC,
        SqlBoolean bluetooth, SqlBoolean midi, SqlBoolean gamepads,
        SqlBoolean hardwareConcurrency, SqlBoolean deviceMemory, SqlBoolean touchSupport,
        SqlBoolean screenExtended, SqlBoolean batteryAPI)
    {
        var bitmap = 0;
        if (!localStorage.IsNull && localStorage.Value) bitmap |= 1 << 0;
        if (!sessionStorage.IsNull && sessionStorage.Value) bitmap |= 1 << 1;
        if (!indexedDB.IsNull && indexedDB.Value) bitmap |= 1 << 2;
        if (!openDatabase.IsNull && openDatabase.Value) bitmap |= 1 << 3;
        if (!serviceWorker.IsNull && serviceWorker.Value) bitmap |= 1 << 4;
        if (!webGL.IsNull && webGL.Value) bitmap |= 1 << 5;
        if (!canvas.IsNull && canvas.Value) bitmap |= 1 << 6;
        if (!audioContext.IsNull && audioContext.Value) bitmap |= 1 << 7;
        if (!webRTC.IsNull && webRTC.Value) bitmap |= 1 << 8;
        if (!bluetooth.IsNull && bluetooth.Value) bitmap |= 1 << 9;
        if (!midi.IsNull && midi.Value) bitmap |= 1 << 10;
        if (!gamepads.IsNull && gamepads.Value) bitmap |= 1 << 11;
        if (!hardwareConcurrency.IsNull && hardwareConcurrency.Value) bitmap |= 1 << 12;
        if (!deviceMemory.IsNull && deviceMemory.Value) bitmap |= 1 << 13;
        if (!touchSupport.IsNull && touchSupport.Value) bitmap |= 1 << 14;
        if (!screenExtended.IsNull && screenExtended.Value) bitmap |= 1 << 15;
        if (!batteryAPI.IsNull && batteryAPI.Value) bitmap |= 1 << 16;
        return new SqlInt32(bitmap);
    }

    // ════════════════════════════════════════════════════════════════════════
    // AccessibilityBitmap — 9 accessibility feature flags → 1 INT
    // ════════════════════════════════════════════════════════════════════════
    // Bit layout:
    //   0: prefersReducedMotion  1: prefersColorScheme (dark)
    //   2: invertedColors         3: highContrast
    //   4: forcedColors           5: prefersReducedTransparency
    //   6: prefersContrast        7: screenReader
    //   8: accessibilityObj

    /// <summary>
    /// Packs 9 accessibility feature booleans into a single integer.
    /// </summary>
    [SqlFunction(
        IsDeterministic = true,
        IsPrecise = true,
        DataAccess = DataAccessKind.None,
        SystemDataAccess = SystemDataAccessKind.None,
        Name = "AccessibilityBitmap")]
    public static SqlInt32 AccessibilityBitmap(
        SqlBoolean prefersReducedMotion, SqlBoolean darkMode,
        SqlBoolean invertedColors, SqlBoolean highContrast,
        SqlBoolean forcedColors, SqlBoolean prefersReducedTransparency,
        SqlBoolean prefersContrast, SqlBoolean screenReader,
        SqlBoolean accessibilityObj)
    {
        var bitmap = 0;
        if (!prefersReducedMotion.IsNull && prefersReducedMotion.Value) bitmap |= 1 << 0;
        if (!darkMode.IsNull && darkMode.Value) bitmap |= 1 << 1;
        if (!invertedColors.IsNull && invertedColors.Value) bitmap |= 1 << 2;
        if (!highContrast.IsNull && highContrast.Value) bitmap |= 1 << 3;
        if (!forcedColors.IsNull && forcedColors.Value) bitmap |= 1 << 4;
        if (!prefersReducedTransparency.IsNull && prefersReducedTransparency.Value) bitmap |= 1 << 5;
        if (!prefersContrast.IsNull && prefersContrast.Value) bitmap |= 1 << 6;
        if (!screenReader.IsNull && screenReader.Value) bitmap |= 1 << 7;
        if (!accessibilityObj.IsNull && accessibilityObj.Value) bitmap |= 1 << 8;
        return new SqlInt32(bitmap);
    }

    // ════════════════════════════════════════════════════════════════════════
    // BotBitmap — top 20 bot detection signals → 1 INT
    // ════════════════════════════════════════════════════════════════════════
    // Bit layout:
    //   0: webdriver           1: headless           2: phantom
    //   3: selenium            4: puppeteer          5: playwright
    //   6: automationCtrl      7: nightmareJS        8: fakePlugins
    //   9: fakeLanguages      10: inconsistentUA    11: missingFeatures
    //  12: datacenter         13: highVelocity      14: noMouse
    //  15: noCookies          16: rapidFire         17: identicalTiming
    //  18: proxyDetected      19: torNode

    /// <summary>
    /// Packs 20 bot detection signals into a single integer.
    /// Enables fast bitwise queries: WHERE (BotBitmap &amp; 0x7) != 0 (any of first 3 signals).
    /// </summary>
    [SqlFunction(
        IsDeterministic = true,
        IsPrecise = true,
        DataAccess = DataAccessKind.None,
        SystemDataAccess = SystemDataAccessKind.None,
        Name = "BotBitmap")]
    public static SqlInt32 BotBitmap(
        SqlBoolean webdriver, SqlBoolean headless, SqlBoolean phantom,
        SqlBoolean selenium, SqlBoolean puppeteer, SqlBoolean playwright,
        SqlBoolean automationCtrl, SqlBoolean nightmareJS, SqlBoolean fakePlugins,
        SqlBoolean fakeLanguages, SqlBoolean inconsistentUA, SqlBoolean missingFeatures,
        SqlBoolean datacenter, SqlBoolean highVelocity, SqlBoolean noMouse,
        SqlBoolean noCookies, SqlBoolean rapidFire, SqlBoolean identicalTiming,
        SqlBoolean proxyDetected, SqlBoolean torNode)
    {
        var bitmap = 0;
        if (!webdriver.IsNull && webdriver.Value) bitmap |= 1 << 0;
        if (!headless.IsNull && headless.Value) bitmap |= 1 << 1;
        if (!phantom.IsNull && phantom.Value) bitmap |= 1 << 2;
        if (!selenium.IsNull && selenium.Value) bitmap |= 1 << 3;
        if (!puppeteer.IsNull && puppeteer.Value) bitmap |= 1 << 4;
        if (!playwright.IsNull && playwright.Value) bitmap |= 1 << 5;
        if (!automationCtrl.IsNull && automationCtrl.Value) bitmap |= 1 << 6;
        if (!nightmareJS.IsNull && nightmareJS.Value) bitmap |= 1 << 7;
        if (!fakePlugins.IsNull && fakePlugins.Value) bitmap |= 1 << 8;
        if (!fakeLanguages.IsNull && fakeLanguages.Value) bitmap |= 1 << 9;
        if (!inconsistentUA.IsNull && inconsistentUA.Value) bitmap |= 1 << 10;
        if (!missingFeatures.IsNull && missingFeatures.Value) bitmap |= 1 << 11;
        if (!datacenter.IsNull && datacenter.Value) bitmap |= 1 << 12;
        if (!highVelocity.IsNull && highVelocity.Value) bitmap |= 1 << 13;
        if (!noMouse.IsNull && noMouse.Value) bitmap |= 1 << 14;
        if (!noCookies.IsNull && noCookies.Value) bitmap |= 1 << 15;
        if (!rapidFire.IsNull && rapidFire.Value) bitmap |= 1 << 16;
        if (!identicalTiming.IsNull && identicalTiming.Value) bitmap |= 1 << 17;
        if (!proxyDetected.IsNull && proxyDetected.Value) bitmap |= 1 << 18;
        if (!torNode.IsNull && torNode.Value) bitmap |= 1 << 19;
        return new SqlInt32(bitmap);
    }

    // ════════════════════════════════════════════════════════════════════════
    // EvasionBitmap — 8 evasion countermeasure signals → 1 INT
    // ════════════════════════════════════════════════════════════════════════
    // Bit layout:
    //   0: canvasNoise        1: webglNoise         2: audioNoise
    //   3: fontMasking        4: timezoneSpoof      5: languageSpoof
    //   6: screenSpoof        7: pluginSpoof

    /// <summary>
    /// Packs 8 evasion/anti-fingerprint countermeasure signals into a single integer.
    /// </summary>
    [SqlFunction(
        IsDeterministic = true,
        IsPrecise = true,
        DataAccess = DataAccessKind.None,
        SystemDataAccess = SystemDataAccessKind.None,
        Name = "EvasionBitmap")]
    public static SqlInt32 EvasionBitmap(
        SqlBoolean canvasNoise, SqlBoolean webglNoise, SqlBoolean audioNoise,
        SqlBoolean fontMasking, SqlBoolean timezoneSpoof, SqlBoolean languageSpoof,
        SqlBoolean screenSpoof, SqlBoolean pluginSpoof)
    {
        var bitmap = 0;
        if (!canvasNoise.IsNull && canvasNoise.Value) bitmap |= 1 << 0;
        if (!webglNoise.IsNull && webglNoise.Value) bitmap |= 1 << 1;
        if (!audioNoise.IsNull && audioNoise.Value) bitmap |= 1 << 2;
        if (!fontMasking.IsNull && fontMasking.Value) bitmap |= 1 << 3;
        if (!timezoneSpoof.IsNull && timezoneSpoof.Value) bitmap |= 1 << 4;
        if (!languageSpoof.IsNull && languageSpoof.Value) bitmap |= 1 << 5;
        if (!screenSpoof.IsNull && screenSpoof.Value) bitmap |= 1 << 6;
        if (!pluginSpoof.IsNull && pluginSpoof.Value) bitmap |= 1 << 7;
        return new SqlInt32(bitmap);
    }
}
