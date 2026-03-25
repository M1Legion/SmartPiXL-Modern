---
title: "F6: Background IP"
node: forge.f6-background-ip
nodeType: subsystem
status: current
related:
  - systems/forge
  - subsystems/f2-enrichment
  - components/ip-data-acquisition
---

# F6: Background IP

## Atlas Public

SmartPiXL enriches IP addresses with network ownership and hostname data that takes too long for the real-time pipeline. The Background IP subsystem runs these slower lookups in the background, ensuring every IP address eventually gets full enrichment without slowing down visitor processing.

## Atlas Internal

### Off-Hot-Path Enrichment

Some IP enrichments are too slow for the real-time enrichment pipeline (F2):
- **Reverse DNS lookups** can take 50–500ms per IP (network round-trip to DNS servers)
- **WHOIS ASN lookups** can take 100ms–2s depending on the registrar

F6 runs these as background workers that process IPs after they've already been written to the database. The results are written back to the IP record, enriching it retroactively.

### How It Works

1. New IP addresses appear in `PiXL.IP` during ETL (F5)
2. F6 workers scan for IPs missing DNS or WHOIS data
3. Workers perform lookups in the background (rate-limited to avoid flooding DNS/WHOIS servers)
4. Results are written back to the IP record

### Health Probes

| Probe | What It Checks |
|-------|---------------|
| **DNS Enrichment** | Background DNS reverse lookup workers |
| **WHOIS Enrichment** | Background WHOIS ASN lookup workers |

## Atlas Technical

### BackgroundDnsService

Polls `PiXL.IP` for records missing reverse DNS hostname. Performs `Dns.GetHostEntryAsync()` lookups. Writes hostname back to the IP record. Rate-limited to avoid overwhelming DNS resolvers.

### BackgroundWhoisService

Polls `PiXL.IP` for records missing ASN data. Queries WHOIS servers for ASN ownership. Writes organization name, ASN number, and network name back to the IP record.

### Why Not In F2?

F2 enrichment services run synchronously per record on the hot path. Adding 500ms DNS lookups to every record would destroy throughput. Instead:
- F2's `DnsLookupService` uses a **bounded cache** for instant lookups of previously-seen IPs
- F6 does the actual network lookups in the background, populating the database
- Next time the same IP appears, F2 finds it in the cache or database

## Atlas Private

### Key Files

| File | Purpose |
|------|---------|
| `SmartPiXL.Forge/Services/BackgroundDnsService.cs` | DNS reverse lookup workers |
| `SmartPiXL.Forge/Services/BackgroundWhoisService.cs` | WHOIS ASN lookup workers |
