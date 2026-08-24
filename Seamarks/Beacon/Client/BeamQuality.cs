using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon.Client;

// Graphics scalability. The beam's artistic target values live in BeamTuning, and this reads the player's
// engine graphics settings to scale the expensive cosmetic layers down from those targets on lower
// presets. The defaults change nothing, so a full-detail machine sees exactly what was calibrated and only
// someone who already turned their settings down gets less. Gameplay never moves: range, existence and
// burn state are server-authoritative and identical at every quality level.
//
// Static with an Init, mirroring BeamTuning: every renderer/emitter reads it without threading an instance
// through. Init wires the one watcher we need (band count is baked into the mesh, so a particle-level
// change has to trigger a rebuild); everything else is re-read live and needs no watcher. Being static
// means the state outlives a world, so Shutdown has to undo Init when the client tears down.
public static class BeamQuality
{
    private static ICoreClientAPI? capi;

    // Held so it can be handed back to RemoveWatcher. A lambda passed inline can't be, and the client
    // starts up again on every world join, so each join would otherwise stack another live watcher whose
    // captured api points at a world that has already gone.
    private static OnSettingsChanged<int>? particleLevelWatcher;

    public static void Init(ICoreClientAPI api)
    {
        Shutdown();
        capi = api;
        particleLevelWatcher = _ => BeamTuning.InvalidateGeometry();
        api.Settings.Int.AddWatcher("particleLevel", particleLevelWatcher);
    }

    public static void Shutdown()
    {
        if (capi != null && particleLevelWatcher != null)
        {
            capi.Settings.Int.RemoveWatcher("particleLevel", particleLevelWatcher);
        }
        particleLevelWatcher = null;
        capi = null;
    }

    // 0..1 cosmetic-detail fraction off the particle-level slider, the one continuous cosmetic dial the
    // engine exposes (0..100, default 100). Used as the general "how much optional beam detail" scalar.
    public static float DetailFactor => capi == null ? 1f
        : GameMath.Clamp(capi.Settings.Int.Get("particleLevel", 100) / 100f, 0f, 1f);

    // Effective nested-cone band count: scaled down from the tuned value on low presets, but never below 2
    // (a beam must still render) and never above what was tuned (full detail is the default ceiling).
    public static int RadialBands(int tunedBands) =>
        GameMath.Clamp((int)MathF.Round(tunedBands * DetailFactor), 2, tunedBands);

    // Dust is the cheapest layer to shed: off entirely at particleLevel 0, else its per-tick count scales
    // with detail (on top of the engine's own ParticleLevel pool throttling, which we already respect).
    public static bool DustEnabled => capi != null && capi.Settings.Int.Get("particleLevel", 100) > 0;
    public static float DustCountFactor => DetailFactor;

    // The engine renders dynamic lights only up to this cap; at 0 our sweeping light can never show, so
    // skip even registering it. Re-read each tick by the point-light manager (no watcher needed).
    public static bool DynamicLightsEnabled => capi == null || capi.Settings.Int.Get("maxDynamicLights", 0) > 0;
}
