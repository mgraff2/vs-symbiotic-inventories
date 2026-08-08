# Symbiotic Inventories

One inventory window for Vintage Story. Chests, clay vessels, baskets, ground-stored bags,
saddlebags and boat crates all pour into a single window alongside your worn backpacks and
the crafting grid — one dense, colour-coded grid instead of a dozen floating boxes fighting
for screen space.

**Status: v0.1.0** — client-side only; your server needs nothing and no save data is altered.

---

## What it does

- **One combined grid.** Every open container's slots flow into a single row-major grid. Each
  container is a contiguous **ribbon** of coloured cells — the cell backgrounds are tinted in
  the container's colour, the way a text selection spans line breaks — so you always know which
  chest a slot belongs to.
- **The window is always inventory-shaped.** The grid fills your screen's width (landscape) and
  reflows to whatever's open, so it stays dense with no wasted space and rarely needs to scroll.
- **Crafting grid is always present**, top-left, with its output slot. Anything left in it
  returns to your inventory when you close the window.
- **A vessel row** across the top: one icon tile per open container, badge-numbered to match its
  ribbon. Hovering any cell names its container (`#3 Rugged Backpack`).
- **Click a tile to hide** that container's ribbon; the grid reflows without resizing the window.
  **Group chips** (`×7`) hide a whole container type — all chests, all vessels — at once.
- **Open adjacent chests together**: one right-click opens every same-type container within a
  configurable radius (1–3 blocks), so a chest wall docks in one click.
- **Mounts and boats**: optionally show the inventory of the mount you're riding, and pull in
  pack animals / moored boats within up to 10 blocks — a boat opens all its crates at once.

## Two layouts

- **Floating** (default): a centered window that fills the screen's width.
- **Docked left**: locks to the left edge and stays up like a HUD while you play — the mouse
  stays with the game. Press the focus key (default **N**) to reach into it; press again to
  hand the mouse back.

Default window hotkey **B**; both keys rebindable in Controls. The window also opens
automatically when you open any container. **Options** (footer button) holds the toggles.

## How it works

Client-side only. Every plain container in the game funnels through one of two dialog classes —
`GuiDialogBlockEntityInventory` (blocks) or `GuiDialogCreatureContents` (entities). The mod
captures those two via Harmony, reads the inventory, and routes slot operations back through
each dialog's own packet sender — so containers from mods it has never seen work unchanged, and
the server still enforces locks, claims and range. Machines (firepit, quern, barrel, traders)
are deliberately left alone; their windows carry readouts a slot grid can't represent.

```
SymbioticInventories/
  src/Core/         sections, config, the unified-grid ribbon math
  src/Integration/  Harmony capture, chain-open, entity discovery
  src/Gui/          the master window and options dialog
tools/              test harness
```

See [COMPATIBILITY.md](COMPATIBILITY.md) for the tested version/mod matrix and
[CLAUDE.md](CLAUDE.md) for the architecture and the gotchas learned the hard way.

## Building

Requires the **.NET 10 SDK** — every 1.22.x game assembly targets `net10.0`.

```powershell
.\package.ps1              # build + zip into dist\
.\package.ps1 -Install     # ...and copy into the Mods folder
```

Point at a non-default install with `-VintagestoryDir "C:\Path\To\Vintagestory"`.

## Testing

```powershell
.\tools\layout-probe.ps1      # ribbon/flow math, headless (seconds)
.\tools\binding-sweep.ps1     # the 5 API bindings resolve on every cached version
.\tools\compat-test.ps1       # headless server boot, solo + each companion mod
.\tools\version-sweep.ps1     # the above across 1.22.0 .. 1.22.6 (~7 min)
.\tools\client-probe.ps1      # launch a real client into a test world (needs a desktop)
```

## Known limits

- **Docked-left scrolls** under heavy load — a narrow locked column can't widen and stay a dock.
- **Mount/boat auto-open** relies on entity selection-box geometry that can't be verified without
  a live elk/boat; it logs what it opens and degrades to "doesn't auto-open" on failure.
