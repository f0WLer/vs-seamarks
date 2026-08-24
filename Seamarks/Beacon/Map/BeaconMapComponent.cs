using System.Text;
using Seamarks.Beacon;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Seamarks.Beacon.Map;

// One lit beacon on the map. A rotating optic replays its flash here the same way the far blip does out at
// sea: the sweep crosses a fixed reference bearing, so each beam lights the marker in its own color for a
// span set by that beam's width, easing down to a dim grey in the gaps between. A steady beacon (Fire, or
// an oil lamp with no drive) just holds its color. It shares the layer's icon and quad, holding no GL
// resources of its own.
public class BeaconMapComponent : MapComponent
{
    private readonly BeaconInfo info;
    private readonly Vec3d worldPos;
    private readonly Vec3d refObserver; // the fixed bearing the sweep is read against
    private readonly LoadedTexture texture;
    private readonly MeshRef quadModel;
    private readonly Vec4f color = new();
    private readonly Matrixf mvMat = new();
    private readonly Matrixf shadowMat = new(); // the dark copy nudged behind the icon, reused every frame
    private Vec2f viewPos = new();
    private bool mouseOver;

    // Kept dimly visible between flashes so a beacon's spot never blanks out mid-gap.
    private const float GreyLevel = 0.28f;

    // The dark copy nudged behind the icon is always this flat color, so it's shared instead of
    // reallocated every frame.
    private static readonly Vec4f ShadowColor = new(0f, 0f, 0f, 0.6f);

    public BeaconMapComponent(ICoreClientAPI capi, BeaconInfo info, LoadedTexture texture, MeshRef quadModel) : base(capi)
    {
        this.info = info;
        this.texture = texture;
        this.quadModel = quadModel;
        worldPos = new Vec3d(info.Pos.X + 0.5, info.Pos.Y, info.Pos.Z + 0.5);
        refObserver = new Vec3d(info.Pos.X, info.Pos.Y, info.Pos.Z + 1); // due north of the beacon
    }

    public override void Render(GuiElementMap map, float dt)
    {
        map.TranslateWorldPosToViewPos(worldPos, ref viewPos);
        if (viewPos.X < -10f || viewPos.Y < -10f
            || viewPos.X > map.Bounds.OuterWidth + 10.0 || viewPos.Y > map.Bounds.OuterHeight + 10.0) return;

        float x = (float)(map.Bounds.renderX + viewPos.X);
        float y = (float)(map.Bounds.renderY + viewPos.Y);
        ICoreClientAPI api = map.Api;

        ResolveColor(BeamState.SweepNowHours(api.World));

        IShaderProgram prog = api.Render.GetEngineShader(EnumShaderProgram.Gui);
        prog.Uniform("extraGlow", 0);
        prog.Uniform("applyColor", 0);
        prog.Uniform("noTexture", 0f);
        prog.BindTexture2D("tex2d", texture.TextureId, 0);
        prog.UniformMatrix("projectionMatrix", api.Render.CurrentProjectionMatrix);

        float scale = (texture.Width + (mouseOver ? 4f : 0f)) * 0.5f;
        mvMat.Set(api.Render.CurrentModelviewMatrix).Translate(x, y, 60f).Scale(scale, scale, 0f);

        // A dark copy nudged behind the icon, so a pale light still reads against water and snow.
        prog.Uniform("rgbaIn", ShadowColor);
        shadowMat.Set(mvMat.Values).Scale(1.15f, 1.15f, 1f);
        prog.UniformMatrix("modelViewMatrix", shadowMat.Values);
        api.Render.RenderMesh(quadModel);

        prog.Uniform("rgbaIn", color);
        prog.UniformMatrix("modelViewMatrix", mvMat.Values);
        api.Render.RenderMesh(quadModel);
    }

    // The marker color for right now: the flashing beam color on a rotating optic, or the steady color
    // otherwise. Uses the same sweep-and-cone maths the far blip runs, so the two agree on which beam is
    // lit and when.
    private void ResolveColor(double nowHours)
    {
        ref readonly BeaconArchetypeData archetype = ref BeaconArchetypeData.Of(info.Tier);

        int beam = 0;
        float lit = 1f;
        if (BeamState.IsSweeping(info))
        {
            float heading = BeamState.CurrentHeadingRad(info, nowHours);
            lit = BeaconVisibility.DirectionalBeamFactor(info, refObserver, heading, out beam);
        }

        BeamColor.Resolve(info, beam, in archetype, out float r, out float g, out float b);
        // Lerp from neutral grey to the beam color, so a gap reads as off and not as a dim tint of it.
        color.Set(GreyLevel + (r - GreyLevel) * lit, GreyLevel + (g - GreyLevel) * lit, GreyLevel + (b - GreyLevel) * lit, 1f);
    }

    public override void OnMouseMove(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
    {
        mapElem.TranslateWorldPosToViewPos(worldPos, ref viewPos);
        double x = viewPos.X + mapElem.Bounds.renderX;
        double y = viewPos.Y + mapElem.Bounds.renderY;
        float size = RuntimeEnv.GUIScale * 8f;
        mouseOver = Math.Abs(args.X - x) < size && Math.Abs(args.Y - y) < size;
        if (!mouseOver) return;

        string tier = Lang.Get("seamarks:maplayer-beacon-tier-" + info.Tier.ToString().ToLowerInvariant());
        hoverText.AppendLine(Lang.Get("seamarks:maplayer-beacon-hover", tier, info.Pos.X, info.Pos.Y, info.Pos.Z));
    }
}
