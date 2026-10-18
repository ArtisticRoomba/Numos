using Numos.API.Dangerous;
using Numos.Collections;

namespace Numos.API.Dangerous.Tests;

internal static partial class GeneratedDangerousData
{
    [ChunkData("tests/chunk-data-generated-dangerous/heat")]
    public static partial ChunkField<float> Heat { get; }

    [ChunkData("tests/chunk-data-generated-dangerous/totals", Length = 2)]
    public static partial ChunkField<long> Totals { get; }
}

[TestFixture]
public sealed class ChunkDataGeneratedDangerousTests
{
    [Test]
    public void GeneratedAccessors_OnTheDangerousView_ShareSolverStorage()
    {
        using var world = new AtmosWorld(new TestAtmosConfig());
        var simulation = world.CreateSimulation(2, 2, 2);
        var handle = simulation.CreateAndRegisterChunk(default);
        float seen = 0f;

        world.Solvers.Register("tests/chunk-data-generated-dangerous/shared", context =>
        {
            var chunks = context.Chunks(simulation);
            Parallel.For(0, chunks.Count, index =>
            {
                AtmosDangerousChunk live = chunks[index].Dangerous();
                FlatArray<float> heat = live.Heat;
                heat[3] = 4f;
                live.Totals[1] = 9;
            });

            seen = chunks[0].Heat[3];
        });

        world.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.EqualTo(4f));
            Assert.That(simulation.Dangerous().GetChunk(handle).Totals[1], Is.EqualTo(9));
            Assert.That(simulation.GetChunkField(handle, GeneratedDangerousData.Heat)[3], Is.EqualTo(4f));
        });
    }
}
