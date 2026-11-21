namespace Seamarks.Beacon;

// The live, mutable home for every look knob of the beam. Debug builds add a Ctrl+B panel (the Dev
// folder) that edits these in game without a rebuild, which is why they're statics and not constants.
//
// The 11 shape knobs come in two sets, Focused (lens fitted) and Unfocused (bare lamp, or a housing with
// no panel), picked per beacon off BeaconInfo.HasFocusingLens.
// Everything else (environment, LOD, point light, dust, ignition) is one global set shared by every
// beacon. Defaults come from the OilLamp archetype's two shapes, so the archetype stays canonical.
// Geometry changes need a mesh rebuild, so those bump GeometryGeneration, which BeamRenderer watches.
public static partial class BeamTuning
{
    // One beam's live shape: the geometry knobs, which force a mesh rebuild when they move, plus the
    // draw-time appearance knobs. Mirrors BeamShape but mutable, so a value can be nudged between frames.
    public sealed class Look
    {
        public float Length, NearRadius, FarRadius, HScale, VScale, RadialBands;        // geometry
        public float CoreFraction, RadialSoftness, LengthFadePow, HaloIntensity, CoreIntensity; // appearance

        public void Seed(BeamShape s)
        {
            Length = s.LengthBlocks; NearRadius = s.NearRadiusBlocks; FarRadius = s.FarRadiusBlocks;
            HScale = s.HorizontalScale; VScale = s.VerticalScale; RadialBands = s.RadialBands;
            CoreFraction = s.CoreRadiusFraction; RadialSoftness = s.RadialSoftness; LengthFadePow = s.LengthFadePow;
            HaloIntensity = s.HaloIntensity; CoreIntensity = s.CoreIntensity;
        }
    }

    // The two per-beacon shapes. The renderer and emitters read these live through ShapeFor.
    public static readonly Look Focused = new();
    public static readonly Look Unfocused = new();

    // The live shape a given beacon renders with.
    public static Look ShapeFor(bool hasHousing) => hasHousing ? Focused : Unfocused;

    // Shared by every beacon, with no housing split. OcclusionSoftness is the feather on the wall cut.
    public static float OcclusionSoftness;
    // How far along the beam, in blocks from the draw anchor, the reflector's own glass actually sits.
    // The mesh still starts at the anchor so the rest of the geometry (radius, width scale) is untouched,
    // but the shader hides everything short of this distance, so the beam reads as coming out of the
    // lens instead of glowing from inside the lamp housing. Split focused/unfocused because an empty
    // housing has no lens to hide behind, so its glow should start right at the anchor.
    public static float LensOffsetBlocks;
    public static float UnfocusedLensOffsetBlocks;
    // Environment response (fed to the beam shader as uniforms).
    public static float FogScale, DayFadeLow, DayFadeHigh;
    // LOD hand-off: the mesh beam fades out into the far blip across [LodStartBlocks, LodEndBlocks]. A view
    // distance past LodStartBlocks pushes the band out, so LodStartBlocks is the minimum and the gap
    // between the two is the fade width.
    public static float LodStartBlocks, LodEndBlocks;
    // Sweeping point light. Two lights per beacon throw the lamp's spill along the beam and sweep with the
    // heading, one straight out at lamp height and one pitched PointLightDownPitch degrees toward the
    // floor. PointLightOffset is how far out they sit, which needs to clear the lamp's own static block
    // light or the sweep washes out. PointLightRange is the activation range.
    public static float PointLightOffset, PointLightBrightness, PointLightRange, PointLightDownPitch;
    // Dust motes. DustLead offsets the spawn heading in radians so the cloud can lead (positive) or lag
    // (negative) the beam's current angle. DustNear and DustFar bound the stretch of beam, in blocks from
    // the lamp, that the motes populate. Life and lead each calibrate best at a different sweep speed (a
    // puff that reads right on a slow-turning lamp looks wrong on a fast one), so both are a Low/High pair
    // BeaconBeamParticles interpolates between by the beacon's own current rps, per BeamState's speed
    // breakpoints. High is also where the sweep is fast enough that the dust effect stops entirely.
    public static float DustPerTick, DustMinSize, DustMaxSize, DustLifeLow, DustLifeHigh, DustAlpha, DustRange, DustLeadLow, DustLeadHigh, DustNear, DustFar;
    // Ignition fade: real-time seconds over which the whole beam ramps in on ignite / out on extinguish.
    public static float IgnitionFadeSeconds;

    public static int GeometryGeneration { get; private set; }

    // Forces a mesh rebuild next frame without any tuning value moving, for when something outside these
    // knobs changes the geometry. A graphics-quality change that alters the effective band count is the
    // case that needs it.
    public static void InvalidateGeometry() => GeometryGeneration++;

    static BeamTuning() => Reset();

    // Puts both shapes and all the globals back to their archetype defaults.
    public static void Reset()
    {
        BeamProfile b = BeaconArchetypeData.Of(BeaconTier.OilLamp).Beam;
        Focused.Seed(b.Focused);
        Unfocused.Seed(b.Unfocused);
        OcclusionSoftness = 0.03f;
        LensOffsetBlocks = 0.2f; // tuned in-game
        UnfocusedLensOffsetBlocks = 0f; // tuned in-game

        FogScale = 30f; DayFadeLow = 0.07f; DayFadeHigh = 0.87f;
        LodStartBlocks = 300f; LodEndBlocks = 420f;
        PointLightOffset = 10f; PointLightBrightness = 20f; PointLightRange = 48f;
        PointLightDownPitch = 45f;
        DustPerTick = 2f; DustMinSize = 0.1f; DustMaxSize = 0.25f; DustAlpha = 90f; DustRange = 40f;
        DustLifeLow = 0.09f; DustLeadLow = 0.04f;   // calibrated at/below BeamState.DustSweepLowRps
        DustLifeHigh = 0.06f; DustLeadHigh = 0.1f;  // calibrated at BeamState.DustSweepHighRps
        DustNear = 2f; DustFar = 22f;
        IgnitionFadeSeconds = 0.6f;

        GeometryGeneration++;
    }

    // Reapplies just the archetype's two beam lengths onto the live shapes, for after a tuning sync has
    // moved them (VisualBeamLength in the config). Leaves every other knob alone, so a length a player
    // nudged live in the tuning panel is the one case this overrides, and bumps the geometry so the
    // cone remeshes at the new reach.
    public static void ReseedLengths()
    {
        BeamProfile b = BeaconArchetypeData.Of(BeaconTier.OilLamp).Beam;
        Focused.Length = b.Focused.LengthBlocks;
        Unfocused.Length = b.Unfocused.LengthBlocks;
        GeometryGeneration++;
    }
}
