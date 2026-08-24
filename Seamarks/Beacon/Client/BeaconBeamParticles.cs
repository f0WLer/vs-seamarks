using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon.Client;

// A few glowing motes spawned inside the near stretch of each burning directional beam, so the light
// reads as passing through dusty air. Particles are always camera-facing billboards, so they could never
// be the beam itself. This is dust caught in it, layered over the mesh.
//
// Client only, and gated to a near-full-brightness beam and close range, since motes only read from
// nearby. IgnoreUserConfig is left alone so this shares the engine's global ParticleLevel-throttled pool,
// which means a busy scene thins it out. Every look value is live-tunable so the calibration panel can
// drive them alongside the beam and point-light parameters.
public sealed class BeaconBeamParticles : IDisposable
{
    private const int TickIntervalMs = 30;
    // Dust waits for the beam to be nearly at full brightness, so motes never hang in the air around a
    // beam that's still a faint wash at dusk. Moving the day-fade knobs moves this with them.
    public const float MinBeamBrightness = 0.8f;
    // Spawn stretch along the beam (DustNear/DustFar) is live-tunable via BeamTuning.

    private readonly SeamarksModSystem modSystem;
    private readonly ICoreClientAPI capi;
    private readonly long listenerId;
    private readonly Random rand = new();

    // One reusable provider, mutated per spawn (the particle manager reads its fields at spawn time,
    // the same pattern vanilla blocks use), to avoid allocating per mote.
    private readonly SimpleParticleProperties motes = new()
    {
        MinQuantity = 1,
        AddQuantity = 0,
        MinPos = new Vec3d(),
        AddPos = new Vec3d(),
        MinVelocity = new Vec3f(-0.06f, -0.02f, -0.06f),
        AddVelocity = new Vec3f(0.12f, 0.1f, 0.12f),
        GravityEffect = 0f,          // floaty dust, doesn't fall
        ParticleModel = EnumParticleModel.Quad,
        LightEmission = ColorUtil.ColorFromRgba(200, 180, 140, 255), // warm self-illumination so motes glow at night
    };

    public BeaconBeamParticles(SeamarksModSystem modSystem, ICoreClientAPI capi)
    {
        this.modSystem = modSystem;
        this.capi = capi;
        listenerId = capi.Event.RegisterGameTickListener(OnTick, TickIntervalMs);
    }

    private void OnTick(float dt)
    {
        var registry = modSystem.Client?.Registry;
        var player = capi.World.Player?.Entity;
        if (registry == null || player == null) return;

        if (BeamState.DayFadeFactor(capi.World.Calendar.DayLightStrength) < MinBeamBrightness) return;
        if (!BeamQuality.DustEnabled) return; // player has particles off entirely

        Vec3d camPos = player.CameraPos;
        double now = BeamState.SweepNowHours(capi.World);

        // Live-tunable look (BeamTuning), refreshed each tick. Life and lead are speed-dependent
        // (below), so they're resolved per beacon instead of once here.
        motes.MinSize = BeamTuning.DustMinSize;
        motes.MaxSize = BeamTuning.DustMaxSize;
        // Per-tick count is the tuned value scaled by the graphics-detail factor, so fewer motes on
        // low presets, on top of the engine's own ParticleLevel pool throttling we already respect.
        int motesPerBeam = (int)(BeamTuning.DustPerTick * BeamQuality.DustCountFactor);
        if (motesPerBeam <= 0) return;
        int alpha = (int)BeamTuning.DustAlpha;
        float rangeSq = BeamTuning.DustRange * BeamTuning.DustRange;

        // One buffer for the whole tick, sized off the beam ceiling itself and refilled per beacon below.
        // Hoisted out of the beacon loop so the stackalloc can't accumulate across iterations.
        Span<int> beamColorInts = stackalloc int[BeamSignature.MaxBeamCount];

        foreach (var rb in registry.RenderBeacons())
        {
            BeaconInfo info = rb.Info;
            ref readonly BeaconArchetypeData archetype = ref BeaconArchetypeData.Of(info.Tier);
            if (!archetype.Beam.Directional) continue;

            // A sweep fast enough blurs the puff past reading as dust at all, so the effect just stops.
            // Below that, life and lead each calibrate best at a different speed, so they ramp smoothly
            // between the two across the band, never snapping.
            float rps = BeamState.CurrentRevolutionsPerSecond(info, capi.World.Calendar);
            if (rps >= BeamState.DustSweepHighRps) continue;
            float speedT = GameMath.Clamp((rps - BeamState.DustSweepLowRps) / (BeamState.DustSweepHighRps - BeamState.DustSweepLowRps), 0f, 1f);
            float dustLife = GameMath.Lerp(BeamTuning.DustLifeLow, BeamTuning.DustLifeHigh, speedT);
            float dustLead = GameMath.Lerp(BeamTuning.DustLeadLow, BeamTuning.DustLeadHigh, speedT);
            motes.LifeLength = dustLife;

            int beamCount = BeamState.ClampedBeamCount(info);

            double lampX = info.Pos.X + archetype.BlipOffsetX;
            double lampY = info.Pos.Y + archetype.BlipOffsetY;
            double lampZ = info.Pos.Z + archetype.BlipOffsetZ;

            double ddx = lampX - camPos.X, ddy = lampY - camPos.Y, ddz = lampZ - camPos.Z;
            if (ddx * ddx + ddy * ddy + ddz * ddz > rangeSq) continue;

            // Fewer motes while the beam fades in or out, so the dust ramps with it instead of popping.
            int beamMotes = (int)(beamCount * motesPerBeam * rb.Fade);
            if (beamMotes <= 0) continue;

            // Each beam's motes take that beam's color (its override, or the tier tint). Precomputed
            // here since motesPerBeam samples share one beam's color.
            for (int bi = 0; bi < beamCount; bi++)
            {
                BeamColor.Resolve(info, bi, in archetype, out float cr, out float cg, out float cb);
                beamColorInts[bi] = ColorUtil.ColorFromRgba((int)(cr * 255), (int)(cg * 255), (int)(cb * 255), alpha);
            }

            // Offset the emission heading so the dust cloud leads or lags the beam's true angle. That
            // phases the motes against the sweep so they appear just as/after the beam front arrives,
            // instead of trailing behind it. A short DustLife keeps it a puff and not a comet tail.
            // A beam holding still has nothing to lead, and the offset would just sit the cloud off to
            // one side of the cone, so it only applies while the beam is turning.
            float lead = BeamState.IsSweeping(info) ? dustLead : 0f;
            float heading = BeamState.CurrentHeadingRad(info, now) + lead;
            float cos = MathF.Cos(heading), sin = MathF.Sin(heading);

            // The motes fill the same cone the mesh draws, so resolve the same lens-selected shape.
            BeamTuning.Look look = BeamTuning.ShapeFor(info.HasFocusingLens);

            for (int i = 0; i < beamMotes; i++)
            {
                int beamIndex = Math.Min(i / motesPerBeam, beamCount - 1);
                motes.Color = beamColorInts[beamIndex];
                // Per-beam width scale, so a mote's fan matches its own beam's mesh width.
                float widthScale = info.BeamWidthDegOf(beamIndex) / BeaconInfo.DefaultBeamWidthDeg;
                SpawnMote(look, beamCount, widthScale, lampX, lampY, lampZ, cos, sin, heading, beamIndex);
            }
        }
    }

    private void SpawnMote(BeamTuning.Look look, int beamCount, float widthScale, double lampX, double lampY, double lampZ, float cos, float sin, float heading, int beamIndex)
    {
        // A point somewhere in the near portion of the cone: distance along the axis, then a radial
        // offset within the (elliptical) cone radius at that distance. sqrt keeps it area-uniform. The
        // cone geometry (near/far radius, length, h/v scale) is this beacon's housing-selected shape.
        float z = BeamTuning.DustNear + (float)rand.NextDouble() * (BeamTuning.DustFar - BeamTuning.DustNear);
        float radiusAtZ = GameMath.Lerp(look.NearRadius, look.FarRadius, z / look.Length);
        float r = radiusAtZ * MathF.Sqrt((float)rand.NextDouble());
        float a = (float)rand.NextDouble() * GameMath.TWOPI;
        // widthScale matches the mesh's per-beacon horizontal scaling so the dust fills the same fan.
        float lx = r * MathF.Cos(a) * look.HScale * widthScale;
        float ly = r * MathF.Sin(a) * look.VScale;

        // For a multi-beam optic, offset this mote's local frame by its beam's share of the circle.
        float mc = cos, ms = sin;
        if (beamIndex > 0)
        {
            float h = BeamState.BeamHeadingRad(heading, beamIndex, beamCount);
            mc = MathF.Cos(h); ms = MathF.Sin(h);
        }

        // Rotate the local (lx, ly, z) into world space around Y (matches BeamMesh's RotateY: local +Z
        // maps to (sin, cos) in x/z), then offset to the lamp.
        double wx = lx * mc + z * ms;
        double wz = -lx * ms + z * mc;
        motes.MinPos.Set(lampX + wx, lampY + ly, lampZ + wz);

        capi.World.SpawnParticles(motes);
    }

    public void Dispose()
    {
        capi.Event.UnregisterGameTickListener(listenerId);
    }
}
