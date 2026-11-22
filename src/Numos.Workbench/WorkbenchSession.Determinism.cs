using Numos.API;
using Numos.CoreSim.Replay;

namespace Numos.Workbench;

/// <summary>
///     What <see cref="WorkbenchSession.VerifyDeterminism" /> found.
/// </summary>
/// <param name="Start">The position that both runs started from.</param>
/// <param name="End">The position that both runs reached. The session stays here, on the state of the first run.</param>
/// <param name="FirstRun">The state hash after the first run, which the timeline recorded.</param>
/// <param name="SecondRun">The state hash after the second run from the checkpoint at <paramref name="Start" />.</param>
/// <param name="Differences">
///     Where the second run differs from the first, with the first run on the left of each entry. The list is empty
///     when the two hashes are equal.
/// </param>
public sealed record DeterminismReport(
    AtmosTimelinePosition Start,
    AtmosTimelinePosition End,
    AtmosWorldStateHash FirstRun,
    AtmosWorldStateHash SecondRun,
    IReadOnlyList<AtmosStateDifference> Differences)
{
    /// <summary>
    ///     Whether both runs ended in the same state.
    /// </summary>
    public bool IsDeterministic => FirstRun == SecondRun;
}

public sealed partial class WorkbenchSession
{
    /// <summary>
    ///     Runs ticks on the live head twice from the same checkpoint and compares the two end states.
    /// </summary>
    /// <param name="ticks">How many ticks each run simulates. Must be at least 1.</param>
    /// <param name="maxDifferences">The most entries in <see cref="DeterminismReport.Differences" />.</param>
    /// <returns>Both hashes and the differences between the two end states.</returns>
    /// <remarks>
    ///     <para>
    ///         The first run is an ordinary <see cref="Tick" /> call, so the timeline records it. The session then
    ///         stops recording through the timeline and restores the checkpoint from before the first run. It
    ///         simulates the same ticks again without recording. Last, it returns the timeline to its head, which
    ///         restores the state of the first run and resumes recording. The session ends <paramref name="ticks" />
    ///         ticks later, with a history that holds only the first run.
    ///     </para>
    ///     <para>
    ///         The second run starts with an <see cref="IAtmosWorldSolver.OnRestored" /> call, and the first run does
    ///         not. A solver whose scratch state changes the result therefore shows up as a difference. The return to
    ///         the head calls <see cref="IAtmosWorldSolver.OnRestored" /> again.
    ///     </para>
    ///     <para>
    ///         Events that stages emit reach their handlers once, during the first run. The second run drops them.
    ///     </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="ticks" /> or <paramref name="maxDifferences" /> is less than 1.
    /// </exception>
    /// <exception cref="InvalidOperationException">No project is open, or the timeline is inspecting history.</exception>
    public DeterminismReport VerifyDeterminism(int ticks, int maxDifferences = 64)
    {
        const string action = "Could not verify determinism";
        var (world, start) = Guard(
            action,
            () =>
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(ticks, 1);
                ArgumentOutOfRangeException.ThrowIfLessThan(maxDifferences, 1);
                var live = RequireWorld();
                ThrowIfInspecting();
                return (live, live.CaptureCheckpoint());
            });

        Tick(ticks);

        AtmosWorldCheckpoint first = null!;
        AtmosWorldCheckpoint second = null!;
        Move(
            action,
            () =>
            {
                var timeline = Timeline!;
                first = world.CaptureCheckpoint();
                try
                {
                    // Seeking to the head stops recording and keeps the first run as the head checkpoint.
                    timeline.SeekPosition(first.Position);
                    var operations = timeline.Operations
                        .Where(operation =>
                            operation.Sequence > start.Position.OperationSequence &&
                            operation.Sequence <= first.Position.OperationSequence)
                        .ToArray();

                    world.ReplayTo(start, operations, first.Position);
                    second = world.CaptureCheckpoint();
                }
                finally
                {
                    timeline.ReturnToHead();
                }
            });

        var firstHash = first.ComputeStateHash();
        var secondHash = second.ComputeStateHash();
        IReadOnlyList<AtmosStateDifference> differences =
            firstHash == secondHash ? [] : first.Diff(second, maxDifferences);

        var report = new DeterminismReport(start.Position, first.Position, firstHash, secondHash, differences);
        if (report.IsDeterministic)
        {
            Info($"Two runs of {ticks} ticks from tick {start.Position.Tick} ended in the same state.");
        }
        else
        {
            Warn(
                $"Two runs of {ticks} ticks from tick {start.Position.Tick} ended in different states. " +
                $"First difference: {differences[0]}.");
        }

        return report;
    }
}
