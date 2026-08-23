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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeGraphics();
        DisposeSimulationProject();
    }
}