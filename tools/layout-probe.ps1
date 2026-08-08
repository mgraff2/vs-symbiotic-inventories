<#
.SYNOPSIS
    Exercises the unified-flow layout headlessly and asserts the ribbon math is sane.

.DESCRIPTION
    The mod lays every storage section's slots into ONE row-major grid; each section is a
    contiguous ribbon of cells (like a text selection). That math is pure computation, so it
    is tested without launching the client. Invariants:

      * ribbons are contiguous: each starts exactly where the previous ended
      * every ribbon's slices cover its slot count exactly, in order, with correct offsets
      * at most three slices per ribbon (lead partial, full-rows block, tail partial)
      * no slice exceeds the grid width; slice geometry matches its cell run
      * total rows = ceil(total slots / cols)

    Prints an ASCII map per scenario - with a flow layout the map should read as solid
    lines of glyphs with no holes anywhere except after the final cell.

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

# Shadow-copy before loading: LoadFrom holds an OS lock for the life of this session and
# would make the next `dotnet build` fail with MSB3027.
$shadow = Join-Path ([IO.Path]::GetTempPath()) ("si-probe-" + [Guid]::NewGuid().ToString('N') + ".dll")
Copy-Item (Resolve-Path $Dll) $shadow
$si = [System.Reflection.Assembly]::LoadFrom($shadow)

$TSection = $si.GetType('SymbioticInventories.Core.InventorySection')
$TKind    = $si.GetType('SymbioticInventories.Core.SectionKind')
$TGrid    = $si.GetType('SymbioticInventories.Core.Layout.UnifiedGrid')

function New-Section($label, $kind, $slots, $number) {
    $s = [System.Activator]::CreateInstance($TSection)
    $s.Id = $label
    $s.Label = $label
    $s.Kind = [System.Enum]::Parse($TKind, $kind)
    $s.SlotIds = [int[]](0..($slots - 1))
    $s.Number = $number
    $s.Accent = [double[]]@(0.5, 0.6, 0.7)
    return $s
}

function New-SectionList($sections) {
    $listType = [System.Collections.Generic.List`1].MakeGenericType($TSection)
    $list = [System.Activator]::CreateInstance($listType)
    foreach ($s in $sections) { $list.Add($s) }
    return ,$list
}

# Flow sections only - crafting and worn-bag slots live in the fixed top strip now.
$scenarios = @{
    'minimal' = @()
    'typical' = @(
        (New-Section 'Hunter bag'  'Backpack'       16 1),
        (New-Section 'Leather bag' 'Backpack'       16 2),
        (New-Section 'Chest'       'GroundContainer' 16 3)
    )
    'boat' = @(
        (New-Section 'Hunter bag'  'Backpack'       16 1),
        (New-Section 'Leather bag' 'Backpack'       16 2),
        (New-Section 'Fore crate'  'Vehicle'        32 3),
        (New-Section 'Mid crate'   'Vehicle'        32 4),
        (New-Section 'Aft crate'   'Vehicle'        16 5),
        (New-Section 'Saddlebag'   'Mount'          12 6)
    )
    # The real 12-section chain-open case from gameplay screenshots.
    'warehouse' = @(
        (New-Section 'Bag 1'   'Backpack'        8 1),
        (New-Section 'Bag 2'   'Backpack'        8 2),
        (New-Section 'Bag 3'   'Backpack'        8 3),
        (New-Section 'Bag 4'   'Backpack'        8 4),
        (New-Section 'Trunk 1' 'GroundContainer' 36 5),
        (New-Section 'Trunk 2' 'GroundContainer' 36 6),
        (New-Section 'Vessel'  'GroundContainer' 12 7),
        (New-Section 'Trunk 3' 'GroundContainer' 36 8),
        (New-Section 'Trunk 4' 'GroundContainer' 36 9),
        (New-Section 'Trunk 5' 'GroundContainer' 36 10),
        (New-Section 'Trunk 6' 'GroundContainer' 36 11),
        (New-Section 'Trunk 7' 'GroundContainer' 36 12)
    )
    'heavy' = @(
        (New-Section 'Bag 1'      'Backpack'       32 1),
        (New-Section 'Bag 2'      'Backpack'       32 2),
        (New-Section 'Bag 3'      'Backpack'       16 3),
        (New-Section 'Bag 4'      'Backpack'       16 4),
        (New-Section 'Chest'      'GroundContainer' 32 5),
        (New-Section 'Vessel'     'GroundContainer' 16 6),
        (New-Section 'Basket'     'GroundContainer' 8 7),
        (New-Section 'Boat crate' 'Vehicle'        32 8)
    )
}

$failures = @()

function Test-Plan($name, $plan) {
    $total = 0
    $expectedStart = 0

    foreach ($ribbon in $plan.Ribbons) {
        $slots = $ribbon.Section.SlotIds.Length
        $label = $ribbon.Section.Label

        if ($ribbon.StartCell -ne $expectedStart) {
            $script:failures += "$name : '$label' starts at cell $($ribbon.StartCell), expected $expectedStart - flow has a gap or overlap"
        }
        $expectedStart = $ribbon.StartCell + $slots
        $total += $slots

        if ($ribbon.Slices.Count -gt 3) {
            $script:failures += "$name : '$label' has $($ribbon.Slices.Count) slices (max 3)"
        }

        $cell = $ribbon.StartCell
        $offset = 0
        foreach ($slice in $ribbon.Slices) {
            if ($slice.SlotOffset -ne $offset) {
                $script:failures += "$name : '$label' slice offset $($slice.SlotOffset), expected $offset"
            }
            $sliceStart = $slice.Row * $plan.Cols + $slice.Col
            if ($sliceStart -ne $cell) {
                $script:failures += "$name : '$label' slice at cell $sliceStart, expected $cell - ribbon not contiguous"
            }
            if ($slice.Col + $slice.Cols -gt $plan.Cols) {
                $script:failures += "$name : '$label' slice exceeds grid width"
            }
            if ($slice.Rows -gt 1 -and ($slice.Col -ne 0 -or $slice.Cols -ne $plan.Cols)) {
                $script:failures += "$name : '$label' multi-row slice is not a full-width block"
            }
            $offset += $slice.Count
            $cell += $slice.Count
        }
        if ($offset -ne $slots) {
            $script:failures += "$name : '$label' slices cover $offset of $slots slots"
        }
    }

    if ($plan.TotalSlots -ne $total) {
        $script:failures += "$name : plan.TotalSlots=$($plan.TotalSlots), sections sum to $total"
    }
    $expectRows = if ($total -eq 0) { 0 } else { [math]::Ceiling($total / [double]$plan.Cols) }
    if ($plan.Rows -ne $expectRows) {
        $script:failures += "$name : plan.Rows=$($plan.Rows), expected $expectRows"
    }
}

function Show-Map($plan) {
    if ($plan.TotalSlots -eq 0) { return }
    $glyphs = '123456789ABCDEFGHJKLMNPQRSTUVWXYZ'
    $rows = @()
    $line = ' ' * 0
    $cells = New-Object 'char[]' ($plan.Rows * $plan.Cols)
    for ($i = 0; $i -lt $cells.Length; $i++) { $cells[$i] = '.' }
    $gi = 0
    foreach ($ribbon in $plan.Ribbons) {
        $g = $glyphs[$gi % $glyphs.Length]; $gi++
        for ($c = $ribbon.StartCell; $c -lt $ribbon.EndCell; $c++) { $cells[$c] = $g }
    }
    for ($r = 0; $r -lt $plan.Rows; $r++) {
        $rowChars = $cells[($r * $plan.Cols)..(($r + 1) * $plan.Cols - 1)]
        Write-Host ("    " + (-join $rowChars)) -ForegroundColor DarkGray
    }
    $gi = 0
    foreach ($ribbon in $plan.Ribbons) {
        $g = $glyphs[$gi % $glyphs.Length]; $gi++
        Write-Host ("      {0} = {1,-12} {2} slots, {3} slice(s)" -f $g, $ribbon.Section.Label, $ribbon.Section.SlotIds.Length, $ribbon.Slices.Count) -ForegroundColor DarkGray
    }
}

$compute = $TGrid.GetMethod('Compute')

foreach ($scenarioName in @('minimal', 'typical', 'boat', 'warehouse', 'heavy')) {
    foreach ($cols in @(24, 8)) {
        $modeName = if ($cols -eq 8) { 'DockLeft' } else { 'Auto' }
        $list = New-SectionList $scenarios[$scenarioName]
        $plan = $compute.Invoke($null, @($list, $cols))

        $label = "$scenarioName/$modeName"
        Test-Plan $label $plan

        Write-Host ("{0,-20} cols={1,-3} rows={2,-3} slots={3}" -f $label, $plan.Cols, $plan.Rows, $plan.TotalSlots) -ForegroundColor Green
        Show-Map $plan
        Write-Host ""
    }
}

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "LAYOUT PROBE PASSED - ribbons contiguous, slices exact, rows minimal" -ForegroundColor Green
    exit 0
}
Write-Host "LAYOUT PROBE FAILED ($($failures.Count)):" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
