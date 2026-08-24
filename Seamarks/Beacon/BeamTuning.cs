namespace Seamarks.Beacon;

// The live, mutable home for every look knob of the beam. These all started life as constants scattered
// across the renderers and emitters, and pulling them together lets the Ctrl+B tuning panel drive them
// in-game without a rebuild.
//
// The 11 shape knobs come in two sets, Focused (lens fitted) and Unfocused (bare lamp, or a housing with
// no panel), picked per beacon off BeaconInfo.HasFocusingLens. The panel edits whichever set
// EditingFocused points at.
// Everything else (environment, LOD, point light, dust, ignition) is one global set shared by every
// beacon. Defaults come from the OilLamp archetype's two shapes so the archetype stays canonical, and
// Dump() prints the active set for pasting back into it. Geometry changes need a mesh rebuild, so those
// bump GeometryGeneration, which BeamRenderer watches.
public static class BeamTuning
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

    // Which shape the command and panel currently edit. false means the no-lens look.
    public static bool EditingFocused;
    private static Look Active => EditingFocused ? Focused : Unfocused;

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
    // LOD hand-off: the mesh beam fades out into the far blip across [LodStartBlocks, LodEndBlocks].
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

    // Switches which shape the panel edits, and which one Dump and the sliders show.
    public static string SetEditingFocused(bool focused)
    {
        EditingFocused = focused;
        return $"Now editing the {(focused ? "WITH-LENS (focused)" : "NO-LENS (unfocused)")} beam shape.";
    }

    // Min and Max bound the GUI sliders. Geometry knobs are flagged so moving one triggers a mesh rebuild.
    private sealed record Knob(Func<float> Get, Action<float> Set, bool Geometry, float Min, float Max);

    // Short keys for the command and panel, in a stable display order. The 11 shape knobs read and write
    // whichever shape EditingFocused selects, and the rest are the shared globals.
    private static readonly Dictionary<string, Knob> Knobs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["length"] = new(() => Active.Length, v => Active.Length = v, true, 8f, 256f),
        ["nearradius"] = new(() => Active.NearRadius, v => Active.NearRadius = v, true, 0f, 4f),
        ["farradius"] = new(() => Active.FarRadius, v => Active.FarRadius = v, true, 0f, 20f),
        ["hscale"] = new(() => Active.HScale, v => Active.HScale = v, true, 0.2f, 3f),
        // Low end sits near zero, well under the 0.2 floor the other axes use, so this can squash the
        // beam's cross-section into a thin horizontal slit and not just a slightly flattened cone.
        ["vscale"] = new(() => Active.VScale, v => Active.VScale = v, true, 0.02f, 3f),
        ["radialbands"] = new(() => Active.RadialBands, v => Active.RadialBands = v, true, 2f, 16f),
        ["corefraction"] = new(() => Active.CoreFraction, v => Active.CoreFraction = v, false, 0f, 1f),
        ["radialsoftness"] = new(() => Active.RadialSoftness, v => Active.RadialSoftness = v, false, 0.2f, 4f),
        ["lengthfade"] = new(() => Active.LengthFadePow, v => Active.LengthFadePow = v, false, 0.2f, 4f),
        ["halo"] = new(() => Active.HaloIntensity, v => Active.HaloIntensity = v, false, 0f, 2f),
        ["core"] = new(() => Active.CoreIntensity, v => Active.CoreIntensity = v, false, 0f, 3f),
        ["occlusionsoftness"] = new(() => OcclusionSoftness, v => OcclusionSoftness = v, false, 0.005f, 0.2f),
        ["lensoffset"] = new(() => LensOffsetBlocks, v => LensOffsetBlocks = v, false, 0f, 2f),
        ["lensoffsetnolens"] = new(() => UnfocusedLensOffsetBlocks, v => UnfocusedLensOffsetBlocks = v, false, 0f, 2f),
        ["fogscale"] = new(() => FogScale, v => FogScale = v, false, 0f, 100f),
        ["dayfadelow"] = new(() => DayFadeLow, v => DayFadeLow = v, false, 0f, 1f),
        ["dayfadehigh"] = new(() => DayFadeHigh, v => DayFadeHigh = v, false, 0f, 1.5f),
        ["lodstart"] = new(() => LodStartBlocks, v => LodStartBlocks = v, false, 50f, 500f),
        ["lodend"] = new(() => LodEndBlocks, v => LodEndBlocks = v, false, 100f, 600f),
        ["lightoffset"] = new(() => PointLightOffset, v => PointLightOffset = v, false, 0f, 32f),
        ["lightbrightness"] = new(() => PointLightBrightness, v => PointLightBrightness = v, false, 0f, 60f),
        ["lightrange"] = new(() => PointLightRange, v => PointLightRange = v, false, 0f, 96f),
        ["lightdownpitch"] = new(() => PointLightDownPitch, v => PointLightDownPitch = v, false, 0f, 90f),
        ["dustpertick"] = new(() => DustPerTick, v => DustPerTick = v, false, 0f, 10f),
        ["dustminsize"] = new(() => DustMinSize, v => DustMinSize = v, false, 0f, 1f),
        ["dustmaxsize"] = new(() => DustMaxSize, v => DustMaxSize = v, false, 0f, 1f),
        ["dustlifelow"] = new(() => DustLifeLow, v => DustLifeLow = v, false, 0f, 3f),
        ["dustlifehigh"] = new(() => DustLifeHigh, v => DustLifeHigh = v, false, 0f, 3f),
        ["dustalpha"] = new(() => DustAlpha, v => DustAlpha = v, false, 0f, 255f),
        ["dustrange"] = new(() => DustRange, v => DustRange = v, false, 0f, 96f),
        ["dustleadlow"] = new(() => DustLeadLow, v => DustLeadLow = v, false, -1f, 1f),
        ["dustleadhigh"] = new(() => DustLeadHigh, v => DustLeadHigh = v, false, -1f, 1f),
        ["dustnear"] = new(() => DustNear, v => DustNear = v, false, 0f, 30f),
        ["dustfar"] = new(() => DustFar, v => DustFar = v, false, 2f, 64f),
        ["ignitefade"] = new(() => IgnitionFadeSeconds, v => IgnitionFadeSeconds = v, false, 0f, 3f),
    };

    public static bool TrySet(string key, float value, out string message)
    {
        if (key == null || !Knobs.TryGetValue(key, out var knob))
        {
            message = $"Unknown key '{key}'.";
            return false;
        }
        knob.Set(value);
        if (knob.Geometry) GeometryGeneration++;
        message = $"{key} = {value:0.###}";
        return true;
    }

    // GUI panel access: the ordered keys, plus each knob's live value and slider bounds.
    public static IReadOnlyCollection<string> KnobKeys => Knobs.Keys;

    public static bool TryGet(string key, out float value, out float min, out float max)
    {
        if (Knobs.TryGetValue(key, out var knob))
        {
            value = knob.Get(); min = knob.Min; max = knob.Max;
            return true;
        }
        value = min = max = 0f;
        return false;
    }

    // Current values, one per line, for pasting back into BeaconArchetype. The 11 shape lines show
    // whichever shape is being edited, so switch with 'edit' and dump again to get the other one. No
    // angle brackets anywhere in here, because the chat renderer eats the message.
    public static string Dump() =>
        $"Current beam tuning (shape shown = {(EditingFocused ? "WITH-LENS/focused" : "NO-LENS/unfocused")}):\n" +
        string.Join("\n", Knobs.Select(kv => $"  {kv.Key} = {kv.Value.Get():0.###}"));
}
