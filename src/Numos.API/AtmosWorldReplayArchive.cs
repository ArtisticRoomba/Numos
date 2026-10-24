using Numos.CoreSim;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;
using System.Collections.ObjectModel;
using Numos.CoreSim.Replay;

namespace Numos.API;

/// <summary>
///     Contains a complete initial world state and globally ordered operations needed to reconstruct it.
/// </summary>
public sealed class AtmosWorldReplayArchive
{
    // Scanning every chunk and operation is only needed when something asks, which is mostly saving and importing.
    private IReadOnlyList<string>? _unsupportedFeatures;

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
    ///     Gets human-readable descriptions of host-defined state that the standard replay file cannot reconstruct.
    ///     Empty when the archive is portable.
    /// </summary>
    /// <remarks>
    ///     A custom solver is listed because a standalone host doesn't have its implementation, even though its name
    ///     and any <see cref="NumosSerializeAttribute" /> state are saved. Such an archive can still be written to a
    ///     file and loaded back with <see cref="AtmosWorldReplayTimeline.Restore" /> by a host that registers the same
    ///     solvers.
    /// </remarks>
    public IReadOnlyList<string> UnsupportedFeatures =>
        _unsupportedFeatures ??= new ReadOnlyCollection<string>(FindUnsupportedFeatures(InitialCheckpoint, Recording, true).ToArray());

    /// <summary>
    ///     Throws when this archive depends on host-defined solver state.
    /// </summary>
    /// <exception cref="NotSupportedException">
    ///     The archive contains custom solvers, solver configurations without <see cref="NumosSerializeAttribute" />
    ///     members, or rollback chunk fields whose element type isn't a primitive or an enum.
    /// </exception>
    public void EnsurePortable()
    {
        if (UnsupportedFeatures.Count != 0)
        {
            throw new NotSupportedException(
                "Portable world replay files do not support the following host-defined state: " +
                string.Join("; ", UnsupportedFeatures));
        }
    }

    /// <summary>
    ///     Throws when this archive holds state the replay file format can't encode. Unlike
    ///     <see cref="EnsurePortable" />, custom solvers are fine here: the file records their names and saved state,
    ///     and leaves supplying their implementations to whoever loads it.
    /// </summary>
    /// <exception cref="NotSupportedException">
    ///     The archive contains solver configurations without <see cref="NumosSerializeAttribute" /> members, or rollback
    ///     chunk fields whose element type isn't a primitive or an enum.
    /// </exception>
    internal void EnsureSerializable()
    {
        string[] unsupported = FindUnsupportedFeatures(InitialCheckpoint, Recording, false).ToArray();
        if (unsupported.Length != 0)
        {
            throw new NotSupportedException(
                "World replay files do not support the following host-defined state: " + string.Join("; ", unsupported));
        }
    }

    private static IEnumerable<string> FindUnsupportedFeatures(
        AtmosWorldCheckpoint checkpoint,
        AtmosWorldRecording recording,
        bool includeCustomSolvers)
    {
        if (includeCustomSolvers)
        {
            foreach (var solver in checkpoint.Solvers.Where(static solver => solver.Kind == AtmosWorldSolverKind.Custom))
                yield return $"custom world solver '{solver.Name}'";
        }

        foreach (var simulation in checkpoint.Simulations)
        foreach (var chunk in simulation.Checkpoint.Chunks)
        foreach (var array in chunk.SolverArrays.Where(static array => !array.IsPortable))
        {
            yield return $"chunk field '{array.Key}' ({array.ElementType.FullName}, not a portable element type) " +
                         $"in simulation {simulation.Simulation}, chunk {chunk.Position}";
        }

        // Configurations with [NumosSerialize] members save themselves. Only hand-written ones have no wire format.
        foreach (var configuration in checkpoint.Config.SolverConfigurations.Where(static value => !IsSavable(value)))
            yield return $"world solver configuration '{configuration.Key}' (no [NumosSerialize] members)";

        foreach (var recorded in recording.Operations)
        {
            if (recorded.Operation is not SetAtmosWorldConfigOperation config)
                continue;

            foreach (var configuration in config.Config.SolverConfigurations.Where(static value => !IsSavable(value)))
                yield return $"recorded solver configuration '{configuration.Key}' (no [NumosSerialize] members)";
        }
    }

    private static bool IsSavable(IAtmosSolverConfiguration configuration)
    {
        return configuration is INumosState;
    }
}

/// <summary>
///     Reports progress while an imported world replay is verified and indexed for scrubbing.
/// </summary>
/// <param name="CompletedTicks">Ticks reconstructed after the archive's initial position.</param>
/// <param name="TotalTicks">Ticks between the initial position and replay head.</param>
public readonly record struct AtmosWorldReplayIndexProgress(ulong CompletedTicks, ulong TotalTicks);