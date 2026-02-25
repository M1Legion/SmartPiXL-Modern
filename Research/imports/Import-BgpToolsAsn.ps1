<#
.SYNOPSIS
    Downloads and imports bgp.tools ASN classification into Ref.BgpToolsAsn.
.DESCRIPTION
    Downloads https://bgp.tools/asns.csv - a ~120K row CSV with ASN classification.
    Fields: asn (format "AS####"), name, class (Eyeball/Transit/Content/Unknown), cc

    The 'class' field is the unique differentiator no other free source has:
      - Eyeball:  Consumer ISP carrying end-user traffic (Comcast, Verizon, etc.)
      - Transit:  Backbone/carrier (Cogent, Level 3, Lumen)
      - Content:  CDN/cloud/hyperscaler (AWS, Cloudflare, Google)
      - Unknown:  Unclassified

    For SmartPiXL, an Eyeball visitor is a very different lead quality signal
    than Content (bot/server traffic) or Transit.
.EXAMPLE
    .\Import-BgpToolsAsn.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SqlInstance = 'localhost\SQL2025',
    [string]$Database   = 'SmartPiXL',
    [string]$DataDir    = (Join-Path $PSScriptRoot '..\data')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$url  = 'https://bgp.tools/asns.csv'
$file = Join-Path $DataDir 'bgptools-asns.csv'

if (-not (Test-Path $DataDir)) { New-Item -ItemType Directory -Path $DataDir -Force | Out-Null }

$startTime = Get-Date

# -- Download --
Write-Host "Downloading bgp.tools ASN CSV..."
# bgp.tools requires a User-Agent header
$headers = @{ 'User-Agent' = 'SmartPiXL-Research/1.0 (data import)' }
Invoke-WebRequest -Uri $url -Headers $headers -UseBasicParsing -OutFile $file

$fileSize = [Math]::Round((Get-Item $file).Length / 1KB, 0)
Write-Host "Downloaded: ${fileSize}KB"

# -- Parse CSV --
# Format: asn,name,class,cc
# ASN field is "AS####" - strip the "AS" prefix to get integer
$rows = [System.Collections.Generic.List[PSObject]]::new(130000)

$lineNum = 0
foreach ($line in [System.IO.File]::ReadLines($file)) {
    $lineNum++
    if ($lineNum -eq 1) { continue }  # Skip header: asn,name,class,cc
    if ([string]::IsNullOrWhiteSpace($line)) { continue }

    # Handle quoted CSV fields (name can contain commas)
    # Use .NET CSV-aware parsing
    $parts = $null
    try {
        # Simple approach: split from the right since cc is always 2 chars and class is a known set
        # Format is always: AS####,"Some Name, Inc.",Class,CC
        # Or: AS####,SimpleName,Class,CC

        # Find ASN (always first, format AS####)
        $firstComma = $line.IndexOf(',')
        $asnStr = $line.Substring(0, $firstComma)

        # Find CC (always last 2 chars after last comma)
        $lastComma = $line.LastIndexOf(',')
        $cc = $line.Substring($lastComma + 1).Trim()

        # Find class (second-to-last field)
        $beforeCc = $line.Substring(0, $lastComma)
        $classComma = $beforeCc.LastIndexOf(',')
        $class = $beforeCc.Substring($classComma + 1).Trim()

        # Name is everything between first comma and class comma
        $name = $beforeCc.Substring($firstComma + 1, $classComma - $firstComma - 1).Trim()
        # Strip surrounding quotes if present
        if ($name.StartsWith('"') -and $name.EndsWith('"')) {
            $name = $name.Substring(1, $name.Length - 2)
        }
    }
    catch {
        Write-Warning "Skipping malformed line $lineNum : $line"
        continue
    }

    # Parse ASN integer
    if (-not $asnStr.StartsWith('AS')) {
        Write-Warning "Skipping non-ASN line $lineNum : $asnStr"
        continue
    }
    $asn = 0
    if (-not [int]::TryParse($asnStr.Substring(2), [ref]$asn)) {
        Write-Warning "Skipping unparseable ASN line $lineNum : $asnStr"
        continue
    }

    # Validate class
    if ($class -notin @('Eyeball','Transit','Content','Unknown')) {
        Write-Warning "Unexpected class '$class' at line $lineNum - keeping anyway"
    }

    $rows.Add([PSCustomObject]@{
        Asn         = $asn
        Name        = $name
        Class       = $class
        CountryCode = if ($cc.Length -eq 2) { $cc } else { $null }
    })
}

Write-Host "Parsed $($rows.Count) ASN rows"

# -- Class distribution --
Write-Host ""
Write-Host "-- Class Distribution --"
$rows | Group-Object Class | Sort-Object Count -Descending | ForEach-Object {
    Write-Host "  $($_.Name.PadRight(10)) $($_.Count)"
}

if (-not $PSCmdlet.ShouldProcess("Ref.BgpToolsAsn", "TRUNCATE + INSERT $($rows.Count) rows")) { return }

# -- Build DataTable --
$dt = [System.Data.DataTable]::new()
$dt.Columns.Add('Asn',         [int])    | Out-Null
$dt.Columns.Add('Name',        [string]) | Out-Null
$dt.Columns.Add('Class',       [string]) | Out-Null
$dt.Columns.Add('CountryCode', [string]) | Out-Null

foreach ($r in $rows) {
    $row = $dt.NewRow()
    $row['Asn']         = $r.Asn
    $row['Name']        = $r.Name
    $row['Class']       = $r.Class
    $row['CountryCode'] = if ($r.CountryCode) { $r.CountryCode } else { [DBNull]::Value }
    $dt.Rows.Add($row)
}

# -- Bulk insert --
$connStr = "Server=$SqlInstance;Database=$Database;Integrated Security=True;TrustServerCertificate=True"
$conn = [System.Data.SqlClient.SqlConnection]::new($connStr)
$conn.Open()

try {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = 'SELECT COUNT(*) FROM Ref.BgpToolsAsn'
    $oldCount = [int]$cmd.ExecuteScalar()

    $cmd.CommandText = 'TRUNCATE TABLE Ref.BgpToolsAsn'
    $cmd.ExecuteNonQuery() | Out-Null

    $bc = [System.Data.SqlClient.SqlBulkCopy]::new($conn)
    $bc.DestinationTableName = 'Ref.BgpToolsAsn'
    $bc.BatchSize = 50000
    $bc.BulkCopyTimeout = 120
    $bc.ColumnMappings.Add('Asn',         'Asn')         | Out-Null
    $bc.ColumnMappings.Add('Name',        'Name')        | Out-Null
    $bc.ColumnMappings.Add('Class',       'Class')       | Out-Null
    $bc.ColumnMappings.Add('CountryCode', 'CountryCode') | Out-Null

    $bc.WriteToServer($dt)

    $durationMs = [int]((Get-Date) - $startTime).TotalMilliseconds
    $cmd.CommandText = ('INSERT INTO Ref.ImportLog (SourceName, RowsLoaded, RowsReplaced, DurationMs, Notes) VALUES (''BgpToolsAsn'', {0}, {1}, {2}, ''bgp.tools asns.csv'')' -f $rows.Count, $oldCount, $durationMs)
    $cmd.ExecuteNonQuery() | Out-Null

    Write-Host ''
    Write-Host ('SUCCESS: Loaded {0} rows into Ref.BgpToolsAsn (replaced {1})' -f $rows.Count, $oldCount)
    Write-Host ('Duration: {0:N1}s' -f (($durationMs) / 1000))
}
finally {
    $conn.Close()
}
