using Raylib_cs;
using rlImGui_cs;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private bool _disposed;

    private void DisposeGraphics()
    {
        _viewport?.Dispose();
        _viewport = null;

        _sliceViewport?.Dispose();
        _sliceViewport = null;

        if (_imguiInitialized)
        {
            rlImGui.Shutdown();
            _imguiInitialized = false;
        }

        if (_windowInitialized)
        {
            Raylib.CloseWindow();
            _windowInitialized = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeGraphics();

        _simulation?.Dispose();
        _simulation = null;
    }
}