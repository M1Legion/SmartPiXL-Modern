-- ============================================================================
-- 77: Nonclustered index on (HitType, ReceivedAt) for BrilliantPiXL dashboard
--
-- PROBLEM:  Dashboard queries filter WHERE HitType = 'modern' but only ~80 of
--           28M rows in the last 7 days are 'modern'. Without this index the
--           optimizer scans the entire clustered range (hundreds of GB).
--
-- FIX:      Narrow NC index lets the optimizer SEEK directly to 'modern' rows,
--           then do key lookups for the ~80 rows that match. Queries drop from
--           minutes of full-table scans to milliseconds.
--
-- CREATED:  2026-04-07
-- ============================================================================

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_Parsed_HitType_ReceivedAt'
      AND object_id = OBJECT_ID('PiXL.Parsed')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_Parsed_HitType_ReceivedAt
    ON PiXL.Parsed (HitType, ReceivedAt DESC)
    WITH (ONLINE = ON, MAXDOP = 4);
END;
GO
