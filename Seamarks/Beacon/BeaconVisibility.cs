using Vintagestory.API.MathTools;

namespace Seamarks.Beacon;

// Shared by the server (broadcast filtering) and the client (fade and cutoff), so both sides agree on
// what "in range" means.
public static class BeaconVisibility
{
    // How far the beacon reaches: a flat base for sitting at sea level, a straight-line bonus for each
    // block of tower above ElevationBonusStart, then the oil lamp's plate and housing boosts on top. The
    // elevation term clamps at the start height, so a beacon at or below the waterline just gets the base.
    // An empty housing is just a bare cover glass, so the range bonus only kicks in once a panel is fitted.
    public static float MaxRange(BeaconInfo info, float seaLevel)
    {
        ref readonly BeaconArchetypeData archetype = ref BeaconArchetypeData.Of(info.Tier);
        float elevation = info.Pos.Y - seaLevel - archetype.ElevationBonusStart;
        float elevationBonus = archetype.RangePerElevationBlock * MathF.Max(0f, elevation);
        return archetype.BaseRangeBlocks
            + elevationBonus
            + archetype.PlateRangeBonus(info.PlateMetal, info.PlateCount)
            + (info.HasFocusingLens ? archetype.HousingRangeBonus : 0f);
    }

    // How much of the half-angle stays at full brightness before the edge starts fading.
    private const float DirectionalBeamFullBrightFraction = 0.78f;

    // A distant blip reading strictly off the lens's own beam width felt too narrow to play well, so the
    // point where it fades to fully invisible is pushed out by this much on top of whatever the fitted
    // lens produces. The crisp full-bright core (above) still tracks the lens width exactly, only the
    // outer fade-to-nothing edge gets the flat padding, so a wide lens still visibly reads wider than a
    // narrow one, just with a softer, more forgiving tail on both.
    private const float BlipVisibilityPaddingDeg = 20f;

    // 1 inside a beam, 0 outside, ramping across the edge band so the cone tapers instead of popping.
    // baseHeadingRad is beam 0's rotation-resolved heading, and the rest sit evenly spaced around it, so
    // a rotating multi-beam optic flashes each viewer once per beam per revolution, each flash lasting in
    // proportion to that beam's width. beamIndex comes back as the winning beam, or the nearest one when
    // nothing is in range, which is what lets the blip flash in that beam's color.
    public static float DirectionalBeamFactor(BeaconInfo info, Vec3d observerPos, float baseHeadingRad, out int beamIndex)
    {
        beamIndex = 0;
        ref readonly BeaconArchetypeData archetype = ref BeaconArchetypeData.Of(info.Tier);
        if (!archetype.Beam.Directional) return 1f;

        double dx = observerPos.X - info.Pos.X;
        double dz = observerPos.Z - info.Pos.Z;
        float angleToObserver = (float)Math.Atan2(dx, dz);

        int count = BeamState.ClampedBeamCount(info);
        float paddingRad = BlipVisibilityPaddingDeg * GameMath.DEG2RAD;

        float best = 0f;
        float nearestOffAxis = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            // Per-beam width, so a wide beam flashes for longer than a narrow one on the same beacon.
            float halfAngle = info.BeamWidthDegOf(i) * 0.5f * GameMath.DEG2RAD;
            float fullBrightEdge = halfAngle * DirectionalBeamFullBrightFraction;
            float visibleEdge = halfAngle + paddingRad;
            float offAxis = MathF.Abs(GameMath.AngleRadDistance(BeamState.BeamHeadingRad(baseHeadingRad, i, count), angleToObserver));
            // Keep the nearest beam so beamIndex still means something when every factor is 0.
            if (offAxis < nearestOffAxis) { nearestOffAxis = offAxis; if (best <= 0f) beamIndex = i; }

            float f = offAxis <= fullBrightEdge ? 1f
                : offAxis >= visibleEdge ? 0f
                : GameMath.SmoothStep(1f - (offAxis - fullBrightEdge) / (visibleEdge - fullBrightEdge));
            if (f > best) { best = f; beamIndex = i; }
            if (best >= 1f) break;
        }
        return best;
    }

    // Shared by the close glare below and the gaze eye-shield, which only fires when the glare is thrown.
    public const float CloseRangeBlocks = 30f;

    // A bare lamp throws a broad wash of unfocused light. A real Fresnel lens gathers that sideways spill
    // into the beam, so fitting a housing removes the glare entirely. This is
    // the total arc it covers, centred on the beam heading, so it only blinds someone standing in front.
    public const float CloseGlareArcDeg = 120f;

    // How much of the half-arc stays full before the edge feathers out.
    private const float CloseGlareFullBrightFraction = 0.7f;

    // 1 dead ahead and point blank, falling to 0 at the arc edge and again at CloseRangeBlocks. headingRad
    // is rotation-resolved, so the glare sweeps on a driven beacon and sits still on a static one.
    public static float CloseGlareFactor(BeaconInfo info, Vec3d observerPos, float headingRad, double dist)
    {
        if (dist >= CloseRangeBlocks) return 0f;

        double dx = observerPos.X - info.Pos.X;
        double dz = observerPos.Z - info.Pos.Z;
        float angleToObserver = (float)Math.Atan2(dx, dz);
        float offAxis = MathF.Abs(GameMath.AngleRadDistance(headingRad, angleToObserver));

        float halfArc = CloseGlareArcDeg * 0.5f * GameMath.DEG2RAD;
        if (offAxis >= halfArc) return 0f;

        float fullBrightEdge = halfArc * CloseGlareFullBrightFraction;
        float angular = offAxis <= fullBrightEdge ? 1f
            : GameMath.SmoothStep(1f - (offAxis - fullBrightEdge) / (halfArc - fullBrightEdge));

        // Strongest at the lamp and easing out by the close-range edge, so it grows on you as you
        // approach instead of snapping into view.
        float distFactor = GameMath.SmoothStep(1f - (float)(dist / CloseRangeBlocks));
        return angular * distFactor;
    }
}
