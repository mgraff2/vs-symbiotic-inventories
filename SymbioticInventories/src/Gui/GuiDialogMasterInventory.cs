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
    /// The single window. Sections are measured and placed by <see cref="SectionPacker"/>;
    /// this class only turns a finished <see cref="LayoutPlan"/> into GUI elements.
    ///
    /// Keeping placement out of here is what makes the two modes cheap: the centered window
    /// and the left dock are different budgets handed to the same packer, not two parallel
    /// implementations of the same window.
    ///
    /// Docked behaviour: the window stays on screen like a HUD element - the mouse stays
    /// grabbed and gameplay continues around it. Pressing the focus hotkey flips it into a
    /// normal interactive dialog (cursor freed, clicks land); pressing it again, or closing,
    /// returns it to passive HUD mode. That flip is what "locked in place" means mechanically:
    /// the window never moves, only the player's ability to reach into it changes.
    /// </summary>
    public class GuiDialogMasterInventory : GuiDialog
    {
        public const string HotkeyCode = "symbioticinventory";
        public const string FocusHotkeyCode = "symbioticinventoryfocus";

        private const double RailW = 200;
        private const double Pad = 10;
        private const double FooterH = 30;

        private readonly SectionRegistry registry;

        private List<InventorySection> sections = new();
        private LayoutPlan plan = new();

        private LayoutMode mode = LayoutMode.Auto;

        /// <summary>Docked only: whether the player currently has the cursor in the window.</summary>
        private bool dockFocused;

        private const string ChromeKey = "chrome";
        private const string ScrollKey = "scrollbar";

        /// <summary>
        /// Grid bounds paired with their unscrolled Y, so scrolling can move them without a
        /// recompose. Recomposing inside the scrollbar's own callback would destroy the
        /// element being dragged and drop the drag on the first pixel of movement.
        /// </summary>
        private readonly List<(ElementBounds bounds, double baseY)> scrollables = new();

        private double scrollY;
        private double viewportH;
        private double contentH;

        /// <summary>Plan-space Y at which the pinned region ends and scrolling begins.</summary>
        private double scrollStart;

        public GuiDialogMasterInventory(ICoreClientAPI capi, SectionRegistry registry) : base(capi)
        {
            this.registry = registry;
        }

        public override string ToggleKeyCombinationCode => HotkeyCode;

        /// <summary>
        /// Centered mode behaves like the vanilla inventory screen: cursor free. Docked mode
        /// only frees the cursor while focused - otherwise the game keeps the mouse and the
        /// window is just a live display at the edge of the screen.
        /// </summary>
        public override bool PrefersUngrabbedMouse => mode != LayoutMode.DockLeft || dockFocused;

        /// <summary>An unfocused dock is a HUD: drawn, but not part of the dialog stack.</summary>
        public override EnumDialogType DialogType
            => mode == LayoutMode.DockLeft && !dockFocused ? EnumDialogType.HUD : EnumDialogType.Dialog;

        /// <summary>An unfocused dock must never swallow clicks meant for the world.</summary>
        public override bool ShouldReceiveMouseEvents()
            => (mode != LayoutMode.DockLeft || dockFocused) && base.ShouldReceiveMouseEvents();

        /// <summary>Toggles cursor access to the docked window. Bound to the focus hotkey.</summary>
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
            // Opening always grants the cursor, whatever triggered it: the player either asked
            // for the window or just opened a container, and both mean "let me reach in now".
            dockFocused = true;
            Compose();
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            dockFocused = false;
        }

        /// <summary>Called when a container docks or undocks while the window is already up.</summary>
        public void Refresh()
        {
            if (!IsOpened()) return;
            Compose();
        }

        // ---- budget -------------------------------------------------------------

        /// <summary>
        /// How much room the layout may use, in unscaled GUI units.
        ///
        /// Measuring the real framebuffer rather than assuming a size is the whole reason the
        /// window stopped running off the screen: at a high GUI scale the usable area in
        /// unscaled units shrinks, and a layout that fits at 1x will not fit at 1.5x.
        /// </summary>
        private LayoutBudget BuildBudget()
        {
            double scale = Math.Max(0.1, RuntimeEnv.GUIScale);
            double screenW = capi.Render.FrameWidth / scale;
            double screenH = capi.Render.FrameHeight / scale;

            var budget = new LayoutBudget { Mode = mode };

            // Chrome that is not available to sections: title bar, footer, padding, and the
            // legend rail where one is shown.
            double chromeH = 40 + FooterH + Pad * 3;

            if (mode == LayoutMode.DockLeft)
            {
                // Locked to the edge: a narrow column that runs the full height. The packers
                // wrap early against the narrow width and fill downward, which is exactly the
                // docked behaviour, so no special-casing is needed here.
                budget.MaxWidth = Math.Min(screenW * 0.28, 8 * LayoutMetrics.Cell);
                budget.MaxHeight = screenH - chromeH;
            }
            else
            {
                budget.MaxWidth = Math.Max(4 * LayoutMetrics.Cell, screenW * 0.82 - RailW - Pad * 3);
                budget.MaxHeight = screenH * 0.86 - chromeH;
            }

            return budget;
        }

        private bool ShowRail => mode != LayoutMode.DockLeft;

        // ---- composition --------------------------------------------------------

        private void Compose()
        {
            sections = registry.Build();
            var budget = BuildBudget();
            plan = SectionPacker.Pack(sections, budget);

            scrollables.Clear();

            // Where the scrolling region begins. Everything above it is pinned and always
            // visible - that is the whole point of the essentials band.
            scrollStart = plan.Height;
            foreach (var band in plan.Bands)
            {
                if (!band.Pinned && band.Boxes.Count > 0) scrollStart = Math.Min(scrollStart, band.Y);
            }
            if (scrollStart >= plan.Height) scrollStart = plan.Height;

            contentH = Math.Max(plan.Height - scrollStart, 0);
            viewportH = Math.Min(contentH, Math.Max(budget.MaxHeight - scrollStart, 2 * LayoutMetrics.Cell));

            bool scrolls = contentH > viewportH + 0.5;
            double maxScroll = Math.Max(0, contentH - viewportH);
            scrollY = Math.Clamp(scrollY, 0, maxScroll);

            double railW = ShowRail ? RailW : 0;
            double contentX = railW + (ShowRail ? Pad : 0);
            double contentW = Math.Max(plan.Width, 4 * LayoutMetrics.Cell);
            double scrollbarW = scrolls ? 20 : 0;

            double bodyW = contentX + contentW + scrollbarW;
            double bodyH = Math.Max(scrollStart + viewportH, 120) + FooterH + Pad;

            var bgBounds = ElementBounds.Fixed(0, 0, bodyW, bodyH)
                .WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(mode == LayoutMode.DockLeft
                    ? EnumDialogArea.LeftMiddle
                    : EnumDialogArea.CenterMiddle);

            var composer = capi.Gui
                .CreateCompo("symbioticinventories:master", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(TitleText(), OnTitleBarClose)
                .BeginChildElements(bgBounds);

            if (ShowRail)
            {
                composer.AddStaticCustomDraw(
                    ElementBounds.Fixed(0, 0, RailW, bodyH - FooterH),
                    (ctx, surface, bounds) => DrawLegend(ctx, bounds));
            }

            // Pinned region: drawn at its true position, never offset, never clipped.
            if (scrollStart > 0)
            {
                var pinnedBounds = ElementBounds.Fixed(contentX, 0, contentW, scrollStart);
                composer.AddStaticCustomDraw(pinnedBounds, (ctx, surface, bounds) => DrawChrome(ctx, bounds, true));

                foreach (var box in PinnedBoxes())
                {
                    var s = box.Section;
                    var b = ElementStdBounds.SlotGrid(EnumDialogArea.None, contentX + box.X, box.GridY, box.Cols, box.Rows);
                    composer.AddItemSlotGrid(s.Inventory, s.SendPacket, box.Cols, s.SlotIds, b, "grid-" + s.Id);
                }
            }

            var viewport = ElementBounds.Fixed(contentX, scrollStart, contentW, Math.Max(viewportH, 1));

            // Scrolling chrome is one *dynamic* custom draw, not one static draw per section.
            // Dynamic is what makes scrolling possible at all: a static element bakes into the
            // background surface at compose time, so a scrolled plate would stay put while its
            // slot grid slid away from it. Redraw() on scroll keeps them locked together, and
            // one element keeps the count flat as containers open.
            composer.AddDynamicCustomDraw(viewport, (ctx, surface, bounds) => DrawChrome(ctx, bounds, false), ChromeKey);

            composer.BeginClip(viewport);
            foreach (var box in ScrollingBoxes())
            {
                var s = box.Section;
                double baseY = box.GridY;

                var gridBounds = ElementStdBounds.SlotGrid(
                    EnumDialogArea.None, contentX + box.X, baseY - scrollY, box.Cols, box.Rows);

                composer.AddItemSlotGrid(s.Inventory, s.SendPacket, box.Cols, s.SlotIds, gridBounds, "grid-" + s.Id);
                scrollables.Add((gridBounds, baseY));
            }
            composer.EndClip();

            if (scrolls)
            {
                var sbBounds = ElementBounds.Fixed(contentX + contentW + 4, 0, 16, viewportH);
                composer.AddVerticalScrollbar(OnNewScrollbarValue, sbBounds, ScrollKey);
            }

            AddFooter(composer, contentX, bodyH - FooterH, bodyW - contentX);

            composer.EndChildElements();
            SingleComposer = composer.Compose();

            if (scrolls)
            {
                SingleComposer.GetScrollbar(ScrollKey)?.SetHeights((float)viewportH, (float)contentH);
            }
        }

        /// <summary>
        /// Moves the scrolled content without recomposing: grid bounds are nudged and the
        /// chrome element is asked to redraw itself at the new offset.
        /// </summary>
        private void OnNewScrollbarValue(float value)
        {
            scrollY = value;

            foreach (var (bounds, baseY) in scrollables)
            {
                bounds.fixedY = baseY - scrollY;
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

        private void AddFooter(GuiComposer composer, double x, double y, double width)
        {
            var btn = ElementBounds.Fixed(x, y + 2, 130, 24);
            composer.AddSmallButton(
                Lang.Get(mode == LayoutMode.DockLeft ? "symbioticinventories:btn-float" : "symbioticinventories:btn-dock"),
                OnToggleMode, btn, EnumButtonStyle.Normal);
        }

        private bool OnToggleMode()
        {
            mode = mode == LayoutMode.Auto ? LayoutMode.DockLeft : LayoutMode.Auto;
            // Entering the dock focused: the player just clicked the button, so their cursor
            // is already in the window - snatching the mouse away mid-gesture would be rude.
            dockFocused = mode == LayoutMode.DockLeft;
            scrollY = 0;   // a preserved offset means nothing in a differently shaped layout
            Compose();
            return true;
        }

        // ---- chrome drawing -----------------------------------------------------

        private IEnumerable<LayoutBox> PinnedBoxes()
        {
            foreach (var band in plan.Bands)
                if (band.Pinned)
                    foreach (var box in band.Boxes) yield return box;
        }

        private IEnumerable<LayoutBox> ScrollingBoxes()
        {
            foreach (var band in plan.Bands)
                if (!band.Pinned)
                    foreach (var box in band.Boxes) yield return box;
        }

        private void DrawChrome(Context ctx, ElementBounds bounds, bool pinned)
        {
            // Both regions draw boxes at their plan-space Y, so the origin is whatever makes
            // that land correctly. Pinned starts at the top of the content area; scrolling is
            // shifted back by where the viewport begins and by how far it has been scrolled.
            // The surface is only as big as its region, so Cairo clips the overspill for us.
            double ox = bounds.drawX;
            double oy = pinned ? bounds.drawY : bounds.drawY - scrollStart - scrollY;

            foreach (var band in plan.Bands)
            {
                if (band.Pinned != pinned) continue;

                if (plan.ShowBandCaptions && band.Boxes.Count > 0)
                {
                    ctx.SetSourceRGBA(0.82, 0.79, 0.72, 0.75);
                    ctx.SelectFontFace(GuiStyle.StandardFontName, FontSlant.Normal, FontWeight.Bold);
                    ctx.SetFontSize(12);
                    ctx.MoveTo(ox, oy + band.Y + 13);
                    ctx.ShowText((band.Title ?? "").ToUpperInvariant());
                }

                foreach (var box in band.Boxes) DrawSectionPlate(ctx, ox, oy, box);
            }
        }

        /// <summary>
        /// A section's backing plate, accent spine and number badge.
        ///
        /// The tint is deliberately weak - it has to survive behind item sprites without
        /// making them hard to read, so the badge and the spine carry the identification and
        /// colour is only the fast secondary cue.
        /// </summary>
        private void DrawSectionPlate(Context ctx, double ox, double oy, LayoutBox box)
        {
            var s = box.Section;
            var a = s.Accent;

            double x = ox + box.X;
            double y = oy + box.Y;
            double w = box.W;
            double h = box.H;

            ctx.SetSourceRGBA(a[0], a[1], a[2], 0.15);
            GuiElement.RoundRectangle(ctx, x, y, w, h, 3);
            ctx.Fill();

            ctx.SetSourceRGBA(a[0], a[1], a[2], 0.85);
            GuiElement.RoundRectangle(ctx, x, y, 3, h, 1);
            ctx.Fill();

            double textX = x + 8;
            if (s.Numbered)
            {
                DrawBadge(ctx, x + 8, y + 3, 17, s.Number, a);
                textX = x + 8 + 17 + 7;
            }

            string caption = s.SubLabel == null ? s.Label : s.Label + "  -  " + s.SubLabel;
            ctx.SetSourceRGBA(0.94, 0.91, 0.86, 0.94);
            ctx.SelectFontFace(GuiStyle.StandardFontName, FontSlant.Normal, FontWeight.Normal);
            ctx.SetFontSize(12.5);
            ctx.MoveTo(textX, y + 16);
            ctx.ShowText(Truncate(ctx, caption, w - (textX - x) - 6));
        }

        private static void DrawBadge(Context ctx, double x, double y, double size, int number, double[] accent)
        {
            ctx.SetSourceRGBA(accent[0], accent[1], accent[2], 0.96);
            GuiElement.RoundRectangle(ctx, x, y, size, size, 3);
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
            double x = bounds.drawX + 4;
            double y = bounds.drawY + 6;

            ctx.SetSourceRGBA(0.94, 0.91, 0.86, 0.96);
            ctx.SelectFontFace(GuiStyle.StandardFontName, FontSlant.Normal, FontWeight.Bold);
            ctx.SetFontSize(13);
            ctx.MoveTo(x, y + 12);
            ctx.ShowText(Lang.Get("symbioticinventories:legend-title"));
            y += 24;

            foreach (var s in sections)
            {
                if (!s.Numbered) continue;

                DrawBadge(ctx, x, y, 14, s.Number, s.Accent);

                ctx.SetSourceRGBA(0.90, 0.88, 0.83, 0.92);
                ctx.SelectFontFace(GuiStyle.StandardFontName, FontSlant.Normal, FontWeight.Normal);
                ctx.SetFontSize(12);
                ctx.MoveTo(x + 21, y + 11);
                ctx.ShowText(Truncate(ctx, s.Label, RailW - 34));

                y += 19;
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

        private void OnTitleBarClose() => TryClose();
    }
}
