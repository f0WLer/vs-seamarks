using Seamarks.Mechanics;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Seamarks.Mechanics.Client;

// Draws the Governor's flyball head: the whole assembly turning about its vertical shaft at the speed of
// the network feeding it, the flyballs flung further out the faster it spins. The spin angle comes off the
// mechanical network itself so the head stays in step with the shafts bolted to it, and the extension
// eases toward its target so the weights look like they carry some weight. The placed block draws nothing
// of its own (empty world shape), so this is the whole of what renders it.
public sealed class GovernorRenderer : IRenderer
{
    public double RenderOrder => 0.5;   // opaque geometry, order among opaque draws doesn't matter
    public int RenderRange => 48;       // a close-range detail

    // How quickly the shown extension chases its target, per second. Low enough that the arms settle rather
    // than snap when the cap is retuned or the source jumps.
    private const float ExtensionEaseRate = 3f;

    // What the standard shader calls its texture sampler. The entity shader calls the equivalent "tex2d",
    // and passing that name here throws a KeyNotFoundException out of the render loop, which takes the
    // client down with it.
    private const string StandardSampler = "tex";

    private readonly ICoreClientAPI capi;
    private readonly BlockEntityGovernor be;
    private readonly BlockPos pos;
    private readonly Matrixf modelMat = new();

    // The head in drawable pieces: the gearbox housing, which holds still, and the nodes that turn, each
    // carrying the upright line it turns about.
    private (MultiTextureMeshRef Mesh, Vec3f? SpinPivot)[] parts = [];
    private int currentStep = -1;
    private float shownExtension;

    public GovernorRenderer(ICoreClientAPI capi, BlockEntityGovernor be)
    {
        this.capi = capi;
        this.be = be;
        pos = be.Pos;
        capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "seamarksgovernor");
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        var player = capi.World.Player?.Entity;
        if (player == null) return;

        // The arms sit at whichever is higher, the speed the cap is set to or the speed actually coming in,
        // both read against the highest cap the block allows. So the cap alone decides where the mechanism
        // rests: wind it to four fifths and the weights idle four fifths of the way out, and they stay
        // there while the drive spins up underneath, since nothing is being held back yet. Only once the
        // input climbs past the cap, the point where the governor starts throttling, do they lift further,
        // running out the last of their travel as the input approaches the top. Eased so retuning the cap
        // swings them over to the new pose instead of teleporting them.
        float target = GameMath.Clamp(
            MathF.Max(be.MaxSpeed, MathF.Abs(be.InputSpeed)) / BlockEntityGovernor.MaxMaxSpeed, 0f, 1f);
        shownExtension += (target - shownExtension) * MathF.Min(1f, deltaTime * ExtensionEaseRate);

        int step = (int)MathF.Round(shownExtension * GovernorMesh.ExtensionSteps);
        if (step != currentStep) SetStep(step);
        if (parts.Length == 0) return;

        IRenderAPI rpi = capi.Render;
        Vec3d camPos = player.CameraPos;
        float spin = be.SpinAngleRad;

        rpi.GlToggleBlend(false);
        // No Tex2D set here: each part draws with the atlas page its own slice of the mesh came from. See
        // the upload in SetStep for why binding one page isn't enough.
        IStandardShaderProgram prog = rpi.PreparedStandardShader(pos.X, pos.Y, pos.Z);
        prog.ViewMatrix = rpi.CameraMatrixOriginf;
        prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;

        foreach ((MultiTextureMeshRef mesh, Vec3f? pivot) in parts)
        {
            modelMat.Identity().Translate(pos.X - camPos.X, pos.Y - camPos.Y, pos.Z - camPos.Z);
            if (pivot != null)
            {
                modelMat
                    .Translate(pivot.X, pivot.Y, pivot.Z)
                    .RotateY(spin)
                    .Translate(-pivot.X, -pivot.Y, -pivot.Z);
            }
            prog.ModelMatrix = modelMat.Values;
            rpi.RenderMultiTextureMesh(mesh, StandardSampler);
        }
        prog.Stop();
    }

    // The meshes belong to GovernorMesh's cache and are shared with every other Governor on the client, so
    // this only ever swaps which set it points at. Disposing them here would blank every other one.
    private void SetStep(int step)
    {
        parts = GovernorMesh.GetUploaded(capi, step) ?? [];
        currentStep = step;
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
    }
}
