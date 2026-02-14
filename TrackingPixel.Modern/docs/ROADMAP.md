# SmartPiXL Modernization Roadmap

Last Updated: January 26, 2026

---

## ✅ Completed

### Phase 1: Core Server
- [x] ASP.NET Core 8 Minimal APIs server
- [x] Fingerprinting script (100+ data points)
- [x] Background SQL bulk writer with graceful shutdown
- [x] Async file logger with buffering
- [x] SQL schema with parsed view (vw_PiXL_Parsed)
- [x] 541K+ test records generated via Playwright

### Phase 2: Code Quality
- [x] Code review - no strings in loops (StringBuilder)
- [x] Code review - using declarations over blocks
- [x] Code review - local functions where appropriate
- [x] Removed dead code (MaxFileSizeMB, duplicate ConnectionStrings)
- [x] Fixed naming consistency (CompanyID/PiXLID)
- [x] Standardized ports to HTTPS 6001
- [x] Deleted obsolete test files
- [x] Cached static paths (test.html)

---

## 🔄 In Progress

### Phase 3: IP Geolocation & Classification

**Goal:** Enrich tracking data with geolocation and bot detection signals before SQL insert.

---

## 📋 Backlog - Detailed Next Steps

### Epic: IP Geolocation Integration

#### Task 3.1: IP Classification Service
**Priority:** P0 (Required before geo)  
**Effort:** 2-3 hours

Create `Services/IpClassificationService.cs`:

1. Parse IPv4 and IPv6 addresses
2. Check against reserved ranges (see [docs/RESERVED_IP_RANGES.md](docs/RESERVED_IP_RANGES.md))
3. Return `IpClassification` record:
   ```csharp
   public record IpClassification(
       IpType Type,           // Public, Private, Loopback, CGNAT, etc.
       bool ShouldGeolocate,  // false for private/loopback/reserved
       string? RangeNote      // "RFC1918", "Loopback", "CGNAT", etc.
   );
   ```
4. Handle edge cases:
   - IPv4-mapped IPv6 (`::ffff:192.168.1.1`) - extract IPv4
   - Malformed IPs - return `Unknown` type
5. Unit tests for all reserved range boundaries

**Files to create:**
- `Services/IpClassificationService.cs`
- `Models/IpClassification.cs`
- Tests (later)

---

#### Task 3.2: Existing Geo Cache Lookup Service
**Priority:** P0  
**Effort:** 3-4 hours

Create `Services/GeoCacheService.cs`:

1. **Database connection** to your existing IP geo table (342M records)
   - Configure connection string in appsettings.json
   - Table name and column mappings configurable
2. **Lookup method:**
   ```csharp
   public GeoResult? LookupFromCache(string ipAddress);
   ```
3. **Batch lookup method:**
   ```csharp
   public Dictionary<string, GeoResult> LookupBatch(IEnumerable<string> ipAddresses);
   ```
4. Return `GeoResult` record:
   ```csharp
   public record GeoResult(
       string? Country,
       string? CountryCode,
       string? Region,
       string? City,
       double? Latitude,
       double? Longitude,
       string? Timezone,
       string? ISP,
       string? Organization,
       DateTime? CachedAt
   );
   ```
5. Handle cache misses - return null, track for later IP-API batch

**Configuration needed:**
```json
{
  "GeoCache": {
    "ConnectionString": "...",
    "TableName": "IP_Geo_Cache",
    "MaxBatchSize": 1000
  }
}
```

**Files to create:**
- `Services/GeoCacheService.cs`
- `Models/GeoResult.cs`
- `Configuration/GeoCacheSettings.cs`

---

#### Task 3.3: Expand TrackingData Model
**Priority:** P0  
**Effort:** 1 hour

Update `Models/TrackingData.cs`:

Add properties:
```csharp
// IP Classification
public string? IpType { get; init; }           // "Public", "Private", etc.
public bool IpShouldGeolocate { get; init; }

// Geo Data (from cache or API)
public string? GeoCountry { get; init; }
public string? GeoCountryCode { get; init; }
public string? GeoRegion { get; init; }
public string? GeoCity { get; init; }
public double? GeoLatitude { get; init; }
public double? GeoLongitude { get; init; }
public string? GeoTimezone { get; init; }
public string? GeoISP { get; init; }
public bool GeoFromCache { get; init; }        // true = from our cache, false = needs API

// Bot Signals
public bool GeoMismatch { get; init; }         // IP geo timezone != reported tz
```

---

#### Task 3.4: Update Database Schema
**Priority:** P0  
**Effort:** 1-2 hours

Create `SQL/04_GeoAndClassificationColumns.sql`:

1. Add columns to PiXL_Test:
   ```sql
   ALTER TABLE PiXL_Test ADD
       IpType NVARCHAR(20) NULL,
       GeoCountry NVARCHAR(100) NULL,
       GeoCountryCode NVARCHAR(5) NULL,
       GeoRegion NVARCHAR(100) NULL,
       GeoCity NVARCHAR(100) NULL,
       GeoLatitude DECIMAL(9,6) NULL,
       GeoLongitude DECIMAL(9,6) NULL,
       GeoTimezone NVARCHAR(50) NULL,
       GeoISP NVARCHAR(200) NULL,
       GeoFromCache BIT NULL,
       GeoMismatch BIT NULL;
   ```
2. Update `CreateDataTableTemplate()` in DatabaseWriterService
3. Update column mappings in `AddColumnMappings()`
4. Add indexes:
   ```sql
   CREATE INDEX IX_PiXL_Test_GeoCountry ON PiXL_Test(GeoCountry);
   CREATE INDEX IX_PiXL_Test_IpType ON PiXL_Test(IpType);
   CREATE INDEX IX_PiXL_Test_GeoMismatch ON PiXL_Test(GeoMismatch) WHERE GeoMismatch = 1;
   ```

---

#### Task 3.5: Integrate Geo Lookup into Batch Writer
**Priority:** P0  
**Effort:** 2-3 hours

Modify `Services/DatabaseWriterService.cs`:

1. Inject `IpClassificationService` and `GeoCacheService`
2. In `WriteBatchAsync`, before building the DataTable:
   ```csharp
   // Collect unique IPs from batch
   var uniqueIps = batch
       .Select(d => d.IPAddress)
       .Where(ip => !string.IsNullOrEmpty(ip))
       .Distinct()
       .ToList();
   
   // Classify all IPs
   var classifications = uniqueIps.ToDictionary(
       ip => ip,
       ip => _ipClassifier.Classify(ip)
   );
   
   // Lookup geo for geolocatable IPs
   var geolocatableIps = classifications
       .Where(kv => kv.Value.ShouldGeolocate)
       .Select(kv => kv.Key)
       .ToList();
   
   var geoResults = _geoCache.LookupBatch(geolocatableIps);
   ```
3. When building each row, populate geo fields from lookup results
4. Track IPs that weren't in cache (for later IP-API batch)

---

#### Task 3.6: Bot Detection - Geo Mismatch
**Priority:** P1  
**Effort:** 2 hours

Create `Services/BotDetectionService.cs`:

1. **Timezone mismatch detection:**
   ```csharp
   public bool DetectGeoMismatch(string? reportedTimezone, int? reportedOffset, string? geoTimezone)
   ```
   - Parse reported timezone (from JS `tz` param) 
   - Compare to geo timezone from IP lookup
   - Allow ~2 hour tolerance (DST, regional variations)
   - Return true if significant mismatch

2. **Extract timezone offset from IANA timezone:**
   - Use `TimeZoneInfo.FindSystemTimeZoneById()` or NodaTime
   - Handle common IANA names: "America/New_York", "Europe/London", etc.

---

#### Task 3.7: Queue Unresolved IPs for IP-API
**Priority:** P2  
**Effort:** 2-3 hours

Create mechanism to track IPs that need external resolution:

1. Add table or queue for pending IPs:
   ```sql
   CREATE TABLE IP_Pending_Geo (
       IPAddress NVARCHAR(50) PRIMARY KEY,
       FirstSeen DATETIME2 DEFAULT GETUTCDATE(),
       AttemptCount INT DEFAULT 0
   );
   ```
2. After batch insert, log any IPs that weren't in cache
3. Separate background job (can be existing process) picks up pending IPs
4. After IP-API resolution, update PiXL_Test records retroactively

**Note:** This maintains compatibility with your existing IP-API batch process.

---

### Epic: Bot Detection Enhancement

#### Task 4.1: Known Bot User Agent Detection
**Priority:** P1  
**Effort:** 2-3 hours

Add to `Services/BotDetectionService.cs`:

1. Compiled regex patterns for known bots:
   ```csharp
   private static readonly Regex[] BotPatterns = [
       new Regex(@"Googlebot|Bingbot|YandexBot|Baiduspider", RegexOptions.Compiled),
       new Regex(@"facebookexternalhit|LinkedInBot|Twitterbot", RegexOptions.Compiled),
       new Regex(@"HeadlessChrome|PhantomJS|Selenium|Puppeteer", RegexOptions.Compiled),
       new Regex(@"curl/|wget/|python-requests/|Go-http-client/", RegexOptions.Compiled)
   ];
   ```
2. Return `BotType` enum or null
3. Add `IsKnownBot` and `BotType` fields to TrackingData

---

#### Task 4.2: Datacenter IP Detection
**Priority:** P2  
**Effort:** 3-4 hours

1. Download and cache major cloud provider IP ranges
   - AWS, GCP, Azure, Cloudflare, DigitalOcean
2. Create lookup service with periodic refresh (weekly)
3. Add `IsDatacenterIP` field to TrackingData

---

### Epic: Mobile Ad ID Integration

#### Task 5.1: Design Cross-System Join
**Priority:** P2 (Future)  
**Effort:** Design phase

**Goal:** Link SmartPiXL website visitors to mobile ad IDs via shared IP.

1. Document your location service schema (IP + AdID tables)
2. Design join strategy:
   - Real-time lookup during ingest?
   - Batch join after insert?
   - Time window matching (IP + timestamp within X minutes)?
3. Define enrichment fields:
   - `LinkedAdID`
   - `LocationLat`, `LocationLon` (GPS precision)
   - `DeviceType` from location service
4. Privacy/compliance review

---

### Epic: Dashboard (Future)

#### Task 6.1: "3026" Futuristic Dashboard
**Priority:** P3 (Future)  
**Effort:** TBD

- Real-time metrics visualization
- Fingerprint uniqueness analysis
- Bot traffic breakdown
- Geographic heatmaps
- Device/browser distribution

---

## 📊 Priority Matrix

| Task | Priority | Effort | Dependencies |
|------|----------|--------|--------------|
| 3.1 IP Classification | P0 | 2-3h | None |
| 3.2 Geo Cache Service | P0 | 3-4h | 3.1 |
| 3.3 Expand TrackingData | P0 | 1h | None |
| 3.4 SQL Schema Update | P0 | 1-2h | 3.3 |
| 3.5 Integrate into Writer | P0 | 2-3h | 3.1, 3.2, 3.4 |
| 3.6 Geo Mismatch Detection | P1 | 2h | 3.5 |
| 3.7 Queue Unresolved IPs | P2 | 2-3h | 3.5 |
| 4.1 Bot UA Detection | P1 | 2-3h | None |
| 4.2 Datacenter IP Detection | P2 | 3-4h | 3.1 |
| 5.1 AdID Integration Design | P2 | Design | 3.5 |
| 6.1 Dashboard | P3 | TBD | All above |

---

## 🔧 Configuration Additions Needed

```json
{
  "GeoCache": {
    "ConnectionString": "Server=...;Database=IP_Geo;...",
    "TableName": "IP_Geo_Cache",
    "IpColumn": "IPAddress",
    "CountryColumn": "Country",
    "RegionColumn": "Region",
    "CityColumn": "City",
    "LatitudeColumn": "Latitude",
    "LongitudeColumn": "Longitude",
    "TimezoneColumn": "Timezone",
    "ISPColumn": "ISP"
  },
  
  "BotDetection": {
    "EnableUADetection": true,
    "EnableDatacenterDetection": true,
    "GeoMismatchToleranceHours": 2
  }
}
```

---

## 📁 New Files to Create

```
Services/
  IpClassificationService.cs
  GeoCacheService.cs
  BotDetectionService.cs

Models/
  IpClassification.cs
  GeoResult.cs
  BotSignals.cs

Configuration/
  GeoCacheSettings.cs
  BotDetectionSettings.cs

SQL/
  04_GeoAndClassificationColumns.sql

docs/
  RESERVED_IP_RANGES.md  ✅ (created)
```

---

## 📝 Notes

- **IP-API compatibility:** The geo cache approach maintains compatibility with your existing IP-API batch process. New IPs that aren't cached will still get queued for IP-API resolution.

- **Performance impact:** The geo cache lookup adds minimal latency to the batch writer. A batch of 100 IPs against a 342M row indexed table should complete in <10ms.

- **Timezone comparison:** Comparing IANA timezone names to IP-derived timezones requires careful handling. Consider using NodaTime for robust timezone math.

- **IPv6 readiness:** All services should handle both IPv4 and IPv6 from day one. The reserved ranges document includes both.
