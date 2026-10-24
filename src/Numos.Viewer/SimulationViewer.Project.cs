using Numos.API;
using Numos.CoreSim;
using Numos.Maths;
using Numos.SimDrawer;
using Numos.Workbench;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private void CreateSimulationProject(
        string projectName,
        int chunkWidth,
        int chunkHeight,
        int chunkDepth,
        bool includeDefaultGases)
    {
        SimulationFrameBuilder? frameBuilder = null;
        _session.CreateProject(
            new WorkbenchProjectOptions(projectName, chunkWidth, chunkHeight, chunkDepth, includeDefaultGases),
            config =>
            {
                var visualizations = VisualizationRegistry.CreateDefault(config);
                _configureVisualizations?.Invoke(visualizations);
                frameBuilder = new SimulationFrameBuilder(config, visualizations);
            });

        _frameBuilder = frameBuilder;
        _isPaused = true;
        _showConfigurationPanel = true;
        _knownSimulationRevision = -1;
        ReconcileSimulationSurfaces();
        RequestNextFrame();
    }

    /// <summary>
    ///     Drops every presentation object tied to the project that is closing. The session raises
    ///     <see cref="WorkbenchSession.ProjectClosing" /> before it disposes the world.
    /// </summary>
    private void ResetProjectPresentation()
    {
        SaveActiveSurfaceState();
        foreach (var surface in _simulationSurfaces)
            surface.Dispose();

        _simulationSurfaces.Clear();
        _viewport = null;
        _simulation = null;
        _knownSimulationRevision = -1;
        _simulationFeedback = null;
        _simulationPendingRemoval = null;
        _removeSimulationModalOpen = false;
        _requestRemoveSimulation = false;
        _selectedLinkSet = null;
        _topologyFeedback = null;
        _topologyPendingRemoval = null;
        _removeTopologyModalOpen = false;
        _requestRemoveTopology = false;
        _portalFirst = null;
        _portalSecond = null;
        _timelineOperation = null;
        _timelineError = null;
        _pendingScrubTick = null;
        _timelineFirstTick = 0;
        _frameBuilder = null;

        _liveChunkHandles.Clear();
        _liveChunkPositions.Clear();
        _chunkCollectionRevision = -1;
        _snapshotCache.Clear();
        _snapshotRequests.Clear();
        _snapshotSourceVersion = 0;
        _orderedSnapshots.Clear();
        _staleSnapshotKeys.Clear();
        _drawData = null;
        _sliceDrawData = null;
        _sliceSourceFrame = null;
        _sliceFrameChunk = null;
        _sliceCameraFit = true;
        _sliceRenderer?.ReleaseTexture();
        _hoveredSliceCell = null;
        _hovered3DCell = null;
        _selectedCell = null;
        _selectedCells.Clear();
        _paintedCells.Clear();
        _lastPaintedCell = null;
        _voxelDragStart = null;
        _voxelDragAnchor = null;
        _voxelDragViewport = 0;
        _voxelDetailCache.Clear();
        _highlights.Clear();
        _focusedChunk = null;
        _cameraInitialized = false;
        _frameSceneOnNextPresentation = false;

        _steppedToTick = null;
        _toolChunkPosition = null;
        _chunkPendingRemoval = null;
        _removeChunkModalOpen = false;
        _requestRemoveChunk = false;
        _resetConfigurationModalOpen = false;
        _requestResetConfiguration = false;
        _chunkFeedback = null;
        _voxelFeedback = null;
        _injectionFeedback = null;
        _gasRegistryFeedback = null;
        _newGasFeedback = null;
        _configurationFeedback = null;
        ClearConfigurationDrafts();
    }

    private void AddProjectChunk(Int3 position, int roomId)
    {
        if (_session.World == null)
            return;

        try
        {
            _session.AddChunk(position, roomId);
            _frameSceneOnNextPresentation = true;
            _chunkFeedback = new FeedbackMessage($"Added chunk {FormatChunkPosition(position)}.", false);
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or InvalidOperationException)
        {
            _chunkFeedback = new FeedbackMessage($"Could not add chunk {FormatChunkPosition(position)}: {exception.Message}", true);
        }
    }

    private void RemoveProjectChunk(AtmosChunkHandle chunk)
    {
        if (_session.World == null)
            return;

        _chunkFeedback = _session.RemoveChunk(chunk.Position)
            ? new FeedbackMessage($"Removed chunk {FormatChunkPosition(chunk.Position)}.", false)
            : new FeedbackMessage($"Chunk {FormatChunkPosition(chunk.Position)} no longer exists.", true);
    }

    private void SealProjectChunk(AtmosChunkHandle chunk)
    {
        if (_session.World == null)
            return;

        try
        {
            _session.SealChunk(chunk.Position);
            _chunkFeedback = new FeedbackMessage(
                $"Replaced the outer faces of chunk {FormatChunkPosition(chunk.Position)} with solid walls.",
                false);
        }
        catch (KeyNotFoundException exception)
        {
            _chunkFeedback = new FeedbackMessage($"Could not seal chunk {FormatChunkPosition(chunk.Position)}: {exception.Message}", true);
        }
    }

    private void UnsleepProjectChunk(AtmosChunkHandle chunk)
    {
        if (_session.World == null)
            return;

        try
        {
            _session.WakeChunk(chunk.Position);
            _chunkFeedback = new FeedbackMessage($"Unslept chunk {FormatChunkPosition(chunk.Position)}.", false);
        }
        catch (KeyNotFoundException exception)
        {
            _chunkFeedback = new FeedbackMessage(exception.Message, true);
        }
    }

    private void FillProjectChunk(AtmosChunkHandle chunk, int classification)
    {
        if (_session.World == null)
            return;

        try
        {
            _session.FillChunk(chunk.Position, classification);
            _chunkFeedback = new FeedbackMessage(
                $"Filled chunk {FormatChunkPosition(chunk.Position)} with classification {classification}.",
                false);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or KeyNotFoundException)
        {
            _chunkFeedback = new FeedbackMessage($"Could not fill chunk {FormatChunkPosition(chunk.Position)}: {exception.Message}", true);
        }
    }

    private void AddProjectGas(GasProperties gas)
    {
        if (_session.Config == null)
            return;

        try
        {
            int gasId = _session.AddGas(gas);
            _newGasFeedback = new FeedbackMessage($"Added gas {gas.Name} with ID {gasId}.", false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _newGasFeedback = new FeedbackMessage(exception.Message, true);
        }
    }

    private void RemoveProjectGas(int gasId)
    {
        if (_session.World == null ||
            _session.Config == null ||
            gasId < 0 ||
            gasId >= _session.Config.GasRegistry.Count)
            return;

        string name = _session.Config.GasRegistry[gasId].Name;
        try
        {
            _session.RemoveGas(gasId);
            _gasRegistryFeedback = new FeedbackMessage($"Removed gas {name}.", false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _gasRegistryFeedback = new FeedbackMessage(exception.Message, true);
        }
    }

    private void InjectProjectGas(
        AtmosChunkHandle chunk,
        int x,
        int y,
        int z,
        int gasId,
        float moles,
        float temperature)
    {
        if (_session.ActiveSimulation == null || _session.Config == null)
            return;

        if (gasId < 0 || gasId >= _session.Config.GasRegistry.Count)
        {
            _injectionFeedback = Report("Select a registered gas before injecting.", true);
            return;
        }

        try
        {
            var cell = _session.GetCell(chunk.Position, x, y, z);
            _session.AddVoxelGas(cell, gasId, moles, temperature);
            _injectionFeedback = new FeedbackMessage(
                $"Injected {moles:G} mol of {_session.Config.GasRegistry[gasId].Name} into ({x}, {y}, {z}).",
                false);
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or KeyNotFoundException or InvalidOperationException)
        {
            _injectionFeedback = new FeedbackMessage($"Could not inject gas: {exception.Message}", true);
        }
    }

    private void IgniteProjectSource(
        AtmosChunkHandle chunk,
        int x,
        int y,
        int z,
        float temperature,
        float volume)
    {
        if (_simulation == null || _session.Config == null)
            return;

        try
        {
            _simulation.IgniteVoxel(chunk, x, y, z, temperature, volume);
            _injectionFeedback = Report(
                $"Did it",
                false);
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or KeyNotFoundException or InvalidOperationException)
        {
            _injectionFeedback = new FeedbackMessage(exception.Message, true);
        }
    }

    /// <summary>
    ///     Logs <paramref name="message" /> and returns it for display beside the control that caused it.
    /// </summary>
    private FeedbackMessage Report(string message, bool isError)
    {
        SetProjectMessage(message, isError);
        return new FeedbackMessage(message, isError);
    }

    private readonly record struct FeedbackMessage(string Message, bool IsError);
}
