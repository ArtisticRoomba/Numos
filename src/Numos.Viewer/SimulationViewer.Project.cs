using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Maths;
using Numos.SimDrawer;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private readonly static GasProperties Oxygen = new()
    {
        Name = "Oxygen",
        MolarHeatCapacityAtConstantVolume =
            AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
        BoilingPoint = 90.2f,
        CondensationEnabled = true,
        MolarEnthalpyOfVaporization = 6_820f,
        LiquidId = 0,
        DiffusionCoefficient = 0.02f
    };

    private readonly static GasProperties Nitrogen = new()
    {
        Name = "Nitrogen",
        MolarHeatCapacityAtConstantVolume =
            AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
        BoilingPoint = 77.34f,
        CondensationEnabled = true,
        MolarEnthalpyOfVaporization = 5_600f,
        LiquidId = 1,
        DiffusionCoefficient = 0.02f
    };

    private Int3 _chunkDimensions;

    private void CreateSimulationProject(
        string projectName,
        int chunkWidth,
        int chunkHeight,
        int chunkDepth,
        bool includeDefaultGases)
    {
        var config = new AtmosConfig();
        if (includeDefaultGases)
        {
            config.GasRegistry.Add(Oxygen);
            config.GasRegistry.Add(Nitrogen);
        }

        AtmosSimulation? simulation = null;
        try
        {
            simulation = new AtmosSimulation(config, chunkWidth, chunkHeight, chunkDepth);
            simulation.StartRecording();
            var visualizations = VisualizationRegistry.CreateDefault(config);
            _configureVisualizations?.Invoke(visualizations);
            var frameBuilder = new SimulationFrameBuilder(config, visualizations);

            DisposeSimulationProject();

            _simulation = simulation;
            _replayTimeline = new AtmosReplayTimeline(simulation);
            _config = config;
            _frameBuilder = frameBuilder;
            _projectName = string.IsNullOrWhiteSpace(projectName)
                ? "Untitled Simulation"
                : projectName.Trim();

            _chunkDimensions = new Int3(chunkWidth, chunkHeight, chunkDepth);
            _isPaused = true;
            _showConfigurationPanel = true;
            simulation = null;
            SetProjectMessage($"Created project '{_projectName}'.", false);
        }
        finally
        {
            simulation?.Dispose();
        }
    }

    private void DisposeSimulationProject()
    {
        _simulation?.Dispose();
        _simulation = null;
        _replayTimeline = null;
        _timelineOperation = null;
        _timelineError = null;
        _pendingScrubTick = null;
        _replayElapsed = 0f;
        _timelineFirstTick = 0;
        _config = null;
        _frameBuilder = null;
        _projectName = null;
        _chunkDimensions = default;

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
        _sliceProjectionKey = null;
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
        _selectedSliceChunkPosition = null;
        _cameraInitialized = false;
        _frameSceneOnNextPresentation = false;
    }

    private void AddProjectChunk(Int3 position, int roomId)
    {
        if (_simulation == null)
            return;

        try
        {
            var chunk = _simulation.CreateAndRegisterChunk(position);
            _simulation.SetChunkClassification(chunk, new VoxelClassification(roomId));
            _frameSceneOnNextPresentation = true;
            SetProjectMessage($"Added chunk {FormatChunkPosition(position)}.", false);
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or InvalidOperationException)
        {
            SetProjectMessage(exception.Message, true);
        }
    }

    private void RemoveProjectChunk(AtmosChunkHandle chunk)
    {
        if (_simulation == null)
            return;

        if (_simulation.UnregisterChunk(chunk))
        {
            SetProjectMessage($"Removed chunk {FormatChunkPosition(chunk.Position)}.", false);
            return;
        }

        SetProjectMessage($"Chunk {FormatChunkPosition(chunk.Position)} no longer exists.", true);
    }

    private void SealProjectChunk(AtmosChunkHandle chunk)
    {
        if (_simulation == null)
            return;

        try
        {
            _simulation.SetChunkBoundaryClassification(chunk, VoxelClassification.RoomSolid);
            SetProjectMessage(
                $"Replaced the outer faces of chunk {FormatChunkPosition(chunk.Position)} with solid walls.",
                false);
        }
        catch (KeyNotFoundException exception)
        {
            SetProjectMessage(exception.Message, true);
        }
    }

    private void AddProjectGas(GasProperties gas)
    {
        if (_config == null)
            return;

        string name = gas.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            SetProjectMessage("A gas name is required.", true);
            return;
        }

        if (!float.IsFinite(gas.MolarHeatCapacityAtConstantVolume) ||
            gas.MolarHeatCapacityAtConstantVolume < 0f ||
            !float.IsFinite(gas.BoilingPoint) ||
            gas.BoilingPoint < 0f ||
            !float.IsFinite(gas.MolarEnthalpyOfVaporization) ||
            gas.MolarEnthalpyOfVaporization < 0f ||
            !float.IsFinite(gas.DiffusionCoefficient) ||
            gas.DiffusionCoefficient < 0f)
        {
            SetProjectMessage("Gas properties must be finite, non-negative values.", true);
            return;
        }

        gas.Name = name;
        _config.GasRegistry.Add(gas);
        ApplyConfiguration();
        SetProjectMessage($"Added gas {name} with ID {_config.GasRegistry.Count - 1}.", false);
    }

    private void RemoveProjectGas(int gasId)
    {
        if (_simulation == null ||
            _config == null ||
            gasId < 0 ||
            gasId >= _config.GasRegistry.Count)
            return;

        foreach (var handle in _simulation.GetChunkHandles())
        {
            var snapshot = _simulation.GetChunkSnapshot(handle);
            if (snapshot.Gases.Any(gas => gas.GasId >= gasId))
            {
                SetProjectMessage(
                    "That gas cannot be removed because it, or a later gas ID, has already been used by a chunk. " +
                    "Remove the affected chunks first so gas IDs remain stable.",
                    true);

                return;
            }
        }

        string name = _config.GasRegistry[gasId].Name;
        _config.GasRegistry.RemoveAt(gasId);
        ApplyConfiguration();
        SetProjectMessage($"Removed gas {name}.", false);
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
        if (_simulation == null || _config == null)
            return;

        if (gasId < 0 || gasId >= _config.GasRegistry.Count)
        {
            SetProjectMessage("Select a registered gas before injecting.", true);
            return;
        }

        try
        {
            _simulation.AddGasToVoxel(chunk, x, y, z, gasId, moles, temperature);
            SetProjectMessage(
                $"Injected {moles:G} mol of {_config.GasRegistry[gasId].Name} into ({x}, {y}, {z}).",
                false);
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or KeyNotFoundException or InvalidOperationException)
        {
            SetProjectMessage(exception.Message, true);
        }
    }

    private void ApplyConfiguration()
    {
        if (_simulation != null && _config != null)
            _simulation.SetAtmosConfig(_config);
    }
}