using Vintagestory.API.MathTools;

namespace Seamarks.Beacon.Server;

// In-memory index of all known beacons on the server, live-updated by BlockEntityBeacon as it
// loads and unloads, and restored across restarts by BeaconServer's savegame snapshot. This class holds
// no persistence itself. Each block entity's own tree attributes stay the source of truth for its state.
// Pure storage: BeaconServer is the only thing that mutates it, and owns notifying clients of a change.
public sealed class BeaconRegistry
{
    private readonly Dictionary<BlockPos, BeaconInfo> beacons = [];

    public IReadOnlyDictionary<BlockPos, BeaconInfo> All => beacons;

    public void Upsert(BeaconInfo info) => beacons[info.Pos] = info;

    public bool Remove(BlockPos pos) => beacons.Remove(pos);

    public bool TryGet(BlockPos pos, out BeaconInfo info) => beacons.TryGetValue(pos, out info!);
}
