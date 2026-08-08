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
                // Already showing this entity's inventory (its dialog persists across a window
                // close): do not re-run the open, which would toggle it shut. This is the fix
                // for the mount inventory appearing only every other open.
                if (capture.IsEntityCaptured(ent.EntityId)) continue;
                if (!opened.Add(ent.EntityId)) continue;   // round-trip dedup within a session
                OpenEntityContainers(ent);
            }
        }

        private static bool HasContainers(Entity e)
            => e.GetBehavior<EntityBehaviorAttachable>() != null;

        /// <summary>
        /// Opens every attachable container slot on the entity. A boat exposes several crate
        /// slots and they all open - the user asked to see all of a boat's inventory at once.
        ///
        /// The interact contract, read from EntityBehaviorAttachable.OnInteract IL:
        ///   - it reads the interacting player's EntitySelection.SelectionBoxIndex (1-based),
        ///   - subtracts 1 and calls GetSlotIndexFromSelectionBoxIndex(that 0-based index),
        ///   - fetches inv[slotIndex]; if it holds a bag, hands off to the bag's
        ///     IAttachedInteractions, which opens the contents dialog.
        /// So to open a given attached slot we point the player's selection at box+1 and call
        /// OnInteract with an EMPTY hand (a real open is a right-click with nothing held; the
        /// held-item arg is what the attach path would consume). The first attempt passed the
        /// container's own slot as the hand and used the box index without the -1 - both wrong.
        /// </summary>
        private void OpenEntityContainers(Entity ent)
        {
            var beh = ent.GetBehavior<EntityBehaviorAttachable>();
            var inv = beh?.Inventory;

            // Rich diagnostics: this path can only be understood from a real animal's log.
            // Report the entity, its behaviours, and every attached item so a single test
            // tells us whether detection, the container filter, or the open is at fault.
            string behaviours = string.Join(", ",
                (ent.SidedProperties?.Behaviors ?? new List<EntityBehavior>())
                .ConvertAll(b => { try { return b.PropertyName(); } catch { return b.GetType().Name; } }));
            logger.Notification("[SymbioticInventories] Mount '{0}' behaviours: [{1}]; attachable={2}, invCount={3}.",
                ent.GetName(), behaviours, beh != null, inv?.Count ?? -1);

            if (inv == null) return;

            var player = capi.World.Player.Entity;
            var savedSel = player.EntitySelection;
            var emptyHand = new DummySlot();

            int opens = 0, containers = 0, filled = 0;
            var doneSlots = new HashSet<int>();

            try
            {
                for (int box0 = 0; box0 < 64 && opens < 8; box0++)
                {
                    int slotIndex;
                    try { slotIndex = beh.GetSlotIndexFromSelectionBoxIndex(box0); }
                    catch { continue; }
                    if (slotIndex < 0 || slotIndex >= inv.Count || !doneSlots.Add(slotIndex)) continue;

                    var slot = inv[slotIndex];
                    if (slot == null || slot.Empty) continue;
                    filled++;

                    // ONLY open items that are containers (a held bag). Synth-interacting with
                    // an empty hand on a cosmetic slot (hat, sock) would DETACH the accessory -
                    // knocking your elk's hat off. IHeldBag is the safe, public gate.
                    bool isContainer = slot.Itemstack?.Collectible?.GetCollectibleInterface<IHeldBag>() != null;
                    logger.Notification("[SymbioticInventories]   box {0} -> slot {1}: {2}{3}",
                        box0, slotIndex, slot.Itemstack?.Collectible?.Code, isContainer ? " [container]" : "");
                    if (!isContainer) continue;
                    containers++;

                    player.EntitySelection = new EntitySelection { Entity = ent, SelectionBoxIndex = box0 + 1 };
                    var handling = EnumHandling.PassThrough;
                    beh.OnInteract(player, emptyHand, ent.Pos.XYZ, EnumInteractMode.Interact, ref handling);
                    if (handling != EnumHandling.PassThrough) opens++;
                }
            }
            catch (Exception e)
            {
                logger.Warning("[SymbioticInventories] Entity container open error on {0}: {1}", ent.Code, e.Message);
            }
            finally
            {
                player.EntitySelection = savedSel;
            }

            logger.Notification(
                "[SymbioticInventories] {0}: {1} filled slot(s), {2} container(s), {3} opened.",
                ent.GetName(), filled, containers, opens);
        }
    }
}
