-- Sentinel Tree Build: 5 subsystems, reparent 4 existing probes, 10 new probes
-- Run: sqlcmd -S localhost\SQL2025 -d SmartPiXL -E -i sentinel_tree.sql

SET QUOTED_IDENTIFIER ON;
SET IDENTITY_INSERT Health.Node ON;

-- ============================================================================
-- SUBSYSTEMS (children of Sentinel=4)
-- ============================================================================
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, SortOrder, IsActive)
VALUES (85, 4, N'sentinel.s1-infra-watch', N'S1: Infrastructure Watch', N'subsystem',
        N'Monitors critical infrastructure: SQL Server, Windows services, IIS, self-health', 1, 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, SortOrder, IsActive)
VALUES (90, 4, N'sentinel.s2-health-aggregation', N'S2: Health Aggregation', N'subsystem',
        N'Builds the unified health tree by polling Edge, Forge, and local probes', 2, 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, SortOrder, IsActive)
VALUES (95, 4, N'sentinel.s3-dashboard-api', N'S3: Dashboard API', N'subsystem',
        N'Serves Tron dashboard and Pipeline Explorer SPAs and their data endpoints', 3, 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, SortOrder, IsActive)
VALUES (100, 4, N'sentinel.s4-atlas', N'S4: Atlas', N'subsystem',
        N'Documentation engine: markdown parsing, 4-tier content, live metrics', 4, 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, SortOrder, IsActive)
VALUES (105, 4, N'sentinel.s5-notifications', N'S5: Notifications', N'subsystem',
        N'Ops alerting via SMTP email and carrier SMS gateway', 5, 1);

-- ============================================================================
-- REPARENT existing probes under S1 (85)
-- ============================================================================
UPDATE Health.Node SET ParentId = 85 WHERE NodeId IN (80, 81, 82, 83);

-- ============================================================================
-- NEW PROBES: S2 Health Aggregation (parent=90)
-- ============================================================================
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder, IsActive)
VALUES (91, 90, N'sentinel.s2-health-aggregation.edge-reachable', N'Edge Reachable', N'probe',
        N'HTTP poll to Edge internal health endpoint succeeds',
        N'{"source":"sentinel","healthFunction":"GET 127.0.0.1/internal/health returns 200"}', 1, 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder, IsActive)
VALUES (92, 90, N'sentinel.s2-health-aggregation.forge-reachable', N'Forge Reachable', N'probe',
        N'HTTP poll to Forge health endpoint succeeds',
        N'{"source":"sentinel","healthFunction":"GET 127.0.0.1:7100/health returns 200"}', 2, 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder, IsActive)
VALUES (93, 90, N'sentinel.s2-health-aggregation.tree-build', N'Tree Build', N'probe',
        N'Health tree builds successfully and completes decoration',
        N'{"source":"sentinel","healthFunction":"BuildTreeAsync completes without error"}', 3, 1);

-- ============================================================================
-- NEW PROBES: S3 Dashboard API (parent=95)
-- ============================================================================
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder, IsActive)
VALUES (96, 95, N'sentinel.s3-dashboard-api.tron-spa', N'Tron SPA', N'probe',
        N'Tron operations dashboard HTML serves correctly',
        N'{"source":"sentinel","healthFunction":"/tron serves 200 with content"}', 1, 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder, IsActive)
VALUES (97, 95, N'sentinel.s3-dashboard-api.pipeline-spa', N'Pipeline SPA', N'probe',
        N'Pipeline Explorer HTML serves correctly',
        N'{"source":"sentinel","healthFunction":"/pipeline serves 200 with content"}', 2, 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder, IsActive)
VALUES (98, 95, N'sentinel.s3-dashboard-api.dash-endpoints', N'Dashboard Endpoints', N'probe',
        N'Dashboard data API returns valid JSON',
        N'{"source":"sentinel","healthFunction":"/api/dash/snapshot returns valid JSON"}', 3, 1);

-- ============================================================================
-- NEW PROBES: S4 Atlas (parent=100)
-- ============================================================================
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder, IsActive)
VALUES (101, 100, N'sentinel.s4-atlas.atlas-spa', N'Atlas SPA', N'probe',
        N'Atlas documentation portal HTML serves correctly',
        N'{"source":"sentinel","healthFunction":"/atlas serves 200 with content"}', 1, 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder, IsActive)
VALUES (102, 100, N'sentinel.s4-atlas.markdown-loader', N'Markdown Loader', N'probe',
        N'Markdown atlas service loaded at least one section',
        N'{"source":"sentinel","healthFunction":"MarkdownAtlasService.GetSections().Count > 0"}', 2, 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder, IsActive)
VALUES (103, 100, N'sentinel.s4-atlas.live-metrics', N'Live Metrics', N'probe',
        N'Atlas metrics endpoint returns data',
        N'{"source":"sentinel","healthFunction":"/api/atlas/metrics returns rows"}', 3, 1);

-- ============================================================================
-- NEW PROBES: S5 Notifications (parent=105)
-- ============================================================================
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder, IsActive)
VALUES (106, 105, N'sentinel.s5-notifications.smtp-config', N'SMTP Config', N'probe',
        N'Email notification service has valid SMTP configuration',
        N'{"source":"sentinel","healthFunction":"EmailNotificationService.IsConfigured == true"}', 1, 1);

INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder, IsActive)
VALUES (107, 105, N'sentinel.s5-notifications.sms-config', N'SMS Config', N'probe',
        N'SMS gateway is configured for ops alerts',
        N'{"source":"sentinel","healthFunction":"EmailNotificationService.IsSmsConfigured == true"}', 2, 1);

SET IDENTITY_INSERT Health.Node OFF;

-- Verify
SELECT NodeId, ParentId, Slug, Name, NodeType, SortOrder
FROM Health.Node
WHERE Slug LIKE 'sentinel%' OR NodeId = 4
ORDER BY NodeId;
