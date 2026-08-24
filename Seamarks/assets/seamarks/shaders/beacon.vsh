#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;

uniform mat4 viewMatrix;
uniform mat4 projectionMatrix;

// Beacon position minus camera position, worked out in double precision CPU-side before it reaches this
// float uniform, which keeps GPU coordinates small even thousands of blocks from the origin.
uniform vec3 centerRelPos;
uniform float size;

out vec2 uv;
flat out float depthForTest;

// Cosmetic structure around a raised beacon (support beams, railings) sits only a few blocks in front of
// the light, and hardware depth testing has no notion of "close enough not to matter", so it silhouettes
// the blip against anything in the way. Small enough to clear a beacon's own structure without letting
// the blip show through blocks placed further out.
const float OcclusionBiasBlocks = 5.0;

// Above zero in third person, where the player's own avatar sits at roughly this distance from the camera
// and would otherwise silhouette the blip the way real terrain does. Pinning the tested depth just inside
// that distance beats the avatar while anything genuinely closer still occludes. Zero in first person,
// where the bias above is used instead.
uniform float thirdPersonPinDepth;

void main()
{
    uv = uvIn;

    // View-space billboard. viewMatrix is rotation-only here (centerRelPos is already camera-relative),
    // so the quad corner can be added straight into view-space XY to face the camera, with no separate
    // right/up uniforms.
    vec4 viewPos = viewMatrix * vec4(centerRelPos, 1.0);

    // Biases only the depth-test value (written to gl_FragDepth in the fragment shader), worked out from
    // the true centre before the quad corner offset below, and never touching gl_Position, so screen
    // position and size are unaffected. Clamped so a beacon closer than the bias can't wrap past the
    // near plane.
    float biasedEyeZ = thirdPersonPinDepth > 0.0
        ? -thirdPersonPinDepth
        : min(viewPos.z + OcclusionBiasBlocks, -0.05);
    vec4 biasedClip = projectionMatrix * vec4(0.0, 0.0, biasedEyeZ, 1.0);
    depthForTest = biasedClip.z / biasedClip.w;

    viewPos.xy += vertexPositionIn.xy * size;
    gl_Position = projectionMatrix * viewPos;
}
