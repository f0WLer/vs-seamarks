using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon;

// Per-frame visual state shared by every beacon renderer, so none of them can disagree about where the
// beam currently points. Most of it is plain maths over its arguments, the shape BeaconVisibility has.
// The sweep clock is the exception: it owns which clock the rotation runs on, and the running correction
// each mode needs to keep a client's copy of that clock smooth and in step with the server's.
public static class BeamState
{
    // Whether the sweep runs off the world calendar or off its own real-time clock. Set from the server
    // config and synced to every client with the rest of the tuning, since the side that stamps an anchor
    // and the sides that read it have to agree on what the number means. On the calendar, /time stop
    // freezes the optic mid-turn and /time speed winds it up; off it, the optic holds its pace whatever
    // the world clock is doing.
    public static bool SweepTracksWorldTime = true;

    // The calendar speed a detached sweep is paced against: the engine's stock 60, times its stock 0.5
    // multiplier. A constant, not the live speed, is the whole point, so a beacon turns at the rate a
    // default world would give it however the world clock has been set.
    public const float DetachedCalendarSpeed = 60f * 0.5f;

    // How far this client's local clock sits from the server's, filled in from BeaconSweepSyncPacket and
    // added to every detached reading here. Wall-clock time would be the obvious epoch and is the wrong
    // one: a beacon turns once every few seconds, so a machine whose clock is a second out would draw its
    // beams tens of degrees from where everyone else sees them, and how well a player's clock is kept is
    // not something we control. Syncing against the server instead costs one small packet and leaves only
    // the latency it arrived with. Untouched on the server, which is its own reference.
    private static double clientOffsetHours;

    // Local elapsed time in calendar hours. Counts from process start, so it never jumps the way a
    // wall clock can, and it keeps running while the world clock is stopped, which is the point.
    private static double LocalSweepHours(IWorldAccessor world) =>
        world.ElapsedMilliseconds / 1000.0 * DetachedCalendarSpeed / 3600.0;

    // Takes the server's own reading of the detached clock and works out this client's standing offset
    // from it. The error left over is however long the packet spent in flight, which at a sweep of a few
    // seconds per turn is a degree or two, plus whatever the two machines drift apart before the next one
    // arrives. Both stay far below what comparing wall clocks would leave.
    public static void SyncDetachedClock(IWorldAccessor world, double serverSweepHours)
    {
        clientOffsetHours = serverSweepHours - LocalSweepHours(world);
    }

    // How long the smoothed world clock takes to close most of a gap against the calendar it follows. Long
    // enough that a resync step is spread into a rate change too small to see, short enough that the
    // steady lag it leaves is a fixed handful of degrees, which nobody can pick out on a turning beam.
    private const double WorldClockLagSeconds = 4.0;

    // Past this much disagreement the filter gives up and jumps, because that is a real move of the world
    // clock and not the few game-seconds a resync shifts. Scaled off the calendar's own speed, since a
    // resync's size scales with it too, with a floor to keep it sane on a stopped or crawling clock.
    private const double WorldClockSnapRealSeconds = 20.0;
    private const double WorldClockMinSnapHours = 1.0 / 60.0;

    private static double smoothedWorldHours = double.NaN;
    private static long lastWorldSmoothMs = -1;

    // The clock the sweep runs on, in calendar hours whichever mode is live, so the anchor, the period and
    // every reader downstream keep working in one unit. Fuel burn doesn't come through here: a beacon
    // still burns its oil on raw world time, and only the optic's rotation gets smoothed or detached.
    //
    // Both corrections are client-side only. On an integrated server both sides run in one process off
    // these statics, and the server has to stay on its own raw reading, the one it hands out as reference.
    public static double SweepNowHours(IWorldAccessor world)
    {
        if (SweepTracksWorldTime)
        {
            return world.Side == EnumAppSide.Server ? world.Calendar.TotalHours : SmoothedWorldHours(world);
        }

        double local = LocalSweepHours(world);
        return world.Side == EnumAppSide.Server ? local : local + clientOffsetHours;
    }

    // The client's calendar is a stepped copy of the server's, and reading it raw puts a visible hitch in
    // the sweep. Two engine details combine into it. The client advances its own calendar once a frame
    // through GameCalendar.Tick, which reads whole milliseconds off a stopwatch and then restarts it,
    // dropping the remainder every time, so the copy runs a few percent slow and runs slower the higher
    // the frame rate goes. Then every few seconds a calendar packet arrives and the client assigns the
    // server's value outright, with no easing, jumping the accumulated shortfall forward in one frame. On
    // an optic that turns once every nine seconds that step is several degrees.
    //
    // Following the calendar through a lag filter spreads the step across a couple of seconds, leaving a
    // rate change too small to notice. The average rate is untouched, so the beam still keeps world time,
    // and the constant lag this leaves is the same for every client watching, so they stay in agreement.
    private static double SmoothedWorldHours(IWorldAccessor world)
    {
        double raw = world.Calendar.TotalHours;

        long nowMs = world.ElapsedMilliseconds;
        double dt = lastWorldSmoothMs < 0 ? 0.0 : (nowMs - lastWorldSmoothMs) / 1000.0;
        lastWorldSmoothMs = nowMs;

        double calendarSpeed = world.Calendar.SpeedOfTime * world.Calendar.CalendarSpeedMul;
        double snapHours = Math.Max(WorldClockMinSnapHours, WorldClockSnapRealSeconds * calendarSpeed / 3600.0);

        if (double.IsNaN(smoothedWorldHours) || Math.Abs(raw - smoothedWorldHours) > snapHours)
        {
            smoothedWorldHours = raw;
        }
        else if (dt > 0.0)
        {
            // Exponential, so the several reads this takes in one frame compose to what a single read of
            // their combined interval would have given.
            smoothedWorldHours += (raw - smoothedWorldHours) * (1.0 - Math.Exp(-dt / WorldClockLagSeconds));
        }

        return smoothedWorldHours;
    }

    // Calendar speed for turning a sweep period into real time, matching whichever clock the sweep is on.
    // Anything quoting a flash duration or a real-time rate has to ask here, or a detached beacon reads as
    // stopped the moment someone runs /time stop.
    public static float SweepCalendarSpeed(IGameCalendar calendar) =>
        SweepTracksWorldTime ? calendar.SpeedOfTime * calendar.CalendarSpeedMul : DetachedCalendarSpeed;

    // A mechanically-active directional beacon sweeps its cone at a constant angular velocity anchored to
    // a timestamp the server sets, so every client derives the same heading from the synced anchor and the
    // shared clock, with no per-frame rotation packets. A beacon that isn't rotating, or isn't directional
    // at all, just holds its fixed placement heading.
    //
    // The period is in calendar hours, so every client works out the same phase off that clock. This is the
    // constant-RPM simplification. Real variable-RPM drive, with spin-up and coasting, would need the
    // server to broadcast the angle. Feed it SweepNowHours, not the calendar's own TotalHours.
    public static float CurrentHeadingRad(BeaconInfo info, double nowTotalHours)
    {
        return SweepHeadingRad(IsSweeping(info), info.HeadingRad,
            info.SweepAnchorRad, info.SweepAnchorTotalHours, info.SweepPeriodHours, nowTotalHours);
    }

    // Whether the beam is actually turning, which is the same set of conditions SweepHeadingRad checks
    // before it advances the angle. Anything that only makes sense against a moving beam can gate on this.
    public static bool IsSweeping(BeaconInfo info)
    {
        return BeaconArchetypeData.Of(info.Tier).Beam.Pattern == BeamPattern.Rotating
            && info.MechanicalActive
            && info.SweepPeriodHours > 0f
            && info.SweepAnchorTotalHours >= 0;
    }

    // The one sweep formula both paths run: the far blip off the synced BeaconInfo, and the near optic off
    // the beam behavior's own copy of the same anchor. Sharing it is what keeps the visible reflector and
    // the distant flash pointing the same way at the same speed. Returns the rest heading when the sweep
    // isn't running or has no anchor yet.
    public static float SweepHeadingRad(bool running, float restHeadingRad, float anchorRad, double anchorHours, float periodHours, double nowHours)
    {
        if (!running || periodHours <= 0f || anchorHours < 0) return restHeadingRad;

        double revs = (nowHours - anchorHours) / periodHours;
        // Wrap into a single revolution before scaling so the angle stays bounded over long burns
        // (float precision), matching the engine's own wrapped-counter idiom for continuous animation.
        return anchorRad + (float)(GameMath.Mod(revs, 1.0) * GameMath.TWOPI);
    }

    // Sweep period for a given drive speed. The archetype's RotationPeriodHours is the period at
    // ReferenceShaftSpeed, and it scales inversely from there, so twice the shaft speed sweeps twice as
    // fast. Clamped at both ends so a crawling shaft doesn't stall the beam for hours and a fast one
    // doesn't strobe. Returns 0 for a shaft that isn't turning.
    public const float ReferenceShaftSpeed = 0.5f;
    private const float MinPeriodFraction = 0.25f, MaxPeriodFraction = 4f;

    // The shaft speed the fitting screen quotes its flash at, labelled "1 rps" on the beamspec line.
    public const float DisplayShaftSpeed = 1f;

    // How long, in real seconds, a distant viewer sees one flash from a beam of the given width, quoted at
    // DisplayShaftSpeed. A viewer sits inside a beam for width/360 of each revolution, and one revolution
    // at that speed takes RotationPeriodHours scaled off ReferenceShaftSpeed of calendar time, which the
    // sweep clock's own speed converts into real seconds. The ground housing isn't driven, so it can only
    // quote a fixed speed; the real flash tracks the beacon's own drive once the housing is fitted.
    public static float NominalFlashSeconds(float widthDeg, IGameCalendar calendar)
    {
        var beam = BeaconArchetypeData.Of(BeaconTier.OilLamp).Beam;
        float calendarSpeed = SweepCalendarSpeed(calendar);
        if (beam.RotationPeriodHours <= 0f || calendarSpeed <= 0f) return 0f;

        float periodHours = beam.RotationPeriodHours * (ReferenceShaftSpeed / DisplayShaftSpeed);
        float revolutionRealSec = periodHours * 3600f / calendarSpeed;
        return widthDeg / 360f * revolutionRealSec;
    }

    // Dust puff calibration breaks down outside this band: readable and puffy at LowRps and below, and
    // cut off entirely at HighRps and above, where a beam sweeping that fast smears the motes into a
    // blur, not a readable puff. BeaconBeamParticles interpolates BeamTuning's Low/High dust life and
    // lead between these two points, and drops the effect past HighRps.
    public const float DustSweepLowRps = 0.2f, DustSweepHighRps = 1f;

    // Real-time revolutions per second the beam is currently sweeping at, or 0 when it isn't turning.
    // Calendar hours per revolution only means something once converted through the sweep clock's own
    // real-time speed, the same conversion NominalFlashSeconds uses for its quoted flash duration.
    public static float CurrentRevolutionsPerSecond(BeaconInfo info, IGameCalendar calendar)
    {
        if (!IsSweeping(info)) return 0f;
        float calendarSpeed = SweepCalendarSpeed(calendar);
        if (info.SweepPeriodHours <= 0f || calendarSpeed <= 0f) return 0f;
        return calendarSpeed / (info.SweepPeriodHours * 3600f);
    }

    // Every beam sits evenly spaced around beam 0's heading, matching the mesh, the far blip, the point
    // lights and the dust: sharing this one formula is what keeps them all agreeing on where beam i points.
    public static float BeamHeadingRad(float baseHeadingRad, int beamIndex, int beamCount) =>
        baseHeadingRad + beamIndex * (GameMath.TWOPI / beamCount);

    // BeamCount clamped to what a housing can actually produce, so every renderer loops the same range
    // even off a stale or pre-feature snapshot.
    public static int ClampedBeamCount(BeaconInfo info) =>
        Math.Clamp(info.BeamCount, BeamSignature.MinBeamCount, BeamSignature.MaxBeamCount);

    public static float SweepPeriodHours(in BeamProfile beam, float shaftSpeed)
    {
        float speed = MathF.Abs(shaftSpeed);
        if (beam.RotationPeriodHours <= 0f || speed <= 0.0001f) return 0f;

        float period = beam.RotationPeriodHours * (ReferenceShaftSpeed / speed);
        return GameMath.Clamp(period,
            beam.RotationPeriodHours * MinPeriodFraction,
            beam.RotationPeriodHours * MaxPeriodFraction);
    }

    // How much of its brightness the beam keeps against the current sky light, 1 in full dark and 0 once
    // the sky is bright enough to wash it out. This mirrors the smoothstep in beaconbeam.fsh exactly, so
    // anything gating on the beam's visibility CPU-side agrees with what the shader actually draws. Feed it
    // Calendar.DayLightStrength, which comes from the sun's elevation and so tracks season and latitude on
    // its own.
    public static float DayFadeFactor(float dayLight)
    {
        float low = BeamTuning.DayFadeLow, high = BeamTuning.DayFadeHigh;
        if (dayLight <= low) return 1f;
        if (dayLight >= high) return 0f;
        return 1f - GameMath.SmoothStep((dayLight - low) / (high - low));
    }

    // Mesh-beam level of detail. The far-distance blip renders continuously at every range, so the
    // mesh beam only has to dissolve smoothly into it at the far edge instead of cutting out. Full within
    // the near band, fading to 0 across the tunable LOD range, and not drawn past that, where the blip
    // carries it alone and a 3D cone would be sub-pixel anyway. The band slides out to the player's view
    // distance when that's larger, keeping its tuned width, so a beacon you can see the tower of still
    // has its beam.
    public static float MeshLodFactor(double dist, float viewDistanceBlocks)
    {
        float start = MathF.Max(BeamTuning.LodStartBlocks, viewDistanceBlocks);
        float end = start + MathF.Max(0f, BeamTuning.LodEndBlocks - BeamTuning.LodStartBlocks);
        if (dist <= start) return 1f;
        if (dist >= end) return 0f;
        // Reaching here means start < dist < end, so end > start and the divide is safe even if a tuner
        // inverts the two (an inverted band just collapses to a hard cutoff via the guards above).
        float t = (float)((dist - start) / (end - start));
        return GameMath.SmoothStep(1f - t);
    }
}
