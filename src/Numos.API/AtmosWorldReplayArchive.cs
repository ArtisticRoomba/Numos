using System.Collections.ObjectModel;
using Numos.CoreSim.Replay;

namespace Numos.API;

/// <summary>
///     Contains a complete initial world state and globally ordered operations needed to reconstruct it.
/// </summary>
public sealed class AtmosWorldReplayArchive
{
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
        UnsupportedFeatures = new ReadOnlyCollection<string>(FindUnsupportedFeatures(initialCheckpoint, recording).ToArray());
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
    ///     Gets host-defined state that the standard replay file cannot reconstruct.
    /// </summary>
    public IReadOnlyList<string> UnsupportedFeatures { get; }

    /// <summary>
    ///     Throws when this archive depends on host-defined solver state.
    /// </summary>
    /// <exception cref="NotSupportedException">
    ///     The archive contains custom solvers, solver configurations, or solver arrays.
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

    private static IEnumerable<string> FindUnsupportedFeatures(
        AtmosWorldCheckpoint checkpoint,
        AtmosWorldRecording recording)
    {
        foreach (var solver in checkpoint.Solvers.Where(static solver => solver.Kind == AtmosWorldSolverKind.Custom))
            yield return $"custom world solver '{solver.Name}'";

        foreach (var simulation in checkpoint.Simulations)
        {
            foreach (var configuration in simulation.Checkpoint.Config.SolverConfigurations)
                yield return $"solver configuration '{configuration.Key}' in simulation {simulation.Simulation}";

            foreach (var chunk in simulation.Checkpoint.Chunks)
            foreach (var array in chunk.SolverArrays)
                yield return $"solver array '{array.Key}' in simulation {simulation.Simulation}, chunk {chunk.Position}";
        }

        foreach (var configuration in checkpoint.Config.SolverConfigurations)
            yield return $"world solver configuration '{configuration.Key}'";

        foreach (var recorded in recording.Operations)
        {
            var config = recorded.Operation switch
            {
                SetAtmosWorldConfigOperation worldConfig => worldConfig.Config,
                AtmosWorldSimulationOperation { Operation: SetAtmosConfigOperation simulationConfig } => simulationConfig.Config,
                _ => null
            };

            if (config == null)
                continue;

            foreach (var configuration in config.SolverConfigurations)
                yield return $"recorded solver configuration '{configuration.Key}'";
        }
    }
}

/// <summary>
///     Reports progress while an imported world replay is verified and indexed for scrubbing.
/// </summary>
/// <param name="CompletedTicks">Ticks reconstructed after the archive's initial position.</param>
/// <param name="TotalTicks">Ticks between the initial position and replay head.</param>
public readonly record struct AtmosWorldReplayIndexProgress(ulong CompletedTicks, ulong TotalTicks);