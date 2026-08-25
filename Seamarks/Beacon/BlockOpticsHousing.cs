using Seamarks.Beacon.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace Seamarks.Beacon;

// A housing keeps its beam signature through every form it takes: placed block, dropped stack, picked
// stack. Breaking one and putting it back down has to give the same beams back, so both drop paths copy
// the signature out of the block entity and into the stack they hand over. The panels themselves ride
// that signature, so they never drop as loose items.
public sealed class BlockOpticsHousing : Block
{
    // The item bare-code whose "type" variant (1..4) picks the lens.
    public const string LensPanelCode = "lenspanel";

    private WorldInteraction[] interactions = [];

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        interactions =
        [
            new WorldInteraction { ActionLangCode = "seamarks:blockhelp-housing-open", MouseButton = EnumMouseButton.Right },
            new WorldInteraction { ActionLangCode = "seamarks:blockhelp-housing-pickup", MouseButton = EnumMouseButton.Right, HotKeyCodes = ["ctrl", "shift"] },
        ];
    }

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        return interactions.Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
    }

    // A placed housing opens its slot panel to any wrench, and shift+ctrl+right-click lifts the whole
    // block back into the hand. It's only ever a placed block here, which is the on-the-ground state both
    // of those are for; the open and inventory sync run in the block entity.
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntityOpticsHousing be)
        {
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }

        var controls = byPlayer.Entity.Controls;
        if (controls.ShiftKey && controls.CtrlKey)
        {
            return TryPickUp(world, byPlayer, blockSel.Position, be);
        }
        if (IsWrench(byPlayer.InventoryManager.ActiveHotbarSlot?.Itemstack))
        {
            return be.OnPlayerRightClick(byPlayer, blockSel);
        }
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    private static bool IsWrench(ItemStack? stack) => stack?.Collectible?.Tool == EnumTool.Wrench;

    // Lifts the placed housing straight into the player's hands, config and all. Server-authoritative, so
    // it clears the block and hands over one stack instead of leaving the break system to drop it.
    private bool TryPickUp(IWorldAccessor world, IPlayer byPlayer, BlockPos pos, BlockEntityOpticsHousing be)
    {
        if (world.Side != EnumAppSide.Server) return true;
        if (!world.Claims.TryAccess(byPlayer, pos, EnumBlockAccessFlags.BuildOrBreak)) return true;

        var stack = new ItemStack(this);
        be.Signature.ToStack(stack);
        world.BlockAccessor.SetBlock(0, pos);
        if (!byPlayer.InventoryManager.TryGiveItemstack(stack, slotNotifyEffect: true))
        {
            world.SpawnItemEntity(stack, pos.ToVec3d().Add(0.5, 0.5, 0.5));
        }
        return true;
    }

    // A seamarks lenspanel item, with its 1..4 type pulled from the "type" variant.
    public static bool LensPanelType(ItemStack? stack, out int type)
    {
        type = 0;
        var collectible = stack?.Collectible;
        var code = collectible?.Code;
        if (code == null || code.Domain != SeamarksModSystem.Domain || code.FirstCodePart() != LensPanelCode) return false;
        return int.TryParse(collectible!.Variant["type"], out type);
    }

    public static bool IsLensPanel(ItemStack? stack) => LensPanelType(stack, out _);

    // A lens panel's glasscolor variant, or "clearoptical" for a panel with none (older stacks predating
    // the axis, or a mismatched variant lookup).
    public static string LensPanelColor(ItemStack? stack) => stack?.Collectible?.Variant["glasscolor"] ?? BeamSignature.DefaultGlassColor;

    // Mirrors lenspanel.json's own texturesByType: the housing mesh grafts a lens panel shape onto itself
    // and has to redo that color swap in code, since the grafted faces resolve off the housing block's
    // texture map, not the panel item's. Full AssetLocations, not bare paths under an assumed "game"
    // domain: Temporal's texture is this mod's own, the other four borrow vanilla stained glass.
    private static readonly Dictionary<string, AssetLocation> GlassTextureByColor = new()
    {
        ["clearoptical"] = new("game", "block/glass/plain"),
        ["redoptical"] = new("game", "block/glass/red"),
        ["greenoptical"] = new("game", "block/glass/green"),
        ["amberoptical"] = new("game", "block/glass/yellow"),
        ["temporaloptical"] = new(SeamarksModSystem.Domain, "block/glass/temporal"),
    };

    public static AssetLocation LensGlassTexture(string glasscolor) =>
        GlassTextureByColor.GetValueOrDefault(glasscolor, GlassTextureByColor[BeamSignature.DefaultGlassColor]);

    public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f)
    {
        return [StackWithSignature(world, pos)];
    }

    // Middle-click pick. Without this the picked stack is plain and the tuning is silently lost.
    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos)
    {
        return StackWithSignature(world, pos);
    }

    private ItemStack StackWithSignature(IWorldAccessor world, BlockPos pos)
    {
        var stack = new ItemStack(this);
        var be = world.BlockAccessor.GetBlockEntity(pos) as BlockEntityOpticsHousing;
        be?.Signature.ToStack(stack);
        return stack;
    }

    // The icon and held model show the same panels the placed block does: a fresh, empty housing reads as
    // just-placed (Panel_N open, the other mounts capped), and a picked-up one shows whatever it carries.
    // Built from the same mesh the block entity draws, keyed by the stack's panel sequence so each distinct
    // configuration uploads once.
    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        var signature = BeamSignature.FromStack(itemstack);
        string key = "seamarks:opticshousing-itemmesh-" + signature.PanelKey();
        MultiTextureMeshRef? meshref = ObjectCacheUtil.GetOrCreate(capi, key, () =>
        {
            MeshData? mesh = OpticsHousingMesh.Get(capi, signature);
            return mesh == null ? null : capi.Render.UploadMultiTextureMesh(mesh);
        });
        if (meshref != null) renderinfo.ModelRef = meshref;
    }

}
