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
            var bgBounds = ElementBounds.Fixed(0, 0, 420, 120)
                .WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            var switchBounds = ElementBounds.Fixed(0, 10, 40, 24);
            var labelBounds = ElementBounds.Fixed(52, 13, 368, 24);
            var hintBounds = ElementBounds.Fixed(52, 38, 368, 44);

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
                .EndChildElements()
                .Compose();

            SingleComposer.GetSwitch("adjacentSwitch").On = config.OpenAdjacentChests;
        }

        private void OnToggleAdjacent(bool on)
        {
            config.OpenAdjacentChests = on;
            onChanged?.Invoke();
        }
    }
}
