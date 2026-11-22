using Numos.CoreSim;
using Numos.Maths;

namespace Numos.API.Tests;

[TestFixture]
public sealed class AtmosRandomTests
{
    private const string Stage = "tests/random";

    [Test]
    public void DefaultState_MatchesTheSplitMix64ReferenceSequence()
    {
        // The published SplitMix64 outputs for a zero state. A change of algorithm fails here first.
        var random = default(AtmosRandom);

        Assert.That(
            new[] { random.NextUInt64(), random.NextUInt64(), random.NextUInt64() },
            Is.EqualTo(new[] { 0xE220A8397B1DCDAFUL, 0x6E789E6AA1B965F4UL, 0x06C45D188009454FUL }));
    }

    // Values from an independent implementation of the documented derivation, so a change to how the seed, tick,
    // stage name, or stream feed the start state is visible.
    [TestCase(0UL, 1L, Stage, 0UL, 0xE7D78A5D5E981195UL, 0xE177E5D3721BD46CUL, 0x40C81FE318F7BF00UL)]
    [TestCase(1UL, 1L, Stage, 0UL, 0xFC7C0061DCB45B32UL, 0xA3B4C092A0BB87DEUL, 0xFAB70F17084FAEFBUL)]
    [TestCase(0xDEADBEEFUL, 960L, "my-mod/fire", 42UL, 0x72E2C9EB347869CEUL, 0x83B0D027B3638214UL, 0xB8D1C1B15C0292DEUL)]
    [TestCase(ulong.MaxValue, 1L, "euler-flow", ulong.MaxValue, 0x40CA242254173F6BUL, 0x51B7901F3A771CF5UL, 0xB28DA6EB626AC780UL)]
    public void FirstValues_MatchFixedVectors(ulong seed, long tick, string stage, ulong stream, ulong first, ulong second, ulong third)
    {
        var random = new AtmosRandom(seed, tick, stage, stream);

        Assert.That(
            new[] { random.NextUInt64(), random.NextUInt64(), random.NextUInt64() },
            Is.EqualTo(new[] { first, second, third }));
    }

    [Test]
    public void TypedDraws_ConvertTheRawBitsExactly()
    {
        // The raw values are 0xE7D78A5D5E981195, 0xE177E5D3721BD46C, 0x40C81FE318F7BF00, and 0x6DECE60D2283F01B.
        var random = new AtmosRandom(0, 1, Stage, 0);

        Assert.Multiple(() =>
        {
            Assert.That(random.NextSingle(), Is.EqualTo(0xE7D78A / 16777216f));
            Assert.That(random.NextUInt32(), Is.EqualTo(0xE177E5D3u));
            Assert.That(random.NextInt32(10), Is.EqualTo(2));
            Assert.That(random.NextDouble(), Is.EqualTo(0xDBD9CC1A4507E / 9007199254740992.0));
        });
    }

    [Test]
    public void Draws_StayInTheirRanges()
    {
        var random = new AtmosRandom(12345, 7, Stage, 3);
        for (int draw = 0; draw < 100_000; draw++)
        {
            float single = random.NextSingle();
            double value = random.NextDouble();
            int index = random.NextInt32(7);
            if (single is < 0f or >= 1f || value is < 0.0 or >= 1.0 || index is < 0 or >= 7)
                Assert.Fail($"Draw {draw} gave {single}, {value}, and {index}.");
        }

        Assert.Multiple(() =>
        {
            Assert.That(random.NextInt32(1), Is.Zero);
            Assert.That(random.NextInt32(int.MaxValue), Is.InRange(0, int.MaxValue - 1));
            Assert.That(() => new AtmosRandom().NextInt32(0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new AtmosRandom().NextInt32(-3), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new AtmosRandom(0, 1, null!, 0), Throws.ArgumentNullException);
        });
    }

    [Test]
    public void NextBoolean_HonorsCertainProbabilities()
    {
        var random = new AtmosRandom(5, 1, Stage, 0);
        for (int draw = 0; draw < 1000; draw++)
        {
            if (random.NextBoolean(0f) || random.NextBoolean(-1f) || random.NextBoolean(float.NaN))
                Assert.Fail("A probability of zero, below zero, or NaN gave true.");

            if (!random.NextBoolean(1f) || !random.NextBoolean(2f))
                Assert.Fail("A probability of one or more gave false.");
        }
    }

    [Test]
    public void NextBoolean_HitsAboutTheGivenFraction()
    {
        var random = new AtmosRandom(9, 1, Stage, 0);
        int hits = 0;
        for (int draw = 0; draw < 100_000; draw++)
        {
            if (random.NextBoolean(0.25f))
                hits++;
        }

        Assert.That(hits, Is.InRange(24_000, 26_000));
    }

    [Test]
    public void SameInputs_GiveTheSameSequence()
    {
        var first = new AtmosRandom(7, 3, Stage, 5);
        var second = new AtmosRandom(7, 3, Stage, 5);

        Assert.That(Draw(ref first, 64), Is.EqualTo(Draw(ref second, 64)));
    }

    [TestCase(8UL, 3L, Stage, 5UL, TestName = "DifferentSeed_GivesAnotherSequence")]
    [TestCase(7UL, 4L, Stage, 5UL, TestName = "DifferentTick_GivesAnotherSequence")]
    [TestCase(7UL, 3L, "tests/random-2", 5UL, TestName = "DifferentStage_GivesAnotherSequence")]
    [TestCase(7UL, 3L, Stage, 6UL, TestName = "DifferentStream_GivesAnotherSequence")]
    public void OneDifferentInput_GivesAnotherSequence(ulong seed, long tick, string stage, ulong stream)
    {
        var baseline = new AtmosRandom(7, 3, Stage, 5);
        var changed = new AtmosRandom(seed, tick, stage, stream);

        Assert.That(Draw(ref changed, 8), Is.Not.EqualTo(Draw(ref baseline, 8)));
    }

    [Test]
    public void Copy_ContinuesFromTheSamePosition()
    {
        var random = new AtmosRandom(1, 1, Stage, 0);
        random.NextUInt64();
        var copy = random;

        Assert.That(copy.NextUInt64(), Is.EqualTo(random.NextUInt64()));
    }

    [Test]
    public void ContextRandom_StartsFromTheSeedTheTickTheStageAndTheStream()
    {
        using var world = new AtmosWorld(new AtmosConfig { RandomSeed = 77 });
        world.CreateSimulation(1, 1, 1);
        var drawn = new List<(long Tick, ulong Value)>();
        world.Solvers.Register(Stage, context =>
        {
            AtmosRandom random = context.Random(stream: 3);
            drawn.Add((context.TickCount, random.NextUInt64()));
        });

        world.Tick();
        world.Tick();

        Assert.That(drawn, Is.EqualTo(new[]
        {
            (1L, new AtmosRandom(77, 1, Stage, 3).NextUInt64()),
            (2L, new AtmosRandom(77, 2, Stage, 3).NextUInt64())
        }));
    }

    [Test]
    public void ContextRandom_WorksOnWorkerThreads()
    {
        using var world = new AtmosWorld(new AtmosConfig());
        world.CreateSimulation(1, 1, 1);
        var values = new ulong[64];
        world.Solvers.Register(Stage, context =>
        {
            Parallel.For(0, values.Length, index => values[index] = context.Random((ulong)index).NextUInt64());
        });

        world.Tick();

        Assert.That(
            values,
            Is.EqualTo(Enumerable.Range(0, values.Length).Select(static index => new AtmosRandom(0, 1, Stage, (ulong)index).NextUInt64())));
    }

    [Test]
    public void ContextRandom_AfterTheTick_Throws()
    {
        using var world = new AtmosWorld(new AtmosConfig());
        world.CreateSimulation(1, 1, 1);
        AtmosWorldSolverContext? retained = null;
        world.Solvers.Register(Stage, context => retained = context);

        world.Tick();

        Assert.That(() => retained!.Random(0), Throws.InvalidOperationException);
    }

    [Test]
    public void SolverThatDraws_ReplaysToTheSameHash_WithASeedChangeMidRecording()
    {
        using var world = CreateDrawingWorld(seed: 11);
        var start = world.CaptureCheckpoint();
        world.StartRecording();
        for (int tick = 0; tick < 20; tick++)
        {
            if (tick == 8)
            {
                var config = new AtmosConfig(world.Config) { RandomSeed = 12 };
                world.SetAtmosConfig(config);
            }

            world.Tick();
        }

        var recording = world.StopRecording();
        var head = world.ComputeStateHash();

        // The same run without the seed change ends somewhere else, so the recorded change carries the draws.
        using var unchanged = CreateDrawingWorld(seed: 11);
        for (int tick = 0; tick < 20; tick++)
            unchanged.Tick();

        world.ReplayTo(start, recording);

        Assert.Multiple(() =>
        {
            Assert.That(world.ComputeStateHash(), Is.EqualTo(head));
            Assert.That(world.Config.RandomSeed, Is.EqualTo(12UL));
            Assert.That(unchanged.ComputeStateHash(), Is.Not.EqualTo(head));
        });
    }

    // Heats each voxel by a random amount, drawn on worker threads with one stream per voxel.
    private static AtmosWorld CreateDrawingWorld(ulong seed)
    {
        var world = new AtmosWorld(new AtmosConfig
        {
            RandomSeed = seed,
            GasRegistry = [new GasProperties { Name = "Air", DiffusionCoefficient = 0.02f }]
        });
        var simulation = world.CreateSimulation(4, 4, 1);
        var chunk = SimSetup.CreateOpenChunk(simulation, Int3.Zero);
        for (ushort voxel = 0; voxel < 16; voxel++)
            simulation.AddGasToVoxel(chunk, voxel, 0, 1f, 300f);

        world.Solvers.Register(Stage, context =>
        {
            foreach (var running in context.Simulations)
            {
                var chunks = context.Chunks(running);
                Parallel.For(0, chunks.Count, index =>
                {
                    var editor = chunks[index].Edit();
                    Span<float> temperature = editor.Temperature;
                    for (int voxel = 0; voxel < temperature.Length; voxel++)
                        temperature[voxel] += context.Random((ulong)voxel).NextSingle();
                });
            }
        });

        return world;
    }

    private static ulong[] Draw(ref AtmosRandom random, int count)
    {
        var values = new ulong[count];
        for (int index = 0; index < count; index++)
            values[index] = random.NextUInt64();

        return values;
    }
}
