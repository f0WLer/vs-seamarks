using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace Seamarks.Beacon;

// What an Optics Housing produces: an ordered set of installed lens panels, which is the beam count. Each
// panel is a type 1..4, and the type sets that beam's width, while its glass sets the color. The housing owns all
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

    // Each installed panel's own glass color, parallel to panels by index. A beam's color follows from
    // this alone (BeamColor.LensColorPacked), so nothing about it is stored separately.
    private readonly List<string> panelColors = [];

    public int BeamCount => panels.Count;
    public IReadOnlyList<int> PanelTypes => panels;
    public IReadOnlyList<string> PanelColors => panelColors;

    // A beacon with nothing fitted. Handed out fresh every time because callers can retune what they get
    // back, and a shared instance would let one beacon's edit leak into every other bare one.
    public static BeamSignature None => new();

    // Replaces the whole panel list from the housing's slots, compacted in slot order.
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

    // Beam i's tint, which is its panel's glass color, or the tier default for a clear panel.
    private int BeamTint(int i) => BeamColor.LensColorPacked(panelColors[i]);

    // Writes the derived beam fields onto the snapshot clients read. Each beam's width comes from its
    // panel type, so the width array is always carried when anything is installed; colors only when a
    // panel actually tints.
    public void ApplyTo(BeaconInfo info)
    {
        info.BeamCount = panels.Count;

        if (panels.Count == 0) { info.BeamWidthsDeg = null; info.BeamColors = null; return; }
        var widths = new float[panels.Count];
        var tints = new int[panels.Count];
        bool anyTint = false;
        for (int i = 0; i < panels.Count; i++)
        {
            widths[i] = WidthForType(panels[i]);
            tints[i] = BeamTint(i);
            anyTint |= tints[i] != BeamColor.TierDefault;
        }
        info.BeamWidthsDeg = widths;
        info.BeamColors = anyTint ? tints : null;
    }

    // A beam's display color, 0..1 RGB: its panel's tint if it has one, else the oil lamp's tier tint. The
    // housing GUI paints each panel's name in this, so it matches the beam it drives.
    public void ResolveBeamColor(int beamIndex, out float r, out float g, out float b)
    {
        ref readonly BeaconArchetypeData archetype = ref BeaconArchetypeData.Of(BeaconTier.OilLamp);
        int tint = (uint)beamIndex < (uint)panels.Count ? BeamTint(beamIndex) : BeamColor.TierDefault;
        BeamColor.ResolvePacked(tint, in archetype, out r, out g, out b);
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
        return sig;
    }

    public void ToStack(ItemStack stack) => ToTree(stack.Attributes);

    // A freshly made housing has no attributes at all, so every key falls back and it starts empty.
    public static BeamSignature FromStack(ItemStack? stack) =>
        stack?.Attributes == null ? new BeamSignature() : FromTree(stack.Attributes);

    // Feedback text for the fitting notice: "2 panels (types 1, 3): 4, 16 deg", or "no panels" when empty.
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
            int tint = BeamTint(i);
            if (tint >= 0) s += $"; beam {i + 1} #{tint:x6}";
        }
        return s;
    }
}
