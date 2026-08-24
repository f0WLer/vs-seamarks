using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Seamarks.Beacon.Server;

// Per-player tracking of which beacons sit inside that player's maximum possible range, diffed each pass
// so only the changes go over the wire. Follows the shape of Farseer's UpdateRegionsInView but much
// simpler, with no priority queue and no generation caching, because beacons are existing block entities
// reporting themselves in through BeaconRegistry and not something this has to generate.
public sealed class BeaconServer : IDisposable
{
    private sealed class TrackedPlayer
    {
        public required IServerPlayer Player { get; init; }
        public readonly HashSet<BlockPos> VisibleBeacons = [];
        public Vec3d? LastCheckedEyePos;
    }

    // Under this, a player's own movement can't plausibly change which beacons are in range by enough to
    // matter, which saves recomputing and re-diffing against every beacon on every tick.
    private const double RecheckMoveThreshold = 16.0;

    // Key for the persisted registry snapshot. Reuses BeaconFullSyncPacket verbatim as the storage
    // shape since ISaveGame.StoreData serializes via the same protobuf-net-based SerializerUtil the
    // network channel already uses.
    private const string SaveDataKey = "seamarks:beaconregistry";

    private readonly ICoreServerAPI sapi;
    private readonly IServerNetworkChannel channel;
    private readonly BeaconRegistry registry = new();
    private readonly Dictionary<string, TrackedPlayer> trackedPlayers = [];

    public BeaconRegistry Registry => registry;

    public BeaconServer(ICoreServerAPI sapi)
    {
        this.sapi = sapi;
        channel = sapi.Network.GetChannel(SeamarksModSystem.ChannelName);

        sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        sapi.Event.PlayerDisconnect += OnPlayerDisconnect;

        sapi.Event.SaveGameLoaded += OnSaveGameLoaded;

        sapi.Event.RegisterGameTickListener(OnTick, 2000);
        sapi.Event.RegisterGameTickListener(_ => BroadcastSweepSync(), SweepSyncIntervalMs);
    }

    // Slow enough to cost nothing, often enough that two machines' clocks can't drift far apart between
    // readings: at the parts-per-million a quartz clock is out by, a couple of minutes is a fraction of a
    // degree of beam.
    private const int SweepSyncIntervalMs = 120000;

    // The reference every client sets its own clock against. Skipped while the sweep tracks world time,
    // where the calendar the engine already syncs does this job.
    private void BroadcastSweepSync()
    {
        if (BeamState.SweepTracksWorldTime) return;
        channel.BroadcastPacket(new BeaconSweepSyncPacket
        {
            ServerSweepHours = BeamState.SweepNowHours(sapi.World),
        });
    }

    // Re-sends the current effective tuning to everyone online, for after a live config change so placed
    // beacons re-range and redraw their beams without anyone reconnecting. Beacons hold no per-block
    // range of their own, they read the tier tuning, so the packet is all it takes.
    // The sweep sync rides along because a config edit is exactly where the clock can have just changed
    // out from under every client, and waiting up to the sync interval to correct that would show.
    public void BroadcastTuning()
    {
        channel.BroadcastPacket(BeaconTuningPacket.FromCurrent());
        BroadcastSweepSync();
    }

    // A beacon was placed, or its state changed enough to matter (ignited, refuelled, given a plate or
    // housing). Writes straight into the registry and notifies immediately, not on the next periodic
    // tick, so a player standing still still hears about a beacon that just became visible.
    public void Upsert(BeaconInfo info)
    {
        registry.Upsert(info);
        NotifyBeaconChanged(info);
    }

    // A beacon burned out or was broken.
    public void Remove(BlockPos pos)
    {
        if (registry.Remove(pos)) NotifyBeaconRemoved(pos);
    }

    // Restores the registry from the last save, independent of which chunks happen to be loaded.
    // Without this, a beacon whose chunk hasn't loaded yet this session stays invisible to everyone until
    // someone wanders near the physical structure, which defeats the whole point of a far-render beacon.
    // Fires once at startup before any player can join, so this writes straight into the registry instead
    // of going through Upsert, which would notify a trackedPlayers set that's still empty anyway.
    private void OnSaveGameLoaded()
    {
        var snapshot = sapi.WorldManager.SaveGame.GetData<BeaconFullSyncPacket>(SaveDataKey, new BeaconFullSyncPacket());
        foreach (var info in snapshot.Beacons)
        {
            registry.Upsert(info);
        }
    }

    // Persisted eagerly on every registry mutation instead of batched onto GameWorldSave, because that
    // event doesn't reliably fire on a singleplayer "save and exit" and silently lost every beacon except
    // the one whose chunk happened to still be loaded, which survives on its own through the block
    // entity's normal chunk save. StoreData only touches an in-memory dictionary, so doing it on every
    // change costs nothing.
    private void PersistSnapshot()
    {
        var snapshot = new BeaconFullSyncPacket
        {
            Beacons = registry.All.Values.ToArray(),
        };
        sapi.WorldManager.SaveGame.StoreData(SaveDataKey, snapshot);
    }

    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        var tracked = new TrackedPlayer { Player = player };
        trackedPlayers[player.PlayerUID] = tracked;

        // Sent ahead of any beacon data so the client's first render already has the server's effective
        // tuning. A live config edit through ConfigLib re-sends it to everyone via BroadcastTuning, so
        // this join-time send and that broadcast are the only two points it goes out.
        channel.SendPacket(BeaconTuningPacket.FromCurrent(), player);

        // After the tuning, which is what tells this client whether the detached clock is even in use.
        if (!BeamState.SweepTracksWorldTime)
        {
            channel.SendPacket(new BeaconSweepSyncPacket
            {
                ServerSweepHours = BeamState.SweepNowHours(sapi.World),
            }, player);
        }

        var entity = player.Entity;
        var visible = entity != null
            ? ComputeVisibleSet(entity.Pos.XYZ.Add(entity.LocalEyePos))
            : [];

        var beacons = visible
            .Select(pos => registry.TryGet(pos, out var info) ? info : null)
            .Where(info => info != null)
            .Select(info => info!)
            .ToArray();
        channel.SendPacket(new BeaconFullSyncPacket { Beacons = beacons }, player);

        foreach (var pos in visible)
        {
            tracked.VisibleBeacons.Add(pos);
        }
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        trackedPlayers.Remove(player.PlayerUID);
    }

    // Shared by the initial join sync and the periodic recheck, so what a given eye position can see gets
    // worked out exactly one way. The registry can hold every beacon ever placed on the server, most of
    // them well out of range for any one player, so sending the lot unconditionally isn't an option.
    private HashSet<BlockPos> ComputeVisibleSet(Vec3d eyePos)
    {
        double nowHours = sapi.World.Calendar.TotalHours;

        var visible = new HashSet<BlockPos>();
        foreach (var info in registry.All.Values)
        {
            if (IsVisible(info, eyePos, nowHours))
            {
                visible.Add(info.Pos);
            }
        }
        return visible;
    }

    // Single-beacon version of the same check, used by NotifyBeaconChanged below so it doesn't have
    // to rescan every beacon just to react to one of them changing.
    private bool IsVisible(BeaconInfo info, Vec3d eyePos, double nowHours)
    {
        if (!info.IsBurning(nowHours)) return false;

        // The directional cone isn't checked here. It's a client-side render decision, not a broadcast
        // filter. Applying it here as well would add RecheckMoveThreshold lag every time someone crossed
        // the cone edge, plus add and remove packet churn as they orbit, and buys nothing, since the
        // physical lighthouse is visible regardless.
        float maxRange = BeaconVisibility.MaxRange(info, sapi.World.SeaLevel);
        return info.Pos.DistanceTo(eyePos) <= maxRange;
    }

    private void OnTick(float dt)
    {
        foreach (var tracked in trackedPlayers.Values)
        {
            UpdateVisibleSetForPlayer(tracked);
        }
    }

    private void UpdateVisibleSetForPlayer(TrackedPlayer tracked)
    {
        var entity = tracked.Player.Entity;
        if (entity == null) return;

        var eyePos = entity.Pos.XYZ.Add(entity.LocalEyePos);

        if (tracked.LastCheckedEyePos != null && tracked.LastCheckedEyePos.DistanceTo(eyePos) < RecheckMoveThreshold)
        {
            return;
        }
        tracked.LastCheckedEyePos = eyePos.Clone();

        var nowVisible = ComputeVisibleSet(eyePos);

        var newlyVisible = nowVisible.Where(p => !tracked.VisibleBeacons.Contains(p)).ToArray();
        var noLongerVisible = tracked.VisibleBeacons.Where(p => !nowVisible.Contains(p)).ToArray();

        if (newlyVisible.Length == 0 && noLongerVisible.Length == 0)
        {
            return;
        }

        foreach (var pos in newlyVisible)
        {
            if (registry.TryGet(pos, out var info))
            {
                channel.SendPacket(info, tracked.Player);
            }
            tracked.VisibleBeacons.Add(pos);
        }

        if (noLongerVisible.Length > 0)
        {
            channel.SendPacket(new BeaconRemovePacket
            {
                Positions = [.. noLongerVisible.Select(BeaconPosRef.From)],
            }, tracked.Player);

            foreach (var pos in noLongerVisible)
            {
                tracked.VisibleBeacons.Remove(pos);
            }
        }
    }

    // A beacon changed state, whether placed, ignited, refuelled, given a plate or housing, or toggled
    // mechanical. Push it out straight away instead of waiting on the periodic tick, which only re-runs
    // for a player once they've moved past RecheckMoveThreshold. A player standing still would otherwise
    // never hear about a beacon that became visible because it changed, say by being lit, instead of
    // because they moved. Rechecks every tracked player's visibility fresh, so anyone newly in range gets
    // an Add and anyone newly out of it, from an extinguish say, gets a Remove.
    private void NotifyBeaconChanged(BeaconInfo info)
    {
        PersistSnapshot();

        double nowHours = sapi.World.Calendar.TotalHours;
        // Built once, not per recipient: every player being dropped from this beacon's visible set gets
        // the same remove packet.
        var removePacket = new BeaconRemovePacket { Positions = [BeaconPosRef.From(info.Pos)] };

        foreach (var tracked in trackedPlayers.Values)
        {
            var entity = tracked.Player.Entity;
            if (entity == null) continue;

            bool nowVisible = IsVisible(info, entity.Pos.XYZ.Add(entity.LocalEyePos), nowHours);

            if (nowVisible)
            {
                channel.SendPacket(info, tracked.Player);
                tracked.VisibleBeacons.Add(info.Pos);
            }
            else if (tracked.VisibleBeacons.Remove(info.Pos))
            {
                channel.SendPacket(removePacket, tracked.Player);
            }
        }
    }

    private void NotifyBeaconRemoved(BlockPos pos)
    {
        PersistSnapshot();

        var removePacket = new BeaconRemovePacket { Positions = [BeaconPosRef.From(pos)] };
        foreach (var tracked in trackedPlayers.Values)
        {
            if (tracked.VisibleBeacons.Remove(pos))
            {
                channel.SendPacket(removePacket, tracked.Player);
            }
        }
    }

    public void Dispose()
    {
        sapi.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
        sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
        sapi.Event.SaveGameLoaded -= OnSaveGameLoaded;
    }
}
