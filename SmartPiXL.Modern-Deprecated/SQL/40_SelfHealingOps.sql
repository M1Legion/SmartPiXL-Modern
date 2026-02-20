-- ============================================================================
-- 40_SelfHealingOps.sql — Ops schema for self-healing remediation log
--
-- Creates the Ops schema and RemediationLog table used by
-- SelfHealingService to track detected issues, auto-executed fixes,
-- and pending operator approvals.
--
-- Usage:
--   sqlcmd -S "localhost\SQL2025" -d "SmartPiXL" -i "40_SelfHealingOps.sql"
-- ============================================================================

SET NOCOUNT ON;

-- Create Ops schema if it doesn't exist
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'Ops')
    EXEC('CREATE SCHEMA Ops');
GO

-- RemediationLog — tracks detected issues and remediation actions
IF OBJECT_ID('Ops.RemediationLog', 'U') IS NULL
BEGIN
    CREATE TABLE Ops.RemediationLog
    (
        Id                  INT IDENTITY(1,1)   NOT NULL,
        DetectedAtUtc       DATETIME2(3)        NOT NULL DEFAULT SYSUTCDATETIME(),
        IssueType           NVARCHAR(50)        NOT NULL,   -- e.g., 'PrimaryFilegroupFull', 'DiskFull', 'EtlLagHigh', 'DeadlockRetryExhausted'
        Severity            NVARCHAR(20)        NOT NULL,   -- 'Info', 'Warning', 'Critical'
        Description         NVARCHAR(500)       NOT NULL,
        RecommendedAction   NVARCHAR(1000)      NULL,
        ActionSql           NVARCHAR(MAX)       NULL,       -- Exact SQL to execute if approved (nullable for info-only entries)
        Status              NVARCHAR(20)        NOT NULL DEFAULT 'Pending',  -- Pending, Executed, Skipped, Failed
        ExecutedAtUtc       DATETIME2(3)        NULL,
        ExecutedBy          NVARCHAR(100)       NULL,       -- 'auto', 'scheduler', 'operator'
        ResultMessage       NVARCHAR(1000)      NULL,

        CONSTRAINT PK_Ops_RemediationLog PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT CK_Ops_RemediationLog_Severity CHECK (Severity IN ('Info', 'Warning', 'Critical')),
        CONSTRAINT CK_Ops_RemediationLog_Status CHECK (Status IN ('Pending', 'Executed', 'Skipped', 'Failed'))
    );

    -- Dashboard query: recent remediation actions
    CREATE NONCLUSTERED INDEX IX_Ops_RemediationLog_Status_Detected
        ON Ops.RemediationLog (Status, DetectedAtUtc DESC)
        INCLUDE (IssueType, Severity, Description, ExecutedBy);

    -- De-duplication check: avoid logging the same issue type repeatedly
    CREATE NONCLUSTERED INDEX IX_Ops_RemediationLog_IssueType_Status
        ON Ops.RemediationLog (IssueType, Status, DetectedAtUtc DESC);

    PRINT 'Created Ops.RemediationLog table with indexes.';
END
ELSE
    PRINT 'Ops.RemediationLog already exists — skipping.';
GO
