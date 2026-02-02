# SmartPiXL MSSQL Architecture Review

**Review Date:** 2026-02-02  
**Reviewer:** MSSQL Specialist

## Executive Summary

This project implements a high-throughput tracking pixel system with an excellent foundation for SQL Server data handling. The architecture follows best practices for write-heavy workloads:

✅ **What's Done Right:**
- SqlBulkCopy for batch inserts (optimal for high-throughput)
- Raw data storage with deferred parsing (fast inserts, flexible queries)
- Async database operations (non-blocking I/O)
- Graceful shutdown with queue draining
- View-based query string parsing (90+ fields extracted at query time)
- Materialization pattern for indexed queries

⚠️ **Areas for Improvement:**
- Missing composite covering indexes for common query patterns
- No data lifecycle/archival strategy
- Scalar function in view can be slow at scale
- Missing UNIQUE constraint on SourceId in PiXL_Materialized
- No connection string pooling options configured

---

## Detailed Findings

### 1. Schema Design

#### Current Pattern (Raw + Parsed)

```
[PiXL_Test] ──parse──▶ [vw_PiXL_Parsed] ──materialize──▶ [PiXL_Materialized]
   (raw)                   (view)                          (indexed)
```

**Assessment:** This is the correct pattern for write-heavy tracking workloads. Fast inserts to raw table, flexible querying via view, indexed access via materialized table.

#### Recommendations

**a) Add UNIQUE constraint to prevent duplicate materialization:**

```sql
-- In PiXL_Materialized table
CREATE UNIQUE INDEX UX_PiXL_Materialized_SourceId 
ON dbo.PiXL_Materialized(SourceId);
```

**b) Consider IDENTITY column type change for scale:**

The current `PiXL_Test.Id` is `INT` (max ~2.1 billion). For high-volume tracking, consider `BIGINT`:

```sql
-- For new installs (cannot ALTER existing IDENTITY)
Id BIGINT IDENTITY(1,1) PRIMARY KEY
```

---

### 2. Index Strategy

#### Current Indexes (Good)
- `IX_PiXL_Test_ReceivedAt` - Supports time-range queries
- `IX_PiXL_Test_CompanyPixl` - Supports company/pixel filtering
- `IX_PiXL_Materialized_CanvasFP` - Supports fingerprint matching
- `IX_PiXL_Materialized_Domain` - Supports domain filtering

#### Missing Indexes (Add These)

**a) Composite covering index for fingerprint matching with context:**

```sql
-- Find visitors by fingerprint with their details
CREATE INDEX IX_PiXL_Materialized_Fingerprints 
ON dbo.PiXL_Materialized (CanvasFingerprint, WebGLFingerprint)
INCLUDE (IPAddress, ReceivedAt, Domain, CompanyID, PiXLID);
```

**b) IP-based queries (for geographic analysis):**

```sql
CREATE INDEX IX_PiXL_Materialized_IP 
ON dbo.PiXL_Materialized (IPAddress)
INCLUDE (ReceivedAt, Domain, CanvasFingerprint);
```

**c) Time-range + Company queries (dashboard/reporting):**

```sql
CREATE INDEX IX_PiXL_Materialized_Company_Time 
ON dbo.PiXL_Materialized (CompanyID, ReceivedAt DESC)
INCLUDE (PiXLID, Domain, CanvasFingerprint);
```

---

### 3. GetQueryParam Function Performance

#### Current Implementation

The scalar UDF `GetQueryParam` is called **100+ times per row** in `vw_PiXL_Parsed`. Scalar UDFs in SQL Server:
- Prevent parallelism
- Execute row-by-row
- Cannot be inlined (prior to SQL 2019 scalar UDF inlining)

#### Impact

| Row Count | View Query Time (est.) |
|-----------|------------------------|
| 1,000     | ~1 second              |
| 100,000   | ~30-60 seconds         |
| 1,000,000 | ~5-10 minutes          |

#### Recommendations

**Option 1: Use Inline Table-Valued Function (Best for SQL 2016+)**

```sql
CREATE FUNCTION dbo.GetQueryParamInline(@QueryString NVARCHAR(MAX), @ParamName NVARCHAR(100))
RETURNS TABLE
AS
RETURN (
    SELECT 
        REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
            SUBSTRING(
                @QueryString,
                CASE 
                    WHEN CHARINDEX(@ParamName + '=', @QueryString) = 1 
                        THEN LEN(@ParamName) + 2
                    WHEN CHARINDEX('&' + @ParamName + '=', @QueryString) > 0 
                        THEN CHARINDEX('&' + @ParamName + '=', @QueryString) + LEN(@ParamName) + 2
                    ELSE 0
                END,
                CASE 
                    WHEN CHARINDEX('&', @QueryString, 
                        CASE 
                            WHEN CHARINDEX(@ParamName + '=', @QueryString) = 1 
                                THEN LEN(@ParamName) + 2
                            WHEN CHARINDEX('&' + @ParamName + '=', @QueryString) > 0 
                                THEN CHARINDEX('&' + @ParamName + '=', @QueryString) + LEN(@ParamName) + 2
                            ELSE 1
                        END) > 0
                    THEN CHARINDEX('&', @QueryString, 
                        CASE 
                            WHEN CHARINDEX(@ParamName + '=', @QueryString) = 1 
                                THEN LEN(@ParamName) + 2
                            WHEN CHARINDEX('&' + @ParamName + '=', @QueryString) > 0 
                                THEN CHARINDEX('&' + @ParamName + '=', @QueryString) + LEN(@ParamName) + 2
                            ELSE 1
                        END) - 
                        CASE 
                            WHEN CHARINDEX(@ParamName + '=', @QueryString) = 1 
                                THEN LEN(@ParamName) + 2
                            WHEN CHARINDEX('&' + @ParamName + '=', @QueryString) > 0 
                                THEN CHARINDEX('&' + @ParamName + '=', @QueryString) + LEN(@ParamName) + 2
                            ELSE 1
                        END
                    ELSE LEN(@QueryString)
                END
            ),
            '%20', ' '), '%2F', '/'), '%3A', ':'), '%2C', ','), 
            '%3D', '='), '%26', '&'), '%3F', '?'), '%23', '#'), '%25', '%'), '+', ' '
        ) AS Value
);
```

**Option 2: Parse in C# before insert (best performance)**

Pre-parse high-value fields in C# and store directly in columns. This trades insert speed for query speed.

**Option 3: Use JSON_VALUE (SQL 2016+)**

Store query string as JSON, use native JSON_VALUE function which is optimized:

```sql
-- In C#, convert query string to JSON: {"sw":"1920","sh":"1080",...}
SELECT JSON_VALUE(QueryStringJson, '$.sw') AS ScreenWidth
```

---

### 4. Data Lifecycle Management

#### Missing: No archival/purge strategy

Tracking data grows fast. Without lifecycle management:
- Storage costs increase linearly
- Query performance degrades
- Backup/restore times grow

#### Recommended: Add maintenance procedures

**a) Archive old data to separate table:**

```sql
CREATE TABLE dbo.PiXL_Archive (
    -- Same schema as PiXL_Materialized
    ArchivedAt DATETIME2 DEFAULT GETUTCDATE()
) ON [SmartPixl]; -- Or separate filegroup

CREATE PROCEDURE dbo.sp_ArchivePiXLData
    @DaysToKeep INT = 90
AS
BEGIN
    SET NOCOUNT ON;
    
    DECLARE @CutoffDate DATETIME2 = DATEADD(DAY, -@DaysToKeep, GETUTCDATE());
    DECLARE @BatchSize INT = 10000;
    DECLARE @RowsAffected INT = 1;
    
    WHILE @RowsAffected > 0
    BEGIN
        -- Move to archive
        INSERT INTO dbo.PiXL_Archive
        SELECT TOP (@BatchSize) * FROM dbo.PiXL_Materialized
        WHERE ReceivedAt < @CutoffDate;
        
        SET @RowsAffected = @@ROWCOUNT;
        
        -- Delete archived records
        DELETE TOP (@BatchSize) FROM dbo.PiXL_Materialized
        WHERE ReceivedAt < @CutoffDate;
        
        -- Yield to other queries
        WAITFOR DELAY '00:00:00.100';
    END
END
```

**b) Purge raw data after materialization:**

```sql
CREATE PROCEDURE dbo.sp_PurgeRawData
    @DaysToKeep INT = 7  -- Keep raw for 7 days in case reprocessing needed
AS
BEGIN
    SET NOCOUNT ON;
    
    DECLARE @MaxMaterializedId INT = (SELECT MAX(SourceId) FROM dbo.PiXL_Materialized);
    DECLARE @CutoffDate DATETIME2 = DATEADD(DAY, -@DaysToKeep, GETUTCDATE());
    
    -- Only purge if: (1) Already materialized AND (2) Older than retention
    DELETE FROM dbo.PiXL_Test
    WHERE Id <= @MaxMaterializedId
      AND ReceivedAt < @CutoffDate;
    
    SELECT @@ROWCOUNT AS RecordsPurged;
END
```

---

### 5. C# Data Access Layer

#### Current Implementation (Excellent)

```csharp
using var bulkCopy = new SqlBulkCopy(_settings.ConnectionString);
bulkCopy.DestinationTableName = "dbo.PiXL_Test";
bulkCopy.BatchSize = batch.Count;
await bulkCopy.WriteToServerAsync(table, ct);
```

**Assessment:** This is textbook optimal for bulk inserts.

#### Minor Recommendations

**a) Connection string optimizations:**

```json
{
  "ConnectionString": "Server=localhost;Database=SmartPixl;Integrated Security=True;TrustServerCertificate=True;Max Pool Size=100;Min Pool Size=10;Application Name=SmartPiXL"
}
```

| Setting | Purpose |
|---------|---------|
| `Max Pool Size=100` | Handle burst traffic |
| `Min Pool Size=10` | Keep connections warm |
| `Application Name=SmartPiXL` | Easier to identify in SQL Server logs |

**b) Consider SqlBulkCopyOptions for better performance:**

```csharp
using var bulkCopy = new SqlBulkCopy(
    _settings.ConnectionString,
    SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.UseInternalTransaction
);
```

| Option | Effect |
|--------|--------|
| `TableLock` | Bulk update lock (faster for exclusive inserts) |
| `UseInternalTransaction` | Auto-rollback on failure |

⚠️ **Caution:** `TableLock` blocks other writers. Only use if this service is the exclusive writer.

---

### 6. Materialization Procedure

#### Current Issue

The `sp_MaterializePiXLData` procedure has no protection against concurrent execution:

```sql
-- Problem: Two executions could process the same records
SELECT @LastProcessedId = MAX(SourceId) FROM dbo.PiXL_Materialized;
```

#### Recommendation

Add explicit locking:

```sql
CREATE PROCEDURE dbo.sp_MaterializePiXLData
AS
BEGIN
    SET NOCOUNT ON;
    
    -- Prevent concurrent execution
    DECLARE @LockResult INT;
    EXEC @LockResult = sp_getapplock 
        @Resource = 'MaterializePiXLData', 
        @LockMode = 'Exclusive', 
        @LockTimeout = 0;
    
    IF @LockResult < 0
    BEGIN
        PRINT 'Another materialization is in progress. Exiting.';
        RETURN;
    END
    
    BEGIN TRY
        DECLARE @LastProcessedId INT = ISNULL((SELECT MAX(SourceId) FROM dbo.PiXL_Materialized), 0);
        
        INSERT INTO dbo.PiXL_Materialized (...)
        SELECT ...
        FROM dbo.vw_PiXL_Parsed
        WHERE Id > @LastProcessedId
        ORDER BY Id;
        
        SELECT @@ROWCOUNT AS RecordsMaterialized;
    END TRY
    BEGIN CATCH
        THROW;
    END CATCH
    
    EXEC sp_releaseapplock @Resource = 'MaterializePiXLData';
END
```

---

### 7. Monitoring Recommendations

Add these queries to your monitoring:

**a) Queue health (check from app):**
```csharp
// Already implemented: writerService.QueueDepth
```

**b) Insert rate monitoring:**
```sql
SELECT 
    DATEADD(MINUTE, DATEDIFF(MINUTE, 0, ReceivedAt), 0) AS MinuteBucket,
    COUNT(*) AS RecordsPerMinute
FROM dbo.PiXL_Test WITH (NOLOCK)
WHERE ReceivedAt >= DATEADD(HOUR, -1, GETUTCDATE())
GROUP BY DATEADD(MINUTE, DATEDIFF(MINUTE, 0, ReceivedAt), 0)
ORDER BY MinuteBucket DESC;
```

**c) Materialization lag:**
```sql
SELECT 
    (SELECT MAX(Id) FROM dbo.PiXL_Test) - 
    ISNULL((SELECT MAX(SourceId) FROM dbo.PiXL_Materialized), 0) AS UnmaterializedRecords;
```

---

## Summary of Changes to Implement

### Priority 1 (Do Now)

1. **Add missing indexes** - See SQL file in `/SQL/04_PerformanceIndexes.sql`
2. **Add UNIQUE constraint on SourceId** - Prevent materialization duplicates
3. **Add app lock to materialization proc** - Prevent race conditions

### Priority 2 (Next Sprint)

4. **Add data lifecycle procedures** - Archive/purge old data
5. **Optimize connection string** - Pool size, app name
6. **Consider SqlBulkCopyOptions** - If exclusive writer

### Priority 3 (Scale Preparation)

7. **Replace scalar UDF** - Use inline TVF or parse in C#
8. **Consider table partitioning** - When approaching 100M+ rows
9. **Change Id to BIGINT** - For new installs

---

## Files Created

- `/SQL/04_PerformanceIndexes.sql` - Recommended indexes
- `/SQL/05_MaintenanceProcedures.sql` - Archival and purge procedures
