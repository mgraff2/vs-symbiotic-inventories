# Symbiotic Inventories

One inventory window for Vintage Story. Chests, clay vessels, baskets, ground-stored bags,
saddlebags and boat crates all dock into a single window alongside your worn backpacks and
the crafting grid — each as a colour-shaded, numbered section, so you can tell at a glance
which bag or chest a slot belongs to.

**Status: v0.1.0 — automated gates green across 1.22.0–1.22.6, but never run in-game.**
Boots clean on all seven game versions, solo and alongside Shipwright, and all five API
bindings resolve on every one. Rendering and click routing remain unproven until someone
launches the game. See [COMPATIBILITY.md](COMPATIBILITY.md) §5 for exactly what is and is not
proven.

---

## The idea

Vintage Story scatters storage across a dozen floating windows that fight for screen space.
Worse, the player backpack inventory is one flat slot list — nothing on screen tells you that
slots 12–19 are the bag you are about to drop. Symbiotic Inventories fixes both:

- **One window.** Container GUIs are intercepted and their slots re-drawn inside the master
  window. No more window-position juggling.
- **Numbered, shaded sections.** Every bag and container gets a number badge, an accent colour,
  and a shaded backing plate spanning exactly its slots. When you need to dump backpack 3, you
  can see which slots that is.
- **Crafting grid is always present.**

## How it works

The whole mod is client-side; the server needs nothing.

The key insight is that *every* plain container in the game funnels through one of two dialog
classes — `GuiDialogBlockEntityInventory` for blocks and `GuiDialogCreatureContents` for
entities. Capturing those two gives near-universal coverage, including third-party mods,
without a single mod-specific code path.

Captured dialogs are **not closed**. Their open/close handshake with the server is what keeps
the inventory synced to the client, so the original dialog is left alive and registered — only
its *rendering* is suppressed and its composer parked off-screen so it cannot eat clicks. Slot
operations are routed back through the dialog's own `DoSendPacket`, so the correct packet
envelope and id offset are preserved for containers this mod has never heard of.

Machines (firepit, quern, barrel, oven, traders, modded machinery) are deliberately left alone —
their windows carry progress bars and readouts a slot grid cannot represent.

```
SymbioticInventories/
  src/Core/         InventorySection, SectionPalette, SectionRegistry   - what to draw
  src/Integration/  DialogCaptureService, CapturedDialog                - what to capture
  src/Gui/          GuiDialogMasterInventory                            - the window
tools/              test harness
dist/               packaged mod zip
```

## Building

Requires the **.NET 10 SDK** — every 1.22.x game assembly targets `net10.0`, and net9 cannot
compile against them.

```powershell
.\package.ps1              # build + zip into dist\
.\package.ps1 -Install     # ...and copy into the Mods folder
```

Point at a non-default install with `-VintagestoryDir "C:\Path\To\Vintagestory"`.

## Testing

```powershell
.\tools\binding-sweep.ps1     # resolve the 5 API bindings on every cached version (seconds)
.\tools\compat-test.ps1       # headless server boot, solo + each companion mod
.\tools\version-sweep.ps1     # the above against 1.22.0 .. 1.22.6 (~7 min)
```

The two sweeps are complements, not alternatives: `version-sweep` covers **load time** across
versions, `binding-sweep` covers **patch time**. Our Harmony patches are client-side and never
run on a dedicated server, so a renamed private field would sail straight through a server boot.
Neither can see a pixel — that stays a manual checklist. See [CLAUDE.md](CLAUDE.md) for the
full gate description.

## Usage

Two layouts, toggled by the footer button:

- **Floating** (default): a centered window; opened storage jigsaw-packs for density.
- **Docked left**: the window locks to the left edge and stays up like a HUD while you play —
  the mouse stays with the game. Press the focus key (default **N**) to get the cursor into
  it; press again to hand the mouse back.

Default window hotkey **B**; both keys rebindable in Controls. The window also opens
automatically when you open any captured container.

## Known gaps

- **Rendering unverified in-game.** The packing math is covered headlessly by
  `tools/layout-probe.ps1`, but nothing has confirmed how it actually looks.
- **Mount/vehicle sections appear only once their container is opened.** Discovery of a mounted
  entity's attached containers without opening them is not implemented.
