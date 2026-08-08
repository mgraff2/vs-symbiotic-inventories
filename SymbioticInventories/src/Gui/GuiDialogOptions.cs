using System;
using SymbioticInventories.Core;
using Vintagestory.API.Client;
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
            Compose();
        }

        // Compact layout, unscaled GUI units. Small font + hover tooltips instead of inline
        // hint paragraphs - the paragraphs made the window a tall wall of text.
        private const double W = 430;          // content width
        private const double SwitchSize = 22;  // switch square
        private const double LabelX = 30;      // text left edge, right of a switch
        private const double Row = 26;          // switch-row pitch
        private const double SmallGap = 3;
        private const double GroupGap = 12;
        private const double SliderLabelW = 200;
        private const double SliderH = 20;
        private const double TipW = 320;        // tooltip wrap width

        private static CairoFont Label() => CairoFont.WhiteSmallText().WithFontSize(15);
        private static CairoFont Header() => CairoFont.WhiteSmallText().WithFontSize(15).WithWeight(Cairo.FontWeight.Bold);

        private void Compose()
        {
            var bgBounds = ElementBounds.Fixed(0, 0, W, 100)
                .WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            double y = 0;

            var adjSwitch = ElementBounds.Fixed(0, y, SwitchSize, SwitchSize);
            var adjLabel = ElementBounds.Fixed(LabelX, y + 3, W - LabelX, 22);
            var adjRow = ElementBounds.Fixed(0, y, W, Row);
            y += Row + SmallGap;

            var radLabel = ElementBounds.Fixed(LabelX, y + 1, SliderLabelW, 22);
            var radSlider = ElementBounds.Fixed(LabelX + SliderLabelW, y, W - LabelX - SliderLabelW, SliderH);
            y += SliderH + GroupGap;

            var sortHeader = ElementBounds.Fixed(0, y + 1, W, 22);
            var sortHeaderRow = ElementBounds.Fixed(0, y, W, 22);
            y += 24;

            double col2 = W / 2;
            var toolsSwitch = ElementBounds.Fixed(0, y, SwitchSize, SwitchSize);
            var toolsLabel = ElementBounds.Fixed(LabelX, y + 3, col2 - LabelX, 22);
            var foodSwitch = ElementBounds.Fixed(col2, y, SwitchSize, SwitchSize);
            var foodLabel = ElementBounds.Fixed(col2 + LabelX, y + 3, W - col2 - LabelX, 22);
            y += Row + SmallGap;

            var seedsSwitch = ElementBounds.Fixed(0, y, SwitchSize, SwitchSize);
            var seedsLabel = ElementBounds.Fixed(LabelX, y + 3, col2 - LabelX, 22);
            var oreSwitch = ElementBounds.Fixed(col2, y, SwitchSize, SwitchSize);
            var oreLabel = ElementBounds.Fixed(col2 + LabelX, y + 3, W - col2 - LabelX, 22);
            y += Row + SmallGap;

            var spoilLabel = ElementBounds.Fixed(LabelX, y + 1, SliderLabelW, 22);
            var spoilSlider = ElementBounds.Fixed(LabelX + SliderLabelW, y, W - LabelX - SliderLabelW, SliderH);
            var spoilRow = ElementBounds.Fixed(0, y, W, SliderH);
            y += SliderH + SmallGap;

            var freshSwitch = ElementBounds.Fixed(0, y, SwitchSize, SwitchSize);
            var freshLabel = ElementBounds.Fixed(LabelX, y + 3, W - LabelX, 22);
            var freshRow = ElementBounds.Fixed(0, y, W, Row);
            y += Row + GroupGap;

            var mountSwitch = ElementBounds.Fixed(0, y, SwitchSize, SwitchSize);
            var mountLabel = ElementBounds.Fixed(LabelX, y + 3, W - LabelX, 22);
            var mountRow = ElementBounds.Fixed(0, y, W, Row);
            y += Row + SmallGap;

            var entLabel = ElementBounds.Fixed(LabelX, y + 1, SliderLabelW, 22);
            var entSlider = ElementBounds.Fixed(LabelX + SliderLabelW, y, W - LabelX - SliderLabelW, SliderH);

            SingleComposer = capi.Gui
                .CreateCompo("symbioticinventories:options", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(Lang.Get("symbioticinventories:options-title"), () => TryClose())
                .BeginChildElements(bgBounds)
                    .AddSwitch(OnToggleAdjacent, adjSwitch, "adjacentSwitch")
                    .AddStaticText(Lang.Get("symbioticinventories:opt-adjacent"), Label(), adjLabel)
                    .AddHoverText(Lang.Get("symbioticinventories:opt-adjacent-hint"), Label(), (int)TipW, adjRow)
                    .AddStaticText(Lang.Get("symbioticinventories:opt-adjacent-radius"), Label(), radLabel)
                    .AddSlider(OnRadiusChanged, radSlider, "radiusSlider")

                    .AddStaticText(Lang.Get("symbioticinventories:opt-sort-header"), Header(), sortHeader)
                    .AddHoverText(Lang.Get("symbioticinventories:opt-sort-hint"), Label(), (int)TipW, sortHeaderRow)
                    .AddSwitch(v => { config.SortPrioritizeTools = v; onChanged?.Invoke(); }, toolsSwitch, "swTools")
                    .AddStaticText(Lang.Get("symbioticinventories:cat-tools"), Label(), toolsLabel)
                    .AddSwitch(v => { config.SortPrioritizeFood = v; onChanged?.Invoke(); }, foodSwitch, "swFood")
                    .AddStaticText(Lang.Get("symbioticinventories:cat-food"), Label(), foodLabel)
                    .AddSwitch(v => { config.SortPrioritizeSeeds = v; onChanged?.Invoke(); }, seedsSwitch, "swSeeds")
                    .AddStaticText(Lang.Get("symbioticinventories:cat-seeds"), Label(), seedsLabel)
                    .AddSwitch(v => { config.SortPrioritizeOre = v; onChanged?.Invoke(); }, oreSwitch, "swOre")
                    .AddStaticText(Lang.Get("symbioticinventories:cat-ore"), Label(), oreLabel)
                    .AddStaticText(Lang.Get("symbioticinventories:opt-food-spoil"), Label(), spoilLabel)
                    .AddSlider(OnSpoilDaysChanged, spoilSlider, "spoilSlider")
                    .AddHoverText(Lang.Get("symbioticinventories:opt-food-spoil-hint"), Label(), (int)TipW, spoilRow)
                    .AddSwitch(v => { config.SortFoodByFreshness = v; onChanged?.Invoke(); }, freshSwitch, "swFresh")
                    .AddStaticText(Lang.Get("symbioticinventories:opt-food-freshness"), Label(), freshLabel)
                    .AddHoverText(Lang.Get("symbioticinventories:opt-food-freshness-hint"), Label(), (int)TipW, freshRow)

                    .AddSwitch(OnToggleMount, mountSwitch, "mountSwitch")
                    .AddStaticText(Lang.Get("symbioticinventories:opt-mount"), Label(), mountLabel)
                    .AddHoverText(Lang.Get("symbioticinventories:opt-mount-hint"), Label(), (int)TipW, mountRow)
                    .AddStaticText(Lang.Get("symbioticinventories:opt-entity-radius"), Label(), entLabel)
                    .AddSlider(OnEntityRadiusChanged, entSlider, "entityRadSlider")
                .EndChildElements()
                .Compose();

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
