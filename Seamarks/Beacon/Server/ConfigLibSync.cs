using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace Seamarks.Beacon.Server;

// Live reload of seamarksbeacons.json when the optional ConfigLib mod saves it from its in-game settings
// window. References no ConfigLib type: the library publishes a save signal on the game's own event bus
// under a domain-scoped name, so listening for that string needs no assembly reference and no build flag.
// Without ConfigLib installed the event never fires and this does nothing, which is the whole
// soft-dependency story. ConfigLib binds straight to our seamarksbeacons.json (assets/seamarks/config/
// configlib-patches.json points it there), so "reload" is just re-running the startup load path.
internal static class ConfigLibSync
{
    // ConfigLib formats its save event as "configlib:{domain}:config-saved", the domain being the one that
    // owns the configlib-patches.json asset. It fires once per actual save, after the file is written.
    private const string ConfigSavedEvent = "configlib:" + SeamarksModSystem.Domain + ":config-saved";

    internal static void Register(ICoreServerAPI sapi, SeamarksModSystem mod)
    {
        sapi.Event.RegisterEventBusListener(
            (string _, ref EnumHandling handling, IAttribute data) => OnConfigSaved(sapi, mod),
            filterByEventName: ConfigSavedEvent);
    }

    private static void OnConfigSaved(ICoreServerAPI sapi, SeamarksModSystem mod)
    {
        // On a dedicated server ConfigLib raises this before it has written the server's own copy of the
        // file, so reloading here would read the pre-save values. Those servers pick the change up on the
        // next restart. On an integrated server (singleplayer or a LAN host) the file is already current.
        if (sapi.Server.IsDedicated) return;

        // A save through ConfigLib's own GUI can't produce malformed JSON, but the file is just as editable
        // by hand between saves, so this goes through the same guard the startup load uses. Keeping the
        // previous values on a bad read beats tearing the live tuning down to defaults mid-session.
        if (!SeamarksModSystem.TryLoadConfig(sapi, out SeamarksConfig config)) return;

        config.ApplyToArchetypes();
        mod.Config = config;
        mod.Server?.BroadcastTuning();
    }
}
