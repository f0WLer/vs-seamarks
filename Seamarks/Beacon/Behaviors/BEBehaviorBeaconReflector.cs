using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace Seamarks.Beacon;

public enum PlateAddResult { Added, Full, MetalMismatch }

// Range-boosting attachments: reflector plates (up to a per-tier max, all one metal) and a single
// Optics Housing. Server-authoritative; contributes the plate/housing fields to BeaconInfo so the flat
// client path can compute range, and the block can drop them on break.
//
// The housing carries the beam signature, so this behavior is where a fitted one lives.
// BEBehaviorBeaconBeam reads it back out when it publishes, and a null housing is what makes a bare
// beacon a single plain beam.
public class BEBehaviorBeaconReflector : BEBehaviorBeacon, IBeaconInfoContributor
{
    private int plateCount;
    private PlateMetal plateMetal; // meaningless while plateCount == 0
    private BeamSignature? housing;

    public int PlateCount => plateCount;
    public PlateMetal PlateMetal => plateMetal;
    public BeamSignature? Housing => housing;

    public BEBehaviorBeaconReflector(BlockEntity blockentity) : base(blockentity) { }

    public void Contribute(BeaconInfo info)
    {
        info.PlateCount = plateCount;
        info.PlateMetal = plateMetal;
        info.HasHousing = housing != null;
    }

    public bool TryAddPlate(PlateMetal metal, out PlateAddResult result)
    {
        int maxPlates = BeaconArchetypeData.Of(Beacon.Tier).MaxPlates;
        if (plateCount >= maxPlates) { result = PlateAddResult.Full; return false; }
        if (plateCount > 0 && plateMetal != metal) { result = PlateAddResult.MetalMismatch; return false; }

        plateMetal = metal;
        plateCount++;
        Beacon.MarkStateChanged();
        result = PlateAddResult.Added;
        return true;
    }

    public bool TryRemovePlate(out PlateMetal removedMetal)
    {
        removedMetal = plateMetal;
        if (plateCount <= 0) return false;

        plateCount--;
        if (plateCount == 0) plateMetal = default; // let a different metal start fresh next time
        Beacon.MarkStateChanged();
        return true;
    }

    public bool TryAttachHousing(BeamSignature signature)
    {
        if (housing != null) return false;
        housing = signature;
        Beacon.MarkStateChanged();
        return true;
    }

    public bool TryDetachHousing(out BeamSignature? detached)
    {
        detached = housing;
        if (housing == null) return false;
        housing = null;
        Beacon.MarkStateChanged();
        return true;
    }

    // Retuning a fitted housing through the beam commands.
    public void HousingChanged() => Beacon.MarkStateChanged();

    // Same tree keys the monolithic block entity used, so existing saved beacons keep their plates. The
    // signature is nested under its own key so an absent one reads back as "no housing" without needing a
    // separate flag.
    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        tree.SetInt("plateCount", plateCount);
        tree.SetInt("plateMetal", (int)plateMetal);
        tree.SetBool("hasHousing", housing != null);
        housing?.ToTree(tree, "housing");
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        plateCount = tree.GetInt("plateCount", 0);
        plateMetal = (PlateMetal)tree.GetInt("plateMetal", 0);
        // "hasPrism" is what beacons saved before the housing existed wrote. Reading it keeps their
        // attachment in place, as a plain signature, so detaching one still hands back a block.
        bool fitted = tree.GetBool("hasHousing", tree.GetBool("hasPrism", false));
        housing = fitted ? BeamSignature.FromTree(tree, "housing") : null;
    }
}
