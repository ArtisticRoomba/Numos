using System.Collections.ObjectModel;
using Numos.CoreSim.Replay;

namespace Numos.API;

/// <summary>
///     Contains the initial continuation state and semantic operations needed to reconstruct a recorded simulation.
/// </summary>
public sealed class AtmosReplayArchive
{
    /// <summary>
    ///     Creates a detached replay archive independent of any storage format.
    /// </summary>
    /// <param name="initialCheckpoint">Authoritative continuation state at the recording start.</param>
    /// <param name="recording">Ordered operations and the interval they cover.</param>
    /// <param name="initialStateHash">Reference hash for <paramref name="initialCheckpoint" />.</param>
    /// <param name="headStateHash">Reference hash for the state reconstructed at the recording head.</param>
    /// <exception cref="ArgumentException">The checkpoint, recording, or hashes describe different timeline positions.</exception>
    public AtmosReplayArchive(
        AtmosSimulationCheckpoint initialCheckpoint,
        AtmosRecording recording,
        AtmosStateHash initialStateHash,
        AtmosStateHash headStateHash)
    {
        ArgumentNullException.ThrowIfNull(initialCheckpoint);
        ArgumentNullException.ThrowIfNull(recording);
        if (recording.Start != initialCheckpoint.Position ||
            initialStateHash.Position != recording.Start ||
            headStateHash.Position != recording.Head)
        {
            throw new ArgumentException("Replay checkpoints, recording bounds and reference hashes must describe the same interval.");
        }

        InitialCheckpoint = initialCheckpoint;
        Recording = recording;
        InitialStateHash = initialStateHash;
        HeadStateHash = headStateHash;
        UnsupportedFeatures = new ReadOnlyCollection<string>(FindUnsupportedFeatures(initialCheckpoint, recording).ToArray());
    }

    /// <summary>
    ///     Gets the authoritative initial continuation state.
    /// </summary>
    public AtmosSimulationCheckpoint InitialCheckpoint { get; }

    /// <summary>
    ///     Gets the replay interval and its ordered semantic operations.
    /// </summary>
    public AtmosRecording Recording { get; }

    /// <summary>
    ///     Gets the expected initial state hash.
    /// </summary>
    public AtmosStateHash InitialStateHash { get; }

    /// <summary>
    ///     Gets the expected state hash at the recording head.
    /// </summary>
    public AtmosStateHash HeadStateHash { get; }

    /// <summary>
    ///     Gets host-defined state that the standard replay file format cannot reconstruct.
    /// </summary>
    public IReadOnlyList<string> UnsupportedFeatures { get; }

    /// <summary>
    ///     Throws when this archive depends on host-defined solver state.
    /// </summary>
    /// <exception cref="NotSupportedException">The archive contains custom solvers, solver configurations, or solver arrays.</exception>
    public void EnsurePortable()
    {
        if (UnsupportedFeatures.Count != 0)
        {
            throw new NotSupportedException(
                "Portable replay files do not support the following host-defined state: " +
                string.Join("; ", UnsupportedFeatures));
        }
    }

    private static IEnumerable<string> FindUnsupportedFeatures(
        AtmosSimulationCheckpoint checkpoint,
        AtmosRecording recording)
    {
        foreach (var solver in checkpoint.Solvers.Where(static solver => solver.IsCustom))
            yield return $"custom solver '{solver.Name}'";

        foreach (var configuration in checkpoint.Config.SolverConfigurations)
            yield return $"solver configuration '{configuration.Key}'";

        foreach (var chunk in checkpoint.Chunks)
        foreach (var array in chunk.SolverArrays)
            yield return $"solver array '{array.Key}' in chunk {chunk.Position}";

        foreach (var operation in recording.Operations)
        {
            if (operation.Operation is not SetAtmosConfigOperation config)
                continue;

            foreach (var configuration in config.Config.SolverConfigurations)
                yield return $"recorded solver configuration '{configuration.Key}'";
        }
    }
}

/// <summary>
///     Reports progress while an imported replay is verified and indexed for scrubbing.
/// </summary>
/// <param name="CompletedTicks">Ticks reconstructed after the archive's initial position.</param>
/// <param name="TotalTicks">Total ticks between the initial position and replay head.</param>
public readonly record struct AtmosReplayIndexProgress(ulong CompletedTicks, ulong TotalTicks);