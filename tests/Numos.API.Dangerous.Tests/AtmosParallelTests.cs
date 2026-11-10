using Numos.Maths;

namespace Numos.API.Dangerous.Tests;

[TestFixture]
public sealed class AtmosParallelTests
{
    [Test]
    public void WorkerCount_IsAtLeastOne()
    {
        Assert.That(AtmosParallel.WorkerCount, Is.GreaterThanOrEqualTo(1));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(37)]
    [TestCase(10_000)]
    public void Run_VisitsEveryIndexExactlyOnce(int count)
    {
        var visits = new int[count];

        AtmosParallel.Run(count, new CountVisits(visits));

        Assert.That(visits, Is.All.EqualTo(1));
    }

    [TestCase(0)]
    [TestCase(-5)]
    public void Run_WithNoItems_RunsNothing(int count)
    {
        var visits = new int[1];

        AtmosParallel.Run(count, new CountVisits(visits));

        Assert.That(visits[0], Is.Zero);
    }

    [Test]
    public void Run_WhenAnItemThrows_RethrowsAfterThePhase()
    {
        Assert.That(
            () => AtmosParallel.Run(256, new Fail(100)),
            Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("item 100"));
    }

    [Test]
    public void Run_InsideACustomStage_CoversEveryChunk()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 2);
        for (int x = 0; x < 6; x++)
            simulation.CreateAndRegisterChunk(new Int3(x, 0, 0));

        int[] visits = new int[6];
        world.Solvers.Register(
            "tests/atmos-parallel/chunks",
            context => AtmosParallel.Run(context.Chunks(simulation).Count, new CountVisits(visits)));

        world.Tick();

        Assert.That(visits, Is.All.EqualTo(1));
    }

    private readonly struct CountVisits(int[] visits) : IAtmosParallelAction
    {
        public void Invoke(int index)
        {
            visits[index]++;
        }
    }

    private readonly struct Fail(int failingIndex) : IAtmosParallelAction
    {
        public void Invoke(int index)
        {
            if (index == failingIndex)
                throw new InvalidOperationException($"item {index}");
        }
    }
}
