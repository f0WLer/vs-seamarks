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

// Server to client: the effective per-tier tuning, sent once on join. Everything the client reads to
// draw fade, cutoff and the beam cone rides along; the fuel numbers and the rain-douse flag stay server
// code's business and never cross. Each array is indexed by the BeaconTier enum, and the plate bonuses
// are flattened three metals per tier, protobuf handling a flat float array more cleanly than a jagged
// one.
[ProtoContract]
public class BeaconTuningPacket
{
    [ProtoMember(1)] public float[] BaseRangeBlocksByTier = [];
    [ProtoMember(2)] public float[] RangePerElevationBlockByTier = [];
    [ProtoMember(3)] public float[] ElevationBonusStartByTier = [];
    [ProtoMember(4)] public float[] FalloffStartFractionByTier = [];
    [ProtoMember(5)] public float[] HousingRangeBonusByTier = [];
    [ProtoMember(6)] public float[] PlateRangeBonusFlat = []; // index tier * 3 + metal
    [ProtoMember(7)] public float[] FocusedBeamLengthByTier = [];
    [ProtoMember(8)] public float[] UnfocusedBeamLengthByTier = [];
    [ProtoMember(9)] public bool[] RequiresFuelByTier = [];
    [ProtoMember(10)] public float[] BlipSizeScaleByTier = [];

    // Not per-tier: which clock every beacon's optic turns on. It has to cross because the server stamps
    // the sweep anchor in this clock's hours and every client reads that anchor back in the same unit.
    // DefaultValue for the same reason BeaconInfo.BeamCount carries one: protobuf-net skips a field equal
    // to its declared default, and without this a genuine false would never leave the wire and would read
    // back as the initializer's true.
    [ProtoMember(11), DefaultValue(true)] public bool SweepTracksWorldTime = true;

    // The admin-configured solid fuels, one entry per (tier, code) across these three parallel arrays. The
    // per-tier lists are jagged, and protobuf takes a flat array more cleanly, which is why the plate
    // bonuses above are flattened too. Both sides need this: each runs the right-click and has to reach the
    // same verdict on whether a held item is fuel.
    [ProtoMember(12)] public int[] ItemFuelTiers = [];
    [ProtoMember(13)] public string[] ItemFuelCodes = [];
    [ProtoMember(14)] public float[] ItemFuelHours = [];

    private const int Metals = 3;

    public static BeaconTuningPacket FromCurrent()
    {
        var byTier = BeaconArchetypeData.ByTier;
        var plateFlat = new float[byTier.Length * Metals];
        for (int i = 0; i < byTier.Length; i++)
        {
            var perMetal = byTier[i].PlateRangeBonusPerPlateByMetal;
            for (int m = 0; m < Metals; m++) plateFlat[i * Metals + m] = m < perMetal.Length ? perMetal[m] : 0f;
        }

        List<int> fuelTiers = [];
        List<string> fuelCodes = [];
        List<float> fuelHours = [];
        for (int i = 0; i < byTier.Length; i++)
        {
            foreach (var (code, hours) in byTier[i].ConfiguredItemFuels)
            {
                fuelTiers.Add(i);
                fuelCodes.Add(code.ToShortString());
                fuelHours.Add(hours);
            }
        }

        return new BeaconTuningPacket
        {
            BaseRangeBlocksByTier = [.. byTier.Select(a => a.BaseRangeBlocks)],
            RangePerElevationBlockByTier = [.. byTier.Select(a => a.RangePerElevationBlock)],
            ElevationBonusStartByTier = [.. byTier.Select(a => a.ElevationBonusStart)],
            FalloffStartFractionByTier = [.. byTier.Select(a => a.FalloffStartFraction)],
            HousingRangeBonusByTier = [.. byTier.Select(a => a.HousingRangeBonus)],
            PlateRangeBonusFlat = plateFlat,
            FocusedBeamLengthByTier = [.. byTier.Select(a => a.Beam.Focused.LengthBlocks)],
            UnfocusedBeamLengthByTier = [.. byTier.Select(a => a.Beam.Unfocused.LengthBlocks)],
            RequiresFuelByTier = [.. byTier.Select(a => a.RequiresFuel)],
            BlipSizeScaleByTier = [.. byTier.Select(a => a.BlipSizeScale)],
            SweepTracksWorldTime = BeamState.SweepTracksWorldTime,
            ItemFuelTiers = [.. fuelTiers],
            ItemFuelCodes = [.. fuelCodes],
            ItemFuelHours = [.. fuelHours],
        };
    }

    public void ApplyToArchetypes()
    {
        BeamState.SweepTracksWorldTime = SweepTracksWorldTime;

        var byTier = BeaconArchetypeData.ByTier;
        var fuelsByTier = new List<(AssetLocation, float)>[byTier.Length];

        // Regrouped off the flat arrays. The tier range check is what keeps a packet from a version that
        // knows more tiers than this build from indexing off the end, and TryParseCode drops any code that
        // didn't survive the trip.
        int fuelCount = Math.Min(ItemFuelTiers.Length, Math.Min(ItemFuelCodes.Length, ItemFuelHours.Length));
        for (int f = 0; f < fuelCount; f++)
        {
            int tier = ItemFuelTiers[f];
            if (tier < 0 || tier >= byTier.Length) continue;
            if (!BeaconArchetypeData.TryParseCode(ItemFuelCodes[f], out AssetLocation? fuelCode)) continue;
            (fuelsByTier[tier] ??= []).Add((fuelCode, ItemFuelHours[f]));
        }

        for (int i = 0; i < byTier.Length; i++)
        {
            var plateBonus = new float[Metals];
            for (int m = 0; m < Metals; m++)
            {
                int idx = i * Metals + m;
                plateBonus[m] = idx < PlateRangeBonusFlat.Length ? PlateRangeBonusFlat[idx] : 0f;
            }

            // The fuel amounts and the rain-douse flag never sync, and the client never reads them, so
            // carry this client's own copies through untouched. RequiresFuel is the exception: it does
            // cross, since the client's own IsBurning turns on it.
            var current = byTier[i];
            BeaconArchetypeData.ApplyTuning((BeaconTier)i, new BeaconArchetypeData.AppliedTuning
            {
                BaseRangeBlocks = AtOrZero(BaseRangeBlocksByTier, i),
                RangePerElevationBlock = AtOrZero(RangePerElevationBlockByTier, i),
                ElevationBonusStart = AtOrZero(ElevationBonusStartByTier, i),
                FalloffStartFraction = AtOrZero(FalloffStartFractionByTier, i),
                HousingRangeBonus = AtOrZero(HousingRangeBonusByTier, i),
                PlateRangeBonusPerMetal = plateBonus,
                FocusedBeamLength = AtOrZero(FocusedBeamLengthByTier, i),
                UnfocusedBeamLength = AtOrZero(UnfocusedBeamLengthByTier, i),
                FuelHoursPerLitre = current.FuelHoursPerLitre,
                MaxFuelHours = current.MaxFuelHours,
                FuelBurnTimeMultiplier = current.FuelBurnTimeMultiplier,
                RainDouseEnabled = current.RainDouseEnabled,
                RequiresFuel = i < RequiresFuelByTier.Length ? RequiresFuelByTier[i] : true,
                BlipSizeScale = i < BlipSizeScaleByTier.Length ? BlipSizeScaleByTier[i] : 1f,
                ConfiguredItemFuels = fuelsByTier[i] == null ? [] : [.. fuelsByTier[i]],
            });
        }
    }

    // Tolerates an array shorter than the tier count, which a version mismatch between server and client
    // could produce, by reading a missing entry as zero instead of throwing.
    private static float AtOrZero(float[] arr, int i) => i < arr.Length ? arr[i] : 0f;
}
