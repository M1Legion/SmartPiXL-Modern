-- ============================================================================
-- Migration 47: Graph Tables for Identity Resolution (Phase 7)
-- ============================================================================
-- Creates SQL Server 2025 graph tables (NODE + EDGE) in the Graph schema
-- for multi-hop identity resolution chains:
--
--   Person → (ResolvesTo) ← Device → (UsesIP) → IpAddress
--
-- Enables MATCH-based traversal queries:
--   "Starting from this Person, find every Device they used, every IP
--    those Devices touched, and every OTHER Person who used those IPs."
--
-- Design doc §8.3 item 10: "Graph Tables for Identity Resolution Chains"
--
-- Population: ETL step inserts from PiXL.Visit + PiXL.Match data.
-- ============================================================================
PRINT '--- 47: Creating graph tables for identity resolution ---';
GO

USE SmartPiXL;
GO

-- =====================================================================
-- Step 1: Create Graph schema
-- =====================================================================
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'Graph')
BEGIN
    EXEC('CREATE SCHEMA Graph');
    PRINT 'Schema [Graph] created.';
END
ELSE
    PRINT 'Schema [Graph] already exists.';
GO

-- =====================================================================
-- Step 2: Create NODE tables
-- =====================================================================

-- Graph.Device — one node per unique device
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Device' AND schema_id = SCHEMA_ID('Graph'))
BEGIN
    CREATE TABLE Graph.Device (
        DeviceId    BIGINT        NOT NULL,
        DeviceHash  VARBINARY(32) NOT NULL
    ) AS NODE;

    CREATE UNIQUE INDEX UX_Graph_Device_DeviceId
        ON Graph.Device (DeviceId);
    CREATE INDEX IX_Graph_Device_DeviceHash
        ON Graph.Device (DeviceHash);

    PRINT 'Graph.Device NODE table created.';
END
ELSE
    PRINT 'Graph.Device already exists.';
GO

-- Graph.Person — one node per resolved identity (email or IndividualKey)
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Person' AND schema_id = SCHEMA_ID('Graph'))
BEGIN
    CREATE TABLE Graph.Person (
        Email          VARCHAR(256)  NULL,
        IndividualKey  VARCHAR(35)   NULL
    ) AS NODE;

    CREATE INDEX IX_Graph_Person_Email
        ON Graph.Person (Email);
    CREATE INDEX IX_Graph_Person_IndividualKey
        ON Graph.Person (IndividualKey);

    PRINT 'Graph.Person NODE table created.';
END
ELSE
    PRINT 'Graph.Person already exists.';
GO

-- Graph.IpAddress — one node per unique IP
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'IpAddress' AND schema_id = SCHEMA_ID('Graph'))
BEGIN
    CREATE TABLE Graph.IpAddress (
        IP        VARCHAR(50)   NOT NULL,
        Subnet24  VARCHAR(18)   NULL
    ) AS NODE;

    CREATE INDEX IX_Graph_IpAddress_IP
        ON Graph.IpAddress (IP);
    CREATE INDEX IX_Graph_IpAddress_Subnet24
        ON Graph.IpAddress (Subnet24);

    PRINT 'Graph.IpAddress NODE table created.';
END
ELSE
    PRINT 'Graph.IpAddress already exists.';
GO

-- =====================================================================
-- Step 3: Create EDGE tables
-- =====================================================================

-- Graph.UsesIP — Device --uses--> IpAddress
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'UsesIP' AND schema_id = SCHEMA_ID('Graph'))
BEGIN
    CREATE TABLE Graph.UsesIP (
        FirstSeen   DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        LastSeen    DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        HitCount    INT          NOT NULL DEFAULT 1
    ) AS EDGE;

    PRINT 'Graph.UsesIP EDGE table created.';
END
ELSE
    PRINT 'Graph.UsesIP already exists.';
GO

-- Graph.ResolvesTo — Device --resolves_to--> Person
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ResolvesTo' AND schema_id = SCHEMA_ID('Graph'))
BEGIN
    CREATE TABLE Graph.ResolvesTo (
        MatchType    VARCHAR(30) NULL,
        Confidence   DECIMAL(5,2) NULL,
        MatchedAt    DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()
    ) AS EDGE;

    PRINT 'Graph.ResolvesTo EDGE table created.';
END
ELSE
    PRINT 'Graph.ResolvesTo already exists.';
GO

-- =====================================================================
-- Step 4: Insert test data and validate MATCH traversal
-- =====================================================================
PRINT '';
PRINT '=== Graph Traversal Validation ===';

-- Build a small test graph:
--   Person(alice@test.com) ←resolves── Device(D1) ──uses──> IP(10.0.0.1)
--                                      Device(D1) ──uses──> IP(10.0.0.2)
--   Person(bob@test.com)  ←resolves── Device(D2) ──uses──> IP(10.0.0.2)
-- This means Alice and Bob share IP 10.0.0.2 via different devices.

-- Insert nodes
INSERT INTO Graph.Device (DeviceId, DeviceHash)
VALUES (999901, 0x01), (999902, 0x02);

INSERT INTO Graph.Person (Email, IndividualKey)
VALUES ('alice@test.com', 'TEST-ALICE'), ('bob@test.com', 'TEST-BOB');

INSERT INTO Graph.IpAddress (IP, Subnet24)
VALUES ('10.0.0.1', '10.0.0.0/24'), ('10.0.0.2', '10.0.0.0/24');

-- Insert edges: D1 resolves to Alice, D2 resolves to Bob
INSERT INTO Graph.ResolvesTo ($from_id, $to_id, MatchType, Confidence)
SELECT d.$node_id, p.$node_id, 'AutoConsumer', 95.0
FROM Graph.Device d, Graph.Person p
WHERE d.DeviceId = 999901 AND p.Email = 'alice@test.com';

INSERT INTO Graph.ResolvesTo ($from_id, $to_id, MatchType, Confidence)
SELECT d.$node_id, p.$node_id, 'AutoConsumer', 90.0
FROM Graph.Device d, Graph.Person p
WHERE d.DeviceId = 999902 AND p.Email = 'bob@test.com';

-- Insert edges: D1 uses IP1 and IP2, D2 uses IP2
INSERT INTO Graph.UsesIP ($from_id, $to_id, HitCount)
SELECT d.$node_id, ip.$node_id, 5
FROM Graph.Device d, Graph.IpAddress ip
WHERE d.DeviceId = 999901 AND ip.IP = '10.0.0.1';

INSERT INTO Graph.UsesIP ($from_id, $to_id, HitCount)
SELECT d.$node_id, ip.$node_id, 3
FROM Graph.Device d, Graph.IpAddress ip
WHERE d.DeviceId = 999901 AND ip.IP = '10.0.0.2';

INSERT INTO Graph.UsesIP ($from_id, $to_id, HitCount)
SELECT d.$node_id, ip.$node_id, 7
FROM Graph.Device d, Graph.IpAddress ip
WHERE d.DeviceId = 999902 AND ip.IP = '10.0.0.2';
GO

-- Validate: Find all IPs used by Alice's devices
SELECT
    p.Email,
    d.DeviceId,
    ip.IP
FROM Graph.Person p,
     Graph.ResolvesTo rt,
     Graph.Device d,
     Graph.UsesIP ui,
     Graph.IpAddress ip
WHERE MATCH(p<-(rt)-d-(ui)->ip)
  AND p.Email = 'alice@test.com';
GO

-- Validate: Find people who share IPs with Alice (identity resolution chain)
-- Person → Device → IP ← Device ← Person (2-hop)
SELECT DISTINCT
    p1.Email AS SourcePerson,
    ip.IP AS SharedIP,
    p2.Email AS LinkedPerson
FROM Graph.Person p1,
     Graph.ResolvesTo rt1,
     Graph.Device d1,
     Graph.UsesIP ui1,
     Graph.IpAddress ip,
     Graph.UsesIP ui2,
     Graph.Device d2,
     Graph.ResolvesTo rt2,
     Graph.Person p2
WHERE MATCH(p1<-(rt1)-d1-(ui1)->ip<-(ui2)-d2-(rt2)->p2)
  AND p1.Email = 'alice@test.com'
  AND p1.Email <> p2.Email;
GO

-- Clean up test data
DELETE FROM Graph.UsesIP;
DELETE FROM Graph.ResolvesTo;
DELETE FROM Graph.IpAddress WHERE IP IN ('10.0.0.1', '10.0.0.2');
DELETE FROM Graph.Person WHERE Email IN ('alice@test.com', 'bob@test.com');
DELETE FROM Graph.Device WHERE DeviceId IN (999901, 999902);
GO

PRINT 'Graph test data cleaned up.';
PRINT '';
PRINT '=== Migration 47 complete ===';
GO
