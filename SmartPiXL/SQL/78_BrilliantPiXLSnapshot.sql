-- ============================================================================
-- 78: BrilliantPiXL Dashboard Snapshot
--
-- Pre-aggregated snapshot table for all 13 BrilliantPiXL dashboard endpoints.
-- Forge calls Dashboard.usp_RefreshBrilliantPiXL every 30s via watermark;
-- Sentinel reads the cached JSON — zero queries against PiXL.Parsed.
--
-- Components:
--   1. Dashboard schema
--   2. Filtered covering index on PiXL.Parsed (modern rows only)
--   3. Dashboard.BrilliantPiXL snapshot table (endpoint → JSON)
--   4. Dashboard.usp_RefreshBrilliantPiXL stored procedure
--   5. ETL.Watermark row for monitoring
--
-- CREATED: 2025-07-15
-- ============================================================================

SET QUOTED_IDENTIFIER ON;
GO

-- ====================================================================
-- 1. Dashboard schema
-- ====================================================================
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'Dashboard')
    EXEC('CREATE SCHEMA Dashboard');
GO

-- ====================================================================
-- 2. Filtered covering index — modern rows only
--
-- Replaces IX_Parsed_HitType_ReceivedAt (narrow, no INCLUDEs).
-- With <1000 modern rows total, this index is tiny despite the wide
-- INCLUDE list. The refresh proc loads modern rows into a temp table
-- via a single scan of this index — zero key lookups.
-- ====================================================================
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_Parsed_HitType_ReceivedAt'
      AND object_id = OBJECT_ID('PiXL.Parsed')
)
    DROP INDEX IX_Parsed_HitType_ReceivedAt ON PiXL.Parsed;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_Parsed_Modern_Dashboard'
      AND object_id = OBJECT_ID('PiXL.Parsed')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_Parsed_Modern_Dashboard
    ON PiXL.Parsed (ReceivedAt DESC)
    INCLUDE (
        SourceId,
        IPAddress, SessionId, BotScore, KnownBot, LeadQualityScore, BotName,
        ParsedBrowser, ParsedOS, ParsedOSVersion, ParsedDeviceType,
        PagePath, PageReferrer, SessionDurationSec,
        MaxMindRegion, MaxMindCity,
        ScreenWidth, ScreenHeight,
        CanvasFingerprint, WebGLFingerprint, AudioFingerprintHash,
        GPURenderer, HardwareConcurrency, DeviceMemoryGB, ConnectionType,
        MouseMoveCount, UA_Architecture,
        CanvasEvasionDetected, WebGLEvasionDetected, WebDriverDetected,
        EvasionSignalsV2, EvasionToolsDetected
    )
    WHERE HitType = 'modern'
    WITH (ONLINE = ON, MAXDOP = 4);
END;
GO

-- ====================================================================
-- 3. Dashboard.BrilliantPiXL — Snapshot table
--    One row per endpoint, stores pre-serialized JSON (camelCase keys).
-- ====================================================================
IF OBJECT_ID('Dashboard.BrilliantPiXL', 'U') IS NULL
BEGIN
    CREATE TABLE Dashboard.BrilliantPiXL
    (
        EndpointName  VARCHAR(50)    NOT NULL
            CONSTRAINT PK_Dashboard_BrilliantPiXL PRIMARY KEY CLUSTERED,
        JsonPayload   NVARCHAR(MAX)  NOT NULL,
        RefreshedAt   DATETIME2(3)   NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
GO

-- ====================================================================
-- 4. Dashboard.usp_RefreshBrilliantPiXL
--    Single-pass: loads modern rows into temp table, computes all 13
--    endpoint results as JSON, and upserts into snapshot table.
-- ====================================================================
CREATE OR ALTER PROCEDURE Dashboard.usp_RefreshBrilliantPiXL
AS
BEGIN
    SET NOCOUNT ON;

    -- ================================================================
    -- Load modern rows from last 30 days into temp table.
    -- The filtered covering index makes this a single narrow scan.
    -- ================================================================
    SELECT
        ReceivedAt, IPAddress, SessionId, BotScore, KnownBot,
        LeadQualityScore, BotName,
        ParsedBrowser, ParsedOS, ParsedOSVersion, ParsedDeviceType,
        PagePath, PageReferrer, SessionDurationSec,
        MaxMindRegion, MaxMindCity,
        ScreenWidth, ScreenHeight,
        CanvasFingerprint, WebGLFingerprint, AudioFingerprintHash,
        GPURenderer, HardwareConcurrency, DeviceMemoryGB, ConnectionType,
        MouseMoveCount, UA_Architecture,
        CanvasEvasionDetected, WebGLEvasionDetected, WebDriverDetected,
        EvasionSignalsV2, EvasionToolsDetected
    INTO #Modern
    FROM PiXL.Parsed
    WHERE HitType = 'modern'
      AND ReceivedAt >= DATEADD(DAY, -30, GETUTCDATE());

    DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @cutoff7  DATETIME2(3) = DATEADD(DAY, -7, GETUTCDATE());

    -- ================================================================
    -- 1. Summary (7 days, all traffic)
    -- ================================================================
    DECLARE @summary NVARCHAR(MAX) = (
        SELECT
            COUNT(*)                                           AS [totalHits],
            COUNT(DISTINCT IPAddress)                          AS [uniqueIPs],
            COUNT(DISTINCT SessionId)                          AS [uniqueSessions],
            SUM(CASE WHEN KnownBot = 1 OR BotScore >= 50 THEN 1 ELSE 0 END) AS [botHits],
            SUM(CASE WHEN KnownBot IS NULL AND (BotScore IS NULL OR BotScore < 50) THEN 1 ELSE 0 END) AS [humanHits],
            CAST(AVG(CAST(BotScore AS FLOAT)) AS DECIMAL(5,1)) AS [avgBotScore],
            CAST(AVG(CAST(LeadQualityScore AS FLOAT)) AS DECIMAL(5,1)) AS [avgLeadScore],
            SUM(CASE WHEN LeadQualityScore >= 70 THEN 1 ELSE 0 END) AS [highQualityLeads],
            MIN(ReceivedAt)                                    AS [earliestHit],
            MAX(ReceivedAt)                                    AS [latestHit]
        FROM #Modern
        WHERE ReceivedAt >= @cutoff7
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
    );
    SET @summary = ISNULL(@summary, '{"totalHits":0,"uniqueIPs":0,"uniqueSessions":0,"botHits":0,"humanHits":0,"avgBotScore":null,"avgLeadScore":null,"highQualityLeads":0,"earliestHit":null,"latestHit":null}');

    -- ================================================================
    -- 2. Daily volume trend (30 days, all traffic)
    -- ================================================================
    DECLARE @daily NVARCHAR(MAX) = (
        SELECT
            CAST(CAST(ReceivedAt AS DATE) AS DATETIME2(0))     AS [hitDate],
            COUNT(*)                                           AS [totalHits],
            SUM(CASE WHEN KnownBot = 1 OR BotScore >= 50 THEN 1 ELSE 0 END) AS [botHits],
            SUM(CASE WHEN KnownBot IS NULL AND (BotScore IS NULL OR BotScore < 50) THEN 1 ELSE 0 END) AS [humanHits],
            COUNT(DISTINCT IPAddress)                          AS [uniqueIPs],
            COUNT(DISTINCT SessionId)                          AS [sessions]
        FROM #Modern
        GROUP BY CAST(ReceivedAt AS DATE)
        ORDER BY CAST(ReceivedAt AS DATE) DESC
        FOR JSON PATH, INCLUDE_NULL_VALUES
    );
    SET @daily = ISNULL(@daily, '[]');

    -- ================================================================
    -- 3. Bot classification breakdown (7 days)
    -- ================================================================
    DECLARE @bots NVARCHAR(MAX) = (
        SELECT
            CASE
                WHEN KnownBot = 1                 THEN 'Known Bot'
                WHEN BotScore >= 50               THEN 'Likely Bot'
                WHEN BotScore >= 30               THEN 'Suspicious'
                WHEN BotScore IS NULL             THEN 'Unscored'
                ELSE                                   'Human'
            END                                    AS [classification],
            COUNT(*)                               AS [hits],
            COUNT(DISTINCT IPAddress)              AS [uniqueIPs],
            CAST(AVG(CAST(BotScore AS FLOAT)) AS DECIMAL(5,1)) AS [avgBotScore],
            CAST(AVG(CAST(LeadQualityScore AS FLOAT)) AS DECIMAL(5,1)) AS [avgLeadScore]
        FROM #Modern
        WHERE ReceivedAt >= @cutoff7
        GROUP BY CASE
                WHEN KnownBot = 1                 THEN 'Known Bot'
                WHEN BotScore >= 50               THEN 'Likely Bot'
                WHEN BotScore >= 30               THEN 'Suspicious'
                WHEN BotScore IS NULL             THEN 'Unscored'
                ELSE                                   'Human'
            END
        ORDER BY COUNT(*) DESC
        FOR JSON PATH, INCLUDE_NULL_VALUES
    );
    SET @bots = ISNULL(@bots, '[]');

    -- ================================================================
    -- 4. Known bot names (7 days)
    -- ================================================================
    DECLARE @botNames NVARCHAR(MAX) = (
        SELECT TOP 15
            BotName                                AS [botName],
            COUNT(*)                               AS [hits]
        FROM #Modern
        WHERE ReceivedAt >= @cutoff7
          AND KnownBot = 1
        GROUP BY BotName
        ORDER BY COUNT(*) DESC
        FOR JSON PATH, INCLUDE_NULL_VALUES
    );
    SET @botNames = ISNULL(@botNames, '[]');

    -- ================================================================
    -- 5. Traffic channels (7 days, human only)
    -- ================================================================
    DECLARE @channels NVARCHAR(MAX) = (
        SELECT
            CASE
                WHEN ParsedBrowser = 'Facebook'                       THEN 'Facebook'
                WHEN ParsedBrowser = 'Instagram'                      THEN 'Instagram'
                WHEN PageReferrer LIKE '%google%'                     THEN 'Google Search'
                WHEN PageReferrer LIKE '%bing%'                       THEN 'Bing Search'
                WHEN PageReferrer IS NULL OR PageReferrer = ''        THEN 'Direct'
                WHEN PageReferrer LIKE '%m1%' OR PageReferrer LIKE '%smart-pixl%'
                     OR PageReferrer LIKE '%ecommerce%'              THEN 'Internal / M1'
                ELSE 'Other Referral'
            END                                    AS [channel],
            COUNT(*)                               AS [hits],
            COUNT(DISTINCT IPAddress)              AS [uniqueVisitors],
            CAST(100.0 * COUNT(*) / NULLIF(SUM(COUNT(*)) OVER(), 0) AS DECIMAL(5,1)) AS [pctOfTotal],
            CAST(AVG(CAST(LeadQualityScore AS FLOAT)) AS DECIMAL(5,1)) AS [avgLeadScore],
            CAST(AVG(CAST(SessionDurationSec AS FLOAT)) AS DECIMAL(8,1)) AS [avgDwellSec]
        FROM #Modern
        WHERE ReceivedAt >= @cutoff7
          AND KnownBot IS NULL AND (BotScore IS NULL OR BotScore < 50)
        GROUP BY CASE
                WHEN ParsedBrowser = 'Facebook'                       THEN 'Facebook'
                WHEN ParsedBrowser = 'Instagram'                      THEN 'Instagram'
                WHEN PageReferrer LIKE '%google%'                     THEN 'Google Search'
                WHEN PageReferrer LIKE '%bing%'                       THEN 'Bing Search'
                WHEN PageReferrer IS NULL OR PageReferrer = ''        THEN 'Direct'
                WHEN PageReferrer LIKE '%m1%' OR PageReferrer LIKE '%smart-pixl%'
                     OR PageReferrer LIKE '%ecommerce%'              THEN 'Internal / M1'
                ELSE 'Other Referral'
            END
        ORDER BY COUNT(*) DESC
        FOR JSON PATH, INCLUDE_NULL_VALUES
    );
    SET @channels = ISNULL(@channels, '[]');

    -- ================================================================
    -- 6. Top landing pages (7 days, human only)
    -- ================================================================
    DECLARE @pages NVARCHAR(MAX) = (
        SELECT TOP 15
            PagePath                               AS [pagePath],
            COUNT(*)                               AS [hits],
            COUNT(DISTINCT IPAddress)              AS [uniqueVisitors],
            CAST(AVG(CAST(LeadQualityScore AS FLOAT)) AS DECIMAL(5,1)) AS [avgLeadScore],
            CAST(AVG(CAST(SessionDurationSec AS FLOAT)) AS DECIMAL(8,1)) AS [avgDwellSec]
        FROM #Modern
        WHERE ReceivedAt >= @cutoff7
          AND KnownBot IS NULL AND (BotScore IS NULL OR BotScore < 50)
        GROUP BY PagePath
        ORDER BY COUNT(*) DESC
        FOR JSON PATH, INCLUDE_NULL_VALUES
    );
    SET @pages = ISNULL(@pages, '[]');

    -- ================================================================
    -- 7. Fingerprint signal coverage (7 days, human only)
    -- ================================================================
    DECLARE @signals NVARCHAR(MAX) = (
        SELECT
            COUNT(*)                                                                              AS [totalHumanHits],
            SUM(CASE WHEN CanvasFingerprint IS NOT NULL AND CanvasFingerprint != '' THEN 1 ELSE 0 END) AS [hasCanvas],
            SUM(CASE WHEN WebGLFingerprint IS NOT NULL AND WebGLFingerprint != '' THEN 1 ELSE 0 END)  AS [hasWebGL],
            SUM(CASE WHEN AudioFingerprintHash IS NOT NULL AND AudioFingerprintHash != '' THEN 1 ELSE 0 END) AS [hasAudio],
            SUM(CASE WHEN GPURenderer IS NOT NULL AND GPURenderer != '' THEN 1 ELSE 0 END)        AS [hasGPU],
            SUM(CASE WHEN HardwareConcurrency > 0 THEN 1 ELSE 0 END)                              AS [hasCores],
            SUM(CASE WHEN DeviceMemoryGB > 0 THEN 1 ELSE 0 END)                                   AS [hasMemory],
            SUM(CASE WHEN ConnectionType IS NOT NULL AND ConnectionType != '' THEN 1 ELSE 0 END)   AS [hasNetwork],
            SUM(CASE WHEN MouseMoveCount > 0 THEN 1 ELSE 0 END)                                   AS [hasMouseMoves],
            SUM(CASE WHEN UA_Architecture IS NOT NULL AND UA_Architecture != '' THEN 1 ELSE 0 END) AS [hasClientHints],
            COUNT(DISTINCT CanvasFingerprint)                                                       AS [uniqueCanvas],
            COUNT(DISTINCT WebGLFingerprint)                                                        AS [uniqueWebGL],
            COUNT(DISTINCT GPURenderer)                                                             AS [uniqueGPU],
            COUNT(DISTINCT CONCAT(ScreenWidth, 'x', ScreenHeight))                                 AS [uniqueResolutions]
        FROM #Modern
        WHERE ReceivedAt >= @cutoff7
          AND KnownBot IS NULL AND (BotScore IS NULL OR BotScore < 50)
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
    );
    SET @signals = ISNULL(@signals, '{"totalHumanHits":0,"hasCanvas":0,"hasWebGL":0,"hasAudio":0,"hasGPU":0,"hasCores":0,"hasMemory":0,"hasNetwork":0,"hasMouseMoves":0,"hasClientHints":0,"uniqueCanvas":0,"uniqueWebGL":0,"uniqueGPU":0,"uniqueResolutions":0}');

    -- ================================================================
    -- 8. Devices — Browsers / OS / Device Types (7 days, human only)
    --    Composed as {"browsers":[...],"oses":[...],"deviceTypes":[...]}
    -- ================================================================
    DECLARE @browsers NVARCHAR(MAX) = (
        SELECT TOP 10
            ParsedBrowser                          AS [name],
            COUNT(*)                               AS [hits]
        FROM #Modern
        WHERE ReceivedAt >= @cutoff7
          AND KnownBot IS NULL AND (BotScore IS NULL OR BotScore < 50)
        GROUP BY ParsedBrowser
        ORDER BY COUNT(*) DESC
        FOR JSON PATH, INCLUDE_NULL_VALUES
    );

    DECLARE @oses NVARCHAR(MAX) = (
        SELECT TOP 10
            ParsedOS                               AS [name],
            COUNT(*)                               AS [hits]
        FROM #Modern
        WHERE ReceivedAt >= @cutoff7
          AND KnownBot IS NULL AND (BotScore IS NULL OR BotScore < 50)
        GROUP BY ParsedOS
        ORDER BY COUNT(*) DESC
        FOR JSON PATH, INCLUDE_NULL_VALUES
    );

    DECLARE @deviceTypes NVARCHAR(MAX) = (
        SELECT TOP 10
            ParsedDeviceType                       AS [name],
            COUNT(*)                               AS [hits]
        FROM #Modern
        WHERE ReceivedAt >= @cutoff7
          AND KnownBot IS NULL AND (BotScore IS NULL OR BotScore < 50)
        GROUP BY ParsedDeviceType
        ORDER BY COUNT(*) DESC
        FOR JSON PATH, INCLUDE_NULL_VALUES
    );

    DECLARE @devices NVARCHAR(MAX) =
        '{"browsers":' + ISNULL(@browsers, '[]')
      + ',"oses":' + ISNULL(@oses, '[]')
      + ',"deviceTypes":' + ISNULL(@deviceTypes, '[]') + '}';

    -- ================================================================
    -- 9. Geographic distribution (7 days, human only, ≥2 hits)
    -- ================================================================
    DECLARE @geo NVARCHAR(MAX) = (
        SELECT TOP 20
            COALESCE(MaxMindRegion, '(unknown)')   AS [region],
            COALESCE(MaxMindCity, '(unknown)')     AS [city],
            COUNT(*)                               AS [hits],
            COUNT(DISTINCT IPAddress)              AS [uniqueIPs],
            CAST(AVG(CAST(LeadQualityScore AS FLOAT)) AS DECIMAL(5,1)) AS [avgLeadScore]
        FROM #Modern
        WHERE ReceivedAt >= @cutoff7
          AND KnownBot IS NULL AND (BotScore IS NULL OR BotScore < 50)
        GROUP BY COALESCE(MaxMindRegion, '(unknown)'), COALESCE(MaxMindCity, '(unknown)')
        HAVING COUNT(*) >= 2
        ORDER BY COUNT(*) DESC
        FOR JSON PATH, INCLUDE_NULL_VALUES
    );
    SET @geo = ISNULL(@geo, '[]');

    -- ================================================================
    -- 10. Evasion detection (7 days)
    --     Composed as {"summary":{...},"signals":[...]}
    -- ================================================================
    DECLARE @evasionSummary NVARCHAR(MAX) = (
        SELECT
            COUNT(*)                                                                              AS [totalHits],
            SUM(CASE WHEN CanvasEvasionDetected = 1 THEN 1 ELSE 0 END)                           AS [canvasEvasions],
            SUM(CASE WHEN WebGLEvasionDetected = 1 THEN 1 ELSE 0 END)                            AS [webGLEvasions],
            SUM(CASE WHEN WebDriverDetected = 1 THEN 1 ELSE 0 END)                               AS [webDriverDetected],
            SUM(CASE WHEN EvasionSignalsV2 IS NOT NULL AND EvasionSignalsV2 != '' THEN 1 ELSE 0 END) AS [evasionV2Hits],
            SUM(CASE WHEN EvasionToolsDetected IS NOT NULL AND EvasionToolsDetected != '' THEN 1 ELSE 0 END) AS [evasionToolHits]
        FROM #Modern
        WHERE ReceivedAt >= @cutoff7
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
    );

    DECLARE @evasionSignals NVARCHAR(MAX) = (
        SELECT TOP 10
            EvasionSignalsV2                       AS [signal],
            COUNT(*)                               AS [hits]
        FROM #Modern
        WHERE ReceivedAt >= @cutoff7
          AND EvasionSignalsV2 IS NOT NULL AND EvasionSignalsV2 != ''
        GROUP BY EvasionSignalsV2
        ORDER BY COUNT(*) DESC
        FOR JSON PATH, INCLUDE_NULL_VALUES
    );

    DECLARE @evasion NVARCHAR(MAX) =
        '{"summary":' + ISNULL(@evasionSummary, '{"totalHits":0,"canvasEvasions":0,"webGLEvasions":0,"webDriverDetected":0,"evasionV2Hits":0,"evasionToolHits":0}')
      + ',"signals":' + ISNULL(@evasionSignals, '[]') + '}';

    -- ================================================================
    -- 11. Top screen resolutions (7 days, human only)
    -- ================================================================
    DECLARE @screens NVARCHAR(MAX) = (
        SELECT TOP 12
            CONCAT(ScreenWidth, 'x', ScreenHeight) AS [resolution],
            COUNT(*)                               AS [hits]
        FROM #Modern
        WHERE ReceivedAt >= @cutoff7
          AND KnownBot IS NULL AND (BotScore IS NULL OR BotScore < 50)
          AND ScreenWidth IS NOT NULL AND ScreenWidth > 0
        GROUP BY CONCAT(ScreenWidth, 'x', ScreenHeight)
        ORDER BY COUNT(*) DESC
        FOR JSON PATH, INCLUDE_NULL_VALUES
    );
    SET @screens = ISNULL(@screens, '[]');

    -- ================================================================
    -- 12. Lead quality distribution (7 days, human only)
    -- ================================================================
    DECLARE @leads NVARCHAR(MAX) = (
        SELECT
            CASE
                WHEN LeadQualityScore >= 80 THEN '80-100 (Excellent)'
                WHEN LeadQualityScore >= 70 THEN '70-79 (High)'
                WHEN LeadQualityScore >= 60 THEN '60-69 (Good)'
                WHEN LeadQualityScore >= 50 THEN '50-59 (Fair)'
                ELSE '0-49 (Low)'
            END                                    AS [scoreBucket],
            COUNT(*)                               AS [hits],
            COUNT(DISTINCT IPAddress)              AS [uniqueVisitors],
            CAST(AVG(CAST(SessionDurationSec AS FLOAT)) AS DECIMAL(8,1)) AS [avgDwellSec]
        FROM #Modern
        WHERE ReceivedAt >= @cutoff7
          AND KnownBot IS NULL AND (BotScore IS NULL OR BotScore < 50)
          AND LeadQualityScore IS NOT NULL
        GROUP BY CASE
                WHEN LeadQualityScore >= 80 THEN '80-100 (Excellent)'
                WHEN LeadQualityScore >= 70 THEN '70-79 (High)'
                WHEN LeadQualityScore >= 60 THEN '60-69 (Good)'
                WHEN LeadQualityScore >= 50 THEN '50-59 (Fair)'
                ELSE '0-49 (Low)'
            END
        ORDER BY MIN(LeadQualityScore) DESC
        FOR JSON PATH, INCLUDE_NULL_VALUES
    );
    SET @leads = ISNULL(@leads, '[]');

    -- ================================================================
    -- 13. OS version breakdown (7 days, human only, with share %)
    -- ================================================================
    DECLARE @humanTotal INT = (
        SELECT COUNT(*) FROM #Modern
        WHERE ReceivedAt >= @cutoff7
          AND KnownBot IS NULL AND (BotScore IS NULL OR BotScore < 50)
    );

    DECLARE @osVersions NVARCHAR(MAX) = (
        SELECT TOP 25
            COALESCE(NULLIF(ParsedOS, ''), '(unknown)')        AS [os],
            COALESCE(NULLIF(ParsedOSVersion, ''), '(unknown)') AS [version],
            COUNT(*)                                           AS [hits],
            CAST(COUNT(*) * 100.0 / NULLIF(@humanTotal, 0) AS DECIMAL(5,1)) AS [sharePct]
        FROM #Modern
        WHERE ReceivedAt >= @cutoff7
          AND KnownBot IS NULL AND (BotScore IS NULL OR BotScore < 50)
        GROUP BY COALESCE(NULLIF(ParsedOS, ''), '(unknown)'),
                 COALESCE(NULLIF(ParsedOSVersion, ''), '(unknown)')
        ORDER BY COUNT(*) DESC
        FOR JSON PATH, INCLUDE_NULL_VALUES
    );
    SET @osVersions = ISNULL(@osVersions, '[]');

    -- ================================================================
    -- UPSERT all 13 snapshots in one atomic MERGE
    -- ================================================================
    MERGE Dashboard.BrilliantPiXL AS tgt
    USING (VALUES
        ('summary',     @summary),
        ('daily',       @daily),
        ('bots',        @bots),
        ('bot-names',   @botNames),
        ('channels',    @channels),
        ('pages',       @pages),
        ('signals',     @signals),
        ('devices',     @devices),
        ('geo',         @geo),
        ('evasion',     @evasion),
        ('screens',     @screens),
        ('leads',       @leads),
        ('os-versions', @osVersions)
    ) AS src(EndpointName, JsonPayload)
    ON tgt.EndpointName = src.EndpointName
    WHEN MATCHED THEN UPDATE SET
        JsonPayload = src.JsonPayload,
        RefreshedAt = @now
    WHEN NOT MATCHED THEN INSERT (EndpointName, JsonPayload, RefreshedAt)
        VALUES (src.EndpointName, src.JsonPayload, @now);

    DROP TABLE #Modern;
END;
GO

-- ====================================================================
-- 5. ETL.Watermark row for monitoring
-- ====================================================================
IF NOT EXISTS (SELECT 1 FROM ETL.Watermark WHERE ProcessName = 'RefreshBrilliantPiXL')
    INSERT INTO ETL.Watermark (ProcessName, LastProcessedId) VALUES ('RefreshBrilliantPiXL', 0);
GO

-- ====================================================================
-- Seed initial data by running the refresh proc once
-- ====================================================================
EXEC Dashboard.usp_RefreshBrilliantPiXL;
GO
