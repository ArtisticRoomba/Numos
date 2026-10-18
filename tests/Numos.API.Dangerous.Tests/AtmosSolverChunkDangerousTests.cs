using Numos.Maths;

namespace Numos.API.Dangerous.Tests;

[TestFixture]
public sealed class AtmosSolverChunkDangerousTests
{
    private static readonly ChunkField<float> Exposure =
        ChunkField<float>.Register("tests/dangerous-solver-chunk/exposure");

    private static readonly ChunkField<long> Totals =
        ChunkField<long>.Register("tests/dangerous-solver-chunk/totals", length: 2);

    [Test]
    public void Dangerous_SharesFieldStorageWithTheSolverView()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 2);
        var handle = simulation.CreateAndRegisterChunk(default);
        int temperatureLength = -1;
        Int3 dimensions = default;
        int fixedLength = -1;

        world.Solvers.Register("tests/dangerous-solver-chunk/shared", context =>
        {
            var chunks = context.Chunks(simulation);
            Parallel.For(0, chunks.Count, index =>
            {
                var live = chunks[index].Dangerous();
                live.GetSpan(Exposure)[3] = 4f;
                live.Get(Totals)[1] = 9;
                temperatureLength = live.Temperature.Length;
                dimensions = live.Get(Exposure).Dimensions;
                fixedLength = live.GetSpan(Totals).Length;
            });

            Assert.That(chunks[0].GetSpan(Exposure)[3], Is.EqualTo(4f));
        });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(temperatureLength, Is.EqualTo(8));
            Assert.That(dimensions, Is.EqualTo(new Int3(2, 2, 2)));
            Assert.That(fixedLength, Is.EqualTo(2));
            Assert.That(simulation.GetChunkField(handle, Exposure)[3], Is.EqualTo(4f));
            Assert.That(simulation.GetChunkField(handle, Totals)[1], Is.EqualTo(9));
            Assert.That(simulation.Dangerous().GetChunk(handle).GetSpan(Totals)[1], Is.EqualTo(9));
        });
    }

    [Test]
    public void Dangerous_OnExpiredOrDefaultView_Throws()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(1, 1, 1);
        simulation.CreateAndRegisterChunk(default);
        AtmosSolverChunk retained = default;

        world.Solvers.Register("tests/dangerous-solver-chunk/retained", context => retained = context.Chunks(simulation)[0]);
        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(() => retained.Dangerous().VoxelCount, Throws.InvalidOperationException);
            Assert.That(() => default(AtmosSolverChunk).Dangerous().VoxelCount, Throws.InvalidOperationException);
        });
    }
}
