$endpoints = @(
    @{ Url = 'http://localhost:7500/brilliantpixl'; Type = 'html' },
    @{ Url = 'http://localhost:7500/api/brilliantpixl/summary'; Type = 'json' },
    @{ Url = 'http://localhost:7500/api/brilliantpixl/daily'; Type = 'json' },
    @{ Url = 'http://localhost:7500/api/brilliantpixl/bots'; Type = 'json' },
    @{ Url = 'http://localhost:7500/api/brilliantpixl/bot-names'; Type = 'json' },
    @{ Url = 'http://localhost:7500/api/brilliantpixl/channels'; Type = 'json' },
    @{ Url = 'http://localhost:7500/api/brilliantpixl/pages'; Type = 'json' },
    @{ Url = 'http://localhost:7500/api/brilliantpixl/signals'; Type = 'json' },
    @{ Url = 'http://localhost:7500/api/brilliantpixl/devices'; Type = 'json' },
    @{ Url = 'http://localhost:7500/api/brilliantpixl/geo'; Type = 'json' },
    @{ Url = 'http://localhost:7500/api/brilliantpixl/evasion'; Type = 'json' },
    @{ Url = 'http://localhost:7500/api/brilliantpixl/screens'; Type = 'json' },
    @{ Url = 'http://localhost:7500/api/brilliantpixl/leads'; Type = 'json' },
    @{ Url = 'http://localhost:7500/api/brilliantpixl/os-versions'; Type = 'json' }
)

foreach ($ep in $endpoints) {
    $url = $ep.Url
    Write-Host ""
    Write-Host "===== $url ====="
    try {
        $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10
        Write-Host "STATUS: $($r.StatusCode)"
        Write-Host "CONTENT-TYPE: $($r.Headers['Content-Type'])"
        $len = $r.Content.Length
        Write-Host "CONTENT-LENGTH: $len"
        if ($ep.Type -eq 'json') {
            if ($len -gt 4000) {
                Write-Host $r.Content.Substring(0, 4000)
                Write-Host "... [TRUNCATED at 4000 of $len chars]"
            } else {
                Write-Host $r.Content
            }
        } else {
            Write-Host "HTML-TITLE: $(if ($r.Content -match '<title>(.*?)</title>') { $matches[1] } else { 'NONE' })"
            Write-Host "HAS-BRILLIANTPIXL: $($r.Content.ToLower().Contains('brilliantpixl'))"
            Write-Host "HAS-CHART: $($r.Content.ToLower().Contains('chart'))"
            Write-Host "HAS-FETCH: $($r.Content.ToLower().Contains('fetch'))"
            Write-Host "TOTAL-HTML-LENGTH: $len"
        }
    } catch {
        Write-Host "ERROR: $($_.Exception.Message)"
        if ($_.Exception.Response) {
            Write-Host "HTTP-STATUS: $([int]$_.Exception.Response.StatusCode)"
        }
    }
}
