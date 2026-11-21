namespace Seamarks.Beacon;

// Per-beam colors. Each of a beacon's beams defaults to its tier tint (the archetype ColorR/G/B), and the
// glass of the lens panel behind it can replace that with a packed 0xRRGGBB value. The values sync on
// BeaconInfo.BeamColors and are resolved here so the mesh, dust, point light and far blip all render a
// beam the same color.
public static class BeamColor
{
    // Sentinel in a BeamColors slot, meaning no tint and fall back to the tier tint.
    public const int TierDefault = -1;

    // Resolves beam `beamIndex`'s color to 0..1 RGB: its tint if one is set, otherwise the tier tint.
    // Out-of-range indices and unset/absent slots both fall back, so callers never special-case.
    public static void Resolve(BeaconInfo info, int beamIndex, in BeaconArchetypeData archetype, out float r, out float g, out float b) =>
        ResolvePacked(info.BeamColors is { } c && (uint)beamIndex < (uint)c.Length ? c[beamIndex] : TierDefault,
            in archetype, out r, out g, out b);

    // One beam's color from its packed 0xRRGGBB value, where a negative value means the tier tint.
    public static void ResolvePacked(int packed, in BeaconArchetypeData archetype, out float r, out float g, out float b)
    {
        if (packed >= 0)
        {
            r = ((packed >> 16) & 0xFF) / 255f;
            g = ((packed >> 8) & 0xFF) / 255f;
            b = (packed & 0xFF) / 255f;
            return;
        }
        r = archetype.ColorR;
        g = archetype.ColorG;
        b = archetype.ColorB;
    }

    // The tints a lens panel's glass can give its beam, keyed by the glass name. "temporal" is pinned to
    // 0x20DCA8, the Temporal Gear's own color, not picked by feel.
    private static readonly Dictionary<string, int> Named = new()
    {
        ["amber"] = 0xFFBF40,
        ["red"] = 0xFF3030,
        ["green"] = 0x30FF40,
        ["temporal"] = 0x20DCA8,
    };

    // A lens panel's glasscolor variant (a seamarks:abstract/glasstype code, e.g. "redoptical") resolved
    // through the named table above. "clearoptical" and anything unrecognized fall back to TierDefault,
    // the same as an uncolored panel.
    public static int LensColorPacked(string glasscolor)
    {
        string name = glasscolor.EndsWith("optical") ? glasscolor[..^"optical".Length] : glasscolor;
        return Named.TryGetValue(name, out int packed) ? packed : TierDefault;
    }
}
