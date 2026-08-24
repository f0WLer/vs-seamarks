using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace Seamarks.Mechanics.Client;

// Builds the flyball head at one point in its travel, from folded in at rest to fully flung out.
//
// The head is a four-bar linkage: the ball arm, the follower arm and the sliding collar swing about
// different pivots, and their shared joints move along arcs. Posing it by interpolating each element's
// angle on its own traces the straight chords of those arcs instead, so the arms drift apart mid-travel
// and only line back up at the two ends. The fix is to blend between authored poses and let the model
// carry the motion: the modeller exports the head at a few points along the travel and this mixes the
// nearest two. Rest and fully raised alone give a straight blend (the arc still cuts a corner); adding one
// mid-travel pose bends the blend back onto the linkage. The travel is quantised into steps and each
// step's mesh cached, since re-tesselating every frame as the speed drifts would be far too much.
public static class GovernorMesh
{
    // Poses along the travel, evenly spaced from idle to fully raised. Each was modelled at the correct
    // linked state for its point, so blending the nearest two follows the linkage's real arc. Any that
    // fail to load are skipped, and the blend just spans the ones that remain. Kept sorted by position.
    private static readonly (string Path, float At)[] KeyframeCandidates =
    {
        ("seamarks:shapes/block/governor/governor_idle.json", 0.0f),
        ("seamarks:shapes/block/governor/governor_m1.json", 0.2f),
        ("seamarks:shapes/block/governor/governor_m2.json", 0.4f),
        ("seamarks:shapes/block/governor/governor_m3.json", 0.6f),
        ("seamarks:shapes/block/governor/governor_m4.json", 0.8f),
        ("seamarks:shapes/block/governor/governor_m5.json", 1.0f),
    };

    // Distinct extension meshes spanning the whole travel. Enough that the motion reads as smooth, few
    // enough that the cache stays tiny.
    public const int ExtensionSteps = 24;

    // The top-level nodes that turn with the shaft. Everything else is the fixed gearbox housing, which
    // holds still even though the lever inside it still rides the extension. Each of these turns about the
    // upright line through its own rotation origin, so the tower and the axle spin where they stand
    // instead of swinging around the middle of the block.
    private static readonly string[] SpinNodes = ["WattGov", "VS_Axle"];

    // One drawable piece of the head. A null pivot means it never turns, so the renderer draws it as it
    // sits; otherwise it turns about the upright line through that point.
    public sealed record GovernorPart(MeshData Mesh, Vec3f? SpinPivot);

    public static GovernorPart[]? Get(ICoreClientAPI capi, int step)
    {
        step = GameMath.Clamp(step, 0, ExtensionSteps);
        return ObjectCacheUtil.GetOrCreate(capi, "seamarks:governor-parts-" + step, () => Build(capi, step));
    }

    // The uploaded form, cached per step the same way the mesh data above is. Every Governor in the world
    // draws from the same set of poses, and the eased extension walks the whole range in about a second on
    // a spin-up, so uploading per renderer per step meant a dispose-and-reupload cycle several times a
    // second for each block. These belong to the cache, so a renderer reads them and never disposes them.
    public static (MultiTextureMeshRef Mesh, Vec3f? SpinPivot)[]? GetUploaded(ICoreClientAPI capi, int step)
    {
        step = GameMath.Clamp(step, 0, ExtensionSteps);
        return ObjectCacheUtil.GetOrCreate(capi, "seamarks:governor-meshrefs-" + step, () =>
        {
            GovernorPart[]? built = Get(capi, step);
            // Per texture, not one buffer: the block atlas spans several pages once enough mods are
            // loaded, and binding a single one draws every face that landed elsewhere with the wrong
            // texture, or drops it.
            return built?.Select(p => (capi.Render.UploadMultiTextureMesh(p.Mesh), p.SpinPivot)).ToArray();
        });
    }

    private static GovernorPart[]? Build(ICoreClientAPI capi, int step)
    {
        Block? block = capi.World.GetBlock(new AssetLocation(SeamarksModSystem.Domain, "governor"));
        if (block == null) return null;

        List<(Shape Shape, float At)> keyframes = LoadKeyframes(capi);
        if (keyframes.Count == 0)
        {
            capi.Logger.Warning("[seamarks] No usable flyball poses, so the Governor will not draw.");
            return null;
        }

        Shape posed = BuildPose(keyframes, (float)step / ExtensionSteps);
        var parts = new List<GovernorPart>();

        // Each turning node is tesselated on its own so it can be handed its own matrix at draw time.
        foreach (string node in SpinNodes)
        {
            ShapeElement? element = posed.GetElementByName(node);
            if (element == null) continue;

            Shape single = posed.Clone();
            ShapeElement kept = single.GetElementByName(node);
            if (kept == null) continue;
            single.Elements = [kept];

            if (Tesselate(capi, block, single, out MeshData mesh)) parts.Add(new GovernorPart(mesh, PivotOf(kept)));
        }

        // Whatever is left is the housing, drawn once and never turned.
        Shape housing = posed.Clone();
        housing.Elements = [.. housing.Elements.Where(e => Array.IndexOf(SpinNodes, e.Name) < 0)];
        if (housing.Elements.Length > 0 && Tesselate(capi, block, housing, out MeshData housingMesh))
        {
            parts.Add(new GovernorPart(housingMesh, null));
        }

        return parts.Count > 0 ? [.. parts] : null;
    }

    // The upright line the node turns about, taken from its own rotation origin so a model edit that moves
    // the tower or the axle carries the spin axis with it. Height plays no part in a turn about upright.
    private static Vec3f PivotOf(ShapeElement element)
    {
        double[]? origin = element.RotationOrigin;
        return origin == null
            ? new Vec3f(0.5f, 0f, 0.5f)
            : new Vec3f((float)origin[0] / 16f, 0f, (float)origin[2] / 16f);
    }

    private static bool Tesselate(ICoreClientAPI capi, Block block, Shape shape, out MeshData mesh)
    {
        try
        {
            capi.Tesselator.TesselateShape("seamarks:governor", shape, out mesh,
                capi.Tesselator.GetTextureSource(block));
            return true;
        }
        catch (Exception e)
        {
            capi.Logger.Warning("[seamarks] Could not tesselate part of the Governor, leaving it out. {0}", e);
            mesh = null!;
            return false;
        }
    }

    private static List<(Shape Shape, float At)> LoadKeyframes(ICoreClientAPI capi)
    {
        var loaded = new List<(Shape, float)>();
        foreach ((string path, float at) in KeyframeCandidates)
        {
            Shape? shape = Shape.TryGet(capi, path);
            if (shape != null) loaded.Add((shape, at));
        }
        return loaded;
    }

    // Blends the two poses bracketing t into a fresh shape. The topology, faces and textures come from the
    // bracketing pose that owns them; only each element's placement (its rotations and its box) is moved,
    // so any element the modeller animated between the two poses follows along without this needing to know
    // which ones move or how they're linked.
    private static Shape BuildPose(List<(Shape Shape, float At)> keyframes, float t)
    {
        (Shape lower, float lowerAt) = keyframes[0];
        (Shape upper, float upperAt) = keyframes[^1];
        for (int i = 0; i < keyframes.Count - 1; i++)
        {
            if (t >= keyframes[i].At && t <= keyframes[i + 1].At)
            {
                (lower, lowerAt) = keyframes[i];
                (upper, upperAt) = keyframes[i + 1];
                break;
            }
        }

        float frac = upperAt > lowerAt ? GameMath.Clamp((t - lowerAt) / (upperAt - lowerAt), 0f, 1f) : 0f;

        Shape posed = lower.Clone();
        foreach (ShapeElement element in AllElements(posed.Elements))
        {
            ShapeElement a = lower.GetElementByName(element.Name);
            ShapeElement b = upper.GetElementByName(element.Name);
            if (a == null || b == null) continue;

            element.RotationX = Lerp(a.RotationX, b.RotationX, frac);
            element.RotationY = Lerp(a.RotationY, b.RotationY, frac);
            element.RotationZ = Lerp(a.RotationZ, b.RotationZ, frac);
            LerpInto(element.From, a.From, b.From, frac);
            LerpInto(element.To, a.To, b.To, frac);
            LerpInto(element.RotationOrigin, a.RotationOrigin, b.RotationOrigin, frac);
        }
        return posed;
    }

    private static IEnumerable<ShapeElement> AllElements(ShapeElement[]? elements)
    {
        if (elements == null) yield break;
        foreach (ShapeElement element in elements)
        {
            yield return element;
            foreach (ShapeElement child in AllElements(element.Children)) yield return child;
        }
    }

    private static void LerpInto(double[]? target, double[]? a, double[]? b, float frac)
    {
        if (target == null || a == null || b == null) return;
        for (int i = 0; i < target.Length && i < a.Length && i < b.Length; i++) target[i] = Lerp(a[i], b[i], frac);
    }

    private static double Lerp(double a, double b, float t) => a + (b - a) * t;
}
