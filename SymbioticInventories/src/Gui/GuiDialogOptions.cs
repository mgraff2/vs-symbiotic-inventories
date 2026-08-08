using System;
using SymbioticInventories.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SymbioticInventories.Gui
{
    /// <summary>
    /// Feature toggles. Deliberately dumb: one switch per ModConfig flag, saved on every
    /// change, no apply/cancel state to get out of sync.
    /// </summary>
    public class GuiDialogOptions : GuiDialog
    {
        private readonly ModConfig config;
        private readonly Action onChanged;

        /// <summary>The master window to hide while these options are open. Wired by the mod system.</summary>
        public GuiDialogMasterInventory MainWindow;

        public GuiDialogOptions(ICoreClientAPI capi, ModConfig config, Action onChanged) : base(capi)
        {
            this.config = config;
            this.onChanged = onChanged;
        }

        public override string ToggleKeyCombinationCode => null;

        public override bool PrefersUngrabbedMouse => true;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            // Hide the master window while options are up (its item sprites would otherwise
            // render on top of this panel - a later render stage, unavoidable by ordering).
            if (MainWindow != null) MainWindow.Suppressed = true;
            Compose();
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            if (MainWindow != null) MainWindow.Suppressed = false;
        }

        // Clean single-column layout, unscaled GUI units. Every row is switch-then-label or
        // label-then-slider, all left-aligned to the same grid - the 2x2 switch block and
        // mixed slider widths were what read as "a mess". Small font, hover tooltips carry
        // the detail so no inline hint paragraphs.
        private const double W = 380;          // content width
        private const double SwitchSize = 20;
        private const double LabelX = 28;      // text left edge, right of a switch
        private const double Row = 25;          // switch-row pitch
        private const double SmallGap = 2;
        private const double GroupGap = 14;
        private const double SliderX = 180;     // sliders all start here, same width
        private const double SliderH = 20;
        private const int TipW = 300;

        private static CairoFont Label() => CairoFont.WhiteSmallText().WithFontSize(14);
        private static CairoFont Header() => CairoFont.WhiteSmallText().WithFontSize(14).WithWeight(Cairo.FontWeight.Bold);

        // Cursor state threaded through the row helpers.
        private GuiComposer c;
        private double yc;

        /// <summary>A switch + label row, with an optional hover tooltip over the whole row.</summary>
        private void SwitchRow(string key, string labelKey, Action<bool> onToggle, string tipKey = null)
        {
            c.AddSwitch(onToggle, ElementBounds.Fixed(0, yc, SwitchSize, SwitchSize), key, SwitchSize);
            c.AddStaticText(Lang.Get(labelKey), Label(), ElementBounds.Fixed(LabelX, yc + 2, W - LabelX, 20));
            if (tipKey != null) c.AddHoverText(Lang.Get(tipKey), Label(), TipW, ElementBounds.Fixed(0, yc, W, Row));
            yc += Row + SmallGap;
        }

        /// <summary>A label + right-aligned slider row (sliders all share SliderX and width).</summary>
        private void SliderRow(string key, string labelKey, ActionConsumable<int> onChange, string tipKey = null)
        {
            c.AddStaticText(Lang.Get(labelKey), Label(), ElementBounds.Fixed(0, yc + 1, SliderX - 4, 20));
            c.AddSlider(onChange, ElementBounds.Fixed(SliderX, yc, W - SliderX, SliderH), key);
            if (tipKey != null) c.AddHoverText(Lang.Get(tipKey), Label(), TipW, ElementBounds.Fixed(0, yc, W, SliderH));
            yc += SliderH + GroupGap;
        }

        private void Compose()
        {
            var bgBounds = ElementBounds.Fixed(0, 0, W, 100)
                .WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

            c = capi.Gui.CreateCompo("symbioticinventories:options", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(Lang.Get("symbioticinventories:options-title"), () => TryClose())
                .BeginChildElements(bgBounds);

            // Start below the title bar - content at y=0 runs into the title (as it did).
            yc = GuiStyle.TitleBarHeight;
            SwitchRow("invKeySwitch", "symbioticinventories:opt-invkey",
                v => { config.OpenOnInventoryKey = v; onChanged?.Invoke(); }, "symbioticinventories:opt-invkey-hint");
            SwitchRow("adjacentSwitch", "symbioticinventories:opt-adjacent", OnToggleAdjacent, "symbioticinventories:opt-adjacent-hint");
            SliderRow("radiusSlider", "symbioticinventories:opt-adjacent-radius", OnRadiusChanged);

            c.AddStaticText(Lang.Get("symbioticinventories:opt-sort-header"), Header(), ElementBounds.Fixed(0, yc + 1, W, 20));
            c.AddHoverText(Lang.Get("symbioticinventories:opt-sort-hint"), Label(), TipW, ElementBounds.Fixed(0, yc, W, 20));
            yc += 24;

            SwitchRow("swTools", "symbioticinventories:cat-tools", v => { config.SortPrioritizeTools = v; onChanged?.Invoke(); });
            SwitchRow("swFood", "symbioticinventories:cat-food", v => { config.SortPrioritizeFood = v; onChanged?.Invoke(); });
            SwitchRow("swSeeds", "symbioticinventories:cat-seeds", v => { config.SortPrioritizeSeeds = v; onChanged?.Invoke(); });
            SwitchRow("swOre", "symbioticinventories:cat-ore", v => { config.SortPrioritizeOre = v; onChanged?.Invoke(); });
            SliderRow("spoilSlider", "symbioticinventories:opt-food-spoil", OnSpoilDaysChanged, "symbioticinventories:opt-food-spoil-hint");
            SwitchRow("swFresh", "symbioticinventories:opt-food-freshness", v => { config.SortFoodByFreshness = v; onChanged?.Invoke(); }, "symbioticinventories:opt-food-freshness-hint");

            yc += GroupGap;
            SwitchRow("mountSwitch", "symbioticinventories:opt-mount", OnToggleMount, "symbioticinventories:opt-mount-hint");
            SliderRow("entityRadSlider", "symbioticinventories:opt-entity-radius", OnEntityRadiusChanged);

            SingleComposer = c.EndChildElements().Compose();

            SingleComposer.GetSwitch("invKeySwitch").On = config.OpenOnInventoryKey;
            SingleComposer.GetSwitch("adjacentSwitch").On = config.OpenAdjacentChests;
            SingleComposer.GetSlider("radiusSlider").SetValues(
                Math.Clamp(config.AdjacentOpenRadius, 1, 3), 1, 3, 1, " " + Lang.Get("symbioticinventories:blocks-unit"));
            SingleComposer.GetSwitch("swTools").On = config.SortPrioritizeTools;
            SingleComposer.GetSwitch("swFood").On = config.SortPrioritizeFood;
            SingleComposer.GetSwitch("swSeeds").On = config.SortPrioritizeSeeds;
            SingleComposer.GetSwitch("swOre").On = config.SortPrioritizeOre;
            SingleComposer.GetSlider("spoilSlider").SetValues(
                Math.Clamp(config.SortFoodMaxSpoilDays, 0, 30), 0, 30, 1, " " + Lang.Get("symbioticinventories:days-unit"));
            SingleComposer.GetSwitch("swFresh").On = config.SortFoodByFreshness;
            SingleComposer.GetSwitch("mountSwitch").On = config.ShowMountInventory;
            SingleComposer.GetSlider("entityRadSlider").SetValues(
                Math.Clamp(config.NearbyEntityRadius, 0, 10), 0, 10, 1, " " + Lang.Get("symbioticinventories:blocks-unit"));
        }

        private bool OnSpoilDaysChanged(int value)
        {
            config.SortFoodMaxSpoilDays = Math.Clamp(value, 0, 30);
            onChanged?.Invoke();
            return true;
        }

        private void OnToggleMount(bool on)
        {
            config.ShowMountInventory = on;
            onChanged?.Invoke();
        }

        private bool OnEntityRadiusChanged(int value)
        {
            config.NearbyEntityRadius = Math.Clamp(value, 0, 10);
            onChanged?.Invoke();
            return true;
        }

        private void OnToggleAdjacent(bool on)
        {
            config.OpenAdjacentChests = on;
            onChanged?.Invoke();
        }

        private bool OnRadiusChanged(int value)
        {
            config.AdjacentOpenRadius = Math.Clamp(value, 1, 3);
            onChanged?.Invoke();
            return true;
        }
    }
}
