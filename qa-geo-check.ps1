$cs = "Server=localhost\SQL2025;Database=SmartPiXL;Integrated Security=True;TrustServerCertificate=True"
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT TOP 3 IPAddress, ISNULL(MaxMindRegion,'(null)') AS Region, ISNULL(MaxMindCity,'(null)') AS City FROM PiXL.Parsed WHERE HitType = 'modern' ORDER BY ReceivedAt DESC"
$reader = $cmd.ExecuteReader()
while ($reader.Read()) {
    Write-Host "IP=$($reader['IPAddress']) Region=$($reader['Region']) City=$($reader['City'])"
}
$reader.Close()

$cmd2 = $conn.CreateCommand()
$cmd2.CommandText = "SELECT COUNT(*) AS Total, SUM(CASE WHEN MaxMindRegion IS NOT NULL AND MaxMindRegion != '' THEN 1 ELSE 0 END) AS WithGeo FROM PiXL.Parsed WHERE HitType = 'modern' AND ReceivedAt >= DATEADD(DAY, -30, GETUTCDATE())"
$reader2 = $cmd2.ExecuteReader()
if ($reader2.Read()) {
    Write-Host "30d total=$($reader2['Total']) withGeo=$($reader2['WithGeo'])"
}
$reader2.Close()

$conn.Close()
