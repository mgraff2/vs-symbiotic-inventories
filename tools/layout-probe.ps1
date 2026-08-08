<#
.SYNOPSIS
    Exercises the unified-flow layout headlessly and asserts the ribbon math is sane.

.DESCRIPTION
    The mod lays every storage section's slots into ONE row-major grid; each section is a
    contiguous ribbon of cells (like a text selection). That math is pure computation, so it
    is tested without launching the client. Invariants:

    The layout is a backward-L: the worn-bag block sits top-left (ONE BAG PER LINE, block
    width = the biggest bag), a one-column blank gutter, and off-body containers flow
    beside the block then full-width below it. Bags alone = just the bag block; a narrow
    (docked) window stacks containers below instead. Invariants:

      * no two slices overlap, and none leaves the grid
      * every ribbon's slices cover its slot count exactly, in order, with correct offsets
      * every bag starts at column 0 on its own line - no row hosts two bags
      * when containers share a bag row, the gutter column right of the bag block is empty
      * bags alone: plan.Cols is exactly the bag-block width
      * the bottom row is never empty; TotalCells equals the slots placed

    Prints an ASCII map per scenario. READ THE MAPS: region-stream continuity of the
    off-body flow is asserted per-ribbon but its overall shape (the L) is judged by eye.

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
    # Bags only - the window must be exactly the 4x8 bag block, one bag per line.
    'solo' = @(
        (New-Section 'Sturdy 1' 'Backpack' 8 1),
        (New-Section 'Sturdy 2' 'Backpack' 8 2),
        (New-Section 'Sturdy 3' 'Backpack' 8 3),
        (New-Section 'Sturdy 4' 'Backpack' 8 4)
    )
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

$onBodyKinds = @('Crafting', 'Hotbar', 'BackpackSlots', 'Backpack')

function Test-Plan($name, $plan) {
    $W = $plan.Cols
    $H = $plan.Rows
    if ($plan.Ribbons.Count -eq 0) { return }
    if ($W -le 0 -or $H -le 0) { $script:failures += "$name : empty grid ($W x $H) with ribbons"; return }

    $map = @{}          # "row,col" -> ribbon index
    $bagRowOwner = @{}  # row -> on-body ribbon index (one bag per line)
    $bagRight = -1      # rightmost bag column = bag block width - 1
    $anyOffBody = $false
    $slotSum = 0
    $ri = 0

    foreach ($ribbon in $plan.Ribbons) {
        $slots = $ribbon.Section.SlotIds.Length
        $label = $ribbon.Section.Label
        $onBody = $onBodyKinds -contains $ribbon.Section.Kind.ToString()
        if (-not $onBody) { $anyOffBody = $true }
        $slotSum += $slots

        $offset = 0
        foreach ($slice in $ribbon.Slices) {
            if ($slice.SlotOffset -ne $offset) {
                $script:failures += "$name : '$label' slice offset $($slice.SlotOffset), expected $offset"
            }
            $offset += $slice.Cols * $slice.Rows
            if ($slice.Col -lt 0 -or $slice.Col + $slice.Cols -gt $W) {
                $script:failures += "$name : '$label' slice exceeds grid width"
            }
            if ($slice.Row -lt 0 -or $slice.Row + $slice.Rows -gt $H) {
                $script:failures += "$name : '$label' slice exceeds grid height"
            }
            if ($onBody -and $slice.Col -ne 0) {
                $script:failures += "$name : bag '$label' slice starts at column $($slice.Col), bags own their lines from column 0"
            }

            for ($r = $slice.Row; $r -lt $slice.Row + $slice.Rows; $r++) {
                for ($c = $slice.Col; $c -lt $slice.Col + $slice.Cols; $c++) {
                    $k = "$r,$c"
                    if ($map.ContainsKey($k)) {
                        $script:failures += "$name : overlap at ($r,$c) between '$label' and ribbon $($map[$k])"
                    }
                    $map[$k] = $ri
                    if ($onBody) {
                        if ($bagRowOwner.ContainsKey($r) -and $bagRowOwner[$r] -ne $ri) {
                            $script:failures += "$name : row $r hosts two bags - each bag gets its own line"
                        }
                        $bagRowOwner[$r] = $ri
                        if ($c -gt $bagRight) { $bagRight = $c }
                    }
                }
            }
        }
        if ($offset -ne $slots) {
            $script:failures += "$name : '$label' slices cover $offset of $slots slots"
        }
        $ri++
    }

    if ($plan.TotalCells -ne $slotSum) {
        $script:failures += "$name : TotalCells=$($plan.TotalCells), slots sum to $slotSum"
    }

    # Bags alone: the window IS the bag block.
    if (-not $anyOffBody -and $bagRight -ge 0 -and $W -ne $bagRight + 1) {
        $script:failures += "$name : bags-only plan is $W cols, bag block is $($bagRight + 1)"
    }

    # Gutter: any bag row that also hosts container cells keeps the column right of the
    # bag block empty, so the two territories read apart.
    if ($anyOffBody -and $bagRight -ge 0 -and $bagRight + 1 -lt $W) {
        foreach ($r in $bagRowOwner.Keys) {
            $rowHasOff = $false
            for ($c = $bagRight + 1; $c -lt $W; $c++) { if ($map.ContainsKey("$r,$c")) { $rowHasOff = $true; break } }
            if ($rowHasOff -and $map.ContainsKey("$r,$($bagRight + 1)")) {
                $script:failures += "$name : row $r has no blank gutter between the bag block and containers"
            }
        }
    }

    # Rows minimal: the bottom row carries at least one cell.
    $bottomUsed = $false
    for ($c = 0; $c -lt $W; $c++) { if ($map.ContainsKey("$($H - 1),$c")) { $bottomUsed = $true; break } }
    if (-not $bottomUsed) {
        $script:failures += "$name : bottom row $($H - 1) is empty - Rows overstated"
    }
}

function Show-Map($plan) {
    if ($plan.TotalCells -eq 0) { return }
    $glyphs = '123456789ABCDEFGHJKLMNPQRSTUVWXYZ'
    $rows = @()
    $line = ' ' * 0
    $cells = New-Object 'char[]' ($plan.Rows * $plan.Cols)
    for ($i = 0; $i -lt $cells.Length; $i++) { $cells[$i] = '.' }
    $gi = 0
    foreach ($ribbon in $plan.Ribbons) {
        $g = $glyphs[$gi % $glyphs.Length]; $gi++
        foreach ($slice in $ribbon.Slices) {
            for ($r = $slice.Row; $r -lt $slice.Row + $slice.Rows; $r++) {
                for ($c = $slice.Col; $c -lt $slice.Col + $slice.Cols; $c++) {
                    $cells[$r * $plan.Cols + $c] = $g
                }
            }
        }
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
$chooseCols = $TGrid.GetMethod('ChooseCols')
$Cell = $si.GetType('SymbioticInventories.Core.Layout.LayoutMetrics').GetField('Cell').GetValue($null)

# ChooseCols must produce LANDSCAPE grids (wider than tall) on landscape viewports - the
# whole point of the aspect-fill: a tall portrait column wastes the wide screen and forces
# scrolling. Test across common screens and container loads.
Write-Host "ChooseCols aspect (landscape viewports)" -ForegroundColor Cyan
foreach ($scr in @(@(1920, 1080), @(2560, 1440), @(1600, 900))) {
    $availW = $scr[0] * 0.92 - 20
    $availH = $scr[1] * 0.92 - 140
    foreach ($n in @(48, 124, 296, 450)) {
        $cols = $chooseCols.Invoke($null, @([int]$n, [double]$availW, [double]$availH, 8))
        $rows = [math]::Ceiling($n / $cols)
        if ($cols -lt $rows) {
            $failures += "ChooseCols $($scr[0])x$($scr[1]) N=$n gave $cols x $rows - portrait, not landscape"
        }
        # Must not exceed the screen width.
        if ($cols * $Cell -gt $availW + 0.01) {
            $failures += "ChooseCols $($scr[0])x$($scr[1]) N=$n gave $cols cols exceeding screen width"
        }
    }
}
Write-Host ""

$ensure = $TGrid.GetMethod('EnsureSideRoom')

foreach ($scenarioName in @('minimal', 'solo', 'typical', 'boat', 'warehouse', 'heavy')) {
    foreach ($cols in @(24, 8)) {
        $modeName = if ($cols -eq 8) { 'DockLeft' } else { 'Auto' }
        $list = New-SectionList $scenarios[$scenarioName]
        # Auto widens for the backward-L exactly as the GUI does; docked stays width-driven.
        $useCols = if ($modeName -eq 'Auto') { $ensure.Invoke($null, @([int]$cols, $list, [int]34)) } else { $cols }
        $plan = $compute.Invoke($null, @($list, [int]$useCols))

        $label = "$scenarioName/$modeName"
        Test-Plan $label $plan

        Write-Host ("{0,-20} cols={1,-3} rows={2,-3} cells={3}" -f $label, $plan.Cols, $plan.Rows, $plan.TotalCells) -ForegroundColor Green
        Show-Map $plan
        Write-Host ""
    }
}

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "LAYOUT PROBE PASSED - no overlaps, bags own their lines, gutter blank, rows minimal" -ForegroundColor Green
    exit 0
}
Write-Host "LAYOUT PROBE FAILED ($($failures.Count)):" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
