-- ============================================================================
-- Migration 55: Geo.Zipcode Polygon Table (Phase 8)
-- ============================================================================
-- Creates Geo.Zipcode — the definitive US zipcode lookup table with:
--   - Centroid lat/lon for quick point matching
--   - Integer bucket columns (LocationIQ pattern) for fast coarse filtering
--   - GEOGRAPHY boundary column for precise polygon containment
--   - Spatial index for STContains/STIntersects queries
--
-- The upgrade from legacy: actual polygon containment instead of crude
-- centroid radius matching. Zipcodes aren't circles.
--
-- Data population is a separate step — requires downloading ZCTA shapefiles
-- from the US Census Bureau and importing via ogr2ogr or custom script.
-- This migration creates the schema, indexes, and query patterns.
--
-- Design doc reference: §8.4 (Zipcode Polygon Table)
-- ============================================================================
USE SmartPiXL;
GO

SET NOCOUNT ON;
GO

PRINT '--- 55: Geo.Zipcode Polygon Table ---';
GO

-- =====================================================================
-- Step 1: Ensure Geo schema exists
-- =====================================================================
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'Geo')
BEGIN
    EXEC('CREATE SCHEMA Geo');
    PRINT '  Created schema: Geo';
END
ELSE
    PRINT '  Schema Geo already exists — skipped';
GO

-- =====================================================================
-- Step 2: Create Geo.Zipcode table
-- =====================================================================
IF OBJECT_ID('Geo.Zipcode', 'U') IS NULL
BEGIN
    CREATE TABLE Geo.Zipcode
    (
        ZipcodeId       INT             NOT NULL IDENTITY(1,1),
        Zipcode         CHAR(5)         NOT NULL,
        State           CHAR(2)         NOT NULL,
        City            VARCHAR(100)    NULL,
        CentroidLat     DECIMAL(9,6)    NOT NULL,
        CentroidLon     DECIMAL(9,6)    NOT NULL,
        -- Integer buckets for fast coarse matching (LocationIQ pattern)
        -- Each bucket ≈ 1.1 km at equator. Integer comparison is orders of
        -- magnitude faster than geography::STDistance().
        LatBucket100    AS CAST(CentroidLat * 100 AS INT) PERSISTED,
        LonBucket100    AS CAST(CentroidLon * 100 AS INT) PERSISTED,
        -- Native geography type for actual polygon boundary
        Boundary        GEOGRAPHY       NULL,       -- From Census ZCTA shapefile
        AreaSqMi        DECIMAL(10,2)   NULL,       -- Computed from Boundary
        Population      INT             NULL,       -- From Census data

        CONSTRAINT PK_Geo_Zipcode PRIMARY KEY CLUSTERED (ZipcodeId),
        CONSTRAINT UQ_Geo_Zipcode UNIQUE (Zipcode)
    );

    PRINT '  Created Geo.Zipcode';
END
ELSE
    PRINT '  Geo.Zipcode already exists — skipped';
GO

-- =====================================================================
-- Step 3: Integer bucket index for fast coarse-pass geo matching
-- =====================================================================
-- This is the key performance pattern from LocationIQ: eliminate 99% of
-- non-matching zipcodes with integer comparison before doing any
-- geography operations.
-- =====================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('Geo.Zipcode')
      AND name = 'IX_Geo_Zipcode_Buckets'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_Geo_Zipcode_Buckets
        ON Geo.Zipcode (LatBucket100, LonBucket100)
        INCLUDE (Zipcode, CentroidLat, CentroidLon, State, City);

    PRINT '  Created index IX_Geo_Zipcode_Buckets (integer bucket pattern)';
END
GO

-- =====================================================================
-- Step 4: State + City index for name-based lookups
-- =====================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('Geo.Zipcode')
      AND name = 'IX_Geo_Zipcode_StateCity'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_Geo_Zipcode_StateCity
        ON Geo.Zipcode (State, City)
        INCLUDE (Zipcode, CentroidLat, CentroidLon);

    PRINT '  Created index IX_Geo_Zipcode_StateCity';
END
GO

-- =====================================================================
-- Step 5: Spatial index on polygon boundary
-- =====================================================================
-- HIGH/HIGH/HIGH/HIGH grid density for maximum precision on
-- STContains/STIntersects queries. Only useful once ZCTA shapefiles
-- are imported and Boundary column is populated.
-- =====================================================================
IF OBJECT_ID('Geo.Zipcode', 'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.spatial_indexes
       WHERE object_id = OBJECT_ID('Geo.Zipcode')
         AND name = 'SIX_Geo_Zipcode_Boundary'
   )
BEGIN
    CREATE SPATIAL INDEX SIX_Geo_Zipcode_Boundary
        ON Geo.Zipcode (Boundary)
        WITH (GRIDS = (HIGH, HIGH, HIGH, HIGH));

    PRINT '  Created spatial index SIX_Geo_Zipcode_Boundary';
END
GO

-- =====================================================================
-- Step 6: Geo lookup stored procedure
-- =====================================================================
-- Two-phase geo lookup: integer bucket coarse pass + polygon containment.
-- Combines the integer bucket trick (eliminate 99% of candidates) with
-- the precision of actual polygon containment.
-- =====================================================================
CREATE OR ALTER PROCEDURE Geo.usp_LookupZipcode
    @Lat DECIMAL(9,6),
    @Lon DECIMAL(9,6)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @point GEOGRAPHY = GEOGRAPHY::Point(@Lat, @Lon, 4326);
    DECLARE @latBucket INT = CAST(@Lat * 100 AS INT);
    DECLARE @lonBucket INT = CAST(@Lon * 100 AS INT);

    -- Phase 1: Coarse bucket filter (integer comparison — fast)
    -- Check ±1 bucket in each direction (~3.3 km at equator)
    -- Phase 2: Precise polygon containment (only on candidates from Phase 1)
    SELECT TOP 1
        z.Zipcode,
        z.State,
        z.City,
        z.CentroidLat,
        z.CentroidLon,
        z.AreaSqMi,
        z.Population,
        1 AS IsContained
    FROM Geo.Zipcode z
    WHERE z.LatBucket100 BETWEEN @latBucket - 1 AND @latBucket + 1
      AND z.LonBucket100 BETWEEN @lonBucket - 1 AND @lonBucket + 1
      AND z.Boundary IS NOT NULL
      AND z.Boundary.STContains(@point) = 1;

    -- If no polygon containment match, fall back to nearest centroid
    IF @@ROWCOUNT = 0
    BEGIN
        SELECT TOP 1
            z.Zipcode,
            z.State,
            z.City,
            z.CentroidLat,
            z.CentroidLon,
            z.AreaSqMi,
            z.Population,
            0 AS IsContained
        FROM Geo.Zipcode z
        WHERE z.LatBucket100 BETWEEN @latBucket - 2 AND @latBucket + 2
          AND z.LonBucket100 BETWEEN @lonBucket - 2 AND @lonBucket + 2
        ORDER BY
            -- Manhattan distance on integer buckets (faster than Euclidean)
            ABS(z.LatBucket100 - @latBucket) + ABS(z.LonBucket100 - @lonBucket);
    END
END;
GO

-- =====================================================================
-- Step 7: Verification
-- =====================================================================
IF OBJECT_ID('Geo.Zipcode', 'U') IS NOT NULL
    PRINT '  OK: Geo.Zipcode exists';
ELSE
    PRINT '  ERROR: Geo.Zipcode missing!';

IF OBJECT_ID('Geo.usp_LookupZipcode', 'P') IS NOT NULL
    PRINT '  OK: Geo.usp_LookupZipcode exists';
ELSE
    PRINT '  ERROR: Geo.usp_LookupZipcode missing!';

-- Verify computed columns exist and are persisted
IF EXISTS (
    SELECT 1 FROM sys.computed_columns
    WHERE object_id = OBJECT_ID('Geo.Zipcode')
      AND name = 'LatBucket100'
      AND is_persisted = 1
)
    PRINT '  OK: LatBucket100 is persisted computed column';
ELSE IF OBJECT_ID('Geo.Zipcode', 'U') IS NOT NULL
    PRINT '  WARNING: LatBucket100 not found or not persisted';

IF EXISTS (
    SELECT 1 FROM sys.computed_columns
    WHERE object_id = OBJECT_ID('Geo.Zipcode')
      AND name = 'LonBucket100'
      AND is_persisted = 1
)
    PRINT '  OK: LonBucket100 is persisted computed column';
ELSE IF OBJECT_ID('Geo.Zipcode', 'U') IS NOT NULL
    PRINT '  WARNING: LonBucket100 not found or not persisted';
GO

PRINT '--- 55: Geo.Zipcode complete ---';
PRINT '  NOTE: Table is empty. Populate with Census ZCTA shapefile data:';
PRINT '  1. Download from https://www.census.gov/cgi-bin/geo/shapefiles/index.php';
PRINT '  2. Select Year → ZCTA5 → Download';
PRINT '  3. Import via ogr2ogr, SSIS, or custom PowerShell/Python script';
PRINT '  4. UPDATE Geo.Zipcode SET AreaSqMi = Boundary.STArea() / 2589988.11';
GO
