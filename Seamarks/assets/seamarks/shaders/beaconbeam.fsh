#version 330 core

// u = around the shell ring (0..1), v = along the beam (0 at the lamp, 1 at the far tip).
in vec2 uv;

// rgb = beam tint, a = brightness. Drawn additively (framebuffer += rgb * a), so alpha is brightness and
// not opacity, and a value above 1 is legal and blows the beam out toward white. The renderer
// feeds a higher alpha to the inner hotspot shell than to the outer halo, and the additive stacking of
// each shell's near and far walls already brightens the beam through its middle with no per-fragment
// radial term.
uniform vec4 beamColor;

// Live environment, fed each frame from the calendar and the ambient fog density. The response curves
// live here, not CPU-side, so they stay hot-reloadable while calibrating.
uniform float dayLight;   // ~0 at night, rising above 1 near noon
uniform float fogDensity;
// Response curve, driven live by the beam tuning panel.
uniform float fogScale;      // how strongly fog reveals the beam
uniform float dayFadeLow;    // daylight at which the beam starts fading out
uniform float dayFadeHigh;   // daylight at which it's fully gone
uniform float lengthFadePow; // exponent shaping the lamp-to-tip fade
uniform float occlusionSoftness; // feather (as a fraction of beam length) on the wall cut

// Fraction along the beam (0..1) where a raycast from the lamp hit a solid block, so the beam stops at
// a wall instead of shining through it. 1 means unobstructed. This is measured from the lamp, unlike
// the camera-relative depth test the draw already does.
uniform float clipFraction;

// Fraction along the beam (0..1) where the reflector's own glass sits, so the mesh's near stretch (still
// anchored at the lamp, same geometry otherwise) stays hidden and the light only reads as starting once
// it reaches the lens.
uniform float startFraction;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;

void main()
{
    // Brightest at the source, fading into the atmosphere toward the far tip.
    float lengthFade = pow(clamp(1.0 - uv.y, 0.0, 1.0), lengthFadePow);

    // Near-invisible under a bright clear sky, full strength at night. A real lighthouse beam only
    // reads against a dark or hazy sky.
    float nightFactor = 1.0 - smoothstep(dayFadeLow, dayFadeHigh, dayLight);
    // Fog is a scattering medium, so it reveals the beam even in daylight. Take whichever of the two
    // makes it more visible.
    float fogVisibility = clamp(fogDensity * fogScale, 0.0, 1.0);
    float environment = max(nightFactor, fogVisibility);

    // Cut the beam where the lamp's ray hit a wall, feathered over a short band before the cut so it
    // reads as the light petering out, not as a flat disc.
    float occlusion = clamp((clipFraction - uv.y) / max(occlusionSoftness, 0.001), 0.0, 1.0);

    // Mirrors occlusion at the near end: feathers in from invisible to full over the same softness band,
    // starting at the lens instead of the anchor back inside the lamp.
    float reveal = clamp((uv.y - startFraction) / max(occlusionSoftness, 0.001), 0.0, 1.0);

    float alpha = beamColor.a * lengthFade * environment * occlusion * reveal;
    if (alpha < 0.003) discard;

    outColor = vec4(beamColor.rgb, alpha);

    // Left at zero to match beacon.fsh. Anything nonzero here writes into the engine's shared glow
    // buffer, which drives the sun-locked godray streaks, and those make no sense for a night beam.
    outGlow = vec4(0.0);
}
