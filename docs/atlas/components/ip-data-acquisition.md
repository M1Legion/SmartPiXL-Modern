---
title: IP Data Acquisition
node: forge.f7-data-sync.ip-data-acquisition
nodeType: component
status: current
related:
  - subsystems/f7-data-sync
  - subsystems/f6-background-ip
  - subsystems/f2-enrichment
---

# IP Data Acquisition

## Atlas Public

SmartPiXL maintains its own IP intelligence databases for fast, offline geographic and network lookups. The IP Data Acquisition component downloads and imports public datasets daily, ensuring visitor geolocation and network identification stays accurate and current.

## Atlas Internal

### Two Public Datasets

| Dataset | What It Provides | Update Frequency | Coverage |
|---------|-----------------|-----------------|----------|
| **IPtoASN** | IP → ASN mapping (network ownership) | Daily | Global, all allocated IP ranges |
| **DB-IP Lite** | IP → City-level geolocation | Monthly | Global, 3M+ city-level ranges |

### How They're Used

- **IPtoASN** feeds into the `IPAPI` schema. When the enrichment engine (F2) or background IP workers (F6) need to know "who owns this IP range?", they look up the ASN from this dataset.
- **DB-IP Lite** provides supplementary geographic data. Used alongside MaxMind GeoIP2 for cross-referencing and gap-filling.

### Health Probes

| Probe | What It Checks |
|-------|---------------|
| **IPtoASN** | IPtoASN daily import into IPAPI schema |
| **DB-IP** | DB-IP Lite monthly import into IPAPI schema |

## Atlas Technical

### Import Process

Both datasets are downloaded as flat files, parsed, and bulk-imported into SQL Server tables in the `IPAPI` schema. The import process:

1. Download latest dataset from public source
2. Parse CSV/TSV into structured records
3. Truncate staging table
4. SqlBulkCopy into staging
5. Merge into production table (upsert)

### Source Data

IPtoASN source: `Research/data/` directory contains historical snapshots. Live imports pull from the public IPtoASN API.

DB-IP source: `Research/data/dbip-city-lite-*.csv` files. Monthly releases with city-level IP geolocation.

## Atlas Private

### Key Files

| File | Purpose |
|------|---------|
| `SmartPiXL.Forge/Services/DataSyncService.cs` | Orchestrates IP data acquisition |
| `Research/data/bgptools-asns.csv` | ASN reference data |
| `Research/data/dbip-city-lite-2026-02.csv` | DB-IP Lite dataset |
| `Research/imports/` | Import scripts and staging |
