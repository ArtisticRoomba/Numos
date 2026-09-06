using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.Headless.Diagnostics;
using Numos.Headless.Protocol;
using Numos.Maths;

namespace Numos.Headless;

/// <summary>
///     Owns the in-memory simulation manipulated by one JSONL host connection.
/// </summary>
internal sealed class SimulationSession : IDisposable
{
    private const int MaximumTicksPerRequest = 1_000_000;
    private AtmosConfig? _config;
    private Coordinate? _dimensions;
    private string? _name;

    private AtmosSimulation? _simulation;

    internal bool HasSimulation => _simulation != null;

    public void Dispose()
    {
        _simulation?.Dispose();
        _simulation = null;
        _config = null;
        _dimensions = null;
        _name = null;
    }

    internal CommandExecution Execute(HeadlessRequest request)
    {
        return request.Op switch
        {
            "createSimulation" => CreateSimulation(request),
            "closeSimulation" => CloseSimulation(),
            "addChunk" => AddChunk(request),
            "removeChunk" => RemoveChunk(request),
            "sealChunk" => SealChunk(request),
            "setChunkClassification" => SetChunkClassification(request),
            "setVoxelClassification" => SetVoxelClassification(request),
            "setVoxelTemperature" => SetVoxelTemperature(request),
            "addGas" => AddGas(request),
            "injectGas" => InjectGas(request),
            "wakeRoom" => WakeRoom(request),
            "sleepChunk" => SleepChunk(request),
            "updateConfig" => UpdateConfig(request),
            "setSolverEnabled" => SetSolverEnabled(request),
            "resetSolvers" => ResetSolvers(),
            "tick" => Tick(request),
            "observe" => Observe(request),
            "exit" => Exit(),
            _ => throw new HeadlessRequestException(
                "unknownOperation",
                $"Unknown operation '{request.Op}'. See docs/headless_runner.md for supported operations.")
        };
    }

    internal SessionState? GetState()
    {
        if (_simulation == null || _config == null || _dimensions == null || _name == null)
            return null;

        return new SessionState
        {
            Name = _name,
            Dimensions = _dimensions.Value,
            Tick = _simulation.TickCount,
            ChunkCount = _simulation.ChunkCount,
            GasCount = _config.GasRegistry.Count
        };
    }

    private CommandExecution CreateSimulation(HeadlessRequest request)
    {
        var dimensions = Require(request.Dimensions, "dimensions");
        var config = new AtmosConfig();
        request.Config?.ApplyTo(config);
        if (request.Gases != null)
        {
            foreach (var gas in request.Gases)
            {
                if (gas == null)
                    throw new HeadlessRequestException("invalidGas", "The gases array cannot contain null entries.");

                config.GasRegistry.Add(gas.ToGasProperties());
            }
        }

        AtmosSimulation? replacement = null;
        try
        {
            replacement = new AtmosSimulation(config, dimensions.X, dimensions.Y, dimensions.Z);
            Dispose();
            _simulation = replacement;
            _config = config;
            _dimensions = new Coordinate(dimensions.X, dimensions.Y, dimensions.Z);
            _name = string.IsNullOrWhiteSpace(request.Name)
                ? "Untitled Simulation"
                : request.Name.Trim();

            replacement = null;
        }
        finally
        {
            replacement?.Dispose();
        }

        return new CommandExecution();
    }

    private CommandExecution CloseSimulation()
    {
        Dispose();
        return new CommandExecution();
    }

    private CommandExecution AddChunk(HeadlessRequest request)
    {
        var simulation = RequireSimulation();
        var position = Require(request.Position, "position");
        var handle = simulation.CreateAndRegisterChunk(ToInt3(position));
        simulation.SetChunkClassification(
            handle,
            new VoxelClassification(request.Classification ?? VoxelClassification.RoomUnassigned));

        return new CommandExecution(new CommandResult { Position = Copy(position) });
    }

    private CommandExecution RemoveChunk(HeadlessRequest request)
    {
        var simulation = RequireSimulation();
        var position = Require(request.Position, "position");
        bool changed = simulation.UnregisterChunk(Handle(position));
        return new CommandExecution(
            new CommandResult
            {
                Position = Copy(position),
                Changed = changed
            });
    }

    private CommandExecution SealChunk(HeadlessRequest request)
    {
        var simulation = RequireSimulation();
        var position = Require(request.Position, "position");
        simulation.SetChunkBoundaryClassification(Handle(position), VoxelClassification.RoomSolid);
        return new CommandExecution(new CommandResult { Position = Copy(position) });
    }

    private CommandExecution SetChunkClassification(HeadlessRequest request)
    {
        var simulation = RequireSimulation();
        var position = Require(request.Position, "position");
        int classification = Require(request.Classification, "classification");
        simulation.SetChunkClassification(Handle(position), new VoxelClassification(classification));
        return new CommandExecution(new CommandResult { Position = Copy(position) });
    }

    private CommandExecution SetVoxelClassification(HeadlessRequest request)
    {
        var simulation = RequireSimulation();
        var position = Require(request.Position, "position");
        var voxel = Require(request.Voxel, "voxel");
        int classification = Require(request.Classification, "classification");
        simulation.SetVoxelClassification(
            Handle(position),
            voxel.X,
            voxel.Y,
            voxel.Z,
            new VoxelClassification(classification));

        return new CommandExecution();
    }

    private CommandExecution SetVoxelTemperature(HeadlessRequest request)
    {
        var simulation = RequireSimulation();
        var position = Require(request.Position, "position");
        var voxel = Require(request.Voxel, "voxel");
        float temperature = Require(request.TemperatureK, "temperatureK");
        simulation.SetVoxelTemperature(Handle(position), voxel.X, voxel.Y, voxel.Z, temperature);
        return new CommandExecution();
    }

    private CommandExecution AddGas(HeadlessRequest request)
    {
        var simulation = RequireSimulation();
        var config = _config!;
        var definition = Require(request.Gas, "gas");
        int gasId = config.GasRegistry.Count;
        config.GasRegistry.Add(definition.ToGasProperties());
        simulation.SetAtmosConfig(config);
        return new CommandExecution(new CommandResult { GasId = gasId });
    }

    private CommandExecution InjectGas(HeadlessRequest request)
    {
        var simulation = RequireSimulation();
        var position = Require(request.Position, "position");
        var voxel = Require(request.Voxel, "voxel");
        int gasId = Require(request.GasId, "gasId");
        if (gasId < 0 || gasId >= _config!.GasRegistry.Count)
        {
            throw new HeadlessRequestException(
                "gasNotFound",
                $"No gas is registered with ID {gasId}.");
        }

        float moles = Require(request.Moles, "moles");
        float temperature = Require(request.TemperatureK, "temperatureK");
        simulation.AddGasToVoxel(
            Handle(position),
            voxel.X,
            voxel.Y,
            voxel.Z,
            gasId,
            moles,
            temperature);

        return new CommandExecution();
    }

    private CommandExecution WakeRoom(HeadlessRequest request)
    {
        var simulation = RequireSimulation();
        var position = Require(request.Position, "position");
        int roomId = Require(request.RoomId, "roomId");
        simulation.WakeRoom(Handle(position), roomId);
        return new CommandExecution();
    }

    private CommandExecution SleepChunk(HeadlessRequest request)
    {
        var simulation = RequireSimulation();
        var position = Require(request.Position, "position");
        simulation.SleepChunk(Handle(position));
        return new CommandExecution();
    }

    private CommandExecution UpdateConfig(HeadlessRequest request)
    {
        var simulation = RequireSimulation();
        var patch = Require(request.Config, "config");
        patch.ApplyTo(_config!);
        simulation.SetAtmosConfig(_config!);
        return new CommandExecution();
    }

    private CommandExecution SetSolverEnabled(HeadlessRequest request)
    {
        var simulation = RequireSimulation();
        if (string.IsNullOrWhiteSpace(request.Solver))
            throw Missing("solver");

        bool enabled = Require(request.Enabled, "enabled");
        bool changed = simulation.Solvers.SetEnabled(request.Solver, enabled);
        if (!changed)
        {
            throw new HeadlessRequestException(
                "solverNotFound",
                $"No solver stage named '{request.Solver}' is registered.");
        }

        return new CommandExecution(new CommandResult { Changed = true });
    }

    private CommandExecution ResetSolvers()
    {
        var simulation = RequireSimulation();
        simulation.Solvers.ResetToDefaults();
        return new CommandExecution();
    }

    private CommandExecution Tick(HeadlessRequest request)
    {
        var simulation = RequireSimulation();
        int count = Require(request.Count, "count");
        if (count <= 0 || count > MaximumTicksPerRequest)
        {
            throw new HeadlessRequestException(
                "invalidTickCount",
                $"count must be between 1 and {MaximumTicksPerRequest}.");
        }

        for (int index = 0; index < count; index++)
            simulation.Tick();

        return new CommandExecution(new CommandResult { TicksExecuted = count });
    }

    private CommandExecution Observe(HeadlessRequest request)
    {
        var simulation = RequireSimulation();
        if (request.Voxel != null && request.Position == null)
            throw new HeadlessRequestException("missingProperty", "voxel requires a chunk position.");

        var options = new SimulationObservationOptions
        {
            Chunk = request.Position,
            Voxels = request.Voxel == null
                ? null
                : [new VoxelSelection(request.Position!, request.Voxel)],
            // A local voxel is an exact probe; it takes precedence over a simultaneous dense-output flag.
            IncludeVoxels = request.Voxel == null && (request.IncludeVoxels ?? false),
            OnlyGasBearingVoxels = request.OnlyGasBearingVoxels ?? false,
            MaxIssueLocations = request.MaxIssueLocations ?? SimulationObservationOptions.DefaultMaxIssueLocations
        };

        var observation = SimulationStateAnalyzer.Analyze(simulation, _config!, options);
        return new CommandExecution(Observation: observation);
    }

    private CommandExecution Exit()
    {
        Dispose();
        return new CommandExecution(ExitRequested: true);
    }

    private AtmosSimulation RequireSimulation()
    {
        return _simulation ??
               throw new HeadlessRequestException(
                   "simulationNotCreated",
                   "Create a simulation before running this operation.");
    }

    private static T Require<T>(T? value, string property) where T : class
    {
        return value ?? throw Missing(property);
    }

    private static T Require<T>(T? value, string property) where T : struct
    {
        return value ?? throw Missing(property);
    }

    private static HeadlessRequestException Missing(string property)
    {
        return new HeadlessRequestException("missingProperty", $"The '{property}' property is required.");
    }

    private static AtmosChunkHandle Handle(Coordinate position)
    {
        return new AtmosChunkHandle(ToInt3(position));
    }

    private static Int3 ToInt3(Coordinate value)
    {
        return new Int3(value.X, value.Y, value.Z);
    }

    private static Coordinate Copy(Coordinate value)
    {
        return new Coordinate(value.X, value.Y, value.Z);
    }
}

internal sealed record CommandExecution(
    CommandResult? Result = null,
    SimulationStateReport? Observation = null,
    bool ExitRequested = false);