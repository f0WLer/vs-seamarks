using System.Text;
using Cairo;
using Seamarks.Beacon;
using Seamarks.Beacon.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Seamarks.Beacon.Map;

// A "Beacons" toggle on the world map that pins every currently-lit beacon on the server. The server owns
// the data, the same full registry the far-render sync is built on, so the client holds nothing until it
// opens the map; then the server streams the lit set and keeps refreshing it while the map stays open.
// That refresh is what catches a beacon burning out on its own, which fires no registry event, only a
// later IsBurning going false.
public class BeaconMapLayer : MapLayer
{
    public override string Title => Lang.Get("seamarks:maplayer-beacons");
    public override string LayerGroupCode => "seamarksbeacons";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Server;

    private readonly ICoreClientAPI? capi;
    private readonly List<BeaconMapComponent> components = [];
    private LoadedTexture? iconTexture;
    private MeshRef? quadModel;

    private readonly ICoreServerAPI? sapi;
    private readonly HashSet<string> openMapPlayers = [];
    private long refreshListenerId;

    // How often the server re-pushes the lit set to a player with the map open. Long enough to cost
    // nothing, short enough that a beacon lit or spent while you're looking at the map catches up on its
    // own.
    private const int RefreshIntervalMs = 1500;

    public BeaconMapLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
    {
        capi = api as ICoreClientAPI;
        sapi = api as ICoreServerAPI;
    }

    public override void OnLoaded()
    {
        if (sapi == null) return;
        refreshListenerId = sapi.Event.RegisterGameTickListener(OnServerRefresh, RefreshIntervalMs);
        sapi.Event.PlayerDisconnect += OnPlayerDisconnect;
    }

    private SeamarksModSystem? Mod => sapi?.ModLoader.GetModSystem<SeamarksModSystem>();
    private BeaconRegistry? Registry => Mod?.Server?.Registry;

    public override void OnMapOpenedServer(IServerPlayer fromPlayer)
    {
        openMapPlayers.Add(fromPlayer.PlayerUID);
        SendMarkers(fromPlayer);
    }

    public override void OnMapClosedServer(IServerPlayer fromPlayer) => openMapPlayers.Remove(fromPlayer.PlayerUID);

    private void OnPlayerDisconnect(IServerPlayer player) => openMapPlayers.Remove(player.PlayerUID);

    private void OnServerRefresh(float dt)
    {
        if (openMapPlayers.Count == 0) return;
        foreach (string uid in openMapPlayers)
        {
            if (sapi!.World.PlayerByUid(uid) is IServerPlayer player) SendMarkers(player);
        }
    }

    private void SendMarkers(IServerPlayer player)
    {
        BeaconRegistry? registry = Registry;
        if (registry == null) return;

        double nowHours = sapi!.World.Calendar.TotalHours;
        // An empty send when the layer is switched off, not a skipped one, so a live toggle clears whatever
        // blips a player already has up.
        BeaconInfo[] lit = (Mod?.Config?.ShowBeaconsOnMap ?? true)
            ? registry.All.Values.Where(info => info.IsBurning(nowHours)).ToArray()
            : [];

        // The full BeaconInfo goes over, not a trimmed marker, so the map can run the exact sweep-and-color
        // maths the far blip does and flash each beam in step with it.
        byte[] data = SerializerUtil.Serialize(new BeaconFullSyncPacket { Beacons = lit });
        mapSink.SendMapDataToClient(this, player, data);
    }

    public override void OnDataFromServer(byte[] data)
    {
        if (capi == null) return;
        EnsureIcon();

        DisposeComponents();
        BeaconFullSyncPacket payload = SerializerUtil.Deserialize<BeaconFullSyncPacket>(data);
        foreach (BeaconInfo info in payload.Beacons)
        {
            components.Add(new BeaconMapComponent(capi, info, iconTexture!, quadModel!));
        }
    }

    public override void OnMapOpenedClient() => EnsureIcon();

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active) return;
        foreach (BeaconMapComponent c in components) c.Render(mapElem, dt);
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
    {
        if (!Active) return;
        foreach (BeaconMapComponent c in components) c.OnMouseMove(args, mapElem, hoverText);
    }

    private void EnsureIcon()
    {
        if (capi == null || iconTexture != null) return;
        quadModel ??= capi.Render.UploadMesh(QuadMeshUtil.GetQuad());
        iconTexture = BuildIcon(capi);
    }

    // A small round beacon light, drawn once in white and tinted per tier at render time. The ring keeps
    // it legible when two markers overlap, and the translucent core lets the one behind still show.
    private static LoadedTexture BuildIcon(ICoreClientAPI capi)
    {
        int size = (int)GuiElement.scaled(24);
        var surface = new ImageSurface(Format.Argb32, size, size);
        var ctx = new Context(surface);
        ctx.SetSourceRGBA(0, 0, 0, 0);
        ctx.Paint();

        double c = size / 2.0;
        ctx.LineWidth = size * 0.11;
        ctx.SetSourceRGBA(1, 1, 1, 1);
        ctx.Arc(c, c, size * 0.30, 0, Math.PI * 2);
        ctx.StrokePreserve();
        ctx.SetSourceRGBA(1, 1, 1, 0.82);
        ctx.Fill();

        var tex = new LoadedTexture(capi, capi.Gui.LoadCairoTexture(surface, false), size, size);
        ctx.Dispose();
        surface.Dispose();
        return tex;
    }

    private void DisposeComponents()
    {
        foreach (BeaconMapComponent c in components) c.Dispose();
        components.Clear();
    }

    public override void Dispose()
    {
        DisposeComponents();
        iconTexture?.Dispose();
        iconTexture = null;
        quadModel?.Dispose();
        quadModel = null;
    }

    public override void OnShutDown()
    {
        if (sapi == null || refreshListenerId == 0) return;
        sapi.Event.UnregisterGameTickListener(refreshListenerId);
        sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
    }
}
