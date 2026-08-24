using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Seamarks.Beacon.Client;

// Draws every known beacon (the server has already filtered them down to this player's maximum possible
// range) as a small camera-relative glowing billboard, well past the normal far clip plane. The approach
// follows the Farseer mod's far-terrain renderer.
public sealed class BeaconBlipRenderer : IRenderer
{
    // A fixed generous bound instead of recomputing from actual beacon ranges each frame. Holding ZFar
    // still also avoids depth-precision churn.
    public const float MaxBeaconDrawDistance = 8192f;

    // AfterOIT, not Opaque. Clouds render during the OIT stage without writing depth, and that
    // buffer is composited into the scene before AfterOIT runs, so drawing at Opaque let clouds paint
    // over the blip no matter which was actually nearer. AfterOIT lands on top of that composite and
    // still depth-tests (without writing), so opaque terrain hides the blip properly.
    public double RenderOrder => 0.1;
    public int RenderRange => 9999; // never culled by the engine's own renderer-distance check

    // Margin so the third-person depth pin clears the avatar outright instead of tying with it, which
    // would only trade the flicker for a slower flicker.
    private const float ThirdPersonPinMarginBlocks = 0.5f;
    private const float MinThirdPersonPinBlocks = 1f;

    // The far blip is a distance cue and it's sized in world space, so it fades out as you approach
    // instead of swelling into a wall-sized billboard. Full past NearFadeStartBlocks, gone by
    // NearFadeEndBlocks, where the close glare takes over.
    private const float NearFadeStartBlocks = 64f;
    private const float NearFadeEndBlocks = 24f;

    // The close glare gets its own size and brightness, separate from the far blip's per-tier BlipSize.
    // Small and very bright reads as a blinding point source (a glowStrength above 1 blows the core out
    // to white). Tuned by feel.
    private const float CloseGlareSize = 1.1f;
    private const float CloseGlareGlowStrength = 2.0f;

    // The smoke blip: a second marker floating above Fire's own far blip, standing in for its smoke
    // plume at a distance, tinted dark and not the fire's own warm color. Its own flat size, not a
    // fraction of BlipSize, since it shouldn't shrink or grow just because the flame's own blip does.
    private const float SmokeBlipHeightBlocks = 1.5f;
    private const float SmokeBlipSize = 2.52f;
    private const float SmokeBlipColorR = 0.05f, SmokeBlipColorG = 0.05f, SmokeBlipColorB = 0.06f;
    // Alpha-blended smoke reads as a solid gray disc at full strength; capped well under 1 so it stays a
    // translucent haze instead.
    private const float SmokeBlipAlphaFraction = 0.5f;
    // How much of its full size the flame's own blip keeps at midday, so it shrinks back as the smoke
    // takes over and grows again through dusk. A fraction of the tuned size, so the config slider still
    // sets both ends at once.
    private const float FireBlipDaySizeFraction = 1f / 3f;

    private readonly SeamarksModSystem modSystem;
    private readonly ICoreClientAPI capi;

    private IShaderProgram? prog;
    private MeshRef? quadMeshRef;

    public BeaconBlipRenderer(SeamarksModSystem modSystem, ICoreClientAPI capi)
    {
        this.modSystem = modSystem;
        this.capi = capi;

        capi.Event.ReloadShader += LoadShader;
        LoadShader();

        quadMeshRef = capi.Render.UploadMesh(QuadMeshUtil.GetQuad());

        capi.Event.RegisterRenderer(this, EnumRenderStage.AfterOIT);
        capi.Event.LevelFinalize += Init;
    }

    public void Init()
    {
        // Raising ZFar is process-global, so it widens the whole scene's depth range and not just ours.
        // That's the price of drawing past the normal far plane. Only ever raised, never lowered, in
        // case another far-render mod already set it higher. Suspect this first if distant z-fighting
        // turns up.
        var clientMain = (ClientMain)capi.World;
        var mainCam = clientMain.MainCamera;
        if (mainCam.ZFar < MaxBeaconDrawDistance)
        {
            mainCam.ZFar = MaxBeaconDrawDistance;
            capi.Render.Reset3DProjection();
        }
    }

    public bool LoadShader()
    {
        prog = capi.Shader.NewShaderProgram();

        prog.AssetDomain = SeamarksModSystem.Domain;
        prog.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        prog.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);

        capi.Shader.RegisterFileShaderProgram("beacon", prog);

        return prog.Compile();
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (prog == null || quadMeshRef == null) return;

        var rapi = capi.Render;
        if (rapi.FrameWidth == 0) return;

        var registry = modSystem.Client?.Registry;
        if (registry == null) return;
        var renderBeams = registry.RenderBeacons();
        if (renderBeams.Count == 0) return;

        var player = capi.World.Player;
        var entity = player?.Entity;
        if (player == null || entity == null) return;

        var camPos = entity.CameraPos;
        var eyePos = entity.Pos.XYZ.Add(entity.LocalEyePos);

        // Read once and reused for every beacon's rotation phase this frame. It drives the cone angle off
        // the shared sweep clock, so all clients agree without any per-frame rotation packets. Separate
        // from nowHours below, which stays on the calendar because fuel burns on world time either way.
        double nowSweepHours = BeamState.SweepNowHours(capi.World);
        double nowHours = capi.World.Calendar.TotalHours;
        // Real-world seconds, for the fire flicker below: it should breathe at a real-time rate and not
        // stretch or race with the calendar's own speed.
        double nowSeconds = capi.World.ElapsedMilliseconds / 1000.0;

        // Set explicitly instead of assumed, since AfterOIT is a less travelled stage than Opaque.
        // Depth testing has to stay on so terrain still occludes the blip.
        rapi.GLEnableDepthTest();
        rapi.GLDepthMask(false);
        rapi.GlToggleBlend(true, EnumBlendMode.Glow);

        // In third person the depth buffer can't tell terrain from the player's own avatar, which sits at
        // a roughly fixed distance between the camera and the target. Turning the depth test off entirely
        // also threw away nearby occlusion, and up close (where the blip covers a lot of screen) that
        // showed up as the terrain around the beacon flickering as the quad's edge swept over it. Pinning
        // the tested depth just inside the avatar's distance handles both, since anything genuinely
        // closer still occludes. 0 means first person, where the shader's own bias applies instead.
        float thirdPersonPinDepth = 0f;
        if (player.CameraMode == EnumCameraMode.ThirdPerson)
        {
            double camToPlayerDx = camPos.X - eyePos.X, camToPlayerDy = camPos.Y - eyePos.Y, camToPlayerDz = camPos.Z - eyePos.Z;
            float avatarDistance = (float)Math.Sqrt(camToPlayerDx * camToPlayerDx + camToPlayerDy * camToPlayerDy + camToPlayerDz * camToPlayerDz);
            thirdPersonPinDepth = MathF.Max(MinThirdPersonPinBlocks, avatarDistance - ThirdPersonPinMarginBlocks);
        }

        prog.Use();
        prog.UniformMatrix("viewMatrix", rapi.CameraMatrixOriginf);
        prog.UniformMatrix("projectionMatrix", rapi.CurrentProjectionMatrix);
        prog.Uniform("thirdPersonPinDepth", thirdPersonPinDepth);

        foreach (var rb in renderBeams)
        {
            BeaconInfo info = rb.Info;
            // Burn state is server-authoritative, so there's no IsBurning() recheck against the client
            // clock here. The server pushes a Remove the moment a beacon expires, and this registry only
            // ever holds beacons it has told us are burning and in range. Trusting that avoids a clock
            // disagreement blanking the blip of a lamp that's still visibly lit.
            ref readonly BeaconArchetypeData archetype = ref BeaconArchetypeData.Of(info.Tier);

            double dx = info.Pos.X + 0.5 - camPos.X;
            double dy = info.Pos.Y + 0.5 - camPos.Y;
            double dz = info.Pos.Z + 0.5 - camPos.Z;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            float maxRange = BeaconVisibility.MaxRange(info, capi.World.SeaLevel);
            if (dist > maxRange) continue;

            // The cone is applied per frame here, not as a server-side broadcast filter, so it
            // tracks the player orbiting with no add/remove network churn. Uses eyePos to match the
            // server's own range check, not the free-cam position. currentHeading resolves rotation, so a
            // driven beacon's cone sweeps and a static one holds its placement heading.
            float currentHeading = BeamState.CurrentHeadingRad(info, nowSweepHours);

            // Draw position uses the tier's own offset, not the anchor-based Pos+0.5 that the range
            // maths uses. Fire's blip sits centred in its 2x2x2 footprint instead of at the placed corner,
            // while range stays anchor-based to match the server. Both blips draw from here.
            double drawDx = info.Pos.X + archetype.BlipOffsetX - camPos.X;
            double drawDy = info.Pos.Y + archetype.BlipOffsetY - camPos.Y;
            double drawDz = info.Pos.Z + archetype.BlipOffsetZ - camPos.Z;

            // Far blip, the distance cue. The beam cone feathers it from 0 to 1 so it fades in and out
            // with no pop, it dims near max range, and it fades again as the viewer closes in. Tinted by
            // whichever beam is currently sweeping the viewer, so a multi-color optic flashes each
            // beam's color in turn. Shared by the smoke blip below too, before the flicker below tints it
            // to this one blip alone.
            float beamFactor = BeaconVisibility.DirectionalBeamFactor(info, eyePos, currentHeading, out int litBeam);
            float baseAlpha = beamFactor
                * FadeNearMaxRange(dist, maxRange, archetype.FalloffStartFraction)
                * NearObserverFade(dist)
                * rb.Fade;
            float farAlpha = baseAlpha;

            // A directional beam already reads as alive through its own sweep and flash, so the layered
            // breathe/flicker/flare animation is reserved for the tiers with no beam to carry that, a big
            // bonfire's distant glow, not a lantern's steady point.
            float blipSize = archetype.BlipSize * archetype.BlipSizeScale;
            float warmShift = 0f;
            float nightFactor = BeamState.DayFadeFactor(capi.World.Calendar.DayLightStrength);
            if (!archetype.Beam.Directional)
            {
                var look = BeaconFireFlicker.Evaluate(info.Pos, nowSeconds, info.RemainingFuelFraction(nowHours));
                farAlpha *= look.Brightness;
                blipSize *= look.HaloScale * GameMath.Lerp(FireBlipDaySizeFraction, 1f, nightFactor);
                warmShift = look.WarmShift;
            }

            if (farAlpha > 0.0015f)
            {
                BeamColor.Resolve(info, litBeam, in archetype, out float cr, out float cg, out float cb);
                if (warmShift != 0f)
                {
                    // Toward yellow/white when brighter than baseline, toward a deeper orange when dimmer,
                    // by nudging green and blue since red is already at or near its ceiling.
                    cg = GameMath.Clamp(cg * (1f + warmShift * 0.15f), 0f, 1f);
                    cb = GameMath.Clamp(cb * (1f + warmShift * 0.35f), 0f, 1f);
                }
                prog.Uniform("centerRelPos", (float)drawDx, (float)drawDy, (float)drawDz);
                prog.Uniform("size", blipSize);
                prog.Uniform("tintColor", cr, cg, cb, farAlpha);
                prog.Uniform("glowStrength", 1f); // 1 keeps the original look now that the uniform is live
                rapi.RenderMesh(quadMeshRef);
            }

            // Smoke blip, floating above the flame's own. Fire has no beam to read from afar, so this only
            // draws on that tier. Uses the same day/night curve the oil lamp's beam fades by, but inverted:
            // the flame's own blip already carries the night silhouette, so the smoke instead reads by day,
            // when a plume shows up better against the sky than a point of light does.
            if (!archetype.Beam.Directional)
            {
                float smokeDayFactor = 1f - nightFactor;
                float smokeAlpha = baseAlpha * smokeDayFactor;
                if (smokeAlpha > 0.0015f)
                {
                    // The rest of this loop draws additive (EnumBlendMode.Glow), which is exactly why the
                    // far blip and close glare read as glowing light. Smoke doesn't emit, so it's drawn
                    // with an ordinary alpha blend instead, the one place in this renderer that switches
                    // blend mode mid-loop, restored right after so the next draw goes back to additive.
                    rapi.GlToggleBlend(true, EnumBlendMode.Standard);
                    prog.Uniform("centerRelPos", (float)drawDx, (float)(drawDy + SmokeBlipHeightBlocks), (float)drawDz);
                    prog.Uniform("size", SmokeBlipSize * archetype.BlipSizeScale);
                    prog.Uniform("tintColor", SmokeBlipColorR, SmokeBlipColorG, SmokeBlipColorB, smokeAlpha * SmokeBlipAlphaFraction);
                    prog.Uniform("glowStrength", 1f);
                    rapi.RenderMesh(quadMeshRef);
                    rapi.GlToggleBlend(true, EnumBlendMode.Glow);
                }
            }

            // Close glare, the broad blinding wash from a lamp with no lens. Only inside the close range
            // and the frontal arc, and only on the directional OilLamp, since omnidirectional Fire never
            // throws one. Fitting a lens focuses that sideways spill into the beam and removes it; an
            // empty housing still glares. Takes the color of whichever beam faces the viewer, so a
            // colored-beam lamp glares in its color.
            if (archetype.Beam.Directional && !info.HasFocusingLens)
            {
                float glareAlpha = BeaconVisibility.CloseGlareFactor(info, eyePos, currentHeading, dist) * rb.Fade;
                if (glareAlpha > 0.0015f)
                {
                    BeamColor.Resolve(info, litBeam, in archetype, out float gr, out float gg, out float gb);
                    prog.Uniform("centerRelPos", (float)drawDx, (float)drawDy, (float)drawDz);
                    prog.Uniform("size", CloseGlareSize);
                    prog.Uniform("tintColor", gr, gg, gb, glareAlpha);
                    prog.Uniform("glowStrength", CloseGlareGlowStrength);
                    rapi.RenderMesh(quadMeshRef);
                }
            }
        }

        prog.Stop();

        rapi.GlToggleBlend(false);
        rapi.GLDepthMask(true);
    }

    private static float FadeNearMaxRange(double dist, float maxRange, float falloffStartFraction)
    {
        float fadeStart = maxRange * falloffStartFraction;
        if (dist <= fadeStart) return 1f;
        return 1f - (float)((dist - fadeStart) / (maxRange - fadeStart));
    }

    // 1 past NearFadeStartBlocks, 0 at or inside NearFadeEndBlocks, linear between. Fades the far blip
    // out as the viewer approaches so it never becomes a wall-sized billboard.
    private static float NearObserverFade(double dist)
    {
        if (dist >= NearFadeStartBlocks) return 1f;
        if (dist <= NearFadeEndBlocks) return 0f;
        return (float)((dist - NearFadeEndBlocks) / (NearFadeStartBlocks - NearFadeEndBlocks));
    }

    public void Dispose()
    {
        capi.Event.UnregisterRenderer(this, EnumRenderStage.AfterOIT);
        quadMeshRef?.Dispose();
        quadMeshRef = null;
        capi.Event.ReloadShader -= LoadShader;
        capi.Event.LevelFinalize -= Init;
    }
}
