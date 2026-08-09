using SymbioticInventories.Core;
using SymbioticInventories.Gui;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SymbioticInventories.Integration
{
    /// <summary>
    /// Redirects the vanilla inventory key (E) to the master window.
    ///
    /// SetHotKeyHandler could not win: every GuiDialog re-registers its own toggle handler in
    /// OnBlockTexturesLoaded, which runs AFTER mod startup, so the vanilla inventory dialog
    /// overwrote ours (E opened both). Instead this prefixes the toggle itself. When the dialog
    /// being toggled is the vanilla inventory and the option is on, it opens our window and
    /// returns false to skip the vanilla open - order-independent, and the exact code path the
    /// game runs for the key, so there is no double window.
    ///
    /// CREATIVE is the exception: there the vanilla dialog IS the creative catalog, and
    /// suppressing it locks the player out of spawning items entirely. So in creative the
    /// prefix toggles our window alongside and lets the vanilla toggle run - E gives both
    /// the catalog and the master window, which is exactly what creative building wants
    /// (no more flipping to survival just to reach your inventory).
    /// </summary>
    public static class InventoryKeyInterceptor
    {
        public static ModConfig Config;
        public static GuiDialogMasterInventory Window;
        public static EntityContainerService Entities;
        public static ICoreClientAPI Capi;

        /// <summary>The vanilla inventory dialog instance (the creative catalog, in
        /// creative). Captured here so the master window can read its live bounds and fit
        /// itself into the space beside it.</summary>
        public static GuiDialog VanillaInventoryDialog;

        public static bool Prefix(GuiDialog __instance, ref bool __result)
        {
            if (__instance.ToggleKeyCombinationCode != "inventorydialog") return true;  // some other dialog
            VanillaInventoryDialog = __instance;

            if (Config == null || Window == null) return true;              // not wired yet
            if (!Config.OpenOnInventoryKey) return true;                    // option off: vanilla as normal

            Window.Toggle();
            if (Window.IsOpened()) Entities?.Discover();

            if (Capi?.World?.Player?.WorldData?.CurrentGameMode == EnumGameMode.Creative)
            {
                return true;   // creative: ALSO run the vanilla toggle - that is the catalog
            }

            __result = true;   // the key was consumed
            return false;      // skip the vanilla inventory toggle
        }

        /// <summary>
        /// After the vanilla toggle actually ran (the creative path lets it through), refit
        /// the master window: its compose happened inside the prefix, BEFORE the catalog
        /// opened, so the first fit could not see the catalog's bounds yet.
        /// </summary>
        public static void Postfix(GuiDialog __instance)
        {
            if (__instance.ToggleKeyCombinationCode != "inventorydialog") return;
            if (Capi?.World?.Player?.WorldData?.CurrentGameMode != EnumGameMode.Creative) return;
            Window?.Refresh();
        }
    }
}
