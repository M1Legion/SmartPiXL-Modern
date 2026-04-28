$ErrorActionPreference = 'Stop'
try {
    $req = [System.Net.HttpWebRequest]::Create("http://localhost:7500/api/dash/infra")
    $req.Timeout = 60000
    $resp = $req.GetResponse()
    $sr = [System.IO.StreamReader]::new($resp.GetResponseStream())
    $body = $sr.ReadToEnd()
    $sr.Close()
    $resp.Close()
    "STATUS: $([int]$resp.StatusCode)" | Out-File C:\temp\infra_result.txt
    "LENGTH: $($body.Length)" | Out-File C:\temp\infra_result.txt -Append
    $body | Out-File C:\temp\infra_body.json -Encoding utf8
    "DONE" | Out-File C:\temp\infra_result.txt -Append
} catch {
    "ERROR: $($_.Exception.Message)" | Out-File C:\temp\infra_result.txt
    if ($_.Exception.InnerException) {
        "INNER: $($_.Exception.InnerException.Message)" | Out-File C:\temp\infra_result.txt -Append
    }
}
