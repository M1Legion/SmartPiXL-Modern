-- ============================================================================
-- Migration 79: Nonclustered Columnstore Index for Dashboard Views
-- ============================================================================
-- The vw_Dash_* views scan PiXL.Parsed (57M+ rows, 888 GB) with 30-day
-- rolling windows. Without columnstore, each view does a full rowstore scan
-- taking 80-600+ seconds. A nonclustered columnstore index (NCCI) on the
-- 43 columns referenced by dashboard views will:
--
--   1. Store data in compressed columnar format (10-100x compression)
--   2. Enable batch-mode execution (10-100x faster aggregation)
--   3. Allow SQL Server to read only needed columns per query
--   4. Coexist with existing rowstore indexes (no schema changes)
--
-- Expected impact: dashboard view queries drop from 80-600s to 2-30s.
-- Build time: 15-60 minutes (one-time, no table lock during build).
-- ============================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('PiXL.Parsed')
      AND name = 'NCCI_Parsed_Dashboard'
)
BEGIN
    PRINT 'Creating NCCI_Parsed_Dashboard...';

    CREATE NONCLUSTERED COLUMNSTORE INDEX [NCCI_Parsed_Dashboard]
    ON [PiXL].[Parsed] (
        -- Identity / Keys
        SourceId,
        CompanyID,
        PiXLID,

        -- Timestamps
        ReceivedAt,
        ParsedAt,

        -- Network
        IPAddress,

        -- Device / Display
        ScreenWidth,
        ScreenHeight,
        Platform,
        MaxTouchPoints,
        ClientUserAgent,
        GPURenderer,
        CanvasFingerprint,
        Timezone,

        -- Classification
        IsSynthetic,
        BotScore,
        CombinedThreatScore,
        AnomalyScore,

        -- Evasion detection
        DoNotTrack,
        WebDriverDetected,
        CanvasEvasionDetected,
        WebGLEvasionDetected,
        AudioNoiseInjectionDetected,
        EvasionToolsDetected,
        ProxyBlockedProperties,
        StealthPluginSignals,
        FontMethodMismatch,
        EvasionSignalsV2,

        -- Behavioral signals
        MouseMoveCount,
        UserScrolled,
        ScrollDepthPx,
        MouseEntropy,
        ScrollContradiction,
        MoveTimingCV,
        MoveSpeedCV,
        BehavioralFlags,

        -- Bot details
        BotSignalsList,

        -- Content / Domain
        PageDomain,
        CrossSignalFlags,

        -- Server-side enrichments
        Srv_HitsIn15s,
        Srv_LastGapMs,
        Srv_SubSecDupe,
        Srv_SubnetAlert,
        Srv_RapidFire
    )
    WITH (
        MAXDOP = 4            -- Limit parallelism to avoid starving other queries
    );

    PRINT 'NCCI_Parsed_Dashboard created successfully.';
END
ELSE
BEGIN
    PRINT 'NCCI_Parsed_Dashboard already exists — skipping.';
END
GO
