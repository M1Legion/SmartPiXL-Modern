<#
.SYNOPSIS
    Downloads and imports RIR delegation files into Ref.RirDelegation.
.DESCRIPTION
    Downloads extended delegation files from all 5 Regional Internet Registries:
      - ARIN (North America)
      - RIPE NCC (Europe/Middle East/Central Asia)
      - APNIC (Asia-Pacific)
      - LACNIC (Latin America/Caribbean)
      - AFRINIC (Africa)

    Filters to IPv4 rows only, computes StartInt/EndInt as BIGINT for range joins,
    truncates the target table, and bulk-inserts via SqlBulkCopy.

    Format: registry|CC|ipv4|startIp|hostCount|date|status|opaqueId
    The 5th field is HOST COUNT (not prefix length) for IPv4.
.EXAMPLE
    .\Import-RirDelegation.ps1
    .\Import-RirDelegation.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SqlInstance = 'localhost\SQL2025',
    [string]$Database   = 'SmartPiXL',
    [string]$DataDir    = (Join-Path $PSScriptRoot '..\data')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# -- RIR download URLs --
$sources = @{
    'arin'    = 'https://ftp.arin.net/pub/stats/arin/delegated-arin-extended-latest'
    'ripencc' = 'https://ftp.ripe.net/pub/stats/ripencc/delegated-ripencc-extended-latest'
    'apnic'   = 'https://ftp.apnic.net/pub/stats/apnic/delegated-apnic-extended-latest'
    'lacnic'  = 'https://ftp.lacnic.net/pub/stats/lacnic/delegated-lacnic-extended-latest'
    'afrinic' = 'https://ftp.afrinic.net/pub/stats/afrinic/delegated-afrinic-extended-latest'
}

function ConvertTo-IpInt([string]$ip) {
    $octets = $ip.Split('.')
    [long]$octets[0] * 16777216 + [long]$octets[1] * 65536 + [long]$octets[2] * 256 + [long]$octets[3]
}

# -- Ensure data directory exists --
if (-not (Test-Path $DataDir)) { New-Item -ItemType Directory -Path $DataDir -Force | Out-Null }

$startTime = Get-Date
$allRows = [System.Collections.Generic.List[PSObject]]::new(400000)

# -- Download and parse each RIR file --
foreach ($registry in $sources.Keys) {
    $url  = $sources[$registry]
    $file = Join-Path $DataDir "delegated-$registry.txt"

    Write-Host "[$registry] Downloading from $url ..."
    Invoke-WebRequest -Uri $url -UseBasicParsing -OutFile $file

    $fileSize = [Math]::Round((Get-Item $file).Length / 1MB, 1)
    Write-Host "[$registry] Downloaded: ${fileSize}MB"

    $ipv4Count = 0
    foreach ($line in [System.IO.File]::ReadLines($file)) {
        # Skip headers, summaries, comments, empty lines
        if ($line.StartsWith('#') -or $line.StartsWith('*') -or [string]::IsNullOrWhiteSpace($line)) { continue }
        $fields = $line.Split('|')
        if ($fields.Count -lt 7) { continue }
        if ($fields[2] -ne 'ipv4') { continue }

        # Fields: registry|CC|type|startIp|hostCount|date|status[|opaqueId]
        $cc        = $fields[1]
        $startIp   = $fields[3]
        $hostCount = [int]$fields[4]
        $dt        = $fields[5]
        $status    = $fields[6]
        $opaqueId  = if ($fields.Count -gt 7) { $fields[7] } else { $null }

        # Skip wildcard/summary rows
        if ($cc -eq '*' -or $startIp -eq '*') { continue }

        $startInt = ConvertTo-IpInt $startIp
        $endInt   = $startInt + $hostCount - 1

        $allRows.Add([PSCustomObject]@{
            Registry      = $registry
            CountryCode   = $cc
            StartIp       = $startIp
            HostCount     = $hostCount
            StartInt      = $startInt
            EndInt        = $endInt
            DateAllocated = $dt
            Status        = $status
            OpaqueId      = $opaqueId
        })
        $ipv4Count++
    }
    Write-Host "[$registry] Parsed $ipv4Count IPv4 rows"
}

Write-Host ""
Write-Host "Total IPv4 rows across all RIRs: $($allRows.Count)"

if (-not $PSCmdlet.ShouldProcess("Ref.RirDelegation", "TRUNCATE + INSERT $($allRows.Count) rows")) { return }

# -- Build DataTable for SqlBulkCopy --
$dt = [System.Data.DataTable]::new()
$dt.Columns.Add('Registry',      [string]) | Out-Null
$dt.Columns.Add('CountryCode',   [string]) | Out-Null
$dt.Columns.Add('StartIp',       [string]) | Out-Null
$dt.Columns.Add('HostCount',     [int])    | Out-Null
$dt.Columns.Add('StartInt',      [long])   | Out-Null
$dt.Columns.Add('EndInt',        [long])   | Out-Null
$dt.Columns.Add('DateAllocated', [string]) | Out-Null
$dt.Columns.Add('Status',        [string]) | Out-Null
$dt.Columns.Add('OpaqueId',      [string]) | Out-Null

foreach ($r in $allRows) {
    $row = $dt.NewRow()
    $row['Registry']      = $r.Registry
    $row['CountryCode']   = $r.CountryCode
    $row['StartIp']       = $r.StartIp
    $row['HostCount']     = $r.HostCount
    $row['StartInt']      = $r.StartInt
    $row['EndInt']        = $r.EndInt
    $row['DateAllocated'] = $r.DateAllocated
    $row['Status']        = $r.Status
    $row['OpaqueId']      = if ($r.OpaqueId) { $r.OpaqueId } else { [DBNull]::Value }
    $dt.Rows.Add($row)
}

# -- Bulk insert --
$connStr = "Server=$SqlInstance;Database=$Database;Integrated Security=True;TrustServerCertificate=True"
$conn = [System.Data.SqlClient.SqlConnection]::new($connStr)
$conn.Open()

try {
    # Get current row count for logging
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = 'SELECT COUNT(*) FROM Ref.RirDelegation'
    $oldCount = [int]$cmd.ExecuteScalar()

    # Truncate
    $cmd.CommandText = 'TRUNCATE TABLE Ref.RirDelegation'
    $cmd.ExecuteNonQuery() | Out-Null

    # Bulk copy
    $bc = [System.Data.SqlClient.SqlBulkCopy]::new($conn)
    $bc.DestinationTableName = 'Ref.RirDelegation'
    $bc.BatchSize = 50000
    $bc.BulkCopyTimeout = 300

    # Explicit column mappings
    $bc.ColumnMappings.Add('Registry',      'Registry')      | Out-Null
    $bc.ColumnMappings.Add('CountryCode',   'CountryCode')   | Out-Null
    $bc.ColumnMappings.Add('StartIp',       'StartIp')       | Out-Null
    $bc.ColumnMappings.Add('HostCount',     'HostCount')     | Out-Null
    $bc.ColumnMappings.Add('StartInt',      'StartInt')      | Out-Null
    $bc.ColumnMappings.Add('EndInt',        'EndInt')         | Out-Null
    $bc.ColumnMappings.Add('DateAllocated', 'DateAllocated')  | Out-Null
    $bc.ColumnMappings.Add('Status',        'Status')         | Out-Null
    $bc.ColumnMappings.Add('OpaqueId',      'OpaqueId')       | Out-Null

    $bc.WriteToServer($dt)

    # Log the import
    $durationMs = [int]((Get-Date) - $startTime).TotalMilliseconds
    $cmd.CommandText = ('INSERT INTO Ref.ImportLog (SourceName, RowsLoaded, RowsReplaced, DurationMs, Notes) VALUES (''RIR'', {0}, {1}, {2}, ''All 5 RIRs: arin ripencc apnic lacnic afrinic'')' -f $allRows.Count, $oldCount, $durationMs)
    $cmd.ExecuteNonQuery() | Out-Null

    Write-Host ''
    Write-Host ('SUCCESS: Loaded {0} rows into Ref.RirDelegation (replaced {1})' -f $allRows.Count, $oldCount)
    Write-Host ('Duration: {0:N1}s' -f (($durationMs) / 1000))

    # Quick sanity check
    $cmd.CommandText = 'SELECT Registry, COUNT(*) AS Cnt FROM Ref.RirDelegation GROUP BY Registry ORDER BY Cnt DESC'
    $reader = $cmd.ExecuteReader()
    Write-Host ""
    Write-Host "-- Per-Registry Counts --"
    while ($reader.Read()) {
        Write-Host "  $($reader['Registry'].ToString().PadRight(10)) $($reader['Cnt'])"
    }
    $reader.Close()
}
finally {
    $conn.Close()
}
