<#
.SYNOPSIS
    Launches a real Vintage Story client into an isolated test world with only this mod
    installed, waits for the mod to come up, presses its hotkey, and screenshots the result.

.DESCRIPTION
    The closest thing to an automated gate 3 (in-game behaviour). What it verifies:
      * the mod loads in a real client and reaches "[SymbioticInventories] Ready."
      * both capture points arm (two "Capturing container dialogs" lines)
      * the master window opens on B - confirmed by a screenshot you can actually look at

    What it cannot verify: slot click routing, capture of a real chest, dock focus flow.
    Those stay manual (COMPATIBILITY.md section 6, gate 3).

    REQUIRES AN ACTIVE DESKTOP SESSION. From a disconnected RDP session or a locked console,
    GLFW cannot enumerate monitors and the game dies at GetPrimaryMonitor() before mods load
    (observed 2026-08-08: ArgumentNullException 'handle' in client-crash.log, "Loaded Mods:"
    empty). The preflight below checks for that and refuses early with a clear message,
    because the crash log alone looks misleadingly like a game bug.

    Uses an isolated --dataPath: your real saves, settings and mods are never touched. Your
    clientsettings.json is copied in (it carries the login session; a fresh file would strand
    the game at the account screen) with the window forced to 1600x900 windowed.

.EXAMPLE
    .\tools\client-probe.ps1
    .\tools\client-probe.ps1 -KeepOpen     # leave the game running for manual poking
#>
[CmdletBinding()]
param(
    [string]$GameDir = "$env:APPDATA\Vintagestory",
    [string]$RealDataPath = "$env:APPDATA\VintagestoryData",
    [string]$WorldName = "si probe world",
    [int]$BootTimeoutSec = 300,
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# ---- preflight: is there a desktop to render to? --------------------------------
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
if ([System.Windows.Forms.Screen]::AllScreens.Count -eq 0) {
    Write-Host "NO ACTIVE DISPLAY: this session has no monitors (disconnected RDP / locked console)." -ForegroundColor Red
    Write-Host "The game would crash in GetPrimaryMonitor() before loading any mods. Run this from an active desktop session." -ForegroundColor Red
    exit 2
}

# ---- isolated data path ---------------------------------------------------------
$test = Join-Path $env:TEMP "si-client-probe"
$shots = Join-Path $root "tools\probe-shots"
Remove-Item -Recurse -Force $test -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$test\Mods", $shots | Out-Null

$zip = Get-ChildItem "$root\dist\symbioticinventories_*.zip" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $zip) { throw "No mod zip in dist\ - run package.ps1 first." }
Copy-Item $zip.FullName "$test\Mods\"

$cs = Get-Content "$RealDataPath\clientsettings.json" -Raw | ConvertFrom-Json
$cs.intSettings.screenWidth = 1600
$cs.intSettings.screenHeight = 900
$cs.intSettings.gameWindowMode = 0
$cs | ConvertTo-Json -Depth 10 | Set-Content "$test\clientsettings.json"

# ---- launch ---------------------------------------------------------------------
Write-Host "Launching client into world '$WorldName' (isolated dataPath)..."
$proc = Start-Process "$GameDir\Vintagestory.exe" `
    -ArgumentList "--dataPath", "`"$test`"", "--openWorld", "`"$WorldName`"", "--playStyle", "surviveandbuild" `
    -PassThru

$log = "$test\Logs\client-main.log"
$deadline = (Get-Date).AddSeconds($BootTimeoutSec)
$ready = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep 3
    if ($proc.HasExited) { break }
    if ((Test-Path $log) -and (Select-String -Path $log -SimpleMatch "[SymbioticInventories] Ready." -Quiet)) {
        $ready = $true; break
    }
}

if (-not $ready) {
    Write-Host "Mod never reported Ready. Recent log:" -ForegroundColor Red
    if (Test-Path $log) { Get-Content $log -Tail 20 }
    $crash = "$test\Logs\client-crash.log"
    if ((Test-Path $crash) -and (Get-Item $crash).Length -gt 0) {
        Write-Host "--- crash log ---" -ForegroundColor Red
        Get-Content $crash
    }
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    exit 1
}

$captures = @(Select-String -Path $log -SimpleMatch "Capturing container dialogs").Count
Write-Host "Mod ready. Capture points armed: $captures/2" -ForegroundColor $(if ($captures -eq 2) { 'Green' } else { 'Yellow' })

# Give the world a few seconds to actually render past the loading screen.
Start-Sleep 12

# ---- drive: press B, screenshot -------------------------------------------------
function Save-Screen([string]$path) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
'@

$game = Get-Process | Where-Object { $_.MainWindowTitle -match 'Vintage Story' } | Select-Object -First 1
if ($game) { [Win32]::SetForegroundWindow($game.MainWindowHandle) | Out-Null; Start-Sleep 1 }

Save-Screen "$shots\1-world.png"

# B = 0x42. Key down, key up.
[Win32]::keybd_event(0x42, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 120
[Win32]::keybd_event(0x42, 0, 2, [UIntPtr]::Zero)
Start-Sleep 2

Save-Screen "$shots\2-master-window.png"

Write-Host "Screenshots: $shots\1-world.png, 2-master-window.png" -ForegroundColor Green
Write-Host "LOOK AT THEM - a blank or vanilla-looking frame in shot 2 is a failure, not a pass."

if (-not $KeepOpen) {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Write-Host "Client closed. (-KeepOpen to leave it running.)"
}
exit 0
