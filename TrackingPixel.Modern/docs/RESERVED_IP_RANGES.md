# Reserved IP Address Ranges

This document catalogs IP address ranges that should NOT be geolocated. These IPs should be classified and flagged but not sent to external geo services.

## Overview

When processing IPs in SmartPiXL, we classify them into categories:

| Category | Action | Example Use Case |
|----------|--------|------------------|
| **Public** | ✅ Geolocate | Real website visitors |
| **Private** | ❌ Skip geo, flag | Internal network (office, home) |
| **Loopback** | ❌ Skip geo, flag | Localhost testing |
| **Link-Local** | ❌ Skip geo, flag | DHCP failure / unconfigured |
| **CGNAT** | ⚠️ Attempt geo, flag | Carrier shared IPs |
| **Datacenter** | ⚠️ Attempt geo, flag as bot candidate | AWS, GCP, Azure, etc. |
| **Documentation** | ❌ Skip geo, flag | Test/example IPs in docs |
| **Multicast** | ❌ Skip geo, flag | Not unicast traffic |
| **Reserved/Future** | ❌ Skip geo, flag | Not yet allocated |

---

## IPv4 Reserved Ranges

### Private Networks (RFC 1918)

Used for internal LANs. Never routable on the public internet.

| CIDR | Range | Count | Name |
|------|-------|-------|------|
| `10.0.0.0/8` | 10.0.0.0 – 10.255.255.255 | 16,777,216 | Class A Private |
| `172.16.0.0/12` | 172.16.0.0 – 172.31.255.255 | 1,048,576 | Class B Private |
| `192.168.0.0/16` | 192.168.0.0 – 192.168.255.255 | 65,536 | Class C Private |

### Loopback

Traffic to these never leaves the host.

| CIDR | Range | Count | Name |
|------|-------|-------|------|
| `127.0.0.0/8` | 127.0.0.0 – 127.255.255.255 | 16,777,216 | Loopback |

### Link-Local (APIPA)

Assigned when DHCP fails. Only valid on local network segment.

| CIDR | Range | Count | Name |
|------|-------|-------|------|
| `169.254.0.0/16` | 169.254.0.0 – 169.254.255.255 | 65,536 | Link-Local |

### Carrier-Grade NAT (CGNAT)

Shared address space for ISPs using NAT. Multiple customers share these IPs.

| CIDR | Range | Count | Name |
|------|-------|-------|------|
| `100.64.0.0/10` | 100.64.0.0 – 100.127.255.255 | 4,194,304 | Shared/CGNAT |

**Note:** These CAN be geolocated (they represent real ISP customers) but should be flagged as shared.

### "This Network" / Unspecified

| CIDR | Range | Count | Name |
|------|-------|-------|------|
| `0.0.0.0/8` | 0.0.0.0 – 0.255.255.255 | 16,777,216 | "This" network |
| `0.0.0.0/32` | 0.0.0.0 | 1 | Unspecified address |

### Documentation / Examples (TEST-NET)

Reserved for use in documentation. Should never appear in real traffic.

| CIDR | Range | Count | Name |
|------|-------|-------|------|
| `192.0.2.0/24` | 192.0.2.0 – 192.0.2.255 | 256 | TEST-NET-1 |
| `198.51.100.0/24` | 198.51.100.0 – 198.51.100.255 | 256 | TEST-NET-2 |
| `203.0.113.0/24` | 203.0.113.0 – 203.0.113.255 | 256 | TEST-NET-3 |

### Benchmarking

Reserved for network testing between devices.

| CIDR | Range | Count | Name |
|------|-------|-------|------|
| `198.18.0.0/15` | 198.18.0.0 – 198.19.255.255 | 131,072 | Benchmarking |

### Multicast

Used for one-to-many communication. Not unicast visitor traffic.

| CIDR | Range | Count | Name |
|------|-------|-------|------|
| `224.0.0.0/4` | 224.0.0.0 – 239.255.255.255 | 268,435,456 | Multicast |
| `233.252.0.0/24` | 233.252.0.0 – 233.252.0.255 | 256 | MCAST-TEST-NET |

### Reserved / Future Use

Not currently allocated. May become valid in the future.

| CIDR | Range | Count | Name |
|------|-------|-------|------|
| `240.0.0.0/4` | 240.0.0.0 – 255.255.255.254 | 268,435,455 | Reserved (Class E) |

### Broadcast

| CIDR | Range | Count | Name |
|------|-------|-------|------|
| `255.255.255.255/32` | 255.255.255.255 | 1 | Limited Broadcast |

### IETF Protocol Assignments

Reserved for protocol-specific purposes.

| CIDR | Name | RFC |
|------|------|-----|
| `192.0.0.0/24` | IETF Protocol Assignments | RFC 6890 |
| `192.0.0.8/32` | IPv4 Dummy Address | RFC 7600 |
| `192.0.0.9/32` | PCP Anycast | RFC 7723 |
| `192.0.0.10/32` | TURN Anycast | RFC 8155 |
| `192.0.0.170/32` | NAT64/DNS64 Discovery | RFC 7050 |
| `192.0.0.171/32` | NAT64/DNS64 Discovery | RFC 7050 |

### Deprecated / Historical

| CIDR | Name | Notes |
|------|------|-------|
| `192.88.99.0/24` | 6to4 Relay Anycast | Deprecated 2015 |

---

## IPv6 Reserved Ranges

### Loopback

| CIDR | Address | Name |
|------|---------|------|
| `::1/128` | `::1` | Loopback |

### Unspecified

| CIDR | Address | Name |
|------|---------|------|
| `::/128` | `::` | Unspecified |

### IPv4-Mapped IPv6

IPv4 addresses embedded in IPv6 format.

| CIDR | Range | Name |
|------|-------|------|
| `::ffff:0:0/96` | `::ffff:0.0.0.0` – `::ffff:255.255.255.255` | IPv4-mapped |

**Note:** Extract the IPv4 portion and apply IPv4 rules.

### Link-Local

| CIDR | Range | Name |
|------|-------|------|
| `fe80::/10` | `fe80::` – `febf:ffff:...` | Link-Local |

### Unique Local Addresses (ULA)

IPv6 equivalent of RFC 1918 private networks.

| CIDR | Range | Name |
|------|-------|------|
| `fc00::/7` | `fc00::` – `fdff:ffff:...` | Unique Local |
| `fd00::/8` | `fd00::` – `fdff:ffff:...` | Unique Local (L=1) |

**Note:** `fc00::/8` (L=0) is reserved but not used.

### Documentation

| CIDR | Range | Name |
|------|-------|------|
| `2001:db8::/32` | `2001:db8::` – `2001:db8:ffff:...` | Documentation |
| `3fff::/20` | `3fff::` – `3fff:fff:ffff:...` | Documentation (new) |

### Multicast

| CIDR | Range | Name |
|------|-------|------|
| `ff00::/8` | `ff00::` – `ffff:ffff:...` | Multicast |

### Tunneling / Translation

| CIDR | Name | Notes |
|------|------|-------|
| `64:ff9b::/96` | NAT64 | IPv4/IPv6 translation |
| `64:ff9b:1::/48` | Local NAT64 | Private use translation |
| `2001::/32` | Teredo | Tunneling (deprecated) |
| `2002::/16` | 6to4 | Tunneling (deprecated) |

### Discard / Blackhole

| CIDR | Name | Notes |
|------|------|-------|
| `100::/64` | Discard Prefix | Explicitly dropped |

### Special Purpose

| CIDR | Name | Notes |
|------|------|-------|
| `2001:20::/28` | ORCHIDv2 | Cryptographic hash IDs |
| `5f00::/16` | SRv6 | Segment Routing |

---

## Datacenter / Cloud Provider Ranges

These are public IPs but originate from known hosting providers. High bot/scraper probability.

### Major Providers

| Provider | IP List Source |
|----------|----------------|
| **AWS** | https://ip-ranges.amazonaws.com/ip-ranges.json |
| **Google Cloud** | https://www.gstatic.com/ipranges/cloud.json |
| **Microsoft Azure** | https://www.microsoft.com/en-us/download/details.aspx?id=56519 |
| **Cloudflare** | https://www.cloudflare.com/ips/ |
| **DigitalOcean** | https://digitalocean.com/geo/google.csv |
| **Linode/Akamai** | https://geoip.linode.com/ |
| **OVH** | Various, no single source |
| **Hetzner** | https://www.hetzner.com/unternehmen/rechenzentrum |

**Recommendation:** Periodically download and cache these lists. Flag IPs that match as `IsDatacenterIP = true`.

---

## Known Bot User Agent Patterns

Not IP-related, but useful for bot detection:

### Search Engine Bots (Legitimate)
```
Googlebot, Bingbot, YandexBot, Baiduspider, DuckDuckBot, Sogou, Exabot
```

### Social Media Crawlers (Legitimate)
```
facebookexternalhit, LinkedInBot, Twitterbot, Pinterest, Slackbot, WhatsApp, TelegramBot
```

### Headless Browsers (Suspicious)
```
HeadlessChrome, PhantomJS, Selenium, Puppeteer, Playwright, Nightmare
```

### HTTP Libraries (Suspicious in browser context)
```
curl/, wget/, python-requests/, Go-http-client/, Java/, libwww-perl, HttpClient
```

### Known Scrapers (Malicious)
```
AhrefsBot, SemrushBot, MJ12bot, DotBot, BLEXBot, DataForSeoBot
```

---

## Implementation Notes

### C# IP Classification

```csharp
public enum IpType
{
    Public,          // Normal routable IP - geolocate
    Private,         // RFC 1918 - skip geo
    Loopback,        // 127.x.x.x, ::1 - skip geo
    LinkLocal,       // 169.254.x.x, fe80:: - skip geo
    CGNAT,           // 100.64.x.x - attempt geo, flag
    Datacenter,      // AWS, GCP, etc. - attempt geo, flag
    Documentation,   // TEST-NET - skip geo
    Multicast,       // 224.x.x.x+ - skip geo
    Reserved,        // Class E, etc. - skip geo
    Broadcast,       // 255.255.255.255 - skip geo
    Unknown          // Couldn't parse
}
```

### View Column Suggestion

Add to `vw_PiXL_Parsed`:

```sql
IpTypeDescription = CASE
    WHEN IPAddress LIKE '10.%' THEN 'Private'
    WHEN IPAddress LIKE '172.1[6-9].%' OR IPAddress LIKE '172.2[0-9].%' OR IPAddress LIKE '172.3[0-1].%' THEN 'Private'
    WHEN IPAddress LIKE '192.168.%' THEN 'Private'
    WHEN IPAddress LIKE '127.%' THEN 'Loopback'
    WHEN IPAddress LIKE '169.254.%' THEN 'LinkLocal'
    WHEN IPAddress LIKE '100.6[4-9].%' OR IPAddress LIKE '100.[7-9][0-9].%' OR IPAddress LIKE '100.1[0-1][0-9].%' OR IPAddress LIKE '100.12[0-7].%' THEN 'CGNAT'
    WHEN IPAddress LIKE '0.%' THEN 'Unspecified'
    WHEN IPAddress LIKE '224.%' OR IPAddress LIKE '225.%' ... THEN 'Multicast'
    WHEN IPAddress LIKE '240.%' OR IPAddress LIKE '255.%' THEN 'Reserved'
    ELSE 'Public'
END
```

---

## References

- [IANA IPv4 Special-Purpose Address Registry](https://www.iana.org/assignments/iana-ipv4-special-registry/iana-ipv4-special-registry.xhtml)
- [IANA IPv6 Special-Purpose Address Registry](https://www.iana.org/assignments/iana-ipv6-special-registry/iana-ipv6-special-registry.xhtml)
- [RFC 6890 - Special-Purpose IP Address Registries](https://tools.ietf.org/html/rfc6890)
- [RFC 1918 - Private Internets](https://tools.ietf.org/html/rfc1918)
- [RFC 6598 - CGNAT Shared Address Space](https://tools.ietf.org/html/rfc6598)
- [Wikipedia - Reserved IP Addresses](https://en.wikipedia.org/wiki/Reserved_IP_addresses)
