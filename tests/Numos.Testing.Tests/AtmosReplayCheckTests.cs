using Numos.API;
using Numos.API.Dangerous;
using Numos.CoreSim;
using Numos.CoreSim.State;
using Numos.Maths;
using Numos.Tests;

namespace Numos.Testing.Tests;

// The chunk field registry is process-wide, so the key is namespaced to this file.
internal static partial class ReplayCheckData
{
    [ChunkData("tests/replay-check/exposure")]
    public static partial ChunkField<float> Exposure { get; }
}

/// <summary>
///     A solver that keeps all of its state where Numos can see it.
/// </summary>
internal sealed partial class ExposureSolver : IAtmosWorldSolver
{
    [NumosSerialize] public float Rate = 0.25f;
    [NumosSerialize] private int _calls;

    [NumosIgnore] private readonly List<ushort> _hot = [];

    public void Solve(AtmosWorldSolverContext context)
    {
        _calls++;
        _hot.Clear();
        foreach (var simulation in context.Simulations)
        foreach (var chunk in context.Chunks(simulation))
        {
            var exposure = chunk.Get(ReplayCheckData.Exposure).AsSpan();
            var moles = chunk.Dangerous().GetGasChannel(0).Moles;
            for (ushort voxel = 0; voxel < exposure.Length; voxel++)
            {
                exposure[voxel] += Rate * moles[voxel];
                if (exposure[voxel] > 4f)
                    _hot.Add(voxel);
            }

            if (_calls % 7 != 0)
                continue;

            foreach (ushort voxel in _hot)
                moles[voxel] += 0.01f;

            _hot.Clear();
        }
    }

    public void OnRestored()
    {
        _hot.Clear();
    }
}

/// <summary>
///     A solver that adds gas from a scratch accumulator and forgets to clear it when Numos restores a checkpoint.
/// </summary>
internal sealed partial class ForgetfulSolver : IAtmosWorldSolver
{
    [NumosIgnore] private float _carry;

    public void Solve(AtmosWorldSolverContext context)
    {
        _carry += 0.001f;
        foreach (var simulation in context.Simulations)
        {
            if (context.Chunks(simulation).TryGet(AtmosReplayCheckTests.FedChunk, out var chunk))
                chunk.Dangerous().GetGasChannel(0).Moles[0] += _carry;
        }
    }

    public void OnRestored()
    {
    }
}

/// <summary>
///     A solver whose output depends on a static that other worlds in the process change.
/// </summary>
internal sealed partial class StaticReadingSolver : IAtmosWorldSolver
{
    public static int WorldsBuilt;

    public void Solve(AtmosWorldSolverContext context)
    {
        foreach (var simulation in context.Simulations)
        {
            if (context.Chunks(simulation).TryGet(AtmosReplayCheckTests.FedChunk, out var chunk))
                chunk.Dangerous().GetGasChannel(0).Moles[0] += WorldsBuilt * 0.001f;
        }
    }
}

/// <summary>
///     A solver that heats one voxel per chunk and feeds a second gas, all through the supported editor.
/// </summary>
internal sealed partial class EditingSolver : IAtmosWorldSolver
{
    [NumosSerialize] private int _calls;

    public void Solve(AtmosWorldSolverContext context)
    {
        _calls++;
        foreach (var simulation in context.Simulations)
        {
            var chunks = context.Chunks(simulation);
            Parallel.For(0, chunks.Count, index =>
            {
                var chunk = chunks[index];
                var voxel = (ushort)(_calls % chunk.VoxelCount);
                if (chunk.Pressure[voxel] <= 0f)
                    return;

                var editor = chunk.Edit();
                editor.Temperature[voxel] += 0.5f;
                if (_calls % 7 == 0)
                    editor.Moles(1)[voxel] += 0.02f;
            });
        }
    }
}

/// <summary>
///     A solver that heats voxels at random, one stream per voxel, drawn on worker threads.
/// </summary>
internal sealed partial class RandomHeatingSolver : IAtmosWorldSolver
{
    [NumosSerialize] public float Chance = 0.3f;

    public void Solve(AtmosWorldSolverContext context)
    {
        foreach (var simulation in context.Simulations)
        {
            var chunks = context.Chunks(simulation);
            Parallel.For(0, chunks.Count, index =>
            {
                var chunk = chunks[index];
                for (int voxel = 0; voxel < chunk.VoxelCount; voxel++)
                {
                    var random = context.Random((ulong)(index * chunk.VoxelCount + voxel));
                    if (chunk.Pressure[voxel] > 0f && random.NextBoolean(Chance))
                        chunk.Edit().Temperature[voxel] += random.NextSingle();
                }
            });
        }
    }
}

[TestFixture]
public sealed class AtmosReplayCheckTests
{
    internal static readonly AtmosChunkHandle FedChunk = new(Int3.PosX);
    private const string FedChunkPath = "Simulations / 0 / Chunks / (1,0,0)";
    private const int Ticks = 120;

    [Test]
    public void BuiltInStages_WithAScript_PassEveryCheck()
    {
        var result = AtmosReplayCheck.Run(() => CreateWorld(), EditGasAndTemperature, Ticks);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.True, () => Report(result));
            Assert.That(result.Failures, Is.Empty);
            Assert.That(result.Skipped, Is.Empty);
        });
    }

    [Test]
    public void Archive_HoldsTheRecordedRunAndReplaysToItsHead()
    {
        var archive = AtmosReplayCheck.Run(() => CreateWorld(), EditGasAndTemperature, Ticks).Archive;
        Assert.That(archive, Is.Not.Null);

        using var world = CreateWorld();
        Assert.Multiple(() =>
        {
            Assert.That(archive!.Recording.Head.Tick, Is.EqualTo(Ticks));
            Assert.That(archive.Recording.Operations, Has.Count.EqualTo(2));
            Assert.That(archive.InitialStateHash, Is.EqualTo(world.ComputeStateHash()));
        });

        world.ReplayTo(archive!.InitialCheckpoint, archive.Recording);
        Assert.That(world.ComputeStateHash(), Is.EqualTo(archive.HeadStateHash));
    }

    [Test]
    public void CorrectCustomSolver_WithChunkDataAndSerializedFields_Passes()
    {
        var result = AtmosReplayCheck.Run(
            () =>
            {
                var world = CreateWorld();
                var solver = new ExposureSolver();
                world.Solvers.Register("tests/replay-check/exposure", solver);
                world.Services.Register(solver);
                return world;
            },
            static (world, tick) =>
            {
                EditGasAndTemperature(world, tick);

                // Inside the second sync test, so the restore has to put the old rate back.
                if (tick == 55)
                    world.Services.Get<ExposureSolver>().Rate = 0.5f;
            },
            Ticks);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.True, () => Report(result));
            Assert.That(result.Skipped, Is.Empty);
        });
    }

    [Test]
    public void SolverThatEditsThroughTheSupportedView_PassesEveryCheck()
    {
        var result = AtmosReplayCheck.Run(
            () =>
            {
                var world = CreateWorld();
                world.Solvers.Register("tests/replay-check/editing", new EditingSolver());
                return world;
            },
            EditGasAndTemperature,
            Ticks);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.True, () => Report(result));
            Assert.That(result.Skipped, Is.Empty);
        });
    }

    [Test]
    public void SolverThatDrawsFromContextRandom_WithASeedChange_PassesEveryCheck()
    {
        var result = AtmosReplayCheck.Run(
            () =>
            {
                var world = CreateWorld();
                world.Solvers.Register("tests/replay-check/random-heating", new RandomHeatingSolver());
                return world;
            },
            static (world, tick) =>
            {
                EditGasAndTemperature(world, tick);
                if (tick == 50)
                    world.SetAtmosConfig(new AtmosConfig(world.Config) { RandomSeed = 99 });
            },
            Ticks);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.True, () => Report(result));
            Assert.That(result.Skipped, Is.Empty);
        });
    }

    [Test]
    public void UnmarkedCounter_FailsSyncTestAndRecordReplay_InTheChunkItWrites()
    {
        var result = AtmosReplayCheck.Run(CreateLeakyWorld, ticks: 40);

        var replay = result.Failures.Single(static failure => failure.Check == AtmosReplayCheckKind.RecordReplay);
        var sync = result.Failures.Single(static failure => failure.Check == AtmosReplayCheckKind.SyncTest);
        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.False);

            // Each new world counts from zero, so a replay into a fresh world matches the recorded run.
            Assert.That(
                result.Failures.Select(static failure => failure.Check),
                Is.EqualTo(new[] { AtmosReplayCheckKind.RecordReplay, AtmosReplayCheckKind.SyncTest }));
            Assert.That(replay.Position.Tick, Is.EqualTo(1UL));
            Assert.That(sync.Position.Tick, Is.EqualTo(1UL));
            Assert.That(replay.Differences, Is.Not.Empty);
            Assert.That(replay.Differences[0].Path, Does.StartWith(FedChunkPath));
            Assert.That(sync.Differences, Is.Not.Empty);
            Assert.That(sync.Differences[0].Path, Does.StartWith(FedChunkPath));
        });
    }

    [Test]
    public void StaticCounter_FailsSecondWorld()
    {
        StaticReadingSolver.WorldsBuilt = 0;
        var result = AtmosReplayCheck.Run(
            () =>
            {
                var world = CreateWorld();
                world.Solvers.Register("tests/replay-check/static", new StaticReadingSolver());
                StaticReadingSolver.WorldsBuilt++;
                return world;
            },
            ticks: 30);

        var second = result.Failures.Single(static failure => failure.Check == AtmosReplayCheckKind.SecondWorld);
        Assert.Multiple(() =>
        {
            // One world records and replays, and another runs every sync test, so neither sees the static move.
            Assert.That(
                result.Failures.Select(static failure => failure.Check),
                Is.EqualTo(new[] { AtmosReplayCheckKind.SaveLoad, AtmosReplayCheckKind.SecondWorld }));
            Assert.That(second.Position.Tick, Is.EqualTo(1UL));
            Assert.That(second.Differences, Is.Not.Empty);
            Assert.That(second.Differences[0].Path, Does.StartWith(FedChunkPath));
        });
    }

    [Test]
    public void OnRestoredThatKeepsScratch_FailsSyncTest()
    {
        var result = AtmosReplayCheck.Run(
            () =>
            {
                var world = CreateWorld();
                world.Solvers.Register("tests/replay-check/forgetful", new ForgetfulSolver());
                return world;
            },
            ticks: 30);

        var sync = result.Failures.Single(static failure => failure.Check == AtmosReplayCheckKind.SyncTest);
        Assert.Multiple(() =>
        {
            Assert.That(sync.Position.Tick, Is.EqualTo(1UL));
            Assert.That(sync.Differences.Select(static difference => difference.Path), Has.Some.StartWith(FedChunkPath));
        });
    }

    [Test]
    public void HandWrittenSolverConfiguration_SkipsSaveLoad()
    {
        var result = AtmosReplayCheck.Run(
            () =>
            {
                var config = new TestAtmosConfig();
                config.SolverConfigurations.Add(new HandWrittenSettings(3));
                return CreateWorld(config);
            },
            EditGasAndTemperature,
            ticks: 40);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.True, () => Report(result));
            Assert.That(result.Skipped, Has.Count.EqualTo(1));
            Assert.That(result.Skipped[0], Does.StartWith("SaveLoad: "));
            Assert.That(result.Skipped[0], Does.Contain("tests/replay-check/hand-written"));
        });
    }

    [Test]
    public void DisabledChecks_AreListedAsSkipped()
    {
        var result = AtmosReplayCheck.Run(
            () => CreateWorld(),
            ticks: 5,
            options: new AtmosReplayCheckOptions { CheckSaveLoad = false, CheckSecondWorld = false });

        Assert.That(
            result.Skipped,
            Is.EqualTo(new[] { "SaveLoad: CheckSaveLoad is false.", "SecondWorld: CheckSecondWorld is false." }));
    }

    [Test]
    public void NoHashTrail_StillFindsTheDivergenceAtTheHead()
    {
        var result = AtmosReplayCheck.Run(
            CreateLeakyWorld,
            ticks: 12,
            options: new AtmosReplayCheckOptions { HashInterval = 0 });

        var replay = result.Failures.Single(static failure => failure.Check == AtmosReplayCheckKind.RecordReplay);
        Assert.Multiple(() =>
        {
            Assert.That(replay.Position.Tick, Is.EqualTo(12UL));
            Assert.That(replay.Differences.Select(static difference => difference.Path), Has.Some.StartWith(FedChunkPath));
        });
    }

    [Test]
    public void ThrowIfFailed_NamesTheCheckTheTickAndAPath()
    {
        var result = AtmosReplayCheck.Run(CreateLeakyWorld, ticks: 20);
        var sync = result.Failures.Single(static failure => failure.Check == AtmosReplayCheckKind.SyncTest);

        var exception = Assert.Throws<InvalidOperationException>(result.ThrowIfFailed)!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Message, Does.Contain("SyncTest at tick 1,"));
            Assert.That(exception.Message, Does.Contain("RecordReplay at tick 1,"));
            Assert.That(exception.Message, Does.Contain(sync.Differences[0].Path + ": " + sync.Differences[0].Detail));
        });
    }

    [Test]
    public void ThrowIfFailed_DoesNothingWhenEveryCheckPasses()
    {
        var result = AtmosReplayCheck.Run(() => CreateWorld(), ticks: 3);

        Assert.That(result.ThrowIfFailed, Throws.Nothing);
    }

    [Test]
    public void InvalidArguments_Throw()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => AtmosReplayCheck.Run(null!), Throws.ArgumentNullException);
            Assert.That(() => AtmosReplayCheck.Run(() => CreateWorld(), ticks: -1), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => AtmosReplayCheck.Run(() => null!), Throws.InvalidOperationException);
            Assert.That(() => new AtmosReplayCheckOptions { SyncInterval = 0 }, Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new AtmosReplayCheckOptions { SyncDistance = 0 }, Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new AtmosReplayCheckOptions { HashInterval = -1 }, Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new AtmosReplayCheckOptions { MaxDifferences = 0 }, Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void RecordingWorld_IsRejected()
    {
        Assert.That(
            () => AtmosReplayCheck.Run(() =>
            {
                var world = CreateWorld();
                world.StartRecording();
                return world;
            }),
            Throws.InvalidOperationException);
    }

    private static AtmosWorld CreateWorld(AtmosConfig? config = null)
    {
        var world = new AtmosWorld(config ?? new TestAtmosConfig());
        var simulation = world.CreateSimulation(4, 4, 1);
        var left = SimSetup.CreateOpenChunk(simulation, Int3.Zero);
        var right = SimSetup.CreateOpenChunk(simulation, FedChunk.Position);
        for (ushort voxel = 0; voxel < 16; voxel++)
        {
            simulation.AddGasToVoxel(left, voxel, 0, 1f, 300f);
            simulation.AddGasToVoxel(right, voxel, 0, 3f, 320f);
        }

        return world;
    }

    // A counter outside the checkpoint that feeds gas. Replay and sync tests keep counting from where it got to.
    private static AtmosWorld CreateLeakyWorld()
    {
        var world = CreateWorld();
        int calls = 0;
        world.Solvers.Register(
            "tests/replay-check/leaky",
            context =>
            {
                calls++;
                foreach (var simulation in context.Simulations)
                {
                    if (context.Chunks(simulation).TryGet(FedChunk, out var chunk))
                        chunk.Dangerous().GetGasChannel(0).Moles[0] += calls * 0.001f;
                }
            });
        return world;
    }

    private static void EditGasAndTemperature(AtmosWorld world, long tick)
    {
        var simulation = world.Simulations[0];
        if (tick == 10)
            simulation.AddGasToVoxel(new AtmosChunkHandle(Int3.Zero), 5, 1, 2f, 280f);

        if (tick == 33)
            simulation.SetVoxelTemperature(FedChunk, 7, 350f);
    }

    private static string Report(AtmosReplayCheckResult result)
    {
        try
        {
            result.ThrowIfFailed();
            return string.Join(Environment.NewLine, result.Skipped);
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }
    }

    // No [NumosSerialize] members, so the replay file has no way to write it.
    private sealed record HandWrittenSettings(int Value) : IAtmosSolverConfiguration
    {
        public string Key => "tests/replay-check/hand-written";

        public IAtmosSolverConfiguration CreateSnapshot(IGasRegistry gasRegistry) => this;

        public bool SemanticallyEquals(IAtmosSolverConfiguration other) => Equals(other);

        public ulong ComputeStateHash() => unchecked((ulong)Value);
    }
}
