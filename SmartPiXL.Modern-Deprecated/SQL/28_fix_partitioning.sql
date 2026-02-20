SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
GO

USE SmartPiXL;
GO

-- ============================================================
-- Step 1: Drop the clustered index on PiXL.Parsed
-- ============================================================
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('PiXL.Parsed') AND name = 'CIX_PiXL_Parsed_ReceivedAt')
BEGIN
    DROP INDEX CIX_PiXL_Parsed_ReceivedAt ON PiXL.Parsed;
    PRINT 'Dropped CIX_PiXL_Parsed_ReceivedAt.';
END
GO

-- ============================================================
-- Step 2: Make SourceId NOT NULL
-- ============================================================
ALTER TABLE PiXL.Parsed ALTER COLUMN SourceId BIGINT NOT NULL;
PRINT 'Made PiXL.Parsed.SourceId BIGINT NOT NULL.';
GO

-- ============================================================
-- Step 3: Recreate clustered index on Parsed
-- ============================================================
CREATE UNIQUE CLUSTERED INDEX CIX_PiXL_Parsed_ReceivedAt
    ON PiXL.Parsed (ReceivedAt, SourceId);
PRINT 'Recreated CIX_PiXL_Parsed_ReceivedAt.';
GO

-- ============================================================
-- Step 4: Recreate PK on Parsed
-- ============================================================
ALTER TABLE PiXL.Parsed
    ADD CONSTRAINT PK_PiXL_Parsed PRIMARY KEY NONCLUSTERED (SourceId);
PRINT 'Recreated PK_PiXL_Parsed.';
GO

-- ============================================================
-- Step 5: Create partitioned indexes on PiXL.Raw
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('PiXL.Raw') AND name = 'CIX_Raw_ReceivedAt')
BEGIN
    CREATE CLUSTERED INDEX CIX_Raw_ReceivedAt
        ON PiXL.[Raw] (ReceivedAt, Id)
        ON ps_Raw_Monthly (ReceivedAt);
    PRINT 'Created partitioned CIX_Raw_ReceivedAt.';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('PiXL.Raw') AND name = 'UIX_Raw_Id')
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UIX_Raw_Id
        ON PiXL.[Raw] (Id)
        ON ps_Raw_Monthly (ReceivedAt);
    PRINT 'Created UIX_Raw_Id.';
END
GO

-- ============================================================
-- Step 6: Re-add FK
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_PiXL_Parsed_Source')
BEGIN
    ALTER TABLE PiXL.Parsed
        ADD CONSTRAINT FK_PiXL_Parsed_Source
        FOREIGN KEY (SourceId) REFERENCES PiXL.[Raw] (Id);
    PRINT 'Re-added FK_PiXL_Parsed_Source.';
END
GO

-- ============================================================
-- Step 7: Reseed and compress
-- ============================================================
DECLARE @maxId BIGINT = (SELECT ISNULL(MAX(Id), 0) FROM PiXL.[Raw]);
DBCC CHECKIDENT ('PiXL.Raw', RESEED, @maxId);
PRINT 'Reseeded to ' + CAST(@maxId AS VARCHAR(20));
GO

EXEC ETL.usp_ManageRawCompression;
GO

-- ============================================================
-- VALIDATION
-- ============================================================
PRINT '';
PRINT '=== FINAL VALIDATION ===';

SELECT 'PiXL.Raw' AS T, COUNT(*) AS Partitions, SUM(p.rows) AS Rows
FROM sys.partitions p WHERE p.object_id = OBJECT_ID('PiXL.Raw') AND p.index_id = 1;

SELECT 'Raw.Id' AS Col, TYPE_NAME(c.system_type_id) AS DataType, c.is_nullable
FROM sys.columns c WHERE c.object_id = OBJECT_ID('PiXL.Raw') AND c.name = 'Id';

SELECT 'Parsed.SourceId' AS Col, TYPE_NAME(c.system_type_id) AS DataType, c.is_nullable
FROM sys.columns c WHERE c.object_id = OBJECT_ID('PiXL.Parsed') AND c.name = 'SourceId';

SELECT i.name, i.type_desc, i.is_primary_key FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('PiXL.Raw') AND i.type > 0;

SELECT i.name, i.type_desc, i.is_primary_key FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('PiXL.Parsed') AND i.is_primary_key = 1;

SELECT f.name AS FK FROM sys.foreign_keys f
WHERE f.referenced_object_id = OBJECT_ID('PiXL.Raw');
GO
