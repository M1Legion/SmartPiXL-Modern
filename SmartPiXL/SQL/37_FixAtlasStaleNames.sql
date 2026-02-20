-- ============================================================================
-- Migration 37: Fix stale PiXL.Test → PiXL.Raw references in Atlas content
--
-- RATIONALE:
--   Migration 28 renamed PiXL.Test → PiXL.Raw, but the Atlas documentation
--   content (Docs.Section HTML tiers) and Docs.SystemStatus descriptions
--   still reference the old name. This updates all text content.
--
-- ALSO FIXES:
--   - Docs.SystemStatus description for DatabaseWriterService: PiXL.Test → PiXL.Raw
--   - PiXL.Pixel → PiXL.Settings in any Docs content
-- ============================================================================

SET NOCOUNT ON;

-- ── Update Docs.Section HTML tiers ───────────────────────────────────────────
IF OBJECT_ID('Docs.Section', 'U') IS NOT NULL
BEGIN
    UPDATE Docs.Section
    SET PitchHtml       = REPLACE(PitchHtml,       'PiXL.Test', 'PiXL.Raw'),
        ManagementHtml  = REPLACE(ManagementHtml,  'PiXL.Test', 'PiXL.Raw'),
        TechnicalHtml   = REPLACE(TechnicalHtml,   'PiXL.Test', 'PiXL.Raw'),
        WalkthroughHtml = REPLACE(WalkthroughHtml, 'PiXL.Test', 'PiXL.Raw'),
        MermaidDiagram  = REPLACE(MermaidDiagram,  'PiXL.Test', 'PiXL.Raw'),
        LastUpdated     = SYSUTCDATETIME(),
        UpdatedBy       = 'agent:remediation-w4'
    WHERE PitchHtml       LIKE '%PiXL.Test%'
       OR ManagementHtml  LIKE '%PiXL.Test%'
       OR TechnicalHtml   LIKE '%PiXL.Test%'
       OR WalkthroughHtml LIKE '%PiXL.Test%'
       OR MermaidDiagram  LIKE '%PiXL.Test%';

    PRINT 'Updated ' + CAST(@@ROWCOUNT AS VARCHAR) + ' Docs.Section rows (PiXL.Test → PiXL.Raw)';

    -- Also fix PiXL.Pixel → PiXL.Settings
    UPDATE Docs.Section
    SET PitchHtml       = REPLACE(PitchHtml,       'PiXL.Pixel', 'PiXL.Settings'),
        ManagementHtml  = REPLACE(ManagementHtml,  'PiXL.Pixel', 'PiXL.Settings'),
        TechnicalHtml   = REPLACE(TechnicalHtml,   'PiXL.Pixel', 'PiXL.Settings'),
        WalkthroughHtml = REPLACE(WalkthroughHtml, 'PiXL.Pixel', 'PiXL.Settings'),
        LastUpdated     = SYSUTCDATETIME(),
        UpdatedBy       = 'agent:remediation-w4'
    WHERE PitchHtml       LIKE '%PiXL.Pixel%'
       OR ManagementHtml  LIKE '%PiXL.Pixel%'
       OR TechnicalHtml   LIKE '%PiXL.Pixel%'
       OR WalkthroughHtml LIKE '%PiXL.Pixel%';

    PRINT 'Updated ' + CAST(@@ROWCOUNT AS VARCHAR) + ' Docs.Section rows (PiXL.Pixel → PiXL.Settings)';
END
ELSE
    PRINT 'Docs.Section table not found — skipping.';

-- ── Update Docs.SystemStatus notes ───────────────────────────────────────────
-- Column is 'Notes' (not 'Description') per actual schema.
IF OBJECT_ID('Docs.SystemStatus', 'U') IS NOT NULL
BEGIN
    UPDATE Docs.SystemStatus
    SET Notes         = REPLACE(Notes, 'PiXL.Test', 'PiXL.Raw'),
        LastVerified  = SYSUTCDATETIME(),
        VerifiedBy    = 'agent:remediation-w4'
    WHERE Notes LIKE '%PiXL.Test%';

    PRINT 'Updated ' + CAST(@@ROWCOUNT AS VARCHAR) + ' Docs.SystemStatus rows (PiXL.Test → PiXL.Raw)';

    UPDATE Docs.SystemStatus
    SET Notes         = REPLACE(Notes, 'PiXL.Pixel', 'PiXL.Settings'),
        LastVerified  = SYSUTCDATETIME(),
        VerifiedBy    = 'agent:remediation-w4'
    WHERE Notes LIKE '%PiXL.Pixel%';

    PRINT 'Updated ' + CAST(@@ROWCOUNT AS VARCHAR) + ' Docs.SystemStatus rows (PiXL.Pixel → PiXL.Settings)';
END
ELSE
    PRINT 'Docs.SystemStatus table not found — skipping.';

PRINT 'Migration 37 complete.';
GO
