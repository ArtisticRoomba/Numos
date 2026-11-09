using System.Runtime.InteropServices;
using Raylib_cs;
using rlImGui_cs;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private bool _disposed;

    /// <summary>
    ///     Stops the simulation host, releases graphics, and restores the console writers.
    /// </summary>
    /// <remarks>
    ///     The host goes first: disposing it waits for the command or tick it is running and then disposes the
    ///     session and world. A replay load in progress is cancelled so that wait stays short. A background draw build
    ///     only reads immutable snapshots, so it can finish after this returns.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _replayFileCancellation?.Cancel();
        try
        {
            _host.Dispose();
            DisposeGraphics();
        }
        finally
        {
            StopMessageCapture();
            _replayFileCancellation?.Dispose();
            _replayFileCancellation = null;
        }
    }

    private void DisposeGraphics()
    {
        foreach (var surface in _document.Surfaces)
            surface.Dispose();

        _viewport = null;

        _sliceRenderer?.Dispose();
        _sliceRenderer = null;

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