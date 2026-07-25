<#
.SYNOPSIS
    Exercises the AutoRim bridge while RimWorld is running.

.DESCRIPTION
    Checks /health, then sends commands through /rpc. With -Concurrency it also fires a burst
    of simultaneous requests, which is how main-thread marshalling bugs surface.

.EXAMPLE
    .\scripts\smoke.ps1
    .\scripts\smoke.ps1 -Concurrency 50
#>
[CmdletBinding()]
param(
    [int]$Port = 7789,
    [int]$Concurrency = 0
)

$ErrorActionPreference = 'Stop'

$configRoot = Join-Path $env:LOCALAPPDATA '..\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\AutoRim'
$tokenFile  = Join-Path $configRoot 'bridge.token'
$base       = "http://127.0.0.1:$Port"

function Write-Result($label, $ok, $detail) {
    $colour = if ($ok) { 'Green' } else { 'Red' }
    $mark   = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1}" -f $mark, $label) -ForegroundColor $colour
    if ($detail) { Write-Host "         $detail" -ForegroundColor DarkGray }
}

Write-Host "`nAutoRim smoke test -> $base" -ForegroundColor Cyan

# --- health (no auth) ---------------------------------------------------------------------
try {
    $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 5
} catch {
    Write-Result "GET /health" $false $_.Exception.Message
    Write-Host "`nIs RimWorld running with the AutoRim mod enabled?" -ForegroundColor Yellow
    Write-Host "Check Player.log for lines starting with [AutoRim]." -ForegroundColor Yellow
    exit 1
}
Write-Result "GET /health" $health.ok ("version={0} gameLoaded={1} commands={2}" -f `
    $health.data.version, $health.data.gameLoaded, $health.data.commandCount)

# --- token --------------------------------------------------------------------------------
if (-not (Test-Path $tokenFile)) {
    Write-Result "token file" $false "Not found at $tokenFile"
    exit 1
}
$token = (Get-Content $tokenFile -Raw).Trim()
Write-Result "token file" $true "$($token.Substring(0,8))... ($($token.Length) chars)"

$headers = @{ 'X-AutoRim-Token' = $token }

function Invoke-Rpc($command, $arguments = @{}, $timeoutMs = 10000) {
    $body = @{ command = $command; args = $arguments; timeoutMs = $timeoutMs } | ConvertTo-Json -Depth 10 -Compress
    Invoke-RestMethod -Uri "$base/rpc" -Method Post -Headers $headers `
        -ContentType 'application/json' -Body $body -TimeoutSec 30
}

# --- auth is enforced ---------------------------------------------------------------------
try {
    $body = @{ command = 'meta.ping' } | ConvertTo-Json -Compress
    Invoke-RestMethod -Uri "$base/rpc" -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 5 | Out-Null
    Write-Result "unauthenticated request rejected" $false "Request unexpectedly succeeded"
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    Write-Result "unauthenticated request rejected" ($code -eq 401) "HTTP $code"
}

# --- round trip through the main thread ---------------------------------------------------
$ping = Invoke-Rpc 'meta.ping' @{ echo = 'hello' }
Write-Result "meta.ping" ($ping.ok -and $ping.data.echo -eq 'hello') `
    ("gameLoaded={0}" -f $ping.data.gameLoaded)

$list = Invoke-Rpc 'meta.list_commands'
Write-Result "meta.list_commands" $list.ok ("{0} commands registered" -f $list.data.count)

$status = Invoke-Rpc 'control.bridge_status'
Write-Result "control.bridge_status" $status.ok `
    ("listening={0} port={1} queue={2}" -f $status.data.listening, $status.data.port, $status.data.queueDepth)

# --- unknown command produces a clean error, not a crash ----------------------------------
$bogus = Invoke-Rpc 'nope.not_a_command'
Write-Result "unknown command rejected cleanly" `
    ((-not $bogus.ok) -and $bogus.error.code -eq 'UNKNOWN_COMMAND') $bogus.error.message

# --- concurrency ---------------------------------------------------------------------------
if ($Concurrency -gt 0) {
    Write-Host "`n  Firing $Concurrency concurrent requests..." -ForegroundColor Cyan

    # HttpClient rather than jobs: Start-ThreadJob is not present in Windows PowerShell 5.1,
    # and genuinely simultaneous sockets are the whole point of this check.
    Add-Type -AssemblyName System.Net.Http
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    $client.DefaultRequestHeaders.Add('X-AutoRim-Token', $token)

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $tasks = [System.Collections.Generic.List[object]]::new()
    foreach ($n in 1..$Concurrency) {
        $payload = "{`"command`":`"meta.ping`",`"args`":{`"echo`":`"$n`"}}"
        $content = [System.Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, 'application/json')
        $tasks.Add([pscustomobject]@{ N = $n; Task = $client.PostAsync("$base/rpc", $content) })
    }

    $failures = @()
    foreach ($entry in $tasks) {
        try {
            $response = $entry.Task.GetAwaiter().GetResult()
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $parsed = $text | ConvertFrom-Json
            if (-not ($parsed.ok -and $parsed.data.echo -eq "$($entry.N)")) {
                $failures += "req $($entry.N): $text"
            }
        } catch {
            $failures += "req $($entry.N): $($_.Exception.Message)"
        }
    }
    $sw.Stop()
    $client.Dispose()

    $okCount = $Concurrency - $failures.Count
    Write-Result "concurrent requests" ($failures.Count -eq 0) `
        "$okCount/$Concurrency correct in $([int]$sw.ElapsedMilliseconds) ms"
    $failures | Select-Object -First 5 | ForEach-Object {
        Write-Host "         $_" -ForegroundColor DarkYellow
    }
}

Write-Host ""
