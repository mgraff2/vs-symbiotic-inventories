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

        // Layout constants, all in unscaled GUI units. Generous so nothing wraps or collides.
        private const double W = 540;          // content width
        private const double SwitchSize = 30;  // switch square
        private const double LabelX = 42;      // text left edge, right of a switch
        private const double RowGap = 6;        // between a row and the next
        private const double GroupGap = 22;     // between the two feature groups
        private const double SliderLabelW = 240;
        private const double SliderH = 24;

        private void Compose()
        {
            var bgBounds = ElementBounds.Fixed(0, 0, W, 100)
                .WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            // A running Y cursor lays every row out in sequence, so rows physically cannot
            // overlap the way the old hand-tuned Fixed coordinates did.
            double y = 0;

            var adjSwitch = ElementBounds.Fixed(0, y, SwitchSize, SwitchSize);
            var adjLabel = ElementBounds.Fixed(LabelX, y + 4, W - LabelX, 26);
            y += SwitchSize + RowGap;

            var adjHint = ElementBounds.Fixed(LabelX, y, W - LabelX, 50);
            y += 50 + RowGap;

            var radLabel = ElementBounds.Fixed(LabelX, y + 2, SliderLabelW, 26);
            var radSlider = ElementBounds.Fixed(LabelX + SliderLabelW, y, W - LabelX - SliderLabelW, SliderH);
            y += SliderH + GroupGap;

            var mountSwitch = ElementBounds.Fixed(0, y, SwitchSize, SwitchSize);
            var mountLabel = ElementBounds.Fixed(LabelX, y + 4, W - LabelX, 26);
            y += SwitchSize + RowGap;

            var entSwitchless = ElementBounds.Fixed(LabelX, y, W - LabelX, 40);   // hint for the mount row
            y += 40 + RowGap;

            var entLabel = ElementBounds.Fixed(LabelX, y + 2, SliderLabelW, 26);
            var entSlider = ElementBounds.Fixed(LabelX + SliderLabelW, y, W - LabelX - SliderLabelW, SliderH);

            SingleComposer = capi.Gui
                .CreateCompo("symbioticinventories:options", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(Lang.Get("symbioticinventories:options-title"), () => TryClose())
                .BeginChildElements(bgBounds)
                    .AddSwitch(OnToggleAdjacent, adjSwitch, "adjacentSwitch")
                    .AddStaticText(Lang.Get("symbioticinventories:opt-adjacent"),
                        CairoFont.WhiteSmallishText(), adjLabel)
                    .AddStaticText(Lang.Get("symbioticinventories:opt-adjacent-hint"),
                        CairoFont.WhiteDetailText(), adjHint)
                    .AddStaticText(Lang.Get("symbioticinventories:opt-adjacent-radius"),
                        CairoFont.WhiteSmallishText(), radLabel)
                    .AddSlider(OnRadiusChanged, radSlider, "radiusSlider")
                    .AddSwitch(OnToggleMount, mountSwitch, "mountSwitch")
                    .AddStaticText(Lang.Get("symbioticinventories:opt-mount"),
                        CairoFont.WhiteSmallishText(), mountLabel)
                    .AddStaticText(Lang.Get("symbioticinventories:opt-mount-hint"),
                        CairoFont.WhiteDetailText(), entSwitchless)
                    .AddStaticText(Lang.Get("symbioticinventories:opt-entity-radius"),
                        CairoFont.WhiteSmallishText(), entLabel)
                    .AddSlider(OnEntityRadiusChanged, entSlider, "entityRadSlider")
                .EndChildElements()
                .Compose();

            SingleComposer.GetSwitch("adjacentSwitch").On = config.OpenAdjacentChests;
            SingleComposer.GetSlider("radiusSlider").SetValues(
                Math.Clamp(config.AdjacentOpenRadius, 1, 3), 1, 3, 1, " " + Lang.Get("symbioticinventories:blocks-unit"));
            SingleComposer.GetSwitch("mountSwitch").On = config.ShowMountInventory;
            SingleComposer.GetSlider("entityRadSlider").SetValues(
                Math.Clamp(config.NearbyEntityRadius, 0, 10), 0, 10, 1, " " + Lang.Get("symbioticinventories:blocks-unit"));
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
