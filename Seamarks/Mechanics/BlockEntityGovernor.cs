using System.Text;
using Seamarks.Mechanics.Client;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace Seamarks.Mechanics;

// Owns the one setting the two halves share: the ceiling the output is allowed to reach. Both MP
// behaviors hang off this block entity, the lower one reading the incoming network and the upper one
// driving the outgoing one.
public class BlockEntityGovernor : BlockEntity
{
    public const float MinMaxSpeed = 0.05f;
    public const float MaxMaxSpeed = 2f;
    public const float DefaultMaxSpeed = 0.5f;

    // Client to server: the new cap the slider settled on, four bytes of float.
    public const int SetSpeedPacketId = 1001;

    private float maxSpeed = DefaultMaxSpeed;
    private GuiDialogGovernor? dialog;

    public float MaxSpeed => maxSpeed;

    public float SetMaxSpeed(float value)
    {
        maxSpeed = GameMath.Clamp(value, MinMaxSpeed, MaxMaxSpeed);
        MarkDirty(true);
        return maxSpeed;
    }

    private BEBehaviorGovernor? Node => GetBehavior<BEBehaviorGovernor>();

    public float InputSpeed => Node?.InputSpeed ?? 0f;
    public float OutputSpeed => Node?.Network?.Speed ?? 0f;

    // The angle the visible head is turned to, read off the incoming network so it tracks the shafts
    // driving it. GovernorRenderer spins the whole flyball model by this each frame.
    public float SpinAngleRad => Node?.InputAngleRad ?? 0f;

    private GovernorRenderer? renderer;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api is ICoreClientAPI capi) renderer = new GovernorRenderer(capi, this);
    }

    // Opens the cap slider on the client. The server runs this interaction too, where it does nothing.
    public void OnInteract(IPlayer byPlayer)
    {
        if (Api is not ICoreClientAPI capi) return;
        if (dialog != null && dialog.IsOpened())
        {
            dialog.TryClose();
            return;
        }
        dialog = new GuiDialogGovernor(capi, Pos, MinMaxSpeed, MaxMaxSpeed, maxSpeed);
        dialog.TryOpen();
    }

    public override void OnReceivedClientPacket(IPlayer fromPlayer, int packetid, byte[] data)
    {
        base.OnReceivedClientPacket(fromPlayer, packetid, data);
        if (packetid == SetSpeedPacketId && data?.Length >= 4)
        {
            SetMaxSpeed(BitConverter.ToSingle(data, 0));
        }
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        dialog?.TryClose();
        renderer?.Dispose();
        renderer = null;
    }

    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        dialog?.TryClose();
        renderer?.Dispose();
        renderer = null;
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        dsc.AppendLine(Lang.Get("seamarks:info-governor", MathF.Abs(InputSpeed), maxSpeed, MathF.Abs(OutputSpeed)));
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetFloat("maxSpeed", maxSpeed);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        maxSpeed = tree.GetFloat("maxSpeed", DefaultMaxSpeed);
    }
}
