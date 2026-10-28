using System.Diagnostics;
using Numos.API;
using Numos.CoreSim;
using Numos.CoreSim.Replay;

namespace Numos.Workbench;

/// <summary>
///     What <see cref="WorkbenchSession.StepForward" /> did.
/// </summary>
public enum TimelineStepResult
{
    /// <summary>
    ///     The timeline already shows its head. Nothing moved.
    /// </summary>
    AtHead,

    /// <summary>
    ///     The timeline moved one tick forward through recorded history.
    /// </summary>
    Seeked,

    /// <summary>
    ///     The live world simulated one tick.
    /// </summary>
    Ticked
}

public sealed partial class WorkbenchSession
{
    private const int DefaultProgressInterval = 100;
    private const string ReplaySource = "Replay";

    /// <summary>
    ///     The timeline of the selected branch, or <see langword="null" /> when no project is open.
    /// </summary>
    /// <remarks>
    ///     Branch operations replace this object. Read it again after each one.
    /// </remarks>
    public AtmosWorldReplayTimeline? Timeline => Branches?.Timeline;

    /// <summary>
    ///     The in-memory branches of the project, or <see langword="null" /> when no project is open.
    /// </summary>
    public ReplayBranchSession? Branches { get; private set; }

    /// <summary>
    ///     Whether the timeline shows history instead of the live head.
    /// </summary>
    public bool IsInspecting => Timeline?.IsInspecting == true;

    /// <summary>
    ///     Counts timeline moves and project changes.
    /// </summary>
    /// <remarks>
    ///     A front end compares this with the value it saw last and rebuilds its presentation when it changes. Seeks,
    ///     branch operations, and project replacement all increase it, even when the operation failed part way.
    /// </remarks>
    public long TimelineRevision { get; private set; }

    /// <summary>
    ///     Seconds accumulated toward the next playback step while inspecting history.
    /// </summary>
    private float PlaybackElapsed { get; set; }

    /// <summary>
    ///     Simulates ticks on the live head and samples checkpoints after each one.
    /// </summary>
    /// <param name="count">How many ticks to run. Must be at least 1.</param>
    /// <param name="progressInterval">
    ///     Log a progress line every this many ticks. Zero turns progress lines off.
    /// </param>
    /// <param name="afterTick">
    ///     Called after each tick with the number of ticks done in this call, from 1 to <paramref name="count" />.
    ///     The callback may read the world. It must not change it.
    /// </param>
    /// <returns>The world tick count afterwards.</returns>
    /// <remarks>
    ///     A call with <paramref name="count" /> above 1 also logs a closing line with the elapsed wall time.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The timeline is inspecting history.</exception>
    public int Tick(int count = 1, int progressInterval = DefaultProgressInterval, Action<int>? afterTick = null)
    {
        return Guard(
            "Could not tick the world",
            () =>
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
                ArgumentOutOfRangeException.ThrowIfNegative(progressInterval);
                var world = RequireWorld();
                ThrowIfInspecting();
                long start = Stopwatch.GetTimestamp();
                for (int done = 1; done <= count; done++)
                {
                    world.Tick();
                    Timeline!.ObserveLiveState();
                    afterTick?.Invoke(done);
                    if (progressInterval > 0 && done % progressInterval == 0 && done < count)
                    {
                        Info(
                            $"Tick {world.TickCount} ({done}/{count}), " +
                            $"{Stopwatch.GetElapsedTime(start).TotalSeconds:F2} s elapsed.");
                    }
                }

                if (count > 1)
                {
                    Info(
                        $"Advanced {count} ticks to tick {world.TickCount} in " +
                        $"{Stopwatch.GetElapsedTime(start).TotalSeconds:F2} s.");
                }

                return world.TickCount;
            });
    }

    /// <summary>
    ///     Advances the live world by wall-clock time. The world runs as many fixed ticks as the time allows.
    /// </summary>
    /// <param name="elapsedSeconds">Frame time in seconds.</param>
    /// <remarks>
    ///     Call <see cref="ObserveLiveState" /> once per frame afterwards. Use <see cref="AccumulatePlayback" /> while
    ///     inspecting history.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The timeline is inspecting history.</exception>
    public void Update(float elapsedSeconds)
    {
        var world = RequireWorld();
        ThrowIfInspecting();
        world.Update(elapsedSeconds);
    }

    /// <summary>
    ///     Counts frame time toward the next playback step while inspecting history.
    /// </summary>
    /// <param name="elapsedSeconds">Frame time in seconds.</param>
    /// <returns>
    ///     <see langword="true" /> when a step is due. The counter restarts, and the caller then calls
    ///     <see cref="StepForward" />.
    /// </returns>
    public bool AccumulatePlayback(float elapsedSeconds)
    {
        PlaybackElapsed += elapsedSeconds;
        if (PlaybackElapsed < 1f / AtmosSimulation.SimulationRate)
            return false;

        PlaybackElapsed = 0f;
        return true;
    }

    /// <summary>
    ///     Samples the live state for the timeline's checkpoints. Does nothing without a project.
    /// </summary>
    public void ObserveLiveState()
    {
        Timeline?.ObserveLiveState();
    }

    /// <summary>
    ///     Moves one tick forward. Inspecting history seeks through the recording. A live timeline simulates a tick.
    /// </summary>
    /// <exception cref="InvalidOperationException">No project is open.</exception>
    public TimelineStepResult StepForward()
    {
        RequireWorld();
        if (!IsInspecting)
        {
            Tick();
            return TimelineStepResult.Ticked;
        }

        if (Timeline!.Position.Tick >= Timeline.Head.Tick)
            return TimelineStepResult.AtHead;

        SeekTick(Timeline.Position.Tick + 1);
        return TimelineStepResult.Seeked;
    }

    /// <summary>
    ///     Restores the world to the state at the start of a tick and inspects it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The tick is outside the timeline.</exception>
    public void SeekTick(ulong tick)
    {
        Move($"Could not seek the replay to tick {tick}", () => Timeline!.SeekTick(tick));
    }

    /// <summary>
    ///     Restores the world to an exact timeline position, including the operation sequence, and inspects it.
    /// </summary>
    public void SeekPosition(AtmosTimelinePosition position)
    {
        Move("Could not seek the replay", () => Timeline!.SeekPosition(position));
    }

    /// <summary>
    ///     Moves the timeline to its head without making it writable.
    /// </summary>
    public void ReturnToHead()
    {
        Move("Could not return the replay to its head", () => Timeline!.ReturnToHead());
    }

    /// <summary>
    ///     Preserves the selected branch and starts a child before operations recorded at the current tick.
    /// </summary>
    /// <returns>The new branch.</returns>
    public ReplayBranchInfo CreateBranch()
    {
        ReplayBranchInfo branch = default;
        Move(
            "Could not create the replay branch",
            () =>
            {
                branch = Branches!.CreateBranchFromCurrentTick();
                Log.Write(
                    WorkbenchLogLevel.Info,
                    ReplaySource,
                    $"Created {branch.Name} from tick {branch.Fork.Tick}.",
                    null,
                    World!.TickCount);
            });

        return branch;
    }

    /// <summary>
    ///     Switches to another branch and inspects it.
    /// </summary>
    /// <param name="branchId">The branch id from <see cref="ReplayBranchSession.Branches" />.</param>
    /// <param name="tick">The tick to inspect, or <see langword="null" /> for the branch's fork tick.</param>
    /// <exception cref="ArgumentOutOfRangeException">The branch or tick does not exist.</exception>
    public void SelectBranch(int branchId, ulong? tick = null)
    {
        Move(
            "Could not select the replay branch",
            () =>
            {
                Branches!.SelectBranch(branchId, tick);

                Log.Write(
                    WorkbenchLogLevel.Info,
                    ReplaySource,
                    $"Selected {Branches.SelectedBranch.Name} on tick {Timeline!.Position.Tick}.",
                    null,
                    World!.TickCount);
            });
    }

    /// <summary>
    ///     Makes the selected branch writable at its head.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timeline is not at the selected branch's head.</exception>
    public void ContinueBranch()
    {
        Move(
            "Could not continue the replay branch",
            () =>
            {
                Branches!.ContinueSelectedBranch();
            });
    }

    /// <summary>
    ///     Copies the world's configuration into <see cref="Config" />, keeping that instance.
    /// </summary>
    /// <remarks>
    ///     A timeline move can restore a different configuration. The session calls this after each move.
    /// </remarks>
    public void SyncConfigFromWorld()
    {
        var world = RequireWorld();
        var restored = new AtmosConfig(world.Config);
        var config = Config!;
        config.GasRegistry = restored.GasRegistry;
        config.SolverConfigurations = restored.SolverConfigurations;
        AtmosConfigSettings.CopyScalarSettings(restored, config);
    }

    private void Move(string action, Action body)
    {
        RequireWorld();
        try
        {
            body();
        }
        catch (Exception exception)
        {
            Failed(action, exception);
            throw;
        }
        finally
        {
            TimelineRevision++;
            ReconcileSimulations();
            SyncConfigFromWorld();
        }
    }
}
