using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Seamarks.Beacon;

// A lens panel's tooltip quotes its beam width. Vintage Story's automatic itemdesc-<code> lookup
// (CollectibleObject.GetItemDescText) has no argument substitution, so that figure would otherwise have
// to be hand-typed into the lang file and kept in sync with BeamSignature.WidthForType by memory. Reading
// it here means a future width retune only ever touches the one table.
public class ItemLensPanel : Item
{
    public override string GetItemDescText()
    {
        if (!int.TryParse(Variant["type"], out int type)) return base.GetItemDescText();
        return Lang.Get($"{Code.Domain}:itemdesc-lenspanel-{type}", BeamSignature.WidthForType(type)) + "\n";
    }
}
