using Numos.API;
using Numos.CoreSim.Replay;
using Numos.CoreSim.State;
using Numos.Serialization;

namespace Numos.Testing;

/// <summary>
///     Checks in one call that a world and its custom solvers replay deterministically.
/// </summary>
/// <remarks>
///     Numos replays a world from a checkpoint and the recorded host operations. Replay matches the original run only
///     when every piece of state that affects a tick is in the checkpoint. A solver that keeps state in an unmarked
///     field, reads a static, or forgets to reset scratch in <see cref="IAtmosWorldSolver.OnRestored" /> breaks that.
///     <see cref="Run" /> drives the world the way a host would and catches those mistakes. It uses no test framework,
///     so any test runner can call it.
/// </remarks>
public static class AtmosReplayCheck
{
    private static readonly NumosReplayMetadata Metadata = new(
        "AtmosReplayCheck",
        DateTimeOffset.UnixEpoch,
        "Numos.Testing",
        "0",
        "0");

    /// <summary>
    ///     Runs a world through the record and replay, sync, save and load, and second world checks.
    /// </summary>
    /// <param name="createWorld">
    ///     Builds a fresh world with its simulations, chunks, gas, solvers, and configuration. It must build the same
    ///     world on every call. <see cref="Run" /> calls it once per check and disposes each world it gets.
    /// </param>
    /// <param name="script">
    ///     Performs the host's external operations between ticks. <see cref="Run" /> calls it before each tick with the
    ///     world and the one-based number of that tick in the run. It must depend only on its arguments, because the
    ///     checks call it again for the same tick.
    /// </param>
    /// <param name="ticks">The number of ticks to run.</param>
    /// <param name="options">The check settings, or <see langword="null" /> for the defaults.</param>
    /// <returns>The failures, one per failed check, and the checks that did not run.</returns>
    /// <remarks>
    ///     <para>The checks run in this order:</para>
    ///     <list type="number">
    ///         <item>
    ///             <description>
    ///                 <see cref="AtmosReplayCheckKind.RecordReplay" /> records the run with a state hash trail. It
    ///                 keeps a checkpoint at each hashed tick. It then replays the recording from the start checkpoint
    ///                 and compares every hash.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <see cref="AtmosReplayCheckKind.SyncTest" /> drives a new world without recording. Every
    ///                 <see cref="AtmosReplayCheckOptions.SyncInterval" /> ticks it captures a checkpoint and runs
    ///                 <see cref="AtmosReplayCheckOptions.SyncDistance" /> ticks. It then restores the checkpoint, runs the
    ///                 same ticks again, and compares both passes tick by tick.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <see cref="AtmosReplayCheckKind.SaveLoad" /> writes the recording to an in-memory replay file and
    ///                 reads it back. It restores the file into a new world through
    ///                 <see cref="AtmosWorldReplayTimeline.Restore" />, which replays it and compares the head hash.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <see cref="AtmosReplayCheckKind.SecondWorld" /> drives a new world like the recorded run and
    ///                 compares it with the kept checkpoints.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     <para>
    ///         Each check stops at its first divergence and reports that position. The other checks still run.
    ///         <see cref="AtmosReplayCheck" /> skips the save and load check when the replay file cannot hold the
    ///         world's state, for example a solver configuration without <see cref="NumosSerializeAttribute" />
    ///         members. Custom solvers do not cause a skip, because the fresh world from
    ///         <paramref name="createWorld" /> registers them.
    ///     </para>
    ///     <para>
    ///         The second world records too, without a hash trail. Recording numbers each host operation, and that
    ///         number is part of the state hash. An unrecorded world would therefore never match the recorded run.
    ///     </para>
    ///     <para>
    ///         An exception from <paramref name="createWorld" />, <paramref name="script" />, or a solver propagates.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    ///     AtmosReplayCheckResult result = AtmosReplayCheck.Run(
    ///         () =&gt; BuildStation(),
    ///         (world, tick) =&gt;
    ///         {
    ///             if (tick == 30)
    ///                 Station(world).AddGasToVoxel(Room(world), 0, "Plasma", 5f, 600f);
    ///         },
    ///         ticks: 300);
    ///     result.ThrowIfFailed();
    ///     </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="createWorld" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ticks" /> is negative.</exception>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="createWorld" /> returned <see langword="null" /> or a world that is already recording.
    /// </exception>
    public static AtmosReplayCheckResult Run(
        Func<AtmosWorld> createWorld,
        Action<AtmosWorld, long>? script = null,
        int ticks = 600,
        AtmosReplayCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(createWorld);
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        options ??= new AtmosReplayCheckOptions();

        var run = new CheckRun(createWorld, script, ticks, options);
        var failures = new List<AtmosReplayCheckFailure>();
        var skipped = new List<string>();
        AtmosWorldReplayArchive archive;

        using (var world = run.CreateWorld())
        {
            var recorded = run.Record(world);
            archive = recorded.ToArchive();
            Add(failures, run.CheckRecordReplay(world, recorded));
            Add(failures, run.CheckSync());

            if (options.CheckSaveLoad)
                Add(failures, run.CheckSaveLoad(recorded, archive, skipped));
            else
                skipped.Add($"{AtmosReplayCheckKind.SaveLoad}: {nameof(AtmosReplayCheckOptions.CheckSaveLoad)} is false.");

            if (options.CheckSecondWorld)
                Add(failures, run.CheckSecondWorld(recorded));
            else
                skipped.Add($"{AtmosReplayCheckKind.SecondWorld}: {nameof(AtmosReplayCheckOptions.CheckSecondWorld)} is false.");
        }

        return new AtmosReplayCheckResult(failures, skipped, archive);
    }

    private static void Add(List<AtmosReplayCheckFailure> failures, AtmosReplayCheckFailure? failure)
    {
        if (failure != null)
            failures.Add(failure);
    }

    /// <summary>
    ///     The state of the recorded run that the later checks compare against.
    /// </summary>
    /// <param name="Start">The checkpoint before the first tick, where recording began.</param>
    /// <param name="Recording">The stopped recording, with its hash trail.</param>
    /// <param name="Trail">One checkpoint per entry of the hash trail, in the same order.</param>
    /// <param name="Head">The checkpoint after the last tick.</param>
    private sealed record RecordedRun(
        AtmosWorldCheckpoint Start,
        AtmosWorldRecording Recording,
        IReadOnlyList<AtmosWorldCheckpoint> Trail,
        AtmosWorldCheckpoint Head)
    {
        public AtmosWorldReplayArchive ToArchive()
        {
            return new AtmosWorldReplayArchive(Start, Recording, Start.ComputeStateHash(), Head.ComputeStateHash());
        }
    }

    /// <summary>
    ///     A divergence that <see cref="CheckRun.Locate" /> found.
    /// </summary>
    private readonly record struct Divergence(AtmosTimelinePosition Position, IReadOnlyList<AtmosStateDifference> Differences);

    private sealed class CheckRun(
        Func<AtmosWorld> createWorld,
        Action<AtmosWorld, long>? script,
        int ticks,
        AtmosReplayCheckOptions options)
    {
        public AtmosWorld CreateWorld()
        {
            var world = createWorld() ??
                        throw new InvalidOperationException("The createWorld callback returned null.");

            if (world.IsRecording)
            {
                world.Dispose();
                throw new InvalidOperationException("The createWorld callback must return a world that is not recording.");
            }

            return world;
        }

        public RecordedRun Record(AtmosWorld world)
        {
            var start = world.CaptureCheckpoint();
            world.StartRecording(new AtmosRecordingOptions { HashInterval = options.HashInterval });
            var trail = new List<AtmosWorldCheckpoint>();
            for (long tick = 1; tick <= ticks; tick++)
            {
                RunTick(world, tick);

                // Recording takes its hash at the end of the same ticks, counted from where recording started. No host
                // edit is pending at the end of a tick, so this capture records nothing.
                if (IsTrailTick(tick))
                    trail.Add(world.CaptureCheckpoint());
            }

            var recording = world.StopRecording();
            return new RecordedRun(start, recording, trail, world.CaptureCheckpoint());
        }

        public AtmosReplayCheckFailure? CheckRecordReplay(AtmosWorld world, RecordedRun recorded)
        {
            if (Locate(world, recorded.Start, recorded.Recording, recorded) is not { } divergence)
                return null;

            return new AtmosReplayCheckFailure(
                AtmosReplayCheckKind.RecordReplay,
                divergence.Position,
                divergence.Differences,
                "Replaying the recording from its start checkpoint gives a different state than the recorded run.");
        }

        public AtmosReplayCheckFailure? CheckSync()
        {
            using var world = CreateWorld();
            long completed = 0;
            long nextSync = 0;
            while (completed < ticks)
            {
                if (completed < nextSync)
                {
                    RunTick(world, ++completed);
                    continue;
                }

                long first = completed + 1;
                int distance = (int)Math.Min(options.SyncDistance, ticks - completed);
                if (SyncTest(world, first, distance) is { } failure)
                    return failure;

                nextSync = completed + options.SyncInterval;
                completed += distance;
            }

            return null;
        }

        private AtmosReplayCheckFailure? SyncTest(AtmosWorld world, long first, int distance)
        {
            var origin = world.CaptureCheckpoint();
            var expected = new AtmosWorldCheckpoint[distance];
            for (int step = 0; step < distance; step++)
            {
                RunTick(world, first + step);
                expected[step] = world.CaptureCheckpoint();
            }

            world.RestoreCheckpoint(origin);
            for (int step = 0; step < distance; step++)
            {
                RunTick(world, first + step);
                var actual = world.CaptureCheckpoint();
                if (actual.ComputeStateHash() == expected[step].ComputeStateHash())
                    continue;

                return new AtmosReplayCheckFailure(
                    AtmosReplayCheckKind.SyncTest,
                    expected[step].Position,
                    expected[step].Diff(actual, options.MaxDifferences),
                    $"Running ticks {first} to {first + distance - 1} of the run again from a restored checkpoint " +
                    $"gives a different state at tick {first + step} of the run.");
            }

            return null;
        }

        public AtmosReplayCheckFailure? CheckSaveLoad(
            RecordedRun recorded,
            AtmosWorldReplayArchive archive,
            List<string> skipped)
        {
            using var file = new MemoryStream();
            try
            {
                NumosWorldReplaySerializer.Serialize(file, new NumosWorldReplayDocument(Metadata, archive));
            }
            catch (NotSupportedException exception)
            {
                skipped.Add($"{AtmosReplayCheckKind.SaveLoad}: {exception.Message}");
                return null;
            }

            file.Position = 0;
            AtmosWorldReplayArchive loaded;
            try
            {
                loaded = NumosWorldReplaySerializer.Deserialize(file).Replay;
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
            {
                return SaveLoadFailure(
                    recorded.Start.Position,
                    [],
                    $"The replay file does not read back: {exception.Message}");
            }

            if (loaded.InitialCheckpoint.ComputeStateHash() != archive.InitialStateHash)
            {
                return SaveLoadFailure(
                    recorded.Start.Position,
                    recorded.Start.Diff(loaded.InitialCheckpoint, options.MaxDifferences),
                    "The start checkpoint reads back from the replay file with different state.");
            }

            if (loaded.HeadStateHash != archive.HeadStateHash)
            {
                return SaveLoadFailure(
                    recorded.Head.Position,
                    [],
                    "The head state hash reads back from the replay file with a different value.");
            }

            using var world = CreateWorld();
            try
            {
                // Restore replays the file through its head and compares the head hash. Unlike Import, it accepts the
                // custom solvers that createWorld registered.
                _ = AtmosWorldReplayTimeline.Restore(world, loaded);
                return null;
            }
            catch (InvalidDataException exception)
            {
                var divergence = Locate(world, loaded.InitialCheckpoint, loaded.Recording, recorded);
                return SaveLoadFailure(
                    divergence?.Position ?? recorded.Head.Position,
                    divergence?.Differences ?? [],
                    $"Replaying the saved file in a fresh world gives a different state than the recorded run. {exception.Message}");
            }
        }

        private static AtmosReplayCheckFailure SaveLoadFailure(
            AtmosTimelinePosition position,
            IReadOnlyList<AtmosStateDifference> differences,
            string message)
        {
            return new AtmosReplayCheckFailure(AtmosReplayCheckKind.SaveLoad, position, differences, message);
        }

        public AtmosReplayCheckFailure? CheckSecondWorld(RecordedRun recorded)
        {
            using var world = CreateWorld();
            var start = world.CaptureCheckpoint();
            if (start.ComputeStateHash() != recorded.Start.ComputeStateHash())
                return SecondWorldFailure(recorded.Start, start, "The second world starts with a different state.");

            // Without recording, the operation sequence would not advance, and every position would differ.
            world.StartRecording();
            int trailIndex = 0;
            for (long tick = 1; tick <= ticks; tick++)
            {
                RunTick(world, tick);
                if (!IsTrailTick(tick))
                    continue;

                var expected = recorded.Trail[trailIndex++];
                var actual = world.CaptureCheckpoint();
                if (actual.ComputeStateHash() != expected.ComputeStateHash())
                    return SecondWorldFailure(expected, actual, $"The second world reaches a different state at tick {tick} of the run.");
            }

            world.StopRecording();
            var head = world.CaptureCheckpoint();
            return head.ComputeStateHash() == recorded.Head.ComputeStateHash()
                ? null
                : SecondWorldFailure(recorded.Head, head, "The second world ends in a different state.");
        }

        private AtmosReplayCheckFailure SecondWorldFailure(
            AtmosWorldCheckpoint expected,
            AtmosWorldCheckpoint actual,
            string message)
        {
            return new AtmosReplayCheckFailure(
                AtmosReplayCheckKind.SecondWorld,
                expected.Position,
                expected.Diff(actual, options.MaxDifferences),
                message);
        }

        /// <summary>
        ///     Replays <paramref name="recording" /> in <paramref name="world" /> with its hash trail and finds the first
        ///     position that differs from the recorded run.
        /// </summary>
        /// <returns>The first differing trail position, or the head, or <see langword="null" /> when the replay matches.</returns>
        private Divergence? Locate(
            AtmosWorld world,
            AtmosWorldCheckpoint start,
            AtmosWorldRecording recording,
            RecordedRun recorded)
        {
            try
            {
                world.ReplayTo(start, recording);
            }
            catch (AtmosReplayDivergedException exception)
            {
                // The failed replay put the pre-call state back. The overload without a trail stops at the position.
                world.ReplayTo(start, recording.Operations, exception.Position);
                var replayed = world.CaptureCheckpoint();
                var expected = recorded.Trail.FirstOrDefault(checkpoint => checkpoint.Position == exception.Position);
                return new Divergence(
                    exception.Position,
                    expected?.Diff(replayed, options.MaxDifferences) ?? []);
            }

            var head = world.CaptureCheckpoint();
            if (head.ComputeStateHash() == recorded.Head.ComputeStateHash())
                return null;

            return new Divergence(recorded.Head.Position, recorded.Head.Diff(head, options.MaxDifferences));
        }

        private bool IsTrailTick(long tick)
        {
            return options.HashInterval > 0 && tick % options.HashInterval == 0;
        }

        private void RunTick(AtmosWorld world, long tick)
        {
            script?.Invoke(world, tick);
            world.Tick();
        }
    }
}
