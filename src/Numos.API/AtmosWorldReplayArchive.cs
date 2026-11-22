using Numos.CoreSim;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;
using System.Collections.ObjectModel;
using Numos.CoreSim.Replay;
using Numos.CoreSim.Solvers;

namespace Numos.API;

/// <summary>
///     Contains a complete initial world state and globally ordered operations needed to reconstruct it.
/// </summary>
public sealed class AtmosWorldReplayArchive
{
    // Scanning every chunk and operation is only needed when something asks, which is mostly saving and importing.
    // The catalogs only grow, so the solver and component parts of UnsupportedFeatures are checked again on every read.
    private IReadOnlyList<string>? _hostStateFeatures;
    private IReadOnlyList<string>? _requiredSolvers;
    private IReadOnlyList<string>? _requiredSimulationComponents;

    /// <summary>
    ///     Creates a detached world replay archive independent of any storage format.
    /// </summary>
    /// <param name="initialCheckpoint">Authoritative world continuation state at the recording start.</param>
    /// <param name="recording">Globally ordered operations and the interval they cover.</param>
    /// <param name="initialStateHash">Reference digest for <paramref name="initialCheckpoint" />.</param>
    /// <param name="headStateHash">Reference digest for the state at the recording head.</param>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">Checkpoint, recording, or hashes describe different positions.</exception>
    public AtmosWorldReplayArchive(
        AtmosWorldCheckpoint initialCheckpoint,
        AtmosWorldRecording recording,
        AtmosWorldStateHash initialStateHash,
        AtmosWorldStateHash headStateHash)
    {
        ArgumentNullException.ThrowIfNull(initialCheckpoint);
        ArgumentNullException.ThrowIfNull(recording);
        if (recording.Start != initialCheckpoint.Position ||
            initialStateHash.Position != recording.Start ||
            headStateHash.Position != recording.Head)
        {
            throw new ArgumentException("World replay state, bounds, and reference hashes must describe the same interval.");
        }

        InitialCheckpoint = initialCheckpoint;
        Recording = recording;
        InitialStateHash = initialStateHash;
        HeadStateHash = headStateHash;
    }

    /// <summary>
    ///     Gets the authoritative initial world continuation state.
    /// </summary>
    public AtmosWorldCheckpoint InitialCheckpoint { get; }

    /// <summary>
    ///     Gets the replay interval and globally ordered semantic operations.
    /// </summary>
    public AtmosWorldRecording Recording { get; }

    /// <summary>
    ///     Gets the expected initial world digest.
    /// </summary>
    public AtmosWorldStateHash InitialStateHash { get; }

    /// <summary>
    ///     Gets the expected world digest at the recording head.
    /// </summary>
    public AtmosWorldStateHash HeadStateHash { get; }

    /// <summary>
    ///     Gets the name of every custom solver stage that replaying this archive needs, in ordinal order.
    /// </summary>
    /// <remarks>
    ///     The list covers the custom stages of <see cref="InitialCheckpoint" /> and every name in a recorded
    ///     <see cref="SetAtmosWorldSolverLayoutOperation" /> that is not a built-in stage. A world that replays the
    ///     archive must have each one registered, or find it in <see cref="AtmosSolverCatalog" />.
    /// </remarks>
    public IReadOnlyList<string> RequiredSolvers => _requiredSolvers ??= FindRequiredSolvers(InitialCheckpoint, Recording);

    /// <summary>
    ///     Gets the key of every simulation component that replaying this archive needs, in ordinal order.
    /// </summary>
    /// <remarks>
    ///     The list covers the keys in the <see cref="AtmosWorldCheckpoint.SimulationComponents" /> of
    ///     <see cref="InitialCheckpoint" />. It also covers the key of every recorded
    ///     <see cref="SetAtmosSimulationComponentStateOperation" /> and
    ///     <see cref="RemoveAtmosSimulationComponentOperation" />. A world that replays the archive creates each
    ///     missing component from <see cref="SimulationComponentCatalog" />, so every key must be registered there.
    /// </remarks>
    public IReadOnlyList<string> RequiredSimulationComponents =>
        _requiredSimulationComponents ??= FindRequiredSimulationComponents(InitialCheckpoint, Recording);

    /// <summary>
    ///     Gets human-readable descriptions of host-defined state that the standard replay file cannot reconstruct.
    ///     Empty when the archive is portable.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A required solver (see <see cref="RequiredSolvers" />) is listed when <see cref="AtmosSolverCatalog" />
    ///         lacks it, because a standalone host then has no way to create it. Its name and any
    ///         <see cref="NumosSerializeAttribute" /> state are still saved. Such an archive can be written to a file
    ///         and loaded back with <see cref="AtmosWorldReplayTimeline.Restore" /> by a host that registers the same
    ///         solvers.
    ///     </para>
    ///     <para>
    ///         A required simulation component (see <see cref="RequiredSimulationComponents" />) is listed when
    ///         <see cref="SimulationComponentCatalog" /> lacks it, because replay then has no class to create it from.
    ///     </para>
    ///     <para>
    ///         Each read checks both catalogs again, so a solver or component whose assembly loads later stops being
    ///         listed.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<string> UnsupportedFeatures
    {
        get
        {
            _hostStateFeatures ??= Array.AsReadOnly(FindUnsupportedHostState(InitialCheckpoint, Recording).ToArray());
            var uncataloged = RequiredSolvers
                .Where(static name => !AtmosSolverCatalog.TryGet(name, out _))
                .Select(static name => $"custom world solver '{name}' (not in the solver catalog)")
                .Concat(RequiredSimulationComponents
                    .Where(static key => !SimulationComponentCatalog.Contains(key))
                    .Select(static key => $"simulation component '{key}' (not in the component catalog)"))
                .ToArray();

            return uncataloged.Length == 0
                ? _hostStateFeatures
                : new ReadOnlyCollection<string>([..uncataloged, .._hostStateFeatures]);
        }
    }

    /// <summary>
    ///     Throws when this archive depends on host-defined solver state.
    /// </summary>
    /// <exception cref="NotSupportedException">
    ///     The archive needs custom solvers that <see cref="AtmosSolverCatalog" /> lacks, simulation components that
    ///     <see cref="SimulationComponentCatalog" /> lacks, solver configurations without
    ///     <see cref="NumosSerializeAttribute" /> members, or rollback chunk fields or recorded chunk-field writes
    ///     whose element type isn't a primitive or an enum.
    /// </exception>
    public void EnsurePortable()
    {
        ThrowIfAny(UnsupportedFeatures, "Portable world replay files do not support the following host-defined state: ");
    }

    /// <summary>
    ///     Throws when this archive holds state the replay file format can't encode. Unlike
    ///     <see cref="EnsurePortable" />, custom solvers are fine here: the file records their names and saved state,
    ///     and leaves supplying their implementations to whoever loads it.
    /// </summary>
    /// <exception cref="NotSupportedException">
    ///     The archive contains solver configurations without <see cref="NumosSerializeAttribute" /> members, or rollback
    ///     chunk fields or recorded chunk-field writes whose element type isn't a primitive or an enum.
    /// </exception>
    internal void EnsureSerializable()
    {
        _hostStateFeatures ??= Array.AsReadOnly(FindUnsupportedHostState(InitialCheckpoint, Recording).ToArray());
        ThrowIfAny(_hostStateFeatures, "World replay files do not support the following host-defined state: ");
    }

    private static void ThrowIfAny(IReadOnlyCollection<string> unsupported, string message)
    {
        if (unsupported.Count != 0)
            throw new NotSupportedException(message + string.Join("; ", unsupported));
    }

    private static ReadOnlyCollection<string> FindRequiredSolvers(AtmosWorldCheckpoint checkpoint, AtmosWorldRecording recording)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var solver in checkpoint.Solvers)
        {
            if (solver.Kind == AtmosWorldSolverKind.Custom)
                names.Add(solver.Name);
        }

        // A layout entry has no kind. A name that isn't a built-in stage can only be a custom one.
        foreach (var recorded in recording.Operations)
        {
            if (recorded.Operation is not SetAtmosWorldSolverLayoutOperation layout)
                continue;

            foreach (var stage in layout.Stages)
            {
                if (!AtmosWorldSolverPipeline.BuiltInStageNames.Contains(stage.Name))
                    names.Add(stage.Name);
            }
        }

        return Array.AsReadOnly(names.ToArray());
    }

    private static ReadOnlyCollection<string> FindRequiredSimulationComponents(
        AtmosWorldCheckpoint checkpoint,
        AtmosWorldRecording recording)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var component in checkpoint.SimulationComponents)
            keys.Add(component.Key);

        foreach (var recorded in recording.Operations)
        {
            string? key = recorded.Operation switch
            {
                SetAtmosSimulationComponentStateOperation set => set.Key,
                RemoveAtmosSimulationComponentOperation remove => remove.Key,
                _ => null
            };

            if (key != null)
                keys.Add(key);
        }

        return Array.AsReadOnly(keys.ToArray());
    }

    private static IEnumerable<string> FindUnsupportedHostState(AtmosWorldCheckpoint checkpoint, AtmosWorldRecording recording)
    {
        foreach (var simulation in checkpoint.Simulations)
        foreach (var chunk in simulation.Checkpoint.Chunks)
        foreach (var array in chunk.SolverArrays.Where(static array => !array.IsPortable))
        {
            yield return $"chunk field '{array.Key}' ({array.ElementType.FullName}, not a portable element type) " +
                         $"in simulation {simulation.Simulation}, chunk {chunk.Position}";
        }

        // Configurations with [NumosSerialize] members save themselves. Only hand-written ones have no wire format.
        foreach (var configuration in checkpoint.Config.SolverConfigurations.Where(static value => value is not INumosState))
            yield return $"world solver configuration '{configuration.Key}' (no [NumosSerialize] members)";

        HashSet<string>? reportedFields = null;
        foreach (var recorded in recording.Operations)
        {
            if (recorded.Operation is SetAtmosWorldConfigOperation config)
            {
                foreach (var configuration in config.Config.SolverConfigurations.Where(static value => value is not INumosState))
                    yield return $"recorded solver configuration '{configuration.Key}' (no [NumosSerialize] members)";

                continue;
            }

            // One entry per field, since a host can record thousands of writes to the same one.
            string? key = recorded.Operation is AtmosWorldSimulationOperation simulation
                ? simulation.Operation switch
                {
                    WriteChunkFieldOperation write => write.Key,
                    FillChunkFieldBoxOperation fill => fill.Key,
                    _ => null
                }
                : null;

            if (key != null &&
                ChunkFieldRegistry.TryGet(key, out var field) &&
                !field.IsPortable &&
                (reportedFields ??= new HashSet<string>(StringComparer.Ordinal)).Add(key))
            {
                yield return $"recorded write to chunk field '{key}' ({field.ElementType.FullName}, not a portable element type)";
            }
        }
    }
}

/// <summary>
///     Reports progress while an imported world replay is verified and indexed for scrubbing.
/// </summary>
/// <param name="CompletedTicks">Ticks reconstructed after the archive's initial position.</param>
/// <param name="TotalTicks">Ticks between the initial position and replay head.</param>
public readonly record struct AtmosWorldReplayIndexProgress(ulong CompletedTicks, ulong TotalTicks);