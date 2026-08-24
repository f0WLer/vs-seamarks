using System.ComponentModel;
using ProtoBuf;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon;

// The single canonical beacon state: the server registry's value, its client mirror, the network
// message (sent directly and nested in BeaconFullSyncPacket), and the persisted snapshot all use
// this one type. Burn state is worked out from the ignition timestamp on demand instead of ticked, so a
// beacon needs no per-frame upkeep to know whether it's still lit.
//
// The proto field numbers are both the on-disk snapshot and the on-wire format, so keep the existing
// numbers stable and only ever append new ones.
[ProtoContract]
public sealed class BeaconInfo
{
    [ProtoMember(1)] public BeaconPosRef PosRef { get; set; } = new();
    [ProtoMember(2)] public BeaconTier Tier { get; set; }

    // -1 while unlit; otherwise the calendar-hours timestamp burn expiry is measured from.
    [ProtoMember(3)] public double IgnitedAtTotalHours { get; set; } = -1;

    [ProtoMember(4)] public float FuelHoursAtIgnition { get; set; }

    [ProtoMember(5)] public int PlateCount { get; set; }
    [ProtoMember(6)] public bool MechanicalActive { get; set; }
    [ProtoMember(7)] public PlateMetal PlateMetal { get; set; } // meaningless while PlateCount == 0
    [ProtoMember(8)] public bool HasHousing { get; set; }

    // Radians, atan2(dx,dz) convention. Only meaningful for tiers with a directional beam.
    [ProtoMember(9)] public float HeadingRad { get; set; }

    // Player-configurable beam "signature" (directional tiers only): how many evenly-spaced beams share
    // the rotation, and each beam's angular width, which doubles as its flash duration on the far blip. Old
    // snapshots without these fields deserialize to the initializer defaults below (protobuf-net leaves
    // absent members at their constructed value), so pre-feature beacons keep working.
    //
    // protobuf-net skips writing a field that equals its declared default, to save bytes, and without the
    // DefaultValue attribute below that comparison is against 0, not DefaultBeamCount. A housing with no
    // panels genuinely contributes 0, and 0 would then vanish off the wire and read back as the
    // initializer's 1, quietly reporting a lens that isn't there. DefaultValue moves the skip-point onto 1
    // instead, so a real 0 always gets sent.
    [ProtoMember(10), DefaultValue(DefaultBeamCount)] public int BeamCount { get; set; } = DefaultBeamCount;
    [ProtoMember(11)] public float BeamWidthDeg { get; set; } = DefaultBeamWidthDeg;

    // Optional per-beam color overrides, packed 0xRRGGBB; index i is beam i's color, a slot of -1
    // (or an absent/short array) means "use the tier tint". Null in the common all-default case, so
    // pre-feature snapshots and beacons that never set a color add nothing to the wire/disk form.
    [ProtoMember(12)] public int[]? BeamColors { get; set; }

    // Optional per-beam angular-width overrides in degrees; index i is beam i's width, a slot of <= 0
    // (or an absent/short array) means "use the beacon-wide BeamWidthDeg above". Null when every beam
    // shares the base width, so an unmixed beacon adds nothing to the wire/disk form.
    [ProtoMember(13)] public float[]? BeamWidthsDeg { get; set; }

    // The sweep, anchored so it stays continuous when the drive speed changes. A time-anchored angle
    // assumes a fixed period, so changing the period alone would teleport the beam; the server captures
    // the angle it had reached and the moment it changed, and every client sweeps on from there.
    //
    // SweepPeriodHours <= 0 means "not turning", which is what an unpowered or ungoverned beacon reports.
    [ProtoMember(14)] public float SweepAnchorRad { get; set; }
    [ProtoMember(15)] public double SweepAnchorTotalHours { get; set; } = -1;
    [ProtoMember(16)] public float SweepPeriodHours { get; set; }

    // An operator has pinned this beacon to burn forever, so it counts as lit off its ignition anchor
    // alone, with no fuel-remaining check. Defaults false, so older snapshots stay ordinary.
    [ProtoMember(17)] public bool Everburning { get; set; }

    public const int DefaultBeamCount = 1;
    public const float DefaultBeamWidthDeg = 16f; // ~matches the calibrated thin fan; crisp flash

    private BlockPos? pos;

    // The registry keys on this, so it stays a real cached BlockPos and isn't rebuilt on every access.
    // The setter keeps the serialized form in step.
    public BlockPos Pos
    {
        get => pos ??= PosRef.ToBlockPos();
        set
        {
            pos = value;
            PosRef = BeaconPosRef.From(value);
        }
    }

    // Beam beamIndex's angular width in degrees: its override if one is set, else the beacon-wide base
    // (which also covers pre-feature snapshots, where BeamWidthsDeg is null). Mirrors BeamColor.Resolve.
    public float BeamWidthDegOf(int beamIndex)
    {
        float[]? w = BeamWidthsDeg;
        if (w != null && (uint)beamIndex < (uint)w.Length && w[beamIndex] > 0f) return w[beamIndex];
        return BeamWidthDeg;
    }

    public bool IsBurning(double nowTotalHours) =>
        IgnitedAtTotalHours >= 0 && (Everburning || !BeaconArchetypeData.Of(Tier).RequiresFuel
            || nowTotalHours - IgnitedAtTotalHours < FuelHoursAtIgnition);

    // 0..1 share of the fuel pool still left, for anything that wants to render a beacon as running low.
    // An everburning or fuel-free beacon always reads full, since it never actually runs down.
    public float RemainingFuelFraction(double nowTotalHours)
    {
        ref readonly BeaconArchetypeData archetype = ref BeaconArchetypeData.Of(Tier);
        if (Everburning || !archetype.RequiresFuel || archetype.MaxFuelHours <= 0f) return 1f;
        if (IgnitedAtTotalHours < 0) return 0f;

        float remaining = MathF.Max(0f, FuelHoursAtIgnition - (float)(nowTotalHours - IgnitedAtTotalHours));
        return GameMath.Clamp(remaining / archetype.MaxFuelHours, 0f, 1f);
    }

    // A housing with no panels fitted is just a cover glass, so the beam still needs the raw, unfocused
    // look until a lens is actually seated. HasHousing alone only tells you the block is on.
    public bool HasFocusingLens => HasHousing && BeamCount > 0;
}
