-- ============================================================================
-- 85_DesignTreeUpgrade.sql
-- ----------------------------------------------------------------------------
-- Phase 1 upgrade of the Health Tree into the authoritative Design Tree.
--
-- Extends Health.Node with:
--     * Lifecycle (planned/active/deprecated/removed)
--     * Owner, Icon
--     * ProbeBinding JSON  (how to resolve the live health for this node)
--     * HealthRules  JSON  (how to interpret metrics: expected, bands, units)
--     * DescMarketing / DescManagement / DescDeveloper — three-audience copy
--     * CreatedAt / UpdatedAt audit columns
--
-- Creates sibling tables in new Design schema:
--     * Design.CodeLink  — every node claims the repo paths it owns
--     * Design.Decision  — locked architectural decisions, anchored to nodes
--
-- Idempotent. Safe to re-run. Existing Metadata JSON is migrated into the
-- new typed columns; the column itself is kept around for one cycle.
--
-- Run: sqlcmd -S localhost\SQL2025 -d SmartPiXL -E -i SmartPiXL/SQL/85_DesignTreeUpgrade.sql
-- ============================================================================

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- ---------------------------------------------------------------------------
-- 1. Design schema
-- ---------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'Design')
    EXEC('CREATE SCHEMA Design');
GO

-- ---------------------------------------------------------------------------
-- 2. Health.Node column additions (all idempotent)
-- ---------------------------------------------------------------------------
IF COL_LENGTH('Health.Node', 'Lifecycle') IS NULL
    ALTER TABLE Health.Node ADD Lifecycle NVARCHAR(20) NOT NULL
        CONSTRAINT DF_Health_Node_Lifecycle DEFAULT ('active');
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Health_Node_Lifecycle'
)
    ALTER TABLE Health.Node ADD CONSTRAINT CK_Health_Node_Lifecycle
        CHECK (Lifecycle IN ('planned','active','deprecated','removed'));
GO

IF COL_LENGTH('Health.Node', 'Owner') IS NULL
    ALTER TABLE Health.Node ADD [Owner] NVARCHAR(100) NULL;
GO

IF COL_LENGTH('Health.Node', 'Icon') IS NULL
    ALTER TABLE Health.Node ADD Icon NVARCHAR(50) NULL;
GO

IF COL_LENGTH('Health.Node', 'ProbeBinding') IS NULL
    ALTER TABLE Health.Node ADD ProbeBinding NVARCHAR(MAX) NULL;
GO

IF COL_LENGTH('Health.Node', 'HealthRules') IS NULL
    ALTER TABLE Health.Node ADD HealthRules NVARCHAR(MAX) NULL;
GO

IF COL_LENGTH('Health.Node', 'DescMarketing') IS NULL
    ALTER TABLE Health.Node ADD DescMarketing NVARCHAR(MAX) NULL;
GO

IF COL_LENGTH('Health.Node', 'DescManagement') IS NULL
    ALTER TABLE Health.Node ADD DescManagement NVARCHAR(MAX) NULL;
GO

IF COL_LENGTH('Health.Node', 'DescDeveloper') IS NULL
    ALTER TABLE Health.Node ADD DescDeveloper NVARCHAR(MAX) NULL;
GO

IF COL_LENGTH('Health.Node', 'CreatedAt') IS NULL
    ALTER TABLE Health.Node ADD CreatedAt DATETIME2(3) NOT NULL
        CONSTRAINT DF_Health_Node_CreatedAt DEFAULT (SYSUTCDATETIME());
GO

IF COL_LENGTH('Health.Node', 'UpdatedAt') IS NULL
    ALTER TABLE Health.Node ADD UpdatedAt DATETIME2(3) NOT NULL
        CONSTRAINT DF_Health_Node_UpdatedAt DEFAULT (SYSUTCDATETIME());
GO

-- ---------------------------------------------------------------------------
-- 3. Migrate existing Metadata JSON into typed columns
--    Source shape:  {"source":"forge","sourceProbeName":"...","sourceSubsystem":"...","healthFunction":"...","icon":"..."}
--    Targets:
--        ProbeBinding  <- {"kind":"<source>Health","subsystem":"...","probeName":"..."} (for edge/forge/sentinel probes)
--        Icon          <- metadata.icon
--        DescDeveloper <- metadata.healthFunction (fallback to Description)
-- ---------------------------------------------------------------------------
UPDATE Health.Node
SET Icon = COALESCE(Icon, JSON_VALUE(Metadata, '$.icon'))
WHERE Metadata IS NOT NULL
  AND JSON_VALUE(Metadata, '$.icon') IS NOT NULL;
GO

-- Build ProbeBinding for probe-tier nodes that have a source
UPDATE Health.Node
SET ProbeBinding = (
    SELECT
        CASE JSON_VALUE(Metadata, '$.source')
             WHEN 'edge'     THEN 'edgeHealth'
             WHEN 'forge'    THEN 'forgeHealth'
             WHEN 'sentinel' THEN 'sentinelLocal'
             ELSE JSON_VALUE(Metadata, '$.source')
        END                                                  AS [kind],
        JSON_VALUE(Metadata, '$.sourceProbeName')            AS [probeName],
        JSON_VALUE(Metadata, '$.sourceSubsystem')            AS [subsystem]
    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES
)
WHERE NodeType = 'probe'
  AND Metadata IS NOT NULL
  AND JSON_VALUE(Metadata, '$.source') IS NOT NULL
  AND ProbeBinding IS NULL;
GO

-- Seed developer-facing description from healthFunction (or base Description) where empty
UPDATE Health.Node
SET DescDeveloper = COALESCE(DescDeveloper, JSON_VALUE(Metadata, '$.healthFunction'), Description)
WHERE DescDeveloper IS NULL
  AND (JSON_VALUE(Metadata, '$.healthFunction') IS NOT NULL OR Description IS NOT NULL);
GO

-- ---------------------------------------------------------------------------
-- 4. Seed default binary HealthRules for every probe that has none.
--    Shape:
--    {
--      "metrics":[
--        {
--          "key":"up","label":"Health","unit":"",
--          "source":{"kind":"aggregate"},
--          "direction":"higherIsBetter","expected":1,
--          "bands":{"green":"=1","red":"=0"},
--          "description":"<healthFunction | Description>"
--        }
--      ]
--    }
-- ---------------------------------------------------------------------------
-- Note: JSON_QUERY() wraps nested FOR JSON so MSSQL embeds them as JSON
-- objects rather than stringifying them as text values.
UPDATE Health.Node
SET HealthRules = (
    SELECT
        JSON_QUERY((SELECT
            'up'                                                   AS [key],
            'Health'                                               AS [label],
            ''                                                     AS [unit],
            JSON_QUERY((SELECT 'aggregate' AS [kind]
                           FOR JSON PATH, WITHOUT_ARRAY_WRAPPER))  AS [source],
            'higherIsBetter'                                       AS [direction],
            1                                                      AS [expected],
            JSON_QUERY((SELECT '=1' AS [green], '=0' AS [red]
                           FOR JSON PATH, WITHOUT_ARRAY_WRAPPER))  AS [bands],
            COALESCE(JSON_VALUE(Metadata,'$.healthFunction'), Description, '') AS [description]
         FOR JSON PATH))                                           AS [metrics]
    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
)
WHERE NodeType = 'probe'
  AND (HealthRules IS NULL
       OR HealthRules LIKE '%"bands":"%'    -- stringified bands = bug fingerprint from prior run
       OR HealthRules LIKE '%"source":"%'); -- ditto for source
GO

-- ---------------------------------------------------------------------------
-- 5. Design.CodeLink — file claims per node
-- ---------------------------------------------------------------------------
IF OBJECT_ID('Design.CodeLink','U') IS NULL
BEGIN
    CREATE TABLE Design.CodeLink (
        CodeLinkId    INT IDENTITY(1,1)  NOT NULL,
        NodeId        INT                NOT NULL,
        Kind          NVARCHAR(20)       NOT NULL,
        [Path]        NVARCHAR(500)      NOT NULL,
        IsPrimary     BIT                NOT NULL CONSTRAINT DF_Design_CodeLink_IsPrimary DEFAULT (0),
        Notes         NVARCHAR(500)      NULL,
        CreatedAt     DATETIME2(3)       NOT NULL CONSTRAINT DF_Design_CodeLink_CreatedAt DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_Design_CodeLink PRIMARY KEY CLUSTERED (CodeLinkId),
        CONSTRAINT FK_Design_CodeLink_Node FOREIGN KEY (NodeId) REFERENCES Health.Node(NodeId),
        CONSTRAINT UQ_Design_CodeLink_NodePath UNIQUE (NodeId, [Path]),
        CONSTRAINT CK_Design_CodeLink_Kind CHECK (Kind IN ('code','sql','doc','test','config','script'))
    );

    CREATE NONCLUSTERED INDEX IX_Design_CodeLink_NodeId ON Design.CodeLink(NodeId);
    CREATE NONCLUSTERED INDEX IX_Design_CodeLink_Path   ON Design.CodeLink([Path]);
END
GO

-- Enforce at most one primary per node (filtered unique index)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Design_CodeLink_OnePrimary'
                                           AND object_id = OBJECT_ID('Design.CodeLink'))
    CREATE UNIQUE NONCLUSTERED INDEX UX_Design_CodeLink_OnePrimary
        ON Design.CodeLink(NodeId) WHERE IsPrimary = 1;
GO

-- ---------------------------------------------------------------------------
-- 6. Design.Decision — locked architectural decisions
-- ---------------------------------------------------------------------------
IF OBJECT_ID('Design.Decision','U') IS NULL
BEGIN
    CREATE TABLE Design.Decision (
        DecisionId    INT IDENTITY(1,1)  NOT NULL,
        NodeId        INT                NULL,           -- scope of decision (NULL = platform-wide)
        Slug          NVARCHAR(150)      NOT NULL,
        Title         NVARCHAR(200)      NOT NULL,
        Decision      NVARCHAR(MAX)      NOT NULL,
        Rationale     NVARCHAR(MAX)      NULL,
        Rejected      NVARCHAR(MAX)      NULL,           -- rejected alternatives
        DecidedAt     DATE               NOT NULL CONSTRAINT DF_Design_Decision_DecidedAt DEFAULT (CAST(SYSUTCDATETIME() AS DATE)),
        Status        NVARCHAR(20)       NOT NULL CONSTRAINT DF_Design_Decision_Status DEFAULT ('locked'),
        SupersededBy  INT                NULL,
        CreatedAt     DATETIME2(3)       NOT NULL CONSTRAINT DF_Design_Decision_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt     DATETIME2(3)       NOT NULL CONSTRAINT DF_Design_Decision_UpdatedAt DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_Design_Decision PRIMARY KEY CLUSTERED (DecisionId),
        CONSTRAINT UQ_Design_Decision_Slug UNIQUE (Slug),
        CONSTRAINT FK_Design_Decision_Node FOREIGN KEY (NodeId) REFERENCES Health.Node(NodeId),
        CONSTRAINT FK_Design_Decision_SupersededBy FOREIGN KEY (SupersededBy) REFERENCES Design.Decision(DecisionId),
        CONSTRAINT CK_Design_Decision_Status CHECK (Status IN ('locked','revisit','superseded','rejected'))
    );

    CREATE NONCLUSTERED INDEX IX_Design_Decision_NodeId ON Design.Decision(NodeId) WHERE NodeId IS NOT NULL;
END
GO

-- ---------------------------------------------------------------------------
-- 7. Verification — counts and a tree-sanity sample
-- ---------------------------------------------------------------------------
SELECT
    (SELECT COUNT(*) FROM Health.Node)                                           AS Nodes_Total,
    (SELECT COUNT(*) FROM Health.Node WHERE Lifecycle = 'active')                AS Nodes_Active,
    (SELECT COUNT(*) FROM Health.Node WHERE NodeType = 'probe')                  AS Probes_Total,
    (SELECT COUNT(*) FROM Health.Node WHERE NodeType = 'probe' AND ProbeBinding IS NOT NULL) AS Probes_Bound,
    (SELECT COUNT(*) FROM Health.Node WHERE NodeType = 'probe' AND HealthRules  IS NOT NULL) AS Probes_WithRules,
    (SELECT COUNT(*) FROM Design.CodeLink)                                       AS CodeLinks_Total,
    (SELECT COUNT(*) FROM Design.Decision)                                       AS Decisions_Total;

SELECT TOP 5
    n.Slug, n.NodeType, n.Lifecycle,
    JSON_VALUE(n.ProbeBinding, '$.kind')      AS BindKind,
    JSON_VALUE(n.ProbeBinding, '$.subsystem') AS BindSubsystem,
    JSON_VALUE(n.ProbeBinding, '$.probeName') AS BindProbe
FROM Health.Node n
WHERE n.NodeType = 'probe'
ORDER BY n.NodeId;
GO
