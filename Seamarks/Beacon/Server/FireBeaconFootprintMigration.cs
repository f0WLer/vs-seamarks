using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Seamarks.Beacon.Server;

// One-time cleanup for the Fire Beacon's multiblock footprint shrinking from 2x2x2 to 1x2x1. A world
// saved before the shrink still has the old footprint's filler cells sitting around every placed Fire
// Beacon, and the engine has no way to resize a multiblock after the fact (BlockBehaviorMultiblock reads
// its cell layout live off the block's current config, not off whatever a structure was actually built
// with), so without this pass those filler cells become permanent, invisible, solid obstructions that
// can't be broken through the normal multiblock-forwarding path.
//
// Runs once per beacon, off BlockEntityBeacon's own footprintMigrated flag, riding whatever chunk load
// was already going to happen, with nothing force-loaded to make it run sooner. Safe to delete this
// whole file, its call site in BlockEntityBeacon.Initialize, and the footprintMigrated tree attribute a
// couple releases after the shrink ships, once nobody is realistically jumping straight from a
// pre-shrink save.
public static class FireBeaconFootprintMigration
{
    // The old sizex/sizey/sizez=2, cposition (0,0,0) footprint filled seven cells beyond its controller.
    // Six of them are stale; (0,1,0) is the one the current 1x2x1 footprint still wants, so it stays.
    private static readonly Vec3i[] StaleOffsets =
    [
        new(1, 0, 0), new(0, 0, 1), new(1, 1, 0),
        new(1, 0, 1), new(0, 1, 1), new(1, 1, 1),
    ];

    public static void RunOnce(ICoreAPI api, BlockPos controllerPos)
    {
        foreach (var offset in StaleOffsets)
        {
            BlockPos pos = controllerPos.AddCopy(offset);
            if (api.World.BlockAccessor.GetBlock(pos) is BlockMultiblock)
            {
                api.World.BlockAccessor.SetBlock(0, pos);
            }
        }
    }
}
