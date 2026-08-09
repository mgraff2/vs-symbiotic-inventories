# Changelog

## Unreleased

- **Creative mode: E opens both.** In creative, the inventory key opens the creative catalog
  AND the master window together - previously the catalog was unreachable, and vanilla
  creative never shows your own inventory at all. Build from the catalog, arrange in the
  window, no more mode-flipping.
- **Row markers.** Each worn bag's own icon sits in a margin left of its line, and a tiny
  live 3D portrait of your mount stands in the blank row above its saddlebag brick (only
  when saddlebags are present - absent, the whole part folds away). The vessel row now
  orders backpacks, then saddlebags, then clicked-open containers.
- **Saddlebags live under your bags.** The mount's inventory joins the player's left block -
  worn bags, a blank row, then the saddlebags - instead of flowing with world containers.
  A blank row also separates the left block from containers continuing below it.
- **Hover glow, both directions.** Hovering a grid cell lights up its container's tile in
  the vessel row; hovering a tile makes that container's whole ribbon glow.
- **Backward-L layout.** Worn bags are a fixed block top-left - one bag per line, block width
  = your biggest bag - then a blank gutter column; open containers flow beside the block and
  wrap full-width below it. With nothing else open the window is just the bag block (4×8 for
  four sturdy backpacks). Docked/narrow windows stack containers below instead. Supersedes
  the on-body/off-body line break.
- **Fixed** the cellar sweep withdrawing goods from click-to-take containers (FoodShelves
  flour sacks). Auto-open now only right-clicks containers that structurally promise a
  dialog (`BlockEntityOpenableContainer`); shelf-family blocks are left alone.
- **Line break between on-body and off-body storage.** The first chest/vehicle/mount section
  starts on a fresh row, leaving the rest of the worn bags' last line empty - one grid, two
  readable blocks: what's on you, then everything else.
- **Fixed** carcass harvesting: carving a dead animal no longer gets swallowed by the master
  window - the harvest loot dialog passes through untouched, so you can collect the drops.
- **Open cellar now reaches the whole cellar.** The server enforces pick range on every open,
  so a big cellar cannot open from one spot; containers past reach now queue and open
  automatically as you walk near them. Sweep ceiling raised 32 → 64.
- **Removed auto-sorting** and all its options (category priorities, spoilage filter, freshness
  ordering). The mod surfaces information — which slot lives in which container — but arranging
  your goods is your work, not the mod's. This also removes the one feature that rewrote real
  chest contents with no undo.
- **Fixed** the mount's saddlebags appearing only every other window open (the auto-open was
  re-toggling an already-open dialog).
- **Fixed** E opening both the vanilla and the master window; E now opens only the master window
  while the option is on.
- **Removed** the character/armour grid from the window (vanilla handles it better).
- Entity container tiles (elk saddlebags) now show the attached bag's icon.

## v0.1.0 — first release

One master inventory window for Vintage Story. Client-side only; no server install, no save
changes.

### Core

- **Unified flow grid.** Every open container's slots pour into a single row-major grid; each
  container is a contiguous colour-tinted **ribbon** (the cell backgrounds carry the container's
  colour), so a slot's owner is obvious at a glance.
- **Landscape layout** that fills the screen width and reflows to whatever is open — dense, and
  rarely needs to scroll. The window sizes to the grid, no dead space.
- **Crafting grid** always present with its output slot; leftover items return to your inventory
  on close.
- **Vessel row** of numbered icon tiles, one per container, matching the ribbons. Hovering a cell
  names its container.

### Interaction

- **Hide/show**: click a tile to drop a container's ribbon; **group chips** hide a whole container
  type at once. The window frame stays put so you never lose your bearings.
- **Sort items** across the visible containers: grouped by category, categories and items ordered,
  laid out respecting container boundaries; partial stacks merge for free.
- **Open adjacent chests** with one right-click, within a configurable 1–3 block radius.
- **Mounts & boats**: optionally show your mount's inventory and nearby pack animals / boats
  (up to 10 blocks); a boat opens all its crates at once.
- **Two modes**: centered floating window, or a left dock that stays up HUD-style with a focus
  hotkey (**N**) to reach into it.
- **Options** dialog for every toggle.

### Compatibility

- Verified across **Vintage Story 1.22.0 – 1.22.6** (binding sweep + headless server boot on all
  seven), and co-loading cleanly with **Shipwright**. Driven in a real ~65-mod client.
- Captures block and entity containers generically, so modded containers work unchanged; machines
  (firepit, quern, barrel, traders) are deliberately left alone.

### Known limits

- Docked-left scrolls under heavy load. Mount/boat auto-open is the one path not verified in-game
  (logs and degrades safely). Sort rearranges real chests with no undo.
