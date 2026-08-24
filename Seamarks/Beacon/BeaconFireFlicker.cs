using Vintagestory.API.MathTools;

namespace Seamarks.Beacon;

// The far blip's brightness animation for non-directional tiers (Fire): a slow breathing pulse plus a
// faint fast flicker plus occasional flare-ups and, when fuel is low, occasional dips. Layered rather
// than one sine or one random value, so it reads as a large body of flame and not a torch. A pure
// function of real time, the beacon's own position (so beacons don't flicker in lockstep) and its
// remaining fuel fraction, so every observer renders the same animation with nothing synced over the
// network for it.
public static class BeaconFireFlicker
{
    public readonly record struct Look(float Brightness, float HaloScale, float WarmShift);

    // The "breathing" pulse: two sines inside the 0.5-2Hz band, summed so the beat between them reads as
    // organic, not one metronomic wave.
    private const float SlowHzA = 0.7f, SlowHzB = 1.3f;
    private const float SlowAmplitude = 0.12f;

    // Fast, tiny-amplitude texture so the light never looks perfectly smooth, without ever outweighing
    // the slow pulse above.
    private const float MicroHzA = 6.3f, MicroHzB = 9.7f;
    private const float MicroAmplitude = 0.03f;

    // A flare's own cycle is fixed, but its centre is jittered across that cycle, which spaces the actual
    // flares out to roughly 10-30s apart instead of one every exact 20 seconds.
    private const float FlareCycleSeconds = 20f;
    private const float FlareJitterSeconds = 10f;
    private const float FlareMinDuration = 1f, FlareMaxDuration = 3f;
    private const float FlareAmplitude = 0.35f;

    // Dim spells: only really show up once a beacon is running low, a longer dip and not a flare,
    // recovering the same smooth way.
    private const float DipCycleSeconds = 15f;
    private const float DipJitterSeconds = 7f;
    private const float DipMinDuration = 3f, DipMaxDuration = 6f;

    public static Look Evaluate(BlockPos pos, double nowSeconds, float fuelFraction)
    {
        int seed = GameMath.oaatHash(pos.X, pos.Y, pos.Z);
        double phase = GameMath.PrettyBadHash(seed, 0) * 1000.0; // per-beacon desync, an arbitrary large range
        double t = nowSeconds + phase;

        float slow = 0.5f * (MathF.Sin((float)t * SlowHzA * GameMath.TWOPI) + MathF.Sin((float)t * SlowHzB * GameMath.TWOPI));
        float micro = 0.5f * (MathF.Sin((float)t * MicroHzA * GameMath.TWOPI) + MathF.Sin((float)t * MicroHzB * GameMath.TWOPI));

        // Flares fire on almost every cycle for a well-fed beacon and skip more of them as fuel drops;
        // their own strength doesn't shrink, there's just fewer of them.
        float flareChance = GameMath.Lerp(0.35f, 1f, fuelFraction);
        float flare = EventEnvelope(t, seed, 1, FlareCycleSeconds, FlareJitterSeconds, FlareMinDuration, FlareMaxDuration, flareChance);

        // Dips are the mirror image: rare on a full tank, common and deep once it's nearly spent.
        float dipChance = GameMath.Lerp(0.9f, 0f, fuelFraction);
        float dipDepth = GameMath.Lerp(0.5f, 0f, fuelFraction);
        float dip = EventEnvelope(t, seed, 2, DipCycleSeconds, DipJitterSeconds, DipMinDuration, DipMaxDuration, dipChance);

        float baseline = GameMath.Lerp(0.55f, 1f, fuelFraction);
        float brightness = baseline + SlowAmplitude * slow + MicroAmplitude * micro + FlareAmplitude * flare - dipDepth * dip;
        brightness = GameMath.Clamp(brightness, 0.15f, 1.6f);

        // The halo only ever nudges with brightness relative to the beacon's own baseline, so a flare
        // swells it slightly instead of visibly growing and shrinking the blip.
        float haloBaseline = GameMath.Lerp(0.8f, 1f, fuelFraction);
        float haloScale = GameMath.Clamp(haloBaseline + 0.15f * (brightness - baseline), 0.7f, 1.25f);

        // Positive when brighter than baseline (a flare pushes toward yellow/white), negative when
        // dimmer (a dip settles toward a deeper orange).
        float warmShift = GameMath.Clamp((brightness - baseline) * 1.5f, -1f, 1f);

        return new Look(brightness, haloScale, warmShift);
    }

    // A smooth 0->1->0 bump somewhere inside the cycle containing t, gated by chance so not every cycle
    // gets one. salt keeps this desynced from any other event sharing the same position and time.
    private static float EventEnvelope(double t, int seed, int salt, float cycleSeconds, float jitterSeconds, float minDuration, float maxDuration, float chance)
    {
        long cycleIndex = (long)Math.Floor(t / cycleSeconds);
        int cycleSeed = seed ^ (salt * 0x2545F491) ^ (int)cycleIndex;

        if (GameMath.PrettyBadHash(cycleSeed, 0) > chance) return 0f;

        float centerJitter = GameMath.PrettyBadHash(cycleSeed, 1) * 2f - 1f;
        float durationT = GameMath.PrettyBadHash(cycleSeed, 2);
        float duration = GameMath.Lerp(minDuration, maxDuration, durationT);

        double cycleStart = cycleIndex * cycleSeconds;
        double center = cycleStart + cycleSeconds * 0.5 + centerJitter * jitterSeconds;
        double intoEvent = t - (center - duration * 0.5);
        if (intoEvent < 0 || intoEvent > duration) return 0f;

        float u = (float)(intoEvent / duration);
        return 0.5f - 0.5f * MathF.Cos(u * GameMath.TWOPI);
    }
}
