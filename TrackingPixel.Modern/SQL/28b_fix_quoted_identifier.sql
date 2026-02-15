/*
    Fix QUOTED_IDENTIFIER on ETL procs.
    Filtered index IX_Parsed_CanvasFP requires QUOTED_IDENTIFIER ON for DML.
    The procs were created/altered with QUOTED_IDENTIFIER OFF (from sqlcmd).
    Re-ALTER them with QUOTED_IDENTIFIER ON so SQL Server records the setting.
*/
USE SmartPiXL;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- Re-ALTER usp_ParseNewHits with QUOTED_IDENTIFIER ON
DECLARE @def NVARCHAR(MAX);
SET @def = OBJECT_DEFINITION(OBJECT_ID('ETL.usp_ParseNewHits'));
IF @def IS NOT NULL
BEGIN
    -- Replace CREATE with ALTER (handle all variants)
    SET @def = REPLACE(@def, 'CREATE OR ALTER PROCEDURE', 'ALTER PROCEDURE');
    SET @def = REPLACE(@def, 'CREATE   OR ALTER PROCEDURE', 'ALTER PROCEDURE');
    SET @def = REPLACE(@def, 'CREATE PROCEDURE', 'ALTER PROCEDURE');
    SET @def = REPLACE(@def, 'CREATE   PROCEDURE', 'ALTER PROCEDURE');

    EXEC sp_executesql @def;
    PRINT 'Re-altered usp_ParseNewHits with QUOTED_IDENTIFIER ON.';
END
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- Re-ALTER usp_EnrichParsedGeo
DECLARE @def2 NVARCHAR(MAX);
SET @def2 = OBJECT_DEFINITION(OBJECT_ID('ETL.usp_EnrichParsedGeo'));
IF @def2 IS NOT NULL
BEGIN
    SET @def2 = REPLACE(@def2, 'CREATE OR ALTER PROCEDURE', 'ALTER PROCEDURE');
    SET @def2 = REPLACE(@def2, 'CREATE   OR ALTER PROCEDURE', 'ALTER PROCEDURE');
    SET @def2 = REPLACE(@def2, 'CREATE PROCEDURE', 'ALTER PROCEDURE');
    SET @def2 = REPLACE(@def2, 'CREATE   PROCEDURE', 'ALTER PROCEDURE');

    EXEC sp_executesql @def2;
    PRINT 'Re-altered usp_EnrichParsedGeo with QUOTED_IDENTIFIER ON.';
END
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- Re-ALTER usp_MatchVisits
DECLARE @def3 NVARCHAR(MAX);
SET @def3 = OBJECT_DEFINITION(OBJECT_ID('ETL.usp_MatchVisits'));
IF @def3 IS NOT NULL
BEGIN
    SET @def3 = REPLACE(@def3, 'CREATE OR ALTER PROCEDURE', 'ALTER PROCEDURE');
    SET @def3 = REPLACE(@def3, 'CREATE   OR ALTER PROCEDURE', 'ALTER PROCEDURE');
    SET @def3 = REPLACE(@def3, 'CREATE PROCEDURE', 'ALTER PROCEDURE');
    SET @def3 = REPLACE(@def3, 'CREATE   PROCEDURE', 'ALTER PROCEDURE');

    EXEC sp_executesql @def3;
    PRINT 'Re-altered usp_MatchVisits with QUOTED_IDENTIFIER ON.';
END
GO

-- Verify
SELECT o.name, m.uses_quoted_identifier
FROM sys.sql_modules m
JOIN sys.objects o ON m.object_id = o.object_id
WHERE o.name IN ('usp_ParseNewHits', 'usp_MatchVisits', 'usp_EnrichParsedGeo');
GO
