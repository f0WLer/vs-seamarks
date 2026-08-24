using Vintagestory.API.Client;

namespace Seamarks.Beacon.Client;

public sealed class BeaconClient : IDisposable
{
    public BeaconClientRegistry Registry { get; }
    public BeaconGazeWatcher GazeWatcher { get; }
    public BeaconPointLights PointLights { get; }
    public GuiDialogBeamTuning TuningPanel { get; } // opened with Ctrl+B
    private readonly BeaconBlipRenderer blipRenderer;
    private readonly BeamRenderer beamRenderer;
    private readonly BeaconBeamParticles beamParticles;

    public BeaconClient(SeamarksModSystem modSystem, ICoreClientAPI capi)
    {
        BeamQuality.Init(capi); // wire graphics-scalability watchers before the renderers build meshes
        Registry = new BeaconClientRegistry(capi);
        blipRenderer = new BeaconBlipRenderer(modSystem, capi);
        beamRenderer = new BeamRenderer(modSystem, capi);
        PointLights = new BeaconPointLights(modSystem, capi);
        beamParticles = new BeaconBeamParticles(modSystem, capi);
        GazeWatcher = new BeaconGazeWatcher(capi, this);
        TuningPanel = new GuiDialogBeamTuning(capi);
    }

    public void Dispose()
    {
        blipRenderer.Dispose();
        beamRenderer.Dispose();
        PointLights.Dispose();
        beamParticles.Dispose();
        GazeWatcher.Dispose();
        TuningPanel.Dispose();
        // Last, mirroring the constructor, where it runs first: the renderers read it while they tear down.
        BeamQuality.Shutdown();
    }
}
