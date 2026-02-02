-- =============================================
-- SmartPiXL Database - PERFORMANCE INDEXES
-- 
-- Run this after initial setup to optimize query performance.
-- These indexes support common query patterns for:
--   - Fingerprint matching across visitors
--   - IP-based geographic analysis
--   - Company/time-range dashboard queries
--   - Duplicate detection
--
-- Last Updated: 2026-02-02
-- =============================================

USE SmartPixl;
GO

-- =============================================
-- SECTION 1: UNIQUE CONSTRAINT FOR MATERIALIZATION
-- Prevents duplicate records from concurrent materialization runs
-- =============================================
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PiXL_Materialized_SourceId')
BEGIN
    CREATE UNIQUE INDEX UX_PiXL_Materialized_SourceId 
    ON dbo.PiXL_Materialized(SourceId) 
    ON [SmartPixl];
    
    PRINT 'Created unique index UX_PiXL_Materialized_SourceId';
END
GO

-- =============================================
-- SECTION 2: FINGERPRINT MATCHING INDEXES
-- Supports cross-visitor identification queries
-- =============================================

-- Composite fingerprint index with covering columns
-- Query: Find all visits with same fingerprint combo
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Materialized_Fingerprints')
BEGIN
    CREATE INDEX IX_PiXL_Materialized_Fingerprints 
    ON dbo.PiXL_Materialized (CanvasFingerprint, WebGLFingerprint)
    INCLUDE (IPAddress, ReceivedAt, Domain, CompanyID, PiXLID)
    ON [SmartPixl];
    
    PRINT 'Created index IX_PiXL_Materialized_Fingerprints';
END
GO

-- Audio fingerprint for additional correlation
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Materialized_AudioFP')
BEGIN
    CREATE INDEX IX_PiXL_Materialized_AudioFP 
    ON dbo.PiXL_Materialized (AudioFingerprint)
    INCLUDE (CanvasFingerprint, WebGLFingerprint, IPAddress)
    WHERE AudioFingerprint IS NOT NULL
    ON [SmartPixl];
    
    PRINT 'Created index IX_PiXL_Materialized_AudioFP';
END
GO

-- =============================================
-- SECTION 3: IP-BASED QUERY INDEXES
-- Supports geographic analysis and IP reputation queries
-- =============================================

-- IP address lookup with context
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Materialized_IPAddress')
BEGIN
    CREATE INDEX IX_PiXL_Materialized_IPAddress 
    ON dbo.PiXL_Materialized (IPAddress)
    INCLUDE (ReceivedAt, Domain, CanvasFingerprint, CompanyID)
    ON [SmartPixl];
    
    PRINT 'Created index IX_PiXL_Materialized_IPAddress';
END
GO

-- =============================================
-- SECTION 4: COMPANY/TIME-RANGE INDEXES
-- Supports dashboard and reporting queries
-- =============================================

-- Company + Time for dashboard queries
-- Query: Get all pixel fires for a company in date range
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Materialized_Company_Time')
BEGIN
    CREATE INDEX IX_PiXL_Materialized_Company_Time 
    ON dbo.PiXL_Materialized (CompanyID, ReceivedAt DESC)
    INCLUDE (PiXLID, Domain, CanvasFingerprint, IPAddress)
    ON [SmartPixl];
    
    PRINT 'Created index IX_PiXL_Materialized_Company_Time';
END
GO

-- PiXL ID specific queries
-- Query: Get all fires for a specific pixel campaign
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Materialized_PiXL')
BEGIN
    CREATE INDEX IX_PiXL_Materialized_PiXL 
    ON dbo.PiXL_Materialized (CompanyID, PiXLID, ReceivedAt DESC)
    INCLUDE (Domain, CanvasFingerprint, IPAddress)
    ON [SmartPixl];
    
    PRINT 'Created index IX_PiXL_Materialized_PiXL';
END
GO

-- =============================================
-- SECTION 5: RAW TABLE INDEX FOR MATERIALIZATION
-- Speeds up the materialization SELECT
-- =============================================

-- Index to support efficient WHERE Id > @LastProcessedId queries
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Test_Id_Covering')
BEGIN
    CREATE INDEX IX_PiXL_Test_Id_Covering 
    ON dbo.PiXL_Test (Id)
    INCLUDE (CompanyID, PiXLID, IPAddress, ReceivedAt, QueryString)
    ON [SmartPixl];
    
    PRINT 'Created index IX_PiXL_Test_Id_Covering';
END
GO

-- =============================================
-- SECTION 6: BOT/AUTOMATION DETECTION INDEX
-- Supports WebDriver detection queries
-- =============================================

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PiXL_Materialized_BotDetection')
BEGIN
    CREATE INDEX IX_PiXL_Materialized_BotDetection 
    ON dbo.PiXL_Materialized (WebDriverDetected)
    INCLUDE (IPAddress, CanvasFingerprint, ReceivedAt, Domain)
    WHERE WebDriverDetected = 1
    ON [SmartPixl];
    
    PRINT 'Created index IX_PiXL_Materialized_BotDetection';
END
GO

-- =============================================
-- VERIFICATION
-- =============================================
PRINT '';
PRINT '============================================';
PRINT 'Performance Indexes Installation Complete!';
PRINT '============================================';
PRINT '';

SELECT 
    i.name AS IndexName,
    OBJECT_NAME(i.object_id) AS TableName,
    i.type_desc AS IndexType,
    i.is_unique AS IsUnique,
    ds.name AS FileGroup
FROM sys.indexes i
INNER JOIN sys.data_spaces ds ON i.data_space_id = ds.data_space_id
WHERE OBJECT_NAME(i.object_id) IN ('PiXL_Test', 'PiXL_Materialized')
  AND i.name IS NOT NULL
ORDER BY OBJECT_NAME(i.object_id), i.name;

GO
