/*
    22_AutoConsumerEmailIndex.sql
    ==============================
    Creates a filtered nonclustered index on AutoUpdate.dbo.AutoConsumer.EMail
    to enable efficient email-based identity resolution in ETL.usp_MatchVisits.

    The AutoConsumer table has ~470M rows but only ~69.5M have an email address.
    The filtered index excludes 84% of rows, keeping the index compact.

    IMPORTANT:
      - This touches a table we don't own (AutoUpdate database)
      - Building the index on 69.5M rows takes ~10-30 minutes
      - Uses ONLINE = ON to avoid blocking concurrent reads/writes
      - Uses SORT_IN_TEMPDB = ON to reduce I/O on the AutoUpdate data files
      - Uses MAXDOP = 4 to avoid starving other workloads

    PREREQUISITES:
      - AutoUpdate database must exist on the same SQL Server instance
      - Sufficient tempdb space (~15 GB for sort operations)
      - Run during low-traffic period if possible

    Run on: localhost\SQL2025
    Date:   2026-02-15
*/

USE AutoUpdate;
GO

SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

-- Sanity check: verify EMail column coverage before building the index
DECLARE @TotalRows BIGINT, @EmailRows BIGINT;
SELECT @TotalRows = COUNT_BIG(*) FROM dbo.AutoConsumer WITH (NOLOCK);
SELECT @EmailRows = COUNT_BIG(*) FROM dbo.AutoConsumer WITH (NOLOCK) WHERE EMail IS NOT NULL;

PRINT '=== AutoConsumer EMail Index ===';
PRINT '  Total rows:      ' + CAST(@TotalRows AS VARCHAR(20));
PRINT '  Rows with EMail: ' + CAST(@EmailRows AS VARCHAR(20));
PRINT '  Coverage:        ' + CAST(CAST(@EmailRows * 100.0 / NULLIF(@TotalRows, 0) AS DECIMAL(5,1)) AS VARCHAR(10)) + '%';
PRINT '';

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.AutoConsumer')
      AND name = 'IX_AutoConsumer_EMail'
)
BEGIN
    PRINT '  IX_AutoConsumer_EMail already exists — skipped';
END
ELSE
BEGIN
    PRINT '  Creating IX_AutoConsumer_EMail (filtered, ONLINE)...';
    PRINT '  This may take 10-30 minutes depending on I/O throughput.';

    CREATE NONCLUSTERED INDEX IX_AutoConsumer_EMail
        ON dbo.AutoConsumer (EMail)
        INCLUDE (IndividualKey, AddressKey)
        WHERE EMail IS NOT NULL
        WITH (ONLINE = ON, SORT_IN_TEMPDB = ON, MAXDOP = 4);

    PRINT '  IX_AutoConsumer_EMail created successfully.';
END
GO

-- Verify
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.AutoConsumer')
      AND name = 'IX_AutoConsumer_EMail'
)
    PRINT '  OK: IX_AutoConsumer_EMail exists';
ELSE
    PRINT '  ERROR: IX_AutoConsumer_EMail not found!';
GO

-- Switch back to SmartPiXL
USE SmartPiXL;
GO
