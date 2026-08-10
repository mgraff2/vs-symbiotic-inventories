using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using SymbioticInventories.Core;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SymbioticInventories.Integration
{
    /// <summary>
    /// Owns the Harmony hooks that divert container GUIs into the master window.
    ///
    /// Every patch is resolved through <see cref="AccessTools"/> at runtime and skipped
    /// with a logged warning if the target is missing. That is what lets one build span
    /// 1.22.0 - 1.22.6 and unknown third-party mods: a renamed or absent method disables
    /// exactly one capture point instead of throwing during mod load.
    /// </summary>
    public class DialogCaptureService
    {
        // Vanilla block containers: chests, clay vessels, baskets, ground-stored bags,
        // and any modded block container that subclasses the API dialog.
        private const string BlockDialogType = "Vintagestory.API.Client.GuiDialogBlockEntity";

        // Entity containers: saddlebags, pack-animal panniers, boat crates.
        // Lives in VSEssentials, so it is resolved by name rather than compile-time reference.
        private const string CreatureDialogType = "Vintagestory.GameContent.GuiDialogCreatureContents";

        private static DialogCaptureService instance;

        private ICoreClientAPI capi;
        private ILogger logger;
        private ModConfig config;
        private long sequence;

        private readonly Dictionary<GuiDialog, CapturedDialog> captured = new();

        /// <summary>
        /// Positions whose dialogs we opened ourselves via chain-open. A capture at one of
        /// these must not chain again, or one click on a chest wall would cascade forever.
        /// </summary>
        private readonly HashSet<BlockPos> autoOpened = new();

        /// <summary>Ceiling on how many neighbours one click may open.</summary>
        private const int MaxChainOpen = 16;

        /// <summary>Ceiling on containers opened by one cellar sweep (cellars can be large).</summary>
        private const int MaxCellarOpen = 64;

        /// <summary>
        /// Cellar containers that were out of pick range when the sweep ran, opened one by one
        /// as the player walks within reach. Value = open attempts made; range rejection is
        /// silent (the server just ignores the click), so a capture is the only success signal
        /// and a few failed attempts mean "locked or otherwise unopenable - stop clicking it".
        /// </summary>
        private readonly Dictionary<BlockPos, int> pendingOpens = new();

        private const int MaxOpenAttempts = 3;

        private RoomRegistry roomRegistry;

        /// <summary>Raised whenever the set of captured dialogs changes, so the window can recompose.</summary>
        public event Action OnCapturesChanged;

        public IEnumerable<CapturedDialog> Captured => captured.Values.OrderBy(c => c.Sequence);

        public void Start(ICoreClientAPI api, Harmony harmony, ILogger log, ModConfig cfg)
        {
            instance = this;
            capi = api;
            logger = log;
            config = cfg;

            PatchDialog(harmony, BlockDialogType);
            PatchDialog(harmony, CreatureDialogType);

            // The room/cellar detector is a VSEssentials mod system; absent only in the most
            // stripped setups. Missing just disables the cellar feature.
            roomRegistry = api.ModLoader.GetModSystem<RoomRegistry>();
            if (roomRegistry == null)
                logger.Warning("[SymbioticInventories] RoomRegistry not found - cellar detection disabled.");

            // The OnGuiClosed prefix is necessary but NOT sufficient. Vanilla's own
            // GuiDialogBlockEntityInventory.OnGuiClosed only calls base.OnGuiClosed() when its
            // packetIdOffset is zero - for any other container it branches straight past the
            // base call, so a patch on the base declaration never fires and the section would
            // linger in the window after the chest was shut. Rather than chase every override
            // (third-party subclasses are unknowable at startup), sweep for dialogs that have
            // gone un-opened and release them. Cheap, and correct for subclasses we have never
            // seen.
            capi.Event.RegisterGameTickListener(_ => SweepClosed(), 200);
        }

        /// <summary>Releases captured dialogs that are no longer open, whatever route closed them.</summary>
        private void SweepClosed()
        {
            // Stale chain-open markers (a locked chest the server refused, a despawned block)
            // would each silently suppress one future chain. With nothing captured there is
            // nothing in flight, so the set can be safely emptied.
            if (captured.Count == 0)
            {
                autoOpened.Clear();
                return;
            }

            List<GuiDialog> dead = null;
            foreach (var dlg in captured.Keys)
            {
                bool open;
                try { open = dlg.IsOpened(); }
                catch { open = false; }
                if (!open) (dead ??= new List<GuiDialog>()).Add(dlg);
            }
            if (dead == null) return;

            foreach (var dlg in dead) captured.Remove(dlg);
            OnCapturesChanged?.Invoke();
        }

        private void PatchDialog(Harmony harmony, string typeName)
        {
            var t = AccessTools.TypeByName(typeName);
            if (t == null)
            {
                logger.Warning("[SymbioticInventories] Capture point '{0}' not found on this game build - containers of that kind will keep using their own window.", typeName);
                return;
            }

            var opened = AccessTools.DeclaredMethod(t, "OnGuiOpened");
            var closed = AccessTools.DeclaredMethod(t, "OnGuiClosed");
            var render = AccessTools.DeclaredMethod(t, "OnRenderGUI", new[] { typeof(float) });

            if (opened == null || closed == null)
            {
                logger.Warning("[SymbioticInventories] '{0}' lacks OnGuiOpened/OnGuiClosed - skipping capture.", typeName);
                return;
            }

            var self = typeof(DialogCaptureService);
            harmony.Patch(opened, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(self, nameof(OpenedPostfix))));
            harmony.Patch(closed, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(self, nameof(ClosedPrefix))));

            if (render != null)
            {
                harmony.Patch(render, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(self, nameof(RenderPrefix))));
            }
            else
            {
                logger.Warning("[SymbioticInventories] '{0}' has no OnRenderGUI(float); its window will still draw alongside the master window.", typeName);
            }

            logger.Notification("[SymbioticInventories] Capturing container dialogs of type {0}.", typeName);
        }

        // ---- Harmony hook bodies -------------------------------------------------

        private static void OpenedPostfix(GuiDialog __instance) => instance?.Capture(__instance);

        private static void ClosedPrefix(GuiDialog __instance) => instance?.Release(__instance);

        /// <summary>Suppresses the original window's drawing once we have adopted its slots.</summary>
        private static bool RenderPrefix(GuiDialog __instance)
            => instance == null || !instance.captured.ContainsKey(__instance);

        // ---- Capture / release ---------------------------------------------------

        private void Capture(GuiDialog dlg)
        {
            if (dlg == null || captured.ContainsKey(dlg)) return;

            var cap = Describe(dlg);
            if (cap == null) return;

            cap.Sequence = ++sequence;
            captured[dlg] = cap;

            // Park the original composer off-screen so it cannot swallow clicks that are
            // meant for the master window. Suppressing OnRenderGUI alone does not do this:
            // GuiDialog hit-tests against composer bounds, not against what was drawn.
            ParkOffscreen(dlg);

            OnCapturesChanged?.Invoke();

            if (cap.BlockPosition != null) MaybeChainOpen(cap.BlockPosition);
        }

        /// <summary>
        /// One click, whole chest wall: when the player opens a block container, also open
        /// every *touching* container whose block entity is the same type, so the cluster
        /// docks into the master window together.
        ///
        /// Each neighbour gets a full synthetic right-click: the block's client-side
        /// OnBlockInteractStart, then the same hand-interaction packets a real click sends
        /// (verified against SystemMouseInWorldInteractions.TryBeginUseBlock). The packet is
        /// the load-bearing half - vanilla chests open server-first: OnPlayerRightClick is a
        /// no-op on the client (its IL gates on IServerWorldAccessor), and the dialog only
        /// appears when the server processes the interaction and answers with packet 5000.
        /// A first attempt that called only the client half opened exactly one chest.
        ///
        /// The server end enforces range, locks and land claims exactly as for a real click,
        /// and modded containers work because it is their own interaction path. Same-type
        /// matching keeps this conservative: a firepit or quern touching the chest is a
        /// different block entity type and is never chained.
        /// </summary>
        private void MaybeChainOpen(BlockPos origin)
        {
            if (config?.OpenAdjacentChests != true) return;

            // This capture is one we triggered: consume the marker, do not cascade.
            if (autoOpened.Remove(origin)) return;

            var originBe = capi.World.BlockAccessor.GetBlockEntity(origin);
            if (originBe == null) return;
            var kind = originBe.GetType();

            // Every same-kind container within the radius box. A face-contiguity flood fill
            // was tried first and failed on a real chest wall: shelf boards between the rows
            // broke the chain, so only the directly touching third of the wall opened.
            int radius = Math.Clamp(config.AdjacentOpenRadius, 1, 3);   // matches the options slider
            var toOpen = new List<BlockPos>();

            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;

                        var np = origin.AddCopy(dx, dy, dz);
                        var nbe = capi.World.BlockAccessor.GetBlockEntity(np);
                        if (nbe == null || nbe.GetType() != kind) continue;
                        if (IsCapturedAt(np)) continue;

                        toOpen.Add(np);
                    }
                }
            }

            // Nearest first, so section numbers count outward from the chest that was
            // clicked, and the cap trims the far edge rather than an arbitrary corner.
            toOpen.Sort((a, b) =>
                (Math.Abs(a.X - origin.X) + Math.Abs(a.Y - origin.Y) + Math.Abs(a.Z - origin.Z))
                .CompareTo(Math.Abs(b.X - origin.X) + Math.Abs(b.Y - origin.Y) + Math.Abs(b.Z - origin.Z)));
            if (toOpen.Count > MaxChainOpen) toOpen.RemoveRange(MaxChainOpen, toOpen.Count - MaxChainOpen);

            foreach (var p in toOpen) SynthOpenBlock(p);
        }

        /// <summary>
        /// Opens the container at <paramref name="p"/> by synthesizing the exact right-click a
        /// player would make (verified against SystemMouseInWorldInteractions.TryBeginUseBlock):
        /// the block's client-side OnBlockInteractStart, then the Start/Stop block-use hand
        /// packets. The server processes it as a genuine interaction - perms/locks/range
        /// enforced - and pushes the dialog, which the capture layer adopts. Marks the position
        /// as auto-opened so the resulting capture does not cascade another chain.
        /// </summary>
        private void SynthOpenBlock(BlockPos p)
        {
            autoOpened.Add(p.Copy());
            try
            {
                var block = capi.World.BlockAccessor.GetBlock(p);
                var sel = new BlockSelection
                {
                    Position = p.Copy(),
                    Face = BlockFacing.UP,
                    HitPosition = new Vec3d(0.5, 0.5, 0.5)
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
                autoOpened.Remove(p);
                logger.Warning("[SymbioticInventories] Synthetic open of {0} failed: {1}", p, e.Message);
            }
        }

        // ---- cellar --------------------------------------------------------------

        /// <summary>
        /// The block positions of not-yet-open standard containers in the cellar the player is
        /// standing in, or null if they are not in a cellar. A cellar is the game's own room
        /// concept: a fully sealed room (no exits) with cooling walls - exactly what qualifies
        /// a space for food preservation, so it matches player intuition.
        /// </summary>
        public List<BlockPos> FindCellarContainers()
        {
            var room = roomRegistry?.GetRoomForPosition(capi.World.Player.Entity.Pos.AsBlockPos);
            if (room == null || room.Location == null) return null;
            if (room.ExitCount != 0 || room.CoolingWallCount <= 0) return null;   // not a cellar

            var loc = room.Location;
            long volume = (long)(loc.X2 - loc.X1 + 1) * (loc.Y2 - loc.Y1 + 1) * (loc.Z2 - loc.Z1 + 1);
            if (volume > 20000) return null;   // absurd "room": bail rather than scan forever

            var found = new List<BlockPos>();
            var ba = capi.World.BlockAccessor;
            for (int x = loc.X1; x <= loc.X2; x++)
            for (int y = loc.Y1; y <= loc.Y2; y++)
            for (int z = loc.Z1; z <= loc.Z2; z++)
            {
                var pos = new BlockPos(x, y, z);
                if (!IsStandardContainer(ba.GetBlockEntity(pos)) || IsCapturedAt(pos)) continue;
                found.Add(pos);
            }
            return found;
        }

        /// <summary>
        /// Opens every not-yet-open standard container in the current cellar. The server
        /// enforces pick range on each synthesized click exactly as on a real one, so a big
        /// cellar cannot be opened from one spot - far vessels would be silently rejected.
        /// What is in reach opens now; the rest queue and open as the player walks near them
        /// (<see cref="TickPendingOpens"/>).
        /// </summary>
        public void OpenCellarContainers()
        {
            var toOpen = FindCellarContainers();
            if (toOpen == null || toOpen.Count == 0) return;
            if (toOpen.Count > MaxCellarOpen) toOpen.RemoveRange(MaxCellarOpen, toOpen.Count - MaxCellarOpen);

            int now = 0;
            foreach (var p in toOpen)
            {
                if (WithinReach(p)) { SynthOpenBlock(p); now++; }
                else pendingOpens[p.Copy()] = 0;
            }
            logger.Notification(
                "[SymbioticInventories] Cellar: opened {0} container(s) in reach, {1} queued to open as you walk near them.",
                now, pendingOpens.Count);
        }

        /// <summary>Conservatively inside the server's own reach check.</summary>
        private bool WithinReach(BlockPos p)
        {
            var plr = capi.World.Player;
            double reach = plr.WorldData.PickingRange - 0.3;
            double dx = plr.Entity.Pos.X - (p.X + 0.5);
            double dy = plr.Entity.Pos.Y - (p.Y + 0.5);
            double dz = plr.Entity.Pos.Z - (p.Z + 0.5);
            return dx * dx + dy * dy + dz * dz <= reach * reach;
        }

        /// <summary>
        /// Opens queued cellar containers as the player comes within pick range of each.
        /// Runs on the window tick. A position leaves the queue when its dialog is captured,
        /// or after <see cref="MaxOpenAttempts"/> fruitless clicks (locked, claimed, or
        /// otherwise refusing to open - stop worrying it).
        /// </summary>
        public void TickPendingOpens()
        {
            if (pendingOpens.Count == 0) return;

            List<BlockPos> drop = null;
            List<BlockPos> attempt = null;
            foreach (var (p, tries) in pendingOpens)
            {
                if (IsCapturedAt(p) || tries >= MaxOpenAttempts) { (drop ??= new()).Add(p); continue; }
                if (WithinReach(p)) (attempt ??= new()).Add(p);
            }

            if (drop != null) foreach (var p in drop) pendingOpens.Remove(p);
            if (attempt == null) return;
            foreach (var p in attempt)
            {
                pendingOpens[p]++;
                SynthOpenBlock(p);
            }
        }

        /// <summary>Forget queued cellar opens (call when the window closes).</summary>
        public void ClearPendingOpens() => pendingOpens.Clear();

        /// <summary>A nearby machine's slot panel: which inventory slots to show in the
        /// window's strip and which of them is take-only output.</summary>
        public class MachineInfo
        {
            public BlockPos Pos;
            public IInventory Inv;

            /// <summary>Inventory slot ids shown, in panel order.</summary>
            public int[] Slots;

            /// <summary>Absolute slot id that is OUTPUT-ONLY (deposit clicks swallowed,
            /// down-arrow marker), or -1.</summary>
            public int OutputSlot = -1;
        }

        /// <summary>
        /// The nearest working machines (up to two): querns and the whole FIREPIT family -
        /// which includes vanilla firepits and the Stone Bake Oven's controller and
        /// cooking top, since they derive from BlockEntityFirepit. Deliberately NOT
        /// captures: machines keep their own dialogs (progress bars, temperatures). The
        /// master window renders tiny side-stations bound directly to these inventories -
        /// they are BlockEntityOpenableContainers, so slot clicks travel the same
        /// block-entity packet envelope their own dialogs use, with REAL slot semantics.
        /// Firepit slot order (vanilla InventorySmelting): 0 fuel, 1 input, 2 output.
        /// </summary>
        public List<MachineInfo> FindNearbyMachines(int radius = 5)
        {
            var found = new List<(int d, MachineInfo m)>();
            var center = capi.World?.Player?.Entity?.Pos?.AsBlockPos;
            if (center == null) return new List<MachineInfo>();

            var ba = capi.World.BlockAccessor;
            for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -2; dy <= 2; dy++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                var p = center.AddCopy(dx, dy, dz);
                var be = ba.GetBlockEntity(p);
                int d = dx * dx + dy * dy + dz * dz;

                if (be is BlockEntityQuern q && q.Inventory != null && q.Inventory.Count >= 2)
                {
                    found.Add((d, new MachineInfo { Pos = p, Inv = q.Inventory, Slots = new[] { 0, 1 }, OutputSlot = 1 }));
                }
                else if (be is BlockEntityFirepit f && f.Inventory != null && f.Inventory.Count >= 3)
                {
                    // With a cooking container in the input, the firepit exposes its four
                    // ingredient slots (user ask: "4 recipe inputs, 1 output, 1 pot-only
                    // slot"): fuel, pot, in1-4, output. Bare firepit: fuel, input, output.
                    int[] slots = f.Inventory is InventorySmelting sm && sm.HaveCookingContainer && f.Inventory.Count >= 7
                        ? new[] { 0, 1, 3, 4, 5, 6, 2 }
                        : new[] { 0, 1, 2 };
                    found.Add((d, new MachineInfo { Pos = p, Inv = f.Inventory, Slots = slots, OutputSlot = 2 }));
                }
            }

            found.Sort((a, b) => a.d.CompareTo(b.d));
            var result = new List<MachineInfo>();
            foreach (var (_, m) in found)
            {
                result.Add(m);
                if (result.Count >= 2) break;   // the strip has room for two stations
            }
            return result;
        }

        /// <summary>
        /// A container the auto-open paths may safely right-click: one whose right-click
        /// OPENS A DIALOG. BlockEntityOpenableContainer is the vanilla base carrying exactly
        /// that contract, and it is what chests, vessels and baskets derive from.
        ///
        /// Requiring it is what keeps a synthesized click from becoming an item transfer:
        /// the click-to-take family - shelves, pallets, FoodShelves flour sacks, anything on
        /// BlockEntityDisplay - are also BlockEntityContainers, and a sweep that clicked one
        /// would WITHDRAW GOODS (it pulled flour out of every sack in reach). Mere
        /// containment is not evidence of a dialog; only OpenableContainer promises one.
        ///
        /// Barrels and querns are openable too but carry machine dialogs the capture layer
        /// ignores; excluded by name so a cellar sweep does not pop their own windows.
        /// </summary>
        private static bool IsStandardContainer(BlockEntity be)
        {
            if (!(be is BlockEntityOpenableContainer)) return false;
            string n = be.GetType().Name;
            return n != "BlockEntityBarrel"
                && n != "BlockEntityQuern"
                && n != "BlockEntityCrock"
                && n != "BlockEntityBloomery";
        }

        private bool IsCapturedAt(BlockPos pos)
        {
            foreach (var cap in captured.Values)
            {
                if (cap.BlockPosition != null && cap.BlockPosition.Equals(pos)) return true;
            }
            return false;
        }

        /// <summary>
        /// Whether a container belonging to this entity is already captured. Entity dialogs
        /// stay open (parked) when the master window closes, so on reopen the auto-discovery
        /// must NOT re-run the open interaction - that would toggle the already-open dialog
        /// shut, making the mount inventory appear only every other open.
        /// </summary>
        public bool IsEntityCaptured(long entityId)
        {
            foreach (var cap in captured.Values)
            {
                if (cap.OwningEntity != null && cap.OwningEntity.EntityId == entityId) return true;
            }
            return false;
        }

        private void Release(GuiDialog dlg)
        {
            if (dlg == null || !captured.Remove(dlg)) return;
            OnCapturesChanged?.Invoke();
        }

        private void ParkOffscreen(GuiDialog dlg)
        {
            try
            {
                var composer = dlg.SingleComposer;
                if (composer?.Bounds == null) return;
                composer.Bounds.Alignment = EnumDialogArea.None;
                composer.Bounds.fixedX = -20000;
                composer.Bounds.fixedY = -20000;
                composer.Bounds.CalcWorldBounds();
            }
            catch (Exception e)
            {
                logger.Warning("[SymbioticInventories] Could not park dialog off-screen: {0}", e.Message);
            }
        }

        /// <summary>
        /// Pulls inventory, packet route and label off a dialog we know nothing about at
        /// compile time. Anything we cannot read is left null and the dialog is not captured.
        /// </summary>
        private CapturedDialog Describe(GuiDialog dlg)
        {
            var type = dlg.GetType();

            // Block containers expose everything publicly on GuiDialogBlockEntity.
            var invProp = AccessTools.Property(type, "Inventory");
            var posProp = AccessTools.Property(type, "BlockEntityPosition");
            if (invProp != null && posProp != null)
            {
                // Machines and workstations also derive from GuiDialogBlockEntity, but their
                // windows carry progress bars, temperature readouts and recipe pickers that a
                // plain slot grid cannot represent. Absorbing a firepit would show its slots
                // while silently destroying any way to see whether it is lit. Only the plain
                // container dialog - chests, vessels, baskets, ground bags - is eligible.
                if (!(dlg is GuiDialogBlockEntityInventory)) return null;

                var inv = invProp.GetValue(dlg) as IInventory;
                if (inv == null) return null;
                var pos = posProp.GetValue(dlg) as BlockPos;
                return new CapturedDialog
                {
                    Dialog = dlg,
                    Inventory = inv,
                    BlockPosition = pos,
                    SendPacket = MakeSender(dlg),
                    Title = BlockTitle(pos)
                };
            }

            // Entity containers keep their state in private fields.
            var invField = AccessTools.Field(type, "inv");
            if (invField != null)
            {
                var inv = invField.GetValue(dlg) as IInventory;
                if (inv == null) return null;
                var ent = AccessTools.Field(type, "owningEntity")?.GetValue(dlg) as Entity;

                // Carcass harvesting opens this same dialog class, but it is a one-shot loot
                // window mid-knife-animation, not a container: absorbing it hides the loot
                // behind the master window. The harvest behavior owns the inventory it shows,
                // so identity against that field is an exact test - a dead pack animal still
                // gets its saddlebags captured (different inventory) while its carcass window
                // passes through.
                if (ent?.GetBehavior("harvestable") is { } bh
                    && ReferenceEquals(AccessTools.Field(bh.GetType(), "inv")?.GetValue(bh), inv))
                {
                    return null;
                }

                var title = AccessTools.Field(type, "title")?.GetValue(dlg) as string;
                return new CapturedDialog
                {
                    Dialog = dlg,
                    Inventory = inv,
                    OwningEntity = ent,
                    SendPacket = MakeSender(dlg),
                    Title = !string.IsNullOrEmpty(title) ? title : EntityTitle(ent)
                };
            }

            return null;
        }

        /// <summary>
        /// Binds the dialog's own DoSendPacket. Reusing it rather than sending raw packets
        /// is what keeps modded containers working: the dialog knows its own packet id
        /// offset and block-entity or entity envelope, and we do not have to.
        /// </summary>
        private Action<object> MakeSender(GuiDialog dlg)
        {
            var m = AccessTools.Method(dlg.GetType(), "DoSendPacket", new[] { typeof(object) });
            if (m == null) return _ => { };
            return p =>
            {
                try { m.Invoke(dlg, new[] { p }); }
                catch (Exception e) { logger.Error("[SymbioticInventories] Slot packet failed: {0}", e); }
            };
        }

        private string BlockTitle(BlockPos pos)
        {
            if (pos == null) return Lang.Get("symbioticinventories:container");
            var block = capi.World.BlockAccessor.GetBlock(pos);
            if (block == null) return Lang.Get("symbioticinventories:container");
            var name = block.GetPlacedBlockName(capi.World, pos);
            return string.IsNullOrEmpty(name) ? block.Code?.ToShortString() : name;
        }

        private string EntityTitle(Entity ent)
            => ent?.GetName() ?? Lang.Get("symbioticinventories:container");

        public void Stop()
        {
            captured.Clear();
            instance = null;
        }
    }
}
