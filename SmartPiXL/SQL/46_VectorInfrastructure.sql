-- ============================================================================
-- Migration 46: Vector Infrastructure (Phase 7)
-- ============================================================================
-- Adds VECTOR(64) column to PiXL.Device for fingerprint similarity matching.
-- SQL Server 2025 native vector type with VECTOR_DISTANCE() enables:
--   - Fuzzy device matching when users clear cookies/change one setting
--   - UA drift detection (bot operators rotating user-agents slightly)
--   - Identity resolution without exact-match DeviceHash
--
-- Design doc §8.3 item 8: "Vector Fingerprint Similarity"
-- 64 dimensions encode: screen dims, cores, memory, color depth, feature bitmap,
--                        timezone offset, language hash, font count, plugin count,
--                        canvas/audio/webgl hashes, etc. (normalized 0-1 range)
--
-- UA drift vector (VECTOR(32)) also added per design doc §8.3 item 8 note.
-- ============================================================================
PRINT '--- 46: Adding vector infrastructure to PiXL.Device ---';
GO

USE SmartPiXL;
GO

-- =====================================================================
-- Step 1: Add FingerprintVector VECTOR(64) to PiXL.Device
-- =====================================================================
IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'PiXL' AND TABLE_NAME = 'Device'
    AND COLUMN_NAME = 'FingerprintVector')
BEGIN
    ALTER TABLE PiXL.Device ADD FingerprintVector VECTOR(64) NULL;
    PRINT 'Column PiXL.Device.FingerprintVector VECTOR(64) added.';
END
ELSE
    PRINT 'Column PiXL.Device.FingerprintVector already exists.';
GO

-- =====================================================================
-- Step 2: Add UaVector VECTOR(32) to PiXL.Device (UA drift detection)
-- =====================================================================
IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'PiXL' AND TABLE_NAME = 'Device'
    AND COLUMN_NAME = 'UaVector')
BEGIN
    ALTER TABLE PiXL.Device ADD UaVector VECTOR(32) NULL;
    PRINT 'Column PiXL.Device.UaVector VECTOR(32) added.';
END
ELSE
    PRINT 'Column PiXL.Device.UaVector already exists.';
GO

-- =====================================================================
-- Step 3: Insert test data and validate VECTOR_DISTANCE()
-- =====================================================================
-- NOTE: SQL Server 2025 RTM-GDR has a query processor bug where
-- VECTOR_DISTANCE with VECTOR variables or CROSS JOIN fails with
-- "Internal Query Processor Error". Inline CAST works fine.
-- This is expected to be fixed in a future CU.
PRINT '';
PRINT '=== Vector Distance Validation ===';

-- Validate VECTOR_DISTANCE works with inline values
SELECT
    VECTOR_DISTANCE('cosine',
        CAST('[0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5]' AS VECTOR(64)),
        CAST('[0.5,1.0,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5,0.5]' AS VECTOR(64))
    ) AS CosineDistance_OneChanged;
-- Expected: small number (close to 0 = very similar)
GO

PRINT '';
PRINT '=== Migration 46 complete ===';
GO
