/**
 * ╔══════════════════════════════════════════════════════════════════╗
 * ║  SmartPiXL RED TEAM — Security Probe (Pass 3)                  ║
 * ║  Attack Vector 2: Application Security Testing                 ║
 * ╚══════════════════════════════════════════════════════════════════╝
 *
 * Tests the SmartPiXL tracking endpoint for:
 *   1. Stored XSS via pixel query parameters & headers
 *   2. SQL injection via GetQueryParam UDF
 *   3. Path traversal through catch-all routes
 *   4. HTTP verb tampering on GET-only endpoints
 *   5. Response header security analysis
 *   6. Debug endpoint exposure (we ARE on localhost)
 *   7. Parameter pollution & oversized payload handling
 *   8. CORS policy testing
 *
 * All payloads are tagged synthetic=1&securityProbe=1 (fair play).
 * Uses Playwright's API request context for clean HTTPS handling.
 *
 * Usage:
 *   node security-probe.js
 */

const { request } = require('playwright');

const TARGET = 'https://smartpixl.info';
const SYNTHETIC_TAG = 'synthetic=1&securityProbe=1&pass=3';
const PIXEL_PATH = '/12345/1_SMART.GIF';

// ============================================================================
// ATTACK PAYLOADS
// ============================================================================

const XSS_PAYLOADS = [
    // Reflected/Stored XSS via query parameter values
    { name: 'basic-script-tag',     value: '<script>alert("xss")</script>' },
    { name: 'img-onerror',          value: '"><img src=x onerror=alert(1)>' },
    { name: 'svg-onload',           value: '<svg onload=alert(1)>' },
    { name: 'event-handler',        value: '" onfocus=alert(1) autofocus="' },
    { name: 'javascript-uri',       value: 'javascript:alert(document.cookie)' },
    { name: 'encoded-script',       value: '%3Cscript%3Ealert(1)%3C/script%3E' },
    { name: 'double-encoded',       value: '%253Cscript%253Ealert(1)%253C%252Fscript%253E' },
    { name: 'null-byte',            value: '%00<script>alert(1)</script>' },
    { name: 'unicode-escape',       value: '\\u003cscript\\u003ealert(1)\\u003c/script\\u003e' },
    { name: 'template-literal',     value: '${alert(1)}' },
    { name: 'html-entity',          value: '&lt;script&gt;alert(1)&lt;/script&gt;' },
    { name: 'data-uri',             value: 'data:text/html,<script>alert(1)</script>' },
];

const SQLI_PAYLOADS = [
    // SQL injection through parameter values
    { name: 'basic-sqli',           value: "' OR 1=1 --" },
    { name: 'union-select',         value: "' UNION SELECT 1,2,3,4,5--" },
    { name: 'drop-table',           value: "'; DROP TABLE PiXL_Test; --" },
    { name: 'time-based-blind',     value: "'; WAITFOR DELAY '0:0:5' --" },
    { name: 'stacked-query',        value: "'; EXEC xp_cmdshell('whoami')--" },
    { name: 'error-based',          value: "' AND 1=CONVERT(int,(SELECT TOP 1 name FROM sys.tables))--" },
    { name: 'comment-bypass',       value: "admin'/**/OR/**/1=1--" },
    { name: 'hex-encoded',          value: "0x27204f5220313d31202d2d" },
    { name: 'param-in-param-name',  value: "sw=1920&sh=1080' OR '1'='1" },
    { name: 'nested-function',      value: "'; DECLARE @x NVARCHAR(4000); SET @x=N'DROP TABLE t'; EXEC(@x);--" },
];

const PATH_TRAVERSAL_PAYLOADS = [
    { name: 'basic-dotdot',         path: '/../../web.config' },
    { name: 'double-encoded-dt',    path: '/%2e%2e/%2e%2e/web.config' },
    { name: 'backslash-dt',         path: '/..\\..\\web.config' },
    { name: 'appsettings',          path: '/../../appsettings.json' },
    { name: 'null-byte-dt',         path: '/../../web.config%00.gif' },
    { name: 'images-traverse',      path: '/images/../../../appsettings.json' },
    { name: 'images-encoded',       path: '/images/..%2f..%2f..%2fappsettings.json' },
    { name: 'images-backslash',     path: '/images/..\\..\\..\\appsettings.json' },
    { name: 'deep-traverse',        path: '/../../../../../../../etc/passwd' },
    { name: 'windows-path',         path: '/C:\\Windows\\win.ini' },
];

const VERB_TAMPERING = ['POST', 'PUT', 'DELETE', 'PATCH', 'OPTIONS', 'HEAD', 'TRACE'];

// ============================================================================
// RESULTS COLLECTOR
// ============================================================================
class Results {
    constructor() {
        this.findings = [];
        this.tests = 0;
        this.passed = 0;
        this.flagged = 0;
    }

    record(category, name, status, detail) {
        this.tests++;
        const isFinding = status === 'VULNERABLE' || status === 'SUSPICIOUS';
        if (isFinding) this.flagged++;
        else this.passed++;
        this.findings.push({ category, name, status, detail });
        const icon = status === 'VULNERABLE' ? '!!!' :
                     status === 'SUSPICIOUS' ? '(?)'  :
                     status === 'INFO'       ? '(i)' : ' . ';
        console.log(`   ${icon} [${category}] ${name}: ${status} — ${detail}`);
    }

    summary() {
        console.log('\n   +====================================================+');
        console.log(  '   | SECURITY PROBE SUMMARY                              |');
        console.log(  '   +====================================================+');
        console.log(`   | Tests run:    ${this.tests}`);
        console.log(`   | Clean:        ${this.passed}`);
        console.log(`   | Findings:     ${this.flagged}`);
        console.log(  '   +----------------------------------------------------+');

        const vulns = this.findings.filter(f => f.status === 'VULNERABLE');
        const suspicious = this.findings.filter(f => f.status === 'SUSPICIOUS');
        const info = this.findings.filter(f => f.status === 'INFO');

        if (vulns.length > 0) {
            console.log('   | VULNERABILITIES:');
            vulns.forEach(v => console.log(`   |   [${v.category}] ${v.name}: ${v.detail}`));
        }
        if (suspicious.length > 0) {
            console.log('   | SUSPICIOUS:');
            suspicious.forEach(s => console.log(`   |   [${s.category}] ${s.name}: ${s.detail}`));
        }
        if (info.length > 0) {
            console.log('   | INFO:');
            info.forEach(i => console.log(`   |   [${i.category}] ${i.name}: ${i.detail}`));
        }
        console.log('   +====================================================+\n');
    }
}

// ============================================================================
// TEST 1: STORED XSS VIA PIXEL PARAMETERS
// These payloads get stored in QueryString and displayed in dashboards.
// ============================================================================
async function testXSS(api, results) {
    console.log('\n   --- XSS Testing ---');

    for (const payload of XSS_PAYLOADS) {
        try {
            // Inject XSS payload into the 'ua' parameter (displayed in dashboards)
            const qs = `sw=1920&sh=1080&tier=5&ua=${encodeURIComponent(payload.value)}&${SYNTHETIC_TAG}`;
            const resp = await api.get(`${TARGET}${PIXEL_PATH}?${qs}`);

            // The pixel endpoint always returns 200 with a GIF
            // XSS is stored — check if the response leaks the payload back
            const body = await resp.body();
            const bodyStr = body.toString();
            const headers = resp.headers();

            if (bodyStr.includes(payload.value) || bodyStr.includes('<script>')) {
                results.record('XSS', payload.name, 'VULNERABLE', 'Payload reflected in response body');
            } else if (!headers['x-content-type-options']) {
                results.record('XSS', payload.name, 'SUSPICIOUS', 'Missing X-Content-Type-Options (stored XSS risk via MIME sniffing)');
            } else {
                results.record('XSS', payload.name, 'CLEAN', `Stored (${resp.status()}) — verify dashboard rendering`);
            }
        } catch (e) {
            results.record('XSS', payload.name, 'CLEAN', `Error: ${e.message.substring(0, 100)}`);
        }
    }

    // Also test XSS via custom headers (stored in HeadersJson)
    try {
        const resp = await api.get(`${TARGET}${PIXEL_PATH}?sw=1&${SYNTHETIC_TAG}`, {
            headers: {
                'User-Agent': '<script>alert("xss-via-ua")</script>',
                'Referer': '"><img src=x onerror=alert(1)>',
                'Accept-Language': '<svg onload=alert(1)>',
            }
        });
        // These get stored in HeadersJson — check if reflected
        const body = (await resp.body()).toString();
        if (body.includes('<script>') || body.includes('onerror')) {
            results.record('XSS', 'header-injection', 'VULNERABLE', 'XSS payload reflected from headers');
        } else {
            results.record('XSS', 'header-injection', 'CLEAN', `Header payloads stored — verify HeadersJson rendering`);
        }
    } catch (e) {
        results.record('XSS', 'header-injection', 'CLEAN', 'Headers not reflected');
    }
}

// ============================================================================
// TEST 2: SQL INJECTION VIA QUERY PARAMETERS
// GetQueryParam UDF does CHARINDEX/SUBSTRING — should be safe from SQLi
// but we test TRY_CAST paths and edge cases.
// ============================================================================
async function testSQLi(api, results) {
    console.log('\n   --- SQL Injection Testing ---');

    for (const payload of SQLI_PAYLOADS) {
        try {
            const qs = `sw=${encodeURIComponent(payload.value)}&sh=1080&tier=5&${SYNTHETIC_TAG}`;
            const resp = await api.get(`${TARGET}${PIXEL_PATH}?${qs}`);
            const status = resp.status();
            const body = (await resp.body()).toString();

            // SQL errors would cause 500 or error messages in response
            if (status === 500) {
                results.record('SQLi', payload.name, 'SUSPICIOUS', 'Server returned 500 — possible SQL error');
            } else if (body.includes('SqlException') || body.includes('syntax error') ||
                       body.includes('unclosed quotation') || body.includes('EXECUTE permission')) {
                results.record('SQLi', payload.name, 'VULNERABLE', 'SQL error message leaked in response');
            } else {
                results.record('SQLi', payload.name, 'CLEAN', `Stored safely (${status})`);
            }
        } catch (e) {
            results.record('SQLi', payload.name, 'CLEAN', `Error: ${e.message.substring(0, 100)}`);
        }
    }

    // Test: Can we inject via parameter NAME (not value)?
    try {
        const resp = await api.get(`${TARGET}${PIXEL_PATH}?sw=1920&' OR 1=1--=malicious&${SYNTHETIC_TAG}`);
        results.record('SQLi', 'param-name-injection', resp.status() === 500 ? 'SUSPICIOUS' : 'CLEAN',
            `Status: ${resp.status()}`);
    } catch (e) {
        results.record('SQLi', 'param-name-injection', 'CLEAN', 'Handled');
    }
}

// ============================================================================
// TEST 3: PATH TRAVERSAL
// The catch-all route serves GIFs for everything — but we test for
// file content leaks via response body/headers.
// ============================================================================
async function testPathTraversal(api, results) {
    console.log('\n   --- Path Traversal Testing ---');

    for (const payload of PATH_TRAVERSAL_PAYLOADS) {
        try {
            const resp = await api.get(`${TARGET}${payload.path}`);
            const status = resp.status();
            const contentType = resp.headers()['content-type'] || '';
            const body = await resp.body();

            // If we get back something that's NOT a GIF, path traversal might work
            if (contentType.includes('application/json') && body.toString().includes('ConnectionString')) {
                results.record('PathTraversal', payload.name, 'VULNERABLE', 'Config file contents leaked!');
            } else if (body.length > 100 && !contentType.includes('image/gif') && status === 200) {
                results.record('PathTraversal', payload.name, 'SUSPICIOUS',
                    `Non-GIF response: ${contentType} (${body.length} bytes)`);
            } else if (status === 400 || status === 404) {
                results.record('PathTraversal', payload.name, 'CLEAN', `Blocked (${status})`);
            } else {
                results.record('PathTraversal', payload.name, 'CLEAN', `GIF returned (${status})`);
            }
        } catch (e) {
            results.record('PathTraversal', payload.name, 'CLEAN', 'Request failed');
        }
    }
}

// ============================================================================
// TEST 4: HTTP VERB TAMPERING
// All tracking endpoints are MapGet — other verbs should 404/405.
// ============================================================================
async function testVerbTampering(api, results) {
    console.log('\n   --- HTTP Verb Tampering ---');

    for (const verb of VERB_TAMPERING) {
        try {
            const resp = await api.fetch(`${TARGET}${PIXEL_PATH}?${SYNTHETIC_TAG}`, {
                method: verb,
            });
            const status = resp.status();

            if (verb === 'OPTIONS') {
                // CORS preflight — check what's allowed
                const allow = resp.headers()['allow'] || resp.headers()['access-control-allow-methods'] || '';
                results.record('Verb', verb, allow ? 'INFO' : 'CLEAN',
                    `Status: ${status}, Allow: ${allow || 'not disclosed'}`);
            } else if (verb === 'HEAD') {
                // HEAD should mirror GET without body
                results.record('Verb', verb, 'CLEAN', `Status: ${status}`);
            } else if (status === 200) {
                results.record('Verb', verb, 'SUSPICIOUS', `${verb} returned 200 — endpoint accepts non-GET`);
            } else {
                results.record('Verb', verb, 'CLEAN', `Rejected (${status})`);
            }
        } catch (e) {
            results.record('Verb', verb, 'CLEAN', `Error: ${e.message.substring(0, 80)}`);
        }
    }
}

// ============================================================================
// TEST 5: RESPONSE HEADER SECURITY ANALYSIS
// Check for missing security headers on the main pixel endpoint.
// ============================================================================
async function testHeaders(api, results) {
    console.log('\n   --- Response Header Analysis ---');

    try {
        const resp = await api.get(`${TARGET}${PIXEL_PATH}?sw=1&${SYNTHETIC_TAG}`);
        const h = resp.headers();

        // Security headers check
        const checks = [
            { header: 'x-content-type-options', expected: 'nosniff', severity: 'SUSPICIOUS' },
            { header: 'x-frame-options', expected: null, severity: 'INFO' },
            { header: 'content-security-policy', expected: null, severity: 'INFO' },
            { header: 'strict-transport-security', expected: null, severity: 'INFO' },
            { header: 'x-xss-protection', expected: null, severity: 'INFO' },
            { header: 'referrer-policy', expected: null, severity: 'INFO' },
            { header: 'permissions-policy', expected: null, severity: 'INFO' },
        ];

        for (const check of checks) {
            const value = h[check.header];
            if (!value) {
                results.record('Headers', check.header, check.severity,
                    `Missing — ${check.header} not set on pixel response`);
            } else {
                results.record('Headers', check.header, 'CLEAN', `Present: ${value}`);
            }
        }

        // Information disclosure
        const server = h['server'];
        if (server) {
            results.record('Headers', 'server-disclosure', 'INFO', `Server header: ${server}`);
        }

        // Cache headers
        const cache = h['cache-control'];
        results.record('Headers', 'cache-control', 'CLEAN', `Value: ${cache || 'not set'}`);

    } catch (e) {
        results.record('Headers', 'analysis', 'CLEAN', `Error: ${e.message.substring(0, 100)}`);
    }
}

// ============================================================================
// TEST 6: DEBUG ENDPOINT EXPOSURE
// /debug/headers is restricted to localhost — we ARE on localhost.
// This check verifies what sensitive data it exposes.
// ============================================================================
async function testDebugEndpoint(api, results) {
    console.log('\n   --- Debug Endpoint Exposure ---');

    try {
        const resp = await api.get(`${TARGET}/debug/headers`);
        const status = resp.status();

        if (status === 200) {
            const body = (await resp.body()).toString();
            let parsed;
            try { parsed = JSON.parse(body); } catch(e) { parsed = null; }

            if (parsed) {
                const fields = Object.keys(parsed);
                results.record('Debug', 'endpoint-accessible', 'INFO',
                    `Accessible from localhost — exposes ${fields.length} fields: ${fields.join(', ')}`);

                // Check for sensitive fields
                if (parsed.IPAddress) {
                    results.record('Debug', 'ip-exposed', 'INFO', `IP field: ${parsed.IPAddress}`);
                }
                if (parsed.HeadersJson) {
                    results.record('Debug', 'headers-exposed', 'INFO', `Raw headers JSON exposed`);
                }
            } else {
                results.record('Debug', 'endpoint-accessible', 'INFO',
                    `Returns non-JSON (${status}): ${body.substring(0, 200)}`);
            }
        } else if (status === 404) {
            results.record('Debug', 'endpoint-hidden', 'CLEAN', 'Returns 404 for non-localhost');
        } else {
            results.record('Debug', 'endpoint-status', 'INFO', `Status: ${status}`);
        }
    } catch (e) {
        results.record('Debug', 'endpoint', 'CLEAN', `Error: ${e.message.substring(0, 100)}`);
    }

    // Also probe for other potential debug/admin endpoints
    const probeEndpoints = [
        '/health',
        '/api/dashboard/kpis',
        '/api/dashboard/live-feed',
        '/debug',
        '/admin',
        '/swagger',
        '/swagger/index.html',
        '/.env',
        '/web.config',
        '/appsettings.json',
    ];

    for (const ep of probeEndpoints) {
        try {
            const resp = await api.get(`${TARGET}${ep}`);
            const status = resp.status();
            const ct = resp.headers()['content-type'] || '';
            const body = (await resp.body());

            if (ep === '/health' && status === 200) {
                const data = JSON.parse(body.toString());
                results.record('Debug', ep, 'INFO',
                    `Health exposed — status: ${data.status}, queueDepth: ${data.queueDepth}`);
            } else if (status === 200 && ct.includes('json')) {
                results.record('Debug', ep, 'SUSPICIOUS',
                    `Returns JSON data (${body.length} bytes) — may expose internal info`);
            } else if (status === 200 && body.length > 43 && !ct.includes('image/gif')) {
                results.record('Debug', ep, 'INFO',
                    `Returns content (${status}, ${ct}, ${body.length}b)`);
            } else {
                results.record('Debug', ep, 'CLEAN', `Status: ${status}`);
            }
        } catch (e) {
            results.record('Debug', ep, 'CLEAN', 'Unreachable');
        }
    }
}

// ============================================================================
// TEST 7: PARAMETER POLLUTION & OVERSIZED PAYLOADS
// ============================================================================
async function testEdgeCases(api, results) {
    console.log('\n   --- Edge Cases & Parameter Pollution ---');

    // Duplicate parameter names
    try {
        const resp = await api.get(
            `${TARGET}${PIXEL_PATH}?sw=1920&sw=HACK&sh=1080&${SYNTHETIC_TAG}`);
        results.record('Edge', 'duplicate-param', 'INFO',
            `Duplicate params accepted (${resp.status()}) — GetQueryParam takes first occurrence`);
    } catch (e) {
        results.record('Edge', 'duplicate-param', 'CLEAN', 'Handled');
    }

    // Empty parameter values
    try {
        const resp = await api.get(
            `${TARGET}${PIXEL_PATH}?sw=&sh=&tier=&${SYNTHETIC_TAG}`);
        results.record('Edge', 'empty-params', 'CLEAN', `Status: ${resp.status()}`);
    } catch (e) {
        results.record('Edge', 'empty-params', 'CLEAN', 'Handled');
    }

    // Very long parameter value (10KB)
    try {
        const longVal = 'A'.repeat(10000);
        const resp = await api.get(
            `${TARGET}${PIXEL_PATH}?sw=${longVal}&${SYNTHETIC_TAG}`);
        results.record('Edge', 'oversized-param', resp.status() === 414 ? 'CLEAN' : 'INFO',
            `Status: ${resp.status()} — ${resp.status() === 200 ? 'accepted long value' : 'rejected'}`);
    } catch (e) {
        results.record('Edge', 'oversized-param', 'CLEAN', `Rejected: ${e.message.substring(0, 80)}`);
    }

    // Very long query string (50KB — many params)
    try {
        const params = [];
        for (let i = 0; i < 500; i++) params.push(`p${i}=${'X'.repeat(100)}`);
        const resp = await api.get(
            `${TARGET}${PIXEL_PATH}?${params.join('&')}&${SYNTHETIC_TAG}`);
        results.record('Edge', 'oversized-querystring',
            resp.status() === 414 ? 'CLEAN' : 'INFO',
            `Status: ${resp.status()} — ${resp.status() === 200 ? '50KB query stored' : 'rejected'}`);
    } catch (e) {
        results.record('Edge', 'oversized-querystring', 'CLEAN', `Rejected: ${e.message.substring(0, 80)}`);
    }

    // Null bytes in parameter
    try {
        const resp = await api.get(
            `${TARGET}${PIXEL_PATH}?sw=1920%00INJECTED&sh=1080&${SYNTHETIC_TAG}`);
        results.record('Edge', 'null-byte-param', 'CLEAN', `Status: ${resp.status()}`);
    } catch (e) {
        results.record('Edge', 'null-byte-param', 'CLEAN', 'Handled');
    }

    // Unicode in parameter names
    try {
        const resp = await api.get(
            `${TARGET}${PIXEL_PATH}?sw=1920&%E2%80%8B=hidden&${SYNTHETIC_TAG}`);
        results.record('Edge', 'unicode-param-name', 'CLEAN', `Status: ${resp.status()}`);
    } catch (e) {
        results.record('Edge', 'unicode-param-name', 'CLEAN', 'Handled');
    }
}

// ============================================================================
// TEST 8: CORS POLICY
// ============================================================================
async function testCORS(api, results) {
    console.log('\n   --- CORS Policy Testing ---');

    try {
        // CORS with arbitrary origin
        const resp = await api.get(`${TARGET}${PIXEL_PATH}?sw=1&${SYNTHETIC_TAG}`, {
            headers: { 'Origin': 'https://evil.example.com' }
        });
        const acao = resp.headers()['access-control-allow-origin'];
        const acac = resp.headers()['access-control-allow-credentials'];

        if (acao === '*') {
            results.record('CORS', 'wildcard-origin', 'INFO',
                `ACAO: * — any origin can read responses (expected for tracking pixels)`);
        } else if (acao === 'https://evil.example.com') {
            results.record('CORS', 'origin-reflection', 'SUSPICIOUS',
                `Origin reflected back — combined with credentials could leak data`);
            if (acac === 'true') {
                results.record('CORS', 'credentials-with-reflection', 'VULNERABLE',
                    'Origin reflection + allow-credentials = cookie theft possible');
            }
        } else if (!acao) {
            results.record('CORS', 'no-cors', 'CLEAN', 'No CORS headers (same-origin only)');
        } else {
            results.record('CORS', 'cors-policy', 'CLEAN', `ACAO: ${acao}`);
        }
    } catch (e) {
        results.record('CORS', 'test', 'CLEAN', `Error: ${e.message.substring(0, 100)}`);
    }

    // Preflight test
    try {
        const resp = await api.fetch(`${TARGET}${PIXEL_PATH}`, {
            method: 'OPTIONS',
            headers: {
                'Origin': 'https://evil.example.com',
                'Access-Control-Request-Method': 'GET',
                'Access-Control-Request-Headers': 'X-Custom-Header',
            }
        });
        const acam = resp.headers()['access-control-allow-methods'];
        const acah = resp.headers()['access-control-allow-headers'];
        results.record('CORS', 'preflight', acam ? 'INFO' : 'CLEAN',
            `Methods: ${acam || 'not set'}, Headers: ${acah || 'not set'}`);
    } catch (e) {
        results.record('CORS', 'preflight', 'CLEAN', 'No preflight support');
    }
}

// ============================================================================
// TEST 9: JS ENDPOINT INJECTION
// /js/{companyId}/{pixlId}.js — Regex validation: ^[a-zA-Z0-9\-_]{1,64}$
// ============================================================================
async function testJSEndpoint(api, results) {
    console.log('\n   --- JS Endpoint Injection ---');

    const jsPayloads = [
        { name: 'script-in-companyId', path: '/js/<script>/1.js' },
        { name: 'dotdot-companyId',    path: '/js/../../../etc/passwd/1.js' },
        { name: 'url-encoded-slash',   path: '/js/..%2f..%2f/1.js' },
        { name: 'long-companyId',      path: `/js/${'A'.repeat(100)}/1.js` },
        { name: 'null-in-companyId',   path: '/js/test%00evil/1.js' },
        { name: 'special-chars',       path: '/js/test;alert(1)/1.js' },
        { name: 'valid-companyId',     path: '/js/12345/1.js' },
    ];

    for (const p of jsPayloads) {
        try {
            const resp = await api.get(`${TARGET}${p.path}`);
            const status = resp.status();
            const body = (await resp.body()).toString();
            const ct = resp.headers()['content-type'] || '';

            if (status === 400 || body.includes('invalid parameters')) {
                results.record('JS', p.name, 'CLEAN', `Blocked (${status})`);
            } else if (status === 200 && ct.includes('javascript')) {
                if (body.includes('<script>') || body.includes('alert(1)')) {
                    results.record('JS', p.name, 'VULNERABLE', 'XSS payload in JS response');
                } else if (p.name === 'valid-companyId') {
                    results.record('JS', p.name, 'CLEAN', `Valid JS returned (${body.length} bytes)`);
                } else {
                    results.record('JS', p.name, 'SUSPICIOUS',
                        `Unexpected 200 for invalid input (${body.length} bytes)`);
                }
            } else {
                results.record('JS', p.name, 'CLEAN', `Status: ${status}`);
            }
        } catch (e) {
            results.record('JS', p.name, 'CLEAN', `Error: ${e.message.substring(0, 80)}`);
        }
    }
}

// ============================================================================
// TEST 10: QUEUE FLOODING (gentle — just verify rate handling)
// ============================================================================
async function testQueueFlood(api, results) {
    console.log('\n   --- Queue Flood Test (gentle) ---');

    // Send 50 rapid-fire requests (not a real DoS, just testing queue behavior)
    const promises = [];
    for (let i = 0; i < 50; i++) {
        promises.push(
            api.get(`${TARGET}${PIXEL_PATH}?sw=1&flood=${i}&${SYNTHETIC_TAG}`)
                .then(r => r.status())
                .catch(() => 'error')
        );
    }
    const statuses = await Promise.all(promises);
    const ok = statuses.filter(s => s === 200).length;
    const errors = statuses.filter(s => s === 'error').length;
    const other = statuses.filter(s => s !== 200 && s !== 'error');

    results.record('Flood', 'rapid-fire-50', ok === 50 ? 'CLEAN' : 'INFO',
        `${ok}/50 accepted, ${errors} errors, ${other.length} other: [${[...new Set(other)].join(',')}]`);

    // Check health endpoint for queue depth
    try {
        const health = await api.get(`${TARGET}/health`);
        const data = JSON.parse((await health.body()).toString());
        results.record('Flood', 'queue-depth-after', 'INFO',
            `Queue depth: ${data.queueDepth}, Status: ${data.queueStatus}`);
    } catch (e) {
        results.record('Flood', 'queue-depth', 'CLEAN', 'Health endpoint unavailable');
    }
}

// ============================================================================
// MAIN
// ============================================================================
async function main() {
    console.log();
    console.log('================================================================');
    console.log('  RED TEAM — Security Probe (Pass 3)');
    console.log('  Attack Vector 2: Application Security Testing');
    console.log('================================================================');
    console.log();
    console.log(`  Target: ${TARGET}`);
    console.log(`  Tag:    ${SYNTHETIC_TAG}`);
    console.log();

    const api = await request.newContext({
        ignoreHTTPSErrors: true,
        extraHTTPHeaders: {
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36',
        },
    });

    const results = new Results();

    try {
        await testXSS(api, results);
        await testSQLi(api, results);
        await testPathTraversal(api, results);
        await testVerbTampering(api, results);
        await testHeaders(api, results);
        await testDebugEndpoint(api, results);
        await testEdgeCases(api, results);
        await testCORS(api, results);
        await testJSEndpoint(api, results);
        await testQueueFlood(api, results);
    } finally {
        await api.dispose();
    }

    results.summary();
}

main().catch(err => { console.error('Fatal:', err); process.exit(1); });
