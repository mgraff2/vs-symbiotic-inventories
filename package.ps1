<#
.SYNOPSIS
    Builds Symbiotic Inventories and packages it as a Vintage Story mod zip.

.PARAMETER Install
    Also copy the zip into the Vintage Story Mods folder.

.EXAMPLE
    .\package.ps1
    .\package.ps1 -Install
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$VintagestoryDir = "$env:APPDATA\Vintagestory",
    [string]$ModsDir = "$env:APPDATA\VintagestoryData\Mods",
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

& $dotnet build "$root\SymbioticInventories\SymbioticInventories.csproj" -c $Configuration -v minimal --nologo `
    -p:VintagestoryDir="$VintagestoryDir"
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$outDir = Join-Path $root "SymbioticInventories\bin\$Configuration"
$modinfo = Get-Content (Join-Path $outDir 'modinfo.json') -Raw | ConvertFrom-Json
$zipName = "symbioticinventories_$($modinfo.version).zip"
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zipPath = Join-Path $dist $zipName

# Stage only what ships: the assembly, modinfo, and assets. No .pdb or .deps.json.
$stage = Join-Path $env:TEMP "si_stage_$([System.Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $stage | Out-Null
try {
    Copy-Item (Join-Path $outDir 'SymbioticInventories.dll') $stage
    Copy-Item (Join-Path $outDir 'modinfo.json') $stage
    $assets = Join-Path $outDir 'assets'
    if (Test-Path $assets) { Copy-Item $assets $stage -Recurse }

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Packaged: $zipPath" -ForegroundColor Green

if ($Install) {
    if (-not (Test-Path $ModsDir)) { throw "Mods folder not found: $ModsDir" }
    Copy-Item $zipPath $ModsDir -Force
    Write-Host "Installed to: $ModsDir" -ForegroundColor Green
}
