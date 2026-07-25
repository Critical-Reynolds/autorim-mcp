<#
.SYNOPSIS
    Builds the AutoRim mod and installs it into RimWorld's Mods folder.

.EXAMPLE
    .\scripts\deploy.ps1
    .\scripts\deploy.ps1 -RimWorldDir "D:\SteamLibrary\steamapps\common\RimWorld" -SkipBuild
#>
[CmdletBinding()]
param(
    [string]$RimWorldDir = "C:\Program Files (x86)\Steam\steamapps\common\RimWorld",
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$source   = Join-Path $repoRoot 'dist-mod\AutoRim'
$modsDir  = Join-Path $RimWorldDir 'Mods'
$target   = Join-Path $modsDir 'AutoRim'

if (-not (Test-Path $modsDir)) {
    throw "RimWorld Mods folder not found at '$modsDir'. Pass -RimWorldDir with the correct install path."
}

if (-not $SkipBuild) {
    Write-Host "Building..." -ForegroundColor Cyan
    dotnet build (Join-Path $repoRoot 'mod-src\AutoRim.csproj') -c Debug -v minimal -nologo -p:RimWorldDir=$RimWorldDir
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}

$dll = Join-Path $source 'Assemblies\AutoRim.dll'
if (-not (Test-Path $dll)) { throw "Built assembly not found at '$dll'. Run without -SkipBuild." }

# Refuse to touch anything that is not our own mod folder.
if ((Split-Path -Leaf $target) -ne 'AutoRim') { throw "Refusing to deploy to unexpected path '$target'." }

Write-Host "Installing to $target" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path (Join-Path $target 'About')      | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $target 'Assemblies') | Out-Null

# Copy the whole About folder: it carries Preview.png for the Workshop listing and, once
# published, PublishedFileId.txt. Losing that file would create a duplicate Workshop item
# instead of updating the existing one, so it is preserved rather than overwritten.
Copy-Item (Join-Path $source 'About\*') (Join-Path $target 'About') -Recurse -Force
Copy-Item (Join-Path $source 'Assemblies\AutoRim.dll') (Join-Path $target 'Assemblies') -Force

# RimWorld writes PublishedFileId.txt into the installed mod on first upload. Mirror it back
# to the repo copy so a later deploy does not drop it.
$publishedId = Join-Path $target 'About\PublishedFileId.txt'
if (Test-Path $publishedId) {
    Copy-Item $publishedId (Join-Path $source 'About') -Force
    Write-Host "Workshop item id: $((Get-Content $publishedId -Raw).Trim())" -ForegroundColor DarkGray
}

$stamp = (Get-Item $dll).LastWriteTime
Write-Host ""
Write-Host "Deployed AutoRim (built $stamp)." -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Launch RimWorld."
Write-Host "  2. Mods -> enable 'AutoRim - MCP Bridge' -> restart when prompted."
Write-Host "  3. Load a save, then check the bridge:  .\scripts\smoke.ps1"
Write-Host ""
Write-Host "Log: $env:LOCALAPPDATA" -NoNewline
Write-Host "Low\Ludeon Studios\RimWorld by Ludeon Studios\Player.log"
