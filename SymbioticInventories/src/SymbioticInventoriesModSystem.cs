using HarmonyLib;
using SymbioticInventories.Core;
using SymbioticInventories.Gui;
using SymbioticInventories.Integration;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace SymbioticInventories
{
    public class SymbioticInventoriesModSystem : ModSystem
    {
        public const string HarmonyId = "com.markc.symbioticinventories";

        private Harmony harmony;
        private ICoreClientAPI capi;
        private DialogCaptureService capture;
        private GuiDialogMasterInventory window;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            harmony = new Harmony(HarmonyId);
            capture = new DialogCaptureService();
            capture.Start(api, harmony, Mod.Logger);

            var registry = new SectionRegistry(api, capture);
            window = new GuiDialogMasterInventory(api, registry);

            api.Input.RegisterHotKey(
                GuiDialogMasterInventory.HotkeyCode,
                Lang.Get("symbioticinventories:hotkey"),
                GlKeys.B,
                HotkeyType.GUIOrOtherControls);

            api.Input.SetHotKeyHandler(GuiDialogMasterInventory.HotkeyCode, _ => { window.Toggle(); return true; });

            // Docked mode: the window stays locked at the edge while the mouse stays with the
            // game; this key hands the cursor to the window and takes it back.
            api.Input.RegisterHotKey(
                GuiDialogMasterInventory.FocusHotkeyCode,
                Lang.Get("symbioticinventories:hotkey-focus"),
                GlKeys.N,
                HotkeyType.GUIOrOtherControls);

            api.Input.SetHotKeyHandler(GuiDialogMasterInventory.FocusHotkeyCode, _ => window.ToggleDockFocus());
            api.Gui.RegisterDialog(window);

            // Opening a chest has to bring the master window up, otherwise capturing it
            // would just make the container disappear with nowhere to show its contents.
            capture.OnCapturesChanged += OnCapturesChanged;

            Mod.Logger.Notification("[SymbioticInventories] Ready.");
        }

        private void OnCapturesChanged()
        {
            if (window == null) return;

            bool anyOpen = false;
            foreach (var _ in capture.Captured) { anyOpen = true; break; }

            if (anyOpen && !window.IsOpened()) window.TryOpen();
            else window.Refresh();
        }

        public override void Dispose()
        {
            if (capture != null)
            {
                capture.OnCapturesChanged -= OnCapturesChanged;
                capture.Stop();
            }
            harmony?.UnpatchAll(HarmonyId);
            base.Dispose();
        }
    }
}
