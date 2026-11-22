using System.Globalization;
using Numos.API.Dangerous;
using Numos.CoreSim;
using Numos.CoreSim.Datatypes.Primitives;
using Numos.CoreSim.Replay;
using Numos.CoreSim.State;
using Numos.CoreSim.State.Advanced;
using Numos.Maths;
using Numos.Serialization;

namespace Numos.API.Tests;

/// <summary>
///     Recorded <see cref="SolverCommandAttribute" /> methods: one operation per call, replayed by stage name.
/// </summary>
[TestFixture]
public sealed partial class SolverCommandTests
{
    private const string Name = "tests/commands/fire";

    private static readonly ChunkField<float> Burn =
        ChunkField<float>.Register("tests/solver-commands/burn", rollback: true);

    [Test]
    public void CommandBetweenTicks_RecordsOneOperationAndReplays()
    {
        using var world = CreateWorld(out var fire, out var simulation, out var chunk);
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        world.Tick();

        fire.Ignite(simulation.GetCellRef(chunk, 1), 2f, 600f);
        world.Tick();
        var recording = world.StopRecording();
        var live = world.ComputeStateHash();

        var invoke = (InvokeAtmosSolverCommandOperation)recording.Operations.Single().Operation;
        Assert.Multiple(() =>
        {
            Assert.That(invoke.Solver, Is.EqualTo(Name));
            Assert.That(invoke.Command, Is.EqualTo(NumosState.FieldId(nameof(FireSolver.Ignite))));
            Assert.That(fire.Ignitions, Is.EqualTo(1));
            Assert.That(ReadBurn(simulation, chunk)[1], Is.EqualTo(600f));
        });

        world.ReplayTo(start, recording);
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(live));
            Assert.That(fire.Ignitions, Is.EqualTo(1));
        });
    }

    [Test]
    public void HostEditBeforeCommand_IsRecordedBeforeIt()
    {
        using var world = CreateWorld(out var fire, out var simulation, out var chunk);
        var start = world.CaptureCheckpoint();
        world.StartRecording();

        // The command reads the threshold, so replay must apply the edit first.
        fire.Threshold = 500f;
        fire.Ignite(simulation.GetCellRef(chunk, 0), 1f, 400f);
        world.Tick();
        var recording = world.StopRecording();
        var live = world.ComputeStateHash();

        Assert.Multiple(() =>
        {
            Assert.That(
                recording.Operations.Select(static op => op.Code),
                Is.EqualTo(new[] { AtmosWorldOperationCode.SetSolverState, AtmosWorldOperationCode.InvokeSolverCommand }));
            Assert.That(fire.Ignitions, Is.Zero);
            Assert.That(fire.Fizzles, Is.EqualTo(1));
        });

        world.ReplayTo(start, recording);
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(live));
            Assert.That(fire.Threshold, Is.EqualTo(500f));
            Assert.That(fire.Fizzles, Is.EqualTo(1));
        });
    }

    [Test]
    public void ThrowingCommand_ChangesAndRecordsNothing()
    {
        using var world = CreateWorld(out var fire, out var simulation, out var chunk);
        world.StartRecording();
        var before = world.ComputeStateHash();

        Assert.That(
            () => fire.Ignite(simulation.GetCellRef(chunk, 0), -1f, 600f),
            Throws.TypeOf<ArgumentOutOfRangeException>());

        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(before));
            Assert.That(world.StopRecording().Operations, Is.Empty);
        });

        // The scope released every gate, so the world still works.
        world.StartRecording();
        fire.Ignite(simulation.GetCellRef(chunk, 0), 1f, 600f);
        Assert.That(world.StopRecording().Operations, Has.Count.EqualTo(1));
    }

    [Test]
    public void CommandFromAnotherSolverDuringTick_RecordsNothingAndReplays()
    {
        using var world = CreateWorld(out var fire, out var simulation, out var chunk);
        var cell = simulation.GetCellRef(chunk, 2);
        world.Solvers.Register("tests/commands/spark", _ => fire.Mark(cell, 7f));
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        world.Tick();
        world.Tick();
        var recording = world.StopRecording();
        var live = world.ComputeStateHash();

        Assert.Multiple(() =>
        {
            Assert.That(recording.Operations, Is.Empty);
            Assert.That(fire.Marks, Is.EqualTo(2));
            Assert.That(ReadBurn(simulation, chunk)[2], Is.EqualTo(14f));
        });

        world.ReplayTo(start, recording);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(live));
    }

    [Test]
    public void CommandFromATickWorker_ThrowsInsteadOfDeadlocking()
    {
        using var world = CreateWorld(out var fire, out var simulation, out var chunk);
        var cell = simulation.GetCellRef(chunk, 0);
        Exception? caught = null;
        world.Solvers.Register(
            "tests/commands/worker",
            _ => caught = Record.Exception(() => Task.Run(() => fire.Mark(cell, 1f)).GetAwaiter().GetResult()));

        world.Tick();
        Assert.Multiple(() =>
        {
            Assert.That(caught, Is.TypeOf<InvalidOperationException>().With.Message.Contains("tick's own thread"));
            Assert.That(fire.Marks, Is.Zero);
        });
    }

    [Test]
    public void CommandWithoutRecording_AppliesAndRecordsNothing()
    {
        using var world = CreateWorld(out var fire, out var simulation, out var chunk);
        fire.Mark(simulation.GetCellRef(chunk, 3), 2f);
        world.StartRecording();
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(fire.Marks, Is.EqualTo(1));
            Assert.That(world.StopRecording().Operations, Is.Empty);
        });
    }

    [Test]
    public void DangerousViewInsideCommand_DoesNotCountAsUnrecordedAccess()
    {
        using var world = CreateWorld(out var fire, out var simulation, out var chunk);
        world.StartRecording();
        fire.MarkThroughDangerous(simulation.GetCellRef(chunk, 1), 3f);
        Assert.Multiple(() =>
        {
            Assert.That(world.UnrecordedAccessCount, Is.Zero);
            Assert.That(ReadBurn(simulation, chunk)[1], Is.EqualTo(3f));
        });

        _ = simulation.Dangerous().GetChunkField(chunk, Burn);
        Assert.That(world.UnrecordedAccessCount, Is.EqualTo(1));
        world.StopRecording();
    }

    [Test]
    public void NestedCommand_RecordsOnlyTheOuterOne()
    {
        using var world = CreateWorld(out var fire, out var simulation, out var chunk);
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        fire.MarkTwice(simulation.GetCellRef(chunk, 0), 1.5f);
        var recording = world.StopRecording();
        var live = world.ComputeStateHash();

        Assert.Multiple(() =>
        {
            Assert.That(recording.Operations, Has.Count.EqualTo(1));
            Assert.That(fire.Marks, Is.EqualTo(2));
        });

        world.ReplayTo(start, recording);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(live));
    }

    [Test]
    public void TickingInsideCommand_Throws()
    {
        using var world = CreateWorld(out var fire, out _, out _);
        Assert.That(fire.TickInside, Throws.InvalidOperationException.With.Message.Contains("solver command"));
        Assert.That(world.Tick, Throws.Nothing);
    }

    [Test]
    public void RestoringInsideCommandWhileRecording_Throws()
    {
        // A command runs with operation application set, which used to let it past the recording check of a restore.
        using var world = CreateWorld(out var fire, out _, out _);
        fire.Saved = world.CaptureCheckpoint();
        world.Tick();
        world.StartRecording();

        Assert.That(fire.RestoreInside, Throws.InvalidOperationException.With.Message.Contains("solver command"));
        Assert.Multiple(() =>
        {
            Assert.That(world.TickCount, Is.EqualTo(1));
            Assert.That(world.StopRecording().Operations, Is.Empty);
        });
    }

    [Test]
    public void StartingRecordingInsideCommand_Throws()
    {
        using var world = CreateWorld(out var fire, out _, out _);
        fire.Inside = world.StartRecording;

        Assert.That(fire.RunInside, Throws.InvalidOperationException.With.Message.Contains("solver command"));
        Assert.Multiple(() =>
        {
            Assert.That(world.IsRecording, Is.False);
            Assert.That(() => world.CaptureRecording(), Throws.InvalidOperationException);
        });
    }

    [Test]
    public void StoppingRecordingInsideCommand_ThrowsAndKeepsRecording()
    {
        using var world = CreateWorld(out var fire, out var simulation, out var chunk);
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        fire.Mark(simulation.GetCellRef(chunk, 0), 2f);
        world.Tick();
        fire.Inside = () => world.StopRecording();

        Assert.That(fire.RunInside, Throws.InvalidOperationException.With.Message.Contains("solver command"));
        Assert.That(world.IsRecording, Is.True);

        fire.Mark(simulation.GetCellRef(chunk, 1), 3f);
        world.Tick();
        var recording = world.StopRecording();
        var live = world.ComputeStateHash();
        world.ReplayTo(start, recording);
        Assert.Multiple(() =>
        {
            Assert.That(recording.Operations, Has.Count.EqualTo(2));
            Assert.That(world.ComputeStateHash(), Is.EqualTo(live));
        });
    }

    [Test]
    public void ResumingRecordingInsideCommand_Throws()
    {
        using var world = CreateWorld(out var fire, out _, out _);
        world.StartRecording();
        world.Tick();
        world.StopRecording();
        fire.Inside = world.ResumeRecording;

        Assert.That(fire.RunInside, Throws.InvalidOperationException.With.Message.Contains("solver command"));
        Assert.That(world.IsRecording, Is.False);

        // The stopped head is still the one to resume at.
        world.ResumeRecording();
        Assert.That(world.IsRecording, Is.True);
    }

    [Test]
    public void SeekingTimelineInsideCommand_ThrowsAndStaysLive()
    {
        using var world = CreateWorld(out var fire, out _, out _);
        var timeline = new AtmosWorldReplayTimeline(world, 0.1);
        world.Tick();
        world.Tick();
        fire.Inside = () => timeline.SeekTick(1);

        Assert.That(fire.RunInside, Throws.InvalidOperationException.With.Message.Contains("solver command"));
        Assert.Multiple(() =>
        {
            Assert.That(timeline.IsInspecting, Is.False);
            Assert.That(world.IsRecording, Is.True);
        });

        Assert.That(() => timeline.SeekTick(1), Throws.Nothing);
    }

    [Test]
    public void BranchingTimelineInsideCommand_ThrowsAndKeepsTheHistory()
    {
        using var world = CreateWorld(out var fire, out var simulation, out var chunk);
        var timeline = new AtmosWorldReplayTimeline(world, 0.1);
        world.Tick();
        fire.Mark(simulation.GetCellRef(chunk, 0), 1f);
        world.Tick();
        fire.Mark(simulation.GetCellRef(chunk, 0), 1f);
        world.Tick();
        ulong[] recorded = timeline.Operations.Select(static op => op.Sequence).ToArray();
        timeline.SeekTick(2);
        fire.Inside = timeline.SimulateFromHere;

        Assert.That(fire.RunInside, Throws.InvalidOperationException.With.Message.Contains("solver command"));
        Assert.That(timeline.IsInspecting, Is.True);

        // A branch that kept its prefix after failing would list the first command twice. The new command moves the
        // head, so the timeline combines its operations again instead of returning the ones it cached.
        timeline.ReturnToHead();
        fire.Mark(simulation.GetCellRef(chunk, 1), 1f);
        Assert.That(
            timeline.Operations.Select(static op => op.Sequence),
            Is.EqualTo(recorded.Append(world.TimelinePosition.OperationSequence)));
    }

    [Test]
    public void DisposingASimulationThatOwnsItsWorldInsideCommand_ThrowsAndKeepsBoth()
    {
        using var simulation = new AtmosSimulation(new TestAtmosConfig());
        var world = simulation.World;
        var fire = new FireSolver { World = world, Inside = simulation.Dispose };
        world.Solvers.Register(Name, fire);

        // The world refuses disposal inside a command, so the simulation must not tear itself down first.
        Assert.That(fire.RunInside, Throws.InvalidOperationException.With.Message.Contains("solver command"));
        Assert.Multiple(() =>
        {
            Assert.That(world.TryGetSimulation(simulation.Id, out _), Is.True);
            Assert.That(() => simulation.CreateAndRegisterChunk(Int3.Zero), Throws.Nothing);
            Assert.That(simulation.Tick, Throws.Nothing);
        });

        simulation.Dispose();
        Assert.That(() => world.ComputeStateHash(), Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public void UnregisteredInstance_Throws()
    {
        var fire = new FireSolver();
        Assert.That(() => fire.Mark(default, 1f), Throws.InvalidOperationException.With.Message.Contains("not a stage"));

        using var world = CreateWorld(out var registered, out var simulation, out var chunk);
        world.Solvers.Unregister(Name);
        Assert.That(() => registered.Mark(simulation.GetCellRef(chunk, 0), 1f), Throws.InvalidOperationException);
    }

    [Test]
    public void InstanceWithCommands_CannotJoinASecondLiveWorld()
    {
        using var first = CreateWorld(out var fire, out _, out _);
        using var second = new AtmosWorld(new TestAtmosConfig());
        Assert.That(
            () => second.Solvers.Register(Name, fire),
            Throws.InvalidOperationException.With.Message.Contains("solver commands"));

        first.Solvers.Unregister(Name);
        Assert.That(() => second.Solvers.Register(Name, fire), Throws.Nothing);
    }

    [Test]
    public void CatalogStage_RecordsAndReplaysCommands()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = simulation.CreateAndRegisterChunk(Int3.Zero);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        var counter = world.Solvers.Add<CatalogCounter>();
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        counter.Bump(3);
        world.Tick();
        counter.Bump(4);
        var recording = world.StopRecording();
        var live = world.ComputeStateHash();

        Assert.Multiple(() =>
        {
            Assert.That(recording.Operations, Has.Count.EqualTo(2));
            Assert.That(counter.Total, Is.EqualTo(7));
        });

        world.ReplayTo(start, recording);
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(live));
            Assert.That(world.Solvers.Get<CatalogCounter>().Total, Is.EqualTo(7));
        });
    }

    [Test]
    public void ImportedCatalogStage_ReplaysACommandThatGetsTheWorldFromApply()
    {
        AtmosWorldReplayArchive archive;
        using (var world = new AtmosWorld(new TestAtmosConfig()))
        {
            var simulation = world.CreateSimulation(2, 2, 1);
            var chunk = simulation.CreateAndRegisterChunk(Int3.Zero);
            simulation.SetChunkClassification(chunk, new VoxelClassification(1));
            simulation.AddGasToVoxel(chunk, 0, 0, 1f, 300f);
            var heater = world.Solvers.Add<CatalogHeater>();
            var timeline = new AtmosWorldReplayTimeline(world, 0.1);
            world.Tick();
            heater.Heat(simulation.GetCellRef(chunk, 0), 500f);
            world.Tick();
            timeline.ObserveLiveState();
            archive = timeline.CaptureReplay();
        }

        // The imported world creates its own instance from the catalog, so nothing but the apply parameter points it
        // at the world.
        using var imported = new AtmosWorld();
        AtmosWorldReplayTimeline.Import(imported, archive, 0.1).ReturnToHead();
        Assert.Multiple(() =>
        {
            Assert.That(archive.Recording.Operations.Select(static op => op.Code), Does.Contain(AtmosWorldOperationCode.InvokeSolverCommand));
            Assert.That(imported.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
            Assert.That(imported.Solvers.Get<CatalogHeater>().Heats, Is.EqualTo(1));
        });
    }

    [Test]
    public void RenamedCommand_ReplaysARecordingMadeUnderItsOldName()
    {
        AtmosWorldCheckpoint start;
        AtmosWorldRecording recording;
        AtmosWorldStateHash live;
        using (var world = CreateWorld(out var fire, out var simulation, out var chunk))
        {
            start = world.CaptureCheckpoint();
            world.StartRecording();
            fire.Mark(simulation.GetCellRef(chunk, 1), 5f);
            world.Tick();
            recording = world.StopRecording();
            live = world.ComputeStateHash();
        }

        using var renamed = new AtmosWorld(new TestAtmosConfig());
        var replaySimulation = renamed.CreateSimulation(2, 2, 1);
        var replayChunk = replaySimulation.CreateAndRegisterChunk(Int3.Zero);
        replaySimulation.SetChunkClassification(replayChunk, new VoxelClassification(1));
        var solver = new RenamedFireSolver { World = renamed };
        renamed.Solvers.Register(Name, solver);

        renamed.ReplayTo(start, recording);
        Assert.Multiple(() =>
        {
            Assert.That(renamed.ComputeStateHash(), Is.EqualTo(live));
            Assert.That(solver.Marks, Is.EqualTo(1));
        });
    }

    [Test]
    public void ReplayWithTheStageMissing_NamesTheStageAndTheCommand()
    {
        using var world = CreateWorld(out _, out _, out _);
        var start = world.CaptureCheckpoint();
        int command = NumosState.FieldId(nameof(FireSolver.Ignite));
        var operation = new InvokeAtmosSolverCommandOperation("tests/commands/missing", command, Array.Empty<byte>());
        var history = new[]
        {
            new AtmosWorldRecordedOperation(new AtmosTimelinePosition(start.Position.Tick, start.Position.OperationSequence + 1), operation)
        };

        var target = history[0].Position;
        Assert.That(
            () => world.ReplayTo(start, history, target),
            Throws.InvalidOperationException
                .With.Message.Contains("tests/commands/missing")
                .And.Message.Contains("Ignite"));

        var wrongId = operation with { Solver = Name, Command = NumosState.FieldId("NoSuchCommand") };
        Assert.That(
            () => world.ReplayTo(start, [history[0] with { Operation = wrongId }], target),
            Throws.InvalidOperationException.With.Message.Contains(Name).And.Message.Contains(typeof(FireSolver).FullName!));
    }

    [Test]
    public void SavedReplay_RoundTripsTheCommandThroughTheSerializer()
    {
        using var source = CreateWorld(out var fire, out var simulation, out var chunk);
        var timeline = new AtmosWorldReplayTimeline(source, 0.1);
        source.Tick();
        fire.Ignite(simulation.GetCellRef(chunk, 3), 1.25f, 700f);
        source.Tick();
        timeline.ObserveLiveState();
        var archive = timeline.CaptureReplay();

        using var stream = new MemoryStream();
        NumosWorldReplaySerializer.Serialize(
            stream,
            new NumosWorldReplayDocument(
                new NumosReplayMetadata("Solver commands", DateTimeOffset.UnixEpoch, "tests", "1.0.0", "1.0.0"),
                archive));

        stream.Position = 0;
        var loaded = NumosWorldReplaySerializer.Deserialize(stream).Replay;
        var written = archive.Recording.Operations.Select(static op => op.Operation).OfType<InvokeAtmosSolverCommandOperation>().Single();
        var read = loaded.Recording.Operations.Select(static op => op.Operation).OfType<InvokeAtmosSolverCommandOperation>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(read.Solver, Is.EqualTo(written.Solver));
            Assert.That(read.Command, Is.EqualTo(written.Command));
            Assert.That(read.Payload.ToArray(), Is.EqualTo(written.Payload.ToArray()));
            // The host-registered stage is the only thing a fresh world lacks. The command itself is portable.
            Assert.That(loaded.UnsupportedFeatures, Is.EqualTo(new[] { $"custom world solver '{Name}' (not in the solver catalog)" }));
        });

        using var target = CreateWorld(out var replayed, out _, out _);
        var restored = AtmosWorldReplayTimeline.Restore(target, loaded, 0.1);
        restored.ReturnToHead();
        Assert.Multiple(() =>
        {
            Assert.That(target.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
            Assert.That(replayed.Ignitions, Is.EqualTo(1));
        });
    }

    [Test]
    public void EveryParameterKind_RoundTripsThroughReplay()
    {
        using var world = CreateWorld(out var fire, out var simulation, out var chunk);
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        var gas = TestGases.Create("TestGas2");
        var cell = simulation.GetCellRef(chunk, 1);
        fire.Configure(
            cell, chunk, simulation.Id, new Int3(1, -2, 3), "label", FireMode.Roaring, gas, [1, 2, 3], ["a", "b"], 4.5, null, 7);

        var recording = world.StopRecording();
        var live = fire.LastConfigure;
        world.ReplayTo(start, recording);

        Assert.That(fire.LastConfigure, Is.EqualTo(live));
    }

    [Test]
    public void BaseAndDerivedCommands_ReplayThroughADerivedInstance()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 1);
        var chunk = simulation.CreateAndRegisterChunk(Int3.Zero);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        var burner = new Burner { World = world };
        world.Solvers.Register("tests/commands/burner", burner);
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        burner.Heat(simulation.GetCellRef(chunk, 0), 3f);
        burner.Heat(simulation.GetCellRef(chunk, 1), 2f);
        world.Tick();
        burner.Quench(simulation.GetCellRef(chunk, 1));
        var recording = world.StopRecording();
        var live = world.ComputeStateHash();

        Assert.Multiple(() =>
        {
            Assert.That(
                recording.Operations.Select(static op => ((InvokeAtmosSolverCommandOperation)op.Operation).Command),
                Is.EqualTo(new[]
                {
                    NumosState.FieldId(nameof(BurnerBase.Heat)),
                    NumosState.FieldId(nameof(BurnerBase.Heat)),
                    NumosState.FieldId(nameof(Burner.Quench)),
                }));
            Assert.That(burner.Heats, Is.EqualTo(2));
            Assert.That(ReadBurn(simulation, chunk)[..2], Is.EqualTo(new[] { 3f, 0f }));
        });

        world.ReplayTo(start, recording);
        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(live));
            Assert.That(burner.Heats, Is.EqualTo(2));
            Assert.That(ReadBurn(simulation, chunk)[..2], Is.EqualTo(new[] { 3f, 0f }));
        });
    }

    private static class Record
    {
        public static Exception? Exception(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }

    private static float[] ReadBurn(AtmosSimulation simulation, AtmosChunkHandle chunk)
    {
        Assert.That(simulation.TryGetChunkField(chunk, Burn, out var values), Is.True);
        return values.AsSpan().ToArray();
    }

    private static AtmosWorld CreateWorld(out FireSolver fire, out AtmosSimulation simulation, out AtmosChunkHandle chunk)
    {
        var world = new AtmosWorld(new TestAtmosConfig());
        simulation = world.CreateSimulation(2, 2, 1);
        chunk = simulation.CreateAndRegisterChunk(Int3.Zero);
        simulation.SetChunkClassification(chunk, new VoxelClassification(1));
        fire = new FireSolver { World = world };
        world.Solvers.Register(Name, fire);
        return world;
    }

    public enum FireMode : byte
    {
        Smoulder,
        Roaring
    }

    /// <summary>
    ///     Applies its commands through ordinary host calls and its own state.
    /// </summary>
    public sealed partial class FireSolver : IAtmosWorldSolver
    {
        [NumosSerialize] public float Threshold = 300f;
        [NumosSerialize] public int Ignitions;
        [NumosSerialize] public int Fizzles;
        [NumosSerialize] public int Marks;
        [NumosIgnore] public AtmosWorld? World;
        [NumosIgnore] public string LastConfigure = "";
        [NumosIgnore] public AtmosWorldCheckpoint? Saved;
        [NumosIgnore] public Action? Inside;

        [SolverCommand]
        public partial void Ignite(AtmosCellRef cell, float moles, float temperature);

        [SolverCommand]
        public partial void Mark(AtmosCellRef cell, float amount);

        [SolverCommand]
        public partial void MarkTwice(AtmosCellRef cell, float amount);

        [SolverCommand]
        public partial void MarkThroughDangerous(AtmosCellRef cell, float amount);

        [SolverCommand]
        public partial void TickInside();

        [SolverCommand]
        public partial void RestoreInside();

        [SolverCommand]
        public partial void RunInside();

        [SolverCommand]
        public partial void Configure(
            AtmosCellRef cell,
            AtmosChunkHandle chunk,
            AtmosSimulationId simulation,
            Int3 position,
            string label,
            FireMode mode,
            GasProperties gas,
            IReadOnlyList<int> counts,
            string[] names,
            double scale,
            string? note,
            int? limit);

        public void Solve(AtmosWorldSolverContext context)
        {
        }

        private AtmosSimulation SimulationOf(AtmosCellRef cell)
        {
            World!.TryGetSimulation(cell.Simulation, out var simulation);
            return simulation!;
        }

        // Three host calls and a state write, so the test sees that only the command is recorded.
        private void ApplyIgnite(AtmosCellRef cell, float moles, float temperature)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(moles);
            if (temperature < Threshold)
            {
                Fizzles++;
                return;
            }

            var simulation = SimulationOf(cell);
            simulation.AddGasToVoxel(cell.Chunk, cell.LocalVoxelIndex, 0, moles, temperature);
            simulation.SetVoxelTemperature(cell.Chunk, cell.LocalVoxelIndex, temperature);
            simulation.SetChunkData(cell.Chunk, Burn, cell.LocalVoxelIndex, temperature);
            Ignitions++;
        }

        private void ApplyMark(AtmosCellRef cell, float amount)
        {
            AddBurn(SimulationOf(cell), cell, amount);
            Marks++;
        }

        private void ApplyMarkTwice(AtmosCellRef cell, float amount)
        {
            Mark(cell, amount);
            Mark(cell, amount);
        }

        private void ApplyMarkThroughDangerous(AtmosCellRef cell, float amount)
        {
            SimulationOf(cell).Dangerous().GetChunkField(cell.Chunk, Burn).AsSpan()[cell.LocalVoxelIndex] = amount;
        }

        private void ApplyTickInside()
        {
            World!.Tick();
        }

        private void ApplyRestoreInside()
        {
            World!.RestoreCheckpoint(Saved!);
        }

        private void ApplyRunInside()
        {
            Inside!();
        }

        private void ApplyConfigure(
            AtmosCellRef cell,
            AtmosChunkHandle chunk,
            AtmosSimulationId simulation,
            Int3 position,
            string label,
            FireMode mode,
            GasProperties gas,
            IReadOnlyList<int> counts,
            string[] names,
            double scale,
            string? note,
            int? limit)
        {
            LastConfigure = string.Join(
                "|",
                cell, chunk, simulation, position, label, mode, gas.Name, string.Join(",", counts), string.Join(",", names),
                scale.ToString(CultureInfo.InvariantCulture), note ?? "null",
                limit?.ToString(CultureInfo.InvariantCulture) ?? "null");
        }
    }

    private static void AddBurn(AtmosSimulation simulation, AtmosCellRef cell, float amount)
    {
        float current = simulation.TryGetChunkField(cell.Chunk, Burn, out var values)
            ? values.AsSpan()[cell.LocalVoxelIndex]
            : 0f;

        simulation.SetChunkData(cell.Chunk, Burn, cell.LocalVoxelIndex, current + amount);
    }

    /// <summary>
    ///     A base stage with a command, so a derived stage's dispatch has to fall back to it.
    /// </summary>
    public partial class BurnerBase : IAtmosWorldSolver
    {
        [NumosSerialize] public int Heats;
        [NumosIgnore] public AtmosWorld? World;

        [SolverCommand]
        public partial void Heat(AtmosCellRef cell, float amount);

        public void Solve(AtmosWorldSolverContext context)
        {
        }

        protected AtmosSimulation SimulationOf(AtmosCellRef cell)
        {
            World!.TryGetSimulation(cell.Simulation, out var simulation);
            return simulation!;
        }

        private void ApplyHeat(AtmosCellRef cell, float amount)
        {
            AddBurn(SimulationOf(cell), cell, amount);
            Heats++;
        }
    }

    public sealed partial class Burner : BurnerBase
    {
        [SolverCommand]
        public partial void Quench(AtmosCellRef cell);

        private void ApplyQuench(AtmosCellRef cell)
        {
            SimulationOf(cell).SetChunkData(cell.Chunk, Burn, cell.LocalVoxelIndex, 0f);
        }
    }

    /// <summary>
    ///     The same stage after its command was renamed. The wire name keeps old recordings working.
    /// </summary>
    public sealed partial class RenamedFireSolver : IAtmosWorldSolver
    {
        [NumosSerialize] public float Threshold = 300f;
        [NumosSerialize] public int Ignitions;
        [NumosSerialize] public int Fizzles;
        [NumosSerialize] public int Marks;
        [NumosIgnore] public AtmosWorld? World;

        [SolverCommand(nameof(FireSolver.Mark))]
        public partial void Scorch(AtmosCellRef cell, float amount);

        public void Solve(AtmosWorldSolverContext context)
        {
        }

        private void ApplyScorch(AtmosCellRef cell, float amount)
        {
            World!.TryGetSimulation(cell.Simulation, out var simulation);
            AddBurn(simulation!, cell, amount);
            Marks++;
        }
    }
}

/// <summary>
///     A catalog solver whose command the world records under its catalog name.
/// </summary>
[AtmosSolver("tests/commands/catalog-counter", Interval = 1)]
public sealed partial class CatalogCounter : IAtmosWorldSolver
{
    [NumosSerialize] public int Total;

    [SolverCommand]
    public partial void Bump(int amount);

    public void Solve(AtmosWorldSolverContext context)
    {
    }

    private void ApplyBump(int amount)
    {
        Total += amount;
    }
}

/// <summary>
///     A catalog solver whose command reaches the grid only through the world its apply method receives.
/// </summary>
[AtmosSolver("tests/commands/catalog-heater", Interval = 1)]
public sealed partial class CatalogHeater : IAtmosWorldSolver
{
    [NumosSerialize] public int Heats;

    [SolverCommand]
    public partial void Heat(AtmosCellRef cell, float temperature);

    public void Solve(AtmosWorldSolverContext context)
    {
    }

    private void ApplyHeat(AtmosWorld world, AtmosCellRef cell, float temperature)
    {
        if (!world.TryGetSimulation(cell.Simulation, out var simulation))
            throw new ArgumentException("The cell's simulation is not in the world.", nameof(cell));

        simulation.SetVoxelTemperature(cell.Chunk, cell.LocalVoxelIndex, temperature);
        Heats++;
    }
}
