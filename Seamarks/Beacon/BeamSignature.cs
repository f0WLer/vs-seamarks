using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon;

// What an Optics Housing produces: an ordered set of installed lens panels (which is the beam count) plus
// a color per beam. Each panel is a type 1..4, and the type sets that beam's width. The housing owns all
// of this and carries it wherever it goes, so the configuration follows the block from inventory to the
// ground to whatever beacon it ends up fitted to. A beacon with no housing publishes None, a plain single
// beam.
//
// The same values travel three ways, and all three have to agree or a housing changes just by being
// picked up: a tree attribute on either block entity, attributes on the itemstack, and the flat
// BeaconInfo that goes out to clients.
public sealed class BeamSignature
{
    public const int MinBeamCount = 1, MaxBeamCount = 3; // at most three panels; the fourth mount stays a cover
    public const int MinPanelType = 1, MaxPanelType = 4;
    public const string DefaultGlassColor = "clearoptical";

    // Lens type to beam width in degrees. Denser Fresnel throws the tighter beam: type 1 is narrowest,
    // type 4 (the ring-less blank) the widest.
    public static float WidthForType(int type) => type switch
    {
        1 => 4f,
        2 => 10f,
        3 => 16f,
        _ => 20f,
    };

    // Install order, so the last one added is the first one shift-clicked back off. Length is the beam
    // count. Which mount each lands on is the renderer's business (OpticsHousingMesh), not stored here.
    private readonly List<int> panels = [];

    // Each installed panel's own glass color, parallel to panels by index. This is the physical panel's
    // color for drawing its lens, separate from colors below (a beam's tint override), so retuning a
    // beam's color by hand never repaints the glass that isn't actually that shade.
    private readonly List<string> panelColors = [];

    // One color slot per possible beam, kept even past the current count so lowering then raising it
    // doesn't lose a set color. BeamColor.TierDefault(-1) means the tier tint.
    private readonly int[] colors = new int[MaxBeamCount];

    // Whether each beam's current color came from /seamarksbeamcolor and not the panel that's fitted.
    // A manual color has to survive swapping panels around; a panel-given color has to update every time
    // a different panel goes in the slot, including back to no tint at all. Without this flag the two
    // looked identical once set, and a panel's color could only ever apply while the slot was still at the
    // tier default, so a second panel swap silently kept the first panel's tint.
    private readonly bool[] colorIsManual = new bool[MaxBeamCount];

    public BeamSignature() => Array.Fill(colors, BeamColor.TierDefault);

    public int BeamCount => panels.Count;
    public IReadOnlyList<int> PanelTypes => panels;
    public IReadOnlyList<string> PanelColors => panelColors;

    // A beacon with nothing fitted. Handed out fresh every time because callers can retune what they get
    // back, and a shared instance would let one beacon's edit leak into every other bare one.
    public static BeamSignature None => new();

    // Replaces the whole panel list from the housing's slots, compacted in slot order. The beam color
    // overrides are left alone, so lowering then raising the beam count keeps a set color.
    public void SetPanels(IEnumerable<(int Type, string GlassColor)> newPanels)
    {
        panels.Clear();
        panelColors.Clear();
        foreach (var (type, glassColor) in newPanels)
        {
            if (panels.Count >= MaxBeamCount) break;
            panels.Add(Math.Clamp(type, MinPanelType, MaxPanelType));
            panelColors.Add(glassColor);
        }
    }

    // The mesh cache key for this exact configuration: type and glass color both change how a panel
    // draws, so both belong in it, or two housings with the same widths but different colors would share
    // a cached mesh and one would render wrong.
    public string PanelKey() => panels.Count == 0
        ? "empty"
        : string.Join("-", panels.Select((type, i) => $"{type}{panelColors[i]}"));

    // packed < 0 clears the override back to the tier tint, and back to panel-driven: the next panel
    // swap can retint this beam again.
    public int SetColor(int beamNumber, int packed)
    {
        int idx = (int)GameMath.Clamp(beamNumber, MinBeamCount, MaxBeamCount) - 1;
        colorIsManual[idx] = packed >= 0;
        colors[idx] = packed < 0 ? BeamColor.TierDefault : (packed & 0xFFFFFF);
        return colors[idx];
    }

    public int SetAllColors(int packed)
    {
        int value = packed < 0 ? BeamColor.TierDefault : (packed & 0xFFFFFF);
        Array.Fill(colors, value);
        Array.Fill(colorIsManual, packed >= 0);
        return value;
    }

    // A lens panel's glass tints its beam, unless a player has retuned that beam by hand: a manual color
    // survives swapping panels, but a panel-given color has to track whatever panel is actually seated,
    // including back to the tier default for a clear one. Called every time SyncPanelsFromInventory
    // rebuilds the panel list, so it always resolves the current panel, not just the first one fitted.
    public void SetColorFromPanel(int beamNumber, int packed)
    {
        int idx = (int)GameMath.Clamp(beamNumber, MinBeamCount, MaxBeamCount) - 1;
        if (colorIsManual[idx]) return;
        colors[idx] = packed < 0 ? BeamColor.TierDefault : (packed & 0xFFFFFF);
    }

    // Writes the derived beam fields onto the snapshot clients read. Each beam's width comes from its
    // panel type, so the width array is always carried when anything is installed; colors only when one
    // is actually overridden. Cloned so the synced snapshot never aliases these live arrays.
    public void ApplyTo(BeaconInfo info)
    {
        info.BeamCount = panels.Count;
        info.BeamWidthDeg = BeaconInfo.DefaultBeamWidthDeg; // fallback, unused once the per-beam array is set
        info.BeamColors = HasColorOverride() ? (int[])colors.Clone() : null;

        if (panels.Count == 0) { info.BeamWidthsDeg = null; return; }
        var widths = new float[panels.Count];
        for (int i = 0; i < panels.Count; i++) widths[i] = WidthForType(panels[i]);
        info.BeamWidthsDeg = widths;
    }

    private bool HasColorOverride()
    {
        foreach (int c in colors) if (c != BeamColor.TierDefault) return true;
        return false;
    }

    // A beam's display color, 0..1 RGB: its override if set, else the oil lamp's tier tint. The housing
    // GUI paints each panel's name in this, so it matches the beam it drives. Mirrors BeamColor.Resolve,
    // which needs a live BeaconInfo the ground housing doesn't have.
    public void ResolveBeamColor(int beamIndex, out float r, out float g, out float b)
    {
        ref readonly BeaconArchetypeData archetype = ref BeaconArchetypeData.Of(BeaconTier.OilLamp);
        if ((uint)beamIndex < (uint)colors.Length && colors[beamIndex] >= 0)
        {
            BeamColor.Unpack(colors[beamIndex], out r, out g, out b);
            return;
        }
        r = archetype.ColorR;
        g = archetype.ColorG;
        b = archetype.ColorB;
    }

    // Prefixed so a signature can share a tree with whatever else the owning block entity stores.
    public void ToTree(ITreeAttribute tree, string prefix = "")
    {
        tree.SetInt(prefix + "panelCount", panels.Count);
        for (int i = 0; i < panels.Count; i++)
        {
            tree.SetInt(prefix + "panel" + i, panels[i]);
            tree.SetString(prefix + "panelColor" + i, panelColors[i]);
        }
        for (int i = 0; i < colors.Length; i++)
        {
            tree.SetInt(prefix + "beamColor" + i, colors[i]);
            tree.SetBool(prefix + "beamColorManual" + i, colorIsManual[i]);
        }
    }

    public static BeamSignature FromTree(ITreeAttribute tree, string prefix = "")
    {
        var sig = new BeamSignature();
        int count = Math.Clamp(tree.GetInt(prefix + "panelCount", 0), 0, MaxBeamCount);
        for (int i = 0; i < count; i++)
        {
            sig.panels.Add(Math.Clamp(tree.GetInt(prefix + "panel" + i, MinPanelType), MinPanelType, MaxPanelType));
            sig.panelColors.Add(tree.GetString(prefix + "panelColor" + i, DefaultGlassColor));
        }
        for (int i = 0; i < sig.colors.Length; i++)
        {
            sig.colors[i] = tree.GetInt(prefix + "beamColor" + i, BeamColor.TierDefault);
            sig.colorIsManual[i] = tree.GetBool(prefix + "beamColorManual" + i, false);
        }
        return sig;
    }

    public void ToStack(ItemStack stack) => ToTree(stack.Attributes);

    // A freshly made housing has no attributes at all, so every key falls back and it starts empty.
    public static BeamSignature FromStack(ItemStack? stack) =>
        stack?.Attributes == null ? new BeamSignature() : FromTree(stack.Attributes);

    // Hover and command feedback: "2 panels (types 1, 3): 4, 16 deg", or "no panels" when empty.
    public string Describe()
    {
        if (panels.Count == 0) return "no panels";

        var types = new StringBuilder();
        var widths = new StringBuilder();
        for (int i = 0; i < panels.Count; i++)
        {
            if (i > 0) { types.Append(", "); widths.Append(", "); }
            types.Append(panels[i]);
            widths.Append($"{WidthForType(panels[i]):0.#}");
        }
        string s = $"{panels.Count} panel{(panels.Count == 1 ? "" : "s")} (type{(panels.Count == 1 ? "" : "s")} {types}): {widths} deg";

        for (int i = 0; i < panels.Count; i++)
        {
            if (colors[i] != BeamColor.TierDefault) s += $"; beam {i + 1} {BeamColor.Describe(colors[i])}";
        }
        return s;
    }
}
