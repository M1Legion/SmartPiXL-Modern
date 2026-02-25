<#
.SYNOPSIS
    Downloads and imports RFC 8805 geofeed data into Ref.Geofeed.
.DESCRIPTION
    RFC 8805 geofeeds are CSV files published by network operators declaring
    "this IP range is located in this country/region/city." This is the most
    authoritative geo source possible - the network owner is telling you
    where their IPs actually serve from.

    This script uses the OpenGeoFeed project's aggregated feed, which collects
    geofeeds from WHOIS inetnum/inet6num objects across all RIRs.

    Source: https://opengeofeed.org/downloadGeofeedCSV
    Alternative: https://geocommons.opengeofeed.org/

    If the aggregated feed is unavailable, major operators publish their own:
      - Cloudflare: referenced in their WHOIS objects
      - Google: referenced in their WHOIS objects
      - Amazon: referenced in their WHOIS objects

    Geofeed CSV format (RFC 8805 §2.1):
      ip_prefix,country_code,region_code,city,zip_code

    Comments start with #. Region is ISO 3166-2 format (e.g., US-CA).
.EXAMPLE
    .\Import-Geofeed.ps1
    .\Import-Geofeed.ps1 -CsvPath ".\data\geofeed-aggregated.csv"
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $false)]
    [string]$CsvPath,

    [string]$SqlInstance = 'localhost\SQL2025',
    [string]$Database   = 'SmartPiXL',
    [string]$DataDir    = (Join-Path $PSScriptRoot '..\data')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path $DataDir)) { New-Item -ItemType Directory -Path $DataDir -Force | Out-Null }

function ConvertTo-IpInt([string]$ip) {
    $octets = $ip.Split('.')
    [long]$octets[0] * 16777216 + [long]$octets[1] * 65536 + [long]$octets[2] * 256 + [long]$octets[3]
}

function Expand-Cidr([string]$cidr) {
    $parts  = $cidr.Split('/')
    $ip     = $parts[0]
    $prefix = [int]$parts[1]
    $startInt = ConvertTo-IpInt $ip
    $hostCount = [Math]::Pow(2, 32 - $prefix)
    $endInt = $startInt + [long]$hostCount - 1
    return @{ StartInt = $startInt; EndInt = $endInt }
}

$startTime = Get-Date

# -- Find or download the geofeed CSV --
if (-not $CsvPath) {
    $file = Join-Path $DataDir 'geofeed-aggregated.csv'

    # Try OpenGeoFeed aggregated download
    $urls = @(
        'https://opengeofeed.org/downloadGeofeedCSV'
        'https://geocommons.opengeofeed.org/geofeed.csv'
    )

    $downloaded = $false
    foreach ($url in $urls) {
        Write-Host "Trying: $url ..."
        try {
            $headers = @{ 'User-Agent' = 'SmartPiXL-Research/1.0 (data import)' }
            Invoke-WebRequest -Uri $url -Headers $headers -UseBasicParsing -OutFile $file -TimeoutSec 60
            $sz = (Get-Item $file).Length
            if ($sz -gt 1000) {
                $downloaded = $true
                Write-Host "Downloaded: $([Math]::Round($sz / 1MB, 1))MB"
                break
            }
            else {
                Write-Host "File too small ($sz bytes) - trying next source"
            }
        }
        catch {
            Write-Host "Failed: $($_.Exception.Message)"
        }
    }

    if (-not $downloaded) {
        Write-Host ""
        Write-Host "Could not auto-download aggregated geofeeds." -ForegroundColor Yellow
        Write-Host "You can manually download from:"
        Write-Host "  - https://opengeofeed.org/"
        Write-Host "  - https://geocommons.opengeofeed.org/"
        Write-Host "Place the CSV in: $DataDir"
        Write-Host "Then run: .\Import-Geofeed.ps1 -CsvPath '<path>'"
        Write-Host ""
        Write-Host "Alternatively, download individual operator geofeeds:"
        Write-Host "  Cloudflare, Google, Amazon all publish geofeeds in their WHOIS objects."
        Write-Host ""
        Write-Host "Skipping geofeed import - this source can be added later."
        return
    }
    $CsvPath = $file
}

if (-not (Test-Path $CsvPath)) {
    Write-Error "File not found: $CsvPath"
    return
}

Write-Host "Parsing geofeed: $CsvPath"

# -- Parse --
$rows = [System.Collections.Generic.List[PSObject]]::new(500000)
$lineNum = 0
$skipped = 0

foreach ($line in [System.IO.File]::ReadLines($CsvPath)) {
    $lineNum++
    $trimmed = $line.Trim()

    # Skip comments and empty lines
    if ($trimmed.StartsWith('#') -or [string]::IsNullOrEmpty($trimmed)) { continue }

    $fields = $trimmed.Split(',')
    if ($fields.Count -lt 2) { $skipped++; continue }

    $cidr = $fields[0].Trim()

    # Skip IPv6
    if ($cidr.Contains(':')) { continue }

    # Validate CIDR format
    if (-not $cidr.Contains('/')) { $skipped++; continue }

    $cc = if ($fields.Count -ge 2) { $fields[1].Trim().ToUpper() } else { '' }
    if ($cc.Length -ne 2) { $skipped++; continue }

    $region = if ($fields.Count -ge 3) { $fields[2].Trim() } else { $null }
    $city   = if ($fields.Count -ge 4) { $fields[3].Trim() } else { $null }
    $zip    = if ($fields.Count -ge 5) { $fields[4].Trim() } else { $null }

    try {
        $range = Expand-Cidr $cidr
    }
    catch {
        $skipped++
        continue
    }

    # Determine source from comment lines (simplified - use filename)
    $rows.Add([PSCustomObject]@{
        NetworkCidr = $cidr
        StartInt    = $range.StartInt
        EndInt      = $range.EndInt
        CountryCode = $cc
        Region      = $region
        City        = $city
        Zip         = $zip
        Source       = [System.IO.Path]::GetFileName($CsvPath)
    })
}

Write-Host "Parsed $($rows.Count) IPv4 geofeed entries ($skipped skipped)"

if ($rows.Count -eq 0) {
    Write-Host "No rows to import." -ForegroundColor Yellow
    return
}

if (-not $PSCmdlet.ShouldProcess("Ref.Geofeed", "TRUNCATE + INSERT $($rows.Count) rows")) { return }

# -- Build DataTable --
$dt = [System.Data.DataTable]::new()
$dt.Columns.Add('NetworkCidr', [string]) | Out-Null
$dt.Columns.Add('StartInt',    [long])   | Out-Null
$dt.Columns.Add('EndInt',      [long])   | Out-Null
$dt.Columns.Add('CountryCode', [string]) | Out-Null
$dt.Columns.Add('Region',      [string]) | Out-Null
$dt.Columns.Add('City',        [string]) | Out-Null
$dt.Columns.Add('Zip',         [string]) | Out-Null
$dt.Columns.Add('Source',      [string]) | Out-Null

foreach ($r in $rows) {
    $row = $dt.NewRow()
    $row['NetworkCidr'] = $r.NetworkCidr
    $row['StartInt']    = $r.StartInt
    $row['EndInt']      = $r.EndInt
    $row['CountryCode'] = $r.CountryCode
    $row['Region']      = if ($r.Region) { $r.Region } else { [DBNull]::Value }
    $row['City']        = if ($r.City)   { $r.City }   else { [DBNull]::Value }
    $row['Zip']         = if ($r.Zip)    { $r.Zip }    else { [DBNull]::Value }
    $row['Source']      = $r.Source
    $dt.Rows.Add($row)
}

# -- Bulk insert --
$connStr = "Server=$SqlInstance;Database=$Database;Integrated Security=True;TrustServerCertificate=True"
$conn = [System.Data.SqlClient.SqlConnection]::new($connStr)
$conn.Open()

try {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = 'SELECT COUNT(*) FROM Ref.Geofeed'
    $oldCount = [int]$cmd.ExecuteScalar()

    $cmd.CommandText = 'TRUNCATE TABLE Ref.Geofeed'
    $cmd.ExecuteNonQuery() | Out-Null

    $bc = [System.Data.SqlClient.SqlBulkCopy]::new($conn)
    $bc.DestinationTableName = 'Ref.Geofeed'
    $bc.BatchSize = 50000
    $bc.BulkCopyTimeout = 300
    $bc.ColumnMappings.Add('NetworkCidr', 'NetworkCidr') | Out-Null
    $bc.ColumnMappings.Add('StartInt',    'StartInt')    | Out-Null
    $bc.ColumnMappings.Add('EndInt',      'EndInt')      | Out-Null
    $bc.ColumnMappings.Add('CountryCode', 'CountryCode') | Out-Null
    $bc.ColumnMappings.Add('Region',      'Region')      | Out-Null
    $bc.ColumnMappings.Add('City',        'City')        | Out-Null
    $bc.ColumnMappings.Add('Zip',         'Zip')         | Out-Null
    $bc.ColumnMappings.Add('Source',      'Source')       | Out-Null

    $bc.WriteToServer($dt)

    $durationMs = [int]((Get-Date) - $startTime).TotalMilliseconds
    $cmd.CommandText = ('INSERT INTO Ref.ImportLog (SourceName, RowsLoaded, RowsReplaced, DurationMs, Notes) VALUES (''Geofeed'', {0}, {1}, {2}, ''RFC 8805 aggregated'')' -f $rows.Count, $oldCount, $durationMs)
    $cmd.ExecuteNonQuery() | Out-Null

    Write-Host ''
    Write-Host ('SUCCESS: Loaded {0} rows into Ref.Geofeed (replaced {1})' -f $rows.Count, $oldCount)
    Write-Host ('Duration: {0:N1}s' -f (($durationMs) / 1000))

    $cmd.CommandText = 'SELECT TOP 10 CountryCode, COUNT(*) AS Cnt FROM Ref.Geofeed GROUP BY CountryCode ORDER BY Cnt DESC'
    $reader = $cmd.ExecuteReader()
    Write-Host ""
    Write-Host "-- Top 10 Countries --"
    while ($reader.Read()) {
        Write-Host "  $($reader['CountryCode'].ToString().PadRight(4)) $($reader['Cnt'])"
    }
    $reader.Close()
}
finally {
    $conn.Close()
}
