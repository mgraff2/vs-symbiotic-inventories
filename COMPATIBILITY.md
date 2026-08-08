# Symbiotic Inventories — Compatibility Matrix

**Mod side:** Client only. No server install required; does not alter save data.
**Build:** single binary, `net10.0`, targets game 1.22.0+.

Every row below is a real result produced by a script in `tools/`, not an assessment.
Reproduce the whole thing with:

```powershell
.\tools\binding-sweep.ps1     # patch-time coverage, all versions, seconds
.\tools\version-sweep.ps1     # load-time coverage, all versions, ~7 min
```

---

## 1. What the mod actually binds to

Everything hinges on five bindings. Compatibility is entirely a question of whether these five
still exist and mean the same thing.

| # | Binding | Assembly | Kind | Used for |
|---|---------|----------|------|----------|
| B1 | `GuiDialogBlockEntity.OnGuiOpened / OnGuiClosed / OnRenderGUI(float)` | VintagestoryAPI | Harmony | Capturing **block** containers: chests, clay vessels, baskets, ground bags |
| B2 | `GuiDialogBlockEntity.Inventory` / `.BlockEntityPosition` / `.DoSendPacket(object)` | VintagestoryAPI | Public API + one non-public method | Reading the captured inventory, routing slot clicks |
| B3 | `GuiDialogCreatureContents.OnGuiOpened / OnGuiClosed / OnRenderGUI(float) / DoSendPacket` | **VSEssentials** | Harmony | Capturing **entity** containers: saddlebags, panniers, boat crates |
| B4 | `GuiDialogCreatureContents.inv` / `owningEntity` / `title` | VSEssentials | **Private fields**, reflected | Labelling and wiring entity containers |
| B5 | `ItemSlotBagContent.BagIndex`, `ItemSlotBackpack`, `GlobalConstants.*InvClassName` | VintagestoryAPI | Public API | Splitting the flat backpack inventory into numbered per-bag sections |

**Failure is graceful by construction.** Every Harmony target is resolved at runtime via
`AccessTools` and skipped with a logged warning if absent. A missing binding disables *one
capture point*; it does not throw during mod load or crash the game.

- B1/B2 break → block containers keep their own window; everything else still unifies.
- B3/B4 break → boat crates and saddlebags keep their own window; chests and backpacks still unify.
- B5 breaks → backpacks render as one undivided block instead of numbered per-bag sections.

B4 is the only binding on non-public members and is therefore the highest-risk row — private
field names carry no compatibility promise and can be renamed in a patch release.

---

## 2. Vintage Story vanilla — 1.22.0 through 1.22.6

| Version | TFM | B1 | B2 | B3 | B4 | B5 | Boot solo | Boot + Shipwright |
|---------|-----|----|----|----|----|----|-----------|-------------------|
| 1.22.0 | net10.0 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ PASS | ✅ PASS |
| 1.22.1 | net10.0 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ PASS | ✅ PASS |
| 1.22.2 | net10.0 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ PASS | ✅ PASS |
| 1.22.3 | net10.0 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ PASS | ✅ PASS |
| 1.22.4 | net10.0 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ PASS | ✅ PASS |
| 1.22.5 | net10.0 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ PASS | ✅ PASS |
| 1.22.6 | net10.0 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ PASS | ✅ PASS |

B-columns: `tools\binding-sweep.ps1`, 22 member resolutions per version.
Boot columns: `tools\version-sweep.ps1`, headless dedicated server per combo.
**One binary was used for all seven versions** — the point is that one build works everywhere,
not that seven builds each work somewhere.

**Resolved risk:** an earlier draft warned that a net9/net10 split across the range could make
a single binary impossible. It cannot — **all seven versions target `net10.0`**, verified from
each `VintagestoryAPI.dll`'s `TargetFrameworkAttribute`.

**Trap for future version work:** assembly version is not a reliable label. 1.22.0–1.22.2 all
stamp `VintagestoryAPI.dll` as `1.0.0.0`; only 1.22.3+ carry a real version. Identify a build
by its install folder name. (The game `.exe`'s file metadata is worse — it read "1.22.0" on a
1.22.6 install.)

---

## 3. Third-party mods

### 3.1 Shipwright: Vessels of Distant Shores 1.4.1 — verified, two ways

| Aspect | Finding | Verdict |
|--------|---------|---------|
| Harmony overlap | Patches exactly one method: `GuiDialogCreatureContents.IsInRangeOfBlock` (a `Prefix`). | ✅ **No conflict.** Disjoint from B3's hooks. Even a shared target would compose; these do not touch. |
| Co-load | Booted alongside our mod on all 7 game versions. | ✅ Clean on every one |
| Seats | `EntityShipwrightBoatSeat : EntityRideableSeat` (vanilla base). | ✅ "Aboard" detection via `MountedOn` works unmodified |
| Boat entity | `EntityShipwrightBoat : Entity`, behaviours from JSON. | ✅ Classified behaviourally, not by type name — no Shipwright-specific code |
| Crate containers | `CollectibleBehaviorInteractibleAttachment`; contents surface through `GuiDialogCreatureContents`. | ✅ Captured by B3 |

**Residual risk:** Shipwright's range-check prefix keeps the dialog open at distances vanilla
would close it at. The master window renders the captured inventory rather than the original
dialog, so it inherits that relaxed range. Expected, but confirm in-game.

### 3.2 Other installed mods

None touch container GUIs, seats, or the bag system. Assessed from metadata and role — **not**
boot-tested, because content mods that only add blocks or recipes cannot collide with B1–B5 and
each companion multiplies sweep time by seven.

| Mod | Version | Verdict |
|-----|---------|---------|
| ACulinaryArtillery | 2.0.0-dev.21 | ✅ Cooking containers are block containers → captured by B1 if standard |
| VS Eco Machina | 0.6.1 | ⚠️ Custom machine dialogs — passed through by design, see §4 |
| Clothing Rarity | 1.1.2 | ✅ Item attributes; does not alter `IHeldBag` |
| Wildcraft Trees / Herbarium / Herty Cups / Deciduous Trees / HC Test Tree | — | ✅ Content only |
| Automatic Forging / SmithingMacroDebug / Clayformer | — | ✅ No container GUI |
| Status HUD Continued / Pin Matrix / Tally Book / Translocator Paths / Waypointer | — | ✅ HUD/map only |

To promote any of these to a tested row, add one line to the companion set in
`tools/compat-test.ps1` and re-run the sweep.

---

## 4. Deliberately **not** captured

Machines and workstations also derive from `GuiDialogBlockEntity`, so B1 would otherwise absorb
them. It must not: their windows carry progress bars, temperature readouts, recipe pickers and
buttons with no representation in a plain slot grid. Absorbing a firepit would show its slots
while silently destroying any way to see whether it is lit.

Passing through unchanged: **firepit, quern, bloomery, barrel, oven, pulverizer, stone coffin,
trader, and modded machinery** (including VS Eco Machina).

**Enforcement is structural, not a name list:** capture requires the dialog to be a
`GuiDialogBlockEntityInventory` (the plain slot-grid container dialog). Machines subclass
`GuiDialogBlockEntity` directly and never pass that test, so a modded machine gets passthrough
for free.

---

## 5. Verification status — what is and is not proven

| Claim | Status |
|-------|--------|
| Builds as one `net10.0` binary | ✅ 0 errors, 0 warnings |
| Five bindings resolve on 1.22.0–1.22.6 | ✅ 22 checks × 7 versions, all pass |
| Loads clean on a server, 1.22.0–1.22.6 | ✅ 7 versions × 2 combos, all pass |
| Client-only gate intact (server-side silence) | ✅ Enforced by the exactly-one-mention check |
| No Harmony conflict with Shipwright | ✅ Verified by reflection **and** co-boot on all 7 |
| Machine dialogs pass through | ✅ Implemented, structural test |
| Window renders, captures, docks containers | ✅ Confirmed in a real ~65-mod client (iterated over many sessions) |
| Slot clicks / crafting route correctly | ✅ Confirmed in-game |
| Layout fills the screen without dead space | ✅ Confirmed in-game (landscape flow) |
| **Mount/boat auto-open** | ⚠️ Mechanism sound, but entity selection-box geometry is unverified in-game; logs and degrades safely |

The honest summary: **the mod has been driven hard in a real, heavily-modded client over many
iterations** — capture, rendering, docking, hide/show and crafting are all confirmed
working there. The one path still flying blind is entity (mount/boat) auto-open, which cannot
be verified without a live elk/boat and is written to log-and-degrade rather than fail.

Several real bugs were found only by playing and fixed in turn: a crash clicking a worn bag
(inventory shape change), two render coordinate-space bugs (clipped grids and off-surface
chrome), a client-killing shader crash from the hover label, non-functional crafting, and the
tile-click and title-overlap issues. Each has a commit explaining the cause.

---

## 6. The procedure

Three gates. The first two are automated and mandatory before any commit; the third is the only
one that can prove the feature works.

### Gate 1 — binding sweep (patch time)

```powershell
.\tools\binding-sweep.ps1
```

Reflects over every cached version and resolves all five bindings. Seconds, launches nothing.

**This gate exists because gate 2 structurally cannot cover it.** Our Harmony patches are
client-side, so they are never applied on a dedicated server — a game update could rename
`GuiDialogCreatureContents.inv` and gate 2 would still report a clean boot on every version.
B4 binds to private fields, so this is the gate most likely to catch a real break.

Each version runs in its own child process: seven copies of `VintagestoryAPI.dll` all claim the
same assembly name, and loaded into one process the first would win for the whole run, silently
reporting one version's members as another's.

### Gate 2 — server boot matrix (load time)

```powershell
.\tools\compat-test.ps1               # installed server, quick iteration
.\tools\version-sweep.ps1             # all of 1.22.0 .. 1.22.6, ~7 min
.\tools\version-sweep.ps1 -KeepGoing  # don't stop at the first failure
```

Boots a headless dedicated server once per combo (solo, +each companion, all together) and
fails on any `[Error]`/`[Warning]`, wrong mod count or load order, or a violated marker.

For a client-side mod this proves less than for a universal one, but what it proves is real:
the server unpacks the zip, loads the assembly and instantiates its ModSystems before
`ShouldLoad` gates them off. That catches a broken zip, a bad modinfo, an assembly that no
longer loads against a game version, and — via the exactly-one-mention silence check — any
accidental loss of the client-only gate.

The zip is built **once** and every version reuses that exact artifact. If you change code
mid-sweep, re-run it; the results belong to the binary that was built, not the one on disk.

Server packages (~300 MB each) cache extracted in `tools/server-cache/`; companion zips in
`tools/compat-cache/`. Both are gitignored and repopulate on demand.

### Gate 3 — manual in-game checklist (behaviour)

Neither automated gate renders a frame. Before any release, in a real client:

1. Open a chest → it docks into the master window instead of its own; the original window is
   nowhere on screen and does not swallow clicks.
2. Close it → its section disappears **(this is the §7 regression; check it explicitly)**.
3. Open two containers at once → both dock, with distinct numbers and accent colours.
4. Click, shift-click and drag between a chest section and a backpack section → items land in
   the inventory the section claims.
5. Backpack sections match the bags actually worn, numbered consistently with the left rail.
6. Crafting grid present and functional.
7. Open a firepit → it uses its **own** window, unchanged, with its progress bar intact.
8. Board a Shipwright boat, open a crate → docks as an "aboard" section.
9. Walk out of range of an open chest → its section clears without a stuck window.

### When adding a game version

Append it to `-Versions` in `tools/version-sweep.ps1`, run both sweeps, and add the row to §2.
The CDN 404s for versions that do not exist, which is how you discover the current latest.

---

## 7. Bug found by this procedure

Worth recording, because it is exactly the class of bug the automated gates **cannot** catch and
that inspection did.

`GuiDialogBlockEntityInventory.OnGuiClosed()` calls `base.OnGuiClosed()` **only when its
`packetIdOffset` is zero** — the IL branches straight past the base call otherwise. A Harmony
patch on the base declaration therefore never fires for those containers, so a captured section
would have lingered in the master window forever after the chest was shut.

Chasing every override is not a fix, because third-party subclasses are unknowable at startup.
The capture service instead sweeps every 200 ms for captured dialogs that are no longer
`IsOpened()` and releases them — correct for subclasses we have never seen. The `OnGuiClosed`
prefix is retained as the fast path.
