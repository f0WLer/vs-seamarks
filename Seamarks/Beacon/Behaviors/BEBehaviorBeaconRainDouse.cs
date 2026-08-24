using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Seamarks.Beacon;

// An open flame doused by rain, mirroring vanilla open-fire behavior. Server-only; runs a burn-gated
// 1s check and extinguishes the beacon in sufficiently heavy, unsheltered rain.
public class BEBehaviorBeaconRainDouse : BEBehaviorBeacon, IBeaconBurnListener
{
    private WeatherSystemBase? weatherSystem;
    private readonly Vec3d checkPos = new();
    private long rainListenerId;

    public BEBehaviorBeaconRainDouse(BlockEntity blockentity) : base(blockentity) { }

    public override void Initialize(ICoreAPI api, JsonObject properties)
    {
        base.Initialize(api, properties);
        if (api.Side == EnumAppSide.Server)
        {
            weatherSystem = api.ModLoader.GetModSystem<WeatherSystemBase>();
        }
    }

    public void OnBurnStateChanged(bool burning)
    {
        if (Api?.Side != EnumAppSide.Server) return;
        // The config can switch dousing off for a tier, in which case the flame never registers the rain
        // check and so can't be put out.
        bool douse = burning && BeaconArchetypeData.Of(Beacon.Tier).RainDouseEnabled;
        ReconcileListener(ref rainListenerId, douse, CheckRain);
    }

    // GetRainMapHeightAt gives the topmost rain-catching surface in this column. If it sits above us,
    // we're sheltered. Divisor of 5 caps the extinguish chance at 20%/sec in the heaviest rain.
    private void CheckRain(float dt)
    {
        if (!Beacon.IsBurning) return;
        if (Api.World.BlockAccessor.GetRainMapHeightAt(Pos.X, Pos.Z) > Pos.Y) return;

        checkPos.Set(Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5);
        double precipitation = weatherSystem?.GetPrecipitation(checkPos) ?? 0;
        if (precipitation > 0.05 && Api.World.Rand.NextDouble() < precipitation / 5.0)
        {
            Beacon.Extinguish();
        }
    }
}
