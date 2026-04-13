-- ============================================================================
-- Health Tree: OS End-of-Life Data Service nodes
-- Parent: forge.f7-data-sync (NodeId=70)
-- 
-- Adds a component and two probes for the OsEndOfLifeService which fetches
-- OS lifecycle data from endoflife.date and caches it locally.
--
-- Run: sqlcmd -S localhost\SQL2025 -d SmartPiXL -E -i SmartPiXL/SQL/76_OsEolHealthNodes.sql
-- ============================================================================

SET QUOTED_IDENTIFIER ON;
SET IDENTITY_INSERT Health.Node ON;

-- Component: OS End-of-Life data acquisition (child of F7: Data Sync)
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder, IsActive)
VALUES (111, 70, N'forge.f7-data-sync.os-eol-acquisition', N'OS EOL Acquisition', N'component',
        N'Fetches and caches OS end-of-life data from endoflife.date API (windows, macos, ios, android, ipados)',
        N'{"source":"forge","healthFunction":"OsEndOfLifeService.IsLoaded","dataSource":"https://endoflife.date/tags/os"}', 3, 1);

-- Probe: Data loaded (at least one product has data)
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder, IsActive)
VALUES (112, 111, N'forge.f7-data-sync.os-eol-acquisition.data-loaded', N'Data Loaded', N'probe',
        N'OS EOL service has loaded lifecycle data for at least one product',
        N'{"source":"forge","healthFunction":"OsEndOfLifeService.ProductCount > 0"}', 1, 1);

-- Probe: All products loaded (all 5 tracked products have data)
INSERT INTO Health.Node (NodeId, ParentId, Slug, Name, NodeType, Description, Metadata, SortOrder, IsActive)
VALUES (113, 111, N'forge.f7-data-sync.os-eol-acquisition.all-products', N'All Products Loaded', N'probe',
        N'All 5 tracked OS products loaded (windows, macos, ios, android, ipados)',
        N'{"source":"forge","healthFunction":"OsEndOfLifeService.ProductCount == 5"}', 2, 1);

SET IDENTITY_INSERT Health.Node OFF;

-- Verify
SELECT NodeId, ParentId, Slug, Name, NodeType, SortOrder
FROM Health.Node
WHERE NodeId >= 111
ORDER BY NodeId;
