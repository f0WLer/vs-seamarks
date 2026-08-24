using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon.Client;

// The "sweeping illumination": two real dynamic point lights per beam of each nearby burning directional
// beacon that cast the lamp's local spill in that beam's direction and sweep with it. A single IPointLight
// is only a sphere and can't project a shaft or a pool along the beam, so instead of faking that, each
// beam gets two lights standing in for the light the lamp itself throws locally: one straight out along
// that beam's heading at lamp height, one pitched down toward the floor so the ground in front brightens
// as the beam points there. Each beam's pair takes that beam's own color. They layer on top of the
// block's own static light, adding the motion the static grid light can't.
//
// A multi-beam beacon therefore uses BeamCount * 2 dynamic lights (up to 6); the engine only renders up
// to the MaxDynamicLights graphics setting, so a busy scene silently drops some, the same way it does
// with the block's own lights.
//
// Client-side and owned here instead of by the beam behavior, because a far beacon in the client registry
// may have no block entity loaded at all, and this manager already holds the whole nearby set each tick.
// The engine does ZERO automatic cleanup of point lights, so every Add is matched by a Remove here: a
// beacon's lights live in its array (rebuilt if its beam count changes), out-of-range/extinguished/gone
// beacons are pruned each tick, and RemoveAll runs on dispose.
public sealed class BeaconPointLights : IDisposable
{
    private const float MinNightFactor = 0.05f; // below this it's daytime, so the light goes off entirely
    private const int LightsPerBeam = 2;        // forward (out along the heading) + downward (toward the floor)

    private readonly SeamarksModSystem modSystem;
    private readonly ICoreClientAPI capi;
    private readonly long listenerId;

    private sealed class BeaconPointLight : IPointLight
    {
        public Vec3f Color { get; set; } = new Vec3f();
        public Vec3d Pos { get; set; } = new Vec3d();
    }

    private readonly Dictionary<BlockPos, BeaconPointLight[]> lights = []; // BeamCount forward+down pairs per beacon
    private readonly HashSet<BlockPos> keep = [];   // beacons refreshed this tick
    private readonly List<BlockPos> stale = [];     // scratch for pruning; reused each tick

    public BeaconPointLights(SeamarksModSystem modSystem, ICoreClientAPI capi)
    {
        this.modSystem = modSystem;
        this.capi = capi;
        listenerId = capi.Event.RegisterGameTickListener(OnTick, 20);
    }

    private void OnTick(float dt)
    {
        var registry = modSystem.Client?.Registry;
        var player = capi.World.Player?.Entity;
        if (registry == null || player == null) { RemoveAll(); return; }

        // Dynamic lights turned off in the graphics settings. The engine would render none anyway, so drop
        // ours and skip the work. Re-checked every tick, so raising the setting brings them back on its own.
        if (!BeamQuality.DynamicLightsEnabled) { RemoveAll(); return; }

        Vec3d camPos = player.CameraPos;
        double now = BeamState.SweepNowHours(capi.World);
        float night = GameMath.Clamp(1f - capi.World.Calendar.DayLightStrength, 0f, 1f);

        keep.Clear();
        if (night > MinNightFactor)
        {
            foreach (var rb in registry.RenderBeacons())
            {
                BeaconInfo info = rb.Info;
                ref readonly BeaconArchetypeData archetype = ref BeaconArchetypeData.Of(info.Tier);
                if (!archetype.Beam.Directional) continue; // only lit-beam tiers get a sweeping light

                double dx = info.Pos.X + 0.5 - camPos.X;
                double dy = info.Pos.Y + archetype.BlipOffsetY - camPos.Y;
                double dz = info.Pos.Z + 0.5 - camPos.Z;
                if (dx * dx + dy * dy + dz * dz > BeamTuning.PointLightRange * BeamTuning.PointLightRange) continue;

                float heading = BeamState.CurrentHeadingRad(info, now);
                // Fade scales brightness, so the lit spot ramps up on ignite and dims out on extinguish;
                // the beacon stays in the render set (and thus in keep) until its fade-out completes.
                UpdateLights(info, in archetype, heading, night * rb.Fade);
                keep.Add(info.Pos);
            }
        }

        PruneExcept();
    }

    // brightnessFactor is night * ignition-fade, so the lights ramp with the beam and vanish by day.
    // baseHeading is beam 0's heading; the other beams are evenly spaced around it, matching the mesh.
    private void UpdateLights(BeaconInfo info, in BeaconArchetypeData archetype, float baseHeading, float brightnessFactor)
    {
        int count = BeamState.ClampedBeamCount(info);
        int needed = count * LightsPerBeam;

        // Rebuild the light set on first sight or when the beam count changed, so the array always has
        // exactly one forward+down pair per beam (no orphaned lights left running).
        if (!lights.TryGetValue(info.Pos, out var beaconLights) || beaconLights.Length != needed)
        {
            if (beaconLights != null) foreach (var light in beaconLights) capi.Render.RemovePointLight(light);
            beaconLights = new BeaconPointLight[needed];
            for (int i = 0; i < needed; i++) { beaconLights[i] = new BeaconPointLight(); capi.Render.AddPointLight(beaconLights[i]); }
            lights[info.Pos] = beaconLights;
        }

        float b = BeamTuning.PointLightBrightness * brightnessFactor;
        float offset = BeamTuning.PointLightOffset;
        float pitch = BeamTuning.PointLightDownPitch * GameMath.DEG2RAD;
        float horiz = offset * MathF.Cos(pitch), drop = offset * MathF.Sin(pitch);
        double baseX = info.Pos.X + 0.5, baseY = info.Pos.Y + archetype.BlipOffsetY, baseZ = info.Pos.Z + 0.5;

        for (int beam = 0; beam < count; beam++)
        {
            float heading = BeamState.BeamHeadingRad(baseHeading, beam, count);
            float sin = MathF.Sin(heading), cos = MathF.Cos(heading); // heading dir = (sin, cos) in x/z

            // Each beam's pair takes that beam's own color. The engine stores IPointLight color
            // channel-reversed into its shader uniform, taking R from Color.Z and B from Color.X, so pass
            // it in reversed to get the tint that was actually intended.
            BeamColor.Resolve(info, beam, in archetype, out float lr, out float lg, out float lb);
            float cr = lb * b, cg = lg * b, cb = lr * b;

            // Forward light: straight out along the heading at lamp height, standing in for the spill the
            // lamp throws that way.
            var fwd = beaconLights[beam * LightsPerBeam];
            fwd.Color.Set(cr, cg, cb);
            fwd.Pos.Set(baseX + sin * offset, baseY, baseZ + cos * offset);

            // Downward: the same distance along a ray pitched down toward the floor, so the ground in front
            // of the beacon brightens where the beam points.
            var down = beaconLights[beam * LightsPerBeam + 1];
            down.Color.Set(cr, cg, cb);
            down.Pos.Set(baseX + sin * horiz, baseY - drop, baseZ + cos * horiz);
        }
    }

    // Removes any light not refreshed this tick: beacon out of range, extinguished (gone from the
    // registry), or daytime.
    private void PruneExcept()
    {
        stale.Clear();
        foreach (var pos in lights.Keys)
        {
            if (!keep.Contains(pos)) stale.Add(pos);
        }
        foreach (var pos in stale)
        {
            foreach (var light in lights[pos]) capi.Render.RemovePointLight(light);
            lights.Remove(pos);
        }
    }

    private void RemoveAll()
    {
        if (lights.Count == 0) return;
        foreach (var beaconLights in lights.Values)
            foreach (var light in beaconLights) capi.Render.RemovePointLight(light);
        lights.Clear();
    }

    public void Dispose()
    {
        capi.Event.UnregisterGameTickListener(listenerId);
        RemoveAll();
    }
}
