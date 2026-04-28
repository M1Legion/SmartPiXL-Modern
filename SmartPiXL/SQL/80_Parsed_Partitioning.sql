-- ============================================================================
-- 80_Parsed_Partitioning.sql
-- ============================================================================
-- Partitions PiXL.Parsed monthly by ReceivedAt, rebuilds indexes onto the
-- partition scheme, drops redundant indexes, enables
-- OPTIMIZE_FOR_SEQUENTIAL_KEY, and sets PAGE compression.
--
-- All objects land on the [SmartPiXL] filegroup. PRIMARY in this database
-- is intentionally small (10 GB) — SmartPiXL data must never live there.
--
-- READ THIS BEFORE RUNNING
-- ----------------------------------------------------------------------------
-- This script rewrites the entire PiXL.Parsed table and all of its indexes.
-- At ~381M rows / ~1.42 TB it will take hours and needs roughly 2× the
-- current table size free on the SmartPiXL filegroup.
--
-- Required steps in order:
--   1. Stop-Service SmartPiXL-Forge            (no writers during rebuild)
--   2. Stop-Service SmartPiXL-Sentinel         (no dashboard readers)
--   3. Verify > 3 TB free on the SmartPiXL filegroup.
--   4. Take a full backup. Really.
--   5. Run this script in SSMS with SQLCMD Mode ON (or via sqlcmd).
--      Use a dedicated connection; do not multiplex with other work.
--   6. Start-Service SmartPiXL-Forge, then SmartPiXL-Sentinel.
--
-- Idempotency
-- ----------------------------------------------------------------------------
-- The script is safe to re-run after a partial failure. It drops every
-- affected object if present, validates the filegroup, then rebuilds from
-- scratch. The long step (CIX rewrite) will run again in full — there is
-- no way around that with partition alignment.
--
-- Design notes
-- ----------------------------------------------------------------------------
-- * Partition function: RANGE RIGHT, DATETIME2(7), monthly boundaries from
--   2024-01-01 through (current month + 3 future months). Empty past
--   partitions cost nothing and protect against historical backfills.
-- * Partition scheme: ALL TO ([SmartPiXL]). Month-specific filegroups
--   (for cheaper archival storage) can be added later by rebuilding
--   individual partitions.
-- * All indexes are aligned on PS_PiXL_Parsed_Monthly(ReceivedAt) EXCEPT
--   UQ_PiXL_Parsed_SourceId, which is deliberately non-aligned because it
--   is keyed on SourceId alone (required for O(1) watermark lookups).
--   Non-aligned NCIs must be dropped before doing a partition SWITCH for
--   archival — the maintenance proc in 81_*.sql handles that.
-- * Direction: all date keys are ASC. SQL Server does backward scans for
--   `ORDER BY ReceivedAt DESC` free of charge; DESC in the index definition
--   only helps for compound sorts like `ORDER BY a ASC, b DESC`, which no
--   query on PiXL.Parsed uses.
-- ============================================================================

SET XACT_ABORT ON;
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
GO

USE SmartPiXL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.filegroups WHERE name = N'SmartPiXL')
BEGIN
    RAISERROR('Filegroup [SmartPiXL] does not exist in this database. Aborting.', 16, 1);
    RETURN;
END
GO

PRINT CONCAT('[', SYSUTCDATETIME(), '] Starting partition migration of PiXL.Parsed onto [SmartPiXL]');
GO

-- ============================================================================
-- PHASE 1 — Drop every index on PiXL.Parsed EXCEPT the clustered index.
-- ----------------------------------------------------------------------------
-- We cannot drop/recreate the partition scheme while any index references it.
-- We cannot rebuild the clustered index on the partition scheme while a
-- misaligned columnstore index exists on the same table. Easiest path: wipe
-- every non-clustered index first, then rebuild them all at the end.
-- ============================================================================

-- PK_PiXL_Parsed (constraint form — must go through ALTER TABLE DROP CONSTRAINT)
IF EXISTS (
    SELECT 1 FROM sys.key_constraints
    WHERE parent_object_id = OBJECT_ID('PiXL.Parsed') AND name = 'PK_PiXL_Parsed')
BEGIN
    ALTER TABLE PiXL.Parsed DROP CONSTRAINT PK_PiXL_Parsed;
    PRINT CONCAT('[', SYSUTCDATETIME(), '] Dropped constraint PK_PiXL_Parsed');
END
GO

-- Every non-clustered index on the table (constraints already handled above).
DECLARE @dropSql NVARCHAR(MAX) = N'';
SELECT @dropSql = @dropSql +
    N'DROP INDEX ' + QUOTENAME(i.name) + N' ON PiXL.Parsed;' + CHAR(13) + CHAR(10)
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('PiXL.Parsed')
  AND i.type_desc <> 'CLUSTERED'
  AND i.is_hypothetical = 0
  AND i.name IS NOT NULL;

IF LEN(@dropSql) > 0
BEGIN
    PRINT CONCAT('[', SYSUTCDATETIME(), '] Dropping non-clustered indexes:');
    PRINT @dropSql;
    EXEC sp_executesql @dropSql;
END
ELSE
BEGIN
    PRINT CONCAT('[', SYSUTCDATETIME(), '] No non-clustered indexes to drop');
END
GO

-- ============================================================================
-- PHASE 2 — Drop existing partition scheme + function (if present).
-- ----------------------------------------------------------------------------
-- Nothing should reference them now that we've wiped non-clustered indexes
-- and the CIX is still on its original (non-partitioned) filegroup.
-- ============================================================================

IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = 'PS_PiXL_Parsed_Monthly')
BEGIN
    DROP PARTITION SCHEME PS_PiXL_Parsed_Monthly;
    PRINT CONCAT('[', SYSUTCDATETIME(), '] Dropped existing PS_PiXL_Parsed_Monthly');
END
GO

IF EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = 'PF_PiXL_Parsed_Monthly')
BEGIN
    DROP PARTITION FUNCTION PF_PiXL_Parsed_Monthly;
    PRINT CONCAT('[', SYSUTCDATETIME(), '] Dropped existing PF_PiXL_Parsed_Monthly');
END
GO

-- ============================================================================
-- PHASE 3 — Create the partition function and scheme on [SmartPiXL].
-- ----------------------------------------------------------------------------
-- RANGE RIGHT: each boundary value belongs to the partition on its right side.
-- Boundary '2026-04-01' => rows with ReceivedAt >= '2026-04-01' land in the
-- partition labelled "April 2026". This is the canonical shape for monthly
-- sliding-window partitioning; it makes SPLIT/MERGE operations predictable.
-- ============================================================================

DECLARE @start DATE = '2024-01-01';
DECLARE @end   DATE = DATEADD(MONTH, 4, DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1));

DECLARE @boundaries NVARCHAR(MAX) = N'';
DECLARE @d DATE = @start;
WHILE @d < @end
BEGIN
    IF LEN(@boundaries) > 0 SET @boundaries += N',';
    SET @boundaries += N'''' + CONVERT(CHAR(10), @d, 23) + N'T00:00:00.0000000''';
    SET @d = DATEADD(MONTH, 1, @d);
END

DECLARE @sql NVARCHAR(MAX) =
    N'CREATE PARTITION FUNCTION PF_PiXL_Parsed_Monthly (DATETIME2(7)) ' +
    N'AS RANGE RIGHT FOR VALUES (' + @boundaries + N');';
EXEC sp_executesql @sql;

PRINT CONCAT('[', SYSUTCDATETIME(), '] Created PF_PiXL_Parsed_Monthly with boundaries 2024-01 .. ',
             CONVERT(CHAR(7), DATEADD(MONTH, -1, @end), 23));
GO

CREATE PARTITION SCHEME PS_PiXL_Parsed_Monthly
AS PARTITION PF_PiXL_Parsed_Monthly
ALL TO ([SmartPiXL]);
GO

PRINT CONCAT('[', SYSUTCDATETIME(), '] Created PS_PiXL_Parsed_Monthly ALL TO [SmartPiXL]');
GO

-- ============================================================================
-- PHASE 4 — Rebuild the clustered index onto the partition scheme.
-- ----------------------------------------------------------------------------
-- DROP_EXISTING = ON performs this as a single offline rewrite of the entire
-- table. This is the long step (hours at 381M rows).
-- ============================================================================

PRINT CONCAT('[', SYSUTCDATETIME(), '] Rebuilding CIX_PiXL_Parsed_ReceivedAt onto PS (long step)');
GO

CREATE UNIQUE CLUSTERED INDEX CIX_PiXL_Parsed_ReceivedAt
    ON PiXL.Parsed (ReceivedAt ASC, SourceId ASC)
    WITH (
        DROP_EXISTING = ON,
        ONLINE = OFF,
        SORT_IN_TEMPDB = ON,
        MAXDOP = 4,
        DATA_COMPRESSION = PAGE,
        OPTIMIZE_FOR_SEQUENTIAL_KEY = ON
    )
    ON PS_PiXL_Parsed_Monthly (ReceivedAt);
GO

-- Verify the CIX actually landed on PS before we continue — otherwise the
-- remaining index builds will either fail (columnstore alignment) or silently
-- land on the wrong filegroup.
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
    WHERE i.object_id = OBJECT_ID('PiXL.Parsed')
      AND i.name = 'CIX_PiXL_Parsed_ReceivedAt'
      AND ds.name = 'PS_PiXL_Parsed_Monthly')
BEGIN
    RAISERROR('CIX_PiXL_Parsed_ReceivedAt is NOT on PS_PiXL_Parsed_Monthly. Aborting before NCI builds.', 16, 1);
    RETURN;
END

PRINT CONCAT('[', SYSUTCDATETIME(), '] CIX rebuilt and partitioned');
GO

-- ============================================================================
-- PHASE 5 — Non-clustered rowstore indexes, aligned on the partition scheme.
-- ============================================================================

-- IX_Parsed_Company_ReceivedAt — keep the historical key definition. Candidate
-- for deletion after 24h of usage stats if it isn't used.
PRINT CONCAT('[', SYSUTCDATETIME(), '] Building IX_Parsed_Company_ReceivedAt');
CREATE NONCLUSTERED INDEX IX_Parsed_Company_ReceivedAt
    ON PiXL.Parsed (
        PiXLID ASC,
        IPAddress ASC,
        BotScore ASC,
        AnomalyScore ASC,
        MouseMoveCount ASC,
        UserScrolled ASC,
        CompanyID ASC,
        ReceivedAt ASC
    )
    WITH (
        ONLINE = OFF,
        SORT_IN_TEMPDB = ON,
        MAXDOP = 4,
        DATA_COMPRESSION = PAGE,
        OPTIMIZE_FOR_SEQUENTIAL_KEY = ON
    )
    ON PS_PiXL_Parsed_Monthly (ReceivedAt);
GO

-- IX_Parsed_Dashboard — the real dashboard index. Key is ReceivedAt DESC
-- because every vw_Dash_* sorts by ReceivedAt DESC.
PRINT CONCAT('[', SYSUTCDATETIME(), '] Building IX_Parsed_Dashboard');
CREATE NONCLUSTERED INDEX IX_Parsed_Dashboard
    ON PiXL.Parsed (ReceivedAt DESC)
    INCLUDE (
        SourceId, IPAddress, SessionId, BotScore, KnownBot, LeadQualityScore,
        BotName, ParsedBrowser, ParsedOS, ParsedOSVersion, ParsedDeviceType,
        PagePath, PageReferrer, SessionDurationSec, MaxMindRegion, MaxMindCity,
        ScreenWidth, ScreenHeight, CanvasFingerprint, WebGLFingerprint,
        AudioFingerprintHash, GPURenderer, HardwareConcurrency, DeviceMemoryGB,
        ConnectionType, MouseMoveCount, UA_Architecture, CanvasEvasionDetected,
        WebGLEvasionDetected, WebDriverDetected, EvasionSignalsV2,
        EvasionToolsDetected
    )
    WITH (
        ONLINE = OFF,
        SORT_IN_TEMPDB = ON,
        MAXDOP = 4,
        DATA_COMPRESSION = PAGE,
        OPTIMIZE_FOR_SEQUENTIAL_KEY = ON
    )
    ON PS_PiXL_Parsed_Monthly (ReceivedAt);
GO

-- ============================================================================
-- PHASE 6 — Rebuild the nonclustered columnstore, aligned on the partition
-- scheme. Extended coverage (HitType, RequestPath, parsed UA, page, geo)
-- eliminates rowstore scans from Sentinel dashboards.
-- ============================================================================

PRINT CONCAT('[', SYSUTCDATETIME(), '] Building NCCI_Parsed_Dashboard');
CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Parsed_Dashboard
    ON PiXL.Parsed (
        -- identity + time
        SourceId, CompanyID, PiXLID, ReceivedAt, ParsedAt,
        -- NEW: hit classification (was missing — caused rowstore scans)
        HitType, RequestPath,
        -- client identity
        IPAddress, SessionId,
        -- device
        ScreenWidth, ScreenHeight, Platform, MaxTouchPoints, ClientUserAgent,
        GPURenderer, CanvasFingerprint, Timezone,
        -- parsed UA (NEW)
        ParsedBrowser, ParsedOS, ParsedOSVersion, ParsedDeviceType,
        -- page (NEW)
        PagePath, PageDomain,
        -- geo (NEW)
        MaxMindRegion, MaxMindCity,
        -- scoring
        IsSynthetic, BotScore, CombinedThreatScore, AnomalyScore,
        LeadQualityScore, KnownBot, BotName,
        -- evasion
        DoNotTrack, WebDriverDetected, CanvasEvasionDetected,
        WebGLEvasionDetected, AudioNoiseInjectionDetected, EvasionToolsDetected,
        ProxyBlockedProperties, StealthPluginSignals, FontMethodMismatch,
        EvasionSignalsV2,
        -- behavioral
        MouseMoveCount, UserScrolled, ScrollDepthPx, MouseEntropy,
        ScrollContradiction, MoveTimingCV, MoveSpeedCV, BehavioralFlags,
        BotSignalsList, CrossSignalFlags,
        -- server-side rapid-fire flags
        Srv_HitsIn15s, Srv_LastGapMs, Srv_SubSecDupe, Srv_SubnetAlert, Srv_RapidFire
    )
    WITH (
        MAXDOP = 4,
        DATA_COMPRESSION = COLUMNSTORE
    )
    ON PS_PiXL_Parsed_Monthly (ReceivedAt);
GO

PRINT CONCAT('[', SYSUTCDATETIME(), '] NCCI built');
GO

-- ============================================================================
-- PHASE 7 — Narrow unique index on SourceId, NON-ALIGNED on [SmartPiXL].
-- ----------------------------------------------------------------------------
-- Deliberately non-aligned because its sole purpose is the monotonic
-- watermark query `TOP 1 SourceId ... ORDER BY SourceId DESC`, which must
-- not scan every partition. FILLFACTOR 100 because inserts are append-only.
-- Partition SWITCH for archival requires this index to be dropped first —
-- the maintenance proc in 81_*.sql handles that.
-- ============================================================================

PRINT CONCAT('[', SYSUTCDATETIME(), '] Building UQ_PiXL_Parsed_SourceId on [SmartPiXL]');
CREATE UNIQUE NONCLUSTERED INDEX UQ_PiXL_Parsed_SourceId
    ON PiXL.Parsed (SourceId ASC)
    WITH (
        ONLINE = OFF,
        SORT_IN_TEMPDB = ON,
        MAXDOP = 4,
        DATA_COMPRESSION = PAGE,
        OPTIMIZE_FOR_SEQUENTIAL_KEY = ON,
        FILLFACTOR = 100
    )
    ON [SmartPiXL];
GO

-- ============================================================================
-- PHASE 8 — Statistics + verification
-- ============================================================================

PRINT CONCAT('[', SYSUTCDATETIME(), '] Updating statistics FULLSCAN');
UPDATE STATISTICS PiXL.Parsed WITH FULLSCAN;
GO

PRINT CONCAT('[', SYSUTCDATETIME(), '] Migration complete. Final index layout:');
SELECT
    i.index_id                         AS IndexId,
    i.name                             AS IndexName,
    i.type_desc                        AS IndexType,
    ds.name                            AS Storage,
    i.is_unique                        AS IsUnique,
    MIN(p.data_compression_desc)       AS Compression,
    i.optimize_for_sequential_key      AS SeqKey,
    COUNT(DISTINCT p.partition_number) AS Partitions
FROM sys.indexes i
JOIN sys.partitions p   ON p.object_id = i.object_id AND p.index_id = i.index_id
JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
WHERE i.object_id = OBJECT_ID('PiXL.Parsed')
GROUP BY i.index_id, i.name, i.type_desc, ds.name, i.is_unique, i.optimize_for_sequential_key
ORDER BY i.index_id;
GO

PRINT CONCAT('[', SYSUTCDATETIME(), '] Done. Run 81_Parsed_PartitionMaintenance.sql next to install the monthly maintenance job.');
GO
