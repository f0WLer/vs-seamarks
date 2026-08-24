using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace Seamarks.Beacon;

// The directional/rotational beam: a fixed placement heading (the visible cone faces it) and
// mechanical sensing (a driven shaft below turns the fixed cone into an all-directions pulse).
// Server-authoritative; contributes HeadingRad, MechanicalActive and the beam signature to BeaconInfo.
//
// The signature itself belongs to the fitted Optics Housing, so this reads it back off the reflector
// behavior at publish time. Doing the write here keeps the two contributors independent of the order
// they happen to run in.
public class BEBehaviorBeaconBeam : BEBehaviorBeacon, IBeaconInfoContributor, IBeaconBurnListener
{
    private float headingRad;

    // A driven shaft's network speed decays gradually instead of snapping to 0, so a small epsilon stops
    // it flickering on and off through the tail of that decay.
    private const float MechanicalSpeedThreshold = 0.001f;
    private bool lastPushedMechanicallyActive;
    private long mechListenerId;

    // Sweep speed is quantized before it drives anything. A windmill's speed drifts continuously, and
    // every distinct value would mean re-anchoring the sweep and resyncing every observer, so only a step
    // of this size counts as a change.
    private const float SpeedQuantum = 0.05f;

    // The live sweep anchor: the angle reached when the speed last changed, when that was, and the period
    // in force since. Rebuilding these on a speed change is what keeps the beam from jumping.
    private float sweepAnchorRad;
    private double sweepAnchorTotalHours = -1;
    private float sweepPeriodHours;
    private float lastQuantizedSpeed;

    public float HeadingRad => headingRad;

    // The optic renderer sweeps off these, the same anchor the far blip gets through BeaconInfo, so the
    // spinning model and the distant flash follow one speed. Synced to the client with the tree.
    public float SweepAnchorRad => sweepAnchorRad;
    public double SweepAnchorTotalHours => sweepAnchorTotalHours;
    public float SweepPeriodHours => sweepPeriodHours;

    // Reads the block directly below instead of registering as a mechanical-power consumer. A driven
    // shaft's network carries a nonzero speed no matter who consumes it, and staying out of the consumer
    // list keeps the block away from the engine's mech renderer, which would redraw it at an unscaled 1x.
    public bool IsMechanicallyActive => MathF.Abs(ShaftBelowSpeed()) > MechanicalSpeedThreshold;

    // A Governor below drives its network at the capped speed, so its own node carries exactly the speed
    // to read; a plain shaft carries whatever the source runs at. Either way the node facing us has it.
    private float ShaftBelowSpeed()
    {
        var below = Api?.World.BlockAccessor.GetBlockEntity(Pos.DownCopy());
        return below?.GetBehavior<BEBehaviorMPBase>()?.Network?.Speed ?? 0f;
    }

    public BEBehaviorBeaconBeam(BlockEntity blockentity) : base(blockentity) { }

    public void Contribute(BeaconInfo info)
    {
        info.HeadingRad = headingRad;
        info.MechanicalActive = IsMechanicallyActive;
        info.SweepAnchorRad = sweepAnchorRad;
        info.SweepAnchorTotalHours = sweepAnchorTotalHours;
        info.SweepPeriodHours = sweepPeriodHours;
        // No housing fitted means the plain single beam, decided here once so no renderer has to check.
        BeamSignature signature = Beacon.GetBehavior<BEBehaviorBeaconReflector>()?.Housing ?? BeamSignature.None;
        signature.ApplyTo(info);
    }

    // Moves the sweep onto a new speed without breaking its continuity: work out the angle it has reached
    // under the old period, make that the new anchor, then start the new period from now. Returns whether
    // anything changed, so the caller only republishes on a real step.
    private bool ReanchorSweep(float shaftSpeed)
    {
        float quantized = MathF.Round(shaftSpeed / SpeedQuantum) * SpeedQuantum;
        if (quantized == lastQuantizedSpeed && sweepAnchorTotalHours >= 0) return false;

        double now = BeamState.SweepNowHours(Api!.World);
        BeamProfile beam = BeaconArchetypeData.Of(Beacon.Tier).Beam;

        // Where the beam is pointing right now, under the period it has been using.
        float angleNow = headingRad;
        if (sweepPeriodHours > 0f && sweepAnchorTotalHours >= 0)
        {
            double revs = (now - sweepAnchorTotalHours) / sweepPeriodHours;
            angleNow = sweepAnchorRad + (float)(GameMath.Mod(revs, 1.0) * GameMath.TWOPI);
        }

        lastQuantizedSpeed = quantized;
        sweepAnchorRad = GameMath.Mod(angleNow, GameMath.TWOPI);
        sweepAnchorTotalHours = now;
        sweepPeriodHours = BeamState.SweepPeriodHours(in beam, quantized);
        return true;
    }

    public void OnBurnStateChanged(bool burning)
    {
        if (Api?.Side != EnumAppSide.Server) return;
        if (burning && mechListenerId == 0) lastPushedMechanicallyActive = IsMechanicallyActive; // fresh baseline
        ReconcileListener(ref mechListenerId, burning, CheckMechanicalStateChanged);
    }

    // Snaps to the nearest of 8 compass directions, set once right after placement.
    public void SetHeadingFacingPlayer(IPlayer byPlayer)
    {
        const float directionIntervalRad = MathF.PI / 4f;
        double dx = byPlayer.Entity.Pos.X - Pos.X;
        double dz = byPlayer.Entity.Pos.Z - Pos.Z;
        headingRad = MathF.Round((float)Math.Atan2(dx, dz) / directionIntervalRad) * directionIntervalRad;
        Beacon.MarkStateChanged();
    }

    // Edge detector for the shaft-below check, and the one place the sweep speed gets re-read. Pushes when
    // the on/off state flips or the drive speed steps to a new quantum, so a Governor's cap and a
    // shifting wind both reach every observer, including the far ones watching only the blip.
    private void CheckMechanicalStateChanged(float dt)
    {
        bool active = IsMechanicallyActive;
        bool sweepChanged = ReanchorSweep(active ? ShaftBelowSpeed() : 0f);
        if (active == lastPushedMechanicallyActive && !sweepChanged) return;
        lastPushedMechanicallyActive = active;
        Beacon.MarkStateChanged();
    }

    // The old per-beacon beamCount/beamColor*/beamWidth* keys are no longer read. Beacons saved with them
    // load as plain and take their signature from whatever housing gets fitted.
    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        tree.SetFloat("headingRad", headingRad);
        tree.SetFloat("sweepAnchorRad", sweepAnchorRad);
        tree.SetDouble("sweepAnchorHours", sweepAnchorTotalHours);
        tree.SetFloat("sweepPeriodHours", sweepPeriodHours);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        headingRad = tree.GetFloat("headingRad", 0f);
        sweepAnchorRad = tree.GetFloat("sweepAnchorRad", 0f);
        sweepAnchorTotalHours = tree.GetDouble("sweepAnchorHours", -1);
        sweepPeriodHours = tree.GetFloat("sweepPeriodHours", 0f);
    }
}
