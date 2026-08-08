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
        private const int MaxChainOpen = 8;

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
        /// Fully client-side: each neighbour block's own OnBlockInteractStart is invoked
        /// locally, exactly as a right-click would - the block runs its complete open flow
        /// (client dialog + Open packet to the server, which registers the open and syncs
        /// the inventory). Because it is the block's own code path, modded containers work
        /// unmodified, and the server still enforces range, locks and land claims - a chest
        /// we may not open simply does not open.
        ///
        /// Same-type matching is what keeps this conservative: a firepit or quern touching
        /// the chest is a different block entity type and is never chained.
        /// </summary>
        private void MaybeChainOpen(BlockPos origin)
        {
            if (config?.OpenAdjacentChests != true) return;

            // This capture is one we triggered: consume the marker, do not cascade.
            if (autoOpened.Remove(origin)) return;

            var originBe = capi.World.BlockAccessor.GetBlockEntity(origin);
            if (originBe == null) return;
            var kind = originBe.GetType();

            // Contiguous same-kind cluster around the clicked chest, breadth-first.
            var toOpen = new List<BlockPos>();
            var visited = new HashSet<BlockPos> { origin.Copy() };
            var queue = new Queue<BlockPos>();
            queue.Enqueue(origin);

            while (queue.Count > 0 && toOpen.Count < MaxChainOpen)
            {
                var p = queue.Dequeue();
                foreach (var face in BlockFacing.ALLFACES)
                {
                    var np = p.AddCopy(face);
                    if (!visited.Add(np)) continue;

                    var nbe = capi.World.BlockAccessor.GetBlockEntity(np);
                    if (nbe == null || nbe.GetType() != kind) continue;
                    if (IsCapturedAt(np)) continue;

                    toOpen.Add(np);
                    queue.Enqueue(np);
                }
            }

            foreach (var p in toOpen)
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
                }
                catch (Exception e)
                {
                    autoOpened.Remove(p);
                    logger.Warning("[SymbioticInventories] Chain-open of {0} failed: {1}", p, e.Message);
                }
            }
        }

        private bool IsCapturedAt(BlockPos pos)
        {
            foreach (var cap in captured.Values)
            {
                if (cap.BlockPosition != null && cap.BlockPosition.Equals(pos)) return true;
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
