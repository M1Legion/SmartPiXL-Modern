<#
.SYNOPSIS
    Downloads and imports DB-IP City Lite CSV into Ref.DbipCityLite.
.DESCRIPTION
    DB-IP City Lite is a free monthly geolocation database.
    No registration required. CC BY 4.0 license (attribution required).

    Source: https://download.db-ip.com/free/dbip-city-lite-{YYYY}-{MM}.csv.gz
    Fields: ip_start,ip_end,continent,country,stateprov,city,latitude,longitude
    ~8M rows for IPv4+IPv6. We filter to IPv4 only and compute integer ranges.

    DB-IP's unique value: continent field + third-party city/lat/lon for consensus voting.
    Weak spots: no ZIP, no timezone, no ISP/ASN.
.EXAMPLE
    .\Import-DbipCityLite.ps1
    .\Import-DbipCityLite.ps1 -CsvPath ".\data\dbip-city-lite-2026-02.csv"
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

$startTime = Get-Date

# -- Find or download the CSV --
if (-not $CsvPath) {
    $y = (Get-Date).Year
    $m = (Get-Date).ToString('MM')
    $gzFile  = Join-Path $DataDir "dbip-city-lite-$y-$m.csv.gz"
    $csvFile = Join-Path $DataDir "dbip-city-lite-$y-$m.csv"

    # Check if CSV already extracted
    if (Test-Path $csvFile) {
        Write-Host "Found existing CSV: $csvFile"
        $CsvPath = $csvFile
    }
    # Check if .gz already downloaded
    elseif (Test-Path $gzFile) {
        Write-Host "Found existing archive: $gzFile - decompressing..."
        # Decompress .gz
        $inStream  = [System.IO.File]::OpenRead($gzFile)
        $gzStream  = [System.IO.Compression.GZipStream]::new($inStream, [System.IO.Compression.CompressionMode]::Decompress)
        $outStream = [System.IO.File]::Create($csvFile)
        $gzStream.CopyTo($outStream)
        $outStream.Close()
        $gzStream.Close()
        $inStream.Close()
        $CsvPath = $csvFile
        Write-Host "Decompressed: $([Math]::Round((Get-Item $csvFile).Length / 1MB, 1))MB"
    }
    else {
        # Download
        $url = "https://download.db-ip.com/free/dbip-city-lite-$y-$m.csv.gz"
        Write-Host "Downloading DB-IP City Lite: $url ..."
        $headers = @{ 'User-Agent' = 'SmartPiXL-Research/1.0 (data import)' }
        Invoke-WebRequest -Uri $url -Headers $headers -UseBasicParsing -OutFile $gzFile -TimeoutSec 300

        $gzSize = [Math]::Round((Get-Item $gzFile).Length / 1MB, 1)
        Write-Host "Downloaded: ${gzSize}MB - decompressing..."

        # Decompress .gz
        $inStream  = [System.IO.File]::OpenRead($gzFile)
        $gzStream  = [System.IO.Compression.GZipStream]::new($inStream, [System.IO.Compression.CompressionMode]::Decompress)
        $outStream = [System.IO.File]::Create($csvFile)
        $gzStream.CopyTo($outStream)
        $outStream.Close()
        $gzStream.Close()
        $inStream.Close()

        $csvSize = [Math]::Round((Get-Item $csvFile).Length / 1MB, 1)
        Write-Host "Decompressed: ${csvSize}MB"
        $CsvPath = $csvFile
    }
}

if (-not (Test-Path $CsvPath)) {
    Write-Error "File not found: $CsvPath"
    return
}

Write-Host "Parsing DB-IP CSV: $CsvPath"

# -- Parse CSV directly into DataTable + stream to SQL in batches --
# Format (no header): ip_start,ip_end,continent,country,stateprov,city,latitude,longitude
# IPv6 rows interspersed (start with hex like 2001:) -- skip them

$connStr = "Server=$SqlInstance;Database=$Database;Integrated Security=True;TrustServerCertificate=True"
$conn = [System.Data.SqlClient.SqlConnection]::new($connStr)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = 'SELECT COUNT(*) FROM Ref.DbipCityLite'
$oldCount = [int]$cmd.ExecuteScalar()

$cmd.CommandText = 'TRUNCATE TABLE Ref.DbipCityLite'
$cmd.ExecuteNonQuery() | Out-Null

function New-DbipDataTable {
    $dt = [System.Data.DataTable]::new()
    $dt.Columns.Add('StartIp',     [string])  | Out-Null
    $dt.Columns.Add('EndIp',       [string])  | Out-Null
    $dt.Columns.Add('StartInt',    [long])    | Out-Null
    $dt.Columns.Add('EndInt',      [long])    | Out-Null
    $dt.Columns.Add('Continent',   [string])  | Out-Null
    $dt.Columns.Add('CountryCode', [string])  | Out-Null
    $dt.Columns.Add('Region',      [string])  | Out-Null
    $dt.Columns.Add('City',        [string])  | Out-Null
    $dt.Columns.Add('Latitude',    [decimal]) | Out-Null
    $dt.Columns.Add('Longitude',   [decimal]) | Out-Null
    return ,$dt
}

function Send-Batch([System.Data.DataTable]$dt, [System.Data.SqlClient.SqlConnection]$conn) {
    if ($dt.Rows.Count -eq 0) { return }
    $bc = [System.Data.SqlClient.SqlBulkCopy]::new($conn)
    $bc.DestinationTableName = 'Ref.DbipCityLite'
    $bc.BatchSize = 100000
    $bc.BulkCopyTimeout = 600
    $bc.ColumnMappings.Add('StartIp',     'StartIp')     | Out-Null
    $bc.ColumnMappings.Add('EndIp',       'EndIp')       | Out-Null
    $bc.ColumnMappings.Add('StartInt',    'StartInt')     | Out-Null
    $bc.ColumnMappings.Add('EndInt',      'EndInt')       | Out-Null
    $bc.ColumnMappings.Add('Continent',   'Continent')    | Out-Null
    $bc.ColumnMappings.Add('CountryCode', 'CountryCode')  | Out-Null
    $bc.ColumnMappings.Add('Region',      'Region')       | Out-Null
    $bc.ColumnMappings.Add('City',        'City')         | Out-Null
    $bc.ColumnMappings.Add('Latitude',    'Latitude')     | Out-Null
    $bc.ColumnMappings.Add('Longitude',   'Longitude')    | Out-Null
    $bc.WriteToServer($dt)
    $bc.Close()
}

$dt = New-DbipDataTable
$lineNum = 0
$ipv4Count = 0
$ipv6Skipped = 0
$parseErrors = 0
$batchSize = 200000

try {
    foreach ($line in [System.IO.File]::ReadLines($CsvPath)) {
        $lineNum++
        if ($line.Length -eq 0) { continue }

        # Quick IPv6 skip
        if ($line[0] -match '[a-fA-F]' -or $line.Contains(':')) { $ipv6Skipped++; continue }

        # Fast path: no quotes = simple comma split
        if (-not $line.Contains('"')) {
            $fields = $line.Split(',')
        }
        else {
            # Quoted CSV: state machine (rare, only city names with commas)
            $fields = [System.Collections.Generic.List[string]]::new(8)
            $inQuote = $false
            $sb = [System.Text.StringBuilder]::new(64)
            for ($i = 0; $i -lt $line.Length; $i++) {
                $ch = $line[$i]
                if ($ch -eq '"') { $inQuote = -not $inQuote }
                elseif ($ch -eq ',' -and -not $inQuote) { $fields.Add($sb.ToString()); [void]$sb.Clear() }
                else { [void]$sb.Append($ch) }
            }
            $fields.Add($sb.ToString())
        }

        if ($fields.Count -lt 8) { $parseErrors++; continue }

        $startIp = $fields[0]
        $endIp   = $fields[1]

        # Compute integer ranges
        $so = $startIp.Split('.')
        $eo = $endIp.Split('.')
        if ($so.Count -ne 4 -or $eo.Count -ne 4) { $parseErrors++; continue }

        $startInt = [long]$so[0] * 16777216 + [long]$so[1] * 65536 + [long]$so[2] * 256 + [long]$so[3]
        $endInt   = [long]$eo[0] * 16777216 + [long]$eo[1] * 65536 + [long]$eo[2] * 256 + [long]$eo[3]

        $lat = 0.0; $lon = 0.0
        [double]::TryParse($fields[6], [ref]$lat) | Out-Null
        [double]::TryParse($fields[7], [ref]$lon) | Out-Null

        $row = $dt.NewRow()
        $row['StartIp']     = $startIp
        $row['EndIp']       = $endIp
        $row['StartInt']    = $startInt
        $row['EndInt']      = $endInt
        $row['Continent']   = $fields[2]
        $row['CountryCode'] = $fields[3]
        $row['Region']      = $fields[4]
        $row['City']        = $fields[5]
        $row['Latitude']    = [decimal]$lat
        $row['Longitude']   = [decimal]$lon
        $dt.Rows.Add($row)
        $ipv4Count++

        # Flush batch to SQL
        if ($ipv4Count % $batchSize -eq 0) {
            Send-Batch $dt $conn
            Write-Host ('  Flushed {0:N0} rows ({1:N0} lines processed, {2:N0} IPv6 skipped)...' -f $ipv4Count, $lineNum, $ipv6Skipped)
            $dt.Dispose()
            $dt = New-DbipDataTable
        }
    }

    # Final batch
    Send-Batch $dt $conn
    $dt.Dispose()

    Write-Host ('Parsed {0:N0} IPv4 rows (IPv6 skipped: {1:N0}, parse errors: {2:N0})' -f $ipv4Count, $ipv6Skipped, $parseErrors)

    $durationMs = [int]((Get-Date) - $startTime).TotalMilliseconds
    $monthTag = Get-Date -Format 'yyyy-MM'
    $cmd.CommandText = ('INSERT INTO Ref.ImportLog (SourceName, RowsLoaded, RowsReplaced, DurationMs, Notes) VALUES (''DbipCityLite'', {0}, {1}, {2}, ''dbip-city-lite {3}'')' -f $ipv4Count, $oldCount, $durationMs, $monthTag)
    $cmd.ExecuteNonQuery() | Out-Null

    Write-Host ''
    Write-Host ('SUCCESS: Loaded {0:N0} rows into Ref.DbipCityLite (replaced {1:N0})' -f $ipv4Count, $oldCount)
    Write-Host ('Duration: {0:N1}s' -f (($durationMs) / 1000))

    $cmd.CommandText = 'SELECT TOP 5 Continent, CountryCode, COUNT(*) AS Cnt FROM Ref.DbipCityLite WHERE StartInt IS NOT NULL GROUP BY Continent, CountryCode ORDER BY Cnt DESC'
    $reader = $cmd.ExecuteReader()
    Write-Host ""
    Write-Host "-- Top 5 Country/Continent --"
    while ($reader.Read()) {
        Write-Host "  $($reader['Continent']) $($reader['CountryCode'].ToString().PadRight(4)) $($reader['Cnt'])"
    }
    $reader.Close()
}
finally {
    $conn.Close()
}
