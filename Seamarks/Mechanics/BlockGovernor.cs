using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent.Mechanics;

namespace Seamarks.Mechanics;

// A single mechanical node that drives the shaft above it. It connects on the top face only, so it never
// joins the network below; the block entity reads that lower network by peeking at the block under it.
// Keeping to one node is what lets the input and output turn at different speeds. Two nodes on one block
// can't, since the power engine keys nodes by position and can't tell a stacked pair apart.
public class BlockGovernor : BlockMPBase
{
    private WorldInteraction[] interactions = [];

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        interactions =
        [
            new WorldInteraction { ActionLangCode = "seamarks:blockhelp-governor-setspeed", MouseButton = EnumMouseButton.Right },
        ];
    }

    // Drives up only. Returning false for the bottom keeps the shaft below on its own network, which the
    // block entity reads without joining.
    public override bool HasMechPowerConnectorAt(IWorldAccessor world, BlockPos pos, BlockFacing face, BlockMPBase forBlock)
    {
        return face == BlockFacing.UP;
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        if (world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntityGovernor governor)
        {
            governor.OnInteract(byPlayer);
            return true;
        }
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        return interactions.Append(base.GetPlacedBlockInteractionHelp(world, selection, forPlayer));
    }

    // The one node discovers its own upward network from the behavior's Initialize, so a neighbour joining
    // from above needs nothing extra here.
    public override void DidConnectAt(IWorldAccessor world, BlockPos pos, BlockFacing face) { }
}
