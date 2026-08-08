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
        private EntityContainerService entityContainers;
        private GuiDialogMasterInventory window;
        private GuiDialogOptions options;
        private ModConfig config;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            // A corrupt config file must not stop the mod from loading - fall back to
            // defaults and let the next save overwrite the broken file.
            try { config = api.LoadModConfig<ModConfig>(ModConfig.Filename); }
            catch (System.Exception e) { Mod.Logger.Warning("[SymbioticInventories] Bad config, using defaults: {0}", e.Message); }
            config ??= new ModConfig();
            api.StoreModConfig(config, ModConfig.Filename);

            harmony = new Harmony(HarmonyId);
            capture = new DialogCaptureService();
            capture.Start(api, harmony, Mod.Logger, config);

            entityContainers = new EntityContainerService();
            entityContainers.Start(api, config, capture, Mod.Logger);

            var registry = new SectionRegistry(api, capture, config);
            window = new GuiDialogMasterInventory(api, registry) { Config = config, Capture = capture };
            options = new GuiDialogOptions(api, config, () => api.StoreModConfig(config, ModConfig.Filename)) { MainWindow = window };
            window.OpenOptions = () => options.TryOpen();
            api.Gui.RegisterDialog(options);

            api.Input.RegisterHotKey(
                GuiDialogMasterInventory.HotkeyCode,
                Lang.Get("symbioticinventories:hotkey"),
                GlKeys.B,
                HotkeyType.GUIOrOtherControls);

            api.Input.SetHotKeyHandler(GuiDialogMasterInventory.HotkeyCode, _ =>
            {
                window.Toggle();
                if (window.IsOpened()) entityContainers.Discover();
                return true;
            });

            // Docked mode: the window stays locked at the edge while the mouse stays with the
            // game; this key hands the cursor to the window and takes it back.
            api.Input.RegisterHotKey(
                GuiDialogMasterInventory.FocusHotkeyCode,
                Lang.Get("symbioticinventories:hotkey-focus"),
                GlKeys.N,
                HotkeyType.GUIOrOtherControls);

            api.Input.SetHotKeyHandler(GuiDialogMasterInventory.FocusHotkeyCode, _ => window.ToggleDockFocus());
            api.Gui.RegisterDialog(window);

            // Take over the vanilla inventory key (E). A listener could only ADD our window
            // on top of the vanilla one (both open, vanilla covering ours - the reported bug),
            // because the hotkey delegate cannot suppress. Overriding the handler lets E open
            // exactly one window: ours when the option is on, the real inventory otherwise
            // (found in LoadedGuis, since replacing the handler removes the game's own open).
            if (api.Input.GetHotKeyByCode("inventorydialog") != null)
            {
                api.Input.SetHotKeyHandler("inventorydialog", _ =>
                {
                    if (config.OpenOnInventoryKey)
                    {
                        window.Toggle();
                        if (window.IsOpened()) entityContainers.Discover();
                    }
                    else
                    {
                        VanillaInventory()?.Toggle();
                    }
                    return true;
                });
            }
            else
            {
                Mod.Logger.Warning("[SymbioticInventories] 'inventorydialog' hotkey not found - E will not open the window.");
            }

            // Opening a chest has to bring the master window up, otherwise capturing it
            // would just make the container disappear with nowhere to show its contents.
            capture.OnCapturesChanged += OnCapturesChanged;

            // While the window is up, keep discovering mount/boat containers - the player may
            // mount an elk or walk up to a boat after opening it. 500 ms is imperceptible and
            // the service skips entities it has already opened.
            api.Event.RegisterGameTickListener(_ =>
            {
                if (window.IsOpened()) entityContainers.Discover();
                else entityContainers.Reset();
            }, 500);

            Mod.Logger.Notification("[SymbioticInventories] Ready.");
        }

        /// <summary>The game's own inventory/character dialog, found by its toggle code.</summary>
        private GuiDialog VanillaInventory()
        {
            foreach (var g in capi.Gui.LoadedGuis)
            {
                if (!ReferenceEquals(g, window) && g.ToggleKeyCombinationCode == "inventorydialog") return g;
            }
            return null;
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
