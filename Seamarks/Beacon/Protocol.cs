using ProtoBuf;
using System.ComponentModel;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon;

[ProtoContract]
public class BeaconPosRef
{
    [ProtoMember(1)] public int X;
    [ProtoMember(2)] public int Y;
    [ProtoMember(3)] public int Z;
    [ProtoMember(4)] public int Dimension;

    public static BeaconPosRef From(BlockPos pos) => new()
    {
        X = pos.X,
        Y = pos.Y,
        Z = pos.Z,
        Dimension = pos.dimension,
    };

    public BlockPos ToBlockPos() => new(X, Y, Z, Dimension);
}

// Server -> client: beacons left this player's range set (or were destroyed). Batched since several
// can leave at once, e.g. on teleport.
[ProtoContract]
public class BeaconRemovePacket
{
    [ProtoMember(1)] public BeaconPosRef[] Positions = [];
}

// Client to server: the sender started or stopped the eye-shield pose. Already
// applied locally; the server replays it on the sender's entity so it replicates to everyone else.
// Purely cosmetic, so it needs no validation. The only client->server packet on this channel.
[ProtoContract]
public class BeaconGazeStatePacket
{
    [ProtoMember(1)] public bool Gazing;
}

// Server -> client: sent once on join/respawn so a client that missed incremental updates isn't left
// with a stale set.
[ProtoContract]
public class BeaconFullSyncPacket
{
    [ProtoMember(1)] public BeaconInfo[] Beacons = [];
}

// Server to client: the server's own reading of the detached sweep clock, so a client can work out how
// far its local clock sits from the server's and draw every beam in the phase everyone else sees. Sent on
// join and repeated on a slow timer, which bounds how far the two machines' clocks can drift apart
// between readings. Carries no beacon of its own: one reading fixes the clock for all of them at once.
// Pointless while the sweep tracks world time, since the calendar is already synced, and not sent then.
[ProtoContract]
public class BeaconSweepSyncPacket
{
    [ProtoMember(1)] public double ServerSweepHours { get; set; }
}

// One tier's slice of BeaconTuningPacket. Everything the client reads to draw fade, cutoff and the beam
// cone lives here; the fuel amounts and the rain-douse flag stay server code's business and never cross.
[ProtoContract]
public class BeaconTierTuning
{
    [ProtoMember(1)] public float BaseRangeBlocks;
    [ProtoMember(2)] public float RangePerElevationBlock;
    [ProtoMember(3)] public float ElevationBonusStart;
    [ProtoMember(4)] public float FalloffStartFraction;
    [ProtoMember(5)] public float HousingRangeBonus;
    [ProtoMember(6)] public float[] PlateRangeBonusPerMetal = []; // one entry per PlateMetal

    [ProtoMember(7)] public float FocusedBeamLength;
    [ProtoMember(8)] public float UnfocusedBeamLength;

    // DefaultValue for the same reason BeaconInfo.BeamCount carries one: protobuf-net skips a field equal
    // to its declared default, and without this a genuine false would never leave the wire and would read
    // back as the initializer's true. Both sides need this: each runs the right-click and has to reach the
    // same verdict on whether the beacon is burning.
    [ProtoMember(9), DefaultValue(true)] public bool RequiresFuel = true;

    [ProtoMember(10)] public float BlipSizeScale = 1f;

    // The admin-configured solid fuels for this tier, as parallel arrays since protobuf has no tuple
    // support. Both sides need this too: each runs the right-click and has to reach the same verdict on
    // whether a held item is fuel.
    [ProtoMember(11)] public string[] ItemFuelCodes = [];
    [ProtoMember(12)] public float[] ItemFuelHours = [];
}

// Server to client: the effective per-tier tuning, sent once on join. One row per tier, indexed by the
// BeaconTier enum.
[ProtoContract]
public class BeaconTuningPacket
{
    [ProtoMember(1)] public BeaconTierTuning[] Tiers = [];

    // Not per-tier: which clock every beacon's optic turns on. It has to cross because the server stamps
    // the sweep anchor in this clock's hours and every client reads that anchor back in the same unit.
    [ProtoMember(2), DefaultValue(true)] public bool SweepTracksWorldTime = true;

    public static BeaconTuningPacket FromCurrent() => new()
    {
        Tiers = [.. BeaconArchetypeData.ByTier.Select(a => new BeaconTierTuning
        {
            BaseRangeBlocks = a.BaseRangeBlocks,
            RangePerElevationBlock = a.RangePerElevationBlock,
            ElevationBonusStart = a.ElevationBonusStart,
            FalloffStartFraction = a.FalloffStartFraction,
            HousingRangeBonus = a.HousingRangeBonus,
            PlateRangeBonusPerMetal = [.. a.PlateRangeBonusPerPlateByMetal],
            FocusedBeamLength = a.Beam.Focused.LengthBlocks,
            UnfocusedBeamLength = a.Beam.Unfocused.LengthBlocks,
            RequiresFuel = a.RequiresFuel,
            BlipSizeScale = a.BlipSizeScale,
            ItemFuelCodes = [.. a.ConfiguredItemFuels.Select(f => f.Code.ToShortString())],
            ItemFuelHours = [.. a.ConfiguredItemFuels.Select(f => f.Hours)],
        })],
        SweepTracksWorldTime = BeamState.SweepTracksWorldTime,
    };

    public void ApplyToArchetypes()
    {
        BeamState.SweepTracksWorldTime = SweepTracksWorldTime;

        var byTier = BeaconArchetypeData.ByTier;
        for (int i = 0; i < byTier.Length; i++)
        {
            // A packet from a version with fewer tiers than this build knows about defaults the extra
            // ones to an empty row instead of indexing off the end.
            BeaconTierTuning row = i < Tiers.Length ? Tiers[i] : new BeaconTierTuning();

            List<(AssetLocation, float)> fuels = [];
            int fuelCount = Math.Min(row.ItemFuelCodes.Length, row.ItemFuelHours.Length);
            for (int f = 0; f < fuelCount; f++)
            {
                if (BeaconArchetypeData.TryParseCode(row.ItemFuelCodes[f], out AssetLocation? fuelCode))
                {
                    fuels.Add((fuelCode, row.ItemFuelHours[f]));
                }
            }

            // The fuel amounts and the rain-douse flag never sync, and the client never reads them, so
            // carry this client's own copies through untouched. RequiresFuel is the exception: it does
            // cross, since the client's own IsBurning turns on it.
            var current = byTier[i];
            BeaconArchetypeData.ApplyTuning((BeaconTier)i, new BeaconArchetypeData.AppliedTuning
            {
                BaseRangeBlocks = row.BaseRangeBlocks,
                RangePerElevationBlock = row.RangePerElevationBlock,
                ElevationBonusStart = row.ElevationBonusStart,
                FalloffStartFraction = row.FalloffStartFraction,
                HousingRangeBonus = row.HousingRangeBonus,
                PlateRangeBonusPerMetal = row.PlateRangeBonusPerMetal,
                FocusedBeamLength = row.FocusedBeamLength,
                UnfocusedBeamLength = row.UnfocusedBeamLength,
                FuelHoursPerLitre = current.FuelHoursPerLitre,
                MaxFuelHours = current.MaxFuelHours,
                FuelBurnTimeMultiplier = current.FuelBurnTimeMultiplier,
                RainDouseEnabled = current.RainDouseEnabled,
                RequiresFuel = row.RequiresFuel,
                BlipSizeScale = row.BlipSizeScale,
                ConfiguredItemFuels = [.. fuels],
            });
        }
    }
}
