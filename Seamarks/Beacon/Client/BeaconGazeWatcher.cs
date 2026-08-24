using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon.Client;

// Plays the eye-shield animation while the local player looks near a lit lantern (OilLamp) beacon:
// poll on a tick, check each beacon with a dot-product cone, confirm line-of-sight, then edge-detect
// the on/off transition and drive the shared animation locally plus notify the server.
public sealed class BeaconGazeWatcher : IDisposable
{
    // Half-angle of the "looked at" cone, pre-converted to its cosine so the per-beacon check is a
    // single dot-product compare.
    private static readonly float GazeMaxAngleCos = MathF.Cos(45f * GameMath.DEG2RAD);

    private const int PollIntervalMs = 100;

    // The eye-shield only triggers within this fraction of the glare blip's range, so it's a "right up
    // against the lamp" reaction and doesn't fire everywhere the glare merely happens to be visible. The
    // glare blip itself still shows out to the full close range, and only the animation is gated tighter.
    private const float GazeRangeFraction = 0.25f;
    private const float GazeRangeBlocks = BeaconVisibility.CloseRangeBlocks * GazeRangeFraction;

    // Consecutive non-gazing polls required before actually dropping the shield. Close to a lantern
    // the cone/range/line-of-sight boundary all sit at a wide angle on screen, so ordinary head
    // movement crosses back and forth over it within a couple of ticks, and without this debounce that
    // reads as the arm popping on and off every 100ms or so. Only the stop is debounced, since starting
    // instantly is what keeps it feeling responsive.
    private const int StopDebounceTicks = 3;

    private readonly ICoreClientAPI capi;
    private readonly BeaconClient client;
    private readonly IClientNetworkChannel channel;
    private readonly long listenerId;

    private bool wasGazing;
    private int notGazingStreak;

    public BeaconGazeWatcher(ICoreClientAPI capi, BeaconClient client)
    {
        this.capi = capi;
        this.client = client;
        channel = capi.Network.GetChannel(SeamarksModSystem.ChannelName);
        listenerId = capi.Event.RegisterGameTickListener(CheckGaze, PollIntervalMs);
    }

    private void CheckGaze(float dt)
    {
        bool gazing = IsLookingAtLitLantern();

        if (gazing)
        {
            notGazingStreak = 0;
            if (wasGazing) return;
        }
        else
        {
            if (!wasGazing) return;
            if (++notGazingStreak < StopDebounceTicks) return;
        }

        wasGazing = gazing;
        BeaconGaze.SetGazing(capi.World.Player?.Entity, gazing);           // local: immediate feedback
        channel.SendPacket(new BeaconGazeStatePacket { Gazing = gazing }); // server: replicate to others
    }

    private bool IsLookingAtLitLantern()
    {
        var player = capi.World.Player?.Entity;
        if (player == null) return false;

        var beacons = client.Registry.All;
        if (beacons.Count == 0) return false;

        Vec3d camPos = player.CameraPos;
        Vec3f viewDir = EntityPos.GetViewVector(capi.Input.MousePitch, capi.Input.MouseYaw);

        foreach (var info in beacons.Values)
        {
            // No IsBurning() recheck against the client's own calendar clock here, for the same reason as
            // the blip renderer. This registry only holds beacons the server says are burning, and
            // re-deriving that from a drifted client clock could stop the glare on a lamp still lit.
            if (!BeaconArchetypeData.Of(info.Tier).Beam.Directional) continue;
            if (Evaluate(info, camPos, viewDir)) return true;
        }

        return false;
    }

    // A fresh Vec3d, not Vec3d.Sub(BlockPos), which mutates its receiver in place and would corrupt
    // camPos across loop iterations.
    private static Vec3d OffsetFromCamera(BlockPos pos, Vec3d camPos) =>
        new(pos.X + 0.5 - camPos.X, pos.Y + 0.5 - camPos.Y, pos.Z + 0.5 - camPos.Z);

    private bool Evaluate(BeaconInfo info, Vec3d camPos, Vec3f viewDir)
    {
        Vec3d offset = OffsetFromCamera(info.Pos, camPos);
        double distSq = offset.LengthSq();
        double dist = Math.Sqrt(distSq);

        // The shield fires when the bare-lamp glare is genuinely being thrown at the player: close enough,
        // inside the lamp's frontal arc, and with no lens fitted to focus that spill into the beam. A lamp
        // with a lens, or one seen from behind, doesn't blind. The heading is rotation-resolved, so on a
        // driven lamp the shield tracks the sweep.
        ref readonly BeaconArchetypeData archetype = ref BeaconArchetypeData.Of(info.Tier);
        float heading = BeamState.CurrentHeadingRad(info, BeamState.SweepNowHours(capi.World));
        bool glarePresent = archetype.Beam.Directional && !info.HasFocusingLens
            && BeaconVisibility.CloseGlareFactor(info, camPos, heading, dist) > 0f;

        bool withinRange = dist <= GazeRangeBlocks;
        if (dist < 1e-6)
        {
            // Standing inside the block, so it counts as looking at it and the angle is undefined.
            // Blinded only if the lamp is actually throwing glare.
            return glarePresent;
        }

        double dot = viewDir.Dot(offset) / dist;
        bool withinCone = dot >= GazeMaxAngleCos;

        // Shield the eyes when looking at a lamp (view cone) that is genuinely glaring at us, and only
        // once quite close (withinRange = a quarter of the glare's range).
        bool lookingAtGlare = withinCone && glarePresent && withinRange;

        // Only pays for the line-of-sight raycast once the cheaper checks pass.
        return lookingAtGlare && HasLineOfSight(camPos, info.Pos);
    }

    // A self-contained voxel march, not world.RayTraceForSelection. That call hands the camera position
    // straight into the engine's own shared, mutable selection-test instance, the same one used every
    // frame to work out what block the player is looking at, and stomping it from a mod tick caused
    // visible terrain popping whenever a lit lantern was in view. A plain GetBlock walk touches no
    // shared engine state.
    private bool HasLineOfSight(Vec3d camPos, BlockPos beaconPos)
    {
        double dx = beaconPos.X + 0.5 - camPos.X;
        double dy = beaconPos.Y + 0.5 - camPos.Y;
        double dz = beaconPos.Z + 0.5 - camPos.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (dist < 1e-6) return true;

        const double stepBlocks = 0.25; // fine enough to not skip past a thin wall
        int steps = (int)(dist / stepBlocks);
        var blockAccessor = capi.World.BlockAccessor;
        var samplePos = new BlockPos(beaconPos.dimension);
        for (int i = 1; i < steps; i++)
        {
            double t = i * stepBlocks / dist;
            samplePos.Set((int)Math.Floor(camPos.X + dx * t), (int)Math.Floor(camPos.Y + dy * t), (int)Math.Floor(camPos.Z + dz * t));
            if (samplePos.X == beaconPos.X && samplePos.Y == beaconPos.Y && samplePos.Z == beaconPos.Z) continue; // the lantern block itself
            if (blockAccessor.GetBlock(samplePos).SideSolid.Any) return false;
        }
        return true;
    }

    public void Dispose()
    {
        capi.Event.UnregisterGameTickListener(listenerId);
    }
}
