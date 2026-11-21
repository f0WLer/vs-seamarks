using Vintagestory.API.Client;

namespace Seamarks.Beacon.Client;

public sealed class BeaconClient : IDisposable
{
    public BeaconClientRegistry Registry { get; }
    private readonly BeaconGazeWatcher gazeWatcher;
    private readonly BeaconPointLights pointLights;
#if DEBUG
    private readonly GuiDialogBeamTuning devPanel; // opened with Ctrl+B, Debug builds only
#endif
    private readonly BeaconBlipRenderer blipRenderer;
    private readonly BeamRenderer beamRenderer;
    private readonly BeaconBeamParticles beamParticles;

    public BeaconClient(SeamarksModSystem modSystem, ICoreClientAPI capi)
    {
        BeamQuality.Init(capi); // wire graphics-scalability watchers before the renderers build meshes
        Registry = new BeaconClientRegistry(capi);
        blipRenderer = new BeaconBlipRenderer(modSystem, capi);
        beamRenderer = new BeamRenderer(modSystem, capi);
        pointLights = new BeaconPointLights(modSystem, capi);
        beamParticles = new BeaconBeamParticles(modSystem, capi);
        gazeWatcher = new BeaconGazeWatcher(capi, this);
#if DEBUG
        devPanel = new GuiDialogBeamTuning(capi);
#endif
    }

    public void Dispose()
    {
        blipRenderer.Dispose();
        beamRenderer.Dispose();
        pointLights.Dispose();
        beamParticles.Dispose();
        gazeWatcher.Dispose();
#if DEBUG
        devPanel.Dispose();
#endif
        // Last, mirroring the constructor, where it runs first: the renderers read it while they tear down.
        BeamQuality.Shutdown();
    }
}
