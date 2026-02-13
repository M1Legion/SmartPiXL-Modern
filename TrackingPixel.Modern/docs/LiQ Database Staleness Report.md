# LiQ Database Staleness Report

## Summary

- DB size on disk: 25.2 TB (data: 25.1 TB, log: 101 GB)
- Total tables: 317
- SQL Server uptime: 9 days (restart Feb 4, 2026)

## Actively Used (today, right now)

Only 4 tables have been touched since the Feb 4 restart:

| Table | Rows | Last Activity | Operation |
| --- | --- | --- | --- |
| dbo.DailyMaid_Status | 1,005 | Today 08:15 | READ |
| dbo.Places_Active | 489,958 | Today 08:00 | WRITE |
| dbo.Client_Places_Map | 127,444 | Today 08:00 | WRITE |
| dbo.ActivePlaces | 0 | Today 07:00 | READ |

### Actively Used Procs (executed since restart)

| Proc | Exec Count (9 days) | Last Run | Purpose |
| --- | --- | --- | --- |
| M1SP_Get_Last_UC_Date | 5,325 | Today 08:15 | Called frequently (hourly?) |
| M1SP_Get_Clients_WF | 5,325 | Today 08:15 | Called frequently |
| M1SP_Delete_MD | 54 | Today 08:00 | Cleanup |
| M1SP_UpdateActivePlaces | 106 | Today 07:00 | Refreshes Places_Active |
| M1SP_Lookback_Unmatched_Panafax | 1,050 | Today 04:20 | Still running lookback |
| M1SP_Lookback_Matched_Panafax | 1,050 | Today 04:20 | Still running lookback |
| M1SP_PurgeHistory | 9 | Today 01:00 | Nightly purge |
| M1SP_CreateNewNightlyStagingTable | 9 | Today 00:00 | Creates daily staging table |

### Active SQL Agent Jobs (enabled, running on this server)

| Job | Last Run | Status |
| --- | --- | --- |
| Delete old DailyMaids | Today 01:00 | Enabled, running daily |
| Create New Nightly Staging Table | Today 00:00 | Enabled, running daily |
| DigitalCoOp_LiQ_Travelers_Audiences | Sep 8, 2025 (158 days ago) | Enabled but idle |
| DigitalCoop_LiQ_Shoppers_Enthusiasts_Audiences | Sep 8, 2025 (158 days ago) | Enabled but idle |
| CreateDailyMaid | Sep 15, 2024 (516 days ago) | Disabled |

## Completely Dead

- 313 tables
- 204 BILLION rows
- No activity since restart

The massive storage consumers with zero activity:

| Category | Tables | Rows | Size (GB) |
| --- | --- | --- | --- |
| Nightly_Staging_YYYYMMDD | 158 | 162.8B | 12,035 GB |
| dailymatches* | 10 | 60.4B | 976 GB |
| ad_id_tracking | 1 | 5.9B | 259 GB |
| bb. (Blockboard)* | 66 | 154M | 27 GB |
| Other (campaign tables, chain stores, etc.) | 63 | 2.5B | 105 GB |
| GWA/STIHL campaign | 16 | 19M | 3 GB |
| LOOKER_* | 7 | 90M | 3 GB |

The Nightly_Staging tables range from 20250907 through 20260211 — that's 158 daily tables, each containing 1-4 billion rows. The M1SP_CreateNewNightlyStagingTable job is still creating a new one every midnight, and the purge job presumably cleans old ones, but you've got 12 TB of staging data sitting here.

## Cross-Database Dependencies

Nothing in SmartPixl references LiQ — zero cross-DB dependencies inbound. Clean.

LiQ procs reference outbound to:

- AutoUpdate.dbo.AutoConsumer / Auto_5_new — used by the active Lookback/Match procs
- lookback database — several procs reference lookback.dbo.AUTOMATED_Lookback_Intersections etc.
- Gaston, GastonOrders — but only from DEPRECATED_* procs

## Verdict

You haven't fully moved off this server. The core LiQ pipeline is still running here:

- The Nightly Staging ingest is active (new table created daily)
- The Panafax lookback matching is active (1,050 executions in 9 days)
- Places_Active and Client_Places_Map are being written to right now
- M1SP_Get_Last_UC_Date and M1SP_Get_Clients_WF are hammering away (5,325 calls in 9 days)

### What you CAN safely nuke (13+ TB of dead weight)

- All Nightly_Staging_2025* tables older than your retention window
- dailymatches2020_2022, dailymatches2023, dailymatches2024 — historical
- ad_id_tracking (5.9B rows, zero reads)
- All bb.* Blockboard tables (zero activity)
- All GWA_* / STIHL_* campaign tables
- All LOOKER_* tables
- All one-off tables: Alesco Ford Jan 2025, NW Ohio dealers, Tesla_Superchargers, etc.

### What you CANNOT delete yet

- Places_Active, Client_Places_Map, DailyMaid_Status, ActivePlaces — active
- The 8 procs executing today
- The 2 enabled Agent jobs that run daily
- Today's Nightly_Staging table (if the pipeline writes to it)

You'll want to confirm whether the other server is running the Panafax lookback and the M1SP_Get_Clients_WF / M1SP_Get_Last_UC_Date procs before cutting this over. Those are still hot.