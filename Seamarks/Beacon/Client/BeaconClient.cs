using Vintagestory.API.Client;

namespace Seamarks.Beacon.Client;

public sealed class BeaconClient : IDisposable
{
    public BeaconClientRegistry Registry { get; }
    private readonly BeaconGazeWatcher gazeWatcher;
    private readonly BeaconPointLights pointLights;
    private readonly GuiDialogBeamTuning tuningPanel; // opened with Ctrl+B
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
        tuningPanel = new GuiDialogBeamTuning(capi);
    }

    public void Dispose()
    {
        blipRenderer.Dispose();
        beamRenderer.Dispose();
        pointLights.Dispose();
        beamParticles.Dispose();
        gazeWatcher.Dispose();
        tuningPanel.Dispose();
        // Last, mirroring the constructor, where it runs first: the renderers read it while they tear down.
        BeamQuality.Shutdown();
    }
}
