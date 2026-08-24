using Vintagestory.API.Common;

namespace Seamarks.Beacon.Server;

// Player/admin-facing tuning, loaded once at server startup via ICoreAPI.LoadModConfig. Server-only:
// the client never loads this file, it receives the values it needs to render over the network (the
// BeaconTuningPacket), so the two sides can never disagree about range, falloff or beam length even on
// separate installs.
public class SeamarksConfig
{
    public const string FileName = "seamarksbeacons.json";

    public FireTuning Fire = new()
    {
        BaseRangeBlocks = 700f,
        RangePerElevationBlock = 4f,
        ElevationBonusStart = 0f,
        FalloffStartFraction = 0.8f,
        FuelBurnTimeMultiplier = 1f,
        MaxFuelHours = 24f,
        RainDouseEnabled = true,
        RequiresFuel = true,
        BlipSizeScale = 1f,
    };

    public OilLampTuning OilLamp = new()
    {
        BaseRangeBlocks = 1000f,
        RangePerElevationBlock = 4f,
        ElevationBonusStart = 0f,
        FalloffStartFraction = 0.8f,
        MaxFuelLitres = 4f,
        FuelHoursPerLitre = 6f,
        HousingRangeBonus = 500f,
        PlateRangeBonusPerMetal = [110f, 150f, 250f],
        VisualBeamLength = 128f,
        VisualBeamNoHousingLength = 100f,
        RequiresFuel = true,
        BlipSizeScale = 1f,
    };

    // The OilLamp burns any liquid whose code contains "oilportion", which covers vanilla and the oils from
    // Vintage Engineering and Expanded Foods without naming them. These two lists are for the oils that
    // don't follow that convention, and for vetoing one that does. Wildcards work, so "somemod:*" is valid,
    // and Blocked wins over both the convention and ExtraOilCodes.
    public string[] ExtraOilCodes = [];
    public string[] BlockedOilCodes = [];

    // Per-oil overrides of the burn hours one litre yields. A key is an oil code (wildcards work, like the
    // two lists above), the value is the hours a litre of that oil adds. Any oil not listed uses
    // OilLamp.FuelHoursPerLitre. There's no GUI for this, since ConfigLib can't render an open-ended map;
    // it's edited here in the file.
    public Dictionary<string, float> FuelHoursPerLitreOverrides = [];

    // Solid items the oil lamp will take as fuel, keyed by item code (wildcards work, same as the lists
    // above), the value being the burn hours one item adds. Empty by default, so out of the box the lamp
    // still drinks only oil. "game:fat-rendered": 3.0 is the case this was built for. No GUI, for the same
    // reason as the map above: ConfigLib can't render an open-ended map.
    //
    // Listing an item here is the only way the lamp accepts a solid, so a code's burn duration in its own
    // JSON has no say. Anything listed is consumed one item per right-click.
    public Dictionary<string, float> OilLampItemFuelHours = [];

    // Whether a lit beacon shows up on the world map's "Beacons" layer. Off leaves the layer's toggle in
    // place but sends nothing to it, so the map stays clear. Read live, so flipping it in-game takes hold
    // within a refresh on an integrated server.
    public bool ShowBeaconsOnMap = true;

    // Whether a driven optic turns on world time. On, it stops when the world clock does and races when
    // that clock is sped up, which is what /time stop and /time speed are for. Off, it keeps turning at
    // the rate a default world would give it, so a frozen or fast-forwarded world still has working
    // lighthouses. Fuel burn stays on world time whichever way this is set.
    public bool SpinTracksWorldTime = true;

    // Both tiers grow their range the same way. BaseRangeBlocks is the reach at sea level, then every
    // block the beacon stands above ElevationBonusStart adds RangePerElevationBlock more, and the oil
    // lamp's plate and housing bonuses stack on top. Set RangePerElevationBlock to 0 and BaseRangeBlocks
    // becomes a flat reach that ignores how high the beacon sits.
    public abstract class RangeTuning
    {
        public float BaseRangeBlocks;
        public float RangePerElevationBlock;
        public float ElevationBonusStart;
        public float FalloffStartFraction; // fraction of max range where the far blip starts to dim

        // When off, the beacon lights with no fuel and stays lit forever without any. Fuelling still works,
        // it just stops being required.
        public bool RequiresFuel = true;

        // Multiplier on the tier's far-out light blip (Fire's smoke blip scales the same way). 1 is the
        // built-in size.
        public float BlipSizeScale = 1f;
    }

    public class FireTuning : RangeTuning
    {
        public float FuelBurnTimeMultiplier; // scales each solid fuel item's own burn time
        public float MaxFuelHours;           // fuel pool cap, in burn hours
        public bool RainDouseEnabled;        // whether heavy unsheltered rain can put the flame out
    }

    public class OilLampTuning : RangeTuning
    {
        public float MaxFuelLitres;                        // tank size; with FuelHoursPerLitre this sets the burn cap
        public float FuelHoursPerLitre;                    // burn hours gained per litre of oil
        public float HousingRangeBonus;                    // flat range added by a fitted Optics Housing
        public float[] PlateRangeBonusPerMetal = [0f, 0f, 0f]; // per plate, indexed gold, silver, electrum
        public float VisualBeamLength;                     // cone length in blocks with a housing fitted
        public float VisualBeamNoHousingLength;            // cone length for the bare lamp, no housing
    }

    // The JSON file is a boundary someone can type anything into, unlike the rest of this mod's internal
    // data, so clamp here instead of trusting it. A FalloffStartFraction of exactly 1 would divide by zero
    // in the client's fade curve.
    public void ApplyToArchetypes()
    {
        BeaconArchetypeData.ApplyOilCodes(ParseCodes(ExtraOilCodes), ParseCodes(BlockedOilCodes));
        BeaconArchetypeData.ApplyOilHourOverrides(ParseOverrides(FuelHoursPerLitreOverrides));
        BeamState.SweepTracksWorldTime = SpinTracksWorldTime;

        // Fire: solid fuel capped at MaxFuelHours, each item's burn time scaled by the multiplier. It has
        // no beam, plates or housing, so those pass through as zero.
        BeaconArchetypeData.ApplyTuning(BeaconTier.Fire, new BeaconArchetypeData.AppliedTuning
        {
            BaseRangeBlocks = MathF.Max(0f, Fire.BaseRangeBlocks),
            RangePerElevationBlock = MathF.Max(0f, Fire.RangePerElevationBlock),
            ElevationBonusStart = Fire.ElevationBonusStart,
            FalloffStartFraction = Math.Clamp(Fire.FalloffStartFraction, 0f, 0.99f),
            HousingRangeBonus = 0f,
            PlateRangeBonusPerMetal = [0f, 0f, 0f],
            FocusedBeamLength = 0f,
            UnfocusedBeamLength = 0f,
            FuelHoursPerLitre = 0f,
            MaxFuelHours = MathF.Max(0f, Fire.MaxFuelHours),
            FuelBurnTimeMultiplier = MathF.Max(0f, Fire.FuelBurnTimeMultiplier),
            RainDouseEnabled = Fire.RainDouseEnabled,
            RequiresFuel = Fire.RequiresFuel,
            BlipSizeScale = MathF.Max(0.1f, Fire.BlipSizeScale),
            ConfiguredItemFuels = [], // the fire already burns anything with a real burn duration
        });

        // OilLamp: liquid fuel, N hours per litre, capped at M litres, so the hour cap tracks litres even
        // if the player retunes hours-per-litre. Plate and housing bonuses and the two beam lengths are
        // its own.
        float hoursPerLitre = MathF.Max(0f, OilLamp.FuelHoursPerLitre);
        float maxLitres = MathF.Max(0f, OilLamp.MaxFuelLitres);
        BeaconArchetypeData.ApplyTuning(BeaconTier.OilLamp, new BeaconArchetypeData.AppliedTuning
        {
            BaseRangeBlocks = MathF.Max(0f, OilLamp.BaseRangeBlocks),
            RangePerElevationBlock = MathF.Max(0f, OilLamp.RangePerElevationBlock),
            ElevationBonusStart = OilLamp.ElevationBonusStart,
            FalloffStartFraction = Math.Clamp(OilLamp.FalloffStartFraction, 0f, 0.99f),
            HousingRangeBonus = MathF.Max(0f, OilLamp.HousingRangeBonus),
            PlateRangeBonusPerMetal = ClampNonNegative(OilLamp.PlateRangeBonusPerMetal),
            FocusedBeamLength = MathF.Max(0f, OilLamp.VisualBeamLength),
            UnfocusedBeamLength = MathF.Max(0f, OilLamp.VisualBeamNoHousingLength),
            FuelHoursPerLitre = hoursPerLitre,
            MaxFuelHours = maxLitres * hoursPerLitre,
            FuelBurnTimeMultiplier = 0f,
            RainDouseEnabled = false,
            RequiresFuel = OilLamp.RequiresFuel,
            BlipSizeScale = MathF.Max(0.1f, OilLamp.BlipSizeScale),
            ConfiguredItemFuels = ParseOverrides(OilLampItemFuelHours),
        });
    }

    // A copy with every entry floored at zero, so a negative in the file can't subtract range. Length
    // fixing to one slot per metal is left to ApplyTuning, the single choke point both this and the packet
    // pass through.
    private static float[] ClampNonNegative(float[]? src)
    {
        if (src == null) return [];
        var clamped = new float[src.Length];
        for (int i = 0; i < src.Length; i++) clamped[i] = MathF.Max(0f, src[i]);
        return clamped;
    }

    // Blank and malformed entries are dropped by TryParseCode. A code with no domain gets "game", the same
    // default AssetLocation uses everywhere else, so "oilportion-flax" means the vanilla one.
    private static AssetLocation[] ParseCodes(string[]? codes)
    {
        if (codes == null) return [];

        var parsed = new List<AssetLocation>(codes.Length);
        foreach (string code in codes)
        {
            if (BeaconArchetypeData.TryParseCode(code, out AssetLocation? loc)) parsed.Add(loc);
        }
        return [.. parsed];
    }

    // A negative override is floored at zero on top of the same parse, so a typo can't drain the tank on
    // refuel.
    private static (AssetLocation code, float hoursPerLitre)[] ParseOverrides(Dictionary<string, float>? overrides)
    {
        if (overrides == null) return [];

        var parsed = new List<(AssetLocation, float)>(overrides.Count);
        foreach (var (code, hours) in overrides)
        {
            if (BeaconArchetypeData.TryParseCode(code, out AssetLocation? loc)) parsed.Add((loc, MathF.Max(0f, hours)));
        }
        return [.. parsed];
    }
}
