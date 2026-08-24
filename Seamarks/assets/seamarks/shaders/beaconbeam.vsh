#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;

// modelViewMatrix already folds in the beam's rotation to its current heading, the camera-relative
// translation to the lamp, and any per-optic scale. Built CPU-side in double precision so GPU coords
// stay small far from the world origin (the same reason the blip passes a camera-relative centre).
uniform mat4 modelViewMatrix;
uniform mat4 projectionMatrix;

// u = around the shell ring (0..1), v = along the beam (0 at the lamp, 1 at the far tip).
out vec2 uv;

void main()
{
    uv = uvIn;
    gl_Position = projectionMatrix * modelViewMatrix * vec4(vertexPositionIn, 1.0);
}
