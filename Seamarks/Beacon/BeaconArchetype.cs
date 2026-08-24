using System.Diagnostics.CodeAnalysis;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Seamarks.Beacon;

// Derived from the placed block's "tier" variant, not chosen or upgraded in place. The Optics Housing
// is a modular attachment on OilLamp, not its own tier.
public enum BeaconTier
{
    Fire = 0,
    OilLamp = 1,
}

// Order matters, since it indexes PlateRangeBonusPerPlateByMetal. A beacon's plates must all be one
// metal.
public enum PlateMetal
{
    Gold = 0,
    Silver = 1,
    Electrum = 2,
}

// Kept in code and not JSON while these numbers are still being balanced. The player-configurable subset
// is exposed through SeamarksConfig.
public readonly struct BeaconArchetypeData
{
    public required string VariantCode { get; init; } // matches Block.Variant["tier"]

    // Reach in blocks with the light sitting at sea level, before any elevation, plates or housing.
    public required float BaseRangeBlocks { get; init; }

    // Blocks of range gained per block the light stands above ElevationBonusStart, so a taller tower
    // reaches further along a straight line. Zero makes placement height count for nothing.
    public required float RangePerElevationBlock { get; init; }

    // Height above sea level where that elevation bonus starts counting. A beacon below it gets only
    // BaseRangeBlocks, so one at or under the waterline still lights the same as one on the shore.
    public required float ElevationBonusStart { get; init; }

    public required float ColorR { get; init; } // blip tint, 0-1
    public required float ColorG { get; init; }
    public required float ColorB { get; init; }

    public required float BlipSize { get; init; } // world-space radius of the close-range blip quad

    // Player-tunable multiplier on BlipSize (and, for Fire, its smoke blip too), left at 1 unless an
    // admin changes it. Kept separate from BlipSize itself so the base per-tier art direction and the
    // config override never fight over the same field.
    public required float BlipSizeScale { get; init; }

    // Where the blip draws, relative to the block position. Fire is a 2x2x2 multiblock whose position
    // is the anchor corner, so this recenters the blip across the footprint; OilLamp is one block, so
    // it's just the centre. Distance, fade and visibility maths all stay anchored to the block position,
    // and this only moves where the blip gets drawn.
    public required float BlipOffsetX { get; init; }
    public required float BlipOffsetY { get; init; }
    public required float BlipOffsetZ { get; init; }

    // Matched anywhere in the collectible's code path. Empty for Fire, which takes anything with a real
    // burn duration instead.
    public required string[] FuelPathKeywords { get; init; }

    public required float FuelHoursPerLitre { get; init; } // OilLamp only
    public required float MaxFuelHours { get; init; } // fuel pool cap, in burn-hours, both tiers

    public required float FuelBurnTimeMultiplier { get; init; } // Fire only, scales each fuel item's own burn time

    public required bool RainDouseEnabled { get; init; } // whether heavy unsheltered rain can put it out (Fire only)

    // When false, the beacon lights with no fuel and never burns out. Read on both sides (it decides the
    // client's own IsBurning too), so it rides the tuning packet, unlike the other fuel fields.
    public required bool RequiresFuel { get; init; }

    // False for OilLamp, which only takes oil out of a container. Otherwise anything that happens to
    // satisfy AcceptsFuel, this mod's own blocks included, could be shoved in by hand. A tier with this
    // off can still be given specific items through ConfiguredItemFuels below.
    public required bool AcceptsHeldItemFuel { get; init; }

    // Item codes an admin has named as burnable here, each with the hours one item adds. Always empty in
    // code: this exists so a server can let the oil lamp take something solid (rendered fat was the case
    // that prompted it) without opening it up to every combustible in the game. Syncs to clients, since
    // both sides decide independently whether a right-click is a refuel or the item's own use.
    public required (AssetLocation Code, float Hours)[] ConfiguredItemFuels { get; init; }

    // Range-boost data read from the flat DTO client-side; the plate/housing behavior (server-side)
    // owns whether a tier can actually gain them.
    public required int MaxPlates { get; init; }
    public required float[] PlateRangeBonusPerPlateByMetal { get; init; } // indexed by (int)PlateMetal
    public required float HousingRangeBonus { get; init; } // flat bonus, not stackable like plates

    // The complete static beam description (directionality, motion, shape). Read client-side off the
    // flat archetype because a far beacon has no block entity there to ask. Consolidates what used to
    // be a lone SupportsDirectionalBeam flag plus a rotation-period float.
    public required BeamProfile Beam { get; init; }

    public required float FalloffStartFraction { get; init; } // fraction of max range where the blip starts dimming

    // Mutable in place through ApplyTuning, which is the one path meant to write it. Kept inside the
    // assembly so another mod can't reach in and rewrite this mod's balance numbers.
    internal static readonly BeaconArchetypeData[] ByTier =
    [
        new BeaconArchetypeData
        {
            VariantCode = "fire",
            BaseRangeBlocks = 700f,
            RangePerElevationBlock = 4f,
            ElevationBonusStart = 0f,
            ColorR = 1f, ColorG = 0.55f, ColorB = 0.2f,
            BlipSize = 4.32f,
            BlipSizeScale = 1f,
            BlipOffsetX = 0.5f, BlipOffsetY = 1.75f, BlipOffsetZ = 0.5f, // center of the 1x1 footprint, up near the flame
            FuelPathKeywords = [],
            FuelHoursPerLitre = 0f,
            MaxFuelHours = 24f,
            FuelBurnTimeMultiplier = 1f,
            RainDouseEnabled = true,
            RequiresFuel = true,
            AcceptsHeldItemFuel = true,
            ConfiguredItemFuels = [],
            MaxPlates = 0,
            PlateRangeBonusPerPlateByMetal = [0f, 0f, 0f],
            HousingRangeBonus = 0f,
            Beam = BeamProfile.None,
            FalloffStartFraction = 0.8f,
        },
        new BeaconArchetypeData
        {
            VariantCode = "oillamp",
            BaseRangeBlocks = 1000f,
            RangePerElevationBlock = 4f,
            ElevationBonusStart = 0f,
            ColorR = 1f, ColorG = 0.9f, ColorB = 0.65f,
            BlipSize = 6f,
            BlipSizeScale = 1f,
            // X and Z off the reflector's rotation pivot, Y off the panel's own height, not the pivot's: a
            // beam this narrow makes the difference between "near the optic" and "the optic" obvious up
            // close, even though it never showed on the far blip's own tiny dot.
            BlipOffsetX = BlockEntityBeacon.ModelPivot.X, BlipOffsetY = BlockEntityBeacon.ReflectorOpeningYFraction, BlipOffsetZ = BlockEntityBeacon.ModelPivot.Z,
            FuelPathKeywords = ["oilportion"],
            FuelHoursPerLitre = 6f,
            MaxFuelHours = 24f, // kept in sync with 4L * 6h/L in SeamarksConfig.ApplyToArchetypes
            FuelBurnTimeMultiplier = 0f,
            RainDouseEnabled = false,
            RequiresFuel = true,
            AcceptsHeldItemFuel = false,
            ConfiguredItemFuels = [],
            MaxPlates = 4,
            // Electrum reflects best and gold worst. A full set of four electrum plates should clearly
            // outrange four of either other metal.
            PlateRangeBonusPerPlateByMetal = [110f, 150f, 250f],
            HousingRangeBonus = 500f,
            Beam = new BeamProfile
            {
                Directional = true,
                Pattern = BeamPattern.Rotating,
                RotationPeriodHours = 0.15f, // ~9 real seconds/revolution at the reference shaft speed
                // With the lens fitted: the tight, bright, flattened searchlight fan, with VerticalScale
                // well under 1. Tuned in-game and pasted back here, and seeds the focused live copy.
                Focused = new BeamShape
                {
                    LengthBlocks = 128f,
                    NearRadiusBlocks = 0.08f,
                    FarRadiusBlocks = 11f,
                    HorizontalScale = 1.6f,
                    VerticalScale = 0.23f,
                    RadialBands = 9.56f,
                    CoreRadiusFraction = 0.28f,
                    RadialSoftness = 1.5f,
                    LengthFadePow = 0.81f,
                    HaloIntensity = 0.16f,
                    CoreIntensity = 0.12f,
                },
                // No lens: the dimmer, rounder, shorter raw-lamp glow, with VerticalScale near 1 and a low
                // halo and core. Seeds the unfocused live copy.
                Unfocused = new BeamShape
                {
                    LengthBlocks = 100f,
                    NearRadiusBlocks = 0.2f,
                    FarRadiusBlocks = 10.4f,
                    HorizontalScale = 1.32f,
                    VerticalScale = 1.331f,
                    RadialBands = 6.76f,
                    CoreRadiusFraction = 0.23f,
                    RadialSoftness = 2.252f,
                    LengthFadePow = 4f,
                    HaloIntensity = 0.08f,
                    CoreIntensity = 0.03f,
                },
            },
            FalloffStartFraction = 0.8f,
        },
    ];

    public static ref readonly BeaconArchetypeData Of(BeaconTier tier)
    {
        int index = (int)tier;
        if (index < 0 || index >= ByTier.Length) index = 0;
        return ref ByTier[index];
    }

    // The slice of an archetype a server admin can retune, and that the sync packet carries to clients.
    // Handed over as one payload so the growing field list stays named at both call sites. Beam length
    // only bites on a directional tier, an omnidirectional one having no cone to resize.
    public struct AppliedTuning
    {
        public float BaseRangeBlocks;
        public float RangePerElevationBlock;
        public float ElevationBonusStart;
        public float FalloffStartFraction;
        public float HousingRangeBonus;
        public float[]? PlateRangeBonusPerMetal; // one entry per PlateMetal
        public float FocusedBeamLength;
        public float UnfocusedBeamLength;
        public float BlipSizeScale;

        // The client's own IsBurning depends on this, so it does get synced, unlike the fuel fields below.
        public bool RequiresFuel;

        // Synced for the same reason: both sides run the interaction and have to agree on whether a held
        // item is fuel here, or one of them will start using the item while the other burns it.
        public (AssetLocation Code, float Hours)[]? ConfiguredItemFuels;

        // Server-only, never synced and never read client-side, but carried through so one apply path
        // sets everything.
        public float FuelHoursPerLitre;
        public float MaxFuelHours;
        public float FuelBurnTimeMultiplier;
        public bool RainDouseEnabled;
    }

    // Overwrites the tunable fields with SeamarksConfig's effective values (server) or the network sync
    // (client), so the two sides can never disagree on them.
    public static void ApplyTuning(BeaconTier tier, in AppliedTuning t)
    {
        int index = (int)tier;
        BeaconArchetypeData a = ByTier[index];

        // A short plate array from a hand-edited file or a version-skewed packet would throw when indexed
        // by metal, so pin it to one slot per PlateMetal before it reaches the range maths.
        float[] plateBonus = new float[3];
        if (t.PlateRangeBonusPerMetal != null)
        {
            int n = Math.Min(3, t.PlateRangeBonusPerMetal.Length);
            for (int i = 0; i < n; i++) plateBonus[i] = t.PlateRangeBonusPerMetal[i];
        }

        ByTier[index] = a with
        {
            BaseRangeBlocks = t.BaseRangeBlocks,
            RangePerElevationBlock = t.RangePerElevationBlock,
            ElevationBonusStart = t.ElevationBonusStart,
            FalloffStartFraction = t.FalloffStartFraction,
            HousingRangeBonus = t.HousingRangeBonus,
            PlateRangeBonusPerPlateByMetal = plateBonus,
            FuelHoursPerLitre = t.FuelHoursPerLitre,
            MaxFuelHours = t.MaxFuelHours,
            FuelBurnTimeMultiplier = t.FuelBurnTimeMultiplier,
            RainDouseEnabled = t.RainDouseEnabled,
            RequiresFuel = t.RequiresFuel,
            BlipSizeScale = t.BlipSizeScale,
            ConfiguredItemFuels = t.ConfiguredItemFuels ?? [],
            // Only a directional optic has a cone to lengthen; Fire keeps its BeamProfile.None untouched.
            Beam = a.Beam.Directional
                ? a.Beam with
                {
                    Focused = a.Beam.Focused with { LengthBlocks = t.FocusedBeamLength },
                    Unfocused = a.Beam.Unfocused with { LengthBlocks = t.UnfocusedBeamLength },
                }
                : a.Beam,
        };
    }

    public static BeaconTier TierFromVariantCode(string? variantCode)
    {
        for (int i = 0; i < ByTier.Length; i++)
        {
            if (ByTier[i].VariantCode == variantCode) return (BeaconTier)i;
        }
        return BeaconTier.Fire;
    }

    // Tuned so firewood (24s burn duration) yields the same 0.75h the Fire tier added per item before
    // fuel started varying by item (charcoal now runs longer than coal, etc).
    private const float FireFuelBurnHoursPerSecond = 0.03125f;

    public float SolidFuelHours(ItemStack? stack)
    {
        float burnSeconds = stack?.Collectible?.CombustibleProps?.BurnDuration ?? 0f;
        if (burnSeconds <= 0f) return 0f;
        return burnSeconds * FireFuelBurnHoursPerSecond * FuelBurnTimeMultiplier;
    }

    // Hours one of this item adds when handed straight to the beacon, or 0 if it isn't fuel here. A code
    // the config names wins outright, so an admin can price rendered fat differently from whatever burn
    // duration it happens to carry, and can feed a tier that otherwise refuses solids. Anything unlisted
    // falls back to the tier's own rule, which for the oil lamp is to take nothing.
    public float HeldItemFuelHours(ItemStack? stack)
    {
        AssetLocation? code = stack?.Collectible?.Code;
        if (code != null)
        {
            foreach (var (pattern, hours) in ConfiguredItemFuels)
            {
                if (WildcardUtil.Match(pattern, code)) return hours;
            }
        }
        return AcceptsHeldItemFuel ? SolidFuelHours(stack) : 0f;
    }

    // Codes reach here from a hand-edited config file, or from a packet built out of one, so they are
    // untrusted text. The AssetLocation constructor only splits on ':' and never throws, so testing Valid
    // is the only thing that actually rejects a malformed entry; without it a typo turns into an entry
    // that quietly matches nothing for the rest of the session.
    internal static bool TryParseCode(string? code, [NotNullWhen(true)] out AssetLocation? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(code)) return false;

        var loc = new AssetLocation(code.Trim());
        if (!loc.Valid) return false;

        parsed = loc;
        return true;
    }

    // Extra liquids to burn and liquids to refuse, both wildcard codes from the config file. Blocked wins
    // over everything, so an admin can always veto a match this makes on its own.
    private static AssetLocation[] extraOilCodes = [];
    private static AssetLocation[] blockedOilCodes = [];

    public static void ApplyOilCodes(AssetLocation[] extra, AssetLocation[] blocked)
    {
        extraOilCodes = extra;
        blockedOilCodes = blocked;
    }

    // Wildcard oil codes mapped to the burn hours a litre of that oil yields, overriding FuelHoursPerLitre
    // for the ones listed. From the config file, empty unless a server sets it.
    private static (AssetLocation code, float hoursPerLitre)[] oilHourOverrides = [];

    public static void ApplyOilHourOverrides((AssetLocation code, float hoursPerLitre)[] overrides)
    {
        oilHourOverrides = overrides;
    }

    // The hours a litre of this liquid adds. First matching override wins; an oil with no entry falls back
    // to the tier's flat FuelHoursPerLitre.
    public float FuelHoursPerLitreFor(ItemStack? content)
    {
        AssetLocation? code = content?.Collectible?.Code;
        if (code != null)
        {
            foreach (var (pattern, hours) in oilHourOverrides)
            {
                if (WildcardUtil.Match(pattern, code)) return hours;
            }
        }
        return FuelHoursPerLitre;
    }

    // Every liquid oil in circulation ends its code with "portion" and carries "oil" ahead of it, but the
    // qualifier sits on the front (vinteng:crudeoilportion, expandedfoods:foodoilportion-flax), so matching
    // a token prefix only ever caught vanilla's game:oilportion-flax. Matching "oilportion" anywhere in the
    // path takes all three, and still leaves out boilingwaterportion, which hides an "oil" inside "boiling".
    public bool AcceptsFuel(ItemStack? stack)
    {
        // Fire drinks nothing, so it passes on every liquid and lets the container's own interaction run.
        // The config lists are scoped to the tiers that burn liquid too, or pouring oil at a fire would
        // start answering "tank full" at a beacon that has no tank.
        if (FuelPathKeywords.Length == 0) return false;

        AssetLocation? code = stack?.Collectible?.Code;
        if (code == null) return false;

        foreach (var blocked in blockedOilCodes)
        {
            if (WildcardUtil.Match(blocked, code)) return false;
        }

        // Anything that can't sit in a bucket is out. This mostly guards the config lists, where a broad
        // wildcard would otherwise let solid items through.
        if (BlockLiquidContainerBase.GetContainableProps(stack)?.Containable != true) return false;

        foreach (var keyword in FuelPathKeywords)
        {
            if (code.Path.Contains(keyword, StringComparison.Ordinal)) return true;
        }
        foreach (var extra in extraOilCodes)
        {
            if (WildcardUtil.Match(extra, code)) return true;
        }
        return false;
    }

    // Diminishing returns instead of linear, so stacking plates reaches a natural point of "enough".
    public float PlateRangeBonus(PlateMetal metal, int plateCount)
    {
        if (plateCount <= 0) return 0f;
        return PlateRangeBonusPerPlateByMetal[(int)metal] * MathF.Sqrt(plateCount);
    }
}
