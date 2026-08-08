# Symbiotic Inventories — working notes for Claude

Client-side-only Vintage Story mod (`"side": "Client"`). One master inventory window: every
plain container the player opens — chests, clay vessels, baskets, ground-stored bags,
saddlebags, boat crates — is captured and re-drawn as a colour-shaded, numbered section
alongside the worn backpacks and the crafting grid.

Architecture and rationale live in [README.md](README.md). Verified compatibility state lives
in [COMPATIBILITY.md](COMPATIBILITY.md). The sections below are the parts that are easy to get
wrong twice.

## Layout

```
SymbioticInventories/        the project (holds modinfo.json + .csproj — tooling finds it this way)
  src/Core/                  InventorySection, SectionPalette, SectionRegistry  - what to draw
  src/Integration/           DialogCaptureService, CapturedDialog               - what to capture
  src/Gui/                   GuiDialogMasterInventory                           - the window
  assets/                    lang files
tools/                       test harness (see below)
dist/                        packaged mod zip
```

The `SymbioticInventories/` subfolder is **not** cosmetic. Both harness scripts discover the
project as "the folder under the repo root holding a `modinfo.json`" and name no mod anywhere.
Keep that property: it is what lets them be copied to the next mod verbatim.

## Build

Requires the **.NET 10 SDK** — every 1.22.x game assembly targets `net10.0`, and net9 cannot
compile against it (hard error CS1705, not a warning). SDK 10.0.302 is installed system-wide,
so plain `dotnet` works here.

```powershell
.\package.ps1              # build + zip into dist\
.\package.ps1 -Install     # ...and copy into the live Mods folder
```

Game references resolve from `%APPDATA%\Vintagestory`; override with `-p:VintagestoryDir=...`.

## Layout (unified flow — current)

The rectangle-packing era (shelf DP, skyline jigsaw, uniform tiles, L-shapes) is over: a run
of real screenshots proved mismatched container sizes can never tile a window without holes
or scrolling. The user proposed the design that replaced it, and it dissolves the problem:

- **One combined grid.** Every storage section's slots pour into a single row-major flow
  (`UnifiedGrid.Compute`). A section is a contiguous **ribbon** of cells - outlined and
  tinted in its accent colour the way a text selection spans line breaks - split into at
  most three slot-grid slices (lead partial row, full-rows block, tail partial row).
- **Maximally dense by construction**: every row is full except the last. Column count is
  arithmetic (`ChooseCols`: enough columns that the rows fit the height, capped by window
  width) - no candidate search, nothing to score.
- **Top strip, never scrolls**: crafting grid, worn-bag slots, and the vessel row - one
  passive icon tile per open storage (bag itemstack / container block), badge-numbered to
  match its ribbon and the legend rail.
- **Stability**: flow order is registry order; earlier ribbons are unaffected by later
  sections, so bags stay put as containers open/close after them. When an EARLIER section
  closes, later ribbons do shift - inherent to a flow, accepted by design.
- The retired packers are in git history (`5bd47d5` and earlier) if rectangles ever return.

## Layout (historical — superseded by unified flow)

Placement lives entirely in `src/Core/Layout/` and never in the GUI class. `SectionPacker`
returns a finished `LayoutPlan`; `GuiDialogMasterInventory` only turns that into elements.
This is what makes the two modes cheap — **Auto** (centered float) and **DockLeft** are
different `LayoutBudget`s handed to the same packer, not two window implementations.
(A third silhouette-packing "Shape" mode existed briefly; the user cut it. This repo has **no
git history**, so the code and all ten verified silhouettes are archived in `docs/attic/` —
deletion here is permanent unless a copy lands there first.)

- `GridShape` — how many columns a slot count wants. Prefers exact divisors (never a ragged
  last row), tie-broken by aspect or by width-filling depending on the candidate.
- `SectionPacker` — bands (Essentials / Storage / Hotbar), then per candidate either
  **optimal shelf breaking** (O(n²) DP, same shape as Knuth line-breaking) or the
  **jigsaw** for the storage band. Order is never changed — a bag that moves whenever a chest
  opens is worse than a bag in a slightly suboptimal spot.
- `JigsawPacker` — skyline packing for the storage band: tall and short sections interlock
  instead of paying per-shelf. Backpacks and containers share this band on purpose: skyline
  placement of *earlier* boxes never depends on later ones, and backpacks come first in
  registry order, so their positions stay stable as chests open/close — stability comes free.
  Candidate shapes are capped at 4:1 aspect; without that cap the first box on an empty
  skyline always scores best as its flattest shape (a 16-slot bag as a 16x1 smear).
- **Pinned band** — `essentials` (crafting + worn bags) sits above the scroll region and is
  always visible. Keep it small: it is fixed height stolen from the viewport it protects, and
  the probe fails the build if it exceeds half the height budget. The hotbar is not pinned
  (the vanilla HUD already shows it permanently).

The packer searches ~21 candidates (width caps × {shelf-aspect, shelf-widest, jigsaw}) and
scores them; fitting the screen dominates, height/raggedness/proportion break ties. It
searches because **neither strategy dominates**: jigsaw wins mixed loads (typical 466 vs 626),
shelf wins uniform chest-heavy loads where interlocking has nothing to grab (heavy 806 vs
942). Measured by the probe, not assumed.

## Dock mode mechanics

Docked, the window is a **HUD**: `DialogType` returns HUD, `PrefersUngrabbedMouse` false,
`ShouldReceiveMouseEvents()` false — the game keeps the mouse and the window is a live display
that can never swallow a click meant for the world. The focus hotkey (default **N**) flips all
three at once and recomposes; that flip is the entire mechanism. Opening always grants focus
(the player either asked for the window or just opened a container); closing clears it.

## Testing — four gates

A client-side mod cannot be fully proven by a headless server, so the gates are layered. Gates
1 and 2 are automated and mandatory. Gate 3 is manual and is the only one that can prove the
feature actually works.

### 0. Layout probe — after any change under `src/Core/Layout/`

```powershell
.\tools\layout-probe.ps1
```

Runs the packer headlessly over realistic section sets in every mode and asserts no overlaps,
no width-budget violations, no wholly-empty trailing rows, and that `Overflows`/`Unplaced` are
set honestly. Prints an ASCII map of each plan.

The packing math is pure computation — no rendering, no game state — so it is the one part of
this mod that can be properly tested without a client. **Read the maps, don't just check the
exit code:** a layout can pass every invariant and still be ugly, and the map is where you see
that. Every layout defect so far was found this way, not in-game.

### 1. Binding sweep — after any change to the capture layer

```powershell
.\tools\binding-sweep.ps1
```

Reflects over every cached game version and resolves the five bindings the mod depends on
(B1–B5, tabulated in COMPATIBILITY.md §1). Takes seconds, launches nothing.

**This gate exists because the server boot cannot cover it.** Our Harmony patches are
client-side, so they are never applied on a dedicated server — a version could rename
`GuiDialogCreatureContents.inv` and gate 2 would still pass clean. B4 binds to *private
fields*, which carry no compatibility promise, so this is the gate most likely to catch a real
break on a game update.

Each version is checked in its own child process. Seven copies of `VintagestoryAPI.dll` all
claim the same assembly name; loaded into one process the first would win for the entire run
and silently report one version's members as another's.

### 2. Compat matrix — after any code change, before any commit or release

```powershell
.\tools\compat-test.ps1              # against the installed server
.\tools\compat-test.ps1 -SkipBuild   # reuse the packaged zip
```

Boots a headless dedicated server once per combo (solo, +each companion, all together) and
fails on any `[Error]`/`[Warning]`, a wrong mod count or load order, or a violated marker.

For a client-side mod this proves less than for a universal one, but what it proves is real:
the server unpacks the zip, loads the assembly and instantiates its ModSystems before
`ShouldLoad` gates them off. That catches a broken zip, a bad modinfo, an assembly that no
longer loads against the target game version, and — via the exactly-one-mention silence check —
any accidental loss of the client-only gate.

**Companion set: `shipwright`.** It is the only installed mod that Harmony-patches a dialog we
also patch (`GuiDialogCreatureContents`), and its boats carry the entity containers we capture.
Grow this set from *this mod's real interaction surface* — mods adding container GUIs, seats or
mounts, or patching those two dialog classes. Do not copy another project's list wholesale;
content mods that only add blocks or recipes cannot collide with us and only cost boot time.

Companion zips are cached in `tools/compat-cache/` (sourced live-Mods-folder first, then
`ModsByServer/` newest-first, else the mod DB API). Delete the cache to re-source.

### 2.5. Client probe — semi-automated slice of gate 3

```powershell
.\tools\client-probe.ps1            # needs an ACTIVE desktop session
```

Boots a real client into an isolated-dataPath test world with only this mod, waits for
"[SymbioticInventories] Ready." + both "Capturing" lines, presses B, and screenshots before/
after. **Look at the screenshots** — a vanilla-looking second frame is a failure, not a pass.

Hard requirement: an active desktop. From a disconnected RDP session GLFW finds no monitors
and the game dies in `GetPrimaryMonitor()` *before mods load* — the crash log
(`ArgumentNullException 'handle'`, empty "Loaded Mods:") looks like a game bug but is purely
environmental. The script preflights for this and exits 2 with a clear message.

### 3. Manual in-game checklist — before any release

Gates 1 and 2 together still cannot see a single pixel. Rendering, click routing, and capture
suppression are unproven until someone launches the game. Until this is run, the mod is not
tested. See COMPATIBILITY.md §5 for the current honest status.

### Full version sweep — end of every version, before the release commit

```powershell
.\tools\version-sweep.ps1              # 1.22.0 .. 1.22.6, ~7 min
.\tools\version-sweep.ps1 -KeepGoing   # don't stop at the first failure
```

Runs gate 2 against every supported game version. `modinfo.json` declares `"game": "1.22.0"`,
which is a promise to every player on every version in that range; this is what keeps it
honest. Server packages (~300 MB each) are cached extracted in `tools/server-cache/`.

**Pair it with `binding-sweep.ps1`.** Neither is sufficient alone: the sweep covers load-time
across versions, the binding sweep covers patch-time across versions.

## Gotchas

- **Never close a captured dialog.** Its `OnGuiOpened`/`OnGuiClosed` pair performs the
  open/close handshake that keeps the inventory synced to this client. Closing it desyncs the
  container. Suppress rendering and park the composer off-screen instead — suppressing
  `OnRenderGUI` alone is not enough, because `GuiDialog` hit-tests against composer bounds
  rather than against what was drawn.
- **Route slot ops through the captured dialog's own `DoSendPacket`**, never a raw packet. The
  dialog knows its packet-id offset and block-entity/entity envelope; that is what makes
  containers from mods we have never seen work for free.
- **Machines must not be captured.** Firepit, quern, barrel, oven, trader and modded machinery
  all derive from `GuiDialogBlockEntity`, but their windows carry progress bars and readouts a
  slot grid cannot represent. The eligibility test is structural — must be a
  `GuiDialogBlockEntityInventory` — so modded machines get passthrough with no name list.
- **Resolve every Harmony target through `AccessTools` with a null guard and a logged warning.**
  A missing target must disable one capture point, never throw during mod load. This is what
  makes a single binary defensible across 1.22.0–1.22.6.
- **`AddStaticCustomDraw` bakes into the background surface at compose time**, so anything that
  has to move must not use it. The section chrome is one `AddDynamicCustomDraw` over the
  viewport, redrawn via `Redraw()` when the scroll offset changes; a static plate would stay
  put while its slot grid slid away.
- **Composer coordinate spaces — verified against engine IL, learned from a real screenshot:**
  elements added between `BeginClip`/`EndClip` are *children of the clip bounds* (BeginClip
  calls `BeginChildElements`), so their coordinates are viewport-relative; and an interactive
  `AddDynamicCustomDraw` element draws on its *own element-sized surface*, so its delegate
  must use local coordinates — `bounds.drawX/drawY` only works for static draws on the shared
  dialog surface. Getting either wrong doesn't error; content just renders displaced or not
  at all, which the headless probe cannot see.
- **Never recompose inside the scrollbar callback.** It destroys the element currently being
  dragged and the drag dies on the first pixel. Scroll by nudging `bounds.fixedY` +
  `CalcWorldBounds()` on the stored grid bounds (what vanilla does) and redrawing the chrome.
- **Assembly version is not a reliable version label.** 1.22.0–1.22.2 all stamp
  `VintagestoryAPI.dll` as `1.0.0.0`; only 1.22.3+ carry a real version. Identify a cached
  build by its folder name.
