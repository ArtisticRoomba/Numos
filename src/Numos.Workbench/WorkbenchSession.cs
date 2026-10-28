using Numos.API;
using Numos.CoreSim;
using Numos.Maths;
using Numos.SimDrawer;

namespace Numos.Workbench;

/// <summary>
///     Options for <see cref="WorkbenchSession.CreateProject" />.
/// </summary>
/// <param name="Name">Project name. A blank name becomes `Untitled Simulation`.</param>
/// <param name="ChunkWidth">Voxels per chunk along X for the first simulation.</param>
/// <param name="ChunkHeight">Voxels per chunk along Y for the first simulation.</param>
/// <param name="ChunkDepth">Voxels per chunk along Z for the first simulation.</param>
/// <param name="IncludeDefaultGases">Whether the gas registry starts with <see cref="DefaultGases" />.</param>
/// <param name="TransportModel">
///     The preset that fills the initial stage list. <see cref="WorkbenchSession.ApplyTransportPreset" /> and
///     <see cref="WorkbenchSession.SetSolverLayout" /> change the stages later, and the timeline records each change.
/// </param>
public readonly record struct WorkbenchProjectOptions(
    string? Name,
    int ChunkWidth = AtmosChunkConstants.DefaultWidth,
    int ChunkHeight = AtmosChunkConstants.DefaultHeight,
    int ChunkDepth = 1,
    bool IncludeDefaultGases = true,
    AtmosTransportModel TransportModel = AtmosTransportModel.Euler);

/// <summary>
///     Names the program that hosts a session. The session writes it to the log banner and to saved replays.
/// </summary>
/// <param name="Name">Program name, such as `Numos.Viewer`.</param>
/// <param name="Version">Program version.</param>
public readonly record struct WorkbenchHostInfo(string Name, string Version);

/// <summary>
///     Thrown when the world rejects an <see cref="AtmosConfig" /> change.
/// </summary>
/// <remarks>
///     The session restores the previous configuration before it throws.
/// </remarks>
public sealed class ConfigurationRejectedException : InvalidOperationException
{
    /// <summary>
    ///     Creates the exception.
    /// </summary>
    /// <param name="message">The complete message for the operation that failed.</param>
    /// <param name="reason">Why the world rejected the configuration.</param>
    public ConfigurationRejectedException(string message, string reason)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>
    ///     Why the world rejected the configuration.
    /// </summary>
    public string Reason { get; }
}

/// <summary>
///     One UI-free simulation session: the world, its configuration, the replay timeline, and every operation a
///     front end can run on them.
/// </summary>
/// <remarks>
///     <para>
///         The viewer and the headless runner share this class, so both do the same thing for the same command.
///         A session holds at most one project. Operations that need a project throw
///         <see cref="InvalidOperationException" /> when none is open.
///     </para>
///     <para>
///         Mutations that run on the live head throw <see cref="InvalidOperationException" /> while the timeline
///         inspects history, because they would be lost or recorded at the wrong position.
///     </para>
///     <para>
///         A session has no locking. Call it from one thread. <see cref="LoadReplay" /> is the exception and can run
///         on a worker thread.
///     </para>
/// </remarks>
public sealed partial class WorkbenchSession : IDisposable
{
    private const string UntitledProjectName = "Untitled Simulation";
    private const string LogSource = "Simulation";

    private readonly Dictionary<AtmosSimulationId, string> _simulationNames = [];
    private Int3 _chunkDimensions;
    private bool _disposed;

    /// <summary>
    ///     Creates a session without a project and writes the startup banner to the log.
    /// </summary>
    /// <param name="log">The log to write to, or <see langword="null" /> to create a private one.</param>
    /// <param name="host">The hosting program, or <see langword="null" /> for an unnamed host.</param>
    public WorkbenchSession(WorkbenchLog? log = null, WorkbenchHostInfo? host = null)
    {
        Log = log ?? new WorkbenchLog();
        Fields.RegisterFlowFields(RequireConfig);
        Host = host ?? new WorkbenchHostInfo("Numos.Workbench", CoreSimBuildInfo.PackageVersion);
        string commit = CoreSimBuildInfo.GitCommit is { Length: > 0 } and not "unknown"
            ? CoreSimBuildInfo.GitCommitShort
            : "unknown commit";

        Info($"{Host.Name} {Host.Version} started. Numos.CoreSim {CoreSimBuildInfo.PackageVersion} ({commit}).");
    }

    /// <summary>
    ///     The hosting program.
    /// </summary>
    public WorkbenchHostInfo Host { get; }

    /// <summary>
    ///     The log that receives session messages.
    /// </summary>
    public WorkbenchLog Log { get; }

    /// <summary>
    ///     Whether a project is open.
    /// </summary>
    public bool HasProject => World != null;

    /// <summary>
    ///     The project's world, or <see langword="null" /> when no project is open.
    /// </summary>
    /// <remarks>
    ///     The session disposes the world when the project closes. Do not keep a reference past that point.
    /// </remarks>
    public AtmosWorld? World { get; private set; }

    /// <summary>
    ///     The editable configuration, or <see langword="null" /> when no project is open.
    /// </summary>
    /// <remarks>
    ///     This instance stays the same for the life of the project. Presentation code can hold it. After a timeline
    ///     move the session copies the restored world values into it. Edits take effect only after
    ///     <see cref="TryApplyConfiguration" /> or <see cref="ApplyConfiguration" />.
    /// </remarks>
    public AtmosConfig? Config { get; private set; }

    /// <summary>
    ///     The project name, or <see langword="null" /> when no project is open.
    /// </summary>
    public string? ProjectName { get; private set; }

    /// <summary>
    ///     The chunk dimensions of the active simulation. The last known value stays after the active simulation goes
    ///     away, and the value is the default when no project is open.
    /// </summary>
    public Int3 ChunkDimensions => _chunkDimensions;

    /// <summary>
    ///     The simulations of the open project in id order, or an empty list.
    /// </summary>
    public IReadOnlyList<AtmosSimulation> Simulations => World?.Simulations ?? [];

    /// <summary>
    ///     The id of the active simulation, or <see langword="null" />.
    /// </summary>
    public AtmosSimulationId? ActiveSimulationId { get; private set; }

    /// <summary>
    ///     The active simulation, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     Chunk, voxel, and cell operations use this simulation unless they take an explicit id.
    /// </remarks>
    public AtmosSimulation? ActiveSimulation =>
        ActiveSimulationId is { } id && World != null && World.TryGetSimulation(id, out var simulation)
            ? simulation
            : null;

    /// <summary>
    ///     Raised after a new project is ready and just before the old one is disposed.
    /// </summary>
    /// <remarks>
    ///     Also raised by <see cref="CloseProject" /> and <see cref="Dispose" />. The old world is still alive during
    ///     the call, so a front end can save per-simulation presentation state.
    /// </remarks>
    public event Action? ProjectClosing;

    /// <summary>
    ///     Creates a project and replaces the open one.
    /// </summary>
    /// <param name="options">Name, first chunk dimensions, and default gases.</param>
    /// <param name="prepare">
    ///     Runs against the new configuration before the old project is closed. The world applies the edits it makes
    ///     to the configuration. If it throws, the old project stays open.
    /// </param>
    /// <exception cref="ArgumentException">The chunk dimensions are invalid.</exception>
    public void CreateProject(WorkbenchProjectOptions options, Action<AtmosConfig>? prepare = null)
    {
        ThrowIfDisposed();
        var config = new AtmosConfig();
        if (options.IncludeDefaultGases)
            DefaultGases.Apply(config);

        AtmosWorld? world = null;
        try
        {
            world = new AtmosWorld(config, options.TransportModel);
            var simulation = world.CreateSimulation(options.ChunkWidth, options.ChunkHeight, options.ChunkDepth);
            if (prepare != null)
            {
                prepare(config);
                world.SetAtmosConfig(config);
            }

            string name = string.IsNullOrWhiteSpace(options.Name) ? UntitledProjectName : options.Name.Trim();
            Open(world, config, name, new AtmosWorldReplayTimeline(world), simulation);
            world = null;
            Info(
                $"Created project '{ProjectName}' with chunk size {options.ChunkWidth} x {options.ChunkHeight} x " +
                $"{options.ChunkDepth} and {config.GasRegistry.Count} gases.");
        }
        catch (Exception exception)
        {
            Failed("Could not create the project", exception);
            throw;
        }
        finally
        {
            world?.Dispose();
        }
    }

    /// <summary>
    ///     Closes the open project and disposes its world. Does nothing when no project is open.
    /// </summary>
    public void CloseProject()
    {
        ThrowIfDisposed();
        if (World == null)
            return;

        string name = ProjectName ?? UntitledProjectName;
        Reset();
        Info($"Closed project '{name}'.");
    }

    /// <summary>
    ///     Closes the open project. The session cannot be used afterwards.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        Reset();
        _disposed = true;
    }

    private void Open(
        AtmosWorld world,
        AtmosConfig config,
        string name,
        AtmosWorldReplayTimeline timeline,
        AtmosSimulation? firstSimulation)
    {
        Reset();
        World = world;
        Config = config;
        ProjectName = name;
        Branches = new ReplayBranchSession(world, timeline);
        PlaybackElapsed = 0f;
        TimelineRevision++;
        ReconcileSimulations();
        if (firstSimulation != null)
            SetActiveSimulation(firstSimulation.Id);
    }

    private void Reset()
    {
        if (World == null)
            return;

        ProjectClosing?.Invoke();
        World.Dispose();
        World = null;
        Config = null;
        ProjectName = null;
        Branches = null;
        ActiveSimulationId = null;
        PlaybackElapsed = 0f;
        _chunkDimensions = default;
        _simulationNames.Clear();
        TimelineRevision++;
    }

    private void Info(string message)
    {
        Log.Write(WorkbenchLogLevel.Info, LogSource, message, null, World?.TickCount);
    }

    private void Warn(string message)
    {
        Log.Write(WorkbenchLogLevel.Warn, LogSource, message, null, World?.TickCount);
    }

    private void Failed(string action, Exception exception)
    {
        Log.Write(
            WorkbenchLogLevel.Error,
            LogSource,
            $"{action}: {exception.Message}",
            exception.ToString(),
            World?.TickCount);
    }

    /// <summary>
    ///     Runs <paramref name="body" />, logs a failure under <paramref name="action" />, and rethrows it.
    /// </summary>
    private void Guard(string action, Action body)
    {
        try
        {
            body();
        }
        catch (Exception exception)
        {
            Failed(action, exception);
            throw;
        }
    }

    private T Guard<T>(string action, Func<T> body)
    {
        try
        {
            return body();
        }
        catch (Exception exception)
        {
            Failed(action, exception);
            throw;
        }
    }

    private AtmosWorld RequireWorld()
    {
        ThrowIfDisposed();
        return World ?? throw new InvalidOperationException("No project is open.");
    }

    internal AtmosConfig RequireConfig()
    {
        RequireWorld();
        return Config!;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void ThrowIfInspecting()
    {
        if (Timeline?.IsInspecting == true)
        {
            throw new InvalidOperationException(
                "Edits are blocked while the timeline inspects history. Return to the head and continue the branch first.");
        }
    }

    private AtmosSimulation RequireSimulation(AtmosSimulationId? id)
    {
        var world = RequireWorld();
        AtmosSimulationId? resolved = id ?? ActiveSimulationId;
        if (resolved is not { } value)
            throw new InvalidOperationException("The project has no active simulation.");

        if (!world.TryGetSimulation(value, out var simulation))
            throw new ArgumentException($"Simulation {value.Index} does not exist.", nameof(id));

        return simulation!;
    }
}
