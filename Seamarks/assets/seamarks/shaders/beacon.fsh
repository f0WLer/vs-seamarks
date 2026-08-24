#version 330 core

in vec2 uv;
flat in float depthForTest;

// rgb = tier color tint, a = overall fade (0..1) from the range-edge falloff, worked out CPU-side from
// the same visibility formula the server uses to decide who gets told about a beacon.
uniform vec4 tintColor;
// Linear brightness multiplier on the emitted color. With the renderer's additive blend a value above 1
// pushes the core past white, which is how the bare-lamp close glare reads as blinding while the ordinary
// far blip stays at 1.
uniform float glowStrength;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;

void main()
{
    vec2 centered = uv * 2.0 - 1.0;
    float dist = length(centered);
    float falloff = smoothstep(1.0, 0.0, dist);

    float alpha = falloff * tintColor.a;
    if (alpha < 0.003) discard;

    outColor = vec4(tintColor.rgb * glowStrength, alpha);

    // Left at zero. Writing anything nonzero marks these pixels in
    // the engine's shared glow buffer, which the godray effect samples to draw light shafts, and that
    // gave every blip a set of streaks radiating out of it when the option was enabled. The blip's glow
    // already comes from the additive blending set up in BeaconBlipRenderer.
    outGlow = vec4(0.0);

    // depthForTest is the true depth pretending to sit closer to the camera, so only close-range
    // occluders (a beacon's own support structure) stop hiding it while distant terrain still occludes
    // normally. GLDepthMask(false) means this only affects the test and never writes back.
    gl_FragDepth = depthForTest * 0.5 + 0.5;
}
