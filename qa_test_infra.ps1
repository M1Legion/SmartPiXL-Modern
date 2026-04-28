$r = Invoke-WebRequest -Uri "http://localhost:7500/api/dash/infra" -UseBasicParsing -TimeoutSec 20
Write-Output "=== INFRA ==="
Write-Output "Status: $($r.StatusCode)"
Write-Output "ContentType: $($r.Headers['Content-Type'])"
Write-Output "Length: $($r.Content.Length)"
Write-Output "Body:"
Write-Output $r.Content
