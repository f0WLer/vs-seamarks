using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon.Client;

// Builds the beam's geometry: a hollow elliptical frustum shell pointing down +Z in local space (the
// renderer rotates it to the beacon's heading and moves it to the lamp). It's only a lateral surface,
// no end caps. Drawn additively with no depth write, the near and far walls of the shell stack up so
// the beam reads brightest through its middle and fades at the silhouette, approximating a light volume
// without being solid. UVs are (u = around the ring 0..1, v = along the length, 0 at the lamp to 1 at
// the far tip) so the shader can fade along the beam's length.
//
// One primitive, parameterised entirely from the beam shape, so every optic (narrow, fan, flattened
// Fresnel, nested hotspot) is a different set of numbers and not different geometry code.
public static class BeamMesh
{
    // A soft additive cone doesn't need a dense ring; this stays smooth at the sizes involved.
    private const int RadialSegments = 24;

    // nearRadius/farRadius are the shell half-widths at the lamp and far end; hScale/vScale squash the
    // circular cross-section into an ellipse (hScale > 1 = a horizontal fan, vScale < 1 = flattened
    // toward the horizon).
    public static MeshData BuildFrustum(float nearRadius, float farRadius, float length, float hScale, float vScale)
    {
        const int vertsPerRing = RadialSegments + 1; // +1 duplicates the seam vertex so u runs cleanly 0..1
        int vertexCount = 2 * vertsPerRing;
        int indexCount = RadialSegments * 6;

        // Position + UV only; color and intensity are shader uniforms, so no per-vertex rgba/flags.
        var mesh = new MeshData(vertexCount, indexCount, withNormals: false, withUv: true, withRgba: false, withFlags: false);

        for (int k = 0; k <= RadialSegments; k++)
        {
            float u = (float)k / RadialSegments;
            float angle = u * GameMath.TWOPI;
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);

            // Near ring (at the lamp) then far ring, interleaved so the index math below stays simple.
            mesh.AddVertex(nearRadius * cos * hScale, nearRadius * sin * vScale, 0f, u, 0f);
            mesh.AddVertex(farRadius * cos * hScale, farRadius * sin * vScale, length, u, 1f);
        }

        for (int k = 0; k < RadialSegments; k++)
        {
            int near0 = k * 2;
            int far0 = near0 + 1;
            int near1 = near0 + 2;
            int far1 = near0 + 3;

            mesh.AddIndex(near0); mesh.AddIndex(far0); mesh.AddIndex(far1);
            mesh.AddIndex(near0); mesh.AddIndex(far1); mesh.AddIndex(near1);
        }

        return mesh;
    }
}
