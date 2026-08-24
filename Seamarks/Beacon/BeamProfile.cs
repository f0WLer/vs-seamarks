namespace Seamarks.Beacon;

// How a beam moves. Static per archetype, since whether it's currently rotating lives on
// BeaconInfo.MechanicalActive. This only says what motion the optic is capable of.
public enum BeamPattern
{
    Fixed = 0,    // holds its placement heading
    Rotating = 1, // sweeps at a rate set by the driving shaft, scaled off RotationPeriodHours
    // Oscillating, dual-opposing and signature flashes would go here, once something actually consumes
    // them.
}

// The geometry and appearance of a beam cone, meaning everything about its look that doesn't depend on
// motion. A directional beacon carries two of these, one focused (lens fitted) and one unfocused (bare
// lamp, or a housing with no panel), picked between per beacon off BeaconInfo.HasFocusingLens. Values are
// world blocks or unitless scales, and they seed the live-editable copies in BeamTuning.
public readonly struct BeamShape
{
    public required float LengthBlocks { get; init; }       // how far the visible shaft reaches
    public required float NearRadiusBlocks { get; init; }   // half-width at the lamp (frustum near face)
    public required float FarRadiusBlocks { get; init; }    // half-width at the far end
    public required float HorizontalScale { get; init; }    // 1 = round; > 1 widens into a fan (reflector optic)
    public required float VerticalScale { get; init; }      // 1 = round; < 1 flattens toward the horizon (Fresnel optic)
    public required float RadialBands { get; init; }        // concentric shells approximating the radial gradient
    public required float CoreRadiusFraction { get; init; } // inner hotspot cone as a fraction of the outer, 0..1
    public required float RadialSoftness { get; init; }     // radial core->halo falloff exponent (higher = tighter core, longer skirt)
    public required float LengthFadePow { get; init; }      // longitudinal lamp->tip fade exponent

    // Additive brightness handed to the beam shader per shell. Above 1 is legal and blows the beam out
    // toward white. Core is the tight inner hotspot, halo the wide soft outer cone.
    public required float HaloIntensity { get; init; }
    public required float CoreIntensity { get; init; }

    // Stand-in for a non-directional tier, whose shapes never get used.
    public static readonly BeamShape Zero = new()
    {
        LengthBlocks = 0f, NearRadiusBlocks = 0f, FarRadiusBlocks = 0f,
        HorizontalScale = 1f, VerticalScale = 1f, RadialBands = 2f,
        CoreRadiusFraction = 0f, RadialSoftness = 1f, LengthFadePow = 1f,
        HaloIntensity = 0f, CoreIntensity = 0f,
    };
}

// Everything static about a beacon's beam: whether it's directional at all, how it moves, and its two
// optic shapes. One per archetype, and every renderer and emitter reads it, so a new lighthouse optic is
// new data here and not new renderer code.
public readonly struct BeamProfile
{
    // False means an omnidirectional point source like the Fire brazier: no cone, no mesh, visible from
    // every angle. The client reads this off the flat archetype, because a far beacon has no block entity
    // on the client to ask.
    public required bool Directional { get; init; }

    public required BeamPattern Pattern { get; init; }

    // One full revolution per this many calendar hours at BeamState.ReferenceShaftSpeed, or 0 to never
    // rotate. The actual period scales inversely with the driving shaft's speed, so this is the middle of
    // the range and not a fixed rate. Calendar hours and not real seconds, so every client works out the
    // same phase from the shared clock. Ignored unless Pattern is Rotating.
    public required float RotationPeriodHours { get; init; }

    // The two optic looks, picked per beacon off HasFocusingLens. Focused is the tight, bright, flattened
    // fan a Fresnel lens throws, and Unfocused the dimmer, rounder, shorter glow of the bare lamp (or a
    // housing with no panel fitted).
    public required BeamShape Focused { get; init; }
    public required BeamShape Unfocused { get; init; }

    // Shared by every omnidirectional tier: no cone, no shaft, nothing to render.
    public static readonly BeamProfile None = new()
    {
        Directional = false,
        Pattern = BeamPattern.Fixed,
        RotationPeriodHours = 0f,
        Focused = BeamShape.Zero,
        Unfocused = BeamShape.Zero,
    };
}
