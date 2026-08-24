using Vintagestory.API.Common;

namespace Seamarks.Beacon;

// One mount in a placed housing. Holds a single Lens Panel and refuses anything else, so a stray item
// can't be dropped onto a beam slot.
public class ItemSlotLensPanel : ItemSlotSurvival
{
    public ItemSlotLensPanel(InventoryBase inventory) : base(inventory)
    {
        MaxSlotStackSize = 1;
    }

    public override bool CanHold(ItemSlot sourceSlot)
    {
        return base.CanHold(sourceSlot) && BlockOpticsHousing.IsLensPanel(sourceSlot?.Itemstack);
    }
}
