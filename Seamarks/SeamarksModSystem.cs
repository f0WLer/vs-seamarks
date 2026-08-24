using Seamarks.Beacon;
using Seamarks.Beacon.Client;
using Seamarks.Beacon.Map;
using Seamarks.Beacon.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Seamarks;

public class SeamarksModSystem : ModSystem
{
    public const string Domain = "seamarks";
    public const string ChannelName = "seamarks";

    // The creative tab code; its display title ("Beacons") lives in the lang file under the vanilla
    // tabname-<code> convention, written there as "game:tabname-seamarks" since the engine's own
    // creative-tab title lookup has no domain awareness and only ever checks the "game" namespace. The
    // same trick Glassmaking uses for its own tab title.
    private const string BeaconsTabCode = "seamarks";

    public BeaconServer? Server { get; private set; }
    public BeaconClient? Client { get; private set; }

    // The loaded server config, kept live so readers like the map layer see edits from ConfigLib without a
    // restart. Server-side only; null on the client, which never loads the file.
    public SeamarksConfig? Config { get; internal set; }

    public override void Start(ICoreAPI api)
    {
        api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<BeaconInfo>()
            .RegisterMessageType<BeaconRemovePacket>()
            .RegisterMessageType<BeaconFullSyncPacket>()
            .RegisterMessageType<BeaconTuningPacket>()
            .RegisterMessageType<BeaconSweepSyncPacket>()
            .RegisterMessageType<BeaconGazeStatePacket>();

        api.RegisterBlockClass("BlockBeacon", typeof(BlockBeacon));
        api.RegisterBlockEntityClass("BlockEntityBeacon", typeof(BlockEntityBeacon));
        api.RegisterBlockClass("BlockOpticsHousing", typeof(BlockOpticsHousing));
        api.RegisterItemClass("ItemLensPanel", typeof(ItemLensPanel));
        api.RegisterBlockEntityClass("BlockEntityOpticsHousing", typeof(BlockEntityOpticsHousing));

        api.RegisterBlockClass("BlockGovernor", typeof(Mechanics.BlockGovernor));
        api.RegisterBlockEntityClass("BlockEntityGovernor", typeof(Mechanics.BlockEntityGovernor));
        // One mechanical-power node that drives the shaft above and reads the one below without joining it,
        // so the Governor's two ends can turn at different speeds.
        api.RegisterBlockEntityBehaviorClass("Governor", typeof(Mechanics.BEBehaviorGovernor));

        // Adds a "Beacons" toggle to the world map, pinning every lit beacon. Registered on both sides,
        // the map manager builds it where each one needs it. A null manager would mean the map mod is
        // absent, which the base game never ships without, so this stays optional-safe anyway.
        api.ModLoader.GetModSystem<WorldMapManager>()?.RegisterMapLayer<BeaconMapLayer>("seamarksbeacons", 1.25);

        // Capability behaviors, attached per tier via entityBehaviorsByType in beacon.json.
        api.RegisterBlockEntityBehaviorClass("BeaconReflector", typeof(BEBehaviorBeaconReflector));
        api.RegisterBlockEntityBehaviorClass("BeaconBeam", typeof(BEBehaviorBeaconBeam));
        api.RegisterBlockEntityBehaviorClass("BeaconRainDouse", typeof(BEBehaviorBeaconRainDouse));
        api.RegisterBlockEntityBehaviorClass("BeaconSound", typeof(BEBehaviorBeaconSound));
    }

    // The Glassmaking casting/annealing/finishing chain (cullet, the two lens blanks, the mold) has no
    // other way to reach a player: their only pathway is those attributes, which Glassmaking alone reads.
    // Without it they'd otherwise just dangle in creative as dead ends, so pull them from every creative
    // tab instead. The finished lenspanel itself is untouched, it keeps whatever other recipe reaches it.
    public override void AssetsFinalize(ICoreAPI api)
    {
        if (api.ModLoader.IsModEnabled("glassmaking"))
        {
            // The grid recipes in recipes/grid/lenspanel.json exist only as a fallback route to a
            // finished panel when Glassmaking isn't installed. With it installed, the cast/anneal/finish
            // chain is the intended path, so drop the shortcut instead of letting both coexist.
            api.World.GridRecipes.RemoveAll(r => r.Output?.Code?.Domain == Domain
                && r.Output.Code.FirstCodePart() == "lenspanel");
        }
        else
        {
            HideFromCreative(api.World.Items, "opticalcullet");
            HideFromCreative(api.World.Items, "lensblank-raw");
            HideFromCreative(api.World.Items, "lensblank-annealed");
            HideFromCreative(api.World.Blocks, "lenspanelmold");
        }

        // A one-stop "Beacons" tab covering everything this mod actually loaded this session, mirroring
        // how Glassmaking and Vintage Engineering ship their own mod-wide tab. Runs after the hide pass
        // above (not before), so an item Glassmaking-compat pulled from every tab stays out of this one
        // too instead of becoming the sole place it's still reachable.
        AddToBeaconsTab(api.World.Items);
        AddToBeaconsTab(api.World.Blocks);
    }

    // FirstCodePart() only splits at the first hyphen, so it can't tell "lensblank-raw-clearoptical" from
    // "lensblank-something-else"; matching on the full "bareCode-" prefix instead handles multi-word bare
    // codes like lensblank-raw/lensblank-annealed as well as single-word ones.
    private static void HideFromCreative(IEnumerable<CollectibleObject> collectibles, string bareCode)
    {
        foreach (var c in collectibles)
        {
            if (c.Code?.Domain == Domain && c.Code.Path.StartsWith(bareCode + "-")) c.CreativeInventoryTabs = [];
        }
    }

    // Appends the tab to whatever tabs a collectible already carries, so an item still shows up under
    // "general"/"tools"/etc too, not just this one. Skips anything with no tabs at all (either it never
    // had one, or HideFromCreative just cleared it), which is what keeps a Glassmaking-only item off this
    // tab as well when that mod isn't installed, and keeps a beacon's "lit" variant, left out of
    // creativeinventory in beacon.json on purpose, from turning up here either.
    private static void AddToBeaconsTab(IEnumerable<CollectibleObject> collectibles)
    {
        foreach (var c in collectibles)
        {
            if (c.Code?.Domain != Domain) continue;
            if (c.CreativeInventoryTabs is not { Length: > 0 } tabs) continue;
            if (Array.IndexOf(tabs, BeaconsTabCode) >= 0) continue;
            c.CreativeInventoryTabs = [.. tabs, BeaconsTabCode];
        }
    }

    // The config file is the one input here nobody validates, and a stray comma in it used to throw
    // straight out of StartServerSide. That left the mod half-loaded: the blocks and block entities were
    // registered from Start, but the mod system itself never reached the mod loader, so every beacon that
    // loaded afterwards threw a null reference looking for it. Falling back to defaults keeps a typo to one
    // log line and a beacon that reads as untuned. False means the file exists but couldn't be parsed; a
    // file that isn't there is readable, since writing the defaults back out is how it gets created.
    internal static bool TryLoadConfig(ICoreServerAPI api, out SeamarksConfig config)
    {
        try
        {
            config = api.LoadModConfig<SeamarksConfig>(SeamarksConfig.FileName) ?? new SeamarksConfig();
            return true;
        }
        catch (Exception e)
        {
            config = new SeamarksConfig();
            api.Logger.Error("[seamarks] Could not read {0}, so this session runs on the built-in defaults "
                + "and every setting in that file is ignored. The file has been left alone; fix it and "
                + "restart. Details: {1}", SeamarksConfig.FileName, e.Message);
            return false;
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        // Server is the sole reader of this file. Clients get the effective values over the network
        // instead of loading their own copy, so the two sides can never disagree about range/falloff
        // even on separate installs.
        bool readable = TryLoadConfig(api, out SeamarksConfig config);
        config.ApplyToArchetypes();
        Config = config;

        // Rewritten only when the file was readable, which is also what adds any keys a new version
        // introduced. Storing defaults over a file that failed to parse would throw away the very settings
        // whoever wrote the typo is about to come back and fix.
        if (readable) api.StoreModConfig(config, SeamarksConfig.FileName);

        Server = new BeaconServer(api);

        // Picks up in-game config edits if the optional ConfigLib mod is installed; a no-op without it.
        ConfigLibSync.Register(api, this);

        // The eye-shield gaze effect is decided entirely client-side. The server's only job is to replay
        // the animation on the gazing player's server entity, so the pose replicates out to every other
        // client that can see them. Follows what SunGaze does server-side.
        api.Network.GetChannel(ChannelName).SetMessageHandler<BeaconGazeStatePacket>(
            (fromPlayer, packet) => BeaconGaze.SetGazing(fromPlayer?.Entity, packet.Gazing));

        // Pins one beacon alight for good, for a server that keeps normal beacons burning down but wants a
        // single central lighthouse that never goes out. No argument toggles it; "on"/"off" set it. The
        // flag rides the block, so it holds through extinguishing, relighting and a reload. Operator-gated:
        // on a server only staff can set it, while a single-player host holds every privilege anyway.
        api.ChatCommands.Create("seamarkseternal")
            .WithDescription("Make the beacon you're looking at burn forever, taking no fuel and never going out. No argument toggles it; 'on' or 'off' set it outright.")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(api.ChatCommands.Parsers.OptionalBool("state"))
            .HandleWith(args =>
            {
                var blockSel = args.Caller.Player?.CurrentBlockSelection;
                if (blockSel == null) return TextCommandResult.Error(Lang.Get("seamarks:cmd-error-notlookingatblock"));
                if (api.World.BlockAccessor.GetBlockEntity(blockSel.Position) is not BlockEntityBeacon beacon)
                {
                    return TextCommandResult.Error(Lang.Get("seamarks:cmd-error-notlookingatbeacon"));
                }

                bool enable = args[0] as bool? ?? !beacon.IsEverburning;
                beacon.SetEverburning(enable);
                return TextCommandResult.Success(Lang.Get(enable ? "seamarks:cmd-eternal-on" : "seamarks:cmd-eternal-off"));
            });

        // Beam count and width now come from the Lens Panels fitted into the housing, not commands. Color
        // is still a command, editing the housing the caller is looking at, placed or already fitted.
        // Server-side, so the change persists and reaches everyone, the far blip-only observers included.
        api.ChatCommands.Create("seamarksbeamcolor")
            .WithDescription($"Set the color of one beam of the Optics Housing you're looking at: a name (red, orange, cyan...), a hex code like ff8800, or 'reset' for the tier default. Beam 0 sets every beam at once; 1-{BeamSignature.MaxBeamCount} sets just that beam.")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.chat)
            .WithArgs(
                api.ChatCommands.Parsers.IntRange("beam", 0, BeamSignature.MaxBeamCount),
                api.ChatCommands.Parsers.Word("color"))
            .HandleWith(args =>
            {
                var target = LookedAtSignature(api, args, out var error);
                if (target == null) return error!;
                if (!BeamColor.TryParse((string)args[1], out int packed))
                    return TextCommandResult.Error($"Unknown color '{(string)args[1]}'. Use a name (red, orange, cyan...), a hex code like ff8800, or 'reset'.");
                int which = (int)args[0];
                if (which == 0)
                {
                    int all = target.Signature.SetAllColors(packed);
                    target.Publish();
                    return TextCommandResult.Success($"All beams set to {BeamColor.Describe(all)}.");
                }
                int applied = target.Signature.SetColor(which, packed);
                target.Publish();
                return TextCommandResult.Success($"Beam {which} color set to {BeamColor.Describe(applied)}.");
            });
    }

    // The signature the caller is pointing at, along with the callback that republishes it once edited.
    // A housing is editable both sitting on the ground and fitted to a beacon, so a player never has to
    // pull one off just to retune it.
    private sealed record SignatureTarget(BeamSignature Signature, Action Publish);

    private static SignatureTarget? LookedAtSignature(ICoreServerAPI api, TextCommandCallingArgs args, out TextCommandResult? error)
    {
        var blockSel = args.Caller.Player?.CurrentBlockSelection;
        if (blockSel == null)
        {
            error = TextCommandResult.Error(Lang.Get("seamarks:cmd-error-notlookingatblock"));
            return null;
        }

        var be = api.World.BlockAccessor.GetBlockEntity(blockSel.Position);
        error = null;

        if (be is BlockEntityOpticsHousing placed)
        {
            return new SignatureTarget(placed.Signature, placed.SignatureChanged);
        }
        if (be is BlockEntityBeacon beacon
            && beacon.GetBehavior<BEBehaviorBeaconReflector>() is { Housing: { } fitted } reflector)
        {
            return new SignatureTarget(fitted, reflector.HousingChanged);
        }

        error = TextCommandResult.Error(Lang.Get("seamarks:cmd-error-nohousing"));
        return null;
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        Client = new BeaconClient(this, api);

        Seamarks.Glassmaking.WorkbenchTooltipFix.Register(api);
    }

    public override void Dispose()
    {
        Server?.Dispose();
        Client?.Dispose();

        Seamarks.Glassmaking.WorkbenchTooltipFix.Unregister();
    }
}
