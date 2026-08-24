using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seamarks.Beacon;

// A plain Block and not a BlockMPBase. Shaft sensing for the OilLamp's rotation pulse reads the block
// below directly. Registering as a real mechanical-power consumer would hand the block to the engine's
// own mech renderer, which redraws it at 1x scale and ignores our shape's scale-up.
public class BlockBeacon : Block
{
    private const float LitresPerFuelClick = 1f;

    // The stacks the interaction help draws beside its hints. Built once here because
    // GetPlacedBlockInteractionHelp runs every frame the player has the block under the crosshair, and
    // both of these are fixed for the life of the world.
    private ItemStack[]? torchStack;
    private ItemStack[]? housingStack;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);

        var torch = api.World.GetBlock(new AssetLocation("game", "torch-basic-lit-up"));
        if (torch != null) torchStack = [new ItemStack(torch)];

        var housing = api.World.GetBlock(new AssetLocation(SeamarksModSystem.Domain, "opticshousing"));
        if (housing != null) housingStack = [new ItemStack(housing)];
    }

    // Each branch below must return false for anything it doesn't recognize, not just true
    // unconditionally, because returning true tells the engine we consumed the interaction, which blocks
    // the held item's own interaction (e.g. a bucket's pour/fill) from running at all.
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntityBeacon beacon)
        {
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        ref readonly BeaconArchetypeData archetype = ref BeaconArchetypeData.Of(beacon.Tier);
        var slot = byPlayer.InventoryManager.ActiveHotbarSlot;
        var heldStack = slot?.Itemstack;

        // Modifier combos are explicit and win over whatever's incidentally in hand. Shift+Ctrl
        // must be checked before plain Shift since it's a subset of it.
        var controls = byPlayer.Entity.Controls;
        if (controls.ShiftKey && controls.CtrlKey)
        {
            return TryDetachHousing(world, byPlayer, beacon);
        }
        if (controls.ShiftKey)
        {
            return TryRemovePlate(world, byPlayer, beacon);
        }

        if (heldStack?.Collectible is BlockLiquidContainerBase liquidContainer)
        {
            return TryFuelFromContainer(world, byPlayer, beacon, archetype, slot!, liquidContainer);
        }
        // !IsIgniter keeps torches and firestarters out. A torch is technically combustible too, but its
        // job here is to light the beacon (below), not be burned as fuel, and this branch runs first.
        // HeldItemFuelHours covers both the fire's general appetite for combustibles and the specific
        // items an admin has listed for a tier that otherwise takes none.
        if (heldStack != null && !IsIgniter(heldStack) && archetype.HeldItemFuelHours(heldStack) > 0f)
        {
            return TryFuelFromHeldItem(world, byPlayer, beacon, archetype, slot!);
        }
        if (heldStack != null && IsOpticsHousing(heldStack))
        {
            return TryAttachHousing(world, byPlayer, beacon, slot!);
        }
        if (heldStack != null && TryGetPlateMetal(heldStack, out var metal))
        {
            return TryAddPlate(world, byPlayer, beacon, slot!, metal);
        }
        // Fire ignites on a plain right-click of a torch/firestarter, not vanilla's usual Shift+hold,
        // since that path can't reach the 2x2x2 multiblock's filler cells, only the anchor. Routing
        // ignition through this interaction instead lights it identically from any of the 8 cells.
        if (heldStack != null && beacon.Tier == BeaconTier.Fire && IsIgniter(heldStack))
        {
            return TryIgniteWithTorch(world, byPlayer, beacon);
        }
        if (heldStack == null)
        {
            return ToggleIgnition(world, byPlayer, beacon);
        }

        return false;
    }

    private static bool IsIgniter(ItemStack stack)
    {
        var collectible = stack.Collectible;
        if (collectible is ItemFirestarter) return true;
        return collectible is Block block && block.HasBehavior<BlockBehaviorCanIgnite>();
    }

    private static bool TryIgniteWithTorch(IWorldAccessor world, IPlayer byPlayer, BlockEntityBeacon beacon)
    {
        if (world.Side != EnumAppSide.Server) return true;
        if (beacon.IsBurning) return true;

        if (beacon.TryIgnite())
        {
            Notify(byPlayer, LitMessage(beacon));
        }
        else
        {
            Notify(byPlayer, Lang.Get("seamarks:notify-no-fuel"));
        }
        return true;
    }

    // Fuelling and igniting stay separate actions, so topping up never lights the beacon.
    private static bool TryFuelFromContainer(IWorldAccessor world, IPlayer byPlayer, BlockEntityBeacon beacon, in BeaconArchetypeData archetype, ItemSlot slot, BlockLiquidContainerBase container)
    {
        var heldStack = slot.Itemstack!;
        var content = container.GetContent(heldStack);
        if (content == null || !archetype.AcceptsFuel(content))
        {
            return false; // not accepted fuel, so the container's own interaction proceeds
        }
        if (world.Side != EnumAppSide.Server) return true;

        // This oil's own per-litre rate, which the config can override for specific codes. The tank's cap is
        // still in hours, so a richer oil just fills it in fewer litres.
        float hoursPerLitre = archetype.FuelHoursPerLitreFor(content);

        // Only draw as much oil as still fits, so topping off a nearly-full lamp doesn't waste a
        // whole litre it can't hold.
        float remainingCapacityHours = archetype.MaxFuelHours - beacon.RemainingFuelHours;
        if (remainingCapacityHours <= 0.0001f || hoursPerLitre <= 0f)
        {
            Notify(byPlayer, Lang.Get("seamarks:notify-fuel-full"));
            return true;
        }
        float litresToTake = MathF.Min(LitresPerFuelClick, remainingCapacityHours / hoursPerLitre);

        float litresBefore = container.GetCurrentLitres(heldStack);
        container.TryTakeLiquid(heldStack, litresToTake);
        float litresTaken = litresBefore - container.GetCurrentLitres(heldStack);
        if (litresTaken > 0)
        {
            beacon.AddFuel(hoursPerLitre * litresTaken, litresTaken);
            slot.MarkDirty();
            Notify(byPlayer, Lang.Get("seamarks:notify-fuel-added-liquid", litresTaken, beacon.RemainingFuelHours));
        }
        return true;
    }

    private static bool TryFuelFromHeldItem(IWorldAccessor world, IPlayer byPlayer, BlockEntityBeacon beacon, in BeaconArchetypeData archetype, ItemSlot slot)
    {
        float fuelHours = archetype.HeldItemFuelHours(slot.Itemstack);
        if (fuelHours <= 0f) return false; // no burn value, so the item's own interaction proceeds
        if (world.Side != EnumAppSide.Server) return true;

        // Solid fuel is discrete items, so an item that would overflow the pool is rejected outright
        // instead of being partly consumed or quietly rounded down.
        if (beacon.RemainingFuelHours + fuelHours > archetype.MaxFuelHours + 0.0001f)
        {
            Notify(byPlayer, Lang.Get("seamarks:notify-fuel-no-space", archetype.MaxFuelHours));
            return true;
        }

        beacon.AddFuel(fuelHours);
        slot.TakeOut(1);
        slot.MarkDirty();
        string hint = Lang.Get(beacon.Tier == BeaconTier.Fire ? "seamarks:hint-ignite-fire" : "seamarks:hint-ignite-oillamp");
        Notify(byPlayer, Lang.Get("seamarks:notify-fuel-added-solid", beacon.RemainingFuelHours, hint));
        return true;
    }

    private static bool TryAttachHousing(IWorldAccessor world, IPlayer byPlayer, BlockEntityBeacon beacon, ItemSlot slot)
    {
        var reflector = beacon.GetBehavior<BEBehaviorBeaconReflector>();
        if (reflector == null) return false;
        if (world.Side != EnumAppSide.Server) return true;

        // The housing brings its tuning with it, so the beacon takes on whatever beams this one was set to.
        BeamSignature fitted = BeamSignature.FromStack(slot.Itemstack);
        if (reflector.TryAttachHousing(fitted))
        {
            slot.TakeOut(1);
            slot.MarkDirty();
            Notify(byPlayer, Lang.Get("seamarks:notify-housing-attached", fitted.Describe()));
        }
        else
        {
            Notify(byPlayer, Lang.Get("seamarks:notify-housing-already-attached"));
        }
        return true;
    }

    private static bool TryDetachHousing(IWorldAccessor world, IPlayer byPlayer, BlockEntityBeacon beacon)
    {
        var reflector = beacon.GetBehavior<BEBehaviorBeaconReflector>();
        if (reflector == null) return false;
        if (world.Side != EnumAppSide.Server) return true;

        if (reflector.TryDetachHousing(out BeamSignature? detached))
        {
            var stack = new ItemStack(world.GetBlock(new AssetLocation(SeamarksModSystem.Domain, "opticshousing")));
            detached!.ToStack(stack); // hand the tuning back out, or refitting it would come up plain
            GiveOrDrop(world, byPlayer, stack, beacon.Pos);
            Notify(byPlayer, Lang.Get("seamarks:notify-housing-detached"));
        }
        else
        {
            Notify(byPlayer, Lang.Get("seamarks:notify-housing-not-attached"));
        }
        return true;
    }

    private static bool TryAddPlate(IWorldAccessor world, IPlayer byPlayer, BlockEntityBeacon beacon, ItemSlot slot, PlateMetal metal)
    {
        var reflector = beacon.GetBehavior<BEBehaviorBeaconReflector>();
        if (reflector == null) return false;
        if (world.Side != EnumAppSide.Server) return true;

        if (reflector.TryAddPlate(metal, out var result))
        {
            slot.TakeOut(1);
            slot.MarkDirty();
            Notify(byPlayer, Lang.Get("seamarks:notify-plate-added", PlateMetalName(metal), reflector.PlateCount, BeaconArchetypeData.Of(beacon.Tier).MaxPlates));
        }
        else if (result == PlateAddResult.MetalMismatch)
        {
            Notify(byPlayer, Lang.Get("seamarks:notify-plate-mismatch", PlateMetalName(reflector.PlateMetal)));
        }
        else
        {
            Notify(byPlayer, Lang.Get("seamarks:notify-plate-max"));
        }
        return true;
    }

    private static bool TryRemovePlate(IWorldAccessor world, IPlayer byPlayer, BlockEntityBeacon beacon)
    {
        var reflector = beacon.GetBehavior<BEBehaviorBeaconReflector>();
        if (reflector == null) return false;
        if (world.Side != EnumAppSide.Server) return true;

        if (reflector.TryRemovePlate(out var removedMetal))
        {
            var plateItem = world.GetItem(new AssetLocation("game", $"metalplate-{PlateMetalCode(removedMetal)}"));
            GiveOrDrop(world, byPlayer, new ItemStack(plateItem), beacon.Pos);
            Notify(byPlayer, Lang.Get("seamarks:notify-plate-removed", PlateMetalName(removedMetal), reflector.PlateCount));
        }
        else
        {
            Notify(byPlayer, Lang.Get("seamarks:notify-plate-none"));
        }
        return true;
    }

    private static void GiveOrDrop(IWorldAccessor world, IPlayer byPlayer, ItemStack stack, BlockPos pos)
    {
        if (!byPlayer.InventoryManager.TryGiveItemstack(stack, slotNotifyEffect: true))
        {
            world.SpawnItemEntity(stack, pos);
        }
    }

    private static bool ToggleIgnition(IWorldAccessor world, IPlayer byPlayer, BlockEntityBeacon beacon)
    {
        if (world.Side != EnumAppSide.Server) return true;

        if (beacon.IsBurning)
        {
            beacon.Extinguish();
            Notify(byPlayer, Lang.Get("seamarks:notify-extinguished"));
        }
        else if (beacon.Tier == BeaconTier.Fire)
        {
            Notify(byPlayer, Lang.Get("seamarks:notify-needs-torch"));
        }
        else if (beacon.TryIgnite())
        {
            Notify(byPlayer, LitMessage(beacon));
        }
        else
        {
            Notify(byPlayer, Lang.Get("seamarks:notify-no-fuel"));
        }
        return true;
    }

    // A fuelled beacon reports how long it'll burn; a fuel-free one just reports it's lit, since there's
    // no countdown to quote.
    private static string LitMessage(BlockEntityBeacon beacon)
    {
        return beacon.RequiresFuel
            ? Lang.Get("seamarks:notify-lit", beacon.RemainingFuelHours)
            : Lang.Get("seamarks:notify-lit-nofuel");
    }

    // Full domain and path match, not just the bare path. Another mod's item that happens to share the
    // name shouldn't be silently accepted here.
    private static bool IsOpticsHousing(ItemStack stack)
    {
        var code = stack.Collectible?.Code;
        return code != null && code.Domain == SeamarksModSystem.Domain && code.Path == "opticshousing";
    }

    private static bool TryGetPlateMetal(ItemStack stack, out PlateMetal metal)
    {
        metal = default;
        if (stack.Collectible is not ItemMetalPlate) return false;

        switch (stack.Collectible.Variant["metal"])
        {
            case "gold": metal = PlateMetal.Gold; return true;
            case "silver": metal = PlateMetal.Silver; return true;
            case "electrum": metal = PlateMetal.Electrum; return true;
            default: return false;
        }
    }

    private static string PlateMetalCode(PlateMetal metal) => metal switch
    {
        PlateMetal.Gold => "gold",
        PlateMetal.Silver => "silver",
        PlateMetal.Electrum => "electrum",
        _ => "gold",
    };

    private static string PlateMetalName(PlateMetal metal) => Lang.Get("material-" + PlateMetalCode(metal));

    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer? byPlayer, float dropQuantityMultiplier = 1f)
    {
        var drops = new List<ItemStack>(base.GetDrops(world, pos, byPlayer, dropQuantityMultiplier) ?? []);

        if (world.BlockAccessor.GetBlockEntity(pos) is BlockEntityBeacon beacon
            && beacon.GetBehavior<BEBehaviorBeaconReflector>() is { } reflector)
        {
            // Breaking the beacon has to return the housing's tuning too, the same way detaching it does.
            if (reflector.Housing is { } housing)
            {
                var stack = new ItemStack(world.GetBlock(new AssetLocation(SeamarksModSystem.Domain, "opticshousing")));
                housing.ToStack(stack);
                drops.Add(stack);
            }
            if (reflector.PlateCount > 0)
            {
                var plateItem = world.GetItem(new AssetLocation("game", $"metalplate-{PlateMetalCode(reflector.PlateMetal)}"));
                drops.Add(new ItemStack(plateItem, reflector.PlateCount));
            }
        }

        return [.. drops];
    }

    // A lit beacon shows its live range instead of the static description, since plates and the housing
    // move it and the static text would go stale.
    public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
    {
        if (world.BlockAccessor.GetBlockEntity(pos) is not BlockEntityBeacon beacon)
        {
            return base.GetPlacedBlockInfo(world, pos, forPlayer);
        }

        ref readonly BeaconArchetypeData archetype = ref BeaconArchetypeData.Of(beacon.Tier);
        var sb = new StringBuilder();

        if (beacon.IsBurning)
        {
            float range = beacon.MaxRangeAtSeaLevel(world.SeaLevel);
            sb.AppendLine(Lang.Get("seamarks:blockinfo-visible-range", (int)range));
        }
        else
        {
            sb.AppendLine(Lang.Get($"seamarks:blockdesc-beacon-{archetype.VariantCode}-unlit"));
        }

        if (beacon.IsEverburning)
        {
            sb.AppendLine(Lang.Get("seamarks:blockinfo-everburning"));
        }
        else if (archetype.RequiresFuel)
        {
            sb.AppendLine(FuelDescriptionLine(beacon, archetype));
        }
        else
        {
            sb.AppendLine(Lang.Get("seamarks:blockinfo-fuel-notrequired"));
        }
        return sb.ToString();
    }

    private static string FuelDescriptionLine(BlockEntityBeacon beacon, in BeaconArchetypeData archetype)
    {
        return archetype.FuelHoursPerLitre > 0f
            ? Lang.Get("seamarks:blockinfo-fuel-liquid", beacon.RemainingFuelLitres, beacon.RemainingFuelHours)
            : Lang.Get("seamarks:blockinfo-fuel-solid", beacon.RemainingFuelHours);
    }

    // Every hint here reads the beacon's current state, so it always describes what the next click will
    // actually do. Both tiers light on a plain right-click and not vanilla's usual Shift+hold, which is
    // the part players don't otherwise discover.
    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        var baseHelp = base.GetPlacedBlockInteractionHelp(world, selection, forPlayer) ?? [];
        if (world.BlockAccessor.GetBlockEntity(selection.Position) is not BlockEntityBeacon beacon)
        {
            return baseHelp;
        }

        // The fire lights only from a torch, and a torch does nothing to one already alight, so the two
        // swap over the same way the lamp's pair does. Putting it out is the same bare-handed click.
        //
        // No RequireFreeHand on the bare-handed hints here or below. Its only effect anywhere in the
        // engine is to draw an empty item slot beside the hint, which reads as "put something here" on an
        // action that wants the opposite; it gates nothing, so dropping it leaves just the button icon.
        if (beacon.Tier == BeaconTier.Fire)
        {
            return [.. baseHelp, beacon.IsBurning
                ? new WorldInteraction
                {
                    ActionLangCode = "seamarks:blockhelp-fire-extinguish",
                    MouseButton = EnumMouseButton.Right,
                }
                : new WorldInteraction
                {
                    ActionLangCode = "seamarks:blockhelp-light-fire",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = torchStack,
                }];
        }

        // One hint always shown, reading whichever way the next click will actually go, so the player is
        // never told to light a lamp that's already burning.
        var help = new List<WorldInteraction>(baseHelp)
        {
            new WorldInteraction
            {
                ActionLangCode = beacon.IsBurning
                    ? "seamarks:blockhelp-beacon-extinguish"
                    : "seamarks:blockhelp-beacon-ignite",
                MouseButton = EnumMouseButton.Right,
            },
        };

        // Fitting and pulling the housing are opposite states, so only the one that applies is offered.
        if (beacon.Housing == null)
        {
            if (housingStack != null)
            {
                help.Add(new WorldInteraction
                {
                    ActionLangCode = "seamarks:blockhelp-beacon-attachhousing",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = housingStack,
                });
            }
        }
        else
        {
            help.Add(new WorldInteraction
            {
                ActionLangCode = "seamarks:blockhelp-beacon-detachhousing",
                MouseButton = EnumMouseButton.Right,
                HotKeyCodes = ["ctrl", "shift"],
            });
        }

        return [.. help];
    }

    public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
    {
        bool placed = base.TryPlaceBlock(world, byPlayer, itemstack, blockSel, ref failureCode);
        if (placed && world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityBeacon beacon)
        {
            beacon.GetBehavior<BEBehaviorBeaconBeam>()?.SetHeadingFacingPlayer(byPlayer);
        }
        return placed;
    }

    private static void Notify(IPlayer? byPlayer, string message)
    {
        if (byPlayer is IServerPlayer serverPlayer)
        {
            serverPlayer.SendMessage(GlobalConstants.CurrentChatGroup, message, EnumChatType.Notification);
        }
    }
}
