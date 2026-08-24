using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace Seamarks.Mechanics;

// The Governor's one mechanical node: a source on the network above it, driven toward the incoming speed
// with the cap laid over the top. It joins only the outgoing (upper) network, never the one below, because
// two live nodes at a single block position can't be told apart by the power engine, which keys them by
// position and merges them back into one. So the incoming speed is read straight off the block below
// instead, the way the vanilla transmission reads the far side it isn't wired to.
//
// The drive is the vanilla rotor's: push while the network runs slower than the target, resist once it
// runs past. That resistance is what actually holds the output down to the cap when something downstream
// would otherwise freewheel faster.
public class BEBehaviorGovernor : BEBehaviorMPBase
{
    // How hard it can drive its network. Roughly a windmill's authority, so a Governor carries what the
    // source could have carried directly.
    private const float TorqueFactor = 0.9f;

    // Idle drag, which keeps the output network from coasting forever after the input stops.
    private const float IdleResistance = 0.0015f;

    // Ramp toward a new target instead of stepping onto it, so a gust or a retune eases in. Kept brisk:
    // the input feeding a Governor is already steady, so the only thing that steps is the cap the player
    // just set, and they expect that to take hold at once, not crawl in over a couple of seconds.
    private const double AccelerationFactor = 0.5;

    private double capableSpeed;
    private long clampListenerId;

    // A placed block entity's own position never moves, so this is computed once instead of on every
    // InputNode read (InputSpeed/InputAngleRad are read every render frame, and TargetSpeed every
    // mechanical tick).
    private BlockPos posBelow = null!;

    public BEBehaviorGovernor(BlockEntity blockentity) : base(blockentity)
    {
        OutFacingForNetworkDiscovery = BlockFacing.UP;
    }

    public override void Initialize(ICoreAPI api, JsonObject properties)
    {
        base.Initialize(api, properties);
        posBelow = Position.DownCopy();
        AxisSign = [0, 1, 0]; // vertical shaft
        // Torque can only push the output up. Its fall is left to the network's own drag, which coasts on
        // purpose so windmills freewheel, and that coast is far too slow for a governor and lets the output
        // outrun a stopped input. A per-tick ceiling holds the output at or below the target both ways. The
        // interval matches the mechanical-power tick, so the two stay in step.
        if (api.Side == EnumAppSide.Server) clampListenerId = Blockentity.RegisterGameTickListener(ClampOutput, 20);
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        if (clampListenerId != 0) Blockentity.UnregisterGameTickListener(clampListenerId);
    }

    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        if (clampListenerId != 0) Blockentity.UnregisterGameTickListener(clampListenerId);
    }

    // The output may sit below the target under a heavy load, but it must never run past it. The engine
    // recomputes the speed from a snapshot taken before the node loop, so a write inside GetTorque would be
    // discarded; clamping here, after the network has ticked, is what actually holds. The target already
    // folds in both the cap and the input, so this covers a lowered cap and a stopped input alike.
    private void ClampOutput(float dt)
    {
        if (Network == null) return;
        float ceiling = MathF.Abs(TargetSpeed);
        if (MathF.Abs(Network.Speed) > ceiling) Network.Speed = Network.Speed < 0f ? -ceiling : ceiling;
    }

    // The node driving the network directly below, read but never joined. Null when nothing mechanical
    // sits under the Governor.
    private BEBehaviorMPBase? InputNode =>
        Api?.World.BlockAccessor.GetBlockEntity(posBelow)?.GetBehavior<BEBehaviorMPBase>();

    // Signed speed coming up from below, the driving node's geared ratio folded in so a large gear under
    // the block reads the way it turns here.
    public float InputSpeed
    {
        get
        {
            var node = InputNode;
            return node?.Network == null ? 0f : node.Network.Speed * node.GearedRatio;
        }
    }

    // The incoming shaft's angle, so the flyball head turns in step with what feeds it.
    public float InputAngleRad
    {
        get
        {
            var node = InputNode;
            return node?.Network == null ? 0f : node.AngleRad;
        }
    }

    // The clamp. A plain min() would let reverse rotation through untouched, since a negative speed is
    // always below the cap, so the magnitude is what gets limited and the direction is put back after.
    private float TargetSpeed
    {
        get
        {
            var governor = Blockentity as BlockEntityGovernor;
            float input = InputSpeed;
            float max = governor?.MaxSpeed ?? BlockEntityGovernor.DefaultMaxSpeed;
            float limited = MathF.Min(MathF.Abs(input), max);
            return input < 0f ? -limited : limited;
        }
    }

    public override float GetResistance() => IdleResistance;

    // Lifted from the vanilla rotor: push by however far the network is short of the target, and turn the
    // overshoot into resistance once it runs past.
    public override float GetTorque(long tick, float speed, out float resistance)
    {
        float target = TargetSpeed;
        capableSpeed += (Math.Abs(target) - capableSpeed) * AccelerationFactor;

        // Which way this node drives its network, so the Governor turns the output the same way the input
        // is going and not always one fixed way.
        float dir = (propagationDir == OutFacingForNetworkDiscovery) ? 1f : -1f;
        if (target < 0f) dir = -dir;

        float absSpeed = Math.Abs(speed);
        double excessSpeed = absSpeed - capableSpeed;
        bool wrongDirection = dir * speed < 0f;

        resistance = wrongDirection
            ? IdleResistance * TorqueFactor * Math.Min(0.8f, absSpeed * 400f)
            : (excessSpeed > 0.0 ? (float)(IdleResistance * Math.Min(0.2, excessSpeed * excessSpeed * 80.0)) : 0f);

        double power = wrongDirection ? capableSpeed : capableSpeed - absSpeed;
        return (float)Math.Max(0.0, power) * TorqueFactor * dir;
    }

    // A source, so power never continues through it down into the network below.
    public override MechPowerPath[] GetMechPowerExits(MechPowerPath fromExitTurnDir) => [];

    public override void WasPlaced(BlockFacing connectedOnFacing) { }
}
