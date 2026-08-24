using Vintagestory.API.Common;

namespace Seamarks.Beacon;

// Populates its slice of the flat BeaconInfo snapshot. Far beacons have no block entity on the
// client, so everything the renderer needs has to live on the DTO and not be read off behaviors.
public interface IBeaconInfoContributor
{
    void Contribute(BeaconInfo info);
}

// Reacts to the beacon's lit and unlit transitions, to start or stop burn-gated tick work or audio.
public interface IBeaconBurnListener
{
    void OnBurnStateChanged(bool burning);
}

// Shared base for the beacon's capability behaviors: typed access to the core block entity plus a
// burn-gated tick-listener helper (the 0 id doubles as "not registered"). Listeners are registered
// through the block entity, so the engine also disposes them on unload/remove.
public abstract class BEBehaviorBeacon : BlockEntityBehavior
{
    protected BEBehaviorBeacon(BlockEntity blockentity) : base(blockentity) { }

    protected BlockEntityBeacon Beacon => (BlockEntityBeacon)Blockentity;

    protected void ReconcileListener(ref long id, bool shouldRun, Action<float> callback) =>
        ReconcileListener(Blockentity, ref id, shouldRun, callback);

    // Shared with BlockEntityBeacon, which reconciles its own burn-expiry listener the same way but isn't
    // a BEBehaviorBeacon itself. The 0 id doubles as the "not registered" sentinel.
    public static void ReconcileListener(BlockEntity blockEntity, ref long id, bool shouldRun, Action<float> callback)
    {
        if (shouldRun && id == 0)
        {
            id = blockEntity.RegisterGameTickListener(callback, 1000);
        }
        else if (!shouldRun && id != 0)
        {
            blockEntity.UnregisterGameTickListener(id);
            id = 0;
        }
    }
}
