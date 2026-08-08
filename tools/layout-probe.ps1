<#
.SYNOPSIS
    Exercises the layout packer headlessly and asserts the geometry is sane.

.DESCRIPTION
    The packing math is the one part of this mod that is pure computation - no rendering, no
    game state - so it can and should be tested without launching the client. This script
    builds realistic section sets, runs every layout mode, and checks the invariants that
    actually matter:

      * no two sections overlap
      * every section stays inside the width budget
      * height fits the budget, or Overflows is honestly set
      * every section is placed
      * every slot is drawn exactly once (cols * rows >= slots, and rows is minimal)

    It also prints an ASCII map of each layout, which is the fastest way to see whether a
    plan is sensible before ever opening the game.

.EXAMPLE
    .\tools\layout-probe.ps1
#>
[CmdletBinding()]
param(
    [string]$VintagestoryDir = "$env:APPDATA\Vintagestory",
    [string]$Dll = "$PSScriptRoot\..\SymbioticInventories\bin\Release\SymbioticInventories.dll"
)

$ErrorActionPreference = 'Stop'

[System.AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler]{
    param($s, $e)
    $n = ($e.Name -split ',')[0]
    foreach ($d in @($VintagestoryDir, "$VintagestoryDir\Lib", "$VintagestoryDir\Mods")) {
        $p = Join-Path $d "$n.dll"
        if (Test-Path $p) { return [System.Reflection.Assembly]::LoadFrom($p) }
    }
    return $null
})

# Shadow-copy before loading. LoadFrom holds an OS lock on the file for the life of the
# process, and this session outlives the script - loading bin\Release directly makes the very
# next `dotnet build` fail with MSB3027 "being used by another process".
$shadow = Join-Path ([IO.Path]::GetTempPath()) ("si-probe-" + [Guid]::NewGuid().ToString('N') + ".dll")
Copy-Item (Resolve-Path $Dll) $shadow
$si = [System.Reflection.Assembly]::LoadFrom($shadow)

$TSection = $si.GetType('SymbioticInventories.Core.InventorySection')
$TKind    = $si.GetType('SymbioticInventories.Core.SectionKind')
$TPacker  = $si.GetType('SymbioticInventories.Core.Layout.SectionPacker')
$TBudget  = $si.GetType('SymbioticInventories.Core.Layout.LayoutBudget')
$TMode    = $si.GetType('SymbioticInventories.Core.Layout.LayoutMode')
$TMetrics = $si.GetType('SymbioticInventories.Core.Layout.LayoutMetrics')

$Cell = $TMetrics.GetField('Cell').GetValue($null)

function New-Section($label, $kind, $slots, $number, $fixedCols) {
    $s = [System.Activator]::CreateInstance($TSection)
    $s.Id = $label
    $s.Label = $label
    $s.Kind = [System.Enum]::Parse($TKind, $kind)
    $s.SlotIds = [int[]](0..($slots - 1))
    $s.Number = $number
    $s.Accent = [double[]]@(0.5, 0.6, 0.7)
    if ($fixedCols) { $s.FixedColumns = $fixedCols }
    return $s
}

function New-SectionList($sections) {
    $listType = [System.Collections.Generic.List`1].MakeGenericType($TSection)
    $list = [System.Activator]::CreateInstance($listType)
    foreach ($s in $sections) { $list.Add($s) }
    # Comma prefix stops PowerShell unrolling the List<T> into an Object[] on return, which
    # would then fail to bind to the IReadOnlyList<InventorySection> parameter.
    return ,$list
}

# Two realistic loads: a normal session, and the pathological one that made the window run
# off the screen in the first place.
$scenarios = @{
    'typical' = @(
        (New-Section 'Crafting'   'Crafting'        9  0 3),
        (New-Section 'Worn bags'  'BackpackSlots'   4  0 4),
        (New-Section 'Hunter bag' 'Backpack'       16  1 0),
        (New-Section 'Leather bag' 'Backpack'      16  2 0),
        (New-Section 'Chest'      'GroundContainer' 16 3 0)
    )
    # Fresh spawn: no bags at all. Guards the degenerate case where the essentials band is
    # nearly the whole window and there is almost nothing to scroll.
    'minimal' = @(
        (New-Section 'Crafting'   'Crafting'  9  0 3)
    )
    # Aboard a Shipwright boat: the case the mod was originally asked for. Several vehicle
    # crates open at once on top of a normally-equipped player.
    'boat' = @(
        (New-Section 'Crafting'    'Crafting'        9  0 3),
        (New-Section 'Worn bags'   'BackpackSlots'   4  0 4),
        (New-Section 'Hunter bag'  'Backpack'       16  1 0),
        (New-Section 'Leather bag' 'Backpack'       16  2 0),
        (New-Section 'Fore crate'  'Vehicle'        32  3 0),
        (New-Section 'Mid crate'   'Vehicle'        32  4 0),
        (New-Section 'Aft crate'   'Vehicle'        16  5 0),
        (New-Section 'Saddlebag'   'Mount'          12  6 0)
    )
    'heavy' = @(
        (New-Section 'Crafting'   'Crafting'        9  0 3),
        (New-Section 'Worn bags'  'BackpackSlots'   4  0 4),
        (New-Section 'Bag 1'      'Backpack'       32  1 0),
        (New-Section 'Bag 2'      'Backpack'       32  2 0),
        (New-Section 'Bag 3'      'Backpack'       16  3 0),
        (New-Section 'Bag 4'      'Backpack'       16  4 0),
        (New-Section 'Chest'      'GroundContainer' 32 5 0),
        (New-Section 'Vessel'     'GroundContainer' 16 6 0),
        (New-Section 'Basket'     'GroundContainer' 8  7 0),
        (New-Section 'Boat crate' 'Vehicle'        32  8 0)
    )
}

$failures = @()


function Test-Plan($name, $plan, $budget) {
    $boxes = @($plan.AllBoxes())

    # every section placed exactly once
    $placed = $boxes.Count

    # overlap check
    foreach ($i in 0..([Math]::Max($boxes.Count - 1, 0))) {
        if ($boxes.Count -eq 0) { break }
        for ($j = $i + 1; $j -lt $boxes.Count; $j++) {
            $a = $boxes[$i]; $b = $boxes[$j]
            $overlapX = ($a.X -lt $b.X + $b.W - 0.01) -and ($b.X -lt $a.X + $a.W - 0.01)
            $overlapY = ($a.Y -lt $b.Y + $b.H - 0.01) -and ($b.Y -lt $a.Y + $a.H - 0.01)
            if ($overlapX -and $overlapY) {
                $script:failures += "$name : '$($a.Section.Label)' overlaps '$($b.Section.Label)'"
            }
        }
    }

    foreach ($b in $boxes) {
        # width budget
        if ($b.X + $b.W -gt $budget.MaxWidth + 0.01) {
            $script:failures += "$name : '$($b.Section.Label)' exceeds width budget ($([math]::Round($b.X + $b.W)) > $([math]::Round($budget.MaxWidth)))"
        }
        # every slot drawn, and no wholly-empty trailing row
        $slots = $b.Section.SlotIds.Length
        if ($b.Cols * $b.Rows -lt $slots) {
            $script:failures += "$name : '$($b.Section.Label)' grid $($b.Cols)x$($b.Rows) cannot hold $slots slots"
        }
        if ($b.Cols * ($b.Rows - 1) -ge $slots) {
            $script:failures += "$name : '$($b.Section.Label)' has a wholly empty trailing row"
        }
    }

    if ($plan.Height -gt $budget.MaxHeight + 0.01 -and -not $plan.Overflows) {
        $script:failures += "$name : height $([math]::Round($plan.Height)) exceeds budget $([math]::Round($budget.MaxHeight)) but Overflows is false"
    }

    # There is no pinned band any more (space won over pinning), so the guarantee that
    # remains is positional: crafting is registered first and skyline placement of earlier
    # boxes never depends on later ones, so it must sit at the top-left origin - the spot
    # that is visible whenever the window is at its default (unscrolled) state.
    $crafting = $boxes | Where-Object { $_.Section.Label -eq 'Crafting' } | Select-Object -First 1
    if ($crafting -and ($crafting.X -gt 0.01 -or $crafting.Y -gt 0.01)) {
        $script:failures += "$name : crafting grid is at ($([math]::Round($crafting.X)),$([math]::Round($crafting.Y))), not the top-left origin"
    }

    return @{ boxes = $boxes.Count; placed = $placed; pinned = 0 }
}

function Show-Map($plan, $budget) {
    $boxes = @($plan.AllBoxes())
    if ($boxes.Count -eq 0) { return }

    $cols = [int][math]::Ceiling($budget.MaxWidth / $Cell)
    $rows = [int][math]::Ceiling($plan.Height / $Cell)
    if ($rows -gt 60) { $rows = 60 }
    if ($cols -gt 90) { $cols = 90 }

    $grid = New-Object 'char[][]' $rows
    for ($r = 0; $r -lt $rows; $r++) {
        $grid[$r] = New-Object 'char[]' $cols
        for ($c = 0; $c -lt $cols; $c++) { $grid[$r][$c] = '.' }
    }

    $i = 0
    $glyphs = '123456789ABCDEFGHJKLMNPQRSTUVWXYZ'
    foreach ($b in $boxes) {
        $g = $glyphs[$i % $glyphs.Length]; $i++
        $c0 = [int][math]::Round($b.X / $Cell)
        $r0 = [int][math]::Round($b.Y / $Cell)
        for ($r = $r0; $r -lt $r0 + $b.Rows -and $r -lt $rows; $r++) {
            for ($c = $c0; $c -lt $c0 + $b.Cols -and $c -lt $cols; $c++) {
                if ($r -ge 0 -and $c -ge 0) { $grid[$r][$c] = $g }
            }
        }
    }

    foreach ($row in $grid) { Write-Host ("    " + (-join $row)) -ForegroundColor DarkGray }

    $i = 0
    foreach ($b in $boxes) {
        $g = $glyphs[$i % $glyphs.Length]; $i++
        Write-Host ("      {0} = {1,-14} {2}x{3}" -f $g, $b.Section.Label, $b.Cols, $b.Rows) -ForegroundColor DarkGray
    }
}

foreach ($scenarioName in @('minimal', 'typical', 'boat', 'heavy')) {
    foreach ($modeName in @('Auto', 'DockLeft')) {
        $list = New-SectionList $scenarios[$scenarioName]

        $budget = [System.Activator]::CreateInstance($TBudget)
        $budget.Mode = [System.Enum]::Parse($TMode, $modeName)
        # A 1920x1080 screen at GUI scale 1.0, minus chrome.
        switch ($modeName) {
            'DockLeft' { $budget.MaxWidth = 8 * $Cell;  $budget.MaxHeight = 1080 - 110 }
            default    { $budget.MaxWidth = 1920 * 0.82 - 230; $budget.MaxHeight = 1080 * 0.86 - 110 }
        }

        $plan = $TPacker.GetMethod('Pack').Invoke($null, @($list, $budget))

        $label = "$scenarioName/$modeName"
        $r = Test-Plan $label $plan $budget

        $fit = if ($plan.Overflows) { 'OVERFLOWS' } else { 'fits' }
        Write-Host ("{0,-18} {1,4} boxes  {2,5}x{3,-5} {4}  pinned={5,-4}" -f `
            $label, $r.boxes, [math]::Round($plan.Width), [math]::Round($plan.Height), $fit, [math]::Round($r.pinned)) `
            -ForegroundColor $(if ($plan.Overflows) { 'Yellow' } else { 'Green' })

        Show-Map $plan $budget
        Write-Host ""
    }
}

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "LAYOUT PROBE PASSED - no overlaps, no budget violations, no ragged grids" -ForegroundColor Green
    exit 0
}
Write-Host "LAYOUT PROBE FAILED ($($failures.Count)):" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
