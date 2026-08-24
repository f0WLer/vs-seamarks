using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace Seamarks.Beacon.Client;

// Builds the housing's look from its installed panels: each panel's lens shape on the mount its position
// in the list maps to, and a blank cover on every mount without one.
//
// The housing shape carries six panel mount points, of which Panel_NW and Panel_NE already have covers
// modelled into them. The four here are the ones that change. Their spacing is what makes the
// configurations come out evenly spread: N alone, N and S opposite each other, then N/SW/SE at thirds.
// So the panel at list index i lands on the i-th mount for the current count, which is what makes the
// second panel jump from S to SW when a third gets added.
public static class OpticsHousingMesh
{
    private const string HousingShape = "seamarks:shapes/block/lighthouse/opticshousing.json";
    private const string CoverPanel = "seamarks:shapes/block/lighthouse/coverpanel.json";

    private static readonly string[] ConfigurableNodes = ["Panel_N", "Panel_S", "Panel_SE", "Panel_SW"];

    // The mount the first panel lands on, left bare while the housing is empty.
    private const string FirstMount = "Panel_N";

    // Which mounts hold a lens at each panel count, kept evenly spread around the housing. The 3-panel
    // order has to be SW before SE: the beam renderer sweeps its evenly-spaced headings in the opposite
    // rotational sense from what the mount names suggest, so pairing them in "compass" order (N, SE, SW)
    // put each beam's light coming out of the panel a third of a turn from where its color actually is.
    private static string[] NodesFor(int count) => count switch
    {
        <= 0 => [],
        1 => ["Panel_N"],
        2 => ["Panel_N", "Panel_S"],
        _ => ["Panel_N", "Panel_SW", "Panel_SE"],
    };

    private static string LensShape(int type) => $"seamarks:shapes/item/glass/lenspanel_{Math.Clamp(type, BeamSignature.MinPanelType, BeamSignature.MaxPanelType)}.json";

    // One mesh per distinct panel sequence, not per block, since composing and tesselating several shapes
    // is far too much to redo for every housing in view. BeamSignature.PanelKey folds in both type and
    // color, so two housings with the same widths but different glass are different cache entries.
    public static MeshData? Get(ICoreClientAPI capi, BeamSignature signature)
    {
        string key = "seamarks:opticshousing-mesh-" + signature.PanelKey();
        return ObjectCacheUtil.GetOrCreate(capi, key, () => Build(capi, signature));
    }

    private static MeshData? Build(ICoreClientAPI capi, BeamSignature signature)
    {
        IReadOnlyList<int> panelTypes = signature.PanelTypes;

        // Textures resolve off the housing's own blocktype wherever the mesh is drawn, so the beacon
        // doesn't need to redeclare the panel keys just to show a fitted one. Each panel's own glass color
        // is layered on top of that per mount, since the blocktype has no way to carry more than one.
        Block? block = capi.World.GetBlock(new AssetLocation(SeamarksModSystem.Domain, "opticshousing"));
        if (block == null)
        {
            capi.Logger.Warning("[seamarks] No opticshousing blocktype, so its panels cannot be textured.");
            return null;
        }

        Shape? housing = Shape.TryGet(capi, HousingShape)?.Clone();
        if (housing == null)
        {
            capi.Logger.Warning("[seamarks] No usable housing shape at {0}, so the Optics Housing will not draw.", HousingShape);
            return null;
        }

        string[] lensNodes = NodesFor(panelTypes.Count);
        var glassByCode = new Dictionary<string, AssetLocation>();

        foreach (string node in ConfigurableNodes)
        {
            // An empty housing leaves Panel_N open, so it reads as the slot the first panel goes into.
            if (panelTypes.Count == 0 && node == FirstMount) continue;

            int mountIndex = Array.IndexOf(lensNodes, node);
            bool glazed = mountIndex >= 0;
            string shape = glazed ? LensShape(panelTypes[mountIndex]) : CoverPanel;

            // Each mount gets its own texture code, since a single "glass" key can only resolve to one atlas
            // position for the whole tesselation call, and different mounts can carry different colors.
            string? glassCode = null;
            if (glazed)
            {
                glassCode = "lensglass_" + node;
                glassByCode[glassCode] = BlockOpticsHousing.LensGlassTexture(signature.PanelColors[mountIndex]);
            }
            Graft(capi, housing, shape, node, glassCode);
        }

        ITexPositionSource texSource = capi.Tesselator.GetTextureSource(block);
        if (glassByCode.Count > 0) texSource = new PanelGlassTextureSource(capi, texSource, glassByCode);

        try
        {
            capi.Tesselator.TesselateShape("seamarks:opticshousing", housing, out MeshData mesh, texSource);
            return mesh;
        }
        catch (Exception e)
        {
            capi.Logger.Warning("[seamarks] Could not tesselate the Optics Housing, drawing nothing. {0}", e);
            return null;
        }
    }

    // Both shapes get cloned before grafting. StepParentShape rewires the child's parent and appends it to
    // the node's children, so handing it the cached asset would leave panels permanently stuck to the
    // shared shape and pile more on with every rebuild. glassCode is null for a plain cover panel; for a
    // lens it's the per-mount texture key its faces get retargeted onto, so its color resolves independent
    // of whatever's glazing every other mount.
    private static void Graft(ICoreClientAPI capi, Shape housing, string panelShape, string node, string? glassCode)
    {
        Shape? panel = Shape.TryGet(capi, panelShape)?.Clone();
        if (panel == null)
        {
            capi.Logger.Warning("[seamarks] No usable panel shape at {0}, leaving {1} empty.", panelShape, node);
            return;
        }

        // Set in code, not in the panel's JSON, so one panel file can serve whichever node needs it, and so
        // a Model Creator save can't drop it.
        foreach (ShapeElement root in panel.Elements)
        {
            root.StepParentName = node;
            SeatOnMount(root);
            if (glassCode != null)
            {
                SetRenderPass(root, EnumChunkRenderPass.Transparent);
                RetargetGlassTexture(root, glassCode);
            }
        }

        housing.StepParentShape(panel, panelShape, HousingShape, capi.Logger, (_, _) => { });
    }

    // Every face in a lenspanel shape uses "glass"; swapping it for the mount's own code is what lets
    // PanelGlassTextureSource give each installed panel its own color instead of one shared atlas lookup.
    // Faces itself is only live during JSON deserialization; by the time an asset is cached and cloned,
    // TrimTextureNamesAndResolveFaces has already moved every face into FacesResolved and stripped the
    // leading '#', so that's the array to rewrite here.
    private static void RetargetGlassTexture(ShapeElement element, string newCode)
    {
        if (element.FacesResolved != null)
        {
            foreach (ShapeElementFace? face in element.FacesResolved)
            {
                if (face?.Texture == "glass") face.Texture = newCode;
            }
        }
        foreach (ShapeElement child in element.Children ?? []) RetargetGlassTexture(child, newCode);
    }

    // Resolves the per-mount lensglass_* codes Graft assigns to whatever glass texture that panel's color
    // calls for, and falls back to the housing block's own texture map (brass, cover glass) for everything
    // else, so the one substitution doesn't need its own full texture source.
    private sealed class PanelGlassTextureSource(ICoreClientAPI capi, ITexPositionSource fallback, IReadOnlyDictionary<string, AssetLocation> glassByCode) : ITexPositionSource
    {
        public Size2i? AtlasSize => fallback.AtlasSize;

        public TextureAtlasPosition? this[string textureCode]
        {
            get
            {
                if (!glassByCode.TryGetValue(textureCode, out AssetLocation? loc)) return fallback[textureCode];
                capi.BlockTextureAtlas.GetOrInsertTexture(loc, out _, out TextureAtlasPosition texPos);
                return texPos;
            }
        }
    }

    // Moves a panel's root to its mount's local origin, which is where every mount expects to find it. The
    // panels are modelled sitting on the block's centre line, and a mount adds that offset on top of its
    // own. On the two unrotated mounts that just shifts the panel, but the four turned ones rotate the
    // stray offset as well, which swings the panel off the frame and into the middle of the housing. The
    // children are positioned relative to the root, so moving the root alone carries the whole panel.
    private static void SeatOnMount(ShapeElement root)
    {
        if (root.To != null && root.From != null)
        {
            for (int i = 0; i < root.To.Length && i < root.From.Length; i++) root.To[i] -= root.From[i];
        }
        root.From = [0, 0, 0];
    }

    // Marks the glass so it lands in the transparent pass and not with the brass. The chunk builder reads
    // this per face, which is what lets one mesh hold both materials.
    private static void SetRenderPass(ShapeElement element, EnumChunkRenderPass pass)
    {
        element.RenderPass = (short)pass;
        foreach (ShapeElement child in element.Children ?? []) SetRenderPass(child, pass);
    }

    // Splits a composed mesh into the part that belongs in the opaque pass and the part that needs
    // blending. The placed block never needs this (the chunk builder sorts faces itself), but the optic
    // renderer draws its own mesh and has to hand each half to a different render stage.
    public static (MeshData Opaque, MeshData? Transparent) SplitByPass(MeshData full)
    {
        if (!full.NeedsRenderPass(EnumChunkRenderPass.Transparent)) return (full, null);

        short want = (short)EnumChunkRenderPass.Transparent;
        MeshData opaque = full.EmptyClone();
        MeshData glass = full.EmptyClone();
        opaque.AddMeshData(full, i => full.RenderPassesAndExtraBits[i] != want);
        glass.AddMeshData(full, i => full.RenderPassesAndExtraBits[i] == want);
        return (opaque, glass);
    }
}
