# Game-version regression guard. Runs the full compat matrix against every supported
# Vintage Story version, so a release can't claim 1.22.0+ support it never tested.
#
# modinfo.json declares `"dependencies": { "game": "1.22.0" }` — that is a promise, and this
# script is what keeps it honest. Each version gets its own extracted dedicated-server
# package and a full compat-test.ps1 run (solo + every companion combo). Failures on ANY
# version fail the sweep.
#
# Run at the end of every version of the mod — after the feature work, before the release
# commit. Roughly 7 versions x N combos x ~30s boot, so budget several minutes; it is not a
# per-edit test (that's compat-test.ps1 against the installed server).
#
#   .\tools\version-sweep.ps1                    -> build once, sweep 1.22.0 .. 1.22.6
#   .\tools\version-sweep.ps1 -Versions 1.22.0,1.22.6   -> just the endpoints (quick check)
#   .\tools\version-sweep.ps1 -KeepGoing         -> run all versions even after a failure
#
# Server packages (~100MB each) are downloaded from the official CDN and cached, extracted,
# in tools\server-cache\<version>\ (gitignored). Delete a version's folder to re-download.
# Adding a new game version: append it to -Versions' default below and re-run. The CDN 404s
# for versions that don't exist, which is how you discover the current latest.
param(
    [string[]]$Versions = @("1.22.0", "1.22.1", "1.22.2", "1.22.3", "1.22.4", "1.22.5", "1.22.6"),
    [switch]$KeepGoing,
    [switch]$SkipBuild
)
$ErrorActionPreference = "Stop"
$cache = "$PSScriptRoot\server-cache"
New-Item -ItemType Directory -Force $cache | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem

# A half-extracted server package is the nastiest failure this script can have: the server
# boots, can't find its own worldgen/worldproperty assets, floods the log with [Error]s, and
# the combo gets reported as a FAIL — making a perfectly good mod look broken on that game
# version. So extraction is verified against the archive's own entry count, and a completion
# stamp is what marks a cached package reusable (the exe alone appearing early in the
# extraction is not proof of anything, least of all after an interrupted run).
#
# Expand-Archive is deliberately NOT used: it was observed truncating this ~9600-file archive
# to ~1400 files without raising an error. System.IO.Compression is both faster and honest.
function Get-ServerPackage([string]$ver) {
    $dir = "$cache\$ver"
    $exe = "$dir\VintagestoryServer.exe"
    $stamp = "$dir\.extract-complete"
    if ((Test-Path $exe) -and (Test-Path $stamp)) { return $exe }

    $url = "https://cdn.vintagestory.at/gamefiles/stable/vs_server_win-x64_$ver.zip"
    $zip = "$cache\vs_server_$ver.zip"
    if (-not (Test-Path $zip)) {
        Write-Host "  downloading server $ver ..." -NoNewline
        try {
            Invoke-WebRequest $url -OutFile $zip
        } catch {
            if (Test-Path $zip) { Remove-Item $zip -Force }
            throw "could not download server package for $ver ($url): $_"
        }
    } else {
        Write-Host "  reusing downloaded $ver zip ..." -NoNewline
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    # entries with an empty Name are directory entries; only files land on disk
    $expected = @($archive.Entries | Where-Object { $_.Name -ne "" }).Count
    $archive.Dispose()

    $extracted = 0
    foreach ($attempt in 1..2) {
        Write-Host " extracting ($expected files) ..." -NoNewline
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $dir)
        $extracted = @(Get-ChildItem -Recurse -File $dir).Count
        if ($extracted -eq $expected) { break }
        Write-Host " INCOMPLETE ($extracted/$expected)" -ForegroundColor Yellow
    }
    if ($extracted -ne $expected) {
        throw "server package $ver extracted $extracted of $expected files after 2 attempts — broken cache, not a mod failure"
    }
    if (-not (Test-Path $exe)) { throw "no VintagestoryServer.exe in the $ver package" }

    New-Item -ItemType File $stamp | Out-Null
    Remove-Item $zip -Force
    Write-Host " ok"
    return $exe
}

# The mod zip is built once, on the first version, and every later version reuses that exact
# artifact — the whole point is that ONE build works everywhere, not that seven builds each
# work somewhere.
$built = $SkipBuild.IsPresent

$results = [ordered]@{}
foreach ($ver in $Versions) {
    Write-Host ""
    Write-Host "######## Vintage Story $ver ########" -ForegroundColor Cyan
    # SETUP is tracked separately from FAIL on purpose: "we could not test this version" and
    # "the mod is broken on this version" are different facts, and conflating them either
    # sends you debugging a mod bug that doesn't exist or hides one that does.
    try {
        $exe = Get-ServerPackage $ver
        & "$PSScriptRoot\compat-test.ps1" -SkipBuild:$built -ServerExe $exe
        $status = if ($LASTEXITCODE -eq 0) { "PASS" } else { "FAIL" }
        $built = $true
    } catch {
        Write-Host "  $_" -ForegroundColor Red
        $status = "SETUP"
    }
    $results[$ver] = $status
    $ok = ($status -eq "PASS")
    if (-not $ok -and -not $KeepGoing) {
        Write-Host ""
        Write-Host "VERSION SWEEP ABORTED at $ver (use -KeepGoing to test the rest anyway)" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""
Write-Host "======== version sweep summary ========"
$results.GetEnumerator() | ForEach-Object {
    $color = switch ($_.Value) { "PASS" { "Green" } "FAIL" { "Red" } default { "Yellow" } }
    Write-Host ("  {0,-8} {1}" -f $_.Key, $_.Value) -ForegroundColor $color
}
$broken = @($results.GetEnumerator() | Where-Object { $_.Value -eq "FAIL" })
$unrun  = @($results.GetEnumerator() | Where-Object { $_.Value -eq "SETUP" })
if ($unrun.Count -gt 0) {
    Write-Host "NOT TESTED (server package setup failed): $($unrun.Key -join ', ')" -ForegroundColor Yellow
}
if ($broken.Count -gt 0) {
    Write-Host "VERSION SWEEP FAILED: $($broken.Key -join ', ')" -ForegroundColor Red
    exit 1
}
if ($unrun.Count -gt 0) {
    Write-Host "VERSION SWEEP INCOMPLETE: no version failed, but some were never tested" -ForegroundColor Yellow
    exit 1
}
Write-Host "VERSION SWEEP PASSED: $($Versions -join ', ')" -ForegroundColor Green
