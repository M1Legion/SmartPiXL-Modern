-- Migration 70: Edge Server-Side Signal Columns
-- Deployed: 2026-03-01
-- Purpose: Store 21 HTTP-level signals extracted at Edge from the request context.
--          These signals cannot be spoofed by client-side JavaScript and provide:
--            1. Unique fingerprint data (header order hash, HTTP version, Accept-Encoding)
--            2. Cross-validation with JS-collected values (Accept-Language vs navigator.language,
--               DNT header vs navigator.doNotTrack, Client Hints vs userAgentData)
--            3. Free coverage for PiXL Lite and legacy hits
--
-- C# side: TrackingEndpoints.CaptureAndEnqueue appends _srv_* params before Forge delivery
-- SQL side: Phase 8E in usp_ParseNewHits extracts them into typed columns
-- Forge side: ParsedRecordParser.Parse maps _srv_* QS params to array indices

-- =====================================================================
-- Step 1: ALTER TABLE — Add 21 new columns to PiXL.Parsed
-- =====================================================================

-- HTTP protocol + header fingerprint
ALTER TABLE PiXL.Parsed ADD Srv_HttpVersion         VARCHAR(10)    NULL;
ALTER TABLE PiXL.Parsed ADD Srv_HeaderCount          INT            NULL;
ALTER TABLE PiXL.Parsed ADD Srv_HeaderOrderHash      CHAR(16)       NULL;

-- Standard request headers (cross-validation)
ALTER TABLE PiXL.Parsed ADD Srv_AcceptLanguage       NVARCHAR(500)  NULL;
ALTER TABLE PiXL.Parsed ADD Srv_AcceptEncoding       NVARCHAR(200)  NULL;
ALTER TABLE PiXL.Parsed ADD Srv_Accept               NVARCHAR(500)  NULL;
ALTER TABLE PiXL.Parsed ADD Srv_Connection           VARCHAR(20)    NULL;
ALTER TABLE PiXL.Parsed ADD Srv_DNT                  VARCHAR(5)     NULL;

-- Fetch Metadata (no JS equivalent — reveals request initiation context)
ALTER TABLE PiXL.Parsed ADD Srv_FetchSite            VARCHAR(20)    NULL;
ALTER TABLE PiXL.Parsed ADD Srv_FetchMode            VARCHAR(20)    NULL;
ALTER TABLE PiXL.Parsed ADD Srv_FetchDest            VARCHAR(20)    NULL;

-- Client Hints — low entropy (sent by default in Chromium 89+)
ALTER TABLE PiXL.Parsed ADD Srv_CH_UA                NVARCHAR(500)  NULL;
ALTER TABLE PiXL.Parsed ADD Srv_CH_Platform          NVARCHAR(50)   NULL;
ALTER TABLE PiXL.Parsed ADD Srv_CH_Mobile            VARCHAR(5)     NULL;

-- Client Hints — high entropy (only when customer page sets Permissions-Policy)
ALTER TABLE PiXL.Parsed ADD Srv_CH_Model             NVARCHAR(100)  NULL;
ALTER TABLE PiXL.Parsed ADD Srv_CH_PlatformVersion   NVARCHAR(50)   NULL;
ALTER TABLE PiXL.Parsed ADD Srv_CH_Arch              NVARCHAR(20)   NULL;
ALTER TABLE PiXL.Parsed ADD Srv_CH_Bitness           VARCHAR(5)     NULL;
ALTER TABLE PiXL.Parsed ADD Srv_CH_FullVersionList   NVARCHAR(500)  NULL;

-- Priority hints (Chrome 124+, no JS equivalent)
ALTER TABLE PiXL.Parsed ADD Srv_Priority             VARCHAR(50)    NULL;

-- TLS fingerprint headers (populated by reverse proxy / Cloudflare when present)
ALTER TABLE PiXL.Parsed ADD Srv_TlsVersion           VARCHAR(20)    NULL;
ALTER TABLE PiXL.Parsed ADD Srv_TlsCipher            VARCHAR(100)   NULL;

-- =====================================================================
-- Step 2: Update ETL proc — Add Phase 8E for Edge HTTP signals
-- =====================================================================
-- The Phase 8E UPDATE is added AFTER Phase 8D and BEFORE Phase 9.
-- It runs inside the existing IF @Inserted > 0 guard (migration 62)
-- so it only processes rows not pre-parsed by the Forge.
--
-- NOTE: This requires re-deploying the full proc via mssql_run_query.
-- The Phase 8E block to insert after Phase 8D in the live proc is:
--
--   UPDATE pp SET
--       Srv_HttpVersion        = dbo.GetQueryParam(src.QueryString, '_srv_httpVer'),
--       Srv_HeaderCount        = TRY_CAST(dbo.GetQueryParam(src.QueryString, '_srv_hdrCount') AS INT),
--       Srv_HeaderOrderHash    = dbo.GetQueryParam(src.QueryString, '_srv_hdrOrder'),
--       Srv_AcceptLanguage     = dbo.GetQueryParam(src.QueryString, '_srv_acceptLang'),
--       Srv_AcceptEncoding     = dbo.GetQueryParam(src.QueryString, '_srv_acceptEnc'),
--       Srv_Accept             = dbo.GetQueryParam(src.QueryString, '_srv_accept'),
--       Srv_Connection         = dbo.GetQueryParam(src.QueryString, '_srv_conn'),
--       Srv_DNT                = dbo.GetQueryParam(src.QueryString, '_srv_dnt'),
--       Srv_FetchSite          = dbo.GetQueryParam(src.QueryString, '_srv_fetchSite'),
--       Srv_FetchMode          = dbo.GetQueryParam(src.QueryString, '_srv_fetchMode'),
--       Srv_FetchDest          = dbo.GetQueryParam(src.QueryString, '_srv_fetchDest'),
--       Srv_CH_UA              = dbo.GetQueryParam(src.QueryString, '_srv_chUa'),
--       Srv_CH_Platform        = dbo.GetQueryParam(src.QueryString, '_srv_chPlatform'),
--       Srv_CH_Mobile          = dbo.GetQueryParam(src.QueryString, '_srv_chMobile'),
--       Srv_CH_Model           = dbo.GetQueryParam(src.QueryString, '_srv_chModel'),
--       Srv_CH_PlatformVersion = dbo.GetQueryParam(src.QueryString, '_srv_chPlatVer'),
--       Srv_CH_Arch            = dbo.GetQueryParam(src.QueryString, '_srv_chArch'),
--       Srv_CH_Bitness         = dbo.GetQueryParam(src.QueryString, '_srv_chBitness'),
--       Srv_CH_FullVersionList = dbo.GetQueryParam(src.QueryString, '_srv_chFullVer'),
--       Srv_Priority           = dbo.GetQueryParam(src.QueryString, '_srv_priority'),
--       Srv_TlsVersion         = dbo.GetQueryParam(src.QueryString, '_srv_tlsVer'),
--       Srv_TlsCipher          = dbo.GetQueryParam(src.QueryString, '_srv_tlsCipher')
--   FROM PiXL.Parsed pp JOIN PiXL.Raw src ON pp.SourceId = src.Id
--   WHERE pp.SourceId > @LastId AND pp.SourceId <= @MaxId;
