using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon.Client;

// Client-side mirror of the server's BeaconRegistry, but only for beacons the server has decided
// are inside this player's max-possible-range set. Fed purely by network packets, never by chunk loading.
//
// It also owns the ignition fade lifecycle: a beacon ramps its visuals in when first lit and out when
// removed, instead of popping. That can't be a pure BeamState function like rotation is, because fade-out
// needs the moment the beacon left, which isn't in the network DTO, so the registry hangs onto a
// just-removed beacon for one short fade window. The fade runs in real time, not calendar hours, since a
// cosmetic transition shouldn't stretch or snap with the world's day length, unlike rotation phase which is
// calendar-anchored so clients agree. Logic consumers that must react instantly (the gaze eye-shield) read
// All (live only); the visual renderers read RenderBeacons (live fading-in + retained fading-out).
public sealed class BeaconClientRegistry
{
    // Real-time fade duration (ms), from the live-tunable BeamTuning.IgnitionFadeSeconds. Floored at 1ms
    // so the divisions below never blow up when a tuner sets the fade to zero (which means "no fade").
    private static long IgnitionFadeMs => (long)MathF.Max(1f, BeamTuning.IgnitionFadeSeconds * 1000f);

    private readonly ICoreClientAPI capi;
    private readonly Dictionary<BlockPos, BeaconInfo> beacons = [];
    private readonly Dictionary<BlockPos, long> igniteMs = [];         // first-seen time, drives fade-in
    private readonly Dictionary<BlockPos, FadingBeacon> fadingOut = []; // retained briefly for fade-out
    private readonly List<RenderBeacon> renderList = [];               // reused each call, no per-frame alloc
    private readonly List<BlockPos> expiredScratch = [];               // scratch for pruning finished fade-outs

    private readonly record struct FadingBeacon(BeaconInfo Info, long ExtinguishMs);
    public readonly record struct RenderBeacon(BeaconInfo Info, float Fade);

    // Live beacons only, for logic consumers like the gaze check that mustn't react to one on its way out.
    public IReadOnlyDictionary<BlockPos, BeaconInfo> All => beacons;

    public BeaconClientRegistry(ICoreClientAPI capi)
    {
        this.capi = capi;
        var channel = capi.Network.GetChannel(SeamarksModSystem.ChannelName);
        channel.SetMessageHandler<BeaconInfo>(OnAdd);
        channel.SetMessageHandler<BeaconRemovePacket>(OnRemove);
        channel.SetMessageHandler<BeaconFullSyncPacket>(OnFullSync);
        channel.SetMessageHandler<BeaconTuningPacket>(OnTuning);
        channel.SetMessageHandler<BeaconSweepSyncPacket>(OnSweepSync);
    }

    private void OnSweepSync(BeaconSweepSyncPacket packet)
    {
        BeamState.SyncDetachedClock(capi.World, packet.ServerSweepHours);
    }

    private void OnTuning(BeaconTuningPacket packet)
    {
        packet.ApplyToArchetypes();
        // The synced beam lengths live on the archetype now, so push them onto the live shapes the
        // renderer actually reads.
        BeamTuning.ReseedLengths();
    }

    private void OnAdd(BeaconInfo info)
    {
        // A fresh ignition (first time we've seen this pos lit) starts a fade-in; a re-add of a beacon
        // already present is just a state refresh and keeps its existing fade so it doesn't restart.
        if (!beacons.ContainsKey(info.Pos)) igniteMs[info.Pos] = capi.World.ElapsedMilliseconds;
        beacons[info.Pos] = info;
        fadingOut.Remove(info.Pos); // re-lit before its fade-out finished
    }

    private void OnRemove(BeaconRemovePacket packet)
    {
        long now = capi.World.ElapsedMilliseconds;
        foreach (var posRef in packet.Positions)
        {
            var pos = posRef.ToBlockPos();
            if (beacons.TryGetValue(pos, out var info))
            {
                fadingOut[pos] = new FadingBeacon(info, now);
                beacons.Remove(pos);
                igniteMs.Remove(pos);
            }
        }
    }

    private void OnFullSync(BeaconFullSyncPacket packet)
    {
        // A bulk resync, from a join or a long teleport, shows everything already lit at full with no mass
        // fade-in, by anchoring the ignite time a fade-window in the past so each fade reads as complete.
        beacons.Clear();
        igniteMs.Clear();
        fadingOut.Clear();
        long past = capi.World.ElapsedMilliseconds - IgnitionFadeMs;
        foreach (var info in packet.Beacons)
        {
            beacons[info.Pos] = info;
            igniteMs[info.Pos] = past;
        }
    }

    // Live beacons (fading in) plus recently-removed ones (fading out), each tagged with its 0..1 fade
    // multiplier, for the visual renderers to scale intensity/alpha by. Rebuilt into a reused list each
    // call (small N, single-threaded). Finished fade-outs are pruned here, so this must be called
    // regularly, which the render frame and the light and dust ticks all do.
    public IReadOnlyList<RenderBeacon> RenderBeacons()
    {
        long now = capi.World.ElapsedMilliseconds;
        renderList.Clear();

        foreach (var (pos, info) in beacons)
        {
            float fade = igniteMs.TryGetValue(pos, out long t)
                ? GameMath.Clamp((now - t) / (float)IgnitionFadeMs, 0f, 1f) : 1f;
            renderList.Add(new RenderBeacon(info, fade));
        }

        expiredScratch.Clear();
        foreach (var (pos, fb) in fadingOut)
        {
            float fade = 1f - (now - fb.ExtinguishMs) / (float)IgnitionFadeMs;
            if (fade <= 0f) { expiredScratch.Add(pos); continue; }
            renderList.Add(new RenderBeacon(fb.Info, fade));
        }
        foreach (var pos in expiredScratch) fadingOut.Remove(pos);

        return renderList;
    }
}
