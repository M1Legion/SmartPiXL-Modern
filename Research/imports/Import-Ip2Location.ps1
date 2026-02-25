<#
.SYNOPSIS
    Imports IP2Location LITE DB11 CSV into Ref.Ip2LocationRange.
.DESCRIPTION
    IP2Location LITE DB11 provides: country, region, city, lat/lon, zip, timezone.
    It's the only free source that has ZIP + timezone in one flat file.

    IMPORTANT: Requires free registration at https://lite.ip2location.com/
    Download the "DB11.LITE" CSV file (IPv4) and place it in Research/data/.
    The file is named something like "IP2LOCATION-LITE-DB11.CSV"

    IP2Location CSV format (no header):
      ip_from,ip_to,country_code,country_name,region_name,city_name,latitude,longitude,zip_code,timezone

    ip_from/ip_to are already integers - no conversion needed.
    Timezone is UTC offset like "-05:00" (not IANA names).
.EXAMPLE
    .\Import-Ip2Location.ps1 -CsvPath ".\data\IP2LOCATION-LITE-DB11.CSV"
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

# -- Find the CSV file --
if (-not $CsvPath) {
    # Auto-detect: look for IP2LOCATION*.CSV or IP2LOCATION*.csv in data dir
    $candidates = Get-ChildItem -Path $DataDir -Filter 'IP2LOCATION*DB11*.CSV' -File -ErrorAction SilentlyContinue
    if (-not $candidates -or $candidates.Count -eq 0) {
        $candidates = Get-ChildItem -Path $DataDir -Filter 'IP2LOCATION*.CSV' -File -ErrorAction SilentlyContinue
    }

    if (-not $candidates -or $candidates.Count -eq 0) {
        Write-Host "ERROR: No IP2Location CSV found in $DataDir" -ForegroundColor Red
        Write-Host ""
        Write-Host "To get the data:"
        Write-Host "  1. Register free at https://lite.ip2location.com/"
        Write-Host "  2. Download 'DB11.LITE' (IPv4 CSV)"
        Write-Host "  3. Extract the CSV to: $DataDir"
        Write-Host "  4. Re-run this script"
        Write-Host ""
        Write-Host "Or specify the path directly:"
        Write-Host "  .\Import-Ip2Location.ps1 -CsvPath 'C:\path\to\IP2LOCATION-LITE-DB11.CSV'"
        return
    }

    $CsvPath = $candidates[0].FullName
    Write-Host "Auto-detected: $CsvPath"
}

if (-not (Test-Path $CsvPath)) {
    Write-Error "File not found: $CsvPath"
    return
}

$startTime = Get-Date
$fileSize = [Math]::Round((Get-Item $CsvPath).Length / 1MB, 1)
Write-Host "Reading IP2Location CSV: $CsvPath (${fileSize}MB)"

# -- Parse CSV --
# Format (no header): ip_from,ip_to,country_code,country_name,region_name,city_name,latitude,longitude,zip_code,timezone
# All fields are quoted: "16777216","16777471","AU","Australia","Queensland","South Brisbane","-27.48177","153.01718","4101","+10:00"

$rows = [System.Collections.Generic.List[PSObject]]::new(8500000)
$lineNum = 0
$skipped = 0

foreach ($line in [System.IO.File]::ReadLines($CsvPath)) {
    $lineNum++
    if ([string]::IsNullOrWhiteSpace($line)) { continue }

    # Strip all quotes and split by comma
    # IP2Location LITE wraps every field in double quotes, no embedded commas in values
    $clean = $line.Replace('"', '')
    $fields = $clean.Split(',')

    if ($fields.Count -lt 10) {
        $skipped++
        continue
    }

    $startInt = 0L
    $endInt   = 0L
    if (-not [long]::TryParse($fields[0], [ref]$startInt)) { $skipped++; continue }
    if (-not [long]::TryParse($fields[1], [ref]$endInt))   { $skipped++; continue }

    # Skip IPv6 mapped addresses (> 4294967295)
    if ($startInt -gt 4294967295) { continue }

    $lat = 0.0
    $lon = 0.0
    [double]::TryParse($fields[6], [ref]$lat) | Out-Null
    [double]::TryParse($fields[7], [ref]$lon) | Out-Null

    $rows.Add([PSCustomObject]@{
        StartInt    = $startInt
        EndInt      = $endInt
        CountryCode = $fields[2]
        Country     = $fields[3]
        Region      = $fields[4]
        City        = $fields[5]
        Latitude    = $lat
        Longitude   = $lon
        Zip         = $fields[8]
        Timezone    = $fields[9]
    })

    if ($lineNum % 1000000 -eq 0) {
        Write-Host "  Parsed $($lineNum / 1000000)M lines..."
    }
}

Write-Host "Parsed $($rows.Count) IPv4 rows ($skipped skipped)"

if (-not $PSCmdlet.ShouldProcess("Ref.Ip2LocationRange", "TRUNCATE + INSERT $($rows.Count) rows")) { return }

# -- Build DataTable for SqlBulkCopy --
Write-Host "Building DataTable..."
$dt = [System.Data.DataTable]::new()
$dt.Columns.Add('StartInt',    [long])   | Out-Null
$dt.Columns.Add('EndInt',      [long])   | Out-Null
$dt.Columns.Add('CountryCode', [string]) | Out-Null
$dt.Columns.Add('Country',     [string]) | Out-Null
$dt.Columns.Add('Region',      [string]) | Out-Null
$dt.Columns.Add('City',        [string]) | Out-Null
$dt.Columns.Add('Latitude',    [decimal])| Out-Null
$dt.Columns.Add('Longitude',   [decimal])| Out-Null
$dt.Columns.Add('Zip',         [string]) | Out-Null
$dt.Columns.Add('Timezone',    [string]) | Out-Null

foreach ($r in $rows) {
    $row = $dt.NewRow()
    $row['StartInt']    = $r.StartInt
    $row['EndInt']      = $r.EndInt
    $row['CountryCode'] = $r.CountryCode
    $row['Country']     = $r.Country
    $row['Region']      = $r.Region
    $row['City']        = $r.City
    $row['Latitude']    = [decimal]$r.Latitude
    $row['Longitude']   = [decimal]$r.Longitude
    $row['Zip']         = $r.Zip
    $row['Timezone']    = $r.Timezone
    $dt.Rows.Add($row)
}
Write-Host "DataTable ready: $($dt.Rows.Count) rows"

# -- Bulk insert --
$connStr = "Server=$SqlInstance;Database=$Database;Integrated Security=True;TrustServerCertificate=True"
$conn = [System.Data.SqlClient.SqlConnection]::new($connStr)
$conn.Open()

try {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = 'SELECT COUNT(*) FROM Ref.Ip2LocationRange'
    $oldCount = [int]$cmd.ExecuteScalar()

    $cmd.CommandText = 'TRUNCATE TABLE Ref.Ip2LocationRange'
    $cmd.ExecuteNonQuery() | Out-Null

    $bc = [System.Data.SqlClient.SqlBulkCopy]::new($conn)
    $bc.DestinationTableName = 'Ref.Ip2LocationRange'
    $bc.BatchSize = 100000
    $bc.BulkCopyTimeout = 600
    $bc.ColumnMappings.Add('StartInt',    'StartInt')    | Out-Null
    $bc.ColumnMappings.Add('EndInt',      'EndInt')      | Out-Null
    $bc.ColumnMappings.Add('CountryCode', 'CountryCode') | Out-Null
    $bc.ColumnMappings.Add('Country',     'Country')     | Out-Null
    $bc.ColumnMappings.Add('Region',      'Region')      | Out-Null
    $bc.ColumnMappings.Add('City',        'City')        | Out-Null
    $bc.ColumnMappings.Add('Latitude',    'Latitude')    | Out-Null
    $bc.ColumnMappings.Add('Longitude',   'Longitude')   | Out-Null
    $bc.ColumnMappings.Add('Zip',         'Zip')         | Out-Null
    $bc.ColumnMappings.Add('Timezone',    'Timezone')    | Out-Null

    Write-Host "Bulk inserting..."
    $bc.WriteToServer($dt)

    $durationMs = [int]((Get-Date) - $startTime).TotalMilliseconds
    $cmd.CommandText = ('INSERT INTO Ref.ImportLog (SourceName, RowsLoaded, RowsReplaced, DurationMs, Notes) VALUES (''Ip2Location'', {0}, {1}, {2}, ''DB11 LITE IPv4'')' -f $rows.Count, $oldCount, $durationMs)
    $cmd.ExecuteNonQuery() | Out-Null

    Write-Host ''
    Write-Host ('SUCCESS: Loaded {0} rows into Ref.Ip2LocationRange (replaced {1})' -f $rows.Count, $oldCount)
    Write-Host ('Duration: {0:N1}s' -f (($durationMs) / 1000))

    # Sanity check
    $cmd.CommandText = 'SELECT TOP 5 CountryCode, COUNT(*) AS Cnt FROM Ref.Ip2LocationRange GROUP BY CountryCode ORDER BY Cnt DESC'
    $reader = $cmd.ExecuteReader()
    Write-Host ""
    Write-Host "-- Top 5 Countries --"
    while ($reader.Read()) {
        Write-Host "  $($reader['CountryCode'].ToString().PadRight(4)) $($reader['Cnt'])"
    }
    $reader.Close()
}
finally {
    $conn.Close()
}
