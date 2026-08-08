<#
.SYNOPSIS
    Resolves the five API bindings Symbiotic Inventories depends on, against every cached
    game version.

.DESCRIPTION
    The server boot in compat-test.ps1 proves the mod LOADS on a version. It cannot prove the
    Harmony targets still exist, because a client-side mod's patches never get applied on a
    dedicated server. This script closes that gap: it reflects over each version's actual
    assemblies and resolves every member the mod binds to.

    Run both. Together they cover load-time and patch-time; neither alone does.

    Each version is checked in its own child process. Seven copies of VintagestoryAPI.dll all
    claim the same assembly name, and the first one loaded would win for the whole run --
    silently reporting version N's members as if they were version M's.

.EXAMPLE
    .\tools\binding-sweep.ps1                     # sweep every version in tools\server-cache
    .\tools\binding-sweep.ps1 -Versions 1.22.0    # just one
    .\tools\binding-sweep.ps1 -GameDir "C:\...\Vintagestory"   # an arbitrary install (worker mode)
#>
[CmdletBinding()]
param(
    [string[]]$Versions,
    [string]$GameDir,
    [string]$Label
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- worker mode
if ($GameDir) {
    if (-not (Test-Path $GameDir)) { throw "Not found: $GameDir" }
    if (-not $Label) { $Label = Split-Path $GameDir -Leaf }

    [System.AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler]{
        param($s, $e)
        $n = ($e.Name -split ',')[0]
        foreach ($d in @($GameDir, "$GameDir\Lib", "$GameDir\Mods")) {
            $p = Join-Path $d "$n.dll"
            if (Test-Path $p) { return [System.Reflection.Assembly]::LoadFrom($p) }
        }
        return $null
    })

    $api = [System.Reflection.Assembly]::LoadFrom("$GameDir\VintagestoryAPI.dll")
    $ess = [System.Reflection.Assembly]::LoadFrom("$GameDir\Mods\VSEssentials.dll")

    $tfm = $api.GetCustomAttributes([System.Runtime.Versioning.TargetFrameworkAttribute], $false)[0].FrameworkName

    $decl = [System.Reflection.BindingFlags]::Public -bor
            [System.Reflection.BindingFlags]::NonPublic -bor
            [System.Reflection.BindingFlags]::Instance -bor
            [System.Reflection.BindingFlags]::DeclaredOnly

    $failures = @()
    function Check($id, $label, $ok) {
        if (-not $ok) { $script:failures += "$id $label" }
    }

    $beDlg = $api.GetType('Vintagestory.API.Client.GuiDialogBlockEntity')
    $beInv = $api.GetType('Vintagestory.API.Client.GuiDialogBlockEntityInventory')
    $ccDlg = $ess.GetType('Vintagestory.GameContent.GuiDialogCreatureContents')

    # B1 - block container capture hooks
    Check 'B1' 'GuiDialogBlockEntity'          ($null -ne $beDlg)
    Check 'B1' 'GuiDialogBlockEntityInventory' ($null -ne $beInv)
    if ($beDlg) {
        Check 'B1' '.OnGuiOpened()'            ($null -ne $beDlg.GetMethod('OnGuiOpened', $decl, $null, [type[]]@(), $null))
        Check 'B1' '.OnGuiClosed()'            ($null -ne $beDlg.GetMethod('OnGuiClosed', $decl, $null, [type[]]@(), $null))
        Check 'B1' '.OnRenderGUI(float)'       ($null -ne $beDlg.GetMethod('OnRenderGUI', $decl, $null, [type[]]@([single]), $null))
        # B2 - block container data surface. DoSendPacket is non-public; the mod reaches it
        # via AccessTools, which searches non-public members and walks base types.
        Check 'B2' '.Inventory'                ($null -ne $beDlg.GetProperty('Inventory'))
        Check 'B2' '.BlockEntityPosition'      ($null -ne $beDlg.GetProperty('BlockEntityPosition'))
        Check 'B2' '.DoSendPacket(object)'     ($null -ne $beDlg.GetMethod('DoSendPacket', $decl, $null, [type[]]@([object]), $null))
    }

    # B3 / B4 - entity container hooks and the private fields behind them
    Check 'B3' 'GuiDialogCreatureContents'     ($null -ne $ccDlg)
    if ($ccDlg) {
        Check 'B3' '.OnGuiOpened()'            ($null -ne $ccDlg.GetMethod('OnGuiOpened', $decl, $null, [type[]]@(), $null))
        Check 'B3' '.OnGuiClosed()'            ($null -ne $ccDlg.GetMethod('OnGuiClosed', $decl, $null, [type[]]@(), $null))
        Check 'B3' '.OnRenderGUI(float)'       ($null -ne $ccDlg.GetMethod('OnRenderGUI', $decl, $null, [type[]]@([single]), $null))
        Check 'B3' '.DoSendPacket(object)'     ($null -ne $ccDlg.GetMethod('DoSendPacket', $decl, $null, [type[]]@([object]), $null))
        Check 'B4' 'field inv'                 ($null -ne $ccDlg.GetField('inv', $decl))
        Check 'B4' 'field owningEntity'        ($null -ne $ccDlg.GetField('owningEntity', $decl))
        Check 'B4' 'field title'               ($null -ne $ccDlg.GetField('title', $decl))
    }

    # B5 - bag decomposition
    $bagContent = $api.GetType('Vintagestory.API.Common.ItemSlotBagContent')
    $gc = $api.GetType('Vintagestory.API.Config.GlobalConstants')
    Check 'B5' 'ItemSlotBagContent'            ($null -ne $bagContent)
    if ($bagContent) { Check 'B5' '.BagIndex'  ($null -ne $bagContent.GetField('BagIndex')) }
    Check 'B5' 'ItemSlotBackpack'              ($null -ne $api.GetType('Vintagestory.API.Common.ItemSlotBackpack'))
    if ($gc) {
        foreach ($f in @('backpackInvClassName', 'craftingInvClassName', 'hotBarInvClassName')) {
            Check 'B5' "GlobalConstants.$f"    ($null -ne $gc.GetField($f))
        }
    }

    if ($failures.Count -eq 0) {
        Write-Host ("  {0,-8}  {1,-16}  ALL BINDINGS OK" -f $Label, $tfm.Replace('.NETCoreApp,Version=v', 'net')) -ForegroundColor Green
        exit 0
    }
    Write-Host ("  {0,-8}  {1,-16}  {2} MISSING" -f $Label, $tfm.Replace('.NETCoreApp,Version=v', 'net'), $failures.Count) -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "      - $_" -ForegroundColor Red }
    exit 1
}

# ------------------------------------------------------------ orchestrator mode
$cache = "$PSScriptRoot\server-cache"
if (-not $Versions) {
    if (-not (Test-Path $cache)) { throw "No server cache at $cache. Run tools\version-sweep.ps1 first to populate it." }
    $Versions = Get-ChildItem $cache -Directory | Select-Object -Expand Name | Sort-Object
}
if (-not $Versions) { throw "No versions to check." }

Write-Host "Binding sweep - five bindings per version" -ForegroundColor Cyan
Write-Host ""

$pwsh = (Get-Process -Id $PID).Path
$results = [ordered]@{}
foreach ($v in $Versions) {
    $dir = Join-Path $cache $v
    if (-not (Test-Path "$dir\VintagestoryAPI.dll")) {
        Write-Host ("  {0,-8}  no cached package" -f $v) -ForegroundColor Yellow
        $results[$v] = 'SETUP'
        continue
    }
    & $pwsh -NoProfile -File $PSCommandPath -GameDir $dir -Label $v
    $results[$v] = if ($LASTEXITCODE -eq 0) { 'PASS' } else { 'FAIL' }
}

Write-Host ""
$bad = @($results.GetEnumerator() | Where-Object { $_.Value -ne 'PASS' })
if ($bad.Count -gt 0) {
    Write-Host "BINDING SWEEP FAILED: $($bad.Key -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "BINDING SWEEP PASSED: $($Versions -join ', ')" -ForegroundColor Green
exit 0
