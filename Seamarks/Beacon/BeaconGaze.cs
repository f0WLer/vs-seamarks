using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace Seamarks.Beacon;

// Shared, side-agnostic helper for the "shield your eyes from the lantern glare" animation.
//
// This can't just reuse vanilla's "protecteyes" animation: EntityPlayer.protectEyesFromWind runs
// every client frame and stops that exact animation whenever there's no dust storm, silently
// killing ours the same frame. So instead we ship our own clip, "seamarks-lampglare" (+ "-fp"),
// added to the player's shape through a shape patch, not an entity-JSON patch, because entity config is
// server-only and would quietly no-op on the client, which is where the animation actually plays.
//
// The keyframes come from protecteyes but drop the head and torso rotation, keeping only the raised arm.
// With immersive first-person eye positioning the engine takes the third-person camera's pivot from the
// animated head bone, so a head tilt here feeds
// back into BeaconGazeWatcher's own camera-relative checks and oscillates, visible as the whole
// view twitching while gazing at a nearby lamp in third person. Don't re-add those keyframes
// without solving that feedback loop first.
public static class BeaconGaze
{
    public const string EyeShieldAnimationCode = "seamarks-lampglare";

    private static readonly AnimationMetaData GazeAnim = new AnimationMetaData
    {
        Code = EyeShieldAnimationCode,
        Animation = EyeShieldAnimationCode,
        BlendMode = EnumAnimationBlendMode.Add,
        EaseInSpeed = 1f,
        ElementWeight = new Dictionary<string, float> { { "UpperArmR", 40f }, { "LowerArmR", 40f } },
        ElementBlendMode = new Dictionary<string, EnumAnimationBlendMode>
        {
            { "UpperArmR", EnumAnimationBlendMode.AddAverage },
            { "LowerArmR", EnumAnimationBlendMode.AddAverage },
        },
        WithFpVariant = true,
    }.Init();

    // Starting/stopping an already-in-that-state animation is a no-op, so the local call (for the
    // gazing player) and the server's replicated call (so other players see the pose) never fight.
    public static void SetGazing(Entity? entity, bool gazing)
    {
        if (entity?.AnimManager == null) return;

        if (gazing) entity.AnimManager.StartAnimation(GazeAnim);
        else entity.AnimManager.StopAnimation(EyeShieldAnimationCode);
    }
}
