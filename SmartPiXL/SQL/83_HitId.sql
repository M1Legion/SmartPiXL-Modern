-- ════════════════════════════════════════════════════════════════════════════
-- 83_HitId.sql
--
-- Phase 0 of the Go-Live plan (see docs/GO-LIVE-PLAN.md).
--
-- Adds a client-generated HitId (UUIDv4) to PiXL.Parsed. Every execution of
-- the PiXL JS tag produces one HitId and stamps it on every beacon that
-- execution fires (main + geo followup + future delta beacons). Forge
-- stitches those beacons into a single row by matching on this column
-- before SqlBulkCopy flushes the row.
--
-- NOT to be confused with SessionId — SessionId is a multi-page visit.
-- HitId is exactly one tag firing on one page view.
--
-- Idempotent: re-runnable without error.
-- ════════════════════════════════════════════════════════════════════════════

SET XACT_ABORT ON;
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
GO

USE SmartPiXL;
GO

PRINT CONCAT('[', SYSUTCDATETIME(), '] Adding HitId column to PiXL.Parsed');
GO

IF COL_LENGTH('PiXL.Parsed', 'HitId') IS NULL
BEGIN
    ALTER TABLE PiXL.Parsed ADD HitId UNIQUEIDENTIFIER NULL;
    PRINT '  Added PiXL.Parsed.HitId (UNIQUEIDENTIFIER NULL)';
END
ELSE
    PRINT '  Skipped: PiXL.Parsed.HitId already exists';
GO

-- Filtered index: most legacy rows have NULL HitId (pre-Phase-0). Only index
-- the populated tail. Post-migration this index is the primary lookup path
-- for stitch-audit queries and delta-beacon upserts.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('PiXL.Parsed')
      AND name = 'IX_Parsed_HitId'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_Parsed_HitId
        ON PiXL.Parsed(HitId)
        WHERE HitId IS NOT NULL;
    PRINT '  Created index IX_Parsed_HitId (filtered)';
END
ELSE
    PRINT '  Skipped: index IX_Parsed_HitId already exists';
GO

PRINT CONCAT('[', SYSUTCDATETIME(), '] Done: 83_HitId.sql');
GO
