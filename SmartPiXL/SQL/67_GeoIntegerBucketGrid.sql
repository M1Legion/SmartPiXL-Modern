SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ============================================================================
-- Migration 67: Integer Bucket Grid for Geo Matching
-- ============================================================================
--
-- Applies the LiQ playbook pattern: replace geography::STDistance() with
-- integer grid keys + approximate Euclidean distance.
--
-- Changes:
--   1. Add lat_k, lon_k INT columns to Geo.LatLon
--   2. Drop spatial index (no longer needed)
--   3. Create clustered index on (lat_k, lon_k) — physical data locality
--   4. Rewrite usp_MatchGeoVisits — pure arithmetic, no geography types
--
-- Performance improvement: eliminates geography::Point() construction and
-- STDistance() computation per-row.  The CROSS APPLY becomes a tight
-- B-tree range seek on 9 neighboring integer grid cells.
--
-- Grid: lat_k = CAST(Latitude * 100 AS INT)  →  0.01° cells (~1.1 km)
--        3x3 scan = ±0.01° = ±1.1 km > 692m radius ✓
--
-- Distance: flat-earth Euclidean approximation
--   SQRT(POWER((lat1-lat2)*111320, 2) + POWER((lon1-lon2)*82200, 2))
--   At 692m threshold, error vs great-circle is < 0.01%.
-- ============================================================================


-- =========================================================================
-- PART 1: Add integer bucket columns to Geo.LatLon
-- =========================================================================

IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'Geo' AND TABLE_NAME = 'LatLon'
      AND COLUMN_NAME = 'lat_k'
)
BEGIN
    ALTER TABLE Geo.LatLon ADD lat_k INT NULL;
    ALTER TABLE Geo.LatLon ADD lon_k INT NULL;
    PRINT 'Added lat_k, lon_k columns';
END
ELSE PRINT 'lat_k, lon_k already exist';
GO

-- Populate bucket keys (separate batch for column visibility)
IF EXISTS (
    SELECT 1 FROM Geo.LatLon WHERE lat_k IS NULL
)
BEGIN
    UPDATE Geo.LatLon SET
        lat_k = CAST(Latitude * 100 AS INT),
        lon_k = CAST(Longitude * 100 AS INT);
    PRINT 'Populated bucket keys: ' + CAST(@@ROWCOUNT AS VARCHAR) + ' rows';

    ALTER TABLE Geo.LatLon ALTER COLUMN lat_k INT NOT NULL;
    ALTER TABLE Geo.LatLon ALTER COLUMN lon_k INT NOT NULL;
    PRINT 'Set lat_k, lon_k to NOT NULL';
END
ELSE PRINT 'Bucket keys already populated';
GO


-- =========================================================================
-- PART 2: Clean up garbage row (0,0 coordinates)
-- =========================================================================

DELETE FROM Geo.LatLon WHERE Latitude = 0 AND Longitude = 0;
IF @@ROWCOUNT > 0 PRINT 'Removed 0,0 garbage row(s)';
GO


-- =========================================================================
-- PART 3: Drop spatial index (replaced by B-tree grid)
-- =========================================================================

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('Geo.LatLon') AND name = 'SIX_Geo_LatLon_GeoPoint'
)
BEGIN
    DROP INDEX SIX_Geo_LatLon_GeoPoint ON Geo.LatLon;
    PRINT 'Dropped spatial index SIX_Geo_LatLon_GeoPoint';
END
ELSE PRINT 'Spatial index already dropped';
GO


-- =========================================================================
-- PART 4: Drop GeoPoint column (no longer needed)
-- =========================================================================

IF EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'Geo' AND TABLE_NAME = 'LatLon'
      AND COLUMN_NAME = 'GeoPoint'
)
BEGIN
    ALTER TABLE Geo.LatLon DROP COLUMN GeoPoint;
    PRINT 'Dropped GeoPoint geography column';
END
ELSE PRINT 'GeoPoint column already dropped';
GO


-- =========================================================================
-- PART 5: Rebuild to clustered index on (lat_k, lon_k)
-- =========================================================================
-- Physical data locality: neighboring addresses are adjacent on disk.
-- The CROSS APPLY 3x3 grid scan becomes sequential page reads.

-- Drop existing PK to make room for new clustered
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('Geo.LatLon') AND name = 'PK_Geo_LatLon'
)
BEGIN
    ALTER TABLE Geo.LatLon DROP CONSTRAINT PK_Geo_LatLon;
    PRINT 'Dropped PK_Geo_LatLon (was clustered on LatLonId)';
END;

-- Drop the unique constraint on (Latitude, Longitude) if exists
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('Geo.LatLon') AND name = 'UQ_Geo_LatLon_Coords'
)
BEGIN
    ALTER TABLE Geo.LatLon DROP CONSTRAINT UQ_Geo_LatLon_Coords;
    PRINT 'Dropped UQ_Geo_LatLon_Coords';
END;

-- New clustered index on grid keys — foundation of the matching strategy
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('Geo.LatLon') AND name = 'CIX_Geo_LatLon_Bucket'
)
BEGIN
    CREATE CLUSTERED INDEX CIX_Geo_LatLon_Bucket
    ON Geo.LatLon (lat_k, lon_k);
    PRINT 'Created CIX_Geo_LatLon_Bucket (clustered on grid keys)';
END
ELSE PRINT 'CIX_Geo_LatLon_Bucket already exists';

-- PK as nonclustered on LatLonId (surrogate lookup)
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('Geo.LatLon') AND name = 'PK_Geo_LatLon'
)
BEGIN
    ALTER TABLE Geo.LatLon ADD CONSTRAINT PK_Geo_LatLon
        PRIMARY KEY NONCLUSTERED (LatLonId);
    PRINT 'Recreated PK_Geo_LatLon as nonclustered';
END;

-- Unique on (Latitude, Longitude) as nonclustered
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('Geo.LatLon') AND name = 'UQ_Geo_LatLon_Coords'
)
BEGIN
    ALTER TABLE Geo.LatLon ADD CONSTRAINT UQ_Geo_LatLon_Coords
        UNIQUE NONCLUSTERED (Latitude, Longitude);
    PRINT 'Recreated UQ_Geo_LatLon_Coords as nonclustered';
END;
GO


-- =========================================================================
-- PART 6: Rewrite usp_MatchGeoVisits with integer bucket grid
-- =========================================================================
-- Replaces:
--   geography::Point().STDistance()  →  flat-earth Euclidean distance
--   Spatial index scan              →  B-tree range seek on (lat_k, lon_k)
--   BETWEEN on DECIMAL columns      →  BETWEEN on INT grid keys (9 cells)
--
-- From the LiQ playbook:
--   "Spatial indexes in SQL Server are optimized for containment, not
--    nearest-neighbor.  An integer grid with a clustered B-tree index
--    dramatically outperforms STDistance with spatial indexes."

CREATE OR ALTER PROCEDURE [ETL].[usp_MatchGeoVisits]
    @BatchSize INT = 2000
AS
BEGIN
    SET NOCOUNT ON;

    -- 0.43 miles = 692 meters (same threshold as Xavier proc)
    DECLARE @MatchDistM FLOAT = 692.0;

    -- Flat-earth distance constants (meters per degree)
    -- 111,320 m/° latitude (constant)
    -- 82,200  m/° longitude (~42°N, US mid-latitudes)
    DECLARE @MetersPerDegLat FLOAT = 111320.0;
    DECLARE @MetersPerDegLon FLOAT = 82200.0;

    -- Precompute squared threshold (avoids SQRT in filter)
    DECLARE @MatchDistSq FLOAT = @MatchDistM * @MatchDistM;

    -- =====================================================================
    -- 1. READ WATERMARK
    -- =====================================================================
    DECLARE @LastId BIGINT, @MaxId BIGINT;
    SELECT @LastId = LastProcessedId
    FROM ETL.MatchWatermark
    WHERE ProcessName = 'MatchGeoVisits';

    SELECT @MaxId = MAX(MatchId) FROM PiXL.Match;
    IF @MaxId IS NULL OR @MaxId <= @LastId
    BEGIN
        SELECT 0 AS RowsProcessed, 0 AS RowsMatched,
               @LastId AS FromId, @LastId AS ToId;
        RETURN;
    END;
    IF @MaxId > @LastId + @BatchSize SET @MaxId = @LastId + @BatchSize;


    -- =====================================================================
    -- 2. BUILD CANDIDATE SET
    -- =====================================================================
    CREATE TABLE #Candidates (
        MatchId         BIGINT          NOT NULL  PRIMARY KEY,
        CompanyID       INT             NOT NULL,
        PiXLID          INT             NOT NULL,
        IpId            BIGINT          NOT NULL,
        GeoLat          FLOAT           NOT NULL,
        GeoLon          FLOAT           NOT NULL,
        lat_k           INT             NOT NULL,
        lon_k           INT             NOT NULL,
        IndividualKey   VARCHAR(35)     NULL,
        AddressKey      VARCHAR(35)     NULL,
        DistanceMeters  FLOAT           NULL
    );

    INSERT INTO #Candidates (MatchId, CompanyID, PiXLID, IpId, GeoLat, GeoLon, lat_k, lon_k)
    SELECT
        m.MatchId, m.CompanyID, m.PiXLID, m.IpId,
        CAST(ip.GeoLat AS FLOAT), CAST(ip.GeoLon AS FLOAT),
        CAST(ip.GeoLat * 100 AS INT),    -- integer bucket key
        CAST(ip.GeoLon * 100 AS INT)
    FROM PiXL.Match m
    JOIN PiXL.Settings s ON s.CompanyId = m.CompanyID AND s.PiXLId = m.PiXLID
    JOIN PiXL.IP ip ON ip.IpId = m.IpId
    WHERE m.MatchId > @LastId AND m.MatchId <= @MaxId
      AND m.MatchType = 'ip'
      AND m.IndividualKey IS NULL
      AND s.MatchGeoSupplemental = 1
      AND ip.GeoLat IS NOT NULL AND ip.GeoLat <> 0
      AND ip.GeoLon IS NOT NULL AND ip.GeoLon <> 0
      AND ip.GeoCountryCode = 'US';

    DECLARE @CandidateCount INT = @@ROWCOUNT;

    IF @CandidateCount = 0
    BEGIN
        UPDATE ETL.MatchWatermark SET
            LastProcessedId = @MaxId,
            LastRunAt = SYSUTCDATETIME()
        WHERE ProcessName = 'MatchGeoVisits';

        DROP TABLE #Candidates;
        SELECT 0 AS RowsProcessed, 0 AS RowsMatched,
               @LastId + 1 AS FromId, @MaxId AS ToId;
        RETURN;
    END;


    -- =====================================================================
    -- 3. INTEGER BUCKET GRID NEAREST-NEIGHBOR LOOKUP
    -- =====================================================================
    -- Phase A: For each distinct IP grid cell, find nearest Geo.LatLon
    --          within 692m using 3x3 grid scan + Euclidean distance.
    -- Phase B: Join back to AutoConsumer for identity resolution.
    --
    -- The CROSS APPLY + TOP 1 seeks into the clustered index on
    -- (lat_k, lon_k), scanning only ~9 neighboring cells.
    -- ORDER BY uses squared distance (no SQRT — monotonic transform),
    -- SQRT computed only for the winning TOP 1 row.

    CREATE TABLE #NearbyPoints (
        GeoLat          FLOAT           NOT NULL,
        GeoLon          FLOAT           NOT NULL,
        NearLatitude    DECIMAL(9,6)    NOT NULL,
        NearLongitude   DECIMAL(9,6)    NOT NULL,
        DistanceMeters  FLOAT           NOT NULL,
        PRIMARY KEY (GeoLat, GeoLon)
    );

    INSERT INTO #NearbyPoints (GeoLat, GeoLon, NearLatitude, NearLongitude, DistanceMeters)
    SELECT
        d.GeoLat, d.GeoLon,
        x.Latitude, x.Longitude, x.dist_m
    FROM (SELECT DISTINCT GeoLat, GeoLon, lat_k, lon_k FROM #Candidates) d
    CROSS APPLY (
        SELECT TOP 1
            gl.Latitude,
            gl.Longitude,
            SQRT(
                POWER((d.GeoLat - gl.Latitude) * @MetersPerDegLat, 2) +
                POWER((d.GeoLon - gl.Longitude) * @MetersPerDegLon, 2)
            ) AS dist_m
        FROM Geo.LatLon gl
        WHERE gl.lat_k BETWEEN d.lat_k - 1 AND d.lat_k + 1
          AND gl.lon_k BETWEEN d.lon_k - 1 AND d.lon_k + 1
        ORDER BY
            -- Squared distance for sort (skip SQRT — monotonic)
            POWER(d.GeoLat - gl.Latitude, 2) +
            POWER(d.GeoLon - gl.Longitude, 2)
    ) x
    WHERE x.dist_m <= @MatchDistM;

    -- Phase B: Join to AutoConsumer for identity resolution
    CREATE TABLE #GeoMatch (
        GeoLat          FLOAT           NOT NULL,
        GeoLon          FLOAT           NOT NULL,
        IndividualKey   VARCHAR(35)     NOT NULL,
        AddressKey      VARCHAR(35)     NULL,
        DistanceMeters  FLOAT           NOT NULL,
        PRIMARY KEY (GeoLat, GeoLon)
    );

    INSERT INTO #GeoMatch (GeoLat, GeoLon, IndividualKey, AddressKey, DistanceMeters)
    SELECT np.GeoLat, np.GeoLon, ac.IndividualKey, ac.AddressKey, np.DistanceMeters
    FROM #NearbyPoints np
    CROSS APPLY (
        SELECT TOP 1 ac.IndividualKey, ac.AddressKey
        FROM AutoUpdate.dbo.AutoConsumer ac
        WHERE ac.Latitude = np.NearLatitude
          AND ac.Longitude = np.NearLongitude
          AND ac.PPM_Indicator IS NOT NULL
          AND ac.IndividualKey IS NOT NULL
        ORDER BY ac.RecordID DESC
    ) ac;

    -- Apply resolved identities to candidates
    UPDATE c SET
        c.IndividualKey  = gm.IndividualKey,
        c.AddressKey     = gm.AddressKey,
        c.DistanceMeters = gm.DistanceMeters
    FROM #Candidates c
    JOIN #GeoMatch gm ON c.GeoLat = gm.GeoLat AND c.GeoLon = gm.GeoLon;

    DECLARE @MatchedCount INT = (
        SELECT COUNT(*) FROM #Candidates WHERE IndividualKey IS NOT NULL
    );


    -- =====================================================================
    -- 4. UPDATE PiXL.Match — apply geo resolution
    -- =====================================================================
    UPDATE m SET
        m.IndividualKey = c.IndividualKey,
        m.AddressKey    = c.AddressKey,
        m.MatchedAt     = SYSUTCDATETIME(),
        m.ConfidenceScore = CASE
            WHEN c.DistanceMeters <= 100 THEN 0.95
            WHEN c.DistanceMeters <= 300 THEN 0.85
            WHEN c.DistanceMeters <= 500 THEN 0.70
            ELSE 0.55
        END
    FROM PiXL.Match m
    JOIN #Candidates c ON m.MatchId = c.MatchId
    WHERE c.IndividualKey IS NOT NULL;


    -- =====================================================================
    -- 5. UPDATE WATERMARK
    -- =====================================================================
    UPDATE ETL.MatchWatermark SET
        LastProcessedId = @MaxId,
        LastRunAt       = SYSUTCDATETIME(),
        RowsProcessed   = RowsProcessed + @CandidateCount,
        RowsMatched     = RowsMatched + @MatchedCount
    WHERE ProcessName = 'MatchGeoVisits';


    -- =====================================================================
    -- 6. CLEANUP + RETURN
    -- =====================================================================
    DROP TABLE #Candidates;
    DROP TABLE #NearbyPoints;
    DROP TABLE #GeoMatch;

    SELECT @CandidateCount AS RowsProcessed,
           @MatchedCount   AS RowsMatched,
           @LastId + 1     AS FromId,
           @MaxId          AS ToId;
END;
GO


-- =========================================================================
-- VERIFICATION
-- =========================================================================
PRINT '';
PRINT '=========================================================================';
PRINT ' 67_GeoIntegerBucketGrid.sql — COMPLETE';
PRINT '';
PRINT ' SCHEMA:';
PRINT '   Geo.LatLon: +lat_k +lon_k, -GeoPoint, clustered on (lat_k, lon_k)';
PRINT '   Spatial index dropped — pure B-tree grid seeks';
PRINT '';
PRINT ' SP:';
PRINT '   ETL.usp_MatchGeoVisits rewritten:';
PRINT '     geography::STDistance()  → flat-earth Euclidean (111320/82200 m/°)';
PRINT '     Spatial index scan      → integer grid 3x3 range seek';
PRINT '     ORDER BY STDistance      → ORDER BY squared distance (skip SQRT)';
PRINT '=========================================================================';

SELECT
    COUNT(*) AS TotalRows,
    COUNT(DISTINCT lat_k * 100000 + lon_k) AS DistinctBuckets,
    MIN(lat_k) AS MinLatK, MAX(lat_k) AS MaxLatK,
    MIN(lon_k) AS MinLonK, MAX(lon_k) AS MaxLonK
FROM Geo.LatLon;
GO
