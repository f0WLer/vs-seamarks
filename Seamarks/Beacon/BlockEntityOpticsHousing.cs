using Seamarks.Beacon.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace Seamarks.Beacon;

// A placed housing the player fits Lens Panels into through a slot GUI. The three slots are the source
// of truth for the beams: their filled panels, compacted in slot order, become the ordered signature the
// mesh draws and a fitted beacon reads. The signature also stores the per-beam colors, which have no
// item to live in, and it's what travels on the dropped or picked stack so break-and-replace is a no-op.
public class BlockEntityOpticsHousing : BlockEntityOpenableContainer
{
    private const int SlotCount = BeamSignature.MaxBeamCount;

    private readonly InventoryGeneric inventory;
    private BeamSignature signature = new();

    public BlockEntityOpticsHousing()
    {
        inventory = new InventoryGeneric(SlotCount, null, null, (id, self) => new ItemSlotLensPanel(self));
    }

    public override InventoryBase Inventory => inventory;
    public override string InventoryClassName => "opticshousing";

    public BeamSignature Signature => signature;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        SyncPanelsFromInventory();
        if (api.Side == EnumAppSide.Server) inventory.SlotModified += OnSlotModified;
    }

    private void OnSlotModified(int slotId)
    {
        SyncPanelsFromInventory();
        MarkDirty(true);
    }

    // Rebuild the ordered panel list from whichever slots hold a panel, left to right. Each slot's glass
    // retints its beam every time this runs (BeamSignature.SetColorFromPanel skips a beam a player has
    // retuned by hand), so swapping to a different colored panel, or back to clear, always updates the
    // beam instead of only the first panel ever seated getting to set it.
    private void SyncPanelsFromInventory()
    {
        var panels = new List<(int Type, string GlassColor)>(SlotCount);
        for (int i = 0; i < inventory.Count; i++)
        {
            ItemStack? stack = inventory[i].Itemstack;
            if (!BlockOpticsHousing.LensPanelType(stack, out int type)) continue;
            string glassColor = BlockOpticsHousing.LensPanelColor(stack);
            panels.Add((type, glassColor));
            signature.SetColorFromPanel(panels.Count, BeamColor.LensColorPacked(glassColor));
        }
        signature.SetPanels(panels);
    }

    // Opens the slot panel on the client; the server side just reports handled. The base container plumbs
    // the slot moves and the open/close handshake.
    public override bool OnPlayerRightClick(IPlayer byPlayer, BlockSelection blockSel)
    {
        if (Api is ICoreClientAPI capi)
        {
            toggleInventoryDialogClient(byPlayer, () =>
                new GuiDialogOpticsHousing(Lang.Get("seamarks:gui-housing-title"), inventory, Pos, capi, this));
        }
        return true;
    }

    // Called after the signature's colors are retuned by the beam-color command. MarkDirty(true) re-runs
    // OnTesselation so the model reglazes, and syncs the change out.
    public void SignatureChanged() => MarkDirty(true);

    // The fitted panels ride the dropped housing stack (BlockOpticsHousing.GetDrops), so they must not
    // also spill as loose items, which means skipping BlockEntityContainer.OnBlockBroken and its DropAll.
    // That base also forwards to the behaviors, and C# can't reach past the direct base, so it's
    // reproduced here instead of being lost along with the drop.
    public override void OnBlockBroken(IPlayer? byPlayer = null)
    {
        foreach (var behavior in Behaviors) behavior.OnBlockBroken(byPlayer);
    }

    // Carries the tuning in from the stack that placed it, and seats a panel item in each slot the saved
    // signature calls for, so a rebuilt housing looks and behaves exactly as it did when broken.
    public override void OnBlockPlaced(ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(byItemStack);
        if (byItemStack == null) return;

        signature = BeamSignature.FromStack(byItemStack);
        // A copy, not the live list: seating each panel fires OnSlotModified, which rebuilds the
        // signature's panels from the half-filled inventory, and iterating the list it's rewriting would
        // drop every panel past the first.
        int[] types = [.. signature.PanelTypes];
        string[] glassColors = [.. signature.PanelColors];
        for (int i = 0; i < inventory.Count; i++)
        {
            Item? panel = i < types.Length
                ? Api.World.GetItem(new AssetLocation(SeamarksModSystem.Domain, $"{BlockOpticsHousing.LensPanelCode}-{types[i]}-{glassColors[i]}"))
                : null;
            inventory[i].Itemstack = panel != null ? new ItemStack(panel) : null;
            inventory[i].MarkDirty();
        }
    }

    // Draws the housing with a lens on each mount a beam leaves through and a cover on the rest. Returning
    // true drops the blocktype's own shape, which is only the bare housing with empty mounts.
    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        if (Api is not ICoreClientAPI capi) return false;

        MeshData? mesh = OpticsHousingMesh.Get(capi, signature);
        if (mesh == null) return false;

        mesher.AddMeshData(mesh);
        return true;
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        signature.ToTree(tree);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        signature = BeamSignature.FromTree(tree);
        // The inventory owns the panel list; the stored one only carries colors across.
        SyncPanelsFromInventory();
    }

    // Everything the fitting GUI needs to label a slot: which beam it drives, its panel name and that
    // beam's color, the width and the nominal flash duration. Null for an empty slot.
    public SlotBeam? SlotBeamAt(int slotIndex)
    {
        if (!BlockOpticsHousing.LensPanelType(inventory[slotIndex].Itemstack, out int type)) return null;

        int beamNumber = 0;
        for (int i = 0; i <= slotIndex; i++)
        {
            if (BlockOpticsHousing.IsLensPanel(inventory[i].Itemstack)) beamNumber++;
        }

        float widthDeg = BeamSignature.WidthForType(type);
        signature.ResolveBeamColor(beamNumber - 1, out float r, out float g, out float b);
        float flashSeconds = BeamState.NominalFlashSeconds(widthDeg, Api.World.Calendar);
        return new SlotBeam(beamNumber, Lang.Get($"{SeamarksModSystem.Domain}:gui-housing-lensname-{type}"), r, g, b, widthDeg, flashSeconds);
    }

    public readonly record struct SlotBeam(int BeamNumber, string Name, float R, float G, float B, float WidthDeg, float FlashSeconds);
}
