using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SymbioticInventories.Integration
{
    /// <summary>A nearby Eternal Stew pot, read for the window's stew station.</summary>
    public class StewInfo
    {
        public BlockPos PotPos;
        public BlockPos StovePos;      // null when no stove found under/near the pot
        public ItemStack PotIcon;
        public string StewName;
        public int Servings;
        public float Litres;
        public bool IsHot;
        public bool Burning;
        public float SimmerFrac = -1;  // 0..1 while simmering, -1 otherwise
        public float FuelSeconds;
        public List<ItemStack> Contents = new();   // pending + serving contents, capped
    }

    /// <summary>
    /// Eternal Stew integration. The pot has NO inventory at all - its stew is custom
    /// data (EsStewData) and ingredients are added by right-clicking with food in hand -
    /// so there are no slots to render. Instead the window gets a STEW STATION: a live
    /// readout (name, servings, litres, heat, simmer, stove fuel) with the actual
    /// ingredient stacks as mini icons, and clicks synthesize the real hand interactions:
    /// click with food adds it, click with a bowl serves, SHIFT-click with fuel feeds the
    /// stove. Soft dependency, resolved by short type name; absent = feature off.
    /// </summary>
    public class StewService
    {
        private ICoreClientAPI capi;
        private ILogger logger;

        private bool probed;
        private Type potType, stoveType;
        private PropertyInfo stewProp, pendingProp;
        private PropertyInfo sdStewCode, sdServings, sdLitres, sdIsHot, sdSimmer, sdSimmerReq, sdServingContents;
        private PropertyInfo stFuel, stBurning;

        public void Start(ICoreClientAPI api, ILogger log)
        {
            capi = api;
            logger = log;
        }

        private bool Resolve()
        {
            if (probed) return potType != null;
            probed = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var ty in asm.GetTypes())
                    {
                        if (ty.Name == "BlockEntityEternalStewPot") potType = ty;
                        else if (ty.Name == "BlockEntityEternalStewStove") stoveType = ty;
                    }
                }
                catch { /* unloadable assemblies: skip */ }
            }
            if (potType == null)
            {
                logger.Notification("[SymbioticInventories] Eternal Stew not installed - stew station off.");
                return false;
            }

            stewProp = AccessTools.Property(potType, "Stew");
            pendingProp = AccessTools.Property(potType, "PendingIngredients");
            var sd = stewProp?.PropertyType;
            if (sd != null)
            {
                sdStewCode = AccessTools.Property(sd, "StewCode");
                sdServings = AccessTools.Property(sd, "ServingsRemaining");
                sdLitres = AccessTools.Property(sd, "BaseLiquidLitres");
                sdIsHot = AccessTools.Property(sd, "IsHot");
                sdSimmer = AccessTools.Property(sd, "SimmerTimeSeconds");
                sdSimmerReq = AccessTools.Property(sd, "RequiredSimmerTimeSeconds");
                sdServingContents = AccessTools.Property(sd, "ServingContents");
            }
            if (stoveType != null)
            {
                stFuel = AccessTools.Property(stoveType, "FuelBufferSeconds");
                stBurning = AccessTools.Property(stoveType, "IsBurning");
            }
            if (stewProp == null)
            {
                logger.Warning("[SymbioticInventories] Eternal Stew's pot shape changed - stew station off.");
                potType = null;
            }
            return potType != null;
        }

        /// <summary>The nearest stew pot in working range, fully read, or null.</summary>
        public StewInfo FindNearby(int radius = 5)
        {
            if (!Resolve()) return null;
            var center = capi.World?.Player?.Entity?.Pos?.AsBlockPos;
            if (center == null) return null;

            var ba = capi.World.BlockAccessor;
            BlockPos best = null;
            object bestBe = null;
            int bestD = int.MaxValue;
            for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -3; dy <= 3; dy++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                var p = center.AddCopy(dx, dy, dz);
                var be = ba.GetBlockEntity(p);
                if (be == null || !potType.IsInstanceOfType(be)) continue;
                int d = dx * dx + dy * dy + dz * dz;
                if (d < bestD) { bestD = d; best = p; bestBe = be; }
            }
            if (best == null) return null;

            try
            {
                var info = new StewInfo { PotPos = best };
                var block = ba.GetBlock(best);
                info.PotIcon = block != null && block.Id != 0 ? new ItemStack(block) : null;

                var stew = stewProp.GetValue(bestBe);
                if (stew != null)
                {
                    var code = sdStewCode?.GetValue(stew) as string;
                    info.StewName = string.IsNullOrEmpty(code) ? null : Vintagestory.API.Config.Lang.Get(code);
                    info.Servings = (sdServings?.GetValue(stew) as int?) ?? 0;
                    info.Litres = (sdLitres?.GetValue(stew) as float?) ?? 0f;
                    info.IsHot = (sdIsHot?.GetValue(stew) as bool?) ?? false;

                    float sim = (sdSimmer?.GetValue(stew) as float?) ?? 0f;
                    float req = (sdSimmerReq?.GetValue(stew) as float?) ?? 0f;
                    if (req > 0 && sim < req) info.SimmerFrac = sim / req;

                    if (sdServingContents?.GetValue(stew) is ItemStack[] serving)
                    {
                        foreach (var st in serving)
                        {
                            if (st != null && info.Contents.Count < 6) info.Contents.Add(st);
                        }
                    }
                }
                if (pendingProp?.GetValue(bestBe) is List<ItemStack> pending)
                {
                    foreach (var st in pending)
                    {
                        if (st != null && info.Contents.Count < 6) info.Contents.Add(st);
                    }
                }

                // The stove sits under the pot (or one below that, for tall setups).
                for (int down = 1; down <= 2 && stoveType != null; down++)
                {
                    var sbe = ba.GetBlockEntity(best.DownCopy(down));
                    if (sbe != null && stoveType.IsInstanceOfType(sbe))
                    {
                        info.StovePos = best.DownCopy(down);
                        info.FuelSeconds = (stFuel?.GetValue(sbe) as float?) ?? 0f;
                        info.Burning = (stBurning?.GetValue(sbe) as bool?) ?? false;
                        break;
                    }
                }
                return info;
            }
            catch (Exception e)
            {
                logger.Warning("[SymbioticInventories] Reading stew pot failed: {0}", e.Message);
                return null;
            }
        }

        /// <summary>Coarse change signature so the window refreshes when the stew moves.</summary>
        public long Signature()
        {
            var s = FindNearby();
            if (s == null) return 0;
            long sig = s.PotPos.GetHashCode();
            sig = sig * 31 + s.Servings;
            sig = sig * 31 + (long)(s.Litres * 10);
            sig = sig * 31 + s.Contents.Count;
            sig = sig * 31 + (s.Burning ? 1 : 0);
            sig = sig * 31 + (long)(s.FuelSeconds / 30);
            return sig;
        }

        /// <summary>The real hand interaction against the pot (add food, serve with a
        /// bowl) or the stove (feed fuel) - the chain-open synthesis pattern; the server
        /// runs Eternal Stew's own rules.</summary>
        public void Interact(BlockPos pos)
        {
            try
            {
                var block = capi.World.BlockAccessor.GetBlock(pos);
                var sel = new BlockSelection
                {
                    Position = pos.Copy(),
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
                logger.Warning("[SymbioticInventories] Stew interact at {0} failed: {1}", pos, e.Message);
            }
        }
    }
}
