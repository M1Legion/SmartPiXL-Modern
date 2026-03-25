---
title: "F7: Data Sync"
node: forge.f7-data-sync
nodeType: subsystem
status: current
related:
  - systems/forge
  - components/ip-data-acquisition
---

# F7: Data Sync

## Atlas Public

SmartPiXL keeps its configuration and reference data current through automated synchronization. The Data Sync subsystem ensures company settings, pixel configurations, and IP intelligence databases stay up to date without manual intervention.

## Atlas Internal

### What F7 Syncs

Two categories of data are managed by F7:

**1. Company/Pixel Configuration Sync**
Customer company records and pixel settings are synchronized from Xavier (the legacy CRM system) into SmartPiXL's `PiXL.Company` and `PiXL.Settings` tables. This runs every 6 hours, ensuring new customers and configuration changes are picked up automatically.

**2. IP Data Acquisition**
Public IP intelligence datasets are acquired and imported daily. This is significant enough to have its own component node — see the IP Data Acquisition component page for details. Two datasets are currently imported:
- **IPtoASN** — IP-to-ASN mapping for network ownership identification
- **DB-IP Lite** — City-level geolocation for IP addresses

### Health Probes

| Probe | What It Checks |
|-------|---------------|
| **Company/Pixel Sync** | Xavier → PiXL.Company/PiXL.Settings sync every 6h |

### Child Component

| Component | Purpose |
|-----------|---------|
| **IP Data Acquisition** | Daily import of IPtoASN and DB-IP Lite datasets |

## Atlas Technical

### DataSyncService

Runs on a timer (every 6 hours for company sync, daily for IP data). Connects to Xavier SQL Server via a separate connection string, reads company/pixel records, and upserts into SmartPiXL tables.

### Xavier Integration

Xavier is the legacy SQL Server 2017 system that manages customer accounts. SmartPiXL reads from Xavier but never writes to it. The sync is one-way: Xavier → SmartPiXL.

## Atlas Private

### Key Files

| File | Purpose |
|------|---------|
| `SmartPiXL.Forge/Services/DataSyncService.cs` | Company/pixel sync + IP data acquisition orchestration |
