-- ============================================================================
-- 73_WhoisCache.sql — Persistent WHOIS lookup cache in SQL
--
-- Stores WHOIS ASN/Org results so Forge can pre-warm its in-memory cache
-- on startup instead of re-querying external WHOIS servers (5s per IP).
--
-- DESIGN:
--   - Background workers write results after each fresh WHOIS query
--   - On startup, Forge loads recent entries to pre-warm BoundedCache
--   - Periodic cleanup removes entries older than 30 days
--   - MERGE handles concurrent upserts from multiple workers
-- ============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'WhoisCache' AND schema_id = SCHEMA_ID('IPAPI'))
BEGIN
    CREATE TABLE IPAPI.WhoisCache
    (
        IPAddress       VARCHAR(45)     NOT NULL,   -- IPv4 or IPv6
        Asn             VARCHAR(50)     NULL,       -- e.g. "AS16509"
        Organization    VARCHAR(200)    NULL,       -- e.g. "Amazon.com, Inc."
        LookedUpAt      DATETIME2(0)    NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_WhoisCache PRIMARY KEY CLUSTERED (IPAddress)
    );

    CREATE NONCLUSTERED INDEX IX_WhoisCache_LookedUpAt
        ON IPAPI.WhoisCache (LookedUpAt)
        INCLUDE (IPAddress, Asn, Organization);
END;
GO

-- Load recent cache entries for startup pre-warm (last 30 days)
CREATE OR ALTER PROCEDURE IPAPI.usp_WhoisCache_Load
    @MaxAgeDays INT = 30
AS
BEGIN
    SET NOCOUNT ON;

    SELECT IPAddress, Asn, Organization
    FROM IPAPI.WhoisCache
    WHERE LookedUpAt >= DATEADD(DAY, -@MaxAgeDays, SYSUTCDATETIME());
END;
GO

-- Upsert a single WHOIS result (called after each fresh external query)
CREATE OR ALTER PROCEDURE IPAPI.usp_WhoisCache_Upsert
    @IPAddress      VARCHAR(45),
    @Asn            VARCHAR(50),
    @Organization   VARCHAR(200)
AS
BEGIN
    SET NOCOUNT ON;

    MERGE IPAPI.WhoisCache AS target
    USING (SELECT @IPAddress AS IPAddress) AS source
    ON target.IPAddress = source.IPAddress
    WHEN MATCHED THEN
        UPDATE SET Asn = @Asn, Organization = @Organization, LookedUpAt = SYSUTCDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (IPAddress, Asn, Organization, LookedUpAt)
        VALUES (@IPAddress, @Asn, @Organization, SYSUTCDATETIME());
END;
GO

-- Cleanup old entries (called by maintenance scheduler)
CREATE OR ALTER PROCEDURE IPAPI.usp_WhoisCache_Cleanup
    @MaxAgeDays INT = 30
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @deleted INT;

    DELETE FROM IPAPI.WhoisCache
    WHERE LookedUpAt < DATEADD(DAY, -@MaxAgeDays, SYSUTCDATETIME());

    SET @deleted = @@ROWCOUNT;

    SELECT @deleted AS DeletedCount;
END;
GO
