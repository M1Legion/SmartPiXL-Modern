using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SmartPiXL.Configuration;
using SmartPiXL.Forge.Services.Enrichments;
using SmartPiXL.Services;

namespace SmartPiXL.Forge.Services;

// ============================================================================
// IP DATA ACQUISITION SERVICE
//
// Automated pipeline to download, cache, and import free public IP data:
//
//   Source               Format     Update   Priority  SourceId
//   ─────────────────────────────────────────────────────────────
//   IPtoASN              TSV.gz     Hourly   1 (ASN)   4
//   DB-IP Lite City      CSV.gz     Monthly  1 (Geo)   2
//   AWS IP Ranges        JSON       Live     1 (DC)    8
//   GCP IP Ranges        JSON       Live     2 (DC)    9
//   Azure IP Ranges      JSON       Weekly   3 (DC)   10
//   Cloudflare Ranges    Text       Live     4 (DC)   11
//   IP2Location DB11     CSV        Monthly  2 (Geo)   3  ← future (needs acct)
//   IP2Proxy LITE PX11   CSV        BiWeek   1 (Prox)  7  ← future (needs acct)
//   BGP.tools ASN        CSV        Periodic 1 (Meta)  6
//
// LIFECYCLE:
//   1. StartAsync: initial load on service start
//   2. Daily check: download if upstream changed (SHA-256 comparison)
//   3. Import: parse CSV/TSV → SqlBulkCopy → staging table → swap
//   4. Log: record import metadata to IPInfo.ImportLog
//
// DESIGN PRINCIPLES:
//   - SP-first: SQL import procs handle the heavy lifting
//   - File-hash dedup: skip re-import if file unchanged
//   - Atomic swap: staging → live via sp_rename (zero-downtime)
//   - Idempotent: safe to re-run after crash
// ============================================================================

/// <summary>
/// Background service that downloads and imports free public IP data
/// into the IPInfo SQL schema on a configurable schedule.
/// </summary>
public sealed class IpDataAcquisitionService : BackgroundService
{
    private readonly ForgeSettings _forgeSettings;
    private readonly TrackingSettings _trackingSettings;
    private readonly ITrackingLogger _logger;
    private readonly HttpClient _http;
    private readonly IpRangeLookupService _ipRangeLookup;
    private string _dataDir = null!;

    // Known data source URLs
    private static readonly Uri IpToAsnV4Uri = new("https://iptoasn.com/data/ip2asn-v4.tsv.gz");
    private static readonly Uri IpToAsnV6Uri = new("https://iptoasn.com/data/ip2asn-v6.tsv.gz");
    private static readonly Uri DbIpCityLiteUri = new("https://download.db-ip.com/free/dbip-city-lite-{0}.csv.gz");
    private static readonly Uri AwsRangesUri = new("https://ip-ranges.amazonaws.com/ip-ranges.json");
    private static readonly Uri GcpRangesUri = new("https://www.gstatic.com/ipranges/cloud.json");
    private static readonly Uri CloudflareV4Uri = new("https://www.cloudflare.com/ips-v4/");
    private static readonly Uri CloudflareV6Uri = new("https://www.cloudflare.com/ips-v6/");

    public IpDataAcquisitionService(
        IOptions<ForgeSettings> forgeSettings,
        IOptions<TrackingSettings> trackingSettings,
        ITrackingLogger logger,
        HttpClient http,
        IpRangeLookupService ipRangeLookup)
    {
        _forgeSettings = forgeSettings.Value;
        _trackingSettings = trackingSettings.Value;
        _logger = logger;
        _http = http;
        _ipRangeLookup = ipRangeLookup;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _dataDir = Path.IsPathRooted(_forgeSettings.IpDataDirectory)
            ? _forgeSettings.IpDataDirectory
            : Path.Combine(AppContext.BaseDirectory, _forgeSettings.IpDataDirectory);
        Directory.CreateDirectory(_dataDir);

        // Initial startup delay (let pipe + enrichment stabilize first)
        _logger.Info($"IpDataAcquisition: data directory = {_dataDir}");
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);

        // Run on startup, then daily at configured hour
        await RunAllImportsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var nextRun = now.Date.AddDays(1).AddHours(_forgeSettings.IpDataAcquisitionHourUtc);
            if (nextRun <= now) nextRun = nextRun.AddDays(1);
            var delay = nextRun - now;

            _logger.Info($"IpDataAcquisition: next run at {nextRun:yyyy-MM-dd HH:mm} UTC ({delay.TotalHours:F1}h)");
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }

            await RunAllImportsAsync(stoppingToken);
        }
    }

    /// <summary>Downloads and imports all data sources.</summary>
    private async Task RunAllImportsAsync(CancellationToken ct)
    {
        _logger.Info("IpDataAcquisition: starting import cycle...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try { await ImportIpToAsnAsync(ct); }
        catch (Exception ex) { _logger.Error($"IpDataAcquisition [IPtoASN]: {ex.Message}"); }

        try { await ImportDbIpCityLiteAsync(ct); }
        catch (Exception ex) { _logger.Error($"IpDataAcquisition [DB-IP]: {ex.Message}"); }

        // Cloud provider ranges are already loaded by DatacenterIpService at startup.
        // Future: import them into SQL tables too for ETL enrichment.

        sw.Stop();
        _logger.Info($"IpDataAcquisition: import cycle complete in {sw.Elapsed.TotalSeconds:F1}s");

        // Hot-reload in-memory range tables so enrichment uses fresh data
        await _ipRangeLookup.ReloadAsync(ct);
    }

    // ========================================================================
    // IPtoASN Import — TSV format: range_start\trange_end\tAS_number\tcc\tAS_desc
    // ========================================================================
    private async Task ImportIpToAsnAsync(CancellationToken ct)
    {
        var localPath = Path.Combine(_dataDir, "ip2asn-v4.tsv.gz");

        // Download if changed
        if (!await DownloadIfChangedAsync(IpToAsnV4Uri, localPath, ct))
        {
            _logger.Info("IpDataAcquisition [IPtoASN]: file unchanged — skipped");
            return;
        }

        _logger.Info("IpDataAcquisition [IPtoASN]: importing...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rowCount = 0;

        await using var conn = new SqlConnection(_trackingSettings.ConnectionString);
        await conn.OpenAsync(ct);

        // Create staging table
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                IF OBJECT_ID('IPInfo.AsnRange_Staging', 'U') IS NOT NULL DROP TABLE IPInfo.AsnRange_Staging;
                CREATE TABLE IPInfo.AsnRange_Staging (
                    IpStart        VARBINARY(16) NOT NULL,
                    IpEnd          VARBINARY(16) NOT NULL,
                    AddrFamily     TINYINT       NOT NULL DEFAULT 4,
                    SourceId       TINYINT       NOT NULL DEFAULT 4,
                    AsnNumber      INT           NOT NULL,
                    CountryCode    CHAR(2)       NULL,
                    AsnDescription VARCHAR(256)  NULL
                );";
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Parse TSV and bulk-load
        using var dataTable = new DataTable();
        dataTable.Columns.Add("IpStart", typeof(byte[]));
        dataTable.Columns.Add("IpEnd", typeof(byte[]));
        dataTable.Columns.Add("AddrFamily", typeof(byte));
        dataTable.Columns.Add("SourceId", typeof(byte));
        dataTable.Columns.Add("AsnNumber", typeof(int));
        dataTable.Columns.Add("CountryCode", typeof(string));
        dataTable.Columns.Add("AsnDescription", typeof(string));

        await using var fs = File.OpenRead(localPath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split('\t');
            if (parts.Length < 5) continue;

            var startBin = IpToBinary(parts[0]);
            var endBin = IpToBinary(parts[1]);
            if (startBin == null || endBin == null) continue;

            if (!int.TryParse(parts[2], out var asn) || asn <= 0) continue;

            var cc = parts[3].Length == 2 ? parts[3] : null;
            var desc = parts[4].Length > 256 ? parts[4][..256] : parts[4];

            dataTable.Rows.Add(startBin, endBin, (byte)4, (byte)4, asn, cc, desc);
            rowCount++;

            // Batch flush every 100K rows
            if (dataTable.Rows.Count >= 100_000)
            {
                await BulkCopyAsync(conn, "IPInfo.AsnRange_Staging", dataTable, ct);
                dataTable.Clear();
            }
        }

        // Flush remainder
        if (dataTable.Rows.Count > 0)
            await BulkCopyAsync(conn, "IPInfo.AsnRange_Staging", dataTable, ct);

        // Ensure ASN dimension rows exist (without FK, for now insert distinct ASNs)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                INSERT INTO IPInfo.ASN (AsnNumber, Name, Organization, SourceId)
                SELECT DISTINCT s.AsnNumber, s.AsnDescription, s.AsnDescription, 4
                FROM IPInfo.AsnRange_Staging s
                WHERE NOT EXISTS (SELECT 1 FROM IPInfo.ASN a WHERE a.AsnNumber = s.AsnNumber);";
            cmd.CommandTimeout = 300;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Build clustered index on staging
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE CLUSTERED INDEX IX_Staging_Lookup ON IPInfo.AsnRange_Staging (AddrFamily, IpStart);";
            cmd.CommandTimeout = 300;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Atomic swap
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                -- Drop FK constraint temporarily
                IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_IPInfo_AsnRange_Source')
                    ALTER TABLE IPInfo.AsnRange DROP CONSTRAINT FK_IPInfo_AsnRange_Source;
                IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_IPInfo_AsnRange_ASN')
                    ALTER TABLE IPInfo.AsnRange DROP CONSTRAINT FK_IPInfo_AsnRange_ASN;
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'PK_IPInfo_AsnRange' AND object_id = OBJECT_ID('IPInfo.AsnRange'))
                    ALTER TABLE IPInfo.AsnRange DROP CONSTRAINT PK_IPInfo_AsnRange;
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_IPInfo_AsnRange_Lookup' AND object_id = OBJECT_ID('IPInfo.AsnRange'))
                    DROP INDEX IX_IPInfo_AsnRange_Lookup ON IPInfo.AsnRange;

                -- Swap
                IF OBJECT_ID('IPInfo.AsnRange_Old', 'U') IS NOT NULL DROP TABLE IPInfo.AsnRange_Old;
                EXEC sp_rename 'IPInfo.AsnRange', 'AsnRange_Old';
                EXEC sp_rename 'IPInfo.AsnRange_Staging', 'AsnRange';
                IF OBJECT_ID('IPInfo.AsnRange_Old', 'U') IS NOT NULL DROP TABLE IPInfo.AsnRange_Old;";
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        sw.Stop();
        _logger.Info($"IpDataAcquisition [IPtoASN]: imported {rowCount:N0} ranges in {sw.Elapsed.TotalSeconds:F1}s");

        await LogImportAsync(4, "AsnRange", rowCount, (int)sw.ElapsedMilliseconds,
            Path.GetFileName(localPath), null, ct);
    }

    // ========================================================================
    // DB-IP Lite City Import — CSV: ip_start,ip_end,continent,cc,region,city,lat,lon
    // ========================================================================
    private async Task ImportDbIpCityLiteAsync(CancellationToken ct)
    {
        // DB-IP publishes monthly files named by year-month
        var yearMonth = DateTime.UtcNow.ToString("yyyy-MM");
        var url = new Uri(string.Format(DbIpCityLiteUri.ToString(), yearMonth));
        var localPath = Path.Combine(_dataDir, $"dbip-city-lite-{yearMonth}.csv.gz");

        if (!await DownloadIfChangedAsync(url, localPath, ct))
        {
            _logger.Info("IpDataAcquisition [DB-IP]: file unchanged — skipped");
            return;
        }

        _logger.Info("IpDataAcquisition [DB-IP]: importing...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rowCount = 0;

        await using var conn = new SqlConnection(_trackingSettings.ConnectionString);
        await conn.OpenAsync(ct);

        // Create staging table
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                IF OBJECT_ID('IPInfo.GeoRange_Staging', 'U') IS NOT NULL DROP TABLE IPInfo.GeoRange_Staging;
                CREATE TABLE IPInfo.GeoRange_Staging (
                    IpStart        VARBINARY(16) NOT NULL,
                    IpEnd          VARBINARY(16) NOT NULL,
                    AddrFamily     TINYINT       NOT NULL,
                    SourceId       TINYINT       NOT NULL DEFAULT 2,
                    CountryCode    CHAR(2)       NULL,
                    Region         VARCHAR(128)  NULL,
                    RegionCode     VARCHAR(10)   NULL,
                    City           VARCHAR(128)  NULL,
                    PostalCode     VARCHAR(20)   NULL,
                    Latitude       DECIMAL(9,6)  NULL,
                    Longitude      DECIMAL(9,6)  NULL,
                    Timezone       VARCHAR(64)   NULL,
                    Continent      CHAR(2)       NULL
                );";
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using var dataTable = new DataTable();
        dataTable.Columns.Add("IpStart", typeof(byte[]));
        dataTable.Columns.Add("IpEnd", typeof(byte[]));
        dataTable.Columns.Add("AddrFamily", typeof(byte));
        dataTable.Columns.Add("SourceId", typeof(byte));
        dataTable.Columns.Add("CountryCode", typeof(string));
        dataTable.Columns.Add("Region", typeof(string));
        dataTable.Columns.Add("RegionCode", typeof(string));
        dataTable.Columns.Add("City", typeof(string));
        dataTable.Columns.Add("PostalCode", typeof(string));
        dataTable.Columns.Add("Latitude", typeof(decimal));
        dataTable.Columns.Add("Longitude", typeof(decimal));
        dataTable.Columns.Add("Timezone", typeof(string));
        dataTable.Columns.Add("Continent", typeof(string));

        await using var fs = File.OpenRead(localPath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // DB-IP CSV: ip_start,ip_end,continent,country,state,city,lat,lon
            var fields = ParseCsvLine(line);
            if (fields.Length < 8) continue;

            var startBin = IpToBinary(fields[0]);
            var endBin = IpToBinary(fields[1]);
            if (startBin == null || endBin == null) continue;

            byte af = (byte)(fields[0].Contains(':') ? 6 : 4);
            var continent = fields[2].Length <= 2 ? fields[2] : null;
            var cc = fields[3].Length == 2 ? fields[3] : null;
            var region = fields[4].Length > 128 ? fields[4][..128] : fields[4];
            var city = fields[5].Length > 128 ? fields[5][..128] : fields[5];

            decimal.TryParse(fields[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat);
            decimal.TryParse(fields[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon);

            dataTable.Rows.Add(startBin, endBin, af, (byte)2, cc, region,
                DBNull.Value, city, DBNull.Value, lat, lon, DBNull.Value, continent);
            rowCount++;

            if (dataTable.Rows.Count >= 100_000)
            {
                await BulkCopyAsync(conn, "IPInfo.GeoRange_Staging", dataTable, ct);
                dataTable.Clear();
            }
        }

        if (dataTable.Rows.Count > 0)
            await BulkCopyAsync(conn, "IPInfo.GeoRange_Staging", dataTable, ct);

        // Build clustered index on staging
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE CLUSTERED INDEX IX_Staging_Lookup ON IPInfo.GeoRange_Staging (AddrFamily, IpStart);";
            cmd.CommandTimeout = 600;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Atomic swap
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_IPInfo_GeoRange_Source')
                    ALTER TABLE IPInfo.GeoRange DROP CONSTRAINT FK_IPInfo_GeoRange_Source;
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'PK_IPInfo_GeoRange' AND object_id = OBJECT_ID('IPInfo.GeoRange'))
                    ALTER TABLE IPInfo.GeoRange DROP CONSTRAINT PK_IPInfo_GeoRange;
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_IPInfo_GeoRange_Lookup' AND object_id = OBJECT_ID('IPInfo.GeoRange'))
                    DROP INDEX IX_IPInfo_GeoRange_Lookup ON IPInfo.GeoRange;
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_IPInfo_GeoRange_V4_Lookup' AND object_id = OBJECT_ID('IPInfo.GeoRange'))
                    DROP INDEX IX_IPInfo_GeoRange_V4_Lookup ON IPInfo.GeoRange;

                IF OBJECT_ID('IPInfo.GeoRange_Old', 'U') IS NOT NULL DROP TABLE IPInfo.GeoRange_Old;
                EXEC sp_rename 'IPInfo.GeoRange', 'GeoRange_Old';
                EXEC sp_rename 'IPInfo.GeoRange_Staging', 'GeoRange';
                IF OBJECT_ID('IPInfo.GeoRange_Old', 'U') IS NOT NULL DROP TABLE IPInfo.GeoRange_Old;";
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        sw.Stop();
        _logger.Info($"IpDataAcquisition [DB-IP]: imported {rowCount:N0} ranges in {sw.Elapsed.TotalSeconds:F1}s");

        await LogImportAsync(2, "GeoRange", rowCount, (int)sw.ElapsedMilliseconds,
            Path.GetFileName(localPath), null, ct);
    }

    // ========================================================================
    // HELPERS
    // ========================================================================

    /// <summary>
    /// Downloads a file if the remote content has changed (HTTP ETag/If-Modified-Since).
    /// Returns true if a new file was downloaded, false if unchanged.
    /// </summary>
    private async Task<bool> DownloadIfChangedAsync(Uri uri, string localPath, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        // Conditional download: if local file exists, use If-Modified-Since
        if (File.Exists(localPath))
            request.Headers.IfModifiedSince = File.GetLastWriteTimeUtc(localPath);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.NotModified)
            return false;

        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        var tempPath = localPath + ".tmp";
        await using (var fileStream = File.Create(tempPath))
        {
            await responseStream.CopyToAsync(fileStream, ct);
        }

        // Atomic replace
        File.Move(tempPath, localPath, overwrite: true);
        return true;
    }

    /// <summary>Converts a dotted-quad IPv4 or colon-hex IPv6 string to binary.</summary>
    private static byte[]? IpToBinary(string ip)
    {
        if (!IPAddress.TryParse(ip.Trim(), out var addr)) return null;
        var bytes = addr.GetAddressBytes();
        return bytes; // 4 bytes for IPv4, 16 bytes for IPv6
    }

    /// <summary>Parses a CSV line respecting quoted fields (handles commas inside quotes).</summary>
    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var inQuote = false;
        var start = 0;

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                inQuote = !inQuote;
            else if (line[i] == ',' && !inQuote)
            {
                fields.Add(UnquoteCsv(line.AsSpan(start, i - start)));
                start = i + 1;
            }
        }
        fields.Add(UnquoteCsv(line.AsSpan(start)));
        return fields.ToArray();
    }

    private static string UnquoteCsv(ReadOnlySpan<char> field)
    {
        if (field.Length >= 2 && field[0] == '"' && field[^1] == '"')
            return field[1..^1].ToString().Replace("\"\"", "\"");
        return field.ToString();
    }

    /// <summary>Bulk copies a DataTable to a SQL Server table.</summary>
    private static async Task BulkCopyAsync(SqlConnection conn, string tableName, DataTable data, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = tableName,
            BulkCopyTimeout = 600,
            BatchSize = 50_000
        };
        await bulk.WriteToServerAsync(data, ct);
    }

    /// <summary>Logs an import result to IPInfo.ImportLog.</summary>
    private async Task LogImportAsync(int sourceId, string syncType, int rowCount,
        int durationMs, string? fileName, string? error, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(_trackingSettings.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO IPInfo.ImportLog (SourceId, SyncType, RowsImported, DurationMs, FileName, ErrorMessage, CompletedAt)
                VALUES (@SourceId, @SyncType, @Rows, @Ms, @File, @Error, SYSUTCDATETIME())";
            cmd.Parameters.AddWithValue("@SourceId", sourceId);
            cmd.Parameters.AddWithValue("@SyncType", syncType);
            cmd.Parameters.AddWithValue("@Rows", rowCount);
            cmd.Parameters.AddWithValue("@Ms", durationMs);
            cmd.Parameters.AddWithValue("@File", (object?)fileName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Error", (object?)error ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            // Non-critical — don't fail the import over logging
        }
    }
}
