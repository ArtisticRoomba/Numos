namespace Numos.Viewer;

public partial class SimulationViewer
{
    private bool _glResourcesDisposed;
    private bool _disposed;

    private void OnWindowClosing()
    {
        DisposeGlResources();
    }

    private void DisposeGlResources()
    {
        if (_glResourcesDisposed)
            return;

        _glResourcesDisposed = true;

        _imguiController?.Dispose();
        _imguiController = null;

        _viewport?.Dispose();
        _viewport = null;

        _sliceViewport?.Dispose();
        _sliceViewport = null;

        _renderer?.Dispose();
        _renderer = null;

        _sliceRenderer?.Dispose();
        _sliceRenderer = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        DisposeGlResources();

        _input?.Dispose();
        _input = null;

        _simulation?.Dispose();
        _simulation = null;
    }
}