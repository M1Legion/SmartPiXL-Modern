SET NOCOUNT ON;
GO

-- ============================================================================
-- Migration 66: Supplemental Geo Match — whitelist migration + spatial infra
-- ============================================================================
--
-- Replaces Xavier's CRM_Match_Supplemental_Whitelist table approach with
-- native PiXL.Company / PiXL.Settings flags.  Builds the spatial lookup
-- infrastructure needed for proximity-based identity resolution:
--
--   Geo.LatLon          — normalized distinct lat/lon from AutoConsumer PPM
--   Spatial index       — on the 32K-row Geo.LatLon (not the 427M-row AC)
--   NCI on AC           — (Latitude, Longitude) for the join back
--   ETL.usp_MatchGeoVisits — the match proc (Phase 5 in the pipeline)
--
-- Xavier proc equivalent: SP_Update_CRM_Match_Reference_RecordID_Supp
-- ============================================================================


-- =========================================================================
-- PART 1: Add MatchGeoSupplemental flag to PiXL.Company
-- =========================================================================
-- Company-level default: when a new PiXL is created, it inherits this flag.

IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'PiXL' AND TABLE_NAME = 'Company'
      AND COLUMN_NAME = 'MatchGeoSupplemental'
)
BEGIN
    ALTER TABLE PiXL.Company ADD MatchGeoSupplemental BIT NOT NULL DEFAULT 0;
    PRINT 'Added PiXL.Company.MatchGeoSupplemental';
END
ELSE PRINT 'PiXL.Company.MatchGeoSupplemental already exists';
GO


-- =========================================================================
-- PART 2: Add MatchGeoSupplemental flag to PiXL.Settings
-- =========================================================================
-- Per-PiXL override.  Geo match only runs for visits whose PiXL has this = 1.

IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'PiXL' AND TABLE_NAME = 'Settings'
      AND COLUMN_NAME = 'MatchGeoSupplemental'
)
BEGIN
    ALTER TABLE PiXL.Settings ADD MatchGeoSupplemental BIT NOT NULL DEFAULT 0;
    PRINT 'Added PiXL.Settings.MatchGeoSupplemental';
END
ELSE PRINT 'PiXL.Settings.MatchGeoSupplemental already exists';
GO


-- =========================================================================
-- PART 3: Populate flags from Xavier whitelist
-- =========================================================================
-- Source: CRM_Match_Supplemental_Whitelist on Xavier (192.168.88.35)
-- 163 company-level entries (PiXLID IS NULL = all pixls for that company)
-- 281 pixl-specific entries
-- Excluded: CompanyID 12718, 12730 (hardcoded exclusions in Xavier proc)

-- 3a. Company-level: set company flag + cascade to all existing pixls
UPDATE PiXL.Company SET MatchGeoSupplemental = 1
WHERE CompanyID IN (
    12346, 12347, 12348, 12349, 12350, 12351, 12353, 12354, 12355, 12356,
    12357, 12358, 12359, 12360, 12361, 12362, 12363, 12364, 12365, 12366,
    12367, 12369, 12370, 12372, 12373, 12374, 12375, 12376, 12377, 12378,
    12381, 12382, 12384, 12386, 12390, 12391, 12393, 12394, 12395, 12396,
    12397, 12399, 12400, 12401, 12402, 12403, 12404, 12405, 12406, 12407,
    12408, 12419, 12420, 12430, 12431, 12432, 12433, 12434, 12435, 12436,
    12437, 12438, 12439, 12443, 12444, 12446, 12447, 12450, 12451, 12452,
    12455, 12456, 12457, 12459, 12462, 12463, 12464, 12472, 12474, 12475,
    12476, 12477, 12478, 12479, 12481, 12482, 12483, 12485, 12487, 12489,
    12493, 12494, 12495, 12498, 12500, 12501, 12502, 12503, 12504, 12505,
    12507, 12508, 12509, 12510, 12512, 12513, 12515, 12516, 12521, 12524,
    12525, 12526, 12531, 12532, 12533, 12534, 12535, 12536, 12537, 12538,
    12539, 12540, 12541, 12542, 12543, 12544, 12545, 12546, 12550, 12551,
    12553, 12554, 12559, 12560, 12562, 12563, 12566, 12568, 12569, 12571,
    12574, 12576, 12577, 12581, 12583, 12586, 12587, 12590, 12595, 12616,
    12687, 12694, 12712, 12715, 12733, 12745, 12750, 12777, 12784, 12786,
    12802, 12803, 12809
);
PRINT 'Company-level flags set: ' + CAST(@@ROWCOUNT AS VARCHAR);

-- Cascade company flag to all existing PiXL.Settings rows for those companies
UPDATE s SET s.MatchGeoSupplemental = 1
FROM PiXL.Settings s
JOIN PiXL.Company c ON s.CompanyId = c.CompanyID
WHERE c.MatchGeoSupplemental = 1;
PRINT 'Settings rows cascaded from company flag: ' + CAST(@@ROWCOUNT AS VARCHAR);

-- 3b. PiXL-specific: set individual pixl flags (these companies may NOT have company-level flag)
;WITH PixlWhitelist AS (
    SELECT * FROM (VALUES
        (12345, 2), (12345, 3), (12345, 4), (12345, 5), (12345, 6), (12345, 7),
        (12345, 8), (12345, 9), (12345, 10), (12345, 11), (12345, 12), (12345, 13),
        (12345, 14), (12345, 15), (12345, 17), (12345, 18), (12345, 19), (12345, 20),
        (12345, 21), (12345, 22), (12345, 23), (12345, 24), (12345, 25), (12345, 26),
        (12345, 27), (12345, 28), (12345, 30), (12345, 31), (12345, 34), (12345, 35),
        (12345, 37), (12345, 38), (12345, 61), (12345, 62), (12345, 94), (12345, 95),
        (12345, 96), (12345, 97), (12345, 98),
        (12379, 3), (12379, 5), (12379, 6), (12379, 7), (12379, 8), (12379, 9),
        (12379, 10), (12379, 12), (12379, 13), (12379, 14), (12379, 15), (12379, 16),
        (12379, 17), (12379, 18), (12379, 19), (12379, 20), (12379, 21), (12379, 22),
        (12383, 1), (12383, 2), (12383, 4),
        (12387, 1), (12387, 2),
        (12471, 1), (12471, 2), (12471, 3), (12471, 4), (12471, 5), (12471, 6),
        (12471, 7), (12471, 8), (12471, 9), (12471, 10), (12471, 11), (12471, 12),
        (12471, 13), (12471, 14), (12471, 15), (12471, 16), (12471, 17), (12471, 18),
        (12471, 19), (12471, 20), (12471, 21), (12471, 22), (12471, 23), (12471, 24),
        (12471, 25), (12471, 26), (12471, 27), (12471, 28), (12471, 29), (12471, 30),
        (12471, 31), (12471, 32), (12471, 33), (12471, 34), (12471, 35), (12471, 36),
        (12471, 37), (12471, 38), (12471, 39), (12471, 40), (12471, 41), (12471, 42),
        (12471, 43), (12471, 44), (12471, 45), (12471, 46), (12471, 47), (12471, 48),
        (12471, 49), (12471, 50), (12471, 51), (12471, 52), (12471, 53), (12471, 54),
        (12471, 55), (12471, 56), (12471, 57), (12471, 58), (12471, 59), (12471, 60),
        (12471, 61), (12471, 63), (12471, 64), (12471, 65), (12471, 66), (12471, 67),
        (12471, 68), (12471, 69), (12471, 70), (12471, 71), (12471, 72), (12471, 73),
        (12471, 74), (12471, 75), (12471, 76), (12471, 77), (12471, 78), (12471, 79),
        (12471, 80), (12471, 81), (12471, 82), (12471, 83), (12471, 84), (12471, 85),
        (12471, 86), (12471, 87), (12471, 88), (12471, 89), (12471, 90), (12471, 91),
        (12506, 1), (12506, 2), (12506, 3), (12506, 4), (12506, 5), (12506, 6),
        (12506, 7), (12506, 8), (12506, 9), (12506, 10), (12506, 11), (12506, 12),
        (12506, 13), (12506, 14), (12506, 15), (12506, 16), (12506, 17), (12506, 18),
        (12506, 19), (12506, 20), (12506, 21), (12506, 22), (12506, 23), (12506, 24),
        (12506, 25), (12506, 26), (12506, 27), (12506, 28), (12506, 29), (12506, 30),
        (12506, 31), (12506, 32), (12506, 33), (12506, 34), (12506, 35), (12506, 36),
        (12506, 37), (12506, 38), (12506, 39), (12506, 40), (12506, 41), (12506, 42),
        (12506, 43), (12506, 44), (12506, 45), (12506, 46), (12506, 47), (12506, 48),
        (12506, 49), (12506, 50), (12506, 51), (12506, 52), (12506, 53), (12506, 54),
        (12506, 55), (12506, 56), (12506, 57), (12506, 58), (12506, 59), (12506, 60),
        (12506, 61), (12506, 62), (12506, 63), (12506, 64), (12506, 65), (12506, 66),
        (12506, 67), (12506, 68), (12506, 69), (12506, 70), (12506, 71), (12506, 72),
        (12506, 73), (12506, 74), (12506, 75), (12506, 76), (12506, 77), (12506, 78),
        (12506, 79), (12506, 80), (12506, 81), (12506, 82), (12506, 83), (12506, 84),
        (12506, 85), (12506, 86), (12506, 87), (12506, 88), (12506, 89), (12506, 90),
        (12506, 91), (12506, 92), (12506, 93), (12506, 94), (12506, 95), (12506, 96),
        (12506, 97), (12506, 98), (12506, 99), (12506, 100), (12506, 101), (12506, 102),
        (12506, 103), (12506, 104), (12506, 105), (12506, 106), (12506, 107), (12506, 108),
        (12506, 109), (12506, 110), (12506, 111), (12506, 112), (12506, 113), (12506, 114),
        (12506, 115), (12506, 116), (12506, 117), (12506, 118), (12506, 119), (12506, 120),
        (12506, 121), (12506, 122), (12506, 123), (12506, 124), (12506, 125), (12506, 126),
        (12506, 127),
        (12588, 2), (12588, 3)
    ) AS v(CompanyID, PiXLID)
)
UPDATE s SET s.MatchGeoSupplemental = 1
FROM PiXL.Settings s
JOIN PixlWhitelist w ON s.CompanyId = w.CompanyID AND s.PiXLId = w.PiXLID;
PRINT 'PiXL-specific flags set: ' + CAST(@@ROWCOUNT AS VARCHAR);
GO


-- =========================================================================
-- PART 4: Geo.LatLon — normalized lat/lon lookup with spatial index
-- =========================================================================
-- 32,604 distinct lat/lon pairs from AutoConsumer PPM-eligible records.
-- Spatial index on this tiny table replaces the need for a spatial index
-- on the 427M-row AutoConsumer table.

IF OBJECT_ID('Geo.LatLon', 'U') IS NULL
BEGIN
    CREATE TABLE Geo.LatLon (
        LatLonId    INT           NOT NULL IDENTITY(1,1),
        Latitude    DECIMAL(9,6)  NOT NULL,
        Longitude   DECIMAL(9,6)  NOT NULL,
        GeoPoint    GEOGRAPHY     NOT NULL,
        RecordCount INT           NOT NULL DEFAULT 0,

        CONSTRAINT PK_Geo_LatLon PRIMARY KEY CLUSTERED (LatLonId),
        CONSTRAINT UQ_Geo_LatLon_Coords UNIQUE (Latitude, Longitude)
    );
    PRINT 'Created Geo.LatLon table';
END
ELSE PRINT 'Geo.LatLon already exists';
GO


-- =========================================================================
-- PART 5: Populate Geo.LatLon from AutoConsumer PPM-eligible records
-- =========================================================================

IF NOT EXISTS (SELECT 1 FROM Geo.LatLon)
BEGIN
    PRINT 'Populating Geo.LatLon from AutoConsumer (PPM-eligible, distinct lat/lon)...';

    INSERT INTO Geo.LatLon (Latitude, Longitude, GeoPoint, RecordCount)
    SELECT
        Latitude,
        Longitude,
        geography::Point(Latitude, Longitude, 4326),
        COUNT(*)
    FROM AutoUpdate.dbo.AutoConsumer WITH (NOLOCK)
    WHERE PPM_Indicator IS NOT NULL
      AND Latitude IS NOT NULL
      AND Longitude IS NOT NULL
      AND Latitude BETWEEN -90 AND 90
      AND Longitude BETWEEN -180 AND 180
    GROUP BY Latitude, Longitude;

    PRINT 'Geo.LatLon populated: ' + CAST(@@ROWCOUNT AS VARCHAR) + ' distinct lat/lon pairs';
END
ELSE PRINT 'Geo.LatLon already populated';
GO


-- =========================================================================
-- PART 6: Spatial index on Geo.LatLon
-- =========================================================================

SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('Geo.LatLon') AND type_desc = 'SPATIAL'
)
BEGIN
    CREATE SPATIAL INDEX SIX_Geo_LatLon_GeoPoint
    ON Geo.LatLon (GeoPoint)
    USING GEOGRAPHY_AUTO_GRID
    WITH (CELLS_PER_OBJECT = 16);

    PRINT 'Created spatial index SIX_Geo_LatLon_GeoPoint';
END
ELSE PRINT 'Spatial index on Geo.LatLon already exists';
GO


-- =========================================================================
-- PART 7: NCI on AutoConsumer for lat/lon → identity join
-- =========================================================================
-- When the spatial query finds nearby Geo.LatLon rows, we join back to
-- AutoConsumer on (Latitude, Longitude) to get IndividualKey + AddressKey.
-- Filter: PPM_Indicator IS NOT NULL (same subset that built Geo.LatLon).

IF NOT EXISTS (
    SELECT 1 FROM AutoUpdate.sys.indexes
    WHERE object_id = OBJECT_ID('AutoUpdate.dbo.AutoConsumer')
      AND name = 'IX_AutoConsumer_LatLon_PPM'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_AutoConsumer_LatLon_PPM
    ON AutoUpdate.dbo.AutoConsumer (Latitude, Longitude)
    INCLUDE (IndividualKey, AddressKey)
    WHERE PPM_Indicator IS NOT NULL
      AND Latitude IS NOT NULL
      AND Longitude IS NOT NULL;

    PRINT 'Created IX_AutoConsumer_LatLon_PPM on AutoUpdate.dbo.AutoConsumer';
END
ELSE PRINT 'IX_AutoConsumer_LatLon_PPM already exists';
GO


-- =========================================================================
-- PART 8: ETL.MatchWatermark row for MatchGeoVisits
-- =========================================================================

IF NOT EXISTS (SELECT 1 FROM ETL.MatchWatermark WHERE ProcessName = 'MatchGeoVisits')
BEGIN
    INSERT INTO ETL.MatchWatermark (ProcessName, LastProcessedId, LastRunAt, RowsProcessed, RowsMatched)
    VALUES ('MatchGeoVisits', 0, SYSUTCDATETIME(), 0, 0);
    PRINT 'Added MatchGeoVisits watermark (starting from 0)';
END
ELSE PRINT 'MatchGeoVisits watermark already exists';
GO


-- =========================================================================
-- PART 9: ETL.usp_MatchGeoVisits — supplemental geo proximity match
-- =========================================================================
-- Modern equivalent of Xavier's SP_Update_CRM_Match_Reference_RecordID_Supp.
--
-- Logic:
--   1. Read PiXL.Match rows (already IP-matched, unresolved) in watermark window
--   2. Gate by PiXL.Settings.MatchGeoSupplemental = 1
--   3. Look up IP's geo coordinates from PiXL.IP
--   4. Build geography point, find nearby Geo.LatLon within 692m (0.43 miles)
--   5. Join back to AutoConsumer via (Latitude, Longitude) to get IndividualKey
--   6. Deduplicate: 1 resolution per Match row, closest match wins
--   7. UPDATE PiXL.Match SET IndividualKey, AddressKey, MatchedAt
--
-- Key difference from Xavier: operates on PiXL.Match (already-created IP match
-- rows with NULL IndividualKey), not on raw visits.  This makes it a
-- "resolution enrichment" step that runs after usp_MatchLegacyVisits.

CREATE OR ALTER PROCEDURE [ETL].[usp_MatchGeoVisits]
    @BatchSize INT = 2000
AS
BEGIN
    SET NOCOUNT ON;
    SET QUOTED_IDENTIFIER ON;

    -- 0.43 miles = 692 meters (same threshold as Xavier proc)
    DECLARE @RadiusMeters FLOAT = 692.0;

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
    -- Candidates: IP-matched rows without an IndividualKey (unresolved),
    -- whose PiXL has MatchGeoSupplemental = 1,
    -- with a US IP that has geo coordinates.

    CREATE TABLE #Candidates (
        MatchId         BIGINT          NOT NULL  PRIMARY KEY,
        CompanyID       INT             NOT NULL,
        PiXLID          INT             NOT NULL,
        IpId            BIGINT          NOT NULL,
        GeoLat          DECIMAL(9,4)    NOT NULL,
        GeoLon          DECIMAL(9,4)    NOT NULL,
        IndividualKey   VARCHAR(35)     NULL,
        AddressKey      VARCHAR(35)     NULL,
        DistanceMeters  FLOAT           NULL
    );

    INSERT INTO #Candidates (MatchId, CompanyID, PiXLID, IpId, GeoLat, GeoLon)
    SELECT
        m.MatchId, m.CompanyID, m.PiXLID, m.IpId,
        ip.GeoLat, ip.GeoLon
    FROM PiXL.Match m
    JOIN PiXL.Settings s ON s.CompanyId = m.CompanyID AND s.PiXLId = m.PiXLID
    JOIN PiXL.IP ip ON ip.IpId = m.IpId
    WHERE m.MatchId > @LastId AND m.MatchId <= @MaxId
      AND m.MatchType = 'ip'
      AND m.IndividualKey IS NULL          -- unresolved
      AND s.MatchGeoSupplemental = 1       -- opted in
      AND ip.GeoLat IS NOT NULL AND ip.GeoLat <> 0
      AND ip.GeoLon IS NOT NULL AND ip.GeoLon <> 0
      AND ip.GeoCountryCode = 'US';       -- US only (same as Xavier)

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
    -- 3. SPATIAL PROXIMITY LOOKUP
    -- =====================================================================
    -- For each candidate IP location, find the nearest Geo.LatLon point
    -- within 692m.  Uses bounding-box pre-filter on the B-tree unique
    -- index (Latitude, Longitude) for speed, then refines with STDistance.
    --
    -- 692m ≈ 0.0063° latitude.  At 45°N ≈ 0.009° longitude.
    -- We use ±0.01° bounding box to be safe, then exact distance check.
    --
    -- Phase A: For each distinct IP lat/lon, find closest Geo.LatLon row.
    -- Phase B: Join back to AutoConsumer via (Latitude, Longitude) for identity.

    DECLARE @LatDelta FLOAT = 0.01;   -- ~1.1 km bounding box
    DECLARE @LonDelta FLOAT = 0.01;

    -- Phase A: Distinct IP locations → nearest geo point
    CREATE TABLE #NearbyPoints (
        GeoLat          DECIMAL(9,4)    NOT NULL,
        GeoLon          DECIMAL(9,4)    NOT NULL,
        NearLatitude    DECIMAL(9,6)    NOT NULL,
        NearLongitude   DECIMAL(9,6)    NOT NULL,
        DistanceMeters  FLOAT           NOT NULL,
        PRIMARY KEY (GeoLat, GeoLon)
    );

    INSERT INTO #NearbyPoints (GeoLat, GeoLon, NearLatitude, NearLongitude, DistanceMeters)
    SELECT
        d.GeoLat, d.GeoLon,
        nearest.Latitude, nearest.Longitude, nearest.Dist
    FROM (SELECT DISTINCT GeoLat, GeoLon FROM #Candidates) d
    CROSS APPLY (
        SELECT TOP 1
            gl.Latitude, gl.Longitude,
            geography::Point(d.GeoLat, d.GeoLon, 4326).STDistance(gl.GeoPoint) AS Dist
        FROM Geo.LatLon gl
        WHERE gl.Latitude  BETWEEN d.GeoLat - @LatDelta AND d.GeoLat + @LatDelta
          AND gl.Longitude BETWEEN d.GeoLon - @LonDelta AND d.GeoLon + @LonDelta
          AND geography::Point(d.GeoLat, d.GeoLon, 4326).STDistance(gl.GeoPoint) <= @RadiusMeters
        ORDER BY geography::Point(d.GeoLat, d.GeoLon, 4326).STDistance(gl.GeoPoint)
    ) nearest;

    -- Phase B: Join to AutoConsumer for identity resolution
    -- Pick best match per (GeoLat, GeoLon): most recent RecordID wins.
    CREATE TABLE #GeoMatch (
        GeoLat          DECIMAL(9,4)    NOT NULL,
        GeoLon          DECIMAL(9,4)    NOT NULL,
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
    -- Only updates rows where we found an identity.  Sets IndividualKey,
    -- AddressKey, MatchedAt.  Does NOT change MatchType (stays 'ip').

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
-- PART 10: Update usp_Dash_PipelineHealth to include geo match watermark
-- =========================================================================

CREATE OR ALTER PROCEDURE dbo.usp_Dash_PipelineHealth
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE
        @RawRows        bigint,
        @ParsedRows     bigint,
        @DeviceRows     bigint,
        @IpRows         bigint,
        @VisitRows      bigint,
        @MatchRows      bigint,
        @MaxRawId       bigint,
        @MaxParsedSrcId bigint,
        @MaxVisitId     bigint,
        @MaxMatchId     bigint,
        -- Parse watermark
        @ParseWM        bigint,
        @ParseTotal     bigint,
        @ParseLastRun   datetime2,
        -- Email match watermark
        @EmailWM        bigint,
        @EmailTotal     bigint,
        @EmailMatched   bigint,
        @EmailLastRun   datetime2,
        -- Legacy IP match watermark
        @LegacyWM       bigint,
        @LegacyTotal    bigint,
        @LegacyMatched  bigint,
        @LegacyLastRun  datetime2,
        -- Geo match watermark
        @GeoWM          bigint,
        @GeoTotal       bigint,
        @GeoMatched     bigint,
        @GeoLastRun     datetime2,
        -- Match aggregates
        @Resolved       bigint,
        @Pending        bigint,
        @UniqueIndiv    bigint,
        @MatchLatest    datetime2,
        -- Timestamps
        @RawLatest      datetime2,
        @ParsedLatest   datetime2,
        @DeviceLatest   datetime,
        @IpLatest       datetime,
        @VisitLatest    datetime2,
        -- Lag
        @ParseLag       bigint,
        @EmailLag       bigint,
        @LegacyLag      bigint,
        @GeoLag         bigint,
        -- Match type breakdown
        @IpMatches      bigint,
        @EmailMatches   bigint,
        @IpResolved     bigint,
        @EmailResolved  bigint;

    -- DMV row counts (instant)
    SELECT @RawRows    = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Raw')    AND index_id IN (0,1);
    SELECT @ParsedRows = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Parsed') AND index_id IN (0,1);
    SELECT @DeviceRows = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Device') AND index_id IN (0,1);
    SELECT @IpRows     = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.IP')     AND index_id IN (0,1);
    SELECT @VisitRows  = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Visit')  AND index_id IN (0,1);
    SELECT @MatchRows  = ISNULL(SUM(row_count),0) FROM sys.dm_db_partition_stats WHERE object_id = OBJECT_ID('PiXL.Match')  AND index_id IN (0,1);

    SELECT @MaxRawId       = MAX(Id)       FROM PiXL.Raw    WITH (NOLOCK);
    SELECT @MaxParsedSrcId = MAX(SourceId) FROM PiXL.Parsed WITH (NOLOCK);
    SELECT @MaxVisitId     = MAX(VisitID)  FROM PiXL.Visit  WITH (NOLOCK);
    SELECT @MaxMatchId     = MAX(MatchId)  FROM PiXL.Match  WITH (NOLOCK);

    -- Parse watermark
    SELECT @ParseWM = LastProcessedId, @ParseTotal = RowsProcessed, @ParseLastRun = LastRunAt
    FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits';

    -- Email match watermark
    SELECT @EmailWM = LastProcessedId, @EmailTotal = RowsProcessed,
           @EmailMatched = RowsMatched, @EmailLastRun = LastRunAt
    FROM ETL.MatchWatermark WHERE ProcessName = 'MatchVisits';

    -- Legacy IP match watermark
    SELECT @LegacyWM = LastProcessedId, @LegacyTotal = RowsProcessed,
           @LegacyMatched = RowsMatched, @LegacyLastRun = LastRunAt
    FROM ETL.MatchWatermark WHERE ProcessName = 'MatchLegacyVisits';

    -- Geo match watermark
    SELECT @GeoWM = LastProcessedId, @GeoTotal = RowsProcessed,
           @GeoMatched = RowsMatched, @GeoLastRun = LastRunAt
    FROM ETL.MatchWatermark WHERE ProcessName = 'MatchGeoVisits';

    -- Match aggregates
    SELECT
        @Resolved    = SUM(CASE WHEN IndividualKey IS NOT NULL THEN 1 ELSE 0 END),
        @Pending     = SUM(CASE WHEN IndividualKey IS NULL     THEN 1 ELSE 0 END),
        @UniqueIndiv = APPROX_COUNT_DISTINCT(IndividualKey),
        @MatchLatest = MAX(LastSeen)
    FROM PiXL.Match WITH (NOLOCK);

    -- Match type breakdown
    SELECT
        @IpMatches     = SUM(CASE WHEN MatchType = 'ip'    THEN 1 ELSE 0 END),
        @EmailMatches  = SUM(CASE WHEN MatchType = 'email' THEN 1 ELSE 0 END),
        @IpResolved    = SUM(CASE WHEN MatchType = 'ip'    AND IndividualKey IS NOT NULL THEN 1 ELSE 0 END),
        @EmailResolved = SUM(CASE WHEN MatchType = 'email' AND IndividualKey IS NOT NULL THEN 1 ELSE 0 END)
    FROM PiXL.Match WITH (NOLOCK);

    -- Lag calculations
    SET @ParseLag  = ISNULL(@MaxRawId - @ParseWM, 0);
    SET @EmailLag  = ISNULL(@MaxVisitId - @EmailWM, 0);
    SET @LegacyLag = ISNULL(@MaxVisitId - @LegacyWM, 0);
    SET @GeoLag    = ISNULL(@MaxMatchId - @GeoWM, 0);

    -- Timestamps
    SELECT @RawLatest    = MAX(ReceivedAt) FROM PiXL.Raw    WITH (NOLOCK);
    SELECT @ParsedLatest = MAX(ReceivedAt) FROM PiXL.Parsed WITH (NOLOCK);
    SELECT @DeviceLatest = MAX(LastSeen)   FROM PiXL.Device WITH (NOLOCK);
    SELECT @IpLatest     = MAX(LastSeen)   FROM PiXL.IP     WITH (NOLOCK);
    SELECT @VisitLatest  = MAX(ReceivedAt) FROM PiXL.Visit  WITH (NOLOCK);

    SELECT
        @RawRows        AS RawRows,
        @ParsedRows     AS ParsedRows,
        @DeviceRows     AS DeviceRows,
        @IpRows         AS IpRows,
        @VisitRows      AS VisitRows,
        @MatchRows      AS MatchRows,
        @MaxRawId       AS MaxRawId,
        @MaxParsedSrcId AS MaxParsedSourceId,
        @MaxVisitId     AS MaxVisitId,
        @MaxMatchId     AS MaxMatchId,
        @ParseWM        AS ParseWatermark,
        @ParseTotal     AS ParseTotalProcessed,
        @ParseLastRun   AS ParseLastRunAt,
        @EmailWM        AS EmailMatchWatermark,
        @EmailTotal     AS EmailMatchProcessed,
        @EmailMatched   AS EmailMatchMatched,
        @EmailLastRun   AS EmailMatchLastRunAt,
        @LegacyWM       AS LegacyMatchWatermark,
        @LegacyTotal    AS LegacyMatchProcessed,
        @LegacyMatched  AS LegacyMatchMatched,
        @LegacyLastRun  AS LegacyMatchLastRunAt,
        @GeoWM          AS GeoMatchWatermark,
        @GeoTotal       AS GeoMatchProcessed,
        @GeoMatched     AS GeoMatchMatched,
        @GeoLastRun     AS GeoMatchLastRunAt,
        @Resolved       AS MatchesResolved,
        @Pending        AS MatchesPending,
        @UniqueIndiv    AS UniqueIndividuals,
        @IpMatches      AS IpMatchCount,
        @EmailMatches   AS EmailMatchCount,
        @IpResolved     AS IpResolved,
        @EmailResolved  AS EmailResolved,
        @ParseLag       AS ParseLag,
        @EmailLag       AS EmailMatchLag,
        @LegacyLag      AS LegacyMatchLag,
        @GeoLag         AS GeoMatchLag,
        @RawLatest      AS RawLatest,
        @ParsedLatest   AS ParsedLatest,
        @DeviceLatest   AS DeviceLatest,
        @IpLatest       AS IpLatest,
        @VisitLatest    AS VisitLatest,
        @MatchLatest    AS MatchLatest,
        (SELECT COUNT(DISTINCT IpId) FROM PiXL.Visit WITH (NOLOCK)) AS UniqueDevicesInVisits,
        (SELECT COUNT(DISTINCT IpId) FROM PiXL.Visit WITH (NOLOCK)) AS UniqueIpsInVisits;
END;
GO


-- =========================================================================
-- VERIFICATION
-- =========================================================================
PRINT '';
PRINT '=========================================================================';
PRINT ' 66_GeoSupplementalMatch.sql — COMPLETE';
PRINT '';
PRINT ' SCHEMA:';
PRINT '   PiXL.Company.MatchGeoSupplemental BIT (company default)';
PRINT '   PiXL.Settings.MatchGeoSupplemental BIT (per-pixl gate)';
PRINT '   Geo.LatLon (normalized lat/lon with spatial index)';
PRINT '   IX_AutoConsumer_LatLon_PPM (NCI for identity join)';
PRINT '';
PRINT ' ETL:';
PRINT '   ETL.usp_MatchGeoVisits (Phase 5 — geo proximity resolution)';
PRINT '   MatchGeoVisits watermark added';
PRINT '   usp_Dash_PipelineHealth updated with geo watermark';
PRINT '=========================================================================';

-- Quick verification
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='PiXL' AND TABLE_NAME='Company' AND COLUMN_NAME='MatchGeoSupplemental')
    PRINT 'OK: PiXL.Company.MatchGeoSupplemental';
IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='PiXL' AND TABLE_NAME='Settings' AND COLUMN_NAME='MatchGeoSupplemental')
    PRINT 'OK: PiXL.Settings.MatchGeoSupplemental';
IF OBJECT_ID('Geo.LatLon', 'U') IS NOT NULL
    PRINT 'OK: Geo.LatLon';
IF OBJECT_ID('ETL.usp_MatchGeoVisits', 'P') IS NOT NULL
    PRINT 'OK: ETL.usp_MatchGeoVisits';
IF EXISTS (SELECT 1 FROM ETL.MatchWatermark WHERE ProcessName = 'MatchGeoVisits')
    PRINT 'OK: MatchGeoVisits watermark';

SELECT
    (SELECT COUNT(*) FROM PiXL.Company WHERE MatchGeoSupplemental = 1) AS CompaniesEnabled,
    (SELECT COUNT(*) FROM PiXL.Settings WHERE MatchGeoSupplemental = 1) AS PixlsEnabled,
    (SELECT COUNT(*) FROM Geo.LatLon) AS GeoLatLonRows;
GO
