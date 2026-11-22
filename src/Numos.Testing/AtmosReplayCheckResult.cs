using System.Collections.ObjectModel;
using System.Text;
using Numos.API;
using Numos.CoreSim.Replay;

namespace Numos.Testing;

/// <summary>
///     Names one of the checks that <see cref="AtmosReplayCheck.Run" /> performs.
/// </summary>
public enum AtmosReplayCheckKind
{
    /// <summary>
    ///     Records a run with a state hash trail and replays the recording from its start checkpoint.
    /// </summary>
    RecordReplay,

    /// <summary>
    ///     Simulates the same short span twice, the second time from a restored checkpoint.
    /// </summary>
    SyncTest,

    /// <summary>
    ///     Writes the recording to a replay file, reads it back, and replays it in a fresh world.
    /// </summary>
    SaveLoad,

    /// <summary>
    ///     Builds a second world and drives it like the recorded run.
    /// </summary>
    SecondWorld
}

/// <summary>
///     Describes the first divergence that one check found.
/// </summary>
/// <param name="Check">The check that failed.</param>
/// <param name="Position">
///     The first compared position whose state differs. The divergence happened at this position or after the previous
///     compared position.
/// </param>
/// <param name="Differences">
///     The fields that differ at <paramref name="Position" />, from <see cref="AtmosWorldCheckpoint.Diff" />. The left
///     side is the expected state. Empty when the check could not capture both states.
/// </param>
/// <param name="Message">A sentence that says what the check compared.</param>
public sealed record AtmosReplayCheckFailure(
    AtmosReplayCheckKind Check,
    AtmosTimelinePosition Position,
    IReadOnlyList<AtmosStateDifference> Differences,
    string Message);

/// <summary>
///     Holds the outcome of <see cref="AtmosReplayCheck.Run" />.
/// </summary>
public sealed class AtmosReplayCheckResult
{
    internal AtmosReplayCheckResult(
        List<AtmosReplayCheckFailure> failures,
        List<string> skipped,
        AtmosWorldReplayArchive? archive)
    {
        Failures = new ReadOnlyCollection<AtmosReplayCheckFailure>(failures);
        Skipped = new ReadOnlyCollection<string>(skipped);
        Archive = archive;
    }

    /// <summary>
    ///     Gets whether every check that ran found no divergence.
    /// </summary>
    /// <remarks>A skipped check does not make this <see langword="false" />. Look at <see cref="Skipped" /> too.</remarks>
    public bool Passed => Failures.Count == 0;

    /// <summary>
    ///     Gets one entry per failed check, in the order the checks ran.
    /// </summary>
    public IReadOnlyList<AtmosReplayCheckFailure> Failures { get; }

    /// <summary>
    ///     Gets one line per check that did not run. Each line starts with the check name and gives the reason.
    /// </summary>
    public IReadOnlyList<string> Skipped { get; }

    /// <summary>
    ///     Gets the archive of the run that <see cref="AtmosReplayCheckKind.RecordReplay" /> recorded, or
    ///     <see langword="null" /> when that check did not run.
    /// </summary>
    /// <remarks>
    ///     The archive holds the start checkpoint, the recording with its hash trail, and both state hashes. Save it with
    ///     <see cref="Numos.Serialization.NumosWorldReplaySerializer" /> to open a failed run in the viewer or to attach
    ///     it to a bug report.
    /// </remarks>
    public AtmosWorldReplayArchive? Archive { get; }

    /// <summary>
    ///     Throws when a check failed.
    /// </summary>
    /// <remarks>
    ///     The message lists each failure with its check, its tick and operation sequence, and the differing paths with
    ///     their details. It ends with the skipped checks.
    /// </remarks>
    /// <exception cref="InvalidOperationException">At least one check failed.</exception>
    public void ThrowIfFailed()
    {
        if (!Passed)
            throw new InvalidOperationException(FormatReport());
    }

    private string FormatReport()
    {
        var report = new StringBuilder();
        report.Append("The replay check found ").Append(Failures.Count).Append(Failures.Count == 1 ? " failure." : " failures.");
        foreach (var failure in Failures)
        {
            report.AppendLine()
                .Append(failure.Check)
                .Append(" at tick ")
                .Append(failure.Position.Tick)
                .Append(", operation ")
                .Append(failure.Position.OperationSequence)
                .Append(": ")
                .Append(failure.Message);

            foreach (var difference in failure.Differences)
                report.AppendLine().Append("    ").Append(difference.Path).Append(": ").Append(difference.Detail);
        }

        foreach (string skipped in Skipped)
            report.AppendLine().Append("Skipped ").Append(skipped);

        return report.ToString();
    }
}
