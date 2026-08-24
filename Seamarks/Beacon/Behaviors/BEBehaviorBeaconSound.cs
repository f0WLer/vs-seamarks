using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Seamarks.Beacon;

// The beacon's audio. The ambient burn loop is a per-beacon client-side ILoadedSound synced to the
// lit state; the ignite/extinguish one-shots play server-side via PlaySoundAt so every nearby client
// hears them. Attached only to tiers that have sound, so it needs no tier check.
public class BEBehaviorBeaconSound : BEBehaviorBeacon, IBeaconBurnListener
{
    private static readonly AssetLocation LightSoundLoc = new(SeamarksModSystem.Domain, "sounds/oil-light");
    private static readonly AssetLocation ExtinguishSoundLoc = new(SeamarksModSystem.Domain, "sounds/oil-extinguish");
    private static readonly AssetLocation AmbientSoundLoc = new(SeamarksModSystem.Domain, "sounds/oil-burning");
    // One of these two plays on each roll, picked 50/50, so a long turning stretch doesn't wear the same
    // creak thin.
    private static readonly AssetLocation[] SqueakSoundLocs =
    [
        new(SeamarksModSystem.Domain, "sounds/metal_squeak"),
        new(SeamarksModSystem.Domain, "sounds/metal_crank"),
    ];
    private const float SoundRange = 24f;
    private const float AmbientFadeOutSeconds = 0.4f;
    private ILoadedSound? ambientSound; // null on the server and whenever unlit

    // How long until the next squeak while the optic is turning, ticked down once a second. Negative
    // means "not rolled yet", which both the initial state and every stretch of not-rotating reset to, so
    // a fresh spin never inherits a stale short wait left over from before it stopped.
    private const float SqueakMinIntervalSeconds = 10f;
    private const float SqueakMaxIntervalSeconds = 15f;
    private double squeakCountdownSeconds = -1;

    public BEBehaviorBeaconSound(BlockEntity blockentity) : base(blockentity) { }

    public override void Initialize(ICoreAPI api, JsonObject properties)
    {
        base.Initialize(api, properties);
        // Client-driven and unsynced, the same way the ambient loop is: each nearby client rolls its own
        // timing, which is fine for incidental mechanical creaking that nobody needs to hear in lockstep.
        if (api is ICoreClientAPI) Blockentity.RegisterGameTickListener(OnSqueakTick, 1000);
    }

    // The ambient loop tracks the lit state; the core re-notifies on every burn transition (ignite,
    // extinguish, burnout) and on client-side tree sync, so this stays in step both ways.
    public void OnBurnStateChanged(bool burning) => SyncAmbient();

    // One-shots are driven by real transitions, not by burn-state sync, so walking up to an
    // already-lit beacon doesn't replay the ignite sound. The core calls these explicitly.
    public void PlayIgnite() => PlayOneShot(LightSoundLoc);
    public void PlayExtinguish() => PlayOneShot(ExtinguishSoundLoc);

    private void OnSqueakTick(float dt)
    {
        if (!Beacon.IsRotating) { squeakCountdownSeconds = -1; return; }
        if (squeakCountdownSeconds < 0) squeakCountdownSeconds = NextSqueakDelaySeconds();

        squeakCountdownSeconds -= dt;
        if (squeakCountdownSeconds > 0) return;

        AssetLocation squeak = SqueakSoundLocs[Random.Shared.Next(SqueakSoundLocs.Length)];
        Api.World.PlaySoundAt(squeak, Pos, 0.0, null, true, SoundRange, 1f);
        squeakCountdownSeconds = NextSqueakDelaySeconds();
    }

    private static double NextSqueakDelaySeconds() =>
        SqueakMinIntervalSeconds + Random.Shared.NextDouble() * (SqueakMaxIntervalSeconds - SqueakMinIntervalSeconds);

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        StopAmbient(immediate: true);
    }

    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        StopAmbient(immediate: true);
    }

    private void SyncAmbient()
    {
        if (Api is not ICoreClientAPI capi) return;
        bool shouldPlay = Beacon.IsBurning;

        if (shouldPlay && ambientSound == null)
        {
            ambientSound = capi.World.LoadSound(new SoundParams
            {
                Location = AmbientSoundLoc,
                ShouldLoop = true,
                DisposeOnFinish = false, // we own its lifetime, since a looping sound never "finishes" on its own
                Position = new Vec3f(Pos.X + 0.5f, Pos.Y + 0.5f, Pos.Z + 0.5f),
                SoundType = EnumSoundType.Ambient,
                Range = SoundRange,
                Volume = 1f,
            });
            ambientSound?.Start();
        }
        else if (!shouldPlay && ambientSound != null)
        {
            StopAmbient(immediate: false);
        }
    }

    // Nulls the field up front so a re-light mid-fade just starts a fresh sound while the old one
    // finishes fading and disposes itself independently.
    private void StopAmbient(bool immediate)
    {
        var snd = ambientSound;
        ambientSound = null;
        if (snd == null) return;

        if (immediate)
        {
            snd.Stop();
            snd.Dispose();
        }
        else
        {
            snd.FadeOut(AmbientFadeOutSeconds, s => s.Dispose());
        }
    }

    private void PlayOneShot(AssetLocation sound)
    {
        if (Api?.Side != EnumAppSide.Server) return;
        Api.World.PlaySoundAt(sound, Pos, 0.0, null, false, SoundRange, 1f);
    }
}
