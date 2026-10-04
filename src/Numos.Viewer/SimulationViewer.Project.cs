using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.GasReactions;
using Numos.Maths;
using Numos.SimDrawer;
using Numos.Units.Generated;

namespace Numos.Viewer;

public partial class SimulationViewer
{
    private readonly static GasProperties Hydrogen = new()
    {
        Name = "Hydrogen",
        MolarHeatCapacityAtConstantVolume =
            AtmosPhysicalConstants.IdealDiatomicMolarHeatCapacityAtConstantVolume,
        BoilingPoint = 20.4f,
        CondensationEnabled = true,
        MolarEnthalpyOfVaporization = 904f,
        LiquidId = 0,
        DiffusionCoefficient = 0.02f
    };

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

    private readonly static GasProperties CarbonDioxide = new()
    {
        Name = "Carbon Dioxide",
        MolarHeatCapacityAtConstantVolume = 28.2f,
        BoilingPoint = 194.7f,
        CondensationEnabled = true,
        MolarEnthalpyOfVaporization = 9800f,
        LiquidId = 0,
        DiffusionCoefficient = 0.02f
    };

    private readonly static GasProperties NitrousOxide = new()
    {
        Name = "Nitrous Oxide",
        MolarHeatCapacityAtConstantVolume = 30.3f,
        BoilingPoint = 184.71f,
        CondensationEnabled = true,
        MolarEnthalpyOfVaporization = 16_540f,
        LiquidId = 0,
        DiffusionCoefficient = 0.02f
    };

    private readonly static GasProperties Water = new()
    {
        Name = "Water Vapour",
        MolarHeatCapacityAtConstantVolume = 28f,
        BoilingPoint = 373.15f,
        CondensationEnabled = true,
        MolarEnthalpyOfVaporization = 40_657f,
        LiquidId = 0,
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
            config.GasRegistry.Add(Hydrogen);
            config.GasRegistry.Add(Oxygen);
            config.GasRegistry.Add(Nitrogen);
            config.GasRegistry.Add(CarbonDioxide);
            config.GasRegistry.Add(NitrousOxide);
            config.GasRegistry.Add(Water);

            var waterSynthesis = new StandardGasReaction(
                new Dictionary<GasProperties, float>
                    { { Hydrogen, 2 }, { Oxygen, 1 } },
                new Dictionary<GasProperties, float>
                {
                    { Water, 2 }
                },
                285.8f,
                1.8e13f,
                UnitConversions.FromKilojoulePerMole(146.4f),
                new Dictionary<GasProperties, float>
                {
                    { Hydrogen, 1 },
                    { Oxygen, 0.5f }
                });

            config.SolverConfigurations = [new GasReactionConfig(standardReactions: [waterSynthesis])];
        }

        AtmosWorld? world = null;
        try
        {
            world = new AtmosWorld(config);
            var simulation = world.CreateSimulation(chunkWidth, chunkHeight, chunkDepth);
            var visualizations = VisualizationRegistry.CreateDefault(config);
            _configureVisualizations?.Invoke(visualizations);
            var frameBuilder = new SimulationFrameBuilder(config, visualizations);

            DisposeSimulationProject();

            _world = world;
            _simulation = simulation;
            _replayTimeline = new AtmosWorldReplayTimeline(world);
            _replayBranches = new ReplayBranchSession(world, _replayTimeline);
            _config = config;
            _frameBuilder = frameBuilder;
            _projectName = string.IsNullOrWhiteSpace(projectName)
                ? "Untitled Simulation"
                : projectName.Trim();

            _chunkDimensions = new Int3(chunkWidth, chunkHeight, chunkDepth);
            _isPaused = true;
            _showConfigurationPanel = true;
            _knownSimulationRevision = -1;
            ReconcileSimulationSurfaces();
            world = null;
            SetProjectMessage($"Created project '{_projectName}'.", false);
        }
        finally
        {
            world?.Dispose();
        }
    }

    private void DisposeSimulationProject()
    {
        SaveActiveSurfaceState();
        foreach (var surface in _simulationSurfaces)
            surface.Dispose();

        _simulationSurfaces.Clear();
        _simulationNames.Clear();
        _viewport = null;
        _world?.Dispose();
        _world = null;
        _simulation = null;
        _activeSimulationId = null;
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
        _replayTimeline = null;
        _replayBranches = null;
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
        if (_simulation == null)
            return;

        try
        {
            var chunk = _simulation.CreateAndRegisterChunk(position);
            _simulation.SetChunkClassification(chunk, new VoxelClassification(roomId));
            _frameSceneOnNextPresentation = true;
            _chunkFeedback = Report($"Added chunk {FormatChunkPosition(position)}.", false);
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or InvalidOperationException)
        {
            _chunkFeedback = ReportException($"Could not add chunk {FormatChunkPosition(position)}", exception);
        }
    }

    private void RemoveProjectChunk(AtmosChunkHandle chunk)
    {
        if (_simulation == null)
            return;

        if (_simulation.UnregisterChunk(chunk))
        {
            _chunkFeedback = Report($"Removed chunk {FormatChunkPosition(chunk.Position)}.", false);
            return;
        }

        _chunkFeedback = Report($"Chunk {FormatChunkPosition(chunk.Position)} no longer exists.", true);
    }

    private void SealProjectChunk(AtmosChunkHandle chunk)
    {
        if (_simulation == null)
            return;

        try
        {
            _simulation.SetChunkBoundaryClassification(chunk, VoxelClassification.RoomSolid);
            _chunkFeedback = Report(
                $"Replaced the outer faces of chunk {FormatChunkPosition(chunk.Position)} with solid walls.",
                false);
        }
        catch (KeyNotFoundException exception)
        {
            _chunkFeedback = ReportException($"Could not seal chunk {FormatChunkPosition(chunk.Position)}", exception);
        }
    }

    private void UnsleepProjectChunk(AtmosChunkHandle chunk)
    {
        if (_simulation == null)
            return;

        try
        {
            _simulation.WakeChunk(chunk);
            _chunkFeedback = Report($"Unslept chunk {FormatChunkPosition(chunk.Position)}.", false);
        }
        catch (KeyNotFoundException exception)
        {
            _chunkFeedback = Report(exception.Message, true);
        }
    }

    private void FillProjectChunk(AtmosChunkHandle chunk, int classification)
    {
        if (_simulation == null)
            return;

        try
        {
            _simulation.SetChunkClassification(chunk, new VoxelClassification(classification));
            _chunkFeedback = Report(
                $"Filled chunk {FormatChunkPosition(chunk.Position)} with classification {classification}.",
                false);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or KeyNotFoundException)
        {
            _chunkFeedback = ReportException($"Could not fill chunk {FormatChunkPosition(chunk.Position)}", exception);
        }
    }

    private void AddProjectGas(GasProperties gas)
    {
        if (_config == null)
            return;

        string name = gas.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            _newGasFeedback = Report("Could not add the gas: a name is required.", true);
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
            _newGasFeedback = Report(
                $"Could not add {name}: molar Cv, boiling point, vaporization enthalpy, and diffusion coefficient " +
                "must be finite and at least 0.",
                true);

            return;
        }

        foreach (var registered in _config.GasRegistry)
        {
            if (registered.Name == name)
            {
                _newGasFeedback = Report($"Could not add {name}: a gas with that name is already registered.", true);
                return;
            }
        }

        gas.Name = name;
        _config.GasRegistry.Add(gas);
        if (!TryApplyConfiguration(out string? error))
        {
            _config.GasRegistry.RemoveAt(_config.GasRegistry.Count - 1);
            _newGasFeedback = new FeedbackMessage($"Could not add {name}: {error}", true);
            return;
        }

        _newGasFeedback = Report($"Added gas {name} with ID {_config.GasRegistry.Count - 1}.", false);
    }

    private void RemoveProjectGas(int gasId)
    {
        if (_world == null ||
            _config == null ||
            gasId < 0 ||
            gasId >= _config.GasRegistry.Count)
            return;

        foreach (var simulation in _world.Simulations)
        foreach (var handle in simulation.GetChunkHandles())
        {
            var snapshot = simulation.GetChunkSnapshot(handle);
            if (snapshot.Gases.Any(gas => gas.GasId >= gasId))
            {
                _gasRegistryFeedback = Report(
                    $"Could not remove {_config.GasRegistry[gasId].Name}: it, or a later gas ID, has already been " +
                    "used by a chunk. Remove the affected chunks first so gas IDs remain stable.",
                    true);

                return;
            }
        }

        string name = _config.GasRegistry[gasId].Name;
        var previous = new GasRegistry();
        foreach (var gas in _config.GasRegistry)
            previous.Add(gas);

        _config.GasRegistry.RemoveAt(gasId);
        if (!TryApplyConfiguration(out string? error))
        {
            _config.GasRegistry = previous;
            _gasRegistryFeedback = new FeedbackMessage($"Could not remove {name}: {error}", true);
            return;
        }

        _gasRegistryFeedback = Report($"Removed gas {name}.", false);
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
            _injectionFeedback = Report("Select a registered gas before injecting.", true);
            return;
        }

        try
        {
            _simulation.AddGasToVoxel(chunk, x, y, z, gasId, moles, temperature);
            _injectionFeedback = Report(
                $"Injected {moles:G} mol of {_config.GasRegistry[gasId].Name} into ({x}, {y}, {z}).",
                false);
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or KeyNotFoundException or InvalidOperationException)
        {
            _injectionFeedback = ReportException("Could not inject gas", exception);
        }
    }

    private void ApplyConfiguration()
    {
        if (_world != null && _config != null)
            _world.SetAtmosConfig(_config);
    }

    /// <summary>
    ///     Applies <see cref="_config" /> to the world, reporting the failure instead of throwing.
    /// </summary>
    /// <param name="error">Why the world rejected the configuration, or <see langword="null" /> on success.</param>
    /// <returns><see langword="true" /> when the world accepted the configuration.</returns>
    /// <remarks>
    ///     On failure the world keeps its previous configuration, so the caller must undo its change to
    ///     <see cref="_config" /> or the panel will show values the simulation is not using.
    /// </remarks>
    private bool TryApplyConfiguration(out string? error)
    {
        try
        {
            ApplyConfiguration();
            error = null;
            return true;
        }
        catch (KeyNotFoundException exception)
        {
            // Gas reactions resolve their species by name when the config is captured.
            error = $"a gas reaction still refers to a gas that would not be registered ({exception.Message})";
            WriteException("Could not apply the atmosphere configuration", exception);
            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            error = exception.Message;
            WriteException("Could not apply the atmosphere configuration", exception);
            return false;
        }
    }

    /// <summary>
    ///     Copies every scalar <see cref="AtmosConfig" /> setting, leaving the gas registry and solver configurations
    ///     alone.
    /// </summary>
    private static void CopyScalarSettings(AtmosConfig source, AtmosConfig destination)
    {
        destination.GlobalTemperature = source.GlobalTemperature;
        destination.DefaultTemperatureFallback = source.DefaultTemperatureFallback;
        destination.DefaultMolarHeatCapacityAtConstantVolume = source.DefaultMolarHeatCapacityAtConstantVolume;
        destination.VoxelVolume = source.VoxelVolume;
        destination.SaturationReferencePressure = source.SaturationReferencePressure;
        destination.DefaultDiffusionCoefficient = source.DefaultDiffusionCoefficient;
        destination.SpaceTemperature = source.SpaceTemperature;
        destination.BulkFlowCoefficient = source.BulkFlowCoefficient;
        destination.VacuumThreshold = source.VacuumThreshold;
        destination.SleepThreshold = source.SleepThreshold;
        destination.SleepEpsilon = source.SleepEpsilon;
        destination.ThermalConductance = source.ThermalConductance;
        destination.CondensationRateFactor = source.CondensationRateFactor;
        destination.MaxPressureTransferFractionPerNeighbor = source.MaxPressureTransferFractionPerNeighbor;
        destination.AccumulatorWakeThreshold = source.AccumulatorWakeThreshold;
        destination.AccumulatorMaxAliveTicks = source.AccumulatorMaxAliveTicks;
    }

    /// <summary>
    ///     Logs <paramref name="message" /> and returns it for display beside the control that caused it.
    /// </summary>
    private FeedbackMessage Report(string message, bool isError)
    {
        SetProjectMessage(message, isError);
        return new FeedbackMessage(message, isError);
    }

    /// <summary>
    ///     Logs <paramref name="exception" /> with its stack trace and returns a short local error for the panel.
    /// </summary>
    private FeedbackMessage ReportException(string context, Exception exception)
    {
        WriteException(context, exception);
        return new FeedbackMessage($"{context}: {exception.Message}", true);
    }

    private readonly record struct FeedbackMessage(string Message, bool IsError);
}