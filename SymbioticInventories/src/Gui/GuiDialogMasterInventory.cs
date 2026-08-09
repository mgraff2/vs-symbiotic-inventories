using System;
using System.Collections.Generic;
using Cairo;
using SymbioticInventories.Core;
using SymbioticInventories.Core.Layout;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SymbioticInventories.Gui
{
    /// <summary>
    /// The single window, unified-flow edition.
    ///
    /// Top strip (never scrolls): crafting grid, worn-bag slots, and the vessel row - one
    /// icon tile per open storage, badge-numbered. Below it, ONE combined slot grid: every
    /// storage section's slots pour into a row-major flow, and each section is a contiguous
    /// coloured *ribbon* outlined the way a text selection spans line breaks. The badge on a
    /// vessel tile matches the badge at its ribbon's first cell.
    ///
    /// This replaced a rectangle-packing design after a run of screenshots showed mismatched
    /// container sizes can never tile a window cleanly: rectangles always left holes or
    /// forced a scrollbar. A flow is maximally dense by construction - every row full except
    /// the last - and fluid: the column count is arithmetic from the window size, not a
    /// candidate search.
    ///
    /// Docked behaviour: the window stays on screen like a HUD element - the mouse stays
    /// grabbed and gameplay continues around it. The focus hotkey flips it into a normal
    /// interactive dialog; pressing it again, or closing, returns it to passive HUD mode.
    /// </summary>
    public class GuiDialogMasterInventory : GuiDialog
    {
        public const string HotkeyCode = "symbioticinventory";
        public const string FocusHotkeyCode = "symbioticinventoryfocus";

        private const string ChromeKey = "chrome";
        private const string ScrollKey = "scrollbar";
        private const string StripGlowKey = "stripglow";

        private const double Pad = 10;
        private const double FooterH = 30;

        /// <summary>Vertical space the dialog title bar occupies at the top; content sits below.</summary>
        private static readonly double TitleH = GuiStyle.TitleBarHeight;

        /// <summary>Vessel-row tiles are 2/3 slot size: recognisable, but clearly not slots.</summary>
        private const double IconTile = 36;

        private readonly SectionRegistry registry;

        private List<InventorySection> sections = new();
        private readonly List<InventorySection> numberedSections = new();
        private readonly List<InventorySection> flowSections = new();
        private UnifiedPlan plan = new();

        /// <summary>
        /// Sections the player has hidden by clicking their vessel tile. Keyed by section id,
        /// so a hidden chest stays hidden across recomposes but forgets on close. The dialog
        /// underneath stays open - hiding is a display choice, not a disconnect.
        /// </summary>
        private readonly HashSet<string> hiddenIds = new();

        private LayoutMode mode = LayoutMode.Auto;
        private bool dockFocused;

        /// <summary>Opens the options dialog. Wired by the mod system.</summary>
        public Action OpenOptions;

        /// <summary>Live config, for behaviours the window drives (sort options). Wired by the mod system.</summary>
        public ModConfig Config;

        /// <summary>Capture service, for the contextual cellar button. Wired by the mod system.</summary>
        public Integration.DialogCaptureService Capture;

        /// <summary>
        /// While true the window renders nothing and ignores input. Used while the Options
        /// panel is open: the master window's item sprites render in a LATER stage than any
        /// dialog's background (OnFinalizeFrame vs OnRenderGUI), so they would always draw on
        /// top of the Options panel no matter the dialog order. Hiding the window entirely is
        /// the only clean fix - the player is adjusting settings, not viewing inventory.
        /// </summary>
        public bool Suppressed;

        /// <summary>Grid bounds paired with their viewport-relative Y, for scrolling without recompose.</summary>
        private readonly List<(ElementBounds bounds, double relY)> scrollables = new();

        private double scrollY;
        private double viewportH;
        private double contentH;

        /// <summary>Dialog-space Y where the scrolling flow begins (below the top strip).</summary>
        private double scrollStart;

        /// <summary>Dialog-space X of the flow grid's left edge.</summary>
        private double contentX;

        /// <summary>Where each vessel tile was placed: dialog-space position for the chrome
        /// pass, live bounds for click hit-testing.</summary>
        private readonly List<(double x, double y, ElementBounds bounds, InventorySection s)> iconTiles = new();

        /// <summary>Group chips: one per container type, toggling every member at once.</summary>
        private readonly List<(double x, double y, double w, ElementBounds bounds, string label, List<InventorySection> members)> groupChips = new();

        /// <summary>Chip width in unscaled units. Narrow: the tiles carry the identity.</summary>
        private const double ChipW = 30;

        /// <summary>Backing inventory for the vessel row's passive tiles.</summary>
        private readonly List<DummySlot> iconSlots = new();

        private readonly List<(IInventory inv, Action<int> handler)> watched = new();

        /// <summary>
        /// Slots we painted with their section's accent, and what colour they had before.
        /// ItemSlot.HexBackgroundColor is the engine's own slot-face tint (it is how vanilla
        /// colours bag-content slots), so the tint renders IN the cell - a plate drawn under
        /// an opaque slot texture only ever showed in the padding gutters.
        /// </summary>
        private readonly Dictionary<ItemSlot, string> paintedSlots = new();

        /// <summary>Hovered-slot lookup: slot object -> owning section, rebuilt each compose.</summary>
        private readonly Dictionary<ItemSlot, InventorySection> slotToSection = new();

        /// <summary>Cached "#N Container" hover label texture and the key it was baked for.</summary>
        private LoadedTexture hoverTex;
        private string hoverTexKey;

        /// <summary>Section whose vessel tile glows because the cursor is on one of its
        /// flow cells (cell -> tile direction of the hover link).</summary>
        private string glowTileId;

        /// <summary>Section whose whole ribbon glows because the cursor is on its vessel
        /// tile (tile -> cells direction). Its tile lights up too.</summary>
        private string glowRibbonId;

        /// <summary>The flow viewport bounds, kept for frame-time screen-space math
        /// (per-row bag icons, the mount portrait).</summary>
        private ElementBounds flowViewport;

        /// <summary>Width (unscaled GUI units) of the icon margin left of the flow.</summary>
        private double iconMargin;

        /// <summary>Reused for the frame-time icon draws (the stack overload is obsolete).</summary>
        private readonly DummySlot iconDrawSlot = new();

        /// <summary>Nearest quern in working range, refreshed each compose. Its two slots
        /// render as a side-station top-right of the strip; null hides the panel.</summary>
        private BlockPos quernPos;
        private IInventory quernInv;

        /// <summary>The quern OUTPUT slot's bounds - deposit clicks on it are swallowed.</summary>
        private ElementBounds quernOutBounds;

        /// <summary>Slot grids belonging to synthetic-interaction sections (FoodShelves):
        /// clicks on them are swallowed and become real block interactions.</summary>
        private readonly List<(ElementBounds b, RibbonSlice slice, InventorySection s)> synthGrids = new();

        public GuiDialogMasterInventory(ICoreClientAPI capi, SectionRegistry registry) : base(capi)
        {
            this.registry = registry;
        }

        public override string ToggleKeyCombinationCode => HotkeyCode;

        public override bool PrefersUngrabbedMouse => mode != LayoutMode.DockLeft || dockFocused;

        public override EnumDialogType DialogType
            => mode == LayoutMode.DockLeft && !dockFocused ? EnumDialogType.HUD : EnumDialogType.Dialog;

        public override bool ShouldReceiveMouseEvents()
            => (mode != LayoutMode.DockLeft || dockFocused) && base.ShouldReceiveMouseEvents();

        public bool ToggleDockFocus()
        {
            if (mode != LayoutMode.DockLeft || !IsOpened()) return false;
            dockFocused = !dockFocused;
            Compose();
            return true;
        }

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            dockFocused = true;

            // The crafting inventory must be OPENED, not merely displayed: recipe matching
            // and ingredient consumption run server-side only for open inventories, and the
            // open packet is what tells the server. Without this the grid accepted items and
            // produced nothing - vanilla's own dialog does exactly this on open.
            var im = capi.World.Player.InventoryManager;
            var craftInv = im.GetOwnInventory(GlobalConstants.craftingInvClassName);
            if (craftInv != null)
            {
                var packet = im.OpenInventory(craftInv);
                if (packet != null) capi.Network.SendPacketClient(packet);
            }

            Compose();
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            dockFocused = false;
            UnwatchInventories();

            // Return anything left in the crafting grid to the player, then close it. This is
            // exactly what vanilla's inventory dialog does on close (verified in its
            // OnGuiClosed IL): CloseInventoryAndSync alone only closes the inventory, it does
            // NOT move the items out, so a half-finished craft used to sit in the grid until
            // reopened. TryTransferAway pushes each stack into the backpack/hotbar and returns
            // the sync packets to send.
            var im = capi.World.Player.InventoryManager;
            var craftInv = im.GetOwnInventory(GlobalConstants.craftingInvClassName);
            if (craftInv != null)
            {
                foreach (var slot in craftInv)
                {
                    if (slot == null || slot.Empty) continue;
                    var op = new ItemStackMoveOperation(capi.World, EnumMouseButton.Left, 0,
                        (EnumMergePriority)0, slot.StackSize) { ActingPlayer = capi.World.Player };
                    var packets = im.TryTransferAway(slot, ref op, true, false);
                    if (packets != null)
                        foreach (var p in packets) capi.Network.SendPacketClient(p);
                }
                im.CloseInventoryAndSync(craftInv);
            }

            RestoreSlotPaint();
        }

        // ---- slot-face painting ---------------------------------------------------

        /// <summary>
        /// Lightened accent: HexBackgroundColor multiplies the parchment slot texture, so a
        /// full-strength accent would render slots muddy-dark. ~45% accent toward white keeps
        /// the hue obvious and the item sprite readable.
        /// </summary>
        private static string PastelHex(double[] a)
        {
            int C(double v) => (int)Math.Round((v * 0.45 + 0.55) * 255);
            return $"#{C(a[0]):X2}{C(a[1]):X2}{C(a[2]):X2}";
        }

        /// <summary>Paints every visible ribbon's slot faces in its section colour, and
        /// rebuilds the hover lookup while it is walking the same slots anyway.</summary>
        private void PaintRibbonSlots()
        {
            RestoreSlotPaint();
            slotToSection.Clear();

            foreach (var s in flowSections)
            {
                string hex = PastelHex(s.Accent);
                foreach (var id in s.SlotIds)
                {
                    var slot = s.Inventory[id];
                    if (slot == null) continue;
                    if (!paintedSlots.ContainsKey(slot)) paintedSlots[slot] = slot.HexBackgroundColor;
                    slot.HexBackgroundColor = hex;
                    slotToSection[slot] = s;
                }
            }
        }

        /// <summary>
        /// Puts every painted slot back to the colour it had (bag slots have vanilla tints
        /// of their own that must survive us). Runs before each repaint and on close.
        /// </summary>
        private void RestoreSlotPaint()
        {
            foreach (var kv in paintedSlots) kv.Key.HexBackgroundColor = kv.Value;
            paintedSlots.Clear();
        }

        public void Refresh()
        {
            if (!IsOpened()) return;
            Compose();
        }

        /// <summary>
        /// Vessel tiles are toggles: click to hide a container's ribbon, click to bring it
        /// back. The hit-test MUST run before base.OnMouseDown - the base dialog marks every
        /// click inside the window as handled (verified in its IL: any composer bounds
        /// containing the point => Handled) to stop click-through to the world, so testing
        /// afterwards sees Handled=true and never fires. Real bug: tiles ignored all clicks.
        /// </summary>
        public override void OnMouseDown(MouseEvent args)
        {
            if (Suppressed) return;   // invisible under the Options panel: let it take the click
            if (IsOpened() && !args.Handled)
            {
                // Quern OUTPUT is output-only: swallow any click that would deposit (the
                // mouse is carrying a stack). Taking with an empty hand falls through to
                // the slot grid as normal.
                if (quernOutBounds != null && quernOutBounds.PointInside(args.X, args.Y)
                    && capi.World.Player.InventoryManager.MouseItemSlot?.Empty == false)
                {
                    args.Handled = true;
                    return;
                }

                // Shelf cells (FoodShelves): there is no slot packet route, so a click
                // acts as the real block interaction on that cell's segment - empty hand
                // takes the item into your inventory, a shelvable item in the ACTIVE
                // HOTBAR slot puts. Swallowed before the slot grid element sees it (its
                // normal slot ops would silently desync).
                foreach (var (b, slice, s) in synthGrids)
                {
                    if (!b.PointInside(args.X, args.Y)) continue;
                    double gsc = RuntimeEnv.GUIScale;
                    int c = Math.Clamp((int)((args.X - b.absX) / gsc / LayoutMetrics.Cell), 0, slice.Cols - 1);
                    int r = Math.Clamp((int)((args.Y - b.absY) / gsc / LayoutMetrics.Cell), 0, slice.Rows - 1);
                    s.OnCellClick(slice.SlotOffset + r * slice.Cols + c);
                    args.Handled = true;
                    return;
                }

                // Group chips: hide the whole container type at once; if the whole group is
                // already hidden, bring it all back.
                foreach (var (_, _, _, bounds, _, members) in groupChips)
                {
                    if (!bounds.PointInside(args.X, args.Y)) continue;

                    bool allHidden = members.TrueForAll(m => hiddenIds.Contains(m.Id));
                    foreach (var m in members)
                    {
                        if (allHidden) hiddenIds.Remove(m.Id);
                        else hiddenIds.Add(m.Id);
                    }
                    scrollY = 0;
                    Compose();
                    args.Handled = true;
                    return;
                }

                foreach (var (_, _, bounds, s) in iconTiles)
                {
                    if (!bounds.PointInside(args.X, args.Y)) continue;

                    if (!hiddenIds.Remove(s.Id)) hiddenIds.Add(s.Id);
                    scrollY = 0;
                    Compose();
                    args.Handled = true;
                    return;
                }
            }

            base.OnMouseDown(args);
        }

        /// <summary>Hover link, tile -> cells: cursor on a vessel tile lights its ribbon.</summary>
        public override void OnMouseMove(MouseEvent args)
        {
            base.OnMouseMove(args);
            if (Suppressed || !IsOpened()) { SetRibbonGlow(null); return; }

            InventorySection hit = null;
            foreach (var (_, _, bounds, s) in iconTiles)
            {
                if (bounds.PointInside(args.X, args.Y)) { hit = s; break; }
            }
            SetRibbonGlow(hit?.Id);
        }

        /// <summary>Change-detected: redraws only when the glowing tile actually changes.</summary>
        private void SetTileGlow(string id)
        {
            if (glowTileId == id) return;
            glowTileId = id;
            (SingleComposer?.GetElement(StripGlowKey) as GuiElementCustomDraw)?.Redraw();
        }

        private void SetRibbonGlow(string id)
        {
            if (glowRibbonId == id) return;
            glowRibbonId = id;
            (SingleComposer?.GetElement(StripGlowKey) as GuiElementCustomDraw)?.Redraw();
            (SingleComposer?.GetElement(ChromeKey) as GuiElementCustomDraw)?.Redraw();
        }

        // ---- composition --------------------------------------------------------

        private void Compose()
        {
            sections = registry.Build();
            numberedSections.Clear();
            flowSections.Clear();
            foreach (var s in sections)
            {
                if (s.Number <= 0) continue;
                numberedSections.Add(s);
            }

            // Group by container type (backpacks / chests / vessels / trunks...), groups in
            // first-seen order, members in original order. The vessel row AND the flow share
            // this ordering, so tiles and ribbons always read in the same sequence.
            var groupOrder = new List<string>();
            foreach (var s in numberedSections) if (!groupOrder.Contains(s.GroupKey)) groupOrder.Add(s.GroupKey);
            numberedSections.Sort((a, b) =>
            {
                int ga = groupOrder.IndexOf(a.GroupKey), gb = groupOrder.IndexOf(b.GroupKey);
                return ga != gb ? ga.CompareTo(gb) : a.Number.CompareTo(b.Number);
            });

            foreach (var s in numberedSections)
            {
                if (!hiddenIds.Contains(s.Id)) flowSections.Add(s);
            }

            PaintRibbonSlots();

            scrollables.Clear();
            iconTiles.Clear();
            iconSlots.Clear();
            groupChips.Clear();
            synthGrids.Clear();

            double scale = Math.Max(0.1, RuntimeEnv.GUIScale);
            double screenW = capi.Render.FrameWidth / scale;
            double screenH = capi.Render.FrameHeight / scale;

            bool docked = mode == LayoutMode.DockLeft;

            // Creative co-op: E in creative opens the catalog AND this window. Rather than
            // overlap it, read the catalog's LIVE bounds and fit this window into the wider
            // free side of the screen beside it - the two act as one combined surface, and
            // this recomposes whenever the catalog opens or closes (mod-system tick), so
            // the fit is dynamic, not a guess.
            // Creative co-op: E in creative opens the catalog AND this window, and this
            // window ALWAYS sits to the catalog's RIGHT - the tab-free side (the catalog's
            // tab column overhangs its left edge, outside its composer bounds, so snapping
            // left collided with it; user settled on right, always). The window sizes to
            // the actual free width and hugs the screen's right edge. Too narrow for a
            // real grid (big GUI scale) falls back to the normal centered layout.
            bool coop = false;
            double coopAvailW = 0;
            if (!docked
                && capi.World?.Player?.WorldData?.CurrentGameMode == EnumGameMode.Creative)
            {
                var cat = Integration.InventoryKeyInterceptor.VanillaInventoryDialog;
                if (cat != null && cat.IsOpened())
                {
                    // The catalog's right edge is the max across ALL its composers -
                    // GuiDialogInventory composes into named composers, so SingleComposer
                    // alone can be null or just one panel of it.
                    double maxX = double.MinValue;
                    foreach (var comp in cat.Composers.Values)
                    {
                        var b = comp?.Bounds;
                        if (b == null) continue;
                        maxX = Math.Max(maxX, b.absX + b.OuterWidth);
                    }

                    if (maxX > double.MinValue)
                    {
                        // ALWAYS snap right of the catalog (user rule). A cramped screen
                        // gets a narrow scrolling grid rather than a centered window under
                        // the catalog - and any residual overlap lands on the catalog's
                        // tab-free right edge, never its tab column.
                        coopAvailW = Math.Max(4 * LayoutMetrics.Cell, screenW - maxX / scale - Pad * 2 - 8);
                        coop = true;
                    }
                }
            }

            // No legend rail: the vessel tiles carry the icons and badges, and hovering a cell
            // now names its container, so the rail was redundant chrome. Removing it shrinks
            // the whole window (user ask). contentX is just the dialog padding.
            double railW = 0;
            double chromeH = 40 + FooterH + Pad * 3;
            double availW = docked
                ? Math.Min(screenW * 0.28, 10 * LayoutMetrics.Cell)
                : coop
                    ? coopAvailW
                    : Math.Max(8 * LayoutMetrics.Cell, screenW * 0.86 - Pad * 2);
            // 0.86 leaves a margin around the window instead of running edge-to-edge. Slot
            // cells cannot be shrunk (the engine renders them at a fixed unscaledSlotSize *
            // GUI scale, with no per-grid override), so this margin - plus the player's GUI
            // scale setting - is the lever for overall window size.
            double availH = screenH * 0.86 - chromeH;

            // ---- top strip: crafting + worn bags + vessel row -------------------
            var crafting = sections.Find(s => s.Kind == SectionKind.Crafting);
            var bagSlots = sections.Find(s => s.Kind == SectionKind.BackpackSlots);

            // Frame capacity is ALL open containers, hidden ones included: the window sizes
            // itself for everything and filter toggles only reflow ribbons INSIDE that fixed
            // frame, leaving empty rows at the bottom. Resizing on every filter click forced
            // the player to re-find their bearings each time (their words); the frame now
            // only changes when a container genuinely opens or closes.
            int frameSlots = 0;
            foreach (var s in numberedSections) frameSlots += s.SlotCount;

            // Crafting is 3x3 PLUS its output slot to the right - the inventory's last slot.
            // Without the output slot on screen there is nowhere for a craft result to
            // appear (real bug: "I can put stuff in it but there is no output").
            double craftingW = crafting != null
                ? (crafting.FixedColumns + 1) * LayoutMetrics.Cell + 8 + Pad
                : 0;
            double bagsW = bagSlots != null ? bagSlots.SlotCount * LayoutMetrics.Cell + Pad : 0;
            double craftingH = crafting != null
                ? Math.Ceiling(crafting.SlotCount / (double)crafting.FixedColumns) * LayoutMetrics.Cell
                : 0;

            double iconAreaX = craftingW + bagsW;

            // ---- the flow WIDTH is chosen first, and everything else fits inside it -------
            // The strip used to wrap to the full available width, making the window wide while
            // the grid stayed narrow - a lake of empty space to the right (user screenshot).
            // Now the grid width leads: it fills the landscape, and the strip wraps within it,
            // so the window is exactly grid-wide with nothing dangling.
            int cols = docked
                ? Math.Max(4, (int)(availW / LayoutMetrics.Cell))
                : UnifiedGrid.ChooseCols(frameSlots, availW, availH - craftingH - Pad * 2);
            // Floating with containers open: guarantee the backward-L a real side region
            // beside the bag block. Docked stays width-driven and stacks instead.
            if (!docked)
                cols = UnifiedGrid.EnsureSideRoom(cols, flowSections, (int)(availW / LayoutMetrics.Cell));
            plan = UnifiedGrid.Compute(flowSections, cols);
            double flowW = plan.Cols * LayoutMetrics.Cell;

            // Quern side-station discovery: reserve its corner of the strip before the
            // vessel tiles compute their wrap width, so the tiles never collide with it.
            var quern = Capture?.FindNearbyQuern();
            quernPos = quern?.pos;
            quernInv = quern?.inv;
            double quernW = quernInv != null ? IconTile + 6 + 2 * LayoutMetrics.Cell + 8 : 0;

            // Vessel row: group chips + tiles, wrapping within the grid width beside crafting
            // and bags. ALL numbered sections get a tile - hidden ones render dimmed. Each
            // group (chests / vessels / backpacks) is prefixed by a narrow toggle chip.
            double iconAreaW = Math.Max(IconTile + ChipW, flowW - iconAreaX - quernW);

            var stripItems = new List<(bool isChip, InventorySection s, List<InventorySection> members, double w)>();
            {
                // Vessel-row order mirrors the grid's own reading order: your bags, then
                // the mount's bags, then the containers you clicked open (user ask).
                var stripOrder = new List<InventorySection>();
                stripOrder.AddRange(numberedSections.FindAll(s => s.Kind == SectionKind.Backpack));
                stripOrder.AddRange(numberedSections.FindAll(s => s.Kind == SectionKind.Mount));
                stripOrder.AddRange(numberedSections.FindAll(
                    s => s.Kind != SectionKind.Backpack && s.Kind != SectionKind.Mount));

                string lastGroup = null;
                foreach (var s in stripOrder)
                {
                    if (s.GroupKey != lastGroup)
                    {
                        lastGroup = s.GroupKey;
                        var members = stripOrder.FindAll(m => m.GroupKey == s.GroupKey);
                        if (members.Count > 1) stripItems.Add((true, s, members, ChipW + 4));
                    }
                    stripItems.Add((false, s, null, IconTile + 4));
                }
            }

            var stripPos = new List<(double x, double y)>();
            {
                double sx = 0, sy = 0;
                foreach (var item in stripItems)
                {
                    if (sx > 0 && sx + item.w > iconAreaW + 0.001) { sx = 0; sy += IconTile + 4; }
                    stripPos.Add((sx, sy));
                    sx += item.w;
                }
            }
            int iconRows = stripItems.Count == 0 ? 0 : (int)((stripPos[^1].y / (IconTile + 4)) + 1);

            double stripH = Math.Max(craftingH, Math.Max(bagSlots != null ? LayoutMetrics.Cell : 0, iconRows * (IconTile + 4))) + Pad;

            contentH = plan.Rows * LayoutMetrics.Cell;
            // Frame height = the plan for ALL numbered sections (hidden ones included), so
            // filter toggles reflow inside a stable window. Computed by the same layout, not
            // by slot arithmetic - the L's gutter and per-bag lines make ceil(n/cols) wrong.
            double frameH = UnifiedGrid.Compute(numberedSections, cols).Rows * LayoutMetrics.Cell;
            // Everything sits below the title bar. Content used to start at Y=0, the same row
            // the title bar draws in, so the crafting grid and vessel tiles covered up
            // "Symbiotic Inventories" (user screenshot).
            scrollStart = TitleH + stripH;
            viewportH = Math.Min(frameH, Math.Max(availH - stripH, 2 * LayoutMetrics.Cell));

            bool scrolls = contentH > viewportH + 0.5;
            scrollY = Math.Clamp(scrollY, 0, Math.Max(0, contentH - viewportH));

            contentX = railW;

            // Left margin for the per-row bag icons - only when the left block exists, so
            // a pure-container window stays flush.
            iconMargin = plan.Ribbons.Exists(r => UnifiedGrid.IsLeftBlock(r.Section.Kind))
                ? LayoutMetrics.Cell * 0.8 : 0;

            double bodyW = contentX + iconMargin + flowW + (scrolls ? 20 : 0);
            double bodyH = TitleH + stripH + Math.Max(viewportH, 60) + FooterH + Pad;

            var bgBounds = ElementBounds.Fixed(0, 0, bodyW, bodyH)
                .WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(docked ? EnumDialogArea.LeftMiddle
                    : coop ? EnumDialogArea.RightMiddle
                    : EnumDialogArea.CenterMiddle);
            // Hug the screen's right edge, a whisker of breathing room from the border.
            if (coop) dialogBounds = dialogBounds.WithFixedAlignmentOffset(-4, 0);

            var composer = capi.Gui
                .CreateCompo("symbioticinventories:master", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(TitleText(), () => TryClose())
                .BeginChildElements(bgBounds);

            // Top strip elements, all shifted below the title bar (dialog space).
            if (crafting != null)
            {
                var b = ElementStdBounds.SlotGrid(EnumDialogArea.None, contentX, TitleH, crafting.FixedColumns,
                    (int)Math.Ceiling(crafting.SlotCount / (double)crafting.FixedColumns));
                composer.AddItemSlotGrid(crafting.Inventory, crafting.SendPacket, crafting.FixedColumns, crafting.SlotIds, b, "grid-craft");

                // Output slot: the crafting inventory's last slot, centered right of the grid.
                int outId = crafting.Inventory.Count - 1;
                var ob = ElementStdBounds.SlotGrid(EnumDialogArea.None,
                    contentX + crafting.FixedColumns * LayoutMetrics.Cell + 8,
                    TitleH + (craftingH - LayoutMetrics.Cell) / 2, 1, 1);
                composer.AddItemSlotGrid(crafting.Inventory, crafting.SendPacket, 1, new[] { outId }, ob, "grid-craftout");
            }
            if (bagSlots != null)
            {
                var b = ElementStdBounds.SlotGrid(EnumDialogArea.None, contentX + craftingW, TitleH, bagSlots.SlotCount, 1);
                composer.AddItemSlotGrid(bagSlots.Inventory, bagSlots.SendPacket, bagSlots.SlotCount, bagSlots.SlotIds, b, "grid-bags");
            }

            for (int i = 0; i < stripItems.Count; i++)
            {
                var item = stripItems[i];
                double ix = contentX + iconAreaX + stripPos[i].x;
                double iy = TitleH + stripPos[i].y;

                if (item.isChip)
                {
                    var chipBounds = ElementBounds.Fixed(ix, iy, ChipW, IconTile);
                    composer.AddStaticCustomDraw(chipBounds, (ctx, surface, b) => { });   // gives the bounds world coords
                    groupChips.Add((ix, iy, ChipW, chipBounds, item.s.Label, item.members));
                    continue;
                }

                var s = item.s;
                var tileBounds = ElementBounds.Fixed(ix, iy, IconTile, IconTile);
                iconTiles.Add((ix, iy, tileBounds, s));

                if (s.Icon != null)
                {
                    var slot = new DummySlot(s.Icon);
                    iconSlots.Add(slot);   // keep alive for the composer's lifetime
                    composer.AddPassiveItemSlot(tileBounds,
                        s.Inventory as InventoryBase, slot, false, "icon-" + s.Id);
                }
                else
                {
                    // No icon (entity containers): the bounds still need world coords for
                    // hit-testing, which only parented elements get. Park an invisible
                    // static element on them.
                    composer.AddStaticCustomDraw(tileBounds, (ctx, surface, b) => { });
                }
            }

            // Strip chrome (badges over vessel tiles) bakes into the dialog background. The
            // element spans from the title bar down; the tile positions stored in iconTiles
            // already include the TitleH offset, so DrawStripChrome adds no offset of its own.
            composer.AddStaticCustomDraw(
                ElementBounds.Fixed(contentX, 0, bodyW - contentX, TitleH + stripH),
                (ctx, surface, bounds) => DrawStripChrome(ctx, bounds));

            // Hover glow OVER the strip. The badge chrome above is static - baked once at
            // compose - so a glow drawn there would be frozen; this layer is dynamic and
            // redraws on hover changes. Dynamic draw = own surface, LOCAL coordinates.
            composer.AddDynamicCustomDraw(
                ElementBounds.Fixed(contentX, 0, bodyW - contentX, TitleH + stripH),
                (ctx, surface, bounds) => DrawStripGlow(ctx), StripGlowKey);

            // Quern side-station, top-right of the strip: the quern's icon beside its two
            // slots (input, output). NOT a capture - the quern keeps its own dialog with
            // its progress bar for real right-clicks. These slots bind the block entity's
            // inventory headlessly; clicks travel the same block-entity packet envelope
            // the quern's own dialog uses, so the server treats them identically, and
            // contents stay fresh through ordinary block-entity sync as it grinds.
            if (quernInv != null && quernPos != null)
            {
                double qx = contentX + iconMargin + flowW - quernW + 8;
                double qy = TitleH;
                var qpos = quernPos.Copy();

                var block = capi.World.BlockAccessor.GetBlock(qpos);
                if (block != null && block.Id != 0)
                {
                    var slot = new DummySlot(new ItemStack(block));
                    iconSlots.Add(slot);   // keep alive for the composer's lifetime
                    composer.AddPassiveItemSlot(
                        ElementBounds.Fixed(qx, qy + (LayoutMetrics.Cell - IconTile) / 2, IconTile, IconTile),
                        quernInv as InventoryBase, slot, false, "quern-icon");
                }

                Action<object> send = p => capi.Network.SendBlockEntityPacket(qpos.X, qpos.Y, qpos.Z, p);

                var inB = ElementStdBounds.SlotGrid(EnumDialogArea.None, qx + IconTile + 6, qy, 1, 1);
                composer.AddItemSlotGrid(quernInv, send, 1, new[] { 0 }, inB, "grid-quernin");

                // Output as its OWN grid so it can be click-gated (no depositing) and
                // marked with the down-arrow.
                double outX = qx + IconTile + 6 + LayoutMetrics.Cell;
                var outB = ElementStdBounds.SlotGrid(EnumDialogArea.None, outX, qy, 1, 1);
                composer.AddItemSlotGrid(quernInv, send, 1, new[] { 1 }, outB, "grid-quernout");
                quernOutBounds = outB;

                // The arrow must be an element ADDED AFTER the grid: it then paints above
                // the opaque slot-box texture but below the item sprites (which render in
                // the later PostRender stage) - visible while the slot is empty, covered
                // by the flour once there is output. A static draw under the slot texture
                // never shows at all - the baked-plate gotcha, hit once already.
                composer.AddDynamicCustomDraw(
                    ElementBounds.Fixed(outX, qy, LayoutMetrics.Cell, LayoutMetrics.Cell),
                    (ctx, surface, b) => DrawQuernArrow(ctx), "quernarrow");
            }
            else
            {
                quernOutBounds = null;
            }

            // ---- scrolling flow -------------------------------------------------
            var viewport = ElementBounds.Fixed(contentX + iconMargin, scrollStart, flowW, Math.Max(viewportH, 1));
            flowViewport = viewport;

            // Ribbon chrome is one dynamic custom draw (own element-sized surface, LOCAL
            // coordinates); grids inside BeginClip are children of the clip and use
            // viewport-relative coordinates. Both facts verified against engine IL and
            // learned the hard way - see CLAUDE.md gotchas.
            composer.AddDynamicCustomDraw(viewport, (ctx, surface, bounds) => DrawRibbons(ctx, bounds), ChromeKey);

            composer.BeginClip(viewport);
            foreach (var ribbon in plan.Ribbons)
            {
                var s = ribbon.Section;
                foreach (var slice in ribbon.Slices)
                {
                    double relY = slice.Row * LayoutMetrics.Cell;
                    var b = ElementStdBounds.SlotGrid(EnumDialogArea.None,
                        slice.Col * LayoutMetrics.Cell, relY - scrollY, slice.Cols, slice.Rows);

                    var ids = new int[slice.Count];
                    Array.Copy(s.SlotIds, slice.SlotOffset, ids, 0, slice.Count);

                    composer.AddItemSlotGrid(s.Inventory, s.SendPacket, slice.Cols, ids, b,
                        "grid-" + s.Id + "-" + slice.SlotOffset);
                    scrollables.Add((b, relY));
                    if (s.OnCellClick != null) synthGrids.Add((b, slice, s));
                }
            }
            composer.EndClip();

            if (scrolls)
            {
                var sb = ElementBounds.Fixed(contentX + iconMargin + flowW + 4, scrollStart, 16, viewportH);
                composer.AddVerticalScrollbar(OnNewScrollbarValue, sb, ScrollKey);
            }

            AddFooter(composer, contentX, TitleH + stripH + Math.Max(viewportH, 60) + Pad / 2);

            composer.EndChildElements();
            SingleComposer = composer.Compose();

            // The engine remembers dragged dialog positions by composer key and restores
            // them on every compose. An offset stored while the window was SMALL (solo bag
            // block) shoves a since-grown window - a chain-opened chest wall is nearly
            // screen-wide - half off the screen. The window belongs in view, always: if the
            // restored position leaves any edge off-screen, discard it and recenter. A
            // deliberate drag of a window that still fits is left alone.
            {
                var db = SingleComposer.Bounds;
                if (db.absX < 0 || db.absY < 0
                    || db.absX + db.OuterWidth > capi.Render.FrameWidth + 1
                    || db.absY + db.OuterHeight > capi.Render.FrameHeight + 1)
                {
                    db.fixedOffsetX = 0;
                    db.fixedOffsetY = 0;
                    db.CalcWorldBounds();
                }
            }

            if (scrolls)
            {
                SingleComposer.GetScrollbar(ScrollKey)?.SetHeights((float)viewportH, (float)contentH);
            }

            WatchInventories();
        }

        /// <summary>
        /// "#N Container" label above the cursor while hovering a flow cell.
        ///
        /// Drawn in OnFinalizeFrame, NOT OnRenderGUI: slot item icons render later, during
        /// GuiComposer.PostRender which the base OnFinalizeFrame drives (confirmed in the
        /// engine call stack - PostRenderInteractiveElements sits under OnFinalizeFrame). A
        /// tooltip drawn in OnRenderGUI is painted first and the item sprites land on top of
        /// it - exactly the "falls behind the items" bug. Drawing after base.OnFinalizeFrame
        /// puts it above them. Above the cursor, too, so it never fights the item tooltip.
        /// </summary>
        private bool hoverRenderFailed;
        private bool iconRenderFailed;

        /// <summary>Render nothing while suppressed - skips the composer entirely.</summary>
        public override void OnRenderGUI(float deltaTime)
        {
            if (Suppressed) return;
            base.OnRenderGUI(deltaTime);
        }

        public override void OnFinalizeFrame(float deltaTime)
        {
            if (Suppressed) return;
            base.OnFinalizeFrame(deltaTime);
            if (!IsOpened()) return;

            // Row icons and the hover label have SEPARATE kill-switches: one bad icon draw
            // used to disable the whole layer forever (real bug - a backpack icon threw on
            // its first frame and took the sack markers and labels with it).
            if (!iconRenderFailed)
            {
                try
                {
                    DrawRowIcons(deltaTime);
                }
                catch (Exception e)
                {
                    iconRenderFailed = true;
                    capi.Logger.Warning("[SymbioticInventories] Row icons disabled after render error: {0}", e);
                }
            }

            if (hoverRenderFailed) return;

            // The whole body is guarded: this is unverifiable render code (I cannot test the
            // GL path headlessly), and a throw in OnFinalizeFrame takes the ENTIRE client down
            // - which it already did once. A hover label is never worth a crash, so on any
            // failure we log once and permanently disable the label, never the game.
            try
            {
                var hovered = capi.World?.Player?.InventoryManager?.CurrentHoveredSlot;
                InventorySection s = null;
                if (hovered != null) slotToSection.TryGetValue(hovered, out s);

                // Hover link, cells -> tile: the hovered cell's container lights up in the
                // vessel row (and clears the moment nothing relevant is hovered).
                SetTileGlow(s?.Id);
                if (s == null) return;

                string key = "#" + s.Number + " " + s.Label;
                if (hoverTex == null || hoverTexKey != key)
                {
                    hoverTex?.Dispose();
                    hoverTex = capi.Gui.TextTexture.GenTextTexture(key, CairoFont.WhiteSmallText(),
                        new TextBackground
                        {
                            FillColor = GuiStyle.DialogStrongBgColor,
                            Padding = 5,
                            Radius = 3,
                            Shade = true
                        });
                    hoverTexKey = key;
                }

                float x = capi.Input.MouseX + 12;
                float y = capi.Input.MouseY - hoverTex.Height - 10;
                if (y < 0) y = capi.Input.MouseY + 24;

                // The GUI shader is NOT bound here: base already ran composer.PostRender, which
                // binds the gui shader, draws the item sprites, then stops it - so rendering
                // raw threw "Can't set uniform on not active shader gui" and crashed the
                // client. Bind it ourselves around the draw; the GUI ortho projection base set
                // up is still current, so position stays correct.
                var guiShader = capi.Render.GetEngineShader(EnumShaderProgram.Gui);
                guiShader.Use();
                try
                {
                    capi.Render.Render2DTexturePremultipliedAlpha(hoverTex.TextureId, x, y, hoverTex.Width, hoverTex.Height);
                }
                finally
                {
                    guiShader.Stop();
                }
            }
            catch (Exception e)
            {
                hoverRenderFailed = true;
                capi.Logger.Warning("[SymbioticInventories] Hover label disabled after render error: {0}", e);
            }
        }

        /// <summary>
        /// Row markers drawn with the engine's own renderers: each worn bag's itemstack in
        /// the margin left of its line, and a live 3D portrait of the mount in the blank
        /// row above its saddlebag brick (bag-icon fallback if the entity is gone). Runs
        /// inside the guarded finalize-frame block - unverifiable GL, never worth a crash.
        /// Rows scrolled out of the viewport draw nothing (the flow clips its grids; these
        /// are raw draws, so they must clip themselves).
        /// </summary>
        private void DrawRowIcons(float deltaTime)
        {
            if (flowViewport == null || plan == null) return;

            double g = RuntimeEnv.GUIScale;
            double cell = LayoutMetrics.Cell;
            double vx = flowViewport.renderX, vy = flowViewport.renderY;

            bool RowVisible(double relY) =>
                relY - scrollY >= -0.2 * cell && relY - scrollY + cell <= viewportH + 0.2 * cell;

            // Item/entity rendering here expects the environment vanilla gives it: the GUI
            // shader active (element PostRender runs inside it). After base.OnFinalizeFrame
            // no shader is bound, and the very first icon draw threw and dark-switched the
            // whole layer (real bug: no bag icons, no sack markers, no portrait). Bind it
            // for the duration, exactly like the hover label does.
            var guiShader = capi.Render.GetEngineShader(EnumShaderProgram.Gui);
            guiShader.Use();
            try
            {
                DrawRowIconsCore(deltaTime, g, cell, vx, vy, RowVisible);
            }
            finally
            {
                guiShader.Stop();
            }
        }

        private void DrawRowIconsCore(float deltaTime, double g, double cell, double vx, double vy, System.Func<double, bool> RowVisible)
        {
            foreach (var ribbon in plan.Ribbons)
            {
                if (ribbon.Slices.Count == 0) continue;
                var s = ribbon.Section;
                var first = ribbon.Slices[0];

                if (s.Kind == SectionKind.Backpack && iconMargin > 0 && s.Icon != null)
                {
                    double relY = first.Row * cell;
                    if (!RowVisible(relY)) continue;
                    iconDrawSlot.Itemstack = s.Icon;
                    capi.Render.RenderItemstackToGui(iconDrawSlot,
                        vx - iconMargin * g * 0.5,
                        vy + (relY - scrollY + cell * 0.5) * g,
                        90, (float)(cell * 0.55 * g), ColorUtil.WhiteArgb, true, false, false);
                }
                else if (s.Kind == SectionKind.Mount && first.Row > 0)
                {
                    // The blank row above the brick - it exists whenever bags are worn
                    // (first.Row > 0); with no bags there is no gap and no portrait.
                    double relY = (first.Row - 1) * cell;
                    if (!RowVisible(relY)) continue;
                    double cx = vx + first.Cols * 0.5 * cell * g;
                    double bottomY = vy + (relY - scrollY + cell * 0.92) * g;

                    if (s.PortraitEntity != null && s.PortraitEntity.Alive)
                    {
                        capi.Render.RenderEntityToGui(deltaTime, s.PortraitEntity,
                            cx, bottomY, 90, -0.4f, (float)(cell * 0.7 * g), ColorUtil.WhiteArgb);
                    }
                    else if (s.Icon != null)
                    {
                        iconDrawSlot.Itemstack = s.Icon;
                        capi.Render.RenderItemstackToGui(iconDrawSlot,
                            cx, vy + (relY - scrollY + cell * 0.5) * g,
                            90, (float)(cell * 0.55 * g), ColorUtil.WhiteArgb, true, false, false);
                    }
                }
                else if (s.OnCellClick != null && s.Icon != null)
                {
                    // Shelf/sack marker IN THE GRID: the container block's own picture in
                    // the blank gutter cell RIGHT of its brick (the layout reserves one
                    // blank column between bricks - "it would fit great", user), so a sack
                    // cell reads like the sack you'd be looking at in person: sack picture
                    // beside the flour type and count.
                    double relY = first.Row * cell;
                    if (!RowVisible(relY)) continue;
                    int markerCol = first.Col + first.Cols;
                    if (markerCol >= plan.Cols) continue;   // brick flush with the edge
                    iconDrawSlot.Itemstack = s.Icon;
                    capi.Render.RenderItemstackToGui(iconDrawSlot,
                        vx + (markerCol * cell + cell * 0.5) * g,
                        vy + (relY - scrollY + cell * 0.5) * g,
                        90, (float)(cell * 0.62 * g), ColorUtil.WhiteArgb, true, false, false);
                }
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            hoverTex?.Dispose();
            hoverTex = null;
        }

        private void OnNewScrollbarValue(float value)
        {
            scrollY = value;
            foreach (var (bounds, relY) in scrollables)
            {
                bounds.fixedY = relY - scrollY;
                bounds.CalcWorldBounds();
            }
            (SingleComposer?.GetElement(ChromeKey) as GuiElementCustomDraw)?.Redraw();
        }

        private string TitleText()
        {
            if (mode != LayoutMode.DockLeft) return Lang.Get("symbioticinventories:window-title");
            var state = Lang.Get(dockFocused ? "symbioticinventories:dock-focused" : "symbioticinventories:dock-locked");
            return Lang.Get("symbioticinventories:window-title") + " - " + state;
        }

        private void AddFooter(GuiComposer composer, double x, double y)
        {
            var btn = ElementBounds.Fixed(x, y + 2, 130, 24);
            composer.AddSmallButton(
                Lang.Get(mode == LayoutMode.DockLeft ? "symbioticinventories:btn-float" : "symbioticinventories:btn-dock"),
                OnToggleMode, btn, EnumButtonStyle.Normal);

            var optBtn = ElementBounds.Fixed(x + 138, y + 2, 110, 24);
            composer.AddSmallButton(Lang.Get("symbioticinventories:btn-options"),
                () => { OpenOptions?.Invoke(); return true; }, optBtn, EnumButtonStyle.Normal);

            // "Open cellar" appears only while the player stands in a cellar that still has
            // unopened containers - a contextual action, no clutter otherwise.
            int cellarCount = Capture?.FindCellarContainers()?.Count ?? 0;
            if (cellarCount > 0)
            {
                var cellarBtn = ElementBounds.Fixed(x + 256, y + 2, 150, 24);
                composer.AddSmallButton(Lang.Get("symbioticinventories:btn-cellar", cellarCount),
                    OnOpenCellar, cellarBtn, EnumButtonStyle.Normal);
            }
        }

        /// <summary>Opens every container in the cellar the player is standing in.</summary>
        private bool OnOpenCellar()
        {
            Capture?.OpenCellarContainers();
            // The captures fire their own recompose via OnCapturesChanged; nothing more to do.
            return true;
        }

        private bool OnToggleMode()
        {
            mode = mode == LayoutMode.Auto ? LayoutMode.DockLeft : LayoutMode.Auto;
            dockFocused = mode == LayoutMode.DockLeft;
            scrollY = 0;
            Compose();
            return true;
        }

        // ---- chrome -------------------------------------------------------------

        /// <summary>Badges over the vessel tiles. Static draw: dialog-surface coordinates.
        /// Hidden sections' tiles render dimmed with a slash - clickable to bring back.</summary>
        /// <summary>The output-only down-arrow, drawn on its own element surface (LOCAL
        /// coordinates, one cell in size) layered above the slot box.</summary>
        private static void DrawQuernArrow(Context ctx)
        {
            double g = RuntimeEnv.GUIScale;
            double cx = LayoutMetrics.Cell * g / 2, cy = LayoutMetrics.Cell * g / 2;
            double s = 7 * g;   // arrow half-width

            ctx.SetSourceRGBA(0.92, 0.90, 0.85, 0.6);
            // stem
            ctx.Rectangle(cx - s * 0.35, cy - s * 1.4, s * 0.7, s * 1.2);
            ctx.Fill();
            // head
            ctx.MoveTo(cx - s, cy - s * 0.3);
            ctx.LineTo(cx + s, cy - s * 0.3);
            ctx.LineTo(cx, cy + s);
            ctx.ClosePath();
            ctx.Fill();
        }

        private void DrawStripChrome(Context ctx, ElementBounds bounds)
        {
            double g = RuntimeEnv.GUIScale;

            // Group chips: neutral tab showing the member count; slashed when the whole
            // group is toggled off. Sits flush against its group's first tile.
            foreach (var (x, y, w, _, _, members) in groupChips)
            {
                bool allHidden = members.TrueForAll(m => hiddenIds.Contains(m.Id));
                double cx = bounds.drawX + (x - contentX) * g;
                double cy = bounds.drawY + y * g;
                double cw = w * g, chh = IconTile * g;

                ctx.SetSourceRGBA(0.30, 0.28, 0.24, allHidden ? 0.45 : 0.85);
                GuiElement.RoundRectangle(ctx, cx, cy, cw, chh, 3 * g);
                ctx.Fill();
                ctx.SetSourceRGBA(0.62, 0.60, 0.55, allHidden ? 0.4 : 0.8);
                ctx.LineWidth = 1 * g;
                GuiElement.RoundRectangle(ctx, cx, cy, cw, chh, 3 * g);
                ctx.Stroke();

                ctx.SetSourceRGBA(0.92, 0.90, 0.85, allHidden ? 0.5 : 0.95);
                ctx.SelectFontFace(GuiStyle.StandardFontName, FontSlant.Normal, FontWeight.Bold);
                ctx.SetFontSize(12 * g);
                string t = "×" + members.Count;
                var ext = ctx.TextExtents(t);
                ctx.MoveTo(cx + cw / 2 - ext.Width / 2 - ext.XBearing, cy + chh / 2 + ext.Height / 2);
                ctx.ShowText(t);

                if (allHidden)
                {
                    ctx.SetSourceRGBA(0.85, 0.30, 0.25, 0.85);
                    ctx.LineWidth = 2 * g;
                    ctx.MoveTo(cx + 3 * g, cy + chh - 3 * g);
                    ctx.LineTo(cx + cw - 3 * g, cy + 3 * g);
                    ctx.Stroke();
                }
            }

            foreach (var (x, y, _, s) in iconTiles)
            {
                bool hidden = hiddenIds.Contains(s.Id);
                double tx = bounds.drawX + (x - contentX) * g;
                double ty = bounds.drawY + y * g;
                double t = IconTile * g;

                double alpha = hidden ? 0.25 : 0.55;
                ctx.SetSourceRGBA(s.Accent[0], s.Accent[1], s.Accent[2], alpha);
                ctx.LineWidth = 1.5 * g;
                GuiElement.RoundRectangle(ctx, tx, ty, t, t, 2 * g);
                ctx.Stroke();

                if (hidden)
                {
                    // Darken the tile and slash it: unmistakably "off", still legible.
                    ctx.SetSourceRGBA(0.05, 0.04, 0.03, 0.55);
                    GuiElement.RoundRectangle(ctx, tx, ty, t, t, 2 * g);
                    ctx.Fill();

                    ctx.SetSourceRGBA(0.85, 0.30, 0.25, 0.85);
                    ctx.LineWidth = 2 * g;
                    ctx.MoveTo(tx + 3 * g, ty + t - 3 * g);
                    ctx.LineTo(tx + t - 3 * g, ty + 3 * g);
                    ctx.Stroke();
                }

                DrawBadge(ctx, tx - 3 * g, ty - 3 * g, 14 * g, s.Number, s.Accent);
            }
        }

        /// <summary>
        /// Ribbon fills and outlines over the flow. Dynamic draw: LOCAL surface coordinates,
        /// origin at the viewport's top-left, shifted by scroll.
        /// </summary>
        private void DrawRibbons(Context ctx, ElementBounds bounds)
        {
            double g = RuntimeEnv.GUIScale;
            double cell = LayoutMetrics.Cell * g;
            double oy = -scrollY * g;

            foreach (var ribbon in plan.Ribbons)
            {
                var s = ribbon.Section;
                var a = s.Accent;
                if (ribbon.Slices.Count == 0) continue;

                // Per-slice rectangles. The backward-L puts ribbons in sub-regions (beside
                // the bag block, then below it), so the old full-width text-selection
                // polygon no longer describes a ribbon's outline; each slice is an exact
                // rectangle by construction, and a shared internal edge just reads as a
                // seam in the same colour.
                void Trace()
                {
                    foreach (var sl in ribbon.Slices)
                        ctx.Rectangle(sl.Col * cell, oy + sl.Row * cell, sl.Cols * cell, sl.Rows * cell);
                }

                // Strong enough that every cell visibly carries its container's colour (the
                // user's ask), still light enough that item sprites stay readable on top.
                // Hovering the section's vessel tile lights the whole ribbon: deeper wash,
                // outer halo, hard bright rim.
                bool glow = s.Id == glowRibbonId;

                ctx.SetSourceRGBA(a[0], a[1], a[2], glow ? 0.45 : 0.20);
                Trace();
                ctx.Fill();

                if (glow)
                {
                    ctx.SetSourceRGBA(a[0], a[1], a[2], 0.35);
                    ctx.LineWidth = 6 * g;
                    Trace();
                    ctx.Stroke();
                }

                ctx.SetSourceRGBA(a[0], a[1], a[2], glow ? 1.0 : 0.65);
                ctx.LineWidth = (glow ? 2.5 : 1.5) * g;
                Trace();
                ctx.Stroke();

                var f = ribbon.Slices[0];
                DrawBadge(ctx, f.Col * cell + 2 * g, oy + f.Row * cell + 2 * g, 14 * g, s.Number, a);
            }
        }

        /// <summary>
        /// The glow layer for the vessel row: the tile of the section under the cursor -
        /// reached through its cells (glowTileId) or hovered directly (glowRibbonId) -
        /// lights up in its accent colour. LOCAL coordinates (dynamic draw, own surface):
        /// the element starts at (contentX, 0) in dialog units, so subtract contentX.
        /// </summary>
        private void DrawStripGlow(Context ctx)
        {
            string id = glowRibbonId ?? glowTileId;
            if (id == null) return;

            double g = RuntimeEnv.GUIScale;
            foreach (var (ix, iy, _, s) in iconTiles)
            {
                if (s.Id != id) continue;
                var a = s.Accent;
                double x = (ix - contentX) * g, y = iy * g, t = IconTile * g;

                // Soft halo around the tile, a wash across it, and a hard rim: reads as
                // "lit" over any icon art without hiding it.
                ctx.SetSourceRGBA(a[0], a[1], a[2], 0.30);
                ctx.LineWidth = 6 * g;
                GuiElement.RoundRectangle(ctx, x - 3 * g, y - 3 * g, t + 6 * g, t + 6 * g, 3 * g);
                ctx.Stroke();

                ctx.SetSourceRGBA(a[0], a[1], a[2], 0.28);
                GuiElement.RoundRectangle(ctx, x, y, t, t, 2 * g);
                ctx.Fill();

                ctx.SetSourceRGBA(a[0], a[1], a[2], 1.0);
                ctx.LineWidth = 2 * g;
                GuiElement.RoundRectangle(ctx, x, y, t, t, 2 * g);
                ctx.Stroke();
                break;
            }
        }

        private static void DrawBadge(Context ctx, double x, double y, double size, int number, double[] accent)
        {
            ctx.SetSourceRGBA(accent[0], accent[1], accent[2], 0.96);
            GuiElement.RoundRectangle(ctx, x, y, size, size, size * 0.18);
            ctx.Fill();

            ctx.SetSourceRGBA(0.06, 0.05, 0.04, 1);
            ctx.SelectFontFace(GuiStyle.StandardFontName, FontSlant.Normal, FontWeight.Bold);
            ctx.SetFontSize(size * 0.72);
            string t = number.ToString();
            var ext = ctx.TextExtents(t);
            ctx.MoveTo(x + size / 2 - ext.Width / 2 - ext.XBearing, y + size / 2 + ext.Height / 2);
            ctx.ShowText(t);
        }


        private static string Truncate(Context ctx, string text, double maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0) return "";
            if (ctx.TextExtents(text).Width <= maxWidth) return text;
            for (int i = text.Length - 1; i > 1; i--)
            {
                var candidate = text.Substring(0, i) + "...";
                if (ctx.TextExtents(candidate).Width <= maxWidth) return candidate;
            }
            return "";
        }

        // ---- inventory shape watching -------------------------------------------
        //
        // Slot grids are composed against a snapshot of slot ids, and the backpack
        // inventory's slot count changes at runtime (picking a worn bag removes its content
        // slots). A stale grid recomposing during render dies on a vanished slot - found by
        // a real crash. Watch SlotModified and recompose the moment the shape drifts.

        private void WatchInventories()
        {
            UnwatchInventories();

            var seen = new HashSet<IInventory>();
            foreach (var s in sections)
            {
                var inv = s.Inventory;
                if (inv == null || !seen.Add(inv)) continue;

                int countAtCompose = inv.Count;
                Action<int> handler = slotId =>
                {
                    if (inv.Count != countAtCompose || slotId >= countAtCompose) Refresh();
                };
                inv.SlotModified += handler;
                watched.Add((inv, handler));
            }
        }

        private void UnwatchInventories()
        {
            foreach (var (inv, handler) in watched) inv.SlotModified -= handler;
            watched.Clear();
        }
    }
}
