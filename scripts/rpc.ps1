<#
.SYNOPSIS
    Sends one command to the AutoRim bridge and prints the raw JSON response.

.DESCRIPTION
    Uses HttpClient rather than Invoke-WebRequest, which needs an interactive session on
    Windows PowerShell 5.1. Reports the response size, since keeping reads small is a design
    constraint of this project rather than an afterthought.

.EXAMPLE
    .\scripts\rpc.ps1 colony.snapshot
    .\scripts\rpc.ps1 pawns.detail '{"pawn":"Ivy"}'
    .\scripts\rpc.ps1 work.set_priority '{"pawn":"Ivy","work":"Cooking","priority":1}'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Command,

    [Parameter(Position = 1)]
    [string]$ArgsJson = '{}',

    [int]$Port = 7789,
    [int]$TimeoutMs = 30000,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$tokenFile = Join-Path $env:LOCALAPPDATA '..\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\AutoRim\bridge.token'
if (-not (Test-Path $tokenFile)) { throw "Token not found at $tokenFile. Has RimWorld run with AutoRim enabled?" }
$token = (Get-Content $tokenFile -Raw).Trim()

Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromMilliseconds($TimeoutMs + 5000)
$client.DefaultRequestHeaders.Add('X-AutoRim-Token', $token)

$payload = "{`"command`":$($Command | ConvertTo-Json),`"args`":$ArgsJson,`"timeoutMs`":$TimeoutMs}"
$content = [System.Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, 'application/json')

try {
    $response = $client.PostAsync("http://127.0.0.1:$Port/rpc", $content).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
} finally {
    $client.Dispose()
}

if (-not $Quiet) {
    $bytes = [Text.Encoding]::UTF8.GetByteCount($text)
    Write-Host ("--- {0}  HTTP {1}  {2} bytes (~{3} tokens) ---" -f `
        $Command, [int]$response.StatusCode, $bytes, [int]($bytes / 4)) -ForegroundColor Cyan
}

$text
