---
description: 'Diagnose why data is not flowing through the ETL pipeline (PiXL.Test → Parsed → Device/IP/Visit → Match)'
agent: 'etl-pipeline'
tools: ['read', 'search', 'execute', 'ms-mssql.mssql/*']
---

# Troubleshoot ETL Pipeline

Data isn't flowing. Diagnose which stage is stuck and fix it.

## Diagnostic Sequence

1. **Check PiXL.Test** — Are raw hits arriving? `SELECT COUNT(*), MAX(ReceivedAt) FROM PiXL.Test`
   - If NO → Problem is upstream (IIS/app/SQL bulk writer). Check app logs and IIS logs.
   - If YES → Continue.

2. **Check ETL.Watermark** — `SELECT * FROM ETL.Watermark WHERE ProcessName = 'ParseNewHits'`
   - Is `LastProcessedId` < MAX(PiXL.Test.Id)? The ETL hasn't caught up. Check EtlBackgroundService logs.
   - Is `LastProcessedId` AHEAD of MAX(PiXL.Test.Id)? Reset: `UPDATE ETL.Watermark SET LastProcessedId = 0, RowsProcessed = 0 WHERE ProcessName = 'ParseNewHits'`

3. **Check PiXL.Parsed** — `SELECT COUNT(*), MAX(ParsedAt) FROM PiXL.Parsed`
   - If zero or stale → Run manually: `EXEC ETL.usp_ParseNewHits`
   - Check for errors in the output.

4. **Check dimension tables** — `SELECT COUNT(*) FROM PiXL.Device; SELECT COUNT(*) FROM PiXL.IP; SELECT COUNT(*) FROM PiXL.Visit`
   - These are populated by phases 9-13 of usp_ParseNewHits.

5. **Check PiXL.Match** — `SELECT * FROM ETL.MatchWatermark`
   - If stale → Run manually: `EXEC ETL.usp_MatchVisits`
   - Check PiXL.Config for MatchEmail/MatchIP/MatchGeo flags.

6. **Check geo enrichment** — `SELECT COUNT(*) FROM PiXL.Parsed WHERE GeoCountry IS NOT NULL`
   - If zero → Run manually: `EXEC ETL.usp_EnrichParsedGeo`
   - Check IPAPI.IP has data: `SELECT COUNT(*) FROM IPAPI.IP`

7. **Check pipeline health view** — `SELECT * FROM dbo.vw_Dash_PipelineHealth`

Report findings with row counts, watermark positions, and timestamps.
