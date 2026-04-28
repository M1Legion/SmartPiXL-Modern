-- ============================================================================
-- 89_CleanseUnicode.sql
-- Replace non-ASCII characters in Health.Node + Design.Decision text columns
-- with ASCII equivalents. Idempotent.
-- ============================================================================

USE SmartPiXL;
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @before INT, @after INT;

SELECT @before = SUM(CASE WHEN v COLLATE Latin1_General_BIN LIKE N'%[^ -~]%' THEN 1 ELSE 0 END)
FROM Health.Node
CROSS APPLY (VALUES (Name),(Description),(DescMarketing),(DescManagement),(DescDeveloper)) x(v)
WHERE IsActive = 1 AND v IS NOT NULL;

UPDATE Health.Node SET
    Name = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
           Name,
           NCHAR(0x2014), N' - '),
           NCHAR(0x2013), N'-'),
           NCHAR(0x2019), N''''),
           NCHAR(0x2018), N''''),
           NCHAR(0x201C), N'"'),
           NCHAR(0x201D), N'"'),
           NCHAR(0x2026), N'...'),
           NCHAR(0x2192), N'->'),
           NCHAR(0x00D7), N'x'),
           NCHAR(0x00A0), N' '),
           NCHAR(0xFFFD), N'-'),

    Description = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
           Description,
           NCHAR(0x2014), N' - '),
           NCHAR(0x2013), N'-'),
           NCHAR(0x2019), N''''),
           NCHAR(0x2018), N''''),
           NCHAR(0x201C), N'"'),
           NCHAR(0x201D), N'"'),
           NCHAR(0x2026), N'...'),
           NCHAR(0x2192), N'->'),
           NCHAR(0x00D7), N'x'),
           NCHAR(0x00A0), N' '),
           NCHAR(0xFFFD), N'-'),

    DescMarketing = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
           DescMarketing,
           NCHAR(0x2014), N' - '),
           NCHAR(0x2013), N'-'),
           NCHAR(0x2019), N''''),
           NCHAR(0x2018), N''''),
           NCHAR(0x201C), N'"'),
           NCHAR(0x201D), N'"'),
           NCHAR(0x2026), N'...'),
           NCHAR(0x2192), N'->'),
           NCHAR(0x00D7), N'x'),
           NCHAR(0x00A0), N' '),
           NCHAR(0xFFFD), N'-'),

    DescManagement = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
           DescManagement,
           NCHAR(0x2014), N' - '),
           NCHAR(0x2013), N'-'),
           NCHAR(0x2019), N''''),
           NCHAR(0x2018), N''''),
           NCHAR(0x201C), N'"'),
           NCHAR(0x201D), N'"'),
           NCHAR(0x2026), N'...'),
           NCHAR(0x2192), N'->'),
           NCHAR(0x00D7), N'x'),
           NCHAR(0x00A0), N' '),
           NCHAR(0xFFFD), N'-'),

    DescDeveloper = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
           DescDeveloper,
           NCHAR(0x2014), N' - '),
           NCHAR(0x2013), N'-'),
           NCHAR(0x2019), N''''),
           NCHAR(0x2018), N''''),
           NCHAR(0x201C), N'"'),
           NCHAR(0x201D), N'"'),
           NCHAR(0x2026), N'...'),
           NCHAR(0x2192), N'->'),
           NCHAR(0x00D7), N'x'),
           NCHAR(0x00A0), N' '),
           NCHAR(0xFFFD), N'-')
WHERE IsActive = 1;

-- Collapse any doubled spaces introduced by " - " replacements
UPDATE Health.Node SET
    Name           = REPLACE(REPLACE(Name,           N'  ', N' '), N'  ', N' '),
    Description    = REPLACE(REPLACE(Description,    N'  ', N' '), N'  ', N' '),
    DescMarketing  = REPLACE(REPLACE(DescMarketing,  N'  ', N' '), N'  ', N' '),
    DescManagement = REPLACE(REPLACE(DescManagement, N'  ', N' '), N'  ', N' '),
    DescDeveloper  = REPLACE(REPLACE(DescDeveloper,  N'  ', N' '), N'  ', N' ')
WHERE IsActive = 1;

-- Design.Decision if present
IF OBJECT_ID('Design.Decision','U') IS NOT NULL
BEGIN
    UPDATE Design.Decision SET
        Title = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
               Title,
               NCHAR(0x2014),N' - '),NCHAR(0x2013),N'-'),NCHAR(0x2019),N''''),NCHAR(0x2018),N''''),
               NCHAR(0x201C),N'"'),NCHAR(0x201D),N'"'),NCHAR(0x2026),N'...'),NCHAR(0x2192),N'->'),
               NCHAR(0x00D7),N'x'),NCHAR(0x00A0),N' '),NCHAR(0xFFFD),N'-'),
        Decision = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
               Decision,
               NCHAR(0x2014),N' - '),NCHAR(0x2013),N'-'),NCHAR(0x2019),N''''),NCHAR(0x2018),N''''),
               NCHAR(0x201C),N'"'),NCHAR(0x201D),N'"'),NCHAR(0x2026),N'...'),NCHAR(0x2192),N'->'),
               NCHAR(0x00D7),N'x'),NCHAR(0x00A0),N' '),NCHAR(0xFFFD),N'-'),
        Rationale = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
               Rationale,
               NCHAR(0x2014),N' - '),NCHAR(0x2013),N'-'),NCHAR(0x2019),N''''),NCHAR(0x2018),N''''),
               NCHAR(0x201C),N'"'),NCHAR(0x201D),N'"'),NCHAR(0x2026),N'...'),NCHAR(0x2192),N'->'),
               NCHAR(0x00D7),N'x'),NCHAR(0x00A0),N' '),NCHAR(0xFFFD),N'-');
END

SELECT @after = SUM(CASE WHEN v COLLATE Latin1_General_BIN LIKE N'%[^ -~]%' THEN 1 ELSE 0 END)
FROM Health.Node
CROSS APPLY (VALUES (Name),(Description),(DescMarketing),(DescManagement),(DescDeveloper)) x(v)
WHERE IsActive = 1 AND v IS NOT NULL;

SELECT Rows_Before_NonAscii = ISNULL(@before,0), Rows_After_NonAscii = ISNULL(@after,0);

-- Stragglers (should be empty)
SELECT NodeId, Slug, Col, LEFT(Val,120) AS Val
FROM (
    SELECT NodeId, Slug, Col='Name',           Val=Name           FROM Health.Node WHERE IsActive=1
    UNION ALL SELECT NodeId, Slug, 'Description',   Description    FROM Health.Node WHERE IsActive=1
    UNION ALL SELECT NodeId, Slug, 'DescMarketing', DescMarketing  FROM Health.Node WHERE IsActive=1
    UNION ALL SELECT NodeId, Slug, 'DescManagement',DescManagement FROM Health.Node WHERE IsActive=1
    UNION ALL SELECT NodeId, Slug, 'DescDeveloper', DescDeveloper  FROM Health.Node WHERE IsActive=1
) t
WHERE Val IS NOT NULL AND Val COLLATE Latin1_General_BIN LIKE N'%[^ -~]%';

PRINT '89_CleanseUnicode.sql complete.';
