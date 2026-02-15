/*
    21_BackfillVisitDeviceIP.sql
    ============================
    One-time backfill: Processes existing PiXL.Parsed rows (that were parsed
    BEFORE Phases 9-13 existed) through the Device/IP/Visit pipeline.

    This script runs Phases 9-13 logic against ALL existing PiXL.Parsed rows
    that don't yet have a PiXL.Visit row.

    PREREQUISITES:
      - SQL/20_ETLPhases9to13.sql must have been run (proc updated)
      - PiXL.Device, PiXL.IP, PiXL.Visit tables must exist

    SAFE TO RE-RUN: Uses NOT EXISTS guard on Visit INSERT and MERGE on Device/IP.

    Run on: SmartPiXL database, localhost\SQL2025
    Date:   2026-02-15
*/

USE SmartPiXL;
GO

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
GO

PRINT '=== Backfill: Device + IP + Visit for existing PiXL.Parsed rows ===';
PRINT '';

DECLARE @StartTime DATETIME2 = SYSUTCDATETIME();

-- Build #BatchRows from ALL existing Parsed rows that don't have a Visit yet
CREATE TABLE #BatchRows (
    SourceId        INT             NOT NULL  PRIMARY KEY,
    CompanyID       INT             NULL,
    PiXLID          INT             NULL,
    IPAddress       VARCHAR(50)     NULL,
    ReceivedAt      DATETIME2(3)    NOT NULL,
    DeviceHash      VARBINARY(32)   NULL,
    DeviceId        BIGINT          NULL,
    IpId            BIGINT          NULL,
    ClientParamsJson JSON           NULL,
    MatchEmail      NVARCHAR(200)   NULL
);

INSERT INTO #BatchRows (SourceId, CompanyID, PiXLID, IPAddress, ReceivedAt, DeviceHash)
SELECT
    p.SourceId,
    TRY_CAST(p.CompanyID AS INT),
    TRY_CAST(p.PiXLID AS INT),
    p.IPAddress,
    p.ReceivedAt,
    CASE
        WHEN COALESCE(p.CanvasFingerprint, p.DetectedFonts, p.GPURenderer,
                      p.WebGLFingerprint, p.AudioFingerprintHash) IS NOT NULL
        THEN HASHBYTES('SHA2_256',
            CONCAT_WS('|',
                p.CanvasFingerprint, p.DetectedFonts, p.GPURenderer,
                p.WebGLFingerprint, p.AudioFingerprintHash))
        ELSE NULL
    END
FROM PiXL.Parsed p
WHERE NOT EXISTS (SELECT 1 FROM PiXL.Visit v WHERE v.VisitID = p.SourceId);

DECLARE @BatchCount INT = @@ROWCOUNT;
PRINT '  Loaded ' + CAST(@BatchCount AS VARCHAR(10)) + ' rows into #BatchRows';

IF @BatchCount = 0
BEGIN
    PRINT '  No rows to backfill — all Parsed rows already have Visit rows.';
    DROP TABLE #BatchRows;
    RETURN;
END

-- Stats
DECLARE @HasDeviceHash INT, @HasIPAddress INT, @HasValidCompany INT;
SELECT @HasDeviceHash = SUM(CASE WHEN DeviceHash IS NOT NULL THEN 1 ELSE 0 END),
       @HasIPAddress = SUM(CASE WHEN IPAddress IS NOT NULL THEN 1 ELSE 0 END),
       @HasValidCompany = SUM(CASE WHEN CompanyID IS NOT NULL AND PiXLID IS NOT NULL THEN 1 ELSE 0 END)
FROM #BatchRows;

PRINT '  DeviceHash computed: ' + CAST(@HasDeviceHash AS VARCHAR(10));
PRINT '  IPAddress present:   ' + CAST(@HasIPAddress AS VARCHAR(10));
PRINT '  Valid CompanyID:     ' + CAST(@HasValidCompany AS VARCHAR(10));
PRINT '';


-- =====================================================================
-- MERGE PiXL.Device
-- =====================================================================
PRINT '  MERGE PiXL.Device...';

MERGE PiXL.Device AS target
USING (
    SELECT DeviceHash,
           MIN(ReceivedAt) AS BatchFirstSeen,
           MAX(ReceivedAt) AS BatchLastSeen,
           COUNT(*)        AS BatchHitCount
    FROM #BatchRows
    WHERE DeviceHash IS NOT NULL
    GROUP BY DeviceHash
) AS source ON target.DeviceHash = source.DeviceHash

WHEN MATCHED THEN UPDATE SET
    LastSeen = CASE WHEN source.BatchLastSeen > target.LastSeen
                    THEN source.BatchLastSeen ELSE target.LastSeen END,
    HitCount = target.HitCount + source.BatchHitCount

WHEN NOT MATCHED THEN INSERT (DeviceHash, FirstSeen, LastSeen, HitCount)
    VALUES (source.DeviceHash, source.BatchFirstSeen, source.BatchLastSeen, source.BatchHitCount);

PRINT '    Devices upserted: ' + CAST(@@ROWCOUNT AS VARCHAR(10));

-- Resolve DeviceIds
UPDATE b SET b.DeviceId = d.DeviceId
FROM #BatchRows b
JOIN PiXL.Device d ON b.DeviceHash = d.DeviceHash
WHERE b.DeviceHash IS NOT NULL;


-- =====================================================================
-- MERGE PiXL.IP
-- =====================================================================
PRINT '  MERGE PiXL.IP...';

MERGE PiXL.IP AS target
USING (
    SELECT IPAddress,
           MIN(ReceivedAt) AS BatchFirstSeen,
           MAX(ReceivedAt) AS BatchLastSeen,
           COUNT(*)        AS BatchHitCount
    FROM #BatchRows
    WHERE IPAddress IS NOT NULL
    GROUP BY IPAddress
) AS source ON target.IPAddress = source.IPAddress

WHEN MATCHED THEN UPDATE SET
    LastSeen = CASE WHEN source.BatchLastSeen > target.LastSeen
                    THEN source.BatchLastSeen ELSE target.LastSeen END,
    HitCount = target.HitCount + source.BatchHitCount

WHEN NOT MATCHED THEN INSERT (IPAddress, FirstSeen, LastSeen, HitCount)
    VALUES (source.IPAddress, source.BatchFirstSeen, source.BatchLastSeen, source.BatchHitCount);

PRINT '    IPs upserted: ' + CAST(@@ROWCOUNT AS VARCHAR(10));

-- Resolve IpIds
UPDATE b SET b.IpId = ip.IpId
FROM #BatchRows b
JOIN PiXL.IP ip ON b.IPAddress = ip.IPAddress
WHERE b.IPAddress IS NOT NULL;


-- =====================================================================
-- Extract _cp_* Client Parameters
-- =====================================================================
PRINT '  Extracting _cp_* client parameters...';

UPDATE b SET
    b.ClientParamsJson = cp.JsonObj,
    b.MatchEmail = JSON_VALUE(cp.JsonObj, '$.email')
FROM #BatchRows b
OUTER APPLY (
    SELECT JSON_OBJECTAGG(
        SUBSTRING(s.value, 5, CHARINDEX('=', s.value + '=') - 5)
        :
        dbo.GetQueryParam(t.QueryString, SUBSTRING(s.value, 1, CHARINDEX('=', s.value + '=') - 1))
    ) AS JsonObj
    FROM PiXL.Test t
    CROSS APPLY STRING_SPLIT(t.QueryString, '&') s
    WHERE t.Id = b.SourceId
      AND s.value LIKE '[_]cp[_]%=_%'
) cp
WHERE cp.JsonObj IS NOT NULL;

DECLARE @CpRows INT = @@ROWCOUNT;
PRINT '    Rows with _cp_ params: ' + CAST(@CpRows AS VARCHAR(10));


-- =====================================================================
-- INSERT PiXL.Visit
-- =====================================================================
PRINT '  INSERT PiXL.Visit...';

INSERT INTO PiXL.Visit (VisitID, CompanyID, PiXLID, DeviceId, IpId,
                         ReceivedAt, ClientParamsJson, MatchEmail)
SELECT b.SourceId, b.CompanyID, b.PiXLID, b.DeviceId, b.IpId,
       b.ReceivedAt, b.ClientParamsJson, b.MatchEmail
FROM #BatchRows b
WHERE b.CompanyID IS NOT NULL
  AND b.PiXLID IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM PiXL.Visit v WHERE v.VisitID = b.SourceId);

DECLARE @VisitsInserted INT = @@ROWCOUNT;
PRINT '    Visits inserted: ' + CAST(@VisitsInserted AS VARCHAR(10));


-- =====================================================================
-- Summary
-- =====================================================================
DROP TABLE #BatchRows;

DECLARE @Elapsed INT = DATEDIFF(MILLISECOND, @StartTime, SYSUTCDATETIME());
DECLARE @DeviceCount INT, @IpCount INT, @VisitCount INT;
SELECT @DeviceCount = COUNT(*) FROM PiXL.Device;
SELECT @IpCount = COUNT(*) FROM PiXL.IP;
SELECT @VisitCount = COUNT(*) FROM PiXL.Visit;

PRINT '';
PRINT '=== Backfill Complete ===';
PRINT '  Duration: ' + CAST(@Elapsed AS VARCHAR(10)) + ' ms';
PRINT '  Devices in PiXL.Device: ' + CAST(@DeviceCount AS VARCHAR(10));
PRINT '  IPs in PiXL.IP:         ' + CAST(@IpCount AS VARCHAR(10));
PRINT '  Visits in PiXL.Visit:   ' + CAST(@VisitCount AS VARCHAR(10));
GO
