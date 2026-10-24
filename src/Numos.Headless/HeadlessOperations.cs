using System.Globalization;
using System.Text.Json;
using Numos.API;
using Numos.CoreSim;
using Numos.Headless.Diagnostics;
using Numos.Headless.Protocol;
using Numos.Maths;
using Numos.SimDrawer;
using Numos.Workbench;

namespace Numos.Headless;

/// <summary>
///     Runs protocol operations against one <see cref="WorkbenchSession" />.
/// </summary>
/// <remarks>
///     Each operation reads its request properties, calls the session, and shapes the result. The session owns all
///     simulation behavior, so the viewer and this class do the same thing for the same command.
/// </remarks>
internal sealed partial class HeadlessOperations
{
    private const int MaximumTicksPerRequest = 1_000_000;
    private const int MaximumSamplePoints = 100_000;
    private const int VoxelClassificationUnassigned = 0;

    private readonly WorkbenchSession _session;

    internal HeadlessOperations(WorkbenchSession session)
    {
        _session = session;
    }

    internal CommandExecution Execute(HeadlessRequest request)
    {
        string op = request.Op!;
        if (!_session.HasProject && ProjectOperations.Contains(op))
        {
            throw new HeadlessRequestException(
                "simulationNotCreated",
                "Create a project with createProject before running this operation.");
        }

        return Dispatch(op, request);
    }

    private CommandExecution Dispatch(string op, HeadlessRequest request)
    {
        return op switch
        {
            "hello" => Hello(),
            "getLog" => GetLog(request),
            "createProject" => CreateProject(request),
            "closeProject" => CloseProject(),
            "addSimulation" => AddSimulation(request),
            "removeSimulation" => RemoveSimulation(request),
            "selectSimulation" => SelectSimulation(request),
            "listSimulations" => ListSimulations(),
            "addChunk" => AddChunk(request),
            "removeChunk" => RemoveChunk(request),
            "sealChunk" => SealChunk(request),
            "wakeChunk" => WakeChunk(request),
            "sleepChunk" => SleepChunk(request),
            "fillChunk" => FillChunk(request),
            "setVoxelClassification" => SetVoxelClassification(request),
            "setVoxelTemperature" => SetVoxelTemperature(request),
            "injectGas" => InjectGas(request),
            "clearVoxelGas" => ClearVoxelGas(request),
            "editRegion" => EditRegion(request),
            "sampleVoxels" => SampleVoxels(request),
            "exportField" => ExportField(request),
            "renderSlice" => RenderSlice(request),
            "addGas" => AddGas(request),
            "removeGas" => RemoveGas(request),
            "listGases" => ListGases(),
            "updateConfig" => UpdateConfig(request),
            "getConfig" => GetConfig(),
            "listSolvers" => ListSolvers(),
            "setSolverEnabled" => SetSolverEnabled(request),
            "resetSolvers" => ResetSolvers(),
            "createPortal" => CreatePortal(request),
            "createDock" => CreateDock(request),
            "destroyLinkSet" => DestroyLinkSet(request),
            "listLinkSets" => ListLinkSets(),
            "tick" => Tick(request),
            "stepForward" => StepForward(),
            "seekTick" => SeekTick(request),
            "returnToHead" => ReturnToHead(),
            "createBranch" => CreateBranch(),
            "selectBranch" => SelectBranch(request),
            "continueBranch" => ContinueBranch(),
            "listBranches" => ListBranches(),
            "timelineInfo" => TimelineInformation(),
            "saveReplay" => SaveReplay(request),
            "loadReplay" => LoadReplay(request),
            "observe" => Observe(request),
            "exit" => Exit(),
            _ => throw new HeadlessRequestException(
                "unknownOperation",
                $"Unknown operation '{request.Op}'. See docs/headless_runner.md for supported operations.")
        };
    }

    private static readonly HashSet<string> ProjectOperations =
    [
        "addSimulation", "removeSimulation", "selectSimulation", "listSimulations", "addChunk", "removeChunk",
        "sealChunk", "wakeChunk", "sleepChunk", "fillChunk", "setVoxelClassification", "setVoxelTemperature",
        "injectGas", "clearVoxelGas", "editRegion", "sampleVoxels", "addGas", "removeGas", "listGases",
        "updateConfig", "getConfig", "listSolvers", "setSolverEnabled", "resetSolvers", "createPortal", "createDock",
        "destroyLinkSet", "listLinkSets", "tick", "stepForward", "seekTick", "returnToHead", "createBranch",
        "selectBranch", "continueBranch", "listBranches", "timelineInfo", "saveReplay", "observe", "exportField",
        "renderSlice"
    ];

    internal SessionState? GetState()
    {
        if (!_session.HasProject)
            return null;

        var simulation = _session.ActiveSimulation;
        return new SessionState
        {
            Project = _session.ProjectName!,
            Simulation = simulation == null ? null : _session.GetSimulationName(simulation.Id),
            SimulationId = simulation?.Id.Index,
            SimulationCount = _session.Simulations.Count,
            Tick = _session.World!.TickCount,
            ChunkCount = simulation?.ChunkCount ?? 0,
            GasCount = _session.Config!.GasRegistry.Count,
            Inspecting = _session.IsInspecting
        };
    }

    // Project and simulations.

    private const int DefaultLogEntries = 200;
    private const int MaximumLogEntries = 5000;

    private CommandExecution GetLog(HeadlessRequest request)
    {
        long after = request.AfterSequence ?? 0;
        int maximum = request.MaxEntries ?? DefaultLogEntries;
        if (maximum < 1 || maximum > MaximumLogEntries)
        {
            throw new HeadlessRequestException(
                "invalidProperty",
                $"maxEntries must be from 1 to {MaximumLogEntries}.");
        }

        var minimum = request.MinLevel switch
        {
            null => WorkbenchLogLevel.Debug,
            "debug" => WorkbenchLogLevel.Debug,
            "info" => WorkbenchLogLevel.Info,
            "warn" => WorkbenchLogLevel.Warn,
            "error" => WorkbenchLogLevel.Error,
            "fatal" => WorkbenchLogLevel.Fatal,
            _ => throw new HeadlessRequestException(
                "invalidProperty",
                "minLevel must be debug, info, warn, error, or fatal.")
        };

        var snapshot = _session.Log.Snapshot();
        var entries = new List<LogEntryInfo>();
        bool more = false;
        foreach (var entry in snapshot)
        {
            if (entry.Sequence <= after || entry.Level < minimum)
                continue;

            if (entries.Count == maximum)
            {
                more = true;
                break;
            }

            entries.Add(new LogEntryInfo
            {
                Sequence = entry.Sequence,
                Timestamp = entry.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                Level = entry.Level.ToString().ToLowerInvariant(),
                Tick = entry.Tick,
                Source = entry.Source,
                Message = entry.Message
            });
        }

        return Result(new CommandResult
        {
            Log = new LogInfo
            {
                Entries = [.. entries],
                LatestSequence = snapshot.Length == 0 ? 0 : snapshot[^1].Sequence,
                More = more
            }
        });
    }

    private CommandExecution Hello()
    {
        return Result(new CommandResult
        {
            Hello = new HelloInfo
            {
                Protocol = HeadlessCommandHost.ProtocolVersion,
                Host = _session.Host.Name,
                HostVersion = _session.Host.Version,
                CoreSimVersion = CoreSimBuildInfo.PackageVersion
            }
        });
    }

    private CommandExecution CreateProject(HeadlessRequest request)
    {
        var dimensions = Require(request.ChunkDimensions, "chunkDimensions");
        var options = new WorkbenchProjectOptions(
            request.Name,
            dimensions.X,
            dimensions.Y,
            dimensions.Z,
            request.IncludeDefaultGases ?? false);

        _session.CreateProject(
            options,
            config =>
            {
                request.Config?.Validate();
                request.Config?.ApplyTo(config);
                if (request.Gases == null)
                    return;

                foreach (var gas in request.Gases)
                {
                    if (gas == null)
                        throw new HeadlessRequestException("invalidGas", "The gases array cannot contain null entries.");

                    config.GasRegistry.Add(gas.ToGasProperties());
                }
            });

        return new CommandExecution();
    }

    private CommandExecution CloseProject()
    {
        _session.CloseProject();
        return new CommandExecution();
    }

    private CommandExecution AddSimulation(HeadlessRequest request)
    {
        var dimensions = Require(request.ChunkDimensions, "chunkDimensions");
        var simulation = _session.AddSimulation(
            dimensions.X,
            dimensions.Y,
            dimensions.Z,
            request.Name,
            request.Activate ?? true);

        return Result(new CommandResult
        {
            SimulationId = simulation.Id.Index,
            SimulationName = _session.GetSimulationName(simulation.Id)
        });
    }

    private CommandExecution RemoveSimulation(HeadlessRequest request)
    {
        var id = RequireSimulationId(request.Simulation);
        bool removed = _session.RemoveSimulation(id);
        return Result(new CommandResult { Changed = removed });
    }

    private CommandExecution SelectSimulation(HeadlessRequest request)
    {
        var id = RequireSimulationId(request.Simulation);
        _session.SetActiveSimulation(id);
        return Result(new CommandResult
        {
            SimulationId = id.Index,
            SimulationName = _session.GetSimulationName(id)
        });
    }

    private CommandExecution ListSimulations()
    {
        var simulations = _session.Simulations
            .Select(simulation => new SimulationInfo
            {
                Id = simulation.Id.Index,
                Name = _session.GetSimulationName(simulation.Id),
                Active = simulation.Id == _session.ActiveSimulationId,
                ChunkDimensions = Coordinate.From(simulation.ChunkDimensions),
                ChunkCount = simulation.ChunkCount
            })
            .ToArray();

        return Result(new CommandResult { Simulations = simulations });
    }

    // Chunks and voxels.

    private CommandExecution AddChunk(HeadlessRequest request)
    {
        var position = Require(request.Position, "position");
        _session.AddChunk(
            position.ToInt3(),
            request.Classification ?? VoxelClassificationUnassigned,
            ResolveSimulation(request.Simulation));

        return Result(new CommandResult { Position = position });
    }

    private CommandExecution RemoveChunk(HeadlessRequest request)
    {
        var position = Require(request.Position, "position");
        bool changed = _session.RemoveChunk(position.ToInt3(), ResolveSimulation(request.Simulation));
        return Result(new CommandResult { Position = position, Changed = changed });
    }

    private CommandExecution SealChunk(HeadlessRequest request)
    {
        var position = Require(request.Position, "position");
        _session.SealChunk(position.ToInt3(), ResolveSimulation(request.Simulation));
        return Result(new CommandResult { Position = position });
    }

    private CommandExecution WakeChunk(HeadlessRequest request)
    {
        _session.WakeChunk(Require(request.Position, "position").ToInt3(), ResolveSimulation(request.Simulation));
        return new CommandExecution();
    }

    private CommandExecution SleepChunk(HeadlessRequest request)
    {
        _session.SleepChunk(Require(request.Position, "position").ToInt3(), ResolveSimulation(request.Simulation));
        return new CommandExecution();
    }

    private CommandExecution FillChunk(HeadlessRequest request)
    {
        var position = Require(request.Position, "position");
        int classification = Require(request.Classification, "classification");
        _session.FillChunk(position.ToInt3(), classification, ResolveSimulation(request.Simulation));
        return Result(new CommandResult { Position = position });
    }

    private VoxelCell Cell(HeadlessRequest request)
    {
        var position = Require(request.Position, "position");
        var voxel = Require(request.Voxel, "voxel");
        return _session.GetCell(
            position.ToInt3(),
            voxel.X,
            voxel.Y,
            voxel.Z,
            ResolveSimulation(request.Simulation));
    }

    private CommandExecution SetVoxelClassification(HeadlessRequest request)
    {
        var cell = Cell(request);
        _session.SetVoxelClassification(cell, Require(request.Classification, "classification"), ResolveSimulation(request.Simulation));
        return new CommandExecution();
    }

    private CommandExecution SetVoxelTemperature(HeadlessRequest request)
    {
        var cell = Cell(request);
        _session.SetVoxelTemperature(cell, Require(request.TemperatureK, "temperatureK"), ResolveSimulation(request.Simulation));
        return new CommandExecution();
    }

    private CommandExecution InjectGas(HeadlessRequest request)
    {
        var cell = Cell(request);
        int gasId = Require(request.GasId, "gasId");
        RequireRegisteredGas(gasId);
        float moles = Require(request.Moles, "moles");
        float temperature = Require(request.TemperatureK, "temperatureK");
        _session.AddVoxelGas(cell, gasId, moles, temperature, ResolveSimulation(request.Simulation));
        return new CommandExecution();
    }

    private CommandExecution ClearVoxelGas(HeadlessRequest request)
    {
        _session.ClearVoxelGas(Cell(request), ResolveSimulation(request.Simulation));
        return new CommandExecution();
    }

    private void RequireRegisteredGas(int gasId)
    {
        if (gasId < 0 || gasId >= _session.Config!.GasRegistry.Count)
            throw new HeadlessRequestException("gasNotFound", $"No gas is registered with ID {gasId}.");
    }

    // Regions and sampling.

    private static VoxelBox Box(HeadlessRequest request)
    {
        var min = Require(request.Min, "min");
        var max = Require(request.Max, "max");
        return new VoxelBox(min.ToInt3(), max.ToInt3());
    }

    private CommandExecution EditRegion(HeadlessRequest request)
    {
        var box = Box(request);
        if (!box.IsValid)
            throw new HeadlessRequestException("invalidRegion", "Region max must be at least min on every axis.");

        RegionGas[] gases = [];
        if (request.Gas is { } gasElement)
        {
            var parsed = gasElement.Deserialize(HeadlessJsonContext.Default.RegionGasRequestArray);
            if (parsed == null)
                throw new HeadlessRequestException("invalidRequest", "The 'gas' property must be an array.");

            gases = parsed.Select(gas => new RegionGas(
                    gas?.GasId ?? throw new HeadlessRequestException("missingProperty", "Every region gas needs a 'gasId'."),
                    gas.Moles,
                    gas.PartialPressurePa))
                .ToArray();
        }

        var edit = new RegionEdit(
            request.CreateChunks ?? false,
            request.Classification,
            request.ClearGas ?? false,
            request.TemperatureK,
            gases,
            request.GasTemperatureK);

        var result = _session.EditRegion(box, edit, ResolveSimulation(request.Simulation));
        return Result(new CommandResult
        {
            Region = new RegionEditInfo
            {
                VoxelsEdited = result.VoxelsEdited,
                VoxelsSkipped = result.VoxelsSkipped,
                ChunksTouched = result.ChunksTouched,
                ChunksCreated = result.ChunksCreated,
                MolesPerVoxel = result.MolesPerVoxel.ToArray()
            }
        });
    }

    private CommandExecution SampleVoxels(HeadlessRequest request)
    {
        var points = Require(request.Points, "points");
        string[] fields = RequireFields(request);
        if (points.Length > MaximumSamplePoints)
        {
            throw new HeadlessRequestException(
                "invalidRequest",
                $"points can hold at most {MaximumSamplePoints} entries. Use exportField for larger extents.");
        }

        float[] flat = _session.SampleVoxels(
            points.Select(static point => point.ToInt3()).ToArray(),
            fields,
            ResolveSimulation(request.Simulation));

        float[][] rows = new float[points.Length][];
        for (int row = 0; row < rows.Length; row++)
            rows[row] = flat.AsSpan(row * fields.Length, fields.Length).ToArray();

        return Result(new CommandResult
        {
            Samples = new SampleInfo { Fields = fields, Points = points, Values = rows }
        });
    }

    private string[] RequireFields(HeadlessRequest request)
    {
        return RequireFields(request.Fields, "fields");
    }

    private string[] RequireFields(string[]? names, string property)
    {
        string[] fields = Require(names, property);
        if (fields.Length == 0)
            throw new HeadlessRequestException("missingProperty", $"The '{property}' property needs at least one name.");

        foreach (string name in fields)
        {
            if (!_session.Fields.TryResolve(name, out _))
            {
                throw new HeadlessRequestException(
                    "fieldNotFound",
                    $"Unknown field '{name}'. Known fields: {string.Join(", ", _session.Fields.Names)}, moles:<gasId>.");
            }
        }

        return fields;
    }

    private const int MaximumInlineValues = 65_536;

    private static SliceAxis ParseAxis(string? text, string property)
    {
        return (text ?? throw Missing(property)).Trim().ToLowerInvariant() switch
        {
            "x" => SliceAxis.X,
            "y" => SliceAxis.Y,
            "z" => SliceAxis.Z,
            _ => throw new HeadlessRequestException("invalidRequest", $"{property} must be x, y, or z.")
        };
    }

    private CommandExecution ExportField(HeadlessRequest request)
    {
        string[] fields = RequireFields(request);
        var simulation = ResolveSimulation(request.Simulation);
        string format = (request.Format ?? "npy").ToLowerInvariant();
        if (format is not ("npy" or "json"))
            throw new HeadlessRequestException("invalidRequest", "format must be npy or json.");

        if ((request.Region == null) == (request.Slice == null))
            throw new HeadlessRequestException("invalidRequest", "Give exactly one of region and slice.");

        string? path = format == "npy" ? RequirePath(request) : null;
        IReadOnlyList<FieldExport> exports;
        if (request.Region is { } region)
        {
            var box = new VoxelBox(
                Require(region.Min, "region.min").ToInt3(),
                Require(region.Max, "region.max").ToInt3());

            if (!box.IsValid)
                throw new HeadlessRequestException("invalidRegion", "Region max must be at least min on every axis.");

            CheckInlineSize(format, box.VoxelCount);
            exports = path == null
                ? _session.ReadRegion(box, fields, simulation)
                : _session.ExportRegion(box, fields, path, simulation);
        }
        else
        {
            var slice = request.Slice!;
            var axis = ParseAxis(slice.Axis, "slice.axis");
            int index = Require(slice.Index, "slice.index");
            if (path == null)
            {
                var box = _session.GetSliceBox(axis, index, simulation);
                CheckInlineSize(format, box.VoxelCount);
                exports = _session.ReadRegion(box, fields, simulation);
                int[] shape = WorkbenchSession.GetSliceShape(axis, box);
                exports = exports.Select(export => export with { Shape = shape }).ToArray();
            }
            else
            {
                exports = _session.ExportSlice(axis, index, fields, path, simulation);
            }
        }

        return Result(new CommandResult
        {
            Export = new ExportInfo
            {
                Format = format,
                Files = exports
                    .Select(export => new ExportFileInfo
                    {
                        Field = export.Field,
                        Unit = export.Unit,
                        Path = export.Path,
                        Shape = export.Shape,
                        Min = export.Minimum,
                        Max = export.Maximum,
                        Values = format == "json" ? export.Values : null
                    })
                    .ToArray()
            }
        });
    }

    private static void CheckInlineSize(string format, long voxelCount)
    {
        if (format == "json" && voxelCount > MaximumInlineValues)
        {
            throw new HeadlessRequestException(
                "invalidRequest",
                $"format json holds at most {MaximumInlineValues} values per field. Use npy for larger extents.");
        }
    }

    private CommandExecution RenderSlice(HeadlessRequest request)
    {
        var axis = ParseAxis(request.Axis, "axis");
        int index = Require(request.Index, "index");
        string path = RequirePath(request);
        int scale = request.Scale ?? 1;
        if (scale < 1)
            throw new HeadlessRequestException("invalidRequest", "scale must be at least 1.");

        VisualizationRange? range = null;
        if (request.Range is { } requested)
        {
            float minimum = Require(requested.Min, "range.min");
            float maximum = Require(requested.Max, "range.max");
            if (!float.IsFinite(minimum) || !float.IsFinite(maximum) || maximum <= minimum)
                throw new HeadlessRequestException("invalidRequest", "range must be finite with max above min.");

            range = new VisualizationRange(minimum, maximum, request.Levels ?? 256);
        }

        var image = _session.RenderSlice(
            axis,
            index,
            request.Visualization ?? BuiltInVisualizationIds.Pressure,
            range,
            scale,
            request.Levels ?? 256,
            ResolveSimulation(request.Simulation));

        string fullPath = System.IO.Path.GetFullPath(path);
        PngEncoder.WriteFile(fullPath, image.Width, image.Height, image.Rgba);
        return Result(new CommandResult
        {
            Render = new RenderInfo
            {
                Path = fullPath,
                Width = image.Width,
                Height = image.Height,
                RangeMin = image.RangeMinimum,
                RangeMax = image.RangeMaximum
            }
        });
    }

    // Gases and configuration.

    private CommandExecution AddGas(HeadlessRequest request)
    {
        var element = Require(request.Gas, "gas");
        var definition = element.Deserialize(HeadlessJsonContext.Default.GasDefinition) ??
                         throw new HeadlessRequestException("invalidGas", "The 'gas' property must be an object.");

        int gasId = _session.AddGas(definition.ToGasProperties());
        return Result(new CommandResult { GasId = gasId });
    }

    private CommandExecution RemoveGas(HeadlessRequest request)
    {
        int gasId = Require(request.GasId, "gasId");
        RequireRegisteredGas(gasId);
        _session.RemoveGas(gasId);
        return new CommandExecution();
    }

    private CommandExecution ListGases()
    {
        return Result(new CommandResult { Gases = SimulationStateAnalyzer.CreateConfigurationReport(_session.Config!).Gases });
    }

    private CommandExecution UpdateConfig(HeadlessRequest request)
    {
        var patch = Require(request.Config, "config");
        patch.Validate();
        _session.ApplyConfigChange(patch.ApplyTo, "apply the configuration patch");
        return new CommandExecution();
    }

    private CommandExecution GetConfig()
    {
        return Result(new CommandResult { Config = SimulationStateAnalyzer.CreateConfigurationReport(_session.Config!) });
    }

    // Solvers and topology.

    private CommandExecution ListSolvers()
    {
        return Result(new CommandResult { Solvers = SolverReports() });
    }

    private SolverStepReport[] SolverReports()
    {
        return _session.GetSolverSteps()
            .Select(static step => new SolverStepReport(
                step.Name,
                step.IsEnabled,
                step.Kind == AtmosWorldSolverKind.BuiltIn ? "builtIn" : "custom"))
            .ToArray();
    }

    private CommandExecution SetSolverEnabled(HeadlessRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Solver))
            throw Missing("solver");

        bool enabled = Require(request.Enabled, "enabled");
        if (!_session.SetSolverEnabled(request.Solver, enabled))
        {
            throw new HeadlessRequestException(
                "solverNotFound",
                $"No solver stage named '{request.Solver}' is registered.");
        }

        return Result(new CommandResult { Changed = true });
    }

    private CommandExecution ResetSolvers()
    {
        bool full = _session.ResetSolvers();
        return Result(new CommandResult { Changed = full });
    }

    private AtmosCellRef CellRef(LinkEndpoint endpoint, string property)
    {
        var simulationId = ResolveSimulation(endpoint.Simulation) ??
                           _session.ActiveSimulationId ??
                           throw new HeadlessRequestException("simulationNotCreated", "The project has no active simulation.");

        var position = Require(endpoint.Position, property + ".position");
        var voxel = Require(endpoint.Voxel, property + ".voxel");
        var cell = _session.GetCell(position.ToInt3(), voxel.X, voxel.Y, voxel.Z, simulationId);
        return new AtmosCellRef(simulationId, new AtmosChunkHandle(cell.ChunkPosition), cell.LocalIndex);
    }

    private static ExplicitLinkFlags ParseFlags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ExplicitLinkFlags.All;

        return text.Trim().ToLowerInvariant() switch
        {
            "all" => ExplicitLinkFlags.All,
            "gas" => ExplicitLinkFlags.GasTransport,
            "thermal" => ExplicitLinkFlags.ThermalTransport,
            "none" => ExplicitLinkFlags.None,
            _ => throw new HeadlessRequestException(
                "invalidRequest",
                $"flags must be one of all, gas, thermal, none. Received '{text}'.")
        };
    }

    private CommandExecution CreatePortal(HeadlessRequest request)
    {
        var first = CellRef(Require(request.First, "first"), "first");
        var second = CellRef(Require(request.Second, "second"), "second");
        var portal = _session.CreatePortal(first, second, ParseFlags(request.Flags));
        return Result(new CommandResult { LinkSet = ToInfo(portal.Links) });
    }

    private DockEndpoint DockEnd(LinkEndpoint endpoint, string property)
    {
        var simulationId = ResolveSimulation(endpoint.Simulation) ??
                           _session.ActiveSimulationId ??
                           throw new HeadlessRequestException("simulationNotCreated", "The project has no active simulation.");

        var position = Require(endpoint.Position, property + ".position");
        string faceText = endpoint.Face ?? throw Missing(property + ".face");
        if (!Enum.TryParse<DockFace>(faceText, true, out var face))
        {
            throw new HeadlessRequestException(
                "invalidRequest",
                $"{property}.face must be one of negativeX, positiveX, negativeY, positiveY, negativeZ, positiveZ.");
        }

        return new DockEndpoint(simulationId, position.ToInt3(), face);
    }

    private CommandExecution CreateDock(HeadlessRequest request)
    {
        var dock = new DockDefinition(
            DockEnd(Require(request.First, "first"), "first"),
            DockEnd(Require(request.Second, "second"), "second"),
            request.Rotation ?? 0,
            request.FlipU ?? false,
            request.FlipV ?? false,
            ParseFlags(request.Flags));

        var handle = _session.CreateDock(dock);
        return Result(new CommandResult { LinkSet = ToInfo(handle.Links) });
    }

    private CommandExecution DestroyLinkSet(HeadlessRequest request)
    {
        var handle = Require(request.LinkSet, "linkSet");
        _session.DestroyLinkSet(new ExplicitLinkSetHandle(
            Require(handle.Index, "linkSet.index"),
            Require(handle.Generation, "linkSet.generation")));

        return new CommandExecution();
    }

    private CommandExecution ListLinkSets()
    {
        var sets = _session.GetLinkSets()
            .Select(set => new LinkSetInfo
            {
                Handle = ToInfo(set.Handle),
                Kind = set.Kind.ToString(),
                State = set.State.ToString(),
                LinkCount = set.Links.Count,
                Flags = set.Links.Count == 0 ? ExplicitLinkFlags.None.ToString() : set.Links[0].Flags.ToString()
            })
            .ToArray();

        return Result(new CommandResult { LinkSets = sets });
    }

    private static LinkSetHandleInfo ToInfo(ExplicitLinkSetHandle handle)
    {
        return new LinkSetHandleInfo { Index = handle.Index, Generation = handle.Generation };
    }

    // Time and timeline.

    private CommandExecution Tick(HeadlessRequest request)
    {
        int count = Require(request.Count, "count");
        if (count <= 0 || count > MaximumTicksPerRequest)
        {
            throw new HeadlessRequestException(
                "invalidTickCount",
                $"count must be between 1 and {MaximumTicksPerRequest}.");
        }

        int progressInterval = request.ProgressInterval ?? 100;
        if (request.Probes is not { } probes)
        {
            _session.Tick(count, progressInterval);
            return Result(new CommandResult { TicksExecuted = count });
        }

        int every = probes.Every ?? 1;
        if (every < 1)
            throw new HeadlessRequestException("invalidRequest", "probes.every must be at least 1.");

        string[] fields = RequireFields(probes.Fields, "probes.fields");
        var points = Require(probes.Points, "probes.points")
            .Select(point => new ProbePoint(
                point?.Name ?? throw Missing("probes.points.name"),
                Require(point.Voxel, "probes.points.voxel").ToInt3()))
            .ToArray();

        if (points.Length == 0)
            throw new HeadlessRequestException("missingProperty", "probes.points needs at least one entry.");

        using var recorder = _session.CreateProbeRecorder(
            points,
            fields,
            RequirePath(probes.Path, "probes.path"),
            ResolveSimulation(request.Simulation));

        recorder.Record();
        _session.Tick(
            count,
            progressInterval,
            done =>
            {
                if (done % every == 0 || done == count)
                    recorder.Record();
            });

        return Result(new CommandResult
        {
            TicksExecuted = count,
            Probes = new ProbeInfo
            {
                Path = recorder.Path,
                Rows = recorder.Rows,
                Columns = 2 + points.Length * fields.Length
            }
        });
    }

    private CommandExecution StepForward()
    {
        var moved = _session.StepForward();
        return Result(new CommandResult { Moved = moved.ToString() });
    }

    private CommandExecution SeekTick(HeadlessRequest request)
    {
        long tick = Require(request.Tick, "tick");
        if (tick < 0)
            throw new HeadlessRequestException("invalidRequest", "tick must be at least 0.");

        _session.SeekTick((ulong)tick);
        return new CommandExecution();
    }

    private CommandExecution ReturnToHead()
    {
        _session.ReturnToHead();
        return new CommandExecution();
    }

    private CommandExecution CreateBranch()
    {
        var branch = _session.CreateBranch();
        return Result(new CommandResult { Branch = ToInfo(branch) });
    }

    private CommandExecution SelectBranch(HeadlessRequest request)
    {
        int id = Require(request.BranchId, "branchId");
        if (_session.Branches!.Branches.All(branch => branch.Id != id))
            throw new HeadlessRequestException("branchNotFound", $"No branch has ID {id}.");

        _session.SelectBranch(id, request.Tick is { } tick && tick >= 0 ? (ulong)tick : null);
        return new CommandExecution();
    }

    private CommandExecution ContinueBranch()
    {
        _session.ContinueBranch();
        return new CommandExecution();
    }

    private CommandExecution ListBranches()
    {
        return Result(new CommandResult { Branches = BranchInfos() });
    }

    private BranchInfo[] BranchInfos()
    {
        return _session.Branches!.Branches.Select(ToInfo).ToArray();
    }

    private static BranchInfo ToInfo(ReplayBranchInfo branch)
    {
        return new BranchInfo
        {
            Id = branch.Id,
            Name = branch.Name,
            ParentId = branch.ParentId,
            ForkTick = branch.Fork.Tick,
            HeadTick = branch.Head.Tick,
            Selected = branch.IsSelected
        };
    }

    private CommandExecution TimelineInformation()
    {
        var timeline = _session.Timeline!;
        var info = new TimelineInfo
        {
            StartTick = timeline.Start.Tick,
            HeadTick = timeline.Head.Tick,
            PositionTick = timeline.Position.Tick,
            Inspecting = timeline.IsInspecting,
            Verified = timeline.IsVerified,
            SelectedBranchId = _session.Branches!.SelectedBranchId,
            Branches = BranchInfos(),
            Checkpoints = timeline.Checkpoints
                .Select(static point => new CheckpointInfo
                {
                    Tick = point.Hash.Position.Tick,
                    Hash = FormatHash(point.Hash.Digest)
                })
                .ToArray(),
            StateHash = FormatHash(_session.World!.ComputeStateHash().Digest)
        };

        return Result(new CommandResult { Timeline = info });
    }

    private static string FormatHash(ulong digest)
    {
        return digest.ToString("x16", CultureInfo.InvariantCulture);
    }

    // Replay files and observation.

    private CommandExecution SaveReplay(HeadlessRequest request)
    {
        string path = RequirePath(request);
        string saved = _session.SaveReplay(
            path,
            request.ThroughCurrentPosition ?? false,
            request.Overwrite ?? false);

        return Result(new CommandResult { Path = saved });
    }

    private CommandExecution LoadReplay(HeadlessRequest request)
    {
        string path = RequirePath(request);
        var loaded = _session.LoadReplay(path);
        try
        {
            _session.InstallReplay(loaded, null, request.AtHead ?? false);
        }
        catch
        {
            loaded.Dispose();
            throw;
        }

        return Result(new CommandResult { Path = loaded.Path });
    }

    private static string RequirePath(HeadlessRequest request)
    {
        return RequirePath(request.Path, "path");
    }

    private static string RequirePath(string? path, string property)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw Missing(property);

        return path;
    }

    private CommandExecution Observe(HeadlessRequest request)
    {
        var simulation = RequireTarget(request.Simulation);
        if (request.Voxel != null && request.Position == null)
            throw new HeadlessRequestException("missingProperty", "voxel requires a chunk position.");

        var options = new SimulationObservationOptions
        {
            Chunk = request.Position,
            Voxels = request.Voxel == null
                ? null
                : [new VoxelSelection(request.Position!, request.Voxel)],
            // A local voxel is an exact probe. It takes precedence over a simultaneous dense-output flag.
            IncludeVoxels = request.Voxel == null && (request.IncludeVoxels ?? false),
            OnlyGasBearingVoxels = request.OnlyGasBearingVoxels ?? false,
            MaxIssueLocations = request.MaxIssueLocations ?? SimulationObservationOptions.DefaultMaxIssueLocations
        };

        var observation = SimulationStateAnalyzer.Analyze(simulation, _session.Config!, options);
        return new CommandExecution(
            new CommandResult { StateHash = FormatHash(_session.World!.ComputeStateHash().Digest) },
            observation);
    }

    private CommandExecution Exit()
    {
        _session.CloseProject();
        return new CommandExecution(ExitRequested: true);
    }

    // Shared helpers.

    private static CommandExecution Result(CommandResult result)
    {
        return new CommandExecution(result);
    }

    private AtmosSimulationId? ResolveSimulation(JsonElement? element)
    {
        if (element is not { } value || value.ValueKind == JsonValueKind.Null)
            return null;

        AtmosSimulation? match = null;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetInt32(out int index):
                match = _session.Simulations.FirstOrDefault(simulation => simulation.Id.Index == index);
                break;
            case JsonValueKind.String:
                string name = value.GetString()!;
                match = _session.FindSimulation(name);
                if (match == null && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
                    match = _session.Simulations.FirstOrDefault(simulation => simulation.Id.Index == parsed);

                break;
            default:
                throw new HeadlessRequestException("invalidRequest", "simulation must be a name or a numeric id.");
        }

        return match?.Id ??
               throw new HeadlessRequestException("simulationNotFound", $"No simulation matches {value}.");
    }

    private AtmosSimulationId RequireSimulationId(JsonElement? element)
    {
        return ResolveSimulation(element) ?? throw Missing("simulation");
    }

    private AtmosSimulation RequireTarget(JsonElement? element)
    {
        var id = ResolveSimulation(element) ?? _session.ActiveSimulationId;
        if (id is { } value && _session.TryGetSimulation(value, out var simulation))
            return simulation!;

        throw new HeadlessRequestException("simulationNotCreated", "The project has no active simulation.");
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
}

internal sealed record CommandExecution(
    CommandResult? Result = null,
    SimulationStateReport? Observation = null,
    bool ExitRequested = false);
