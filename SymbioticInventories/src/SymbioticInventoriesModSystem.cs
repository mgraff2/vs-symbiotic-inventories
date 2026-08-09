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

            var registry = new SectionRegistry(api, capture);
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

            // Take over the vanilla inventory key (E) via a Harmony prefix on the dialog
            // toggle. SetHotKeyHandler loses a startup race - each dialog re-registers its own
            // toggle in OnBlockTexturesLoaded, after mod load, so the vanilla inventory
            // reclaimed E and both windows opened. The prefix is order-independent.
            InventoryKeyInterceptor.Config = config;
            InventoryKeyInterceptor.Window = window;
            InventoryKeyInterceptor.Entities = entityContainers;
            InventoryKeyInterceptor.Capi = api;
            harmony.Patch(
                AccessTools.Method(typeof(GuiDialog), "OnKeyCombinationToggle"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(InventoryKeyInterceptor), nameof(InventoryKeyInterceptor.Prefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(InventoryKeyInterceptor), nameof(InventoryKeyInterceptor.Postfix))));

            // Opening a chest has to bring the master window up, otherwise capturing it
            // would just make the container disappear with nowhere to show its contents.
            capture.OnCapturesChanged += OnCapturesChanged;

            // While the window is up, keep discovering mount/boat containers - the player may
            // mount an elk or walk up to a boat after opening it - and drain the cellar
            // walk-by queue (containers past pick range open as the player nears them).
            // 500 ms is imperceptible and both services skip what is already open.
            bool lastCatalogOpen = false;
            api.Event.RegisterGameTickListener(_ =>
            {
                if (window.IsOpened())
                {
                    entityContainers.Discover();
                    capture.TickPendingOpens();
                }
                else
                {
                    entityContainers.Reset();
                    capture.ClearPendingOpens();
                }

                // Creative co-op: refit the master window when the catalog appears or goes
                // away, so the side-by-side pairing stays seamless without a manual toggle.
                bool catalogOpen = InventoryKeyInterceptor.VanillaInventoryDialog?.IsOpened() ?? false;
                if (catalogOpen != lastCatalogOpen)
                {
                    lastCatalogOpen = catalogOpen;
                    window.Refresh();
                }
            }, 500);

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
