namespace Seamarks.Beacon;

// The editing side of BeamTuning: a named knob for every look value, which the Ctrl+B panel drives and
// Dump prints. Compiled into Debug builds only (the csproj leaves the Dev folder out of Release), so
// none of this ships.
public static partial class BeamTuning
{
    // Which shape the shape knobs edit. false means the no-lens look.
    public static bool EditingFocused;
    private static Look Active => EditingFocused ? Focused : Unfocused;

    public static void SetEditingFocused(bool focused) => EditingFocused = focused;

    // Min and Max bound the GUI sliders. Geometry knobs are flagged so moving one triggers a mesh rebuild.
    private sealed record Knob(Func<float> Get, Action<float> Set, bool Geometry, float Min, float Max);

    // Short keys for the panel, in a stable display order. The 11 shape knobs read and write whichever
    // shape EditingFocused selects, and the rest are the shared globals.
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

    public static bool TrySet(string key, float value)
    {
        if (!Knobs.TryGetValue(key, out var knob)) return false;
        knob.Set(value);
        if (knob.Geometry) GeometryGeneration++;
        return true;
    }

    // Panel access: the ordered keys, plus each knob's live value and slider bounds.
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
    // whichever shape is being edited, so switch with the panel's toggle and dump again to get the other
    // one. No angle brackets anywhere in here, because the chat renderer eats the message.
    public static string Dump() =>
        $"Current beam tuning (shape shown = {(EditingFocused ? "WITH-LENS/focused" : "NO-LENS/unfocused")}):\n" +
        string.Join("\n", Knobs.Select(kv => $"  {kv.Key} = {kv.Value.Get():0.###}"));
}
