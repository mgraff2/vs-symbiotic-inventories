using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SymbioticInventories.Integration
{
    /// <summary>One nearby FoodShelves container, with its flattened shelf geometry.</summary>
    public class AmbientShelf
    {
        public BlockPos Pos;
        public IInventory Inventory;

        /// <summary>Rows = shelf levels, Cols = segments x items-per-segment: the shelf's
        /// real spatial arrangement flattened into an X x Y grid block. Cols = 0 means the
        /// geometry did not match the inventory (bulk sacks, modded oddities) and the
        /// section flows as an ordinary ribbon instead.</summary>
        public int Rows, Cols;

        /// <summary>Slots per clickable segment - maps a slot index back to the segment
        /// selection box a real right-click would hit.</summary>
        public int ItemsPerSegment;

        /// <summary>Bulk container (ItemsPerSegment > 1, e.g. a flour sack): rendered as
        /// ONE FACADE CELL per segment showing the live aggregate count, instead of its
        /// raw slots. Rows/Cols then describe the segment grid.</summary>
        public bool Facade;

        public string Label;
        public ItemStack Icon;
    }

    /// <summary>
    /// Discovery + interaction for FoodShelves-style ambient containers: crock shelves,
    /// bread/pie/cheese shelves, flour sacks, baskets, display cases. These blocks have NO
    /// dialog at all - putting and taking is done by right-clicking shelf segments in the
    /// world - so there is nothing to capture, and no inventory packet route to reuse
    /// (their block entities never override OnReceivedClientPacket; a slot packet would be
    /// silently dropped server-side, verified against FoodShelves 3.0.2).
    ///
    /// Instead: sections render the block entity's client-synced inventory (block-entity
    /// sync keeps it live), and a CLICK on a cell synthesizes the real block interaction
    /// against that cell's segment selection box - the same trick as chain-open and mount
    /// discovery. Empty hand takes from the segment; a shelvable item in the ACTIVE HOTBAR
    /// slot puts. The server runs FoodShelves' own OnInteract, so every placement rule
    /// (what fits on which shelf) is enforced by the mod itself - including on shelf types
    /// this mod has never seen.
    /// </summary>
    public class ShelfDiscoveryService
    {
        private ICoreClientAPI capi;
        private ILogger logger;

        // FoodShelves is a SOFT dependency: resolved by name once, absent = feature off.
        private Type beBaseType;
        private bool probed;
        private PropertyInfo invProp, shelfCountProp, segsProp, perSegProp;

        public void Start(ICoreClientAPI api, ILogger log)
        {
            capi = api;
            logger = log;
            // Facade cells display live aggregates; keep them current as sacks fill/drain.
            api.Event.RegisterGameTickListener(UpdateFacades, 250);
        }

        private Type BaseType()
        {
            if (probed) return beBaseType;
            probed = true;

            beBaseType = AccessTools.TypeByName("FoodShelves.BEBaseFSContainer");
            if (beBaseType == null)
            {
                logger.Notification("[SymbioticInventories] FoodShelves not installed - shelf integration off.");
                return null;
            }

            invProp = AccessTools.Property(beBaseType, "Inventory");
            shelfCountProp = AccessTools.Property(beBaseType, "ShelfCount");
            segsProp = AccessTools.Property(beBaseType, "SegmentsPerShelf");
            perSegProp = AccessTools.Property(beBaseType, "ItemsPerSegment");
            if (invProp == null)
            {
                logger.Warning("[SymbioticInventories] FoodShelves found but its Inventory member moved - shelf integration off.");
                beBaseType = null;
            }
            return beBaseType;
        }

        /// <summary>Every FoodShelves container within the radius, nearest first (then by
        /// coordinates, so section order - and therefore ribbon layout - is stable while
        /// the player stands still).</summary>
        public List<AmbientShelf> Discover(int radius = 5)
        {
            var found = new List<(int dist, AmbientShelf shelf)>();
            var t = BaseType();
            var center = capi.World?.Player?.Entity?.Pos?.AsBlockPos;
            if (t == null || center == null) return new List<AmbientShelf>();

            var ba = capi.World.BlockAccessor;
            for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -3; dy <= 3; dy++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                var p = center.AddCopy(dx, dy, dz);
                var be = ba.GetBlockEntity(p);
                if (be == null || !t.IsInstanceOfType(be)) continue;

                var inv = invProp.GetValue(be) as IInventory;
                if (inv == null || inv.Count == 0) continue;

                int shelves = (shelfCountProp?.GetValue(be) as int?) ?? 1;
                int segs = (segsProp?.GetValue(be) as int?) ?? 1;
                int perSeg = (perSegProp?.GetValue(be) as int?) ?? 1;

                // Bulk containers (many items per segment - flour sacks, baskets) become
                // FACADE grids: one cell per segment carrying the live total, "a container
                // with an internal grid of 1" (user ask). Per-item shelves keep their real
                // X x Y slot grid when the declared geometry matches the inventory; only
                // genuinely odd shapes fall back to a flowing ribbon.
                bool geomOk = shelves >= 1 && segs >= 1 && perSeg >= 1
                           && shelves * segs * perSeg == inv.Count;
                bool facade = geomOk && perSeg > 1 && segs <= 12;
                bool structured = geomOk && perSeg == 1 && segs <= 12;

                var block = ba.GetBlock(p);
                var stack = block != null && block.Id != 0 ? new ItemStack(block) : null;

                found.Add((dx * dx + dy * dy + dz * dz, new AmbientShelf
                {
                    Pos = p,
                    Inventory = inv,
                    Rows = facade || structured ? shelves : 0,
                    Cols = facade ? segs : (structured ? segs : 0),
                    ItemsPerSegment = Math.Max(1, perSeg),
                    Facade = facade,
                    Label = stack?.GetName() ?? "?",
                    Icon = stack
                }));
            }

            found.Sort((a, b) =>
            {
                int c = a.dist.CompareTo(b.dist);
                if (c != 0) return c;
                c = a.shelf.Pos.X.CompareTo(b.shelf.Pos.X);
                if (c != 0) return c;
                c = a.shelf.Pos.Y.CompareTo(b.shelf.Pos.Y);
                return c != 0 ? c : a.shelf.Pos.Z.CompareTo(b.shelf.Pos.Z);
            });
            return found.ConvertAll(f => f.shelf);
        }

        /// <summary>Cheap change signature for the tick watcher: recompose only when the
        /// set of nearby shelves (or their slot counts) actually changes.</summary>
        public long Signature()
        {
            long sig = 17;
            foreach (var sh in Discover())
            {
                sig = sig * 31 + sh.Pos.GetHashCode();
                sig = sig * 31 + sh.Inventory.Count;
            }
            return sig;
        }

        // ---- facade cells ---------------------------------------------------------
        //
        // A bulk container renders as one DISPLAY-ONLY cell per segment: a DummyInventory
        // whose stack mirrors the segment's live total (icon + count, "flour x188"). The
        // slot grid never mutates it - clicks are intercepted upstream and drive the pump
        // - and a quarter-second tick keeps the numbers matching the real inventory.

        private class FacadeBinding
        {
            public DummyInventory Dummy;
            public IInventory Real;
            public int PerSeg;
        }

        private readonly List<FacadeBinding> facades = new();

        /// <summary>Sections rebuild on every compose; stale bindings go with them.</summary>
        public void ClearFacades() => facades.Clear();

        /// <summary>The display inventory for one bulk container: one cell per segment.</summary>
        public IInventory BuildFacade(AmbientShelf sh)
        {
            var b = new FacadeBinding
            {
                Dummy = new DummyInventory(capi, Math.Max(1, sh.Rows * sh.Cols)),
                Real = sh.Inventory,
                PerSeg = Math.Max(1, sh.ItemsPerSegment)
            };
            facades.Add(b);
            UpdateFacade(b);
            return b.Dummy;
        }

        private void UpdateFacades(float dt)
        {
            foreach (var b in facades) UpdateFacade(b);
        }

        private static void UpdateFacade(FacadeBinding b)
        {
            for (int s = 0; s < b.Dummy.Count; s++)
            {
                int sum = 0;
                ItemStack first = null;
                for (int i = s * b.PerSeg; i < (s + 1) * b.PerSeg && i < b.Real.Count; i++)
                {
                    var st = b.Real[i]?.Itemstack;
                    if (st == null) continue;
                    sum += st.StackSize;
                    first ??= st;
                }

                var slot = b.Dummy[s];
                if (first == null)
                {
                    slot.Itemstack = null;
                    continue;
                }
                if (slot.Itemstack == null || slot.Itemstack.Collectible != first.Collectible)
                {
                    slot.Itemstack = first.Clone();
                }
                slot.Itemstack.StackSize = sum;
            }
        }

        // ---- native bulk gestures: the control-packet sandwich --------------------
        //
        // FoodShelves decides interaction QUANTITY server-side from two booleans on the
        // player's controls: ShiftKey picks put-vs-take on bulk slots, CtrlKey picks
        // whole-stack-vs-one. Those flags reach the server ONLY as tiny MoveKeyChange
        // packets (Packet_Client Id=21) riding the SAME ordered TCP stream as the hand-
        // interaction packet - so "flags down, interact, flags restored" is guaranteed to
        // be processed in exactly that order, and ONE interaction moves a whole stack with
        // the mod's own server-side rules. (Engine and FoodShelves 3.0.5 IL both verified
        // by two independent reviews.) This replaced a 64-step interaction pump whose
        // client-side state machine kept reversing deposits into takes - see git history.

        private Type pktClientType, pktMoveType;
        private FieldInfo pktIdField, pktMoveField, moveKeyField, moveDownField;
        private bool forgeProbed, forgeOk;

        private bool ForgeAvailable()
        {
            if (forgeProbed) return forgeOk;
            forgeProbed = true;

            pktClientType = AccessTools.TypeByName("Packet_Client");
            pktMoveType = AccessTools.TypeByName("Packet_MoveKeyChange");
            pktIdField = pktClientType == null ? null : AccessTools.Field(pktClientType, "Id");
            pktMoveField = pktClientType == null ? null : AccessTools.Field(pktClientType, "MoveKeyChange");
            moveKeyField = pktMoveType == null ? null : AccessTools.Field(pktMoveType, "Key");
            moveDownField = pktMoveType == null ? null : AccessTools.Field(pktMoveType, "Down");

            forgeOk = pktIdField != null && pktMoveField != null
                   && moveKeyField != null && moveDownField != null;
            if (!forgeOk)
                logger.Warning("[SymbioticInventories] MoveKeyChange packet shape changed on this game build - shelf clicks fall back to single items.");
            return forgeOk;
        }

        /// <summary>One MoveKeyChange packet: "this control flag is now down/up".</summary>
        private void SendMoveKey(int key, bool down)
        {
            var move = Activator.CreateInstance(pktMoveType);
            moveKeyField.SetValue(move, key);
            moveDownField.SetValue(move, down ? 1 : 0);
            var pkt = Activator.CreateInstance(pktClientType);
            pktIdField.SetValue(pkt, 21);
            pktMoveField.SetValue(pkt, move);
            capi.Network.SendPacketClient(pkt);
        }

        /// <summary>
        /// One native interaction with forged modifier flags. The client controls are
        /// flipped only around the synchronous OnBlockInteractStart call (so client
        /// prediction moves the same amount the server will), then restored before the
        /// input tick can ever observe them; the server-side flags are restored to the
        /// client's REAL current view, so physically held keys never desync. While
        /// mounted the flags land on the seat's controls (exactly as physical keys do),
        /// so the forge degrades to the plain single-item interaction there - parity
        /// with the in-world gesture, not a regression.
        /// </summary>
        private void InteractForged(AmbientShelf shelf, int slotIndex, bool ctrl, bool shift)
        {
            var player = capi.World.Player;
            if (player.Entity.MountedOn != null)
            {
                // Unreachable via InteractCell (it refuses mounted clicks outright), kept
                // as defense: control-key packets go to the mount's controls while riding,
                // so a forged whole-stack flag could never reach the shelf mod anyway.
                Interact(shelf, slotIndex);
                return;
            }
            if (!ForgeAvailable())
            {
                Interact(shelf, slotIndex);
                return;
            }

            int ctrlKey = (int)EnumEntityAction.CtrlKey;
            int shiftKey = (int)EnumEntityAction.ShiftKey;
            SendMoveKey(ctrlKey, ctrl);
            SendMoveKey(shiftKey, shift);

            var controls = player.Entity.Controls;
            bool c0 = controls.CtrlKey, s0 = controls.ShiftKey;
            controls.CtrlKey = ctrl;
            controls.ShiftKey = shift;
            try
            {
                Interact(shelf, slotIndex);
            }
            finally
            {
                controls.CtrlKey = c0;
                controls.ShiftKey = s0;
                SendMoveKey(ctrlKey, c0);
                SendMoveKey(shiftKey, s0);
            }
        }

        /// <summary>
        /// A click on a shelf cell. STATELESS - every gesture is exactly one native
        /// interaction (no jobs, no timers, nothing a second click could reverse):
        ///   holding a stack in the active hand -> pour the whole stack in (one op)
        ///   stack on the mouse cursor          -> shuttle to a free hotbar slot, pour
        ///   empty-handed                       -> take a whole stack out (one op)
        ///   SHIFT-click                        -> move exactly ONE, either direction
        /// </summary>
        public void InteractCell(AmbientShelf shelf, int slotIndex)
        {
            // From the saddle, shelf cells refuse outright (user decision): the server
            // hardcodes control-key packets to the MOUNT's controls while riding, so the
            // whole-stack signal can never reach the shelf mod - and a sack that dribbles
            // single items reads as broken. Refuse with the reason instead.
            if (capi.World.Player.Entity.MountedOn != null)
            {
                capi.TriggerIngameError(this, "si-mounted",
                    Vintagestory.API.Config.Lang.Get("symbioticinventories:mounted-bulk"));
                return;
            }

            bool shiftClick = capi.Input.KeyboardKeyState[(int)GlKeys.LShift]
                           || capi.Input.KeyboardKeyState[(int)GlKeys.RShift];
            var im = capi.World.Player.InventoryManager;
            var hotbar = im.GetHotbarInventory();
            var hand = hotbar[im.ActiveHotbarSlotNumber];
            var mouse = im.MouseItemSlot;

            // Shift-click: move exactly one - put if the hand holds something, else take.
            if (shiftClick)
            {
                InteractForged(shelf, slotIndex, ctrl: false, shift: !hand.Empty);
                return;
            }

            // Whole-stack pour straight from the active hand - the natural in-world flow.
            if (!hand.Empty)
            {
                InteractForged(shelf, slotIndex, ctrl: true, shift: true);
                return;
            }

            // Mouse-carried stack: one shuttle click into an empty hotbar slot (ordered on
            // the same stream, so the server has the stack in hand before the interact),
            // ONE forged pour, selection restored immediately (restoring a selection moves
            // no items). Leftover - sack full - returns to the cursor after the server
            // settles; with one op that is one small callback, not a state machine.
            if (mouse != null && !mouse.Empty)
            {
                int empty = -1;
                for (int i = 0; i < 10 && i < hotbar.Count; i++)
                {
                    if (hotbar[i].Empty) { empty = i; break; }
                }
                if (empty < 0)
                {
                    logger.Notification("[SymbioticInventories] Depositing from the cursor needs one empty hotbar slot (or hold the stack in your hand).");
                    return;
                }

                int restore = im.ActiveHotbarSlotNumber;
                im.ActiveHotbarSlotNumber = empty;
                ClickSlot(hotbar, empty);   // carried -> working hand
                try
                {
                    InteractForged(shelf, slotIndex, ctrl: true, shift: true);
                }
                finally
                {
                    im.ActiveHotbarSlotNumber = restore;
                }

                capi.Event.RegisterCallback(_ =>
                {
                    try
                    {
                        if (!hotbar[empty].Empty && im.MouseItemSlot.Empty) ClickSlot(hotbar, empty);
                    }
                    catch (Exception e)
                    {
                        logger.Warning("[SymbioticInventories] Leftover return failed: {0}", e.Message);
                    }
                }, 400);
                return;
            }

            // Empty-handed: take a whole stack, then lift it onto the CURSOR - a vessel
            // pickup (user ask: "to my hand, not my belt"). FoodShelves delivers the stack
            // into the player's inventory itself, wherever it fits; snapshot the hotbar
            // first, and after the server settles, the slot that GREW is where it landed -
            // one click moves it to the cursor. If it landed in the bags instead, or the
            // cursor is busy by then, it simply stays where the mod put it.
            var before = new int[hotbar.Count];
            for (int i = 0; i < hotbar.Count; i++) before[i] = hotbar[i].StackSize;

            InteractForged(shelf, slotIndex, ctrl: true, shift: false);

            // The interaction runs the mod's CLIENT prediction synchronously, so the slot
            // the stack landed in is known right now - lift it to the cursor in the same
            // breath. The lift packet rides the same ordered stream as the take, so the
            // server performs take -> lift back-to-back and the stack never visibly rests
            // in the belt (the earlier 350ms settle showed exactly that layover - user).
            // If prediction didn't place it (unusual), one delayed retry catches it.
            bool LiftLanded()
            {
                if (!im.MouseItemSlot.Empty) return true;   // cursor busy: leave it in the belt
                for (int i = 0; i < hotbar.Count && i < before.Length; i++)
                {
                    if (hotbar[i].StackSize > before[i])
                    {
                        ClickSlot(hotbar, i);   // the landed stack -> cursor
                        return true;
                    }
                }
                return false;
            }

            if (!LiftLanded())
            {
                capi.Event.RegisterCallback(_ =>
                {
                    try { LiftLanded(); }
                    catch (Exception e)
                    {
                        logger.Warning("[SymbioticInventories] Take-to-cursor lift failed: {0}", e.Message);
                    }
                }, 300);
            }
        }

        /// <summary>An ordinary slot click with the mouse-cursor slot - the exact packets a
        /// real click sends (recipe verified against GuiElementItemSlotGridBase.SlotClick).</summary>
        private void ClickSlot(IInventory inv, int slotId)
        {
            var player = capi.World.Player;
            var op = new ItemStackMoveOperation(capi.World, EnumMouseButton.Left,
                0, (EnumMergePriority)0, 0) { ActingPlayer = player };
            var packet = inv.ActivateSlot(slotId, player.InventoryManager.MouseItemSlot, ref op);
            if (packet != null) capi.Network.SendPacketClient(packet);
        }

        /// <summary>
        /// Acts on one shelf cell exactly as a right-click on its segment's selection box
        /// would: client-side OnBlockInteractStart plus the Start/StopBlockUse hand packets
        /// (verified pattern from chain-open). The server runs the mod's own interaction -
        /// perms, claims, range, and shelf rules all enforced there.
        /// </summary>
        private void Interact(AmbientShelf shelf, int slotIndex)
        {
            try
            {
                var block = capi.World.BlockAccessor.GetBlock(shelf.Pos);
                var sel = new BlockSelection
                {
                    Position = shelf.Pos.Copy(),
                    Face = BlockFacing.UP,
                    HitPosition = new Vec3d(0.5, 0.5, 0.5),
                    SelectionBoxIndex = Math.Max(0, slotIndex / Math.Max(1, shelf.ItemsPerSegment))
                };
                block.OnBlockInteractStart(capi.World, capi.World.Player, sel);
                capi.Network.SendHandInteraction(2, sel, null,
                    EnumHandInteract.BlockInteract, (int)EnumHandInteractNw.StartBlockUse,
                    false, (EnumItemUseCancelReason)0);
                capi.Network.SendHandInteraction(2, sel, null,
                    EnumHandInteract.BlockInteract, (int)EnumHandInteractNw.StopBlockUse,
                    false, (EnumItemUseCancelReason)0);
            }
            catch (Exception e)
            {
                logger.Warning("[SymbioticInventories] Shelf interact at {0} failed: {1}", shelf.Pos, e.Message);
            }
        }
    }
}
