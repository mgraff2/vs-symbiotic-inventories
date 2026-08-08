using System;
using System.Collections.Generic;
using SymbioticInventories.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SymbioticInventories.Integration
{
    /// <summary>
    /// Auto-opens the containers carried by entities - the mount you are riding, and
    /// optionally pack animals and moored boats nearby - so their inventories appear in the
    /// master window without opening each by hand.
    ///
    /// It never touches the inventory data directly. For each candidate it invokes the
    /// entity's own <see cref="EntityBehaviorAttachable.OnInteract"/> per container slot,
    /// exactly as clicking that saddlebag would: the game opens a real
    /// GuiDialogCreatureContents (routed via SendEntityPacketWithOffset), and the existing
    /// capture layer adopts it with correct packet syncing. Same philosophy as block
    /// chain-open - trigger the real path, adopt the result - so modded mounts and boats work
    /// unchanged and the server still enforces access.
    ///
    /// UNVERIFIED IN-GAME: entity selection-box geometry cannot be inspected without a live
    /// elk/boat in front of the client, so this is the least battle-tested capture path. It
    /// logs what it opens; failures degrade to "that container just doesn't auto-open".
    /// </summary>
    public class EntityContainerService
    {
        private ICoreClientAPI capi;
        private ModConfig config;
        private ILogger logger;
        private DialogCaptureService capture;

        /// <summary>Entities we have already auto-opened this window session, so the tick
        /// does not re-fire OnInteract every 500 ms.</summary>
        private readonly HashSet<long> opened = new();

        public void Start(ICoreClientAPI api, ModConfig cfg, DialogCaptureService cap, ILogger log)
        {
            capi = api;
            config = cfg;
            capture = cap;
            logger = log;
        }

        /// <summary>Clears the per-session open memory (call when the window closes).</summary>
        public void Reset() => opened.Clear();

        /// <summary>
        /// Finds and opens eligible entity containers. Cheap to call repeatedly - it skips
        /// entities it has already opened and does nothing when both features are off.
        /// </summary>
        public void Discover()
        {
            var player = capi.World?.Player?.Entity;
            if (player == null) return;

            var candidates = new List<Entity>();

            if (config.ShowMountInventory)
            {
                var mount = player.MountedOn?.MountSupplier?.OnEntity ?? player.MountedOn?.Entity;
                if (mount != null) candidates.Add(mount);
            }

            int r = Math.Clamp(config.NearbyEntityRadius, 0, 10);
            if (r > 0)
            {
                foreach (var e in capi.World.GetEntitiesAround(player.Pos.XYZ, r, r,
                             e => e.EntityId != player.EntityId && HasContainers(e)))
                {
                    candidates.Add(e);
                }
            }

            foreach (var ent in candidates)
            {
                if (!opened.Add(ent.EntityId)) continue;
                OpenEntityContainers(ent);
            }
        }

        private static bool HasContainers(Entity e)
            => e.GetBehavior<EntityBehaviorAttachable>() != null;

        /// <summary>
        /// Opens every attachable container slot on the entity. A boat exposes several crate
        /// slots and they all open - the user asked to see all of a boat's inventory at once.
        /// </summary>
        private void OpenEntityContainers(Entity ent)
        {
            var beh = ent.GetBehavior<EntityBehaviorAttachable>();
            if (beh?.Inventory == null) return;

            var player = capi.World.Player.Entity;
            int opens = 0;

            // Selection-box indices are 1-based in OnInteract (it subtracts 1 before mapping);
            // scan a generous range and open the boxes that map to a real, non-empty container
            // slot. Capped so a pathological entity cannot spawn dozens of dialogs.
            for (int box = 1; box <= 32 && opens < 8; box++)
            {
                ItemSlot slot;
                try { slot = beh.GetSlotFromSelectionBoxIndex(box); }
                catch { continue; }
                if (slot == null || slot.Empty) continue;

                // Only slots whose stack is itself a container are worth opening.
                if (slot.Itemstack?.Collectible?.GetCollectibleInterface<IHeldBag>() == null
                    && slot.Itemstack?.Collectible?.Attributes?["attachableToEntity"].Exists != true)
                {
                    // Fall through anyway - GetCollectibleInterface is not available on all
                    // builds; OnInteract itself is the real gate and no-ops on a non-container.
                }

                try
                {
                    // Point the player's selection at this box, then run the entity's own
                    // interact - the same call vanilla makes on a right-click.
                    if (player.EntitySelection == null) player.EntitySelection = new EntitySelection();
                    player.EntitySelection.Entity = ent;
                    player.EntitySelection.SelectionBoxIndex = box;

                    var handling = EnumHandling.PassThrough;
                    beh.OnInteract(player, slot, ent.Pos.XYZ, EnumInteractMode.Interact, ref handling);
                    opens++;
                }
                catch (Exception e)
                {
                    logger.Warning("[SymbioticInventories] Entity container open failed on {0} box {1}: {2}",
                        ent.Code, box, e.Message);
                }
            }

            if (opens > 0)
                logger.Notification("[SymbioticInventories] Auto-opened {0} container(s) on {1}.", opens, ent.GetName());
        }
    }
}
