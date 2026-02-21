---
title: SmartPiXL Atlas Documentation Index
version: 1.0
last_updated: 2026-02-20
---

# Atlas Documentation Catalog

This index lists all documented subsystems in the SmartPiXL platform. Each document contains four audience tiers:

| Tier | Audience | Access |
|------|----------|--------|
| **Atlas Public** | Customers, marketing, sales | Public-facing via Atlas endpoint |
| **Atlas Internal** | M1 management, account managers, support | Internal, not customer-visible |
| **Atlas Technical** | M1 engineering, DevOps, integration partners | Technical staff |
| **Atlas Private** | Platform owner | Owner-only access |

## Architecture

| Document | Status | Description |
|----------|--------|-------------|
| [overview](architecture/overview.md) | Current | System architecture — 3-process design |
| [data-flow](architecture/data-flow.md) | Current | Request lifecycle from browser to SQL |
| [edge](architecture/edge.md) | Current | PiXL Edge (IIS) deep dive |
| [forge](architecture/forge.md) | Current | SmartPiXL Forge deep dive |
| [sentinel](architecture/sentinel.md) | Current | SmartPiXL Sentinel — dashboards + monitoring |

## Subsystems

| Document | Status | Description |
|----------|--------|-------------|
| [pixl-script](subsystems/pixl-script.md) | Current | Browser-side data collection (159 fields) |
| [fingerprinting](subsystems/fingerprinting.md) | Current | Device identification via canvas/WebGL/audio |
| [bot-detection](subsystems/bot-detection.md) | Current | Bot/crawler scoring (80+ signals) |
| [enrichment-pipeline](subsystems/enrichment-pipeline.md) | Current | Tier 1-3 Forge enrichments (15 steps) |
| [identity-resolution](subsystems/identity-resolution.md) | Current | PiXL.Match + graph-based matching |
| [etl](subsystems/etl.md) | Current | Raw → Parsed → Device/IP/Visit/Match |
| [geo-intelligence](subsystems/geo-intelligence.md) | Current | IPAPI, MaxMind, cultural arbitrage |
| [traffic-alerts](subsystems/traffic-alerts.md) | Current | Visitor scoring, customer summaries |
| [failover](subsystems/failover.md) | Current | JSONL durability, catch-up mechanism |

## Database

| Document | Status | Description |
|----------|--------|-------------|
| [schema-map](database/schema-map.md) | Current | All schemas, tables, relationships |
| [etl-procedures](database/etl-procedures.md) | Current | Stored procedure documentation |
| [sql-features](database/sql-features.md) | Current | SQL 2025 features: vectors, graph, JSON, CLR |

## Operations

| Document | Status | Description |
|----------|--------|-------------|
| [deployment](operations/deployment.md) | Current | IIS Edge + Forge service deployment |
| [troubleshooting](operations/troubleshooting.md) | Current | Common failure modes and fixes |
| [monitoring](operations/monitoring.md) | Current | Health checks, metrics, self-healing |

## Reference

| Document | Status | Description |
|----------|--------|-------------|
| [glossary](glossary.md) | Current | Term definitions from design doc §1.5 |
