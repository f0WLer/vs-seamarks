using System.Globalization;

namespace Seamarks.Beacon;

// Per-beam color overrides. Each of a beacon's beams defaults to its tier tint (the archetype
// ColorR/G/B); a player can override any individual beam with a packed 0xRRGGBB value via the
// /seamarksbeamcolor command. The overrides live on the beam behaviour, sync on BeaconInfo.BeamColors,
// and are resolved here so the mesh, dust, point light and far blip all render a beam the same color.
public static class BeamColor
{
    // Sentinel in a BeamColors slot, meaning no override and fall back to the tier tint.
    public const int TierDefault = -1;

    // Unpacks a 0xRRGGBB int to 0..1 RGB.
    public static void Unpack(int packed, out float r, out float g, out float b)
    {
        r = ((packed >> 16) & 0xFF) / 255f;
        g = ((packed >> 8) & 0xFF) / 255f;
        b = (packed & 0xFF) / 255f;
    }

    // Resolves beam `beamIndex`'s color to 0..1 RGB: its override if one is set, otherwise the tier
    // tint. Out-of-range indices and unset/absent slots both fall back, so callers never special-case.
    public static void Resolve(BeaconInfo info, int beamIndex, in BeaconArchetypeData archetype, out float r, out float g, out float b)
    {
        int[]? colors = info.BeamColors;
        if (colors != null && (uint)beamIndex < (uint)colors.Length && colors[beamIndex] >= 0)
        {
            Unpack(colors[beamIndex], out r, out g, out b);
            return;
        }
        r = archetype.ColorR;
        g = archetype.ColorG;
        b = archetype.ColorB;
    }

    // Parses the /seamarksbeamcolor argument: "reset"/"default" -> TierDefault, a #rrggbb or rrggbb hex
    // code, or one of the named colors below. Returns false for anything else.
    public static bool TryParse(string? text, out int packed)
    {
        packed = TierDefault;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim().ToLowerInvariant();

        if (s is "reset" or "default" or "tier") { packed = TierDefault; return true; }
        if (Named.TryGetValue(s, out packed)) return true;

        string hex = s.StartsWith('#') ? s[1..] : s;
        if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
        {
            packed = v & 0xFFFFFF;
            return true;
        }
        return false;
    }

    // Mostly a warm, saturated-but-not-neon set that reads well against night sky and water; hex covers
    // anything not named here. "temporal" is the one exception: it's pinned to 0x20DCA8, the Temporal
    // Gear's own color, not picked by feel. The Temporal lens panel resolves to this same name
    // (LensColorPacked strips its "optical" suffix), so the two have to agree exactly.
    private static readonly Dictionary<string, int> Named = new()
    {
        ["white"] = 0xFFFFFF, ["warm"] = 0xFFE5A6, ["amber"] = 0xFFBF40, ["yellow"] = 0xFFE040,
        ["orange"] = 0xFF8020, ["red"] = 0xFF3030, ["pink"] = 0xFF80C0, ["magenta"] = 0xFF40FF,
        ["purple"] = 0xA040FF, ["blue"] = 0x4060FF, ["cyan"] = 0x40FFFF, ["green"] = 0x30FF40,
        ["temporal"] = 0x20DCA8,
    };

    // For command feedback: the hex of an override, or a plain label for the tier default.
    public static string Describe(int packed) =>
        packed < 0 ? "tier default" : $"#{packed & 0xFFFFFF:x6}";

    // A lens panel's glasscolor variant (a seamarks:abstract/glasstype code, e.g. "redoptical") resolved
    // through the same named table /seamarksbeamcolor uses, so a red panel and "/seamarksbeamcolor red"
    // tint a beam identically. "clearoptical" and anything unrecognized fall back to TierDefault, same as
    // an uncolored panel.
    public static int LensColorPacked(string glasscolor)
    {
        string name = glasscolor.EndsWith("optical") ? glasscolor[..^"optical".Length] : glasscolor;
        return TryParse(name, out int packed) ? packed : TierDefault;
    }
}
