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

        private void Compose()
        {
            var bgBounds = ElementBounds.Fixed(0, 0, 420, 210)
                .WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            var switchBounds = ElementBounds.Fixed(0, 10, 40, 24);
            var labelBounds = ElementBounds.Fixed(52, 13, 368, 24);
            var hintBounds = ElementBounds.Fixed(52, 38, 368, 44);
            var radiusLabelBounds = ElementBounds.Fixed(52, 92, 200, 24);
            var radiusSliderBounds = ElementBounds.Fixed(260, 92, 160, 22);
            var mountSwitchBounds = ElementBounds.Fixed(0, 128, 40, 24);
            var mountLabelBounds = ElementBounds.Fixed(52, 131, 368, 24);
            var entityRadLabelBounds = ElementBounds.Fixed(52, 168, 200, 24);
            var entityRadSliderBounds = ElementBounds.Fixed(260, 168, 160, 22);

            SingleComposer = capi.Gui
                .CreateCompo("symbioticinventories:options", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(Lang.Get("symbioticinventories:options-title"), () => TryClose())
                .BeginChildElements(bgBounds)
                    .AddSwitch(OnToggleAdjacent, switchBounds, "adjacentSwitch")
                    .AddStaticText(Lang.Get("symbioticinventories:opt-adjacent"),
                        CairoFont.WhiteSmallishText(), labelBounds)
                    .AddStaticText(Lang.Get("symbioticinventories:opt-adjacent-hint"),
                        CairoFont.WhiteDetailText(), hintBounds)
                    .AddStaticText(Lang.Get("symbioticinventories:opt-adjacent-radius"),
                        CairoFont.WhiteSmallishText(), radiusLabelBounds)
                    .AddSlider(OnRadiusChanged, radiusSliderBounds, "radiusSlider")
                    .AddSwitch(OnToggleMount, mountSwitchBounds, "mountSwitch")
                    .AddStaticText(Lang.Get("symbioticinventories:opt-mount"),
                        CairoFont.WhiteSmallishText(), mountLabelBounds)
                    .AddStaticText(Lang.Get("symbioticinventories:opt-entity-radius"),
                        CairoFont.WhiteSmallishText(), entityRadLabelBounds)
                    .AddSlider(OnEntityRadiusChanged, entityRadSliderBounds, "entityRadSlider")
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
