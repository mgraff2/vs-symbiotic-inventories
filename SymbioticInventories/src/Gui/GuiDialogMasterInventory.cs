using System;
using System.Collections.Generic;
using Cairo;
using SymbioticInventories.Core;
using SymbioticInventories.Core.Layout;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

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

        private const double RailW = 200;
        private const double Pad = 10;
        private const double FooterH = 30;

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

            // Counterpart of the open: lets the server drop grid contents back to the player
            // the same way closing the vanilla crafting dialog does.
            var im = capi.World.Player.InventoryManager;
            var craftInv = im.GetOwnInventory(GlobalConstants.craftingInvClassName);
            if (craftInv != null) im.CloseInventoryAndSync(craftInv);

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

        /// <summary>Paints every visible ribbon's slot faces in its section colour.</summary>
        private void PaintRibbonSlots()
        {
            RestoreSlotPaint();

            foreach (var s in flowSections)
            {
                string hex = PastelHex(s.Accent);
                foreach (var id in s.SlotIds)
                {
                    var slot = s.Inventory[id];
                    if (slot == null) continue;
                    if (!paintedSlots.ContainsKey(slot)) paintedSlots[slot] = slot.HexBackgroundColor;
                    slot.HexBackgroundColor = hex;
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
            if (IsOpened() && !args.Handled)
            {
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

            double scale = Math.Max(0.1, RuntimeEnv.GUIScale);
            double screenW = capi.Render.FrameWidth / scale;
            double screenH = capi.Render.FrameHeight / scale;

            bool docked = mode == LayoutMode.DockLeft;
            bool showRail = !docked;

            double railW = showRail ? RailW + Pad : 0;
            double chromeH = 40 + FooterH + Pad * 3;
            double availW = docked
                ? Math.Min(screenW * 0.28, 10 * LayoutMetrics.Cell)
                : Math.Max(8 * LayoutMetrics.Cell, screenW * 0.92 - railW - Pad * 2);
            double availH = screenH * 0.92 - chromeH;

            // ---- top strip: crafting + worn bags + vessel row -------------------
            var crafting = sections.Find(s => s.Kind == SectionKind.Crafting);
            var bagSlots = sections.Find(s => s.Kind == SectionKind.BackpackSlots);

            int totalSlots = 0;
            foreach (var s in flowSections) totalSlots += s.SlotCount;

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

            // Vessel row: group chips + tiles, wrapping within the width beside crafting and
            // bags. ALL numbered sections get a tile - hidden ones render dimmed. Each group
            // (chests / vessels / backpacks / trunks) is prefixed by a narrow chip that
            // toggles the whole group at once.
            double iconAreaX = craftingW + bagsW;
            double iconAreaW = Math.Max(IconTile + ChipW, availW - iconAreaX);

            var stripItems = new List<(bool isChip, InventorySection s, List<InventorySection> members, double w)>();
            {
                string lastGroup = null;
                foreach (var s in numberedSections)
                {
                    if (s.GroupKey != lastGroup)
                    {
                        lastGroup = s.GroupKey;
                        var members = numberedSections.FindAll(m => m.GroupKey == s.GroupKey);
                        if (members.Count > 1) stripItems.Add((true, s, members, ChipW + 4));
                    }
                    stripItems.Add((false, s, null, IconTile + 4));
                }
            }

            // Wrap the variable-width sequence into rows.
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

            // ---- the flow -------------------------------------------------------
            int cols = docked
                ? Math.Max(4, (int)(availW / LayoutMetrics.Cell))
                : UnifiedGrid.ChooseCols(totalSlots, availW, availH - stripH);
            plan = UnifiedGrid.Compute(flowSections, cols);

            double flowW = plan.Cols * LayoutMetrics.Cell;
            contentH = plan.Rows * LayoutMetrics.Cell;
            scrollStart = stripH;
            viewportH = Math.Min(contentH, Math.Max(availH - stripH, 2 * LayoutMetrics.Cell));

            bool scrolls = contentH > viewportH + 0.5;
            scrollY = Math.Clamp(scrollY, 0, Math.Max(0, contentH - viewportH));

            contentX = railW;
            double bodyW = contentX + Math.Max(flowW, iconAreaX + 4 * LayoutMetrics.Cell) + (scrolls ? 20 : 0);
            double bodyH = stripH + Math.Max(viewportH, 60) + FooterH + Pad;

            var bgBounds = ElementBounds.Fixed(0, 0, bodyW, bodyH)
                .WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(docked ? EnumDialogArea.LeftMiddle : EnumDialogArea.CenterMiddle);

            var composer = capi.Gui
                .CreateCompo("symbioticinventories:master", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(TitleText(), () => TryClose())
                .BeginChildElements(bgBounds);

            if (showRail)
            {
                composer.AddStaticCustomDraw(
                    ElementBounds.Fixed(0, 0, RailW, bodyH - FooterH),
                    (ctx, surface, bounds) => DrawLegend(ctx, bounds));
            }

            // Top strip elements (dialog space, above the scroll viewport).
            if (crafting != null)
            {
                var b = ElementStdBounds.SlotGrid(EnumDialogArea.None, contentX, 0, crafting.FixedColumns,
                    (int)Math.Ceiling(crafting.SlotCount / (double)crafting.FixedColumns));
                composer.AddItemSlotGrid(crafting.Inventory, crafting.SendPacket, crafting.FixedColumns, crafting.SlotIds, b, "grid-craft");

                // Output slot: the crafting inventory's last slot, centered right of the grid.
                int outId = crafting.Inventory.Count - 1;
                var ob = ElementStdBounds.SlotGrid(EnumDialogArea.None,
                    contentX + crafting.FixedColumns * LayoutMetrics.Cell + 8,
                    (craftingH - LayoutMetrics.Cell) / 2, 1, 1);
                composer.AddItemSlotGrid(crafting.Inventory, crafting.SendPacket, 1, new[] { outId }, ob, "grid-craftout");
            }
            if (bagSlots != null)
            {
                var b = ElementStdBounds.SlotGrid(EnumDialogArea.None, contentX + craftingW, 0, bagSlots.SlotCount, 1);
                composer.AddItemSlotGrid(bagSlots.Inventory, bagSlots.SendPacket, bagSlots.SlotCount, bagSlots.SlotIds, b, "grid-bags");
            }

            for (int i = 0; i < stripItems.Count; i++)
            {
                var item = stripItems[i];
                double ix = contentX + iconAreaX + stripPos[i].x;
                double iy = stripPos[i].y;

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

            // Strip chrome (badges over vessel tiles) bakes into the dialog background.
            composer.AddStaticCustomDraw(
                ElementBounds.Fixed(contentX, 0, bodyW - contentX, stripH),
                (ctx, surface, bounds) => DrawStripChrome(ctx, bounds));

            // ---- scrolling flow -------------------------------------------------
            var viewport = ElementBounds.Fixed(contentX, scrollStart, flowW, Math.Max(viewportH, 1));

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
                }
            }
            composer.EndClip();

            if (scrolls)
            {
                var sb = ElementBounds.Fixed(contentX + flowW + 4, scrollStart, 16, viewportH);
                composer.AddVerticalScrollbar(OnNewScrollbarValue, sb, ScrollKey);
            }

            AddFooter(composer, contentX, stripH + Math.Max(viewportH, 60) + Pad / 2);

            composer.EndChildElements();
            SingleComposer = composer.Compose();

            if (scrolls)
            {
                SingleComposer.GetScrollbar(ScrollKey)?.SetHeights((float)viewportH, (float)contentH);
            }

            WatchInventories();
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

            var sortBtn = ElementBounds.Fixed(x + 256, y + 2, 110, 24);
            composer.AddSmallButton(Lang.Get("symbioticinventories:btn-sort"), OnSort, sortBtn, EnumButtonStyle.Normal);
        }

        /// <summary>Sorts the visible containers' contents globally, then recomposes.</summary>
        private bool OnSort()
        {
            int moves = InventorySorter.Sort(capi, flowSections);
            capi.Logger.Notification("[SymbioticInventories] Sort: {0} slot operations.", moves);
            Refresh();
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

                int W = plan.Cols;
                int start = ribbon.StartCell, end = ribbon.EndCell - 1;
                int r0 = start / W, c0 = start % W;
                int r1 = end / W, c1 = end % W + 1;

                // Fill + outline. Contiguous multi-row ribbons trace the text-selection
                // polygon; the two-partial-rows-no-overlap case falls back to per-slice
                // rectangles (the polygon would self-intersect).
                bool polygon = r1 > r0 && (r1 - r0 >= 2 || c1 > c0);

                void Trace()
                {
                    if (r0 == r1)
                    {
                        ctx.Rectangle(c0 * cell, oy + r0 * cell, (c1 - c0) * cell, cell);
                    }
                    else if (polygon)
                    {
                        ctx.MoveTo(c0 * cell, oy + r0 * cell);
                        ctx.LineTo(W * cell, oy + r0 * cell);
                        ctx.LineTo(W * cell, oy + r1 * cell);
                        ctx.LineTo(c1 * cell, oy + r1 * cell);
                        ctx.LineTo(c1 * cell, oy + (r1 + 1) * cell);
                        ctx.LineTo(0, oy + (r1 + 1) * cell);
                        ctx.LineTo(0, oy + (r0 + 1) * cell);
                        ctx.LineTo(c0 * cell, oy + (r0 + 1) * cell);
                        ctx.ClosePath();
                    }
                    else
                    {
                        ctx.Rectangle(c0 * cell, oy + r0 * cell, (W - c0) * cell, cell);
                        ctx.Rectangle(0, oy + r1 * cell, c1 * cell, cell);
                    }
                }

                // Strong enough that every cell visibly carries its container's colour (the
                // user's ask), still light enough that item sprites stay readable on top.
                ctx.SetSourceRGBA(a[0], a[1], a[2], 0.20);
                Trace();
                ctx.Fill();

                ctx.SetSourceRGBA(a[0], a[1], a[2], 0.65);
                ctx.LineWidth = 1.5 * g;
                Trace();
                ctx.Stroke();

                DrawBadge(ctx, c0 * cell + 2 * g, oy + r0 * cell + 2 * g, 14 * g, s.Number, a);
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

        private void DrawLegend(Context ctx, ElementBounds bounds)
        {
            double g = RuntimeEnv.GUIScale;
            double x = bounds.drawX + 4 * g;
            double y = bounds.drawY + 6 * g;

            ctx.SetSourceRGBA(0.94, 0.91, 0.86, 0.96);
            ctx.SelectFontFace(GuiStyle.StandardFontName, FontSlant.Normal, FontWeight.Bold);
            ctx.SetFontSize(13 * g);
            ctx.MoveTo(x, y + 12 * g);
            ctx.ShowText(Lang.Get("symbioticinventories:legend-title"));
            y += 24 * g;

            foreach (var s in numberedSections)
            {
                bool hidden = hiddenIds.Contains(s.Id);
                DrawBadge(ctx, x, y, 14 * g, s.Number, s.Accent);

                string caption = s.SubLabel == null ? s.Label : s.Label + " - " + s.SubLabel;
                if (hidden) caption += " " + Lang.Get("symbioticinventories:hidden-suffix");
                ctx.SetSourceRGBA(0.90, 0.88, 0.83, hidden ? 0.45 : 0.92);
                ctx.SelectFontFace(GuiStyle.StandardFontName, FontSlant.Normal, FontWeight.Normal);
                ctx.SetFontSize(12 * g);
                ctx.MoveTo(x + 21 * g, y + 11 * g);
                ctx.ShowText(Truncate(ctx, caption, (RailW - 34) * g));

                y += 19 * g;
            }
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
