using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon;

// A fuelled beacon that burns down over time. Burn state is derived from the time since ignition instead
// of being ticked, so nothing has to run per frame to know whether a beacon is still lit. Everything past
// burning (plates, the housing, the beam, rain dousing, sound) is composed on as a BlockEntityBehavior
// attached per tier in the block JSON. This core owns the fuel and burn state, the lit variant swap, and
// publishing to the server-side far-render registry.
public class BlockEntityBeacon : BlockEntity
{
    private BeaconTier tier;
    private double litAtTotalHours = -1;

    // While lit, what's left is this pool minus the time elapsed since ignition. While unlit, this value
    // is itself the remainder, frozen in place.
    private float fuelPoolHours;

    // The litres backing fuelPoolHours, for liquid tiers only (always 0 on a solid-fuel tier). Tracked as
    // its own pool instead of reverse-computed from hours at display time, since an oil's hours-per-litre
    // can be overridden per code and a beacon might be topped up with more than one oil over its life;
    // reverse-computing off the tier's flat default rate silently mismatched whatever oil actually filled
    // the tank. Kept in the same ratio to fuelPoolHours as it decays, so mixed fills blend proportionally.
    private float fuelPoolLitres;

    // Set by an operator to make this one beacon burn without end: it lights with no fuel and never runs
    // out, and the flag rides the block through save, extinguish and relight, so a central always-on
    // lighthouse stays on across all of them. Fuelling still works and is just moot while it's set.
    private bool everburning;

    // Guards FireBeaconFootprintMigration so it runs at most once per beacon. Temporary: safe to remove
    // alongside that class and this field once old-footprint saves are no longer a realistic concern.
    private bool footprintMigrated;

    // Burnout is time-derived with no per-tick sim, so nothing would otherwise notice it happening. This
    // listener catches it while lit and settles the spent fuel and the lit variant.
    private long expiryCheckListenerId;

    // Client only. The spinning optic gets its own renderer, turned to the beam heading and properly lit,
    // instead of going through the chunk mesh. The blocktype's ignoreElements drops "Reflector" from the
    // default mesh and this draws it. Always present on directional tiers.
    private Client.ReflectorRenderer? reflectorRenderer;

    // Shared vertical axis for orienting and spinning the model, taken from the Reflector element's
    // rotationOrigin ([8,2,9] in 1/16 shape units) converted to 0..1 block space. The static parts are
    // oriented about it to the placement heading and the optic spins about it, so the lamp stays rigid at
    // rest. Both paths have to use this same axis or they drift apart.
    public static readonly Vec3f ModelPivot = new(8f / 16f, 2f / 16f, 9f / 16f);

    // The visible reflector panel's height, not the pivot's: the pivot sits low, down near the bearing the
    // optic actually spins on, so a beam anchored to its Y reads as coming out of the bottom of the block
    // instead of the lens itself. X and Z do line up with the pivot, the panel sits dead-centered over it
    // on those two axes. Started from RPanelCenter's own box center (7/16) and tuned in-game from there.
    public const float ReflectorOpeningYFraction = 0.59f;

    // Built by ignoring every other root instead of selecting "Reflector". The Reflector root is a
    // zero-size pivot, and SelectiveElements matches only the named element and not its children, while
    // IgnoreElements prunes whole subtrees. Ignoring the other roots leaves exactly the Reflector subtree
    // and keeps working as its internals get edited. VS_Gear is its own top-level root, not a Reflector
    // descendant, so it needs excluding here too or it would double up with its own mesh below.
    private static readonly string[] ReflectorExcludedRoots = ["Burner", "ChimneyBase", "Base", "VS_Gear"];

    // The mirror image of ReflectorExcludedRoots: everything but VS_Gear. It spins with the reflector
    // (same pivot, same heading) but sits outside the optic, so it stays visible even when a multi-panel
    // housing hides the reflector itself.
    private static readonly string[] GearExcludedRoots = ["Burner", "ChimneyBase", "Base", "Reflector"];

    public BeaconTier Tier => tier;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api); // creates + initializes the tier's behaviors
        tier = BeaconArchetypeData.TierFromVariantCode(Block?.Variant["tier"]);

        // A save from before the litres pool existed has fuel hours but no matching litres. Back-fill an
        // approximation off the tier's flat default rate, tier being unknown any earlier than this, so the
        // display doesn't drop to 0L until the next top-up.
        if (fuelPoolLitres <= 0f && fuelPoolHours > 0f)
        {
            float rate = BeaconArchetypeData.Of(tier).FuelHoursPerLitre;
            if (rate > 0f) fuelPoolLitres = fuelPoolHours / rate;
        }

        // The rotating optic is a client render concern and only exists on directional (OilLamp) tiers.
        // The lens mesh is uploaded alongside it whether or not one is fitted, and the renderer just skips
        // drawing it while there's no housing, which keeps attaching one from needing a mesh rebuild.
        if (api is ICoreClientAPI capi && BeaconArchetypeData.Of(tier).Beam.Directional)
        {
            MeshData? mesh = BuildReflectorMesh(capi);
            if (mesh != null) reflectorRenderer = new Client.ReflectorRenderer(capi, this, mesh, BuildHousingMesh(capi), BuildGearMesh(capi));
            var initHousing = GetBehavior<BEBehaviorBeaconReflector>()?.Housing;
            renderedHousingKey = initHousing?.PanelKey();
        }

        if (api.Side == EnumAppSide.Server)
        {
            // A beacon can burn out while its chunk is unloaded; settle that now so its spent fuel is
            // zeroed and the lit/unlit variant matches reality on load.
            if (SettleIfBurnedOut()) MarkDirty(true);
            SyncLitBlockVariant();

            if (tier == BeaconTier.Fire && !footprintMigrated)
            {
                Server.FireBeaconFootprintMigration.RunOnce(api, Pos);
                footprintMigrated = true;
                MarkDirty(true);
            }
        }
        SyncState();
    }

    // A beacon placed fresh was already built under the current multiblock footprint, so it has nothing
    // for FireBeaconFootprintMigration to clean up. Marking it migrated up front, instead of leaving the
    // flag false and letting the scan run and find nothing, keeps that scan from ever touching an
    // unrelated multiblock structure's filler cell that happens to land on one of its hardcoded stale
    // offsets.
    public override void OnBlockPlaced(ItemStack? byItemStack = null)
    {
        base.OnBlockPlaced(byItemStack);
        footprintMigrated = true;
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved(); // disposes all tick listeners + notifies behaviors (sound stops)
        reflectorRenderer?.Dispose();
        reflectorRenderer = null;
        if (Api?.Side == EnumAppSide.Server)
        {
            Api.ModLoader.GetModSystem<SeamarksModSystem>()?.Server?.Remove(Pos);
        }
    }

    // The engine does no automatic cleanup of registered renderers, so the optic renderer is disposed on
    // chunk unload too (not just block break), or it would leak and keep drawing a phantom optic.
    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        reflectorRenderer?.Dispose();
        reflectorRenderer = null;
    }

    // Tesselates just the Reflector subtree by ignoring every other root element. Textures resolve off
    // this block's texture source (the blocktype defines every #brass/#candle key). Returned to the
    // renderer, which uploads it and draws it rotated to the heading each frame.
    private MeshData? BuildReflectorMesh(ICoreClientAPI capi)
    {
        var reflectorShape = new CompositeShape
        {
            Base = new AssetLocation(SeamarksModSystem.Domain, "block/lighthouse/argandbeacon"),
            IgnoreElements = ReflectorExcludedRoots,
        };
        capi.Tesselator.TesselateShape(
            "seamarks:argandbeacon-reflector", reflectorShape.Base, reflectorShape,
            out MeshData mesh, capi.Tesselator.GetTextureSource(Block));
        return mesh;
    }

    // VS_Gear, tesselated on its own so it can keep spinning in view even when a multi-panel housing hides
    // the reflector behind it. Null (not an empty mesh) whenever the shape has no such root, so a beacon
    // built against an older argandbeacon.json degrades to just not showing a gear, nothing worse.
    private MeshData? BuildGearMesh(ICoreClientAPI capi)
    {
        var gearShape = new CompositeShape
        {
            Base = new AssetLocation(SeamarksModSystem.Domain, "block/lighthouse/argandbeacon"),
            IgnoreElements = GearExcludedRoots,
        };
        capi.Tesselator.TesselateShape(
            "seamarks:argandbeacon-gear", gearShape.Base, gearShape,
            out MeshData mesh, capi.Tesselator.GetTextureSource(Block));
        return mesh.VerticesCount > 0 ? mesh : null;
    }

    // The fitted optic is the same composed Optics Housing model as the standalone block, panelled to the
    // beacon's beam count and returned already split into its opaque and glass halves. The shape is
    // authored in the lamp's own coordinate space, so drawing it at the beacon origin under the reflector's
    // rotation lines it up with no maths here; the optic renderer supplies both. Null while nothing is
    // fitted, so the renderer draws only the bare reflector.
    private MeshData? BuildHousingMesh(ICoreClientAPI capi)
    {
        var housing = GetBehavior<BEBehaviorBeaconReflector>()?.Housing;
        if (housing == null) return null;
        return Client.OpticsHousingMesh.Get(capi, housing);
    }

    // The panel sequence the fitted optic was last built for, so a change to it (an attach, detach, or a
    // panel added/removed) rebuilds the mesh without redoing it every frame. Null means nothing is fitted.
    private string? renderedHousingKey;

    // Rebuilds the fitted optic's mesh when what it should show has changed. Cheap on a repeat because
    // OpticsHousingMesh caches one mesh per configuration; this only re-splits and re-uploads.
    private void RefreshHousingMesh(ICoreClientAPI capi)
    {
        var housing = GetBehavior<BEBehaviorBeaconReflector>()?.Housing;
        string? key = housing?.PanelKey();
        if (key == renderedHousingKey) return;
        renderedHousingKey = key;
        reflectorRenderer?.SetHousingMesh(housing == null ? null : Client.OpticsHousingMesh.Get(capi, housing));
    }

    // Read per frame by the optic renderer, which derives both HasHousing and ReflectorHidden from this
    // one lookup.
    public BeamSignature? Housing => GetBehavior<BEBehaviorBeaconReflector>()?.Housing;

    // Turns the whole static lamp (everything bar the separately drawn optic) to the placement heading, so
    // the model faces one way as a unit and not just the optic. Tesselates this block's own shape, which
    // is already variant-correct since its ignoreElements always drop the Reflector and drop the Flame on
    // the unlit code, then turns it about the shared axis and replaces the default mesh. Re-runs on
    // MarkDirty(true), which covers setting the placement heading and the lit/unlit swap.
    //
    // Fire has no heading, but still needs a say here: the Fuel pile tracks the actual remaining fuel
    // pool, not the lit/unlit variant (extinguishing by hand freezes the pool instead of clearing it), and
    // a variant's ignoreElements is fixed per variant, so it can't answer a question that continuous. This
    // drops Fuel from a fresh tesselation of its own whenever the pool reads empty, on top of whatever
    // ignoreElements the current variant already carries, and falls through to the plain default mesh
    // otherwise. MarkDirty(true) already fires on every fuel-changing transition (ignite, extinguish,
    // burnout, refuel), so no extra trigger is needed here.
    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        bool skipDefault = base.OnTesselation(mesher, tessThreadTesselator);
        ref readonly BeaconArchetypeData archetype = ref BeaconArchetypeData.Of(tier);

        if (archetype.Beam.Directional)
        {
            float heading = GetBehavior<BEBehaviorBeaconBeam>()?.HeadingRad ?? 0f;
            tessThreadTesselator.TesselateShape(
                "seamarks:argandbeacon-static", Block.Shape.Base, Block.Shape,
                out MeshData mesh, tessThreadTesselator.GetTextureSource(Block));
            mesh.Rotate(ModelPivot, 0f, heading, 0f);
            mesher.AddMeshData(mesh);
            return true; // our rotated copy replaces the default unrotated one
        }

        // An everburning beacon reads as always fed, whatever its actual pool says, since an operator
        // pinned it to burn forever and the pile shouldn't flicker out from under that.
        if (tier == BeaconTier.Fire && !IsEverburning && RemainingFuelHours <= 0f)
        {
            // Cloned so scale/offset/rotation carry over unchanged; a hand-rolled CompositeShape here
            // previously left them at their defaults and rendered the no-fuel mesh unscaled.
            CompositeShape noFuelShape = Block.Shape.Clone();
            noFuelShape.IgnoreElements = [.. noFuelShape.IgnoreElements ?? [], "Fuel"];
            tessThreadTesselator.TesselateShape(
                "seamarks:firebeacon-nofuel", noFuelShape.Base, noFuelShape,
                out MeshData mesh, tessThreadTesselator.GetTextureSource(Block));
            mesher.AddMeshData(mesh);
            return true; // our Fuel-less copy replaces the default mesh
        }

        return skipDefault;
    }

    // The optic's current heading, used by ReflectorRenderer. Mirrors the beam's own rotation
    // (BeamState.CurrentHeadingRad) so the visible reflector and the light beam stay locked together:
    // a fixed placement heading at rest, sweeping while lit and mechanically driven.
    public float CurrentReflectorHeadingRad()
    {
        var beam = GetBehavior<BEBehaviorBeaconBeam>();
        if (beam == null) return 0f;
        return BeamState.SweepHeadingRad(IsRotating, beam.HeadingRad,
            beam.SweepAnchorRad, beam.SweepAnchorTotalHours, beam.SweepPeriodHours,
            BeamState.SweepNowHours(Api.World));
    }

    // Whether the optic is actually turning right now, the same condition CurrentReflectorHeadingRad uses
    // to decide whether to advance the angle. Exposed for anything that only makes sense while the beam is
    // moving, like the mechanical squeak.
    public bool IsRotating =>
        BeaconArchetypeData.Of(tier).Beam.Pattern == BeamPattern.Rotating
        && IsBurning
        && (GetBehavior<BEBehaviorBeaconBeam>()?.IsMechanicallyActive ?? false);

    private double NowHours => Api.World.Calendar.TotalHours;

    // Whether this tier needs fuel at all. Off means it lights empty and never burns down, so IsBurning
    // drops the time-versus-pool check and rests on the anchor alone.
    public bool RequiresFuel => BeaconArchetypeData.Of(tier).RequiresFuel;

    // Whether an operator has pinned this beacon to burn forever.
    public bool IsEverburning => everburning;

    // The effective fuel requirement, folding in the per-beacon override: an everburning beacon needs none
    // even on a tier that normally does.
    private bool NeedsFuel => RequiresFuel && !everburning;

    public bool IsBurning => litAtTotalHours >= 0 && (!NeedsFuel || NowHours - litAtTotalHours < fuelPoolHours);

    // Correct in every state without waiting on the expiry tick to settle. While lit it counts down from
    // the pool, while frozen unlit (put out by hand or never lit at all) it's the stored pool, and a
    // beacon whose anchor is still set but whose time has run out reads 0 instead of the stale pool.
    public float RemainingFuelHours => litAtTotalHours < 0
        ? fuelPoolHours
        : MathF.Max(0f, fuelPoolHours - (float)(NowHours - litAtTotalHours));

    // Mirrors RemainingFuelHours but in litres, decaying in the same proportion as the hours pool so it
    // always agrees with whatever oil (or blend of oils) is actually behind the remaining hours.
    public float RemainingFuelLitres => fuelPoolHours <= 0f ? 0f : fuelPoolLitres * (RemainingFuelHours / fuelPoolHours);

    // Nominal range for an observer at sea level, following the lighthouse convention of quoting range as
    // a property of the light and not of wherever the reader happens to be standing.
    public float MaxRangeAtSeaLevel(float seaLevel) => BeaconVisibility.MaxRange(BuildInfo(), seaLevel);

    // Doesn't ignite: fuelling and lighting are separate actions. Builds on RemainingFuelHours, which is
    // right for a burning, frozen or just-burned-out beacon alike, so topping up can never resurrect a
    // stale pre-burnout pool. Resets the anchor to now while lit to restart the countdown cleanly, and
    // otherwise normalises to unlit so the added fuel stays frozen. litres is 0 for solid fuel, which adds
    // nothing to fuelPoolLitres and leaves the litre display untouched.
    public void AddFuel(float fuelHours, float litres = 0f)
    {
        bool wasBurning = IsBurning;
        float hoursBefore = RemainingFuelHours;
        float litresBefore = RemainingFuelLitres;
        fuelPoolHours = MathF.Min(hoursBefore + fuelHours, BeaconArchetypeData.Of(tier).MaxFuelHours);

        // Only the fraction of fuelHours that actually fit under the cap counts toward litres too, so
        // topping past a full tank doesn't invent litres for hours that were clamped away.
        float hoursAccepted = fuelPoolHours - hoursBefore;
        float litresAccepted = fuelHours > 0f ? litres * (hoursAccepted / fuelHours) : 0f;
        fuelPoolLitres = litresBefore + litresAccepted;

        litAtTotalHours = wasBurning ? NowHours : -1;
        MarkDirty(true);
        SyncState();
    }

    public bool TryIgnite()
    {
        if (IsBurning || (NeedsFuel && RemainingFuelHours <= 0)) return false;
        litAtTotalHours = NowHours;
        MarkDirty(true);
        SyncState();
        SyncLitBlockVariant();
        GetBehavior<BEBehaviorBeaconSound>()?.PlayIgnite();
        return true;
    }

    // Freezes whatever fuel is left instead of discarding it. Safe to call even if already unlit.
    public void Extinguish()
    {
        bool wasBurning = IsBurning;
        float remainingLitres = RemainingFuelLitres; // read before fuelPoolHours changes, since it ratios off the old value
        fuelPoolHours = RemainingFuelHours;
        fuelPoolLitres = remainingLitres;
        litAtTotalHours = -1;
        MarkDirty(true);
        SyncState();
        SyncLitBlockVariant();
        if (wasBurning) GetBehavior<BEBehaviorBeaconSound>()?.PlayExtinguish();
    }

    // Operator toggle for the burn-forever flag. Turning it on lights the beacon straight away if it was
    // dark, so the light comes on at once and stays with no fuel; turning it off hands the beacon back to
    // normal fuel rules and settles it out if it was only coasting on the flag with an empty tank.
    public void SetEverburning(bool value)
    {
        bool wasBurning = IsBurning;
        everburning = value;

        if (value)
        {
            if (litAtTotalHours < 0) litAtTotalHours = NowHours;
        }
        else
        {
            SettleIfBurnedOut();
        }

        MarkDirty(true);
        SyncState();
        SyncLitBlockVariant();

        if (IsBurning && !wasBurning) GetBehavior<BEBehaviorBeaconSound>()?.PlayIgnite();
        else if (!IsBurning && wasBurning) GetBehavior<BEBehaviorBeaconSound>()?.PlayExtinguish();
    }

    // Catches natural fuel expiry. Calling SyncState from inside this listener's own callback, which
    // unregisters it, is safe: removing a GameTickListener from within its own callback nulls the slot
    // instead of shifting it.
    private void CheckBurnExpiry(float dt)
    {
        if (!SettleIfBurnedOut()) return;
        MarkDirty(true);
        SyncState();
        SyncLitBlockVariant();
        GetBehavior<BEBehaviorBeaconSound>()?.PlayExtinguish();
    }

    // Normalises the stored state of a beacon that ran its own fuel pool dry, as opposed to one put out
    // by hand, which already freezes the remainder and clears the anchor. RemainingFuelHours works out
    // correctly without this, so it's hygiene and not the only safeguard. An anchor that's still set
    // while the beacon isn't burning is what identifies a natural burnout.
    private bool SettleIfBurnedOut()
    {
        if (IsBurning || litAtTotalHours < 0) return false;
        fuelPoolHours = 0f;
        fuelPoolLitres = 0f;
        litAtTotalHours = -1;
        return true;
    }

    // Matches the expiry-tick lifecycle and every capability behavior to the current burn state, then
    // republishes to the registry. Idempotent, so it's safe to call from Initialize and every
    // transition. The expiry listener is server-only; the burn listeners (rain/mech/sound) self-guard.
    private void SyncState()
    {
        bool burning = IsBurning;
        if (Api?.Side == EnumAppSide.Server)
        {
            ReconcileListener(ref expiryCheckListenerId, burning, CheckBurnExpiry);
        }
        NotifyBurnListeners(burning);
        PushToRegistry();
    }

    private void NotifyBurnListeners(bool burning)
    {
        foreach (var behavior in Behaviors)
        {
            (behavior as IBeaconBurnListener)?.OnBurnStateChanged(burning);
        }
    }

    private void ReconcileListener(ref long id, bool shouldRun, Action<float> callback) =>
        BEBehaviorBeacon.ReconcileListener(this, ref id, shouldRun, callback);

    // Called by a capability behavior when its contributed state changes (plate added, housing fitted,
    // mechanical toggled, heading set), and by the core's own fuel changes: re-sync to clients and
    // republish the aggregated snapshot. Burn state is unchanged by these, so it skips the listener
    // reconcile that the transition paths do.
    public void MarkStateChanged()
    {
        MarkDirty(true);
        PushToRegistry();
    }

    private void PushToRegistry()
    {
        if (Api?.Side != EnumAppSide.Server) return;
        // Null-conditional on the mod system itself, not just on Server: a mod system whose start-up threw
        // never reaches the mod loader, and every beacon in the world then loads into this method and
        // rethrows, burying the one error that actually mattered under a wall of null references.
        var server = Api.ModLoader.GetModSystem<SeamarksModSystem>()?.Server;
        if (server == null) return;

        if (IsBurning) server.Upsert(BuildInfo());
        else server.Remove(Pos);
    }

    // Swaps between the "lit" and "unlit" block codes so the beacon is only a real light source while
    // burning. ExchangeBlock keeps this same block entity and is what queues the engine's relight pass.
    // A live burn-state read with no block-ID change never gets picked up.
    private void SyncLitBlockVariant()
    {
        if (Api?.Side != EnumAppSide.Server) return;

        string targetState = IsBurning ? "lit" : "unlit";
        if (Block.Variant["lit"] == targetState) return;

        if (Api.World.GetBlock(Block.CodeWithVariant("lit", targetState)) is not Block newBlock) return;
        Api.World.BlockAccessor.ExchangeBlock(newBlock.Id, Pos);
        Block = newBlock;
    }

    // The flat snapshot sent to clients / stored in the registry: core burn fields plus each
    // capability behavior's contributed slice.
    private BeaconInfo BuildInfo()
    {
        var info = new BeaconInfo
        {
            Pos = Pos.Copy(),
            Tier = tier,
            IgnitedAtTotalHours = litAtTotalHours,
            FuelHoursAtIgnition = fuelPoolHours,
            Everburning = everburning,
        };
        foreach (var behavior in Behaviors)
        {
            (behavior as IBeaconInfoContributor)?.Contribute(info);
        }
        return info;
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree); // behaviors persist their own state under their own keys
        tree.SetDouble("ignitedAtTotalHours", litAtTotalHours);
        tree.SetFloat("fuelHoursAtIgnition", fuelPoolHours);
        tree.SetFloat("fuelLitresAtIgnition", fuelPoolLitres);
        tree.SetBool("everburning", everburning);
        tree.SetBool("footprintMigrated", footprintMigrated);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        litAtTotalHours = tree.GetDouble("ignitedAtTotalHours", -1);
        fuelPoolHours = tree.GetFloat("fuelHoursAtIgnition", 0);
        fuelPoolLitres = tree.GetFloat("fuelLitresAtIgnition", 0);
        everburning = tree.GetBool("everburning", false);
        footprintMigrated = tree.GetBool("footprintMigrated", false);

        // How the client finds out a beacon was lit or put out: MarkDirty pushes the fields down and they
        // land here, so re-notify to let the sound behavior resync its loop. Guarded because
        // FromTreeAttributes runs before Initialize on first load, when Api is still null.
        if (Api != null) NotifyBurnListeners(IsBurning);

        // The same push carries a housing being fitted, pulled, or retuned to a new beam count. Rebuild the
        // fitted optic to match, but only when it actually changed.
        if (Api is ICoreClientAPI capi) RefreshHousingMesh(capi);
    }
}
