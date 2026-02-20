-- ============================================================================
-- Migration 48: Subnet Reputation Table + Daily Aggregation Proc (Phase 8)
-- ============================================================================
-- Creates PiXL.SubnetReputation — materialized /24 subnet-level reputation
-- scores aggregated from all historical traffic data. Updated daily by
-- ETL.usp_UpdateSubnetReputation.
--
-- The Forge checks this table in real-time: "this IP is from a subnet
-- with 87% bot rate across 6 months."
--
-- Uses dbo.GetSubnet24() CLR function (deployed in migration 45).
--
-- Design doc reference: §8.3 item 2 (Subnet Reputation Scoring)
-- ============================================================================
USE SmartPiXL;
GO

SET NOCOUNT ON;
GO

PRINT '--- 48: Subnet Reputation Table + Daily Aggregation ---';
GO

-- =====================================================================
-- Step 1: Create PiXL.SubnetReputation table
-- =====================================================================
IF OBJECT_ID('PiXL.SubnetReputation', 'U') IS NULL
BEGIN
    CREATE TABLE PiXL.SubnetReputation
    (
        SubnetReputationId  INT             NOT NULL IDENTITY(1,1),
        Subnet24            VARCHAR(18)     NOT NULL,   -- e.g. '192.168.1.0/24'
        UniqueIPs           INT             NOT NULL DEFAULT 0,
        UniqueDevices       INT             NOT NULL DEFAULT 0,
        TotalHits           BIGINT          NOT NULL DEFAULT 0,
        AvgBotScore         DECIMAL(5,2)    NULL,
        BotPercent          DECIMAL(5,2)    NULL,       -- % of hits with BotScore >= 50
        AvgAnomalyScore     DECIMAL(5,2)    NULL,
        FirstSeen           DATETIME2(3)    NOT NULL DEFAULT SYSUTCDATETIME(),
        LastSeen            DATETIME2(3)    NOT NULL DEFAULT SYSUTCDATETIME(),
        LastUpdated         DATETIME2(3)    NOT NULL DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_PiXL_SubnetReputation PRIMARY KEY NONCLUSTERED (SubnetReputationId),
        CONSTRAINT UQ_PiXL_SubnetReputation_Subnet UNIQUE CLUSTERED (Subnet24)
    );

    PRINT '  Created PiXL.SubnetReputation';
END
ELSE
    PRINT '  PiXL.SubnetReputation already exists — skipped';
GO

-- =====================================================================
-- Step 2: Index for Forge lookups — high bot-rate subnets
-- =====================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('PiXL.SubnetReputation')
      AND name = 'IX_SubnetReputation_BotPercent'
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_SubnetReputation_BotPercent
        ON PiXL.SubnetReputation (BotPercent DESC)
        INCLUDE (UniqueIPs, UniqueDevices, TotalHits, AvgBotScore)
        WHERE BotPercent >= 50;

    PRINT '  Created filtered index IX_SubnetReputation_BotPercent (BotPercent >= 50)';
END
GO

-- =====================================================================
-- Step 3: Create ETL.usp_UpdateSubnetReputation
-- =====================================================================
-- Daily aggregation proc. Computes subnet-level statistics from
-- PiXL.Parsed joined to PiXL.IP and PiXL.Visit, using dbo.GetSubnet24()
-- CLR function for subnet extraction.
--
-- Uses MERGE to insert new subnets and update existing ones.
-- =====================================================================
SET QUOTED_IDENTIFIER ON;
GO
CREATE OR ALTER PROCEDURE ETL.usp_UpdateSubnetReputation
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @StartTime DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @RowsMerged INT = 0;

    -- Aggregate subnet reputation from all parsed data
    -- Uses PiXL.IP for the IP→subnet mapping (avoids re-parsing)
    ;WITH SubnetAgg AS (
        SELECT
            dbo.GetSubnet24(ip.IPAddress)   AS Subnet24,
            COUNT(DISTINCT ip.IPAddress)    AS UniqueIPs,
            COUNT(DISTINCT v.DeviceId)      AS UniqueDevices,
            COUNT_BIG(*)                    AS TotalHits,
            AVG(CAST(p.BotScore AS DECIMAL(5,2)))   AS AvgBotScore,
            CASE
                WHEN COUNT_BIG(*) = 0 THEN 0
                ELSE CAST(
                    100.0 * SUM(CASE WHEN p.BotScore >= 50 THEN 1 ELSE 0 END)
                    / COUNT_BIG(*) AS DECIMAL(5,2))
            END                             AS BotPercent,
            AVG(CAST(p.AnomalyScore AS DECIMAL(5,2))) AS AvgAnomalyScore,
            MIN(v.ReceivedAt)               AS FirstSeen,
            MAX(v.ReceivedAt)               AS LastSeen
        FROM PiXL.Visit v
        JOIN PiXL.IP ip ON v.IpId = ip.IpId
        JOIN PiXL.Parsed p ON v.VisitID = p.SourceId
        WHERE ip.IPAddress IS NOT NULL
          AND dbo.GetSubnet24(ip.IPAddress) IS NOT NULL
        GROUP BY dbo.GetSubnet24(ip.IPAddress)
        HAVING COUNT_BIG(*) >= 5            -- Minimum 5 hits for statistical relevance
    )
    MERGE PiXL.SubnetReputation AS target
    USING SubnetAgg AS source
        ON target.Subnet24 = source.Subnet24

    WHEN MATCHED THEN UPDATE SET
        UniqueIPs       = source.UniqueIPs,
        UniqueDevices   = source.UniqueDevices,
        TotalHits       = source.TotalHits,
        AvgBotScore     = source.AvgBotScore,
        BotPercent      = source.BotPercent,
        AvgAnomalyScore = source.AvgAnomalyScore,
        FirstSeen       = source.FirstSeen,
        LastSeen        = source.LastSeen,
        LastUpdated     = SYSUTCDATETIME()

    WHEN NOT MATCHED THEN INSERT (
        Subnet24, UniqueIPs, UniqueDevices, TotalHits,
        AvgBotScore, BotPercent, AvgAnomalyScore,
        FirstSeen, LastSeen, LastUpdated
    )
    VALUES (
        source.Subnet24, source.UniqueIPs, source.UniqueDevices, source.TotalHits,
        source.AvgBotScore, source.BotPercent, source.AvgAnomalyScore,
        source.FirstSeen, source.LastSeen, SYSUTCDATETIME()
    );

    SET @RowsMerged = @@ROWCOUNT;

    SELECT @RowsMerged AS SubnetsMerged,
           DATEDIFF(MILLISECOND, @StartTime, SYSUTCDATETIME()) AS ElapsedMs;
END;
GO

-- =====================================================================
-- Step 4: Verification
-- =====================================================================
IF OBJECT_ID('PiXL.SubnetReputation', 'U') IS NOT NULL
    PRINT '  OK: PiXL.SubnetReputation exists';
ELSE
    PRINT '  ERROR: PiXL.SubnetReputation missing!';

IF OBJECT_ID('ETL.usp_UpdateSubnetReputation', 'P') IS NOT NULL
    PRINT '  OK: ETL.usp_UpdateSubnetReputation exists';
ELSE
    PRINT '  ERROR: ETL.usp_UpdateSubnetReputation missing!';
GO

PRINT '--- 48: SubnetReputation complete ---';
GO
