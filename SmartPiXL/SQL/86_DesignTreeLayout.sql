-- ============================================================================
-- 86_DesignTreeLayout.sql
-- Adds LayoutX / LayoutY to Health.Node so the blueprint designer can
-- persist node positions. Floats because the canvas is infinite and fractional
-- positions occur during smooth-drag autosave.
-- Idempotent. Safe to re-run.
-- ============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF COL_LENGTH('Health.Node', 'LayoutX') IS NULL
    ALTER TABLE Health.Node ADD LayoutX FLOAT NULL;
GO

IF COL_LENGTH('Health.Node', 'LayoutY') IS NULL
    ALTER TABLE Health.Node ADD LayoutY FLOAT NULL;
GO

-- Seed a reasonable initial layout for the existing 68 nodes.
-- Rows by NodeType tier, columns by sibling order. Only touches nodes that
-- currently have NULL layout so repeat-runs won't clobber user-placed nodes.
;WITH ordered AS (
    SELECT
        NodeId,
        ParentId,
        NodeType,
        ROW_NUMBER() OVER (PARTITION BY NodeType ORDER BY NodeId) AS col,
        CASE NodeType
            WHEN 'platform'  THEN 0
            WHEN 'system'    THEN 1
            WHEN 'subsystem' THEN 2
            WHEN 'component' THEN 3
            WHEN 'probe'     THEN 4
            ELSE 5
        END AS row
    FROM Health.Node
)
UPDATE n
SET LayoutX = o.col * 280.0,
    LayoutY = o.row * 180.0
FROM Health.Node n
JOIN ordered o ON o.NodeId = n.NodeId
WHERE n.LayoutX IS NULL OR n.LayoutY IS NULL;
GO

SELECT TOP 5 NodeId, Slug, NodeType, LayoutX, LayoutY FROM Health.Node ORDER BY NodeId;
GO
