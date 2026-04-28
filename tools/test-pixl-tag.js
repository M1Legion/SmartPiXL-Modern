/**
 * SmartPiXL end-to-end tag test.
 *
 * Runs Chromium headless against a page that has the PiXL tag installed,
 * pre-grants geolocation (so no user interaction needed), captures every
 * request fired at *smartpixl.info*, and reports what landed.
 *
 * Usage:
 *   node tools\test-pixl-tag.js                  # tests https://smartpixl.info/
 *   node tools\test-pixl-tag.js <url>            # tests any page with the tag
 *
 * Exit 0 = main pixel beacon seen AND geo follow-up seen with granted status.
 * Exit 1 = something went wrong; see log.
 */

const { chromium } = require('playwright');

const TARGET = process.argv[2] || 'https://smartpixl.info/';
// San Francisco — arbitrary, just so the grant has coords to return.
const FAKE_GEO = { latitude: 37.7749, longitude: -122.4194, accuracy: 40 };

(async () => {
    console.log(`[test] target: ${TARGET}`);
    const browser = await chromium.launch({ headless: true });
    const ctx = await browser.newContext({
        ignoreHTTPSErrors: true,
        permissions: ['geolocation'],
        geolocation: FAKE_GEO,
        userAgent: 'Mozilla/5.0 (SmartPiXL-Test/1.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36'
    });
    const page = await ctx.newPage();

    // Instrument the page BEFORE any script runs so we can see if our code
    // calls navigator.geolocation at all, and whether permissions.query fires.
    await page.addInitScript(() => {
        const origGCP = navigator.geolocation && navigator.geolocation.getCurrentPosition.bind(navigator.geolocation);
        if (origGCP) {
            navigator.geolocation.getCurrentPosition = function (success, err, opts) {
                console.log('[PROBE] getCurrentPosition called opts=' + JSON.stringify(opts));
                return origGCP(
                    pos => { console.log('[PROBE] geo success lat=' + pos.coords.latitude); success && success(pos); },
                    e   => { console.log('[PROBE] geo error code=' + (e && e.code) + ' msg=' + (e && e.message)); err && err(e); },
                    opts
                );
            };
        }
        const origQuery = navigator.permissions && navigator.permissions.query && navigator.permissions.query.bind(navigator.permissions);
        if (origQuery) {
            navigator.permissions.query = function (d) {
                console.log('[PROBE] permissions.query ' + JSON.stringify(d));
                return origQuery(d).then(r => { console.log('[PROBE] permissions.query result state=' + r.state); return r; });
            };
        }
        const origSend = navigator.sendBeacon && navigator.sendBeacon.bind(navigator);
        if (origSend) {
            navigator.sendBeacon = function (u, data) {
                // Blob → async read so we can log what's in the payload.
                let preview = '';
                try {
                    if (data && data.text) { data.text().then(t => console.log('[PROBE] sendBeacon body: ' + (t || '').slice(0, 200))); }
                    else if (typeof data === 'string') preview = data.slice(0, 200);
                } catch (e) {}
                console.log('[PROBE] sendBeacon ' + u + (preview ? ' body: ' + preview : ''));
                return origSend(u, data);
            };
        }
    });

    const consoleErrors = [];
    const consoleAll = [];
    page.on('console', m => {
        consoleAll.push(`[${m.type()}] ${m.text()}`);
        if (m.type() === 'error') consoleErrors.push(m.text());
    });
    page.on('pageerror', e => { consoleErrors.push('PAGEERROR: ' + e.message); consoleAll.push('PAGEERROR: ' + e.message); });

    const hits = [];
    page.on('request', req => {
        const u = req.url();
        if (/smartpixl\.info\/.+_SMART\.(GIF|DATA|js)/i.test(u)) {
            hits.push({
                method: req.method(),
                url: u,
                resourceType: req.resourceType(),
                body: req.postData() || ''
            });
        }
    });

    try {
        await page.goto(TARGET, { waitUntil: 'load', timeout: 20000 });
    } catch (e) {
        console.log(`[test] page.goto failed: ${e.message}`);
        await browser.close();
        process.exit(1);
    }

    // Wait long enough for: 3s engagement timer + geo acquisition + 3.2s watchPosition + beacon
    await page.waitForTimeout(9000);

    // Probe the geolocation API directly from the page to see what the browser reports.
    const geoProbe = await page.evaluate(() => new Promise(resolve => {
        const out = { hasApi: !!navigator.geolocation, hasPermissionsApi: !!(navigator.permissions && navigator.permissions.query) };
        if (!navigator.geolocation) { resolve(out); return; }
        const t0 = Date.now();
        navigator.geolocation.getCurrentPosition(
            pos => { out.ok = true; out.ms = Date.now() - t0; out.lat = pos.coords.latitude; out.lon = pos.coords.longitude; resolve(out); },
            err => { out.ok = false; out.ms = Date.now() - t0; out.errCode = err.code; out.errMsg = err.message; resolve(out); },
            { enableHighAccuracy: false, timeout: 5000, maximumAge: 0 }
        );
    }));

    await browser.close();

    console.log(`\n[test] PiXL network activity (${hits.length} requests):`);
    hits.forEach((h, i) => {
        const isScript   = /_SMART\.js/i.test(h.url);
        const isFollowup = /_geo_followup=1/i.test(h.body);
        const tag = isScript ? ' [SCRIPT]'
                   : isFollowup ? ' [GEO-FOLLOWUP]'
                   : ' [MAIN-BEACON]';
        console.log(`  ${i + 1}. [${h.method}]${tag}`);
        console.log(`     ${h.url.length > 140 ? h.url.slice(0, 140) + '...' : h.url}`);
        if (h.body) {
            // Dump selected fields + size so we can see what the script captured.
            const m = {};
            h.body.split('&').forEach(kv => {
                const [k, v] = kv.split('=');
                if (k) m[k] = decodeURIComponent(v || '').slice(0, 60);
            });
            const keys = Object.keys(m);
            const geoKeys = keys.filter(k => /_usr_|_geo_/.test(k));
            console.log(`     body-size: ${h.body.length} bytes, ${keys.length} fields`);
            console.log(`     geo-fields: ${geoKeys.length === 0 ? '(none)' : JSON.stringify(Object.fromEntries(geoKeys.map(k => [k, m[k]])))}`);
            console.log(`     first-keys: ${keys.slice(0, 10).join(', ')}`);
        }
    });

    const gotScript    = hits.some(h => /_SMART\.js/i.test(h.url));
    const gotMain      = hits.some(h => !/_SMART\.js/i.test(h.url) && !/_geo_followup=1/i.test(h.body));
    const geoHit       = hits.find(h => /_geo_followup=1/i.test(h.body));
    const geoGranted   = geoHit && /_usr_geo_status=granted/i.test(geoHit.body);

    // Playwright does not expose the body of sendBeacon(Blob) via request.postData(),
    // so we ALSO read the probe-instrumented console logs as ground truth.
    const probeFollowup = consoleAll.some(m => /sendBeacon body:.*_geo_followup=1/.test(m));
    const probeGranted  = consoleAll.some(m => /sendBeacon body:.*_usr_geo_status=granted/.test(m));
    const probeGeoCalled = consoleAll.some(m => /getCurrentPosition called/.test(m));
    const probeLatLon    = consoleAll.find(m => /_usr_lat=([\d.-]+)/.test(m));
    const latLonMatch    = probeLatLon && probeLatLon.match(/_usr_lat=([\d.-]+)&_usr_lon=([\d.-]+)/);

    console.log(`\n[test] RESULTS:`);
    console.log(`   script loaded:         ${gotScript          ? 'YES' : 'NO'}`);
    console.log(`   main pixel beacon:     ${gotMain            ? 'YES' : 'NO'}`);
    console.log(`   getCurrentPosition:    ${probeGeoCalled     ? 'YES' : 'NO'}`);
    console.log(`   geo follow-up beacon:  ${probeFollowup      ? 'YES' : 'NO'}`);
    console.log(`   geo status = granted:  ${probeGranted       ? 'YES' : 'NO'}`);
    if (latLonMatch) {
        console.log(`   captured coords:       ${latLonMatch[1]}, ${latLonMatch[2]}`);
    }
    if (consoleErrors.length) {
        console.log(`\n[test] CONSOLE ERRORS:`);
        consoleErrors.forEach(e => console.log('   - ' + e));
    } else {
        console.log(`   console errors:        none`);
    }

    const ok = gotScript && gotMain && probeFollowup && probeGranted && consoleErrors.length === 0;

    console.log(`\n[test] GEO PROBE (direct navigator.geolocation call):`);
    console.log('   ' + JSON.stringify(geoProbe));

    if (!ok && consoleAll.length) {
        console.log(`\n[test] CONSOLE MESSAGES (first 40):`);
        consoleAll.slice(0, 40).forEach(m => console.log('   ' + m));
    }

    console.log(`\n[test] ${ok ? 'PASS' : 'FAIL'}`);
    process.exit(ok ? 0 : 1);
})();
