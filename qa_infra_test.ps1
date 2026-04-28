$ErrorActionPreference = 'Stop'
$out = @()

# Test 1: Infra
try {
    $r = Invoke-WebRequest -Uri "http://localhost:7500/api/dash/infra" -UseBasicParsing -TimeoutSec 20
    $out += "=== INFRA ==="
    $out += "Status: $($r.StatusCode)"
    $out += "ContentType: $($r.Headers['Content-Type'])"
    $out += "Length: $($r.Content.Length)"
    $out += $r.Content
} catch {
    $out += "=== INFRA ERROR ==="
    $out += $_.Exception.Message
}

$out += ""
$out += "=== DONE ==="

$out | Out-File -FilePath "C:\temp\qa_infra_result.txt" -Encoding utf8
