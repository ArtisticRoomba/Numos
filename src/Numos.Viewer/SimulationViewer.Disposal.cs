using Raylib_cs;
using rlImGui_cs;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;

        _replayFileCancellation?.Cancel();
        Task<LoadedReplay>? replayLoadTask = _replayLoadTask;
        if (replayLoadTask?.IsCompletedSuccessfully == true)
            replayLoadTask.Result.World.Dispose();
        else if (replayLoadTask != null)
        {
            _ = replayLoadTask.ContinueWith(
                static task => task.Result.World.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
        }

        _disposed = true;
        try
        {
            DisposeGraphics();
            DisposeSimulationProject();
        }
        finally
        {
            StopMessageCapture();
        }
    }

    private void DisposeGraphics()
    {
        foreach (var surface in _simulationSurfaces)
            surface.Dispose();

        _viewport = null;

        _sliceViewport?.Dispose();
        _sliceViewport = null;

        if (_viewportBranding.Id != 0)
        {
            Raylib.UnloadTexture(_viewportBranding);
            _viewportBranding = default;
        }

        if (_imguiInitialized)
        {
            SaveCurrentLayout();
            rlImGui.Shutdown();
            _imguiInitialized = false;
        }

        if (_windowInitialized)
        {
            Raylib.CloseWindow();
            _windowInitialized = false;
        }
    }
}