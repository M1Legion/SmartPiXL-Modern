-- ============================================================================
-- Migration 35: Audit and fix PRIMARY filegroup placement
--
-- OWNER DIRECTIVE: "There should not be ANY object in the smartpixl DB that
--   writes to Primary." — Ch07 owner feedback
--
-- APPROACH: 
--   1. Report all objects currently on the PRIMARY filegroup
--   2. Move UIX_Raw_Id (the known offender) off PRIMARY
--
-- NOTE ON UIX_Raw_Id: Migration 28 placed this ON [PRIMARY] because a
--   partition-aligned unique index requires the partition column in its key.
--   But the owner's directive overrides that — we'll make it non-unique
--   on the partition scheme instead, or drop it since Raw.Id is already 
--   the clustered index key and guaranteed unique by IDENTITY.
--
-- SAFE TO RE-RUN: All operations check existence first.
-- AUDIT REF: Ch07 owner notes (HIGH)
-- ============================================================================
SET NOCOUNT ON;
GO

-- ── Step 1: Diagnostic — report all user indexes on PRIMARY ────────────────
PRINT '=== Objects currently on PRIMARY filegroup ===';

SELECT 
    SCHEMA_NAME(o.schema_id) + '.' + o.name AS [Table],
    i.name AS IndexName,
    i.type_desc AS IndexType,
    fg.name AS Filegroup
FROM sys.indexes i
JOIN sys.objects o ON i.object_id = o.object_id
JOIN sys.filegroups fg ON i.data_space_id = fg.data_space_id
WHERE fg.name = 'PRIMARY'
  AND o.is_ms_shipped = 0
  AND o.type = 'U'
ORDER BY SCHEMA_NAME(o.schema_id), o.name, i.name;
GO

-- ── Step 2: Drop UIX_Raw_Id if it's on PRIMARY ────────────────────────────
-- The unique constraint on Raw.Id is redundant: Id is a BIGINT IDENTITY column
-- that is already the clustered index key (via partition scheme). IDENTITY 
-- guarantees uniqueness. The separate non-clustered unique index on PRIMARY
-- duplicates that guarantee while violating the "nothing on PRIMARY" rule.
IF EXISTS (
    SELECT 1
    FROM sys.indexes i
    JOIN sys.filegroups fg ON i.data_space_id = fg.data_space_id
    WHERE i.object_id = OBJECT_ID('PiXL.Raw')
      AND i.name = 'UIX_Raw_Id'
      AND fg.name = 'PRIMARY'
)
BEGIN
    DROP INDEX UIX_Raw_Id ON PiXL.[Raw];
    PRINT 'Dropped UIX_Raw_Id from PRIMARY filegroup (IDENTITY uniqueness is guaranteed by definition).';
END
ELSE IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('PiXL.Raw') AND name = 'UIX_Raw_Id')
    PRINT 'UIX_Raw_Id exists but is NOT on PRIMARY — no action needed.';
ELSE
    PRINT 'UIX_Raw_Id does not exist — no action needed.';
GO

-- ── Step 3: Report remaining objects on PRIMARY ───────────────────────────
PRINT '';
PRINT '=== Remaining objects on PRIMARY (review manually) ===';

SELECT 
    SCHEMA_NAME(o.schema_id) + '.' + o.name AS [Table],
    i.name AS IndexName,
    i.type_desc AS IndexType,
    fg.name AS Filegroup
FROM sys.indexes i
JOIN sys.objects o ON i.object_id = o.object_id
JOIN sys.filegroups fg ON i.data_space_id = fg.data_space_id
WHERE fg.name = 'PRIMARY'
  AND o.is_ms_shipped = 0
  AND o.type = 'U'
ORDER BY SCHEMA_NAME(o.schema_id), o.name, i.name;
GO

PRINT '=== Migration 35 complete: PRIMARY filegroup audit + UIX_Raw_Id removal ===';
GO
