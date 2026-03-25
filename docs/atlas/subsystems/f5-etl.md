---
title: "F5: ETL Pipeline"
node: forge.f5-etl
nodeType: subsystem
status: current
related:
  - systems/forge
  - subsystems/f3-sql-writer
  - reference/etl-procedures
---

# F5: ETL Pipeline

## Atlas Public

SmartPiXL's ETL pipeline automatically processes enriched visitor data into structured, queryable intelligence. Every 60 seconds, new data is organized into device records, visit histories, identity matches, and quality scores — ready for analysis within minutes of a visitor interaction.

**What the ETL delivers:**
- **Device records** — each unique device identified and tracked across visits
- **Visit history** — individual visits linked to devices and contacts
- **Contact matching** — form submissions retroactively link anonymous visits to known individuals
- **Traffic quality scores** — composite scoring identifies bots, high-value leads, and anomalies

## Atlas Internal

### ETL Cycle — What Happens Every 60 Seconds

The Forge runs the ETL cycle automatically via stored procedures:

1. **Match Visits** (`usp_MatchVisits`) — Processes new records in PiXL.Parsed. Creates Device records, IP records, Visit records. Matches email addresses to contacts in AutoConsumer, creating identity links in PiXL.Match. This is how anonymous visitors become known leads.

2. **Match Legacy Visits** (`usp_MatchLegacyVisits`) — Identity resolution for legacy records that pre-date the current pipeline.

3. **Materialize Visitor Scores** (`usp_MaterializeVisitorScores`) — Computes composite quality scores (mouse authenticity, session quality, composite quality) and writes to TrafficAlert.VisitorScore.

4. **Materialize Customer Summary** (`usp_MaterializeCustomerSummary`) — Aggregates daily/weekly/monthly quality metrics per customer into TrafficAlert.CustomerSummary.

### Data Flow

```
PiXL.Parsed (231 columns, written by F3 via SqlBulkCopy)
  → usp_MatchVisits
    → PiXL.Device (one per unique device)
    → PiXL.IP (one per unique IP address)
    → PiXL.Visit (one per visit — the fact table)
    → PiXL.Match (links visits to contacts)
  → usp_MaterializeVisitorScores
    → TrafficAlert.VisitorScore (per-visit quality scores)
  → usp_MaterializeCustomerSummary
    → TrafficAlert.CustomerSummary (per-customer period aggregates)
```

### Watermark Pattern

Every ETL procedure tracks progress using a watermark — the last processed ID. If the procedure crashes mid-run:
- The watermark hasn't been updated (updated at the end)
- Next run picks up from the same point
- Self-healing: if the target table has rows beyond the watermark, it auto-advances

This guarantees exactly-once processing with crash recovery.

### Health Probes

| Probe | What It Checks |
|-------|---------------|
| **MatchVisits** | `usp_MatchVisits` identity resolution stored procedure |
| **MatchLegacyVisits** | `usp_MatchLegacyVisits` for legacy records |

## Atlas Technical

### EtlOrchestratorService

The ETL orchestrator runs on a 60-second timer. Each tick:
1. Checks watermark for new records
2. Calls stored procedures in sequence
3. Updates watermarks on success
4. Logs timing and row counts to `ETL.BatchLog`

### Watermark Table

```sql
SELECT ProcessName, LastProcessedId, LastRunAt
FROM ETL.Watermark
```

To reset a stuck ETL process:
```sql
UPDATE ETL.Watermark SET LastProcessedId = 0
WHERE ProcessName = 'MatchVisits'
```

### Volume Expectations

| Metric | Typical | Peak |
|--------|---------|------|
| Parsed rows per minute | 100–1,000 | 10,000+ during campaigns |
| Match time per batch | < 1 second | 2 seconds |
| End-to-end latency | ~60 seconds | ~120 seconds under load |

## Atlas Private

### Key Files

| File | Purpose |
|------|---------|
| `SmartPiXL.Forge/Services/EtlOrchestratorService.cs` | ETL scheduling and execution |
| `SmartPiXL/SQL/usp_MatchVisits.sql` | Identity resolution SP |
| `SmartPiXL/SQL/usp_MatchLegacyVisits.sql` | Legacy identity resolution |
| `SmartPiXL/SQL/usp_MaterializeVisitorScores.sql` | Quality scoring SP |
| `SmartPiXL/SQL/usp_MaterializeCustomerSummary.sql` | Customer summary SP |
