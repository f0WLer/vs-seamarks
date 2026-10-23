using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon.Client;

// Draws the near and mid-range volumetric beam. Every burning directional beacon in range gets a stack of
// nested additive frustum shells, brightest near the axis and fading out to the halo intensity at the
// edge, turned to the beam's current heading and moved to the lamp. This is real world geometry, so
// unlike the far blip it just depth-tests against terrain normally with no depth faking.
//
// Registered at AfterOIT with the same additive, no-depth-write setup the blip uses, which already
// composites correctly over terrain, water and clouds. The OIT stage would be the other candidate, but
// AfterOIT is what works here for additive world overlays, so the beam follows the blip instead of
// fighting the OIT accumulation buffer.
public sealed class BeamRenderer : IRenderer
{
    public double RenderOrder => 0.1; // additive + no depth write, so order vs the blip doesn't matter
    public int RenderRange => 9999;

    private readonly SeamarksModSystem modSystem;
    private readonly ICoreClientAPI capi;

    // The innermost band never sits exactly on the axis, since a zero-radius shell is degenerate geometry
    // and draws nothing. It's pinned to this small fraction of the full radius, which makes the hottest
    // band a thin but real cone.
    private const float MinBandRadiusFraction = 0.05f;

    // Beam width at which the calibrated mesh shape draws unscaled (widthScale 1). Wider/narrower beacon
    // settings scale the fan's local X from here. Tied to the default width so the tuned look is the baseline.
    private const float RefBeamWidthDeg = BeaconInfo.DefaultBeamWidthDeg;

    private IShaderProgram? prog;
    private readonly Dictionary<BeaconTier, BeamMeshRefs> meshesByTier = [];
    private readonly Matrixf modelView = new();
    private int lastGeometryGeneration = -1; // triggers a mesh rebuild when BeamTuning geometry changes

    // Reused by the per-beam occlusion walk below, so tracing a beam allocates nothing.
    private readonly BlockPos samplePos = new(0);

    // Concentric shells, evenly spaced from the axis out to the full profile radius. More bands make for a
    // smoother radial gradient. Built once per shape, since the focused and unfocused looks carry their
    // own geometry and usually their own band count, and a beacon draws one or the other off HasFocusingLens.
    private readonly record struct BeamMeshRefs(MeshRef[] FocusedBands, MeshRef[] UnfocusedBands);

    public BeamRenderer(SeamarksModSystem modSystem, ICoreClientAPI capi)
    {
        this.modSystem = modSystem;
        this.capi = capi;

        capi.Event.ReloadShader += LoadShader;
        LoadShader();
        BuildMeshes();

        capi.Event.RegisterRenderer(this, EnumRenderStage.AfterOIT);
    }

    public bool LoadShader()
    {
        prog = capi.Shader.NewShaderProgram();
        prog.AssetDomain = SeamarksModSystem.Domain;
        prog.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        prog.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        capi.Shader.RegisterFileShaderProgram("beaconbeam", prog);
        return prog.Compile();
    }

    // Geometry comes from BeamTuning (live-tunable), so rebuild whenever its GeometryGeneration bumps.
    // Each directional tier gets both shapes' meshes (only OilLamp is directional today).
    private void BuildMeshes()
    {
        DisposeMeshes();
        for (int i = 0; i < BeaconArchetypeData.ByTier.Length; i++)
        {
            if (!BeaconArchetypeData.ByTier[i].Beam.Directional) continue;
            meshesByTier[(BeaconTier)i] = new BeamMeshRefs(
                BuildBands(BeamTuning.Focused), BuildBands(BeamTuning.Unfocused));
        }
        lastGeometryGeneration = BeamTuning.GeometryGeneration;
    }

    // One shape's nested frustum shells. Band count starts from the shape's tuned RadialBands and scales
    // down for the player's graphics preset, so low detail gets fewer shells and the default gets the full
    // count. Never drops below 2, or the beam stops rendering.
    private MeshRef[] BuildBands(BeamTuning.Look look)
    {
        int bandCount = BeamQuality.RadialBands(Math.Clamp((int)look.RadialBands, 2, 16));
        var bands = new MeshRef[bandCount];
        for (int b = 0; b < bandCount; b++)
        {
            float t = BandRadiusFraction(b, bandCount);
            bands[b] = capi.Render.UploadMesh(BeamMesh.BuildFrustum(
                look.NearRadius * t, look.FarRadius * t, look.Length, look.HScale, look.VScale));
        }
        return bands;
    }

    private static float BandRadiusFraction(int band, int bandCount) =>
        bandCount > 1 ? GameMath.Lerp(MinBandRadiusFraction, 1f, (float)band / (bandCount - 1)) : 1f;

    private void DisposeMeshes()
    {
        foreach (var m in meshesByTier.Values)
        {
            foreach (var band in m.FocusedBands) band.Dispose();
            foreach (var band in m.UnfocusedBands) band.Dispose();
        }
        meshesByTier.Clear();
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (prog == null) return;

        var registry = modSystem.Client?.Registry;
        if (registry == null) return;
        var renderBeams = registry.RenderBeacons();
        if (renderBeams.Count == 0) return;

        var rapi = capi.Render;
        if (rapi.FrameWidth == 0) return;

        if (BeamTuning.GeometryGeneration != lastGeometryGeneration) BuildMeshes();

        var player = capi.World.Player?.Entity;
        if (player == null) return;

        var camPos = player.CameraPos;
        double nowHours = BeamState.SweepNowHours(capi.World);
        // Not WorldData.LastApprovedViewDistance: the client only writes that when the server overrides the
        // request, so it sits at 0 in singleplayer. The engine writes any server cap back into this setting.
        float viewDistance = capi.Settings.Int.Get("viewDistance", 256);

        rapi.GLEnableDepthTest();
        rapi.GLDepthMask(false); // additive translucent: test against terrain, never write depth
        rapi.GlToggleBlend(true, EnumBlendMode.Glow);

        prog.Use();
        prog.UniformMatrix("projectionMatrix", rapi.CurrentProjectionMatrix);
        // Global per-frame environment; the shader shapes the day/night + fog response from these plus
        // the tunable curve parameters.
        prog.Uniform("dayLight", capi.World.Calendar.DayLightStrength);
        prog.Uniform("fogDensity", capi.Ambient.BlendedFogDensity);
        prog.Uniform("fogScale", BeamTuning.FogScale);
        prog.Uniform("dayFadeLow", BeamTuning.DayFadeLow);
        prog.Uniform("dayFadeHigh", BeamTuning.DayFadeHigh);
        prog.Uniform("occlusionSoftness", BeamTuning.OcclusionSoftness);
        // lengthFadePow is set per-beacon in the loop below (it differs between the focused/unfocused shapes).

        foreach (var rb in renderBeams)
        {
            BeaconInfo info = rb.Info;
            if (!meshesByTier.TryGetValue(info.Tier, out var meshes)) continue; // non-directional tier

            ref readonly BeaconArchetypeData archetype = ref BeaconArchetypeData.Of(info.Tier);

            // Pick the optic shape (geometry + appearance) by whether a lens panel is actually fitted: the
            // tight bright fan with one, the dim rounder glow without (an empty housing counts as without).
            // Both the mesh set and the shading uniforms come from this one choice.
            BeamTuning.Look look = BeamTuning.ShapeFor(info.HasFocusingLens);
            MeshRef[] bands = info.HasFocusingLens ? meshes.FocusedBands : meshes.UnfocusedBands;
            prog.Uniform("lengthFadePow", look.LengthFadePow);
            // Hides the stretch of mesh between the draw anchor and the reflector's actual glass, so the
            // beam reads as emerging from the lens and not from inside the lamp. Anchor and geometry are
            // untouched; only the shader's visible range shrinks.
            float lensOffset = info.HasFocusingLens ? BeamTuning.LensOffsetBlocks : BeamTuning.UnfocusedLensOffsetBlocks;
            prog.Uniform("startFraction", GameMath.Clamp(lensOffset / look.Length, 0f, 0.95f));

            // Emanate from the tier's visual anchor (same offset the blip draws at).
            double lampX = info.Pos.X + archetype.BlipOffsetX;
            double lampY = info.Pos.Y + archetype.BlipOffsetY;
            double lampZ = info.Pos.Z + archetype.BlipOffsetZ;
            // Camera-relative in double precision before dropping to float, so coords stay small far from
            // the origin.
            double dx = lampX - camPos.X;
            double dy = lampY - camPos.Y;
            double dz = lampZ - camPos.Z;
            // Fold the ignition fade straight into the LOD factor. Both are only alpha multipliers on the
            // shells, so a beam fading in or out scales exactly like one fading with distance.
            float lod = BeamState.MeshLodFactor(Math.Sqrt(dx * dx + dy * dy + dz * dz), viewDistance) * rb.Fade;
            if (lod <= 0f) continue; // faded out into the distant blip, or fully un-ignited/extinguished

            // Player-set signature: BeamCount evenly-spaced beams, each with its own color and its own
            // horizontal scale so its fan subtends that beam's chosen width (WidthScale = width / the
            // reference at which the calibrated shape reads 1:1). All come off the synced BeaconInfo, so
            // every observer sees the same beams.
            int count = BeamState.ClampedBeamCount(info);
            float heading = BeamState.CurrentHeadingRad(info, nowHours);
            for (int b = 0; b < count; b++)
            {
                float h = BeamState.BeamHeadingRad(heading, b, count);
                float widthScale = info.BeamWidthDegOf(b) / RefBeamWidthDeg;
                // Stop the beam where the lamp's ray hits a wall (light-relative occlusion), per heading.
                float clip = BeamClipFraction(info.Pos, lampX, lampY, lampZ, h, look.Length);
                // Each beam draws in its own color (its override, or the tier tint).
                BeamColor.Resolve(info, b, in archetype, out float cr, out float cg, out float cb);
                DrawShells(bands, look, rapi, (float)dx, (float)dy, (float)dz, h, lod, clip, widthScale, cr, cg, cb);
            }
        }

        prog.Stop();
        rapi.GlToggleBlend(false);
        rapi.GLDepthMask(true);
    }

    private void DrawShells(MeshRef[] bands, BeamTuning.Look look, IRenderAPI rapi, float dx, float dy, float dz, float heading, float lod, float clipFraction, float widthScale, float cr, float cg, float cb)
    {
        // The frustum's near face sits at the local origin, so translating to the lamp and then rotating
        // about Y sweeps the cone around the lamp itself with no pivot juggling. The local-X scale comes
        // last and widens or narrows the fan to the beacon's chosen beam width.
        modelView.Identity();
        modelView.Mul(rapi.CameraMatrixOriginf);
        modelView.Translate(dx, dy, dz);
        modelView.RotateY(heading);
        modelView.Scale(widthScale, 1f, 1f);
        prog!.UniformMatrix("modelViewMatrix", modelView.Values);
        prog.Uniform("clipFraction", clipFraction);

        // lod fades the whole beam out across the far band. Each band's alpha comes from RadialAlpha, so
        // the brightest band near the axis reads close to CoreIntensity and grades out to HaloIntensity at
        // the edge, instead of stepping hard between two shells.
        int bandCount = bands.Length;
        for (int b = 0; b < bandCount; b++)
        {
            float t = BandRadiusFraction(b, bandCount);
            float alpha = RadialAlpha(t, look) * lod;
            prog.Uniform("beamColor", cr, cg, cb, alpha);
            rapi.RenderMesh(bands[b]);
        }
    }

    // Fraction along the beam (0..1) at which the lamp's ray first meets a wall, for the shader to cut the
    // beam there. Walks from the lamp along the beam heading (local +Z maps to (sin, cos) in x/z, matching
    // the mesh) out to the full beam length, and returns 1 when nothing blocks it.
    //
    // A hand-rolled walk, not world.RayTraceForSelection. That call loads the engine's one shared
    // AABBIntersectionTest, the same instance it uses every frame to resolve what the player is looking
    // at, and it also sweeps every entity within the ray's length, which here is the beam's whole reach.
    // Driving it from a renderer several times a frame both stomps that shared state and pays for an
    // entity query whose result is discarded. BeaconGazeWatcher keeps off it for the same reason.
    //
    // The ray is horizontal, so this steps the x/z grid alone, one block boundary at a time, and stops at
    // the first opaque block. AllSidesOpaque reads false for glass, leaves and fences, so light carries on
    // through those. The block holding the lamp is never sampled, since the walk only tests what it
    // crosses into.
    private float BeamClipFraction(BlockPos beaconPos, double lampX, double lampY, double lampZ, float heading, float length)
    {
        double dirX = MathF.Sin(heading), dirZ = MathF.Cos(heading);

        int x = (int)Math.Floor(lampX), y = (int)Math.Floor(lampY), z = (int)Math.Floor(lampZ);
        int stepX = dirX > 0 ? 1 : -1, stepZ = dirZ > 0 ? 1 : -1;

        // Distance along the ray between successive grid lines on each axis, then the distance to the
        // first one. A zero component gives infinity here, which is what makes that axis never step.
        double tDeltaX = dirX == 0 ? double.PositiveInfinity : Math.Abs(1.0 / dirX);
        double tDeltaZ = dirZ == 0 ? double.PositiveInfinity : Math.Abs(1.0 / dirZ);
        double tMaxX = dirX == 0 ? double.PositiveInfinity : (dirX > 0 ? x + 1 - lampX : lampX - x) * tDeltaX;
        double tMaxZ = dirZ == 0 ? double.PositiveInfinity : (dirZ > 0 ? z + 1 - lampZ : lampZ - z) * tDeltaZ;

        samplePos.dimension = beaconPos.dimension;
        var blockAccessor = capi.World.BlockAccessor;

        double travelled = 0;
        while (travelled < length)
        {
            if (tMaxX < tMaxZ) { travelled = tMaxX; tMaxX += tDeltaX; x += stepX; }
            else { travelled = tMaxZ; tMaxZ += tDeltaZ; z += stepZ; }
            if (travelled > length) break;

            // The beacon's own cells never stop its beam, which matters where the lamp sits off-centre in
            // its block and the walk crosses straight out into the rest of the structure.
            if (x == beaconPos.X && y == beaconPos.Y && z == beaconPos.Z) continue;

            samplePos.Set(x, y, z);
            if (blockAccessor.GetBlock(samplePos).AllSidesOpaque)
            {
                return GameMath.Clamp((float)(travelled / length), 0f, 1f);
            }
        }
        return 1f;
    }

    // Brightness across the radius, where t runs 0 at the axis to 1 at the outer edge. Flat at
    // CoreIntensity out to CoreFraction, then eases toward HaloIntensity by RadialSoftness, where a higher
    // value gives a tighter hot core with a longer soft skirt and 1 is a straight taper. All draw-time, so
    // moving either knob needs no mesh rebuild and both tune live.
    private static float RadialAlpha(float t, BeamTuning.Look look)
    {
        float core = look.CoreFraction;
        if (t <= core) return look.CoreIntensity;
        float u = core >= 1f ? 0f : (t - core) / (1f - core);
        float shaped = MathF.Pow(GameMath.Clamp(u, 0f, 1f), MathF.Max(look.RadialSoftness, 0.05f));
        return GameMath.Lerp(look.CoreIntensity, look.HaloIntensity, shaped);
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterOIT);
        DisposeMeshes();
        capi.Event.ReloadShader -= LoadShader;
    }
}
