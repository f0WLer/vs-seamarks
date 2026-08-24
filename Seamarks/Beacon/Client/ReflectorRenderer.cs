using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon.Client;

// Draws the OilLamp's reflector as its own mesh, turned to the beam heading, so the opening always faces
// where the light is thrown and sweeps with the beam on a driven beacon. The static parts (burner,
// chimney, base) come through the normal chunk mesh, since the blocktype's ignoreElements drops the
// Reflector subtree out of it, so this only ever draws the optic.
//
// Uses PreparedStandardShader at the block position, the same way the vanilla quern's top renderer does,
// so the optic picks up real world block-light instead of looking flat. Camera-relative, which keeps the
// maths in single precision far from the origin. The engine won't clean renderers up on its own, so the
// owning block entity disposes this on both break and unload.
public sealed class ReflectorRenderer : IRenderer
{
    public double RenderOrder => 0.5;   // opaque geometry; order among opaque draws doesn't matter
    public int RenderRange => 48;       // the optic is a close-range detail, culled well before the blip

    // The same axis the static parts are oriented about, so the optic sits rigid with the lamp at rest
    // and only turns relative to it once driven.
    private static Vec3f Pivot => BlockEntityBeacon.ModelPivot;

    // Lines the model's built-in forward up with heading 0. The model already points along the beam's
    // local +Z so this sits at zero, but it's here as a one-line knob if a model edit ever leaves the
    // optic pointing the wrong way.
    private const float ForwardOffsetRad = 0f;

    // The housing shape faces opposite the reflector, so it draws half a turn round from it about the same
    // axis. It still sweeps with the reflector, just mounted the other way.
    private const float HousingForwardOffsetRad = MathF.PI;

    // What the standard shader calls its texture sampler. The entity shader calls the equivalent "tex2d",
    // and passing that name here throws a KeyNotFoundException out of the render loop, which takes the
    // client down with it.
    private const string StandardSampler = "tex";

    private readonly ICoreClientAPI capi;
    private readonly BlockEntityBeacon beacon;
    private readonly BlockPos pos;
    private readonly Matrixf modelMat = new();

    // Uploaded per texture, not as one buffer. The block atlas runs to several pages once enough mods are
    // loaded, and a shape can easily draw its metal from one page and its glass from another. Binding a
    // single page and drawing the lot samples the wrong page for anything that didn't land on it, which
    // shows up as a face wearing some unrelated texture, or as no face at all where the coordinates fall on
    // empty atlas space. The engine's own inventory meshes go up this way for the same reason, which is why
    // a beacon looks right in the creative menu and wrong once placed.
    private MultiTextureMeshRef? meshRef;

    // The fitted lens, uploaded once whether or not one is attached and left undrawn while it
    // isn't. Fitting a housing is a block-entity state change, not a variant swap, so this avoids
    // rebuilding and re-uploading a mesh every time one goes on or comes off.
    //
    // Split by material. The brass goes out with the reflector in the opaque stage, where it gets proper
    // world lighting and sorts against everything else. The glass can't: the opaque stage writes depth for
    // every fragment, so blending there shows whatever was drawn before it and hides whatever comes after.
    // It waits for AfterOIT, once the world's own transparent geometry is down. That's the same stage the
    // beam and blip use, and the stage vanilla itself uses for entity transparency.
    private MultiTextureMeshRef? housingMeshRef;
    private MultiTextureMeshRef? housingGlassRef;

    // VS_Gear: shares the reflector's mesh, pivot and heading, but draws unconditionally, since it sits
    // outside the optic and so has nothing to clip through when a multi-panel housing hides the reflector.
    private readonly MultiTextureMeshRef? gearMeshRef;

    public ReflectorRenderer(ICoreClientAPI capi, BlockEntityBeacon beacon, MeshData mesh, MeshData? housingMesh, MeshData? gearMesh)
    {
        this.capi = capi;
        this.beacon = beacon;
        pos = beacon.Pos;
        meshRef = capi.Render.UploadMultiTextureMesh(mesh);
        SetHousingMesh(housingMesh);
        gearMeshRef = gearMesh == null ? null : capi.Render.UploadMultiTextureMesh(gearMesh);

        capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "seamarksreflector");
        capi.Event.RegisterRenderer(this, EnumRenderStage.AfterOIT, "seamarksreflectorglass");
    }

    // The housing's look depends on its beam count, so this gets called again whenever that changes. Splits
    // the composed mesh into its opaque and glass halves and re-uploads both, replacing whatever was there.
    public void SetHousingMesh(MeshData? housingMesh)
    {
        housingMeshRef?.Dispose();
        housingGlassRef?.Dispose();
        housingMeshRef = null;
        housingGlassRef = null;
        if (housingMesh == null) return;

        (MeshData solid, MeshData? glass) = OpticsHousingMesh.SplitByPass(housingMesh);
        if (solid.VerticesCount > 0) housingMeshRef = capi.Render.UploadMultiTextureMesh(solid);
        if (glass is { VerticesCount: > 0 }) housingGlassRef = capi.Render.UploadMultiTextureMesh(glass);
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        var player = capi.World.Player?.Entity;
        if (player == null) return;

        // One behavior lookup covers both housing questions: whether it's fitted at all, and whether it
        // wraps the optic in enough panels to hide the bare reflector.
        BeamSignature? housing = beacon.Housing;
        bool hasHousing = housing != null;
        bool reflectorHidden = (housing?.BeamCount ?? 1) >= 2;

        bool glassStage = stage == EnumRenderStage.AfterOIT;
        if (glassStage && (housingGlassRef == null || !hasHousing)) return;
        if (!glassStage && meshRef == null) return;

        IRenderAPI rpi = capi.Render;
        Vec3d camPos = player.CameraPos;
        float heading = beacon.CurrentReflectorHeadingRad();

        rpi.GlDisableCullFace(); // the optic has thin two-sided panels
        rpi.GlToggleBlend(true);

        // No Tex2D set here: each draw below binds the atlas page that its own slice of the mesh came from.
        IStandardShaderProgram prog = rpi.PreparedStandardShader(pos.X, pos.Y, pos.Z);
        prog.ViewMatrix = rpi.CameraMatrixOriginf;
        prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;

        // The glass and the brass housing both sit at the housing's own facing, half a turn from the
        // reflector. Sharing the heading keeps all three locked together as the optic sweeps.
        if (glassStage)
        {
            SetModelMatrix(prog, camPos, heading + HousingForwardOffsetRad);
            rpi.RenderMultiTextureMesh(housingGlassRef, StandardSampler);
        }
        else
        {
            // The reflector and VS_Gear share the same heading (ForwardOffsetRad is 0), so one matrix
            // serves both draws instead of rebuilding the identical rotation twice.
            bool drawReflector = meshRef != null && !reflectorHidden;
            if (drawReflector || gearMeshRef != null) SetModelMatrix(prog, camPos, heading + ForwardOffsetRad);

            // A 2- or 3-beam housing wraps the optic in panels, so the reflector inside would just clip
            // through them and is left undrawn. The opaque stage still runs for the brass housing itself.
            if (drawReflector) rpi.RenderMultiTextureMesh(meshRef, StandardSampler);
            // VS_Gear sits outside the optic, so it keeps turning in view whether or not the reflector
            // itself is hidden behind a housing.
            if (gearMeshRef != null) rpi.RenderMultiTextureMesh(gearMeshRef, StandardSampler);
            if (housingMeshRef != null && hasHousing)
            {
                SetModelMatrix(prog, camPos, heading + HousingForwardOffsetRad);
                rpi.RenderMultiTextureMesh(housingMeshRef, StandardSampler);
            }
        }
        prog.Stop();

        rpi.GlEnableCullFace();
    }

    private void SetModelMatrix(IStandardShaderProgram prog, Vec3d camPos, float rotationRad)
    {
        prog.ModelMatrix = modelMat
            .Identity()
            .Translate(pos.X - camPos.X, pos.Y - camPos.Y, pos.Z - camPos.Z)
            .Translate(Pivot.X, Pivot.Y, Pivot.Z)
            .RotateY(rotationRad)
            .Translate(-Pivot.X, -Pivot.Y, -Pivot.Z)
            .Values;
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterOIT);
        meshRef?.Dispose();
        meshRef = null;
        gearMeshRef?.Dispose();
        housingMeshRef?.Dispose();
        housingMeshRef = null;
        housingGlassRef?.Dispose();
        housingGlassRef = null;
    }
}
